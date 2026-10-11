using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace TileSmith.Theme;

/// <summary>
/// Re-skins the stock WinUI controls (settings, menus, dialogs) with TileSmith's palette: the accent ramp,
/// rounder corners and the dark DailyDash surfaces, so nothing looks like default Windows chrome.
/// </summary>
public static class DesignTokens
{
    /// <summary>Call once in OnLaunched, before any window is created.</summary>
    public static void ApplyToApp(Application app, bool dark, string accentSetting)
    {
        Palette.Apply(dark, accentSetting);
        var p = Palette.Current;
        var r = app.Resources;
        r["ControlCornerRadius"] = new CornerRadius(10);
        r["OverlayCornerRadius"] = new CornerRadius(16);

        // WinUI derives every accent brush from this ramp (dark theme uses Light1–3, light theme Dark1–3)
        var a = p.Accent;
        if (dark)
        {
            r["SystemAccentColorLight3"] = Mix(a, Microsoft.UI.Colors.White, 0.35);
            r["SystemAccentColorLight2"] = a;
            r["SystemAccentColorLight1"] = Palette.Darken(a, 0.86);
            r["SystemAccentColor"] = Palette.Darken(a, 0.72);
            r["SystemAccentColorDark1"] = Palette.Darken(a, 0.6);
            r["SystemAccentColorDark2"] = Palette.Darken(a, 0.48);
            r["SystemAccentColorDark3"] = Palette.Darken(a, 0.38);
        }
        else
        {
            r["SystemAccentColorDark3"] = Palette.Darken(a, 0.7);
            r["SystemAccentColorDark2"] = Palette.Darken(a, 0.85);
            r["SystemAccentColorDark1"] = a;
            r["SystemAccentColor"] = Mix(a, Microsoft.UI.Colors.White, 0.2);
            r["SystemAccentColorLight1"] = Mix(a, Microsoft.UI.Colors.White, 0.35);
            r["SystemAccentColorLight2"] = Mix(a, Microsoft.UI.Colors.White, 0.5);
            r["SystemAccentColorLight3"] = Mix(a, Microsoft.UI.Colors.White, 0.65);
        }
    }

    /// <summary>Surface brushes for a window built from stock controls (the Settings window).</summary>
    public static void ApplyToWindow(FrameworkElement root)
    {
        var p = Palette.Current;
        var r = root.Resources;
        r["CardBackgroundFillColorDefaultBrush"] = p.Brush(p.Card);
        r["CardBackgroundFillColorSecondaryBrush"] = p.Brush(p.Tile);
        r["CardStrokeColorDefaultBrush"] = p.Brush(p.Hairline);
        r["ExpanderHeaderBackground"] = p.Brush(p.Card);
        r["ExpanderContentBackground"] = p.Brush(p.IsDark ? Palette.Parse("#1B1B1B") : p.Canvas);
        r["SettingsCardBackground"] = p.Brush(p.Card);
        r["SettingsCardBackgroundPointerOver"] = p.Brush(p.Tile);
        r["SettingsCardBackgroundPressed"] = p.Brush(p.TileHover);
        r["SettingsCardBorderBrush"] = p.Brush(p.Hairline);
        r["NavigationViewContentBackground"] = p.Brush(p.Canvas);
        r["NavigationViewContentGridBorderBrush"] = p.Brush(p.Hairline);
        r["NavigationViewExpandedPaneBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        r["NavigationViewDefaultPaneBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        r["NavigationViewTopPaneBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        r["InfoBarInformationalSeverityBackgroundBrush"] = p.Brush(Palette.WithAlpha(p.Accent, 0.12));
        r["InfoBarSuccessSeverityBackgroundBrush"] = p.Brush(Palette.WithAlpha(p.Success, 0.14));
        r["InfoBarWarningSeverityBackgroundBrush"] = p.Brush(Palette.WithAlpha(p.Warning, 0.14));
        r["InfoBarErrorSeverityBackgroundBrush"] = p.Brush(Palette.WithAlpha(p.Error, 0.14));
        r["TextFillColorPrimaryBrush"] = p.Brush(p.Text1);
        r["TextFillColorSecondaryBrush"] = p.Brush(p.Text2);
        r["TextFillColorTertiaryBrush"] = p.Brush(p.Text3);
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        255,
        (byte)(a.R + (b.R - a.R) * t),
        (byte)(a.G + (b.G - a.G) * t),
        (byte)(a.B + (b.B - a.B) * t));
}
