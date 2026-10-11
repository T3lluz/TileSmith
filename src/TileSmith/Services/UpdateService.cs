using Microsoft.UI.Dispatching;
using TileSmith.Core;
using TileSmith.Core.Settings;
using Velopack;
using Velopack.Sources;

namespace TileSmith.Services;

public enum UpdateStage { Idle, Checking, UpToDate, Available, Downloading, Ready, Error, NotInstalled }

/// <summary>
/// Velopack updates from GitHub Releases: check → download (delta when possible) → restart.
/// Raises <see cref="Changed"/> on the UI thread so views can bind to it directly.
/// </summary>
public sealed class UpdateService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(3);
    private readonly DispatcherQueue _dispatcher;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly UpdateManager _manager;
    private UpdateInfo? _info;
    private DispatcherQueueTimer? _timer;
    private bool _busy;

    public UpdateService(DispatcherQueue dispatcher, AppSettings settings, Action saveSettings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _saveSettings = saveSettings;
        _manager = new UpdateManager(new GithubSource(AppInfo.RepoUrl, accessToken: null, prerelease: false));
        Stage = _manager.IsInstalled ? UpdateStage.Idle : UpdateStage.NotInstalled;
    }

    public event Action? Changed;

    public UpdateStage Stage { get; private set; }
    public string? NewVersion { get; private set; }
    public string? ReleaseNotes { get; private set; }
    public int Progress { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastChecked { get; private set; }
    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.IsInstalled ? _manager.CurrentVersion?.ToString() ?? AppInfo.Version : AppInfo.Version;
    public bool HasUpdate => Stage is UpdateStage.Available or UpdateStage.Downloading or UpdateStage.Ready;

    public void Start()
    {
        if (!_manager.IsInstalled)
        {
            Log.Info("Not running from an installation — updates are disabled for this build.");
            return;
        }
        if (_manager.UpdatePendingRestart is { } pending)
        {
            NewVersion = pending.Version.ToString();
            ReleaseNotes = pending.NotesMarkdown;
            Set(UpdateStage.Ready);
        }

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = CheckInterval;
        _timer.Tick += async (_, _) => { if (_settings.AutoCheckUpdates) await CheckAsync(userInitiated: false); };
        _timer.Start();

        // first check shortly after startup, once the network is usually up
        var first = _dispatcher.CreateTimer();
        first.Interval = TimeSpan.FromSeconds(12);
        first.IsRepeating = false;
        first.Tick += async (_, _) => { if (_settings.AutoCheckUpdates) await CheckAsync(userInitiated: false); };
        first.Start();
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (!_manager.IsInstalled || _busy || Stage is UpdateStage.Downloading or UpdateStage.Ready) return;
        _busy = true;
        Set(UpdateStage.Checking);
        try
        {
            _info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            LastChecked = DateTimeOffset.Now;
            if (_info is null)
            {
                Set(UpdateStage.UpToDate);
                return;
            }

            NewVersion = _info.TargetFullRelease.Version.ToString();
            ReleaseNotes = _info.TargetFullRelease.NotesMarkdown;
            Log.Info($"Update available: {NewVersion}");
            Set(UpdateStage.Available);

            if (!userInitiated && _settings.LastNotifiedVersion != NewVersion)
            {
                _settings.LastNotifiedVersion = NewVersion;
                _dispatcher.TryEnqueue(() => _saveSettings());
                Notifications.Show($"TileSmith {NewVersion} is available", "Open the panel to update — it only takes a few seconds.",
                    "open", "Update now", "update");
            }

            if (_settings.AutoDownloadUpdates) await DownloadCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
            Error = userInitiated ? "Couldn't check for updates. Check your internet connection." : null;
            Set(userInitiated ? UpdateStage.Error : UpdateStage.Idle);
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task DownloadAsync()
    {
        if (_busy) return;
        _busy = true;
        try { await DownloadCoreAsync().ConfigureAwait(false); }
        finally { _busy = false; }
    }

    private async Task DownloadCoreAsync()
    {
        if (_info is null) return;
        Progress = 0;
        Set(UpdateStage.Downloading);
        try
        {
            await _manager.DownloadUpdatesAsync(_info, p =>
            {
                Progress = p;
                _dispatcher.TryEnqueue(() => Changed?.Invoke());
            }).ConfigureAwait(false);
            Set(UpdateStage.Ready);
        }
        catch (Exception ex)
        {
            Log.Error("Update download failed", ex);
            Error = "The download didn't finish. Try again in a moment.";
            Set(UpdateStage.Error);
        }
    }

    /// <summary>Applies the downloaded update and relaunches TileSmith.</summary>
    public void RestartToApply(Action beforeExit)
    {
        var asset = _manager.UpdatePendingRestart ?? _info?.TargetFullRelease;
        if (asset is null) return;
        beforeExit();
        _manager.ApplyUpdatesAndRestart(asset, ["--updated"]);
    }

    private void Set(UpdateStage stage)
    {
        Stage = stage;
        _dispatcher.TryEnqueue(() => Changed?.Invoke());
    }
}
