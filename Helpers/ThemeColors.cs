using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PCCast2TV.Helpers;

// The 8 color themes, lightest to darkest (shown 4 over 4 in settings). Each takes CYAN's steps from its own main color,
// so every part changes together; Peak is a deep accent, Ink the same black in all. RANDOM builds a set the same way
// from any hue, or now and then a grey; Roll (its saved key) is set only on those.
public static class ThemeColors
{
    public sealed record Palette(string Name, Color Main, Color Soft, Color Second, Color Enc, Color Peak, Color Dim, Color DimText, string Roll = null);

    // Saved keys: one of the 8 by name, HUE:<degrees> for a rolled color, GREY:<degrees>:<tint %>:<lightness %> for a rolled grey.
    const string HueKey = "HUE:", GreyKey = "GREY:";

    public static IReadOnlyList<Palette> All { get; } = new[]
    {
        Make("AMBER",  "#F5B83D", "#FAE1A8", "#C4A84F", "#AC9B6A", "#5F2D91", "#3A300E", "#5E5534"),
        Make("CYAN",   "#3CCBF4", "#A8E6FA", "#4F9FC4", "#6A98AC", "#662D91", "#0E2C3A", "#34505E"),
        Make("GREEN",  "#34D399", "#A8FADE", "#4FC4A4", "#6AAC99", "#2D2F91", "#0E3A2E", "#345E53"),
        Make("ORANGE", "#FF8A3D", "#FACAA8", "#C4884F", "#AC896A", "#702D91", "#3A240E", "#5E4934"),
        Make("PINK",   "#F472B6", "#FAA8D1", "#C44F81", "#AC6A87", "#4B2D91", "#3A0E21", "#5E3445"),
        Make("VIOLET", "#A78BFA", "#BEA8FA", "#774FC4", "#806AAC", "#912D55", "#1D0E3A", "#43345E"),
        Make("RED",    "#F25F5C", "#FAABA8", "#C45C4F", "#AC716A", "#3E2D91", "#3A130E", "#5E3934"),
        Make("COBALT", "#3D6BE8", "#A8BCFA", "#4F64C4", "#6A76AC", "#912D89", "#0E163A", "#343B5E"),
    };

    public static Palette Current { get; private set; } = All[1];

    // After the colors change; windows repaint what they drew themselves (title bar, glow, device row).
    public static event Action Changed;

    // What the settings file keeps for a theme.
    public static string KeyOf(Palette p) => p.Roll ?? p.Name;

    // RANDOM: about 1 in 6 rolls is a grey (half plain, half lightly tinted), the rest a full color.
    // Each roll is far enough from the one on now that every click shows a real change.
    public static string Roll()
    {
        var r = Random.Shared;
        bool greyNow = Current.Roll?.StartsWith(GreyKey, StringComparison.Ordinal) == true;
        if (r.Next(6) == 0)
        {
            double lumNow = Luminance(Current.Main), lum;
            do lum = Math.Round(0.17 + r.NextDouble() * 0.38, 2);
            while (greyNow && Math.Abs(lum - lumNow) < 0.1);
            return FromGrey(r.Next(360), r.Next(2) == 0 ? 0 : (8 + r.Next(11)) / 100.0, lum).Roll;
        }
        double now = HueOf(Current.Main), h;
        do h = r.Next(360);
        while (!greyNow && Math.Min(Math.Abs(h - now), 360 - Math.Abs(h - now)) < 40); // after a grey any color is a change
        return FromHue(h).Roll;
    }

    // CYAN's steps from any hue. Only the hue is random: the brightness stays in the 8 themes' range (no darker than
    // COBALT, no brighter than AMBER), so yellows are toned down, deep blues lifted, and every roll reads well on the black.
    static Palette FromHue(double hue)
    {
        double l = 0.60;
        while (l > 0.40 && Luminance(Hsl(hue, 0.85, l)) > 0.55) l -= 0.01;
        while (l < 0.75 && Luminance(Hsl(hue, 0.85, l)) < 0.17) l += 0.01;
        return new Palette("RANDOM", Hsl(hue, 0.85, l), Hsl(hue + 1.3, 0.891, 0.82), Hsl(hue + 5.6, 0.498, 0.539), Hsl(hue + 4.8, 0.284, 0.545),
                           Hsl(hue + 80.8, 0.526, 0.373), Hsl(hue + 5.7, 0.611, 0.141), Hsl(hue + 6.6, 0.288, 0.286),
                           HueKey + hue.ToString("0", CultureInfo.InvariantCulture));
    }

    // A grey from silver (lightness 0.55) down to gunmetal (0.17), plain (tint 0) or with a hint of a hue (up to 0.18,
    // like steel or stone). The other parts step down from it the way CYAN's do; the peak keeps a deep color so it stands out.
    static Palette FromGrey(double hue, double tint, double lum)
    {
        double l = 0.30;
        while (l < 0.90 && Luminance(Hsl(hue, tint, l)) < lum) l += 0.005;
        return new Palette("RANDOM", Hsl(hue, tint, l), Hsl(hue, tint, Math.Min(0.92, l + 0.22)), Hsl(hue, tint * 0.6, l * 0.88), Hsl(hue, tint * 0.4, l * 0.9),
                           Hsl(hue + 180, 0.526, 0.373), Hsl(hue, tint * 0.8, 0.14), Hsl(hue, tint * 0.5, 0.29),
                           string.Create(CultureInfo.InvariantCulture, $"{GreyKey}{hue:0}:{tint * 100:0}:{lum * 100:0}"));
    }

    // The palette a saved key names: one of the 8, a rolled color or a rolled grey; anything unreadable means CYAN.
    public static Palette FromKey(string key)
    {
        static bool Num(string s, out double v) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        static double Wrap(double h) => ((h % 360) + 360) % 360;
        if (key != null && key.StartsWith(HueKey, StringComparison.Ordinal) && Num(key[HueKey.Length..], out var hue)) return FromHue(Wrap(hue));
        if (key != null && key.StartsWith(GreyKey, StringComparison.Ordinal))
        {
            var p = key[GreyKey.Length..].Split(':');
            if (p.Length == 3 && Num(p[0], out var h) && Num(p[1], out var t) && Num(p[2], out var lum))
                return FromGrey(Wrap(h), Math.Clamp(t / 100, 0, 0.18), Math.Clamp(lum / 100, 0.17, 0.55));
        }
        return All.FirstOrDefault(t => t.Name == key) ?? All[1];
    }

    // Swaps the shared colors every window reads.
    public static void Apply(string key)
    {
        var p = FromKey(key);
        Current = p;
        var r = Application.Current.Resources;
        r["Main"] = Brush(p.Main);
        r["Soft"] = Brush(p.Soft);
        r["Second"] = Brush(p.Second);
        r["Enc"] = Brush(p.Enc);
        r["Peak"] = Brush(p.Peak);
        r["Dim"] = Brush(p.Dim);
        r["DimText"] = Brush(p.DimText);
        r["Grid"] = Brush(Color.FromArgb(0x38, p.Second.R, p.Second.G, p.Second.B));
        Changed?.Invoke();
    }

    // A pill clicked in settings: the colors change at once and are kept for next time.
    public static void Pick(string key)
    {
        if (key == KeyOf(Current)) return;
        Apply(key);
        AppSettings.SetTheme(key);
    }

    // MY COLOR: the one RANDOM roll kept with SAVE, or null before any.
    public static string MyColor => AppSettings.MyTheme;

    // The roll on screen when it isn't MY COLOR yet (what SAVE would keep), else null.
    public static string UnsavedRoll => Current.Roll != null && Current.Roll != MyColor ? Current.Roll : null;

    // SAVE: the roll on screen becomes MY COLOR (one only, a new save replaces it).
    public static void KeepRoll()
    {
        if (UnsavedRoll is string roll) AppSettings.SetMyTheme(roll);
    }

    static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    static Color Hsl(double hue, double s, double l)
    {
        hue = ((hue % 360) + 360) % 360 / 60;
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(hue % 2 - 1)), m = l - c / 2;
        var (r, g, b) = (int)hue switch { 0 => (c, x, 0d), 1 => (x, c, 0d), 2 => (0d, c, x), 3 => (0d, x, c), 4 => (x, 0d, c), _ => (c, 0d, x) };
        return Color.FromRgb(Byte(r + m), Byte(g + m), Byte(b + m));
    }

    static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);

    static double HueOf(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), d = max - Math.Min(r, Math.Min(g, b));
        if (d == 0) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 + 360) % 360;
    }

    // How light a color looks (0 black, 1 white), weighted the way eyes see it.
    static double Luminance(Color c)
    {
        static double Lin(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    static Palette Make(string name, params string[] hex)
    {
        var c = hex.Select(h => (Color)ColorConverter.ConvertFromString(h)).ToArray();
        return new Palette(name, c[0], c[1], c[2], c[3], c[4], c[5], c[6]);
    }
}
