using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using TileSmith.Core;
using TileSmith.Core.Ai;
using TileSmith.Services;
using TileSmith.Theme;

namespace TileSmith.Controls;

/// <summary>
/// Picks the AI. A rail of provider logos on the left (Automatic, Recent, then every ready provider); every model of
/// the selected provider on the right, with its maker's logo, release date, context size, price and badges. Search
/// looks through all providers at once. Lists come live from the providers and models.dev, cached so it opens
/// instantly; the list area has a fixed size, so nothing moves while lists load.
/// </summary>
public sealed class ModelPicker : Grid
{
    private const double ListHeight = 430;
    private const int MaxRows = 250;
    private static readonly TimeSpan Stale = TimeSpan.FromHours(6);

    private readonly AppHost _host;
    private readonly Action<string, string?> _chosen;
    private readonly StackPanel _rail = new() { Spacing = 4 };
    private readonly StackPanel _list = new() { Spacing = 1 };
    private readonly ScrollViewer _scroll;
    private readonly TextBox _search;
    private readonly HashSet<string> _loading = [];
    private readonly Dictionary<string, string> _errors = [];
    private readonly CancellationTokenSource _cts = new();
    private string _tab;

    public ModelPicker(AppHost host, Action<string, string?> chosen, Action manage)
    {
        _host = host;
        _chosen = chosen;
        var p = Palette.Current;
        Width = 540;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(ListHeight) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _search = Kit.SearchBox("Search every model");
        _search.Margin = new Thickness(12, 12, 12, 10);
        _search.TextChanged += (_, _) => Render();
        SetColumnSpan(_search, 2);
        Children.Add(_search);

        var railScroll = new ScrollViewer { Content = _rail, Padding = new Thickness(10, 2, 8, 8), VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        railScroll.BorderBrush = p.Brush(p.Hairline);
        var railHost = new Border { Child = railScroll, BorderBrush = p.Brush(p.Hairline), BorderThickness = new Thickness(0, 0, 1, 0) };
        SetRow(railHost, 1);
        Children.Add(railHost);

        _scroll = new ScrollViewer { Content = _list, Padding = new Thickness(8, 0, 10, 8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(_scroll, 1);
        SetColumn(_scroll, 1);
        Children.Add(_scroll);

        var footer = new Grid { BorderBrush = p.Brush(p.Hairline), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(8) };
        var manageButton = new Chip("AI providers and keys…", "tune", style: ChipStyle.Ghost, height: 34) { HorizontalAlignment = HorizontalAlignment.Left };
        manageButton.Click += (_, _) => manage();
        footer.Children.Add(manageButton);
        SetRow(footer, 2);
        SetColumnSpan(footer, 2);
        Children.Add(footer);

        var settings = _host.Settings;
        _tab = settings.Ai.Engine == "auto" ? "auto" : AiEngineFactory.ResolveEngine(settings);
        Unloaded += (_, _) => _cts.Cancel();
        Loaded += (_, _) => _search.Focus(FocusState.Programmatic);
        RenderRail();
        Render();
        _ = RefreshStaleAsync();
    }

    /// <summary>Wraps a picker in a styled flyout.</summary>
    public static Flyout Flyout(ModelPicker picker)
    {
        var p = Palette.Current;
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(20)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, p.Brush(p.Card)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, p.Brush(p.HairlineStrong)));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 600.0));
        style.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 640.0));
        return new Flyout { Content = picker, FlyoutPresenterStyle = style, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
    }

    private IEnumerable<ProviderInfo> Providers()
    {
        var active = AiEngineFactory.ResolveEngine(_host.Settings);
        return ProviderCatalog.All.Where(pr => pr.Id == active || ProviderCatalog.Status(pr, _host.Settings).IsReady);
    }

    private async Task RefreshStaleAsync()
    {
        await ModelDirectory.EnsureFreshAsync(_cts.Token);
        if (_cts.IsCancellationRequested) return;
        Render();
        foreach (var provider in Providers())
        {
            if (provider.Id == "command") continue;
            var fetched = ModelCatalog.LastFetched(provider);
            if (fetched is { } f && DateTimeOffset.Now - f < Stale) continue;
            _ = RefreshAsync(provider);
        }
    }

    private async Task RefreshAsync(ProviderInfo provider)
    {
        if (!_loading.Add(provider.Id)) return;
        if (_tab == provider.Id) Render();
        try
        {
            await ModelCatalog.RefreshAsync(provider, _host.Settings, _host.Server.GetConnection, _cts.Token);
            _errors.Remove(provider.Id);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _errors[provider.Id] = ex.Message;
            Log.Warn($"Model list for {provider.Name} failed: {ex.Message}");
        }
        finally
        {
            _loading.Remove(provider.Id);
        }
        if (_tab == provider.Id || _search.Text.Length > 0) Render();
    }

    // ── Rail ────────────────────────────────────────────────────────────────────────

    private void RenderRail()
    {
        _rail.Children.Clear();
        _rail.Children.Add(RailItem("auto", "Automatic", ProviderLogoOrGlyph(null, "auto_awesome")));
        if (Recent().Count > 0) _rail.Children.Add(RailItem("recent", "Recent", ProviderLogoOrGlyph(null, "history")));
        _rail.Children.Add(new Border { Height = 1, Margin = new Thickness(6, 4, 6, 4), Background = Palette.Current.Brush(Palette.Current.Hairline) });
        foreach (var provider in Providers()) _rail.Children.Add(RailItem(provider.Id, provider.Name, ProviderLogo.Make(provider, 24, tile: false)));
    }

    private static FrameworkElement ProviderLogoOrGlyph(ProviderInfo? provider, string glyph) =>
        provider is not null ? ProviderLogo.Make(provider, 24, tile: false) : Glyph.Make(glyph, 22, Palette.Current.Text1);

    private Pressable RailItem(string tab, string tooltip, FrameworkElement content)
    {
        var p = Palette.Current;
        var selected = tab == _tab && _search.Text.Length == 0;
        var item = new Pressable { Width = 44, Height = 44, CornerRadius = new CornerRadius(14), Fill = selected ? p.AccentSoft : Colors.Transparent, AccessibleName = tooltip };
        item.BorderBrush = p.Brush(selected ? Palette.WithAlpha(p.Accent, 0.55) : Colors.Transparent);
        item.BorderThickness = new Thickness(1.5);
        content.HorizontalAlignment = HorizontalAlignment.Center;
        content.VerticalAlignment = VerticalAlignment.Center;
        item.SetContent(content);
        ToolTipService.SetToolTip(item, tooltip);
        item.Click += (_, _) =>
        {
            _tab = tab;
            _search.Text = "";
            RenderRail();
            Render();
            _scroll.ChangeView(null, 0, null, true);
        };
        return item;
    }

    // ── List ────────────────────────────────────────────────────────────────────────

    private List<(ProviderInfo Provider, string Model)> Recent()
    {
        var settings = _host.Settings;
        return settings.Ai.RecentModels
            .Select(r => r.Split('/', 2))
            .Where(parts => parts.Length == 2 && ProviderCatalog.Find(parts[0]) is { } pr && ProviderCatalog.Status(pr, settings).IsReady)
            .Select(parts => (ProviderCatalog.Get(parts[0]), parts[1]))
            .Take(8).ToList();
    }

    private bool IsActive(ProviderInfo provider, string modelId)
    {
        var settings = _host.Settings;
        return settings.Ai.Engine != "auto" && AiEngineFactory.ResolveEngine(settings) == provider.Id && (settings.Ai.ModelFor(provider.Id) ?? "") == modelId;
    }

    private void Render()
    {
        _list.Children.Clear();
        var query = _search.Text.Trim();
        if (query.Length > 0)
        {
            RenderSearch(query);
            return;
        }
        switch (_tab)
        {
            case "auto":
                RenderAutomatic();
                break;
            case "recent":
                _list.Children.Add(Header(null, "Recent", "The models you picked lately", null));
                foreach (var (provider, model) in Recent())
                {
                    var known = ModelCatalog.Known(provider).FirstOrDefault(m => m.Id == model) ?? new ModelInfo(model, ProviderCatalog.ModelLabel(provider, model));
                    _list.Children.Add(ModelRow.Make(known, provider.Id, IsActive(provider, model), () => _chosen(provider.Id, model.Length == 0 ? null : model), note: provider.Name));
                }
                break;
            default:
                if (ProviderCatalog.Find(_tab) is not { } selected) { _tab = "auto"; RenderAutomatic(); return; }
                RenderProvider(selected);
                break;
        }
    }

    private void RenderAutomatic()
    {
        var p = Palette.Current;
        var settings = _host.Settings;
        var resolved = ProviderCatalog.Get(AiEngineFactory.ResolveEngine(settings));
        _list.Children.Add(Header(null, "Automatic", "TileSmith picks for you", null));
        var auto = new ModelInfo("auto", "Automatic", $"Uses {resolved.Name} now, and the next ready AI when it can't be reached") { Brand = Brands.ForProvider(resolved.Id) };
        _list.Children.Add(ModelRow.Make(auto, resolved.Id, settings.Ai.Engine == "auto", () => _chosen("auto", null)));
        var chain = AiEngineFactory.Chain(settings);
        if (chain.Count > 1)
        {
            var label = Ui.Text("FALLBACK ORDER", 10.5, p.Text3, semibold: true);
            label.CharacterSpacing = 60;
            label.Margin = new Thickness(12, 16, 0, 6);
            _list.Children.Add(label);
            var order = new WrapPanel { HorizontalSpacing = 6, VerticalSpacing = 6, Margin = new Thickness(10, 0, 0, 0) };
            foreach (var id in chain)
            {
                var provider = ProviderCatalog.Get(id);
                var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                chip.Children.Add(new BrandIcon(Brands.ForProvider(id), 14, "api"));
                chip.Children.Add(Ui.Text(provider.Name, 12, p.Text2));
                order.Children.Add(new Border { Child = chip, Padding = new Thickness(8, 5, 10, 5), CornerRadius = new CornerRadius(10), Background = p.Brush(p.Overlay(0.05)) });
            }
            _list.Children.Add(order);
        }
    }

    private void RenderProvider(ProviderInfo provider)
    {
        var models = Models(provider);
        var fetched = ModelCatalog.LastFetched(provider);
        var note = _loading.Contains(provider.Id) ? "Updating the list…"
            : _errors.TryGetValue(provider.Id, out var error) ? "Couldn't update: " + error
            : $"{models.Count(m => m.Id.Length > 0)} models" + (fetched is { } f ? $" · updated {Ago(f)}" : "");
        _list.Children.Add(Header(provider, provider.Name, note, provider.Id == "command" ? null : () => _ = RefreshAsync(provider)));
        var shown = 0;
        foreach (var m in models)
        {
            if (shown++ >= MaxRows)
            {
                _list.Children.Add(More(models.Count - MaxRows));
                break;
            }
            var id = m.Id;
            _list.Children.Add(ModelRow.Make(m, provider.Id, IsActive(provider, id), () => _chosen(provider.Id, id.Length == 0 ? null : id)));
        }
    }

    private void RenderSearch(string query)
    {
        var any = false;
        foreach (var provider in Providers())
        {
            var matches = Models(provider).Where(m => m.Id.Length > 0 && (m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || m.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || provider.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || (query.Equals("free", StringComparison.OrdinalIgnoreCase) && m.Free))).Take(40).ToList();
            if (matches.Count == 0) continue;
            any = true;
            _list.Children.Add(Header(provider, provider.Name, $"{matches.Count} match{(matches.Count == 1 ? "" : "es")}", null, compact: true));
            foreach (var m in matches)
            {
                var id = m.Id;
                _list.Children.Add(ModelRow.Make(m, provider.Id, IsActive(provider, id), () => _chosen(provider.Id, id)));
            }
        }
        if (!any) _list.Children.Add(Kit.Empty("search_off", "No models match", "Try another name — or “free”."));
    }

    private static List<ModelInfo> Models(ProviderInfo provider)
    {
        var models = ModelCatalog.Known(provider).ToList();
        if (models.Count == 0 || models.All(m => m.Id.Length > 0))
            models.Insert(0, ModelInfo.Default(provider.DefaultModel is { } d ? $"Uses {d}" : "The provider's default model") with { Brand = Brands.ForProvider(provider.Id) });
        return models;
    }

    private static Grid Header(ProviderInfo? provider, string title, string note, Action? refresh, bool compact = false)
    {
        var p = Palette.Current;
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(6, compact ? 12 : 4, 0, 6), MinHeight = compact ? 0 : 52 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (provider is not null) grid.Children.Add(ProviderLogo.Make(provider, compact ? 22 : 34));
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        texts.Children.Add(Ui.Text(title, compact ? 12.5 : 15, p.Text1, semibold: true));
        // one line, always present: the note changes in place without moving the rows below
        texts.Children.Add(Ui.TipWhenTrimmed(Ui.Text(note, 11.5, p.Text3)));
        Grid.SetColumn(texts, provider is null ? 0 : 1);
        if (provider is null) Grid.SetColumnSpan(texts, 2);
        grid.Children.Add(texts);
        if (refresh is not null)
        {
            var button = new IconButton("refresh", "Update the list", 32, 17) { VerticalAlignment = VerticalAlignment.Center };
            button.Click += (_, _) => refresh();
            Grid.SetColumn(button, 2);
            grid.Children.Add(button);
        }
        return grid;
    }

    private static TextBlock More(int count)
    {
        var text = Ui.Text($"{count} more — search to find them", 11.5, Palette.Current.Text3);
        text.Margin = new Thickness(12, 8, 0, 4);
        return text;
    }

    private static string Ago(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        return span.TotalMinutes < 1 ? "just now" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min ago" : span.TotalDays < 1 ? $"{(int)span.TotalHours} h ago" : when.LocalDateTime.ToString("d MMM");
    }
}
