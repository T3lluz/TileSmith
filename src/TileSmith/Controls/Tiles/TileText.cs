using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Windows.UI.Text;

namespace TileSmith.Controls.Tiles;

/// <summary>
/// Picks text sizes that fit. Every block knows its exact size before it draws, so a value is measured in the panel's
/// own font and set at the biggest step of the type scale that fits its room, in as many lines as it may take. Nothing
/// is cut off mid-glyph or shrunk to dust in a block that had room for a smaller size (DailyDash's TileText).
/// </summary>
public static class TileText
{
    /// <summary>The type scale, largest first (display, headline, title, body, label sizes) in px.</summary>
    public static readonly double[] Scale = [57, 45, 40, 36, 32, 28, 24, 22, 20, 18, 16, 15, 14, 13, 12, 11, 10, 9];

    /// <summary>The share of a width text is fitted into.</summary>
    public const double Safety = 0.94;

    public readonly record struct Fit(double Size, int Lines, bool Fits);

    private static readonly TextBlock Probe = new() { TextWrapping = TextWrapping.NoWrap };
    private static readonly Dictionary<(string, double, ushort, bool, double), Size> Cache = [];

    public static FontWeight Weight(int w) => new() { Weight = (ushort)w };
    public static readonly FontWeight Regular = FontWeights.Normal;
    public static readonly FontWeight Medium = FontWeights.Medium;
    public static readonly FontWeight Bold = FontWeights.SemiBold;

    /// <summary>Measures <paramref name="text"/> at <paramref name="size"/>, wrapped at <paramref name="width"/> (or not wrapped).</summary>
    public static Size Measure(string text, double size, FontWeight weight, double width = double.PositiveInfinity)
    {
        var wrap = !double.IsInfinity(width);
        var key = (text, size, weight.Weight, wrap, wrap ? Math.Round(width) : 0);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        Probe.Text = text;
        Probe.FontSize = size;
        Probe.FontWeight = weight;
        Probe.TextWrapping = wrap ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap;
        Probe.Measure(new Size(wrap ? width : double.PositiveInfinity, double.PositiveInfinity));
        var s = Probe.DesiredSize;
        if (Cache.Count > 4000) Cache.Clear();
        Cache[key] = s;
        return s;
    }

    public static double Width(string text, double size, FontWeight weight) => Measure(text, size, weight).Width;

    public static double LineHeight(double size, FontWeight weight) => Measure("Ag", size, weight).Height;

    /// <summary>
    /// The biggest size from <paramref name="max"/> down to <paramref name="min"/> at which <paramref name="text"/> fits
    /// <paramref name="width"/> × <paramref name="height"/> in at most <paramref name="maxLines"/> lines. When even the
    /// smallest overflows, <see cref="Fit.Fits"/> is false and the caller drops the text or lets it end in "…".
    /// </summary>
    public static Fit FitText(string text, double width, double height, double max, double min = 10, int maxLines = 1, FontWeight? weight = null)
    {
        var w = weight ?? Regular;
        if (width <= 4 || height <= 4 || text.Length == 0) return new Fit(min, 1, text.Length == 0);
        foreach (var size in Scale)
        {
            if (size > max || size < min) continue;
            var lh = LineHeight(size, w);
            var linesRoom = Math.Min((int)Math.Floor((height + 0.5) / lh), maxLines);
            if (linesRoom < 1) continue;
            var room = width * Safety;
            if (maxLines == 1)
            {
                if (Width(text, size, w) <= room) return new Fit(size, 1, true);
                continue;
            }
            var measured = Measure(text, size, w, room);
            var lines = Math.Max(1, (int)Math.Round(measured.Height / lh));
            if (lines <= linesRoom && measured.Width <= room + 0.5) return new Fit(size, lines, true);
        }
        var minLh = LineHeight(min, w);
        return new Fit(min, Math.Clamp((int)Math.Floor(height / minLh), 1, maxLines), false);
    }

    /// <summary>A text block set up for a tile: sized, coloured, trimmed with "…" when it must be.</summary>
    public static TextBlock Block(string text, double size, Color color, FontWeight? weight = null, int lines = 1, TextAlignment align = TextAlignment.Left)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontWeight = weight ?? Regular,
            Foreground = Theme.Palette.Current.Brush(color),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = lines > 1 ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap,
            MaxLines = Math.Max(1, lines),
            TextAlignment = align,
            IsHitTestVisible = false,
            TextLineBounds = TextLineBounds.Full,
            OpticalMarginAlignment = OpticalMarginAlignment.TrimSideBearings,
        };
        return block;
    }

    /// <summary>One line of small text sized to its room: <paramref name="size"/> or smaller, down to 9.5; left out when even that is too long and <paramref name="drop"/>.</summary>
    public static TextBlock? Label(string? text, double width, double size, Color color, FontWeight? weight = null, bool drop = true, TextAlignment align = TextAlignment.Left)
    {
        if (string.IsNullOrWhiteSpace(text) || width < 12) return null;
        var w = weight ?? Medium;
        var room = width * Safety;
        var s = size;
        while (s > 9.5 && Width(text, s, w) > room) s -= 0.5;
        if (drop && Width(text, s, w) > room) return null;
        var block = Block(text, s, color, w, 1, align);
        block.Width = width;
        return block;
    }
}
