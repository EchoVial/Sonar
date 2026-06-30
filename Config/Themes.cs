namespace SpotifyLyricsTaskbar.Config;

public sealed record ThemeDef(
    string Name, string Font, string Weight, string Text, string Glow,
    double Blur, double Opacity, string ColorMode, string Animation);

/// <summary>Preset looks. Applying one sets the appearance fields on the config.</summary>
public static class Themes
{
    public static readonly IReadOnlyList<ThemeDef> All = new[]
    {
        new ThemeDef("Midnight", "Poppins",    "SemiBold", "#FFFFFFFF", "#FFFFFFFF", 20, 1.0, "Static", "Slide"),
        new ThemeDef("Mint",     "Poppins",    "SemiBold", "#FF3DDC97", "#FF3DDC97", 22, 1.0, "Static", "Slide"),
        new ThemeDef("Vapor",    "Poppins",    "Bold",     "#FFFF6AD5", "#FF7AF7FF", 24, 1.0, "Static", "Slide"),
        new ThemeDef("Sunset",   "Poppins",    "SemiBold", "#FFFFB86C", "#FFFF6E6E", 22, 1.0, "Static", "Slide"),
        new ThemeDef("Mono",     "Space Mono", "Bold",     "#FFEDEDED", "#FF9AE6C1", 14, 0.8, "Static", "Scramble"),
        new ThemeDef("Album",    "Poppins",    "SemiBold", "#FFFFFFFF", "#FFFFFFFF", 20, 1.0, "Album",  "Slide"),
        new ThemeDef("RGB",      "Poppins",    "Bold",     "#FFFFFFFF", "#FFFFFFFF", 22, 1.0, "Rgb",    "Slide"),
    };

    public static void Apply(AppConfig c, string name)
    {
        var t = All.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? All[0];
        c.Theme = t.Name;
        c.FontFamily = t.Font;
        c.FontWeight = t.Weight;
        c.TextColor = t.Text;
        c.GlowColor = t.Glow;
        c.GlowBlurRadius = t.Blur;
        c.GlowOpacity = t.Opacity;
        c.ColorMode = t.ColorMode;
        c.AnimationStyle = t.Animation;
    }
}
