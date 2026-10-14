using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TileSmith.Core.Widgets;
using TileSmith.Theme;
using Windows.Foundation;

namespace TileSmith.Controls;

/// <summary>
/// Launcher-style grid: items take W×H cells and pack in order into the first free spot (row-major),
/// like an Android home screen. Width 4 means "full width" whatever the column count. A tile whose content
/// needs more height than its cells gets more rows, so nothing is ever clipped.
/// </summary>
public sealed class BoardPanel : Panel
{
    public static readonly DependencyProperty CellsProperty =
        DependencyProperty.RegisterAttached("Cells", typeof(object), typeof(BoardPanel), new PropertyMetadata(new CellSize(2, 1)));

    public static CellSize GetCells(UIElement e) => e is WidgetTile t ? t.Cells : (CellSize)e.GetValue(CellsProperty);
    public static void SetCells(UIElement e, CellSize value) => e.SetValue(CellsProperty, value);

    public int Columns { get; set; } = 4;
    public double CellWidth { get; set; } = TileLayout.CellWidth;
    public double CellHeight { get; set; } = TileLayout.CellHeight;
    public double Gap { get; set; } = TileLayout.Gap;

    /// <summary>Packing order (children order is left alone so dragging never re-parents).</summary>
    public List<UIElement> Order { get; } = [];

    public Dictionary<UIElement, Rect> Slots { get; } = [];

    public event Action? Arranged;

    /// <summary>The item being dragged: it follows the pointer, so it's left out of the reflow glide.</summary>
    public UIElement? Dragging { get; set; }

    /// <summary>Drawn in the dragged item's slot: where it will land.</summary>
    public UIElement? Placeholder { get; set; }

    private readonly Dictionary<UIElement, Rect> _previous = [];

    public double BoardWidth => Columns * CellWidth + (Columns - 1) * Gap;

    public CellSize Clamp(CellSize size) => new(size.W >= 4 ? Columns : Math.Min(size.W, Columns), Math.Clamp(size.H, 1, WidgetSize.MaxHeight));

    private double Pack(Dictionary<UIElement, Rect> slots, Dictionary<UIElement, CellSize> sizes)
    {
        var rows = new List<bool[]>();
        bool Free(int r, int c, int w, int h)
        {
            for (var y = r; y < r + h; y++)
            {
                while (rows.Count <= y) rows.Add(new bool[Columns]);
                for (var x = c; x < c + w; x++) if (rows[y][x]) return false;
            }
            return true;
        }

        var maxRow = 0;
        foreach (var child in Order.Where(c => c.Visibility == Visibility.Visible && c != Placeholder))
        {
            var size = sizes[child];
            for (var r = 0; ; r++)
            {
                var placed = false;
                for (var c = 0; c <= Columns - size.W; c++)
                {
                    if (!Free(r, c, size.W, size.H)) continue;
                    for (var y = r; y < r + size.H; y++)
                        for (var x = c; x < c + size.W; x++) rows[y][x] = true;
                    slots[child] = new Rect(
                        c * (CellWidth + Gap),
                        r * (CellHeight + Gap),
                        size.W * CellWidth + (size.W - 1) * Gap,
                        size.H * CellHeight + (size.H - 1) * Gap);
                    maxRow = Math.Max(maxRow, r + size.H);
                    placed = true;
                    break;
                }
                if (placed) break;
            }
        }
        return maxRow == 0 ? 0 : maxRow * CellHeight + (maxRow - 1) * Gap;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children.Where(c => !Order.Contains(c) && c != Placeholder)) Order.Add(child);
        Order.RemoveAll(c => !Children.Contains(c));

        // measure each item at its width with unlimited height, then give it enough rows for what it needs
        var sizes = new Dictionary<UIElement, CellSize>();
        foreach (var child in Order.Where(c => c.Visibility == Visibility.Visible))
        {
            var size = Clamp(GetCells(child));
            var width = size.W * CellWidth + (size.W - 1) * Gap;
            child.Measure(new Size(width, double.PositiveInfinity));
            var needed = TileLayout.RowsFor(child.DesiredSize.Height);
            if (needed > size.H) size = size with { H = Math.Min(needed, WidgetSize.MaxHeight + 1) };
            sizes[child] = size;
        }

        Slots.Clear();
        var height = Pack(Slots, sizes);
        foreach (var (child, rect) in Slots) child.Measure(new Size(rect.Width, rect.Height));
        if (Placeholder is not null && Dragging is not null && Slots.TryGetValue(Dragging, out var slot)) Placeholder.Measure(new Size(slot.Width, slot.Height));
        return new Size(BoardWidth, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            if (child == Placeholder)
            {
                child.Arrange(Dragging is not null && Slots.TryGetValue(Dragging, out var target) ? target : new Rect(0, 0, 0, 0));
                continue;
            }
            if (!Slots.TryGetValue(child, out var rect))
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            child.Arrange(rect);
            // FLIP: anything that moved springs from where it was instead of jumping
            if (child != Dragging && _previous.TryGetValue(child, out var old) && (old.X != rect.X || old.Y != rect.Y))
                Motion.Glide(child, new Vector3((float)(old.X - rect.X), (float)(old.Y - rect.Y), 0));
            _previous[child] = rect;
        }
        foreach (var gone in _previous.Keys.Where(k => !Children.Contains(k)).ToList()) _previous.Remove(gone);
        Arranged?.Invoke();
        return finalSize;
    }

    /// <summary>The item (other than <paramref name="except"/>) whose slot contains the point.</summary>
    public UIElement? ItemAt(Point point, UIElement? except) =>
        Slots.FirstOrDefault(kv => kv.Key != except && kv.Key != Placeholder && kv.Value.Contains(point)).Key;
}

/// <summary>
/// The tile board. Press and hold a tile to lift it, then drag it where you want it — the others spring out of the
/// way and a placeholder shows where it will land; the board scrolls when you drag near its edge. Holding without
/// moving opens the tile's menu. A quick press-and-drag never moves tiles (so sliders and scrolling feel normal).
/// In edit mode the corner handle resizes by cells.
/// </summary>
public sealed class TileBoard : Grid
{
    private const double MoveTolerance = 8;
    private static readonly TimeSpan HoldDelay = TimeSpan.FromMilliseconds(380);

    private readonly BoardPanel _panel = new();
    private readonly DispatcherTimer _holdTimer = new() { Interval = HoldDelay };
    private readonly DispatcherTimer _scrollTimer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Pressable _addTile;
    private readonly Rectangle _placeholder;

    private WidgetTile? _pressTile;
    private Point _pressPoint;
    private Pointer? _pointer;
    private WidgetTile? _dragTile;
    private Point _grab;
    private Point _lastPoint;
    private Point _lastViewportPoint;
    private bool _movedSinceLift;
    private WidgetTile? _resizeTile;
    private CellSize _resizeStart;
    private Point _resizeOrigin;
    private bool _editMode;

    public TileBoard()
    {
        var p = Palette.Current;
        Children.Add(_panel);
        _panel.Arranged += OnArranged;
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer.Stop();
            if (_pressTile is not null) Lift(_pressTile, _pressPoint);
        };
        _scrollTimer.Tick += (_, _) => AutoScroll();
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        // capture-lost bubbles up from tiles' own buttons (they let go when the board lifts a tile): only ours counts
        PointerCaptureLost += (_, e) => { if (ReferenceEquals(e.OriginalSource, this)) EndGesture(commit: true); };
        PointerCanceled += (_, _) => EndGesture(commit: true);

        _placeholder = new Rectangle
        {
            RadiusX = WidgetTile.Radius,
            RadiusY = WidgetTile.Radius,
            Stroke = p.Brush(Palette.WithAlpha(p.Accent, 0.7)),
            StrokeThickness = 1.5,
            StrokeDashArray = [5, 4],
            Fill = p.Brush(Palette.WithAlpha(p.Accent, 0.08)),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _panel.Placeholder = _placeholder;
        _addTile = BuildAddTile();
    }

    public event Action<IReadOnlyList<string>>? OrderChanged;
    public event Action<WidgetTile, CellSize>? Resized;
    public event Action? AddRequested;

    /// <summary>A drag or resize just finished (the panel may resize now).</summary>
    public event Action? GestureEnded;

    /// <summary>The scroll viewer around the board (for scrolling while dragging near its edges).</summary>
    public ScrollViewer? Scroller { get; set; }

    public bool IsDragging => _dragTile is not null || _resizeTile is not null;

    public int Columns
    {
        get => _panel.Columns;
        set { _panel.Columns = value; _panel.InvalidateMeasure(); }
    }

    public double CellWidth => _panel.CellWidth;
    public double CellHeight => _panel.CellHeight;
    public double Gap => _panel.Gap;
    public double BoardWidth => _panel.BoardWidth;

    public IEnumerable<WidgetTile> Tiles => _panel.Order.OfType<WidgetTile>();

    public bool IsEditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            foreach (var t in Tiles) t.IsEditMode = value;
            // while a tile is being dragged the layout stays put; the add tile appears after the drop
            _addTile.Visibility = value && !IsDragging ? Visibility.Visible : Visibility.Collapsed;
            if (value && _addTile.Visibility == Visibility.Visible) Motion.Enter(_addTile, 8);
            _panel.InvalidateMeasure();
        }
    }

    /// <summary>Replaces all tiles (keeps the ghost / add tile at the end).</summary>
    public void Load(IEnumerable<WidgetTile> tiles)
    {
        _panel.Children.Clear();
        _panel.Order.Clear();
        _panel.Children.Add(_placeholder);
        foreach (var tile in tiles) AddTileCore(tile);
        _panel.Children.Add(_addTile);
        _panel.Order.Add(_addTile);
    }

    private void AddTileCore(WidgetTile tile)
    {
        tile.IsEditMode = _editMode;
        tile.AddHandler(PointerPressedEvent, new PointerEventHandler(OnTilePressed), handledEventsToo: true);
        tile.ResizeHandlePressed += OnResizePressed;
        _panel.Children.Add(tile);
        _panel.Order.Add(tile);
    }

    private Pressable BuildAddTile()
    {
        var p = Palette.Current;
        var tile = new Pressable { CornerRadius = new CornerRadius(WidgetTile.Radius), Visibility = Visibility.Collapsed, AccessibleName = "Add a widget" };
        BoardPanel.SetCells(tile, new CellSize(2, 1));
        var content = new Grid();
        content.Children.Add(new Rectangle
        {
            RadiusX = WidgetTile.Radius,
            RadiusY = WidgetTile.Radius,
            Stroke = p.Brush(p.HairlineStrong),
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
        });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(Glyph.Make("add", 20, p.Text2));
        var label = Ui.Text("Add widget", 13, p.Text2, semibold: true);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        content.Children.Add(row);
        tile.SetContent(content);
        tile.Click += (_, _) => AddRequested?.Invoke();
        return tile;
    }

    // ── Hold to lift, drag to move ──────────────────────────────────────────────────

    private void OnTilePressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not WidgetTile tile || _dragTile is not null || _resizeTile is not null) return;
        var point = e.GetCurrentPoint(this);
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !point.Properties.IsLeftButtonPressed) return;
        // holding a slider means adjusting it, never lifting the tile (move slider tiles in edit mode)
        if (!_editMode && IsInside<PillSlider>(e.OriginalSource as DependencyObject, tile)) return;
        _pressTile = tile;
        _pressPoint = point.Position;
        _pointer = e.Pointer;
        _holdTimer.Stop();
        _holdTimer.Start();
    }

    private void Lift(WidgetTile tile, Point at)
    {
        _holdTimer.Stop();
        if (!_panel.Slots.TryGetValue(tile, out var slot)) return;
        tile.CancelInteraction();
        _dragTile = tile;
        _movedSinceLift = false;
        _grab = new Point(at.X - slot.X, at.Y - slot.Y);
        _lastPoint = at;
        Canvas.SetZIndex(tile, 100);
        _panel.Dragging = tile;
        _placeholder.Visibility = Visibility.Visible;
        Motion.Enter(_placeholder, 0, 0, Motion.Fast);
        tile.Scale = new Vector3(1.05f, 1.05f, 1);
        tile.Opacity = 0.96;
        if (_pointer is not null) CapturePointer(_pointer);
        _panel.InvalidateMeasure();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        if (Scroller is not null) _lastViewportPoint = e.GetCurrentPoint(Scroller).Position;
        if (_resizeTile is not null)
        {
            ResizeTo(point);
            return;
        }
        if (_dragTile is null)
        {
            // moving before the hold completes is a normal tap-drag (slider, scroll): never a move
            if (_pressTile is not null && Distance(point, _pressPoint) > MoveTolerance)
            {
                _holdTimer.Stop();
                _pressTile = null;
            }
            return;
        }

        if (!_movedSinceLift && Distance(point, _pressPoint) > MoveTolerance) _movedSinceLift = true;
        _lastPoint = point;
        FollowPointer();
        Reorder(point);
        if (!_scrollTimer.IsEnabled) _scrollTimer.Start();
    }

    private void Reorder(Point point)
    {
        if (_dragTile is null) return;
        var target = _panel.ItemAt(point, _dragTile);
        if (target is not WidgetTile targetTile) return;
        var from = _panel.Order.IndexOf(_dragTile);
        var to = _panel.Order.IndexOf(targetTile);
        if (from < 0 || to < 0 || from == to) return;
        _panel.Order.RemoveAt(from);
        _panel.Order.Insert(to, _dragTile);
        _panel.InvalidateMeasure();
    }

    private void FollowPointer()
    {
        if (_dragTile is null || !_panel.Slots.TryGetValue(_dragTile, out var slot)) return;
        var x = _lastPoint.X - _grab.X - slot.X;
        var y = _lastPoint.Y - _grab.Y - slot.Y;
        _dragTile.Translation = new Vector3((float)x, (float)y, 32);
    }

    /// <summary>Scrolls the board while a lifted tile is held near the top or bottom edge.</summary>
    private void AutoScroll()
    {
        if (_dragTile is null || Scroller is null || Scroller.ScrollableHeight <= 0)
        {
            _scrollTimer.Stop();
            return;
        }
        const double edge = 40;
        var y = _lastViewportPoint.Y;
        var speed = y < edge ? -(edge - y) / edge : y > Scroller.ViewportHeight - edge ? (y - (Scroller.ViewportHeight - edge)) / edge : 0;
        if (Math.Abs(speed) < 0.01) return;
        var offset = Math.Clamp(Scroller.VerticalOffset + speed * 12, 0, Scroller.ScrollableHeight);
        if (Math.Abs(offset - Scroller.VerticalOffset) < 0.5) return;
        var delta = offset - Scroller.VerticalOffset;
        Scroller.ChangeView(null, offset, null, disableAnimation: true);
        // the board moved under the pointer: keep the tile under the finger
        _lastPoint = new Point(_lastPoint.X, _lastPoint.Y + delta);
        FollowPointer();
        Reorder(_lastPoint);
    }

    private void OnArranged()
    {
        if (_dragTile is not null) FollowPointer();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _holdTimer.Stop();
        var menuTile = _dragTile is not null && !_movedSinceLift ? _dragTile : null;
        EndGesture(commit: true);
        ReleasePointerCaptures();
        // held without moving: Android's long-press menu
        menuTile?.ShowMenu();
    }

    private void EndGesture(bool commit)
    {
        _holdTimer.Stop();
        _scrollTimer.Stop();
        _pressTile = null;
        var wasGesture = IsDragging;
        if (_dragTile is { } tile)
        {
            _dragTile = null;
            _panel.Dragging = null;
            _placeholder.Visibility = Visibility.Collapsed;
            Motion.TranslateTo(tile, Vector3.Zero, damping: 0.72f);
            tile.Scale = Vector3.One;
            tile.Opacity = 1;
            Canvas.SetZIndex(tile, 0);
            if (commit && _movedSinceLift) OrderChanged?.Invoke(Tiles.Select(t => t.Widget.Id).ToList());
            _panel.InvalidateMeasure();
        }
        if (wasGesture && _editMode && _resizeTile is null)
        {
            _addTile.Visibility = Visibility.Visible;
            _panel.InvalidateMeasure();
        }
        if (_resizeTile is { } resized)
        {
            _resizeTile = null;
            resized.Scale = Vector3.One;
            if (!commit)
            {
                // cancelled: nothing to save
            }
            else if (resized.Cells != _resizeStart)
            {
                Resized?.Invoke(resized, resized.Cells);
            }
            else
            {
                // a tap on the handle cycles through the sizes that fit
                var min = WidgetSize.Minimum(resized.Widget);
                CellSize[] cycle = [new(1, 1), new(2, 1), new(4, 1), new(2, 2), new(4, 2), new(4, 3)];
                var fits = cycle.Where(s => s.W >= min.W && s.H >= min.H).ToList();
                var next = fits[(fits.IndexOf(_resizeStart) + 1) % fits.Count];
                resized.Cells = next;
                _panel.InvalidateMeasure();
                Resized?.Invoke(resized, next);
            }
            if (_editMode) _addTile.Visibility = Visibility.Visible;
        }
        if (wasGesture) GestureEnded?.Invoke();
    }

    // ── Drag to resize ──────────────────────────────────────────────────────────────

    private void OnResizePressed(WidgetTile tile, PointerRoutedEventArgs e)
    {
        _holdTimer.Stop();
        _pressTile = null;
        _resizeTile = tile;
        _resizeStart = tile.Cells;
        _resizeOrigin = e.GetCurrentPoint(this).Position;
        _pointer = e.Pointer;
        CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void ResizeTo(Point point)
    {
        if (_resizeTile is null) return;
        var start = _panel.Clamp(_resizeStart);
        var startW = start.W * _panel.CellWidth + (start.W - 1) * _panel.Gap;
        var startH = start.H * _panel.CellHeight + (start.H - 1) * _panel.Gap;
        var w = (int)Math.Round((startW + point.X - _resizeOrigin.X + _panel.Gap) / (_panel.CellWidth + _panel.Gap));
        var h = (int)Math.Round((startH + point.Y - _resizeOrigin.Y + _panel.Gap) / (_panel.CellHeight + _panel.Gap));
        var min = WidgetSize.Minimum(_resizeTile.Widget);
        w = Math.Clamp(w, min.W, _panel.Columns);
        h = Math.Clamp(h, min.H, WidgetSize.MaxHeight);
        if (w == 3 && _panel.Columns == 4) w = 4; // widths are 1, 2 or full
        var size = new CellSize(w >= _panel.Columns ? 4 : w, h);
        if (size == _resizeTile.Cells) return;
        _resizeTile.Cells = size;
        _panel.InvalidateMeasure();
    }

    private static bool IsInside<T>(DependencyObject? element, DependencyObject stop)
    {
        for (var e = element; e is not null && e != stop; e = VisualTreeHelper.GetParent(e))
        {
            if (e is T) return true;
        }
        return false;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
