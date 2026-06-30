using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SpotifyLyricsTaskbar.Config;
using SpotifyLyricsTaskbar.Interop;
using SpotifyLyricsTaskbar.Lyrics;
using SpotifyLyricsTaskbar.Taskbar;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar.Overlay;

/// <summary>
/// Transparent, click-through, topmost overlay over the left of the taskbar.
/// Intro card (album art + Artist — Track) → push art out + scramble → word-by-word
/// lyrics. Colours support Static / Album-match / RGB; words enter Fade / Slide / Scramble.
/// </summary>
public partial class OverlayWindow : Window
{
    private const double SlideY = 4;
    private const string Pool = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789#$%&*@/\\";

    private AppConfig _config;
    private FontFamily _fontFamily = new("Segoe UI");
    private FontWeight _fontWeight = FontWeights.SemiBold;
    private readonly SolidColorBrush _textBrush = new(Colors.White);
    private Color? _albumColor;

    private readonly StackPanel _hostA;
    private readonly StackPanel _hostB;
    private readonly Grid _lyricLayer; // carries the overall text opacity; intro card sits outside it
    private StackPanel _frontHost;
    private List<TextBlock> _frontWords = new();
    private readonly DropShadowEffect _glowA;
    private readonly DropShadowEffect _glowB;
    private readonly DropShadowEffect _glowIntro;
    private int _curLineId = int.MinValue;
    private int _curReveal;
    private LyricView _lastView = LyricView.Empty;

    private readonly StackPanel _introHost;
    private readonly Border _artBorder;
    private readonly ImageBrush _artBrush;
    private readonly TextBlock _introText;
    private bool _introActive;
    private LyricView _pendingView = LyricView.Empty;

    private readonly Random _rnd = new();
    private DispatcherTimer? _scrambleTimer;

    private IntPtr _hwnd;
    private bool _shown;
    private OverlayPlacement? _placement;

    public OverlayWindow(AppConfig config)
    {
        _config = config;
        InitializeComponent();

        _glowA = MakeGlow();
        _glowB = MakeGlow();
        _glowIntro = MakeGlow();

        _hostA = MakeHost(_glowA);
        _hostB = MakeHost(_glowB);
        _lyricLayer = new Grid();
        _lyricLayer.Children.Add(_hostA);
        _lyricLayer.Children.Add(_hostB);
        Root.Children.Add(_lyricLayer);
        _frontHost = _hostA;
        _hostB.Opacity = 0;

        _artBrush = new ImageBrush { Stretch = Stretch.UniformToFill };
        _artBorder = new Border
        {
            Width = config.AlbumArtSize,
            Height = config.AlbumArtSize,
            CornerRadius = new CornerRadius(6),
            Background = _artBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            SnapsToDevicePixels = true,
        };
        _introText = new TextBlock
        {
            Foreground = Brushes.White, // Artist — Track is always white, regardless of lyric colour mode
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Effect = _glowIntro,
        };
        _introHost = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
        };
        _introHost.Children.Add(_artBorder);
        _introHost.Children.Add(_introText);
        Root.Children.Add(_introHost);

        ApplyTypography();
        ApplyGlowMetrics();
        ApplyColorMode();
        ApplyTextOpacity();

        Opacity = 0; // hidden until shown
    }

    private DropShadowEffect MakeGlow() => new()
    {
        Color = Colors.White,
        BlurRadius = _config.GlowBlurRadius,
        ShadowDepth = 0,
        Opacity = _config.GlowOpacity,
        Direction = 0,
    };

    // Single horizontal line, left-anchored. Long lines are scaled down to fit (FitOneLine)
    // rather than wrapping — always one line at a time.
    private static StackPanel MakeHost(Effect glow) => new()
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Effect = glow,
    };

    private TextBlock MakeWord(string text, bool revealed) => new()
    {
        Text = text + " ",
        FontFamily = _fontFamily,
        FontSize = _config.FontSize,
        FontWeight = _fontWeight,
        Foreground = _textBrush,
        TextWrapping = TextWrapping.NoWrap,
        Opacity = revealed ? 1 : 0,
        RenderTransform = new TranslateTransform(0, revealed ? 0 : SlideY),
        RenderTransformOrigin = new Point(0, 0.5),
    };

    // ---- Live visual config -------------------------------------------------
    /// <summary>Re-read appearance from config and repaint without recreating the window.</summary>
    public void RefreshVisuals(AppConfig config)
    {
        _config = config;
        ApplyTypography();
        ApplyGlowMetrics();
        ApplyColorMode();
        ApplyTextOpacity();
        _introText.FontFamily = _fontFamily;
        _introText.FontWeight = _fontWeight;
        _introText.FontSize = _config.FontSize;
        _artBorder.Width = _config.AlbumArtSize;
        _artBorder.Height = _config.AlbumArtSize;

        // rebuild the current line so font/size changes take effect immediately
        if (!_introActive && _lastView.LineId != -1 && _lastView.Words.Count > 0)
        {
            _curLineId = int.MinValue; // force NewLine
            ApplyView(_lastView);
        }
    }

    private void ApplyTypography()
    {
        _fontFamily = FontResolver.Resolve(_config.FontFamily);
        _fontWeight = FontResolver.Weight(_config.FontWeight);
        _introText.FontFamily = _fontFamily;
        _introText.FontWeight = _fontWeight;
        _introText.FontSize = _config.FontSize;
    }

    private void ApplyTextOpacity() => _lyricLayer.Opacity = Math.Clamp(_config.TextOpacity, 0.05, 1.0);

    /// <summary>Scale a line uniformly so it fits the empty region on one line (down only, never up).</summary>
    private void FitOneLine(StackPanel host)
    {
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double natural = host.DesiredSize.Width;
        double avail = AvailableWidthDip();
        double scale = natural > avail && natural > 1 ? Math.Max(0.4, avail / natural) : 1.0;
        host.LayoutTransform = scale < 0.999 ? new ScaleTransform(scale, scale) : null;
    }

    private double AvailableWidthDip()
    {
        if (_placement is { } p && p.Dpi > 0) return Math.Max(40, p.Width * 96.0 / Math.Max(96u, p.Dpi) - 4);
        return ActualWidth > 4 ? ActualWidth - 4 : 240;
    }

    private void ApplyGlowMetrics()
    {
        foreach (var g in new[] { _glowA, _glowB, _glowIntro })
        {
            g.BlurRadius = _config.GlowBlurRadius;
            g.Opacity = _config.GlowOpacity;
        }
    }

    public void SetAlbumColor(Color? c)
    {
        _albumColor = c;
        if (string.Equals(_config.ColorMode, "Album", StringComparison.OrdinalIgnoreCase))
            ApplyColorMode();
    }

    private void ApplyColorMode()
    {
        StopRgb();
        switch ((_config.ColorMode ?? "Static").ToLowerInvariant())
        {
            case "rgb":
                StartRgb();
                return;
            case "album":
                SetTextColor(_albumColor ?? ParseColor(_config.TextColor, Colors.White));
                break;
            default:
                SetTextColor(ParseColor(_config.TextColor, Colors.White));
                break;
        }
    }

    private void SetTextColor(Color c)
    {
        _textBrush.Color = c;
        var glow = _config.GlowFollowsText ? c : ParseColor(_config.GlowColor, c);
        // intro glow (_glowIntro) stays white; only the lyric rows follow the colour.
        _glowA.Color = glow; _glowB.Color = glow;
    }

    private void StartRgb()
    {
        var anim = new ColorAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(Math.Max(1, _config.RgbSpeedSec)),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        Color[] hues = { Hue(0), Hue(60), Hue(120), Hue(180), Hue(240), Hue(300), Hue(360) };
        for (int i = 0; i < hues.Length; i++)
            anim.KeyFrames.Add(new LinearColorKeyFrame(hues[i], KeyTime.FromPercent((double)i / (hues.Length - 1))));
        anim.Freeze();

        _textBrush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        if (_config.GlowFollowsText)
        {
            _glowA.BeginAnimation(DropShadowEffect.ColorProperty, anim);
            _glowB.BeginAnimation(DropShadowEffect.ColorProperty, anim);
        }
    }

    private void StopRgb()
    {
        _textBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        _glowA.BeginAnimation(DropShadowEffect.ColorProperty, null);
        _glowB.BeginAnimation(DropShadowEffect.ColorProperty, null);
    }

    // ---- Intro -------------------------------------------------------------
    public void ShowIntro(ImageSource? art, string text)
    {
        CancelScramble();
        _introActive = true;
        _pendingView = LyricView.Empty;

        _hostA.BeginAnimation(OpacityProperty, null); _hostA.Opacity = 0;
        _hostB.BeginAnimation(OpacityProperty, null); _hostB.Opacity = 0;
        _curLineId = int.MinValue; _curReveal = 0;

        // size the art to fit the bar (cap to client height minus padding)
        double clientH = _placement is { } p ? p.Height * 96.0 / Math.Max(96u, p.Dpi) : ActualHeight;
        double size = Math.Max(16, Math.Min(_config.AlbumArtSize, (clientH > 0 ? clientH : 48) - 8));
        if (art != null)
        {
            _artBrush.ImageSource = art;
            _artBorder.Visibility = Visibility.Visible;
            _artBorder.BeginAnimation(WidthProperty, null);
            _artBorder.BeginAnimation(OpacityProperty, null);
            _artBorder.Width = size;
            _artBorder.Height = size;
            _artBorder.Opacity = 1;
        }
        else _artBorder.Visibility = Visibility.Collapsed;

        _introText.Text = text;
        _introHost.BeginAnimation(OpacityProperty, null);
        Animate(_introHost, OpacityProperty, 0, 1, _config.LineFadeInMs);
    }

    public void EndIntro(string targetLine, Action? onDone)
    {
        if (!_introActive) { onDone?.Invoke(); return; }

        int half = Math.Max(1, _config.ScrambleMs);
        if (_artBorder.Visibility == Visibility.Visible)
        {
            double w = _artBorder.ActualWidth > 0 ? _artBorder.ActualWidth : _artBorder.Width;
            Animate(_artBorder, WidthProperty, w, 0, half);
            Animate(_artBorder, OpacityProperty, _artBorder.Opacity, 0, half);
        }

        Scramble(_introText, targetLine ?? string.Empty, _config.ScrambleMs, () =>
        {
            Animate(_introHost, OpacityProperty, 1, 0, _config.LineFadeOutMs);
            _introActive = false;
            ApplyView(_pendingView);
            onDone?.Invoke();
        });
    }

    private void Scramble(TextBlock tb, string target, int durationMs, Action? onComplete)
    {
        CancelScramble();
        const int frameMs = 28;
        int steps = Math.Max(1, durationMs / frameMs);
        int frame = 0;
        _scrambleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(frameMs) };
        _scrambleTimer.Tick += (_, _) =>
        {
            frame++;
            double prog = Math.Min(1.0, (double)frame / steps);
            tb.Text = target.Length == 0 ? RandomRun((int)Math.Round((1 - prog) * 10)) : ResolveScramble(target, prog);
            if (frame >= steps) { CancelScramble(); tb.Text = target; onComplete?.Invoke(); }
        };
        _scrambleTimer.Start();
    }

    private string RandomRun(int n)
    {
        var sb = new StringBuilder(n);
        for (int i = 0; i < n; i++) sb.Append(Pool[_rnd.Next(Pool.Length)]);
        return sb.ToString();
    }

    private string ResolveScramble(string target, double prog)
    {
        int resolved = (int)Math.Round(prog * target.Length);
        var sb = new StringBuilder(target.Length);
        for (int i = 0; i < target.Length; i++)
        {
            char c = target[i];
            sb.Append(i < resolved || c == ' ' ? c : Pool[_rnd.Next(Pool.Length)]);
        }
        return sb.ToString();
    }

    private void CancelScramble() { _scrambleTimer?.Stop(); _scrambleTimer = null; }

    // ---- Lyrics ------------------------------------------------------------
    public void ApplyView(LyricView view)
    {
        if (_introActive) { _pendingView = view; return; }
        _lastView = view;
        if (view.LineId == -1 || view.Words.Count == 0) { ClearContent(); return; }
        if (view.Snap) { NewLine(view, snap: true); return; }
        if (view.LineId == _curLineId) { RevealTo(view.Reveal); return; }
        NewLine(view, snap: false);
    }

    private void NewLine(LyricView view, bool snap)
    {
        var incoming = ReferenceEquals(_frontHost, _hostA) ? _hostB : _hostA;
        var outgoing = _frontHost;

        incoming.Children.Clear();
        incoming.LayoutTransform = null;
        var words = new List<TextBlock>(view.Words.Count);
        for (int k = 0; k < view.Words.Count; k++)
        {
            var tb = MakeWord(view.Words[k], k < view.Reveal);
            words.Add(tb);
            incoming.Children.Add(tb);
        }
        FitOneLine(incoming); // scale the line down if it would exceed the empty region — never wrap

        if (snap)
        {
            // jump instantly (no cross-fade) — used after a manual seek
            incoming.BeginAnimation(OpacityProperty, null); incoming.Opacity = 1;
            outgoing.BeginAnimation(OpacityProperty, null); outgoing.Opacity = 0;
        }
        else
        {
            Animate(incoming, OpacityProperty, incoming.Opacity, 1, _config.LineFadeInMs);
            Animate(outgoing, OpacityProperty, outgoing.Opacity, 0, _config.LineFadeOutMs);
        }

        _frontHost = incoming;
        _frontWords = words;
        _curLineId = view.LineId;
        _curReveal = view.Reveal;
    }

    /// <summary>Immediately drop the intro card (no scramble) — used on a manual seek.</summary>
    public void CancelIntro()
    {
        if (!_introActive) return;
        CancelScramble();
        _introActive = false;
        _introHost.BeginAnimation(OpacityProperty, null);
        _introHost.Opacity = 0;
    }

    private void RevealTo(int count)
    {
        for (int k = _curReveal; k < count && k < _frontWords.Count; k++)
            EnterWord(_frontWords[k]);
        _curReveal = Math.Max(_curReveal, count);
    }

    private void EnterWord(TextBlock tb)
    {
        int ms = Math.Max(1, _config.WordFadeMs);
        switch ((_config.AnimationStyle ?? "Slide").ToLowerInvariant())
        {
            case "fade":
                Animate(tb, OpacityProperty, tb.Opacity, 1, ms);
                break;
            case "scramble":
                tb.Opacity = 1;
                ScrambleWordIn(tb, ms);
                break;
            default: // slide
                Animate(tb, OpacityProperty, tb.Opacity, 1, ms);
                if (tb.RenderTransform is TranslateTransform t)
                    Animate(t, TranslateTransform.YProperty, t.Y, 0, ms);
                break;
        }
    }

    private void ScrambleWordIn(TextBlock tb, int ms)
    {
        string target = tb.Text;
        if (string.IsNullOrWhiteSpace(target)) return;
        const int frameMs = 24;
        int steps = Math.Max(1, ms / frameMs);
        int frame = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(frameMs) };
        timer.Tick += (_, _) =>
        {
            frame++;
            tb.Text = ResolveScramble(target, Math.Min(1.0, (double)frame / steps));
            if (frame >= steps) { timer.Stop(); tb.Text = target; }
        };
        timer.Start();
    }

    private void ClearContent()
    {
        if (_curLineId == -1) return;
        Animate(_frontHost, OpacityProperty, _frontHost.Opacity, 0, _config.LineFadeOutMs);
        _curLineId = -1;
        _curReveal = 0;
    }

    // ---- Window placement / styles -----------------------------------------
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        long ex = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        ex |= NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TRANSPARENT
            | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
        ex &= ~(long)NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));

        if (_placement is { } p) ApplyPlacementCore(p);
    }

    public void ApplyPlacement(OverlayPlacement p)
    {
        _placement = p;
        if (_hwnd != IntPtr.Zero) ApplyPlacementCore(p);
    }

    private void ApplyPlacementCore(OverlayPlacement p)
        => NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
            p.Left, p.Top, p.Width, p.Height, NativeMethods.SWP_NOACTIVATE);

    public void ReassertTopmost()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE);
    }

    public void FadeShow()
    {
        if (_shown) return;
        _shown = true;
        ReassertTopmost();
        Animate(this, OpacityProperty, Opacity, 1, _config.ShowHideMs);
    }

    public void FadeHide()
    {
        if (!_shown) return;
        _shown = false;
        BeginAnimation(OpacityProperty, MakeAnim(Opacity, 0, _config.ShowHideMs));
    }

    // ---- Helpers -----------------------------------------------------------
    private static void Animate(IAnimatable target, DependencyProperty prop, double from, double to, int ms)
        => target.BeginAnimation(prop, MakeAnim(from, to, ms));

    private static DoubleAnimation MakeAnim(double from, double to, int ms) => new(from, to,
        new Duration(TimeSpan.FromMilliseconds(Math.Max(1, ms))))
    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

    private static Color Hue(double deg)
    {
        double h = deg / 60.0; int i = (int)Math.Floor(h) % 6; double f = h - Math.Floor(h);
        double v = 1, p = 0, q = 1 - f, t = f;
        double r, g, b;
        switch (i)
        {
            case 0: r = v; g = t; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = t; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = t; g = p; b = v; break;
            default: r = v; g = p; b = q; break;
        }
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static Color ParseColor(string s, Color fallback)
    {
        try { if (ColorConverter.ConvertFromString(s) is Color c) return c; }
        catch { /* fall through */ }
        return fallback;
    }

    /// <summary>Test hook (used by --render-line): lay the content out at a DIP size on a dark
    /// backdrop and save it to a PNG, so wrapping can be verified without screen capture.</summary>
    public void RenderTo(string path, int widthDip, int heightDip)
    {
        Root.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)); // taskbar-like backdrop
        Width = widthDip; Height = heightDip;
        Root.Measure(new Size(widthDip, heightDip));
        Root.Arrange(new Rect(0, 0, widthDip, heightDip));
        Root.UpdateLayout();

        var rtb = new RenderTargetBitmap((int)(widthDip * 1.5), (int)(heightDip * 1.5), 144, 144, PixelFormats.Pbgra32);
        rtb.Render(Root);
        rtb.Freeze();
        using var fs = System.IO.File.Create(path);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        enc.Save(fs);
    }
}
