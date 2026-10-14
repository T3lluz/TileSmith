using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace TileSmith.Theme;

/// <summary>
/// Liquid Glass surfaces, after Apple's material and the ports of it to other platforms (Kyant's AndroidLiquidGlass and
/// apps such as LibrePods): the blur and colour boost come from the window's backdrop; each glass surface adds a specular rim that
/// is brightest where the light hits (top-left), a soft sheen across its top, and a highlight that follows the pointer —
/// the desktop version of the highlight that follows a phone's motion. Everything is drawn above the content and
/// ignores input, so it never changes layout or steals clicks.
/// </summary>
public static class Glass
{
    /// <summary>The bright-edged border of a glass surface.</summary>
    public static LinearGradientBrush Rim(Palette p)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        if (p.IsDark)
        {
            brush.GradientStops.Add(new GradientStop { Color = Palette.WithAlpha(Colors.White, 0.55), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Palette.WithAlpha(Colors.White, 0.12), Offset = 0.38 });
            brush.GradientStops.Add(new GradientStop { Color = Palette.WithAlpha(Colors.White, 0.05), Offset = 0.62 });
            brush.GradientStops.Add(new GradientStop { Color = Palette.WithAlpha(Colors.White, 0.24), Offset = 1 });
        }
        else
        {
            brush.GradientStops.Add(new GradientStop { Color = Palette.WithAlpha(Colors.White, 1), Offset = 0 });
            brush.GradientStops.Add(new GradientStop { Color = Palette.WithAlpha(Colors.White, 0.55), Offset = 0.45 });
            brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(18, 0, 0, 0), Offset = 0.75 });
            brush.GradientStops.Add(new GradientStop { Color = Palette.WithAlpha(Colors.White, 0.85), Offset = 1 });
        }
        return brush;
    }

    /// <summary>The glass panel's own fill: a little denser at the bottom, where the content sits.</summary>
    public static LinearGradientBrush PanelFill(Palette p)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        var tint = p.IsDark ? Palette.Parse("#121216") : Palette.Parse("#FBFBFD");
        brush.GradientStops.Add(new GradientStop { Color = Palette.WithAlpha(tint, p.IsDark ? 0.34 : 0.30), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Palette.WithAlpha(tint, p.IsDark ? 0.5 : 0.46), Offset = 1 });
        return brush;
    }

    /// <summary>
    /// The layer that makes a surface read as glass: rim, top sheen and a pointer highlight. Add it as the top child of
    /// <paramref name="host"/> (a Grid holding the surface) with the surface's corner radius.
    /// </summary>
    public static Grid Overlay(FrameworkElement host, double radius)
    {
        var p = Palette.Current;
        var layer = new Grid { IsHitTestVisible = false, CornerRadius = new CornerRadius(radius) };

        // sheen: light falling on the upper part of the glass
        var sheen = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        sheen.GradientStops.Add(new GradientStop { Color = Palette.WithAlpha(Colors.White, p.IsDark ? 0.09 : 0.35), Offset = 0 });
        sheen.GradientStops.Add(new GradientStop { Color = Palette.WithAlpha(Colors.White, 0), Offset = 0.55 });
        layer.Children.Add(new Border { Background = sheen, CornerRadius = new CornerRadius(radius) });

        // highlight under the pointer
        var glow = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.75,
            RadiusY = 0.9,
        };
        glow.GradientStops.Add(new GradientStop { Color = Palette.WithAlpha(Colors.White, p.IsDark ? 0.16 : 0.45), Offset = 0 });
        glow.GradientStops.Add(new GradientStop { Color = Palette.WithAlpha(Colors.White, 0), Offset = 1 });
        var highlight = new Border
        {
            Background = glow,
            CornerRadius = new CornerRadius(radius),
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(220) },
        };
        layer.Children.Add(highlight);

        // the rim on top of everything
        layer.Children.Add(new Border { BorderBrush = Rim(p), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(radius) });

        void Follow(PointerRoutedEventArgs e)
        {
            if (host.ActualWidth <= 0 || host.ActualHeight <= 0) return;
            var point = e.GetCurrentPoint(host).Position;
            var at = new Point(Math.Clamp(point.X / host.ActualWidth, 0, 1), Math.Clamp(point.Y / host.ActualHeight, 0, 1));
            glow.Center = at;
            glow.GradientOrigin = at;
        }
        host.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler((_, e) => { Follow(e); highlight.Opacity = 1; }), true);
        host.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) => Follow(e)), true);
        host.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler((_, _) => highlight.Opacity = 0), true);
        return layer;
    }

    /// <summary>A stand-in wallpaper for previews of glass widgets inside opaque windows (Widget Studio).</summary>
    public static LinearGradientBrush PreviewWallpaper(bool dark)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        string[] colors = dark ? ["#0D2436", "#1D4F63", "#7A4A2E"] : ["#BFD9EC", "#E4ECF3", "#F2D7B6"];
        for (var i = 0; i < colors.Length; i++)
            brush.GradientStops.Add(new GradientStop { Color = Palette.Parse(colors[i]), Offset = i / (double)(colors.Length - 1) });
        return brush;
    }
}
