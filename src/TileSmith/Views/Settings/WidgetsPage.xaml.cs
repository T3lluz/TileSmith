using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Core;
using TileSmith.Core.Actions;
using TileSmith.Core.Ai;
using TileSmith.Core.Widgets;
using TileSmith.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TileSmith.Views.Settings;

public sealed partial class WidgetsPage : UserControl
{
    private readonly AppHost _host;
    private readonly SettingsWindow _window;

    public WidgetsPage(AppHost host, SettingsWindow window)
    {
        _host = host;
        _window = window;
        InitializeComponent();
        _host.Widgets.CollectionChanged += OnChanged;
        Unloaded += (_, _) => _host.Widgets.CollectionChanged -= OnChanged;
        BuildList();
    }

    private void OnChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => BuildList();

    private void BuildList()
    {
        WidgetList.Children.Clear();
        if (_host.Widgets.Count == 0)
        {
            WidgetList.Children.Add(new TextBlock { Text = "No widgets yet — describe one in the panel.", Margin = new Thickness(1, 0, 0, 0) });
            return;
        }
        foreach (var widget in _host.Widgets)
        {
            var actions = widget.AllActions().Select(a => ActionCatalog.Find(a["type"]?.ToString())).OfType<ActionInfo>().ToList();
            var traits = new List<string>();
            if (actions.Any(a => a.Traits.HasFlag(ActionTraits.Script))) traits.Add("runs scripts");
            if (actions.Any(a => a.Traits.HasFlag(ActionTraits.Server))) traits.Add("uses your server");
            if (actions.Any(a => a.Traits.HasFlag(ActionTraits.Network))) traits.Add("uses the internet");
            var description = $"{Capitalize(widget.Size)} · {string.Join(", ", actions.Where(a => !a.IsQuery || a.Traits.HasFlag(ActionTraits.Script)).Select(a => a.Type).Distinct().Take(4))}";
            if (traits.Count > 0) description += " · " + string.Join(", ", traits);

            var remove = new Button { Content = "Remove" };
            var id = widget.Id;
            remove.Click += (_, _) => _host.RemoveWidget(id);
            var card = new SettingsCard
            {
                Header = widget.Title,
                Description = description,
                HeaderIcon = new FontIcon { Glyph = Icons.Glyph(widget.Icon), FontFamily = Controls.Glyph.Family },
                Content = remove,
            };
            WidgetList.Children.Add(card);
        }
    }

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker { SuggestedFileName = "tilesmith-widgets", SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeChoices.Add("JSON", [".json"]);
        InitializeWithWindow.Initialize(picker, _window.Handle);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await File.WriteAllTextAsync(file.Path, JsonSerializer.Serialize(_host.Widgets.ToList(), JsonDefaults.Options));
        Show($"Exported {_host.Widgets.Count} widgets.", InfoBarSeverity.Success);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, _window.Handle);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var imported = WidgetStore.Import(await File.ReadAllTextAsync(file.Path));
            foreach (var w in imported) _host.UpsertWidget(w);
            Show($"Imported {imported.Count} widget{(imported.Count == 1 ? "" : "s")}.", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Show("Couldn't import: " + ex.Message, InfoBarSeverity.Error);
        }
    }

    private void OpenTools_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AgentPrompt.ToolsDir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AgentPrompt.ToolsDir}\"") { UseShellExecute = true });
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Reset widgets?",
            Content = "All your widgets will be replaced with the starter set. Export them first if you want to keep them.",
            PrimaryButtonText = "Reset",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _host.Widgets.Clear();
        foreach (var w in WidgetStore.Defaults()) _host.Widgets.Add(w);
        _host.SaveWidgets();
        Show("Widgets reset.", InfoBarSeverity.Success);
    }

    private void Show(string message, InfoBarSeverity severity)
    {
        ResultBar.Message = message;
        ResultBar.Severity = severity;
        ResultBar.IsOpen = true;
    }
}
