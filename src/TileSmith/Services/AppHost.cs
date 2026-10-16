using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using TileSmith.Core;
using TileSmith.Core.Actions;
using TileSmith.Core.Ai;
using TileSmith.Core.Remote;
using TileSmith.Core.Native;
using TileSmith.Core.Settings;
using TileSmith.Core.Tiles;
using Windows.ApplicationModel.DataTransfer;

namespace TileSmith.Services;

public enum ConnectionState { NotConfigured, Idle, Connecting, Connected, Error }

/// <summary>Owns the SSH connection to the user's server and its status.</summary>
public sealed class ServerManager(AppHost host)
{
    private ServerConnection? _connection;
    private bool _detectedThisSession;

    public ConnectionState State { get; private set; } = host.Settings.Server.IsConfigured ? ConnectionState.Idle : ConnectionState.NotConfigured;
    public string StatusText { get; private set; } = host.Settings.Server.IsConfigured ? "Not connected yet" : "No server set up";
    public HostKeyChangedException? PendingHostKeyChange { get; private set; }
    public event Action? Changed;

    public string DisplayName =>
        SshTarget.TryParse(host.Settings.Server.Address, out var t, out _) ? t.Host : "server";

    public ServerConnection GetConnection()
    {
        if (!host.Settings.Server.IsConfigured) throw new ServerException("Set up your server in Settings › Server & AI first.");
        return _connection ??= new ServerConnection(host.Settings.Server, host.KnownHosts);
    }

    /// <summary>Drop the connection after settings changed.</summary>
    public void Reset()
    {
        _connection?.Dispose();
        _connection = null;
        PendingHostKeyChange = null;
        Set(host.Settings.Server.IsConfigured ? ConnectionState.Idle : ConnectionState.NotConfigured,
            host.Settings.Server.IsConfigured ? "Not connected yet" : "No server set up");
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        Set(ConnectionState.Connecting, $"Connecting to {DisplayName}…");
        try
        {
            var connection = GetConnection();
            await connection.ConnectAsync(ct);
            PendingHostKeyChange = null;
            Set(ConnectionState.Connected, $"Connected to {DisplayName}");
            if (!_detectedThisSession) _ = RefreshDetectionAsync(connection);
            return true;
        }
        catch (HostKeyChangedException ex)
        {
            PendingHostKeyChange = ex;
            Set(ConnectionState.Error, ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is ServerException or ArgumentException or InvalidOperationException)
        {
            Set(ConnectionState.Error, ex.Message);
            return false;
        }
        catch (OperationCanceledException)
        {
            Set(ConnectionState.Idle, "Cancelled");
            return false;
        }
    }

    /// <summary>
    /// Once per session, re-checks which AI engines the server has (installs move: a stale native Hermes,
    /// a new Docker container…) so the saved command keeps working without the user pressing Detect.
    /// </summary>
    private async Task RefreshDetectionAsync(ServerConnection connection)
    {
        _detectedThisSession = true;
        try
        {
            var found = await EngineDetector.DetectAsync(connection, CancellationToken.None);
            host.Dispatcher.TryEnqueue(() =>
            {
                EngineDetector.Apply(found, host.Settings.Ai);
                host.SaveSettings();
                Log.Info($"Engines on {DisplayName}: {found.Summary} (hermes: {found.HermesCommand ?? "none"})");
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Engine detection failed: {ex.Message}");
        }
    }

    public async Task<bool> TrustNewHostKeyAndConnectAsync()
    {
        if (PendingHostKeyChange is not { } change) return false;
        GetConnection().TrustHostKey(change.Actual);
        _connection?.Dispose();
        _connection = null;
        return await ConnectAsync();
    }

    public void MarkConnected() => Set(ConnectionState.Connected, $"Connected to {DisplayName}");

    public void MarkFailed(string message) => Set(ConnectionState.Error, message);

    private void Set(ConnectionState state, string text)
    {
        State = state;
        StatusText = text;
        host.Dispatcher.TryEnqueue(() => Changed?.Invoke());
    }
}

/// <summary>App-wide state and services. Lives for the whole process.</summary>
public sealed class AppHost : IActionHost, ITileHost
{
    public AppHost(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        Settings = SettingsStore.Load();
        KnownHosts = new KnownHostsStore(AppPaths.KnownHostsFile);
        Server = new ServerManager(this);
        Actions = new ActionEngine(this);
        Updates = new UpdateService(dispatcher, Settings, SaveSettings);
        Environment = DetectEnvironment();
        Library = new TileLibrary();
        Library.Load(Environment);
        Values = new TileResolver(this);
        Controls = new TileControls(this, Values);
        // slow values (weather, the web, AI lines) are kept on disk so the panel opens on them
        _saveValues = dispatcher.CreateTimer();
        _saveValues.Interval = TimeSpan.FromSeconds(30);
        _saveValues.Tick += (_, _) => Values.Save();
        _saveValues.Start();
        // charts of the last two minutes (CPU, memory, network) are ready when the panel opens
        _history = dispatcher.CreateTimer();
        _history.Interval = TimeSpan.FromSeconds(PcHistory.IntervalSeconds * 2);
        _history.Tick += (_, _) =>
        {
            if (Library.Panel.Any(d => d.Blocks.Any(b => TileSources.Canonical(b.Bind) is "pc.cpu_history" or "pc.memory_history" or "net.speed_history")))
                _ = Task.Run(PcHistory.Sample);
        };
        _history.Start();
    }

    private readonly DispatcherQueueTimer _saveValues;
    private readonly DispatcherQueueTimer _history;

    public DispatcherQueue Dispatcher { get; }
    public AppSettings Settings { get; }
    public KnownHostsStore KnownHosts { get; }
    public ServerManager Server { get; }
    public ActionEngine Actions { get; }
    public UpdateService Updates { get; }

    /// <summary>Every design and which are on the panel.</summary>
    public TileLibrary Library { get; }

    /// <summary>What widgets' sources read, shared by every tile.</summary>
    public TileResolver Values { get; }

    /// <summary>Switches, sliders, choices and taps.</summary>
    public TileControls Controls { get; }

    /// <summary>What this PC has (battery, brightness, Wi-Fi, displays), for the gallery and the first panel.</summary>
    public TileEnvironment Environment { get; private set; }

    /// <summary>Raised (on the UI thread) after settings were saved.</summary>
    public event Action? SettingsChanged;

    /// <summary>Set by the app: hides the panel before simulated keystrokes.</summary>
    public Func<Task>? PrepareForInput { get; set; }

    public void SaveSettings()
    {
        SettingsStore.Save(Settings);
        SettingsChanged?.Invoke();
    }

    public IAiBackend CreateBackend() => AiEngineFactory.Create(Settings, Server.GetConnection);

    /// <summary>"Claude Opus", "Hermes", "gpt-5.2"… — the AI in use, for messages.</summary>
    public string EngineDisplayName()
    {
        var provider = AiEngineFactory.ResolveProvider(Settings);
        return ProviderCatalog.ModelLabel(provider, Settings.Ai.ModelFor(provider.Id));
    }

    public TileEnvironment DetectEnvironment()
    {
        static bool Safe(Func<bool> check)
        {
            try { return check(); }
            catch { return false; }
        }
        var displays = 1;
        try { displays = DisplayManager.GetDisplays().Count; }
        catch (Exception ex) { Log.Warn($"Couldn't count displays: {ex.Message}"); }
        var wifi = Safe(() => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Any(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211));
        return Environment = new TileEnvironment(
            HasBattery: Safe(() => PowerControl.GetBattery().HasBattery),
            HasBrightness: Safe(BrightnessControl.IsSupported),
            HasWifi: wifi,
            Displays: displays,
            ServerConfigured: Settings.Server.IsConfigured);
    }

    // ── ITileHost ───────────────────────────────────────────────────────────────────

    public TileSettings Tiles => Settings.Tiles;

    public bool ServerConfigured => Settings.Server.IsConfigured;

    public string ServerName => Server.DisplayName;

    async Task<string> ITileHost.RunOnServerAsync(string script, TimeSpan timeout, CancellationToken ct)
    {
        var result = await Server.GetConnection().RunScriptAsync(script, timeout, ct);
        Server.MarkConnected();
        if (!result.Success && string.IsNullOrWhiteSpace(result.Stdout))
            throw new ActionException($"The server said: {result.Stderr.Trim()}");
        return result.Stdout;
    }

    public bool AiReady => AiEngineFactory.Chain(Settings).Any(id => ProviderCatalog.Status(ProviderCatalog.Get(id), Settings).IsReady);

    public IAiBackend CreateAi() => CreateBackend();

    // ── IActionHost ─────────────────────────────────────────────────────────────────

    public async Task<string> RunOnServerAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        var result = await Server.GetConnection().RunAsync(command, timeout, ct);
        Server.MarkConnected();
        if (!result.Success && string.IsNullOrWhiteSpace(result.Stdout))
            throw new ActionException($"Server command failed (exit {result.ExitCode}): {result.Stderr.Trim()}");
        return result.Stdout;
    }

    public void Notify(string title, string? message) => Notifications.Show(title, message);

    public void SetClipboardText(string text) => RunOnUi(() =>
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    });

    public async Task PrepareForInputAsync()
    {
        if (PrepareForInput is null) return;
        var tcs = new TaskCompletionSource();
        Dispatcher.TryEnqueue(async () =>
        {
            try { await PrepareForInput(); }
            finally { tcs.TrySetResult(); }
        });
        await tcs.Task;
    }

    private void RunOnUi(Action action)
    {
        if (Dispatcher.HasThreadAccess)
        {
            action();
            return;
        }
        var tcs = new TaskCompletionSource();
        Dispatcher.TryEnqueue(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        tcs.Task.GetAwaiter().GetResult();
    }
}
