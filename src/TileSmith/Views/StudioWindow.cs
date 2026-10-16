using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using TileSmith.Controls;
using TileSmith.Core;
using TileSmith.Core.Ai;
using TileSmith.Core.Remote;
using TileSmith.Core.Tiles;
using TileSmith.Interop;
using TileSmith.Services;
using TileSmith.Theme;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace TileSmith.Views;

/// <summary>One line in the Studio's conversation.</summary>
internal sealed record StudioLine(bool FromUser, string Text, bool Error = false);

/// <summary>
/// Widget Studio, after DailyDash's Tilesmith studio. The conversation is on the left; on the right the widget sits on
/// a stage at its real size with its real data (and works: switches switch), what it still needs is one strip of
/// chips under it, and two panes take the rest: its blocks one at a time, and its look (size, palette, surface,
/// shapes, refresh) — tweaks that need no AI. Every change saves as it happens; Undo steps back through the last
/// twenty. "Add to panel" puts it on the panel, where it then updates live as you keep changing it here.
/// </summary>
public sealed partial class StudioWindow : Window
{
    private const int UndoDepth = 20;
    private const double RightWidth = 580;

    private readonly AppHost _host;
    private readonly IntPtr _hwnd;
    private readonly AppWindow _appWindow;
    private readonly Grid _root = new();

    // state
    private TileDesign? _design;
    private readonly List<StudioLine> _lines = [];
    private readonly List<TileDesign> _undo = [];
    private IReadOnlyList<string> _suggestions = [];
    private IReadOnlyList<TileAsk> _asks = [];
    private CancellationTokenSource? _cts;
    private DateTime _busySince;
    private string? _step;
    private string? _stepDetail;
    private int _dropped;
    private string? _deviceContext;

    // chat column
    private TextBlock _title = null!;
    private TextBlock _subtitle = null!;
    private IconButton _undoButton = null!;
    private StackPanel _messages = null!;
    private ScrollViewer _messagesScroll = null!;
    private WrapPanel _suggestionChips = null!;
    private TextBox _prompt = null!;
    private IconButton _send = null!;
    private Grid _modelChipHost = null!;
    private TextBlock _modelChipText = null!;
    private Flyout? _modelFlyout;
    private Grid? _workingLine;
    private TextBlock? _workingText;
    private readonly DispatcherTimer _workingTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public StudioWindow(AppHost host)
    {
        _host = host;
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        Title = "Widget Studio · TileSmith";
        Content = _root;
        ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        _appWindow.SetIcon(AppInfo.AssetPath("TileSmith.ico"));
        var scale = Native.GetDpiForWindow(_hwnd) / 96.0;
        _appWindow.Resize(new SizeInt32((int)(1240 * scale), (int)(840 * scale)));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(980 * scale);
            presenter.PreferredMinimumHeight = (int)(640 * scale);
        }
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        _appWindow.Move(new PointInt32(area.X + (area.Width - _appWindow.Size.Width) / 2, area.Y + (area.Height - _appWindow.Size.Height) / 2));

        _stageDark = _host.Settings.Theme switch { "light" => false, "dark" => true, _ => Core.Native.ThemeControl.IsDark() };
        _host.SettingsChanged += OnSettingsChanged;
        _host.Library.Changed += OnLibraryChanged;
        _host.Values.ValueChanged += OnValueChanged;
        _workingTimer.Tick += (_, _) => RefreshWorking();
        _stagePoll.Tick += (_, _) => _ = LoadValuesAsync();
        Closed += (_, _) =>
        {
            _host.SettingsChanged -= OnSettingsChanged;
            _host.Library.Changed -= OnLibraryChanged;
            _host.Values.ValueChanged -= OnValueChanged;
            _cts?.Cancel();
            _workingTimer.Stop();
            _stagePoll.Stop();
        };
        Build();
        Render();
        // facts about this PC for the AI, gathered once in the background
        _ = Task.Run(() => _deviceContext = DeviceContext.Collect());
    }

    public bool IsRunning => _cts is not null;

    // ── Dev capture (--capture) ──────────────────────────────────────────────────────

    internal static readonly string[] CaptureStates = ["front", "pulse", "pulse-look", "weather", "sound-dark"];

    internal UIElement CaptureRoot => _root;

    internal Task AskForCaptureAsync(string text) => SendAsync(text);

    internal void PlaceForCapture()
    {
        if (_design is not null && !_host.Library.IsPlaced(_design.Id)) TogglePlaced();
    }

    internal void PrepareCapture(string state)
    {
        Activate();
        switch (state)
        {
            case "front":
                NewWidget();
                break;
            case "pulse" or "pulse-look":
                StartFrom(TileStarters.ById("pulse")!);
                _editing = state == "pulse" ? 0 : -1;
                _tabs.SetSelected(state == "pulse" ? 0 : 1);
                _tab = state == "pulse" ? "blocks" : "look";
                _suggestions = ["Add the disk as a bar", "Use the ocean palette", "Make it a large tile"];
                Render();
                break;
            case "weather":
                StartFrom(TileStarters.ById("weather")!);
                break;
            case "sound-dark":
                _stageDark = !_stageDark;
                StartFrom(TileStarters.ById("sound")!);
                break;
        }
    }

    private Palette P => Palette.Current;

    /// <summary>Opens the Studio: on a design, from a starter, and/or with a request for the AI.</summary>
    public void Open(string? request, TileDesign? design, TileStarters.Starter? starter)
    {
        Activate();
        Native.ForceForeground(_hwnd);
        if (IsRunning) return;
        if (design is not null && design.Id != _design?.Id) SwitchTo(_host.Library.Find(design.Id) ?? design, save: _host.Library.Find(design.Id) is null);
        else if (starter is not null) StartFrom(starter);
        // a request from the panel's prompt is a new widget, not a change to the one open here
        else if (design is null && !string.IsNullOrWhiteSpace(request) && _design is not null) NewWidget();
        if (!string.IsNullOrWhiteSpace(request)) _ = SendAsync(request!);
        else _prompt.Focus(FocusState.Programmatic);
    }

    // ── Opening designs ──────────────────────────────────────────────────────────────

    private void SwitchTo(TileDesign design, bool save = false)
    {
        if (IsRunning) return;
        _undo.Clear();
        _suggestions = [];
        _dropped = 0;
        _lines.Clear();
        _lines.AddRange(design.Chat.Select(t => new StudioLine(t.FromUser, t.Text)));
        if (_lines.Count == 0) _lines.Add(new StudioLine(false, $"Here's {design.Name}. Tell me what to change, or tweak it in Blocks and Look."));
        Show(design, save: save);
        Render();
    }

    private void StartFrom(TileStarters.Starter starter)
    {
        var d = TileStarters.Instantiate(starter);
        foreach (var url in TileStarters.SitesOf(d)) TileApprovals.ApproveSite(_host.Settings.Tiles, url);
        _undo.Clear();
        _lines.Clear();
        _suggestions = [];
        _lines.Add(new StudioLine(false, $"Here's {starter.Title}. Tell me what to change, or add it as it is."));
        Show(d, save: true);
        Render();
    }

    /// <summary>Back to the front door: ideas, starters and your widgets.</summary>
    private void NewWidget()
    {
        if (IsRunning) return;
        _design = null;
        _lines.Clear();
        _undo.Clear();
        _suggestions = [];
        _asks = [];
        _dropped = 0;
        _stagePoll.Stop();
        Render();
        _prompt.Focus(FocusState.Programmatic);
    }

    /// <summary>Puts <paramref name="d"/> on the stage; saved straight away unless it is a draft shown while the AI works.</summary>
    private void Show(TileDesign d, bool save = true, IEnumerable<TileDesign>? undo = null)
    {
        _design = save ? _host.Library.Upsert(d) : d;
        if (undo is not null)
        {
            _undo.Clear();
            _undo.AddRange(undo.TakeLast(UndoDepth));
        }
        Recheck();
        RenderStage();
        RenderPanes();
        RenderHeader();
        _ = LoadValuesAsync(force: false);
        _stagePoll.Interval = TimeSpan.FromSeconds(Math.Max(1, TileRules.EffectiveRefreshSeconds(_design)));
        if (_design.Blocks.Any(b => b.Bind is not null)) _stagePoll.Start(); else _stagePoll.Stop();
    }

    /// <summary>A tweak that needs no AI: applied, saved, undoable.</summary>
    private void Edit(Func<TileDesign, TileDesign> change)
    {
        // while the AI works the stage may hold a draft: nothing is saved until its answer is in
        if (_design is null || IsRunning) return;
        var before = _design;
        var next = TileRules.Sanitize(change(before));
        if (TileCodec.ToJson(next, false).ToJsonString() == TileCodec.ToJson(before, false).ToJsonString()) return;
        Show(next, undo: _undo.Append(before));
    }

    private void Undo()
    {
        if (_undo.Count == 0 || IsRunning) return;
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _dropped = 0;
        Show(previous with { Chat = _design?.Chat ?? previous.Chat }, undo: _undo.ToList());
        Motion.Pulse(_stageHost, 1.02f);
    }

    private void OnLibraryChanged(string? id)
    {
        // the same design changed elsewhere (resized on the panel): show it, without touching the undo stack
        if (id is null || _design is null || id != _design.Id || IsRunning) return;
        var stored = _host.Library.Find(id);
        if (stored is null || ReferenceEquals(stored, _design)) return;
        if (TileCodec.ToJson(stored, false).ToJsonString() == TileCodec.ToJson(_design, false).ToJsonString()) { _design = stored; RenderHeader(); return; }
        _design = stored;
        RenderStage();
        RenderPanes();
        RenderHeader();
    }

    private void OnSettingsChanged()
    {
        UpdateModelChip();
        Recheck();
        RenderStage();
    }

    /// <summary>What the design still needs (a town, an OK for a script…), as chips under the stage.</summary>
    private void Recheck()
    {
        _asks = _design is null ? [] : TileAsks.Check(_design, _host);
        RenderNeeds();
    }

    // ── Layout ───────────────────────────────────────────────────────────────────────

    private void Build()
    {
        _root.RequestedTheme = _host.Settings.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        Palette.Apply(_root.ActualTheme != ElementTheme.Light, _host.Settings.Accent);
        DesignTokens.ApplyToWindow(_root);
        _root.Background = P.Brush(P.Chrome);
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(48) });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.KeyDown += Root_KeyDown;

        var titleBar = new Grid { Padding = new Thickness(18, 0, 150, 0), Background = new SolidColorBrush(Colors.Transparent) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new Image { Width = 18, Height = 18, Source = new BitmapImage(new Uri(AppInfo.AssetPath("AppIcon64.png"))) });
        var title = Ui.Text("Widget Studio", 12.5, P.Text2);
        title.VerticalAlignment = VerticalAlignment.Center;
        titleRow.Children.Add(title);
        titleBar.Children.Add(titleRow);
        _root.Children.Add(titleBar);
        SetTitleBar(titleBar);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 380 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RightWidth) });
        Grid.SetRow(body, 1);
        _root.Children.Add(body);
        body.Children.Add(BuildChat());
        var right = BuildRight();
        Grid.SetColumn(right, 1);
        body.Children.Add(right);
    }

    private FrameworkElement BuildChat()
    {
        var chat = new Grid
        {
            Background = P.Brush(P.Canvas),
            CornerRadius = new CornerRadius(16, 0, 0, 0),
            BorderBrush = P.Brush(P.Hairline),
            BorderThickness = new Thickness(1, 1, 0, 0),
        };
        chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        chat.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        chat.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // header: the widget's name (click to rename), Undo, your widgets, New
        var header = new Grid { Padding = new Thickness(20, 12, 14, 8), ColumnSpacing = 6 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var names = new Pressable { Fill = Colors.Transparent, CornerRadius = new CornerRadius(10), HorizontalAlignment = HorizontalAlignment.Left, AccessibleName = "Rename" };
        var nameStack = new StackPanel { Padding = new Thickness(6, 2, 10, 2) };
        _title = Ui.Text("Widget Studio", 17, P.Text1, semibold: true);
        _subtitle = Ui.Text("", 12, P.Text2);
        nameStack.Children.Add(_title);
        nameStack.Children.Add(_subtitle);
        names.SetContent(nameStack);
        names.Click += (_, _) => _ = RenameAsync();
        header.Children.Add(names);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        _undoButton = new IconButton("undo", "Undo (Ctrl+Z)", 36, 19);
        _undoButton.Click += (_, _) => Undo();
        actions.Children.Add(_undoButton);
        var widgets = new IconButton("widgets", "Your widgets", 36, 19);
        widgets.Click += (_, _) => ShowYourWidgets(widgets);
        actions.Children.Add(widgets);
        var fresh = new Chip("New", "add", style: ChipStyle.Outline, height: 34);
        fresh.Click += (_, _) => NewWidget();
        ToolTipService.SetToolTip(fresh, "Start a new widget (Ctrl+N)");
        actions.Children.Add(fresh);
        Grid.SetColumn(actions, 1);
        header.Children.Add(actions);
        chat.Children.Add(header);

        _messages = new StackPanel { Spacing = 14, MaxWidth = 760 };
        _messagesScroll = new ScrollViewer
        {
            Content = _messages,
            Padding = new Thickness(24, 4, 24, 16),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
        Grid.SetRow(_messagesScroll, 1);
        chat.Children.Add(_messagesScroll);

        // suggestions and the composer, always at the bottom
        var composerArea = new StackPanel { Spacing = 8, Padding = new Thickness(20, 4, 20, 18), MaxWidth = 800 };
        _suggestionChips = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        composerArea.Children.Add(_suggestionChips);
        var composer = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = P.Brush(P.Card),
            BorderBrush = P.Brush(P.HairlineStrong),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 6, 6, 6),
        };
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var modelChip = new Pressable { Height = 36, CornerRadius = new CornerRadius(18), Fill = P.Overlay(0.06), AccessibleName = "Choose the AI", VerticalAlignment = VerticalAlignment.Bottom };
        var modelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Padding = new Thickness(8, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        _modelChipHost = new Grid { Width = 20, Height = 20 };
        modelRow.Children.Add(_modelChipHost);
        _modelChipText = Ui.Text("", 12, P.Text1, semibold: true);
        _modelChipText.VerticalAlignment = VerticalAlignment.Center;
        _modelChipText.MaxWidth = 150;
        modelRow.Children.Add(_modelChipText);
        modelRow.Children.Add(Glyph.Make("expand_more", 15, P.Text3));
        modelChip.SetContent(modelRow);
        modelChip.Click += (_, _) => ShowModelPicker(modelChip);
        ToolTipService.SetToolTip(modelChip, "The AI that builds your widgets (Ctrl+M)");
        row.Children.Add(modelChip);
        _prompt = new TextBox
        {
            PlaceholderText = "Describe a widget, or what to change…",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            MinHeight = 36,
            MaxHeight = 160,
            Padding = new Thickness(4, 8, 4, 8),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsSpellCheckEnabled = true,
        };
        Kit.Transparent(_prompt);
        _prompt.PreviewKeyDown += Prompt_PreviewKeyDown;
        _prompt.TextChanged += (_, _) => RefreshSend();
        Grid.SetColumn(_prompt, 1);
        row.Children.Add(_prompt);
        _send = new IconButton("arrow_upward", "Send (Enter)", 38, 21) { VerticalAlignment = VerticalAlignment.Bottom };
        _send.Click += (_, _) =>
        {
            if (IsRunning) Stop();
            else _ = SendAsync(_prompt.Text);
        };
        Grid.SetColumn(_send, 2);
        row.Children.Add(_send);
        composer.Child = row;
        composerArea.Children.Add(composer);
        var hint = Ui.Text("Enter to send · Shift+Enter for a new line · every change saves as you go", 11, P.Text3);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        composerArea.Children.Add(hint);
        Grid.SetRow(composerArea, 2);
        chat.Children.Add(composerArea);
        UpdateModelChip();
        RefreshSend();
        return chat;
    }

    /// <summary>Draws everything that depends on the state.</summary>
    private void Render()
    {
        RenderHeader();
        RenderMessages();
        RenderSuggestions();
        RenderStage();
        RenderNeeds();
        RenderPanes();
        RefreshSend();
    }

    private void RenderHeader()
    {
        if (_title is null) return;
        var d = _design;
        _title.Text = d?.Name ?? "Widget Studio";
        var placed = d is not null && _host.Library.IsPlaced(d.Id);
        _subtitle.Text = d is null ? "Describe any widget and the AI builds it" : $"{d.Size.Label} {d.Size.Cells}" + (placed ? " · on your panel" : "");
        _undoButton.IsEnabledForInput = _undo.Count > 0 && !IsRunning;
        _undoButton.Opacity = _undoButton.IsEnabledForInput ? 1 : 0.4;
        RefreshPlaceButton();
    }

    // ── Conversation ─────────────────────────────────────────────────────────────────

    private void RenderMessages()
    {
        _messages.Children.Clear();
        if (_design is null && !IsRunning && _lines.Count == 0)
        {
            _messages.Children.Add(BuildFrontDoor());
            return;
        }
        foreach (var line in _lines) _messages.Children.Add(Line(line));
        if (IsRunning) _messages.Children.Add(_workingLine = WorkingLine());
        ScrollToEnd();
    }

    private UIElement Line(StudioLine line)
    {
        if (line.FromUser)
        {
            var bubble = new Border
            {
                Background = P.Brush(P.AccentSoft),
                CornerRadius = new CornerRadius(18, 18, 6, 18),
                Padding = new Thickness(14, 10, 14, 10),
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = 520,
            };
            var label = Ui.Text(line.Text, 13.5, P.Text1, maxLines: 30);
            label.IsTextSelectionEnabled = true;
            label.IsHitTestVisible = true;
            bubble.Child = label;
            return bubble;
        }
        if (line.Error)
        {
            var banner = Kit.Banner(Kit.Tone.Error, "That didn't work", line.Text);
            banner.MaxWidth = 600;
            banner.HorizontalAlignment = HorizontalAlignment.Left;
            return banner;
        }
        var grid = new Grid { ColumnSpacing = 10, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 620 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var avatar = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Background = P.Brush(P.Accent), VerticalAlignment = VerticalAlignment.Top, Child = Glyph.Make("auto_awesome", 15, P.OnAccent) };
        grid.Children.Add(avatar);
        var text = Ui.Text(line.Text, 13.5, P.Text1, maxLines: 40);
        text.IsTextSelectionEnabled = true;
        text.IsHitTestVisible = true;
        text.VerticalAlignment = VerticalAlignment.Center;
        text.Margin = new Thickness(0, 4, 0, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    /// <summary>What the AI is doing right now, with how long it has been at it.</summary>
    private Grid WorkingLine()
    {
        var grid = new Grid { ColumnSpacing = 10, Height = 40 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new WorkingScanner(3, 1.5) { Color = P.Accent, HorizontalAlignment = HorizontalAlignment.Center });
        _workingText = Ui.Text("", 12.5, P.Text2, maxLines: 2);
        _workingText.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_workingText, 1);
        grid.Children.Add(_workingText);
        RefreshWorking();
        return grid;
    }

    private void RefreshWorking()
    {
        if (_workingText is null || !IsRunning) return;
        var seconds = (int)(DateTime.UtcNow - _busySince).TotalSeconds;
        var text = (_step ?? "Thinking") + (_stepDetail is null ? "" : " · " + _stepDetail) + (seconds >= 3 ? $"  ·  {seconds} s" : "");
        if (_workingText.Text != text) _workingText.Text = text;
    }

    private void SetStep(string step, string? detail)
    {
        _step = step;
        _stepDetail = detail;
        RefreshWorking();
    }

    private void RenderSuggestions()
    {
        _suggestionChips.Children.Clear();
        if (IsRunning) return;
        var items = _suggestions.Count > 0 ? _suggestions
                  : _design is null ? []
                  : ["Make it more compact", "Use another palette", "Add a graph"];
        foreach (var s in items)
        {
            var chip = new Chip(s, "auto_awesome", style: ChipStyle.Outline, height: 30);
            chip.Click += (_, _) => _ = SendAsync(s is "Try again" or "Fix this" && _retry is not null ? _retry : s);
            _suggestionChips.Children.Add(chip);
        }
    }

    private void ScrollToEnd() =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => _messagesScroll.ChangeView(null, _messagesScroll.ScrollableHeight, null, false));

    private void Say(string text, bool error = false)
    {
        _lines.Add(new StudioLine(false, text, error));
        RenderMessages();
    }

    // ── Talking to the AI ────────────────────────────────────────────────────────────

    private async Task SendAsync(string text)
    {
        var ask = text.Trim();
        if (ask.Length == 0 || IsRunning) return;
        _prompt.Text = "";
        RememberPrompt(ask);
        if (!_host.AiReady)
        {
            _lines.Add(new StudioLine(true, ask));
            Say("Pick an AI in Settings › AI and I can build this for you.", error: true);
            _suggestions = [];
            RenderSuggestions();
            App.Current.OpenSettings("ai");
            return;
        }
        var current = _design is not null && _host.Library.Find(_design.Id) is not null ? _design : null;
        _lines.Add(new StudioLine(true, ask));
        _suggestions = [];
        _cts = new CancellationTokenSource();
        _busySince = DateTime.UtcNow;
        _step = "Reading your sources";
        _stepDetail = null;
        // the stage is never empty while the AI works: the closest starter goes up as a draft
        if (current is null && TileStarters.Match(ask) is { } match) Show(TileStarters.Instantiate(match) with { Name = match.Title }, save: false);
        Render();
        _workingTimer.Start();

        IAiBackend backend;
        try
        {
            backend = _host.CreateBackend();
        }
        catch (Exception ex)
        {
            Finish();
            Say(ex.Message, error: true);
            return;
        }
        if (backend is RoutedBackend routed)
        {
            routed.Switched += (from, to, reason) => DispatcherQueue.TryEnqueue(() =>
            {
                _lines.Add(new StudioLine(false, $"{from} couldn't answer ({reason}); {to} takes over."));
                RenderMessages();
                UpdateModelChip();
            });
        }
        var sources = SourceStates();
        var context = new TileAi.Context(_deviceContext ?? "", _host.Settings.Tiles.HomeTown, _host.ServerConfigured, _host.ServerConfigured ? _host.ServerName : null);
        try
        {
            var token = _cts.Token;
            var result = await Task.Run(() => TileAi.DraftAsync(backend, ask, sources, context, current,
                p => DispatcherQueue.TryEnqueue(() => SetStep(p.Step, p.Detail)), token), token);
            var now = TileDesign.Now();
            var chat = (current?.Chat ?? []).Append(new TileChatTurn(true, ask, now)).Append(new TileChatTurn(false, result.Reply, now)).TakeLast(24).ToList();
            var design = result.Design with
            {
                Id = current?.Id ?? result.Design.Id,
                CreatedAt = current?.CreatedAt ?? now,
                Chat = chat,
            };
            var reply = string.Join(" ", new[] { result.Reply }.Concat(result.Notes));
            Finish();
            _lines.Add(new StudioLine(false, reply));
            if (current is null && !_host.Library.IsPlaced(design.Id)) _lines.Add(new StudioLine(false, "Press “Add to panel” when you like it, or tell me what to change."));
            _suggestions = result.Suggestions;
            _dropped = 0;
            Show(design, undo: current is null ? _undo : _undo.Append(current));
            Render();
            Motion.Pulse(_stageHost, 1.03f);
            Log.Info($"Studio: designed \"{design.Name}\" in {(DateTime.UtcNow - _busySince).TotalSeconds:0.0}s with {backend.DisplayName}");
        }
        catch (OperationCanceledException)
        {
            Finish();
            // a stopped first draft leaves nothing behind
            if (current is null && _design is not null && _host.Library.Find(_design.Id) is null) _design = null;
            Say("Stopped. Nothing changed.");
            Render();
        }
        catch (AiLoginRequiredException ex)
        {
            Finish();
            Say(ex.Message, error: true);
            _suggestions = [];
            Render();
            LocalCliBackend.StartLogin(ex.Cli);
        }
        catch (Exception ex)
        {
            Log.Error("Studio: the AI failed", ex);
            Finish();
            if (ex is ServerException) _host.Server.MarkFailed(ex.Message);
            if (current is null && _design is not null && _host.Library.Find(_design.Id) is null) _design = null;
            Say(ex.Message.Length > 300 ? ex.Message[..300] + "…" : ex.Message, error: true);
            _suggestions = ["Try again"];
            _retry = ask;
            Render();
        }
    }

    private string? _retry;

    private void Finish()
    {
        _cts?.Dispose();
        _cts = null;
        _workingTimer.Stop();
        _workingLine = null;
        _workingText = null;
        UpdateModelChip();
    }

    private void Stop() => _cts?.Cancel();

    /// <summary>What each source needs right now, for the AI's instructions.</summary>
    private List<TileAi.SourceState> SourceStates()
    {
        var states = new List<TileAi.SourceState>();
        var s = _host.Settings.Tiles;
        foreach (var src in TileSources.All)
        {
            string? missing = null;
            foreach (var need in src.Needs)
            {
                missing ??= need switch
                {
                    TileNeed.Location when string.IsNullOrWhiteSpace(s.HomeTown) => "no town set; name one in query or TileSmith asks",
                    TileNeed.Server when !_host.ServerConfigured => "no server set up",
                    TileNeed.Ai when !_host.AiReady => "no AI set up",
                    _ => null,
                };
            }
            string? extra = src.Key switch
            {
                "pc.battery" when !_host.Environment.HasBattery => "this PC has no battery",
                "sys.brightness" when !_host.Environment.HasBrightness => "this PC can't set brightness",
                "sys.wifi" when !_host.Environment.HasWifi => "this PC has no Wi-Fi",
                "sys.projection" when _host.Environment.Displays < 2 => "only one display is connected now",
                _ => null,
            };
            if (missing is not null || extra is not null) states.Add(new TileAi.SourceState(src.Key, missing is null, missing, extra));
        }
        return states;
    }

    private void RememberPrompt(string text)
    {
        var history = _host.Settings.PromptHistory;
        history.Remove(text);
        history.Insert(0, text);
        if (history.Count > 30) history.RemoveRange(30, history.Count - 30);
        Core.Settings.SettingsStore.Save(_host.Settings);
    }

    private void RefreshSend()
    {
        if (_send is null) return;
        var stop = IsRunning;
        var ready = stop || _prompt.Text.Trim().Length > 0;
        _send.SetIcon(stop ? "stop" : "arrow_upward");
        ToolTipService.SetToolTip(_send, stop ? "Stop" : "Send (Enter)");
        _send.SetColors(ready ? P.Accent : P.Overlay(0.06), ready ? P.OnAccent : P.Text3);
        _send.IsEnabledForInput = ready;
    }

    private void Prompt_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var history = _host.Settings.PromptHistory;
        switch (e.Key)
        {
            case VirtualKey.Enter:
                var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                if (shift) return;
                e.Handled = true;
                _ = SendAsync(_prompt.Text);
                break;
            case VirtualKey.Up when history.Count > 0 && _prompt.Text.Length == 0:
                e.Handled = true;
                _prompt.Text = history[0];
                _prompt.SelectionStart = _prompt.Text.Length;
                break;
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;
        switch (e.Key)
        {
            case VirtualKey.N:
                e.Handled = true;
                NewWidget();
                break;
            case VirtualKey.M:
                e.Handled = true;
                ShowModelPicker(_modelChipHost);
                break;
            case VirtualKey.Z when FocusManager.GetFocusedElement(_root.XamlRoot) is not TextBox:
                e.Handled = true;
                Undo();
                break;
            case VirtualKey.Enter when _design is not null:
                e.Handled = true;
                TogglePlaced();
                break;
        }
    }

    private async Task RenameAsync()
    {
        if (_design is null) return;
        var box = new TextBox { Text = _design.Name, MaxLength = 40 };
        var dialog = new ContentDialog
        {
            Title = "Name",
            Content = box,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _root.XamlRoot,
            RequestedTheme = _root.ActualTheme,
        };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var name = box.Text.Trim();
        if (name.Length > 0) Edit(d => d with { Name = name });
    }

    // ── The AI in use ────────────────────────────────────────────────────────────────

    private void UpdateModelChip()
    {
        if (_modelChipHost is null) return;
        var provider = AiEngineFactory.ResolveProvider(_host.Settings);
        _modelChipHost.Children.Clear();
        _modelChipHost.Children.Add(ProviderLogo.Make(provider, 20, tile: false));
        _modelChipText.Text = ProviderCatalog.ModelLabel(provider, _host.Settings.Ai.ModelFor(provider.Id));
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
        _modelFlyout.Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedLeft;
        _modelFlyout.ShowAt(anchor);
    }
}
