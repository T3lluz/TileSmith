using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Controls;
using TileSmith.Services;
using TileSmith.Theme;

namespace TileSmith.Views.Settings;

public sealed class UpdatesPage : SettingsPage
{
    private readonly Border _heroIconHost;
    private readonly TextBlock _heroIcon;
    private readonly WorkingScanner _heroScanner;
    private readonly TextBlock _heroTitle;
    private readonly TextBlock _heroSubtitle;
    private readonly ProgressBar _heroProgress;
    private readonly Chip _heroButton;
    private readonly StackPanel _notesHost = new();

    public UpdatesPage(AppHost host, SettingsWindow window)
        : base(host, window, "Updates", "TileSmith updates itself from GitHub Releases.")
    {
        var p = Palette.Current;
        var hero = new Grid { Padding = new Thickness(20), ColumnSpacing = 18 };
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hero.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _heroIcon = Glyph.Make("verified", 30, p.Accent);
        _heroScanner = new WorkingScanner(5, 2) { Color = p.Accent, Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center };
        var iconGrid = new Grid();
        iconGrid.Children.Add(_heroIcon);
        iconGrid.Children.Add(_heroScanner);
        _heroIconHost = new Border { Width = 60, Height = 60, CornerRadius = new CornerRadius(30), Background = p.Brush(p.AccentSoft), Child = iconGrid };
        hero.Children.Add(_heroIconHost);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 3 };
        _heroTitle = Ui.Text("", 18, p.Text1, semibold: true);
        _heroSubtitle = Kit.Body("", 13, p.Text2);
        _heroProgress = new ProgressBar { Maximum = 100, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0), MaxWidth = 360, HorizontalAlignment = HorizontalAlignment.Left, Width = 360, Foreground = p.Brush(p.Accent) };
        texts.Children.Add(_heroTitle);
        texts.Children.Add(_heroSubtitle);
        texts.Children.Add(_heroProgress);
        Grid.SetColumn(texts, 1);
        hero.Children.Add(texts);
        _heroButton = new Chip("Check for updates", "refresh", filled: true, height: 38) { VerticalAlignment = VerticalAlignment.Center };
        _heroButton.Click += (_, _) => _ = HeroAsync();
        Grid.SetColumn(_heroButton, 2);
        hero.Children.Add(_heroButton);
        Children.Add(new Border { Height = 18 });
        Children.Add(Kit.Card(hero));
        Children.Add(_notesHost);

        Section("Options", null,
            Kit.Row("autorenew", "Check automatically", "Every few hours.", Kit.Toggle(S.AutoCheckUpdates, on => { S.AutoCheckUpdates = on; Save(); })),
            Kit.Row("cloud_download", "Download in the background", "Then one click restarts into the new version.", Kit.Toggle(S.AutoDownloadUpdates, on => { S.AutoDownloadUpdates = on; Save(); })),
            Kit.Row("history", "Release history", "Every version and its notes on GitHub.", Kit.Link("Open", AppInfo.RepoUrl + "/releases")));

        Host.Updates.Changed += Refresh;
        Refresh();
    }

    public override void OnHidden() => Host.Updates.Changed -= Refresh;

    private void Refresh()
    {
        var p = Palette.Current;
        var u = Host.Updates;
        var busy = u.Stage is UpdateStage.Checking or UpdateStage.Downloading;
        _heroScanner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _heroIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        _heroProgress.Visibility = u.Stage == UpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed;
        _heroProgress.Value = u.Progress;
        _heroButton.IsEnabledForInput = !busy && u.Stage != UpdateStage.NotInstalled;
        _heroButton.Opacity = _heroButton.IsEnabledForInput ? 1 : 0.5;
        var lastChecked = u.LastChecked is { } t ? $"Last checked {t:t}." : "";

        (string icon, string title, string subtitle, string button) = u.Stage switch
        {
            UpdateStage.Checking => ("refresh", "Checking for updates…", $"Version {u.CurrentVersion}", "Checking…"),
            UpdateStage.Available => ("new_releases", $"TileSmith {u.NewVersion} is available", $"You have {u.CurrentVersion}.", "Download and install"),
            UpdateStage.Downloading => ("downloading", $"Downloading {u.NewVersion}… {u.Progress}%", "You can keep using TileSmith.", "Downloading…"),
            UpdateStage.Ready => ("restart_alt", $"TileSmith {u.NewVersion} is ready", "Restart TileSmith to finish — it takes a couple of seconds.", "Restart now"),
            UpdateStage.Error => ("error", "Something went wrong", u.Error ?? "Try again in a moment.", "Try again"),
            UpdateStage.NotInstalled => ("construction", "Development build", $"Version {u.CurrentVersion}. Install TileSmith from GitHub Releases to get automatic updates.", "Not available"),
            _ => ("verified", "You're up to date", $"Version {u.CurrentVersion}. {lastChecked}".Trim(), "Check for updates"),
        };
        _heroIcon.Text = Core.Widgets.Icons.Glyph(icon);
        _heroIcon.Foreground = p.Brush(u.Stage == UpdateStage.Error ? p.Warning : p.Accent);
        _heroTitle.Text = title;
        _heroSubtitle.Text = subtitle;
        _heroButton.Text = button;

        _notesHost.Children.Clear();
        if (u.HasUpdate && !string.IsNullOrWhiteSpace(u.ReleaseNotes))
        {
            _notesHost.Children.Add(Kit.Section($"What's new in {u.NewVersion}"));
            var notes = Kit.Body(PlainText(u.ReleaseNotes!), 13, p.Text1, maxLines: 400);
            notes.IsTextSelectionEnabled = true;
            notes.IsHitTestVisible = true;
            _notesHost.Children.Add(Kit.Card(Kit.Padded(notes, 16)));
        }
    }

    /// <summary>Very small Markdown → readable text conversion for release notes.</summary>
    private static string PlainText(string markdown)
    {
        var text = Regex.Replace(markdown, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*[-*]\s+", "•  ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        text = Regex.Replace(text, @"`([^`]+)`", "$1");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
        return text.Trim();
    }

    private async Task HeroAsync()
    {
        var u = Host.Updates;
        switch (u.Stage)
        {
            case UpdateStage.Available:
                await u.DownloadAsync();
                break;
            case UpdateStage.Ready:
                u.RestartToApply(App.Current.PrepareForExit);
                break;
            case UpdateStage.NotInstalled or UpdateStage.Checking or UpdateStage.Downloading:
                break;
            default:
                await u.CheckAsync(userInitiated: true);
                break;
        }
    }
}
