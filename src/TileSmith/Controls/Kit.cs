using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileSmith.Theme;
using Windows.UI;

namespace TileSmith.Controls;

/// <summary>
/// TileSmith's building blocks for forms and settings: rounded cards with rows, fields, selects, badges and
/// banners in the DailyDash palette — so nothing looks like stock Windows chrome.
/// </summary>
public static class Kit
{
    // ── Text ─────────────────────────────────────────────────────────────────────────

    public static TextBlock Title(string text, double size = 28)
    {
        var t = Ui.Text(text, size, Palette.Current.Text1, semibold: true);
        t.FontFamily = new FontFamily("Segoe UI Variable Display");
        return t;
    }

    public static TextBlock Body(string text, double size = 13, Color? color = null, int maxLines = 4)
    {
        var t = Ui.Text(text, size, color ?? Palette.Current.Text2, maxLines: maxLines);
        t.TextWrapping = TextWrapping.WrapWholeWords;
        return t;
    }

    /// <summary>Small caps section label above a card.</summary>
    public static FrameworkElement Section(string title, string? subtitle = null)
    {
        var p = Palette.Current;
        var stack = new StackPanel { Spacing = 2, Margin = new Thickness(4, 22, 4, 8) };
        var label = Ui.Text(title.ToUpperInvariant(), 11.5, p.Text2, semibold: true);
        label.CharacterSpacing = 80;
        stack.Children.Add(label);
        if (subtitle is not null) stack.Children.Add(Body(subtitle, 12.5, p.Text3));
        return stack;
    }

    // ── Containers ───────────────────────────────────────────────────────────────────

    /// <summary>A rounded card; rows are separated by hairlines.</summary>
    public static Border Card(params UIElement?[] rows)
    {
        var p = Palette.Current;
        var stack = new StackPanel();
        var first = true;
        foreach (var row in rows)
        {
            if (row is null) continue;
            if (!first) stack.Children.Add(new Border { Height = 1, Background = p.Brush(p.Hairline), Margin = new Thickness(16, 0, 16, 0) });
            stack.Children.Add(row);
            first = false;
        }
        return new Border
        {
            Background = p.Brush(p.Card),
            BorderBrush = p.Brush(p.Hairline),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Child = stack,
        };
    }

    /// <summary>
    /// A settings row: icon tile, title and description, and a control on the right. When the row gets narrow the
    /// control moves under the text instead of squeezing it.
    /// </summary>
    public static Grid Row(string icon, string title, string? description, UIElement? trailing = null, Color? iconTint = null, double stackBelow = 520)
    {
        var p = Palette.Current;
        var grid = new Grid { Padding = new Thickness(16, 14, 16, 14), ColumnSpacing = 14, RowSpacing = 12, MinHeight = 64 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var tint = iconTint ?? p.Accent;
        var iconTile = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(11),
            Background = p.Brush(Palette.WithAlpha(tint, 0.14)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Glyph.Make(icon, 19, tint),
        };
        grid.Children.Add(iconTile);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        texts.Children.Add(Ui.Text(title, 14, p.Text1, semibold: true, maxLines: 2));
        if (!string.IsNullOrWhiteSpace(description)) texts.Children.Add(Body(description!, 12.5, p.Text2, maxLines: 4));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        if (trailing is FrameworkElement control)
        {
            control.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(control, 2);
            grid.Children.Add(control);
            grid.SizeChanged += (_, e) =>
            {
                var narrow = e.NewSize.Width < stackBelow;
                Grid.SetRow(control, narrow ? 1 : 0);
                Grid.SetColumn(control, narrow ? 1 : 2);
                Grid.SetColumnSpan(control, narrow ? 2 : 1);
                control.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            };
        }
        return grid;
    }

    /// <summary>A row of controls (buttons, chips) that wraps.</summary>
    public static WrapPanel Actions(params UIElement[] items)
    {
        var wrap = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        foreach (var i in items) wrap.Children.Add(i);
        return wrap;
    }

    /// <summary>Free-form content inside a card with the standard padding.</summary>
    public static Border Padded(UIElement content, double vertical = 14) => new() { Padding = new Thickness(16, vertical, 16, vertical), Child = content };

    // ── Controls ─────────────────────────────────────────────────────────────────────

    public static Chip Button(string text, string? icon = null, ChipStyle style = ChipStyle.Tonal, Action? onClick = null)
    {
        var chip = new Chip(text, icon, height: 36, style: style);
        if (onClick is not null) chip.Click += (_, _) => onClick();
        return chip;
    }

    public static PillSwitch Toggle(bool on, Action<bool> changed)
    {
        var toggle = new PillSwitch { IsOn = on };
        toggle.Toggled += changed;
        return toggle;
    }

    /// <summary>A dropdown: the current choice in a pill; a menu of options on click.</summary>
    public static Chip Select(IReadOnlyList<(string Id, string Label)> options, string selected, Action<string> changed, double minWidth = 180)
    {
        var current = options.FirstOrDefault(o => o.Id == selected);
        var chip = new Chip(current.Label ?? options.FirstOrDefault().Label ?? "", height: 36, style: ChipStyle.Outline, trailingIcon: "expand_more") { MinWidth = minWidth };
        chip.Click += (_, _) =>
        {
            var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
            foreach (var (id, label) in options)
            {
                var item = new RadioMenuFlyoutItem { Text = label, GroupName = "select", IsChecked = id == selected };
                item.Click += (_, _) =>
                {
                    selected = id;
                    chip.Text = label;
                    changed(id);
                };
                menu.Items.Add(item);
            }
            menu.ShowAt(chip);
        };
        return chip;
    }

    public static SegmentedPill Segmented(IReadOnlyList<(string Id, string Label)> options, string selected, Action<string> changed, double width = 300, double height = 36)
    {
        var seg = new SegmentedPill(options.Select(o => (o.Label, (string?)null)).ToList(), height) { Width = width };
        seg.SetSelected(Math.Max(0, options.ToList().FindIndex(o => o.Id == selected)));
        seg.Selected += i => changed(options[i].Id);
        return seg;
    }

    /// <summary>A text field: rounded well, accent border while focused. <paramref name="commit"/> runs on Enter and on leaving the field.</summary>
    public static Border TextField(string placeholder, string value, Action<string>? commit, out TextBox box, double width = 280, bool mono = false)
    {
        var p = Palette.Current;
        var tb = new TextBox
        {
            Text = value,
            PlaceholderText = placeholder,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 8),
            FontSize = 13,
            IsSpellCheckEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (mono) tb.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        Transparent(tb);
        var border = Field(tb, width);
        if (commit is not null)
        {
            var last = value;
            void Commit()
            {
                if (tb.Text == last) return;
                last = tb.Text;
                commit(tb.Text.Trim());
            }
            tb.LostFocus += (_, _) => Commit();
            tb.KeyDown += (_, e) =>
            {
                if (e.Key != Windows.System.VirtualKey.Enter) return;
                e.Handled = true;
                Commit();
            };
        }
        box = tb;
        return border;
    }

    public static Border TextField(string placeholder, string value, Action<string>? commit, double width = 280, bool mono = false) =>
        TextField(placeholder, value, commit, out _, width, mono);

    public static Border PasswordField(string placeholder, string value, Action<string> changed, out PasswordBox box, double width = 280)
    {
        var pb = new PasswordBox
        {
            Password = value,
            PlaceholderText = placeholder,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(12, 8, 12, 8),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Transparent(pb);
        pb.PasswordChanged += (_, _) => changed(pb.Password);
        box = pb;
        return Field(pb, width);
    }

    public static TextBox SearchBox(string placeholder)
    {
        var p = Palette.Current;
        var tb = new TextBox
        {
            PlaceholderText = placeholder,
            Padding = new Thickness(12, 8, 12, 8),
            FontSize = 13,
            IsSpellCheckEnabled = false,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
        };
        Transparent(tb);
        // a resource key can only be set once per dictionary: replace the transparent ones
        void Set(string key, object value)
        {
            tb.Resources.Remove(key);
            tb.Resources[key] = value;
        }
        Set("TextControlBackground", p.Brush(p.Well));
        Set("TextControlBackgroundPointerOver", p.Brush(p.Well));
        Set("TextControlBackgroundFocused", p.Brush(p.Well));
        Set("TextControlBorderBrush", p.Brush(p.Hairline));
        Set("TextControlBorderBrushPointerOver", p.Brush(p.HairlineStrong));
        Set("TextControlBorderBrushFocused", p.Brush(p.Accent));
        return tb;
    }

    private static Border Field(Control input, double width)
    {
        var p = Palette.Current;
        var border = new Border
        {
            Width = width,
            MinHeight = 38,
            CornerRadius = new CornerRadius(12),
            Background = p.Brush(p.Well),
            BorderBrush = p.Brush(p.Hairline),
            BorderThickness = new Thickness(1),
            Child = input,
            BackgroundTransition = new BrushTransition { Duration = Motion.Fast },
        };
        input.GotFocus += (_, _) => border.BorderBrush = p.Brush(p.Accent);
        input.LostFocus += (_, _) => border.BorderBrush = p.Brush(p.Hairline);
        border.PointerEntered += (_, _) => { if (input.FocusState == FocusState.Unfocused) border.BorderBrush = p.Brush(p.HairlineStrong); };
        border.PointerExited += (_, _) => { if (input.FocusState == FocusState.Unfocused) border.BorderBrush = p.Brush(p.Hairline); };
        return border;
    }

    /// <summary>Strips the stock text-box chrome so it can sit inside TileSmith's own field.</summary>
    public static void Transparent(Control box)
    {
        var p = Palette.Current;
        var transparent = new SolidColorBrush(Colors.Transparent);
        foreach (var key in new[] { "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused", "TextControlBackgroundDisabled",
                                    "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused", "TextControlBorderBrushDisabled" })
            box.Resources[key] = transparent;
        foreach (var key in new[] { "TextControlForeground", "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            box.Resources[key] = p.Brush(p.Text1);
        foreach (var key in new[] { "TextControlPlaceholderForeground", "TextControlPlaceholderForegroundPointerOver", "TextControlPlaceholderForegroundFocused" })
            box.Resources[key] = p.Brush(p.Text3);
        foreach (var key in new[] { "TextControlButtonForeground", "TextControlButtonForegroundPointerOver" })
            box.Resources[key] = p.Brush(p.Text2);
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        if (box is TextBox tb) tb.SelectionHighlightColor = p.Brush(Palette.WithAlpha(p.Accent, 0.45));
        if (box is PasswordBox pb) pb.SelectionHighlightColor = p.Brush(Palette.WithAlpha(p.Accent, 0.45));
    }

    // ── Status ───────────────────────────────────────────────────────────────────────

    public static Border Badge(string text, Color tone, string? icon = null)
    {
        var p = Palette.Current;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null) row.Children.Add(Glyph.Make(icon, 12, tone));
        var label = Ui.Text(text, 11, tone, semibold: true);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        return new Border
        {
            Height = 22,
            Padding = new Thickness(8, 0, 9, 0),
            CornerRadius = new CornerRadius(11),
            Background = p.Brush(Palette.WithAlpha(tone, 0.14)),
            Child = row,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public enum Tone { Info, Success, Warning, Error }

    public static Color ToneColor(Tone tone) => tone switch
    {
        Tone.Success => Palette.Current.Success,
        Tone.Warning => Palette.Current.Warning,
        Tone.Error => Palette.Current.Error,
        _ => Palette.Current.Accent,
    };

    /// <summary>A tinted message strip with an optional action.</summary>
    public static Grid Banner(Tone tone, string title, string? message = null, UIElement? action = null)
    {
        var p = Palette.Current;
        var color = ToneColor(tone);
        var grid = new Grid { Padding = new Thickness(14, 12, 12, 12), ColumnSpacing = 12, CornerRadius = new CornerRadius(16), Background = p.Brush(Palette.WithAlpha(color, 0.12)) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = Glyph.Make(tone switch { Tone.Success => "check_circle", Tone.Warning => "warning", Tone.Error => "error", _ => "info" }, 20, color);
        icon.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(icon);
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.Text(title, 13.5, p.Text1, semibold: true, maxLines: 2));
        if (!string.IsNullOrWhiteSpace(message))
        {
            var body = Body(message!, 12.5, p.Text2, maxLines: 6);
            body.IsTextSelectionEnabled = true;
            body.IsHitTestVisible = true;
            texts.Children.Add(body);
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (action is FrameworkElement a)
        {
            a.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(a, 2);
            grid.Children.Add(a);
        }
        return grid;
    }

    public static StackPanel Empty(string icon, string title, string? message)
    {
        var p = Palette.Current;
        var stack = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(16, 24, 16, 24) };
        var circle = new Border { Width = 48, Height = 48, CornerRadius = new CornerRadius(24), Background = p.Brush(p.AccentSoft), HorizontalAlignment = HorizontalAlignment.Center, Child = Glyph.Make(icon, 24, p.Accent) };
        stack.Children.Add(circle);
        var t = Ui.Text(title, 14, p.Text1, semibold: true);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(t);
        if (message is not null)
        {
            var m = Body(message, 12.5, p.Text2);
            m.TextAlignment = TextAlignment.Center;
            m.HorizontalAlignment = HorizontalAlignment.Center;
            m.MaxWidth = 300;
            stack.Children.Add(m);
        }
        return stack;
    }

    /// <summary>Opens a URL in the browser.</summary>
    public static Chip Link(string text, string url, string icon = "open_in_new") =>
        Button(text, icon, ChipStyle.Ghost, () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose());
}
