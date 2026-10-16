using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileSmith.Core.Tiles;
using TileSmith.Theme;

namespace TileSmith.Controls.Tiles;

/// <summary>
/// A design drawn at an exact size: its surface, then every block laid out to the pixel by <see cref="TileLayout"/>
/// and sized by <see cref="TileText"/>, so nothing wraps-to-content and nothing pushes a sibling off the edge. Values
/// come in through <see cref="Show"/> and update the blocks in place. Previews (the gallery, the Studio) fill anything
/// not read yet with the source's sample, so a widget never looks empty while its data loads.
/// </summary>
public sealed class TileView : Grid
{
    private static readonly HashSet<BlockType> SelfDrawn = [BlockType.Button, BlockType.Toggle, BlockType.Slider, BlockType.Choice];
    private static readonly HashSet<BlockType> EdgeToEdge = [BlockType.Image, BlockType.Carousel];

    private readonly List<(TileBlock Block, string? Id, BlockView View, FrameworkElement Cell)> _blocks = [];
    private readonly bool _preview;
    private IReadOnlyDictionary<string, TileValue> _values = new Dictionary<string, TileValue>();
    private int _highlighted = -1;
    private readonly bool _pickable;

    /// <param name="pickable">Clicking a block reports it (<see cref="BlockPicked"/>): the Studio's stage.</param>
    public TileView(TileDesign design, double width, double height, TileTheme theme, ITileActions? actions, bool preview, bool pickable = false)
    {
        _pickable = pickable;
        Design = design;
        Theme = theme;
        _preview = preview;
        Width = width;
        Height = height;
        CornerRadius = new CornerRadius(TileLayout.Radius);
        Plan = TileLayout.Layout(design, width, height);
        if (!Plan.Solo) Background = Palette.Current.Brush(theme.Background);
        Build(actions);
    }

    public TileDesign Design { get; }
    public TileTheme Theme { get; }
    public TileLayout.Plan Plan { get; }

    /// <summary>A block was clicked in the Studio's stage (index in reading order).</summary>
    public event Action<int>? BlockPicked;

    /// <summary>Shows what the sources read.</summary>
    public void Show(IReadOnlyDictionary<string, TileValue> values)
    {
        _values = values;
        foreach (var (block, id, view, _) in _blocks) view.Update(ValueFor(block, id));
    }

    /// <summary>The value a block shows: what its source read, or (in a preview) the source's sample until then.</summary>
    private TileValue? ValueFor(TileBlock b, string? id)
    {
        var read = id is not null && _values.TryGetValue(id, out var v) ? v : null;
        if (!_preview) return read;
        if (read is { State: TileValueState.Ok }) return read;
        return TileSources.ByKey(b.Bind)?.Sample ?? read;
    }

    /// <summary>Rings one block (the Studio's Blocks list hovering it).</summary>
    public void Highlight(int index)
    {
        if (_highlighted == index) return;
        _highlighted = index;
        var cells = Plan.Cells.ToList();
        for (var i = 0; i < _blocks.Count && i < cells.Count; i++)
        {
            if (_blocks[i].Cell is not Border border) continue;
            var on = cells[i].Index == index;
            border.BorderBrush = on ? Palette.Current.Brush(Theme.C(TileRole.Primary)) : null;
            border.BorderThickness = new Thickness(on ? 2 : 0);
        }
    }

    private void Build(ITileActions? actions)
    {
        var plan = Plan;
        var root = new Grid { Padding = new Thickness(plan.Pad) };
        var y = 0.0;
        var i = 0;
        foreach (var row in plan.Rows)
        {
            var x = 0.0;
            foreach (var cell in row.Cells)
            {
                var element = BuildCell(cell, actions, plan.Solo);
                element.HorizontalAlignment = HorizontalAlignment.Left;
                element.VerticalAlignment = VerticalAlignment.Top;
                element.Margin = new Thickness(x, y, 0, 0);
                root.Children.Add(element);
                x += cell.Width + plan.Gap;
                i++;
            }
            y += row.Height + plan.Gap;
        }
        if (Design.Tap is { } tap && actions is not null)
        {
            // the whole widget opens something: the frame takes the clicks its blocks don't
            var press = new Pressable { Fill = Colors.Transparent, CornerRadius = new CornerRadius(TileLayout.Radius), AccessibleName = Design.Name };
            ToolTipService.SetToolTip(press, TileTap.Describe(tap));
            press.SetContent(root);
            press.Click += (_, _) => _ = actions.TapAsync(null, tap, null, press);
            Children.Add(press);
        }
        else
        {
            Children.Add(root);
        }
        if (plan.Rows.Count == 0)
        {
            var empty = TileText.Block("Nothing to show", 12, Theme.C(Theme.OnBgVariant), TileText.Medium, 1, TextAlignment.Center);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            Children.Add(empty);
        }
    }

    private FrameworkElement BuildCell(TileLayout.Cell cell, ITileActions? actions, bool solo)
    {
        var b = cell.Block;
        var look = Theme.Look(b);
        var (w, h) = (cell.Width, cell.Height);
        var radius = solo ? TileLayout.Radius : Math.Min(TileLayout.InnerRadius, Math.Min(w, h) / 2);
        var selfDrawn = SelfDrawn.Contains(b.Type);
        var edge = EdgeToEdge.Contains(b.Type);
        var bg = selfDrawn ? null : look.Bg is { } r ? Theme.Container(r) : edge ? Theme.Container(Theme.NeutralTile) : (Windows.UI.Color?)null;
        var padded = !selfDrawn && !edge && (look.Bg is not null || b.Style == BlockStyle.Filled);
        // very short tiles give their padding back to the text
        var p = selfDrawn || edge ? 0 : padded ? Math.Min(Math.Clamp(Math.Min(w, h) * 0.12, 8, 14), Math.Max(2, (h - 18) / 2)) : 2;
        var cw = Math.Max(1, w - 2 * p);
        var ch = Math.Max(1, h - 2 * p);
        var view = BlockView.Create(new BlockContext(Design, b, cell.Index, Theme, look, cw, ch, radius, solo, actions));
        view.HorizontalAlignment = HorizontalAlignment.Center;
        view.VerticalAlignment = VerticalAlignment.Center;
        var border = new Border
        {
            Width = w,
            Height = h,
            Padding = new Thickness(p),
            CornerRadius = new CornerRadius(radius),
            Background = bg is { } color ? Palette.Current.Brush(color) : null,
            Child = view,
        };
        FrameworkElement element = border;
        if (b.Tap is { } tap && b.Type != BlockType.Button && actions is not null)
        {
            var press = new Pressable { Fill = Colors.Transparent, CornerRadius = new CornerRadius(radius), Width = w, Height = h, AccessibleName = b.Label ?? TileTap.Describe(tap) };
            ToolTipService.SetToolTip(press, TileTap.Describe(tap));
            press.SetContent(border);
            press.Click += (_, _) => _ = actions.TapAsync(b, tap, ValueFor(b, TileSources.BindingId(b)), press);
            element = press;
        }
        if (_pickable)
        {
            element.Tapped += (_, e) =>
            {
                BlockPicked?.Invoke(cell.Index);
                e.Handled = false;
            };
        }
        var id = TileSources.BindingId(b);
        _blocks.Add((b, id, view, border));
        view.Update(ValueFor(b, id));
        return element;
    }
}
