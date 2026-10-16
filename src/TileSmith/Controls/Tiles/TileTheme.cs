using Microsoft.UI.Xaml.Media;
using TileSmith.Core.Tiles;
using TileSmith.Theme;
using Windows.UI;

namespace TileSmith.Controls.Tiles;

/// <summary>The roles one block paints with. <see cref="Bg"/> null: it sits straight on the widget.</summary>
public sealed record TileLook(TileRole? Bg, TileRole Content, TileRole Variant, TileRole Accent, TileRole Track, TileRole ShapeFill, TileRole OnShape);

/// <summary>
/// A design's colours on this panel: its palette's Material 3 roles in the panel's light or dark mode (the accent
/// palette grows from TileSmith's accent colour). On Liquid Glass the widget's own surface lets the blurred wallpaper
/// through, so tiles read as tinted glass; what sits on them stays opaque enough to read. The same rules as
/// DailyDash's Tilesmith: a tile's container, what sits on it, its accent and its track.
/// </summary>
public sealed class TileTheme
{
    private readonly uint _seed;

    public TileTheme(TilePalette palette, TileSurface surface, bool dark, uint seed, bool glass)
    {
        Palette = palette;
        Surface = surface;
        IsDark = dark;
        IsGlass = glass;
        _seed = seed;
        Bg = surface switch
        {
            TileSurface.Soft => TileRole.SurfaceContainerLow,
            TileSurface.Tonal => TileRole.SurfaceContainer,
            TileSurface.Tinted => TileRole.PrimaryContainer,
            _ => TileRole.Primary,
        };
        OnBg = surface switch
        {
            TileSurface.Tinted => TileRole.OnPrimaryContainer,
            TileSurface.Vivid => TileRole.OnPrimary,
            _ => TileRole.OnSurface,
        };
        OnBgVariant = surface switch
        {
            TileSurface.Tinted => TileRole.OnPrimaryContainer,
            TileSurface.Vivid => TileRole.OnPrimary,
            _ => TileRole.OnSurfaceVariant,
        };
        NeutralTile = surface switch
        {
            TileSurface.Soft => TileRole.SurfaceContainerHigh,
            TileSurface.Tonal => TileRole.SurfaceContainerHighest,
            _ => TileRole.SurfaceContainerLowest,
        };
    }

    /// <summary>The theme a design gets on the panel right now.</summary>
    public static TileTheme For(TileDesign d, string accentSetting) =>
        new(d.Palette, d.Surface, Theme.Palette.Current.IsDark, Theme.Palette.SeedFor(accentSetting), Theme.Palette.Current.IsGlass);

    public TilePalette Palette { get; }
    public TileSurface Surface { get; }
    public bool IsDark { get; }
    public bool IsGlass { get; }

    public TileRole Bg { get; }
    public TileRole OnBg { get; }
    public TileRole OnBgVariant { get; }

    /// <summary>The quiet tile for data (rings, lists, charts): a step up from the widget's own surface.</summary>
    public TileRole NeutralTile { get; }

    public Color C(TileRole role, double alpha = 1) => Theme.Palette.FromRgb(Palette.Color(role, IsDark, _seed), alpha);

    public SolidColorBrush B(TileRole role, double alpha = 1) => Theme.Palette.Current.Brush(C(role, alpha));

    /// <summary>The widget's own background: see-through on glass.</summary>
    public Color Background => C(Bg, IsGlass ? (Surface is TileSurface.Soft or TileSurface.Tonal ? (IsDark ? 0.62 : 0.66) : 0.82) : 1);

    /// <summary>A block's container colour: a little see-through on glass so the tile reads as one sheet.</summary>
    public Color Container(TileRole role) => C(role, IsGlass ? 0.86 : 1);

    public TileRole ContainerOf(TileTone t)
    {
        var r = t switch
        {
            TileTone.Primary => TileRole.PrimaryContainer,
            TileTone.Secondary => TileRole.SecondaryContainer,
            TileTone.Tertiary => TileRole.TertiaryContainer,
            TileTone.Error => TileRole.ErrorContainer,
            _ => NeutralTile,
        };
        return r == Bg ? NeutralTile : r;
    }

    public TileRole OnContainer(TileTone t) => ContainerOf(t) switch
    {
        TileRole.PrimaryContainer => TileRole.OnPrimaryContainer,
        TileRole.SecondaryContainer => TileRole.OnSecondaryContainer,
        TileRole.TertiaryContainer => TileRole.OnTertiaryContainer,
        TileRole.ErrorContainer => TileRole.OnErrorContainer,
        _ => TileRole.OnSurface,
    };

    public TileRole AccentOf(TileTone t)
    {
        var r = t switch
        {
            TileTone.Primary => TileRole.Primary,
            TileTone.Secondary => TileRole.Secondary,
            TileTone.Tertiary => TileRole.Tertiary,
            TileTone.Error => TileRole.Error,
            _ => TileRole.OnSurface,
        };
        return r == Bg ? TileRole.OnPrimary : r;
    }

    public TileRole OnAccent(TileTone t) => AccentOf(t) switch
    {
        TileRole.Primary => TileRole.OnPrimary,
        TileRole.Secondary => TileRole.OnSecondary,
        TileRole.Tertiary => TileRole.OnTertiary,
        TileRole.Error => TileRole.OnError,
        TileRole.OnPrimary => TileRole.Primary,
        _ => TileRole.Surface,
    };

    private static readonly HashSet<BlockType> DataTypes =
    [
        BlockType.Ring, BlockType.Bar, BlockType.Chart, BlockType.List, BlockType.Text, BlockType.Ai,
        BlockType.Image, BlockType.Carousel, BlockType.Media, BlockType.Slider, BlockType.Choice,
    ];

    /// <summary>A shape on the widget's own background: its container, or the accent where the container would vanish into a coloured surface.</summary>
    private (TileRole Fill, TileRole On) ShapeOnBg(TileTone t) =>
        ContainerOf(t) == NeutralTile && Surface is not (TileSurface.Soft or TileSurface.Tonal) ? (AccentOf(t), OnAccent(t)) : (ContainerOf(t), OnContainer(t));

    public TileLook Look(TileBlock b)
    {
        var t = b.Tone;
        var (shapeFill, onShape) = ShapeOnBg(t);
        return b.Style switch
        {
            BlockStyle.Tile when DataTypes.Contains(b.Type) =>
                new TileLook(NeutralTile, TileRole.OnSurface, TileRole.OnSurfaceVariant, AccentOf(t), Distinct(ContainerOf(t), NeutralTile), ContainerOf(t), OnContainer(t)),
            BlockStyle.Tile =>
                new TileLook(ContainerOf(t), OnContainer(t), OnContainer(t), AccentOf(t), Distinct(NeutralTile, ContainerOf(t)), AccentOf(t), OnAccent(t)),
            BlockStyle.Plain =>
                new TileLook(null, OnBg, OnBgVariant, Surface == TileSurface.Vivid ? OnBg : AccentOf(t), Distinct(ContainerOf(t), Bg), shapeFill, onShape),
            BlockStyle.Filled =>
                new TileLook(AccentOf(t), OnAccent(t), OnAccent(t), OnAccent(t), ContainerOf(t), ContainerOf(t), OnContainer(t)),
            _ => new TileLook(null, OnBg, OnBgVariant, AccentOf(t), Distinct(ContainerOf(t), Bg), shapeFill, onShape),
        };
    }

    private static TileRole Distinct(TileRole r, TileRole behind) => r == behind ? TileRole.OutlineVariant : r;
}
