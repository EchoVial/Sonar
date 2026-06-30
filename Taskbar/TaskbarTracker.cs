using SpotifyLyricsTaskbar.Config;
using SpotifyLyricsTaskbar.Interop;

namespace SpotifyLyricsTaskbar.Taskbar;

/// <summary>Physical-pixel bounds for the overlay's left region, plus a probe point and DPI.</summary>
public readonly record struct OverlayPlacement(int Left, int Top, int Width, int Height, uint Dpi);

public static class TaskbarTracker
{
    public static IntPtr GetTaskbarHandle() => NativeMethods.FindWindow("Shell_TrayWnd", null);

    internal static bool TryGetTaskbarRect(out NativeMethods.RECT rect)
    {
        rect = default;
        var h = GetTaskbarHandle();
        return h != IntPtr.Zero && NativeMethods.GetWindowRect(h, out rect);
    }

    private static double ScaleOf(IntPtr taskbar, out uint dpi)
    {
        dpi = taskbar != IntPtr.Zero ? NativeMethods.GetDpiForWindow(taskbar) : 96;
        if (dpi == 0) dpi = 96;
        return dpi / 96.0;
    }

    /// <summary>Compute where to draw lyrics: left-padded into the empty left region of the taskbar.</summary>
    public static OverlayPlacement? GetPlacement(AppConfig config)
    {
        var h = GetTaskbarHandle();
        if (h == IntPtr.Zero || !NativeMethods.GetWindowRect(h, out var r)) return null;

        // Only horizontal taskbars (top or bottom) can host a line of lyrics. The rect
        // already carries the actual edge (r.Top) and height, so this auto-adapts.
        if (r.Height >= r.Width) return null; // vertical (left/right) taskbar — unsupported

        double scale = ScaleOf(h, out uint dpi);
        int edgePad = (int)Math.Round(10 * scale);

        int left, available;
        if (TaskbarLayout.GetEmptyRegion(h, in r) is { } region)
        {
            // Sit in the blank stretch wherever it is (left of centred icons, or the middle when
            // icons are left‑aligned). Keep the configured left margin when the gap starts at the
            // taskbar edge; otherwise use a small even margin.
            int padL = region.left <= r.Left + 4 ? (int)Math.Round(config.LeftPadding * scale) : edgePad;
            left = region.left + padL;
            available = Math.Max(0, region.right - left - edgePad);
        }
        else
        {
            // Fallback: a fraction of the taskbar width from the left edge.
            left = r.Left + (int)Math.Round(config.LeftPadding * scale);
            available = Math.Max(0, (int)Math.Round(r.Width * config.AutoWidthFraction) - (left - r.Left));
        }

        int width = config.MaxWidth > 0 ? Math.Min((int)Math.Round(config.MaxWidth * scale), available) : available;
        if (width < 40) width = Math.Min(Math.Max(available, 40), 240);

        return new OverlayPlacement(left, r.Top, width, r.Height, dpi);
    }

    /// <summary>A point on the far-left of the taskbar used to test whether the taskbar is topmost there.</summary>
    internal static NativeMethods.POINT GetProbePoint()
    {
        var h = GetTaskbarHandle();
        if (h == IntPtr.Zero || !NativeMethods.GetWindowRect(h, out var r)) return new NativeMethods.POINT(0, 0);
        double scale = ScaleOf(h, out _);
        int x = r.Left + (int)Math.Round(8 * scale);
        int y = r.Top + r.Height / 2;
        return new NativeMethods.POINT(x, y);
    }
}
