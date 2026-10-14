using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Controls;
using TileSmith.Services;
using TileSmith.Theme;
using Windows.UI.ViewManagement;

namespace TileSmith.Views.Settings;

public sealed class AppearancePage : SettingsPage
{
    public AppearancePage(AppHost host, SettingsWindow window)
        : base(host, window, "Look and feel", "Colours, shape and motion. Changes apply right away.")
    {
        Section("Theme", null,
            Kit.Row("dark_mode", "App theme", "Follow Windows, or pick light or dark.",
                Kit.Segmented([("system", "System"), ("light", "Light"), ("dark", "Dark")], S.Theme, id => { S.Theme = id; Save(); }, 270)),
            AccentBlock());

        Section("Panel", null,
            Kit.Row("blur_on", "Style", "Liquid Glass lets your wallpaper shine through blurred glass with bright edges; Solid is calm and opaque.",
                Kit.Segmented([("glass", "Liquid Glass"), ("solid", "Solid")], S.PanelStyle == "solid" ? "solid" : "glass", id => { S.PanelStyle = id; Save(); }, 240)),
            Kit.Row("view_column", "Width", "Automatic starts at 4 columns and widens to 5 when you add lots of widgets.",
                Kit.Segmented([("0", "Auto"), ("3", "3"), ("4", "4"), ("5", "5")], S.PanelColumns.ToString(), id => { S.PanelColumns = int.Parse(id); Save(); }, 240)),
            Kit.Row("animation", "Reduce motion", "Shorter, calmer animations: no springy tiles or cascades.",
                Kit.Toggle(S.ReduceMotion, on => { S.ReduceMotion = on; Save(); })));
    }

    private StackPanel AccentBlock()
    {
        var block = new StackPanel();
        block.Children.Add(Kit.Row("palette", "Accent colour", "Active tiles, sliders and the AI.", null));
        block.Children.Add(Swatches());
        return block;
    }

    private UIElement Swatches()
    {
        var p = Palette.Current;
        var wrap = new WrapPanel { HorizontalSpacing = 10, VerticalSpacing = 10, Margin = new Thickness(66, -4, 16, 16) };
        foreach (var (id, name, hex) in Palette.AccentChoices)
        {
            var color = id == "windows" ? WindowsAccent() : Palette.Parse(hex);
            var selected = S.Accent == id;
            var swatch = new Pressable { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Fill = color, AccessibleName = name };
            if (selected)
            {
                swatch.BorderBrush = p.Brush(p.Text1);
                swatch.BorderThickness = new Thickness(2.5);
                swatch.SetContent(Glyph.Make("check", 20, Palette.ContentOn(color)));
            }
            else if (id == "windows")
            {
                swatch.SetContent(Glyph.Make("desktop_windows", 16, Palette.ContentOn(color)));
            }
            ToolTipService.SetToolTip(swatch, name);
            swatch.Click += (_, _) =>
            {
                S.Accent = id;
                Save();
            };
            wrap.Children.Add(swatch);
        }
        return wrap;
    }

    private static Windows.UI.Color WindowsAccent()
    {
        try { return new UISettings().GetColorValue(UIColorType.Accent); }
        catch { return Colors.SteelBlue; }
    }
}
