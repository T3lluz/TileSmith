using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TileSmith.Controls;
using TileSmith.Interop;
using TileSmith.Services;
using TileSmith.Theme;
using TileSmith.Views.Settings;
using Windows.Graphics;
using WinRT.Interop;

namespace TileSmith.Views;

/// <summary>
/// Settings, drawn with TileSmith's own controls: a navigation rail whose highlight glides between pages, and pages
/// that slide in. Collapses the rail to icons when the window is narrow.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const double RailWide = 232;
    private const double RailNarrow = 72;
    private const double ItemHeight = 44;

    private readonly AppHost _host;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly List<(string Tag, Pressable Item, TextBlock Label)> _items = [];
    private Grid _rail = null!;
    private Border _indicator = null!;
    private StackPanel _itemStack = null!;
    private ScrollViewer _pageScroll = null!;
    private Grid? _updatesDot;
    private TextBlock _titleText = null!;
    private SettingsPage? _page;
    private string _tag = "ai";
    private bool? _narrow;
    private (Windows.UI.Color, bool, ElementTheme) _themeKey;

    internal static readonly string[] PageTags = ["ai", "server", "appearance", "general", "widgets", "updates", "about"];

    private static readonly (string Tag, string Icon, string Label)[] Pages =
    [
        ("ai", "auto_awesome", "AI"),
        ("server", "dns", "Server"),
        ("appearance", "palette", "Look and feel"),
        ("general", "tune", "General"),
        ("widgets", "widgets", "Widgets"),
        ("updates", "system_update_alt", "Updates"),
        ("about", "info", "About"),
    ];

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        _appWindow.SetIcon(AppInfo.AssetPath("TileSmith.ico"));

        var scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        _appWindow.Resize(new SizeInt32((int)(1060 * scale), (int)(780 * scale)));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(560 * scale);
            presenter.PreferredMinimumHeight = (int)(480 * scale);
        }
        CenterOnScreen();

        _host.Updates.Changed += UpdateBadge;
        _host.SettingsChanged += RebuildIfThemeChanged;
        Closed += (_, _) =>
        {
            _host.Updates.Changed -= UpdateBadge;
            _host.SettingsChanged -= RebuildIfThemeChanged;
            _page?.OnHidden();
        };
        Root.SizeChanged += (_, e) => ApplyWidth(e.NewSize.Width);
        Root.ActualThemeChanged += (_, _) => RebuildIfThemeChanged();
        Build();
    }

    public IntPtr Handle => _hwnd;

    internal UIElement CaptureRoot => Root;

    public void ShowPage(string? tag)
    {
        tag = tag switch
        {
            null => _tag,
            "connection" => "ai",
            _ when Pages.Any(p => p.Tag == tag) => tag,
            _ => "ai",
        };
        Navigate(tag, animate: true);
        Activate();
        Native.ForceForeground(_hwnd);
    }

    // ── Shell ───────────────────────────────────────────────────────────────────────

    private ElementTheme RequestedTheme => _host.Settings.Theme switch
    {
        "light" => ElementTheme.Light,
        "dark" => ElementTheme.Dark,
        _ => ElementTheme.Default,
    };

    private void ApplyTheme()
    {
        Root.RequestedTheme = RequestedTheme;
        Motion.Reduced = _host.Settings.ReduceMotion;
        Palette.Apply(Root.ActualTheme != ElementTheme.Light, _host.Settings.Accent);
        DesignTokens.ApplyToWindow(Root);
        _themeKey = (Palette.Current.Accent, Palette.Current.IsDark, Root.RequestedTheme);
    }

    private void RebuildIfThemeChanged()
    {
        Root.RequestedTheme = RequestedTheme;
        Palette.Apply(Root.ActualTheme != ElementTheme.Light, _host.Settings.Accent);
        if (_themeKey == (Palette.Current.Accent, Palette.Current.IsDark, Root.RequestedTheme)) return;
        var keepScroll = _pageScroll?.VerticalOffset ?? 0;
        Build();
        _pageScroll?.ChangeView(null, keepScroll, null, true);
    }

    private void Build()
    {
        ApplyTheme();
        var p = Palette.Current;
        _page?.OnHidden();
        _page = null;
        _items.Clear();
        _narrow = null;
        Root.Children.Clear();
        Root.Background = p.Brush(p.Chrome);
        Root.ColumnDefinitions.Clear();
        Root.RowDefinitions.Clear();
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RailWide) });
        Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // title bar (draggable), leaving room for the caption buttons
        var titleBar = new Grid { Padding = new Thickness(18, 0, 150, 0), Background = new SolidColorBrush(Colors.Transparent) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new Image { Width = 18, Height = 18, Source = new BitmapImage(new Uri(AppInfo.AssetPath("AppIcon64.png"))) });
        _titleText = Ui.Text("TileSmith Settings", 12.5, p.Text2);
        _titleText.VerticalAlignment = VerticalAlignment.Center;
        titleRow.Children.Add(_titleText);
        titleBar.Children.Add(titleRow);
        Grid.SetColumnSpan(titleBar, 2);
        Root.Children.Add(titleBar);
        SetTitleBar(titleBar);

        // navigation rail
        _rail = new Grid { Padding = new Thickness(10, 8, 10, 14) };
        _rail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _rail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var railItems = new Grid();
        _itemStack = new StackPanel { Spacing = 2 };
        _indicator = new Border
        {
            Width = 3,
            Height = 18,
            CornerRadius = new CornerRadius(2),
            Background = p.Brush(p.Accent),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, (ItemHeight - 18) / 2, 0, 0),
            IsHitTestVisible = false,
        };
        railItems.Children.Add(_itemStack);
        railItems.Children.Add(_indicator);
        foreach (var (tag, icon, label) in Pages) _itemStack.Children.Add(NavItem(tag, icon, label));
        _rail.Children.Add(railItems);

        var version = Ui.Text($"Version {_host.Updates.CurrentVersion}", 11.5, p.Text3);
        version.Margin = new Thickness(14, 0, 0, 0);
        Grid.SetRow(version, 1);
        _rail.Children.Add(version);
        Grid.SetRow(_rail, 1);
        Root.Children.Add(_rail);

        // the page area: a lighter canvas with a rounded top-left corner, like Windows Settings
        var pageHost = new Grid
        {
            Background = p.Brush(p.Canvas),
            CornerRadius = new CornerRadius(16, 0, 0, 0),
            BorderBrush = p.Brush(p.Hairline),
            BorderThickness = new Thickness(1, 1, 0, 0),
        };
        _pageScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(40, 28, 40, 48),
        };
        pageHost.Children.Add(_pageScroll);
        Grid.SetRow(pageHost, 1);
        Grid.SetColumn(pageHost, 1);
        Root.Children.Add(pageHost);

        UpdateBadge();
        ApplyWidth(Root.ActualWidth);
        Navigate(_tag, animate: false);
    }

    private Pressable NavItem(string tag, string icon, string label)
    {
        var p = Palette.Current;
        var item = new Pressable { Height = ItemHeight, CornerRadius = new CornerRadius(12), Fill = Colors.Transparent, AccessibleName = label };
        var row = new Grid { ColumnSpacing = 14, Padding = new Thickness(14, 0, 10, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var glyphHost = new Grid { Width = 22 };
        glyphHost.Children.Add(Glyph.Make(icon, 20, p.Text2));
        if (tag == "updates")
        {
            _updatesDot = new Grid { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = p.Brush(p.Accent), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 8, -2, 0), Visibility = Visibility.Collapsed };
            glyphHost.Children.Add(_updatesDot);
        }
        row.Children.Add(glyphHost);
        var text = Ui.Text(label, 13.5, p.Text1);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        item.SetContent(row);
        ToolTipService.SetToolTip(item, label);
        item.Click += (_, _) => Navigate(tag, animate: true);
        _items.Add((tag, item, text));
        return item;
    }

    private void ApplyWidth(double width)
    {
        if (_rail is null || width <= 0) return;
        var narrow = width < 820;
        if (narrow == _narrow) return;
        _narrow = narrow;
        Root.ColumnDefinitions[0].Width = new GridLength(narrow ? RailNarrow : RailWide);
        foreach (var (_, _, label) in _items) label.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        _rail.Children[1].Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        _pageScroll.Padding = narrow ? new Thickness(20, 20, 20, 40) : new Thickness(40, 28, 40, 48);
    }

    private void Navigate(string tag, bool animate)
    {
        var p = Palette.Current;
        var forward = Array.FindIndex(Pages, x => x.Tag == tag) >= Array.FindIndex(Pages, x => x.Tag == _tag);
        _tag = tag;
        for (var i = 0; i < _items.Count; i++)
        {
            var (itemTag, item, label) = _items[i];
            var selected = itemTag == tag;
            item.Fill = selected ? p.AccentSoft : Colors.Transparent;
            label.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            if (!selected) continue;
            // the accent bar glides to the selected item
            _indicator.TranslationTransition = animate ? new Vector3Transition { Duration = Motion.Normal } : null;
            _indicator.Translation = new Vector3(0, (float)(i * (ItemHeight + 2)), 0);
        }

        _page?.OnHidden();
        _page = tag switch
        {
            "server" => new ServerPage(_host, this),
            "appearance" => new AppearancePage(_host, this),
            "general" => new GeneralPage(_host, this),
            "widgets" => new WidgetsPage(_host, this),
            "updates" => new UpdatesPage(_host, this),
            "about" => new AboutPage(_host, this),
            _ => new AiPage(_host, this),
        };
        _page.MaxWidth = 960;
        _page.HorizontalAlignment = HorizontalAlignment.Stretch;
        _pageScroll.Content = _page;
        _pageScroll.ChangeView(null, 0, null, true);
        _titleText.Text = $"TileSmith Settings · {Pages.First(x => x.Tag == tag).Label}";
        if (animate)
        {
            var delay = 0;
            foreach (var child in _page.Children)
            {
                Motion.EnterFromSide(child, forward ? 28 : -28, Math.Min(delay, 220));
                delay += 28;
            }
        }
        _page.OnShown();
    }

    /// <summary>Goes to another page (used by links inside pages).</summary>
    public void Go(string tag) => Navigate(tag, animate: true);

    private void UpdateBadge()
    {
        if (_updatesDot is not null) _updatesDot.Visibility = _host.Updates.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = _appWindow.Size;
        _appWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }
}
