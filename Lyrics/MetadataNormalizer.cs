using System.Text.RegularExpressions;

namespace SpotifyLyricsTaskbar.Lyrics;

/// <summary>
/// Cleans Spotify track/artist strings for fuzzy lyric lookups: strips
/// "(feat. …)", bracket tags, and "- Remaster/Remix/Live/…" suffixes, and
/// reduces a multi-artist string to its primary artist.
/// </summary>
public static partial class MetadataNormalizer
{
    [GeneratedRegex(@"\s*[\(\[]?\s*(?:feat\.?|ft\.?|featuring)\s+[^)\]]+[\)\]]?", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex FeatRegex();

    [GeneratedRegex(@"\s*[\(\[][^)\]]*[\)\]]", RegexOptions.Compiled)]
    private static partial Regex BracketRegex();

    [GeneratedRegex(@"\s*-\s*(?:remaster(?:ed)?|[^-]*remaster[^-]*|live|radio edit|mono|stereo|[^-]*version|[^-]*mix|remix|bonus track|demo|acoustic)\b.*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex DashSuffixRegex();

    [GeneratedRegex(@"\s*(?:,|&|;|/|\bfeat\.?\b|\bft\.?\b|\bwith\b)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex ArtistSplitRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespaceRegex();

    public static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var t = FeatRegex().Replace(title, string.Empty);
        t = DashSuffixRegex().Replace(t, string.Empty);
        t = BracketRegex().Replace(t, string.Empty);
        return WhitespaceRegex().Replace(t, " ").Trim();
    }

    public static string PrimaryArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return string.Empty;
        var a = FeatRegex().Replace(artist, string.Empty);
        var parts = ArtistSplitRegex().Split(a);
        foreach (var p in parts)
            if (!string.IsNullOrWhiteSpace(p))
                return WhitespaceRegex().Replace(p, " ").Trim();
        return WhitespaceRegex().Replace(a, " ").Trim();
    }

    public static string Normalize(string? s)
        => string.IsNullOrWhiteSpace(s) ? string.Empty : WhitespaceRegex().Replace(s, " ").Trim().ToLowerInvariant();
}
