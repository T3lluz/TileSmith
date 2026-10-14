using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace TileSmith.Controls;

/// <summary>
/// The Liquid Glass backdrop: Windows acrylic tuned to be clear rather than milky (low tint, light luminosity layer),
/// so the wallpaper shows through blurred and with its colours intact. It stays rendered when the window isn't
/// focused, so the panel never flashes to a flat colour while it animates in or shows a dialog.
/// </summary>
public sealed class GlassBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private FrameworkElement? _root;

    public static bool IsSupported => DesktopAcrylicController.IsSupported();

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _configuration = new SystemBackdropConfiguration { IsInputActive = true };
        _root = xamlRoot.Content as FrameworkElement;
        if (_root is not null)
        {
            _root.ActualThemeChanged += OnThemeChanged;
            ApplyTheme();
        }
        _controller = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
        ApplyTint();
        _controller.AddSystemBackdropTarget(connectedTarget);
        _controller.SetSystemBackdropConfiguration(_configuration);
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        if (_root is not null) _root.ActualThemeChanged -= OnThemeChanged;
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;
    }

    private void OnThemeChanged(FrameworkElement sender, object args)
    {
        ApplyTheme();
        ApplyTint();
    }

    private void ApplyTheme()
    {
        if (_configuration is null || _root is null) return;
        _configuration.Theme = _root.ActualTheme == ElementTheme.Light ? SystemBackdropTheme.Light : SystemBackdropTheme.Dark;
    }

    private void ApplyTint()
    {
        if (_controller is null) return;
        var light = _root?.ActualTheme == ElementTheme.Light;
        _controller.TintColor = light ? Windows.UI.Color.FromArgb(255, 246, 246, 250) : Windows.UI.Color.FromArgb(255, 16, 16, 20);
        _controller.TintOpacity = light ? 0.12f : 0.18f;
        _controller.LuminosityOpacity = light ? 0.55f : 0.3f;
        _controller.FallbackColor = light ? Windows.UI.Color.FromArgb(255, 242, 242, 247) : Windows.UI.Color.FromArgb(255, 28, 28, 30);
    }
}
