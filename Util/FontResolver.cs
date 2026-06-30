using System.Windows;
using System.Windows.Media;

namespace SpotifyLyricsTaskbar.Util;

public static class FontResolver
{
    // Only these are bundled in /Fonts; everything else is a system font.
    private static readonly HashSet<string> Embedded =
        new(StringComparer.OrdinalIgnoreCase) { "Poppins", "Space Mono" };

    /// <summary>Resolve an embedded font (in /Fonts) by family name, else a system font.</summary>
    public static FontFamily Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return new FontFamily("Segoe UI");
        name = name.Trim();

        if (Embedded.Contains(name))
        {
            try
            {
                var packed = new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#" + name);
                if (packed.GetTypefaces().Count > 0) return packed;
            }
            catch { /* fall through to system */ }
        }

        try { return new FontFamily(name); } catch { return new FontFamily("Segoe UI"); }
    }

    public static FontWeight Weight(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return FontWeights.Normal;
        if (s.Equals("SemiLight", StringComparison.OrdinalIgnoreCase)) return FontWeight.FromOpenTypeWeight(350);
        try { if (new FontWeightConverter().ConvertFromString(s) is FontWeight fw) return fw; }
        catch { /* fall through */ }
        if (int.TryParse(s, out var n) && n is >= 1 and <= 999) return FontWeight.FromOpenTypeWeight(n);
        return FontWeights.Normal;
    }
}
