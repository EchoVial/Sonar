using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpotifyLyricsTaskbar.Util;

public static class ColorTools
{
    /// <summary>Pick a vibrant, readable colour representative of album art (or null/white if grayscale).</summary>
    public static Color? ExtractAlbumColor(byte[]? data)
    {
        if (data is not { Length: > 0 }) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(data);
            bmp.DecodePixelWidth = 32;
            bmp.EndInit();
            bmp.Freeze();

            var fmt = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            fmt.Freeze();
            int w = fmt.PixelWidth, h = fmt.PixelHeight, stride = w * 4;
            var px = new byte[h * stride];
            fmt.CopyPixels(px, stride, 0);

            double rs = 0, gs = 0, bs = 0, wsum = 0;
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i], g = px[i + 1], r = px[i + 2];
                byte a = px[i + 3];
                if (a < 8) continue;
                double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                double sat = max <= 0 ? 0 : (max - min) / max;
                double bright = max / 255.0;
                double weight = sat * sat * (bright > 0.15 ? 1 : 0.2); // favour colourful, non-black pixels
                rs += r * weight; gs += g * weight; bs += b * weight; wsum += weight;
            }
            if (wsum < 1e-3) return Colors.White; // grayscale art
            return Boost(Color.FromRgb((byte)(rs / wsum), (byte)(gs / wsum), (byte)(bs / wsum)));
        }
        catch { return null; }
    }

    /// <summary>Push a colour toward a bright, saturated value so it reads on a dark taskbar.</summary>
    private static Color Boost(Color c)
    {
        RgbToHsl(c, out double h, out double s, out double _);
        return FromHsl(h, Math.Max(s, 0.55), 0.62);
    }

    /// <summary>Public HSL decomposition (h,s,l each 0..1) for the colour editor.</summary>
    public static (double h, double s, double l) ToHsl(Color c)
    {
        RgbToHsl(c, out double h, out double s, out double l);
        return (h, s, l);
    }

    private static void RgbToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        l = (max + min) / 2;
        if (d < 1e-6) { h = 0; s = 0; return; }
        s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h /= 6;
    }

    public static Color FromHsl(double h, double s, double l)
    {
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        return Color.FromRgb(Chan(p, q, h + 1.0 / 3), Chan(p, q, h), Chan(p, q, h - 1.0 / 3));
    }

    private static byte Chan(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        double v = t < 1.0 / 6 ? p + (q - p) * 6 * t
            : t < 1.0 / 2 ? q
            : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6
            : p;
        return (byte)Math.Round(v * 255);
    }
}
