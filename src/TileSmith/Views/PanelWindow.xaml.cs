using System.Collections.Specialized;
using System.Globalization;
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
/// The panel: an Android-style sheet that rises from the taskbar corner. Clock, the AI in use and quick actions up
/// top, a launcher-like grid of widgets (or the widget gallery), and the AI composer at the bottom that builds
/// widgets right into the grid.
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
    private readonly List<Action> _undo = [];
    private readonly HashSet<string> _celebrate = [];
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _resultTimer = new() { Interval = TimeSpan.FromSeconds(7) };

    // chrome
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
    private Border _activity = null!;
    private StackPanel _activityList = null!;
    private ScrollViewer _activityScroll = null!;
    private StackPanel _interaction = null!;
    private WrapPanel _suggestions = null!;
    private Border _composer = null!;
    private TextBox _prompt = null!;
    private Grid _inputRow = null!;
    private Grid _statusRow = null!;
    private WorkingScanner _scanner = null!;
    private TextBlock _statusLine = null!;
    private Grid _resultRow = null!;
    private TextBlock _resultText = null!;
    private TextBlock _resultIcon = null!;
    private Chip _undoChip = null!;
    private Chip _sizeChip = null!;
    private Border _editingChip = null!;
    private TextBlock _editingText = null!;
    private IconButton _sendButton = null!;

    private bool _visible;
    private bool _pinned;
    private bool _editMode;
    private bool _dialogOpen;
    private bool _hiding;
    private DateTime _lastHidden = DateTime.MinValue;
    private DateTime _lastShown = DateTime.MinValue;
    private PointInt32? _anchor;
    private CancellationTokenSource? _agentCts;
    private WidgetDefinition? _editing;
    private string _sizePreference = "auto";
    private TaskCompletionSource<bool>? _approval;
    private TaskCompletionSource<string>? _question;
    private bool _updateDismissed;
    private bool _resizeQueued;
    private string? _styleApplied;
    private int _historyIndex = -1;
    private Flyout? _modelFlyout;
    private double _lastSurfaceHeight;
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
        _resultTimer.Tick += (_, _) => { _resultTimer.Stop(); ShowComposerState(ComposerState.Input); };

        ApplyTheme();
        Rebuild();
    }

    public bool IsOpen => _visible;

    // ── Dev capture (--capture) ─────────────────────────────────────────────────────

    internal static readonly string[] CaptureStates = ["default", "edit", "gallery", "working", "models"];

    internal UIElement CaptureRoot => Root;

    internal void PrepareCapture(string state)
    {
        Show();
        SetPinned(true);
        CloseGallery();
        _boardScroll.Content = _boardStack;
        SetEditMode(state == "edit");
        _activity.Visibility = Visibility.Collapsed;
        ShowComposerState(ComposerState.Input);
        switch (state)
        {
            case "gallery":
                OpenGallery();
                break;
            case "working":
                _activityList.Children.Clear();
                AddSay("A toggle for my VPN", isUser: true);
                AddSay("Checking which VPN connections Windows knows about");
                var step = new AgentStep { Tool = "powershell", Title = "Running PowerShell", Detail = "Get-VpnConnection" };
                AddStep(step);
                step.State = StepState.Done;
                step.Output = "Name: Home";
                FinishStep(step);
                AddStep(new AgentStep { Tool = "test_action", Title = "Testing shell.powershell" });
                _activity.Visibility = Visibility.Visible;
                _statusLine.Text = "Testing shell.powershell…";
                ShowComposerState(ComposerState.Working);
                _board.ShowGhost(new CellSize(2, 1));
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
        SystemBackdrop = Solid ? new TransparentBackdrop() : new GlassBackdrop();
        _frameX = _frameY = 0;
    }

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
        Palette.Apply(Root.ActualTheme != ElementTheme.Light, _host.Settings.Accent);
        if (_styleApplied != (Solid ? "solid" : "glass") || before != (Palette.Current.Accent, Palette.Current.IsDark, Root.RequestedTheme)) Rebuild();
        ApplyColumns();
        UpdateStatus();
        UpdateBanners();
    }

    // ── Building the UI ─────────────────────────────────────────────────────────────

    private void Rebuild()
    {
        Palette.Apply(Root.ActualTheme != ElementTheme.Light, _host.Settings.Accent);
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
            Background = p.Brush(Solid ? Palette.WithAlpha(p.Canvas, 0.985) : Palette.WithAlpha(p.Canvas, p.IsDark ? 0.55 : 0.6)),
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

        // the clip host carries the grow animation; the surface keeps its own facade animations
        _clipHost = new Grid();
        var shadowHost = new Grid { IsHitTestVisible = false };
        _clipHost.Children.Add(shadowHost); // behind the surface: child visuals draw above their host's content
        _clipHost.Children.Add(_surface);
        Root.Children.Add(_clipHost);
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

        // live activity (steps, questions, approvals)
        _activityList = new StackPanel { Spacing = 6 };
        _activityScroll = new ScrollViewer { Content = _activityList, MaxHeight = 168, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled };
        _interaction = new StackPanel { Spacing = 8 };
        var activityStack = new StackPanel { Spacing = 10 };
        activityStack.Children.Add(_activityScroll);
        activityStack.Children.Add(_interaction);
        _activity = new Border
        {
            Background = p.Brush(p.Card),
            BorderBrush = p.Brush(p.Hairline),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(14, 12, 14, 12),
            Child = activityStack,
            Visibility = Visibility.Collapsed,
        };
        area.Children.Add(_activity);

        // suggestion chips
        _suggestions = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6, Visibility = Visibility.Collapsed, Margin = new Thickness(4, 0, 4, 0) };
        area.Children.Add(_suggestions);

        // "changing this widget" chip
        _editingText = Ui.Text("", 12, p.OnAccent, semibold: true);
        _editingText.VerticalAlignment = VerticalAlignment.Center;
        _editingText.MaxWidth = 260;
        var editingRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Padding = new Thickness(10, 0, 4, 0) };
        editingRow.Children.Add(Glyph.Make("auto_awesome", 14, p.OnAccent));
        editingRow.Children.Add(_editingText);
        var clearEdit = new IconButton("close", "Stop editing", 22, 14);
        clearEdit.SetColors(Colors.Transparent, p.OnAccent);
        clearEdit.Click += (_, _) => ClearEditing();
        editingRow.Children.Add(clearEdit);
        _editingChip = new Border
        {
            Height = 28,
            CornerRadius = new CornerRadius(14),
            Background = p.Brush(p.Accent),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = editingRow,
            Visibility = _editing is null ? Visibility.Collapsed : Visibility.Visible,
            Margin = new Thickness(4, 0, 0, 0),
        };
        if (_editing is not null) _editingText.Text = $"Changing “{_editing.Title}”";
        area.Children.Add(_editingChip);

        // the composer pill
        _composer = new Border
        {
            MinHeight = 52,
            CornerRadius = new CornerRadius(26),
            Background = p.Brush(p.Chrome),
            BorderBrush = p.Brush(p.HairlineStrong),
            BorderThickness = new Thickness(1),
        };
        var composerGrid = new Grid();
        _inputRow = BuildInputRow();
        _statusRow = BuildStatusRow();
        _resultRow = BuildResultRow();
        composerGrid.Children.Add(_inputRow);
        composerGrid.Children.Add(_statusRow);
        composerGrid.Children.Add(_resultRow);
        _composer.Child = composerGrid;
        area.Children.Add(_composer);
        ShowComposerState(_agentCts is null ? ComposerState.Input : ComposerState.Working);
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
            PlaceholderText = _editing is null ? "Describe a widget to build…" : "What should change?",
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
        _prompt.TextChanged += (_, _) => { RefreshSendButton(); if (_prompt.Text.Length > 0) ShowSuggestions(false); };
        _prompt.GotFocus += (_, _) => ShowSuggestions(true);
        Grid.SetColumn(_prompt, 1);
        row.Children.Add(_prompt);

        _sizeChip = MakeSizeChip();
        row.Children.Add(_sizeChip);

        _sendButton = new IconButton("arrow_upward", "Build it (Enter)", 40, 22) { VerticalAlignment = VerticalAlignment.Center };
        _sendButton.Click += (_, _) => _ = SendAsync();
        Grid.SetColumn(_sendButton, 3);
        row.Children.Add(_sendButton);
        RefreshSendButton();
        return row;
    }

    private Grid BuildStatusRow()
    {
        var p = Palette.Current;
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(18, 0, 8, 0), Visibility = Visibility.Collapsed, MinHeight = 50 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // tapping the status shows/hides the step-by-step activity
        var tap = new Pressable { CornerRadius = new CornerRadius(26), Fill = Colors.Transparent, Margin = new Thickness(-18, 0, 0, 0), AccessibleName = "Show steps" };
        tap.Click += (_, _) =>
        {
            _activity.Visibility = _activity.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (_activity.Visibility == Visibility.Visible) Motion.Enter(_activity, 8);
            QueueResize();
        };
        Grid.SetColumnSpan(tap, 2);
        row.Children.Add(tap);
        _scanner = new WorkingScanner(5, 2) { Color = p.Accent, IsHitTestVisible = false };
        row.Children.Add(_scanner);
        _statusLine = Ui.TipWhenTrimmed(Ui.Text("Working…", 13.5, p.Text1));
        _statusLine.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_statusLine, 1);
        row.Children.Add(_statusLine);
        var stop = new Chip("Stop", "stop") { VerticalAlignment = VerticalAlignment.Center };
        stop.Click += (_, _) => _agentCts?.Cancel();
        Grid.SetColumn(stop, 2);
        row.Children.Add(stop);
        return row;
    }

    private Grid BuildResultRow()
    {
        var p = Palette.Current;
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(18, 0, 8, 0), Visibility = Visibility.Collapsed, MinHeight = 50 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _resultIcon = Glyph.Make("check_circle", 20, p.Success);
        row.Children.Add(_resultIcon);
        _resultText = Ui.TipWhenTrimmed(Ui.Text("", 13.5, p.Text1));
        _resultText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_resultText, 1);
        row.Children.Add(_resultText);
        _undoChip = new Chip("Undo", "undo") { VerticalAlignment = VerticalAlignment.Center };
        _undoChip.Click += (_, _) => Undo();
        Grid.SetColumn(_undoChip, 2);
        row.Children.Add(_undoChip);
        var close = new IconButton("close", "Dismiss", 32, 16) { VerticalAlignment = VerticalAlignment.Center };
        close.Click += (_, _) =>
        {
            _resultTimer.Stop();
            ShowComposerState(ComposerState.Input);
            _activity.Visibility = Visibility.Collapsed;
            QueueResize();
        };
        Grid.SetColumn(close, 3);
        row.Children.Add(close);
        return row;
    }

    private Chip MakeSizeChip()
    {
        var label = _sizePreference == "auto" ? "Auto" : _sizePreference.Replace('x', '×');
        var chip = new Chip(label, "grid_view", height: 30, style: ChipStyle.Tonal) { VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(chip, "Widget size — Auto lets the AI choose");
        chip.Click += (_, _) => SizeMenu().ShowAt(chip);
        Grid.SetColumn(chip, 2);
        return chip;
    }

    private MenuFlyout SizeMenu()
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
        foreach (var (id, text) in new[] { ("auto", "Auto — let the AI choose"), ("1x1", "1 × 1  Icon"), ("2x1", "2 × 1  Quick Settings tile"), ("4x1", "4 × 1  Full-width bar"), ("2x2", "2 × 2  Square card"), ("4x2", "4 × 2  Large card") })
        {
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = "size", IsChecked = _sizePreference == id };
            item.Click += (_, _) =>
            {
                _sizePreference = id;
                _inputRow.Children.Remove(_sizeChip);
                _sizeChip = MakeSizeChip();
                _inputRow.Children.Add(_sizeChip);
            };
            menu.Items.Add(item);
        }
        return menu;
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
        foreach (var old in _board.Tiles) old.StopPolling();
        var tiles = new List<WidgetTile>();
        foreach (var widget in _host.Widgets)
        {
            try
            {
                var tile = new WidgetTile(widget, _host);
                tile.Command += OnTileCommand;
                tile.ResizeRequested += (t, size) => _host.ResizeWidget(t.Widget.Id, size.ToString());
                tile.Failed += (t, message) => ShowNotice($"{t.Widget.Title}: {message}", warning: true);
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
                StartEditing(widget);
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
            _appWindow.Show(true);
            Activate();
            Native.ForceForeground(_hwnd);
            MeasureFrame();
            Motion.PanelIn(_surface, fromBottom: true);
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
            Motion.PanelIn(_surface, fromBottom: true);
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
        _hiding = true;
        Motion.PanelOut(_surface, () =>
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
                else if (_editing is not null) ClearEditing();
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
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));

        // a taller panel slides open upward instead of jumping
        if (_visible && _lastSurfaceHeight > 0 && surfaceHeight > _lastSurfaceHeight + 1)
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
        _modelAvatarHost.Children.Add(new Avatar(provider.Monogram, Palette.Parse(provider.Color), 22));

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
        if (_agentCts is not null)
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

    private enum ComposerState { Input, Working, Result }

    private void ShowComposerState(ComposerState state)
    {
        if (_inputRow is null) return;
        _inputRow.Visibility = state == ComposerState.Input ? Visibility.Visible : Visibility.Collapsed;
        _statusRow.Visibility = state == ComposerState.Working ? Visibility.Visible : Visibility.Collapsed;
        _resultRow.Visibility = state == ComposerState.Result ? Visibility.Visible : Visibility.Collapsed;
        UIElement visible = state switch { ComposerState.Input => _inputRow, ComposerState.Working => _statusRow, _ => _resultRow };
        Motion.Enter(visible, 6, 0, Motion.Fast);
    }

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

    private void ShowSuggestions(bool show)
    {
        if (_suggestions is null) return;
        show &= _agentCts is null && _question is null && _prompt.Text.Length == 0 && _editing is null;
        if (show && _suggestions.Children.Count == 0)
        {
            foreach (var (label, icon, prompt) in Suggestions())
            {
                var chip = new Chip(label, icon);
                chip.Click += (_, _) =>
                {
                    _prompt.Text = prompt;
                    _ = SendAsync();
                };
                _suggestions.Children.Add(chip);
            }
        }
        var was = _suggestions.Visibility == Visibility.Visible;
        _suggestions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show && !was)
        {
            var i = 0;
            foreach (var chip in _suggestions.Children) Motion.Enter(chip, 6, i++ * 30, Motion.Fast);
        }
        if (show != was) QueueResize();
    }

    /// <summary>Context-aware ideas: laptop display modes, your server, media, cleanup.</summary>
    private IEnumerable<(string Label, string Icon, string Prompt)> Suggestions()
    {
        var list = new List<(string, string, string)>();
        try
        {
            var displays = Core.Native.DisplayManager.GetDisplays();
            var main = displays.FirstOrDefault(d => d.IsInternal) ?? displays.FirstOrDefault();
            if (main is not null)
            {
                var res = main.ModesByResolution().Select(g => g.Key).Take(3).ToList();
                if (res.Count >= 2)
                    list.Add(("Resolution switcher", "aspect_ratio", $"A widget to switch my {(main.IsInternal ? "laptop" : "main")} display between {res[0].Replace("x", " × ")} and {res[1].Replace("x", " × ")}"));
            }
        }
        catch { }
        list.Add(("VPN toggle", "vpn_lock", "A toggle for my VPN connection"));
        if (_host.Settings.Server.IsConfigured) list.Add(("Restart a container", "deployed_code", "A button that restarts a Docker container on my server — ask me which one"));
        list.Add(("Night light", "nightlight", "A toggle for Windows night light"));
        list.Add(("Clean temp files", "cleaning_services", "A button that clears my temp folder and tells me how much space it freed"));
        return list.Take(5);
    }

    private void StartEditing(WidgetDefinition widget)
    {
        SetEditMode(false);
        _editing = widget;
        _editingText.Text = $"Changing “{widget.Title}”";
        _editingChip.Visibility = Visibility.Visible;
        Motion.Enter(_editingChip, 6);
        _prompt.PlaceholderText = "What should change?";
        ShowSuggestions(false);
        ShowComposerState(ComposerState.Input);
        _prompt.Focus(FocusState.Programmatic);
        QueueResize();
    }

    private void ClearEditing()
    {
        _editing = null;
        _editingChip.Visibility = Visibility.Collapsed;
        _prompt.PlaceholderText = "Describe a widget to build…";
        QueueResize();
    }

    private async Task SendAsync()
    {
        var text = _prompt.Text.Trim();
        if (text.Length == 0) return;
        _historyIndex = -1;
        if (_question is not null)
        {
            _prompt.Text = "";
            AnswerQuestion(text);
            return;
        }
        if (_agentCts is not null) return;
        RememberPrompt(text);
        if (_gallery is not null) CloseGallery();
        await RunAgentAsync(text);
    }

    private void RememberPrompt(string text)
    {
        var history = _host.Settings.PromptHistory;
        history.Remove(text);
        history.Insert(0, text);
        if (history.Count > 30) history.RemoveRange(30, history.Count - 30);
        Core.Settings.SettingsStore.Save(_host.Settings);
    }

    private async Task RunAgentAsync(string request)
    {
        IAiBackend backend;
        try
        {
            backend = _host.CreateBackend();
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, warning: true, "Settings", () => OpenSettings("ai"));
            return;
        }
        if (backend is RoutedBackend routed)
        {
            routed.Switched += (from, to, reason) => DispatcherQueue.TryEnqueue(() =>
            {
                AddSay($"{from} couldn't be reached — switching to {to}.");
                AddError(reason);
            });
        }

        var editing = _editing;
        _prompt.Text = "";
        ShowSuggestions(false);
        _editingChip.Visibility = Visibility.Collapsed;
        _undo.Clear();
        _steps.Clear();
        _activityList.Children.Clear();
        _interaction.Children.Clear();
        _activity.Visibility = Visibility.Visible;
        Motion.Enter(_activity, 10);
        _resultTimer.Stop();
        _statusLine.Text = editing is null ? "Thinking…" : $"Changing “{editing.Title}”…";
        ShowComposerState(ComposerState.Working);
        AddSay(request, isUser: true);

        _agentCts = new CancellationTokenSource();
        UpdateStatus();
        if (editing is null)
        {
            var size = _sizePreference != "auto" && WidgetSize.TryParse(_sizePreference, out var s) ? s : new CellSize(2, 1);
            _board.ShowGhost(size);
            _board.UpdateLayout();
            _boardScroll.ChangeView(null, _boardScroll.ScrollableHeight, null);
        }
        else if (_board.Tiles.FirstOrDefault(t => t.Widget.Id == editing.Id) is { } editingTile)
        {
            Motion.Breathe(editingTile, 0.55f);
        }
        QueueResize();

        var agent = new WidgetAgent(backend, _host.Actions, new AgentSession(this), DeviceContext.Collect);
        var success = false;
        string title;
        try
        {
            var result = await agent.RunAsync(request, _sizePreference, editing, _agentCts.Token);
            var provider = backend is RoutedBackend r ? r.ProviderId : AiEngineFactory.ResolveEngine(_host.Settings);
            if (AiEngineFactory.NeedsServer(provider, _host.Settings.Ai)) _host.Server.MarkConnected();
            title = result.SavedWidgets.Count switch
            {
                0 => result.Message ?? "Done",
                1 when editing is not null && result.SavedWidgets[0].Id == editing.Id => $"Updated “{result.SavedWidgets[0].Title}”",
                1 => $"Added “{result.SavedWidgets[0].Title}”",
                var n => $"Added {n} widgets",
            };
            if (result.SavedWidgets.Count > 0 && !string.IsNullOrWhiteSpace(result.Message) && result.Message != title) AddSay(result.Message!);
            success = true;
            if (!_visible) Notifications.Show(title, "Open TileSmith to try it.");
            Log.Info($"Agent finished in {result.Turns} turns / {result.Elapsed.TotalSeconds:0}s with {backend.DisplayName}: {title}");
        }
        catch (OperationCanceledException)
        {
            title = "Stopped";
        }
        catch (AiLoginRequiredException ex)
        {
            title = "Sign-in needed";
            AddError(ex.Message);
            AddActionChip("Sign in", "login", () => LocalCliBackend.StartLogin(ex.Cli));
            AddActionChip("Choose another AI", "swap_horiz", ShowModelPicker);
        }
        catch (Exception ex) when (ex is ServerException or HostKeyChangedException)
        {
            _host.Server.MarkFailed(ex.Message);
            title = "Couldn't reach your server";
            AddError(ex.Message);
            AddActionChip("Server settings", "dns", () => OpenSettings("server"));
            AddActionChip("Choose another AI", "swap_horiz", ShowModelPicker);
        }
        catch (Exception ex)
        {
            Log.Error("Agent run failed", ex);
            title = "Couldn't finish that";
            AddError(ex.Message);
            if (ex is AiException { CanFallback: true }) AddActionChip("Choose another AI", "swap_horiz", ShowModelPicker);
        }
        finally
        {
            _agentCts?.Dispose();
            _agentCts = null;
            _approval?.TrySetResult(false);
            _question?.TrySetResult("(no answer)");
            _interaction.Children.Clear();
            _board.HideGhost();
            foreach (var tile in _board.Tiles) Motion.StopBreathe(tile);
            if (editing is not null) ClearEditing();
            UpdateStatus();
        }

        var p = Palette.Current;
        _resultText.Text = title;
        _resultIcon.Text = Icons.Glyph(success ? "check_circle" : "error");
        _resultIcon.Foreground = p.Brush(success ? p.Success : p.Warning);
        _undoChip.Visibility = _undo.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowComposerState(ComposerState.Result);
        if (success)
        {
            _resultTimer.Start();
            _activity.Visibility = Visibility.Collapsed;
        }
        QueueResize();
    }

    private void Undo()
    {
        for (var i = _undo.Count - 1; i >= 0; i--) _undo[i]();
        _undo.Clear();
        _resultText.Text = "Undone";
        _undoChip.Visibility = Visibility.Collapsed;
        _resultTimer.Stop();
        _resultTimer.Start();
    }

    // Activity feed -------------------------------------------------------------------

    private readonly Dictionary<AgentStep, (TextBlock Icon, WorkingScanner Spinner, TextBlock Detail)> _steps = [];

    private void AddSay(string text, bool isUser = false)
    {
        var p = Palette.Current;
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var icon = Glyph.Make(isUser ? "person" : "auto_awesome", 15, isUser ? p.Text3 : p.Accent);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 1, 0, 0);
        row.Children.Add(icon);
        var label = Ui.Text(text, 12.5, isUser ? p.Text2 : p.Text1, maxLines: 6);
        label.IsTextSelectionEnabled = true;
        label.IsHitTestVisible = true;
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        AppendActivity(row);
        if (!isUser && _statusLine is not null)
        {
            _statusLine.Text = text;
            Motion.Enter(_statusLine, 6, 0, Motion.Fast);
        }
    }

    private void AddError(string text)
    {
        var block = Ui.Text(text, 12, Palette.Current.Error, maxLines: 6);
        block.IsTextSelectionEnabled = true;
        block.IsHitTestVisible = true;
        block.Margin = new Thickness(26, 0, 0, 0);
        AppendActivity(block);
    }

    private void AddActionChip(string text, string icon, Action action)
    {
        var chip = new Chip(text, icon) { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(26, 0, 0, 0) };
        chip.Click += (_, _) => action();
        AppendActivity(chip);
    }

    private void AddStep(AgentStep step)
    {
        var p = Palette.Current;
        var row = new Grid { ColumnSpacing = 8, Background = new SolidColorBrush(Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var status = new Grid { Width = 18, Height = 16 };
        var spinner = new WorkingScanner(2.5, 1) { Color = p.Accent, HorizontalAlignment = HorizontalAlignment.Center };
        var icon = Glyph.Make("check", 14, p.Success);
        icon.Visibility = Visibility.Collapsed;
        status.Children.Add(spinner);
        status.Children.Add(icon);
        row.Children.Add(status);
        var title = Ui.TipWhenTrimmed(Ui.Text(step.Title, 12, p.Text2));
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        var chevron = Glyph.Make("expand_more", 14, p.Text3);
        Grid.SetColumn(chevron, 2);
        row.Children.Add(chevron);
        var detail = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 14,
            Foreground = p.Brush(p.Text3),
            IsTextSelectionEnabled = true,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 4, 0, 2),
            Text = step.Detail ?? "",
        };
        Grid.SetRow(detail, 1);
        Grid.SetColumn(detail, 1);
        Grid.SetColumnSpan(detail, 2);
        row.Children.Add(detail);
        row.Tapped += (_, _) =>
        {
            var open = detail.Visibility != Visibility.Visible;
            detail.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = Icons.Glyph(open ? "expand_less" : "expand_more");
            if (open) Motion.Enter(detail, 4, 0, Motion.Fast);
            QueueResize();
        };
        ToolTipService.SetToolTip(row, "Show details");
        _steps[step] = (icon, spinner, detail);
        AppendActivity(row);
        _statusLine.Text = step.Title + "…";
    }

    private void FinishStep(AgentStep step)
    {
        if (!_steps.TryGetValue(step, out var row)) return;
        var p = Palette.Current;
        row.Spinner.Visibility = Visibility.Collapsed;
        row.Icon.Visibility = Visibility.Visible;
        (row.Icon.Text, row.Icon.Foreground) = step.State switch
        {
            StepState.Done => (Icons.Glyph("check"), p.Brush(p.Success)),
            StepState.Failed => (Icons.Glyph("close"), p.Brush(p.Error)),
            _ => (Icons.Glyph("remove"), p.Brush(p.Text3)),
        };
        Motion.CenterScale(row.Icon);
        Motion.Pulse(row.Icon, 1.3f);
        row.Detail.Text = string.IsNullOrWhiteSpace(step.Detail) ? step.Output ?? "" : $"{step.Detail}\n\n→ {step.Output}";
    }

    private void AppendActivity(UIElement element)
    {
        _activityList.Children.Add(element);
        Motion.Enter(element, 6, 0, Motion.Fast);
        QueueResize();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => _activityScroll.ChangeView(null, _activityScroll.ScrollableHeight, null, true));
    }

    // Questions & approvals -----------------------------------------------------------

    private Task<string> AskAsync(string question, IReadOnlyList<string> options)
    {
        var p = Palette.Current;
        _question = new TaskCompletionSource<string>();
        _interaction.Children.Clear();
        _interaction.Children.Add(Ui.Text(question, 13.5, p.Text1, semibold: true, maxLines: 4));
        var chips = new WrapPanel();
        foreach (var option in options)
        {
            var chip = new Chip(option);
            chip.Click += (_, _) => AnswerQuestion(option);
            chips.Children.Add(chip);
        }
        _interaction.Children.Add(chips);
        _activity.Visibility = Visibility.Visible;
        ShowComposerState(ComposerState.Input);
        _prompt.PlaceholderText = "…or type your own answer";
        _prompt.Focus(FocusState.Programmatic);
        if (!_visible) Notifications.Show("TileSmith has a question", question);
        QueueResize();
        return _question.Task;
    }

    private void AnswerQuestion(string answer)
    {
        AddSay(answer, isUser: true);
        _interaction.Children.Clear();
        _prompt.PlaceholderText = "Describe a widget to build…";
        ShowComposerState(ComposerState.Working);
        var tcs = _question;
        _question = null;
        tcs?.TrySetResult(answer);
        QueueResize();
    }

    private Task<bool> ApproveAsync(string title, string detail)
    {
        var p = Palette.Current;
        _approval = new TaskCompletionSource<bool>();
        _interaction.Children.Clear();
        _interaction.Children.Add(Ui.Text(title, 13.5, p.Text1, semibold: true, maxLines: 3));
        var code = new TextBlock
        {
            Text = detail.Length > 4000 ? detail[..4000] + "\n…" : detail,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5,
            Foreground = p.Brush(p.Text2),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        _interaction.Children.Add(new Border
        {
            Background = p.Brush(p.Well),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new ScrollViewer { Content = code, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        });
        var row = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        var allow = new Chip("Allow", "check", filled: true);
        allow.Click += (_, _) => ResolveApproval(true);
        var always = new Chip("Allow all", style: ChipStyle.Outline);
        always.Click += (_, _) =>
        {
            _host.Settings.Ai.Permission = "auto";
            _host.SaveSettings();
            ResolveApproval(true);
        };
        var deny = new Chip("Deny", "block", style: ChipStyle.Ghost);
        deny.Click += (_, _) => ResolveApproval(false);
        row.Children.Add(allow);
        row.Children.Add(always);
        row.Children.Add(deny);
        _interaction.Children.Add(row);
        _activity.Visibility = Visibility.Visible;
        if (!_visible) Notifications.Show("TileSmith needs your OK", title);
        QueueResize();
        return _approval.Task;
    }

    private void ResolveApproval(bool approved)
    {
        _interaction.Children.Clear();
        var tcs = _approval;
        _approval = null;
        tcs?.TrySetResult(approved);
        QueueResize();
    }

    /// <summary>Bridges the agent to the panel. Calls arrive on the UI thread.</summary>
    private sealed class AgentSession(PanelWindow panel) : IAgentHost
    {
        private AppHost Host => panel._host;

        public AgentPermission Permission =>
            Host.Settings.Ai.Permission == "ask" ? AgentPermission.AskForChanges : AgentPermission.Auto;

        public IReadOnlyList<WidgetDefinition> GetWidgets() => [.. Host.Widgets];

        public Task<string> SaveWidgetAsync(WidgetDefinition widget, CancellationToken ct)
        {
            var index = Host.IndexOf(widget.Id);
            if (index >= 0)
            {
                var previous = Host.Widgets[index];
                panel._undo.Add(() => Host.UpsertWidget(previous));
            }
            else
            {
                panel._undo.Add(() => Host.RemoveWidget(widget.Id));
            }
            panel._board.HideGhost();
            panel._celebrate.Add(widget.Id);
            var replaced = Host.UpsertWidget(widget);
            return Task.FromResult(replaced ? "updated on the panel" : "added to the panel");
        }

        public Task<string> RemoveWidgetAsync(string id, CancellationToken ct)
        {
            var index = Host.IndexOf(id);
            var removed = Host.RemoveWidget(id);
            if (removed is null) return Task.FromResult("error: no widget with that id");
            panel._undo.Add(() => Host.InsertWidget(index, removed));
            return Task.FromResult($"removed “{removed.Title}”");
        }

        public Task<string> MoveWidgetAsync(string id, int position, CancellationToken ct)
        {
            var from = Host.IndexOf(id);
            if (!Host.MoveWidget(id, position)) return Task.FromResult("error: no widget with that id");
            panel._undo.Add(() => Host.MoveWidget(id, from));
            return Task.FromResult($"moved to position {Host.IndexOf(id)}");
        }

        public Task<string> ResizeWidgetAsync(string id, string size, CancellationToken ct)
        {
            var index = Host.IndexOf(id);
            if (index < 0) return Task.FromResult("error: no widget with that id");
            var previous = Host.Widgets[index];
            Host.ResizeWidget(id, size);
            panel._undo.Add(() => Host.UpsertWidget(previous));
            return Task.FromResult($"size is now {Host.Widgets[Host.IndexOf(id)].Size}");
        }

        public Task<bool> ApproveAsync(string title, string detail, CancellationToken ct) => panel.ApproveAsync(title, detail);

        public Task<string> AskUserAsync(string question, IReadOnlyList<string> options, CancellationToken ct) => panel.AskAsync(question, options);

        public void OnStatus(string text) => panel._statusLine.Text = text;

        public void OnSay(string text) => panel.AddSay(text);

        public void OnStepStarted(AgentStep step) => panel.AddStep(step);

        public void OnStepFinished(AgentStep step) => panel.FinishStep(step);
    }
}
