using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace TileSmith.Theme;

/// <summary>
/// Colour tokens. Dark follows DailyDash's "Cursor dark" scheme (canvas #181818, cards #1F1F1F,
/// steel-blue accent), light follows Cinema-Info's warm paper palette. Text steps clear WCAG AA.
/// </summary>
public sealed class Palette
{
    private readonly Dictionary<Color, SolidColorBrush> _brushes = [];

    public required bool IsDark { get; init; }
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

    public static void Apply(bool dark, string accentSetting)
    {
        var accent = ResolveAccent(accentSetting, dark);
        Current = dark ? Dark(accent) : Light(accent);
    }

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
