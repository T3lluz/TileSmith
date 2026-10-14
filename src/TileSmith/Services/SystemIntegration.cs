using Microsoft.Win32;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using TileSmith.Core;

namespace TileSmith.Services;

/// <summary>
/// Start with Windows: the per-user Run key (no admin needed, shows in Task Manager › Startup apps), or — when
/// TileSmith runs as administrator — a logon task with highest privileges, since Windows won't start an elevated app
/// from the Run key without a prompt.
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string TaskName = "TileSmith (administrator)";

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

    public static void Apply(bool enabled, bool admin = false)
    {
        try
        {
            // the logon task can only be created or removed with administrator rights
            if (admin && enabled && Core.Native.Elevation.IsElevated)
            {
                if (CreateTask()) { Disable(); return; }
            }
            else if (Core.Native.Elevation.IsElevated)
            {
                DeleteTask();
            }
            if (enabled) Enable(); else Disable();
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't update startup entry: {ex.Message}");
        }
    }

    private static bool CreateTask()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        var user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        var result = RunSchtasks(["/Create", "/F", "/TN", TaskName, "/SC", "ONLOGON", "/RL", "HIGHEST", "/IT", "/RU", user, "/TR", $"\"{exe}\" --background"]);
        if (result != 0) Log.Warn($"Couldn't create the startup task (schtasks exit {result})");
        return result == 0;
    }

    private static void DeleteTask() => RunSchtasks(["/Delete", "/F", "/TN", TaskName]);

    private static int RunSchtasks(string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi);
        if (p is null) return -1;
        p.WaitForExit(10_000);
        return p.HasExited ? p.ExitCode : -1;
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
