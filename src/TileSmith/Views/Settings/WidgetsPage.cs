using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Controls;
using TileSmith.Core;
using TileSmith.Core.Tiles;
using TileSmith.Services;
using TileSmith.Theme;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TileSmith.Views.Settings;

public sealed class WidgetsPage : SettingsPage
{
    private readonly StackPanel _list = new();
    private readonly StackPanel _messages = new() { Spacing = 8 };

    public WidgetsPage(AppHost host, SettingsWindow window)
        : base(host, window, "Widgets", "Every widget you made or added. Arrange and resize them right in the panel: hold a tile to move it.")
    {
        Children.Add(_messages);
        Children.Add(Kit.Section("Your widgets", null));
        Children.Add(Kit.Card(_list));
        var town = Kit.TextField("Oslo, or Bergen, Norway", Host.Settings.Tiles.HomeTown ?? "", v =>
        {
            Host.Settings.Tiles.HomeTown = v.Length == 0 ? null : v;
            Host.SaveSettings();
            Show(Kit.Tone.Success, v.Length == 0 ? "Weather widgets will ask for a town." : $"Weather widgets now use {v}.");
        }, 220);
        Section("Data", null,
            Kit.Row("location_on", "Your town", "What weather widgets use when they don't name a place.", town),
            Kit.Row("thermostat", "Units", "Temperatures and wind speed.",
                Kit.Segmented([("metric", "°C, m/s"), ("imperial", "°F, mph")], Host.Settings.Tiles.Imperial ? "imperial" : "metric", id =>
                {
                    Host.Settings.Tiles.Imperial = id == "imperial";
                    Host.SaveSettings();
                }, 200)),
            Kit.Row("verified_user", "Allowed scripts and sites", $"{Host.Settings.Tiles.ApprovedCommands.Count} script{(Host.Settings.Tiles.ApprovedCommands.Count == 1 ? "" : "s")} and {Host.Settings.Tiles.ApprovedSites.Count} site{(Host.Settings.Tiles.ApprovedSites.Count == 1 ? "" : "s")} widgets may use. Forgetting them makes every widget ask again.",
                Kit.Button("Forget all", null, ChipStyle.Outline, () =>
                {
                    Host.Settings.Tiles.ApprovedCommands.Clear();
                    Host.Settings.Tiles.ApprovedSites.Clear();
                    Host.SaveSettings();
                    Show(Kit.Tone.Success, "Widgets will ask again before running scripts or reading sites.");
                })));
        Section("Backup and sharing", null,
            Kit.Row("upload", "Export", "Save all widgets to a .json file to back them up or share them.", Kit.Button("Export…", null, ChipStyle.Outline, () => _ = ExportAsync())),
            Kit.Row("download", "Import", "Add widgets from a .json file. Scripts and sites in them still ask before they run.", Kit.Button("Import…", null, ChipStyle.Outline, () => _ = ImportAsync())),
            Kit.Row("restart_alt", "Start over", "Put the starter Quick Settings back on the panel. Your widgets stay in Widget Studio.", Kit.Button("Reset panel", null, ChipStyle.Outline, () => _ = ResetAsync()), Palette.Current.Warning));
        Host.Library.Changed += OnChanged;
        BuildList();
    }

    public override void OnHidden() => Host.Library.Changed -= OnChanged;

    private void OnChanged(string? id) => BuildList();

    private void BuildList()
    {
        var p = Palette.Current;
        _list.Children.Clear();
        var designs = Host.Library.Designs.OrderBy(d => Host.Library.IsPlaced(d.Id) ? Host.Library.PanelIndexOf(d.Id) : int.MaxValue).ToList();
        if (designs.Count == 0)
        {
            _list.Children.Add(Kit.Empty("widgets", "No widgets yet", "Add ready-made ones from the panel's gallery, or describe one in Widget Studio."));
            return;
        }
        var first = true;
        foreach (var design in designs)
        {
            if (!first) _list.Children.Add(new Border { Height = 1, Background = p.Brush(p.Hairline), Margin = new Thickness(16, 0, 16, 0) });
            first = false;
            var placed = Host.Library.IsPlaced(design.Id);
            var sources = design.Blocks.Select(b => TileSources.ByKey(b.Bind)).OfType<TileSource>().ToList();
            var traits = new List<string>();
            if (sources.Any(s => s.Key is "ps.value")) traits.Add("runs a script");
            if (sources.Any(s => s.Group == SourceGroup.Server)) traits.Add("uses your server");
            if (sources.Any(s => s.Key.StartsWith("web.", StringComparison.Ordinal) || s.Group == SourceGroup.Weather)) traits.Add("uses the internet");
            if (sources.Any(s => s.Key.StartsWith("ai.", StringComparison.Ordinal))) traits.Add("asks your AI");
            var description = $"{design.Size.Label} {design.Size.Cells} · {design.Blocks.Count} block{(design.Blocks.Count == 1 ? "" : "s")}" + (placed ? " · on your panel" : "");
            if (traits.Count > 0) description += " · " + string.Join(", ", traits);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            var open = new IconButton("auto_awesome", "Open in Widget Studio", 34, 18);
            open.Click += (_, _) => App.Current.OpenStudio(null, design);
            actions.Children.Add(open);
            var place = new IconButton(placed ? "remove_from_queue" : "add_to_queue", placed ? "Take off the panel" : "Put on the panel", 34, 18);
            place.Click += (_, _) =>
            {
                if (placed) Host.Library.Unplace(design.Id);
                else Host.Library.Place(design);
            };
            actions.Children.Add(place);
            var remove = new IconButton("delete", "Delete", 34, 18);
            remove.Click += (_, _) =>
            {
                var (removed, index) = Host.Library.Delete(design.Id);
                if (removed is not null)
                    Show(Kit.Tone.Info, $"Deleted “{removed.Name}”", Kit.Button("Undo", "undo", ChipStyle.Tonal, () =>
                    {
                        Host.Library.Upsert(removed, touch: false);
                        if (index >= 0) Host.Library.Place(removed, index);
                    }));
            };
            actions.Children.Add(remove);
            var icon = design.Blocks.Select(b => b.Icon ?? TileSources.ByKey(b.Bind)?.Icon).FirstOrDefault(i => i is not null) ?? "widgets";
            _list.Children.Add(Kit.Row(icon, design.Name, description, actions));
        }
    }

    private void Show(Kit.Tone tone, string message, UIElement? action = null)
    {
        _messages.Children.Clear();
        var banner = Kit.Banner(tone, message, null, action);
        banner.Margin = new Thickness(0, 12, 0, 0);
        _messages.Children.Add(banner);
        Motion.Enter(banner, 8);
    }

    private async Task ExportAsync()
    {
        var picker = new FileSavePicker { SuggestedFileName = "tilesmith-widgets", SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        InitializeWithWindow.Initialize(picker, Window.Handle);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var designs = Host.Library.Designs;
        var json = new JsonArray(designs.Select(d => (JsonNode)TileCodec.ToJson(d, withChat: false)).ToArray()).ToJsonString(JsonDefaults.Options);
        await File.WriteAllTextAsync(file.Path, json);
        Show(Kit.Tone.Success, $"Exported {designs.Count} widgets.");
    }

    private async Task ImportAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, Window.Handle);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var text = await File.ReadAllTextAsync(file.Path);
            var node = JsonNode.Parse(text, documentOptions: JsonDefaults.DocumentOptions);
            var items = node switch { JsonArray a => a.OfType<JsonObject>().ToList(), JsonObject o => [o], _ => [] };
            var count = 0;
            foreach (var o in items)
            {
                // a widgets.json from TileSmith 0.4 converts; a design comes in as a copy of its own
                var design = o.ContainsKey("controls") || o.ContainsKey("layout") ? TileMigration.Convert(o) : TileCodec.Parse(o) with { Id = TileDesign.NewId() };
                if (design is null || design.Blocks.Count == 0) continue;
                Host.Library.Place(design with { Id = TileDesign.NewId() });
                count++;
            }
            Show(count > 0 ? Kit.Tone.Success : Kit.Tone.Warning, count > 0 ? $"Imported {count} widget{(count == 1 ? "" : "s")} onto your panel." : "There were no widgets in that file.");
        }
        catch (Exception ex)
        {
            Show(Kit.Tone.Error, "Couldn't import: " + ex.Message);
        }
    }

    private async Task ResetAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Reset the panel?",
            Content = "The panel goes back to the starter Quick Settings. Your widgets stay in Widget Studio, so you can put them back.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Host.Library.ResetPanel(Host.DetectEnvironment());
        Show(Kit.Tone.Success, "Panel reset.");
    }
}
