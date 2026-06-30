using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar.Lyrics.Providers;

/// <summary>
/// Best-effort secondary source. NetEase Cloud Music tends to cover obscure
/// electronic/phonk tracks LRCLIB lacks. The unofficial endpoints can change or
/// be geo-limited, so every failure is swallowed and treated as "no match".
/// </summary>
public sealed partial class NetEaseProvider
{
    private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";
    private readonly HttpClient _http;
    public NetEaseProvider(HttpClient http) => _http = http;

    public async Task<LyricCandidate?> GetBestAsync(string title, string artist, double durationSec, CancellationToken ct)
    {
        try
        {
            long? id = await SearchSongIdAsync(title, artist, durationSec, ct);
            if (id == null) return null;
            var lrc = await FetchLyricAsync(id.Value, ct);
            return LrcParser.HasRealTimestamps(lrc) ? new LyricCandidate(lrc, null, false, "netease") : null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log.Write("netease error: " + ex.Message); return null; }
    }

    private async Task<long?> SearchSongIdAsync(string title, string artist, double durationSec, CancellationToken ct)
    {
        var q = Uri.EscapeDataString($"{title} {artist}".Trim());
        var url = $"https://music.163.com/api/search/get?s={q}&type=1&limit=10&offset=0";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://music.163.com");
        req.Headers.TryAddWithoutValidation("User-Agent", Ua);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
        if (!result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return null;

        string wantTitle = MetadataNormalizer.Normalize(title);
        string wantArtist = MetadataNormalizer.Normalize(artist);
        long best = 0;
        double bestDur = 0;
        double bestScore = double.MaxValue;
        foreach (var s in songs.EnumerateArray())
        {
            if (!s.TryGetProperty("id", out var idEl)) continue;
            long sid = idEl.GetInt64();
            if (sid == 0) continue;
            string name = s.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? string.Empty) : string.Empty;
            double songDurSec = s.TryGetProperty("duration", out var dEl) ? dEl.GetDouble() / 1000.0 : 0;

            double score = (durationSec > 1 && songDurSec > 1) ? Math.Abs(songDurSec - durationSec) : 5;
            if (!string.Equals(MetadataNormalizer.Normalize(name), wantTitle, StringComparison.Ordinal)) score += 3;
            if (!ArtistMatchesAny(s, wantArtist)) score += 6; // prefer the right artist, don't require it
            if (score < bestScore) { bestScore = score; best = sid; bestDur = songDurSec; }
        }

        if (best == 0) return null;
        // Only reject on a wildly-wrong duration; artist is a preference, not a gate.
        if (durationSec > 1 && bestDur > 1 && Math.Abs(bestDur - durationSec) > 15) return null;
        return best;
    }

    private static bool ArtistMatchesAny(JsonElement song, string wantArtist)
    {
        if (wantArtist.Length == 0) return true;
        foreach (var key in new[] { "artists", "ar" })
        {
            if (song.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var a in arr.EnumerateArray())
                    if (a.TryGetProperty("name", out var nm) && LrclibProvider.ArtistMatches(nm.GetString(), wantArtist))
                        return true;
        }
        return false;
    }

    private async Task<string?> FetchLyricAsync(long id, CancellationToken ct)
    {
        // v1 endpoint returns yrc (word-by-word) when available, plus the plain lrc.
        var url = $"https://music.163.com/api/song/lyric/v1?id={id}&cp=false&lv=1&kv=1&tv=-1&yv=1";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://music.163.com");
        req.Headers.TryAddWithoutValidation("User-Agent", Ua);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        // Prefer word-level (yrc) → convert to enhanced LRC for precise sync.
        if (root.TryGetProperty("yrc", out var yrc) && yrc.TryGetProperty("lyric", out var yl))
        {
            var enhanced = ConvertYrc(yl.GetString());
            if (LrcParser.HasRealTimestamps(enhanced)) return enhanced;
        }
        if (root.TryGetProperty("lrc", out var lrc) && lrc.TryGetProperty("lyric", out var lyric))
            return lyric.GetString();
        return null;
    }

    [GeneratedRegex(@"^\[(\d+),(\d+)\]", RegexOptions.Compiled)]
    private static partial Regex YrcLineRegex();

    [GeneratedRegex(@"\((\d+),(\d+),\d+\)([^()]*)", RegexOptions.Compiled)]
    private static partial Regex YrcWordRegex();

    /// <summary>Convert NetEase "yrc" karaoke lyrics into enhanced LRC (inline &lt;mm:ss.xx&gt; word tags).</summary>
    private static string? ConvertYrc(string? yrc)
    {
        if (string.IsNullOrWhiteSpace(yrc)) return null;
        var sb = new StringBuilder();
        int lines = 0;
        foreach (var raw in yrc.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            var lm = YrcLineRegex().Match(line);
            if (!lm.Success) continue; // skip JSON metadata / non-yrc lines
            var words = YrcWordRegex().Matches(line);
            if (words.Count == 0) continue;

            sb.Append('[').Append(FormatTime(long.Parse(lm.Groups[1].Value))).Append(']');
            foreach (Match w in words)
                sb.Append('<').Append(FormatTime(long.Parse(w.Groups[1].Value))).Append('>').Append(w.Groups[3].Value);
            sb.Append('\n');
            lines++;
        }
        return lines >= 2 ? sb.ToString() : null;
    }

    private static string FormatTime(long ms)
    {
        long cs = ms / 10;            // centiseconds
        return $"{cs / 6000:00}:{cs / 100 % 60:00}.{cs % 100:00}";
    }
}
