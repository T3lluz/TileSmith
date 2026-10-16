namespace TileSmith.Core.Tiles;

/// <summary>The Material 3 colour roles a design paints with. The order matches <see cref="TilePalette"/>'s tables.</summary>
public enum TileRole
{
    Primary,
    OnPrimary,
    PrimaryContainer,
    OnPrimaryContainer,
    Secondary,
    OnSecondary,
    SecondaryContainer,
    OnSecondaryContainer,
    Tertiary,
    OnTertiary,
    TertiaryContainer,
    OnTertiaryContainer,
    Error,
    OnError,
    ErrorContainer,
    OnErrorContainer,
    Surface,
    OnSurface,
    OnSurfaceVariant,
    SurfaceContainerLowest,
    SurfaceContainerLow,
    SurfaceContainer,
    SurfaceContainerHigh,
    SurfaceContainerHighest,
    Outline,
    OutlineVariant,
    InverseSurface,
    InverseOnSurface,
}

/// <summary>
/// A widget's colours: the panel's accent (an M3 scheme generated from TileSmith's accent colour, which can follow
/// Windows), or one of ten fixed M3 schemes (the same ones DailyDash's Tilesmith uses: SchemeVibrant from each seed;
/// Sand is TonalSpot and Mono is SchemeMonochrome). Tables hold the roles in <see cref="TileRole"/> order as 0xRRGGBB.
/// </summary>
public sealed class TilePalette
{
    private readonly uint[]? _light;
    private readonly uint[]? _dark;

    private TilePalette(string id, string label, uint seed, uint[]? light, uint[]? dark)
    {
        Id = id;
        Label = label;
        Seed = seed;
        _light = light;
        _dark = dark;
    }

    public string Id { get; }
    public string Label { get; }

    /// <summary>The colour the scheme grew from (for swatches), 0xRRGGBB.</summary>
    public uint Seed { get; }

    /// <summary>True for the accent palette: its colours come from the accent the UI passes in.</summary>
    public bool FollowsAccent => _light is null;

    public static readonly TilePalette Accent = new("accent", "Accent", 0x81A1C1, null, null);

    public static readonly TilePalette Ocean = new("ocean", "Ocean", 0x1E6FD9,
        [0x005CBD, 0xFFFFFF, 0xD7E2FF, 0x004591, 0x585C7E, 0xFFFFFF, 0xDFE0FF, 0x404565, 0x615889, 0xFFFFFF, 0xE6DEFF, 0x494070, 0xBA1A1A, 0xFFFFFF, 0xFFDAD6, 0x93000A, 0xF9F9FF, 0x171C25, 0x414753, 0xFFFFFF, 0xF1F3FF, 0xEAEDFA, 0xE5E8F5, 0xDFE2EF, 0x727785, 0xC1C6D6, 0x2C303A, 0xEDF0FD],
        [0xACC7FF, 0x002F67, 0x004591, 0xD7E2FF, 0xC0C4EB, 0x2A2E4D, 0x404565, 0xDFE0FF, 0xCABFF8, 0x322958, 0x494070, 0xE6DEFF, 0xFFB4AB, 0x690005, 0x93000A, 0xFFDAD6, 0x0F131C, 0xDFE2EF, 0xC1C6D6, 0x0A0E17, 0x171C25, 0x1B2029, 0x262A33, 0x31353E, 0x8B919F, 0x414753, 0xDFE2EF, 0x2C303A]);

    public static readonly TilePalette Berry = new("berry", "Berry", 0xC2185B,
        [0xBB0055, 0xFFFFFF, 0xFFD9DF, 0x8F003F, 0x82524E, 0xFFFFFF, 0xFFDAD6, 0x663B37, 0x8A4F37, 0xFFFFFF, 0xFFDBCE, 0x6E3822, 0xBA1A1A, 0xFFFFFF, 0xFFDAD6, 0x93000A, 0xFFF8F7, 0x26181B, 0x574145, 0xFFFFFF, 0xFFF0F1, 0xFFE8EB, 0xFCE2E6, 0xF7DCE0, 0x8A7175, 0xDDBFC4, 0x3C2C2F, 0xFFECEE],
        [0xFFB1C2, 0x66002B, 0x8F003F, 0xFFD9DF, 0xF5B7B2, 0x4C2522, 0x663B37, 0xFFDAD6, 0xFFB599, 0x52220E, 0x6E3822, 0xFFDBCE, 0xFFB4AB, 0x690005, 0x93000A, 0xFFDAD6, 0x1D1012, 0xF7DCE0, 0xDDBFC4, 0x170B0D, 0x26181B, 0x2A1C1F, 0x352629, 0x413034, 0xA58A8F, 0x574145, 0xF7DCE0, 0x3C2C2F]);

    public static readonly TilePalette Forest = new("forest", "Forest", 0x2E7D32,
        [0x006E1B, 0xFFFFFF, 0x71FF75, 0x005312, 0x426650, 0xFFFFFF, 0xC4ECD0, 0x2A4E39, 0x256958, 0xFFFFFF, 0xACF0DA, 0x005141, 0xBA1A1A, 0xFFFFFF, 0xFFDAD6, 0x93000A, 0xF3FCEC, 0x161E14, 0x3F4A3C, 0xFFFFFF, 0xEDF6E7, 0xE8F1E1, 0xE2EBDB, 0xDCE5D6, 0x6F7A6B, 0xBECAB8, 0x2A3328, 0xEAF4E4],
        [0x00E543, 0x003909, 0x005312, 0x71FF75, 0xA8D0B5, 0x133724, 0x2A4E39, 0xC4ECD0, 0x91D4BE, 0x00382C, 0x005141, 0xACF0DA, 0xFFB4AB, 0x690005, 0x93000A, 0xFFDAD6, 0x0E150C, 0xDCE5D6, 0xBECAB8, 0x081008, 0x161E14, 0x1A2218, 0x242C22, 0x2F372C, 0x889484, 0x3F4A3C, 0xDCE5D6, 0x2A3328]);

    public static readonly TilePalette Sunset = new("sunset", "Sunset", 0xF4511E,
        [0xB02E00, 0xFFFFFF, 0xFFDBD1, 0x872100, 0x7F5539, 0xFFFFFF, 0xFFDCC7, 0x643E24, 0x80561F, 0xFFFFFF, 0xFFDDB9, 0x643E08, 0xBA1A1A, 0xFFFFFF, 0xFFDAD6, 0x93000A, 0xFFF8F6, 0x271814, 0x58423B, 0xFFFFFF, 0xFFF1ED, 0xFFE9E4, 0xFFE2DB, 0xF9DCD5, 0x8C716A, 0xE0C0B7, 0x3D2C28, 0xFFEDE8],
        [0xFFB5A0, 0x5F1500, 0x872100, 0xFFDBD1, 0xF2BB99, 0x492810, 0x643E24, 0xFFDCC7, 0xF4BC7C, 0x472A00, 0x643E08, 0xFFDDB9, 0xFFB4AB, 0x690005, 0x93000A, 0xFFDAD6, 0x1E100C, 0xF9DCD5, 0xE0C0B7, 0x180B07, 0x271814, 0x2B1C18, 0x372622, 0x42312C, 0xA78A83, 0x58423B, 0xF9DCD5, 0x3D2C28]);

    public static readonly TilePalette Lemon = new("lemon", "Lemon", 0xF9C80E,
        [0x745B00, 0xFFFFFF, 0xFFE08A, 0x574400, 0x675F30, 0xFFFFFF, 0xEFE3A9, 0x4E471B, 0x616123, 0xFFFFFF, 0xE7E79A, 0x49490C, 0xBA1A1A, 0xFFFFFF, 0xFFDAD6, 0x93000A, 0xFFF8F0, 0x201B0D, 0x4E4631, 0xFFFFFF, 0xFEF2DB, 0xF8EDD5, 0xF3E7D0, 0xEDE1CA, 0x80765F, 0xD2C5AA, 0x363020, 0xFBF0D8],
        [0xF1C100, 0x3D2F00, 0x574400, 0xFFE08A, 0xD2C78F, 0x373106, 0x4E471B, 0xEFE3A9, 0xCACB80, 0x323200, 0x49490C, 0xE7E79A, 0xFFB4AB, 0x690005, 0x93000A, 0xFFDAD6, 0x181306, 0xEDE1CA, 0xD2C5AA, 0x120E03, 0x201B0D, 0x241F11, 0x2F291A, 0x3A3424, 0x9A9077, 0x4E4631, 0xEDE1CA, 0x363020]);

    public static readonly TilePalette Lavender = new("lavender", "Lavender", 0x7E57C2,
        [0x7B00FD, 0xFFFFFF, 0xEBDCFF, 0x5D00C2, 0x6C5778, 0xFFFFFF, 0xF4DAFF, 0x533F5F, 0x79507A, 0xFFFFFF, 0xFFD6FD, 0x5F3961, 0xBA1A1A, 0xFFFFFF, 0xFFDAD6, 0x93000A, 0xFEF7FF, 0x1E1A24, 0x4A4453, 0xFFFFFF, 0xF9F0FF, 0xF4EBFA, 0xEEE5F4, 0xE8DFEE, 0x7B7484, 0xCCC3D5, 0x332E39, 0xF7EEFD],
        [0xD4BBFF, 0x41008B, 0x5D00C2, 0xEBDCFF, 0xD8BDE4, 0x3C2947, 0x533F5F, 0xF4DAFF, 0xE8B7E7, 0x46234A, 0x5F3961, 0xFFD6FD, 0xFFB4AB, 0x690005, 0x93000A, 0xFFDAD6, 0x15121C, 0xE8DFEE, 0xCCC3D5, 0x100C16, 0x1E1A24, 0x221E28, 0x2C2833, 0x37333E, 0x958E9E, 0x4A4453, 0xE8DFEE, 0x332E39]);

    public static readonly TilePalette Mint = new("mint", "Mint", 0x00A389,
        [0x006B59, 0xFFFFFF, 0x00FFD7, 0x005143, 0x386664, 0xFFFFFF, 0xBBECE8, 0x1F4E4C, 0x1B6871, 0xFFFFFF, 0xA9EEF8, 0x004F57, 0xBA1A1A, 0xFFFFFF, 0xFFDAD6, 0x93000A, 0xEEFDF6, 0x121E1A, 0x3A4A45, 0xFFFFFF, 0xE9F7F1, 0xE3F1EB, 0xDDEBE5, 0xD7E6E0, 0x697B75, 0xB8CBC4, 0x26332F, 0xE6F4EE],
        [0x00DFBD, 0x00382D, 0x005143, 0x00FFD7, 0xA0CFCC, 0x003735, 0x1F4E4C, 0xBBECE8, 0x8DD2DC, 0x00363C, 0x004F57, 0xA9EEF8, 0xFFB4AB, 0x690005, 0x93000A, 0xFFDAD6, 0x091612, 0xD7E6E0, 0xB8CBC4, 0x05100D, 0x121E1A, 0x16221E, 0x202C29, 0x2B3733, 0x83958E, 0x3A4A45, 0xD7E6E0, 0x26332F]);

    public static readonly TilePalette Racing = new("racing", "Racing", 0xE10600,
        [0xC00500, 0xFFFFFF, 0xFFDAD4, 0x930300, 0x80543D, 0xFFFFFF, 0xFFDBCB, 0x653D28, 0x835423, 0xFFFFFF, 0xFFDCBF, 0x673D0D, 0xBA1A1A, 0xFFFFFF, 0xFFDAD6, 0x93000A, 0xFFF8F6, 0x271815, 0x58413E, 0xFFFFFF, 0xFFF0EE, 0xFFE9E5, 0xFFE2DD, 0xF9DCD7, 0x8C716C, 0xDFBFBA, 0x3D2C29, 0xFFEDEA],
        [0xFFB4A8, 0x680200, 0x930300, 0xFFDAD4, 0xF4BA9E, 0x4B2714, 0x653D28, 0xFFDBCB, 0xF9BA80, 0x4B2800, 0x673D0D, 0xFFDCBF, 0xFFB4AB, 0x690005, 0x93000A, 0xFFDAD6, 0x1E100D, 0xF9DCD7, 0xDFBFBA, 0x180B09, 0x271815, 0x2B1C19, 0x372623, 0x42312E, 0xA78A85, 0x58413E, 0xF9DCD7, 0x3D2C29]);

    public static readonly TilePalette Sand = new("sand", "Sand", 0xB08D57,
        [0x7D570E, 0xFFFFFF, 0xFFDEAE, 0x604100, 0x6E5B40, 0xFFFFFF, 0xF9DFBB, 0x55442A, 0x4F6442, 0xFFFFFF, 0xD1EABE, 0x384C2C, 0xBA1A1A, 0xFFFFFF, 0xFFDAD6, 0x93000A, 0xFFF8F3, 0x201B13, 0x4F4539, 0xFFFFFF, 0xFEF2E5, 0xF8ECDF, 0xF2E6DA, 0xECE1D4, 0x817567, 0xD2C4B4, 0x362F27, 0xFBEFE2],
        [0xF1BE6D, 0x432C00, 0x604100, 0xFFDEAE, 0xDCC3A1, 0x3D2E16, 0x55442A, 0xF9DFBB, 0xB6CEA3, 0x223517, 0x384C2C, 0xD1EABE, 0xFFB4AB, 0x690005, 0x93000A, 0xFFDAD6, 0x18130B, 0xECE1D4, 0xD2C4B4, 0x120D07, 0x201B13, 0x241F17, 0x2F2921, 0x3A342B, 0x9B8F80, 0x4F4539, 0xECE1D4, 0x362F27]);

    public static readonly TilePalette Mono = new("mono", "Mono", 0x5F6368,
        [0x000000, 0xE2E2E2, 0x3B3B3B, 0xFFFFFF, 0x5E5E5E, 0xFFFFFF, 0xD4D4D4, 0x1B1B1B, 0x3B3B3B, 0xE2E2E2, 0x747474, 0xFFFFFF, 0xBA1A1A, 0xFFFFFF, 0xFFDAD6, 0x410002, 0xF9F9F9, 0x1B1B1B, 0x474747, 0xFFFFFF, 0xF3F3F3, 0xEEEEEE, 0xE8E8E8, 0xE2E2E2, 0x777777, 0xC6C6C6, 0x303030, 0xF1F1F1],
        [0xFFFFFF, 0x1B1B1B, 0xD4D4D4, 0x000000, 0xC6C6C6, 0x1B1B1B, 0x474747, 0xE2E2E2, 0xE2E2E2, 0x1B1B1B, 0x919191, 0x000000, 0xFFB4AB, 0x690005, 0x93000A, 0xFFDAD6, 0x131313, 0xE2E2E2, 0xC6C6C6, 0x0E0E0E, 0x1B1B1B, 0x1F1F1F, 0x2A2A2A, 0x353535, 0x919191, 0x474747, 0xE2E2E2, 0x303030]);

    public static readonly IReadOnlyList<TilePalette> All = [Accent, Ocean, Berry, Forest, Sunset, Lemon, Lavender, Mint, Racing, Sand, Mono];

    public static TilePalette? ById(string? id)
    {
        var key = id?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key)) return null;
        return All.FirstOrDefault(p => p.Id == key || p.Label.Equals(key, StringComparison.OrdinalIgnoreCase))
               ?? (key is "wallpaper" or "dynamic" or "material you" or "system" or "auto" or "windows" or "default" or "theme" ? Accent : null);
    }

    /// <summary>
    /// <paramref name="role"/> as 0xRRGGBB in dark or light mode. The accent palette is generated from
    /// <paramref name="accent"/> (0xRRGGBB): the panel's accent colour.
    /// </summary>
    public uint Color(TileRole role, bool dark, uint accent)
    {
        var table = dark ? _dark : _light;
        if (table is not null) return table[(int)role];
        return Generated(accent, dark)[(int)role];
    }

    private static readonly Dictionary<(uint, bool), uint[]> Schemes = [];

    /// <summary>The accent palette's scheme for a seed (cached: the UI asks for it on every draw).</summary>
    public static uint[] Generated(uint seed, bool dark)
    {
        lock (Schemes)
        {
            if (!Schemes.TryGetValue((seed, dark), out var scheme)) Schemes[(seed, dark)] = scheme = TonalScheme.Build(seed, dark);
            return scheme;
        }
    }

    public override string ToString() => Id;
}

/// <summary>
/// A Material 3 scheme from one seed colour, close to material-color-utilities' SchemeVibrant: tones are CIELAB
/// lightness (exactly HCT's tone), hue and chroma come from the seed in LCh, chroma is pulled in until the colour
/// fits sRGB. Primary keeps the seed's hue, secondary is a quieter version, tertiary turns 60°, the neutrals carry a
/// hint of the hue so surfaces feel related to the accent.
/// </summary>
internal static class TonalScheme
{
    public static uint[] Build(uint seed, bool dark)
    {
        var (_, c, h) = ToLch(seed);
        var primary = (H: h, C: Math.Max(c, 48));
        var secondary = (H: h, C: Math.Max(c * 0.33, 16));
        var tertiary = (H: (h + 60) % 360, C: Math.Max(c * 0.6, 32));
        var neutral = (H: h, C: Math.Min(c * 0.08, 6));
        var neutralVariant = (H: h, C: Math.Min(c * 0.16, 10));
        uint T((double H, double C) p, double tone) => FromLch(tone, p.C, p.H);

        uint[] errorLight = [0xBA1A1A, 0xFFFFFF, 0xFFDAD6, 0x93000A];
        uint[] errorDark = [0xFFB4AB, 0x690005, 0x93000A, 0xFFDAD6];
        var e = dark ? errorDark : errorLight;
        if (!dark)
        {
            return
            [
                T(primary, 40), T(primary, 100), T(primary, 90), T(primary, 30),
                T(secondary, 40), T(secondary, 100), T(secondary, 90), T(secondary, 30),
                T(tertiary, 40), T(tertiary, 100), T(tertiary, 90), T(tertiary, 30),
                e[0], e[1], e[2], e[3],
                T(neutral, 98), T(neutral, 10), T(neutralVariant, 30),
                T(neutral, 100), T(neutral, 96), T(neutral, 94), T(neutral, 92), T(neutral, 90),
                T(neutralVariant, 50), T(neutralVariant, 80), T(neutral, 20), T(neutral, 95),
            ];
        }
        return
        [
            T(primary, 80), T(primary, 20), T(primary, 30), T(primary, 90),
            T(secondary, 80), T(secondary, 20), T(secondary, 30), T(secondary, 90),
            T(tertiary, 80), T(tertiary, 20), T(tertiary, 30), T(tertiary, 90),
            e[0], e[1], e[2], e[3],
            T(neutral, 6), T(neutral, 90), T(neutralVariant, 80),
            T(neutral, 4), T(neutral, 10), T(neutral, 12), T(neutral, 17), T(neutral, 22),
            T(neutralVariant, 60), T(neutralVariant, 30), T(neutral, 90), T(neutral, 20),
        ];
    }

    // ── sRGB ⇄ CIELAB / LCh (D65) ──────────────────────────────────────────────────────

    public static (double L, double C, double H) ToLch(uint rgb)
    {
        var (l, a, b) = ToLab(rgb);
        var c = Math.Sqrt(a * a + b * b);
        var h = Math.Atan2(b, a) * 180 / Math.PI;
        if (h < 0) h += 360;
        return (l, c, h);
    }

    /// <summary>The colour at lightness <paramref name="l"/>, hue <paramref name="h"/>, with as much of chroma <paramref name="c"/> as sRGB holds.</summary>
    public static uint FromLch(double l, double c, double h)
    {
        if (l >= 100) return 0xFFFFFF;
        if (l <= 0) return 0x000000;
        var rad = h * Math.PI / 180;
        var lo = 0.0;
        var hi = c;
        // the full chroma first; otherwise bisect for the largest that fits
        if (TryLab(l, c * Math.Cos(rad), c * Math.Sin(rad), out var direct)) return direct;
        for (var i = 0; i < 18; i++)
        {
            var mid = (lo + hi) / 2;
            if (TryLab(l, mid * Math.Cos(rad), mid * Math.Sin(rad), out _)) lo = mid;
            else hi = mid;
        }
        TryLab(l, lo * Math.Cos(rad), lo * Math.Sin(rad), out var fit);
        return fit;
    }

    private static (double L, double A, double B) ToLab(uint rgb)
    {
        static double Lin(uint v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        var r = Lin((rgb >> 16) & 0xFF);
        var g = Lin((rgb >> 8) & 0xFF);
        var b = Lin(rgb & 0xFF);
        var x = (0.4124 * r + 0.3576 * g + 0.1805 * b) / 0.95047;
        var y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
        var z = (0.0193 * r + 0.1192 * g + 0.9505 * b) / 1.08883;
        static double F(double t) => t > 216.0 / 24389 ? Math.Cbrt(t) : (24389.0 / 27 * t + 16) / 116;
        var fx = F(x);
        var fy = F(y);
        var fz = F(z);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    private static bool TryLab(double l, double a, double b, out uint rgb)
    {
        var fy = (l + 16) / 116;
        var fx = fy + a / 500;
        var fz = fy - b / 200;
        static double Inv(double t) => t * t * t > 216.0 / 24389 ? t * t * t : (116 * t - 16) / (24389.0 / 27);
        var x = Inv(fx) * 0.95047;
        var y = l > 8 ? fy * fy * fy : l / (24389.0 / 27);
        var z = Inv(fz) * 1.08883;
        var rl = 3.2406 * x - 1.5372 * y - 0.4986 * z;
        var gl = -0.9689 * x + 1.8758 * y + 0.0415 * z;
        var bl = 0.0557 * x - 0.2040 * y + 1.0570 * z;
        const double slack = 0.0005;
        var inside = rl is >= -slack and <= 1 + slack && gl is >= -slack and <= 1 + slack && bl is >= -slack and <= 1 + slack;
        static uint Enc(double v)
        {
            v = Math.Clamp(v, 0, 1);
            var s = v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;
            return (uint)Math.Round(Math.Clamp(s, 0, 1) * 255);
        }
        rgb = (Enc(rl) << 16) | (Enc(gl) << 8) | Enc(bl);
        return inside;
    }
}
