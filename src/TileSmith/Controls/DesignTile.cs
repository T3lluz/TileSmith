using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using TileSmith.Controls.Tiles;
using TileSmith.Core;
using TileSmith.Core.Actions;
using TileSmith.Core.Tiles;
using TileSmith.Services;
using TileSmith.Theme;
using Windows.UI;

namespace TileSmith.Controls;

public enum TileCommand { EditWithAi, EditJson, Duplicate, Remove }

/// <summary>
/// One widget on the panel: its design drawn at its cell size, kept fresh at the pace its sources allow while the
/// panel is open, and alive — switches switch, sliders slide, buttons do what they say. Hold it to lift and move it;
/// its menu changes the size, duplicates it, opens it in the Studio or takes it off the panel.
/// </summary>
public sealed class DesignTile : Grid, ITileActions
{
    public const double Radius = TileLayout.Radius;

    private readonly AppHost _host;
    private readonly Grid _surfaceHost = new();
    private readonly Grid _editOverlay;
    private readonly DispatcherTimer _poll = new();
    private TileView? _view;
    private bool _editMode;
    private bool _polling;
    private CancellationTokenSource? _refreshCts;
    private HashSet<string> _bindings = [];

    public DesignTile(TileDesign design, AppHost host, bool preview = false)
    {
        Design = design;
        _host = host;
        IsPreview = preview;
        _size = design.Size;
        Background = new SolidColorBrush(Colors.Transparent); // hit-testable everywhere (long-press anywhere)
        Children.Add(_surfaceHost);
        Build();
        if (Palette.Current.IsGlass) Children.Add(Glass.Overlay(this, Radius));
        _editOverlay = BuildEditOverlay();
        Children.Add(_editOverlay);
        ContextFlyout = BuildMenu();

        Motion.EnableTransitions(this, Motion.Normal);
        Motion.CenterScale(this);
        Motion.EnableTransitions(_surfaceHost, Motion.Normal);
        Motion.CenterScale(_surfaceHost);
        _surfaceHost.OpacityTransition = new ScalarTransition { Duration = Motion.Normal };
        _poll.Tick += (_, _) => _ = RefreshAsync();
        Unloaded += (_, _) =>
        {
            StopPolling();
            _host.Values.ValueChanged -= OnValueChanged;
        };
        Loaded += (_, _) => _host.Values.ValueChanged += OnValueChanged;
    }

    public TileDesign Design { get; private set; }

    /// <summary>The size it is drawn at: the design's, or another while the resize handle is being dragged.</summary>
    public TileSize Size
    {
        get => _size;
        set
        {
            if (_size == value) return;
            _size = value;
            if (_view is not null) Build();
        }
    }

    private TileSize _size = TileSize.Small;
    private double? _slotWidth;

    /// <summary>The width the board gives it (a full-width tile on a five-column panel is wider than four cells).</summary>
    public double? SlotWidth
    {
        get => _slotWidth;
        set
        {
            if (_slotWidth is { } old && value is { } now && Math.Abs(old - now) < 0.5) return;
            _slotWidth = value;
            if (_view is not null) Build();
        }
    }
    public bool IsPreview { get; }

    public event Action<DesignTile, TileCommand>? Command;
    public event Action<DesignTile, TileSize>? ResizeRequested;
    public event Action<DesignTile, string>? Failed;
    public event Action<DesignTile, PointerRoutedEventArgs>? ResizeHandlePressed;

    /// <summary>Draws the design again (after a size change or when it was edited in the Studio).</summary>
    public void SetDesign(TileDesign design)
    {
        Design = design;
        _size = design.Size;
        Build();
        if (_polling) StartPolling();
    }

    private void Build()
    {
        _surfaceHost.Children.Clear();
        var (w, h) = TileLayout.SizeOf(Size);
        if (_slotWidth is { } slot && slot > 40) w = slot;
        var design = Size == Design.Size ? Design : TileRules.Reflow(Design, Size).Design;
        _view = new TileView(design, w, h, TileTheme.For(design, _host.Settings.Accent), IsPreview ? null : this, preview: IsPreview);
        _view.Show(_host.Values.Snapshot(design));
        _bindings = design.Blocks.Select(TileSources.BindingId).OfType<string>().ToHashSet();
        _surfaceHost.Children.Add(_view);
        AutomationName();
    }

    private void AutomationName() => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this, Design.Name);

    // ── Values ───────────────────────────────────────────────────────────────────────

    private void OnValueChanged(string id)
    {
        if (!_bindings.Contains(id)) return;
        DispatcherQueue.TryEnqueue(() => _view?.Show(_host.Values.Snapshot(_view.Design)));
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (_view is null) return;
        _refreshCts?.Cancel();
        var cts = _refreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var values = await _host.Values.RefreshAsync(_view.Design, force, cts.Token);
            if (!cts.IsCancellationRequested) _view?.Show(values);
        }
        catch (OperationCanceledException)
        {
            // a newer pass took over
        }
        catch (Exception ex)
        {
            Log.Warn($"Refreshing \"{Design.Name}\" failed: {ex.Message}");
        }
    }

    public void StartPolling()
    {
        _polling = true;
        _poll.Interval = TimeSpan.FromSeconds(Math.Max(1, TileRules.EffectiveRefreshSeconds(Design)));
        if (Design.Blocks.Any(b => b.Bind is not null)) _poll.Start();
    }

    public void StopPolling()
    {
        _polling = false;
        _poll.Stop();
        _refreshCts?.Cancel();
    }

    // ── Running things (ITileActions) ────────────────────────────────────────────────

    private async Task Run(Func<Task> work, TileBlock? block)
    {
        if (IsPreview) return;
        try
        {
            await work();
        }
        catch (ActionException ex)
        {
            Failed?.Invoke(this, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Widget \"{Design.Name}\" failed", ex);
            Failed?.Invoke(this, ex.Message);
        }
        if (block is not null)
        {
            await Task.Delay(400);
            await RefreshAsync();
        }
    }

    public Task SwitchAsync(TileBlock block, bool on) => Run(() => _host.Controls.SetSwitchAsync(block, on), block);

    private readonly Dictionary<TileBlock, (double Value, bool Running, bool Dirty)> _levels = [];

    /// <summary>Sliders send their value while you drag, but never queue up more than one call at a time.</summary>
    public async Task LevelAsync(TileBlock block, double value, bool final)
    {
        if (IsPreview) return;
        var fast = TileSources.Canonical(block.Bind) is "sys.volume" or "sys.brightness";
        _levels.TryGetValue(block, out var state);
        state.Value = value;
        state.Dirty = true;
        _levels[block] = state;
        if (state.Running) return;
        _levels[block] = state with { Running = true };
        try
        {
            while (_levels[block].Dirty)
            {
                var pending = _levels[block].Value;
                _levels[block] = _levels[block] with { Dirty = false };
                if (!fast)
                {
                    await Task.Delay(final ? 0 : 280);
                    if (_levels[block].Dirty) continue;
                }
                await Run(() => _host.Controls.SetLevelAsync(block, pending), null);
                if (fast) await Task.Delay(40);
            }
        }
        finally
        {
            _levels[block] = _levels[block] with { Running = false };
        }
    }

    public Task ChooseAsync(TileBlock block, TileOption option) => Run(() => _host.Controls.ChooseAsync(block, option), block);

    public async Task TapAsync(TileBlock? block, string? tap, TileValue? value, FrameworkElement anchor)
    {
        if (IsPreview) return;
        if (block?.Confirm is { Length: > 0 } question && !await ConfirmAsync(question, anchor)) return;
        TapOutcome outcome = TapOutcome.Nothing;
        await Run(async () => outcome = await _host.Controls.TapAsync(tap, block, value), null);
        switch (outcome)
        {
            case TapOutcome.Studio:
                Command?.Invoke(this, TileCommand.EditWithAi);
                break;
            case TapOutcome.Refresh:
                await RefreshAsync(force: true);
                break;
            case TapOutcome.Done when block is not null && TileSources.Canonical(block.Bind) is not null:
                await Task.Delay(500);
                await RefreshAsync();
                break;
        }
    }

    public Task OpenLinkAsync(string link) => Run(() => _host.Controls.OpenLinkAsync(link), null);

    public Task MediaAsync(TileBlock block, string command) => Run(() => _host.Controls.MediaAsync(command, block), block);

    private static Task<bool> ConfirmAsync(string text, FrameworkElement anchor)
    {
        var tcs = new TaskCompletionSource<bool>();
        var p = Palette.Current;
        var yes = new Chip("Continue", filled: true);
        var no = new Chip("Cancel", style: ChipStyle.Ghost);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        var body = new StackPanel { Spacing = 12, MaxWidth = 260 };
        body.Children.Add(Ui.Text(text, 13, p.Text1, maxLines: 4));
        body.Children.Add(buttons);
        var flyout = new Flyout { Content = body };
        yes.Click += (_, _) => { tcs.TrySetResult(true); flyout.Hide(); };
        no.Click += (_, _) => { tcs.TrySetResult(false); flyout.Hide(); };
        flyout.Closed += (_, _) => tcs.TrySetResult(false);
        flyout.ShowAt(anchor);
        return tcs.Task;
    }

    // ── Edit mode ────────────────────────────────────────────────────────────────────

    public bool IsEditMode
    {
        get => _editMode;
        set
        {
            if (_editMode == value) return;
            _editMode = value;
            _editOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            _surfaceHost.IsHitTestVisible = !value;
            // the content shrinks a little so the corner badges don't cover it
            _surfaceHost.Scale = value ? new Vector3(0.93f, 0.93f, 1) : Vector3.One;
            _surfaceHost.Opacity = value ? 0.85 : 1;
            if (value)
            {
                foreach (var badge in _editOverlay.Children.OfType<Pressable>()) Motion.Enter(badge, 6, 40, Motion.Fast);
            }
        }
    }

    /// <summary>A long-press lifted the tile: nothing inside may fire.</summary>
    public void CancelInteraction()
    {
        foreach (var p in Descendants(_surfaceHost).OfType<Pressable>()) p.CancelPress();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private Grid BuildEditOverlay()
    {
        var p = Palette.Current;
        var overlay = new Grid { Visibility = Visibility.Collapsed };
        overlay.Children.Add(new Rectangle
        {
            RadiusX = Radius,
            RadiusY = Radius,
            Stroke = p.Brush(Palette.WithAlpha(p.Accent, 0.6)),
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3],
            Fill = p.Brush(p.Overlay(0.04)),
            IsHitTestVisible = false,
        });

        var remove = Badge("close", p.Error, Colors.White, "Take off the panel");
        remove.HorizontalAlignment = HorizontalAlignment.Left;
        remove.VerticalAlignment = VerticalAlignment.Top;
        remove.Margin = new Thickness(5, 5, 0, 0);
        remove.Click += (_, _) => Command?.Invoke(this, TileCommand.Remove);
        overlay.Children.Add(remove);

        var ai = Badge("auto_awesome", p.Accent, p.OnAccent, "Change in the Studio");
        ai.HorizontalAlignment = HorizontalAlignment.Right;
        ai.VerticalAlignment = VerticalAlignment.Top;
        ai.Margin = new Thickness(0, 5, 5, 0);
        ai.Click += (_, _) => Command?.Invoke(this, TileCommand.EditWithAi);
        overlay.Children.Add(ai);

        var resize = Badge("open_in_full", p.Tile, p.Text1, "Drag to resize · click for the next size");
        resize.HorizontalAlignment = HorizontalAlignment.Right;
        resize.VerticalAlignment = VerticalAlignment.Bottom;
        resize.Margin = new Thickness(0, 0, 5, 5);
        resize.BorderBrush = p.Brush(p.HairlineStrong);
        resize.BorderThickness = new Thickness(1);
        resize.PointerPressed += (_, e) => ResizeHandlePressed?.Invoke(this, e);
        overlay.Children.Add(resize);
        return overlay;
    }

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();
        AddMenu(menu, "Change in the Studio…", "auto_awesome", () => Command?.Invoke(this, TileCommand.EditWithAi));
        var sizes = new MenuFlyoutSubItem { Text = "Size", Icon = MenuIcon("aspect_ratio") };
        foreach (var size in TileSize.All)
        {
            var item = new RadioMenuFlyoutItem { Text = $"{size.Label}  ·  {size.Cells}", GroupName = "size-" + Design.Id, IsChecked = size == Design.Size };
            var chosen = size;
            item.Click += (_, _) => ResizeRequested?.Invoke(this, chosen);
            sizes.Items.Add(item);
        }
        menu.Items.Add(sizes);
        AddMenu(menu, "Refresh", "refresh", () => _ = RefreshAsync(force: true));
        AddMenu(menu, "Duplicate", "content_copy", () => Command?.Invoke(this, TileCommand.Duplicate));
        AddMenu(menu, "Edit JSON…", "data_object", () => Command?.Invoke(this, TileCommand.EditJson));
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenu(menu, "Take off the panel", "close", () => Command?.Invoke(this, TileCommand.Remove));
        menu.Opening += (_, _) =>
        {
            foreach (var item in sizes.Items.OfType<RadioMenuFlyoutItem>()) item.IsChecked = item.Text.StartsWith(Design.Size.Label + " ", StringComparison.Ordinal);
        };
        return menu;
    }

    private static FontIcon MenuIcon(string name) => new() { Glyph = Icons.Glyph(name), FontFamily = Glyph.Family, FontSize = 16 };

    private static void AddMenu(MenuFlyout menu, string text, string icon, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = MenuIcon(icon) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    public void ShowMenu() => ((MenuFlyout)ContextFlyout).ShowAt(this, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
    {
        Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
    });

    private static Pressable Badge(string icon, Color fill, Color fg, string tooltip)
    {
        var badge = new Pressable { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Fill = fill };
        badge.SetContent(Glyph.Make(icon, 15, fg));
        badge.AccessibleName = tooltip;
        ToolTipService.SetToolTip(badge, tooltip);
        return badge;
    }

    /// <summary>The AI (or the gallery) just added or changed this tile: an accent ring that fades, and a spring pop.</summary>
    public void Celebrate()
    {
        var p = Palette.Current;
        var glow = new Rectangle { RadiusX = Radius, RadiusY = Radius, Stroke = p.Brush(p.Accent), StrokeThickness = 2, IsHitTestVisible = false };
        Children.Add(glow);
        Scale = new Vector3(0.92f, 0.92f, 1);
        DispatcherQueue.TryEnqueue(() => Scale = Vector3.One);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        glow.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            glow.Opacity = 0;
            var remove = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            remove.Tick += (_, _) => { remove.Stop(); Children.Remove(glow); };
            remove.Start();
        };
        timer.Start();
    }
}
