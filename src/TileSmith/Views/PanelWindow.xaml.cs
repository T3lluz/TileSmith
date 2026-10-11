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
/// The panel: an Android-style sheet that rises from the taskbar corner. Clock and status up top, a
/// launcher-like grid of widgets, and the AI composer at the bottom that builds widgets right into the grid.
/// </summary>
public sealed partial class PanelWindow : Window
{
    private const double SurfacePadding = 16;
    private const double ShadowMargin = 18;
    private const double EdgeGap = 14;
    private const double CellWidth = 118;

    private readonly AppHost _host;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly List<Action> _undo = [];
    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _resultTimer = new() { Interval = TimeSpan.FromSeconds(7) };

    // chrome
    private Grid _surface = null!;
    private TextBlock _clock = null!;
    private TextBlock _date = null!;
    private LiveDot _statusDot = null!;
    private TextBlock _statusText = null!;
    private Pressable _statusChip = null!;
    private IconButton _pinButton = null!;
    private IconButton _editButton = null!;
    private Grid _settingsBadge = null!;
    private StackPanel _banners = null!;
    private ScrollViewer _boardScroll = null!;
    private TileBoard _board = null!;
    private TextBlock _emptyHint = null!;

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
    private string? _celebrateId;
    private TaskCompletionSource<bool>? _approval;
    private TaskCompletionSource<string>? _question;
    private bool _updateDismissed;
    private bool _resizeQueued;
    private string? _styleApplied;

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

        Rebuild();
    }

    public bool IsOpen => _visible;

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
    }

    private void OnSettingsChanged()
    {
        var before = Palette.Current.Accent;
        Palette.Apply(Root.ActualTheme != ElementTheme.Light, _host.Settings.Accent);
        if (_styleApplied != (Solid ? "solid" : "glass") || before != Palette.Current.Accent) Rebuild();
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

        _surface.Children.Add(BuildHeader());

        _banners = new StackPanel { Spacing = 8, Margin = new Thickness(SurfacePadding, 0, SurfacePadding, 0) };
        Grid.SetRow(_banners, 1);
        _surface.Children.Add(_banners);

        _board = new TileBoard { CellWidth = CellWidth, HorizontalAlignment = HorizontalAlignment.Center };
        _board.EditModeRequested += () => SetEditMode(true);
        _board.OrderChanged += OnOrderChanged;
        _board.Resized += OnResized;
        _board.AddRequested += () => { SetEditMode(false); _prompt.Focus(FocusState.Programmatic); };
        _board.GestureEnded += QueueResize;
        _emptyHint = Ui.Text("Describe a widget below — it'll appear right here.", 13, p.Text3, maxLines: 2);
        _emptyHint.HorizontalAlignment = HorizontalAlignment.Center;
        _emptyHint.Margin = new Thickness(0, 24, 0, 24);
        var boardStack = new StackPanel();
        boardStack.Children.Add(_board);
        boardStack.Children.Add(_emptyHint);
        _boardScroll = new ScrollViewer
        {
            Content = boardStack,
            Padding = new Thickness(SurfacePadding, 4, SurfacePadding, 6),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
        Grid.SetRow(_boardScroll, 2);
        _surface.Children.Add(_boardScroll);

        var ai = BuildAiArea();
        Grid.SetRow(ai, 3);
        _surface.Children.Add(ai);

        var shadowHost = new Grid { IsHitTestVisible = false };
        Root.Children.Add(shadowHost); // behind the surface: child visuals draw above their host's content
        Root.Children.Add(_surface);
        if (Solid) Motion.AttachShadow(shadowHost, _surface, blur: 26, opacity: p.IsDark ? 0.55f : 0.22f, offsetY: 8, radius: 28);

        LoadTiles();
        UpdateClock();
        UpdateStatus();
        UpdateBanners();
        QueueResize();
    }

    private UIElement BuildHeader()
    {
        var p = Palette.Current;
        var header = new Grid { Padding = new Thickness(SurfacePadding + 6, 14, SurfacePadding - 2, 8), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var time = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        _clock = Ui.Text("", 26, p.Text1, semibold: true);
        _clock.FontFamily = new FontFamily("Segoe UI Variable Display");
        _date = Ui.Text("", 13, p.Text2);
        _date.VerticalAlignment = VerticalAlignment.Bottom;
        _date.Margin = new Thickness(0, 0, 0, 5);
        time.Children.Add(_clock);
        time.Children.Add(_date);
        header.Children.Add(time);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        _statusChip = new Pressable { Height = 30, CornerRadius = new CornerRadius(15), Fill = p.Overlay(0.06), Margin = new Thickness(0, 0, 6, 0) };
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(12, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
        _statusDot = new LiveDot(7);
        _statusText = Ui.Text("", 12, p.Text2, semibold: true);
        _statusText.MaxWidth = 170;
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        chipRow.Children.Add(_statusDot);
        chipRow.Children.Add(_statusText);
        _statusChip.SetContent(chipRow);
        _statusChip.Click += (_, _) => OpenSettings("connection");
        actions.Children.Add(_statusChip);

        _pinButton = new IconButton("keep_off", "Keep open", 34, 18);
        _pinButton.Click += (_, _) => SetPinned(!_pinned);
        actions.Children.Add(_pinButton);
        _editButton = new IconButton("edit", "Edit tiles", 34, 18);
        _editButton.Click += (_, _) => SetEditMode(!_editMode);
        actions.Children.Add(_editButton);

        var settingsHost = new Grid();
        var settings = new IconButton("settings", "Settings", 34, 18);
        settings.Click += (_, _) => OpenSettings(_host.Updates.HasUpdate ? "updates" : null);
        settingsHost.Children.Add(settings);
        _settingsBadge = new Grid { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = p.Brush(p.Accent), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 6, 0), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        settingsHost.Children.Add(_settingsBadge);
        actions.Children.Add(settingsHost);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        RefreshHeaderButtons();
        return header;
    }

    private FrameworkElement BuildAiArea()
    {
        var p = Palette.Current;
        var area = new StackPanel { Spacing = 8, Padding = new Thickness(SurfacePadding - 4, 0, SurfacePadding - 4, SurfacePadding - 4) };

        // live activity (steps, questions, approvals)
        _activityList = new StackPanel { Spacing = 6 };
        _activityScroll = new ScrollViewer { Content = _activityList, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
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
            Height = 52,
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
        var row = new Grid { ColumnSpacing = 6, Padding = new Thickness(14, 0, 6, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        row.Children.Add(Glyph.Make("auto_awesome", 20, p.Accent));

        _prompt = new TextBox
        {
            PlaceholderText = _editing is null ? "Ask TileSmith to build a widget…" : "What should change?",
            VerticalAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 6, 6, 6),
            FontSize = 14,
            AcceptsReturn = false,
            IsSpellCheckEnabled = false,
        };
        StyleTextBox(_prompt);
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
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(18, 0, 8, 0), Visibility = Visibility.Collapsed };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // tapping the status shows/hides the step-by-step activity
        var tap = new Pressable { CornerRadius = new CornerRadius(26), Background = new SolidColorBrush(Colors.Transparent), Margin = new Thickness(-18, 0, 0, 0), AccessibleName = "Show steps" };
        tap.Click += (_, _) =>
        {
            _activity.Visibility = _activity.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            QueueResize();
        };
        Grid.SetColumnSpan(tap, 2);
        row.Children.Add(tap);
        _scanner = new WorkingScanner(5, 2) { Color = p.Accent, IsHitTestVisible = false };
        row.Children.Add(_scanner);
        _statusLine = Ui.Text("Working…", 13.5, p.Text1);
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
        var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(18, 0, 8, 0), Visibility = Visibility.Collapsed };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _resultIcon = Glyph.Make("check_circle", 20, p.Success);
        row.Children.Add(_resultIcon);
        _resultText = Ui.Text("", 13.5, p.Text1);
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

    private static void StyleTextBox(TextBox box)
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
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);
        box.SelectionHighlightColor = p.Brush(Palette.WithAlpha(p.Accent, 0.45));
    }

    private Chip MakeSizeChip()
    {
        var chip = new Chip(_sizePreference == "auto" ? "Auto" : _sizePreference.Replace('x', '×'), "grid_view", height: 30) { VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(chip, "Widget size — Auto lets the AI choose");
        chip.Click += (_, _) => SizeMenu().ShowAt(chip);
        Grid.SetColumn(chip, 2);
        return chip;
    }

    private MenuFlyout SizeMenu()
    {
        var menu = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Top };
        foreach (var (id, text) in new[] { ("auto", "Auto — let the AI choose"), ("1x1", "1×1 icon tile"), ("2x1", "2×1 Quick Settings tile"), ("2x2", "2×2 square"), ("4x1", "4×1 full-width bar"), ("4x2", "4×2 large card") })
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
                tile.Failed += (t, message) => ShowNotice($"{t.Widget.Title}: {message}", warning: true);
                tiles.Add(tile);
                if (_visible) { _ = tile.RefreshAsync(); tile.StartPolling(); }
                if (widget.Id == _celebrateId)
                {
                    _celebrateId = null;
                    tile.Loaded += (_, _) => tile.Celebrate();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Couldn't render widget \"{widget.Title}\"", ex);
            }
        }
        _board.Load(tiles);
        _board.IsEditMode = _editMode;
        _emptyHint.Visibility = tiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            return;
        }
        // automatic: 4 columns (≈4:3 panel), widening to 5 once 4 would need many rows
        var cells = _host.Widgets.Sum(w => { var c = w.Cells; return Math.Min(c.W, 4) * c.H; });
        _board.Columns = cells > 4 * 6 ? 5 : 4;
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
        var error = Ui.Text("", 12, Palette.Current.Error, maxLines: 6);
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
                _celebrateId = edited.Id;
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
        _editMode = on;
        if (_board is not null) _board.IsEditMode = on;
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
        ToolTipService.SetToolTip(_editButton, _editMode ? "Done" : "Edit tiles (or long-press a tile)");
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
        PositionWindow();
        if (!_visible)
        {
            _visible = true;
            _lastShown = DateTime.UtcNow;
            _appWindow.Show(true);
            Activate();
            Native.ForceForeground(_hwnd);
            Motion.PanelIn(_surface, fromBottom: true);
            var i = 0;
            foreach (var tile in _board.Tiles)
            {
                Motion.Enter(tile, 8, Math.Min(i++ * 18, 220), Motion.Normal);
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
        _appWindow.Hide();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState != WindowActivationState.Deactivated || !_visible) return;
        if (_dialogOpen || _pinned) return;
        if ((DateTime.UtcNow - _lastShown).TotalMilliseconds < 250) return;
        Hide();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || _dialogOpen) return;
        e.Handled = true;
        if (_editMode) SetEditMode(false);
        else if (_editing is not null) ClearEditing();
        else Hide();
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
        var surfaceWidth = _board.BoardWidth + 2 * SurfacePadding + 6; // + border and layout-rounding slack
        var surfaceHeight = MeasureSurface(surfaceWidth, maxSurfaceHeight);
        var m = ShadowSpace;
        var width = (int)Math.Round((surfaceWidth + 2 * m) * scale);
        var height = (int)Math.Round((surfaceHeight + 2 * m) * scale);
        var gap = (int)Math.Round((EdgeGap - m) * scale);

        var x = work.X + work.Width - width - gap;
        var y = work.Y + work.Height - height - gap;
        if (work.X > outer.X) x = work.X + gap;
        if (work.Y > outer.Y) y = work.Y + gap;
        _appWindow.MoveAndResize(new RectInt32(x, y, width, height));
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
        if (_statusText is null) return;
        var p = Palette.Current;
        var engine = AiEngineFactory.ResolveEngine(_host.Settings);
        string text;
        Color color;
        var live = false;
        if (engine is "claude" or "cursor")
        {
            text = engine == "claude" ? "Claude" : "Cursor";
            color = p.Accent;
        }
        else if (engine == "openai" && !_host.Settings.Ai.OpenAiViaTunnel)
        {
            text = _host.Settings.Ai.OpenAiModel ?? "API";
            color = p.Accent;
        }
        else
        {
            var server = _host.Server;
            var name = engine == "hermes" ? "Hermes" : "AI";
            (text, color, live) = server.State switch
            {
                ConnectionState.Connected => ($"{name} · {server.DisplayName}", p.Success, true),
                ConnectionState.Connecting => ("Connecting…", p.Warning, false),
                ConnectionState.Error => ($"{server.DisplayName} offline", p.Error, false),
                ConnectionState.NotConfigured => ("Set up AI", p.Text3, false),
                _ => ($"{name} · {server.DisplayName}", p.Text3, false),
            };
            ToolTipService.SetToolTip(_statusChip, server.StatusText);
        }
        if (_agentCts is not null)
        {
            color = p.Accent;
            live = true;
        }
        _statusText.Text = text;
        _statusDot.Set(color, live);
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

        var setup = AiEngineFactory.NeedsServer(AiEngineFactory.ResolveEngine(_host.Settings), _host.Settings.Ai) && !_host.Settings.Server.IsConfigured;
        if (setup)
        {
            _updateBanner = Banner("hub", "Connect your AI", "Pick your server or your Claude / Cursor subscription.", "Set up", () => OpenSettings("connection"), p.Accent);
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
        if (message is not null) texts.Children.Add(Ui.Text(message, 12, p.Text2, maxLines: 2));
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
        _banners.Children.Remove(_noticeBanner);
        _noticeBanner = null;
        _banners.Margin = new Thickness(SurfacePadding, 0, SurfacePadding, _banners.Children.Count > 0 ? 8 : 0);
        QueueResize();
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
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        _ = SendAsync();
    }

    private void ShowSuggestions(bool show)
    {
        if (_suggestions is null) return;
        show &= _agentCts is null && _question is null && _prompt.Text.Length == 0;
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
                var rates = main.Modes.Where(m => m.Width == main.Current.Width && m.Height == main.Current.Height).Select(m => m.RefreshRate).Distinct().OrderDescending().ToList();
                if (rates.Count >= 2 && rates.Contains(60))
                    list.Add(("Refresh rate", "speed", $"Toggle my display between {rates[0]} Hz and 60 Hz"));
            }
        }
        catch { }
        list.Add(("Media controls", "play_circle", "Media controls: previous, play/pause and next"));
        if (_host.Settings.Server.IsConfigured) list.Add(("Server status", "dns", "Show my server's uptime, load and free disk space"));
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
        _prompt.PlaceholderText = "What should change?";
        ShowComposerState(ComposerState.Input);
        _prompt.Focus(FocusState.Programmatic);
        QueueResize();
    }

    private void ClearEditing()
    {
        _editing = null;
        _editingChip.Visibility = Visibility.Collapsed;
        _prompt.PlaceholderText = "Ask TileSmith to build a widget…";
        QueueResize();
    }

    private async Task SendAsync()
    {
        var text = _prompt.Text.Trim();
        if (text.Length == 0) return;
        if (_question is not null)
        {
            _prompt.Text = "";
            AnswerQuestion(text);
            return;
        }
        if (_agentCts is not null) return;
        await RunAgentAsync(text);
    }

    private async Task RunAgentAsync(string request)
    {
        var engine = AiEngineFactory.ResolveEngine(_host.Settings);
        if (AiEngineFactory.NeedsServer(engine, _host.Settings.Ai) && !_host.Settings.Server.IsConfigured)
        {
            UpdateBanners();
            return;
        }
        IAiBackend backend;
        try
        {
            backend = _host.CreateBackend();
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, warning: true, "Settings", () => OpenSettings("connection"));
            return;
        }

        var editing = _editing;
        _prompt.Text = "";
        ShowSuggestions(false);
        _editingChip.Visibility = Visibility.Collapsed;
        _undo.Clear();
        _steps.Clear();
        _activityList.Children.Clear();
        _interaction.Children.Clear();
        _activity.Visibility = Visibility.Collapsed;
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
            if (AiEngineFactory.NeedsServer(engine, _host.Settings.Ai)) _host.Server.MarkConnected();
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
            Log.Info($"Agent finished in {result.Turns} turns / {result.Elapsed.TotalSeconds:0}s: {title}");
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
            _activity.Visibility = Visibility.Visible;
        }
        catch (Exception ex) when (ex is ServerException or HostKeyChangedException)
        {
            _host.Server.MarkFailed(ex.Message);
            title = "Couldn't reach your server";
            AddError(ex.Message);
            AddActionChip("Open settings", "settings", () => OpenSettings("connection"));
            _activity.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            Log.Error("Agent run failed", ex);
            title = "Couldn't finish that";
            AddError(ex.Message);
            _activity.Visibility = Visibility.Visible;
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
        if (success) _resultTimer.Start();
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
        AppendActivity(block);
    }

    private void AddActionChip(string text, string icon, Action action)
    {
        var chip = new Chip(text, icon) { HorizontalAlignment = HorizontalAlignment.Left };
        chip.Click += (_, _) => action();
        AppendActivity(chip);
    }

    private void AddStep(AgentStep step)
    {
        var p = Palette.Current;
        var row = new Grid { ColumnSpacing = 8, Background = new SolidColorBrush(Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var status = new Grid { Width = 18, Height = 16 };
        var spinner = new WorkingScanner(2.5, 1) { Color = p.Accent, HorizontalAlignment = HorizontalAlignment.Center };
        var icon = Glyph.Make("check", 14, p.Success);
        icon.Visibility = Visibility.Collapsed;
        status.Children.Add(spinner);
        status.Children.Add(icon);
        row.Children.Add(status);
        var title = Ui.Text(step.Title, 12, p.Text2);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
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
        row.Children.Add(detail);
        row.Tapped += (_, _) =>
        {
            detail.Visibility = detail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
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
        row.Detail.Text = string.IsNullOrWhiteSpace(step.Detail) ? step.Output ?? "" : $"{step.Detail}\n\n→ {step.Output}";
    }

    private void AppendActivity(UIElement element)
    {
        _activityList.Children.Add(element);
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
        if (!_visible) Notifications.Show("TileSmith has a question", question);
        QueueResize();
        return _question.Task;
    }

    private void AnswerQuestion(string answer)
    {
        AddSay(answer, isUser: true);
        _interaction.Children.Clear();
        _prompt.PlaceholderText = "Ask TileSmith to build a widget…";
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
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var allow = new Chip("Allow", "check", filled: true);
        allow.Click += (_, _) => ResolveApproval(true);
        var always = new Chip("Allow all");
        always.Click += (_, _) =>
        {
            _host.Settings.Ai.Permission = "auto";
            _host.SaveSettings();
            ResolveApproval(true);
        };
        var deny = new Chip("Deny", "block");
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
            panel._celebrateId = widget.Id;
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
