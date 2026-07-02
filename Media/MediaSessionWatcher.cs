using SpotifyLyricsTaskbar.Config;
using SpotifyLyricsTaskbar.Util;
using Windows.Media.Control;

namespace SpotifyLyricsTaskbar.Media;

public sealed record TrackInfo(string Title, string Artist, string Album, double DurationSeconds)
{
    public byte[]? AlbumArt { get; init; }
    /// <summary>Identity for caching + per-song offset. Intentionally duration-free: SMTC can
    /// report a slightly different duration between plays, which used to fragment the cache
    /// (a song's good synced lyric stopped being reused) and drop its saved offset. Duration is
    /// still passed to the providers to match the right lyric version.</summary>
    public string Key => $"{Artist}|{Title}".Trim().ToLowerInvariant();
    public bool HasMinimum => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>
/// Wraps the Windows System Media Transport Controls (SMTC) to report what
/// Spotify is playing, with no authentication. Position is anchored at each
/// update and interpolated between updates for smooth line-level sync.
/// Events may fire on background (WinRT) threads; consumers must marshal.
/// </summary>
public sealed class MediaSessionWatcher : IDisposable
{
    private readonly AppConfig _config;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    private readonly object _lock = new();
    private TimeSpan _lastPosition;
    private DateTimeOffset _anchor = DateTimeOffset.UtcNow;
    private double _rate = 1.0;
    private double _durationMs;
    private bool _isPlaying;
    private string? _currentKey;
    private DateTimeOffset _trackChangeAt = DateTimeOffset.MinValue;

    public event Action<TrackInfo>? TrackChanged;
    public event Action<bool>? PlaybackChanged;
    public event Action? Seeked;

    public bool IsPlaying { get { lock (_lock) { return _isPlaying; } } }

    public MediaSessionWatcher(AppConfig config) => _config = config;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_manager == null) { Log.Write("SMTC: manager unavailable"); return; }
            _manager.CurrentSessionChanged += (_, _) => HookSession();
            _manager.SessionsChanged += (_, _) => HookSession();
            HookSession();
        }
        catch (Exception ex) { Log.Write("SMTC start failed: " + ex.Message); }
    }

    private void HookSession()
    {
        try
        {
            var target = PickSession();
            if (ReferenceEquals(target, _session)) return;

            DetachSession();
            _session = target;
            if (_session == null)
            {
                lock (_lock) { _isPlaying = false; _currentKey = null; }
                PlaybackChanged?.Invoke(false);
                return;
            }

            _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;

            // Prime current state immediately.
            OnPlaybackInfoChanged(_session, null!);
            OnTimelineChanged(_session, null!);
            OnMediaPropertiesChanged(_session, null!);
        }
        catch (Exception ex) { Log.Write("SMTC hook failed: " + ex.Message); }
    }

    private void DetachSession()
    {
        if (_session == null) return;
        try
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        catch { /* ignore */ }
    }

    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager == null) return null;
        if (_config.SpotifyOnly)
        {
            foreach (var s in _manager.GetSessions())
            {
                var id = s.SourceAppUserModelId ?? string.Empty;
                if (id.Contains("spotify", StringComparison.OrdinalIgnoreCase))
                    return s;
            }
            return null; // Spotify not currently a media source
        }
        return _manager.GetCurrentSession();
    }

    private async void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        try
        {
            var props = await sender.TryGetMediaPropertiesAsync();
            if (props == null) return;

            var title = props.Title ?? string.Empty;
            var artist = props.Artist ?? string.Empty;
            var album = props.AlbumTitle ?? string.Empty;

            double durSec;
            lock (_lock) { durSec = _durationMs / 1000.0; }
            try
            {
                var tl = sender.GetTimelineProperties();
                var d = (tl.EndTime - tl.StartTime).TotalSeconds;
                if (d > 1) durSec = d;
            }
            catch { /* keep existing */ }

            var info = new TrackInfo(title, artist, album, durSec);
            bool changed;
            lock (_lock)
            {
                changed = info.Key != _currentKey;
                _currentKey = info.Key;
                if (durSec > 0) _durationMs = durSec * 1000.0;
                if (changed) _trackChangeAt = DateTimeOffset.UtcNow;
            }
            if (changed && info.HasMinimum)
            {
                byte[]? art = await ReadThumbnailAsync(props);
                TrackChanged?.Invoke(info with { AlbumArt = art });
            }
        }
        catch (Exception ex) { Log.Write("SMTC media props failed: " + ex.Message); }
    }

    private static async Task<byte[]?> ReadThumbnailAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        try
        {
            var thumb = props.Thumbnail;
            if (thumb == null) return null;
            using var stream = await thumb.OpenReadAsync();
            if (stream == null || stream.Size == 0 || stream.Size > 8_000_000) return null;
            using var reader = new Windows.Storage.Streams.DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch { return null; }
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        try
        {
            var pi = sender.GetPlaybackInfo();
            bool playing = pi?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            double rate = pi?.PlaybackRate ?? 1.0;
            if (rate <= 0) rate = 1.0;

            var tl = sender.GetTimelineProperties();
            lock (_lock)
            {
                _isPlaying = playing;
                _rate = rate;
                _lastPosition = tl.Position;
                _anchor = AnchorFrom(tl);
            }
            PlaybackChanged?.Invoke(playing);
        }
        catch (Exception ex) { Log.Write("SMTC playback failed: " + ex.Message); }
    }

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        try
        {
            var tl = sender.GetTimelineProperties();
            bool seek = false;
            lock (_lock)
            {
                // A large position jump on the same track (not just after a track change) = a manual seek.
                if (_durationMs > 0 && DateTimeOffset.UtcNow - _trackChangeAt > TimeSpan.FromSeconds(2))
                {
                    double delta = Math.Abs(tl.Position.TotalMilliseconds - EstimateUnlocked());
                    if (delta > 1500) seek = true;
                }
                _lastPosition = tl.Position;
                _anchor = AnchorFrom(tl);
                var d = (tl.EndTime - tl.StartTime).TotalMilliseconds;
                if (d > 1000) _durationMs = d;
            }
            if (seek) Seeked?.Invoke();
        }
        catch (Exception ex) { Log.Write("SMTC timeline failed: " + ex.Message); }
    }

    /// <summary>
    /// Anchor interpolation to the session's own LastUpdatedTime when it's sane, so we
    /// account for the delay between when the position was measured and when we received it.
    /// </summary>
    private static DateTimeOffset AnchorFrom(GlobalSystemMediaTransportControlsSessionTimelineProperties tl)
    {
        var now = DateTimeOffset.UtcNow;
        var lut = tl.LastUpdatedTime;
        return (lut.Year > 2000 && lut <= now.AddSeconds(2) && lut >= now.AddMinutes(-10)) ? lut : now;
    }

    /// <summary>Interpolated current playback position in milliseconds.</summary>
    private double EstimateUnlocked()
    {
        double pos = _lastPosition.TotalMilliseconds;
        if (_isPlaying) pos += (DateTimeOffset.UtcNow - _anchor).TotalMilliseconds * _rate;
        if (_durationMs > 0) pos = Math.Clamp(pos, 0, _durationMs);
        return pos;
    }

    public long EstimatePositionMs()
    {
        lock (_lock) { return (long)EstimateUnlocked(); }
    }

    // ---- Playback control (drives the actual Spotify session) ----
    public async void TogglePlayPause() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
    public async void Next() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
    public async void Previous() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }

    public void Dispose() => DetachSession();
}
