using System.Collections.Specialized;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Controls;
using TileSmith.Core;
using TileSmith.Core.Actions;
using TileSmith.Core.Ai;
using TileSmith.Core.Widgets;
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
        : base(host, window, "Widgets", "Everything on your panel. Arrange and resize tiles right in the panel: hold a tile to move it.")
    {
        Children.Add(_messages);
        Children.Add(Kit.Section("Your widgets", null));
        Children.Add(Kit.Card(_list));
        Section("Backup and sharing", null,
            Kit.Row("upload", "Export", "Save all widgets to a .json file to back them up or share them.", Kit.Button("Export…", null, ChipStyle.Outline, () => _ = ExportAsync())),
            Kit.Row("download", "Import", "Add widgets from a .json file. Check what they run before importing files from others.", Kit.Button("Import…", null, ChipStyle.Outline, () => _ = ImportAsync())),
            Kit.Row("folder_code", "Helper scripts", "Scripts the AI writes for your widgets live here.", Kit.Button("Open folder", "open_in_new", ChipStyle.Outline, OpenTools)),
            Kit.Row("restart_alt", "Start over", "Replace all widgets with the starter set.", Kit.Button("Reset widgets", null, ChipStyle.Outline, () => _ = ResetAsync()), Palette.Current.Warning));
        Host.Widgets.CollectionChanged += OnChanged;
        BuildList();
    }

    public override void OnHidden() => Host.Widgets.CollectionChanged -= OnChanged;

    private void OnChanged(object? sender, NotifyCollectionChangedEventArgs e) => BuildList();

    private void BuildList()
    {
        var p = Palette.Current;
        _list.Children.Clear();
        if (Host.Widgets.Count == 0)
        {
            _list.Children.Add(Kit.Empty("widgets", "No widgets yet", "Add ready-made ones from the panel's gallery, or describe one to the AI."));
            return;
        }
        var first = true;
        foreach (var widget in Host.Widgets)
        {
            if (!first) _list.Children.Add(new Border { Height = 1, Background = p.Brush(p.Hairline), Margin = new Thickness(16, 0, 16, 0) });
            first = false;
            var actions = widget.AllActions().Select(a => ActionCatalog.Find(a["type"]?.ToString())).OfType<ActionInfo>().ToList();
            var traits = new List<string>();
            if (actions.Any(a => a.Traits.HasFlag(ActionTraits.Script))) traits.Add("runs scripts");
            if (actions.Any(a => a.Traits.HasFlag(ActionTraits.Server))) traits.Add("uses your server");
            if (actions.Any(a => a.Traits.HasFlag(ActionTraits.Network))) traits.Add("uses the internet");
            var size = widget.Cells;
            var description = $"{size.W} × {size.H} {WidgetTile.SizeName(size).ToLowerInvariant()} · {widget.Controls.Count} control{(widget.Controls.Count == 1 ? "" : "s")}";
            if (traits.Count > 0) description += " · " + string.Join(", ", traits);
            var id = widget.Id;
            var remove = new IconButton("delete", "Remove", 34, 18);
            remove.Click += (_, _) =>
            {
                var index = Host.IndexOf(id);
                var removed = Host.RemoveWidget(id);
                if (removed is not null) Show(Kit.Tone.Info, $"Removed “{removed.Title}”", Kit.Button("Undo", "undo", ChipStyle.Tonal, () => Host.InsertWidget(index, removed)));
            };
            _list.Children.Add(Kit.Row(widget.Icon ?? "widgets", widget.Title, description, remove));
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
        await File.WriteAllTextAsync(file.Path, JsonSerializer.Serialize(Host.Widgets.ToList(), JsonDefaults.Options));
        Show(Kit.Tone.Success, $"Exported {Host.Widgets.Count} widgets.");
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
            var imported = WidgetStore.Import(await File.ReadAllTextAsync(file.Path));
            foreach (var w in imported) Host.UpsertWidget(w);
            Show(Kit.Tone.Success, $"Imported {imported.Count} widget{(imported.Count == 1 ? "" : "s")}.");
        }
        catch (Exception ex)
        {
            Show(Kit.Tone.Error, "Couldn't import: " + ex.Message);
        }
    }

    private static void OpenTools()
    {
        Directory.CreateDirectory(AgentPrompt.ToolsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AgentPrompt.ToolsDir}\"") { UseShellExecute = true })?.Dispose();
    }

    private async Task ResetAsync()
    {
        var dialog = new ContentDialog
        {
            Title = "Reset widgets?",
            Content = "All your widgets will be replaced with the starter set. Export them first if you want to keep them.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Host.Widgets.Clear();
        foreach (var w in WidgetStore.Defaults()) Host.Widgets.Add(w);
        Host.SaveWidgets();
        Show(Kit.Tone.Success, "Widgets reset.");
    }
}
