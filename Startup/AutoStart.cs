using Microsoft.Win32;

namespace SpotifyLyricsTaskbar.Startup;

/// <summary>Toggles launch-at-login via the per-user Run key (no admin / UAC).</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Sonar";
    private const string LegacyValueName = "SpotifyLyricsTaskbar";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                          ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (k == null) return;

            k.DeleteValue(LegacyValueName, throwOnMissingValue: false); // clean up the old pre-Sonar entry

            if (enabled)
            {
                var path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path)) k.SetValue(ValueName, $"\"{path}\"");
            }
            else
            {
                k.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch { /* best effort */ }
    }
}
