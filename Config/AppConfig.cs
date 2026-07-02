using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpotifyLyricsTaskbar.Config;

/// <summary>
/// User-tunable settings, persisted as JSON in %APPDATA%. All sizes are in
/// device-independent pixels (DIPs); the overlay scales them to physical pixels
/// using the taskbar monitor's DPI.
/// </summary>
public sealed class AppConfig
{
    public bool Enabled { get; set; } = true;

    // ---- Theme (preset label; applying a theme sets the fields below) ----
    public string Theme { get; set; } = "Midnight";

    // ---- Appearance ----
    public string FontFamily { get; set; } = "Poppins";
    public string FontWeight { get; set; } = "SemiBold";
    public double FontSize { get; set; } = 14;
    public string TextColor { get; set; } = "#FFFFFFFF";
    public string GlowColor { get; set; } = "#FFFFFFFF";
    public double GlowBlurRadius { get; set; } = 20;
    public double GlowOpacity { get; set; } = 1.0;
    /// <summary>Overall opacity of the lyric text (0.2–1.0). The intro card is unaffected.</summary>
    public double TextOpacity { get; set; } = 1.0;
    /// <summary>User-saved custom colours (ARGB hex), shown as swatches in the colour picker.</summary>
    public List<string> CustomColors { get; set; } = new();
    /// <summary>"Static" = TextColor; "Album" = match the album-art colour; "Rgb" = animated rainbow.</summary>
    public string ColorMode { get; set; } = "Static";
    /// <summary>Seconds per full hue cycle in RGB mode.</summary>
    public double RgbSpeedSec { get; set; } = 6;
    /// <summary>Tie the glow colour to the text colour (incl. Album/Rgb modes).</summary>
    public bool GlowFollowsText { get; set; } = true;

    // ---- Word-by-word reveal ----
    /// <summary>Reveal a line word-by-word as it's sung (timed within each line). False = whole line at once.</summary>
    public bool WordByWord { get; set; } = true;
    /// <summary>Per-word fade-in duration (ms).</summary>
    public int WordFadeMs { get; set; } = 140;
    /// <summary>Approx singing pace: a line's words are revealed over (wordCount * this), capped by the gap to the next line. Lower = faster reveal.</summary>
    public int WordPaceMs { get; set; } = 300;
    /// <summary>How each word enters: "Fade", "Slide", or "Scramble".</summary>
    public string AnimationStyle { get; set; } = "Slide";

    // ---- Instrumental tag (shown during long no-lyric gaps) ----
    public bool ShowInstrumental { get; set; } = true;
    public string InstrumentalTag { get; set; } = "♪";
    /// <summary>A gap must be at least this long (ms) to show the instrumental tag.</summary>
    public int InstrumentalGapMs { get; set; } = 4000;

    // ---- Intro card (album art + artist/track, then scramble into lyrics) ----
    public bool ShowIntro { get; set; } = true;
    /// <summary>How long the album-art / artist–track card holds before transitioning.</summary>
    public int IntroMs { get; set; } = 2400;
    /// <summary>Duration of the scramble transition (lower = faster).</summary>
    public int ScrambleMs { get; set; } = 200;
    /// <summary>Album-art square size in DIPs (also capped to fit the taskbar height).</summary>
    public double AlbumArtSize { get; set; } = 28;

    // ---- Placement (DIPs) ----
    public double LeftPadding { get; set; } = 16;
    /// <summary>0 = auto: a fraction of the taskbar width (see AutoWidthFraction).</summary>
    public double MaxWidth { get; set; } = 0;
    public double AutoWidthFraction { get; set; } = 0.42;

    // ---- Animation (ms) ----
    public int LineFadeInMs { get; set; } = 300;
    public int LineFadeOutMs { get; set; } = 220;
    public int ShowHideMs { get; set; } = 250;

    // ---- Behavior ----
    public bool SpotifyOnly { get; set; } = true;
    public bool HideWhenPaused { get; set; } = true;
    public bool ShowTitleWhenNoLyrics { get; set; } = true;
    public bool PlainLyricsBestEffort { get; set; } = true;
    public int TickIntervalMs { get; set; } = 100;
    /// <summary>Global offset (ms) applied to the playback position. Positive = lyrics sooner (catch up if they lag).</summary>
    public int SyncOffsetMs { get; set; } = 0;
    /// <summary>Per-track offsets (ms), keyed by TrackInfo.Key — fixes songs whose source lyrics are mistimed.</summary>
    public Dictionary<string, int> SongOffsets { get; set; } = new();
    /// <summary>The active track's per-song offset (resolved at runtime; not serialized).</summary>
    [JsonIgnore] public int CurrentSongOffset { get; set; }

    // ---- Startup ----
    public bool RunAtStartup { get; set; } = true;

    // ---- Audio sync (experimental) ----
    /// <summary>Listen to Spotify's audio and auto-correct constant timing drift.</summary>
    public bool AudioSyncEnabled { get; set; } = false;
    /// <summary>Live correction (ms) computed from the audio; added to the playback position. Not serialized.</summary>
    [JsonIgnore] public int AudioCorrectionMs { get; set; }

    // ---- Lyrics network ----
    /// <summary>Optional contact (email/URL) appended to the HTTP User-Agent, as LRCLIB requests.</summary>
    public string? ContactForUserAgent { get; set; }
    /// <summary>Per-request timeout for LRCLIB calls (it can be slow from some regions).</summary>
    public int LrclibTimeoutMs { get; set; } = 12000;
    /// <summary>Per-request timeout for NetEase calls.</summary>
    public int NetEaseTimeoutMs { get; set; } = 7000;
    /// <summary>Per-request budget for Genius (search + page scrape, plain lyrics only).</summary>
    public int GeniusTimeoutMs { get; set; } = 9000;
    /// <summary>How long to wait for LRCLIB (preferred, cleaner) before using a NetEase result that's already in hand.</summary>
    public int LrclibPreferMs { get; set; } = 4000;

    // ---- Paths (not serialized) ----
    [JsonIgnore]
    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sonar");
    [JsonIgnore]
    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");
    [JsonIgnore]
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sonar");
    [JsonIgnore]
    public static string CacheDir => Path.Combine(DataDir, "cache");
    [JsonIgnore]
    public static string LogPath => Path.Combine(DataDir, "log.txt");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public AppConfig Clone() => (AppConfig)MemberwiseClone();

    public static AppConfig Load()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (cfg != null)
                {
                    cfg.Save(); // rewrite to backfill any newly added fields
                    return cfg;
                }
            }
        }
        catch { /* fall through to defaults */ }

        var def = new AppConfig();
        def.Save();
        return def;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* best effort */ }
    }
}
