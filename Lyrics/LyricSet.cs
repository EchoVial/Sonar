namespace SpotifyLyricsTaskbar.Lyrics;

public enum LyricKind { Synced, PlainBestEffort, TitleOnly, None }

/// <summary>A resolved lyrics result for one track.</summary>
public sealed class LyricSet
{
    public LyricKind Kind { get; init; } = LyricKind.None;
    public IReadOnlyList<LyricLine> Lines { get; init; } = Array.Empty<LyricLine>();
    public string Source { get; init; } = string.Empty;
    public string? StaticText { get; init; }

    public static readonly LyricSet None = new() { Kind = LyricKind.None, Source = "none" };

    public static LyricSet Synced(IReadOnlyList<LyricLine> lines, string source)
        => new() { Kind = LyricKind.Synced, Lines = lines, Source = source };

    public static LyricSet Plain(IReadOnlyList<LyricLine> lines, string source)
        => new() { Kind = LyricKind.PlainBestEffort, Lines = lines, Source = source };

    public static LyricSet Title(string text, string source)
        => new() { Kind = LyricKind.TitleOnly, StaticText = text, Source = source };
}

/// <summary>Raw lyric payload returned by a single provider attempt.</summary>
public sealed record LyricCandidate(string? Synced, string? Plain, bool Instrumental, string Source);
