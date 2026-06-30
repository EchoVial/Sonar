using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpotifyLyricsTaskbar.Util;

/// <summary>
/// Releases unused resident pages back to the OS when the app is idle/hidden.
/// Pages fault back in on demand; for a mostly-idle overlay this keeps the
/// working set (the "Memory" figure in Task Manager) small.
/// </summary>
public static class MemoryTrim
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    public static void Trim()
    {
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); }
        catch { /* best effort */ }
    }
}
