using System.Net.Http;
using SpotifyLyricsTaskbar.Config;
using SpotifyLyricsTaskbar.Lyrics.Providers;
using SpotifyLyricsTaskbar.Media;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar.Lyrics;

/// <summary>
/// Resolves lyrics with the fallback chain, run concurrently for speed:
///   - fire LRCLIB (exact + fuzzy) and NetEase at once;
///   - prefer an LRCLIB synced result if it arrives within LrclibPreferMs (cleaner, English);
///   - otherwise use NetEase the moment it's ready;
///   - then plain best-effort, then the title chip / nothing.
/// Results are cached in memory and (synced LRC) on disk.
/// </summary>
public sealed class LyricsProvider
{
    private readonly AppConfig _config;
    private readonly HttpClient _http;
    private readonly LrclibProvider _lrclib;
    private readonly NetEaseProvider _netease;
    private readonly GeniusProvider _genius;
    private readonly Dictionary<string, LyricSet> _memory = new();
    private readonly object _memLock = new();

    public LyricsProvider(AppConfig config)
    {
        _config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var contact = string.IsNullOrWhiteSpace(config.ContactForUserAgent) ? "local-build" : config.ContactForUserAgent;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"SpotifyLyricsTaskbar/1.0 (+{contact})");
        _lrclib = new LrclibProvider(_http);
        _netease = new NetEaseProvider(_http);
        _genius = new GeniusProvider(_http);
    }

    /// <summary>Pre-establish the LRCLIB connection so the first real lookup isn't paying cold-start latency.</summary>
    public void Warmup()
    {
        _ = Task.Run(async () =>
        {
            try { using var _ = await _http.GetAsync("https://lrclib.net/api/search?q=hello"); }
            catch { /* ignore */ }
        });
    }

    public async Task<LyricSet> GetAsync(TrackInfo track, CancellationToken ct)
    {
        var key = track.Key;
        lock (_memLock) { if (_memory.TryGetValue(key, out var cached)) return cached; }

        string title = track.Title;
        string artist = track.Artist;
        string cleanTitle = MetadataNormalizer.CleanTitle(title);
        string primaryArtist = MetadataNormalizer.PrimaryArtist(artist);

        // If the track metadata is Latin-script (e.g. an English song), reject lyrics that
        // come back mostly CJK — that's a wrong NetEase match (a Chinese cover / translation).
        bool expectCjk = HasCjk(title) || HasCjk(artist);
        bool Acceptable(LyricCandidate? c)
            => c != null && LrcParser.HasRealTimestamps(c.Synced) && (expectCjk || !IsMostlyCjk(c.Synced));

        // Disk cache (synced LRC) — apply the same script check so a stale wrong-language entry can't resurface.
        var diskLrc = LyricsCache.Read(key);
        if (LrcParser.HasRealTimestamps(diskLrc) && (expectCjk || !IsMostlyCjk(diskLrc)))
            return Remember(key, LyricSet.Synced(LrcParser.Parse(diskLrc!), "cache"));

        // Kick off every source at once, each with its own timeout.
        var getT = Timed(c => _lrclib.GetExactAsync(title, artist, track.Album, track.DurationSeconds, c), _config.LrclibTimeoutMs, ct);
        var searchT = Timed(c => _lrclib.SearchBestAsync(cleanTitle, primaryArtist, track.DurationSeconds, c), _config.LrclibTimeoutMs, ct);
        var neT = Timed(c => _netease.GetBestAsync(cleanTitle, primaryArtist, track.DurationSeconds, c), _config.NetEaseTimeoutMs, ct);
        // Genius (plain only) runs alongside as a strong fallback for obscure tracks.
        var geniusT = _config.PlainLyricsBestEffort
            ? Timed(c => _genius.GetBestAsync(cleanTitle, primaryArtist, c), _config.GeniusTimeoutMs, ct)
            : Task.FromResult<LyricCandidate?>(null);

        var seen = new List<LyricCandidate?>();

        // 1) Prefer LRCLIB if it answers (with acceptable synced lyrics) within the preference window.
        var preferred = await FirstSyncedWithin(new List<Task<LyricCandidate?>> { getT, searchT }, _config.LrclibPreferMs, Acceptable);
        if (preferred != null) return UseSynced(key, preferred, track.DurationSeconds);

        // 2) Otherwise take NetEase if its lyrics are acceptable.
        var ne = await Safe(neT); seen.Add(ne);
        if (Acceptable(ne)) return UseSynced(key, ne!, track.DurationSeconds);
        if (!expectCjk && ne != null && LrcParser.HasRealTimestamps(ne.Synced) && IsMostlyCjk(ne.Synced))
            Log.Write($"lyrics: rejected CJK lyric from {ne.Source} for {artist} - {title}");

        // 3) Give LRCLIB the rest of its budget.
        var g = await Safe(getT); seen.Add(g);
        if (Acceptable(g)) return UseSynced(key, g!, track.DurationSeconds);
        var s = await Safe(searchT); seen.Add(s);
        if (Acceptable(s)) return UseSynced(key, s!, track.DurationSeconds);

        // 4) Plain lyrics, distributed across the track as a rough sync.
        //    Genius first (clean English text, covers many obscure tracks), then any plain we already have.
        if (_config.PlainLyricsBestEffort)
        {
            var plainSources = new List<LyricCandidate?> { await Safe(geniusT) };
            plainSources.AddRange(seen);
            foreach (var c in plainSources)
            {
                if (string.IsNullOrWhiteSpace(c?.Plain)) continue;
                if (!expectCjk && IsMostlyCjk(c!.Plain)) continue; // skip wrong-language plain lyrics too
                var lines = BuildPlainTimed(c!.Plain!, track.DurationSeconds);
                if (lines.Count == 0) continue;
                Log.Write($"lyrics: plain best-effort {c.Source} for {artist} - {title}");
                return Remember(key, LyricSet.Plain(lines, c.Source + "/plain"));
            }
        }

        // 5) Graceful floor: now-playing chip or nothing.
        LyricSet final = _config.ShowTitleWhenNoLyrics ? LyricSet.Title(BuildTitle(track), "title") : LyricSet.None;
        Log.Write($"lyrics: none for {artist} - {title} -> {final.Kind}");
        return Remember(key, final);
    }

    private static bool IsCjkChar(char c)
        => (c >= 0x4E00 && c <= 0x9FFF)   // CJK Unified Ideographs (Chinese)
        || (c >= 0x3400 && c <= 0x4DBF)   // CJK Extension A
        || (c >= 0xF900 && c <= 0xFAFF)   // CJK Compatibility Ideographs
        || (c >= 0x3040 && c <= 0x30FF)   // Hiragana + Katakana (Japanese)
        || (c >= 0xAC00 && c <= 0xD7A3);  // Hangul syllables (Korean)

    private static bool HasCjk(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var c in s) if (IsCjkChar(c)) return true;
        return false;
    }

    /// <summary>True if a meaningful share (≥30%) of the letter characters are CJK — i.e. a non-Latin lyric.</summary>
    private static bool IsMostlyCjk(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int cjk = 0, latin = 0;
        foreach (var c in text)
        {
            if (IsCjkChar(c)) cjk++;
            else if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')) latin++;
        }
        int total = cjk + latin;
        return total > 0 && cjk >= total * 0.30;
    }

    private LyricSet UseSynced(string key, LyricCandidate c, double durationSec)
    {
        // Only persist matches we could verify against the track duration — the cache key is now
        // duration-free, so a loose (duration-unknown) match must not stick permanently.
        if (durationSec > 1) LyricsCache.Write(key, c.Synced!);
        Log.Write($"lyrics: synced via {c.Source} for {key}");
        return Remember(key, LyricSet.Synced(LrcParser.Parse(c.Synced!), c.Source));
    }

    private LyricSet Remember(string key, LyricSet set)
    {
        lock (_memLock) { _memory[key] = set; }
        return set;
    }

    private async Task<LyricCandidate?> Timed(Func<CancellationToken, Task<LyricCandidate?>> call, int ms, CancellationToken outer)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        cts.CancelAfter(Math.Max(1000, ms));
        try { return await call(cts.Token); }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log.Write("provider error: " + ex.Message); return null; }
    }

    private static async Task<LyricCandidate?> Safe(Task<LyricCandidate?> t)
    {
        try { return await t; } catch { return null; }
    }

    /// <summary>Returns the first task result accepted by <paramref name="accept"/>, or null if the window elapses first.</summary>
    private static async Task<LyricCandidate?> FirstSyncedWithin(List<Task<LyricCandidate?>> tasks, int windowMs, Func<LyricCandidate?, bool> accept)
    {
        var all = new List<Task>(tasks);
        var delay = Task.Delay(Math.Max(1, windowMs));
        all.Add(delay);
        while (all.Count > 1)
        {
            var done = await Task.WhenAny(all);
            if (ReferenceEquals(done, delay)) return null;
            all.Remove(done);
            LyricCandidate? cand;
            try { cand = ((Task<LyricCandidate?>)done).Result; } catch { cand = null; }
            if (accept(cand)) return cand;
        }
        return null;
    }

    private static string BuildTitle(TrackInfo t)
        => string.IsNullOrWhiteSpace(t.Artist) ? t.Title : $"{t.Artist} — {t.Title}";

    private static List<LyricLine> BuildPlainTimed(string plain, double durationSec)
    {
        var texts = new List<string>();
        foreach (var l in plain.Replace("\r\n", "\n").Split('\n'))
        {
            var t = l.Trim();
            if (t.Length > 0) texts.Add(t);
        }
        var lines = new List<LyricLine>(texts.Count);
        if (texts.Count == 0) return lines;

        // Plain lyrics have no timing, so we spread them across the track. The biggest perceived
        // error was starting at ~4% — most songs open with an instrumental intro, so the lines ran
        // well ahead of the vocal. Hold the first line until an estimated vocal-start (a bounded
        // share of the track) and leave a real outro, so the fallback lands much closer.
        double start = durationSec > 30 ? Math.Clamp(durationSec * 0.09, 6, 20) : durationSec * 0.04;
        double end = durationSec > 10 ? durationSec * 0.93 : Math.Max(durationSec, texts.Count);
        double span = Math.Max(end - start, texts.Count);

        // Weight each line by an estimated syllable count, so a wordy line holds longer and a short
        // interjection passes quickly — pacing tracks the lyrics instead of advancing at a flat rate
        // (the flat rate is what made untimed songs drift badly through verses/choruses).
        var weights = new double[texts.Count];
        double totalW = 0;
        for (int i = 0; i < texts.Count; i++) { weights[i] = EstimateSyllables(texts[i]); totalW += weights[i]; }

        double cum = 0;
        for (int i = 0; i < texts.Count; i++)
        {
            double frac = totalW > 0 ? cum / totalW : (double)i / texts.Count;
            lines.Add(new LyricLine(TimeSpan.FromSeconds(start + span * frac), texts[i]));
            cum += weights[i];
        }
        return lines;
    }

    /// <summary>Rough syllable count (vowel groups) — a stand-in for how long a line is sung.</summary>
    private static double EstimateSyllables(string line)
    {
        int syl = 0;
        bool prevVowel = false;
        foreach (char ch in line.ToLowerInvariant())
        {
            bool vowel = ch is 'a' or 'e' or 'i' or 'o' or 'u' or 'y';
            if (vowel && !prevVowel) syl++;
            prevVowel = vowel;
        }
        return Math.Max(1, syl);
    }
}
