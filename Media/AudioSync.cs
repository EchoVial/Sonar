using SpotifyLyricsTaskbar.Config;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar.Media;

/// <summary>
/// Turns Spotify's audio into a live timing correction. It tracks the short‑time energy
/// envelope, flags onsets (energy jumping above an adaptive floor), and for each onset asks
/// how far the nearest lyric line's timestamp sits from the real playback position. The median
/// of recent gaps — smoothed and clamped — becomes <see cref="AppConfig.AudioCorrectionMs"/>,
/// so the lyrics drift back onto the vocal. Fast (energy only, no model); corrects constant
/// lag/lead, not individual words. Feeds arrive on the capture thread.
/// </summary>
public sealed class AudioSync
{
    private readonly AppConfig _config;
    private readonly Func<long> _getPositionMs;
    private readonly Func<long, long> _nearestLineMs; // position → nearest line ms, or long.MinValue

    private double _prevEnergy;    // last frame energy (for flux)
    private double _fluxAvg;       // adaptive flux baseline
    private long _lastOnsetPos = long.MinValue;
    private readonly List<int> _gaps = new();
    private long _lastLogPos = long.MinValue;

    public AudioSync(AppConfig config, Func<long> getPositionMs, Func<long, long> nearestLineMs)
    {
        _config = config;
        _getPositionMs = getPositionMs;
        _nearestLineMs = nearestLineMs;
    }

    public void Reset()
    {
        _prevEnergy = _fluxAvg = 0;
        _lastOnsetPos = long.MinValue;
        _lastLogPos = long.MinValue;
        _gaps.Clear();
        _config.AudioCorrectionMs = 0;
    }

    /// <summary>Feed a mono chunk (~10 ms) from the capture.</summary>
    public void Feed(float[] samples, int sampleRate)
    {
        if (samples.Length == 0) return;
        double sum = 0;
        for (int i = 0; i < samples.Length; i++) sum += samples[i] * samples[i];
        double e = Math.Sqrt(sum / samples.Length);

        // Onset = a positive jump in energy (spectral‑flux proxy) above an adaptive baseline —
        // fires on transients within continuous music, not just silence→sound.
        double flux = Math.Max(0, e - _prevEnergy);
        _prevEnergy = e;
        _fluxAvg = _fluxAvg <= 0 ? flux : _fluxAvg * 0.95 + flux * 0.05;

        long pos = _getPositionMs();
        bool onset = e > 0.012 && flux > Math.Max(0.006, _fluxAvg * 2.3) && pos - _lastOnsetPos > 200;
        if (!onset) return;
        _lastOnsetPos = pos;

        long line = _nearestLineMs(pos);
        if (line == long.MinValue) return;

        int baseOffset = _config.SyncOffsetMs + _config.CurrentSongOffset;
        int gap = (int)(line - pos - baseOffset); // >0: lyric line sits later than the vocal → show sooner
        if (Math.Abs(gap) >= 4000) return;         // implausible → ignore (likely a non‑vocal hit)

        _gaps.Add(gap);
        if (_gaps.Count > 14) _gaps.RemoveAt(0);
        if (_gaps.Count < 4) return;               // need a few before trusting it

        // Robust central tendency, then ease the live correction toward it.
        var sorted = new List<int>(_gaps); sorted.Sort();
        int median = sorted[sorted.Count / 2];
        int cur = _config.AudioCorrectionMs;
        _config.AudioCorrectionMs = Math.Clamp(cur + (int)Math.Round(0.2 * (median - cur)), -8000, 8000);

        if (pos - _lastLogPos > 3000)
        {
            _lastLogPos = pos;
            Log.Write($"audiosync: pos={pos} median={median} correction={_config.AudioCorrectionMs} (n={_gaps.Count})");
        }
    }
}
