using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpotifyLyricsTaskbar.Config;
using SpotifyLyricsTaskbar.Lyrics;
using SpotifyLyricsTaskbar.Media;
using SpotifyLyricsTaskbar.Overlay;
using SpotifyLyricsTaskbar.Startup;
using SpotifyLyricsTaskbar.Taskbar;
using SpotifyLyricsTaskbar.Tray;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar;

public partial class App : Application
{
    private Mutex? _mutex;
    private AppConfig _config = null!;
    private MediaSessionWatcher _watcher = null!;
    private LyricsProvider _lyrics = null!;
    private LyricScheduler _scheduler = null!;
    private SpotifyAudioCapture _audioCapture = null!;
    private AudioSync _audioSync = null!;
    private OverlayWindow _overlay = null!;
    private VisibilityController _visibility = null!;
    private TrayIcon _tray = null!;

    private DispatcherTimer _tick = null!;     // advances the active line while shown
    private DispatcherTimer _backstop = null!; // re-checks taskbar visibility while playing
    private DispatcherTimer _idleTrim = null!; // one-shot working-set trim when idle
    private DispatcherTimer _learn = null!;    // folds a settled audio correction into the song's offset
    private DispatcherTimer _periodicTrim = null!; // keeps the working set trimmed while active
    private int _lastLearnCorr;
    private DateTime _corrStableSince = DateTime.UtcNow;

    private bool _taskbarVisible = true;
    private bool _isPlaying;
    private bool _hasTrack;
    private CancellationTokenSource? _fetchCts;

    private bool _introPending;
    private ImageSource? _introArt;
    private string _introLabel = string.Empty;
    private DispatcherTimer? _introTimer;
    private DateTime _introStartedAt;
    private Settings.SettingsWindow? _settings;

    public Color? CurrentAlbumColor { get; private set; }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Hidden diagnostic: validate the lyrics fallback chain without playback.
        //   SpotifyLyricsTaskbar.exe --test-lyrics "Artist" "Title" [durationSeconds]
        if (e.Args.Length >= 1 && e.Args[0] == "--test-lyrics")
        {
            _ = RunLyricTestAsync(e.Args);
            return;
        }

        // Hidden diagnostic: exercise only the Genius scraper (no priority chain).
        //   Sonar.exe --test-genius "Artist" "Title"
        if (e.Args.Length >= 1 && e.Args[0] == "--test-genius")
        {
            _ = RunGeniusTestAsync(e.Args);
            return;
        }

        // Hidden diagnostic: parse a (synthetic) LRC string and log the cleaned text + word tokens.
        if (e.Args.Length >= 2 && e.Args[0] == "--test-parse")
        {
            foreach (var l in Lyrics.LrcParser.Parse(e.Args[1]))
            {
                var sb = new System.Text.StringBuilder();
                if (l.Words != null) foreach (var w in l.Words) sb.Append('[').Append(w.Text.TrimEnd()).Append(']');
                Log.Write($"PARSE text=<{l.Text}> words={sb}");
            }
            Shutdown();
            return;
        }

        // Hidden diagnostic: verify Spotify‑only audio capture works (logs levels for ~12s).
        //   Sonar.exe --test-audio     (play a song in Spotify first)
        if (e.Args.Length >= 1 && e.Args[0] == "--test-audio")
        {
            _config = AppConfig.Load();
            int pid = FindSpotifyPid();
            Log.Write($"AUDIO test: spotify pid={pid}");
            var cap = new SpotifyAudioCapture();
            double peak = 0; long total = 0;
            cap.FrameReady += (samples, _) =>
            {
                double s = 0; foreach (var v in samples) s += v * v;
                double rms = samples.Length > 0 ? Math.Sqrt(s / samples.Length) : 0;
                if (rms > peak) peak = rms;
                total += samples.Length;
            };
            cap.Start(pid);
            int ticks = 0;
            var at = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            at.Tick += (_, _) =>
            {
                Log.Write($"AUDIO test t={ticks + 1}s capturing={cap.IsCapturing} peakRms={peak:F4} samples={total}");
                peak = 0;
                if (++ticks >= 12) { at.Stop(); cap.Stop(); Shutdown(); }
            };
            at.Start();
            return;
        }

        // Hidden diagnostic: report taskbar geometry + detected cluster edge + computed placement.
        if (e.Args.Length >= 1 && e.Args[0] == "--test-layout")
        {
            _config = AppConfig.Load();
            var hb = TaskbarTracker.GetTaskbarHandle();
            string rectStr = TaskbarTracker.TryGetTaskbarRect(out var rr)
                ? $"L={rr.Left} R={rr.Right} W={rr.Width} H={rr.Height}" : "none";
            string region = TaskbarLayout.GetEmptyRegion(hb, in rr) is { } reg ? $"{reg.left}..{reg.right} (w={reg.right - reg.left})" : "null(fallback)";
            var pl = TaskbarTracker.GetPlacement(_config);
            string plStr = pl is { } p ? $"Left={p.Left} Top={p.Top} Width={p.Width} Height={p.Height} dpi={p.Dpi}" : "null";
            Log.Write($"LAYOUT: rect[{rectStr}] emptyRegion[{region}] placement[{plStr}]");
            Shutdown();
            return;
        }

        // Hidden: open just the Settings window (for previewing the UI).
        if (e.Args.Length >= 1 && e.Args[0] == "--settings")
        {
            _config = AppConfig.Load();
            new Settings.SettingsWindow(this).Show();
            return;
        }

        // Hidden diagnostic: render one lyric line to a PNG at a given DIP width (verifies wrapping).
        //   Sonar.exe --render-line "the full line text" <widthDip> <outPath>
        if (e.Args.Length >= 4 && e.Args[0] == "--render-line")
        {
            _config = AppConfig.Load();
            _overlay = new OverlayWindow(_config);
            _overlay.Show();
            var words = e.Args[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int widthDip = int.TryParse(e.Args[2], out var wv) ? wv : 600;
            string outPath = e.Args[3];
            _overlay.ApplyPlacement(new Taskbar.OverlayPlacement(0, 0, widthDip, 48, 96)); // so FitOneLine targets widthDip
            _overlay.ApplyView(new LyricView(0, words, words.Length, Snap: true));
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                _overlay.RenderTo(outPath, widthDip, 48);
                Log.Write($"RENDER: wrote {outPath} at {widthDip}dip");
                Shutdown();
            }));
            return;
        }

        // Hidden visual demo: render the overlay with sample text (no Spotify needed).
        //   Sonar.exe --demo ["sample line"]
        if (e.Args.Length >= 1 && e.Args[0] == "--demo")
        {
            _config = AppConfig.Load();
            _overlay = new OverlayWindow(_config);
            _overlay.Show();
            ApplyPlacement();
            var demoText = e.Args.Length > 1 ? e.Args[1] : "One more time we're gonna celebrate";
            var demoWords = demoText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            _overlay.FadeShow();
            _overlay.ShowIntro(MakeDemoArt(), "Daft Punk — One More Time");
            var introT = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(300, _config.IntroMs)) };
            introT.Tick += (_, _) =>
            {
                introT.Stop();
                _overlay.EndIntro(string.Empty, () =>
                {
                    int reveal = 0;
                    var demoTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
                    demoTimer.Tick += (_, _) =>
                    {
                        if (reveal >= demoWords.Length) { demoTimer.Stop(); return; }
                        reveal++;
                        _overlay.ApplyView(new LyricView(0, demoWords, reveal));
                    };
                    demoTimer.Start();
                });
            };
            introT.Start();
            return;
        }

        _mutex = new Mutex(true, @"Local\Sonar.SingleInstance", out bool isNew);
        if (!isNew) { Shutdown(); return; }

        DispatcherUnhandledException += (_, args) => { Log.Write("UI exception: " + args.Exception); args.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write("Fatal: " + args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Write("Task exception: " + args.Exception); args.SetObserved(); };

        _config = AppConfig.Load();
        Log.Write("startup");

        StartMenu.EnsureShortcut(); // show up in the Start menu like an installed app
        // Keep the registry Run entry in sync with the saved preference.
        if (AutoStart.IsEnabled() != _config.RunAtStartup) AutoStart.Set(_config.RunAtStartup);

        _overlay = new OverlayWindow(_config);
        _overlay.Show(); // creates the HWND (stays invisible at opacity 0 / Hidden)
        ApplyPlacement();

        _lyrics = new LyricsProvider(_config);
        _lyrics.Warmup();
        _scheduler = new LyricScheduler(() => _watcher?.EstimatePositionMs() ?? 0, _config);
        _scheduler.ViewChanged += view => _overlay.ApplyView(view);

        _audioCapture = new SpotifyAudioCapture();
        _audioSync = new AudioSync(_config, () => _watcher?.EstimatePositionMs() ?? 0, pos => _scheduler.NearestLineMs(pos, 3500));
        _audioCapture.FrameReady += (s, sr) => _audioSync.Feed(s, sr);

        _tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(60, _config.TickIntervalMs)) };
        _tick.Tick += (_, _) => _scheduler.Tick();

        _backstop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _backstop.Tick += (_, _) =>
        {
            _visibility.Reevaluate();
            // Re-place while playing so taskbar alignment / icon changes are picked up live
            // (the empty-region probe itself is cached, so this is cheap).
            if (_taskbarVisible && _isPlaying) ApplyPlacement();
        };

        _idleTrim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _idleTrim.Tick += (_, _) => { _idleTrim.Stop(); MemoryTrim.Trim(); };

        _learn = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _learn.Tick += (_, _) => LearnOffsetTick();

        // Audio sync keeps the app "active", so the idle trim rarely fires — trim on a cadence
        // instead (cheap; evicts cold startup/JIT pages). Keeps the working set ~40–60 MB with
        // capture running, vs a ~175 MB untrimmed startup peak.
        _periodicTrim = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _periodicTrim.Tick += (_, _) => MemoryTrim.Trim();

        _visibility = new VisibilityController(OnTaskbarVisibilityChanged);
        _visibility.Start();

        _watcher = new MediaSessionWatcher(_config);
        _watcher.TrackChanged += OnTrackChanged;
        _watcher.PlaybackChanged += OnPlaybackChanged;
        _watcher.Seeked += OnSeeked;

        _tray = new TrayIcon(_config, this);
        _tray.Show();

        _ = _watcher.StartAsync();
        RefreshOverlayState();
        _idleTrim.Start(); // trim startup pages once we've settled
        _learn.Start();
        _periodicTrim.Start();
    }

    /// <summary>
    /// Once the audio correction settles for a song, fold it into that song's persistent offset:
    /// next time it starts already-corrected and the live correction shrinks toward zero, so the
    /// work is "learned" and offloaded. Re-fires if a residual drift builds up again.
    /// </summary>
    private void LearnOffsetTick()
    {
        if (!_config.AudioSyncEnabled || !_isPlaying || string.IsNullOrEmpty(CurrentTrackKey)) return;
        if (_scheduler.FirstLineMs() == long.MaxValue) return; // no timed lyrics → nothing to learn

        int corr = _config.AudioCorrectionMs;
        if (Math.Abs(corr - _lastLearnCorr) > 60) { _lastLearnCorr = corr; _corrStableSince = DateTime.UtcNow; return; }
        if (Math.Abs(corr) < 40 || (DateTime.UtcNow - _corrStableSince).TotalSeconds < 12) return;

        int learned = Math.Clamp(_config.CurrentSongOffset + corr, -12000, 12000);
        _config.SongOffsets[CurrentTrackKey!] = learned;
        _config.CurrentSongOffset = learned;   // apply as the new stable base
        _config.AudioCorrectionMs = 0;          // live correction folded in → back to ~0
        _audioSync.ClearGaps();
        _config.Save();
        _lastLearnCorr = 0;
        _corrStableSince = DateTime.UtcNow;
        Log.Write($"audiosync: learned {learned}ms for {CurrentTrackKey}");
    }

    private void ApplyPlacement()
    {
        if (TaskbarTracker.GetPlacement(_config) is { } pl) _overlay.ApplyPlacement(pl);
    }

    private async Task RunLyricTestAsync(string[] args)
    {
        var cfg = AppConfig.Load();
        var lyrics = new LyricsProvider(cfg);
        string artist = args.Length > 1 ? args[1] : string.Empty;
        string title = args.Length > 2 ? args[2] : string.Empty;
        double dur = args.Length > 3 && double.TryParse(args[3], out var d) ? d : 0;

        Log.Write($"TEST start: '{artist}' - '{title}' ({dur}s)");
        try
        {
            var set = await lyrics.GetAsync(new TrackInfo(title, artist, string.Empty, dur), CancellationToken.None);
            int wordLevel = 0;
            foreach (var l in set.Lines) if (l.Words is { Count: > 0 }) wordLevel++;
            Log.Write($"TEST result: kind={set.Kind} source={set.Source} lines={set.Lines.Count} wordLevelLines={wordLevel}");
        }
        catch (Exception ex) { Log.Write("TEST error: " + ex); }
        Shutdown();
    }

    /// <summary>Root Spotify process (the UI/browser process; its tree includes the audio child).</summary>
    private static int FindSpotifyPid()
    {
        try
        {
            var procs = System.Diagnostics.Process.GetProcessesByName("Spotify");
            foreach (var p in procs) if (p.MainWindowHandle != IntPtr.Zero) return p.Id;
            return procs.Length > 0 ? procs[0].Id : 0;
        }
        catch { return 0; }
    }

    private async Task RunGeniusTestAsync(string[] args)
    {
        string artist = args.Length > 1 ? args[1] : string.Empty;
        string title = args.Length > 2 ? args[2] : string.Empty;
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var genius = new Lyrics.Providers.GeniusProvider(http);
        Log.Write($"GENIUS test start: '{artist}' - '{title}'");
        try
        {
            var c = await genius.GetBestAsync(title, artist, CancellationToken.None);
            int lines = string.IsNullOrEmpty(c?.Plain) ? 0 : c!.Plain!.Split('\n').Length;
            string first = c?.Plain?.Split('\n')[0] ?? string.Empty;
            bool noise = System.Text.RegularExpressions.Regex.IsMatch(first, "contributor|translation", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Log.Write($"GENIUS test result: hit={(c != null)} source={c?.Source} plainLines={lines} firstLineNoise={noise} firstLen={first.Length}");
        }
        catch (Exception ex) { Log.Write("GENIUS test error: " + ex); }
        Shutdown();
    }

    // ---- Media events (may arrive on background threads) --------------------

    private void OnTrackChanged(TrackInfo track) => Dispatcher.InvokeAsync(() =>
    {
        _hasTrack = true;
        _scheduler.Clear(); // immediately drop the previous song's line
        _scheduler.SetPending(true); // show ♪ between the intro card and the first line while fetching
        _audioSync?.Reset(); // new song → clear the learned audio correction
        CurrentTrackKey = track.Key;
        _config.CurrentSongOffset = _config.SongOffsets.TryGetValue(track.Key, out var so) ? so : 0;
        _lastLearnCorr = 0; _corrStableSince = DateTime.UtcNow; // reset learning for the new song
        _introArt = BuildBitmap(track.AlbumArt);
        _introLabel = IntroLabel(track);
        CurrentAlbumColor = ColorTools.ExtractAlbumColor(track.AlbumArt);
        _overlay.SetAlbumColor(CurrentAlbumColor);
        _introPending = _config.ShowIntro;
        RefreshOverlayState();
        FetchLyrics(track);
    });

    private void StartIntro()
    {
        _introStartedAt = DateTime.UtcNow;
        _overlay.ShowIntro(_introArt, _introLabel);
        _introTimer?.Stop();
        _introTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(300, _config.IntroMs)) };
        _introTimer.Tick += (_, _) =>
        {
            _introTimer!.Stop();
            _overlay.EndIntro(string.Empty, AfterIntro); // dissolve the card text, then lyrics fade in
        };
        _introTimer.Start();
    }

    /// <summary>After the intro ends, re-emit the current line so the first lyrics always appear.</summary>
    private void AfterIntro()
    {
        _scheduler.ForceNext();
        _scheduler.Tick();
    }

    /// <summary>Once lyrics are known, shorten/skip the intro so it never overruns the first line.</summary>
    private void AdjustIntroForLyrics()
    {
        if (_introTimer is null || !_introTimer.IsEnabled) return;
        long firstMs = _scheduler.FirstLineMs();
        // No timed lyrics → let the intro finish normally so the card dissolves into the
        // title-only display, which follows the chosen colour / album / RGB mode.
        if (firstMs == long.MaxValue) return;

        long pos = _watcher?.EstimatePositionMs() ?? 0;
        long until = firstMs - pos;
        long desiredTotal = Math.Min(_config.IntroMs, Math.Max(0, until - _config.ScrambleMs - 150));
        double remaining = desiredTotal - (DateTime.UtcNow - _introStartedAt).TotalMilliseconds;

        _introTimer.Stop();
        if (remaining <= 30)
            _overlay.EndIntro(string.Empty, AfterIntro);
        else
        {
            _introTimer.Interval = TimeSpan.FromMilliseconds(remaining);
            _introTimer.Start();
        }
    }

    private void OnSeeked() => Dispatcher.InvokeAsync(() =>
    {
        if (!_hasTrack) return;
        _introPending = false;
        _introTimer?.Stop();
        _overlay.CancelIntro();   // skip the card/animation on a manual seek
        _scheduler.Snap();        // jump straight to the line at the new position
        RefreshOverlayState();    // its tick consumes the snap
    });

    private static string IntroLabel(TrackInfo t)
        => string.IsNullOrWhiteSpace(t.Artist) ? t.Title : $"{t.Artist} — {t.Title}";

    private static ImageSource? BuildBitmap(byte[]? data)
    {
        if (data is not { Length: > 0 }) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(data);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static ImageSource MakeDemoArt()
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(Colors.MediumPurple, Colors.DeepPink, 45), null, new Rect(0, 0, 100, 100));
            dc.DrawEllipse(Brushes.White, null, new Point(50, 50), 16, 16);
        }
        var rtb = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private async void FetchLyrics(TrackInfo track)
    {
        _fetchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _fetchCts = cts;
        try
        {
            var set = await _lyrics.GetAsync(track, cts.Token);
            if (cts.IsCancellationRequested) return;
            _scheduler.SetLyrics(set);
            AdjustIntroForLyrics(); // speed up / skip the intro if the first line is imminent
            RefreshOverlayState();
        }
        catch (OperationCanceledException) { /* superseded by a newer track */ }
        catch (Exception ex) { Log.Write("fetch error: " + ex.Message); }
    }

    private void OnPlaybackChanged(bool playing) => Dispatcher.InvokeAsync(() =>
    {
        _isPlaying = playing;
        if (playing) { if (!_backstop.IsEnabled) _backstop.Start(); }
        else _backstop.Stop();
        RefreshOverlayState();
    });

    // ---- Visibility (already on the UI thread) ------------------------------

    private void OnTaskbarVisibilityChanged(bool visible)
    {
        _taskbarVisible = visible;
        if (visible) _overlay.ReassertTopmost();
        RefreshOverlayState();
    }

    private void RefreshOverlayState()
    {
        bool active = _config.Enabled && _taskbarVisible && _hasTrack && (_isPlaying || !_config.HideWhenPaused);
        if (active)
        {
            _idleTrim.Stop();
            ApplyPlacement();
            _overlay.FadeShow();
            if (_introPending) { _introPending = false; StartIntro(); }
            if (!_tick.IsEnabled) _tick.Start();
            _scheduler.Tick();
        }
        else
        {
            _tick.Stop();
            _overlay.FadeHide();
            _idleTrim.Stop();
            _idleTrim.Start(); // trim ~2s after going idle
        }
        RefreshAudioSync();
    }

    /// <summary>Start/stop the Spotify audio capture + sync based on the toggle and playback.</summary>
    private void RefreshAudioSync()
    {
        if (_audioCapture is null) return;
        bool shouldRun = _config.AudioSyncEnabled && _hasTrack && _isPlaying;
        if (shouldRun && !_audioCapture.IsCapturing)
        {
            _audioSync.Reset();
            _audioCapture.Start(FindSpotifyPid());
        }
        else if (!shouldRun && _audioCapture.IsCapturing)
        {
            _audioCapture.Stop();
            _config.AudioCorrectionMs = 0;
            _scheduler.Tick();
        }
    }

    // ---- Tray actions -------------------------------------------------------

    public AppConfig Config => _config;
    public bool IsPlaying => _isPlaying;

    /// <summary>Human-readable audio-sync state for the settings UI.</summary>
    public string AudioSyncStatus
    {
        get
        {
            if (!_config.AudioSyncEnabled) return string.Empty;
            if (_audioCapture is null || !_audioCapture.IsCapturing)
                return _isPlaying ? "starting…" : "waiting for playback";
            int c = _config.AudioCorrectionMs;
            return c == 0 ? "listening to Spotify… (no correction yet)" : $"correcting {(c > 0 ? "+" : "")}{c} ms from audio";
        }
    }
    public string? CurrentTrackKey { get; private set; }
    public int EffectiveSongOffset => !string.IsNullOrEmpty(CurrentTrackKey) ? _config.CurrentSongOffset : _config.SyncOffsetMs;

    /// <summary>The Offset control nudges the current song (remembered per track); if nothing is playing, the global offset.</summary>
    public void SetSongOffset(int ms)
    {
        if (!string.IsNullOrEmpty(CurrentTrackKey))
        {
            _config.SongOffsets[CurrentTrackKey] = ms;
            _config.CurrentSongOffset = ms;
        }
        else _config.SyncOffsetMs = ms;
        _config.Save();
        _scheduler?.Tick();
    }

    public void MediaPlayPause() => _watcher?.TogglePlayPause();
    public void MediaNext() => _watcher?.Next();
    public void MediaPrevious() => _watcher?.Previous();

    public void OpenSettings()
    {
        if (_settings is { IsLoaded: true }) { _settings.Activate(); return; }
        _settings = new Settings.SettingsWindow(this);
        _settings.Closed += (_, _) => _settings = null;
        _settings.Show();
        _settings.Activate();
    }

    /// <summary>Apply current config to the running overlay/scheduler/timers without restarting.</summary>
    public void ApplyLiveConfig()
    {
        if (_overlay is null || _scheduler is null) return; // e.g. --settings preview mode
        _overlay.RefreshVisuals(_config);
        _tick.Interval = TimeSpan.FromMilliseconds(Math.Max(60, _config.TickIntervalMs));
        RefreshOverlayState();
        _scheduler.Tick();
    }

    public void SetEnabled(bool enabled)
    {
        _config.Enabled = enabled;
        _config.Save();
        RefreshOverlayState();
    }

    public void ReloadConfig()
    {
        var c = AppConfig.Load();
        _config.Enabled = c.Enabled;
        _config.HideWhenPaused = c.HideWhenPaused;
        _config.SpotifyOnly = c.SpotifyOnly;
        _config.ShowTitleWhenNoLyrics = c.ShowTitleWhenNoLyrics;
        _config.PlainLyricsBestEffort = c.PlainLyricsBestEffort;
        _config.LeftPadding = c.LeftPadding;
        _config.MaxWidth = c.MaxWidth;
        _config.AutoWidthFraction = c.AutoWidthFraction;
        _config.SyncOffsetMs = c.SyncOffsetMs;
        ApplyPlacement();
        RefreshOverlayState();
        Log.Write("config reloaded (font/glow changes need a restart)");
    }

    public void QuitApp()
    {
        try
        {
            _tick?.Stop();
            _backstop?.Stop();
            _learn?.Stop();
            _periodicTrim?.Stop();
            _audioCapture?.Stop();
            _visibility?.Dispose();
            _watcher?.Dispose();
            _tray?.Dispose();
            _overlay?.Close();
        }
        catch { /* ignore */ }
        finally { Shutdown(); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mutex?.ReleaseMutex(); _mutex?.Dispose(); } catch { /* ignore */ }
        base.OnExit(e);
    }
}
