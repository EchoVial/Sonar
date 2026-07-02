using System.IO;

namespace SpotifyLyricsTaskbar.Startup;

/// <summary>
/// Drops a Start-menu shortcut so Sonar is searchable / pinnable like an installed app,
/// even though it ships as a portable exe. Idempotent and best-effort.
/// </summary>
public static class StartMenu
{
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Sonar.lnk");

    public static void EnsureShortcut()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var lnk = ShortcutPath;
            if (File.Exists(lnk) && string.Equals(ReadTarget(lnk), exe, StringComparison.OrdinalIgnoreCase))
                return; // already points here
            Create(lnk, exe);
        }
        catch { /* best effort — never block startup */ }
    }

    private static void Create(string lnkPath, string exePath)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        if (t == null) return;
        dynamic shell = Activator.CreateInstance(t)!;
        var sc = shell.CreateShortcut(lnkPath);
        sc.TargetPath = exePath;
        sc.WorkingDirectory = Path.GetDirectoryName(exePath);
        sc.IconLocation = exePath + ",0";
        sc.Description = "Sonar — taskbar lyrics";
        sc.Save();
    }

    private static string? ReadTarget(string lnkPath)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null) return null;
            dynamic shell = Activator.CreateInstance(t)!;
            var sc = shell.CreateShortcut(lnkPath);
            return (string)sc.TargetPath;
        }
        catch { return null; }
    }
}
