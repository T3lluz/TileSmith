using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TileSmith.Theme;

/// <summary>
/// Colour tokens. Solid: dark follows DailyDash's "Cursor dark" scheme (canvas #181818, cards #1F1F1F), light
/// follows Cinema-Info's warm paper palette. Liquid Glass: see-through fills over the blurred wallpaper with Apple's
/// system colours, so tiles read as glass in both themes. Text steps clear WCAG AA on their usual backgrounds.
/// </summary>
public sealed class Palette
{
    private readonly Dictionary<Color, SolidColorBrush> _brushes = [];

    public required bool IsDark { get; init; }

    /// <summary>Liquid Glass: fills are translucent and surfaces get specular rims (see <see cref="Glass"/>).</summary>
    public bool IsGlass { get; init; }
    public required Color Canvas { get; init; }
    public required Color Tile { get; init; }
    public required Color TileHover { get; init; }
    public required Color Card { get; init; }
    public required Color Chrome { get; init; }
    public required Color Well { get; init; }
    public required Color Hairline { get; init; }
    public required Color HairlineStrong { get; init; }
    public required Color Text1 { get; init; }
    public required Color Text2 { get; init; }
    public required Color Text3 { get; init; }
    public required Color Placeholder { get; init; }
    public required Color Accent { get; init; }
    public required Color OnAccent { get; init; }
    public required Color Success { get; init; }
    public required Color Error { get; init; }
    public required Color Warning { get; init; }

    /// <summary>Tinted chip fill behind accent text (DailyDash chipFill: 14%).</summary>
    public Color AccentSoft => WithAlpha(Accent, 0.16);

    public Color Overlay(double alpha) => WithAlpha(IsDark ? Colors.White : Color.FromArgb(255, 30, 24, 18), alpha);

    public SolidColorBrush Brush(Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush)) _brushes[color] = brush = new SolidColorBrush(color);
        return brush;
    }

    public static Palette Current { get; private set; } = Dark(Parse("#81A1C1"));

    public static void Apply(bool dark, string accentSetting, bool glass = false) => Current = For(dark, accentSetting, glass);

    /// <summary>The palette for a theme, accent and style, without making it current.</summary>
    public static Palette For(bool dark, string accentSetting, bool glass)
    {
        var accent = ResolveAccent(accentSetting, dark);
        return glass ? (dark ? GlassDark(accent) : GlassLight(accent)) : dark ? Dark(accent) : Light(accent);
    }

    /// <summary>Builds something with another palette (e.g. a glass preview inside an opaque window), then restores.</summary>
    public static IDisposable Use(Palette palette)
    {
        var previous = Current;
        Current = palette;
        return new Restore(previous);
    }

    private sealed class Restore(Palette previous) : IDisposable
    {
        public void Dispose() => Current = previous;
    }

    public static Palette GlassDark(Color accent) => new()
    {
        IsDark = true,
        IsGlass = true,
        Canvas = Parse("#1C1C1E"),
        Tile = Color.FromArgb(34, 255, 255, 255),
        TileHover = Color.FromArgb(46, 255, 255, 255),
        Card = Color.FromArgb(24, 255, 255, 255),
        Chrome = Color.FromArgb(70, 0, 0, 0),
        Well = Color.FromArgb(64, 0, 0, 0),
        Hairline = WithAlpha(Colors.White, 0.12),
        HairlineStrong = WithAlpha(Colors.White, 0.22),
        Text1 = Parse("#F5F5F7"),
        Text2 = Parse("#C7C7CC"),
        Text3 = Parse("#98989F"),
        Placeholder = Parse("#8E8E93"),
        Accent = accent,
        OnAccent = ContentOn(accent),
        Success = Parse("#30D158"),
        Error = Parse("#FF453A"),
        Warning = Parse("#FFD60A"),
    };

    public static Palette GlassLight(Color accent) => new()
    {
        IsDark = false,
        IsGlass = true,
        Canvas = Parse("#F2F2F7"),
        Tile = Color.FromArgb(150, 255, 255, 255),
        TileHover = Color.FromArgb(185, 255, 255, 255),
        Card = Color.FromArgb(120, 255, 255, 255),
        Chrome = Color.FromArgb(140, 255, 255, 255),
        Well = Color.FromArgb(18, 0, 0, 0),
        Hairline = Color.FromArgb(24, 0, 0, 0),
        HairlineStrong = Color.FromArgb(40, 0, 0, 0),
        Text1 = Parse("#1C1C1E"),
        Text2 = Parse("#48484D"),
        Text3 = Parse("#6E6E73"),
        Placeholder = Parse("#8A8A8E"),
        Accent = accent,
        OnAccent = ContentOn(accent),
        Success = Parse("#248A3D"),
        Error = Parse("#D70015"),
        Warning = Parse("#B25000"),
    };

    public static Palette Dark(Color accent) => new()
    {
        IsDark = true,
        Canvas = Parse("#181818"),
        Tile = Parse("#242424"),
        TileHover = Parse("#2B2B2B"),
        Card = Parse("#1F1F1F"),
        Chrome = Parse("#141414"),
        Well = Parse("#121212"),
        Hairline = WithAlpha(Colors.White, 0.07),
        HairlineStrong = WithAlpha(Colors.White, 0.15),
        Text1 = Parse("#E4E4E4"),
        Text2 = Parse("#A3A3A3"),
        Text3 = Parse("#858585"),
        Placeholder = Parse("#6E6E6E"),
        Accent = accent,
        OnAccent = ContentOn(accent),
        Success = Parse("#3FA266"),
        Error = Parse("#E34671"),
        Warning = Parse("#F1B467"),
    };

    public static Palette Light(Color accent) => new()
    {
        IsDark = false,
        Canvas = Parse("#F4F2EF"),
        Tile = Parse("#FFFFFF"),
        TileHover = Parse("#FAF9F7"),
        Card = Parse("#FFFFFF"),
        Chrome = Parse("#EEEBE7"),
        Well = Parse("#E9E5E0"),
        Hairline = Color.FromArgb(22, 30, 24, 18),
        HairlineStrong = Color.FromArgb(38, 30, 24, 18),
        Text1 = Parse("#1A1612"),
        Text2 = Parse("#5D5750"),
        Text3 = Parse("#8E877F"),
        Placeholder = Parse("#A39C94"),
        Accent = accent,
        OnAccent = ContentOn(accent),
        Success = Parse("#138A43"),
        Error = Parse("#D0202E"),
        Warning = Parse("#B45309"),
    };

    public static readonly (string Id, string Name, string Hex)[] AccentChoices =
    [
        ("steel", "Steel (DailyDash)", "#81A1C1"),
        ("windows", "Windows accent", ""),
        ("blue", "Blue", "#7AB4FF"),
        ("teal", "Teal", "#5CC8B8"),
        ("green", "Green", "#7FCF8E"),
        ("amber", "Amber", "#F1B467"),
        ("coral", "Coral", "#FF8A80"),
        ("pink", "Pink", "#F48FB1"),
        ("violet", "Violet", "#B39DDB"),
    ];

    private static Color ResolveAccent(string setting, bool dark)
    {
        if (setting == "windows")
        {
            try
            {
                var ui = new UISettings();
                return ui.GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
            }
            catch
            {
                // fall through to steel
            }
        }
        var choice = AccentChoices.FirstOrDefault(a => a.Id == setting);
        var hex = !string.IsNullOrEmpty(choice.Hex) ? choice.Hex : setting.StartsWith('#') ? setting : "#81A1C1";
        var color = Parse(hex);
        return dark ? color : Darken(color, 0.62);
    }

    /// <summary>Dark text on light fills, white on dark ones (DailyDash contentColorOn, break-even at 0.2).</summary>
    public static Color ContentOn(Color fill) => Luminance(fill) > 0.2 ? Parse("#14181D") : Colors.White;

    public static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), c.R, c.G, c.B);

    public static Color Darken(Color c, double factor) =>
        Color.FromArgb(c.A, (byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

    public static Color Parse(string hex)
    {
        var h = hex.TrimStart('#');
        if (h.Length == 6) h = "FF" + h;
        var v = Convert.ToUInt32(h, 16);
        return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }
}
