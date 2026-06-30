using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SpotifyLyricsTaskbar.Util;

namespace SpotifyLyricsTaskbar.Settings;

/// <summary>
/// "Add colour" popup: an HSV wheel (hue + saturation) with a brightness slider,
/// plus hex and R/G/B inputs that stay in sync. Calls back with the chosen colour
/// when the user confirms. Built programmatically to match the settings styling.
/// </summary>
internal sealed class ColorEditor
{
    private static readonly Color Panel = (Color)ColorConverter.ConvertFromString("#0E0F11");
    private static readonly Color Sub = (Color)ColorConverter.ConvertFromString("#16171A");
    private static readonly Color Stroke = (Color)ColorConverter.ConvertFromString("#292B30");
    private static readonly Color TextC = (Color)ColorConverter.ConvertFromString("#F1F1F3");
    private static readonly Color Muted = (Color)ColorConverter.ConvertFromString("#83858C");
    private static readonly Color Mint = (Color)ColorConverter.ConvertFromString("#E9EEF1");

    private double _h = 0, _s = 0, _l = 1; // current HSL (0..1)
    private bool _suppress;

    private readonly Image _wheel = new() { Width = 150, Height = 150, Cursor = Cursors.Cross };
    private readonly Ellipse _dot = new() { Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Slider _bright = new() { Minimum = 0.06, Maximum = 0.97, Orientation = Orientation.Vertical, Height = 150, Width = 24 };
    private readonly TextBox _hex = MakeBox(86);
    private readonly TextBox _r = MakeBox(40), _g = MakeBox(40), _b = MakeBox(40);
    private readonly Border _previewChip = new() { Width = 40, Height = 28, CornerRadius = new CornerRadius(7), BorderBrush = new SolidColorBrush(Stroke), BorderThickness = new Thickness(1) };

    private readonly Panel _host;
    private readonly Action<Color> _onApply;
    private Popup? _popup;

    private ColorEditor(Panel host, Action<Color> onApply) { _host = host; _onApply = onApply; }

    public static void Open(Panel host, UIElement target, Color initial, Action<Color> onApply)
        => new ColorEditor(host, onApply).Show(target, initial);

    private void Show(UIElement target, Color initial)
    {
        (_h, _s, _l) = ColorTools.ToHsl(initial);
        BuildWheel();

        _bright.Value = Math.Clamp(_l, _bright.Minimum, _bright.Maximum);
        _bright.ValueChanged += (_, _) => { if (_suppress) return; _l = _bright.Value; Recompute(); };

        _wheel.MouseLeftButtonDown += WheelPick;
        _wheel.MouseMove += (s, e) => { if (e.LeftButton == MouseButtonState.Pressed) WheelPick(s, e); };

        _hex.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitHex(); };
        _hex.LostFocus += (_, _) => CommitHex();
        foreach (var t in new[] { _r, _g, _b })
        {
            t.KeyDown += (_, e) => { if (e.Key == Key.Enter) CommitRgb(); };
            t.LostFocus += (_, _) => CommitRgb();
        }

        var wheelCell = new Grid { Width = 150, Height = 150 };
        wheelCell.Children.Add(_wheel);
        wheelCell.Children.Add(_dot);

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(wheelCell);
        top.Children.Add(new Border { Width = 12 });
        top.Children.Add(_bright);

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        hexRow.Children.Add(Label("HEX"));
        hexRow.Children.Add(_hex);
        hexRow.Children.Add(new Border { Width = 12 });
        hexRow.Children.Add(_previewChip);

        var rgbRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        rgbRow.Children.Add(Label("R")); rgbRow.Children.Add(_r); rgbRow.Children.Add(new Border { Width = 8 });
        rgbRow.Children.Add(Label("G")); rgbRow.Children.Add(_g); rgbRow.Children.Add(new Border { Width = 8 });
        rgbRow.Children.Add(Label("B")); rgbRow.Children.Add(_b);

        var add = new Button { Content = "Add colour", Cursor = Cursors.Hand, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Stretch, Foreground = new SolidColorBrush(Mint), FontWeight = FontWeights.SemiBold };
        add.Template = ButtonTemplate(Mint);
        add.Click += (_, _) => { _onApply(ColorTools.FromHsl(_h, _s, _l)); Close(); };

        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = "ADD COLOUR", Foreground = new SolidColorBrush(Muted), FontSize = 11, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        content.Children.Add(top);
        content.Children.Add(hexRow);
        content.Children.Add(rgbRow);
        content.Children.Add(add);

        var shell = new Border
        {
            Background = new SolidColorBrush(Panel),
            CornerRadius = new CornerRadius(14),
            BorderBrush = new SolidColorBrush(Stroke),
            BorderThickness = new Thickness(1),
            Child = content,
        };

        _popup = new Popup { PlacementTarget = target, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = shell };
        _host.Children.Add(_popup);
        _popup.Closed += (_, _) => _host.Children.Remove(_popup);
        _popup.IsOpen = true;
        Recompute();
    }

    private void Close() { if (_popup != null) _popup.IsOpen = false; }

    private void WheelPick(object sender, MouseEventArgs e)
    {
        double r = _wheel.Width / 2;
        var p = e.GetPosition(_wheel);
        double dx = p.X - r, dy = p.Y - r, dist = Math.Sqrt(dx * dx + dy * dy);
        _h = (Math.Atan2(dy, dx) / (2 * Math.PI) + 1) % 1;
        _s = Math.Min(1, dist / r);
        Recompute();
    }

    private void CommitHex()
    {
        if (_suppress) return;
        var text = _hex.Text.Trim().TrimStart('#');
        if (text.Length == 6 && uint.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            SetFromColor(Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v));
    }

    private void CommitRgb()
    {
        if (_suppress) return;
        byte P(TextBox t) => (byte)Math.Clamp(int.TryParse(t.Text.Trim(), out var n) ? n : 0, 0, 255);
        SetFromColor(Color.FromRgb(P(_r), P(_g), P(_b)));
    }

    private void SetFromColor(Color c)
    {
        (_h, _s, _l) = ColorTools.ToHsl(c);
        Recompute();
    }

    private void Recompute()
    {
        var c = ColorTools.FromHsl(_h, _s, _l);
        _suppress = true;
        _previewChip.Background = new SolidColorBrush(c);
        _hex.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        _r.Text = c.R.ToString(); _g.Text = c.G.ToString(); _b.Text = c.B.ToString();
        _bright.Value = Math.Clamp(_l, _bright.Minimum, _bright.Maximum);
        double rad = _wheel.Width / 2;
        double angle = _h * 2 * Math.PI;
        _dot.Margin = new Thickness(rad + Math.Cos(angle) * _s * rad - _dot.Width / 2,
                                    rad + Math.Sin(angle) * _s * rad - _dot.Height / 2, 0, 0);
        _dot.HorizontalAlignment = HorizontalAlignment.Left;
        _dot.VerticalAlignment = VerticalAlignment.Top;
        _dot.Fill = new SolidColorBrush(c);
        _suppress = false;
    }

    private void BuildWheel()
    {
        const int size = 150;
        var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
        var px = new byte[size * size * 4];
        double r = size / 2.0;
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                double dx = x - r, dy = y - r, dist = Math.Sqrt(dx * dx + dy * dy);
                int i = (y * size + x) * 4;
                if (dist > r) { px[i + 3] = 0; continue; }
                double hue = (Math.Atan2(dy, dx) / (2 * Math.PI) + 1) % 1;
                var c = ColorTools.FromHsl(hue, Math.Min(1, dist / r), 0.5);
                px[i] = c.B; px[i + 1] = c.G; px[i + 2] = c.R; px[i + 3] = 255;
            }
        bmp.WritePixels(new Int32Rect(0, 0, size, size), px, size * 4, 0);
        bmp.Freeze();
        _wheel.Source = bmp;
    }

    // ---- small UI builders ----
    private static TextBlock Label(string t) => new()
    {
        Text = t, Foreground = new SolidColorBrush(Muted), FontSize = 12, Width = 30,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static TextBox MakeBox(double w)
    {
        var tb = new TextBox
        {
            Width = w, FontSize = 13, Padding = new Thickness(6, 4, 6, 4),
            Background = new SolidColorBrush(Sub), Foreground = new SolidColorBrush(TextC),
            BorderBrush = new SolidColorBrush(Stroke), BorderThickness = new Thickness(1),
            CaretBrush = new SolidColorBrush(TextC), VerticalContentAlignment = VerticalAlignment.Center,
        };
        return tb;
    }

    private static ControlTemplate ButtonTemplate(Color border)
    {
        var b = new FrameworkElementFactory(typeof(Border));
        b.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        b.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));
        b.SetValue(Border.BorderBrushProperty, new SolidColorBrush(border));
        b.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        b.SetValue(Border.PaddingProperty, new Thickness(12, 9, 12, 9));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        b.AppendChild(cp);
        return new ControlTemplate(typeof(Button)) { VisualTree = b };
    }
}
