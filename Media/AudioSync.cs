using SpotifyLyricsTaskbar.Config;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar.Media;

/// <summary>
/// Turns Spotify's audio into a live timing correction. It runs a short FFT over the captured
/// stream and measures <b>spectral flux</b> (rising energy across frequency bins) to flag onsets —
/// far more reliable than raw RMS in continuous music. For each onset it asks how far the nearest
/// lyric line's timestamp sits from the real playback position; the median of recent gaps, smoothed
/// and clamped, becomes <see cref="AppConfig.AudioCorrectionMs"/> so the lyrics drift onto the
/// vocal. Fast (one 1024-pt FFT per ~12 ms, no model); corrects constant lag/lead, not words.
/// Feeds arrive on the capture thread.
/// </summary>
public sealed class AudioSync
{
    private readonly AppConfig _config;
    private readonly Func<long> _getPositionMs;
    private readonly Func<long, long> _nearestLineMs; // position → nearest line ms, or long.MinValue

    private const int FftSize = 1024, Hop = 512;
    private readonly float[] _buf = new float[FftSize];
    private int _bufFill;
    private readonly double[] _re = new double[FftSize], _im = new double[FftSize];
    private readonly double[] _prevMag = new double[FftSize / 2];
    private readonly double[] _hann = new double[FftSize];

    private double _fluxAvg, _fluxMax;
    private long _lastOnsetPos = long.MinValue;
    private long _lastLogPos = long.MinValue;
    private int _onsetCount, _matchCount;
    private readonly List<int> _gaps = new();

    public AudioSync(AppConfig config, Func<long> getPositionMs, Func<long, long> nearestLineMs)
    {
        _config = config;
        _getPositionMs = getPositionMs;
        _nearestLineMs = nearestLineMs;
        for (int i = 0; i < FftSize; i++) _hann[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1));
    }

    /// <summary>Drop accumulated gaps (used after the live correction is folded into the song's base offset).</summary>
    public void ClearGaps() => _gaps.Clear();

    public void Reset()
    {
        _bufFill = 0;
        _fluxAvg = 0;
        _lastOnsetPos = _lastLogPos = long.MinValue;
        _onsetCount = _matchCount = 0;
        Array.Clear(_prevMag);
        _gaps.Clear();
        _config.AudioCorrectionMs = 0;
    }

    /// <summary>Feed a mono chunk from the capture; buffered into overlapping FFT windows.</summary>
    public void Feed(float[] samples, int sampleRate)
    {
        int idx = 0;
        while (idx < samples.Length)
        {
            int n = Math.Min(FftSize - _bufFill, samples.Length - idx);
            Array.Copy(samples, idx, _buf, _bufFill, n);
            _bufFill += n; idx += n;
            if (_bufFill < FftSize) break;
            ProcessWindow();
            Array.Copy(_buf, Hop, _buf, 0, FftSize - Hop); // slide by hop (overlap)
            _bufFill = FftSize - Hop;
        }
    }

    private void ProcessWindow()
    {
        for (int i = 0; i < FftSize; i++) { _re[i] = _buf[i] * _hann[i]; _im[i] = 0; }
        Fft(_re, _im);

        double flux = 0;
        for (int k = 1; k < FftSize / 2; k++)
        {
            double mag = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
            double d = mag - _prevMag[k];
            if (d > 0) flux += d;   // positive spectral flux only
            _prevMag[k] = mag;
        }

        long pos = _getPositionMs();
        if (flux > _fluxMax) _fluxMax = flux;
        if (_lastLogPos == long.MinValue) _lastLogPos = pos;
        if (pos - _lastLogPos > 4000)
        {
            Log.Write($"audiosync: onsets/4s={_onsetCount} matched={_matchCount} n={_gaps.Count} fluxAvg={_fluxAvg:F1} fluxMax={_fluxMax:F1} correction={_config.AudioCorrectionMs}ms");
            _onsetCount = _matchCount = 0; _fluxMax = 0;
            _lastLogPos = pos;
        }

        _fluxAvg = _fluxAvg <= 0 ? flux : _fluxAvg * 0.92 + flux * 0.08;
        bool spacedOut = _lastOnsetPos == long.MinValue || pos - _lastOnsetPos > 170; // avoid MinValue overflow
        bool onset = flux > _fluxAvg * 1.3 && flux > 0.05 && spacedOut;
        if (!onset) return;
        _lastOnsetPos = pos;
        _onsetCount++;

        long line = _nearestLineMs(pos);
        if (line == long.MinValue) return;
        _matchCount++;

        int baseOffset = _config.SyncOffsetMs + _config.CurrentSongOffset;
        int gap = (int)(line - pos - baseOffset); // >0: lyric line sits later than the vocal → show sooner
        if (Math.Abs(gap) >= 5000) return;

        _gaps.Add(gap);
        if (_gaps.Count > 20) _gaps.RemoveAt(0);
        if (_gaps.Count < 6) return;

        var sorted = new List<int>(_gaps); sorted.Sort();
        int median = sorted[sorted.Count / 2];
        int cur = _config.AudioCorrectionMs;
        int delta = median - cur;
        // Deadband + slow ease: ignore sub‑40 ms wobble so the lyrics settle rather than jitter.
        if (Math.Abs(delta) > 40)
            _config.AudioCorrectionMs = Math.Clamp(cur + (int)Math.Round(0.12 * delta), -8000, 8000);
    }

    /// <summary>In-place iterative radix-2 FFT.</summary>
    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wlr = Math.Cos(ang), wli = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci;
                    double ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wlr - ci * wli; ci = cr * wli + ci * wlr; cr = ncr;
                }
            }
        }
    }
}
