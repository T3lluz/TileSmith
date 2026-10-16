using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Controls.Tiles;
using TileSmith.Core.Tiles;
using TileSmith.Services;
using TileSmith.Theme;

namespace TileSmith.Controls;

/// <summary>
/// "Add a widget": the ready-made designs, each drawn as it will look on the panel (with sample values), grouped by
/// what they're for. One click adds one; the pencil opens it in the Studio first. A card at the top hands anything
/// else to the AI. Shown in place of the board.
/// </summary>
public sealed class GallerySheet : StackPanel
{
    private readonly AppHost _host;
    private readonly Action<TileDesign> _add;
    private readonly Action<TileStarters.Starter> _customize;
    private readonly IReadOnlyList<TileStarters.Starter> _starters;
    private readonly WrapPanel _chips = new() { HorizontalSpacing = 6, VerticalSpacing = 6 };
    private readonly StackPanel _results = new() { Spacing = 8 };
    private readonly TextBox _search;
    private readonly double _width;
    private string _category = "All";

    public GallerySheet(AppHost host, double width, Action<TileDesign> add, Action<TileStarters.Starter> customize, Action describe, Action close)
    {
        _host = host;
        _add = add;
        _customize = customize;
        _width = width;
        Spacing = 12;
        Width = width;
        var p = Palette.Current;
        _starters = TileStarters.All.Where(s => s.IsAvailable(host.Environment)).ToList();

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
        aiTexts.Children.Add(Ui.Text("Describe it and watch it appear in Widget Studio", 12, p.Text2));
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
        foreach (var category in new[] { "All" }.Concat(TileStarters.Categories.Where(c => _starters.Any(s => s.Category == c))))
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
        var matches = _starters.Where(s =>
            (_category == "All" || s.Category == _category || query.Length > 0)
            && (query.Length == 0 || s.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Prompt.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.Keywords.Any(k => k.Contains(query, StringComparison.OrdinalIgnoreCase)) || s.Category.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        if (matches.Count == 0)
        {
            _results.Children.Add(Kit.Empty("search_off", "Nothing ready-made for that", "Describe it to the AI instead — it can build almost anything."));
            return;
        }
        var groups = _category == "All" && query.Length == 0 ? matches.GroupBy(s => s.Category) : matches.GroupBy(_ => "");
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
            var wrap = new WrapPanel { HorizontalSpacing = 10, VerticalSpacing = 12 };
            // full-width ones first in each group reads calmer than a mix
            foreach (var starter in group.OrderBy(s => s.Design.Size.Cols < 4 ? 1 : 0))
            {
                var card = Card(starter);
                wrap.Children.Add(card);
                Motion.Enter(card, 8, Math.Min(delay, 240), Motion.Normal);
                delay += 18;
            }
            _results.Children.Add(wrap);
        }
    }

    private FrameworkElement Card(TileStarters.Starter starter)
    {
        var p = Palette.Current;
        var d = starter.Design;
        var (w, h) = TileLayout.SizeOf(d.Size);
        if (d.Size.IsFullWidth) w = _width;
        var view = new TileView(d, w, h, TileTheme.For(d, _host.Settings.Accent), null, preview: true);
        var host = new Grid { Width = w, Height = h };
        host.Children.Add(view);
        if (p.IsGlass) host.Children.Add(Glass.Overlay(host, TileLayout.Radius));
        var press = new Pressable { CornerRadius = new CornerRadius(TileLayout.Radius), AccessibleName = $"Add {starter.Title}" };
        press.SetContent(host);
        ToolTipService.SetToolTip(press, $"{starter.Prompt}\nClick to add it to your panel");

        var caption = new Grid { Width = w, ColumnSpacing = 6 };
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        caption.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var name = Ui.Text(starter.Title, 12.5, p.Text1, semibold: true);
        texts.Children.Add(name);
        var status = Ui.Text($"{d.Size.Label} · {d.Size.Cells}", 11, p.Text3);
        texts.Children.Add(status);
        caption.Children.Add(texts);
        var edit = new IconButton("edit", "Change it in Widget Studio first", 30, 16) { VerticalAlignment = VerticalAlignment.Center };
        edit.Click += (_, _) => _customize(starter);
        Grid.SetColumn(edit, 1);
        caption.Children.Add(edit);

        var stack = new StackPanel { Spacing = 6, Width = w };
        stack.Children.Add(press);
        stack.Children.Add(caption);
        press.Click += (_, _) =>
        {
            var design = TileStarters.Instantiate(starter);
            // the gallery's own feeds are known good: no "allow this site?" for them
            var sites = TileStarters.SitesOf(design).ToList();
            foreach (var url in sites) TileApprovals.ApproveSite(_host.Settings.Tiles, url);
            if (sites.Count > 0) _host.SaveSettings();
            _add(design);
            status.Text = "✓ Added to your panel";
            status.Foreground = p.Brush(p.Accent);
            Motion.Pulse(press, 1.04f);
        };
        return stack;
    }
}
