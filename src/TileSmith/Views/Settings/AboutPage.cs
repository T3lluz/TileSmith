using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using TileSmith.Controls;
using TileSmith.Core;
using TileSmith.Services;
using TileSmith.Theme;

namespace TileSmith.Views.Settings;

public sealed class AboutPage : SettingsPage
{
    public AboutPage(AppHost host, SettingsWindow window)
        : base(host, window, "About", "AI-built quick settings for Windows.")
    {
        var p = Palette.Current;
        var hero = new Grid { Padding = new Thickness(20), ColumnSpacing = 18 };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.Children.Add(new Image { Width = 64, Height = 64, Source = new BitmapImage(new Uri(AppInfo.AssetPath("AppIcon.png"))) });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        texts.Children.Add(Kit.Title("TileSmith", 22));
        var version = Ui.Text($"Version {host.Updates.CurrentVersion}", 13, p.Text2);
        version.IsTextSelectionEnabled = true;
        version.IsHitTestVisible = true;
        texts.Children.Add(version);
        texts.Children.Add(Kit.Body("Quick settings for Windows that your AI builds for you.", 13, p.Text2));
        Grid.SetColumn(texts, 1);
        hero.Children.Add(texts);
        Children.Add(new Border { Height = 18 });
        Children.Add(Kit.Card(hero));

        Section("Links", null,
            Kit.Row("code", "Source code and releases", "github.com/T3lluz/TileSmith", Kit.Link("Open", AppInfo.RepoUrl)),
            Kit.Row("bug_report", "Report a problem or suggest an idea", null, Kit.Link("Open", AppInfo.RepoUrl + "/issues")),
            Kit.Row("partly_cloudy_day", "Weather data by Open-Meteo.com", "Used by weather widgets (CC BY 4.0). Model details from models.dev; logos from LobeHub Icons.",
                Kit.Link("Open", "https://open-meteo.com")),
            Kit.Row("description", "Logs", "Useful when something doesn't work.", Kit.Button("Open folder", "folder_open", ChipStyle.Outline, () =>
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                Open(AppPaths.LogsDir);
            })),
            Kit.Row("folder", "Settings and widgets folder", AppPaths.DataDir, Kit.Button("Open folder", "folder_open", ChipStyle.Outline, () => Open(AppPaths.DataDir))));

        Children.Add(Kit.Section("Built with"));
        Children.Add(Kit.Card(Kit.Padded(Kit.Body(
            "Windows App SDK and WinUI 3 · Velopack · SSH.NET · the Anthropic SDK · Material Symbols. Works with Claude Code, Cursor, Hermes Agent, " +
            "the Anthropic, OpenAI, Gemini, OpenRouter, Groq, Mistral, xAI and DeepSeek APIs, Ollama, LM Studio and any OpenAI-compatible endpoint.", 13, p.Text2, maxLines: 8), 16)));
    }

    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
