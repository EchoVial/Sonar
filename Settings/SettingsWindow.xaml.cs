using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SpotifyLyricsTaskbar.Config;
using SpotifyLyricsTaskbar.Startup;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar.Settings;

public partial class SettingsWindow : Window
{
    private static readonly string[] Fonts =
        { "Poppins", "Space Mono", "Segoe UI", "Consolas", "Cascadia Mono", "Georgia", "Arial",
          "Verdana", "Tahoma", "Trebuchet MS", "Times New Roman", "Impact", "Comic Sans MS" };
    private static readonly string[] Weights = { "Light", "Normal", "Medium", "SemiBold", "Bold" };
    private static readonly string[] Anims = { "Slide", "Fade", "Scramble" };
    private static readonly string[] SolidSwatches =
        { "#FFFFFFFF", "#FF000000", "#FFFF3B3B", "#FF35D07A", "#FF4D8FFF" }; // white, black, red, green, blue

    private readonly App _app;
    private AppConfig C => _app.Config;
    private bool _loading;
    private bool _offsetDragging;  // user is dragging the Offset slider → don't auto-move it
    private bool _syncingOffset;   // we're setting the slider from the audio → don't treat as a manual change
    private bool _overrideUnlocked; // "override" clicked → slider unlocked, audio stays on until it's actually moved
    private Button? _audioToggle;   // the "Audio sync (beta)" toggle, so we can refresh its state externally

    private readonly PreviewPlayer _preview;
    private readonly List<Button> _animBtns = new();
    private readonly List<(Button btn, Action refresh)> _modeRows = new();
    private readonly List<(string hex, Border border, TextBlock check)> _swatches = new();
    private readonly List<(Button btn, string mode)> _audioModePills = new();
    private readonly DispatcherTimer _playPoll = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        _preview = new PreviewPlayer(PreviewHost);

        DeviceRoot.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        CloseBtn.Click += (_, _) => Close();
        ResetBtn.Click += (_, _) => ResetDefaults();

        PreviewArt.Background = new LinearGradientBrush(Color.FromRgb(0x8B, 0x5C, 0xF6), Color.FromRgb(0xEC, 0x48, 0x99), 45);

        BuildSwatches();
        BuildAnims();
        BuildModes();
        BuildAppToggles();
        WireControls();
        LoadFromConfig();

        _preview.Apply(C, _app.CurrentAlbumColor);
        _preview.Start();

        _playPoll.Tick += (_, _) => { UpdatePlayGlyph(); UpdateAudioStatus(); UpdateOffsetState(); };
        _playPoll.Start();
        Closed += (_, _) => { _preview.Stop(); _playPoll.Stop(); };
    }

    // ---------- swatches + colour ----------
    private void BuildSwatches()
    {
        SwatchPanel.Children.Clear();
        _swatches.Clear();
        foreach (var hex in SolidSwatches) AddSwatch(hex, removable: false);
        foreach (var hex in C.CustomColors.ToArray()) AddSwatch(hex, removable: true);
        SwatchPanel.Children.Add(MakeAddTile());
    }

    private void AddSwatch(string hex, bool removable)
    {
        Color col;
        try { col = (Color)ColorConverter.ConvertFromString(hex); } catch { return; }
        void Pick() { C.TextColor = hex; C.ColorMode = "Static"; ApplyAll(); }
        var swatch = MakeSwatch(hex, col, Pick);

        if (!removable)
        {
            swatch.Margin = new Thickness(0, 0, 8, 8);
            SwatchPanel.Children.Add(swatch);
            return;
        }

        // Custom colour: the swatch plus a small ✕ (shown on hover) that removes it.
        var grid = new Grid { Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 8) };
        grid.Children.Add(swatch);
        var x = new Border
        {
            Width = 15, Height = 15, CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x21, 0x24)),
            BorderBrush = (Brush)Resources["Stroke"], BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -5, -5, 0), Cursor = Cursors.Hand, Visibility = Visibility.Collapsed,
            ToolTip = "Remove colour",
            Child = new TextBlock { Text = "✕", FontSize = 9, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        x.MouseLeftButtonDown += (_, e) => { e.Handled = true; RemoveCustom(hex); };
        grid.MouseEnter += (_, _) => x.Visibility = Visibility.Visible;
        grid.MouseLeave += (_, _) => x.Visibility = Visibility.Collapsed;
        grid.Children.Add(x);
        SwatchPanel.Children.Add(grid);
    }

    private void RemoveCustom(string hex)
    {
        C.CustomColors.RemoveAll(c => Eq(c, hex));
        if (Eq(C.TextColor, hex)) C.TextColor = "#FFFFFFFF"; // was the active colour → revert to white
        BuildSwatches();
        ApplyAll();
    }

    /// <summary>A colour tile that shows a selection ring + contrast-coloured tick when it's the active colour.</summary>
    private FrameworkElement MakeSwatch(string hex, Color col, Action onClick)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(col),
            BorderThickness = new Thickness(1.5),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
        };
        var check = new TextBlock
        {
            Text = "✓", FontSize = 13, FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = ContrastBrush(col), Visibility = Visibility.Collapsed,
        };
        var grid = new Grid { Width = 28, Height = 28, Cursor = Cursors.Hand };
        grid.Children.Add(border);
        grid.Children.Add(check);
        grid.MouseLeftButtonDown += (_, _) => onClick();
        grid.MouseEnter += (_, _) => { if (check.Visibility != Visibility.Visible) border.BorderBrush = new SolidColorBrush(Colors.White); };
        grid.MouseLeave += (_, _) => { if (check.Visibility != Visibility.Visible) border.BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)); };
        _swatches.Add((hex, border, check));
        return grid;
    }

    private static Brush ContrastBrush(Color c)
    {
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        return new SolidColorBrush(lum > 0.6 ? Color.FromRgb(0x18, 0x18, 0x1A) : Colors.White);
    }

    private Button MakeAddTile()
    {
        var b = new Button { Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, ToolTip = "Add a custom colour" };
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        border.SetValue(Border.BackgroundProperty, (Brush)Resources["Sub"]);
        border.SetValue(Border.BorderBrushProperty, (Brush)Resources["Stroke"]);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        var plus = new FrameworkElementFactory(typeof(TextBlock));
        plus.SetValue(TextBlock.TextProperty, "+");
        plus.SetValue(TextBlock.FontSizeProperty, 16.0);
        plus.SetValue(TextBlock.ForegroundProperty, (Brush)Resources["Muted"]);
        plus.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        plus.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(plus);
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, (Brush)Resources["Mint"]));
        var t = new ControlTemplate(typeof(Button)) { VisualTree = border };
        t.Triggers.Add(hover);
        b.Template = t;
        b.Click += (_, _) => OpenColorEditor();
        return b;
    }

    private void OpenColorEditor()
    {
        Color init;
        try { init = (Color)ColorConverter.ConvertFromString(C.TextColor); } catch { init = Colors.White; }
        ColorEditor.Open(RootGrid, SwatchPanel, init, c =>
        {
            string hex = $"#FF{c.R:X2}{c.G:X2}{c.B:X2}";
            if (!C.CustomColors.Contains(hex) && Array.IndexOf(SolidSwatches, hex) < 0) C.CustomColors.Add(hex);
            C.TextColor = hex; C.ColorMode = "Static";
            BuildSwatches();
            ApplyAll();
        });
    }

    // ---------- animation ----------
    private void BuildAnims()
    {
        foreach (var a in Anims)
        {
            var b = new Button { Style = (Style)Resources["Pill"], Margin = new Thickness(0, 0, 8, 0), Content = new TextBlock { Text = a, FontSize = 12 } };
            b.Click += (_, _) => { C.AnimationStyle = a; ApplyAll(); };
            b.MouseEnter += (_, _) => HoverPreview(c => c.AnimationStyle = a);
            b.MouseLeave += (_, _) => _preview.Apply(C, _app.CurrentAlbumColor);
            _animBtns.Add(b);
            AnimPanel.Children.Add(b);
        }
    }

    // ---------- modes (labelled rows with descriptions) ----------
    private void BuildModes()
    {
        _modeRows.Add(MakeModeRow("✦", "Intro sequence",
            _ => "Shows the album cover, artist, and track name before playing the lyrics",
            () => C.ShowIntro, () => C.ShowIntro = !C.ShowIntro));
        _modeRows.Add(MakeModeRow("◐", "Sync colour to album cover",
            _ => "Match the font colour to the album art",
            () => Eq(C.ColorMode, "Album"), () => C.ColorMode = Eq(C.ColorMode, "Album") ? "Static" : "Album"));
        _modeRows.Add(MakeModeRow("∿", "Rainbow (RGB)",
            _ => "Cycle the text through the spectrum",
            () => Eq(C.ColorMode, "Rgb"), () => C.ColorMode = Eq(C.ColorMode, "Rgb") ? "Static" : "Rgb"));
        _modeRows.Add(MakeModeRow("≣", "Word by word",
            _ => "Reveal each word as it's sung",
            () => C.WordByWord, () => C.WordByWord = !C.WordByWord));
        foreach (var (btn, _) in _modeRows) ModesPanel.Children.Add(btn);
    }

    private (Button, Action) MakeModeRow(string icon, string title, Func<bool, string> subtitle, Func<bool> on, Action toggle)
    {
        var iconTb = new TextBlock { Text = icon, FontSize = 17, Foreground = (Brush)Resources["Text"], Width = 24, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var titleTb = new TextBlock { Text = title, FontSize = 13, Foreground = (Brush)Resources["Text"] };
        var subTb = new TextBlock { FontSize = 11, Foreground = (Brush)Resources["Muted"], Margin = new Thickness(0, 1, 8, 0), TextWrapping = TextWrapping.Wrap };
        var chip = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

        var textStack = new StackPanel { Margin = new Thickness(11, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(titleTb);
        textStack.Children.Add(subTb);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(iconTb, 0); Grid.SetColumn(textStack, 1); Grid.SetColumn(chip, 2);
        grid.Children.Add(iconTb); grid.Children.Add(textStack); grid.Children.Add(chip);

        var b = new Button { Style = (Style)Resources["Pill"], Margin = new Thickness(0, 0, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = grid };
        b.Click += (_, _) => { toggle(); ApplyAll(); };

        void Refresh()
        {
            bool o = on();
            subTb.Text = subtitle(o);
            chip.Text = o ? "ON" : "OFF";
            chip.Foreground = o ? (Brush)Resources["Mint"] : (Brush)Resources["Muted"];
            b.Tag = o ? "on" : null;
        }
        Refresh();
        return (b, Refresh);
    }

    // ---------- app toggles ----------
    private void BuildAppToggles()
    {
        AppTogglesPanel.Children.Clear();
        AppTogglesPanel.Children.Add(MakeToggle("Hide when paused", () => C.HideWhenPaused, v => C.HideWhenPaused = v));
        AppTogglesPanel.Children.Add(MakeToggle("Spotify only", () => C.SpotifyOnly, v => C.SpotifyOnly = v));
        AppTogglesPanel.Children.Add(MakeToggle("Run at startup", AutoStart.IsEnabled, v => { AutoStart.Set(v); C.RunAtStartup = v; }));
        _audioToggle = MakeToggle("Audio sync (beta)", () => C.AudioSyncEnabled, v => { C.AudioSyncEnabled = v; _overrideUnlocked = false; });
        AppTogglesPanel.Children.Add(_audioToggle);
    }

    // ---------- audio sync mode (Balanced / Accuracy Boost) ----------
    private void BuildAudioModePanel()
    {
        AudioModePanel.Children.Clear();
        _audioModePills.Clear();
        if (!C.AudioSyncEnabled) return; // modes only matter when audio sync is on
        AudioModePanel.Children.Add(MakeModePill("Balanced", "Balanced", "Fast — learns one offset per song."));
        AudioModePanel.Children.Add(MakeModePill("Boost", "Accuracy Boost", "Recommended only for niche artists and tracks · slightly increases resource usage"));
        RefreshAudioModePills();
    }

    private Button MakeModePill(string mode, string label, string tip)
    {
        var b = new Button { Style = (Style)Resources["MiniPill"], Margin = new Thickness(0, 0, 8, 0), ToolTip = tip, Content = new TextBlock { Text = label, FontSize = 11 } };
        b.Click += (_, _) => { C.AudioSyncMode = mode; ApplyAll(); };
        _audioModePills.Add((b, mode));
        return b;
    }

    private void RefreshAudioModePills()
    {
        foreach (var (btn, mode) in _audioModePills) btn.Tag = Eq(C.AudioSyncMode, mode) ? "on" : null;
    }

    private Button MakeToggle(string label, Func<bool> get, Action<bool> set)
    {
        var b = new Button { Style = (Style)Resources["Pill"], Margin = new Thickness(0, 0, 8, 8), Content = new TextBlock { Text = label, FontSize = 12 } };
        b.Tag = get() ? "on" : null;
        b.Click += (_, _) => { set(!get()); b.Tag = get() ? "on" : null; ApplyAll(); };
        return b;
    }

    // ---------- dropdowns ----------
    private Popup? _listPopup;
    private void OpenList(UIElement target, IEnumerable<string> items, bool asFont, Action<string> onPick)
    {
        CloseListPopup();
        var list = new StackPanel { MinWidth = 200 };
        foreach (var it in items)
        {
            var tb = new TextBlock { Text = it, FontSize = 14, Foreground = (Brush)Resources["Text"] };
            if (asFont) tb.FontFamily = FontResolver.Resolve(it);
            else tb.FontWeight = FontResolver.Weight(it);
            var item = new Button
            {
                Content = tb, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(11, 7, 11, 7), Cursor = Cursors.Hand,
            };
            string val = it;
            item.MouseEnter += (_, _) => HoverPreview(c => { if (asFont) c.FontFamily = val; else c.FontWeight = val; });
            item.MouseLeave += (_, _) => _preview.Apply(C, _app.CurrentAlbumColor);
            item.Click += (_, _) => { onPick(val); CloseListPopup(); };
            list.Children.Add(item);
        }
        var border = new Border { Background = (Brush)Resources["Sub"], CornerRadius = new CornerRadius(11), BorderBrush = (Brush)Resources["Stroke"], BorderThickness = new Thickness(1), Padding = new Thickness(5), Child = list };
        _listPopup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = border };
        RootGrid.Children.Add(_listPopup);
        _listPopup.IsOpen = true;
    }

    private void CloseListPopup()
    {
        if (_listPopup == null) return;
        _listPopup.IsOpen = false;
        RootGrid.Children.Remove(_listPopup);
        _listPopup = null;
    }

    // ---------- wiring ----------
    private void WireControls()
    {
        FontPill.Click += (_, _) => OpenList(FontPill, Fonts, true, f => { C.FontFamily = f; ApplyAll(); });
        WeightPill.Click += (_, _) => OpenList(WeightPill, Weights, false, w => { C.FontWeight = w; ApplyAll(); });

        SizeSlider.ValueChanged += (_, _) => { if (_loading) return; C.FontSize = Math.Round(SizeSlider.Value); SizeVal.Text = ((int)C.FontSize).ToString(); ApplyAll(); };
        GlowSlider.ValueChanged += (_, _) => { if (_loading) return; C.GlowBlurRadius = Math.Round(GlowSlider.Value); GlowVal.Text = ((int)C.GlowBlurRadius).ToString(); ApplyAll(); };
        OpacitySlider.ValueChanged += (_, _) => { if (_loading) return; C.TextOpacity = Math.Round(OpacitySlider.Value) / 100.0; OpacityVal.Text = $"{(int)Math.Round(OpacitySlider.Value)}%"; ApplyAll(); };
        OffsetSlider.ValueChanged += (_, _) =>
        {
            if (_loading || _syncingOffset) return;
            if (C.AudioSyncEnabled) { C.AudioSyncEnabled = false; _overrideUnlocked = false; } // moving it turns off auto-sync
            int ms = (int)Math.Round(OffsetSlider.Value); _app.SetSongOffset(ms); OffsetVal.Text = $"{ms} ms"; ApplyAll();
        };
        OffsetSlider.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => _offsetDragging = true), true);
        OffsetSlider.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => _offsetDragging = false), true);

        // Editable value fields — type a number to set the slider.
        WireValueBox(SizeVal, SizeSlider);
        WireValueBox(GlowVal, GlowSlider);
        WireValueBox(OpacityVal, OpacitySlider);
        WireValueBox(OffsetVal, OffsetSlider);

        // Per-slider reset dots.
        var def = new AppConfig();
        SizeReset.Click += (_, _) => SizeSlider.Value = def.FontSize;
        GlowReset.Click += (_, _) => GlowSlider.Value = def.GlowBlurRadius;
        OpacityReset.Click += (_, _) => OpacitySlider.Value = def.TextOpacity * 100;
        OffsetReset.Click += (_, _) => { _app.ResetCurrentSongOffset(); UpdateOffsetState(); };

        // "override" unlocks the greyed offset slider; audio sync stays on until it's actually moved.
        OverrideLink.Click += (_, _) => { _overrideUnlocked = true; UpdateOffsetState(); };

        PrevBtn.Click += (_, _) => _app.MediaPrevious();
        PlayBtn.Click += (_, _) => { _app.MediaPlayPause(); Dispatcher.BeginInvoke(new Action(UpdatePlayGlyph), DispatcherPriority.Background); };
        NextBtn.Click += (_, _) => _app.MediaNext();
    }

    private void WireValueBox(TextBox box, Slider slider)
    {
        void Commit()
        {
            var m = System.Text.RegularExpressions.Regex.Match(box.Text ?? string.Empty, @"-?\d+");
            if (m.Success && int.TryParse(m.Value, out int v))
                slider.Value = Math.Clamp(v, slider.Minimum, slider.Maximum);
        }
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Keyboard.ClearFocus(); } };
    }

    private void HoverPreview(Action<AppConfig> tweak)
    {
        var t = C.Clone();
        tweak(t);
        _preview.Apply(t, _app.CurrentAlbumColor);
    }

    // ---------- state ----------
    private void LoadFromConfig()
    {
        _loading = true;
        SizeSlider.Value = Math.Clamp(C.FontSize, SizeSlider.Minimum, SizeSlider.Maximum);
        SizeVal.Text = ((int)C.FontSize).ToString();
        GlowSlider.Value = Math.Clamp(C.GlowBlurRadius, GlowSlider.Minimum, GlowSlider.Maximum);
        GlowVal.Text = ((int)C.GlowBlurRadius).ToString();
        OpacitySlider.Value = Math.Clamp(C.TextOpacity * 100, OpacitySlider.Minimum, OpacitySlider.Maximum);
        OpacityVal.Text = $"{(int)Math.Round(OpacitySlider.Value)}%";
        OffsetSlider.Value = Math.Clamp(_app.EffectiveSongOffset, OffsetSlider.Minimum, OffsetSlider.Maximum);
        OffsetVal.Text = $"{_app.EffectiveSongOffset} ms";
        _loading = false;
        UpdateStates();
    }

    private void ApplyAll()
    {
        C.Save();
        _app.ApplyLiveConfig();
        _preview.Apply(C, _app.CurrentAlbumColor);
        UpdateStates();
    }

    private void UpdateStates()
    {
        FontName.Text = C.FontFamily;
        WeightName.Text = C.FontWeight;
        for (int i = 0; i < _animBtns.Count; i++)
            _animBtns[i].Tag = Eq(Anims[i], C.AnimationStyle) ? "on" : null;
        foreach (var (_, refresh) in _modeRows) refresh();

        // Highlight the active colour (only meaningful in Static mode).
        bool staticMode = Eq(C.ColorMode, "Static");
        foreach (var (hex, border, check) in _swatches)
        {
            bool sel = staticMode && Eq(C.TextColor, hex);
            check.Visibility = sel ? Visibility.Visible : Visibility.Collapsed;
            border.BorderBrush = sel ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
            border.BorderThickness = new Thickness(sel ? 2 : 1.5);
        }

        PreviewTitle.Text = C.ShowIntro ? "intro · then lyrics" : "lyrics only";
        UpdatePlayGlyph();
        UpdateAudioStatus();
        if (_audioToggle != null) _audioToggle.Tag = C.AudioSyncEnabled ? "on" : null;
        BuildAudioModePanel();
        UpdateOffsetState();
    }

    private void UpdateAudioStatus()
    {
        string s = _app.AudioSyncStatus;
        AudioStatus.Text = string.IsNullOrEmpty(s) ? string.Empty : "Audio sync — " + s;
    }

    /// <summary>
    /// Offset row state machine:
    ///  • audio sync on, locked → slider greyed, shows the live auto-tuned value, "override" link.
    ///  • "override" clicked → slider unlocked (audio still on); moving it turns audio sync off.
    ///  • audio sync off → a normal manual offset slider.
    /// </summary>
    private void UpdateOffsetState()
    {
        bool auto = C.AudioSyncEnabled && !_overrideUnlocked;
        OffsetSlider.IsEnabled = !auto;
        OffsetVal.IsEnabled = !auto;
        OverrideLink.Visibility = auto ? Visibility.Visible : Visibility.Collapsed;

        if (auto)
        {
            OffsetHint.Text = "auto-offset from Spotify with audio sync (beta)";
            OffsetHint.Foreground = (Brush)Resources["Mint"];
            if (_offsetDragging) return;
            _syncingOffset = true;
            OffsetSlider.Value = Math.Clamp(_app.LiveSongOffset, OffsetSlider.Minimum, OffsetSlider.Maximum);
            OffsetVal.Text = $"{_app.LiveSongOffset} ms";
            _syncingOffset = false;
        }
        else if (C.AudioSyncEnabled)
        {
            OffsetHint.Text = "move the slider to override auto-sync";
            OffsetHint.Foreground = (Brush)Resources["Muted"];
        }
        else
        {
            OffsetHint.Text = string.IsNullOrEmpty(_app.CurrentTrackKey) ? "global offset · + sooner / − later" : "nudges this song · + sooner / − later";
            OffsetHint.Foreground = (Brush)Resources["Muted"];
        }
    }

    private void UpdatePlayGlyph() => PlayBtn.Content = _app.IsPlaying ? "⏸" : "▶";

    private void UpdatePreview() => _preview.Apply(C, _app.CurrentAlbumColor);

    private static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void ResetDefaults()
    {
        var d = new AppConfig();
        C.FontFamily = d.FontFamily; C.FontWeight = d.FontWeight; C.FontSize = d.FontSize;
        C.TextColor = d.TextColor; C.GlowColor = d.GlowColor; C.GlowBlurRadius = d.GlowBlurRadius; C.GlowOpacity = d.GlowOpacity;
        C.TextOpacity = d.TextOpacity;
        C.ColorMode = d.ColorMode; C.RgbSpeedSec = d.RgbSpeedSec; C.GlowFollowsText = d.GlowFollowsText;
        C.AnimationStyle = d.AnimationStyle; C.WordPaceMs = d.WordPaceMs; C.ShowIntro = d.ShowIntro; C.ScrambleMs = d.ScrambleMs;
        C.WordByWord = d.WordByWord;
        LoadFromConfig();
        ApplyAll();
    }
}
