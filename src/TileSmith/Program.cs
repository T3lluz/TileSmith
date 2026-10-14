using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TileSmith.Core;
using TileSmith.Services;
using Velopack;

namespace TileSmith;

public static class Program
{
    public const string AppUserModelId = "T3lluz.TileSmith";

    public static bool IsFirstRun { get; private set; }
    public static bool JustUpdated { get; private set; }
    public static string[] Arguments { get; private set; } = [];

    /// <summary>
    /// Deep links for shortcuts and scripts: <c>--settings [page]</c>, <c>--gallery</c>, <c>--prompt "text"</c>, <c>--show</c>.
    /// </summary>
    public static string? CommandFromArguments(string[] args)
    {
        string? After(string flag)
        {
            var i = Array.IndexOf(args, flag);
            return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
        }
        if (args.Contains("--settings")) return "settings:" + (After("--settings") ?? "");
        if (args.Contains("--gallery")) return "gallery";
        if (args.Contains("--studio")) return "studio:" + (After("--studio") ?? "");
        if (After("--prompt") is { } prompt) return "prompt:" + prompt;
        if (args.Contains("--show")) return "show";
        return null;
    }

    /// <summary>Starts this exe again with administrator rights. False when the prompt was declined.</summary>
    public static bool RelaunchElevated(IEnumerable<string> args)
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return false;
        var arguments = string.Join(' ', args.Where(a => a is not "--elevated").Append("--elevated").Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, arguments) { UseShellExecute = true, Verb = "runas" })?.Dispose();
            return true;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Warn($"Starting as administrator was declined or failed: {ex.Message}");
            return false;
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Arguments = args;

        // Must run first: Velopack handles install/update/uninstall hooks and may exit the process.
        VelopackApp.Build()
            .SetAppUserModelId(AppUserModelId)
            .OnFirstRun(_ => IsFirstRun = true)
            .OnRestarted(_ => JustUpdated = true)
            .OnBeforeUninstallFastCallback(_ =>
            {
                StartupManager.Disable();
            })
            .Run();

        Log.Init(AppPaths.LogsDir);
        Log.Info($"TileSmith {AppInfo.Version} starting (args: {string.Join(' ', args)})");

        // Run as administrator: start again elevated (one UAC prompt). If the prompt is declined, carry on normally.
        if (!args.Contains("--no-elevate") && !Core.Native.Elevation.IsElevated && Core.Native.Elevation.CanElevate && Core.Settings.SettingsStore.Load().RunAsAdmin)
        {
            // already running (elevated): just hand it the command, no prompt
            if (SingleInstance.IsRunning())
            {
                SingleInstance.SignalExisting(CommandFromArguments(args) ?? "show");
                return 0;
            }
            if (RelaunchElevated(args)) return 0;
        }

        if (!SingleInstance.TryAcquire(waitForPrevious: args.Contains("--restarted") || args.Contains("--updated") || args.Contains("--elevated")))
        {
            SingleInstance.SignalExisting(CommandFromArguments(args) ?? "show");
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ignored =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                _ = new App();
            }
            catch (Exception ex)
            {
                Log.Error("Startup failed", ex);
                throw;
            }
        });
        return 0;
    }
}

/// <summary>One TileSmith per user. A second launch asks the running one to open its panel.</summary>
internal static class SingleInstance
{
    private static readonly string Name = $"TileSmith-{Environment.UserName}" +
        (AppPaths.IsCustomDataDir ? "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppPaths.DataDir.ToLowerInvariant())))[..8] : "");
    private static Mutex? _mutex;

    /// <summary>Another TileSmith of this user is running (also when it runs elevated and its mutex can't be opened).</summary>
    public static bool IsRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting($@"Local\{Name}-mutex", out var existing)) return false;
            existing.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static bool TryAcquire(bool waitForPrevious)
    {
        bool created;
        try
        {
            _mutex = new Mutex(true, $@"Local\{Name}-mutex", out created);
        }
        catch (UnauthorizedAccessException)
        {
            // the running instance is elevated: it owns the mutex and we may not even open it
            return false;
        }
        if (created) return true;
        if (!waitForPrevious) return false;
        try
        {
            // a restart: the previous process is on its way out
            return _mutex.WaitOne(TimeSpan.FromSeconds(8));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    public static void SignalExisting(string command)
    {
        try
        {
            // the file alone is enough: the running instance watches it (an elevated one can't be signalled directly)
            File.WriteAllText(CommandFile, command);
            using var evt = EventWaitHandle.OpenExisting($@"Local\{Name}-show");
            evt.Set();
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't signal the running instance: {ex.Message}");
        }
    }

    private static string CommandFile => Path.Combine(AppPaths.DataDir, ".command");

    /// <summary>Listens for "open the panel" requests from later launches.</summary>
    public static void Listen(Action<string> onCommand)
    {
        var evt = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{Name}-show");
        var thread = new Thread(() =>
        {
            while (evt.WaitOne())
            {
                // the file watcher below may have picked the command up already
                string? command = null;
                try
                {
                    if (File.Exists(CommandFile))
                    {
                        command = File.ReadAllText(CommandFile).Trim();
                        File.Delete(CommandFile);
                    }
                }
                catch { }
                if (!string.IsNullOrEmpty(command)) onCommand(command);
            }
        })
        { IsBackground = true, Name = "SingleInstanceListener" };
        thread.Start();

        // a second launch that can't open our event (we run elevated, it doesn't) still drops its command file
        Directory.CreateDirectory(AppPaths.DataDir);
        _watcher = new FileSystemWatcher(AppPaths.DataDir, ".command") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName };
        void OnFile(object? sender, FileSystemEventArgs e)
        {
            Thread.Sleep(50); // let the writer finish
            string? command = null;
            try
            {
                if (!File.Exists(CommandFile)) return;
                command = File.ReadAllText(CommandFile).Trim();
                File.Delete(CommandFile);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            if (!string.IsNullOrEmpty(command)) onCommand(command);
        }
        _watcher.Created += OnFile;
        _watcher.Changed += OnFile;
        _watcher.EnableRaisingEvents = true;
    }

    private static FileSystemWatcher? _watcher;
}
