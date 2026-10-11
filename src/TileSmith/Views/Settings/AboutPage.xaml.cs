using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Core;
using TileSmith.Services;

namespace TileSmith.Views.Settings;

public sealed partial class AboutPage : UserControl
{
    public AboutPage(AppHost host)
    {
        InitializeComponent();
        AppBitmap.UriSource = new Uri(AppInfo.AssetPath("AppIcon64.png"));
        VersionText.Text = $"Version {host.Updates.CurrentVersion}";
    }

    private static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    private void Repo_Click(object sender, RoutedEventArgs e) => Open(AppInfo.RepoUrl);
    private void Issues_Click(object sender, RoutedEventArgs e) => Open(AppInfo.RepoUrl + "/issues");

    private void Logs_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.LogsDir);
        Open(AppPaths.LogsDir);
    }

    private void Data_Click(object sender, RoutedEventArgs e) => Open(AppPaths.DataDir);
}
