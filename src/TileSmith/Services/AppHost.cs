using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using TileSmith.Core;
using TileSmith.Core.Actions;
using TileSmith.Core.Ai;
using TileSmith.Core.Remote;
using TileSmith.Core.Settings;
using TileSmith.Core.Widgets;
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
public sealed class AppHost : IActionHost
{
    public AppHost(DispatcherQueue dispatcher)
    {
        Dispatcher = dispatcher;
        Settings = SettingsStore.Load();
        KnownHosts = new KnownHostsStore(AppPaths.KnownHostsFile);
        Widgets = new ObservableCollection<WidgetDefinition>(WidgetStore.Load());
        Server = new ServerManager(this);
        Actions = new ActionEngine(this);
        Updates = new UpdateService(dispatcher, Settings, SaveSettings);
        Directory.CreateDirectory(AgentPrompt.ToolsDir);
    }

    public DispatcherQueue Dispatcher { get; }
    public AppSettings Settings { get; }
    public KnownHostsStore KnownHosts { get; }
    public ObservableCollection<WidgetDefinition> Widgets { get; }
    public ServerManager Server { get; }
    public ActionEngine Actions { get; }
    public UpdateService Updates { get; }

    /// <summary>Raised (on the UI thread) after settings were saved.</summary>
    public event Action? SettingsChanged;

    /// <summary>Set by the app: hides the panel before simulated keystrokes.</summary>
    public Func<Task>? PrepareForInput { get; set; }

    public void SaveSettings()
    {
        SettingsStore.Save(Settings);
        SettingsChanged?.Invoke();
    }

    public void SaveWidgets() => WidgetStore.Save(Widgets);

    public IAiBackend CreateBackend() => AiEngineFactory.Create(Settings, Server.GetConnection);

    public string EngineDisplayName()
    {
        var engine = AiEngineFactory.ResolveEngine(Settings);
        return engine switch
        {
            "claude" => "Claude",
            "cursor" => "Cursor",
            "openai" => Settings.Ai.OpenAiModel ?? "your model",
            "hermes" => "Hermes",
            _ => "AI",
        };
    }

    // ── Widget operations (used by the panel and by the agent) ─────────────────────────

    /// <summary>Adds the widget, or replaces the one with the same id. Returns true if it replaced.</summary>
    public bool UpsertWidget(WidgetDefinition widget)
    {
        var index = IndexOf(widget.Id);
        if (index >= 0) Widgets[index] = widget;
        else Widgets.Add(widget);
        SaveWidgets();
        return index >= 0;
    }

    public WidgetDefinition? RemoveWidget(string id)
    {
        var index = IndexOf(id);
        if (index < 0) return null;
        var removed = Widgets[index];
        Widgets.RemoveAt(index);
        SaveWidgets();
        return removed;
    }

    public void InsertWidget(int index, WidgetDefinition widget)
    {
        Widgets.Insert(Math.Clamp(index, 0, Widgets.Count), widget);
        SaveWidgets();
    }

    public bool MoveWidget(string id, int position)
    {
        var index = IndexOf(id);
        if (index < 0) return false;
        position = Math.Clamp(position, 0, Widgets.Count - 1);
        if (index != position) Widgets.Move(index, position);
        SaveWidgets();
        return true;
    }

    public bool ResizeWidget(string id, string size)
    {
        var index = IndexOf(id);
        if (index < 0) return false;
        var copy = Widgets[index].Clone();
        copy.Size = size;
        WidgetValidator.Validate(copy);
        Widgets[index] = copy;
        SaveWidgets();
        return true;
    }

    public int IndexOf(string id)
    {
        for (var i = 0; i < Widgets.Count; i++)
        {
            if (Widgets[i].Id == id) return i;
        }
        return -1;
    }

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
