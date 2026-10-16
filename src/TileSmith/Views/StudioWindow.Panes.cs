using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using TileSmith.Controls;
using TileSmith.Controls.Tiles;
using TileSmith.Core;
using TileSmith.Core.Actions;
using TileSmith.Core.Tiles;
using TileSmith.Theme;
using Windows.UI;

namespace TileSmith.Views;

public sealed partial class StudioWindow : ITileActions
{
    private bool _stageDark;
    private readonly DispatcherTimer _stagePoll = new();
    private Grid _stageHost = null!;
    private Grid _stage = null!;
    private TileView? _stageView;
    private WrapPanel _needs = null!;
    private TextBlock _droppedNote = null!;
    private Chip _placeButton = null!;
    private IconButton _darkButton = null!;
    private SegmentedPill _tabs = null!;
    private StackPanel _pane = null!;
    private ScrollViewer _paneScroll = null!;
    private string _tab = "blocks";
    private int _editing = -1;
    private IReadOnlyDictionary<string, TileValue> _values = new Dictionary<string, TileValue>();

    private FrameworkElement BuildRight()
    {
        var grid = new Grid { Padding = new Thickness(20, 10, 22, 16), RowSpacing = 10 };
        for (var i = 0; i < 6; i++) grid.RowDefinitions.Add(new RowDefinition { Height = i == 5 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        // the stage header: what it is, light / dark, add to panel
        var header = new Grid { ColumnSpacing = 8, MinHeight = 36 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Ui.Text("Preview", 15, P.Text1, semibold: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(title);
        _darkButton = new IconButton("dark_mode", "See it in dark or light mode", 34, 18) { VerticalAlignment = VerticalAlignment.Center };
        _darkButton.Click += (_, _) =>
        {
            _stageDark = !_stageDark;
            RenderStage();
        };
        Grid.SetColumn(_darkButton, 1);
        header.Children.Add(_darkButton);
        _placeButton = new Chip("Add to panel", "add_to_photos", filled: true, height: 36);
        _placeButton.Click += (_, _) => TogglePlaced();
        Grid.SetColumn(_placeButton, 2);
        header.Children.Add(_placeButton);
        grid.Children.Add(header);

        // the stage: a piece of the panel the widget sits on, at its real size
        _stageHost = new Grid { CornerRadius = new CornerRadius(26), MinHeight = 200 };
        Motion.CenterScale(_stageHost);
        _stage = new Grid { Padding = new Thickness(24) };
        _stageHost.Children.Add(_stage);
        Grid.SetRow(_stageHost, 1);
        grid.Children.Add(_stageHost);

        _needs = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        Grid.SetRow(_needs, 2);
        grid.Children.Add(_needs);
        _droppedNote = Ui.Text("", 12, P.Warning, maxLines: 2);
        _droppedNote.Visibility = Visibility.Collapsed;
        Grid.SetRow(_droppedNote, 3);
        grid.Children.Add(_droppedNote);

        _tabs = new SegmentedPill([("Blocks", "view_quilt"), ("Look", "palette"), ("JSON", "data_object")], 36) { Margin = new Thickness(0, 4, 0, 0) };
        _tabs.SetSelected(0);
        _tabs.Selected += i =>
        {
            _tab = i switch { 0 => "blocks", 1 => "look", _ => "json" };
            RenderPanes();
        };
        Grid.SetRow(_tabs, 4);
        grid.Children.Add(_tabs);

        _pane = new StackPanel { Spacing = 10 };
        _paneScroll = new ScrollViewer { Content = _pane, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Disabled, Padding = new Thickness(0, 2, 8, 0) };
        Grid.SetRow(_paneScroll, 5);
        grid.Children.Add(_paneScroll);
        return grid;
    }

    // ── The stage ────────────────────────────────────────────────────────────────────

    private void RenderStage()
    {
        if (_stage is null) return;
        var glass = _host.Settings.PanelStyle == "glass";
        _stageHost.Background = glass ? Glass.PreviewWallpaper(_stageDark) : P.Brush(_stageDark ? Palette.Parse("#181818") : Palette.Parse("#F4F2EF"));
        _darkButton.SetIcon(_stageDark ? "light_mode" : "dark_mode");
        _stage.Children.Clear();
        _stageView = null;
        var d = _design;
        if (d is null)
        {
            var empty = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 30, 0, 30) };
            empty.Children.Add(Glyph.Make(IsRunning ? "auto_awesome" : "widgets", 36, _stageDark ? Palette.Parse("#C7C7CC") : Palette.Parse("#6E6E73")));
            var t = Ui.Text(IsRunning ? "Your widget appears here" : "Your widget appears here, at its real size", 13, _stageDark ? Palette.Parse("#C7C7CC") : Palette.Parse("#48484D"));
            t.HorizontalAlignment = HorizontalAlignment.Center;
            empty.Children.Add(t);
            _stage.Children.Add(empty);
            return;
        }
        var (w, h) = TileLayout.SizeOf(d.Size);
        var theme = new TileTheme(d.Palette, d.Surface, _stageDark, Palette.SeedFor(_host.Settings.Accent), glass);
        _stageView = new TileView(d, w, h, theme, this, preview: true, pickable: true);
        _stageView.BlockPicked += index =>
        {
            _editing = index;
            if (_tab != "blocks") _tabs.SetSelected(0);
            _tab = "blocks";
            RenderPanes();
        };
        _stageView.Show(_values);
        var host = new Grid { Width = w, Height = h, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        host.Children.Add(_stageView);
        if (glass)
        {
            using (Palette.Use(Palette.For(_stageDark, _host.Settings.Accent, glass: true))) host.Children.Add(Glass.Overlay(host, TileLayout.Radius));
        }
        if (_editing >= 0) _stageView.Highlight(_editing);
        _stage.Children.Add(host);
        if (IsRunning && _host.Library.Find(d.Id) is null)
        {
            // a draft while the AI works: shown faded
            host.Opacity = 0.55;
        }
    }

    private async Task LoadValuesAsync(bool force = false)
    {
        var d = _design;
        if (d is null) return;
        try
        {
            var values = await _host.Values.RefreshAsync(d, force, CancellationToken.None);
            if (_design?.Id != d.Id) return;
            _values = values;
            _stageView?.Show(values);
        }
        catch (Exception ex)
        {
            Log.Warn($"Studio values: {ex.Message}");
        }
    }

    private void OnValueChanged(string id)
    {
        var d = _design;
        if (d is null || !d.Blocks.Any(b => TileSources.BindingId(b) == id)) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_design is null) return;
            _values = _host.Values.Snapshot(_design);
            _stageView?.Show(_values);
        });
    }

    private void RefreshPlaceButton()
    {
        if (_placeButton is null) return;
        var d = _design;
        var saved = d is not null && _host.Library.Find(d.Id) is not null;
        var placed = d is not null && _host.Library.IsPlaced(d.Id);
        _placeButton.Visibility = saved ? Visibility.Visible : Visibility.Collapsed;
        _placeButton.Text = placed ? "On your panel" : "Add to panel";
        _placeButton.SetIcon(placed ? "check" : "add_to_photos");
        _placeButton.Fill = placed ? P.AccentSoft : P.Accent;
        ToolTipService.SetToolTip(_placeButton, placed ? "It updates live as you change it here. Click to take it off the panel." : "Put it on the panel (Ctrl+Enter)");
    }

    private void TogglePlaced()
    {
        var d = _design;
        if (d is null || _host.Library.Find(d.Id) is null || IsRunning) return;
        if (_host.Library.IsPlaced(d.Id))
        {
            _host.Library.Unplace(d.Id);
            Say($"Took {d.Name} off the panel. It stays here in your widgets.");
        }
        else
        {
            App.Current.Celebrate(d.Id);
            _host.Library.Place(d);
            Say($"{d.Name} is on your panel. Keep changing it here and the panel follows.");
            App.Current.ShowPanel();
        }
        RenderHeader();
        Motion.Pulse(_placeButton, 1.06f);
    }

    // ── What it still needs ──────────────────────────────────────────────────────────

    private void RenderNeeds()
    {
        if (_needs is null) return;
        _needs.Children.Clear();
        foreach (var ask in _asks)
        {
            var icon = ask.Need switch
            {
                TileNeed.Location => "location_on",
                TileNeed.Server => "dns",
                TileNeed.Ai => "auto_awesome",
                TileNeed.Command => "terminal",
                _ => "public",
            };
            var chip = new Chip(ask.Title, icon, filled: true, height: 32);
            ToolTipService.SetToolTip(chip, ask.Detail ?? TileNeeds.Label(ask.Need));
            chip.Click += (_, _) => _ = HandleAskAsync(ask, chip);
            _needs.Children.Add(chip);
        }
        _droppedNote.Text = _dropped > 0 ? $"{_dropped} block{(_dropped == 1 ? "" : "s")} didn't fit this size. Undo brings {(_dropped == 1 ? "it" : "them")} back." : "";
        _droppedNote.Visibility = _dropped > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task HandleAskAsync(TileAsk ask, FrameworkElement anchor)
    {
        switch (ask.Need)
        {
            case TileNeed.Location:
            {
                var box = new TextBox { PlaceholderText = "Oslo, or Bergen, Norway", Text = _host.Settings.Tiles.HomeTown ?? "", Width = 260 };
                var save = new Chip("Save", "check", filled: true);
                var body = new StackPanel { Spacing = 10 };
                body.Children.Add(Ui.Text("Your town, for the weather", 13, P.Text1, semibold: true));
                body.Children.Add(box);
                body.Children.Add(save);
                var flyout = new Flyout { Content = body };
                void Commit()
                {
                    var town = box.Text.Trim();
                    if (town.Length == 0) return;
                    _host.Settings.Tiles.HomeTown = town;
                    _host.SaveSettings();
                    flyout.Hide();
                    Recheck();
                    _ = LoadValuesAsync(force: true);
                }
                save.Click += (_, _) => Commit();
                box.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) Commit(); };
                flyout.ShowAt(anchor);
                box.Focus(FocusState.Programmatic);
                break;
            }
            case TileNeed.Server:
                App.Current.OpenSettings("server");
                break;
            case TileNeed.Ai:
                App.Current.OpenSettings("ai");
                break;
            case TileNeed.Command or TileNeed.Site:
            {
                var command = ask.Need == TileNeed.Command;
                var code = new TextBlock
                {
                    Text = (command ? ask.Cmd : ask.Url) ?? "",
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    Foreground = P.Brush(P.Text1),
                };
                var content = new StackPanel { Spacing = 10, MaxWidth = 460 };
                content.Children.Add(Ui.Text(command
                    ? ask.Server is not null ? "The widget runs this on your server over SSH each time it updates. Only allow commands you understand; read-only ones are best."
                                             : "The widget runs this PowerShell each time it updates. Only allow scripts you understand; read-only ones are best."
                    : "The widget reads this address each time it updates.", 13, P.Text2, maxLines: 4));
                content.Children.Add(new Border { Background = P.Brush(P.Well), CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Child = new ScrollViewer { Content = code, MaxHeight = 220 } });
                var dialog = new ContentDialog
                {
                    Title = command ? (ask.Server is not null ? "Run this on your server?" : "Run this script?") : $"Read from {TileApprovals.SiteOf(ask.Url)}?",
                    Content = content,
                    PrimaryButtonText = "Allow",
                    CloseButtonText = "Not now",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = _root.XamlRoot,
                    RequestedTheme = _root.ActualTheme,
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                if (command) TileApprovals.ApproveCommand(_host.Settings.Tiles, ask.Server, ask.Cmd!);
                else TileApprovals.ApproveSite(_host.Settings.Tiles, ask.Url!);
                _host.SaveSettings();
                Recheck();
                _ = LoadValuesAsync(force: true);
                break;
            }
        }
    }

    // ── Panes ────────────────────────────────────────────────────────────────────────

    private void RenderPanes()
    {
        if (_pane is null) return;
        _pane.Children.Clear();
        _tabs.Visibility = _design is null ? Visibility.Collapsed : Visibility.Visible;
        if (_design is null) return;
        switch (_tab)
        {
            case "look": LookPane(_design); break;
            case "json": JsonPane(_design); break;
            default: BlocksPane(_design); break;
        }
    }

    private static string BlockIcon(BlockType t) => t switch
    {
        BlockType.Metric => "tag",
        BlockType.Ring => "donut_large",
        BlockType.Bar => "linear_scale",
        BlockType.Chart => "bar_chart",
        BlockType.List => "list",
        BlockType.Text => "notes",
        BlockType.Badge => "category",
        BlockType.Clock => "schedule",
        BlockType.Date => "calendar_today",
        BlockType.Countdown => "timer",
        BlockType.Button => "smart_button",
        BlockType.Ai => "auto_awesome",
        BlockType.Image => "image",
        BlockType.Carousel => "view_carousel",
        BlockType.Media => "music_note",
        BlockType.Toggle => "toggle_on",
        BlockType.Slider => "tune",
        _ => "segment",
    };

    /// <summary>What a block shows, in a few words: its label, else its source's name, else its own text.</summary>
    private static string BlockTitle(TileBlock b) => b.Label ?? TileSources.ByKey(b.Bind)?.Label ?? b.Text ?? BlockTypes.Label(b.Type);

    private void BlocksPane(TileDesign d)
    {
        var blocks = d.Blocks;
        var hint = Ui.Text("Click a block (here or on the stage) to change it. Ask the AI for anything bigger.", 12, P.Text3, maxLines: 2);
        _pane.Children.Add(hint);
        for (var i = 0; i < blocks.Count; i++)
        {
            var index = i;
            var b = blocks[i];
            var row = new Pressable { Fill = index == _editing ? P.AccentSoft : P.Card, CornerRadius = new CornerRadius(14), AccessibleName = BlockTitle(b) };
            var grid = new Grid { Padding = new Thickness(10, 8, 12, 8), ColumnSpacing = 10 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(10), Background = P.Brush(P.AccentSoft), Child = Glyph.Make(BlockIcon(b.Type), 17, P.Accent) });
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(Ui.Text(BlockTitle(b), 13, P.Text1, semibold: true));
            var src = TileSources.ByKey(b.Bind);
            texts.Children.Add(Ui.Text(BlockTypes.Label(b.Type) + (src is null ? b.Tap is not null ? " · " + TileTap.Describe(b.Tap) : "" : " · " + src.Label) + (b.Weight > 1 ? $" · {b.Weight}× wide" : ""), 11.5, P.Text2));
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            var chevron = Glyph.Make(index == _editing ? "expand_less" : "expand_more", 18, P.Text3);
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(chevron);
            row.SetContent(grid);
            row.Click += (_, _) =>
            {
                _editing = _editing == index ? -1 : index;
                _stageView?.Highlight(_editing);
                RenderPanes();
            };
            row.PointerEntered += (_, _) => _stageView?.Highlight(index);
            row.PointerExited += (_, _) => _stageView?.Highlight(_editing);
            _pane.Children.Add(row);
            if (index == _editing) _pane.Children.Add(BlockEditor(d, b, index, blocks.Count));
        }
        var add = new Chip("Add a block", "add", style: ChipStyle.Outline, height: 34) { HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) => AddBlockMenu(add);
        _pane.Children.Add(add);
    }

    private FrameworkElement BlockEditor(TileDesign d, TileBlock b, int index, int count)
    {
        var card = new Border { Background = P.Brush(P.Card), CornerRadius = new CornerRadius(14), Padding = new Thickness(14), BorderBrush = P.Brush(P.Hairline), BorderThickness = new Thickness(1) };
        var stack = new StackPanel { Spacing = 10 };
        void Change(Func<TileBlock, TileBlock> f) => Edit(x => x.WithBlock(index, f));
        var src = TileSources.ByKey(b.Bind);

        // what it shows
        stack.Children.Add(Section("Shows"));
        var sources = new ComboBox { Width = 380, PlaceholderText = "Your own text or a button" };
        sources.Items.Add(new ComboBoxItem { Content = "Nothing: your own text or a button", Tag = "" });
        foreach (var group in TileSources.All.Where(s => !s.Hidden).GroupBy(s => s.Group))
        {
            foreach (var s in group) sources.Items.Add(new ComboBoxItem { Content = $"{SourceGroups.Label(group.Key)} · {s.Label}", Tag = s.Key });
        }
        sources.SelectedIndex = Math.Max(0, sources.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == (src?.Key ?? "")));
        sources.SelectionChanged += (_, _) =>
        {
            if (sources.SelectedItem is not ComboBoxItem item) return;
            var key = (string)item.Tag;
            var next = TileSources.ByKey(key);
            Change(x =>
            {
                var type = next is null ? (x.Type is BlockType.Text or BlockType.Button or BlockType.Badge ? x.Type : BlockType.Text) : TileSources.Fits(x.Type, next) ? x.Type : TileSources.TypesFor(next)[0];
                return x with { Bind = next?.Key, Type = type, Label = x.Label, Text = next is null ? x.Text ?? BlockTitle(x) : x.Text };
            });
        };
        stack.Children.Add(sources);

        // as
        stack.Children.Add(Section("As"));
        var types = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        foreach (var t in TileSources.TypesFor(src).Concat(src is null ? [] : new[] { b.Type }).Distinct())
        {
            var chip = new Chip(BlockTypes.Label(t), BlockIcon(t), style: t == b.Type ? ChipStyle.Filled : ChipStyle.Outline, height: 30);
            chip.Click += (_, _) => Change(x => x with { Type = t });
            types.Children.Add(chip);
        }
        stack.Children.Add(types);

        // words and settings
        stack.Children.Add(Section("Label"));
        stack.Children.Add(Field(src?.Label ?? "A short label", b.Label ?? "", v => Change(x => x with { Label = v.Length == 0 ? null : v[..Math.Min(v.Length, 40)] })));
        var key = src?.Key;
        if (key is null && b.Type is BlockType.Text or BlockType.Button || key is "ai.text" or "ai.list" or "time.countdown")
        {
            stack.Children.Add(Section(key switch { "ai.text" or "ai.list" => "What the AI answers", "time.countdown" => "Counts down to (2026-12-24T18:00 or 17:00)", _ => "Text" }));
            stack.Children.Add(Field("", b.Text ?? "", v => Change(x => x with { Text = v.Length == 0 ? null : v })));
        }
        if (key is not null && (key.StartsWith("weather.", StringComparison.Ordinal) || key is "pc.disk" or "net.ping" or "sys.vpn" or "sys.app" or "store.value" or "sys.resolution" or "sys.refresh_rate"))
        {
            var what = key switch
            {
                "pc.disk" => "Drive (default the Windows drive)",
                "net.ping" => "Host (default 1.1.1.1)",
                "sys.vpn" => "VPN name (default the first)",
                "sys.app" => "Program name (spotify, obs64)",
                "store.value" => "Name of the kept value",
                "sys.resolution" or "sys.refresh_rate" => "Display (internal, external, 1, 2…)",
                _ => $"Town (default {_host.Settings.Tiles.HomeTown ?? "the one in Settings"})",
            };
            stack.Children.Add(Section(what));
            stack.Children.Add(Field("", b.Query ?? "", v => Change(x => x with { Query = v.Length == 0 ? null : v })));
        }
        if (key is "ps.value" or "server.cmd")
        {
            stack.Children.Add(Section(key == "ps.value" ? "PowerShell (read-only; you approve it)" : "Shell command on your server"));
            var box = new TextBox { Text = b.Cmd ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12, MinHeight = 60, Width = 380 };
            box.LostFocus += (_, _) => Change(x => x with { Cmd = box.Text.Trim().Length == 0 ? null : box.Text.Trim() });
            stack.Children.Add(box);
        }
        if (key is "web.json" or "web.feed" or "web.image" || b.Type == BlockType.Image && key is null)
        {
            stack.Children.Add(Section("Address"));
            stack.Children.Add(Field("https://…", b.Url ?? "", v => Change(x => x with { Url = v.Length == 0 ? null : v })));
        }
        if (b.Type == BlockType.Button || b.Tap is not null)
        {
            stack.Children.Add(Section("When clicked (url:…, run:notepad, keys:Win+E, settings:display, power:lock…)"));
            stack.Children.Add(Field("url:https://…", b.Tap ?? "", v => Change(x => x with { Tap = TileTap.Normalize(v) })));
        }
        if (b.Type is BlockType.Badge or BlockType.Button or BlockType.Metric or BlockType.Toggle)
        {
            stack.Children.Add(Section("Icon (a Material Symbols name)"));
            stack.Children.Add(Field(src?.Icon ?? "star", b.Icon ?? "", v => Change(x => x with { Icon = v.Length == 0 ? null : Icons.Normalize(v) ?? v })));
        }

        // colour, container and width
        stack.Children.Add(Section("Colour"));
        var tones = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 6 };
        var theme = new TileTheme(d.Palette, d.Surface, _stageDark, Palette.SeedFor(_host.Settings.Accent), false);
        foreach (var tone in Enum.GetValues<TileTone>())
        {
            var dot = new Pressable { Width = 30, Height = 30, CornerRadius = new CornerRadius(15), Fill = theme.C(theme.AccentOf(tone)), AccessibleName = tone.ToString() };
            if (tone == b.Tone) { dot.BorderBrush = P.Brush(P.Text1); dot.BorderThickness = new Thickness(2); }
            ToolTipService.SetToolTip(dot, tone.ToString());
            dot.Click += (_, _) => Change(x => x with { Tone = tone });
            tones.Children.Add(dot);
        }
        stack.Children.Add(tones);
        stack.Children.Add(Section("Container"));
        var styles = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        foreach (var style in Enum.GetValues<BlockStyle>())
        {
            var chip = new Chip(style.ToString(), style: style == b.Style ? ChipStyle.Filled : ChipStyle.Outline, height: 30);
            chip.Click += (_, _) => Change(x => x with { Style = style, Shape = style == BlockStyle.Shape ? x.Shape ?? TileShape.Cookie9 : x.Shape });
            styles.Children.Add(chip);
        }
        stack.Children.Add(styles);
        stack.Children.Add(Section("Width in its row"));
        stack.Children.Add(Kit.Segmented([("1", "1×"), ("2", "2×"), ("3", "3×"), ("4", "4×")], b.Weight.ToString(), v => Change(x => x with { Weight = int.Parse(v) }), 240, 32));

        // move and remove
        var actions = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        var earlier = new Chip("Earlier", "arrow_upward", style: ChipStyle.Outline, height: 32);
        earlier.Click += (_, _) => Move(index, -1);
        var later = new Chip("Later", "arrow_downward", style: ChipStyle.Outline, height: 32);
        later.Click += (_, _) => Move(index, 1);
        var newRow = new Chip("Own row", "splitscreen", style: ChipStyle.Outline, height: 32);
        newRow.Click += (_, _) => OwnRow(index);
        var remove = new Chip("Remove", "delete", style: ChipStyle.Ghost, height: 32);
        remove.Click += (_, _) =>
        {
            if (count <= 1) return;
            _editing = -1;
            var i = 0;
            Edit(x => x with { Rows = x.Rows.Select(r => r with { Cells = r.Cells.Where(_ => i++ != index).ToList() }).Where(r => r.Cells.Count > 0).ToList() });
        };
        if (index > 0) actions.Children.Add(earlier);
        if (index < count - 1) actions.Children.Add(later);
        if (d.Size.MaxRows > 1) actions.Children.Add(newRow);
        if (count > 1) actions.Children.Add(remove);
        stack.Children.Add(actions);
        card.Child = stack;
        Motion.Enter(card, -4, 0, Motion.Fast);
        return card;
    }

    /// <summary>A text field that sits flush left in the editor.</summary>
    private static Border Field(string placeholder, string value, Action<string> commit)
    {
        var field = Kit.TextField(placeholder, value, commit, 380);
        field.HorizontalAlignment = HorizontalAlignment.Left;
        return field;
    }

    private TextBlock Section(string text)
    {
        var t = Ui.Text(text, 11.5, P.Text3, semibold: true, maxLines: 2);
        t.Margin = new Thickness(0, 4, 0, -4);
        return t;
    }

    /// <summary>Moves the block one place earlier or later in reading order, across rows.</summary>
    private void Move(int index, int delta)
    {
        Edit(d =>
        {
            var flat = d.Rows.SelectMany((r, ri) => r.Cells.Select(c => (Row: ri, Block: c))).ToList();
            var j = index + delta;
            if (index < 0 || j < 0 || index >= flat.Count || j >= flat.Count) return d;
            (flat[index], flat[j]) = ((flat[index].Row, flat[j].Block), (flat[j].Row, flat[index].Block));
            return d with { Rows = d.Rows.Select((r, ri) => r with { Cells = flat.Where(x => x.Row == ri).Select(x => x.Block).ToList() }).Where(r => r.Cells.Count > 0).ToList() };
        });
        _editing = Math.Clamp(index + delta, 0, (_design?.Blocks.Count ?? 1) - 1);
        RenderPanes();
        _stageView?.Highlight(_editing);
    }

    /// <summary>Gives the block a row of its own, right under the one it was in.</summary>
    private void OwnRow(int index)
    {
        Edit(d =>
        {
            var rows = new List<TileRow>();
            var i = 0;
            foreach (var r in d.Rows)
            {
                var before = new List<TileBlock>();
                TileBlock? moved = null;
                foreach (var c in r.Cells)
                {
                    if (i++ == index) moved = c;
                    else before.Add(c);
                }
                if (before.Count > 0) rows.Add(r with { Cells = before });
                if (moved is not null) rows.Add(new TileRow(r.Height, [moved]));
            }
            return d with { Rows = rows };
        });
        if (_design is not null && TileRules.Sanitize(_design).Blocks.Count < _design.Blocks.Count) _dropped = 1;
    }

    private void AddBlockMenu(FrameworkElement anchor)
    {
        var menu = new MenuFlyout();
        foreach (var group in TileSources.All.Where(s => !s.Hidden).GroupBy(s => s.Group))
        {
            var sub = new MenuFlyoutSubItem { Text = SourceGroups.Label(group.Key) };
            foreach (var s in group)
            {
                var item = new MenuFlyoutItem { Text = s.Label };
                item.Click += (_, _) => AddBlock(new TileBlock { Type = TileSources.TypesFor(s)[0], Bind = s.Key });
                sub.Items.Add(item);
            }
            menu.Items.Add(sub);
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        var text = new MenuFlyoutItem { Text = "Your own text" };
        text.Click += (_, _) => AddBlock(new TileBlock { Type = BlockType.Text, Text = "Hello" });
        menu.Items.Add(text);
        var button = new MenuFlyoutItem { Text = "A button" };
        button.Click += (_, _) => AddBlock(new TileBlock { Type = BlockType.Button, Label = "Open", Icon = "open_in_new", Tap = "url:https://github.com" });
        menu.Items.Add(button);
        menu.ShowAt(anchor);
    }

    private void AddBlock(TileBlock block)
    {
        if (_design is null) return;
        var d = _design;
        TileDesign next;
        if (d.Rows.Count < d.Size.MaxRows) next = d with { Rows = d.Rows.Append(new TileRow([block])).ToList() };
        else next = d with { Rows = d.Rows.Select((r, i) => i == d.Rows.Count - 1 ? r with { Cells = r.Cells.Append(block).ToList() } : r).ToList() };
        if (TileRules.Sanitize(next).Blocks.Count <= d.Blocks.Count)
        {
            // no room at this size: grow it first
            var bigger = TileSize.All.FirstOrDefault(s => s.MaxBlocks > d.Blocks.Count && s.Cols >= d.Size.Cols && s.Rows >= d.Size.Rows);
            if (bigger is null)
            {
                Say("This widget is full. Remove a block first, or ask the AI to split it into two widgets.");
                return;
            }
            next = TileRules.Reflow(d, bigger).Design;
            next = next with { Rows = next.Rows.Count < bigger.MaxRows ? next.Rows.Append(new TileRow([block])).ToList() : next.Rows.Select((r, i) => i == next.Rows.Count - 1 ? r with { Cells = r.Cells.Append(block).ToList() } : r).ToList() };
        }
        Edit(_ => next);
        _editing = (_design?.Blocks.Count ?? 1) - 1;
        RenderPanes();
        Recheck();
    }

    private void LookPane(TileDesign d)
    {
        // size
        _pane.Children.Add(Section("Size"));
        var sizes = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        foreach (var size in TileSize.All)
        {
            var selected = size == d.Size;
            var chip = new Pressable { CornerRadius = new CornerRadius(12), Fill = selected ? P.AccentSoft : P.Card, AccessibleName = size.Label };
            var col = new StackPanel { Spacing = 6, Padding = new Thickness(10, 8, 10, 8) };
            var cells = new Grid { Width = 4 * 12 + 3 * 2, Height = 3 * 9 + 2 * 2, HorizontalAlignment = HorizontalAlignment.Center };
            for (var r = 0; r < 3; r++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var on = c < size.Cols && r < size.Rows;
                    cells.Children.Add(new Border
                    {
                        Width = 12,
                        Height = 9,
                        CornerRadius = new CornerRadius(2.5),
                        Background = P.Brush(on ? (selected ? P.Accent : P.Text2) : P.Overlay(0.08)),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(c * 14, r * 11, 0, 0),
                    });
                }
            }
            col.Children.Add(cells);
            var label = Ui.Text($"{size.Label} {size.Cells}", 11.5, selected ? P.Accent : P.Text2, semibold: selected);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            col.Children.Add(label);
            chip.SetContent(col);
            chip.Click += (_, _) =>
            {
                if (_design is null || IsRunning) return;
                var (next, dropped) = TileRules.Reflow(_design, size);
                _dropped = dropped;
                Show(next, undo: _undo.Append(_design));
                RenderNeeds();
            };
            sizes.Children.Add(chip);
        }
        _pane.Children.Add(sizes);

        // palette
        _pane.Children.Add(Section("Palette"));
        var palettes = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        foreach (var palette in TilePalette.All)
        {
            var seed = palette.FollowsAccent ? Palette.SeedFor(_host.Settings.Accent) : palette.Seed;
            var swatch = new Pressable { Width = 34, Height = 34, CornerRadius = new CornerRadius(17), Fill = Palette.FromRgb(palette.Color(TileRole.Primary, _stageDark, seed)), AccessibleName = palette.Label };
            var inner = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = P.Brush(Palette.FromRgb(palette.Color(TileRole.TertiaryContainer, _stageDark, seed))), IsHitTestVisible = false };
            swatch.SetContent(inner);
            if (palette == d.Palette) { swatch.BorderBrush = P.Brush(P.Text1); swatch.BorderThickness = new Thickness(2.5); }
            ToolTipService.SetToolTip(swatch, palette.FollowsAccent ? "Accent (follows TileSmith's accent colour)" : palette.Label);
            swatch.Click += (_, _) => Edit(x => x with { Palette = palette });
            palettes.Children.Add(swatch);
        }
        _pane.Children.Add(palettes);

        // surface
        _pane.Children.Add(Section("Background"));
        var surfaces = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
        foreach (var surface in Enum.GetValues<TileSurface>())
        {
            var theme = new TileTheme(d.Palette, surface, _stageDark, Palette.SeedFor(_host.Settings.Accent), false);
            var chip = new Pressable { CornerRadius = new CornerRadius(12), Fill = theme.C(theme.Bg), Width = 96, Height = 52, AccessibleName = surface.ToString() };
            var inner = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 4 };
            inner.Children.Add(new Border { Width = 40, Height = 10, CornerRadius = new CornerRadius(5), Background = P.Brush(theme.C(theme.ContainerOf(TileTone.Primary))), HorizontalAlignment = HorizontalAlignment.Center });
            inner.Children.Add(TileText.Block(surface.ToString(), 11.5, theme.C(theme.OnBg), TileText.Medium, 1, TextAlignment.Center));
            chip.SetContent(inner);
            if (surface == d.Surface) { chip.BorderBrush = P.Brush(P.Text1); chip.BorderThickness = new Thickness(2); }
            chip.Click += (_, _) => Edit(x => x with { Surface = surface });
            surfaces.Children.Add(chip);
        }
        _pane.Children.Add(surfaces);

        // shapes
        _pane.Children.Add(Section("Shape (badges and shaped blocks)"));
        var shapes = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        var current = d.Blocks.FirstOrDefault(b => b.Style == BlockStyle.Shape || b.Type == BlockType.Badge)?.Shape;
        foreach (var shape in TileShape.Badges)
        {
            var chip = new Pressable { Width = 40, Height = 40, CornerRadius = new CornerRadius(10), Fill = shape == current ? P.AccentSoft : Colors.Transparent, AccessibleName = shape.Label };
            chip.SetContent(TileDrawing.Shape(shape, 28, shape == current ? P.Accent : P.Text2));
            ToolTipService.SetToolTip(chip, shape.Label);
            chip.Click += (_, _) => Edit(x => SetShape(x, shape));
            shapes.Children.Add(chip);
        }
        _pane.Children.Add(shapes);

        // pace
        _pane.Children.Add(Section($"Refresh while the panel is open (now every {Pace(TileRules.EffectiveRefreshSeconds(d))})"));
        var paces = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        foreach (var seconds in TileRules.RefreshChoices)
        {
            var chip = new Chip(seconds == 0 ? "As fast as it can" : Pace(seconds), style: seconds == d.Refresh ? ChipStyle.Filled : ChipStyle.Outline, height: 30);
            chip.Click += (_, _) => Edit(x => x with { Refresh = seconds });
            paces.Children.Add(chip);
        }
        _pane.Children.Add(paces);

        // AI lines
        if (d.Blocks.Any(b => TileSources.Canonical(b.Bind) is "ai.text" or "ai.list"))
        {
            var toggle = Kit.Toggle(d.Ai, on => Edit(x => x with { Ai = on }));
            _pane.Children.Add(Kit.Card(Kit.Row("auto_awesome", "AI lines", "Its AI blocks ask your AI on each refresh. Off keeps the last answer.", toggle)));
        }
    }

    private static string Pace(int seconds) => seconds switch
    {
        < 60 => $"{seconds} s",
        < 3600 => $"{seconds / 60} min",
        _ => $"{seconds / 3600} h",
    };

    /// <summary>Puts every shape in the design on <paramref name="shape"/>; with none, the first badge, metric or clock takes it.</summary>
    private static TileDesign SetShape(TileDesign d, TileShape shape)
    {
        var touched = false;
        var rows = d.Rows.Select(r => r with
        {
            Cells = r.Cells.Select(b =>
            {
                if (b.Style != BlockStyle.Shape && b.Type != BlockType.Badge) return b;
                touched = true;
                return b with { Shape = shape, Style = b.Type == BlockType.Badge && b.Style != BlockStyle.Filled ? BlockStyle.Shape : b.Style };
            }).ToList(),
        }).ToList();
        if (touched) return d with { Rows = rows };
        var done = false;
        return d with
        {
            Rows = d.Rows.Select(r => r with
            {
                Cells = r.Cells.Select(b =>
                {
                    if (done || b.Type is not (BlockType.Metric or BlockType.Clock)) return b;
                    done = true;
                    return b with { Shape = shape, Style = BlockStyle.Shape };
                }).ToList(),
            }).ToList(),
        };
    }

    private void JsonPane(TileDesign d)
    {
        var box = new TextBox
        {
            Text = TileCodec.ToAiJson(d).ToJsonString(JsonDefaults.Options),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 11.5,
            MinHeight = 260,
            IsSpellCheckEnabled = false,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        var error = Ui.Text("", 12, P.Error, maxLines: 4);
        var apply = Kit.Button("Apply", "check", ChipStyle.Filled, () =>
        {
            try
            {
                if (System.Text.Json.Nodes.JsonNode.Parse(box.Text, documentOptions: JsonDefaults.DocumentOptions) is not System.Text.Json.Nodes.JsonObject o)
                    throw new FormatException("It must be one JSON object.");
                var next = TileCodec.Parse(o, d) with { Id = d.Id, Chat = d.Chat, CreatedAt = d.CreatedAt };
                if (next.Blocks.Count == 0) throw new FormatException("There are no blocks TileSmith can show.");
                error.Text = "";
                Edit(_ => next);
            }
            catch (Exception ex)
            {
                error.Text = ex.Message;
            }
        });
        apply.HorizontalAlignment = HorizontalAlignment.Left;
        _pane.Children.Add(Ui.Text("The design as the AI writes it. Change anything and apply.", 12, P.Text3, maxLines: 2));
        _pane.Children.Add(box);
        _pane.Children.Add(apply);
        _pane.Children.Add(error);
    }

    // ── The front door ───────────────────────────────────────────────────────────────

    private FrameworkElement BuildFrontDoor()
    {
        var stack = new StackPanel { Spacing = 14, Padding = new Thickness(0, 24, 0, 8) };
        stack.Children.Add(Ui.Text("What should we build?", 26, P.Text1, semibold: true));
        stack.Children.Add(Kit.Body("Describe a widget in your own words: the AI designs it in seconds and it appears on the right with your real data. Then keep chatting, or tweak it yourself.", 13.5, P.Text2));
        var ideas = new WrapPanel { HorizontalSpacing = 10, VerticalSpacing = 10, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (icon, title, prompt) in Ideas())
        {
            var card = new Pressable { Width = 236, Height = 72, CornerRadius = new CornerRadius(18), Fill = P.Card, AccessibleName = title };
            card.BorderBrush = P.Brush(P.Hairline);
            card.BorderThickness = new Thickness(1);
            var grid = new Grid { Padding = new Thickness(14, 0, 12, 0), ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(12), Background = P.Brush(P.AccentSoft), Child = Glyph.Make(icon, 19, P.Accent), VerticalAlignment = VerticalAlignment.Center });
            var text = Ui.Text(title, 13, P.Text1, semibold: true, maxLines: 2);
            text.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            card.SetContent(grid);
            card.Click += (_, _) => _ = SendAsync(prompt);
            ideas.Children.Add(card);
        }
        stack.Children.Add(ideas);

        stack.Children.Add(Section("Or start from one of these"));
        var starters = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
        foreach (var s in TileStarters.All.Where(s => s.IsAvailable(_host.Environment) && s.Category != "Shortcuts").Take(18))
        {
            var chip = new Chip(s.Title, style: ChipStyle.Outline, height: 30);
            ToolTipService.SetToolTip(chip, s.Prompt);
            chip.Click += (_, _) => StartFrom(s);
            starters.Children.Add(chip);
        }
        stack.Children.Add(starters);

        var mine = _host.Library.Designs;
        if (mine.Count > 0)
        {
            stack.Children.Add(Section("Your widgets"));
            stack.Children.Add(DesignGrid(mine.Take(12), null));
        }
        return stack;
    }

    private IEnumerable<(string Icon, string Title, string Prompt)> Ideas()
    {
        yield return ("partly_cloudy_day", "Weather where I live", "The weather where I live: now, the next hours and the next days");
        yield return ("monitoring", "PC dashboard", "A dashboard with CPU and memory as rings, the disk, and a CPU graph");
        yield return ("music_note", "Now playing", "What's playing, with the cover art and play/pause, next and previous");
        yield return ("toggle_on", "Quick settings", "Wi-Fi, Bluetooth, dark mode and the microphone as switches, with the volume under them");
        yield return ("network_check", "Network", "My network: the Wi-Fi name, ping and live download speed with a graph");
        yield return ("newspaper", "Headlines", "The latest tech headlines to scroll through, with pictures");
        if (_host.Settings.Server.IsConfigured) yield return ("deployed_code", "My server", "My server: online or not, CPU, memory, disk and its Docker containers");
        else yield return ("celebration", "Countdown", "A countdown to Christmas Eve at 18:00");
    }

    /// <summary>Designs as their small previews, newest first. Click opens, right-click duplicates or deletes.</summary>
    private WrapPanel DesignGrid(IEnumerable<TileDesign> designs, Flyout? close)
    {
        var wrap = new WrapPanel { HorizontalSpacing = 12, VerticalSpacing = 12 };
        foreach (var d in designs)
        {
            var (w, h) = TileLayout.SizeOf(d.Size);
            const double scale = 0.5;
            var view = new TileView(d, w, h, new TileTheme(d.Palette, d.Surface, _stageDark, Palette.SeedFor(_host.Settings.Accent), false), null, preview: true);
            view.Show(_host.Values.Snapshot(d));
            var box = new Viewbox { Width = w * scale, Height = h * scale, Child = view };
            var card = new Pressable { CornerRadius = new CornerRadius(12), Fill = d.Id == _design?.Id ? P.AccentSoft : Colors.Transparent, AccessibleName = d.Name };
            var col = new StackPanel { Spacing = 4, Padding = new Thickness(6) };
            col.Children.Add(box);
            var name = Ui.Text(d.Name, 12, P.Text1, semibold: true);
            name.MaxWidth = Math.Max(110, w * scale);
            col.Children.Add(name);
            col.Children.Add(Ui.Text(_host.Library.IsPlaced(d.Id) ? "On your panel" : Ago(d.UpdatedAt), 11, _host.Library.IsPlaced(d.Id) ? P.Accent : P.Text3));
            card.SetContent(col);
            card.Click += (_, _) =>
            {
                close?.Hide();
                SwitchTo(d);
            };
            var menu = new MenuFlyout();
            var dup = new MenuFlyoutItem { Text = "Duplicate" };
            dup.Click += (_, _) =>
            {
                close?.Hide();
                var copy = _host.Library.Duplicate(d);
                SwitchTo(copy);
            };
            menu.Items.Add(dup);
            var del = new MenuFlyoutItem { Text = "Delete" };
            del.Click += (_, _) =>
            {
                close?.Hide();
                _host.Library.Delete(d.Id);
                if (_design?.Id == d.Id) NewWidget(); else Render();
            };
            menu.Items.Add(del);
            card.ContextFlyout = menu;
            wrap.Children.Add(card);
        }
        return wrap;
    }

    private static string Ago(long ms)
    {
        var ago = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return ago.TotalMinutes < 1 ? "Just now" : ago.TotalHours < 1 ? $"{(int)ago.TotalMinutes} min ago" : ago.TotalDays < 1 ? $"{(int)ago.TotalHours} h ago" : $"{(int)ago.TotalDays} d ago";
    }

    private void ShowYourWidgets(FrameworkElement anchor)
    {
        var designs = _host.Library.Designs;
        var body = new StackPanel { Spacing = 10, Width = 560 };
        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(Ui.Text("Your widgets", 17, P.Text1, semibold: true));
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        var fresh = new Chip("New", "add", filled: true, height: 32);
        fresh.Click += (_, _) => { flyout.Hide(); NewWidget(); };
        Grid.SetColumn(fresh, 1);
        header.Children.Add(fresh);
        body.Children.Add(header);
        body.Children.Add(Ui.Text("Click one to open it; right-click to duplicate or delete.", 12, P.Text2));
        body.Children.Add(designs.Count == 0 ? Kit.Empty("widgets", "No widgets yet", "Describe one and the AI builds it.") : new ScrollViewer { Content = DesignGrid(designs, flyout), MaxHeight = 520 });
        flyout.Content = body;
        flyout.ShowAt(anchor);
    }

    // ── The stage works for real (ITileActions) ──────────────────────────────────────

    private async Task RunStage(Func<Task> work, TileBlock? block)
    {
        try
        {
            await work();
        }
        catch (ActionException ex)
        {
            Say(ex.Message, error: true);
            _suggestions = ["Fix this"];
            _retry = $"Using the widget fails with: \"{ex.Message}\". Fix it and keep everything else as it is.";
            RenderSuggestions();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error("Studio stage action failed", ex);
            Say(ex.Message, error: true);
        }
        if (block is not null)
        {
            await Task.Delay(400);
            await LoadValuesAsync();
        }
    }

    public Task SwitchAsync(TileBlock block, bool on) => RunStage(() => _host.Controls.SetSwitchAsync(block, on), block);

    public Task LevelAsync(TileBlock block, double value, bool final) => final ? RunStage(() => _host.Controls.SetLevelAsync(block, value), null) : Task.CompletedTask;

    public Task ChooseAsync(TileBlock block, TileOption option) => RunStage(() => _host.Controls.ChooseAsync(block, option), block);

    public async Task TapAsync(TileBlock? block, string? tap, TileValue? value, FrameworkElement anchor)
    {
        if (block?.Confirm is { Length: > 0 } question)
        {
            var dialog = new ContentDialog { Title = question, PrimaryButtonText = "Continue", CloseButtonText = "Cancel", XamlRoot = _root.XamlRoot, RequestedTheme = _root.ActualTheme };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        var outcome = TapOutcome.Nothing;
        await RunStage(async () => outcome = await _host.Controls.TapAsync(tap, block, value), null);
        if (outcome is TapOutcome.Refresh or TapOutcome.Done) await LoadValuesAsync(force: outcome == TapOutcome.Refresh);
    }

    public Task OpenLinkAsync(string link) => RunStage(() => _host.Controls.OpenLinkAsync(link), null);

    public Task MediaAsync(TileBlock block, string command) => RunStage(() => _host.Controls.MediaAsync(command, block), block);
}
