using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar.Lyrics.Providers;

/// <summary>LRCLIB lookups: exact /api/get and fuzzy /api/search.</summary>
public sealed class LrclibProvider
{
    private readonly HttpClient _http;
    public LrclibProvider(HttpClient http) => _http = http;

    private sealed class Dto
    {
        [JsonPropertyName("trackName")] public string? TrackName { get; set; }
        [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
        [JsonPropertyName("duration")] public double Duration { get; set; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
    }

    public async Task<LyricCandidate?> GetExactAsync(string title, string artist, string album, double durationSec, CancellationToken ct)
    {
        try
        {
            var url = "https://lrclib.net/api/get"
                + $"?artist_name={Uri.EscapeDataString(artist)}"
                + $"&track_name={Uri.EscapeDataString(title)}"
                + $"&album_name={Uri.EscapeDataString(album)}"
                + $"&duration={(int)Math.Round(durationSec)}";
            using var resp = await _http.GetAsync(url, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound || !resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var dto = await JsonSerializer.DeserializeAsync<Dto>(stream, cancellationToken: ct);
            return dto == null ? null : new LyricCandidate(dto.SyncedLyrics, dto.PlainLyrics, dto.Instrumental, "lrclib/get");
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log.Write("lrclib get error: " + ex.Message); return null; }
    }

    public async Task<LyricCandidate?> SearchBestAsync(string title, string artist, double durationSec, CancellationToken ct)
    {
        try
        {
            var url = "https://lrclib.net/api/search"
                + $"?track_name={Uri.EscapeDataString(title)}"
                + $"&artist_name={Uri.EscapeDataString(artist)}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var list = await JsonSerializer.DeserializeAsync<List<Dto>>(stream, cancellationToken: ct);
            if (list == null || list.Count == 0) return null;

            Dto? best = null;
            double bestScore = double.MaxValue;
            string wantTitle = MetadataNormalizer.Normalize(title);
            string wantArtist = MetadataNormalizer.Normalize(artist);
            foreach (var d in list)
            {
                if (!LrcParser.HasRealTimestamps(d.SyncedLyrics)) continue;
                double score = durationSec > 1 ? Math.Abs(d.Duration - durationSec) : 0;
                if (!string.Equals(MetadataNormalizer.Normalize(d.TrackName), wantTitle, StringComparison.Ordinal))
                    score += 2.5;
                if (!ArtistMatches(d.ArtistName, wantArtist)) score += 6; // prefer the right artist, don't require it
                if (score < bestScore) { bestScore = score; best = d; }
            }
            if (best == null) return null;

            // Only reject on a wildly-wrong duration; artist is a preference, not a gate.
            if (durationSec > 1 && Math.Abs(best.Duration - durationSec) > 14) return null;
            return new LyricCandidate(best.SyncedLyrics, best.PlainLyrics, best.Instrumental, "lrclib/search");
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex) { Log.Write("lrclib search error: " + ex.Message); return null; }
    }

    internal static bool ArtistMatches(string? candidate, string wantArtistNormalized)
    {
        if (wantArtistNormalized.Length == 0) return true; // unknown artist → don't restrict
        var a = MetadataNormalizer.Normalize(candidate);
        return a.Length > 0 && (a.Contains(wantArtistNormalized) || wantArtistNormalized.Contains(a));
    }
}
