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

namespace TileSmith.Controls;

public enum TileCommand { EditWithAi, EditJson, Duplicate, Remove, CycleSize }

/// <summary>
/// One widget on the board, drawn Android-style for its cell size: 1x1 icon tile, 2x1 Quick Settings pill,
/// full-width pill slider or segmented bar, or a card for several controls. Runs the widget's actions and
/// keeps each control in sync with its state query.
/// </summary>
public sealed class WidgetTile : Grid
{
    public const double Radius = 22;

    private readonly AppHost _host;
    private readonly List<Func<CancellationToken, Task>> _refreshers = [];
    private readonly List<Pressable> _pressables = [];
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

        Background = new SolidColorBrush(Colors.Transparent); // hit-testable everywhere (drag in edit mode)
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

        Motion.EnableTransitions(this, Motion.Normal);
        Motion.CenterScale(this);
        Unloaded += (_, _) => StopPolling();
    }

    public WidgetDefinition Widget { get; }
    public CellSize Cells { get; set; }
    public bool IsPreview { get; }

    public event Action<WidgetTile, TileCommand>? Command;
    public event Action<WidgetTile, string>? Failed;
    public event Action<WidgetTile, PointerRoutedEventArgs>? ResizeHandlePressed;

    public bool IsEditMode
    {
        get => _editMode;
        set
        {
            _editMode = value;
            _editOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            foreach (var p in _pressables) p.IsEnabledForInput = !value;
            _surfaceHost.IsHitTestVisible = !value;
            if (_slider is not null) _slider.IsEnabledForInput = !value;
        }
    }

    /// <summary>A long-press became a drag: inner buttons must not fire.</summary>
    public void CancelInteraction()
    {
        foreach (var p in _pressables) p.CancelPress();
    }

    // ── Layout per size ─────────────────────────────────────────────────────────────

    private UIElement Build()
    {
        var controls = Widget.Controls;
        var single = controls.Count == 1 ? controls[0] : null;
        var p = Palette.Current;

        if (single is { Type: ControlType.Toggle or ControlType.Button })
            return Cells.W == 1 ? IconTile(single) : PillTile(single);

        if (single is { Type: ControlType.Slider } && Cells.H == 1)
        {
            _slider = BuildSlider(single, height: double.NaN, label: Widget.Title);
            _slider.CornerRadius = new CornerRadius(Radius);
            _slider.VerticalAlignment = VerticalAlignment.Stretch;
            _busyTarget = _slider;
            return _slider;
        }

        if (single is { Type: ControlType.Choice } && Cells.H == 1)
            return ChoiceBar(single);

        if (single is { Type: ControlType.Status } && Cells.H == 1)
            return StatusTile(single);

        return Card();
    }

    private Pressable Surface(Color fill)
    {
        var s = new Pressable { Fill = fill, CornerRadius = new CornerRadius(Radius) };
        _pressables.Add(s);
        return s;
    }

    /// <summary>1x1: icon on top, short label under it.</summary>
    private UIElement IconTile(ControlDefinition control)
    {
        var p = Palette.Current;
        var surface = Surface(p.Tile);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4 };
        var icon = Glyph.Make(control.Icon ?? Widget.Icon, 24, p.Text1);
        var label = Ui.Text(control.Label ?? Widget.Title, 11.5, p.Text2);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.MaxWidth = 110;
        stack.Children.Add(icon);
        stack.Children.Add(label);
        surface.SetContent(stack);
        surface.AccessibleName = control.Label ?? Widget.Title;
        ToolTipService.SetToolTip(surface, Widget.Title);
        _busyTarget = icon;
        WireActivation(control, surface, on =>
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
        var layout = new Grid { Padding = tall ? new Thickness(16) : new Thickness(14, 0, 16, 0), ColumnSpacing = 12, RowSpacing = 10 };

        var circle = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = p.Brush(p.Overlay(0.07)),
            VerticalAlignment = tall ? VerticalAlignment.Top : VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var icon = Glyph.Make(control.Icon ?? Widget.Icon, 22, p.Text1);
        circle.Child = icon;

        var texts = new StackPanel { VerticalAlignment = tall ? VerticalAlignment.Bottom : VerticalAlignment.Center, Spacing = 1 };
        var title = Ui.Text(control.Label ?? Widget.Title, 14, p.Text1, semibold: true);
        var state = Ui.Text(control.Type == ControlType.Button ? (Widget.Subtitle ?? "") : (control.OffText ?? "Off"), 12, p.Text2);
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
        surface.AccessibleName = control.Label ?? Widget.Title;
        _busyTarget = icon;
        WireActivation(control, surface, on =>
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
    private void WireActivation(ControlDefinition control, Pressable surface, Action<bool> render)
    {
        if (control.Type == ControlType.Toggle)
        {
            var on = false;
            render(false);
            surface.Click += async (_, _) =>
            {
                on = !on;
                render(on);
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
            surface.Click += async (_, _) => await RunButtonAsync(control, surface);
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

    private readonly List<SegmentedPill> _segmenteds = [];

    /// <summary>One-row choice: icon + title on the left, gliding segmented control on the right.</summary>
    private UIElement ChoiceBar(ControlDefinition control)
    {
        var p = Palette.Current;
        var surface = new Border { Background = p.Brush(p.Tile), CornerRadius = new CornerRadius(Radius), Padding = new Thickness(14, 0, 12, 0) };
        var grid = new Grid { ColumnSpacing = 12 };
        var wide = Cells.W >= 3;
        var seg = BuildSegmented(control, wide ? 44 : 38);
        if (wide)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            var icon = Glyph.Make(Widget.Icon, 22, p.Text1);
            head.Children.Add(icon);
            var title = Ui.Text(control.Label ?? Widget.Title, 13.5, p.Text1, semibold: true);
            title.VerticalAlignment = VerticalAlignment.Center;
            title.MaxWidth = 120;
            head.Children.Add(title);
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
            var title = Ui.Text(control.Label ?? Widget.Title, 11.5, p.Text2, semibold: true);
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
        var surface = new Border { Background = p.Brush(p.Tile), CornerRadius = new CornerRadius(Radius), Padding = new Thickness(14, 0, 14, 0) };
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var compact = Cells.W == 1;
        var circle = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = p.Brush(p.AccentSoft), VerticalAlignment = VerticalAlignment.Center };
        var icon = Glyph.Make(control.Icon ?? Widget.Icon, 20, p.Accent);
        circle.Child = icon;
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var value = Ui.Text("—", compact ? 17 : 19, p.Text1, semibold: true);
        var label = Ui.Text(control.Label ?? Widget.Title, 11.5, p.Text2);
        texts.Children.Add(value);
        texts.Children.Add(label);
        if (!compact) grid.Children.Add(circle);
        SetColumn(texts, compact ? 0 : 1);
        if (compact) { texts.HorizontalAlignment = HorizontalAlignment.Center; SetColumnSpan(texts, 2); }
        grid.Children.Add(texts);
        surface.Child = grid;
        _busyTarget = icon;
        if (control.State is not null)
        {
            _refreshers.Add(async ct =>
            {
                var result = await QueryAsync(control.State, ct);
                if (result is not null) value.Text = Format(control.Format ?? "{value}", result.Length > 60 ? result[..60] + "…" : result);
            });
        }
        return surface;
    }

    /// <summary>Several controls: a card with a header and one row per control.</summary>
    private UIElement Card()
    {
        var p = Palette.Current;
        var card = new Border
        {
            Background = p.Brush(p.Card),
            BorderBrush = p.Brush(p.Hairline),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radius + 2),
            Padding = new Thickness(14, 12, 14, 14),
        };
        var stack = new StackPanel { Spacing = 10 };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var icon = Glyph.Make(Widget.Icon, 20, p.Accent);
        header.Children.Add(icon);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Ui.Text(Widget.Title, 13.5, p.Text1, semibold: true));
        if (!string.IsNullOrWhiteSpace(Widget.Subtitle)) titles.Children.Add(Ui.Text(Widget.Subtitle!, 11.5, p.Text2));
        header.Children.Add(titles);
        stack.Children.Add(header);
        _busyTarget = icon;

        var buttons = new List<ControlDefinition>();
        void Flush()
        {
            if (buttons.Count == 0) return;
            var row = new Grid { ColumnSpacing = 8 };
            for (var i = 0; i < buttons.Count; i++)
            {
                var control = buttons[i];
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var accent = string.Equals(control.Style, "accent", StringComparison.OrdinalIgnoreCase);
                var pill = Surface(accent ? p.Accent : p.AccentSoft);
                pill.Height = 40;
                pill.CornerRadius = new CornerRadius(20);
                var fg = accent ? p.OnAccent : (p.IsDark ? Palette.Parse("#D3E1F0") : p.Accent);
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                if (!string.IsNullOrWhiteSpace(control.Icon)) content.Children.Add(Glyph.Make(control.Icon, 18, fg));
                var text = Ui.Text(control.Label ?? "", 12.5, fg, semibold: true);
                text.VerticalAlignment = VerticalAlignment.Center;
                content.Children.Add(text);
                pill.SetContent(content);
                pill.AccessibleName = control.Label;
                pill.Click += async (_, _) => await RunButtonAsync(control, pill);
                SetColumn(pill, i);
                row.Children.Add(pill);
            }
            stack.Children.Add(row);
            buttons.Clear();
        }

        foreach (var control in Widget.Controls)
        {
            if (control.Type == ControlType.Button) { buttons.Add(control); continue; }
            Flush();
            switch (control.Type)
            {
                case ControlType.Toggle:
                    stack.Children.Add(ToggleRow(control));
                    break;
                case ControlType.Slider:
                    var slider = BuildSlider(control, 48, control.Label ?? "");
                    _slider ??= slider;
                    stack.Children.Add(slider);
                    break;
                case ControlType.Choice:
                    if (!string.IsNullOrWhiteSpace(control.Label) && control.Label != Widget.Title)
                        stack.Children.Add(Ui.Text(control.Label!, 11.5, p.Text2, semibold: true));
                    stack.Children.Add(BuildSegmented(control, 40));
                    break;
                default:
                    stack.Children.Add(StatusRow(control));
                    break;
            }
        }
        Flush();
        card.Child = stack;
        return card;
    }

    private UIElement ToggleRow(ControlDefinition control)
    {
        var p = Palette.Current;
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var label = Ui.Text(control.Label ?? "", 13, p.Text1);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        var toggle = new PillSwitch();
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
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Ui.Text(control.Label ?? "", 12.5, p.Text2));
        var value = Ui.Text("—", 15, p.Text1, semibold: true);
        value.IsTextSelectionEnabled = true;
        value.IsHitTestVisible = true;
        SetColumn(value, 1);
        row.Children.Add(value);
        if (control.State is not null)
        {
            _refreshers.Add(async ct =>
            {
                var result = await QueryAsync(control.State, ct);
                if (result is not null) value.Text = Format(control.Format ?? "{value}", result.Length > 80 ? result[..80] + "…" : result);
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
            Stroke = p.Brush(p.HairlineStrong),
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
            Fill = p.Brush(p.Overlay(0.03)),
            IsHitTestVisible = false,
        });

        var remove = Badge("close", p.Error, Colors.White, "Remove");
        remove.HorizontalAlignment = HorizontalAlignment.Left;
        remove.VerticalAlignment = VerticalAlignment.Top;
        remove.Margin = new Thickness(-7, -7, 0, 0);
        remove.Click += (_, _) => Command?.Invoke(this, TileCommand.Remove);
        overlay.Children.Add(remove);

        var ai = Badge("auto_awesome", p.Accent, p.OnAccent, "Change with AI");
        ai.HorizontalAlignment = HorizontalAlignment.Right;
        ai.VerticalAlignment = VerticalAlignment.Top;
        ai.Margin = new Thickness(0, -7, -7, 0);
        ai.Click += (_, _) => Command?.Invoke(this, TileCommand.EditWithAi);
        overlay.Children.Add(ai);

        var resize = Badge("open_in_full", p.Tile, p.Text1, "Drag to resize (click to cycle sizes)");
        resize.HorizontalAlignment = HorizontalAlignment.Right;
        resize.VerticalAlignment = VerticalAlignment.Bottom;
        resize.Margin = new Thickness(0, 0, -7, -7);
        resize.BorderBrush = p.Brush(p.HairlineStrong);
        resize.BorderThickness = new Thickness(1);
        resize.Click += (_, _) => Command?.Invoke(this, TileCommand.CycleSize);
        resize.PointerPressed += (_, e) => ResizeHandlePressed?.Invoke(this, e);
        overlay.Children.Add(resize);

        var menu = new MenuFlyout();
        AddMenu(menu, "Change with AI…", TileCommand.EditWithAi);
        AddMenu(menu, "Edit JSON…", TileCommand.EditJson);
        AddMenu(menu, "Duplicate", TileCommand.Duplicate);
        AddMenu(menu, "Remove", TileCommand.Remove);
        ContextFlyout = menu;
        return overlay;
    }

    private void AddMenu(MenuFlyout menu, string text, TileCommand command)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => Command?.Invoke(this, command);
        menu.Items.Add(item);
    }

    private static Pressable Badge(string icon, Color fill, Color fg, string tooltip)
    {
        var badge = new Pressable { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Fill = fill };
        badge.SetContent(Glyph.Make(icon, 14, fg));
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
        var no = new Chip("Cancel");
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
        if (Widget.RefreshSeconds is not { } seconds || _refreshers.Count == 0) return;
        _poll ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(2, seconds)) };
        _poll.Tick -= OnPoll;
        _poll.Tick += OnPoll;
        _poll.Start();
    }

    private void OnPoll(object? sender, object e) => _ = RefreshAsync();

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

    /// <summary>The AI just added or changed this tile: a soft accent glow that fades.</summary>
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
        Scale = new Vector3(0.94f, 0.94f, 1);
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
