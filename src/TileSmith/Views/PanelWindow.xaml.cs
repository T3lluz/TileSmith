using System.Collections.Specialized;
using System.Globalization;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using TileSmith.Controls;
using TileSmith.Core;
using TileSmith.Core.Ai;
using TileSmith.Core.Remote;
using TileSmith.Core.Widgets;
using TileSmith.Interop;
using TileSmith.Services;
using TileSmith.Theme;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace TileSmith.Views;

/// <summary>
/// The panel: a sheet of (Liquid) glass that rises from the taskbar corner. Clock, the AI in use and quick actions up
/// top, a launcher-like grid of widgets (or the widget gallery), and a prompt at the bottom that hands requests to
/// Widget Studio, where the AI builds them with a live preview.
/// </summary>
public sealed partial class PanelWindow : Window
{
    private const double SurfacePadding = 16;
    private const double ShadowMargin = 18;
    private const double EdgeGap = 14;
    private const double WidthSlack = 6;

    private readonly AppHost _host;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly HashSet<string> _celebrate = [];
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(10) };

    // chrome
    private Grid _motionHost = null!;
    private Grid _clipHost = null!;
    private Grid _surface = null!;
    private Grid _header = null!;
    private TextBlock _clock = null!;
    private TextBlock _date = null!;
    private LiveDot _statusDot = null!;
    private TextBlock _modelText = null!;
    private Grid _modelAvatarHost = null!;
    private Pressable _modelChip = null!;
    private IconButton _pinButton = null!;
    private IconButton _editButton = null!;
    private Grid _settingsBadge = null!;
    private StackPanel _banners = null!;
    private ScrollViewer _boardScroll = null!;
    private StackPanel _boardStack = null!;
    private TileBoard _board = null!;
    private FrameworkElement _emptyState = null!;
    private Grid _editHint = null!;
    private GallerySheet? _gallery;

    // AI surface
    private Border _composer = null!;
    private TextBox _prompt = null!;
    private Grid _inputRow = null!;
    private IconButton _sendButton = null!;

    private bool _visible;
    private bool _pinned;
    private bool _editMode;
    private bool _dialogOpen;
    private bool _hiding;
    private DateTime _lastHidden = DateTime.MinValue;
    private DateTime _lastShown = DateTime.MinValue;
    private PointInt32? _anchor;
    private bool _updateDismissed;
    private bool _resizeQueued;
    private string? _styleApplied;
    private int _historyIndex = -1;
    private Flyout? _modelFlyout;
    private double _lastSurfaceHeight;
    private Vector2 _windowSizeDip;
    private bool _opensFromBottom = true;
    private bool _opensFromRight = true;
    private int _frameX, _frameY;

    public PanelWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        ConfigureWindow();

        Activated += OnActivated;
        _host.Widgets.CollectionChanged += OnWidgetsChanged;
        _host.Server.Changed += UpdateStatus;
        _host.Updates.Changed += UpdateBanners;
        _host.SettingsChanged += OnSettingsChanged;
        Root.ActualThemeChanged += (_, _) => Rebuild();
        _host.PrepareForInput = async () =>
        {
            if (!_visible) return;
            HideNow();
            await Task.Delay(180);
        };
        _clockTimer.Tick += (_, _) => UpdateClock();

        ApplyTheme();
        Rebuild();
    }

    public bool IsOpen => _visible;

    // ── Dev capture (--capture) ─────────────────────────────────────────────────────

    internal static readonly string[] CaptureStates = ["default", "edit", "gallery", "models"];

    internal UIElement CaptureRoot => Root;

    internal void PrepareCapture(string state)
    {
        Show();
        SetPinned(true);
        CloseGallery();
        _boardScroll.Content = _boardStack;
        SetEditMode(state == "edit");
        switch (state)
        {
            case "gallery":
                OpenGallery();
                break;
            case "models":
                // flyouts aren't captured: show the picker inline instead
                _boardScroll.Content = new ModelPicker(_host, (_, _) => { }, () => { });
                break;
        }
        QueueResize();
    }

    // ── Window ──────────────────────────────────────────────────────────────────────

    private void ConfigureWindow()
    {
        _appWindow.Title = "TileSmith";
        _appWindow.IsShownInSwitchers = false;
        _appWindow.SetIcon(AppInfo.AssetPath("TileSmith.ico"));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        _appWindow.Closing += (_, e) =>
        {
            e.Cancel = true; // Alt+F4 just hides the panel; TileSmith keeps running in the tray
            HideNow();
        };
    }

    private bool Solid => _host.Settings.PanelStyle != "glass" || !GlassBackdrop.IsSupported;
    private double ShadowSpace => Solid ? ShadowMargin : 0;

    private void ApplyStyle()
    {
        var key = Solid ? "solid" : "glass";
        if (_styleApplied == key) return;
        _styleApplied = key;
        if (_appWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(!Solid, false);
        var corner = Solid ? 1 /* DWMWCP_DONOTROUND */ : Native.DWMWCP_ROUND;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var border = Solid ? unchecked((int)0xFFFFFFFE) /* DWMWA_COLOR_NONE */ : unchecked((int)0xFFFFFFFF) /* default */;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref border, sizeof(int));
        // "sheet of glass": DWM composites the client area with per-pixel alpha, so the corners outside the
        // rounded surface are truly transparent
        var margins = Solid ? new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 } : new Native.MARGINS();
        Native.DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        // solid: TileSmith animates the panel and its shadow itself, so Windows' own show/hide fade would double it.
        // glass: the blur is the window, so only Windows' fade can take it away smoothly
        var noTransitions = Solid ? 1 : 0;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_TRANSITIONS_FORCEDISABLED, ref noTransitions, sizeof(int));
        SystemBackdrop = Solid ? new TransparentBackdrop() : new GlassBackdrop();
        _frameX = _frameY = 0;
    }

    /// <summary>Tiles and chrome in the panel use its style's palette (other windows switch it back to opaque).</summary>
    private void ApplyPanelPalette() => Palette.Apply(Root.ActualTheme != ElementTheme.Light, _host.Settings.Accent, glass: !Solid);

    private void ApplyTheme()
    {
        Root.RequestedTheme = _host.Settings.Theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        Motion.Reduced = _host.Settings.ReduceMotion;
    }

    private void OnSettingsChanged()
    {
        var before = (Palette.Current.Accent, Palette.Current.IsDark, Root.RequestedTheme);
        ApplyTheme();
        ApplyPanelPalette();
        if (_styleApplied != (Solid ? "solid" : "glass") || before != (Palette.Current.Accent, Palette.Current.IsDark, Root.RequestedTheme)) Rebuild();
        ApplyColumns();
        UpdateStatus();
        UpdateBanners();
    }

    // ── Building the UI ─────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        ApplyPanelPalette();
        ApplyStyle();
        var p = Palette.Current;
        var galleryWasOpen = _gallery is not null;
        _gallery = null;
        Root.Children.Clear();
        Root.Background = new SolidColorBrush(Colors.Transparent);

        _surface = new Grid
        {
            Margin = new Thickness(ShadowSpace),
            CornerRadius = new CornerRadius(Solid ? 28 : 0),
            Background = Solid ? p.Brush(Palette.WithAlpha(p.Canvas, 0.985)) : Glass.PanelFill(p),
            BorderBrush = p.Brush(Solid ? p.Hairline : Colors.Transparent),
            BorderThickness = new Thickness(Solid ? 1 : 0),
        };
        for (var i = 0; i < 4; i++) _surface.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        _header = BuildHeader();
        _surface.Children.Add(_header);

        _banners = new StackPanel { Spacing = 8, Margin = new Thickness(SurfacePadding, 0, SurfacePadding, 0) };
        Grid.SetRow(_banners, 1);
        _surface.Children.Add(_banners);

        _board = new TileBoard { HorizontalAlignment = HorizontalAlignment.Center };
        _board.OrderChanged += OnOrderChanged;
        _board.Resized += OnResized;
        _board.AddRequested += () => OpenGallery();
        _board.GestureEnded += QueueResize;
        _emptyState = BuildEmptyState();
        _editHint = BuildEditHint();
        _boardStack = new StackPanel();
        _boardStack.Children.Add(_editHint);
        _boardStack.Children.Add(_board);
        _boardStack.Children.Add(_emptyState);
        _boardScroll = new ScrollViewer
        {
            Content = _boardStack,
            Padding = new Thickness(SurfacePadding, 4, SurfacePadding, 8),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        _board.Scroller = _boardScroll;
        Grid.SetRow(_boardScroll, 2);
        _surface.Children.Add(_boardScroll);

        var ai = BuildAiArea();
        Grid.SetRow(ai, 3);
        _surface.Children.Add(ai);

        // the motion host opens/closes the panel and holds the shadow too, so the shadow never stays behind; the clip
        // host carries the grow animation; the surface keeps its own facade animations
        _clipHost = new Grid();
        var shadowHost = new Grid { IsHitTestVisible = false };
        _clipHost.Children.Add(shadowHost); // behind the surface: child visuals draw above their host's content
        _clipHost.Children.Add(_surface);
        _motionHost = new Grid();
        _motionHost.Children.Add(_clipHost);
        Root.Children.Add(_motionHost);
        if (!_visible) Motion.PanelHidden(_motionHost);
        if (Solid) Motion.AttachShadow(shadowHost, _surface, blur: 26, opacity: p.IsDark ? 0.55f : 0.22f, offsetY: 8, radius: 28);

        LoadTiles();
        UpdateClock();
        UpdateStatus();
        UpdateBanners();
        if (galleryWasOpen) OpenGallery();
        QueueResize();
    }

    private Grid BuildHeader()
    {
        var p = Palette.Current;
        var header = new Grid { Padding = new Thickness(SurfacePadding + 6, 14, SurfacePadding - 4, 8), ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _clock = Kit.Title("", 26);
        _clock.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_clock);
        _date = Ui.Text("", 13, p.Text2);
        _date.VerticalAlignment = VerticalAlignment.Bottom;
        _date.Margin = new Thickness(0, 0, 0, 5);
        Grid.SetColumn(_date, 1);
        header.Children.Add(_date);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };

        // the AI in use: tap to pick another provider or model
        _modelChip = new Pressable { Height = 32, CornerRadius = new CornerRadius(16), Fill = p.Overlay(0.06), Margin = new Thickness(0, 0, 4, 0) };
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Padding = new Thickness(5, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        _modelAvatarHost = new Grid { Width = 22, Height = 22 };
        _statusDot = new LiveDot(7) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -1, -1) };
        var avatarStack = new Grid();
        avatarStack.Children.Add(_modelAvatarHost);
        avatarStack.Children.Add(_statusDot);
        chipRow.Children.Add(avatarStack);
        _modelText = Ui.Text("", 12, p.Text1, semibold: true);
        _modelText.MaxWidth = 130;
        _modelText.VerticalAlignment = VerticalAlignment.Center;
        chipRow.Children.Add(_modelText);
        chipRow.Children.Add(Glyph.Make("expand_more", 16, p.Text3));
        _modelChip.SetContent(chipRow);
        _modelChip.AccessibleName = "Choose the AI model";
        _modelChip.Click += (_, _) => ShowModelPicker();
        actions.Children.Add(_modelChip);

        _editButton = new IconButton("edit", "Edit tiles", 34, 18);
        _editButton.Click += (_, _) => SetEditMode(!_editMode);
        actions.Children.Add(_editButton);
        _pinButton = new IconButton("keep_off", "Keep open", 34, 18);
        _pinButton.Click += (_, _) => SetPinned(!_pinned);
        actions.Children.Add(_pinButton);

        var settingsHost = new Grid();
        var settings = new IconButton("settings", "Settings", 34, 18);
        settings.Click += (_, _) => OpenSettings(_host.Updates.HasUpdate ? "updates" : null);
        settingsHost.Children.Add(settings);
        _settingsBadge = new Grid { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = p.Brush(p.Accent), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        settingsHost.Children.Add(_settingsBadge);
        actions.Children.Add(settingsHost);
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);
        RefreshHeaderButtons();
        return header;
    }

    private Grid BuildEditHint()
    {
        var p = Palette.Current;
        var hint = new Grid { ColumnSpacing = 8, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10), CornerRadius = new CornerRadius(14), Background = p.Brush(p.AccentSoft), Visibility = Visibility.Collapsed };
        hint.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hint.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hint.Children.Add(Glyph.Make("pan_tool", 16, p.Accent));
        var text = Ui.Text("Hold a tile, then drag it to move it  ·  ↘ resizes  ·  ✦ changes it with AI", 12, p.Text1, maxLines: 2);
        text.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(text, 1);
        hint.Children.Add(text);
        return hint;
    }

    private FrameworkElement BuildEmptyState()
    {
        var empty = Kit.Empty("widgets", "Your panel is empty", "Add ready-made widgets, or describe one below and the AI builds it.");
        var browse = Kit.Button("Browse widgets", "add", ChipStyle.Filled, () => OpenGallery());
        browse.HorizontalAlignment = HorizontalAlignment.Center;
        browse.Margin = new Thickness(0, 8, 0, 0);
        empty.Children.Add(browse);
        empty.Visibility = Visibility.Collapsed;
        return empty;
    }

    private FrameworkElement BuildAiArea()
    {
        var p = Palette.Current;
        var area = new StackPanel { Spacing = 8, Padding = new Thickness(SurfacePadding - 4, 0, SurfacePadding - 4, SurfacePadding - 4) };


        // the composer pill
        _composer = new Border
        {
            MinHeight = 52,
            CornerRadius = new CornerRadius(26),
            Background = p.Brush(p.Chrome),
            BorderBrush = p.Brush(p.HairlineStrong),
            BorderThickness = new Thickness(1),
        };
        _inputRow = BuildInputRow();
        _composer.Child = _inputRow;
        area.Children.Add(_composer);
        return area;
    }

    private Grid BuildInputRow()
    {
        var p = Palette.Current;
        var row = new Grid { ColumnSpacing = 6, Padding = new Thickness(14, 0, 6, 0), MinHeight = 50 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(Glyph.Make("auto_awesome", 20, p.Accent));

        _prompt = new TextBox
        {
            PlaceholderText = "Describe a widget to build…",
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 6, 6, 6),
            FontSize = 14,
            AcceptsReturn = false,
            IsSpellCheckEnabled = false,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 96,
        };
        Kit.Transparent(_prompt);
        _prompt.KeyDown += Prompt_KeyDown;
        _prompt.TextChanged += (_, _) => RefreshSendButton();
        Grid.SetColumn(_prompt, 1);
        row.Children.Add(_prompt);

        var studio = new IconButton("open_in_new", "Open Widget Studio", 36, 18) { VerticalAlignment = VerticalAlignment.Center };
        studio.Click += (_, _) =>
        {
            HideNow();
            App.Current.OpenStudio();
        };
        Grid.SetColumn(studio, 2);
        row.Children.Add(studio);

        _sendButton = new IconButton("arrow_upward", "Build it (Enter)", 40, 22) { VerticalAlignment = VerticalAlignment.Center };
        _sendButton.Click += (_, _) => _ = SendAsync();
        Grid.SetColumn(_sendButton, 3);
        row.Children.Add(_sendButton);
        RefreshSendButton();
        return row;
    }

    // ── Model picker ────────────────────────────────────────────────────────────────

    private void ShowModelPicker()
    {
        var picker = new ModelPicker(_host, (providerId, model) =>
        {
            _modelFlyout?.Hide();
            if (providerId == "auto")
            {
                _host.Settings.Ai.Engine = "auto";
            }
            else
            {
                _host.Settings.Ai.Engine = providerId;
                _host.Settings.Ai.SetModel(providerId, model);
            }
            _host.SaveSettings();
            UpdateStatus();
            Motion.Pulse(_modelAvatarHost, 1.15f);
        }, () =>
        {
            _modelFlyout?.Hide();
            OpenSettings("ai");
        });
        _modelFlyout = ModelPicker.Flyout(picker);
        _modelFlyout.Opened += (_, _) => _dialogOpen = true;
        _modelFlyout.Closed += (_, _) => _dialogOpen = false;
        _modelFlyout.ShowAt(_modelChip);
    }

    // ── Tiles ───────────────────────────────────────────────────────────────────────

    private void OnWidgetsChanged(object? sender, NotifyCollectionChangedEventArgs e) => LoadTiles();

    private void LoadTiles()
    {
        if (_board is null) return;
        ApplyPanelPalette();
        foreach (var old in _board.Tiles) old.StopPolling();
        var tiles = new List<WidgetTile>();
        foreach (var widget in _host.Widgets)
        {
            try
            {
                var tile = new WidgetTile(widget, _host);
                tile.Command += OnTileCommand;
                tile.ResizeRequested += (t, size) => _host.ResizeWidget(t.Widget.Id, size.ToString());
                tile.Failed += (t, message) => ShowNotice($"{t.Widget.Title}: {message}", warning: true, "Fix with AI", () =>
                {
                    HideNow();
                    App.Current.OpenStudio($"This widget doesn't work. Using it fails with: \"{message}\". Find out why on this PC (test its actions), fix it and keep everything else as it is.", t.Widget);
                });
                tiles.Add(tile);
                if (_visible) { _ = tile.RefreshAsync(); tile.StartPolling(); }
                if (_celebrate.Remove(widget.Id)) tile.Loaded += (_, _) => tile.Celebrate();
            }
            catch (Exception ex)
            {
                Log.Error($"Couldn't render widget \"{widget.Title}\"", ex);
            }
        }
        _board.Load(tiles);
        _board.IsEditMode = _editMode;
        _emptyState.Visibility = tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyColumns();
        QueueResize();
    }

    private void ApplyColumns()
    {
        if (_board is null) return;
        var setting = _host.Settings.PanelColumns;
        if (setting is >= 3 and <= 5)
        {
            _board.Columns = setting;
        }
        else
        {
            // automatic: 4 columns (≈4:3 panel), widening to 5 once 4 would need many rows
            var cells = _host.Widgets.Sum(w => { var c = w.Cells; return Math.Min(c.W, 4) * c.H; });
            _board.Columns = cells > 4 * 6 ? 5 : 4;
        }
        if (_modelText is not null) _modelText.MaxWidth = _board.Columns <= 3 ? 84 : 130;
        if (_date is not null) _date.Visibility = _board.Columns <= 3 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnOrderChanged(IReadOnlyList<string> ids)
    {
        var ordered = ids.Select(id => _host.Widgets.FirstOrDefault(w => w.Id == id)).OfType<WidgetDefinition>().ToList();
        if (ordered.Count != _host.Widgets.Count || ordered.SequenceEqual(_host.Widgets)) return;
        _host.Widgets.CollectionChanged -= OnWidgetsChanged;
        for (var i = 0; i < ordered.Count; i++)
        {
            var current = _host.Widgets.IndexOf(ordered[i]);
            if (current != i) _host.Widgets.Move(current, i);
        }
        _host.Widgets.CollectionChanged += OnWidgetsChanged;
        _host.SaveWidgets();
    }

    private void OnResized(WidgetTile tile, CellSize size) => _host.ResizeWidget(tile.Widget.Id, size.ToString());

    private async void OnTileCommand(WidgetTile tile, TileCommand command)
    {
        var widget = tile.Widget;
        switch (command)
        {
            case TileCommand.EditWithAi:
                HideNow();
                App.Current.OpenStudio(null, widget);
                break;
            case TileCommand.EditJson:
                await EditJsonAsync(widget);
                break;
            case TileCommand.Duplicate:
                var copy = widget.Clone();
                copy.Id = WidgetDefinition.NewId();
                _celebrate.Add(copy.Id);
                _host.InsertWidget(_host.IndexOf(widget.Id) + 1, copy);
                break;
            case TileCommand.Remove:
                var index = _host.IndexOf(widget.Id);
                var removed = _host.RemoveWidget(widget.Id);
                if (removed is not null) ShowNotice($"Removed “{removed.Title}”", warning: false, "Undo", () => _host.InsertWidget(index, removed));
                break;
        }
    }

    private async Task EditJsonAsync(WidgetDefinition widget)
    {
        var p = Palette.Current;
        var box = new TextBox
        {
            Text = widget.ToJson(),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Height = 300,
            IsSpellCheckEnabled = false,
        };
        var error = Ui.Text("", 12, p.Error, maxLines: 6);
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(box);
        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            Title = $"Edit “{widget.Title}”",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        dialog.PrimaryButtonClick += (_, e) =>
        {
            try
            {
                var edited = WidgetDefinition.FromJson(box.Text);
                edited.Id = widget.Id;
                var result = WidgetValidator.Validate(edited);
                if (!result.IsValid)
                {
                    error.Text = string.Join("\n", result.Errors);
                    e.Cancel = true;
                    return;
                }
                _celebrate.Add(edited.Id);
                _host.UpsertWidget(edited);
            }
            catch (Exception ex)
            {
                error.Text = "That isn't valid JSON: " + ex.Message;
                e.Cancel = true;
            }
        };
        _dialogOpen = true;
        try { await dialog.ShowAsync(); }
        finally { _dialogOpen = false; }
    }

    private void SetEditMode(bool on)
    {
        if (on && _gallery is not null) CloseGallery();
        _editMode = on;
        if (_board is not null) _board.IsEditMode = on;
        if (_editHint is not null)
        {
            _editHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (on) Motion.Enter(_editHint, -6);
        }
        RefreshHeaderButtons();
        QueueResize();
    }

    private void SetPinned(bool pinned)
    {
        _pinned = pinned;
        RefreshHeaderButtons();
    }

    public void Pin() => SetPinned(true);

    /// <summary>The next time this widget appears on the board it gets the "new" accent ring.</summary>
    public void CelebrateNext(string id) => _celebrate.Add(id);

    private void RefreshHeaderButtons()
    {
        if (_pinButton is null || _editButton is null) return;
        var p = Palette.Current;
        _pinButton.SetIcon(_pinned ? "keep" : "keep_off");
        _pinButton.SetColors(_pinned ? p.AccentSoft : Colors.Transparent, _pinned ? p.Accent : p.Text2);
        ToolTipService.SetToolTip(_pinButton, _pinned ? "Close when I click elsewhere" : "Keep open");
        _editButton.SetIcon(_editMode ? "check" : "edit");
        _editButton.SetColors(_editMode ? p.Accent : Colors.Transparent, _editMode ? p.OnAccent : p.Text2);
        ToolTipService.SetToolTip(_editButton, _editMode ? "Done" : "Edit tiles (or hold a tile)");
    }

    // ── Gallery ─────────────────────────────────────────────────────────────────────

    public void OpenGallery(bool focusSearch = false)
    {
        ApplyPanelPalette();
        if (_editMode) SetEditMode(false);
        _gallery ??= new GallerySheet(_host, _board.BoardWidth, widget =>
        {
            _celebrate.Add(widget.Id);
            _host.UpsertWidget(widget);
        }, () =>
        {
            CloseGallery();
            _prompt.Focus(FocusState.Programmatic);
        }, CloseGallery);
        if (!ReferenceEquals(_boardScroll.Content, _gallery))
        {
            _boardScroll.Content = _gallery;
            _boardScroll.ChangeView(null, 0, null, true);
            Motion.Enter(_gallery, 18);
        }
        if (focusSearch) _gallery.FocusSearch();
        QueueResize();
    }

    private void CloseGallery()
    {
        if (_gallery is null || _boardScroll is null) return;
        _gallery = null;
        _boardScroll.Content = _boardStack;
        Motion.Enter(_boardStack, -12);
        var i = 0;
        foreach (var tile in _board.Tiles) Motion.Enter(tile, 6, Math.Min(i++ * 14, 160), Motion.Normal);
        QueueResize();
    }

    // ── Show / hide / position ──────────────────────────────────────────────────────

    public void Toggle(PointInt32? anchor)
    {
        if (_visible) Hide();
        else if ((DateTime.UtcNow - _lastHidden).TotalMilliseconds > 350) Show(anchor);
    }

    public void Show(PointInt32? anchor = null, bool focusPrompt = false)
    {
        _anchor = anchor;
        _hiding = false;
        UpdateStatus();
        UpdateBanners();
        UpdateClock();
        _lastSurfaceHeight = 0; // no grow animation while opening
        PositionWindow();
        if (!_visible)
        {
            _visible = true;
            _lastShown = DateTime.UtcNow;
            // the first frame Windows composes must already be the start of the animation, not the last closed frame
            Motion.PanelIn(_motionHost, _windowSizeDip, _opensFromBottom, _opensFromRight, fade: !Solid);
            _appWindow.Show(true);
            Activate();
            Native.ForceForeground(_hwnd);
            MeasureFrame();
            // tiles cascade in from the corner the panel opens from
            var tiles = _board.Tiles.ToList();
            for (var i = 0; i < tiles.Count; i++)
            {
                var tile = tiles[i];
                if (!Motion.Reduced) Motion.Enter(tile, 10, Math.Min((tiles.Count - 1 - i) * 16, 200), Motion.Normal);
                _ = tile.RefreshAsync();
                tile.StartPolling();
            }
            _clockTimer.Start();
            EnsureServerStatus();
        }
        else
        {
            // clicked again while closing: turn around from wherever the close animation got to
            Motion.PanelIn(_motionHost, _windowSizeDip, _opensFromBottom, _opensFromRight, fade: !Solid, fromCurrent: true);
            Activate();
            Native.ForceForeground(_hwnd);
        }
        QueueResize();
        if (focusPrompt) _prompt.Focus(FocusState.Programmatic);
    }

    /// <summary>Animated hide (click elsewhere, tray click, Esc).</summary>
    public void Hide()
    {
        if (!_visible || _hiding) return;
        if (!Solid)
        {
            // glass: fading the content would leave the bare blur behind; Windows fades the whole window instead
            HideNow();
            return;
        }
        _hiding = true;
        Motion.PanelOut(_motionHost, _opensFromBottom, () =>
        {
            if (_hiding) HideNow();
        });
    }

    private void HideNow()
    {
        _hiding = false;
        if (!_visible) return;
        _visible = false;
        _lastHidden = DateTime.UtcNow;
        _clockTimer.Stop();
        foreach (var tile in _board.Tiles) tile.StopPolling();
        if (_editMode) SetEditMode(false);
        if (_gallery is not null) CloseGallery();
        _modelFlyout?.Hide();
        _appWindow.Hide();
        Motion.PanelHidden(_motionHost);
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated || !_visible) return;
        if (_dialogOpen || _pinned) return;
        if ((DateTime.UtcNow - _lastShown).TotalMilliseconds < 250) return;
        Hide();
    }

    /// <summary>Puts text in the composer (deep link: TileSmith.exe --prompt "…").</summary>
    public void SetPrompt(string text)
    {
        _prompt.Text = text;
        _prompt.SelectionStart = text.Length;
        _prompt.Focus(FocusState.Programmatic);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_dialogOpen) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                if (_gallery is not null) CloseGallery();
                else if (_editMode) SetEditMode(false);
                else Hide();
                break;
            // Ctrl+K ask the AI · Ctrl+G gallery · Ctrl+E edit tiles · Ctrl+M choose the model · Ctrl+, settings
            case VirtualKey.K when ctrl:
                e.Handled = true;
                if (_gallery is not null) CloseGallery();
                _prompt.Focus(FocusState.Keyboard);
                break;
            case VirtualKey.G when ctrl:
                e.Handled = true;
                if (_gallery is null) OpenGallery(focusSearch: true); else CloseGallery();
                break;
            case VirtualKey.E when ctrl:
                e.Handled = true;
                SetEditMode(!_editMode);
                break;
            case VirtualKey.M when ctrl:
                e.Handled = true;
                ShowModelPicker();
                break;
            case (VirtualKey)188 when ctrl: // Ctrl+,
                e.Handled = true;
                OpenSettings(null);
                break;
        }
    }

    /// <summary>The window frame Windows keeps around a borderless window (it eats into the client area).</summary>
    private void MeasureFrame()
    {
        var size = _appWindow.Size;
        var client = _appWindow.ClientSize;
        var fx = Math.Max(0, size.Width - client.Width);
        var fy = Math.Max(0, size.Height - client.Height);
        if (fx == _frameX && fy == _frameY) return;
        _frameX = fx;
        _frameY = fy;
        PositionWindow();
    }

    private void PositionWindow()
    {
        Native.GetCursorPos(out var cursor);
        var point = _anchor ?? new PointInt32(cursor.X, cursor.Y);
        var display = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var outer = display.OuterBounds;
        var scale = Native.ScaleAt(work.X + work.Width / 2, work.Y + work.Height / 2);

        // an auto-hiding taskbar doesn't shrink the work area; keep clear of where it pops up
        var taskbar = Native.GetTaskbar();
        var onThisDisplay = taskbar.Rect.Right > outer.X && taskbar.Rect.Left < outer.X + outer.Width
                            && taskbar.Rect.Bottom > outer.Y && taskbar.Rect.Top < outer.Y + outer.Height;
        if (taskbar.AutoHide && onThisDisplay && work.Width == outer.Width && work.Height == outer.Height)
        {
            var t = (int)Math.Round(48 * scale);
            work = taskbar.Edge switch
            {
                0 => new RectInt32(work.X + t, work.Y, work.Width - t, work.Height),
                1 => new RectInt32(work.X, work.Y + t, work.Width, work.Height - t),
                2 => new RectInt32(work.X, work.Y, work.Width - t, work.Height),
                _ => new RectInt32(work.X, work.Y, work.Width, work.Height - t),
            };
        }

        var maxSurfaceHeight = work.Height / scale - 2 * EdgeGap;
        var surfaceWidth = _board.BoardWidth + 2 * SurfacePadding + 2 /* border */ + WidthSlack;
        var surfaceHeight = MeasureSurface(surfaceWidth, maxSurfaceHeight);
        var m = ShadowSpace;
        var width = (int)Math.Ceiling((surfaceWidth + 2 * m) * scale) + _frameX;
        var height = (int)Math.Ceiling((surfaceHeight + 2 * m) * scale) + _frameY;
        var gap = (int)Math.Round((EdgeGap - m) * scale);

        var x = work.X + work.Width - width - gap;
        var y = work.Y + work.Height - height - gap;
        if (work.X > outer.X) x = work.X + gap;
        if (work.Y > outer.Y) y = work.Y + gap;
        _opensFromRight = work.X <= outer.X;
        _opensFromBottom = work.Y <= outer.Y;
        _windowSizeDip = new Vector2((float)(surfaceWidth + 2 * m), (float)(surfaceHeight + 2 * m));
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));

        // a taller panel slides open upward instead of jumping (not while it is still opening: one motion at a time)
        var opening = (DateTime.UtcNow - _lastShown).TotalMilliseconds < 400;
        if (_visible && !opening && _lastSurfaceHeight > 0 && surfaceHeight > _lastSurfaceHeight + 1)
            Motion.GrowFromBottom(_clipHost, (float)(surfaceHeight - _lastSurfaceHeight), _header, _banners);
        _lastSurfaceHeight = surfaceHeight;
    }

    private double MeasureSurface(double width, double maxHeight)
    {
        _boardScroll.MaxHeight = double.PositiveInfinity;
        _surface.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        var desired = _surface.DesiredSize.Height;
        if (desired > maxHeight)
        {
            _boardScroll.MaxHeight = Math.Max(160, _boardScroll.DesiredSize.Height - (desired - maxHeight));
            desired = maxHeight;
        }
        return Math.Max(desired, 200);
    }

    private void QueueResize()
    {
        if (_resizeQueued) return;
        _resizeQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _resizeQueued = false;
            if (_visible && !_board.IsDragging) PositionWindow(); // never move the panel under a dragging finger
        });
    }

    public void OnDisplayChanged()
    {
        if (_visible) QueueResize();
    }

    // ── Header status ───────────────────────────────────────────────────────────────

    private void UpdateClock()
    {
        if (_clock is null) return;
        var now = DateTime.Now;
        _clock.Text = now.ToString("t", CultureInfo.CurrentCulture);
        _date.Text = now.ToString("ddd d MMM", CultureInfo.CurrentCulture);
    }

    private void UpdateStatus()
    {
        if (_modelText is null) return;
        var p = Palette.Current;
        var settings = _host.Settings;
        var provider = AiEngineFactory.ResolveProvider(settings);
        var model = settings.Ai.ModelFor(provider.Id);
        _modelText.Text = ProviderCatalog.ModelLabel(provider, model);
        _modelAvatarHost.Children.Clear();
        _modelAvatarHost.Children.Add(ProviderLogo.Make(provider, 22, tile: false));

        Color color;
        var live = false;
        string tip;
        if (ProviderCatalog.NeedsServer(provider, settings.Ai))
        {
            var server = _host.Server;
            (color, live, tip) = server.State switch
            {
                ConnectionState.Connected => (p.Success, false, $"{provider.Name} · {server.StatusText}"),
                ConnectionState.Connecting => (p.Warning, true, "Connecting…"),
                ConnectionState.Error => (p.Error, false, server.StatusText),
                ConnectionState.NotConfigured => (p.Text3, false, "Set up your server in Settings"),
                _ => (p.Text3, false, $"{provider.Name} on {server.DisplayName}"),
            };
        }
        else
        {
            var status = ProviderCatalog.Status(provider, settings);
            (color, tip) = status.IsReady ? (p.Success, $"{provider.Name} · {provider.KindLabel}") : (p.Warning, $"{provider.Name}: {status.Text}");
        }
        if (settings.Ai.Engine == "auto") tip += " · picked automatically";
        if (App.Current.IsStudioBusy)
        {
            color = p.Accent;
            live = true;
        }
        _statusDot.Set(color, live);
        ToolTipService.SetToolTip(_modelChip, tip + "\nClick to choose another AI or model");
    }

    private void EnsureServerStatus()
    {
        var engine = AiEngineFactory.ResolveEngine(_host.Settings);
        if (!AiEngineFactory.NeedsServer(engine, _host.Settings.Ai) || !_host.Settings.Server.IsConfigured) return;
        if (_host.Server.State is ConnectionState.Idle or ConnectionState.Error) _ = _host.Server.ConnectAsync();
    }

    // ── Banners ─────────────────────────────────────────────────────────────────────

    private Grid? _updateBanner;
    private Grid? _noticeBanner;
    private readonly DispatcherTimer _noticeTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    private void UpdateBanners()
    {
        if (_banners is null) return;
        var u = _host.Updates;
        var p = Palette.Current;
        if (_updateBanner is not null) _banners.Children.Remove(_updateBanner);
        _updateBanner = null;
        _settingsBadge.Visibility = u.HasUpdate ? Visibility.Visible : Visibility.Collapsed;

        var provider = AiEngineFactory.ResolveProvider(_host.Settings);
        var status = ProviderCatalog.Status(provider, _host.Settings);
        if (!status.IsReady)
        {
            _updateBanner = Banner("hub", "Connect your AI", $"{provider.Name}: {status.Text}. Or pick another provider.", "Set up", () => OpenSettings("ai"), p.Accent);
        }
        else if (u.HasUpdate && !_updateDismissed)
        {
            var (title, action) = u.Stage switch
            {
                UpdateStage.Ready => ($"TileSmith {u.NewVersion} is ready", "Restart"),
                UpdateStage.Downloading => ($"Downloading {u.NewVersion}… {u.Progress}%", ""),
                _ => ($"TileSmith {u.NewVersion} is available", "Update"),
            };
            _updateBanner = Banner("system_update_alt", title, null, action, () =>
            {
                if (u.Stage == UpdateStage.Ready) u.RestartToApply(App.Current.PrepareForExit);
                else _ = u.DownloadAsync();
            }, p.Accent, dismiss: () => { _updateDismissed = true; UpdateBanners(); }, progress: u.Stage == UpdateStage.Downloading ? u.Progress : null);
        }
        if (_updateBanner is not null) _banners.Children.Insert(0, _updateBanner);
        _banners.Margin = new Thickness(SurfacePadding, 0, SurfacePadding, _banners.Children.Count > 0 ? 8 : 0);
        QueueResize();
    }

    private static Grid Banner(string icon, string title, string? message, string action, Action onAction, Color tone, Action? dismiss = null, int? progress = null)
    {
        var p = Palette.Current;
        var grid = new Grid { Padding = new Thickness(12, 10, 8, 10), ColumnSpacing = 10, CornerRadius = new CornerRadius(18), Background = p.Brush(Palette.WithAlpha(tone, 0.13)) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Glyph.Make(icon, 20, tone));
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.Text(title, 13, p.Text1, semibold: true, maxLines: 2));
        if (message is not null) texts.Children.Add(Ui.Text(message, 12, p.Text2, maxLines: 3));
        if (progress is { } pct) texts.Children.Add(new ProgressBar { Value = pct, Maximum = 100, Margin = new Thickness(0, 6, 0, 0), Foreground = p.Brush(tone) });
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrEmpty(action))
        {
            var chip = new Chip(action, filled: true);
            chip.Click += (_, _) => onAction();
            buttons.Children.Add(chip);
        }
        if (dismiss is not null)
        {
            var close = new IconButton("close", "Dismiss", 30, 16);
            close.Click += (_, _) => dismiss();
            buttons.Children.Add(close);
        }
        Grid.SetColumn(buttons, 2);
        grid.Children.Add(buttons);
        Motion.Enter(grid, 6);
        return grid;
    }

    public void ShowNotice(string message, bool warning, string? actionText = null, Action? action = null)
    {
        if (_banners is null) return;
        var p = Palette.Current;
        if (_noticeBanner is not null) _banners.Children.Remove(_noticeBanner);
        _noticeBanner = Banner(warning ? "error" : "info", message, null, actionText ?? "", () =>
        {
            action?.Invoke();
            DismissNotice();
        }, warning ? p.Warning : p.Accent, dismiss: DismissNotice);
        _banners.Children.Add(_noticeBanner);
        _banners.Margin = new Thickness(SurfacePadding, 0, SurfacePadding, 8);
        _noticeTimer.Stop();
        _noticeTimer.Tick -= OnNoticeTimer;
        _noticeTimer.Tick += OnNoticeTimer;
        _noticeTimer.Start();
        QueueResize();
    }

    private void OnNoticeTimer(object? sender, object e) => DismissNotice();

    private void DismissNotice()
    {
        _noticeTimer.Stop();
        if (_noticeBanner is null) return;
        var banner = _noticeBanner;
        _noticeBanner = null;
        Motion.FadeOut(banner, () =>
        {
            _banners.Children.Remove(banner);
            _banners.Margin = new Thickness(SurfacePadding, 0, SurfacePadding, _banners.Children.Count > 0 ? 8 : 0);
            QueueResize();
        }, toY: -6);
    }

    private void OpenSettings(string? page)
    {
        HideNow();
        App.Current.OpenSettings(page);
    }

    // ── AI composer ─────────────────────────────────────────────────────────────────

    private void RefreshSendButton()
    {
        if (_sendButton is null) return;
        var p = Palette.Current;
        var ready = _prompt.Text.Trim().Length > 0;
        _sendButton.SetColors(ready ? p.Accent : p.Overlay(0.06), ready ? p.OnAccent : p.Text3);
        _sendButton.IsEnabledForInput = ready;
    }

    private void Prompt_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var history = _host.Settings.PromptHistory;
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                _ = SendAsync();
                break;
            // Up / Down walk through earlier prompts (like a terminal)
            case VirtualKey.Up when history.Count > 0 && (_prompt.Text.Length == 0 || _historyIndex >= 0):
                e.Handled = true;
                _historyIndex = Math.Min(_historyIndex + 1, history.Count - 1);
                _prompt.Text = history[_historyIndex];
                _prompt.SelectionStart = _prompt.Text.Length;
                break;
            case VirtualKey.Down when _historyIndex >= 0:
                e.Handled = true;
                _historyIndex--;
                _prompt.Text = _historyIndex >= 0 ? history[_historyIndex] : "";
                _prompt.SelectionStart = _prompt.Text.Length;
                break;
        }
    }

    private async Task SendAsync()
    {
        var text = _prompt.Text.Trim();
        if (text.Length == 0) return;
        _historyIndex = -1;
        _prompt.Text = "";
        if (_gallery is not null) CloseGallery();
        // building happens in the Studio: chat, live preview, then onto the panel
        HideNow();
        App.Current.OpenStudio(text);
        await Task.CompletedTask;
    }

}
