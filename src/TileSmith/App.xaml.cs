using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using TileSmith.Core;
using TileSmith.Interop;
using TileSmith.Services;
using TileSmith.Views;
using Windows.Graphics;

namespace TileSmith;

public partial class App : Application
{
    private TrayIcon? _tray;
    private PanelWindow? _panel;
    private SettingsWindow? _settings;

    public App()
    {
        // Theme and accent have to be chosen before any XAML loads.
        var settings = Core.Settings.SettingsStore.Load();
        if (settings.Theme == "light") RequestedTheme = ApplicationTheme.Light;
        else if (settings.Theme == "dark") RequestedTheme = ApplicationTheme.Dark;
        _dark = settings.Theme switch { "light" => false, "dark" => true, _ => Core.Native.ThemeControl.IsDark() };
        _accent = settings.Accent;

        InitializeComponent();
        DebugSettings.XamlResourceReferenceFailed += (_, e) => Log.Warn("XAML resource: " + e.Message);
        DebugSettings.BindingFailed += (_, e) => Log.Warn("XAML binding: " + e.Message);
        UnhandledException += (_, e) =>
        {
            Log.Error("Unhandled UI exception", e.Exception);
            e.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    private readonly bool _dark;
    private readonly string _accent;

    public static new App Current => (App)Application.Current;

    public AppHost Host { get; private set; } = null!;

    /// <summary>False when another app already owns the configured shortcut.</summary>
    public bool HotkeyOk { get; private set; } = true;

    public static readonly string[] HotkeyFallbacks = ["Win+Shift+Q", "Win+Alt+Q", "Ctrl+Alt+T", "Alt+Shift+T", "Ctrl+Shift+Space"];

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        Theme.DesignTokens.ApplyToApp(this, _dark, _accent); // resources are only reachable once launched
        Theme.Motion.Reduced = Core.Settings.SettingsStore.Load().ReduceMotion;
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        Host = new AppHost(dispatcher);
        Log.Info("Host ready");

        _panel = new PanelWindow(Host);
        Log.Info("Panel created");
        _tray = new TrayIcon { Tooltip = "TileSmith" };
        _tray.LeftClick += (x, y) => _panel.Toggle(new PointInt32(x, y));
        _tray.HotkeyPressed += () =>
        {
            if (_panel.IsOpen) _panel.Hide();
            else _panel.Show(focusPrompt: true);
        };
        _tray.DisplayChanged += () => _panel.OnDisplayChanged();
        _tray.MenuRequested += BuildTrayMenu;
        ApplyHotkey();

        Notifications.Invoked += action => dispatcher.TryEnqueue(() => HandleCommand(action));
        Notifications.Init();
        SingleInstance.Listen(command => dispatcher.TryEnqueue(() => HandleCommand(command)));

        Host.Updates.Changed += UpdateTrayState;
        Host.Updates.Start();
        Host.SettingsChanged += ApplyHotkey;

        // only an installed copy registers itself (a dev build would point Windows at a temporary path)
        if (Host.Updates.IsInstalled) StartupManager.Apply(Host.Settings.LaunchAtStartup);
        if (Host.Settings.KeepIconVisible)
        {
            // Explorer registers the icon's settings key a moment after it first appears.
            var timer = dispatcher.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(4);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => TrayIconVisibility.SetPromoted(true);
            timer.Start();
        }

        var firstRun = Program.IsFirstRun || !Host.Settings.FirstRunCompleted;
        if (firstRun)
        {
            Host.Settings.FirstRunCompleted = true;
            Host.SaveSettings();
            _panel.Show();
            Notifications.Show("TileSmith is ready", "Click the TileSmith icon next to the clock anytime — or press " + Host.Settings.Hotkey + ".");
        }
        else if (Program.Arguments.Contains("--updated") || Program.JustUpdated)
        {
            Notifications.Show($"TileSmith updated to {AppInfo.Version}", "Thanks for keeping it fresh.");
        }

        if (Program.CommandFromArguments(Program.Arguments) is { } command) HandleCommand(command);

        // a server set up before 0.2 may never have been checked properly: look for Hermes & co. in the background
        if (Host.Settings.Server.IsConfigured && Host.Settings.Ai.DetectedEngine is null)
        {
            var detect = dispatcher.CreateTimer();
            detect.Interval = TimeSpan.FromSeconds(6);
            detect.IsRepeating = false;
            detect.Tick += (_, _) => _ = Host.Server.ConnectAsync();
            detect.Start();
        }
        if (Program.Arguments.Contains("--pin")) _panel.Pin();
        var captureAt = Array.IndexOf(Program.Arguments, "--capture");
        if (captureAt >= 0 && captureAt + 1 < Program.Arguments.Length) StartCapture(Program.Arguments[captureAt + 1]);
        Log.Info("Ready");
    }

    private void StartCapture(string folder)
    {
        var shots = new List<DevCapture.Shot>();
        foreach (var state in PanelWindow.CaptureStates)
        {
            shots.Add(new($"panel-{state}", async () =>
            {
                _panel!.PrepareCapture(state);
                await Task.Delay(150);
                return _panel.CaptureRoot;
            }));
        }
        foreach (var page in SettingsWindow.PageTags)
        {
            shots.Add(new($"settings-{page}", () =>
            {
                OpenSettings(page);
                return Task.FromResult<UIElement?>(_settings?.CaptureRoot);
            }));
        }
        _ = DevCapture.RunAsync(folder, shots, Quit);
    }

    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        var updates = Host.Updates;
        var updateItem = updates.Stage switch
        {
            UpdateStage.Ready => new TrayMenuItem($"Restart to update to {updates.NewVersion}", () => updates.RestartToApply(PrepareForExit)),
            UpdateStage.Available => new TrayMenuItem($"Update to {updates.NewVersion}", () => { _panel?.Show(); _ = updates.DownloadAsync(); }),
            UpdateStage.Downloading => new TrayMenuItem($"Downloading update… {updates.Progress}%", null, Enabled: false),
            UpdateStage.NotInstalled => new TrayMenuItem("Updates: development build", null, Enabled: false),
            _ => new TrayMenuItem("Check for updates", () => _ = updates.CheckAsync(userInitiated: true)),
        };
        return
        [
            new TrayMenuItem("Open TileSmith", () => _panel?.Show(), IsDefault: true),
            new TrayMenuItem("Add a widget", () => { _panel?.Show(); _panel?.OpenGallery(); }),
            new TrayMenuItem("Settings", () => OpenSettings()),
            TrayMenuItem.Separator,
            updateItem,
            TrayMenuItem.Separator,
            new TrayMenuItem("Quit TileSmith", Quit),
        ];
    }

    private void HandleCommand(string command)
    {
        var (verb, argument) = command.IndexOf(':') is var i and > 0 ? (command[..i], command[(i + 1)..]) : (command, "");
        switch (verb)
        {
            case "settings":
                OpenSettings(argument.Length > 0 ? argument : null);
                break;
            case "update":
                _panel?.Show();
                if (Host.Updates.Stage == UpdateStage.Ready) Host.Updates.RestartToApply(PrepareForExit);
                else _ = Host.Updates.DownloadAsync();
                break;
            case "gallery":
                _panel?.Show();
                _panel?.OpenGallery(focusSearch: true);
                break;
            case "prompt":
                _panel?.Show(focusPrompt: true);
                if (argument.Length > 0) _panel?.SetPrompt(argument);
                break;
            default:
                _panel?.Show();
                break;
        }
    }

    private void UpdateTrayState()
    {
        if (_tray is null) return;
        _tray.ShowBadge = Host.Updates.HasUpdate;
        _tray.Tooltip = Host.Updates.HasUpdate ? $"TileSmith — update {Host.Updates.NewVersion} available" : "TileSmith";
    }

    private void ApplyHotkey()
    {
        if (_tray is null) return;
        if (Host.Settings.HotkeyEnabled)
        {
            HotkeyOk = _tray.RegisterHotkey(Host.Settings.Hotkey);
            if (!HotkeyOk && !Host.Settings.HotkeyChosenByUser)
            {
                // the default is taken on this PC: quietly fall back to a free one
                foreach (var candidate in HotkeyFallbacks.Where(k => k != Host.Settings.Hotkey))
                {
                    if (!_tray.RegisterHotkey(candidate)) continue;
                    Log.Info($"Shortcut {Host.Settings.Hotkey} is taken; using {candidate}");
                    Host.Settings.Hotkey = candidate;
                    Core.Settings.SettingsStore.Save(Host.Settings);
                    HotkeyOk = true;
                    break;
                }
            }
        }
        else
        {
            _tray.UnregisterHotkey();
            HotkeyOk = true;
        }
    }

    public void OpenSettings(string? page = null)
    {
        _settings ??= new SettingsWindow(Host);
        _settings.Closed += (_, _) => _settings = null;
        _settings.ShowPage(page);
    }

    public void ShowPanel() => _panel?.Show();

    /// <summary>Removes the tray icon before the process exits (otherwise a ghost icon lingers).</summary>
    public void PrepareForExit()
    {
        try
        {
            _tray?.Dispose();
            _tray = null;
            Notifications.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Warn($"Cleanup failed: {ex.Message}");
        }
    }

    public void Quit()
    {
        Log.Info("Quitting");
        PrepareForExit();
        Exit();
    }

    /// <summary>Relaunches TileSmith (used after changing the theme).</summary>
    public void Restart()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        PrepareForExit();
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--show --restarted") { UseShellExecute = false });
        Environment.Exit(0);
    }
}
