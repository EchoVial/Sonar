using SpotifyLyricsTaskbar.Interop;

namespace SpotifyLyricsTaskbar.Taskbar;

/// <summary>
/// Decides whether the overlay should be shown by testing what window is actually
/// topmost over the taskbar strip. Because the overlay is WS_EX_TRANSPARENT, the
/// WindowFromPoint probe passes through it to the taskbar (show) or to a covering
/// fullscreen app (hide). Driven by WinEvent hooks (foreground / minimize / move),
/// so it reacts instantly to pressing Win without continuous polling.
/// All callbacks fire on the thread that installed the hooks (the WPF UI thread).
/// </summary>
public sealed class VisibilityController : IDisposable
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
        "TopLevelWindowForOverflowXamlIsland", "Shell_InputSwitchTopLevelWindow",
        "Shell_Dim", "MultitaskingViewFrame", "ForegroundStaging",
    };

    private readonly Action<bool> _onChanged;
    private readonly NativeMethods.WinEventDelegate _proc; // kept referenced to avoid GC
    private readonly List<IntPtr> _hooks = new();
    private bool _last;
    private bool _started;

    public VisibilityController(Action<bool> onChanged)
    {
        _onChanged = onChanged;
        _proc = WinEventProc;
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        Hook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND);
        Hook(NativeMethods.EVENT_SYSTEM_MINIMIZESTART, NativeMethods.EVENT_SYSTEM_MINIMIZEEND);
        Hook(NativeMethods.EVENT_SYSTEM_MOVESIZEEND, NativeMethods.EVENT_SYSTEM_MOVESIZEEND);
        _last = Evaluate();
        _onChanged(_last);
    }

    private void Hook(uint min, uint max)
    {
        var h = NativeMethods.SetWinEventHook(min, max, IntPtr.Zero, _proc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
        if (h != IntPtr.Zero) _hooks.Add(h);
    }

    private void WinEventProc(IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        => Reevaluate();

    /// <summary>Re-check visibility and fire the callback if it changed. UI thread only.</summary>
    public void Reevaluate()
    {
        bool now = Evaluate();
        if (now != _last)
        {
            _last = now;
            _onChanged(now);
        }
    }

    private static bool Evaluate()
    {
        try
        {
            var probe = TaskbarTracker.GetProbePoint();
            if (probe.X == 0 && probe.Y == 0) return false; // no taskbar found

            IntPtr underRoot = NativeMethods.GetAncestor(NativeMethods.WindowFromPoint(probe), NativeMethods.GA_ROOT);
            bool probeIsShell = ShellClasses.Contains(NativeMethods.ClassNameOf(underRoot));

            IntPtr fgRoot = NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), NativeMethods.GA_ROOT);
            bool fgIsShell = ShellClasses.Contains(NativeMethods.ClassNameOf(fgRoot));

            // Exclusive D3D fullscreen hides the taskbar even when its rect still exists.
            if (NativeMethods.SHQueryUserNotificationState(out var state) == 0 &&
                state == NativeMethods.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN &&
                !fgIsShell)
            {
                return false;
            }

            return probeIsShell;
        }
        catch
        {
            return true; // fail open: better to show than to vanish
        }
    }

    public void Dispose()
    {
        foreach (var h in _hooks) NativeMethods.UnhookWinEvent(h);
        _hooks.Clear();
    }
}
