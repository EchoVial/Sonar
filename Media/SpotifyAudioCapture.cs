using System.Runtime.InteropServices;
using System.Threading;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar.Media;

/// <summary>
/// Captures ONLY a target process's audio (Spotify) via the Windows Application/Process
/// Loopback API (Windows 10 2004+), so other system sounds don't interfere. Delivers mono
/// float frames to <see cref="FrameReady"/>. Capture runs on its own thread; everything is
/// best-effort and logs on failure — audio sync is an optional enhancement, never required.
/// </summary>
public sealed class SpotifyAudioCapture : IDisposable
{
    /// <summary>Mono samples (‑1..1) and their sample rate, delivered in ~10 ms chunks.</summary>
    public event Action<float[], int>? FrameReady;
    public bool IsCapturing { get; private set; }

    private const int SampleRate = 44100;
    private Thread? _thread;
    private volatile bool _run;

    public bool Start(int targetPid)
    {
        if (IsCapturing) return true;
        if (targetPid <= 0) { Log.Write("audio: no target pid"); return false; }
        _run = true;
        _thread = new Thread(() => CaptureLoop((uint)targetPid)) { IsBackground = true, Name = "SonarAudioCapture" };
        _thread.Start();
        return true;
    }

    public void Stop()
    {
        _run = false;
        try { _thread?.Join(500); } catch { }
        _thread = null;
        IsCapturing = false;
    }

    public void Dispose() => Stop();

    private void CaptureLoop(uint pid)
    {
        object? clientObj = null;
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        IntPtr paramsPtr = IntPtr.Zero, propPtr = IntPtr.Zero, fmtPtr = IntPtr.Zero;
        try
        {
            // 1) Activation params: capture this process tree's render stream.
            var acParams = new AUDIOCLIENT_ACTIVATION_PARAMS
            {
                ActivationType = 1, // AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK
                TargetProcessId = pid,
                ProcessLoopbackMode = 0, // INCLUDE_TARGET_PROCESS_TREE
            };
            paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>());
            Marshal.StructureToPtr(acParams, paramsPtr, false);

            var prop = new PROPVARIANT { vt = 65 /* VT_BLOB */, blobSize = (uint)Marshal.SizeOf<AUDIOCLIENT_ACTIVATION_PARAMS>(), blobData = paramsPtr };
            propPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PROPVARIANT>());
            Marshal.StructureToPtr(prop, propPtr, false);

            var iidClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
            var handler = new ActivationHandler();
            int hr = ActivateAudioInterfaceAsync(VirtualDeviceProcessLoopback, ref iidClient, propPtr, handler, out var op);
            if (hr != 0) { Log.Write($"audio: ActivateAudioInterfaceAsync hr=0x{hr:X8}"); return; }

            if (!handler.Wait(3000)) { Log.Write("audio: activation timed out"); return; }
            op.GetActivateResult(out int actHr, out clientObj);
            if (actHr != 0 || clientObj is not IAudioClient c) { Log.Write($"audio: activate result hr=0x{actHr:X8}"); return; }
            client = c;

            // 2) Fixed 16‑bit PCM stereo @44.1k; AUTOCONVERTPCM lets WASAPI resample for us.
            var fmt = new WAVEFORMATEX
            {
                wFormatTag = 1, nChannels = 2, nSamplesPerSec = SampleRate, wBitsPerSample = 16,
                nBlockAlign = 4, nAvgBytesPerSec = SampleRate * 4, cbSize = 0,
            };
            fmtPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WAVEFORMATEX>());
            Marshal.StructureToPtr(fmt, fmtPtr, false);

            const uint SHARED = 0;
            const uint LOOPBACK = 0x00020000, AUTOCONVERT = 0x80000000, SRC_QUALITY = 0x08000000;
            hr = client.Initialize(SHARED, LOOPBACK | AUTOCONVERT | SRC_QUALITY, 2_000_000 /*200ms in 100ns*/, 0, fmtPtr, IntPtr.Zero);
            if (hr != 0) { Log.Write($"audio: Initialize hr=0x{hr:X8}"); return; }

            var iidCapture = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
            hr = client.GetService(ref iidCapture, out var capObj);
            if (hr != 0 || capObj is not IAudioCaptureClient cap) { Log.Write($"audio: GetService hr=0x{hr:X8}"); return; }
            capture = cap;

            hr = client.Start();
            if (hr != 0) { Log.Write($"audio: Start hr=0x{hr:X8}"); return; }

            IsCapturing = true;
            Log.Write("audio: capturing Spotify process loopback");

            // 3) Poll for packets; downmix to mono float and publish.
            while (_run)
            {
                Thread.Sleep(10);
                while (capture.GetNextPacketSize(out uint frames) == 0 && frames > 0)
                {
                    if (capture.GetBuffer(out IntPtr data, out uint got, out uint flags, out _, out _) != 0) break;
                    if (got > 0 && (flags & 0x2) == 0 /* not SILENT */)
                        FrameReady?.Invoke(ToMono(data, got), SampleRate);
                    else if (got > 0)
                        FrameReady?.Invoke(new float[got], SampleRate); // silent packet → zeros
                    capture.ReleaseBuffer(got);
                }
            }
        }
        catch (Exception ex) { Log.Write("audio: capture loop failed: " + ex.Message); }
        finally
        {
            try { client?.Stop(); } catch { }
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            if (clientObj != null && clientObj != client) Marshal.ReleaseComObject(clientObj);
            if (fmtPtr != IntPtr.Zero) Marshal.FreeHGlobal(fmtPtr);
            if (propPtr != IntPtr.Zero) Marshal.FreeHGlobal(propPtr);
            if (paramsPtr != IntPtr.Zero) Marshal.FreeHGlobal(paramsPtr);
            IsCapturing = false;
        }
    }

    private static unsafe float[] ToMono(IntPtr data, uint frames)
    {
        var outp = new float[frames];
        short* p = (short*)data;
        for (int i = 0; i < frames; i++)
        {
            int l = p[i * 2], r = p[i * 2 + 1];
            outp[i] = (l + r) * 0.5f / 32768f;
        }
        return outp;
    }

    // ---- interop -----------------------------------------------------------
    private const string VirtualDeviceProcessLoopback = "VAD\\Process_Loopback";

    [DllImport("Mmdevapi.dll", ExactSpelling = true)]
    private static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath, ref Guid riid,
        IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    [StructLayout(LayoutKind.Sequential)]
    private struct AUDIOCLIENT_ACTIVATION_PARAMS { public int ActivationType; public uint TargetProcessId; public int ProcessLoopbackMode; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public uint blobSize; public IntPtr blobData; public IntPtr pad; }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels; public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    private sealed class ActivationHandler : IActivateAudioInterfaceCompletionHandler
    {
        private readonly ManualResetEventSlim _done = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation op) => _done.Set();
        public bool Wait(int ms) => _done.Wait(ms);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(uint shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(uint shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFrames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFrames);
        [PreserveSig] int GetNextPacketSize(out uint numFrames);
    }
}
