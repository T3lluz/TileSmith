using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TileSmith.Interop;
using TileSmith.Services;
using TileSmith.Views.Settings;
using Windows.Graphics;
using WinRT.Interop;

namespace TileSmith.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly AppHost _host;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        // same surfaces as the panel: recessed chrome around a slightly lighter canvas (DailyDash style)
        Theme.Palette.Apply(Root.ActualTheme != ElementTheme.Light, _host.Settings.Accent);
        Theme.DesignTokens.ApplyToWindow(Root);
        Root.Background = Theme.Palette.Current.Brush(Theme.Palette.Current.Chrome);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        _appWindow.SetIcon(AppInfo.AssetPath("TileSmith.ico"));
        TitleIcon.Source = new BitmapImage(new Uri(AppInfo.AssetPath("AppIcon64.png")));

        var scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        _appWindow.Resize(new SizeInt32((int)(1040 * scale), (int)(760 * scale)));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(720 * scale);
            presenter.PreferredMinimumHeight = (int)(520 * scale);
        }
        CenterOnScreen();

        _host.Updates.Changed += UpdateBadge;
        Closed += (_, _) => _host.Updates.Changed -= UpdateBadge;
        UpdateBadge();
    }

    public void ShowPage(string? tag)
    {
        tag ??= (Nav.SelectedItem as NavigationViewItem)?.Tag as string ?? "general";
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag)
                   ?? Nav.MenuItems.OfType<NavigationViewItem>().First();
        Nav.SelectedItem = item;
        Activate();
        Native.ForceForeground(_hwnd);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        PageTitle.Text = item.Content as string;
        PageHost.Content = (item.Tag as string) switch
        {
            "connection" => new ConnectionPage(_host, this),
            "widgets" => new WidgetsPage(_host, this),
            "updates" => new UpdatesPage(_host),
            "about" => new AboutPage(_host),
            _ => new GeneralPage(_host),
        };
        PageScroll.ChangeView(null, 0, null, true);
    }

    private void UpdateBadge() =>
        UpdatesBadge.Visibility = _host.Updates.HasUpdate ? Visibility.Visible : Visibility.Collapsed;

    public IntPtr Handle => _hwnd;

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = _appWindow.Size;
        _appWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }
}
