using System.IO;
using System.Security.Cryptography;
using System.Text;
using SpotifyLyricsTaskbar.Config;

namespace SpotifyLyricsTaskbar.Lyrics;

/// <summary>Persists winning synced LRC text to disk, keyed by a hash of the track key.</summary>
public static class LyricsCache
{
    private static string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(AppConfig.CacheDir, hash + ".lrc");
    }

    public static string? Read(string key)
    {
        try
        {
            var p = PathFor(key);
            return File.Exists(p) ? File.ReadAllText(p) : null;
        }
        catch { return null; }
    }

    public static void Write(string key, string lrc)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.CacheDir);
            File.WriteAllText(PathFor(key), lrc);
        }
        catch { /* best effort */ }
    }
}
