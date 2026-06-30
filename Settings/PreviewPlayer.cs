using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SpotifyLyricsTaskbar.Config;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar.Settings;

/// <summary>Loops a sample lyric in the settings "display", honouring the current
/// font / colour / glow / animation style so changes (incl. hover) preview live.</summary>
public sealed class PreviewPlayer
{
    private const string Pool = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789#$%&*@/";

    private readonly StackPanel _host;
    private readonly DropShadowEffect _glow = new() { ShadowDepth = 0, BlurRadius = 18, Opacity = 1, Color = Colors.White };
    private readonly SolidColorBrush _brush = new(Colors.White);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly Random _rnd = new();

    private readonly string[] _samples =
    {
        "feel the night",
        "we are dreamers",
        "city lights glow",
        "lost in the music",
    };
    private int _sampleIdx;
    private string[] _words = Array.Empty<string>();
    private int _reveal, _phase, _hold;
    private bool _paused;

    private FontFamily _font = new("Segoe UI");
    private FontWeight _weight = FontWeights.SemiBold;
    private double _size = 20;
    private string _style = "Slide";

    public PreviewPlayer(StackPanel host)
    {
        _host = host;
        _host.Effect = _glow;
        _timer.Tick += Tick;
    }

    public void Apply(AppConfig c, Color? album)
    {
        _font = FontResolver.Resolve(c.FontFamily);
        _weight = FontResolver.Weight(c.FontWeight);
        _size = Math.Max(13, c.FontSize + 5);
        _style = c.AnimationStyle ?? "Slide";
        _glow.BlurRadius = c.GlowBlurRadius;
        _glow.Opacity = c.GlowOpacity;
        _host.Opacity = Math.Clamp(c.TextOpacity, 0.1, 1.0);

        _brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
        switch ((c.ColorMode ?? "Static").ToLowerInvariant())
        {
            case "rgb":
                var anim = new ColorAnimationUsingKeyFrames
                { Duration = TimeSpan.FromSeconds(Math.Max(1, c.RgbSpeedSec)), RepeatBehavior = RepeatBehavior.Forever };
                for (int i = 0; i <= 6; i++)
                    anim.KeyFrames.Add(new LinearColorKeyFrame(ColorTools.FromHsl(i / 6.0, 0.9, 0.6), KeyTime.FromPercent(i / 6.0)));
                anim.Freeze();
                _brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
                _glow.Color = ColorTools.FromHsl(0.45, 0.9, 0.6);
                break;
            case "album":
                var ac = album ?? Color.FromRgb(0xA7, 0xE0, 0xC1);
                _brush.Color = ac; _glow.Color = ac;
                break;
            default:
                var col = Parse(c.TextColor, Colors.White);
                _brush.Color = col;
                _glow.Color = c.GlowFollowsText ? col : Parse(c.GlowColor, col);
                break;
        }
        Rebuild();
    }

    public void Start() { _paused = false; if (!_timer.IsEnabled) _timer.Start(); }
    public void Stop() => _timer.Stop();
    public void TogglePlay() { _paused = !_paused; }
    public void NextSample() { _sampleIdx++; Rebuild(); }
    public void Replay() { Rebuild(); }

    private void Rebuild()
    {
        _words = _samples[((_sampleIdx % _samples.Length) + _samples.Length) % _samples.Length].Split(' ');
        _reveal = 0; _phase = 0; _hold = 0;
        _host.Children.Clear();
        foreach (var w in _words)
        {
            _host.Children.Add(new TextBlock
            {
                Text = w + " ",
                FontFamily = _font,
                FontWeight = _weight,
                FontSize = _size,
                Foreground = _brush,
                Opacity = 0,
                RenderTransform = new TranslateTransform(0, 5),
                RenderTransformOrigin = new Point(0, 0.5),
            });
        }
    }

    private void Tick(object? sender, EventArgs e)
    {
        if (_paused) return;
        switch (_phase)
        {
            case 0:
                if (_reveal < _words.Length && _reveal < _host.Children.Count)
                    EnterWord((TextBlock)_host.Children[_reveal++]);
                else { _phase = 1; _hold = 0; }
                break;
            case 1:
                if (++_hold > 11) { _phase = 2; _hold = 0; FadeOut(); }
                break;
            default:
                if (++_hold > 6) { _sampleIdx++; Rebuild(); }
                break;
        }
    }

    private void EnterWord(TextBlock tb)
    {
        switch (_style.ToLowerInvariant())
        {
            case "fade":
                Animate(tb, UIElement.OpacityProperty, 0, 1, 150);
                break;
            case "scramble":
                tb.Opacity = 1;
                ScrambleIn(tb);
                break;
            default: // slide
                Animate(tb, UIElement.OpacityProperty, 0, 1, 150);
                if (tb.RenderTransform is TranslateTransform t)
                    Animate(t, TranslateTransform.YProperty, 5, 0, 180);
                break;
        }
    }

    private void ScrambleIn(TextBlock tb)
    {
        string target = tb.Text;
        int steps = 5, frame = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(26) };
        timer.Tick += (_, _) =>
        {
            frame++;
            double prog = (double)frame / steps;
            int resolved = (int)Math.Round(prog * target.Length);
            var sb = new StringBuilder(target.Length);
            for (int i = 0; i < target.Length; i++)
                sb.Append(i < resolved || target[i] == ' ' ? target[i] : Pool[_rnd.Next(Pool.Length)]);
            tb.Text = sb.ToString();
            if (frame >= steps) { timer.Stop(); tb.Text = target; }
        };
        timer.Start();
    }

    private void FadeOut()
    {
        foreach (UIElement c in _host.Children)
            Animate(c, UIElement.OpacityProperty, c.Opacity, 0, 220);
    }

    private static void Animate(IAnimatable target, DependencyProperty prop, double from, double to, int ms)
        => target.BeginAnimation(prop, new DoubleAnimation(from, to, new Duration(TimeSpan.FromMilliseconds(ms)))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

    private static Color Parse(string s, Color fallback)
    {
        try { if (ColorConverter.ConvertFromString(s) is Color c) return c; } catch { }
        return fallback;
    }
}
