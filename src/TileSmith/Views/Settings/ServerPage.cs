using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Controls;
using TileSmith.Core;
using TileSmith.Core.Ai;
using TileSmith.Core.Remote;
using TileSmith.Core.Settings;
using TileSmith.Services;
using TileSmith.Theme;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TileSmith.Views.Settings;

/// <summary>The SSH server: where Hermes and other server-side AI live, and where "server" widgets run commands.</summary>
public sealed class ServerPage : SettingsPage
{
    private readonly StackPanel _status = new();
    private readonly StackPanel _authRows = new();
    private readonly StackPanel _detected = new();
    private readonly TextBox _address;
    private readonly WrapPanel _tailnet = new() { HorizontalSpacing = 6, VerticalSpacing = 6 };
    private readonly TextBlock _tailnetNote;
    private Chip _connect = null!;
    private Chip _detect = null!;
    private bool _busy;

    public ServerPage(AppHost host, SettingsWindow window)
        : base(host, window, "Server", "Your own machine, reached over SSH — for example through Tailscale. Hermes and other AI there, and widgets that run commands on it, use this connection.")
    {
        var p = Palette.Current;
        Children.Add(_status);

        var addressField = Kit.TextField("you@your-server", S.Server.Address, CommitAddress, out _address, width: 300);
        _tailnetNote = Kit.Body("Looking for Tailscale…", 12.5, p.Text3);
        var tailnet = new StackPanel { Spacing = 8, Margin = new Thickness(66, 0, 16, 14) };
        tailnet.Children.Add(_tailnetNote);
        tailnet.Children.Add(_tailnet);

        _connect = Kit.Button("Connect", "link", ChipStyle.Filled, () => _ = ConnectAsync());
        _detect = Kit.Button("Detect AI", "travel_explore", ChipStyle.Outline, () => _ = DetectAsync());
        var addressBlock = new StackPanel();
        addressBlock.Children.Add(Kit.Row("dns", "Address", "user@hostname or user@100.x.y.z — add :port if it isn't 22.", addressField));
        addressBlock.Children.Add(tailnet);
        Section("Connection", null,
            addressBlock,
            Kit.Row("key", "Sign in with", null, Kit.Segmented([("password", "Password"), ("key", "SSH key")], S.Server.AuthMethod, id =>
            {
                S.Server.AuthMethod = id;
                if (id == "key" && string.IsNullOrEmpty(S.Server.KeyPath))
                {
                    S.Server.KeyPath = new[] { "id_ed25519", "id_ecdsa", "id_rsa" }
                        .Select(n => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", n))
                        .FirstOrDefault(File.Exists);
                }
                SaveServer();
                BuildAuthRows();
            }, 240)),
            _authRows,
            Kit.Padded(Kit.Actions(_connect, _detect), 14));

        Children.Add(Kit.Section("AI on the server", "What Detect found. TileSmith re-checks every time it connects."));
        Children.Add(Kit.Card(_detected));

        BuildAuthRows();
        UpdateStatus();
        BuildDetected();
        Host.Server.Changed += UpdateStatus;
        _ = LoadTailnetAsync();
    }

    public override void OnHidden() => Host.Server.Changed -= UpdateStatus;

    private void SaveServer()
    {
        Host.Server.Reset();
        Save();
    }

    private void CommitAddress(string text)
    {
        if (text == S.Server.Address) return;
        if (text.Length > 0 && !SshTarget.TryParse(text, out _, out var error))
        {
            ShowStatus(Kit.Tone.Warning, "Check the address", error);
            return;
        }
        S.Server.Address = text;
        SaveServer();
    }

    private void BuildAuthRows()
    {
        _authRows.Children.Clear();
        var p = Palette.Current;
        void Divider() => _authRows.Children.Add(new Border { Height = 1, Background = p.Brush(p.Hairline), Margin = new Thickness(16, 0, 16, 0) });
        if (S.Server.AuthMethod == "key")
        {
            var keyField = Kit.TextField(@"C:\Users\you\.ssh\id_ed25519", S.Server.KeyPath ?? "", path => { S.Server.KeyPath = path; SaveServer(); }, out var keyBox, width: 300);
            var browse = Kit.Button("Browse", "folder_open", ChipStyle.Outline, async () =>
            {
                var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                InitializeWithWindow.Initialize(picker, Window.Handle);
                var file = await picker.PickSingleFileAsync();
                if (file is null) return;
                keyBox.Text = file.Path;
                S.Server.KeyPath = file.Path;
                SaveServer();
            });
            var keyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            keyRow.Children.Add(keyField);
            keyRow.Children.Add(browse);
            _authRows.Children.Add(Kit.Row("vpn_key", "Private key file", null, keyRow, stackBelow: 680));
            Divider();
            _authRows.Children.Add(Kit.Row("password", "Key passphrase", "Only if your key has one. Stored encrypted for your Windows account.",
                Kit.PasswordField("Passphrase", Secrets.Unprotect(S.Server.ProtectedKeyPassphrase) ?? "", value =>
                {
                    S.Server.ProtectedKeyPassphrase = Secrets.Protect(value);
                    SaveServer();
                }, out _, width: 300)));
        }
        else
        {
            _authRows.Children.Add(Kit.Row("password", "Password", "Stored encrypted for your Windows account only.",
                Kit.PasswordField("Password", Secrets.Unprotect(S.Server.ProtectedPassword) ?? "", value =>
                {
                    S.Server.ProtectedPassword = Secrets.Protect(value);
                    SaveServer();
                }, out _, width: 300)));
            Divider();
            _authRows.Children.Add(Kit.Row("encrypted", "Use a key instead", "Creates an SSH key for TileSmith, adds it to the server and forgets the password.",
                Kit.Button("Set up key", "key", ChipStyle.Outline, () => _ = UseKeyAsync())));
        }
    }

    private async Task LoadTailnetAsync()
    {
        var status = await Tailscale.GetStatusAsync();
        if (!status.Installed)
        {
            _tailnetNote.Text = "Tip: Tailscale is the easiest way to reach your server from anywhere.";
            return;
        }
        if (!status.Running)
        {
            _tailnetNote.Text = "Tailscale is installed but not connected. Open Tailscale and sign in to pick a device here.";
            return;
        }
        var peers = status.Peers.Where(p => !p.Os.Equals("android", StringComparison.OrdinalIgnoreCase) && !p.Os.Equals("ios", StringComparison.OrdinalIgnoreCase)).ToList();
        var online = peers.Count(p => p.Online);
        _tailnetNote.Text = $"Your tailnet{(status.Tailnet is null ? "" : $" ({status.Tailnet})")}: {online} device{(online == 1 ? "" : "s")} online — tap one to use it.";
        foreach (var peer in peers.OrderByDescending(p => p.Online).Take(12))
        {
            var chip = new Chip(peer.Address, peer.Online ? "computer" : "cloud_off", height: 30, style: ChipStyle.Outline);
            ToolTipService.SetToolTip(chip, $"{peer.DnsName} · {peer.Os}{(peer.Online ? "" : " · offline")}");
            chip.Opacity = peer.Online ? 1 : 0.6;
            chip.Click += (_, _) =>
            {
                SshTarget.TryParse(_address.Text, out var current, out _);
                var user = current.User ?? Environment.UserName.ToLowerInvariant();
                _address.Text = $"{user}@{peer.Address}";
                CommitAddress(_address.Text);
                _address.Focus(FocusState.Programmatic);
                _address.Select(0, user.Length); // the user name is the part people usually need to change
            };
            _tailnet.Children.Add(chip);
        }
    }

    private void UpdateStatus()
    {
        var server = Host.Server;
        var (tone, title) = server.State switch
        {
            ConnectionState.Connected => (Kit.Tone.Success, "Connected"),
            ConnectionState.Connecting => (Kit.Tone.Info, "Connecting…"),
            ConnectionState.Error => (Kit.Tone.Error, "Can't connect"),
            ConnectionState.NotConfigured => (Kit.Tone.Info, "No server yet"),
            _ => (Kit.Tone.Info, "Not connected"),
        };
        var message = server.State == ConnectionState.NotConfigured
            ? "Optional — Claude Code, Cursor and API keys work without one. Add your server to use Hermes or other AI running there."
            : server.StatusText;
        UIElement? action = server.PendingHostKeyChange is not null
            ? Kit.Button("Trust the new key", "verified_user", ChipStyle.Filled, () => _ = Host.Server.TrustNewHostKeyAndConnectAsync())
            : null;
        ShowStatus(tone, title, message, action);
    }

    private void ShowStatus(Kit.Tone tone, string title, string? message, UIElement? action = null)
    {
        _status.Children.Clear();
        var banner = Kit.Banner(tone, title, message, action);
        banner.Margin = new Thickness(0, 14, 0, 0);
        _status.Children.Add(banner);
    }

    private void BuildDetected()
    {
        var p = Palette.Current;
        var ai = S.Ai;
        _detected.Children.Clear();
        if (ai.DetectedSummary is null)
        {
            _detected.Children.Add(Kit.Empty("travel_explore", "Not checked yet", "Connect, then press Detect AI."));
            return;
        }
        var rows = new List<UIElement>();
        if (ai.DetectedEngine == "hermes" || ai.HermesCommand.StartsWith("docker", StringComparison.Ordinal))
        {
            rows.Add(Kit.Row("smart_toy", "Hermes Agent", $"Runs as: {ai.HermesCommand}" + (ai.HermesDefaultModel is null ? "" : $"\nModel: {ai.HermesDefaultModel}") + (ai.HermesBaseUrl is null ? "" : $" via {ai.HermesBaseUrl}"),
                Kit.Badge("Ready", p.Success, "check_circle"), Palette.Parse("#A78BFA")));
        }
        rows.Add(Kit.Row("info", "Summary", ai.DetectedSummary, null, p.Text3));
        var first = true;
        foreach (var row in rows)
        {
            if (!first) _detected.Children.Add(new Border { Height = 1, Background = p.Brush(p.Hairline), Margin = new Thickness(16, 0, 16, 0) });
            first = false;
            _detected.Children.Add(row);
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _connect.IsEnabledForInput = _detect.IsEnabledForInput = !busy;
        _connect.Opacity = _detect.Opacity = busy ? 0.6 : 1;
    }

    private async Task ConnectAsync()
    {
        if (_busy) return;
        CommitAddress(_address.Text.Trim());
        if (!S.Server.IsConfigured)
        {
            _address.Focus(FocusState.Programmatic);
            return;
        }
        SetBusy(true);
        try
        {
            if (await Host.Server.ConnectAsync()) await DetectCoreAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task DetectAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try { await DetectCoreAsync(); }
        finally { SetBusy(false); }
    }

    private async Task DetectCoreAsync()
    {
        try
        {
            if (!Host.Server.GetConnection().IsConnected && !await Host.Server.ConnectAsync()) return;
            ShowStatus(Kit.Tone.Info, "Looking for AI on the server…", null);
            var found = await EngineDetector.DetectAsync(Host.Server.GetConnection(), CancellationToken.None);
            EngineDetector.Apply(found, S.Ai);
            Save();
            BuildDetected();
            ShowStatus(found.Recommended is null ? Kit.Tone.Warning : Kit.Tone.Success, found.Recommended is null ? "No AI found on the server" : "AI found",
                found.Summary + (found.Os is null ? "" : $" ({found.Os})")
                + (found.Recommended is null ? " Install Hermes Agent there, or use Claude Code, Cursor or an API key." : ""),
                found.Recommended is null ? null : Kit.Button("Set up AI", "auto_awesome", ChipStyle.Filled, () => Window.Go("ai")));
        }
        catch (Exception ex)
        {
            ShowStatus(Kit.Tone.Error, "Detection failed", ex.Message);
        }
    }

    /// <summary>Creates a dedicated ed25519 key, authorizes it on the server, then drops the stored password.</summary>
    private async Task UseKeyAsync()
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            if (!await Host.Server.ConnectAsync()) return;
            Directory.CreateDirectory(AppPaths.KeysDir);
            var keyPath = Path.Combine(AppPaths.KeysDir, "id_ed25519");
            if (!File.Exists(keyPath))
            {
                var psi = new ProcessStartInfo("ssh-keygen.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "-t", "ed25519", "-N", "", "-C", $"tilesmith@{Environment.MachineName}", "-f", keyPath }) psi.ArgumentList.Add(a);
                using var proc = Process.Start(psi) ?? throw new InvalidOperationException("ssh-keygen isn't available (Windows OpenSSH client).");
                await proc.WaitForExitAsync();
                if (proc.ExitCode != 0) throw new InvalidOperationException("ssh-keygen failed: " + await proc.StandardError.ReadToEndAsync());
            }
            var publicKey = (await File.ReadAllTextAsync(keyPath + ".pub")).Trim();
            var script = $"""
                mkdir -p ~/.ssh && chmod 700 ~/.ssh
                touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys
                grep -qxF '{publicKey}' ~/.ssh/authorized_keys || echo '{publicKey}' >> ~/.ssh/authorized_keys
                echo ok
                """;
            var result = await Host.Server.GetConnection().RunScriptAsync(script, TimeSpan.FromSeconds(20), CancellationToken.None);
            if (!result.Stdout.Contains("ok", StringComparison.Ordinal)) throw new InvalidOperationException(result.Stderr.Trim());

            var previous = (S.Server.AuthMethod, S.Server.KeyPath, S.Server.ProtectedPassword);
            S.Server.AuthMethod = "key";
            S.Server.KeyPath = keyPath;
            S.Server.ProtectedKeyPassphrase = null;
            Host.Server.Reset();
            if (await Host.Server.ConnectAsync())
            {
                S.Server.ProtectedPassword = null;
                Save();
                Window.Go("server");
            }
            else
            {
                (S.Server.AuthMethod, S.Server.KeyPath, S.Server.ProtectedPassword) = previous;
                Host.Server.Reset();
                Save();
            }
        }
        catch (Exception ex)
        {
            ShowStatus(Kit.Tone.Error, "Couldn't set up the key", ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }
}
