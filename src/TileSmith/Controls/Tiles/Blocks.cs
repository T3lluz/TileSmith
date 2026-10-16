using System.Globalization;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TileSmith.Core.Actions;
using TileSmith.Core.Tiles;
using TileSmith.Theme;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI.Text;

namespace TileSmith.Controls.Tiles;

/// <summary>What clicking inside a widget does. The panel runs it for real; the gallery's previews have none.</summary>
public interface ITileActions
{
    Task SwitchAsync(TileBlock block, bool on);
    Task LevelAsync(TileBlock block, double value, bool final);
    Task ChooseAsync(TileBlock block, TileOption option);
    /// <summary>A button's or a block's tap (<paramref name="tap"/>), asking first when the block says so.</summary>
    Task TapAsync(TileBlock? block, string? tap, TileValue? value, FrameworkElement anchor);
    Task OpenLinkAsync(string link);
    Task MediaAsync(TileBlock block, string command);
}

/// <summary>Everything one block needs to draw itself at its size.</summary>
public sealed record BlockContext(TileDesign Design, TileBlock Block, int Index, TileTheme Theme, TileLook Look, double Width, double Height, double Radius, bool Solo, ITileActions? Actions);

/// <summary>
/// One block, drawn at its exact size from what its source read. Read-only blocks redraw themselves when their value
/// changes; controls (toggles, sliders, choices) update in place so a drag or a click is never interrupted.
/// </summary>
public abstract class BlockView : Grid
{
    protected BlockView(BlockContext ctx)
    {
        Ctx = ctx;
        Width = Math.Max(1, ctx.Width);
        Height = Math.Max(1, ctx.Height);
        IsHitTestVisible = BlockTypes.IsInteractive(ctx.Block.Type) || ctx.Block.Tap is not null || ctx.Block.Type is BlockType.List or BlockType.Carousel;
    }

    protected BlockContext Ctx { get; }
    protected TileBlock B => Ctx.Block;
    protected TileTheme T => Ctx.Theme;
    protected TileLook L => Ctx.Look;
    protected double W => Width;
    protected double H => Height;

    protected Color C(TileRole role, double alpha = 1) => T.C(role, alpha);

    private string? _shown;

    /// <summary>Shows <paramref name="v"/> (null: nothing read yet).</summary>
    public virtual void Update(TileValue? v)
    {
        var key = v is null ? "" : TileValueCodec.ToJson(v with { ReadAt = 0 }).ToJsonString();
        if (key == _shown && Children.Count > 0) return;
        _shown = key;
        Children.Clear();
        Draw(v);
    }

    protected abstract void Draw(TileValue? v);

    // ── Shared pieces ────────────────────────────────────────────────────────────────

    protected TileSource? Source => TileSources.ByKey(B.Bind);

    /// <summary>The block's own label, else its source's name (text, AI and list blocks speak for themselves).</summary>
    protected string? LabelOf() => B.Label ?? (B.Type is BlockType.Text or BlockType.Ai or BlockType.List ? null : Source?.Label);

    protected string? IconOf(TileValue? v) => FirstIcon(B.Icon, v?.Icon, Source?.Icon);

    protected static string? FirstIcon(params string?[] names) => names.FirstOrDefault(n => n is not null && Icons.IsKnown(n));

    /// <summary>"7,412" and "steps", or "14°" with no unit since the text carries it.</summary>
    protected static (string Main, string? Unit) ValueParts(TileValue? v)
    {
        if (v is null) return ("—", null);
        var main = v.Display ?? v.Note ?? "—";
        string? unit = v.Text is null && v.Unit is { Length: > 0 and <= 6 } u ? u : null;
        return (main, unit);
    }

    protected static string Joined(string main, string? unit) => main + (unit is null ? "" : unit is "%" or "°" ? unit : " " + unit);

    protected TextBlock Text(string text, double size, TileRole role, FontWeight? weight = null, int lines = 1, TextAlignment align = TextAlignment.Left) =>
        TileText.Block(text, size, C(role), weight, lines, align);

    protected static StackPanel Column(double? width = null, VerticalAlignment v = VerticalAlignment.Center, HorizontalAlignment h = HorizontalAlignment.Stretch)
    {
        var s = new StackPanel { VerticalAlignment = v, HorizontalAlignment = h };
        if (width is { } w) s.Width = w;
        return s;
    }

    protected TextBlock Glyph(string? icon, double size, TileRole role) => Controls.Glyph.Make(icon, size, C(role));

    /// <summary>A shape with an icon (or a few characters) on it.</summary>
    protected Grid ShapeWith(TileShape shape, double s, TileRole fill, TileRole on, string? icon, string? text)
    {
        var g = new Grid { Width = s, Height = s, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(TileDrawing.Shape(shape, s, C(fill)));
        if (text is not null)
        {
            var fit = TileText.FitText(text, s * 0.66, s * 0.42, 45, 10, 1, TileText.Bold);
            var t = Text(text, fit.Size, on, TileText.Bold, 1, TextAlignment.Center);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            g.Children.Add(t);
        }
        else if (icon is not null)
        {
            g.Children.Add(Glyph(icon, s * 0.44, on));
        }
        return g;
    }

    /// <summary>The number (and its unit), then its label and sub line, sized to the room.</summary>
    protected StackPanel ValueStack(string main, string? unit, string? label, string? sub, double w, double h, double maxValue, bool bottom = false, TileRole? valueRole = null)
    {
        const double small = 12;
        var smallH = TileText.LineHeight(small, TileText.Medium);
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(label)) lines.Add(label!);
        if (!string.IsNullOrWhiteSpace(sub) && sub != label) lines.Add(sub!);
        // keep at least half the height for the number
        while (lines.Count > 0 && h - lines.Count * smallH < h * 0.5) lines.RemoveAt(lines.Count - 1);
        var valueH = h - lines.Count * smallH;
        var full = Joined(main, unit);
        var fit = TileText.FitText(full, w, valueH, maxValue, 12, 1, TileText.Bold);
        var shown = fit.Fits ? full : main;
        if (!fit.Fits) fit = TileText.FitText(main, w, valueH, maxValue, 11, 1, TileText.Bold);
        var stack = Column(w, bottom ? VerticalAlignment.Bottom : VerticalAlignment.Center);
        var value = Text(shown, fit.Size, valueRole ?? L.Content, TileText.Bold);
        value.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        value.LineHeight = Math.Ceiling(fit.Size * 1.18);
        stack.Children.Add(value);
        foreach (var line in lines)
        {
            if (TileText.Label(line, w, small, C(L.Variant)) is { } l) stack.Children.Add(l);
        }
        return stack;
    }

    /// <summary>What a block shows instead of its value when its source needs something or failed.</summary>
    protected void Note(string icon, string note)
    {
        var iconSize = Math.Clamp(Math.Min(W, H) * 0.28, 14, 22);
        var compact = H < 56;
        var textW = compact ? W - iconSize - 6 : W;
        var textH = compact ? H : H - iconSize - 4;
        var fit = TileText.FitText(note, textW, textH, 15, 9, 3, TileText.Medium);
        var text = Text(note, fit.Size, L.Content, TileText.Medium, fit.Lines);
        text.Width = Math.Max(1, textW);
        var glyph = Glyph(icon, iconSize, L.Variant);
        if (compact)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(glyph);
            text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(text);
            Children.Add(row);
        }
        else
        {
            var col = Column(null, VerticalAlignment.Center);
            glyph.HorizontalAlignment = HorizontalAlignment.Left;
            col.Children.Add(glyph);
            col.Spacing = 4;
            col.Children.Add(text);
            Children.Add(col);
        }
    }

    /// <summary>Draws the note for a value that can't show (true), or nothing (false).</summary>
    protected bool DrawState(TileValue? v)
    {
        if (B.Bind is null || v is null) return false;
        switch (v.State)
        {
            case TileValueState.Needs:
                Note("key", v.Note ?? "Needs setting up");
                return true;
            case TileValueState.Error when v.Display is null:
                Note("warning", v.Note ?? "Couldn't read this");
                return true;
            case TileValueState.Empty when B.Type is not (BlockType.List or BlockType.Carousel or BlockType.Media):
                Note("info", v.Note ?? "Nothing here");
                return true;
        }
        return false;
    }

    public static BlockView Create(BlockContext ctx) => ctx.Block.Type switch
    {
        BlockType.Metric => new MetricBlock(ctx),
        BlockType.Ring => new RingBlock(ctx),
        BlockType.Bar => new BarBlock(ctx),
        BlockType.Chart => new ChartBlock(ctx),
        BlockType.List => new ListBlock(ctx),
        BlockType.Text or BlockType.Ai => new TextBlockView(ctx),
        BlockType.Badge => new BadgeBlock(ctx),
        BlockType.Clock or BlockType.Date => new ClockBlock(ctx),
        BlockType.Countdown => new CountdownBlock(ctx),
        BlockType.Button => new ButtonBlock(ctx),
        BlockType.Image => new ImageBlock(ctx),
        BlockType.Carousel => new CarouselBlock(ctx),
        BlockType.Media => new MediaBlock(ctx),
        BlockType.Toggle => new ToggleBlock(ctx),
        BlockType.Slider => new SliderBlock(ctx),
        BlockType.Choice => new ChoiceBlock(ctx),
        _ => new TextBlockView(ctx),
    };
}

// ── Metric ─────────────────────────────────────────────────────────────────────────

internal sealed class MetricBlock(BlockContext ctx) : BlockView(ctx)
{
    protected override void Draw(TileValue? v)
    {
        if (DrawState(v)) return;
        var (main, unit) = ValueParts(v);
        var label = LabelOf();
        var sub = v?.Sub is { Length: > 0 } s && s != label ? s : null;
        var icon = IconOf(v);
        var shape = B.Style == BlockStyle.Shape ? B.Shape ?? TileShape.Cookie9 : null;

        // wide and short: the icon (on its shape) on the left, the number and its label beside it
        if (W > H * 2.1 && H < 92)
        {
            var badge = Math.Min(H * 0.86, 64);
            var showBadge = icon is not null && W - badge > 70;
            var textW = showBadge ? W - badge - 10 : W;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            if (showBadge) row.Children.Add(ShapeWith(shape ?? TileShape.Cookie9, badge, L.ShapeFill, L.OnShape, icon, null));
            row.Children.Add(ValueStack(main, unit, label, sub, textW, H, 45));
            Children.Add(row);
            return;
        }
        // a shape behind the number, the label under
        if (shape is not null)
        {
            var labelH = label is not null && H > 90 ? TileText.LineHeight(12, TileText.Medium) + 2 : 0;
            var size = Math.Min(W, H - labelH);
            var col = Column(null, VerticalAlignment.Center, HorizontalAlignment.Center);
            col.Children.Add(ShapeWith(shape, size, L.ShapeFill, L.OnShape, null, Joined(main, unit is "%" or "°" ? unit : null)));
            if (labelH > 0 && TileText.Label(label, W, 12, C(L.Variant), align: TextAlignment.Center) is { } l) col.Children.Add(l);
            Children.Add(col);
            return;
        }
        // tall: icon and label on top, the number big, its line at the bottom
        var headH = (label is not null || icon is not null) && H >= 64 ? Math.Max(TileText.LineHeight(12, TileText.Medium), 18) : 0;
        var stack = new Grid();
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headH) });
        stack.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        if (headH > 0)
        {
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            if (icon is not null) head.Children.Add(Glyph(icon, 16, L.Bg is null ? L.Accent : L.Content));
            if (TileText.Label(label, W - (icon is null ? 0 : 21), 12, C(L.Variant)) is { } l) head.Children.Add(l);
            stack.Children.Add(head);
        }
        var value = ValueStack(main, unit, headH > 0 ? null : label, sub, W, H - headH, 57, bottom: headH > 0);
        SetRow(value, 1);
        stack.Children.Add(value);
        Children.Add(stack);
    }
}

// ── Ring ───────────────────────────────────────────────────────────────────────────

internal sealed class RingBlock(BlockContext ctx) : BlockView(ctx)
{
    protected override void Draw(TileValue? v)
    {
        if (DrawState(v)) return;
        var fraction = v?.Fraction(B.Goal) ?? 0;
        var (main, unit) = ValueParts(v);
        var label = LabelOf();
        var wide = W > H * 1.75;
        var d = wide ? H : Math.Min(W, H - (label is not null && H - W > 16 ? 16 : 0));
        var ring = new Grid { Width = d, Height = d, VerticalAlignment = VerticalAlignment.Center };
        ring.Children.Add(TileDrawing.Ring(d, fraction, C(L.Accent), C(L.Track)));
        var hole = d - 2 * TileDrawing.RingStroke(d) - 8;
        var centreIcon = wide ? IconOf(v) : null;
        if (centreIcon is not null)
        {
            ring.Children.Add(Glyph(centreIcon, d * 0.32, L.Accent));
        }
        else
        {
            var inUnit = unit is "%" or "°" ? unit : null;
            var txt = main + (inUnit ?? "");
            var fit = TileText.FitText(txt, hole, d * 0.38, 32, 9, 1, TileText.Bold);
            var t = Text(txt, fit.Size, L.Content, TileText.Bold, 1, TextAlignment.Center);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            ring.Children.Add(t);
        }
        if (wide)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(ring);
            row.Children.Add(ValueStack(main, unit, label, v?.Sub, W - d - 10, H, 40));
            Children.Add(row);
            return;
        }
        var col = Column(null, VerticalAlignment.Center, HorizontalAlignment.Center);
        col.Children.Add(ring);
        if (label is not null && H - d >= 14 && TileText.Label(label, W, 11.5, C(L.Variant), align: TextAlignment.Center) is { } l)
        {
            l.Margin = new Thickness(0, 2, 0, 0);
            col.Children.Add(l);
        }
        Children.Add(col);
    }
}

// ── Bar ────────────────────────────────────────────────────────────────────────────

internal sealed class BarBlock(BlockContext ctx) : BlockView(ctx)
{
    protected override void Draw(TileValue? v)
    {
        if (DrawState(v)) return;
        var fraction = v?.Fraction(B.Goal) ?? 0;
        var (main, unit) = ValueParts(v);
        var label = LabelOf() ?? "";
        var headSize = H > 60 ? 14 : 12.5;
        var headH = TileText.LineHeight(headSize, TileText.Medium);
        var barH = Math.Clamp(H - headH - 4, 8, 16);
        var subH = TileText.LineHeight(11, TileText.Regular);
        var sub = H >= headH + barH + subH + 8 ? v?.Sub : null;
        var valueText = Joined(main, unit);
        var valueW = Math.Min(TileText.Width(valueText, headSize, TileText.Bold), W * 0.55) + 2;
        var col = Column(W, VerticalAlignment.Center);
        var head = new Grid { Height = headH };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(valueW) });
        if (TileText.Label(label, W - valueW, headSize, C(L.Variant), drop: false) is { } l) head.Children.Add(l);
        var value = Text(valueText, headSize, L.Content, TileText.Bold, 1, TextAlignment.Right);
        SetColumn(value, 1);
        head.Children.Add(value);
        col.Children.Add(head);
        var bar = TileDrawing.WavyBar(W, barH, fraction, C(L.Accent), C(L.Track), wavy: barH >= 10);
        bar.Margin = new Thickness(0, 4, 0, 0);
        col.Children.Add(bar);
        if (sub is not null && TileText.Label(sub, W, 11, C(L.Variant), TileText.Regular) is { } s) col.Children.Add(s);
        Children.Add(col);
    }
}

// ── Chart ──────────────────────────────────────────────────────────────────────────

internal sealed class ChartBlock(BlockContext ctx) : BlockView(ctx)
{
    protected override void Draw(TileValue? v)
    {
        if (DrawState(v)) return;
        var series = v?.Series ?? [];
        if (B.Max is { } max && series.Count > max) series = series.TakeLast(Math.Max(2, max)).ToList();
        var label = LabelOf();
        var (main, unit) = ValueParts(v);
        var headSize = H > 90 ? 14 : 12;
        var headH = H >= 64 ? TileText.LineHeight(headSize, TileText.Medium) + 4 : 0;
        var footH = H >= 96 && (v?.SeriesStart is not null || v?.SeriesEnd is not null) ? TileText.LineHeight(10, TileText.Regular) + 2 : 0;
        var chartH = Math.Max(8, H - headH - footH);
        var line = B.Variant == "line";
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(headH) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(chartH) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(footH) });
        if (headH > 0)
        {
            var valueText = v?.Display is not null ? Joined(main, unit) : "";
            var valueW = valueText.Length == 0 ? 0 : Math.Min(TileText.Width(valueText, headSize, TileText.Bold), W * 0.6) + 2;
            var head = new Grid { VerticalAlignment = VerticalAlignment.Top };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(valueW) });
            if (TileText.Label(label ?? " ", W - valueW, headSize, C(L.Variant), drop: false) is { } l) head.Children.Add(l);
            if (valueW > 0)
            {
                var value = Text(valueText, headSize, L.Content, TileText.Bold, 1, TextAlignment.Right);
                SetColumn(value, 1);
                head.Children.Add(value);
            }
            grid.Children.Add(head);
        }
        UIElement chart = series.Count >= 2
            ? TileDrawing.Chart(W, chartH, series, line, C(L.Accent), C(L.Track))
            : Text(v?.Note ?? "Collecting…", 11, L.Variant);
        if (chart is FrameworkElement fe && series.Count < 2) fe.VerticalAlignment = VerticalAlignment.Center;
        SetRow((FrameworkElement)chart, 1);
        grid.Children.Add(chart);
        if (footH > 0)
        {
            var foot = new Grid();
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            foot.ColumnDefinitions.Add(new ColumnDefinition());
            foot.Children.Add(Text(v?.SeriesStart ?? "", 10, L.Variant));
            var end = Text(v?.SeriesEnd ?? "", 10, L.Variant, null, 1, TextAlignment.Right);
            SetColumn(end, 1);
            foot.Children.Add(end);
            SetRow(foot, 2);
            grid.Children.Add(foot);
        }
        Children.Add(grid);
    }
}

// ── List ───────────────────────────────────────────────────────────────────────────

internal sealed class ListBlock(BlockContext ctx) : BlockView(ctx)
{
    private bool WidePictures => B.Bind?.Trim().ToLowerInvariant() is "web.feed" or "web.json" or "ai.list";

    protected override void Draw(TileValue? v)
    {
        if (DrawState(v)) return;
        var scroll = B.Variant == "scroll";
        var items = (v?.Items ?? []).ToList();
        if (scroll) items = items.Take(B.Max ?? 30).ToList();
        var label = B.Label ?? (H >= 120 ? Source?.Label : null);
        const double headSize = 12;
        var headH = label is not null && H >= 70 ? TileText.LineHeight(headSize, TileText.Medium) + 4 : 0;
        var titleSize = W > 200 ? 13.5 : 12.5;
        var titleH = TileText.LineHeight(titleSize, TileText.Medium);
        var subH = TileText.LineHeight(11, TileText.Regular);
        const double gap = 4;
        var pictures = B.Variant != "plain" && (B.Variant == "thumbs" || items.Any(i => i.Image is not null)) && W >= 150;
        var icons = !pictures && items.Any(i => i.Icon is not null);
        var withSub = items.Any(i => i.Sub is not null) && (scroll || pictures || (H - headH) / (titleH + subH + gap) >= Math.Min(items.Count, 2));
        var textH = titleH + (withSub ? subH : 0);
        var rowH = pictures ? Math.Max(textH, WidePictures ? 40 : 34) : textH;
        var picW = pictures && WidePictures ? rowH * 16 / 9 : rowH;
        var bodyH = H - headH;
        var col = Column(W, VerticalAlignment.Top);
        if (headH > 0 && TileText.Label(label, W, headSize, C(L.Variant), drop: false) is { } l)
        {
            l.Margin = new Thickness(0, 0, 0, 4);
            col.Children.Add(l);
        }
        if (items.Count == 0)
        {
            col.Children.Add(Text(v?.Note ?? (v is null ? "…" : "Nothing here"), titleSize, L.Variant, null, 2));
            Children.Add(col);
            return;
        }
        var rows = new StackPanel { Spacing = gap };
        var fitRows = scroll ? items.Count : Math.Max(0, (int)Math.Floor((bodyH + gap) / (rowH + gap)));
        foreach (var item in items.Take(scroll ? items.Count : Math.Min(fitRows, Math.Min(B.Max ?? 8, 8))))
            rows.Children.Add(Row(item, rowH, titleSize, withSub, pictures ? picW : 0, icons));
        if (scroll)
        {
            var viewer = new ScrollViewer
            {
                Content = rows,
                Height = Math.Max(10, bodyH),
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            col.Children.Add(viewer);
        }
        else
        {
            col.Children.Add(rows);
        }
        Children.Add(col);
    }

    private FrameworkElement Row(TileItem item, double rowH, double titleSize, bool withSub, double picW, bool icons)
    {
        var mark = picW <= 0 && !icons && item.Color is { } hex && TryColor(hex, out var c) ? c : (Color?)null;
        var trailW = item.Trailing is { } trailText ? Math.Min(TileText.Width(trailText, 12, TileText.Bold), W * 0.42) + 6 : 0;
        var grid = new Grid { Height = rowH, Width = W, ColumnSpacing = 0, Background = new SolidColorBrush(Colors.Transparent) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(trailW) });
        if (mark is { } m)
        {
            grid.Children.Add(new Border { Width = 4, Height = Math.Max(6, rowH - 4), CornerRadius = new CornerRadius(2), Background = Palette.Current.Brush(m), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        }
        else if (picW > 0)
        {
            var pic = TileDrawing.Picture(item.Image, picW, rowH, 8, C(L.Track), C(L.Variant), Source?.Icon ?? "image");
            pic.Margin = new Thickness(0, 0, 8, 0);
            grid.Children.Add(pic);
        }
        else if (icons)
        {
            var g = Glyph(item.Icon ?? "circle", Math.Min(rowH, 20), L.Accent);
            g.Margin = new Thickness(0, 0, 8, 0);
            g.Width = Math.Min(rowH, 20) + 2;
            grid.Children.Add(g);
        }
        var texts = Column(null, VerticalAlignment.Center);
        texts.Children.Add(Text(item.Title, titleSize, L.Content, TileText.Medium));
        if (withSub) texts.Children.Add(Text(item.Sub ?? "", 11, L.Variant));
        SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (item.Trailing is not null)
        {
            var trailing = Text(item.Trailing, 12, L.Bg is not null ? L.Accent : L.Content, TileText.Bold, 1, TextAlignment.Right);
            trailing.VerticalAlignment = VerticalAlignment.Center;
            SetColumn(trailing, 2);
            grid.Children.Add(trailing);
        }
        if (item.Link is { } link && Ctx.Actions is { } actions)
        {
            var press = new Pressable { Fill = Colors.Transparent, CornerRadius = new CornerRadius(8) };
            press.SetContent(grid);
            press.AccessibleName = item.Title;
            ToolTipService.SetToolTip(press, item.Title + (item.Sub is null ? "" : "\n" + item.Sub));
            press.Click += (_, _) => _ = actions.OpenLinkAsync(link);
            return press;
        }
        ToolTipService.SetToolTip(grid, item.Title);
        return grid;
    }

    internal static bool TryColor(string hex, out Color color)
    {
        color = default;
        try
        {
            if (!hex.StartsWith('#') || hex.Length is not (7 or 9)) return false;
            color = Palette.Parse(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

// ── Text and AI lines ──────────────────────────────────────────────────────────────

internal sealed class TextBlockView(BlockContext ctx) : BlockView(ctx)
{
    protected override void Draw(TileValue? v)
    {
        var ai = B.Type == BlockType.Ai;
        if (B.Bind is not null && DrawState(v)) return;
        var words = (B.Bind is not null && (B.Bind != "ai.text" || ai) ? v?.Display ?? v?.Note : null) ?? B.Text ?? v?.Display ?? (ai ? "Thinking…" : "");
        var label = B.Label;
        var labelH = label is not null && H >= 50 ? TileText.LineHeight(12, TileText.Medium) + 2 : 0;
        var iconW = ai && W > 110 ? 20 : 0;
        var bodyW = W - iconW;
        var bodyH = H - labelH;
        var headline = !ai && words.Length <= 28;
        var fit = TileText.FitText(words, bodyW, bodyH, headline ? 36 : 18, 10, 6, headline ? TileText.Bold : TileText.Regular);
        var col = Column(W, VerticalAlignment.Center);
        if (labelH > 0 && TileText.Label(label, W, 12, C(L.Variant)) is { } l)
        {
            l.Margin = new Thickness(0, 0, 0, 2);
            col.Children.Add(l);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (iconW > 0)
        {
            var g = Glyph(FirstIcon(v?.Icon, "auto_awesome"), 14, L.Accent);
            g.VerticalAlignment = VerticalAlignment.Top;
            g.Margin = new Thickness(0, 3, 0, 0);
            row.Children.Add(g);
        }
        var text = Text(words, fit.Size, L.Content, headline ? TileText.Bold : TileText.Regular, Math.Max(1, fit.Lines));
        text.Width = Math.Max(1, bodyW - (iconW > 0 ? 6 : 0));
        if (!fit.Fits) ToolTipService.SetToolTip(text, words);
        row.Children.Add(text);
        col.Children.Add(row);
        Children.Add(col);
    }
}

// ── Badge ──────────────────────────────────────────────────────────────────────────

internal sealed class BadgeBlock(BlockContext ctx) : BlockView(ctx)
{
    protected override void Draw(TileValue? v)
    {
        var shape = B.Shape ?? TileShape.Cookie9;
        var label = B.Label ?? v?.Sub;
        var icon = IconOf(v);
        var wide = W > H * 1.8 && (B.Bind is not null || B.Label is not null);
        var labelH = !wide && label is not null && H - W > 12 ? TileText.LineHeight(12, TileText.Medium) + 2 : 0;
        var s = wide ? H : Math.Min(W, H - labelH);
        var fill = B.Style == BlockStyle.Filled ? L.Accent : L.ShapeFill;
        var on = B.Style == BlockStyle.Filled ? L.Content : L.OnShape;
        var shortValue = icon is null && v?.Display is { Length: <= 5 } d ? d : null;
        var badge = ShapeWith(shape, s, fill, on, shortValue is null ? icon : null, shortValue);
        if (shortValue is null && icon is null) badge.Children.Add(Glyph("star", s * 0.44, on));
        if (wide)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(badge);
            var (main, unit) = ValueParts(v);
            row.Children.Add(ValueStack(main, unit, B.Label, v?.Sub, W - s - 10, H, 36));
            Children.Add(row);
            return;
        }
        var col = Column(null, VerticalAlignment.Center, HorizontalAlignment.Center);
        col.Children.Add(badge);
        if (labelH > 0 && TileText.Label(label, W, 12, C(L.Variant), align: TextAlignment.Center) is { } l)
        {
            l.Margin = new Thickness(0, 2, 0, 0);
            col.Children.Add(l);
        }
        Children.Add(col);
        ToolTipService.SetToolTip(this, v?.Display is { } shown ? shown + (v.Sub is null ? "" : " · " + v.Sub) : null);
    }
}

// ── Clock, date and countdown: they tick by themselves ───────────────────────────────

internal sealed class ClockBlock : BlockView
{
    private readonly DispatcherTimer _timer = new();
    private TextBlock? _text;
    private string _format = "t";

    public ClockBlock(BlockContext ctx) : base(ctx)
    {
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    protected override void Draw(TileValue? v)
    {
        var date = B.Type == BlockType.Date;
        var custom = B.Text is { Length: <= 24 } f && f.Any(char.IsLetter) && Valid(f) ? f : null;
        _format = custom ?? (date ? "ddd d MMM" : CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern);
        _timer.Interval = TimeSpan.FromSeconds(_format.Contains('s') ? 1 : 5);
        var sample = DateTime.Today.AddHours(18).AddMinutes(58).AddSeconds(58).ToString(_format, CultureInfo.CurrentCulture).Replace('1', '8');
        var label = B.Label is not null && H >= 70 ? B.Label : null;
        var labelH = label is not null ? TileText.LineHeight(12, TileText.Medium) + 2 : 0;
        var shape = B.Style == BlockStyle.Shape ? B.Shape ?? TileShape.Cookie12 : null;
        var boxW = shape is not null ? Math.Min(W, H - labelH) : W;
        var boxH = shape is not null ? boxW : H - labelH;
        var fit = TileText.FitText(sample, shape is not null ? boxW * 0.7 : boxW, shape is not null ? boxW * 0.4 : boxH, date ? 32 : 57, 11, 1, TileText.Bold);
        var centered = shape is not null || (B.Style == BlockStyle.Tile && !date);
        _text = Text(DateTime.Now.ToString(_format, CultureInfo.CurrentCulture), fit.Size, shape is not null ? L.OnShape : L.Content, TileText.Bold, 1, centered ? TextAlignment.Center : TextAlignment.Left);
        _text.HorizontalAlignment = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _text.VerticalAlignment = VerticalAlignment.Center;
        var col = Column(null, VerticalAlignment.Center, centered ? HorizontalAlignment.Center : HorizontalAlignment.Stretch);
        if (label is not null && TileText.Label(label, W, 12, C(L.Variant)) is { } l) col.Children.Add(l);
        if (shape is not null)
        {
            var g = new Grid { Width = boxW, Height = boxW };
            g.Children.Add(TileDrawing.Shape(shape, boxW, C(L.ShapeFill)));
            g.Children.Add(_text);
            col.Children.Add(g);
        }
        else
        {
            col.Children.Add(_text);
        }
        Children.Add(col);
    }

    private static bool Valid(string format)
    {
        try { _ = DateTime.Now.ToString(format, CultureInfo.CurrentCulture); return true; }
        catch (FormatException) { return false; }
    }

    private void Tick()
    {
        if (_text is null) return;
        var now = DateTime.Now.ToString(_format, CultureInfo.CurrentCulture);
        if (_text.Text != now) _text.Text = now;
    }
}

internal sealed class CountdownBlock : BlockView
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private TextBlock? _text;
    private long? _at;

    public CountdownBlock(BlockContext ctx) : base(ctx)
    {
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
    }

    protected override void Draw(TileValue? v)
    {
        if (DrawState(v)) return;
        var label = B.Label ?? v?.Text ?? Source?.Label;
        var sub = v?.Sub;
        _at = v?.At;
        var smallH = TileText.LineHeight(12, TileText.Medium);
        var lines = (label is null ? 0 : 1) + (sub is not null && H >= 64 ? 1 : 0);
        var valueH = Math.Max(16, H - lines * smallH);
        var col = Column(W, VerticalAlignment.Center);
        if (TileText.Label(label, W, 12, C(L.Variant)) is { } l) col.Children.Add(l);
        var sample = Remaining() is { } r && r < TimeSpan.FromDays(1) ? (r >= TimeSpan.FromHours(1) ? "88:88:88" : "88:88") : Format(Remaining()) ?? "—";
        var fit = TileText.FitText(sample, W, valueH, 45, 11, 1, TileText.Bold);
        _text = Text(Format(Remaining()) ?? v?.Display ?? "—", fit.Size, L.Content, TileText.Bold);
        col.Children.Add(_text);
        if (sub is not null && H >= 64 && TileText.Label(sub, W, 12, C(L.Variant), TileText.Regular) is { } s) col.Children.Add(s);
        Children.Add(col);
    }

    private TimeSpan? Remaining() => _at is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) - DateTimeOffset.Now : null;

    internal static string? Format(TimeSpan? remain)
    {
        if (remain is not { } r) return null;
        if (r <= TimeSpan.Zero) return "Now";
        if (r < TimeSpan.FromHours(1)) return $"{(int)r.TotalMinutes:00}:{r.Seconds:00}";
        if (r < TimeSpan.FromDays(1)) return $"{(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00}";
        if (r < TimeSpan.FromDays(3)) return $"{(int)r.TotalDays}d {r.Hours}h";
        return $"{(int)Math.Ceiling(r.TotalDays)} days";
    }

    private void Tick()
    {
        if (_text is null || _at is null) return;
        var t = Format(Remaining());
        if (t is not null && _text.Text != t) _text.Text = t;
    }
}

// ── Button ─────────────────────────────────────────────────────────────────────────

internal sealed class ButtonBlock(BlockContext ctx) : BlockView(ctx)
{
    protected override void Draw(TileValue? v)
    {
        var label = B.Label ?? B.Text;
        var icon = FirstIcon(B.Icon);
        var press = new Pressable
        {
            Fill = Ctx.Solo ? T.Container(T.NeutralTile) : L.Bg is { } bg ? T.Container(bg) : C(T.NeutralTile, 0.0),
            CornerRadius = new CornerRadius(Ctx.Solo ? Ctx.Radius : Math.Min(Ctx.Radius, H / 2)),
            AccessibleName = label ?? TileTap.Describe(B.Tap),
        };
        ToolTipService.SetToolTip(press, B.Tap is null && B.Action is null ? label : TileTap.Describe(B.Tap) + (label is null ? "" : $" ({label})"));
        var content = Ctx.Solo && W < 140 || H >= 64 && W < H * 1.6 ? Stacked(label, icon) : Inline(label, icon);
        press.SetContent(content);
        if (Ctx.Actions is { } actions) press.Click += (_, _) => _ = actions.TapAsync(B, B.Tap, v, press);
        else press.IsEnabledForInput = false;
        Children.Add(press);
    }

    /// <summary>The icon over its label: an app-launcher button.</summary>
    private FrameworkElement Stacked(string? label, string? icon)
    {
        var col = Column(null, VerticalAlignment.Center, HorizontalAlignment.Center);
        col.Spacing = 4;
        var iconSize = Math.Clamp(Math.Min(W, H) * 0.34, 18, 30);
        col.Children.Add(Glyph(icon ?? "touch_app", iconSize, L.Content));
        if (label is not null && H - iconSize > 26 && TileText.Label(label, W - 10, 12, C(L.Content), TileText.Medium, align: TextAlignment.Center) is { } l)
        {
            l.HorizontalAlignment = HorizontalAlignment.Center;
            col.Children.Add(l);
        }
        return col;
    }

    /// <summary>A pill: icon and label in a line.</summary>
    private FrameworkElement Inline(string? label, string? icon)
    {
        var iconSize = Math.Clamp(H * 0.42, 14, 22);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null) row.Children.Add(Glyph(icon, iconSize, L.Content));
        if (label is not null)
        {
            var fit = TileText.FitText(label, W - (icon is null ? 12 : iconSize + 18), H, 15, 10, 1, TileText.Medium);
            if (fit.Fits || icon is null) row.Children.Add(Text(label, fit.Size, L.Content, TileText.Medium));
        }
        return row;
    }
}

// ── Image ──────────────────────────────────────────────────────────────────────────

internal sealed class ImageBlock : BlockView
{
    private string? _link;

    public ImageBlock(BlockContext ctx) : base(ctx)
    {
        Background = new SolidColorBrush(Colors.Transparent);
        Tapped += (_, _) =>
        {
            if (_link is not null && Ctx.Actions is { } actions && B.Tap is null) _ = actions.OpenLinkAsync(_link);
        };
    }

    protected override void Draw(TileValue? v)
    {
        if (B.Bind is not null && v is not null && v.State is TileValueState.Needs or TileValueState.Error && v.Picture is null)
        {
            Note(v.State == TileValueState.Needs ? "key" : "warning", v.Note ?? "No picture");
            return;
        }
        var src = B.Bind is null ? B.Url : v?.Picture;
        var first = v?.Items.FirstOrDefault();
        var caption = B.Label ?? (B.Variant == "bare" ? null : v?.Image is not null ? v.Text : first?.Title);
        var radius = Ctx.Solo ? Ctx.Radius : Math.Min(Ctx.Radius, Math.Min(W, H) / 2);
        Children.Add(TileDrawing.Picture(src, W, H, radius, C(L.Track), C(L.Variant), Source?.Icon ?? "image", B.Variant == "fit"));
        if (caption is not null && H >= 72 && W >= 90)
        {
            var scrim = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
            scrim.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0 });
            scrim.GradientStops.Add(new GradientStop { Color = Color.FromArgb(170, 0, 0, 0), Offset = 1 });
            var band = new Border { Height = Math.Min(H * 0.55, 56), VerticalAlignment = VerticalAlignment.Bottom, Background = scrim, CornerRadius = new CornerRadius(0, 0, radius, radius), IsHitTestVisible = false };
            var t = TileText.Block(caption, 13, Colors.White, TileText.Bold);
            t.VerticalAlignment = VerticalAlignment.Bottom;
            t.Margin = new Thickness(10, 0, 10, 8);
            band.Child = t;
            Children.Add(band);
        }
        _link = v?.Url ?? first?.Link ?? (TileTap.IsWebLink(B.Url ?? "") ? B.Url : null);
        IsHitTestVisible = _link is not null && Ctx.Actions is not null || B.Tap is not null;
        if (_link is not null) ToolTipService.SetToolTip(this, caption ?? _link);
    }
}

// ── Carousel ───────────────────────────────────────────────────────────────────────

internal sealed class CarouselBlock : BlockView
{
    private int _index;
    private IReadOnlyList<TileItem> _items = [];
    private readonly List<Pressable> _arrows = [];
    private bool _hover;

    public CarouselBlock(BlockContext ctx) : base(ctx)
    {
        Background = new SolidColorBrush(Colors.Transparent);
        PointerWheelChanged += (_, e) =>
        {
            var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
            Flip(delta < 0 ? 1 : -1);
            e.Handled = true;
        };
        PointerEntered += (_, _) => { _hover = true; foreach (var a in _arrows) a.Opacity = 1; };
        PointerExited += (_, _) => { _hover = false; foreach (var a in _arrows) a.Opacity = 0; };
    }

    protected override void Draw(TileValue? v)
    {
        if (DrawState(v)) return;
        _items = (v?.Items ?? []).Take(Math.Clamp(B.Max ?? 10, 1, 12)).ToList();
        if (_items.Count == 0)
        {
            var t = Text(v?.Note ?? "Nothing here yet", 13, L.Variant, null, 2, TextAlignment.Center);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(t);
            return;
        }
        _index = Math.Min(_index, _items.Count - 1);
        Show(animate: false);
    }

    private void Flip(int by)
    {
        if (_items.Count < 2) return;
        _index = (_index + by + _items.Count) % _items.Count;
        Show(animate: true, from: by);
    }

    private void Show(bool animate, int from = 0)
    {
        Children.Clear();
        _arrows.Clear();
        var item = _items[_index];
        var radius = Ctx.Solo ? Ctx.Radius : Math.Min(Ctx.Radius, Math.Min(W, H) / 2);
        var card = new Grid { Width = W, Height = H, CornerRadius = new CornerRadius(radius) };
        card.Children.Add(TileDrawing.Picture(item.Image, W, H, radius, C(L.Track), C(L.Variant), Source?.Icon ?? "image"));
        var scrim = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        scrim.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, 0, 0, 0), Offset = 0.35 });
        scrim.GradientStops.Add(new GradientStop { Color = Color.FromArgb(190, 0, 0, 0), Offset = 1 });
        card.Children.Add(new Border { Background = scrim, IsHitTestVisible = false });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 10), IsHitTestVisible = false };
        var titleSize = H >= 170 ? 15 : H >= 120 ? 14 : 12.5;
        texts.Children.Add(TileText.Block(item.Title, titleSize, Colors.White, TileText.Bold, H >= 130 ? 2 : 1));
        if (item.Sub is { Length: > 0 } sub && H >= 110) texts.Children.Add(TileText.Block(sub, 11.5, Color.FromArgb(220, 255, 255, 255)));
        card.Children.Add(texts);
        var badgeText = string.Join(" · ", new[] { item.Trailing, _items.Count > 1 ? $"{_index + 1}/{_items.Count}" : null }.OfType<string>());
        if (badgeText.Length > 0)
        {
            var badge = new Border
            {
                Background = Palette.Current.Brush(Color.FromArgb(150, 0, 0, 0)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 3),
                Margin = new Thickness(0, 8, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Child = TileText.Block(badgeText, 11, Colors.White, TileText.Medium),
            };
            card.Children.Add(badge);
        }
        if (Ctx.Actions is { } actions && item.Link is { } link)
        {
            var press = new Pressable { Fill = Colors.Transparent, CornerRadius = new CornerRadius(radius), AccessibleName = item.Title };
            press.Click += (_, _) => _ = actions.OpenLinkAsync(link);
            card.Children.Add(press);
        }
        if (_items.Count > 1)
        {
            card.Children.Add(Arrow("chevron_left", HorizontalAlignment.Left, -1));
            card.Children.Add(Arrow("chevron_right", HorizontalAlignment.Right, 1));
        }
        Children.Add(card);
        if (animate) Motion.EnterFromSide(card, from * 24);
    }

    private Pressable Arrow(string icon, HorizontalAlignment side, int by)
    {
        var p = new Pressable
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Fill = Color.FromArgb(120, 0, 0, 0),
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0),
            AccessibleName = by < 0 ? "Previous" : "Next",
            Opacity = _hover ? 1 : 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) },
        };
        p.SetContent(Controls.Glyph.Make(icon, 18, Colors.White));
        p.Click += (_, _) => Flip(by);
        _arrows.Add(p);
        return p;
    }
}

// ── Player ─────────────────────────────────────────────────────────────────────────

internal sealed class MediaBlock(BlockContext ctx) : BlockView(ctx)
{
    protected override void Draw(TileValue? v)
    {
        if (B.Bind is not null && v?.State is TileValueState.Needs or TileValueState.Error && v.Text is null)
        {
            Note("warning", v.Note ?? "Can't see what's playing");
            return;
        }
        var title = v?.Text ?? "Nothing playing";
        var artist = v?.Sub;
        var playing = v?.Active == true;
        var wide = W >= H * 1.25;
        var btn = Math.Clamp(Math.Min(W, H) * (wide ? 0.3 : 0.22), 28, 44);
        var controlsW = btn * 3 + 16;
        var art = wide ? Math.Min(H, W * 0.4) : Math.Max(0, Math.Min(W, H - btn - TileText.LineHeight(14, TileText.Bold) - 14));
        var radius = Math.Min(Ctx.Radius * 0.8, art / 4);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(Button("prev", "skip_previous", btn * 0.86, null, L.Content));
        controls.Children.Add(Button("toggle", playing ? "pause" : "play_arrow", btn, L.Accent, B.Style == BlockStyle.Filled ? L.Bg ?? T.OnAccent(B.Tone) : T.OnAccent(B.Tone)));
        controls.Children.Add(Button("next", "skip_next", btn * 0.86, null, L.Content));
        StackPanel Words(double tw, double th)
        {
            var fit = TileText.FitText(title, tw, Math.Max(14, th * 0.62), 18, 11, 2, TileText.Bold);
            var col = Column(tw);
            col.Children.Add(Text(title, fit.Size, L.Content, TileText.Bold, Math.Max(1, fit.Lines)));
            if (artist is not null && th >= 34 && TileText.Label(artist, tw, 12, C(L.Variant), TileText.Regular) is { } a) col.Children.Add(a);
            ToolTipService.SetToolTip(col, artist is null ? title : $"{title}\n{artist}");
            return col;
        }
        if (wide)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
            var showArt = art >= 36;
            if (showArt) row.Children.Add(TileDrawing.Picture(v?.Image, art, art, radius, C(L.Track), C(L.Variant), "music_note"));
            var tw = Math.Max(10, (showArt ? W - art - 12 : W));
            var col = Column(tw, VerticalAlignment.Center);
            col.Children.Add(Words(tw, H - btn - 8));
            controls.Margin = new Thickness(0, 6, 0, 0);
            controls.HorizontalAlignment = HorizontalAlignment.Left;
            if (controlsW <= tw + 4) col.Children.Add(controls);
            row.Children.Add(col);
            Children.Add(row);
            return;
        }
        var stack = Column(null, VerticalAlignment.Center, HorizontalAlignment.Center);
        if (art >= 40)
        {
            var pic = TileDrawing.Picture(v?.Image, art, art, radius, C(L.Track), C(L.Variant), "music_note");
            pic.Margin = new Thickness(0, 0, 0, 6);
            stack.Children.Add(pic);
        }
        stack.Children.Add(Words(W, TileText.LineHeight(14, TileText.Bold) * 2));
        controls.HorizontalAlignment = HorizontalAlignment.Center;
        controls.Margin = new Thickness(0, 4, 0, 0);
        stack.Children.Add(controls);
        Children.Add(stack);
    }

    private Pressable Button(string command, string icon, double size, TileRole? fill, TileRole tint)
    {
        var p = new Pressable
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Fill = fill is { } f ? C(f) : Colors.Transparent,
            AccessibleName = command switch { "prev" => "Previous", "next" => "Next", _ => "Play or pause" },
        };
        p.SetContent(Glyph(icon, size * 0.5, tint));
        if (Ctx.Actions is { } actions) p.Click += (_, _) => _ = actions.MediaAsync(B, command);
        return p;
    }
}

// ── Toggle: a Quick Settings tile ──────────────────────────────────────────────────

internal sealed class ToggleBlock : BlockView
{
    private Pressable? _surface;
    private Border? _circle;
    private TextBlock? _icon;
    private TextBlock? _title;
    private TextBlock? _state;
    private bool _on;
    private DateTime _changedAt;
    private TileValue? _value;

    public ToggleBlock(BlockContext ctx) : base(ctx) { }

    public override void Update(TileValue? v)
    {
        _value = v;
        if (_surface is null) Draw(v);
        // a click wins over a read that started before it
        if ((DateTime.UtcNow - _changedAt).TotalSeconds < 2.5) return;
        if (v?.Active is { } on) _on = on;
        Render();
    }

    protected override void Draw(TileValue? v)
    {
        Children.Clear();
        var label = B.Label ?? Source?.Label ?? "Switch";
        var compact = W < 130 && H < 90;
        var tall = !compact && H >= 100 && W < H * 1.6;
        _surface = new Pressable
        {
            CornerRadius = new CornerRadius(Ctx.Solo ? Ctx.Radius : Math.Min(Ctx.Radius, Math.Min(W, H) / 2)),
            AccessibleName = label,
        };
        var iconSize = compact ? Math.Clamp(Math.Min(W, H) * 0.34, 18, 26) : 22;
        _icon = Glyph(IconOf(v), iconSize, L.Content);
        _title = Text(label, compact ? 11.5 : 13.5, L.Content, compact ? TileText.Medium : TileText.Bold, 1, compact ? TextAlignment.Center : TextAlignment.Left);
        _state = Text("", 12, L.Variant);
        if (compact)
        {
            var col = Column(null, VerticalAlignment.Center, HorizontalAlignment.Center);
            col.Spacing = 5;
            col.Children.Add(_icon);
            _title.MaxWidth = W - 8;
            if (H >= 52) col.Children.Add(_title);
            _surface.SetContent(col);
        }
        else
        {
            var grid = new Grid { Padding = tall ? new Thickness(14) : new Thickness(Math.Min(14, H * 0.2), 0, 12, 0), ColumnSpacing = 10, RowSpacing = 8 };
            var circleSize = Math.Clamp(Math.Min(H - 16, 40), 26, 40);
            _circle = new Border { Width = circleSize, Height = circleSize, CornerRadius = new CornerRadius(circleSize / 2), Child = _icon, VerticalAlignment = tall ? VerticalAlignment.Top : VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            _circle.BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(160) };
            var texts = Column(null, tall ? VerticalAlignment.Bottom : VerticalAlignment.Center);
            texts.Children.Add(_title);
            if (H >= 44) texts.Children.Add(_state);
            if (tall)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                grid.Children.Add(_circle);
                SetRow(texts, 1);
                grid.Children.Add(texts);
            }
            else
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.Children.Add(_circle);
                SetColumn(texts, 1);
                grid.Children.Add(texts);
            }
            _surface.SetContent(grid);
        }
        Motion.CenterScale(_circle ?? (FrameworkElement)_icon);
        if (Ctx.Actions is { } actions)
        {
            _surface.Click += (_, _) =>
            {
                if (_value is { State: TileValueState.Empty or TileValueState.Needs }) return;
                _on = !_on;
                _changedAt = DateTime.UtcNow;
                Render();
                if (_on) Motion.Pulse(_circle ?? (FrameworkElement)_icon!, 1.12f);
                _ = actions.SwitchAsync(B, _on);
            };
        }
        else
        {
            _surface.IsEnabledForInput = false;
        }
        Children.Add(_surface);
    }

    private void Render()
    {
        if (_surface is null) return;
        var v = _value;
        var unavailable = v is { State: TileValueState.Empty or TileValueState.Needs };
        // off is the quiet container, on is the palette's primary: Android's Quick Settings tiles
        var off = T.NeutralTile;
        _surface.Fill = _on && !unavailable ? C(TileRole.Primary, T.IsGlass ? 0.92 : 1) : T.Container(off);
        var content = _on && !unavailable ? TileRole.OnPrimary : TileRole.OnSurface;
        var variant = _on && !unavailable ? TileRole.OnPrimary : TileRole.OnSurfaceVariant;
        var p = Palette.Current;
        _icon!.Foreground = p.Brush(C(content));
        _icon.Text = Icons.Glyph(FirstIcon(B.Icon, v?.Icon, Source?.Icon));
        _title!.Foreground = p.Brush(C(content));
        _state!.Foreground = p.Brush(C(variant, _on ? 0.85 : 1));
        _state.Text = unavailable ? v!.Note ?? "Not available" : v?.Text is { Length: > 0 } t ? t : _on ? "On" : "Off";
        if (_circle is not null) _circle.Background = p.Brush(_on && !unavailable ? C(TileRole.OnPrimary, 0.14) : C(TileRole.OnSurface, 0.08));
        _surface.Opacity = unavailable ? 0.6 : 1;
        ToolTipService.SetToolTip(_surface, $"{_title.Text}: {_state.Text}");
    }
}

// ── Slider ─────────────────────────────────────────────────────────────────────────

internal sealed class SliderBlock : BlockView
{
    private PillSlider? _slider;
    private DateTime _lastUser = DateTime.MinValue;
    private TileValue? _value;

    public SliderBlock(BlockContext ctx) : base(ctx) { }

    public override void Update(TileValue? v)
    {
        _value = v;
        if (_slider is null) Draw(v);
        if (_slider is null || _slider.IsDragging || (DateTime.UtcNow - _lastUser).TotalSeconds < 1.5) return;
        if (v?.Number is { } n) _slider.SetValue(n);
        if (FirstIcon(B.Icon, v?.Icon, Source?.Icon) is { } icon) _slider.SetIcon(icon);
    }

    protected override void Draw(TileValue? v)
    {
        Children.Clear();
        var label = B.Label ?? Source?.Label ?? "";
        var h = Ctx.Solo ? H : Math.Min(H, 52);
        var unit = v?.Unit ?? Source?.Sample.Unit;
        _slider = new PillSlider(IconOf(v), label, unit == "%" ? "{value}%" : "{value}", h)
        {
            VerticalAlignment = VerticalAlignment.Center,
            Width = W,
        };
        _slider.CornerRadius = new CornerRadius(Ctx.Solo ? Ctx.Radius : Math.Min(Ctx.Radius, h / 2));
        _slider.SetColors(new PillSlider.SliderColors(
            T.Container(Ctx.Solo || L.Bg is null ? T.NeutralTile : L.Bg.Value),
            C(TileRole.Primary),
            C(TileRole.OnPrimary),
            C(TileRole.OnSurface),
            C(TileRole.OnSurfaceVariant)));
        _slider.Configure(B.From ?? v?.Min ?? 0, B.To ?? v?.Max ?? 100, B.Step ?? 1);
        if (v?.Number is { } n) _slider.SetValue(n);
        if (Ctx.Actions is { } actions)
        {
            _slider.ValueChanged += (value, final) =>
            {
                _lastUser = DateTime.UtcNow;
                _ = actions.LevelAsync(B, value, final);
            };
        }
        else
        {
            _slider.IsEnabledForInput = false;
        }
        Children.Add(_slider);
    }
}

// ── Choice ─────────────────────────────────────────────────────────────────────────

internal sealed class ChoiceBlock : BlockView
{
    private SegmentedPill? _pill;
    private IReadOnlyList<TileOption> _options = [];
    private DateTime _changedAt;

    public ChoiceBlock(BlockContext ctx) : base(ctx) { }

    public override void Update(TileValue? v)
    {
        var options = B.Options.Count > 0 ? B.Options : v?.Options ?? Source?.Sample.Options ?? [];
        if (_pill is null || !options.Select(o => o.Label).SequenceEqual(_options.Select(o => o.Label))) Draw(v);
        if (_pill is null || (DateTime.UtcNow - _changedAt).TotalSeconds < 2.5) return;
        var selected = v?.Selected;
        _pill.SetSelected(selected is null ? -1 : _options.ToList().FindIndex(o => ValueText.Matches(o.Value, selected) || ValueText.Matches(o.Label, selected)));
    }

    protected override void Draw(TileValue? v)
    {
        Children.Clear();
        _options = (B.Options.Count > 0 ? B.Options : v?.Options ?? Source?.Sample.Options ?? []).Take(6).ToList();
        if (B.Bind is not null && v is { State: TileValueState.Needs or TileValueState.Error } && _options.Count == 0)
        {
            Note("warning", v.Note ?? "Couldn't read the options");
            return;
        }
        if (_options.Count == 0)
        {
            Note("info", "No options");
            return;
        }
        var label = B.Label ?? Source?.Label;
        var icon = IconOf(v);
        var segH = Math.Clamp(Math.Min(H, 44), 28, 44);
        _pill = new SegmentedPill(_options.Select(o => (o.Label, o.Icon)).ToList(), segH) { VerticalAlignment = VerticalAlignment.Center };
        _pill.SetColors(T.Container(Ctx.Solo || L.Bg is null ? TileRole.SurfaceContainerHighest : L.Bg.Value == TileRole.SurfaceContainerHighest ? TileRole.SurfaceContainerLowest : TileRole.SurfaceContainerHighest),
            C(TileRole.Primary), C(TileRole.OnPrimary), C(TileRole.OnSurfaceVariant));
        if (Ctx.Actions is { } actions)
        {
            _pill.Selected += index =>
            {
                _changedAt = DateTime.UtcNow;
                _ = actions.ChooseAsync(B, _options[index]);
            };
        }
        else
        {
            _pill.IsEnabledForInput = false;
        }

        var segmentsNeed = _options.Count * (_options.Max(o => TileText.Width(o.Label, 12.5, TileText.Bold)) + 22) + 6;
        var pad = Ctx.Solo ? 14.0 : 0;
        var inner = W - 2 * pad;
        var side = label is not null && inner - segmentsNeed >= 110 && H < 90;
        var container = new Grid { Padding = new Thickness(pad, 0, Ctx.Solo ? 10 : 0, 0) };
        if (Ctx.Solo) container.Background = Palette.Current.Brush(T.Container(T.NeutralTile));
        if (Ctx.Solo) container.CornerRadius = new CornerRadius(Ctx.Radius);
        if (side)
        {
            // title on the left, the segments on the right (a full-width bar)
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            container.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            container.ColumnSpacing = 12;
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            if (icon is not null) head.Children.Add(Glyph(icon, 22, Ctx.Solo || L.Bg is not null ? L.Content : L.Accent));
            var titles = Column(null);
            if (TileText.Label(label, Math.Min(140, inner - segmentsNeed - 40), 13.5, C(L.Content), TileText.Bold) is { } t) titles.Children.Add(t);
            if (v?.Sub is { } sub && TileText.Label(sub, Math.Min(140, inner - segmentsNeed - 40), 11, C(L.Variant), TileText.Regular) is { } s) titles.Children.Add(s);
            head.Children.Add(titles);
            container.Children.Add(head);
            SetColumn(_pill, 1);
            container.Children.Add(_pill);
        }
        else
        {
            var col = Column(null, VerticalAlignment.Center);
            col.Spacing = 4;
            if (label is not null && H - segH >= 22 && TileText.Label(label, inner, 11.5, C(L.Variant), TileText.Bold) is { } l) col.Children.Add(l);
            col.Children.Add(_pill);
            container.Children.Add(col);
        }
        Children.Add(container);
    }
}
