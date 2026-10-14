using Microsoft.UI;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TileSmith.Core;
using TileSmith.Core.Actions;
using TileSmith.Core.Widgets;
using TileSmith.Services;
using TileSmith.Theme;
using Windows.UI;
using L = TileSmith.Core.Widgets.TileLayout;

namespace TileSmith.Controls;

public enum TileCommand { EditWithAi, EditJson, Duplicate, Remove }

/// <summary>
/// One widget on the board, drawn Android-style for its cell size: 1x1 icon tile, 2x1 Quick Settings pill,
/// full-width pill slider or segmented bar, or a card for several controls. Every part uses the sizes in
/// <see cref="TileLayout"/>, the same numbers the validator and the AI work with, so content fits its tile.
/// Runs the widget's actions and keeps each control in sync with its state query.
/// </summary>
public sealed partial class WidgetTile : Grid
{
    public const double Radius = 22;

    private static readonly CellSize[] Sizes = [new(1, 1), new(2, 1), new(4, 1), new(2, 2), new(4, 2), new(2, 3), new(4, 3)];

    private readonly AppHost _host;
    private readonly List<Func<CancellationToken, Task>> _refreshers = [];
    private readonly List<Pressable> _pressables = [];
    private readonly List<SegmentedPill> _segmenteds = [];
    private readonly List<PillSwitch> _switches = [];
    private readonly Grid _surfaceHost = new();
    private readonly Grid _editOverlay;
    private readonly Ellipse _errorDot;
    private UIElement? _busyTarget;
    private PillSlider? _slider;
    private int _busy;
    private bool _editMode;
    private CancellationTokenSource? _refreshCts;
    private DispatcherTimer? _poll;

    public WidgetTile(WidgetDefinition widget, AppHost host, bool preview = false)
    {
        Widget = widget;
        _host = host;
        IsPreview = preview;
        Cells = widget.Cells;

        Background = new SolidColorBrush(Colors.Transparent); // hit-testable everywhere (long-press anywhere)
        Children.Add(_surfaceHost);
        _surfaceHost.Children.Add(Build());

        _errorDot = new Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = Palette.Current.Brush(Palette.Current.Error),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 12, 0),
            Visibility = Visibility.Collapsed,
        };
        Children.Add(_errorDot);

        _editOverlay = BuildEditOverlay();
        Children.Add(_editOverlay);
        ContextFlyout = BuildMenu();

        Motion.EnableTransitions(this, Motion.Normal);
        Motion.CenterScale(this);
        Motion.EnableTransitions(_surfaceHost, Motion.Normal);
        Motion.CenterScale(_surfaceHost);
        _surfaceHost.OpacityTransition = new ScalarTransition { Duration = Motion.Normal };
        Unloaded += (_, _) => StopPolling();
    }

    public WidgetDefinition Widget { get; }
    public CellSize Cells { get; set; }
    public bool IsPreview { get; }

    public event Action<WidgetTile, TileCommand>? Command;
    public event Action<WidgetTile, CellSize>? ResizeRequested;
    public event Action<WidgetTile, string>? Failed;
    public event Action<WidgetTile, PointerRoutedEventArgs>? ResizeHandlePressed;

    public bool IsEditMode
    {
        get => _editMode;
        set
        {
            if (_editMode == value) return;
            _editMode = value;
            _editOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            foreach (var p in _pressables) p.IsEnabledForInput = !value;
            foreach (var s in _segmenteds) s.IsEnabledForInput = !value;
            foreach (var s in _switches) s.IsEnabledForInput = !value;
            _surfaceHost.IsHitTestVisible = !value;
            // the content shrinks a little so the corner badges don't cover it
            _surfaceHost.Scale = value ? new Vector3(0.93f, 0.93f, 1) : Vector3.One;
            _surfaceHost.Opacity = value ? 0.85 : 1;
            if (_slider is not null) _slider.IsEnabledForInput = !value;
            if (value)
            {
                // badges are Pressables (Scale has a property transition), so they fade/rise in rather than scale
                foreach (var badge in _editOverlay.Children.OfType<Pressable>()) Motion.Enter(badge, 6, 40, Motion.Fast);
            }
        }
    }

    /// <summary>A long-press lifted the tile: inner buttons must not fire.</summary>
    public void CancelInteraction()
    {
        foreach (var p in _pressables) p.CancelPress();
    }

    // ── Layout per size ─────────────────────────────────────────────────────────────

    private UIElement Build()
    {
        if (Widget.HasLayout) return LayoutTile();
        var single = L.IsSingle(Widget, Cells) ? Widget.Controls[0] : null;
        if (single is { Type: ControlType.Toggle or ControlType.Button })
            return Cells.W == 1 && Cells.H == 1 ? IconTile(single) : PillTile(single);

        if (single is { Type: ControlType.Slider })
        {
            _slider = BuildSlider(single, height: double.NaN, label: single.Label ?? Widget.Title);
            _slider.CornerRadius = new CornerRadius(Radius);
            _slider.VerticalAlignment = VerticalAlignment.Stretch;
            _busyTarget = _slider;
            return _slider;
        }

        if (single is { Type: ControlType.Choice }) return ChoiceBar(single);
        if (single is { Type: ControlType.Status }) return StatusTile(single);
        return Card();
    }

    private Pressable Surface(Color fill)
    {
        var s = new Pressable { Fill = fill, CornerRadius = new CornerRadius(Radius) };
        _pressables.Add(s);
        return s;
    }

    private string LabelOf(ControlDefinition control) => string.IsNullOrWhiteSpace(control.Label) ? Widget.Title : control.Label!;

    /// <summary>1x1: icon on top, short label under it.</summary>
    private UIElement IconTile(ControlDefinition control)
    {
        var p = Palette.Current;
        var surface = Surface(p.Tile);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Stretch, Spacing = 5, Padding = new Thickness(6, 0, 6, 0) };
        var icon = Glyph.Make(control.Icon ?? Widget.Icon, 24, p.Text1);
        var label = Ui.TipWhenTrimmed(Ui.Text(LabelOf(control), 11.5, p.Text2));
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.TextAlignment = TextAlignment.Center;
        stack.Children.Add(icon);
        stack.Children.Add(label);
        surface.SetContent(stack);
        surface.AccessibleName = LabelOf(control);
        ToolTipService.SetToolTip(surface, Widget.Title);
        _busyTarget = icon;
        WireActivation(control, surface, icon, on =>
        {
            surface.Fill = on ? p.Accent : p.Tile;
            icon.Foreground = p.Brush(on ? p.OnAccent : p.Text1);
            label.Foreground = p.Brush(on ? Palette.WithAlpha(p.OnAccent, 0.8) : p.Text2);
        });
        return surface;
    }

    /// <summary>2x1 (or bigger): Android Quick Settings tile — icon in a circle, label and state.</summary>
    private UIElement PillTile(ControlDefinition control)
    {
        var p = Palette.Current;
        var surface = Surface(p.Tile);
        var tall = Cells.H >= 2;
        var layout = new Grid { Padding = tall ? new Thickness(16) : new Thickness(14, 0, 14, 0), ColumnSpacing = 12, RowSpacing = 10 };

        var circle = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = p.Brush(p.Overlay(0.07)),
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(180) },
            VerticalAlignment = tall ? VerticalAlignment.Top : VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var icon = Glyph.Make(control.Icon ?? Widget.Icon, 22, p.Text1);
        circle.Child = icon;

        var texts = new StackPanel { VerticalAlignment = tall ? VerticalAlignment.Bottom : VerticalAlignment.Center, Spacing = 1 };
        var title = Ui.TipWhenTrimmed(Ui.Text(LabelOf(control), 14, p.Text1, semibold: true));
        var stateText = control.Type == ControlType.Button ? (Widget.Subtitle ?? "") : (control.OffText ?? "Off");
        var state = Ui.Text(stateText, 12, p.Text2);
        texts.Children.Add(title);
        if (state.Text.Length > 0 || control.Type == ControlType.Toggle) texts.Children.Add(state);

        if (tall)
        {
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(circle);
            SetRow(texts, 1);
            layout.Children.Add(texts);
        }
        else
        {
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.Children.Add(circle);
            SetColumn(texts, 1);
            layout.Children.Add(texts);
        }

        surface.SetContent(layout);
        surface.AccessibleName = LabelOf(control);
        _busyTarget = icon;
        WireActivation(control, surface, circle, on =>
        {
            surface.Fill = on ? p.Accent : p.Tile;
            circle.Background = p.Brush(on ? Palette.WithAlpha(p.OnAccent, 0.12) : p.Overlay(0.07));
            icon.Foreground = p.Brush(on ? p.OnAccent : p.Text1);
            title.Foreground = p.Brush(on ? p.OnAccent : p.Text1);
            state.Foreground = p.Brush(on ? Palette.WithAlpha(p.OnAccent, 0.75) : p.Text2);
            if (control.Type == ControlType.Toggle) state.Text = on ? (control.OnText ?? "On") : (control.OffText ?? "Off");
        });
        return surface;
    }

    /// <summary>Hooks a toggle or button to a surface. Toggles call <paramref name="render"/> with their state.</summary>
    private void WireActivation(ControlDefinition control, Pressable surface, FrameworkElement pulseTarget, Action<bool> render)
    {
        Motion.CenterScale(pulseTarget);
        if (control.Type == ControlType.Toggle)
        {
            var on = false;
            render(false);
            surface.Click += async (_, _) =>
            {
                on = !on;
                render(on);
                if (on) Motion.Pulse(pulseTarget);
                await RunToggleAsync(control, on);
            };
            if (control.State is not null)
            {
                _refreshers.Add(async ct =>
                {
                    var value = await QueryAsync(control.State, ct);
                    if (value is null) return;
                    on = IsOn(control, value);
                    render(on);
                });
            }
        }
        else
        {
            render(false);
            surface.Click += async (_, _) =>
            {
                Motion.Pulse(pulseTarget, 1.12f);
                await RunButtonAsync(control, surface);
            };
        }
    }

    private PillSlider BuildSlider(ControlDefinition control, double height, string label)
    {
        var slider = new PillSlider(control.Icon ?? Widget.Icon, label, control.Format ?? "{value}", double.IsNaN(height) ? 72 : height);
        if (double.IsNaN(height)) slider.ClearValue(HeightProperty);
        slider.Configure(control.Min ?? 0, control.Max ?? 100, control.Step ?? 1);

        var fast = control.Action?["type"]?.ToString() is { } t && (t.StartsWith("audio.", StringComparison.Ordinal) || t == "display.setBrightness");
        var running = false;
        var dirty = false;
        double pending = 0;
        var lastUser = DateTime.MinValue;
        slider.ValueChanged += async (value, final) =>
        {
            if (control.Action is null) return;
            lastUser = DateTime.UtcNow;
            pending = value;
            dirty = true;
            if (running) return;
            running = true;
            try
            {
                while (dirty)
                {
                    dirty = false;
                    if (!fast)
                    {
                        await Task.Delay(final ? 0 : 280);
                        if (dirty) continue;
                    }
                    await RunAsync(control.Action, ValueText.FormatNumber(pending), refreshAfter: false);
                    if (fast) await Task.Delay(40);
                }
            }
            finally
            {
                running = false;
            }
        };
        if (control.State is not null)
        {
            _refreshers.Add(async ct =>
            {
                if (running || slider.IsDragging || (DateTime.UtcNow - lastUser).TotalSeconds < 1.5) return;
                var value = await QueryAsync(control.State, ct);
                if (value is not null && double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    slider.SetValue(d);
            });
        }
        return slider;
    }

    private SegmentedPill BuildSegmented(ControlDefinition control, double height)
    {
        var options = control.Options ?? [];
        var seg = new SegmentedPill(options.Select(o => (o.Label, (string?)o.Icon)).ToList(), height);
        seg.Selected += async index =>
        {
            var option = options[index];
            if (option.Action is not null) await RunAsync(option.Action, option.EffectiveValue);
        };
        if (control.State is not null)
        {
            _refreshers.Add(async ct =>
            {
                var value = await QueryAsync(control.State, ct);
                if (value is null) return;
                seg.SetSelected(options.FindIndex(o => ValueText.Matches(o.EffectiveValue, value)));
            });
        }
        _segmenteds.Add(seg);
        return seg;
    }

    private Border TileSurface(Thickness padding) => new()
    {
        Background = Palette.Current.Brush(Palette.Current.Tile),
        CornerRadius = new CornerRadius(Radius),
        Padding = padding,
    };

    /// <summary>One-row choice: icon + title on the left and the segments on the right, or a label over them when narrow.</summary>
    private UIElement ChoiceBar(ControlDefinition control)
    {
        var p = Palette.Current;
        var surface = TileSurface(new Thickness(L.CardPaddingX, 0, 12, 0));
        var grid = new Grid { ColumnSpacing = 12 };
        var wide = Cells.W >= 3;
        var seg = BuildSegmented(control, wide ? 44 : 36);
        if (wide)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 140 };
            var icon = Glyph.Make(Widget.Icon, 22, p.Text1);
            head.Children.Add(icon);
            var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = Ui.TipWhenTrimmed(Ui.Text(LabelOf(control), 13.5, p.Text1, semibold: true));
            title.MaxWidth = 108;
            titles.Children.Add(title);
            if (!string.IsNullOrWhiteSpace(Widget.Subtitle))
            {
                var sub = Ui.TipWhenTrimmed(Ui.Text(Widget.Subtitle!, 11, p.Text3));
                sub.MaxWidth = 108;
                titles.Children.Add(sub);
            }
            head.Children.Add(titles);
            grid.Children.Add(head);
            seg.VerticalAlignment = VerticalAlignment.Center;
            SetColumn(seg, 1);
            grid.Children.Add(seg);
            _busyTarget = icon;
        }
        else
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.VerticalAlignment = VerticalAlignment.Center;
            grid.RowSpacing = 4;
            var title = Ui.TipWhenTrimmed(Ui.Text(LabelOf(control), 11.5, p.Text2, semibold: true));
            grid.Children.Add(title);
            SetRow(seg, 1);
            grid.Children.Add(seg);
            _busyTarget = title;
        }
        surface.Child = grid;
        return surface;
    }

    /// <summary>One-row status: value up front, label under it.</summary>
    private UIElement StatusTile(ControlDefinition control)
    {
        var p = Palette.Current;
        var compact = Cells.W == 1;
        var surface = TileSurface(new Thickness(compact ? 8 : L.CardPaddingX, 0, compact ? 8 : L.CardPaddingX, 0));
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var circle = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = p.Brush(p.AccentSoft), VerticalAlignment = VerticalAlignment.Center };
        var icon = Glyph.Make(control.Icon ?? Widget.Icon, 20, p.Accent);
        circle.Child = icon;
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var value = Ui.TipWhenTrimmed(Ui.Text("—", compact ? 16 : 19, p.Text1, semibold: true));
        var label = Ui.TipWhenTrimmed(Ui.Text(LabelOf(control), 11.5, p.Text2));
        texts.Children.Add(value);
        texts.Children.Add(label);
        if (!compact) grid.Children.Add(circle);
        SetColumn(texts, compact ? 0 : 1);
        if (compact)
        {
            texts.HorizontalAlignment = HorizontalAlignment.Center;
            value.HorizontalAlignment = label.HorizontalAlignment = HorizontalAlignment.Center;
            SetColumnSpan(texts, 2);
        }
        grid.Children.Add(texts);
        surface.Child = grid;
        _busyTarget = compact ? value : icon;
        if (control.State is not null)
        {
            _refreshers.Add(async ct =>
            {
                var result = await QueryAsync(control.State, ct);
                if (result is null) return;
                var text = Format(control.Format ?? "{value}", Ui.FriendlyValue(result));
                if (text != value.Text)
                {
                    value.Text = text;
                    Motion.Enter(value, 4, 0, Motion.Fast);
                }
            });
        }
        return surface;
    }

    /// <summary>Several controls: a card with a header and one row per control (consecutive buttons share a row).</summary>
    private UIElement Card()
    {
        var p = Palette.Current;
        var card = new Border
        {
            Background = p.Brush(p.Card),
            BorderBrush = p.Brush(p.Hairline),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius + 2),
            Padding = new Thickness(L.CardPaddingX, L.CardPaddingTop - 1, L.CardPaddingX, L.CardPaddingBottom - 1),
        };
        var stack = new StackPanel { Spacing = L.RowGap, VerticalAlignment = VerticalAlignment.Top };
        var header = new Grid { ColumnSpacing = 10, Height = string.IsNullOrWhiteSpace(Widget.Subtitle) ? L.CardHeader : L.CardHeaderWithSubtitle };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Glyph.Make(Widget.Icon, 18, p.Accent);
        icon.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Ui.TipWhenTrimmed(Ui.Text(Widget.Title, 13.5, p.Text1, semibold: true)));
        if (!string.IsNullOrWhiteSpace(Widget.Subtitle)) titles.Children.Add(Ui.TipWhenTrimmed(Ui.Text(Widget.Subtitle!, 11.5, p.Text2)));
        SetColumn(titles, 1);
        header.Children.Add(titles);
        stack.Children.Add(header);
        _busyTarget = icon;

        foreach (var row in L.CardRows(Widget))
        {
            var control = row[0];
            switch (control.Type)
            {
                case ControlType.Button:
                    stack.Children.Add(ButtonRow(row));
                    break;
                case ControlType.Toggle:
                    stack.Children.Add(ToggleRow(control));
                    break;
                case ControlType.Slider:
                    var slider = BuildSlider(control, L.SliderRow, control.Label ?? "");
                    _slider ??= slider;
                    stack.Children.Add(slider);
                    break;
                case ControlType.Choice:
                    var choice = new StackPanel { Spacing = 4 };
                    if (L.HasOwnLabel(control, Widget))
                    {
                        var label = Ui.TipWhenTrimmed(Ui.Text(control.Label!, 11.5, p.Text2, semibold: true));
                        label.Height = L.ChoiceLabel;
                        choice.Children.Add(label);
                    }
                    choice.Children.Add(BuildSegmented(control, L.ChoiceRow));
                    stack.Children.Add(choice);
                    break;
                default:
                    stack.Children.Add(StatusRow(control));
                    break;
            }
        }
        card.Child = stack;
        return card;
    }

    private Grid ButtonRow(IReadOnlyList<ControlDefinition> buttons)
    {
        var p = Palette.Current;
        var row = new Grid { ColumnSpacing = 8, Height = L.ButtonRow };
        for (var i = 0; i < buttons.Count; i++)
        {
            var control = buttons[i];
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var accent = string.Equals(control.Style, "accent", StringComparison.OrdinalIgnoreCase);
            var pill = Surface(accent ? p.Accent : p.AccentSoft);
            pill.CornerRadius = new CornerRadius(L.ButtonRow / 2);
            var fg = accent ? p.OnAccent : (p.IsDark ? Palette.Parse("#D3E1F0") : p.Accent);
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
            TextBlock? glyph = null;
            if (!string.IsNullOrWhiteSpace(control.Icon))
            {
                glyph = Glyph.Make(control.Icon, 18, fg);
                content.Children.Add(glyph);
            }
            if (!string.IsNullOrWhiteSpace(control.Label))
            {
                var text = Ui.TipWhenTrimmed(Ui.Text(control.Label!, 12.5, fg, semibold: true));
                text.VerticalAlignment = VerticalAlignment.Center;
                content.Children.Add(text);
            }
            pill.SetContent(content);
            pill.AccessibleName = control.Label ?? control.Icon;
            ToolTipService.SetToolTip(pill, control.Label ?? control.Icon);
            var target = (FrameworkElement?)glyph ?? content;
            Motion.CenterScale(target);
            pill.Click += async (_, _) =>
            {
                Motion.Pulse(target, 1.12f);
                await RunButtonAsync(control, pill);
            };
            SetColumn(pill, i);
            row.Children.Add(pill);
        }
        return row;
    }

    private UIElement ToggleRow(ControlDefinition control)
    {
        var p = Palette.Current;
        var row = new Grid { Height = L.ToggleRow, ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Ui.TipWhenTrimmed(Ui.Text(control.Label ?? "", 13, p.Text1));
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        var toggle = new PillSwitch { VerticalAlignment = VerticalAlignment.Center };
        _switches.Add(toggle);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, control.Label ?? Widget.Title);
        toggle.Toggled += async on => await RunToggleAsync(control, on);
        SetColumn(toggle, 1);
        row.Children.Add(toggle);
        if (control.State is not null)
        {
            _refreshers.Add(async ct =>
            {
                var value = await QueryAsync(control.State, ct);
                if (value is not null) toggle.IsOn = IsOn(control, value);
            });
        }
        return row;
    }

    private UIElement StatusRow(ControlDefinition control)
    {
        var p = Palette.Current;
        var row = new Grid { Height = L.StatusRow, ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Ui.TipWhenTrimmed(Ui.Text(control.Label ?? "", 12.5, p.Text2));
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        var value = Ui.TipWhenTrimmed(Ui.Text("—", 13.5, p.Text1, semibold: true));
        value.VerticalAlignment = VerticalAlignment.Center;
        value.TextAlignment = TextAlignment.Right;
        // the value may take up to 60% of the row; longer values are cut off with … (full text in the tooltip)
        row.SizeChanged += (_, e) => value.MaxWidth = Math.Max(40, e.NewSize.Width * 0.6);
        SetColumn(value, 1);
        row.Children.Add(value);
        if (control.State is not null)
        {
            _refreshers.Add(async ct =>
            {
                var result = await QueryAsync(control.State, ct);
                if (result is not null) value.Text = Format(control.Format ?? "{value}", Ui.FriendlyValue(result.Length > 120 ? result[..120] + "…" : result));
            });
        }
        return row;
    }

    // ── Edit mode chrome ────────────────────────────────────────────────────────────

    private Grid BuildEditOverlay()
    {
        var p = Palette.Current;
        var overlay = new Grid { Visibility = Visibility.Collapsed };
        overlay.Children.Add(new Rectangle
        {
            RadiusX = Radius,
            RadiusY = Radius,
            Stroke = p.Brush(Palette.WithAlpha(p.Accent, 0.6)),
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
            Fill = p.Brush(p.Overlay(0.04)),
            IsHitTestVisible = false,
        });

        // badges sit inside the tile so nothing is clipped at the edge of the board
        var remove = Badge("close", p.Error, Colors.White, "Remove");
        remove.HorizontalAlignment = HorizontalAlignment.Left;
        remove.VerticalAlignment = VerticalAlignment.Top;
        remove.Margin = new Thickness(5, 5, 0, 0);
        remove.Click += (_, _) => Command?.Invoke(this, TileCommand.Remove);
        overlay.Children.Add(remove);

        var ai = Badge("auto_awesome", p.Accent, p.OnAccent, "Change with AI");
        ai.HorizontalAlignment = HorizontalAlignment.Right;
        ai.VerticalAlignment = VerticalAlignment.Top;
        ai.Margin = new Thickness(0, 5, 5, 0);
        ai.Click += (_, _) => Command?.Invoke(this, TileCommand.EditWithAi);
        overlay.Children.Add(ai);

        var resize = Badge("open_in_full", p.Tile, p.Text1, "Drag to resize · click to cycle sizes");
        resize.HorizontalAlignment = HorizontalAlignment.Right;
        resize.VerticalAlignment = VerticalAlignment.Bottom;
        resize.Margin = new Thickness(0, 0, 5, 5);
        resize.BorderBrush = p.Brush(p.HairlineStrong);
        resize.BorderThickness = new Thickness(1);
        resize.PointerPressed += (_, e) => ResizeHandlePressed?.Invoke(this, e);
        overlay.Children.Add(resize);

        return overlay;
    }

    /// <summary>The long-press / right-click menu.</summary>
    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        AddMenu(menu, "Change with AI…", "auto_awesome", () => Command?.Invoke(this, TileCommand.EditWithAi));
        var resize = new MenuFlyoutSubItem { Text = "Size", Icon = MenuIcon("aspect_ratio") };
        var minimum = TileLayout.Minimum(Widget);
        foreach (var size in Sizes.Where(s => s.W >= minimum.W && s.H >= minimum.H))
        {
            var item = new RadioMenuFlyoutItem { Text = $"{size.W} × {size.H}  {SizeName(size)}", GroupName = "size-" + Widget.Id, IsChecked = size == Cells };
            var chosen = size;
            item.Click += (_, _) => ResizeRequested?.Invoke(this, chosen);
            resize.Items.Add(item);
        }
        menu.Items.Add(resize);
        AddMenu(menu, "Duplicate", "content_copy", () => Command?.Invoke(this, TileCommand.Duplicate));
        AddMenu(menu, "Edit JSON…", "data_object", () => Command?.Invoke(this, TileCommand.EditJson));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenu(menu, "Remove", "delete", () => Command?.Invoke(this, TileCommand.Remove));
        return menu;
    }

    public static string SizeName(CellSize size) => (size.W, size.H) switch
    {
        (1, 1) => "Icon",
        (2, 1) => "Tile",
        (4, 1) => "Bar",
        (2, 2) => "Square",
        (4, 2) => "Card",
        (2, 3) => "Tall",
        _ => "Large card",
    };

    private static FontIcon MenuIcon(string name) => new() { Glyph = Icons.Glyph(name), FontFamily = Glyph.Family, FontSize = 16 };

    private static void AddMenu(MenuFlyout menu, string text, string icon, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = MenuIcon(icon) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    public void ShowMenu() => ((MenuFlyout)ContextFlyout).ShowAt(this, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
    {
        Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
    });

    private static Pressable Badge(string icon, Color fill, Color fg, string tooltip)
    {
        var badge = new Pressable { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Fill = fill };
        badge.SetContent(Glyph.Make(icon, 15, fg));
        badge.AccessibleName = tooltip;
        ToolTipService.SetToolTip(badge, tooltip);
        return badge;
    }

    // ── Running actions ─────────────────────────────────────────────────────────────

    private async Task RunButtonAsync(ControlDefinition control, FrameworkElement anchor)
    {
        if (control.Action is null) return;
        if (!string.IsNullOrWhiteSpace(control.Confirm) && !await ConfirmAsync(control.Confirm!, anchor)) return;
        await RunAsync(control.Action, null);
    }

    private async Task RunToggleAsync(ControlDefinition control, bool on)
    {
        var action = control.On is not null || control.Off is not null ? (on ? control.On : control.Off) : control.Action;
        if (action is not null) await RunAsync(action, on ? "true" : "false");
    }

    private static Task<bool> ConfirmAsync(string text, FrameworkElement anchor)
    {
        var tcs = new TaskCompletionSource<bool>();
        var p = Palette.Current;
        var yes = new Chip("Continue", filled: true);
        var no = new Chip("Cancel", style: ChipStyle.Ghost);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        var body = new StackPanel { Spacing = 12, MaxWidth = 260 };
        body.Children.Add(Ui.Text(text, 13, p.Text1, maxLines: 4));
        body.Children.Add(buttons);
        var flyout = new Flyout { Content = body };
        yes.Click += (_, _) => { tcs.TrySetResult(true); flyout.Hide(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); flyout.Hide(); };
        flyout.Closed += (_, _) => tcs.TrySetResult(false);
        flyout.ShowAt(anchor);
        return tcs.Task;
    }

    public async Task RunAsync(JsonObject action, string? value, bool refreshAfter = true)
    {
        if (IsPreview) return;
        SetBusy(+1);
        try
        {
            await _host.Actions.RunAsync(action, value);
            ShowError(null);
        }
        catch (ActionException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Widget \"{Widget.Title}\" action failed", ex);
            ShowError(ex.Message);
        }
        finally
        {
            SetBusy(-1);
        }
        if (refreshAfter)
        {
            await Task.Delay(450);
            await RefreshAsync();
        }
    }

    private async Task<string?> QueryAsync(JsonObject action, CancellationToken ct)
    {
        try
        {
            return await _host.Actions.RunAsync(action, null, ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return null;
            Log.Warn($"State of \"{Widget.Title}\" couldn't be read: {ex.Message}");
            ShowError(ex.Message, raise: false);
            return null;
        }
    }

    public async Task RefreshAsync()
    {
        if (_refreshers.Count == 0 || IsPreview) return;
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        foreach (var refresh in _refreshers)
        {
            if (cts.IsCancellationRequested) return;
            try { await refresh(cts.Token); }
            catch (Exception ex) { Log.Warn($"Refreshing \"{Widget.Title}\" failed: {ex.Message}"); }
        }
    }

    public void StartPolling()
    {
        if (PollSeconds is not { } seconds || _refreshers.Count == 0) return;
        _poll ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(2, seconds)) };
        _poll.Tick -= OnPoll;
        _poll.Tick += OnPoll;
        _poll.Start();
    }

    private void OnPoll(object? sender, object e)
    {
        _pollTick = true;
        _ = RefreshAsync();
    }

    public void StopPolling()
    {
        _poll?.Stop();
        _refreshCts?.Cancel();
    }

    private void SetBusy(int delta)
    {
        var was = _busy > 0;
        _busy = Math.Max(0, _busy + delta);
        var busy = _busy > 0;
        if (was == busy || _busyTarget is null) return;
        if (busy) Motion.Breathe(_busyTarget, 0.35f); else Motion.StopBreathe(_busyTarget);
    }

    private void ShowError(string? message, bool raise = true)
    {
        _errorDot.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(_errorDot, message);
        if (message is not null && raise) Failed?.Invoke(this, message);
    }

    /// <summary>The AI (or the gallery) just added or changed this tile: an accent ring that fades, and a spring pop.</summary>
    public void Celebrate()
    {
        var p = Palette.Current;
        var glow = new Rectangle
        {
            RadiusX = Radius,
            RadiusY = Radius,
            Stroke = p.Brush(p.Accent),
            StrokeThickness = 2,
            IsHitTestVisible = false,
        };
        Children.Add(glow);
        Scale = new Vector3(0.92f, 0.92f, 1);
        DispatcherQueue.TryEnqueue(() => Scale = Vector3.One);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        glow.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            glow.Opacity = 0;
            var remove = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            remove.Tick += (_, _) => { remove.Stop(); Children.Remove(glow); };
            remove.Start();
        };
        timer.Start();
    }

    private static bool IsOn(ControlDefinition control, string value) =>
        control.OnValue is { Length: > 0 } onValue ? ValueText.Matches(onValue, value) : ValueText.IsTruthy(value);

    private static string Format(string format, string value) =>
        format.Contains("{value}", StringComparison.OrdinalIgnoreCase) ? ActionCall.Substitute(format, value) : value;
}
