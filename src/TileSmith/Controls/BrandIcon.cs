using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TileSmith.Core;
using TileSmith.Core.Ai;
using TileSmith.Services;
using TileSmith.Theme;
using Windows.UI;

namespace TileSmith.Controls;

/// <summary>
/// A provider's or model maker's logo (Assets/Brands, from the LobeHub icon set). One-colour logos take the text
/// colour, so they read in light and dark; brand-colour logos keep their colours. Falls back to a Material glyph.
/// </summary>
public sealed class BrandIcon : Grid
{
    private static readonly Dictionary<string, string?> Sources = new(StringComparer.OrdinalIgnoreCase);

    public BrandIcon(string? brand, double size, string fallbackGlyph = "neurology", Color? mono = null)
    {
        Width = Height = size;
        IsHitTestVisible = false;
        var svg = Load(brand);
        if (svg is null)
        {
            Children.Add(Glyph.Make(fallbackGlyph, size * 0.86, mono ?? Palette.Current.Text2));
            return;
        }
        var color = mono ?? Palette.Current.Text1;
        svg = svg.Replace("currentColor", $"#{color.R:X2}{color.G:X2}{color.B:X2}", StringComparison.Ordinal);
        // rasterized at 3× so it stays sharp up to 300 % display scaling
        var source = new SvgImageSource { RasterizePixelWidth = size * 3, RasterizePixelHeight = size * 3 };
        Children.Add(new Image { Source = source, Width = size, Height = size, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform });
        _ = SetAsync(source, svg);
    }

    public static bool Exists(string? brand) => Load(brand) is not null;

    private static async Task SetAsync(SvgImageSource source, string svg)
    {
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(svg));
            await source.SetSourceAsync(stream.AsRandomAccessStream());
        }
        catch (Exception ex)
        {
            Log.Warn($"Logo couldn't render: {ex.Message}");
        }
    }

    private static string? Load(string? brand)
    {
        if (string.IsNullOrWhiteSpace(brand)) return null;
        lock (Sources)
        {
            if (Sources.TryGetValue(brand, out var cached)) return cached;
            string? text = null;
            try
            {
                var path = AppInfo.AssetPath(Path.Combine("Brands", brand + ".svg"));
                if (File.Exists(path)) text = File.ReadAllText(path);
            }
            catch (IOException) { }
            Sources[brand] = text;
            return text;
        }
    }
}

/// <summary>A provider's logo on a soft rounded tile (settings, picker, the panel's model chip).</summary>
public static class ProviderLogo
{
    public static FrameworkElement Make(ProviderInfo provider, double size, bool tile = true)
    {
        var p = Palette.Current;
        var brand = Brands.ForProvider(provider.Id);
        if (!BrandIcon.Exists(brand))
        {
            // custom endpoint / server command: a glyph instead of a logo
            var glyph = provider.Id switch { "compatible" => "api", "command" => "terminal", _ => "neurology" };
            return Wrap(Glyph.Make(glyph, size * 0.5, p.Accent), size, tile ? Palette.WithAlpha(p.Accent, 0.14) : Colors.Transparent);
        }
        if (!tile) return new BrandIcon(brand, size);
        return Wrap(new BrandIcon(brand, Math.Round(size * 0.58)), size, p.Overlay(p.IsDark ? 0.08 : 0.05));
    }

    private static Border Wrap(UIElement content, double size, Color fill)
    {
        var p = Palette.Current;
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size * 0.3),
            Background = p.Brush(fill),
            BorderBrush = p.Brush(p.Hairline),
            BorderThickness = new Thickness(fill.A == 0 ? 0 : 1),
            Child = content,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
    }
}

/// <summary>One model in a list: maker's logo, name, a detail line and Free / New / Reasoning badges.</summary>
public static class ModelRow
{
    public static Pressable Make(ModelInfo model, string providerId, bool selected, Action onClick, bool showId = false, string? note = null)
    {
        var p = Palette.Current;
        var row = new Pressable { CornerRadius = new CornerRadius(12), Fill = selected ? p.AccentSoft : Colors.Transparent, MinHeight = 46, AccessibleName = model.Name };
        var grid = new Grid { ColumnSpacing = 11, Padding = new Thickness(10, 6, 10, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brand = model.Brand ?? Brands.ForModel(model.Id, providerId);
        var icon = new BrandIcon(model.Id.Length == 0 ? Brands.ForProvider(providerId) : brand, 20, model.Id.Length == 0 ? "auto_awesome" : "neurology");
        icon.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(icon);

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var name = Ui.TipWhenTrimmed(Ui.Text(model.Name, 13, p.Text1, semibold: selected));
        name.MaxWidth = 260;
        nameLine.Children.Add(name);
        if (model.Free) nameLine.Children.Add(Pill("Free", p.Success));
        if (model.IsNew) nameLine.Children.Add(Pill("New", p.Accent));
        texts.Children.Add(nameLine);
        var detailText = note ?? model.Detail ?? "";
        if (showId && model.Id.Length > 0 && model.Id != model.Name) detailText = detailText.Length == 0 ? model.Id : $"{detailText} · {model.Id}";
        if (detailText.Length > 0) texts.Children.Add(Ui.TipWhenTrimmed(Ui.Text(detailText, 11.5, p.Text3)));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var trailing = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (model.Reasoning)
        {
            var thinking = Glyph.Make("psychology", 15, p.Text3);
            ToolTipService.SetToolTip(thinking, "Thinks before answering");
            thinking.IsHitTestVisible = true;
            trailing.Children.Add(thinking);
        }
        // the check keeps its space either way, so rows don't shift when the selection moves
        var check = Glyph.Make("check", 18, p.Accent);
        check.Width = 18;
        check.Opacity = selected ? 1 : 0;
        trailing.Children.Add(check);
        Grid.SetColumn(trailing, 2);
        grid.Children.Add(trailing);

        row.SetContent(grid);
        row.Click += (_, _) => onClick();
        return row;
    }

    private static Border Pill(string text, Color tone)
    {
        var p = Palette.Current;
        return new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 1, 6, 1),
            Background = p.Brush(Palette.WithAlpha(tone, 0.16)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Text(text, 10.5, tone, semibold: true),
        };
    }
}
