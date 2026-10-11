using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Core.Widgets;
using TileSmith.Services;
using TileSmith.Theme;

namespace TileSmith.Controls;

/// <summary>
/// "Add a widget": ready-made tiles grouped by category (one tap adds them, no AI needed), search, and a shortcut
/// to describe anything else to the AI. Shown in place of the board.
/// </summary>
public sealed class GallerySheet : StackPanel
{
    private readonly AppHost _host;
    private readonly Action<WidgetDefinition> _add;
    private readonly IReadOnlyList<WidgetTemplate> _templates;
    private readonly WrapPanel _chips = new() { HorizontalSpacing = 6, VerticalSpacing = 6 };
    private readonly StackPanel _results = new() { Spacing = 8 };
    private readonly TextBox _search;
    private readonly double _width;
    private string _category = "All";

    public GallerySheet(AppHost host, double width, Action<WidgetDefinition> add, Action describe, Action close)
    {
        _host = host;
        _add = add;
        _width = width;
        Spacing = 12;
        Width = width;
        var p = Palette.Current;
        _templates = WidgetTemplates.ForThisPc(host.Settings.Server.IsConfigured);

        var header = new Grid { ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var back = new IconButton("arrow_back", "Back to the panel", 34, 20);
        back.Click += (_, _) => close();
        header.Children.Add(back);
        var title = Ui.Text("Add a widget", 17, p.Text1, semibold: true);
        title.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        var done = new Chip("Done", "check", filled: true);
        done.Click += (_, _) => close();
        done.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(done, 2);
        header.Children.Add(done);
        Children.Add(header);

        // the AI shortcut
        var ai = new Pressable { Fill = p.AccentSoft, CornerRadius = new CornerRadius(18) };
        var aiRow = new Grid { Padding = new Thickness(14, 12, 14, 12), ColumnSpacing = 12 };
        aiRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        aiRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        aiRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        aiRow.Children.Add(new Border { Width = 38, Height = 38, CornerRadius = new CornerRadius(19), Background = p.Brush(p.Accent), Child = Glyph.Make("auto_awesome", 20, p.OnAccent) });
        var aiTexts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        aiTexts.Children.Add(Ui.Text("Build anything with AI", 14, p.Text1, semibold: true));
        aiTexts.Children.Add(Ui.Text("Describe it — TileSmith writes, tests and adds it", 12, p.Text2));
        Grid.SetColumn(aiTexts, 1);
        aiRow.Children.Add(aiTexts);
        var arrow = Glyph.Make("arrow_forward", 20, p.Accent);
        Grid.SetColumn(arrow, 2);
        aiRow.Children.Add(arrow);
        ai.SetContent(aiRow);
        ai.Click += (_, _) => describe();
        Children.Add(ai);

        _search = Kit.SearchBox("Search ready-made widgets");
        _search.TextChanged += (_, _) => Render();
        Children.Add(_search);

        Children.Add(_chips);
        Children.Add(_results);
        BuildChips();
        Render();
    }

    public void FocusSearch() => _search.Focus(FocusState.Programmatic);

    private void BuildChips()
    {
        _chips.Children.Clear();
        foreach (var category in new[] { "All" }.Concat(WidgetTemplates.Categories.Where(c => _templates.Any(t => t.Category == c))))
        {
            var chip = new Chip(category, style: category == _category ? ChipStyle.Filled : ChipStyle.Outline, height: 30);
            chip.Click += (_, _) =>
            {
                _category = category;
                BuildChips();
                Render();
            };
            _chips.Children.Add(chip);
        }
    }

    private void Render()
    {
        var p = Palette.Current;
        _results.Children.Clear();
        var query = _search.Text.Trim();
        var matches = _templates.Where(t =>
            (_category == "All" || t.Category == _category || query.Length > 0)
            && (query.Length == 0 || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || t.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.Category.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        if (matches.Count == 0)
        {
            _results.Children.Add(Kit.Empty("search_off", "Nothing ready-made for that", "Describe it to the AI instead — it can build almost anything."));
            return;
        }
        var groups = _category == "All" && query.Length == 0 ? matches.GroupBy(t => t.Category) : matches.GroupBy(_ => "");
        var delay = 0;
        foreach (var group in groups)
        {
            if (group.Key.Length > 0)
            {
                var label = Ui.Text(group.Key.ToUpperInvariant(), 11, p.Text3, semibold: true);
                label.CharacterSpacing = 80;
                label.Margin = new Thickness(4, 6, 0, 0);
                _results.Children.Add(label);
            }
            var wrap = new WrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };
            foreach (var template in group)
            {
                var card = Card(template);
                wrap.Children.Add(card);
                Motion.Enter(card, 8, Math.Min(delay, 240), Motion.Normal);
                delay += 18;
            }
            _results.Children.Add(wrap);
        }
    }

    private Pressable Card(WidgetTemplate template)
    {
        var p = Palette.Current;
        var preview = template.Create();
        var onPanel = _host.Widgets.Any(w => string.Equals(w.Title, preview.Title, StringComparison.OrdinalIgnoreCase));
        var card = new Pressable { Width = (_width - 8) / 2, Height = 76, CornerRadius = new CornerRadius(16), Fill = p.Tile };
        var grid = new Grid { Padding = new Thickness(12, 10, 12, 10), ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var circle = new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = p.Brush(p.AccentSoft), VerticalAlignment = VerticalAlignment.Center };
        var icon = Glyph.Make(preview.Icon, 19, p.Accent);
        circle.Child = icon;
        grid.Children.Add(circle);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(Ui.TipWhenTrimmed(Ui.Text(template.Name, 13, p.Text1, semibold: true)));
        var size = Ui.Text(preview.Size.Replace('x', '×'), 10.5, p.Text3);
        size.Margin = new Thickness(6, 0, 0, 0);
        size.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(size, 1);
        top.Children.Add(size);
        texts.Children.Add(top);
        var description = Ui.Text(onPanel ? "✓ On your panel" : template.Description, 11.5, onPanel ? p.Accent : p.Text2, maxLines: 2);
        if (onPanel) ToolTipService.SetToolTip(description, "Already on your panel — tap to add another");
        texts.Children.Add(description);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        card.SetContent(grid);
        card.AccessibleName = $"Add {template.Name}";
        ToolTipService.SetToolTip(card, template.Description);
        card.Click += (_, _) =>
        {
            _add(template.Build());
            icon.Text = Core.Widgets.Icons.Glyph("check");
            circle.Background = p.Brush(p.Accent);
            icon.Foreground = p.Brush(p.OnAccent);
            description.Text = "Added to your panel";
            description.Foreground = p.Brush(p.Accent);
            Motion.Pulse(circle);
        };
        Motion.CenterScale(circle);
        return card;
    }
}
