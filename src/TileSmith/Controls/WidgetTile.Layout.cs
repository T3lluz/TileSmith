using System.Text.Json.Nodes;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using TileSmith.Core;
using TileSmith.Core.Widgets;
using TileSmith.Theme;
using Windows.UI;
using WL = TileSmith.Core.Widgets.WidgetLayout;

namespace TileSmith.Controls;

/// <summary>
/// Layout widgets: draws the element tree the AI composed (see <see cref="WidgetLayout"/>) with the panel's tokens,
/// keeps its "data" fresh and re-applies every placeholder and condition when a value changes.
/// </summary>
public sealed partial class WidgetTile
{
    private readonly BindingData _data = new();

    /// <summary>The palette the tile was built with: colour tokens resolve against it even when another window changed the current one.</summary>
    private readonly Palette _palette = Palette.Current;
    private List<Action> _bindings = [];
    private readonly List<(string Key, string Template)> _trackers = [];
    private readonly Dictionary<string, DateTime> _dataRunAt = new(StringComparer.OrdinalIgnoreCase);
    private bool _pollTick;

    /// <summary>How often the tile polls while the panel is open: the widget's refreshSeconds or its fastest data source.</summary>
    private int? PollSeconds
    {
        get
        {
            var all = WL.DataSources(Widget).Select(s => s.Every).OfType<int>().ToList();
            if (Widget.RefreshSeconds is { } r) all.Add(r);
            return all.Count == 0 ? null : all.Min();
        }
    }

    private UIElement LayoutTile()
    {
        var p = _palette;
        var root = Widget.Layout!;
        var surface = new Border
        {
            CornerRadius = new CornerRadius(Radius),
            Background = p.Brush(p.Tile),
        };
        var content = Element(root, parent: null, depth: 0);
        if (root["padding"] is null) content.Margin = new Thickness(WL.RootPadding);
        surface.Child = content;

        if (WL.DataSources(Widget).Count > 0) _refreshers.Insert(0, RefreshDataAsync);
        ApplyBindings();
        return surface;
    }

    private async Task RefreshDataAsync(CancellationToken ct)
    {
        var poll = _pollTick;
        _pollTick = false;
        var now = DateTime.UtcNow;
        var due = WL.DataSources(Widget).Where(s =>
        {
            if (!poll || !_dataRunAt.TryGetValue(s.Name, out var last)) return true;
            var every = s.Every ?? Widget.RefreshSeconds;
            return every is { } e && (now - last).TotalSeconds >= e - 0.5;
        }).ToList();
        if (due.Count == 0) return;
        var results = await Task.WhenAll(due.Select(async s => (s.Name, Value: await QueryAsync(s.Action, ct))));
        if (ct.IsCancellationRequested) return;
        foreach (var (name, value) in results)
        {
            _dataRunAt[name] = now;
            if (value is not null) _data.Set(name, value);
        }
        foreach (var (key, template) in _trackers)
        {
            if (Bindings.Number(template, _data) is { } n) _data.Track(key, n);
        }
        ApplyBindings();
    }

    private void ApplyBindings()
    {
        foreach (var apply in _bindings.ToArray())
        {
            try { apply(); }
            catch (Exception ex) { Log.Warn($"Layout of \"{Widget.Title}\": {ex.Message}"); }
        }
    }

    // ── Elements ────────────────────────────────────────────────────────────────────

    private FrameworkElement Element(JsonObject el, string? parent, int depth)
    {
        var type = WL.TypeOf(el) ?? "text";
        FrameworkElement view = depth > WL.MaxDepth ? new Grid() : type switch
        {
            "row" => Row(el, depth),
            "column" => Column(el, depth),
            "stack" => Stack(el, depth),
            "list" => ListElement(el, depth),
            "text" => TextElement(el),
            "icon" => IconElement(el),
            "image" => ImageElement(el, parent),
            "progress" => ProgressElement(el),
            "ring" => RingElement(el, depth),
            "sparkline" => SparklineElement(el),
            "divider" => new Border { Height = 1, Background = _palette.Brush(Tint(WL.Text(el["color"])) ?? _palette.Hairline) },
            "button" => ButtonElement(el),
            "toggle" => ToggleElement(el),
            "slider" => SliderElement(el),
            "choice" => ChoiceElement(el),
            _ => new Grid(), // spacer
        };
        if (type == "spacer" && WL.Num(el["size"]) is { } gap) view.Width = view.Height = gap;
        return Decorate(el, type, view, parent);
    }

    /// <summary>Box properties every element has: background, radius, border, padding, tap, size, alignment, visibility.</summary>
    private FrameworkElement Decorate(JsonObject el, string type, FrameworkElement view, string? parent)
    {
        var p = _palette;
        var isRoot = parent is null;
        var radius = WL.Num(el["radius"]) ?? (isRoot ? Radius : 0);
        var pad = WL.Padding(el);
        var hasBox = el["background"] is not null || el["border"] is not null || el["padding"] is not null || el["radius"] is not null;
        FrameworkElement outer = view;

        if (el["tap"] is JsonObject tap)
        {
            var press = new Pressable { CornerRadius = new CornerRadius(radius), Fill = Colors.Transparent, Padding = new Thickness(pad.L, pad.T, pad.R, pad.B) };
            press.SetContent(view);
            press.AccessibleName = WL.Text(el["tooltip"]) ?? Widget.Title;
            press.Click += async (_, _) =>
            {
                Motion.Pulse(press, 1.04f);
                await RunAsync(Bindings.ResolveAction(tap, _data), null);
            };
            _pressables.Add(press);
            outer = press;
            BindBox(el, () => press.Fill = Tint(WL.Prop(el, "background", _data)) ?? Colors.Transparent, press, null);
        }
        else if (hasBox)
        {
            var border = new Border { Child = view, CornerRadius = new CornerRadius(radius), Padding = new Thickness(pad.L, pad.T, pad.R, pad.B) };
            outer = border;
            BindBox(el, () => border.Background = el["background"] is null ? null : p.Brush(Tint(WL.Prop(el, "background", _data)) ?? Colors.Transparent), null, border);
        }

        if (WL.Text(el["tooltip"]) is { Length: > 0 } tip) ToolTipService.SetToolTip(outer, tip);
        if (WL.Num(el["width"]) is { } w) outer.Width = w;
        if (WL.Num(el["height"]) is { } h) outer.Height = h;

        outer.HorizontalAlignment = Horizontal(WL.Text(el["align"]) ?? DefaultAlign(type, parent));
        outer.VerticalAlignment = Vertical(WL.Text(el["valign"]) ?? DefaultValign(type, parent));
        if (WL.Text(el["width"]) == "fill") outer.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (WL.Text(el["height"]) == "fill") outer.VerticalAlignment = VerticalAlignment.Stretch;

        if (el["visible"] is not null || el["opacity"] is not null)
        {
            _bindings.Add(() =>
            {
                outer.Visibility = Bindings.Test(WL.Text(el["visible"]), _data) ? Visibility.Visible : Visibility.Collapsed;
                if (el["opacity"] is not null && double.TryParse(WL.Prop(el, "opacity", _data), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var o))
                    outer.Opacity = Math.Clamp(o, 0, 1);
            });
        }
        return outer;
    }

    private void BindBox(JsonObject el, Action applyBackground, Pressable? press, Border? border)
    {
        var p = _palette;
        void Apply()
        {
            applyBackground();
            if (el["border"] is null) return;
            var color = p.Brush(Tint(WL.Prop(el, "border", _data)) ?? p.HairlineStrong);
            if (press is not null) { press.BorderBrush = color; press.BorderThickness = new Thickness(1); }
            if (border is not null) { border.BorderBrush = color; border.BorderThickness = new Thickness(1); }
        }
        Apply();
        _bindings.Add(Apply);
    }

    private static string DefaultAlign(string type, string? parent) => parent switch
    {
        null => "stretch",
        "column" => type is "icon" or "image" or "ring" or "button" or "spacer" ? "start" : "stretch",
        "stack" or "ring" => type is "icon" or "text" or "ring" or "button" ? "center" : "stretch",
        _ => "stretch",
    };

    private static string DefaultValign(string type, string? parent) => parent switch
    {
        null => "stretch",
        "row" => type is "column" or "stack" or "divider" or "spacer" ? "stretch" : "center",
        "stack" or "ring" => type is "icon" or "text" or "ring" or "button" ? "center" : "stretch",
        _ => "stretch",
    };

    private static HorizontalAlignment Horizontal(string value) => value switch
    {
        "start" or "left" => HorizontalAlignment.Left,
        "center" => HorizontalAlignment.Center,
        "end" or "right" => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Stretch,
    };

    private static VerticalAlignment Vertical(string value) => value switch
    {
        "start" or "top" => VerticalAlignment.Top,
        "center" => VerticalAlignment.Center,
        "end" or "bottom" => VerticalAlignment.Bottom,
        _ => VerticalAlignment.Stretch,
    };

    /// <summary>Whether a child takes a share of the free space along its parent's direction.</summary>
    private static double? Flex(JsonObject child, string along)
    {
        if (WL.Num(child["flex"]) is { } f && f > 0) return f;
        if (WL.Text(child[along]) == "fill") return 1;
        if (WL.TypeOf(child) == "spacer" && child["size"] is null) return 1;
        return null;
    }

    private Grid Row(JsonObject el, int depth)
    {
        var grid = new Grid { ColumnSpacing = WL.Num(el["gap"]) ?? 8 };
        var i = 0;
        foreach (var child in WL.Children(el))
        {
            var flex = Flex(child, "width");
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = flex is { } f ? new GridLength(f, GridUnitType.Star) : GridLength.Auto });
            var view = Element(child, "row", depth + 1);
            Grid.SetColumn(view, i++);
            grid.Children.Add(view);
        }
        return grid;
    }

    private Grid Column(JsonObject el, int depth)
    {
        var grid = new Grid { RowSpacing = WL.Num(el["gap"]) ?? 6 };
        var i = 0;
        foreach (var child in WL.Children(el))
        {
            var flex = Flex(child, "height");
            grid.RowDefinitions.Add(new RowDefinition { Height = flex is { } f ? new GridLength(f, GridUnitType.Star) : GridLength.Auto });
            var view = Element(child, "column", depth + 1);
            Grid.SetRow(view, i++);
            grid.Children.Add(view);
        }
        return grid;
    }

    private Grid Stack(JsonObject el, int depth)
    {
        var grid = new Grid();
        foreach (var child in WL.Children(el)) grid.Children.Add(Element(child, "stack", depth + 1));
        return grid;
    }

    /// <summary>Repeats the template for each item of a JSON array value; rebuilt only when the number of items changes.</summary>
    private Grid ListElement(JsonObject el, int depth)
    {
        var p = _palette;
        var path = WL.ListPath(el) ?? "";
        var template = el["template"] as JsonObject;
        var horizontal = WL.Text(el["direction"]) == "row";
        var max = (int)Math.Clamp(WL.Num(el["max"]) ?? 5, 1, 20);
        var gap = WL.Num(el["gap"]) ?? 6;
        var host = new Grid();
        var items = new Grid { ColumnSpacing = gap, RowSpacing = gap };
        var empty = Ui.Text(WL.Text(el["empty"]) ?? "", 12, p.Text3, maxLines: 2);
        empty.Visibility = Visibility.Collapsed;
        host.Children.Add(items);
        host.Children.Add(empty);
        var itemBindings = new List<Action>();
        var shown = -1;
        _bindings.Add(() =>
        {
            var count = template is null ? 0 : Math.Min(_data.Length(path) ?? 0, max);
            if (count != shown)
            {
                shown = count;
                items.Children.Clear();
                items.RowDefinitions.Clear();
                items.ColumnDefinitions.Clear();
                itemBindings.Clear();
                var outer = _bindings;
                _bindings = itemBindings;
                try
                {
                    for (var i = 0; i < count; i++)
                    {
                        var view = Element(WL.Instantiate(template!, path, i), horizontal ? "row" : "column", depth + 1);
                        if (horizontal)
                        {
                            items.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                            Grid.SetColumn(view, i);
                        }
                        else
                        {
                            items.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                            Grid.SetRow(view, i);
                        }
                        items.Children.Add(view);
                    }
                }
                finally
                {
                    _bindings = outer;
                }
            }
            foreach (var apply in itemBindings.ToArray()) apply();
            empty.Visibility = count == 0 && empty.Text.Length > 0 && _data.Has(Bindings.SplitPath(path).Name) ? Visibility.Visible : Visibility.Collapsed;
        });
        return host;
    }

    private TextBlock TextElement(JsonObject el)
    {
        var p = _palette;
        var size = StyleTokens.FontSize(el["size"], 13) ?? 13;
        var lines = (int)Math.Clamp(WL.Num(el["maxLines"]) ?? 1, 1, 6);
        var block = Ui.TipWhenTrimmed(Ui.Text("", size, p.Text1, maxLines: lines));
        block.FontWeight = (WL.Text(el["weight"]) ?? "").ToLowerInvariant() switch
        {
            "medium" => Microsoft.UI.Text.FontWeights.Medium,
            "semibold" => Microsoft.UI.Text.FontWeights.SemiBold,
            "bold" => Microsoft.UI.Text.FontWeights.Bold,
            _ => Microsoft.UI.Text.FontWeights.Normal,
        };
        if (el["mono"] is { } mono && WL.Text(mono) == "true") block.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        block.TextAlignment = (WL.Text(el["textAlign"]) ?? WL.Text(el["align"])) switch
        {
            "center" => TextAlignment.Center,
            "end" or "right" => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        // big numbers sit on their baseline like Android's display type
        if (size >= 28) block.TextLineBounds = TextLineBounds.TrimToBaseline;
        _bindings.Add(() =>
        {
            var text = Bindings.Render(WL.Prop(el, "text", _data), _data);
            // values change in place: a dashboard that refreshes every few seconds must not twitch
            if (block.Text != text) block.Text = text;
            block.Foreground = p.Brush(Tint(WL.Prop(el, "color", _data)) ?? p.Text1);
        });
        return block;
    }

    private TextBlock IconElement(JsonObject el)
    {
        var p = _palette;
        var icon = Glyph.Make(null, WL.Num(el["size"]) ?? 20, p.Text1);
        _bindings.Add(() =>
        {
            icon.Text = Icons.Glyph(Bindings.Render(WL.Prop(el, "icon", _data), _data));
            icon.Foreground = p.Brush(Tint(WL.Prop(el, "color", _data)) ?? p.Text1);
        });
        return icon;
    }

    private Border ImageElement(JsonObject el, string? parent)
    {
        var brush = new ImageBrush
        {
            Stretch = WL.Text(el["fit"]) switch
            {
                "contain" => Stretch.Uniform,
                "fill" => Stretch.Fill,
                _ => Stretch.UniformToFill,
            },
        };
        var view = new Border { Background = brush, CornerRadius = new CornerRadius(WL.Num(el["radius"]) ?? 0), IsHitTestVisible = false };
        if (parent != "stack" && el["width"] is null && el["height"] is null) view.Width = view.Height = 48;
        string? loaded = null;
        _bindings.Add(() =>
        {
            var src = Bindings.Render(WL.Prop(el, "src", _data), _data).Trim();
            if (src == loaded) return;
            loaded = src;
            view.Visibility = src.Length == 0 || src == Bindings.Missing ? Visibility.Collapsed : Visibility.Visible;
            if (view.Visibility == Visibility.Visible) LoadImage(brush, src);
        });
        return view;
    }

    private async void LoadImage(ImageBrush brush, string src)
    {
        try
        {
            if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                brush.ImageSource = new BitmapImage(new Uri(src));
                return;
            }
            byte[] bytes;
            if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                bytes = Convert.FromBase64String(src[(src.IndexOf(',') + 1)..]);
            }
            else
            {
                var path = Environment.ExpandEnvironmentVariables(src.Trim('"'));
                if (!File.Exists(path))
                {
                    brush.ImageSource = null;
                    return;
                }
                bytes = await File.ReadAllBytesAsync(path);
            }
            var bitmap = new BitmapImage();
            using var stream = new MemoryStream(bytes);
            await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            brush.ImageSource = bitmap;
        }
        catch (Exception ex)
        {
            Log.Warn($"Image for \"{Widget.Title}\" couldn't load: {ex.Message}");
        }
    }

    private double Fraction(JsonObject el)
    {
        var value = Bindings.Number(WL.Prop(el, "value", _data), _data) ?? 0;
        var min = WL.Num(el["min"]) ?? 0;
        var max = WL.Num(el["max"]) ?? 100;
        return max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;
    }

    private Grid ProgressElement(JsonObject el)
    {
        var p = _palette;
        var thickness = WL.Num(el["thickness"]) ?? 6;
        var track = new Grid { Height = thickness, CornerRadius = new CornerRadius(thickness / 2) };
        var fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(thickness / 2), Width = 0 };
        track.Children.Add(fill);
        void Apply()
        {
            track.Background = p.Brush(Tint(WL.Prop(el, "track", _data)) ?? p.Overlay(0.1));
            fill.Background = p.Brush(Tint(WL.Prop(el, "color", _data)) ?? p.Accent);
            var fraction = Fraction(el);
            fill.Width = fraction <= 0 ? 0 : Math.Max(thickness, fraction * track.ActualWidth);
        }
        track.SizeChanged += (_, _) => Apply();
        _bindings.Add(Apply);
        return track;
    }

    private Grid RingElement(JsonObject el, int depth)
    {
        var p = _palette;
        var size = WL.Num(el["size"]) ?? 56;
        var thickness = WL.Num(el["thickness"]) ?? 6;
        var grid = new Grid { Width = size, Height = size };
        var track = new Ellipse { StrokeThickness = thickness };
        var arc = new Microsoft.UI.Xaml.Shapes.Path { StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        grid.Children.Add(track);
        grid.Children.Add(arc);
        foreach (var child in WL.Children(el)) grid.Children.Add(Element(child, "ring", depth + 1));
        _bindings.Add(() =>
        {
            track.Stroke = p.Brush(Tint(WL.Prop(el, "track", _data)) ?? p.Overlay(0.1));
            arc.Stroke = p.Brush(Tint(WL.Prop(el, "color", _data)) ?? p.Accent);
            arc.Data = Arc(size, thickness, Fraction(el));
        });
        return grid;
    }

    private static Geometry? Arc(double size, double thickness, double fraction)
    {
        if (fraction <= 0.001) return null;
        var r = (size - thickness) / 2;
        var c = size / 2;
        var angle = Math.Min(fraction, 0.9999) * 2 * Math.PI;
        var start = new Windows.Foundation.Point(c, c - r);
        var end = new Windows.Foundation.Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Windows.Foundation.Size(r, r),
            IsLargeArc = angle > Math.PI,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private Grid SparklineElement(JsonObject el)
    {
        var p = _palette;
        var template = WL.Text(el["value"]) ?? "";
        var key = "spark:" + template;
        _trackers.Add((key, template));
        var host = new Grid { Height = WL.Num(el["height"]) ?? 28, MinWidth = 40 };
        var area = new Polygon { IsHitTestVisible = false };
        var line = new Polyline { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, IsHitTestVisible = false };
        host.Children.Add(area);
        host.Children.Add(line);
        var fill = WL.Text(el["fill"]) == "true";
        void Apply()
        {
            var color = Tint(WL.Prop(el, "color", _data)) ?? p.Accent;
            line.Stroke = p.Brush(color);
            area.Fill = p.Brush(Palette.WithAlpha(color, 0.16));
            var points = (int)Math.Clamp(WL.Num(el["points"]) ?? 30, 5, BindingData.HistoryLength);
            var values = _data.History(key).TakeLast(points).ToList();
            var w = host.ActualWidth;
            var h = host.ActualHeight;
            line.Points.Clear();
            area.Points.Clear();
            if (values.Count < 2 || w <= 0 || h <= 0) return;
            var min = WL.Num(el["min"]) ?? values.Min();
            var max = WL.Num(el["max"]) ?? values.Max();
            if (max - min < 1e-9) { max += 1; min -= 1; }
            var step = w / (points - 1);
            var x0 = w - step * (values.Count - 1);
            for (var i = 0; i < values.Count; i++)
            {
                var y = 1 + (h - 2) * (1 - Math.Clamp((values[i] - min) / (max - min), 0, 1));
                line.Points.Add(new Windows.Foundation.Point(x0 + i * step, y));
            }
            if (!fill) return;
            foreach (var pt in line.Points) area.Points.Add(pt);
            area.Points.Add(new Windows.Foundation.Point(w, h));
            area.Points.Add(new Windows.Foundation.Point(x0, h));
        }
        host.SizeChanged += (_, _) => Apply();
        _bindings.Add(Apply);
        return host;
    }

    // ── Interactive elements (same behaviour as the standard controls) ─────────────

    private ControlDefinition ControlOf(JsonObject el, string type) => new()
    {
        Type = type,
        Label = WL.Text(el["label"]),
        Icon = WL.Text(el["icon"]),
        Style = WL.Text(el["variant"]) ?? WL.Text(el["style"]),
        Action = Resolve(el["action"] as JsonObject),
        On = Resolve(el["on"] as JsonObject),
        Off = Resolve(el["off"] as JsonObject),
        State = Resolve(el["state"] as JsonObject),
        OnValue = WL.Text(el["onValue"]),
        OnText = WL.Text(el["onText"]),
        OffText = WL.Text(el["offText"]),
        Min = WL.Num(el["min"]),
        Max = WL.Num(el["max"]),
        Step = WL.Num(el["step"]),
        Format = WL.Text(el["format"]),
        Confirm = WL.Text(el["confirm"]),
        Options = (el["options"] as JsonArray)?.OfType<JsonObject>().Select(o => new OptionDefinition
        {
            Label = WL.Text(o["label"]) ?? "",
            Value = WL.Text(o["value"]),
            Icon = WL.Text(o["icon"]),
            Action = Resolve(o["action"] as JsonObject),
        }).ToList(),
    };

    private JsonObject? Resolve(JsonObject? action) => action is null ? null : Bindings.ResolveAction(action, _data);

    private Pressable ButtonElement(JsonObject el)
    {
        var p = _palette;
        var control = ControlOf(el, ControlType.Button);
        var hasLabel = WL.Branches(el["label"]).Any(l => l.Length > 0);
        var variant = WL.Text(el["variant"]) ?? (hasLabel ? "tonal" : "icon");
        var pill = new Pressable { Height = 36, MinWidth = 36, CornerRadius = new CornerRadius(18) };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(hasLabel ? 14 : 0, 0, hasLabel ? 14 : 0, 0) };
        var glyph = Glyph.Make(null, 18, p.Text1);
        var text = Ui.Text("", 12.5, p.Text1, semibold: true);
        text.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(glyph);
        content.Children.Add(text);
        pill.SetContent(content);
        if (!hasLabel) pill.Width = 36;
        _pressables.Add(pill);
        Motion.CenterScale(glyph);
        pill.Click += async (_, _) =>
        {
            Motion.Pulse(glyph, 1.15f);
            var resolved = control.Action is null ? control : new ControlDefinition
            {
                Type = control.Type,
                Label = control.Label,
                Confirm = control.Confirm is null ? null : Bindings.RenderKnown(control.Confirm, _data),
                Action = Bindings.ResolveAction(control.Action, _data),
            };
            await RunButtonAsync(resolved, pill);
        };
        _bindings.Add(() =>
        {
            var custom = Tint(WL.Prop(el, "color", _data));
            var (fill, fg) = variant switch
            {
                "filled" => (custom ?? p.Accent, Palette.ContentOn(custom ?? p.Accent)),
                "outline" or "ghost" => (Colors.Transparent, custom ?? p.Text1),
                "icon" => (p.Overlay(0.07), custom ?? p.Text1),
                _ => (custom is { } c ? Palette.WithAlpha(c, 0.18) : p.AccentSoft, custom ?? (p.IsDark ? Palette.Parse("#D3E1F0") : p.Accent)),
            };
            pill.Fill = fill;
            if (variant == "outline") { pill.BorderBrush = p.Brush(p.HairlineStrong); pill.BorderThickness = new Thickness(1); }
            var icon = Bindings.Render(WL.Prop(el, "icon", _data), _data);
            var label = Bindings.Render(WL.Prop(el, "label", _data), _data);
            glyph.Text = icon.Length > 0 ? Icons.Glyph(icon) : "";
            glyph.Visibility = icon.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            glyph.Foreground = text.Foreground = p.Brush(fg);
            text.Text = label;
            text.Visibility = label.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            pill.AccessibleName = label.Length > 0 ? label : icon;
            ToolTipService.SetToolTip(pill, WL.Text(el["tooltip"]) ?? (label.Length > 0 ? label : null));
        });
        return pill;
    }

    private FrameworkElement ToggleElement(JsonObject el)
    {
        var control = ControlOf(el, ControlType.Toggle);
        switch (WL.Text(el["variant"]))
        {
            case "tile":
                return (FrameworkElement)PillTile(control);
            case "chip":
            {
                var p = _palette;
                var chip = Surface(p.Tile);
                chip.Height = 36;
                chip.CornerRadius = new CornerRadius(18);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(12, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
                var icon = Glyph.Make(control.Icon ?? Widget.Icon, 18, p.Text1);
                var label = Ui.Text(control.Label ?? "", 12.5, p.Text1, semibold: true);
                label.VerticalAlignment = VerticalAlignment.Center;
                if (!string.IsNullOrWhiteSpace(control.Icon)) row.Children.Add(icon);
                row.Children.Add(label);
                chip.SetContent(row);
                chip.AccessibleName = control.Label;
                WireActivation(control, chip, icon, on =>
                {
                    chip.Fill = on ? p.Accent : p.Overlay(0.07);
                    icon.Foreground = label.Foreground = p.Brush(on ? p.OnAccent : p.Text1);
                });
                return chip;
            }
            default:
                return (FrameworkElement)ToggleRow(control);
        }
    }

    private FrameworkElement SliderElement(JsonObject el)
    {
        var control = ControlOf(el, ControlType.Slider);
        var slider = BuildSlider(control, 40, control.Label ?? "");
        _slider ??= slider;
        return slider;
    }

    private FrameworkElement ChoiceElement(JsonObject el)
    {
        var control = ControlOf(el, ControlType.Choice);
        var stack = new StackPanel { Spacing = 4 };
        if (!string.IsNullOrWhiteSpace(control.Label))
        {
            var label = Ui.TipWhenTrimmed(Ui.Text(control.Label!, 11.5, _palette.Text2, semibold: true));
            stack.Children.Add(label);
        }
        stack.Children.Add(BuildSegmented(control, 36));
        return stack;
    }

    /// <summary>Token ("accent", "text2/60") or #hex → colour for the current theme.</summary>
    private Color? Tint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var p = _palette;
        var v = value.Trim();
        if (v.StartsWith('#'))
        {
            var hex = v[1..];
            if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
            try { return hex.Length is 6 or 8 ? Palette.Parse(hex) : null; }
            catch (FormatException) { return null; }
        }
        var alpha = 1.0;
        var slash = v.IndexOf('/');
        if (slash >= 0)
        {
            if (int.TryParse(v[(slash + 1)..], out var a)) alpha = Math.Clamp(a, 0, 100) / 100.0;
            v = v[..slash];
        }
        Color? color = v.ToLowerInvariant() switch
        {
            "accent" => p.Accent,
            "onaccent" => p.OnAccent,
            "text" => p.Text1,
            "text2" => p.Text2,
            "text3" => p.Text3,
            "surface" => p.Tile,
            "card" => p.Card,
            "well" => p.Well,
            "success" => p.Success,
            "warning" => p.Warning,
            "error" => p.Error,
            "white" => Colors.White,
            "black" => Colors.Black,
            "transparent" => Colors.Transparent,
            _ => null,
        };
        return color is { } col && alpha < 1 ? Palette.WithAlpha(col, alpha * col.A / 255.0) : color;
    }
}
