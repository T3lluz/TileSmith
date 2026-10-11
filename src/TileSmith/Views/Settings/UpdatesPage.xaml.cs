using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Services;

namespace TileSmith.Views.Settings;

public sealed partial class UpdatesPage : UserControl
{
    private readonly AppHost _host;
    private bool _loading = true;

    public UpdatesPage(AppHost host)
    {
        _host = host;
        InitializeComponent();
        AutoCheckToggle.IsOn = host.Settings.AutoCheckUpdates;
        AutoDownloadToggle.IsOn = host.Settings.AutoDownloadUpdates;
        _loading = false;
        host.Updates.Changed += Refresh;
        Unloaded += (_, _) => host.Updates.Changed -= Refresh;
        Refresh();
    }

    private void Refresh()
    {
        var u = _host.Updates;
        var busy = u.Stage is UpdateStage.Checking or UpdateStage.Downloading;
        HeroRing.IsActive = busy;
        HeroRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        HeroIcon.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        HeroProgress.Visibility = u.Stage == UpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed;
        HeroProgress.Value = u.Progress;
        HeroButton.IsEnabled = !busy && u.Stage != UpdateStage.NotInstalled;
        var lastChecked = u.LastChecked is { } t ? $"Last checked: {t:t}" : "";

        (HeroIcon.Glyph, HeroTitle.Text, HeroSubtitle.Text, HeroButton.Content) = u.Stage switch
        {
            UpdateStage.Checking => ("", "Checking for updates…", $"Version {u.CurrentVersion}", "Checking…"),
            UpdateStage.Available => ("", $"TileSmith {u.NewVersion} is available", $"You have {u.CurrentVersion}.", "Download and install"),
            UpdateStage.Downloading => ("", $"Downloading {u.NewVersion}… {u.Progress}%", "You can keep using TileSmith.", "Downloading…"),
            UpdateStage.Ready => ("", $"TileSmith {u.NewVersion} is ready", "Restart TileSmith to finish — it takes a couple of seconds.", "Restart now"),
            UpdateStage.Error => ("", "Something went wrong", u.Error ?? "Try again in a moment.", "Try again"),
            UpdateStage.NotInstalled => ("", "Development build", $"Version {u.CurrentVersion}. Install TileSmith from GitHub Releases to get automatic updates.", "Not available"),
            _ => ("", "You're up to date", $"Version {u.CurrentVersion}. {lastChecked}".Trim(), "Check for updates"),
        };

        var notes = u.HasUpdate ? u.ReleaseNotes : null;
        NotesExpander.Visibility = string.IsNullOrWhiteSpace(notes) ? Visibility.Collapsed : Visibility.Visible;
        NotesExpander.Header = $"What's new in {u.NewVersion}";
        NotesText.Text = PlainText(notes ?? "");
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

    private async void HeroButton_Click(object sender, RoutedEventArgs e)
    {
        var u = _host.Updates;
        switch (u.Stage)
        {
            case UpdateStage.Available:
                await u.DownloadAsync();
                break;
            case UpdateStage.Ready:
                u.RestartToApply(App.Current.PrepareForExit);
                break;
            default:
                await u.CheckAsync(userInitiated: true);
                break;
        }
    }

    private void AutoCheckToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.Settings.AutoCheckUpdates = AutoCheckToggle.IsOn;
        _host.SaveSettings();
    }

    private void AutoDownloadToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _host.Settings.AutoDownloadUpdates = AutoDownloadToggle.IsOn;
        _host.SaveSettings();
    }

    private void Releases_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(AppInfo.RepoUrl + "/releases") { UseShellExecute = true });
}
