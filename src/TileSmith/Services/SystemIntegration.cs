using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TileSmith.Core;

namespace TileSmith.Services;

/// <summary>Start with Windows via the per-user Run key (no admin needed, shows in Task Manager › Startup apps).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppInfo.Name) is string;
    }

    public static void Enable()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(AppInfo.Name, $"\"{exe}\" --background");
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(AppInfo.Name, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't remove startup entry: {ex.Message}");
        }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            if (enabled) Enable(); else Disable();
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't update startup entry: {ex.Message}");
        }
    }
}

/// <summary>
/// Windows 11 hides new tray icons in the overflow flyout. This keeps TileSmith's icon visible next to the clock
/// by setting the same per-user flag as Settings › Personalization › Taskbar › Other system tray icons.
/// </summary>
public static class TrayIconVisibility
{
    private const string Root = @"Control Panel\NotifyIconSettings";

    public static bool SetPromoted(bool promoted)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return false;
            using var root = Registry.CurrentUser.OpenSubKey(Root);
            if (root is null) return false;
            foreach (var name in root.GetSubKeyNames())
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{Root}\{name}", writable: true);
                if (key?.GetValue("ExecutablePath") is string path && string.Equals(path, exe, StringComparison.OrdinalIgnoreCase))
                {
                    key.SetValue("IsPromoted", promoted ? 1 : 0, RegistryValueKind.DWord);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't change tray icon visibility: {ex.Message}");
        }
        return false;
    }
}

/// <summary>Windows toast notifications through the Windows App SDK.</summary>
public static class Notifications
{
    private static bool _ready;
    public static event Action<string>? Invoked;

    public static void Init()
    {
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, e) =>
            {
                e.Arguments.TryGetValue("action", out var action);
                Invoked?.Invoke(action ?? "open");
            };
            AppNotificationManager.Default.Register();
            _ready = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Notifications unavailable: {ex.Message}");
        }
    }

    public static void Show(string title, string? message, string action = "open", string? buttonText = null, string? buttonAction = null)
    {
        if (!_ready) return;
        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument("action", action)
                .AddText(title);
            if (!string.IsNullOrWhiteSpace(message)) builder.AddText(message);
            if (buttonText is not null)
                builder.AddButton(new AppNotificationButton(buttonText).AddArgument("action", buttonAction ?? action));
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception ex)
        {
            Log.Warn($"Showing a notification failed: {ex.Message}");
        }
    }

    public static void Shutdown()
    {
        if (!_ready) return;
        try { AppNotificationManager.Default.Unregister(); } catch { }
    }
}
