using System.IO;
using SpotifyLyricsTaskbar.Config;

namespace SpotifyLyricsTaskbar.Util;

/// <summary>Tiny size-capped diagnostic log in %LOCALAPPDATA% (helps trace which lyric source matched).</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDir);
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
            lock (Gate)
            {
                var path = AppConfig.LogPath;
                if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024)
                {
                    try { File.Delete(path); } catch { /* ignore */ }
                }
                File.AppendAllText(path, line);
            }
        }
        catch { /* logging must never throw */ }
    }
}
