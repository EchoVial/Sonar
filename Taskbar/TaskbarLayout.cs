using System.Windows.Automation;
using SpotifyLyricsTaskbar.Interop;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar.Taskbar;

/// <summary>
/// Finds the largest empty horizontal stretch of the taskbar so lyrics sit in the blank area
/// regardless of taskbar alignment — to the left of centred icons, or in the middle when icons
/// are left‑aligned. Works by enumerating the taskbar's buttons (UI Automation) plus the system
/// tray, then picking the widest gap between them. Cached briefly (the cluster shifts as apps
/// open/close); all failures return null so the caller can fall back to a width fraction.
/// </summary>
internal static class TaskbarLayout
{
    private static (int left, int right)? _cached;
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2.5);
    private static readonly object Lock = new();

    /// <summary>The widest empty [left,right] span (physical px) on the taskbar, or null.</summary>
    internal static (int left, int right)? GetEmptyRegion(IntPtr taskbar, in NativeMethods.RECT bar)
    {
        if (taskbar == IntPtr.Zero) return null;
        lock (Lock)
        {
            if (DateTime.UtcNow - _cachedAt < CacheTtl) return _cached;
        }
        var region = Compute(taskbar, bar);
        lock (Lock) { _cached = region; _cachedAt = DateTime.UtcNow; }
        return region;
    }

    private static (int, int)? Compute(IntPtr taskbar, NativeMethods.RECT bar)
    {
        var occ = ProbeOccupied(taskbar, bar);
        if (occ.Count == 0) return null;

        occ.Sort((a, b) => a.l.CompareTo(b.l));
        var merged = new List<(int l, int r)>();
        foreach (var iv in occ)
        {
            if (merged.Count > 0 && iv.l <= merged[^1].r + 2) merged[^1] = (merged[^1].l, Math.Max(merged[^1].r, iv.r));
            else merged.Add(iv);
        }

        // Widest gap between bar.Left, the occupied spans, and bar.Right.
        int bestL = 0, bestR = 0, bestW = 0, cursor = bar.Left;
        foreach (var (l, r) in merged)
        {
            if (l - cursor > bestW) { bestW = l - cursor; bestL = cursor; bestR = l; }
            cursor = Math.Max(cursor, r);
        }
        if (bar.Right - cursor > bestW) { bestW = bar.Right - cursor; bestL = cursor; bestR = bar.Right; }

        return bestW >= 60 ? (bestL, bestR) : null;
    }

    private static List<(int l, int r)> ProbeOccupied(IntPtr taskbar, NativeMethods.RECT bar)
    {
        var list = new List<(int, int)>();
        try
        {
            var root = AutomationElement.FromHandle(taskbar);
            if (root != null)
            {
                foreach (AutomationElement btn in root.FindAll(TreeScope.Descendants,
                             new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
                {
                    var rc = btn.Current.BoundingRectangle;
                    if (rc.IsEmpty || rc.Width <= 0) continue;
                    int l = (int)Math.Round(rc.Left), r = (int)Math.Round(rc.Right);
                    if (r <= bar.Left || l >= bar.Right) continue; // off this taskbar
                    list.Add((Math.Max(l, bar.Left), Math.Min(r, bar.Right)));
                }
            }
        }
        catch (Exception ex) { Log.Write("taskbar probe failed: " + ex.Message); }

        // The system tray (clock / notifications) lives at the trailing edge — treat it as
        // occupied so the empty region never runs into it (esp. with left‑aligned icons).
        var tray = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && NativeMethods.GetWindowRect(tray, out var tr) && tr.Width > 0)
            list.Add((Math.Max(tr.Left, bar.Left), Math.Min(tr.Right, bar.Right)));

        return list;
    }
}
