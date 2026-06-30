using System.Windows.Automation;
using SpotifyLyricsTaskbar.Interop;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar.Taskbar;

/// <summary>
/// Finds the left edge of the taskbar's centred app cluster (the Start button) via UI
/// Automation, so lyrics fill only the empty area to its left and never overrun the icons.
/// The result is cached briefly because the cluster shifts as apps open/close. All failures
/// fall back to "unknown" so the caller can use a width fraction instead.
/// </summary>
internal static class TaskbarLayout
{
    private static int _cachedLeft;
    private static DateTime _cachedAt = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2.5);
    private static readonly object Lock = new();

    /// <summary>
    /// Physical-pixel X of the centred cluster's left edge, or null if it can't be determined.
    /// Returns null when the edge would sit too close to the taskbar's left (e.g. a far-left
    /// Widgets button) so the caller doesn't end up with a zero-width strip.
    /// </summary>
    internal static int? GetClusterLeft(IntPtr taskbar, in NativeMethods.RECT bar)
    {
        if (taskbar == IntPtr.Zero) return null;

        lock (Lock)
        {
            if (DateTime.UtcNow - _cachedAt < CacheTtl)
                return _cachedLeft > 0 ? _cachedLeft : null;
        }

        int? left = Probe(taskbar, bar);
        lock (Lock)
        {
            _cachedLeft = left ?? 0;
            _cachedAt = DateTime.UtcNow;
        }
        return left;
    }

    private static int? Probe(IntPtr taskbar, in NativeMethods.RECT bar)
    {
        try
        {
            var root = AutomationElement.FromHandle(taskbar);
            if (root == null) return null;

            // Start button is the leftmost element of the centred cluster on Windows 11.
            var start = root.FindFirst(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.AutomationIdProperty, "StartButton"))
                        ?? root.FindFirst(TreeScope.Descendants,
                            new AndCondition(
                                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                                new PropertyCondition(AutomationElement.NameProperty, "Start")));

            int barLeft = bar.Left, barRight = bar.Right, barWidth = Math.Max(1, bar.Width);
            int Sane(int x) => (x > barLeft + barWidth / 10 && x < barRight) ? x : 0;

            if (start != null)
            {
                int x = Sane((int)Math.Round(start.Current.BoundingRectangle.Left));
                if (x > 0) return x;
            }

            // Fallback: the leftmost button past the far-left tenth (skips a Widgets button).
            int best = int.MaxValue;
            foreach (AutomationElement btn in root.FindAll(TreeScope.Descendants,
                         new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                var r = btn.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0) continue;
                int x = Sane((int)Math.Round(r.Left));
                if (x > 0 && x < best) best = x;
            }
            return best == int.MaxValue ? null : best;
        }
        catch (Exception ex) { Log.Write("taskbar layout probe failed: " + ex.Message); return null; }
    }
}
