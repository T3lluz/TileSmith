using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TileSmith.Controls;
using TileSmith.Core;
using TileSmith.Core.Actions;
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
/// Widget Studio: chat with the AI on the left, the widget it builds on the right — live, at its real size, with its
/// real data — so you can keep asking for changes until it's right, step back to an earlier version, and only then put
/// it on the panel. The conversation carries over between messages, so "make it bigger" knows what "it" is.
/// </summary>
public sealed class StudioWindow : Window
{
    private const double PreviewWidth = 520;
    private const string StudioContext = """
        You are in TileSmith's Widget Studio. Each widget you save becomes a draft the user sees right away in a live
        preview next to this chat (with its real data); it is NOT on the panel until they press "Add to panel". They
        will look at it and ask for changes, so keep the widget's id and change only what they ask for. Build one widget
        at a time here. After saving, say in one short sentence what you made or changed.
        """;

    private readonly AppHost _host;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly Grid _root = new();
    private readonly List<AiMessage> _conversation = [];
    private readonly List<WidgetDefinition> _versions = [];

    // chat
    private StackPanel _messages = null!;
    private ScrollViewer _messagesScroll = null!;
    private FrameworkElement _welcome = null!;
    private TextBox _prompt = null!;
    private IconButton _send = null!;
    private WrapPanel _refinements = null!;
    private Grid _modelChipHost = null!;
    private TextBlock _modelChipText = null!;
    private Flyout? _modelFlyout;

    // the turn being shown
    private StackPanel? _turnBody;
    private Grid? _statusRow;
    private TextBlock? _statusText;
    private StackPanel? _stepsList;
    private TextBlock? _stepsHeader;
    private readonly Dictionary<AgentStep, (Grid Slot, TextBlock Detail)> _steps = [];

    // preview
    private Grid _stage = null!;
    private StackPanel _versionChips = null!;
    private StackPanel _checks = null!;
    private Grid _sizeHost = null!;
    private Chip _addButton = null!;
    private Chip _jsonButton = null!;
    private Border _jsonHost = null!;
    private TextBox _jsonBox = null!;
    private WidgetTile? _previewTile;
    private int _shownVersion = -1;

    private CancellationTokenSource? _cts;
    private TaskCompletionSource<string>? _question;
    private TaskCompletionSource<bool>? _approval;
    private string? _targetId;
    private CancellationTokenSource? _checkCts;

    public StudioWindow(AppHost host)
    {
        _host = host;
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        Title = "TileSmith Studio";
        Content = _root;
        ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        _appWindow.SetIcon(AppInfo.AssetPath("TileSmith.ico"));
        var scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        _appWindow.Resize(new SizeInt32((int)(1180 * scale), (int)(800 * scale)));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(920 * scale);
            presenter.PreferredMinimumHeight = (int)(600 * scale);
        }
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        _appWindow.Move(new PointInt32(area.X + (area.Width - _appWindow.Size.Width) / 2, area.Y + (area.Height - _appWindow.Size.Height) / 2));

        _host.SettingsChanged += UpdateModelChip;
        _host.Widgets.CollectionChanged += OnWidgetsChanged;
        Closed += (_, _) =>
        {
            _host.SettingsChanged -= UpdateModelChip;
            _host.Widgets.CollectionChanged -= OnWidgetsChanged;
            _cts?.Cancel();
            _checkCts?.Cancel();
            _previewTile?.StopPolling();
        };
        Build();
    }

    public bool IsRunning => _cts is not null;

    private void OnWidgetsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => RefreshAddButton();

    /// <summary>Opens the studio: with a request it starts building; with a panel widget it loads that widget to change.</summary>
    public void Open(string? request, WidgetDefinition? widget)
    {
        if (widget is not null && !IsRunning && widget.Id != _targetId)
        {
            NewSession();
            _targetId = widget.Id;
            AddVersion(widget.Clone(), fromAi: false);
            AddNote($"Loaded “{widget.Title}” from your panel. Tell me what to change.");
        }
        Activate();
        Native.ForceForeground(_hwnd);
        if (!string.IsNullOrWhiteSpace(request) && !IsRunning) _ = SendAsync(request!);
        else _prompt.Focus(FocusState.Programmatic);
    }

    // ── Layout ──────────────────────────────────────────────────────────────────────

    private void Build()
    {
        _root.RequestedTheme = _host.Settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        Palette.Apply(_root.ActualTheme != ElementTheme.Light, _host.Settings.Accent);
        DesignTokens.ApplyToWindow(_root);
        var p = Palette.Current;
        _root.Background = p.Brush(p.Chrome);
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.KeyDown += Root_KeyDown;

        var titleBar = new Grid { Padding = new Thickness(18, 0, 150, 0), Background = new SolidColorBrush(Colors.Transparent) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new Image { Width = 18, Height = 18, Source = new BitmapImage(new Uri(AppInfo.AssetPath("AppIcon64.png"))) });
        var title = Ui.Text("TileSmith Studio", 12.5, p.Text2);
        title.VerticalAlignment = VerticalAlignment.Center;
        titleRow.Children.Add(title);
        titleBar.Children.Add(titleRow);
        _root.Children.Add(titleBar);
        SetTitleBar(titleBar);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(PreviewWidth) });
        Grid.SetRow(body, 1);
        _root.Children.Add(body);

        body.Children.Add(BuildChat());
        var preview = BuildPreview();
        Grid.SetColumn(preview, 1);
        body.Children.Add(preview);
        RenderPreview();
    }

    private FrameworkElement BuildChat()
    {
        var p = Palette.Current;
        var chat = new Grid
        {
            Background = p.Brush(p.Canvas),
            CornerRadius = new CornerRadius(16, 0, 0, 0),
            BorderBrush = p.Brush(p.Hairline),
            BorderThickness = new Thickness(1, 1, 0, 0),
        };
        chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chat.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // header: new chat + the AI in use
        var header = new Grid { Padding = new Thickness(20, 12, 16, 8), ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = Ui.Text("Chat", 15, p.Text1, semibold: true);
        heading.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(heading);
        var modelChip = new Pressable { Height = 34, CornerRadius = new CornerRadius(17), Fill = p.Overlay(0.06), AccessibleName = "Choose the AI" };
        var modelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(10, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        _modelChipHost = new Grid { Width = 18, Height = 18 };
        modelRow.Children.Add(_modelChipHost);
        _modelChipText = Ui.Text("", 12.5, p.Text1, semibold: true);
        _modelChipText.VerticalAlignment = VerticalAlignment.Center;
        _modelChipText.MaxWidth = 200;
        modelRow.Children.Add(_modelChipText);
        modelRow.Children.Add(Glyph.Make("expand_more", 16, p.Text3));
        modelChip.SetContent(modelRow);
        modelChip.Click += (_, _) => ShowModelPicker(modelChip);
        ToolTipService.SetToolTip(modelChip, "Choose the AI (Ctrl+M)");
        Grid.SetColumn(modelChip, 1);
        header.Children.Add(modelChip);
        var newChat = new Chip("New widget", "add", style: ChipStyle.Outline, height: 34);
        newChat.Click += (_, _) => { if (!IsRunning) NewSession(); };
        ToolTipService.SetToolTip(newChat, "Start over (Ctrl+N)");
        Grid.SetColumn(newChat, 2);
        header.Children.Add(newChat);
        chat.Children.Add(header);
        UpdateModelChip();

        // messages
        _messages = new StackPanel { Spacing = 16, MaxWidth = 760 };
        _welcome = BuildWelcome();
        var stack = new StackPanel();
        stack.Children.Add(_welcome);
        stack.Children.Add(_messages);
        _messagesScroll = new ScrollViewer
        {
            Content = stack,
            Padding = new Thickness(24, 8, 24, 16),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
        Grid.SetRow(_messagesScroll, 1);
        chat.Children.Add(_messagesScroll);

        // composer
        var composerArea = new StackPanel { Spacing = 8, Padding = new Thickness(20, 4, 20, 18), MaxWidth = 800 };
        _refinements = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6, MinHeight = 32 };
        composerArea.Children.Add(_refinements);
        var composer = new Border
        {
            CornerRadius = new CornerRadius(22),
            Background = p.Brush(p.Card),
            BorderBrush = p.Brush(p.HairlineStrong),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(16, 6, 6, 6),
        };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _prompt = new TextBox
        {
            PlaceholderText = "Describe a widget, or what to change…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            MinHeight = 40,
            MaxHeight = 160,
            Padding = new Thickness(0, 9, 0, 9),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsSpellCheckEnabled = true,
        };
        Kit.Transparent(_prompt);
        _prompt.PreviewKeyDown += Prompt_PreviewKeyDown;
        _prompt.TextChanged += (_, _) => RefreshSend();
        row.Children.Add(_prompt);
        _send = new IconButton("arrow_upward", "Send (Enter)", 40, 22) { VerticalAlignment = VerticalAlignment.Bottom };
        _send.Click += (_, _) =>
        {
            if (IsRunning && _question is null) _cts?.Cancel();
            else _ = SendAsync(_prompt.Text);
        };
        Grid.SetColumn(_send, 1);
        row.Children.Add(_send);
        composer.Child = row;
        composerArea.Children.Add(composer);
        var hint = Ui.Text("Enter to send · Shift+Enter for a new line · taps in the preview run for real", 11, p.Text3);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        composerArea.Children.Add(hint);
        Grid.SetRow(composerArea, 2);
        chat.Children.Add(composerArea);
        RefreshSend();
        RefreshRefinements();
        return chat;
    }

    private FrameworkElement BuildWelcome()
    {
        var p = Palette.Current;
        var stack = new StackPanel { Spacing = 14, Padding = new Thickness(0, 28, 0, 8), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Stretch };
        stack.Children.Add(Ui.Text("What should we build?", 26, p.Text1, semibold: true));
        stack.Children.Add(Kit.Body("Describe it in your own words. The AI looks at your PC, builds the widget and shows it on the right with your real data — then keep chatting until it's exactly right.", 13.5, p.Text2));
        var ideas = new WrapPanel { HorizontalSpacing = 10, VerticalSpacing = 10, Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (icon, title, prompt) in Ideas())
        {
            var card = new Pressable { Width = 230, Height = 76, CornerRadius = new CornerRadius(18), Fill = p.Card, AccessibleName = title };
            card.BorderBrush = p.Brush(p.Hairline);
            card.BorderThickness = new Thickness(1);
            var grid = new Grid { Padding = new Thickness(14, 0, 12, 0), ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(12), Background = p.Brush(p.AccentSoft), Child = Glyph.Make(icon, 19, p.Accent), VerticalAlignment = VerticalAlignment.Center });
            var text = Ui.Text(title, 13, p.Text1, semibold: true, maxLines: 2);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            card.SetContent(grid);
            card.Click += (_, _) => _ = SendAsync(prompt);
            ideas.Children.Add(card);
        }
        stack.Children.Add(ideas);
        return stack;
    }

    private IEnumerable<(string Icon, string Title, string Prompt)> Ideas()
    {
        yield return ("partly_cloudy_day", "Weather where I live", "A weather widget for where I live with the temperature, conditions and the next days — ask me where that is");
        yield return ("monitoring", "System dashboard", "A dashboard with CPU, memory and disk as gauges, refreshing every few seconds");
        yield return ("music_note", "Now playing", "Show what's playing with the cover art and play/pause, next and previous buttons");
        yield return ("leaderboard", "Apps using my CPU", "A list of the five apps using the most CPU right now, with their usage");
        yield return ("network_check", "Network speed", "My network: Wi-Fi name and signal, IP address and live download/upload speed");
        yield return ("event", "Countdown", "A countdown to a date — ask me which date and what it's for");
        if (_host.Settings.Server.IsConfigured) yield return ("deployed_code", "My server's containers", "A list of the Docker containers running on my server with their status");
        else yield return ("vpn_lock", "VPN toggle", "A toggle for my VPN connection that shows whether it's connected");
    }

    private FrameworkElement BuildPreview()
    {
        var p = Palette.Current;
        var grid = new Grid { Padding = new Thickness(20, 12, 24, 18), RowSpacing = 12 };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { ColumnSpacing = 10, MinHeight = 34 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Ui.Text("Preview", 15, p.Text1, semibold: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        _versionChips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(_versionChips, 1);
        header.Children.Add(_versionChips);
        grid.Children.Add(header);

        // the stage: a piece of panel the draft sits on, at its real size
        _stage = new Grid
        {
            CornerRadius = new CornerRadius(26),
            Background = p.Brush(p.Canvas),
            BorderBrush = p.Brush(p.Hairline),
            BorderThickness = new Thickness(1),
        };
        Grid.SetRow(_stage, 1);
        grid.Children.Add(_stage);

        var below = new StackPanel { Spacing = 10 };
        _sizeHost = new Grid();
        below.Children.Add(_sizeHost);
        _checks = new StackPanel { Spacing = 4 };
        below.Children.Add(_checks);
        _jsonBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5,
            Height = 200,
            IsSpellCheckEnabled = false,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_jsonBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_jsonBox, ScrollBarVisibility.Auto);
        var jsonStack = new StackPanel { Spacing = 8 };
        jsonStack.Children.Add(_jsonBox);
        jsonStack.Children.Add(Kit.Button("Apply JSON", "check", ChipStyle.Tonal, ApplyJson));
        _jsonHost = new Border { Child = jsonStack, Visibility = Visibility.Collapsed };
        below.Children.Add(_jsonHost);
        Grid.SetRow(below, 2);
        grid.Children.Add(below);

        var actions = new Grid { ColumnSpacing = 8 };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _jsonButton = new Chip("JSON", "data_object", style: ChipStyle.Ghost, height: 38);
        _jsonButton.Click += (_, _) =>
        {
            _jsonHost.Visibility = _jsonHost.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (_jsonHost.Visibility == Visibility.Visible && Draft is { } draft) _jsonBox.Text = draft.ToJson();
        };
        actions.Children.Add(_jsonButton);
        _addButton = new Chip("Add to panel", "add_to_photos", filled: true, height: 40);
        _addButton.Click += (_, _) => AddToPanel();
        Grid.SetColumn(_addButton, 2);
        actions.Children.Add(_addButton);
        Grid.SetRow(actions, 3);
        grid.Children.Add(actions);
        return grid;
    }

    // ── Preview ─────────────────────────────────────────────────────────────────────

    private WidgetDefinition? Draft => _shownVersion >= 0 && _shownVersion < _versions.Count ? _versions[_shownVersion] : null;

    private void AddVersion(WidgetDefinition widget, bool fromAi)
    {
        if (_targetId is not null) widget.Id = _targetId;
        else if (_versions.Count > 0) widget.Id = _versions[^1].Id; // one widget per session: versions share an id
        _versions.Add(widget);
        _shownVersion = _versions.Count - 1;
        RenderPreview();
        if (fromAi) Motion.Pulse(_stage, 1.02f);
    }

    private void RenderPreview()
    {
        var p = Palette.Current;
        _previewTile?.StopPolling();
        _previewTile = null;
        _stage.Children.Clear();
        _versionChips.Children.Clear();
        _sizeHost.Children.Clear();
        var draft = Draft;
        if (draft is null)
        {
            _stage.Background = p.Brush(p.Canvas);
            var empty = Kit.Empty("widgets", "Your widget appears here", "Live, with your real data — try it before it goes on the panel.");
            empty.VerticalAlignment = VerticalAlignment.Center;
            _stage.Children.Add(empty);
            _checks.Children.Clear();
            RefreshAddButton();
            return;
        }

        for (var i = 0; i < _versions.Count; i++)
        {
            var index = i;
            var chip = new Chip($"v{i + 1}", style: i == _shownVersion ? ChipStyle.Filled : ChipStyle.Ghost, height: 28);
            ToolTipService.SetToolTip(chip, i == _shownVersion ? "Shown now" : "Show this version");
            chip.Click += (_, _) =>
            {
                _shownVersion = index;
                RenderPreview();
            };
            _versionChips.Children.Add(chip);
        }

        // the preview uses the panel's own look: glass tiles over a stand-in wallpaper, or the solid panel colour
        var size = draft.Cells;
        var glass = _host.Settings.PanelStyle != "solid" && GlassBackdrop.IsSupported;
        _stage.Background = glass ? Glass.PreviewWallpaper(p.IsDark) : p.Brush(p.Canvas);
        WidgetTile tile;
        using (Palette.Use(Palette.For(p.IsDark, _host.Settings.Accent, glass)))
        {
            tile = new WidgetTile(draft, _host)
            {
                Width = TileLayout.Width(size.W),
                Height = TileLayout.Height(size.H),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ContextFlyout = null,
            };
        }
        tile.Failed += (_, message) => ShowCheckLine("error", $"When you used it: {message}", p.Error);
        _stage.Children.Add(tile);
        _previewTile = tile;
        Motion.PopIn(tile);
        _ = tile.RefreshAsync();
        tile.StartPolling();

        // sizes the content fits in
        var minimum = TileLayout.Minimum(draft);
        var sizes = new[] { new CellSize(1, 1), new(2, 1), new(4, 1), new(2, 2), new(4, 2), new(2, 3), new(4, 3) }
            .Where(s => s.W >= minimum.W && s.H >= minimum.H).ToList();
        if (sizes.Count > 1)
        {
            var segmented = Kit.Segmented(sizes.Select(s => (s.ToString(), $"{s.W}×{s.H}")).ToList(), size.ToString(), id =>
            {
                var copy = draft.Clone();
                copy.Size = id;
                WidgetValidator.Validate(copy);
                AddVersion(copy, fromAi: false);
            }, Math.Min(PreviewWidth - 44, sizes.Count * 64), 34);
            _sizeHost.Children.Add(segmented);
        }

        if (_jsonHost.Visibility == Visibility.Visible) _jsonBox.Text = draft.ToJson();
        RefreshAddButton();
        _ = CheckAsync(draft);
    }

    /// <summary>Runs the draft's queries and lists what works and what doesn't, so problems are visible before it's added.</summary>
    private async Task CheckAsync(WidgetDefinition draft)
    {
        _checkCts?.Cancel();
        var cts = _checkCts = new CancellationTokenSource();
        var p = Palette.Current;
        _checks.Children.Clear();
        if (!draft.AllActions().Any(a => ActionCatalog.Find(a["type"]?.ToString())?.IsQuery == true))
        {
            ShowCheckLine("touch_app", "No live data — it acts when you tap it.", p.Text3);
            return;
        }
        ShowCheckLine("hourglass_top", "Checking its data…", p.Text3);
        VerifyResult result;
        try
        {
            result = await WidgetVerifier.VerifyAsync(draft, _host.Actions, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested) return;
        _checks.Children.Clear();
        if (result.Problems.Count == 0 && result.Notes.Count == 0) ShowCheckLine("check_circle", "All of its data loads correctly.", p.Success);
        foreach (var problem in result.Problems.Take(4)) ShowCheckLine("error", problem, p.Error);
        foreach (var note in result.Notes.Take(3)) ShowCheckLine("info", note, p.Warning);
        if (result.Problems.Count > 0)
        {
            var fix = Kit.Button("Ask the AI to fix this", "auto_fix_high", ChipStyle.Tonal, () =>
                _ = SendAsync("The preview shows these problems — please fix them:\n- " + string.Join("\n- ", result.Problems)));
            fix.HorizontalAlignment = HorizontalAlignment.Left;
            _checks.Children.Add(fix);
        }
    }

    private void ShowCheckLine(string icon, string text, Color color)
    {
        var p = Palette.Current;
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var glyph = Glyph.Make(icon, 15, color);
        glyph.VerticalAlignment = VerticalAlignment.Top;
        row.Children.Add(glyph);
        var label = Ui.Text(text, 12, p.Text2, maxLines: 3);
        label.IsTextSelectionEnabled = true;
        label.IsHitTestVisible = true;
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        _checks.Children.Add(row);
    }

    private void RefreshAddButton()
    {
        if (_addButton is null) return;
        var draft = Draft;
        var onPanel = draft is not null && _host.IndexOf(draft.Id) >= 0;
        var same = onPanel && _host.Widgets[_host.IndexOf(draft!.Id)].ToJson() == draft.ToJson();
        _addButton.Text = same ? "On the panel" : onPanel ? "Update on panel" : "Add to panel";
        _addButton.SetIcon(same ? "check" : onPanel ? "sync" : "add_to_photos");
        _addButton.IsEnabledForInput = draft is not null && !same;
        _addButton.Opacity = _addButton.IsEnabledForInput ? 1 : 0.55;
    }

    private void AddToPanel()
    {
        if (Draft is not { } draft) return;
        var widget = draft.Clone();
        App.Current.Celebrate(widget.Id);
        var updated = _host.UpsertWidget(widget);
        _targetId = widget.Id;
        AddNote(updated ? $"Updated “{widget.Title}” on your panel." : $"Added “{widget.Title}” to your panel.", "check_circle");
        RefreshAddButton();
    }

    private void ApplyJson()
    {
        try
        {
            var widget = WidgetDefinition.FromJson(_jsonBox.Text);
            var result = WidgetValidator.Validate(widget);
            if (!result.IsValid)
            {
                _checks.Children.Clear();
                foreach (var error in result.Errors.Take(5)) ShowCheckLine("error", error, Palette.Current.Error);
                return;
            }
            AddVersion(result.Widget, fromAi: false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidDataException)
        {
            _checks.Children.Clear();
            ShowCheckLine("error", "That isn't valid widget JSON: " + ex.Message, Palette.Current.Error);
        }
    }

    // ── Chat ────────────────────────────────────────────────────────────────────────

    private void NewSession()
    {
        _cts?.Cancel();
        _conversation.Clear();
        _versions.Clear();
        _shownVersion = -1;
        _targetId = null;
        _messages.Children.Clear();
        _welcome.Visibility = Visibility.Visible;
        _turnBody = null;
        RenderPreview();
        RefreshRefinements();
        _prompt.Focus(FocusState.Programmatic);
    }

    private void RefreshSend()
    {
        if (_send is null) return;
        var p = Palette.Current;
        var stop = IsRunning && _question is null;
        var ready = stop || _prompt.Text.Trim().Length > 0;
        _send.SetIcon(stop ? "stop" : "arrow_upward");
        ToolTipService.SetToolTip(_send, stop ? "Stop" : "Send (Enter)");
        _send.SetColors(ready ? p.Accent : p.Overlay(0.06), ready ? p.OnAccent : p.Text3);
        _send.IsEnabledForInput = ready;
    }

    /// <summary>One-tap follow-ups once there's a draft (the row keeps its height, so nothing jumps).</summary>
    private void RefreshRefinements()
    {
        if (_refinements is null) return;
        _refinements.Children.Clear();
        if (Draft is null || IsRunning)
        {
            var tip = Ui.Text(IsRunning ? "" : "Tip: say what it should show and do — the AI checks this PC before building.", 11.5, Palette.Current.Text3);
            tip.VerticalAlignment = VerticalAlignment.Center;
            tip.Margin = new Thickness(6, 7, 0, 0);
            _refinements.Children.Add(tip);
            return;
        }
        foreach (var (label, prompt) in new[]
                 {
                     ("More compact", "Make it more compact — smaller, with less text, keeping what matters"),
                     ("Bigger", "Make it bigger and show more detail"),
                     ("Show a graph", "Add a small graph of how the main value changed recently"),
                     ("Different look", "Give it a fresh look — different layout and colours, same content"),
                     ("Simplify", "Simplify it: fewer elements, one clear main value"),
                 })
        {
            var chip = new Chip(label, style: ChipStyle.Outline, height: 30);
            chip.Click += (_, _) => _ = SendAsync(prompt);
            _refinements.Children.Add(chip);
        }
    }

    private void Prompt_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (shift) return;
        e.Handled = true;
        _ = SendAsync(_prompt.Text);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;
        switch (e.Key)
        {
            case VirtualKey.N when !IsRunning:
                e.Handled = true;
                NewSession();
                break;
            case VirtualKey.M:
                e.Handled = true;
                ShowModelPicker(_modelChipHost);
                break;
            case VirtualKey.Enter when Draft is not null:
                e.Handled = true;
                AddToPanel();
                break;
        }
    }

    private async Task SendAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return;
        if (_question is not null)
        {
            _prompt.Text = "";
            AnswerQuestion(text);
            return;
        }
        if (IsRunning) return;
        _prompt.Text = "";
        RememberPrompt(text);
        await RunAsync(text);
    }

    private void RememberPrompt(string text)
    {
        var history = _host.Settings.PromptHistory;
        history.Remove(text);
        history.Insert(0, text);
        if (history.Count > 30) history.RemoveRange(30, history.Count - 30);
        Core.Settings.SettingsStore.Save(_host.Settings);
    }

    private async Task RunAsync(string request)
    {
        _welcome.Visibility = Visibility.Collapsed;
        AddUserBubble(request);
        IAiBackend backend;
        try
        {
            backend = _host.CreateBackend();
        }
        catch (Exception ex)
        {
            BeginTurn();
            AddError(ex.Message);
            AddActionChip("AI settings", "tune", () => App.Current.OpenSettings("ai"));
            EndTurn();
            return;
        }
        if (backend is RoutedBackend routed)
        {
            routed.Switched += (from, to, reason) => DispatcherQueue.TryEnqueue(() =>
            {
                AddSay($"{from} couldn't be reached ({reason}) — {to} takes over.");
                UpdateModelChip();
            });
        }

        BeginTurn();
        _cts = new CancellationTokenSource();
        RefreshSend();
        RefreshRefinements();
        var agent = new WidgetAgent(backend, _host.Actions, new Session(this), DeviceContext.Collect)
        {
            Conversation = _conversation,
            Context = StudioContext,
        };
        try
        {
            var result = await agent.RunAsync(request, "auto", Draft, _cts.Token);
            var provider = backend is RoutedBackend r ? r.ProviderId : AiEngineFactory.ResolveEngine(_host.Settings);
            if (AiEngineFactory.NeedsServer(provider, _host.Settings.Ai)) _host.Server.MarkConnected();
            if (!string.IsNullOrWhiteSpace(result.Message)) AddSay(result.Message!);
            if (result.SavedWidgets.Count > 0)
            {
                var hint = _host.IndexOf(Draft?.Id ?? "") >= 0 ? "Press “Update on panel” when you like it." : "Press “Add to panel” when you like it — or tell me what to change.";
                AddNote($"Draft v{_versions.Count} is in the preview. {hint}", "auto_awesome");
            }
            Log.Info($"Studio run finished in {result.Turns} turns / {result.Elapsed.TotalSeconds:0}s with {backend.DisplayName}");
        }
        catch (OperationCanceledException)
        {
            AddNote("Stopped.", "stop_circle");
        }
        catch (AiLoginRequiredException ex)
        {
            AddError(ex.Message);
            AddActionChip("Sign in", "login", () => LocalCliBackend.StartLogin(ex.Cli));
            AddActionChip("Choose another AI", "swap_horiz", () => ShowModelPicker(_modelChipHost));
        }
        catch (Exception ex) when (ex is ServerException or HostKeyChangedException)
        {
            _host.Server.MarkFailed(ex.Message);
            AddError(ex.Message);
            AddActionChip("Server settings", "dns", () => App.Current.OpenSettings("server"));
            AddActionChip("Choose another AI", "swap_horiz", () => ShowModelPicker(_modelChipHost));
        }
        catch (Exception ex)
        {
            Log.Error("Studio run failed", ex);
            AddError(ex.Message);
            if (ex is AiException { CanFallback: true }) AddActionChip("Choose another AI", "swap_horiz", () => ShowModelPicker(_modelChipHost));
            AddActionChip("Try again", "refresh", () => _ = SendAsync(request));
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _approval?.TrySetResult(false);
            _question?.TrySetResult("(no answer)");
            _question = null;
            _approval = null;
            EndTurn();
            RefreshSend();
            RefreshRefinements();
            UpdateModelChip();
        }
    }

    // ── Messages ────────────────────────────────────────────────────────────────────

    private void AddUserBubble(string text)
    {
        var p = Palette.Current;
        var bubble = new Border
        {
            Background = p.Brush(p.AccentSoft),
            CornerRadius = new CornerRadius(18, 18, 6, 18),
            Padding = new Thickness(14, 10, 14, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth = 520,
        };
        var label = Ui.Text(text, 13.5, p.Text1, maxLines: 30);
        label.IsTextSelectionEnabled = true;
        label.IsHitTestVisible = true;
        bubble.Child = label;
        Append(bubble);
    }

    /// <summary>An AI turn: its logo, what it says, a collapsible list of steps and a status line that changes in place.</summary>
    private void BeginTurn()
    {
        var p = Palette.Current;
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var avatar = ProviderLogo.Make(AiEngineFactory.ResolveProvider(_host.Settings), 30);
        avatar.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(avatar);
        _turnBody = new StackPanel { Spacing = 8 };
        Grid.SetColumn(_turnBody, 1);
        grid.Children.Add(_turnBody);

        // steps: a quiet card, collapsed to its header line
        var stepsCard = new Border
        {
            Background = p.Brush(p.Card),
            BorderBrush = p.Brush(p.Hairline),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 8, 12, 8),
            Visibility = Visibility.Collapsed,
        };
        var stepsStack = new StackPanel { Spacing = 6 };
        var stepsHeaderRow = new Grid { ColumnSpacing = 8, Background = new SolidColorBrush(Colors.Transparent) };
        stepsHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stepsHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        stepsHeaderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        stepsHeaderRow.Children.Add(Glyph.Make("checklist", 15, p.Text3));
        _stepsHeader = Ui.Text("Steps", 12, p.Text2, semibold: true);
        Grid.SetColumn(_stepsHeader, 1);
        stepsHeaderRow.Children.Add(_stepsHeader);
        var chevron = Glyph.Make("expand_more", 15, p.Text3);
        Grid.SetColumn(chevron, 2);
        stepsHeaderRow.Children.Add(chevron);
        _stepsList = new StackPanel { Spacing = 5, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        var list = _stepsList;
        stepsHeaderRow.Tapped += (_, _) =>
        {
            var open = list.Visibility != Visibility.Visible;
            list.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = Icons.Glyph(open ? "expand_less" : "expand_more");
        };
        stepsStack.Children.Add(stepsHeaderRow);
        stepsStack.Children.Add(_stepsList);
        stepsCard.Child = stepsStack;
        _turnBody.Children.Add(stepsCard);

        // status: scanner + one line of text, fixed height
        _statusRow = new Grid { ColumnSpacing = 10, Height = 22 };
        _statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _statusRow.Children.Add(new WorkingScanner(4, 2) { Color = p.Accent });
        _statusText = Ui.Text("Thinking…", 12.5, p.Text2);
        _statusText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_statusText, 1);
        _statusRow.Children.Add(_statusText);
        _turnBody.Children.Add(_statusRow);
        _steps.Clear();
        Append(grid);
    }

    private void EndTurn()
    {
        if (_statusRow is not null && _turnBody is not null) _turnBody.Children.Remove(_statusRow);
        _statusRow = null;
        _statusText = null;
    }

    private void SetStatus(string text)
    {
        if (_statusText is null || _statusText.Text == text) return;
        _statusText.Text = text;
    }

    /// <summary>Adds to the current turn above its status line.</summary>
    private void AddToTurn(UIElement element)
    {
        if (_turnBody is null) BeginTurn();
        var index = _statusRow is not null ? _turnBody!.Children.IndexOf(_statusRow) : _turnBody!.Children.Count;
        _turnBody.Children.Insert(index < 0 ? _turnBody.Children.Count : index, element);
        Motion.Enter(element, 0, 0, Motion.Fast);
        ScrollToEnd();
    }

    private void AddSay(string text)
    {
        var label = Ui.Text(text, 13.5, Palette.Current.Text1, maxLines: 20);
        label.IsTextSelectionEnabled = true;
        label.IsHitTestVisible = true;
        AddToTurn(label);
    }

    private void AddNote(string text, string icon = "info")
    {
        var p = Palette.Current;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Glyph.Make(icon, 16, icon is "check_circle" or "auto_awesome" ? p.Accent : p.Text3));
        var label = Ui.Text(text, 12.5, p.Text2, maxLines: 4);
        label.MaxWidth = 600;
        row.Children.Add(label);
        if (_turnBody is not null && IsRunning) AddToTurn(row);
        else
        {
            row.Margin = new Thickness(42, 0, 0, 0);
            _welcome.Visibility = Visibility.Collapsed;
            Append(row);
        }
    }

    private void AddError(string text)
    {
        var banner = Kit.Banner(Kit.Tone.Error, "That didn't work", text);
        AddToTurn(banner);
    }

    private void AddActionChip(string text, string icon, Action action)
    {
        var chip = new Chip(text, icon) { HorizontalAlignment = HorizontalAlignment.Left };
        chip.Click += (_, _) => action();
        AddToTurn(chip);
    }

    private void AddStep(AgentStep step)
    {
        if (_turnBody is null || _stepsList is null) return;
        var p = Palette.Current;
        ((FrameworkElement)_turnBody.Children[0]).Visibility = Visibility.Visible;
        var row = new Grid { ColumnSpacing = 8, Background = new SolidColorBrush(Colors.Transparent) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        // a fixed 18×16 slot: the spinner and the result icon swap in place
        var slot = new Grid { Width = 18, Height = 16 };
        slot.Children.Add(new WorkingScanner(1.6, 0.8) { Color = p.Accent, HorizontalAlignment = HorizontalAlignment.Center });
        row.Children.Add(slot);
        var title = Ui.TipWhenTrimmed(Ui.Text(step.Title, 12, p.Text2));
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        var detail = new TextBlock
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 16,
            Foreground = p.Brush(p.Text3),
            IsTextSelectionEnabled = true,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 3, 0, 2),
            Text = step.Detail ?? "",
        };
        Grid.SetRow(detail, 1);
        Grid.SetColumn(detail, 1);
        row.Children.Add(detail);
        row.Tapped += (_, _) => detail.Visibility = detail.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(row, "Show details");
        _steps[step] = (slot, detail);
        _stepsList.Children.Add(row);
        if (_stepsHeader is not null) _stepsHeader.Text = $"{_steps.Count} step{(_steps.Count == 1 ? "" : "s")}";
        SetStatus(step.Title + "…");
    }

    private void FinishStep(AgentStep step)
    {
        if (!_steps.TryGetValue(step, out var row)) return;
        var p = Palette.Current;
        row.Slot.Children.Clear();
        var (icon, color) = step.State switch
        {
            StepState.Done => ("check", p.Success),
            StepState.Failed => ("close", p.Error),
            _ => ("remove", p.Text3),
        };
        row.Slot.Children.Add(Glyph.Make(icon, 14, color));
        row.Detail.Text = string.IsNullOrWhiteSpace(step.Detail) ? step.Output ?? "" : $"{step.Detail}\n\n→ {step.Output}";
    }

    private void Append(UIElement element)
    {
        _messages.Children.Add(element);
        Motion.Enter(element, 8, 0, Motion.Fast);
        ScrollToEnd();
    }

    private void ScrollToEnd() =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => _messagesScroll.ChangeView(null, _messagesScroll.ScrollableHeight, null, false));

    // ── Questions and approvals ─────────────────────────────────────────────────────

    private Task<string> AskAsync(string question, IReadOnlyList<string> options)
    {
        var p = Palette.Current;
        _question = new TaskCompletionSource<string>();
        var card = new Border
        {
            Background = p.Brush(p.Card),
            BorderBrush = p.Brush(Palette.WithAlpha(p.Accent, 0.5)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 12, 14, 12),
        };
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Ui.Text(question, 13.5, p.Text1, semibold: true, maxLines: 6));
        var chips = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        foreach (var option in options)
        {
            var chip = new Chip(option, style: ChipStyle.Outline);
            chip.Click += (_, _) => AnswerQuestion(option);
            chips.Children.Add(chip);
        }
        stack.Children.Add(chips);
        stack.Children.Add(Ui.Text("…or type your own answer below.", 11.5, p.Text3));
        card.Child = stack;
        AddToTurn(card);
        SetStatus("Waiting for your answer");
        _prompt.PlaceholderText = "Your answer…";
        _prompt.Focus(FocusState.Programmatic);
        RefreshSend();
        if (!IsActiveWindow) Notifications.Show("TileSmith has a question", question);
        return _question.Task;
    }

    private void AnswerQuestion(string answer)
    {
        var tcs = _question;
        _question = null;
        _prompt.PlaceholderText = "Describe a widget, or what to change…";
        var p = Palette.Current;
        var reply = new Border
        {
            Background = p.Brush(p.AccentSoft),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 6, 12, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = Ui.Text(answer, 12.5, p.Text1, maxLines: 6),
        };
        AddToTurn(reply);
        SetStatus("Thinking…");
        RefreshSend();
        tcs?.TrySetResult(answer);
    }

    private Task<bool> ApproveAsync(string title, string detail)
    {
        var p = Palette.Current;
        _approval = new TaskCompletionSource<bool>();
        var card = new Border
        {
            Background = p.Brush(p.Card),
            BorderBrush = p.Brush(Palette.WithAlpha(p.Warning, 0.6)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 12, 14, 12),
        };
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(Ui.Text(title, 13.5, p.Text1, semibold: true, maxLines: 3));
        var code = new TextBlock
        {
            Text = detail.Length > 4000 ? detail[..4000] + "\n…" : detail,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5,
            Foreground = p.Brush(p.Text2),
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        stack.Children.Add(new Border
        {
            Background = p.Brush(p.Well),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8),
            Child = new ScrollViewer { Content = code, MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        });
        var buttons = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        var allow = new Chip("Allow", "check", filled: true);
        var always = new Chip("Allow all from now on", style: ChipStyle.Outline);
        var deny = new Chip("Deny", "block", style: ChipStyle.Ghost);
        void Resolve(bool ok)
        {
            buttons.Visibility = Visibility.Collapsed;
            stack.Children.Add(Ui.Text(ok ? "Allowed" : "Denied", 12, ok ? p.Success : p.Text3, semibold: true));
            var tcs = _approval;
            _approval = null;
            tcs?.TrySetResult(ok);
        }
        allow.Click += (_, _) => Resolve(true);
        always.Click += (_, _) =>
        {
            _host.Settings.Ai.Permission = "auto";
            _host.SaveSettings();
            Resolve(true);
        };
        deny.Click += (_, _) => Resolve(false);
        buttons.Children.Add(allow);
        buttons.Children.Add(always);
        buttons.Children.Add(deny);
        stack.Children.Add(buttons);
        card.Child = stack;
        AddToTurn(card);
        SetStatus("Waiting for your OK");
        if (!IsActiveWindow) Notifications.Show("TileSmith needs your OK", title);
        return _approval.Task;
    }

    private bool IsActiveWindow => Native.GetForegroundWindow() == _hwnd;

    // ── The AI in use ───────────────────────────────────────────────────────────────

    private void UpdateModelChip()
    {
        if (_modelChipHost is null) return;
        var provider = AiEngineFactory.ResolveProvider(_host.Settings);
        _modelChipHost.Children.Clear();
        _modelChipHost.Children.Add(ProviderLogo.Make(provider, 18, tile: false));
        _modelChipText.Text = ProviderCatalog.ModelLabel(provider, _host.Settings.Ai.ModelFor(provider.Id)) + (_host.Settings.Ai.Engine == "auto" ? " · auto" : "");
    }

    private void ShowModelPicker(FrameworkElement anchor)
    {
        var picker = new ModelPicker(_host, (providerId, model) =>
        {
            _modelFlyout?.Hide();
            if (providerId == "auto") _host.Settings.Ai.Engine = "auto";
            else
            {
                _host.Settings.Ai.Engine = providerId;
                _host.Settings.Ai.SetModel(providerId, model);
            }
            _host.SaveSettings();
            UpdateModelChip();
        }, () =>
        {
            _modelFlyout?.Hide();
            App.Current.OpenSettings("ai");
        });
        _modelFlyout = ModelPicker.Flyout(picker);
        _modelFlyout.ShowAt(anchor);
    }

    /// <summary>Bridges the agent to the studio. Calls arrive on the UI thread.</summary>
    private sealed class Session(StudioWindow studio) : IAgentHost
    {
        private AppHost Host => studio._host;

        public AgentPermission Permission => Host.Settings.Ai.Permission == "ask" ? AgentPermission.AskForChanges : AgentPermission.Auto;

        public IReadOnlyList<WidgetDefinition> GetWidgets() => [.. Host.Widgets];

        public Task<string> SaveWidgetAsync(WidgetDefinition widget, CancellationToken ct)
        {
            studio.AddVersion(widget.Clone(), fromAi: true);
            studio.RefreshRefinements();
            var onPanel = Host.IndexOf(studio.Draft!.Id) >= 0;
            return Task.FromResult($"saved as draft v{studio._versions.Count} and shown in the live preview" +
                                   (onPanel ? " (the panel keeps the old version until the user presses Update)" : " (not on the panel until the user adds it)"));
        }

        public Task<string> RemoveWidgetAsync(string id, CancellationToken ct)
        {
            var removed = Host.RemoveWidget(id);
            return Task.FromResult(removed is null ? "error: no widget with that id" : $"removed “{removed.Title}” from the panel");
        }

        public Task<string> MoveWidgetAsync(string id, int position, CancellationToken ct) =>
            Task.FromResult(Host.MoveWidget(id, position) ? $"moved to position {Host.IndexOf(id)}" : "error: no widget with that id");

        public Task<string> ResizeWidgetAsync(string id, string size, CancellationToken ct)
        {
            if (Host.IndexOf(id) < 0) return Task.FromResult("error: no widget with that id");
            Host.ResizeWidget(id, size);
            return Task.FromResult($"size is now {Host.Widgets[Host.IndexOf(id)].Size}");
        }

        public Task<bool> ApproveAsync(string title, string detail, CancellationToken ct) => studio.ApproveAsync(title, detail);

        public Task<string> AskUserAsync(string question, IReadOnlyList<string> options, CancellationToken ct) => studio.AskAsync(question, options);

        public void OnStatus(string text) => studio.SetStatus(text);

        public void OnSay(string text) => studio.AddSay(text);

        public void OnStepStarted(AgentStep step) => studio.AddStep(step);

        public void OnStepFinished(AgentStep step) => studio.FinishStep(step);
    }
}
