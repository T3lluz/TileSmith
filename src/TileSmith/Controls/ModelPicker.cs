using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using TileSmith.Core;
using TileSmith.Core.Ai;
using TileSmith.Services;
using TileSmith.Theme;

namespace TileSmith.Controls;

/// <summary>
/// Picks the AI: every ready provider with its models (live lists, cached), recent picks on top, search, and a
/// link to set up more providers. Shown in a flyout from the panel's model chip.
/// </summary>
public sealed class ModelPicker : Grid
{
    private static readonly TimeSpan Stale = TimeSpan.FromHours(6);

    private readonly AppHost _host;
    private readonly Action<string, string?> _chosen;
    private readonly StackPanel _list = new() { Spacing = 2 };
    private readonly TextBox _search;
    private readonly HashSet<string> _loading = [];
    private readonly Dictionary<string, string> _errors = [];
    private readonly CancellationTokenSource _cts = new();

    public ModelPicker(AppHost host, Action<string, string?> chosen, Action manage)
    {
        _host = host;
        _chosen = chosen;
        var p = Palette.Current;
        Width = 340;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _search = Kit.SearchBox("Search models");
        _search.Margin = new Thickness(12, 12, 12, 8);
        _search.TextChanged += (_, _) => Rebuild();
        Children.Add(_search);

        var scroll = new ScrollViewer { Content = _list, MaxHeight = 400, Padding = new Thickness(6, 0, 6, 6), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SetRow(scroll, 1);
        Children.Add(scroll);

        var footer = new Grid { BorderBrush = p.Brush(p.Hairline), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(6) };
        var manageButton = new Chip("AI providers and keys…", "tune", style: ChipStyle.Ghost, height: 34) { HorizontalAlignment = HorizontalAlignment.Stretch };
        manageButton.Click += (_, _) => manage();
        footer.Children.Add(manageButton);
        SetRow(footer, 2);
        Children.Add(footer);

        Unloaded += (_, _) => _cts.Cancel();
        Rebuild();
        RefreshStale();
    }

    /// <summary>Wraps a picker in a styled flyout.</summary>
    public static Flyout Flyout(ModelPicker picker)
    {
        var p = Palette.Current;
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(18)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, p.Brush(p.Card)));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, p.Brush(p.HairlineStrong)));
        style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 420.0));
        return new Flyout { Content = picker, FlyoutPresenterStyle = style, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
    }

    private IEnumerable<ProviderInfo> Providers()
    {
        var active = AiEngineFactory.ResolveEngine(_host.Settings);
        return ProviderCatalog.All.Where(pr => pr.Id == active || ProviderCatalog.Status(pr, _host.Settings).IsReady);
    }

    private void RefreshStale()
    {
        foreach (var provider in Providers())
        {
            if (provider.Id is "claude" or "command") continue;
            var fetched = ModelCatalog.LastFetched(provider);
            if (fetched is { } f && DateTimeOffset.Now - f < Stale) continue;
            _ = RefreshAsync(provider);
        }
    }

    private async Task RefreshAsync(ProviderInfo provider)
    {
        _loading.Add(provider.Id);
        Rebuild();
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
        Rebuild();
    }

    private void Rebuild()
    {
        var p = Palette.Current;
        _list.Children.Clear();
        var query = _search.Text.Trim();
        var settings = _host.Settings;
        var active = AiEngineFactory.ResolveEngine(settings);
        var activeModel = settings.Ai.ModelFor(active);
        bool Matches(ProviderInfo provider, ModelInfo m) => query.Length == 0
            || m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Id.Contains(query, StringComparison.OrdinalIgnoreCase)
            || provider.Name.Contains(query, StringComparison.OrdinalIgnoreCase);

        if (query.Length == 0)
        {
            var auto = settings.Ai.Engine == "auto";
            var resolved = ProviderCatalog.Get(active);
            _list.Children.Add(Row(null, "Automatic", $"Uses {resolved.Name} now and falls back when it can't be reached", auto, () => _chosen("auto", null)));

            var recent = settings.Ai.RecentModels
                .Select(r => r.Split('/', 2))
                .Where(parts => parts.Length == 2 && ProviderCatalog.Find(parts[0]) is { } pr && ProviderCatalog.Status(pr, settings).IsReady)
                .Take(3).ToList();
            if (recent.Count > 0)
            {
                _list.Children.Add(SectionHeader("Recent", null, null));
                foreach (var parts in recent)
                {
                    var provider = ProviderCatalog.Get(parts[0]);
                    var model = parts[1];
                    var known = ModelCatalog.Known(provider).FirstOrDefault(m => m.Id == model);
                    var isActive = !auto && active == provider.Id && activeModel == model;
                    _list.Children.Add(Row(provider, known?.Name ?? ProviderCatalog.ModelLabel(provider, model), provider.Name, isActive, () => _chosen(provider.Id, model)));
                }
            }
        }

        foreach (var provider in Providers())
        {
            var models = ModelCatalog.Known(provider).Where(m => Matches(provider, m)).ToList();
            if (query.Length > 0 && models.Count == 0) continue;
            var header = SectionHeader(provider.Name, provider, _loading.Contains(provider.Id) ? "Loading…" : _errors.ContainsKey(provider.Id) ? "Couldn't load" : provider.KindLabel);
            if (_errors.TryGetValue(provider.Id, out var error)) ToolTipService.SetToolTip(header, error);
            _list.Children.Add(header);
            var shown = 0;
            foreach (var m in models)
            {
                if (shown++ >= (query.Length > 0 ? 40 : 12))
                {
                    _list.Children.Add(Ui.Text($"   {models.Count - 12} more — search to find them", 11.5, p.Text3));
                    break;
                }
                var isActive = settings.Ai.Engine != "auto" && active == provider.Id && (activeModel ?? "") == m.Id;
                var id = m.Id;
                _list.Children.Add(Row(null, m.Name, m.Detail, isActive, () => _chosen(provider.Id, string.IsNullOrEmpty(id) ? null : id), indent: true));
            }
            if (models.Count == 0 && query.Length == 0)
                _list.Children.Add(Row(null, "Default", "The provider's default model", settings.Ai.Engine != "auto" && active == provider.Id && activeModel is null, () => _chosen(provider.Id, null), indent: true));
        }
        if (_list.Children.Count == 0) _list.Children.Add(Kit.Empty("search_off", "No models match", "Try another name, or add providers in Settings."));
    }

    private static Grid SectionHeader(string title, ProviderInfo? provider, string? note)
    {
        var p = Palette.Current;
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 12, 10, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (provider is not null) grid.Children.Add(new Avatar(provider.Monogram, Palette.Parse(provider.Color), 22));
        var text = Ui.Text(title.ToUpperInvariant(), 11, p.Text2, semibold: true);
        text.CharacterSpacing = 60;
        text.VerticalAlignment = VerticalAlignment.Center;
        SetColumn(text, provider is null ? 0 : 1);
        if (provider is null) SetColumnSpan(text, 2);
        grid.Children.Add(text);
        if (note is not null)
        {
            var noteText = Ui.Text(note, 11, p.Text3);
            noteText.VerticalAlignment = VerticalAlignment.Center;
            SetColumn(noteText, 2);
            grid.Children.Add(noteText);
        }
        return grid;
    }

    private static Pressable Row(ProviderInfo? provider, string name, string? detail, bool active, Action onClick, bool indent = false)
    {
        var p = Palette.Current;
        var row = new Pressable { CornerRadius = new CornerRadius(12), Fill = active ? p.AccentSoft : Colors.Transparent, MinHeight = 40 };
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(indent ? 42 : 10, 7, 10, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (provider is not null) grid.Children.Add(new Avatar(provider.Monogram, Palette.Parse(provider.Color), 24));
        else if (!indent) grid.Children.Add(Glyph.Make("auto_awesome", 18, p.Accent));
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.TipWhenTrimmed(Ui.Text(name, 13, active ? p.Text1 : p.Text1, semibold: active)));
        if (!string.IsNullOrWhiteSpace(detail)) texts.Children.Add(Ui.TipWhenTrimmed(Ui.Text(detail!, 11.5, p.Text3, maxLines: 2)));
        SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (active)
        {
            var check = Glyph.Make("check", 18, p.Accent);
            SetColumn(check, 2);
            grid.Children.Add(check);
        }
        row.SetContent(grid);
        row.AccessibleName = name;
        row.Click += (_, _) => onClick();
        return row;
    }
}
