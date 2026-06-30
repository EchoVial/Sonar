using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar.Lyrics.Providers;

/// <summary>
/// Genius as a plain-lyrics source. Genius has no timestamps, so results come back
/// as plain text and the orchestrator distributes them across the track as a rough
/// sync. Covers a lot of obscure tracks (rap/phonk/indie) that LRCLIB/NetEase miss.
/// Uses the public website search endpoint (no API token) + page scrape.
/// </summary>
public sealed partial class GeniusProvider
{
    private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";
    private readonly HttpClient _http;
    public GeniusProvider(HttpClient http) => _http = http;

    public async Task<LyricCandidate?> GetBestAsync(string title, string artist, CancellationToken ct)
    {
        try
        {
            var hit = await SearchAsync(title, artist, ct);
            if (hit is null) return null;
            var plain = await ScrapeLyricsAsync(hit.Value.url, ct);
            if (string.IsNullOrWhiteSpace(plain)) return null;
            return new LyricCandidate(null, plain, false, "genius");
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log.Write("genius error: " + ex.Message); return null; }
    }

    private async Task<(string url, string title, string artist)?> SearchAsync(string title, string artist, CancellationToken ct)
    {
        var q = Uri.EscapeDataString($"{title} {artist}".Trim());
        var url = $"https://genius.com/api/search/multi?per_page=5&q={q}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", Ua);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        if (!doc.RootElement.TryGetProperty("response", out var response)) return null;
        if (!response.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array) return null;

        string wantTitle = MetadataNormalizer.Normalize(title);
        string wantArtist = MetadataNormalizer.Normalize(artist);

        (string url, string title, string artist)? best = null;
        double bestScore = double.MaxValue;
        foreach (var section in sections.EnumerateArray())
        {
            if (!section.TryGetProperty("type", out var t) || t.GetString() != "song") continue;
            if (!section.TryGetProperty("hits", out var hits) || hits.ValueKind != JsonValueKind.Array) continue;
            foreach (var hit in hits.EnumerateArray())
            {
                if (!hit.TryGetProperty("result", out var r)) continue;
                string? hitUrl = r.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(hitUrl)) continue;
                string hitTitle = r.TryGetProperty("title", out var ti) ? (ti.GetString() ?? "") : "";
                string hitArtist = r.TryGetProperty("primary_artist", out var pa) && pa.TryGetProperty("name", out var pn)
                    ? (pn.GetString() ?? "") : "";

                string nTitle = MetadataNormalizer.Normalize(hitTitle);
                string nArtist = MetadataNormalizer.Normalize(hitArtist);

                double score = 0;
                if (nTitle != wantTitle) score += nTitle.Contains(wantTitle) || wantTitle.Contains(nTitle) ? 1 : 4;
                if (!ArtistMatches(nArtist, wantArtist)) score += 5;
                if (score < bestScore) { bestScore = score; best = (hitUrl, hitTitle, hitArtist); }
            }
        }

        // Don't accept a wildly-wrong hit (neither title nor artist matched).
        if (best is null || bestScore >= 9) return null;
        return best;
    }

    private async Task<string?> ScrapeLyricsAsync(string pageUrl, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, pageUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", Ua);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        var html = await resp.Content.ReadAsStringAsync(ct);

        var sb = new StringBuilder();
        foreach (var inner in ExtractContainers(html, "data-lyrics-container=\"true\""))
        {
            // Genius nests its header chrome ("N Contributors", translations, buttons) inside the
            // lyrics container as data-exclude-from-selection="true" — strip those subtrees first,
            // then what remains is the actual lyric text.
            var body = RemoveSubtrees(inner, "data-exclude-from-selection=\"true\"");
            body = BreakRegex().Replace(body, "\n");   // <br> → newline
            body = TagRegex().Replace(body, "");        // strip remaining tags
            sb.Append(WebUtility.HtmlDecode(body)).Append('\n');
        }

        var lines = new List<string>();
        foreach (var raw in sb.ToString().Replace("\r\n", "\n").Split('\n'))
        {
            var line = EmbedRegex().Replace(raw.Trim(), string.Empty).Trim(); // strip trailing "27Embed"
            if (line.Length == 0) continue;
            if (line.Equals("You might also like", StringComparison.OrdinalIgnoreCase)) continue; // Genius ad row
            if (SectionHeaderRegex().IsMatch(line)) continue;   // drop "[Verse 1]" / "[Chorus]" headers
            if (HeaderNoiseRegex().IsMatch(line)) continue;     // belt-and-suspenders: stray contributor/translation text
            lines.Add(line);
        }
        return lines.Count >= 2 ? string.Join('\n', lines) : null;
    }

    /// <summary>Inner HTML of every element whose opening tag contains <paramref name="marker"/>,
    /// honouring &lt;div&gt; nesting so extraction never stops at a nested close tag.</summary>
    private static IEnumerable<string> ExtractContainers(string html, string marker)
    {
        int from = 0;
        while (true)
        {
            int at = html.IndexOf(marker, from, StringComparison.Ordinal);
            if (at < 0) yield break;
            int tagEnd = html.IndexOf('>', at);
            if (tagEnd < 0) yield break;
            int end = MatchingDivEnd(html, tagEnd + 1, out int closeStart);
            if (end < 0) yield break;
            yield return html.Substring(tagEnd + 1, closeStart - (tagEnd + 1));
            from = end;
        }
    }

    /// <summary>Remove every &lt;div&gt; subtree whose opening tag contains <paramref name="marker"/>.</summary>
    private static string RemoveSubtrees(string html, string marker)
    {
        while (true)
        {
            int at = html.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0) return html;
            int divStart = html.LastIndexOf("<div", at, StringComparison.Ordinal);
            int tagEnd = html.IndexOf('>', at);
            if (divStart < 0 || tagEnd < 0) return html;
            int end = MatchingDivEnd(html, tagEnd + 1, out _);
            if (end < 0) return html;
            html = html.Remove(divStart, end - divStart);
        }
    }

    /// <summary>From an index just past a &lt;div&gt; open (depth 1), find the index past its matching
    /// &lt;/div&gt; and where that close tag starts. Returns -1 if unbalanced.</summary>
    private static int MatchingDivEnd(string html, int i, out int closeStart)
    {
        closeStart = -1;
        int depth = 1;
        while (i < html.Length && depth > 0)
        {
            int no = html.IndexOf("<div", i, StringComparison.Ordinal);
            int nc = html.IndexOf("</div", i, StringComparison.Ordinal);
            if (nc < 0) return -1;
            if (no >= 0 && no < nc) { depth++; i = no + 4; }
            else
            {
                if (--depth == 0) { closeStart = nc; int gt = html.IndexOf('>', nc); return gt < 0 ? html.Length : gt + 1; }
                i = nc + 5;
            }
        }
        return -1;
    }

    private static bool ArtistMatches(string nArtist, string wantArtist)
    {
        if (wantArtist.Length == 0 || nArtist.Length == 0) return wantArtist.Length == 0;
        return nArtist.Contains(wantArtist) || wantArtist.Contains(nArtist);
    }

    [GeneratedRegex(@"^(\d[\d,]*\s+Contributors?|Translations?|Romanization|Read More)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex HeaderNoiseRegex();

    [GeneratedRegex(@"\d*Embed$", RegexOptions.Compiled)]
    private static partial Regex EmbedRegex();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Compiled)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^\[[^\]]*\]$", RegexOptions.Compiled)]
    private static partial Regex SectionHeaderRegex();
}
