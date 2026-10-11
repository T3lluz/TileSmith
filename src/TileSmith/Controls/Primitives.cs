using Microsoft.UI;
using System.Numerics;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using TileSmith.Core.Widgets;
using TileSmith.Theme;
using Windows.System;
using Windows.UI;

namespace TileSmith.Controls;

/// <summary>Material Symbols glyphs (the icon set Android uses).</summary>
public static class Glyph
{
    private static FontFamily? _family;

    public static FontFamily Family => _family ??= new FontFamily("ms-appx:///Assets/Fonts/MaterialSymbolsRounded.ttf#Material Symbols Rounded Filled");

    public static TextBlock Make(string? icon, double size, Color color) => new()
    {
        Text = Icons.Glyph(icon),
        FontFamily = Family,
        FontSize = size,
        Foreground = Palette.Current.Brush(color),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextLineBounds = TextLineBounds.Tight,
        IsHitTestVisible = false,
    };
}

public static class Ui
{
    public static TextBlock Text(string text, double size, Color color, bool semibold = false, int maxLines = 1) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = Palette.Current.Brush(color),
        FontWeight = semibold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = maxLines > 1 ? TextWrapping.WrapWholeWords : TextWrapping.NoWrap,
        MaxLines = maxLines,
        IsHitTestVisible = false,
    };
}

/// <summary>
/// A tappable surface: hover tint, press-shrink (0.97), a Material ripple from the touch point, keyboard
/// activation and an automation name. Base for tiles, chips and buttons.
/// </summary>
public class Pressable : Grid
{
    private readonly Border _hover;
    private readonly Canvas _rippleLayer;
    private bool _pressed;
    private bool _cancelled;
    private Color _fill;

    public Pressable()
    {
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        _hover = new Border { Opacity = 0, IsHitTestVisible = false };
        _rippleLayer = new Canvas { IsHitTestVisible = false };
        Children.Add(_hover);
        Children.Add(_rippleLayer);
        Motion.EnableTransitions(this);
        Motion.CenterScale(this);
        _hover.OpacityTransition = new ScalarTransition { Duration = Motion.Fast };

        PointerEntered += (_, _) => _hover.Opacity = IsEnabledForInput ? 1 : 0;
        PointerExited += (_, _) => { _hover.Opacity = 0; Release(); };
        PointerPressed += OnPressed;
        PointerReleased += OnReleased;
        PointerCaptureLost += (_, _) => Release();
        PointerCanceled += (_, _) => Release();
        KeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Enter or VirtualKey.Space && IsEnabledForInput)
            {
                e.Handled = true;
                Click?.Invoke(this, EventArgs.Empty);
            }
        };
        SizeChanged += (_, _) => ClipRipples();
        ApplyCorner();
    }

    /// <summary>Ripples are clipped to the rounded shape, not just the bounding box.</summary>
    private void ClipRipples()
    {
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(_rippleLayer);
        var c = visual.Compositor;
        var geometry = c.CreateRoundedRectangleGeometry();
        geometry.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        geometry.CornerRadius = new Vector2((float)base.CornerRadius.TopLeft);
        visual.Clip = c.CreateGeometricClip(geometry);
    }

    public event EventHandler? Click;

    public bool IsEnabledForInput { get; set; } = true;

    public Color Fill
    {
        get => _fill;
        set
        {
            _fill = value;
            Background = Palette.Current.Brush(value);
        }
    }

    public new CornerRadius CornerRadius
    {
        get => base.CornerRadius;
        set
        {
            base.CornerRadius = value;
            ApplyCorner();
        }
    }

    public string? AccessibleName
    {
        get => AutomationProperties.GetName(this);
        set => AutomationProperties.SetName(this, value);
    }

    /// <summary>Content goes underneath the hover tint and ripple.</summary>
    public void SetContent(UIElement content)
    {
        Children.Insert(0, content);
    }

    private void ApplyCorner()
    {
        if (_hover is null) return;
        _hover.CornerRadius = base.CornerRadius;
        _hover.Background = Palette.Current.Brush(Palette.Current.Overlay(Palette.Current.IsDark ? 0.06 : 0.04));
    }

    /// <summary>Used when a long-press turns into a drag: the tap must not fire.</summary>
    public void CancelPress()
    {
        _cancelled = true;
        Release();
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsEnabledForInput) return;
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        _cancelled = false;
        Scale = new Vector3(0.97f, 0.97f, 1);
        Ripple(e.GetCurrentPoint(this).Position);
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        var wasPressed = _pressed && !_cancelled;
        Release();
        if (!wasPressed || !IsEnabledForInput) return;
        var p = e.GetCurrentPoint(this).Position;
        if (p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight) Click?.Invoke(this, EventArgs.Empty);
    }

    private void Release()
    {
        _pressed = false;
        Scale = Vector3.One;
    }

    private void Ripple(Windows.Foundation.Point at)
    {
        var radius = Math.Sqrt(ActualWidth * ActualWidth + ActualHeight * ActualHeight);
        var circle = new Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            Fill = Palette.Current.Brush(Palette.Current.Overlay(0.10)),
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = new ScaleTransform { ScaleX = 0.05, ScaleY = 0.05 },
        };
        Canvas.SetLeft(circle, at.X - radius);
        Canvas.SetTop(circle, at.Y - radius);
        _rippleLayer.Children.Add(circle);

        var ease = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var sb = new Storyboard();
        foreach (var prop in new[] { "ScaleX", "ScaleY" })
        {
            var grow = new DoubleAnimation { To = 1, Duration = TimeSpan.FromMilliseconds(520), EasingFunction = ease };
            Storyboard.SetTarget(grow, circle.RenderTransform);
            Storyboard.SetTargetProperty(grow, prop);
            sb.Children.Add(grow);
        }
        var fade = new DoubleAnimation { From = 1, To = 0, BeginTime = TimeSpan.FromMilliseconds(220), Duration = TimeSpan.FromMilliseconds(420) };
        Storyboard.SetTarget(fade, circle);
        Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Completed += (_, _) => _rippleLayer.Children.Remove(circle);
        sb.Begin();
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}

/// <summary>Round icon button (header actions, close, send).</summary>
public sealed class IconButton : Pressable
{
    private readonly TextBlock _glyph;

    public IconButton(string icon, string tooltip, double size = 36, double iconSize = 20)
    {
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(size / 2);
        _glyph = Glyph.Make(icon, iconSize, Palette.Current.Text2);
        SetContent(_glyph);
        ToolTipService.SetToolTip(this, tooltip);
        AccessibleName = tooltip;
    }

    public void SetIcon(string icon) => _glyph.Text = Icons.Glyph(icon);

    public void SetColors(Color fill, Color foreground)
    {
        Fill = fill;
        _glyph.Foreground = Palette.Current.Brush(foreground);
    }
}

/// <summary>Pill chip: tonal (accent 16%) or filled. Used for suggestions, answers and small actions.</summary>
public sealed class Chip : Pressable
{
    public Chip(string text, string? icon = null, bool filled = false, double height = 32)
    {
        var p = Palette.Current;
        Height = height;
        CornerRadius = new CornerRadius(height / 2);
        Fill = filled ? p.Accent : p.AccentSoft;
        var fg = filled ? p.OnAccent : (p.IsDark ? Palette.Parse("#D3E1F0") : p.Accent);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Padding = new Thickness(icon is null ? 14 : 10, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null) row.Children.Add(Glyph.Make(icon, 16, fg));
        var label = Ui.Text(text, 12.5, fg, semibold: true);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        SetContent(row);
        AccessibleName = text;
    }
}

/// <summary>
/// Android's chunky Quick Settings slider: the whole pill is the control, the accent fill carries the icon,
/// the label and value sit on the track. Drag or click anywhere; arrows/PageUp/PageDown work too.
/// </summary>
public sealed class PillSlider : Grid
{
    private readonly Border _fill;
    private readonly TextBlock _icon;
    private readonly TextBlock _label;
    private readonly TextBlock _value;
    private readonly StackPanel _right;
    private readonly string _format;
    private double _min, _max = 100, _step = 1, _current;
    private bool _dragging;

    public PillSlider(string? icon, string label, string format, double height)
    {
        var p = Palette.Current;
        Height = height;
        CornerRadius = new CornerRadius(Math.Min(height / 2, 24));
        Background = p.Brush(p.Tile);
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        _format = format;

        _fill = new Border
        {
            Background = p.Brush(p.Accent),
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = CornerRadius,
            Width = height,
        };
        Children.Add(_fill);

        _icon = Glyph.Make(icon, 22, p.OnAccent);
        _icon.HorizontalAlignment = HorizontalAlignment.Left;
        _icon.Margin = new Thickness((height - 22) / 2, 0, 0, 0);
        Children.Add(_icon);

        _right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        _label = Ui.Text(label, 13, p.Text2);
        _value = Ui.Text("", 13, p.Text1, semibold: true);
        _right.Children.Add(_label);
        _right.Children.Add(_value);
        Children.Add(_right);

        AutomationProperties.SetName(this, label);
        PointerPressed += (_, e) =>
        {
            if (!IsEnabledForInput) return;
            CapturePointer(e.Pointer);
            _dragging = true;
            SetFromX(e.GetCurrentPoint(this).Position.X, final: false);
            e.Handled = true;
        };
        PointerMoved += (_, e) =>
        {
            if (_dragging) SetFromX(e.GetCurrentPoint(this).Position.X, final: false);
        };
        PointerReleased += (_, e) =>
        {
            if (!_dragging) return;
            _dragging = false;
            ReleasePointerCapture(e.Pointer);
            SetFromX(e.GetCurrentPoint(this).Position.X, final: true);
        };
        PointerCaptureLost += (_, _) => _dragging = false;
        KeyDown += (_, e) =>
        {
            var delta = e.Key switch
            {
                VirtualKey.Right or VirtualKey.Up => _step,
                VirtualKey.Left or VirtualKey.Down => -_step,
                VirtualKey.PageUp => (_max - _min) / 10,
                VirtualKey.PageDown => -(_max - _min) / 10,
                _ => 0,
            };
            if (delta == 0) return;
            e.Handled = true;
            SetValue(_current + delta, user: true, final: true);
        };
        SizeChanged += (_, _) => Render();
    }

    /// <summary>(value, final): final is true on release; intermediate values arrive while dragging.</summary>
    public event Action<double, bool>? ValueChanged;

    public bool IsEnabledForInput { get; set; } = true;
    public bool IsDragging => _dragging;
    public double Value => _current;

    public void Configure(double min, double max, double step)
    {
        _min = min;
        _max = max > min ? max : min + 1;
        _step = step > 0 ? step : 1;
        Render();
    }

    public void SetValue(double value, bool user = false, bool final = false)
    {
        var snapped = Math.Round((Math.Clamp(value, _min, _max) - _min) / _step) * _step + _min;
        var changed = Math.Abs(snapped - _current) > 1e-9;
        _current = snapped;
        Render();
        if (user && (changed || final)) ValueChanged?.Invoke(_current, final);
    }

    private void SetFromX(double x, bool final)
    {
        var h = ActualHeight;
        var w = Math.Max(1, ActualWidth - h);
        var fraction = Math.Clamp((x - h / 2) / w, 0, 1);
        SetValue(_min + fraction * (_max - _min), user: true, final: final);
    }

    private void Render()
    {
        if (ActualWidth <= 0) return;
        var h = ActualHeight;
        var fraction = (_current - _min) / (_max - _min);
        _fill.Width = h + fraction * Math.Max(0, ActualWidth - h);
        _fill.CornerRadius = CornerRadius;
        _value.Text = ActionFormat(_format, Core.Actions.ValueText.FormatNumber(_current));
        // once the fill reaches the label, flip the text to the on-accent colour so it stays readable
        var p = Palette.Current;
        var covered = _fill.Width > ActualWidth - _right.ActualWidth - 18 - 4;
        _label.Foreground = p.Brush(covered ? Palette.WithAlpha(p.OnAccent, 0.75) : p.Text2);
        _value.Foreground = p.Brush(covered ? p.OnAccent : p.Text1);
    }

    private static string ActionFormat(string format, string value) =>
        format.Contains("{value}", StringComparison.OrdinalIgnoreCase) ? Core.Actions.ActionCall.Substitute(format, value) : value;
}

/// <summary>Segmented selector whose accent pill glides between options.</summary>
public sealed class SegmentedPill : Grid
{
    private readonly Border _indicator;
    private readonly List<(Grid Hit, TextBlock Label, TextBlock? Icon)> _items = [];
    private int _selected = -1;

    public SegmentedPill(IReadOnlyList<(string Label, string? Icon)> options, double height = 40)
    {
        var p = Palette.Current;
        Height = height;
        CornerRadius = new CornerRadius(height / 2);
        Background = p.Brush(p.Well);
        Padding = new Thickness(3);

        _indicator = new Border
        {
            Background = p.Brush(p.Accent),
            CornerRadius = new CornerRadius((height - 6) / 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Opacity = 0,
        };
        _indicator.TranslationTransition = new Vector3Transition { Duration = Motion.Normal };
        _indicator.OpacityTransition = new ScalarTransition { Duration = Motion.Fast };
        Children.Add(_indicator);

        var row = new Grid();
        for (var i = 0; i < options.Count; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var index = i;
            var hit = new Grid { Background = new SolidColorBrush(Colors.Transparent), IsTabStop = true, UseSystemFocusVisuals = true };
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            TextBlock? icon = null;
            if (!string.IsNullOrWhiteSpace(options[i].Icon))
            {
                icon = Glyph.Make(options[i].Icon, 16, p.Text2);
                content.Children.Add(icon);
            }
            var label = Ui.Text(options[i].Label, 12.5, p.Text2, semibold: true);
            label.VerticalAlignment = VerticalAlignment.Center;
            content.Children.Add(label);
            hit.Children.Add(content);
            AutomationProperties.SetName(hit, options[i].Label);
            ToolTipService.SetToolTip(hit, options[i].Label);
            hit.PointerReleased += (_, _) => Choose(index);
            hit.KeyDown += (_, e) =>
            {
                if (e.Key is VirtualKey.Enter or VirtualKey.Space) { e.Handled = true; Choose(index); }
            };
            SetColumn(hit, i);
            row.Children.Add(hit);
            _items.Add((hit, label, icon));
        }
        Children.Add(row);
        SizeChanged += (_, _) => Layout(animate: false);
    }

    public event Action<int>? Selected;

    public bool IsEnabledForInput { get; set; } = true;

    private void Choose(int index)
    {
        if (!IsEnabledForInput) return;
        SetSelected(index);
        Selected?.Invoke(index);
    }

    public void SetSelected(int index)
    {
        _selected = index;
        Layout(animate: true);
    }

    private void Layout(bool animate)
    {
        var p = Palette.Current;
        var inner = ActualWidth - Padding.Left - Padding.Right;
        if (inner <= 0 || _items.Count == 0) return;
        var w = inner / _items.Count;
        _indicator.Width = w;
        _indicator.Height = Height - Padding.Top - Padding.Bottom;
        if (!animate) _indicator.TranslationTransition = null;
        _indicator.Translation = new Vector3((float)(Math.Max(0, _selected) * w), 0, 0);
        if (!animate) _indicator.TranslationTransition = new Vector3Transition { Duration = Motion.Normal };
        _indicator.Opacity = _selected >= 0 ? 1 : 0;
        for (var i = 0; i < _items.Count; i++)
        {
            var color = i == _selected ? p.OnAccent : p.Text2;
            _items[i].Label.Foreground = p.Brush(color);
            if (_items[i].Icon is { } icon) icon.Foreground = p.Brush(color);
        }
    }
}

/// <summary>Material 3 switch (track + thumb that grows when on).</summary>
public sealed class PillSwitch : Grid
{
    private readonly Border _thumb;
    private bool _on;

    public PillSwitch()
    {
        Width = 52;
        Height = 32;
        CornerRadius = new CornerRadius(16);
        IsTabStop = true;
        UseSystemFocusVisuals = true;
        _thumb = new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _thumb.TranslationTransition = new Vector3Transition { Duration = Motion.Normal };
        Children.Add(_thumb);
        Render();
        PointerReleased += (_, _) => Toggle();
        KeyDown += (_, e) =>
        {
            if (e.Key is VirtualKey.Enter or VirtualKey.Space) { e.Handled = true; Toggle(); }
        };
    }

    public event Action<bool>? Toggled;

    public bool IsEnabledForInput { get; set; } = true;

    public bool IsOn
    {
        get => _on;
        set
        {
            _on = value;
            Render();
        }
    }

    private void Toggle()
    {
        if (!IsEnabledForInput) return;
        IsOn = !_on;
        Toggled?.Invoke(_on);
    }

    private void Render()
    {
        var p = Palette.Current;
        Background = p.Brush(_on ? p.Accent : p.Well);
        BorderBrush = p.Brush(_on ? p.Accent : p.HairlineStrong);
        BorderThickness = new Thickness(_on ? 0 : 2);
        _thumb.Width = _thumb.Height = _on ? 24 : 16;
        _thumb.Background = p.Brush(_on ? p.OnAccent : p.Text3);
        _thumb.Translation = new Vector3(_on ? 24 : 6, 0, 0);
    }
}

/// <summary>
/// opencode's working indicator, as DailyDash draws it: eight blocks, a bright lead with a fading trail
/// sweeping out and back, resting blocks drawn small. One frame every 40 ms while visible.
/// </summary>
public sealed class WorkingScanner : StackPanel
{
    private const int Blocks = 8, HoldStart = 30, HoldEnd = 9, Trail = 6;
    private readonly List<Border> _blocks = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly double _size;
    private long _frame = Blocks - 1;

    public WorkingScanner(double blockSize = 5, double gap = 2)
    {
        _size = blockSize;
        Orientation = Orientation.Horizontal;
        Spacing = gap;
        VerticalAlignment = VerticalAlignment.Center;
        for (var i = 0; i < Blocks; i++)
        {
            var block = new Border { Width = blockSize, Height = blockSize, CornerRadius = new CornerRadius(blockSize * 0.22), VerticalAlignment = VerticalAlignment.Center };
            _blocks.Add(block);
            Children.Add(block);
        }
        _timer.Tick += (_, _) =>
        {
            _frame++;
            Draw();
        };
        Loaded += (_, _) => { if (Visibility == Visibility.Visible) _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) =>
        {
            if (Visibility == Visibility.Visible) _timer.Start(); else _timer.Stop();
        });
        Draw();
    }

    public Color Color { get; set; } = Palette.Current.Accent;

    private void Draw()
    {
        var total = Blocks + HoldEnd + (Blocks - 1) + HoldStart;
        var frame = (int)(_frame % total);
        var (active, holding, holdProgress, holdTotal, moveProgress, moveTotal, forward) = State(frame);
        var fade = holding && holdTotal > 0
            ? Math.Max(0.3, 1 - Math.Min((double)holdProgress / holdTotal, 1) * 0.7)
            : !holding && moveTotal > 0 ? 0.3 + Math.Min((double)moveProgress / Math.Max(1, moveTotal - 1), 1) * 0.7 : 1;
        for (var i = 0; i < Blocks; i++)
        {
            var distance = forward ? active - i : i - active;
            var trail = holding ? distance + holdProgress : distance is >= 0 and < Trail ? distance : -1;
            var block = _blocks[i];
            if (trail is >= 0 and < Trail)
            {
                var alpha = trail switch { 0 => 1.0, 1 => 0.9, _ => Math.Pow(0.65, trail - 1) };
                block.Width = block.Height = _size;
                block.Background = Palette.Current.Brush(Palette.WithAlpha(Color, alpha));
            }
            else
            {
                block.Width = block.Height = _size * 0.42;
                block.Background = Palette.Current.Brush(Palette.WithAlpha(Color, 0.6 * fade));
            }
        }
    }

    private static (int, bool, int, int, int, int, bool) State(int frame)
    {
        const int back = Blocks - 1;
        if (frame < Blocks) return (frame, false, 0, 0, frame, Blocks, true);
        if (frame < Blocks + HoldEnd) return (Blocks - 1, true, frame - Blocks, HoldEnd, 0, 0, true);
        if (frame < Blocks + HoldEnd + back)
        {
            var b = frame - Blocks - HoldEnd;
            return (Blocks - 2 - b, false, 0, 0, b, back, false);
        }
        return (0, true, frame - Blocks - HoldEnd - back, HoldStart, 0, 0, false);
    }
}

/// <summary>Status dot with an optional "live" ping halo.</summary>
public sealed class LiveDot : Grid
{
    private readonly Ellipse _dot;
    private readonly Ellipse _halo;

    public LiveDot(double size = 8)
    {
        Width = Height = size;
        _halo = new Ellipse { Width = size, Height = size, Opacity = 0, IsHitTestVisible = false };
        _dot = new Ellipse { Width = size, Height = size, IsHitTestVisible = false };
        Children.Add(_halo);
        Children.Add(_dot);
        VerticalAlignment = VerticalAlignment.Center;
    }

    public void Set(Color color, bool live)
    {
        _dot.Fill = Palette.Current.Brush(color);
        _halo.Fill = Palette.Current.Brush(color);
        if (live) Motion.Ping(_halo); else Motion.StopPing(_halo);
    }
}
