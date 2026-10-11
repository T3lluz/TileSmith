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
        if (After("--prompt") is { } prompt) return "prompt:" + prompt;
        if (args.Contains("--show")) return "show";
        return null;
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

        if (!SingleInstance.TryAcquire(waitForPrevious: args.Contains("--restarted") || args.Contains("--updated")))
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

    public static bool TryAcquire(bool waitForPrevious)
    {
        _mutex = new Mutex(true, $@"Local\{Name}-mutex", out var created);
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
                var command = "show";
                try
                {
                    if (File.Exists(CommandFile))
                    {
                        command = File.ReadAllText(CommandFile).Trim();
                        File.Delete(CommandFile);
                    }
                }
                catch { }
                onCommand(command);
            }
        })
        { IsBackground = true, Name = "SingleInstanceListener" };
        thread.Start();
    }
}
