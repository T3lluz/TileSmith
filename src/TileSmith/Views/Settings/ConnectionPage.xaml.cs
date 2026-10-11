using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TileSmith.Core;
using TileSmith.Core.Ai;
using TileSmith.Core.Remote;
using TileSmith.Core.Settings;
using TileSmith.Services;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace TileSmith.Views.Settings;

public sealed partial class ConnectionPage : UserControl
{
    private readonly AppHost _host;
    private readonly SettingsWindow _window;
    private bool _loading = true;
    private IReadOnlyList<TailscalePeer> _peers = [];

    private AppSettings S => _host.Settings;

    public ConnectionPage(AppHost host, SettingsWindow window)
    {
        _host = host;
        _window = window;
        InitializeComponent();

        foreach (var (id, name, _) in AiEngineFactory.Engines) EngineCombo.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        Select(EngineCombo, S.Ai.Engine);
        LocalModelBox.Text = S.Ai.LocalModel ?? "";
        HermesCommandBox.Text = S.Ai.HermesCommand;
        HermesArgsBox.Text = S.Ai.HermesArgs ?? "";
        BaseUrlBox.Text = S.Ai.OpenAiBaseUrl;
        TunnelToggle.IsOn = S.Ai.OpenAiViaTunnel;
        ApiKeyBox.Password = Secrets.Unprotect(S.Ai.ProtectedOpenAiKey) ?? "";
        if (!string.IsNullOrEmpty(S.Ai.OpenAiModel))
        {
            ModelCombo.Items.Add(S.Ai.OpenAiModel);
            ModelCombo.SelectedItem = S.Ai.OpenAiModel;
        }
        CustomCommandBox.Text = S.Ai.CustomCommand ?? "";
        Select(PermissionCombo, S.Ai.Permission);

        AddressBox.Text = S.Server.Address;
        Select(AuthCombo, S.Server.AuthMethod);
        PasswordBox.Password = Secrets.Unprotect(S.Server.ProtectedPassword) ?? "";
        KeyPathBox.Text = S.Server.KeyPath ?? "";
        PassphraseBox.Password = Secrets.Unprotect(S.Server.ProtectedKeyPassphrase) ?? "";

        _loading = false;
        UpdateEnginePanels();
        UpdateAuthPanels();
        UpdateServerStatus();
        _host.Server.Changed += UpdateServerStatus;
        Unloaded += (_, _) => _host.Server.Changed -= UpdateServerStatus;
        _ = LoadTailnetAsync();
    }

    private static void Select(ComboBox combo, string tag) =>
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? combo.Items[0];

    private static string? TagOf(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void Save(bool serverChanged = false)
    {
        if (_loading) return;
        if (serverChanged) _host.Server.Reset();
        _host.SaveSettings();
    }

    // ── Engine ───────────────────────────────────────────────────────────────────

    private void EngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        S.Ai.Engine = TagOf(EngineCombo) ?? "auto";
        Save();
        UpdateEnginePanels();
    }

    private void UpdateEnginePanels()
    {
        var chosen = S.Ai.Engine;
        var effective = AiEngineFactory.ResolveEngine(S);
        var info = AiEngineFactory.Engines.First(x => x.Id == chosen);
        EngineCard.Description = chosen == "auto"
            ? $"{info.Description}. Right now: {AiEngineFactory.Engines.First(x => x.Id == effective).Name}."
            : info.Description;

        LocalPanel.Visibility = effective is "claude" or "cursor" ? Visibility.Visible : Visibility.Collapsed;
        HermesPanel.Visibility = effective == "hermes" ? Visibility.Visible : Visibility.Collapsed;
        OpenAiPanel.Visibility = effective == "openai" ? Visibility.Visible : Visibility.Collapsed;
        CommandPanel.Visibility = effective == "command" ? Visibility.Visible : Visibility.Collapsed;
        if (effective is "claude" or "cursor") _ = RefreshAccountAsync();
    }

    private LocalCli CurrentCli => AiEngineFactory.ResolveEngine(S) == "cursor" ? LocalCli.Cursor : LocalCli.Claude;

    private async Task RefreshAccountAsync()
    {
        var cli = CurrentCli;
        var name = cli == LocalCli.Claude ? "Claude Code" : "Cursor Agent";
        AccountCard.Header = cli == LocalCli.Claude ? "Claude account" : "Cursor account";
        if (LocalCliBackend.Locate(cli) is null)
        {
            AccountCard.Description = $"{name} isn't installed on this PC.";
            SignInButton.Content = "Get it";
            return;
        }
        AccountCard.Description = "Checking…";
        SignInButton.Content = "Sign in";
        try
        {
            var status = await LocalCliBackend.GetAccountAsync(cli, CancellationToken.None) ?? "";
            var firstLine = status.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
            var loggedIn = status.Contains("logged in", StringComparison.OrdinalIgnoreCase) && !status.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
                           || status.Contains("\"loggedIn\": true", StringComparison.OrdinalIgnoreCase);
            AccountCard.Description = loggedIn
                ? $"Signed in. {Trim(firstLine)} TileSmith uses {name} on this PC; your sign-in stays with {name}."
                : $"Not signed in. {Trim(firstLine)}";
            SignInButton.Content = loggedIn ? "Switch account" : "Sign in";
        }
        catch (Exception ex)
        {
            AccountCard.Description = "Couldn't check: " + ex.Message;
        }
    }

    private static string Trim(string s) => s.Length > 120 ? s[..120] + "…" : s.TrimEnd('.') + ".";

    private void RefreshAccount_Click(object sender, RoutedEventArgs e) => _ = RefreshAccountAsync();

    private void SignIn_Click(object sender, RoutedEventArgs e)
    {
        var cli = CurrentCli;
        if (LocalCliBackend.Locate(cli) is null)
        {
            var url = cli == LocalCli.Claude ? "https://claude.com/product/claude-code" : "https://cursor.com/cli";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }
        LocalCliBackend.StartLogin(cli);
    }

    private void LocalModelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        S.Ai.LocalModel = string.IsNullOrWhiteSpace(LocalModelBox.Text) ? null : LocalModelBox.Text.Trim();
        Save();
    }

    private void HermesBoxes_LostFocus(object sender, RoutedEventArgs e)
    {
        S.Ai.HermesCommand = string.IsNullOrWhiteSpace(HermesCommandBox.Text) ? "hermes" : HermesCommandBox.Text.Trim();
        S.Ai.HermesArgs = string.IsNullOrWhiteSpace(HermesArgsBox.Text) ? null : HermesArgsBox.Text.Trim();
        Save();
    }

    private void OpenAiBoxes_LostFocus(object sender, RoutedEventArgs e)
    {
        S.Ai.OpenAiBaseUrl = BaseUrlBox.Text.Trim();
        Save();
    }

    private void TunnelToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Ai.OpenAiViaTunnel = TunnelToggle.IsOn;
        Save();
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Ai.ProtectedOpenAiKey = Secrets.Protect(ApiKeyBox.Password);
        Save();
    }

    private async void LoadModels_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var backend = new OpenAiBackend(S.Ai.OpenAiViaTunnel ? _host.Server.GetConnection() : null, S.Ai.OpenAiBaseUrl,
                S.Ai.OpenAiViaTunnel, null, Secrets.Unprotect(S.Ai.ProtectedOpenAiKey), TimeSpan.FromSeconds(30));
            var models = await backend.ListModelsAsync(CancellationToken.None);
            ModelCombo.Items.Clear();
            foreach (var m in models) ModelCombo.Items.Add(m);
            if (models.Count > 0 && string.IsNullOrEmpty(S.Ai.OpenAiModel)) ModelCombo.SelectedIndex = 0;
            else if (S.Ai.OpenAiModel is not null) ModelCombo.SelectedItem = S.Ai.OpenAiModel;
            ShowTestResult(models.Count == 0 ? "The API returned no models." : $"Found {models.Count} models.", models.Count > 0);
        }
        catch (Exception ex)
        {
            ShowTestResult("Couldn't load models: " + ex.Message, false);
        }
    }

    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModelCombo.SelectedItem is not string model) return;
        S.Ai.OpenAiModel = model;
        Save();
    }

    private void ModelCombo_TextSubmitted(ComboBox sender, ComboBoxTextSubmittedEventArgs args)
    {
        S.Ai.OpenAiModel = string.IsNullOrWhiteSpace(args.Text) ? null : args.Text.Trim();
        Save();
    }

    private void CustomCommandBox_LostFocus(object sender, RoutedEventArgs e)
    {
        S.Ai.CustomCommand = string.IsNullOrWhiteSpace(CustomCommandBox.Text) ? null : CustomCommandBox.Text.Trim();
        Save();
    }

    private void PermissionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        S.Ai.Permission = TagOf(PermissionCombo) ?? "auto";
        Save();
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestRing.IsActive = true;
        TestRing.Visibility = Visibility.Visible;
        TestCard.Description = "Waiting for the reply…";
        var sw = Stopwatch.StartNew();
        try
        {
            var backend = _host.CreateBackend();
            var reply = await backend.CompleteAsync(
                [AiMessage.System("You are a connectivity check."), AiMessage.User("Reply with exactly this JSON and nothing else: {\"ok\": true}")],
                CancellationToken.None);
            var ok = reply.Contains("ok", StringComparison.OrdinalIgnoreCase);
            ShowTestResult(ok
                ? $"{backend.DisplayName} answered in {sw.Elapsed.TotalSeconds:0.0} s. You're all set."
                : $"Got an unexpected reply in {sw.Elapsed.TotalSeconds:0.0} s: {Short(reply)}", ok);
            if (AiEngineFactory.NeedsServer(AiEngineFactory.ResolveEngine(S), S.Ai)) _host.Server.MarkConnected();
        }
        catch (AiLoginRequiredException ex)
        {
            ShowTestResult(ex.Message + " Use “Sign in” above.", false);
        }
        catch (Exception ex)
        {
            ShowTestResult(ex.Message, false);
        }
        finally
        {
            TestButton.IsEnabled = true;
            TestRing.IsActive = false;
            TestRing.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowTestResult(string text, bool ok) => TestCard.Description = (ok ? "✓ " : "⚠ ") + text;

    private static string Short(string s) => s.Length > 160 ? s[..160] + "…" : s;

    // ── Server ───────────────────────────────────────────────────────────────────

    private void AddressBox_LostFocus(object sender, RoutedEventArgs e)
    {
        var text = AddressBox.Text.Trim();
        if (text == S.Server.Address) return;
        if (text.Length > 0 && !SshTarget.TryParse(text, out _, out var error))
        {
            ServerStatusBar.Severity = InfoBarSeverity.Warning;
            ServerStatusBar.Title = "Check the address";
            ServerStatusBar.Message = error;
            return;
        }
        S.Server.Address = text;
        Save(serverChanged: true);
        UpdateEnginePanels();
    }

    private async Task LoadTailnetAsync()
    {
        var status = await Tailscale.GetStatusAsync();
        if (!status.Installed)
        {
            TailnetCard.Description = "Tailscale isn't installed. It's the easiest way to reach your server from anywhere.";
            TailnetCombo.IsEnabled = false;
            return;
        }
        if (!status.Running)
        {
            TailnetCard.Description = "Tailscale is installed but not connected. Open Tailscale and sign in.";
            TailnetCombo.IsEnabled = false;
            return;
        }
        _peers = status.Peers.Where(p => !p.Os.Equals("android", StringComparison.OrdinalIgnoreCase) && !p.Os.Equals("ios", StringComparison.OrdinalIgnoreCase)).ToList();
        var online = _peers.Count(p => p.Online);
        TailnetCard.Description = $"Connected as {status.Self}{(status.Tailnet is null ? "" : $" on {status.Tailnet}")} · {online} device{(online == 1 ? "" : "s")} online";
        foreach (var peer in _peers)
            TailnetCombo.Items.Add($"{peer.Address}  ·  {peer.Os}{(peer.Online ? "" : "  ·  offline")}");
    }

    private void TailnetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TailnetCombo.SelectedIndex < 0 || TailnetCombo.SelectedIndex >= _peers.Count) return;
        var peer = _peers[TailnetCombo.SelectedIndex];
        SshTarget.TryParse(AddressBox.Text, out var current, out _);
        var user = current.User ?? Environment.UserName.ToLowerInvariant();
        AddressBox.Text = $"{user}@{peer.Address}";
        AddressBox_LostFocus(AddressBox, new RoutedEventArgs());
        AddressBox.Focus(FocusState.Programmatic);
        AddressBox.Select(0, user.Length); // the user name is the part people usually need to change
    }

    private void AuthCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        S.Server.AuthMethod = TagOf(AuthCombo) ?? "password";
        if (S.Server.AuthMethod == "key" && string.IsNullOrEmpty(S.Server.KeyPath))
        {
            var guess = new[] { "id_ed25519", "id_ecdsa", "id_rsa" }
                .Select(n => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", n))
                .FirstOrDefault(File.Exists);
            if (guess is not null)
            {
                S.Server.KeyPath = guess;
                KeyPathBox.Text = guess;
            }
        }
        Save(serverChanged: true);
        UpdateAuthPanels();
    }

    private void UpdateAuthPanels()
    {
        var key = S.Server.AuthMethod == "key";
        PasswordCard.Visibility = key ? Visibility.Collapsed : Visibility.Visible;
        KeyCard.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
        PassphraseCard.Visibility = key ? Visibility.Visible : Visibility.Collapsed;
        UseKeyCard.Visibility = key ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Server.ProtectedPassword = Secrets.Protect(PasswordBox.Password);
        Save(serverChanged: true);
    }

    private void KeyPathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        S.Server.KeyPath = KeyPathBox.Text.Trim();
        Save(serverChanged: true);
    }

    private void PassphraseBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Server.ProtectedKeyPassphrase = Secrets.Protect(PassphraseBox.Password);
        Save(serverChanged: true);
    }

    private async void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _window.Handle);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        KeyPathBox.Text = file.Path;
        S.Server.KeyPath = file.Path;
        Save(serverChanged: true);
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        AddressBox_LostFocus(AddressBox, e);
        if (!S.Server.IsConfigured)
        {
            AddressBox.Focus(FocusState.Programmatic);
            return;
        }
        SetBusy(ConnectRing, ConnectButton, true);
        try
        {
            if (await _host.Server.ConnectAsync() && S.Ai.DetectedEngine is null) await DetectAsync();
        }
        finally
        {
            SetBusy(ConnectRing, ConnectButton, false);
        }
    }

    private async void TrustKey_Click(object sender, RoutedEventArgs e) => await _host.Server.TrustNewHostKeyAndConnectAsync();

    private async void Detect_Click(object sender, RoutedEventArgs e) => await DetectAsync();

    private async Task DetectAsync()
    {
        SetBusy(DetectRing, DetectButton, true);
        try
        {
            if (!_host.Server.GetConnection().IsConnected && !await _host.Server.ConnectAsync()) return;
            var found = await EngineDetector.DetectAsync(_host.Server.GetConnection(), CancellationToken.None);
            EngineDetector.Apply(found, S.Ai);
            Save();
            HermesCommandBox.Text = S.Ai.HermesCommand;
            BaseUrlBox.Text = S.Ai.OpenAiBaseUrl;
            UpdateEnginePanels();
            ServerStatusBar.Severity = found.Recommended is null ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
            ServerStatusBar.Title = found.Recommended is null ? "No AI found on the server" : "AI found";
            ServerStatusBar.Message = found.Summary + (found.Os is null ? "" : $" ({found.Os})")
                + (found.Recommended is null ? " Install Hermes Agent there, or use your Claude / Cursor subscription." : "");
        }
        catch (Exception ex)
        {
            ServerStatusBar.Severity = InfoBarSeverity.Error;
            ServerStatusBar.Title = "Detection failed";
            ServerStatusBar.Message = ex.Message;
        }
        finally
        {
            SetBusy(DetectRing, DetectButton, false);
        }
    }

    /// <summary>Creates a dedicated ed25519 key, authorizes it on the server, then drops the stored password.</summary>
    private async void UseKey_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(ConnectRing, UseKeyButton, true);
        try
        {
            if (!await _host.Server.ConnectAsync()) return;
            Directory.CreateDirectory(AppPaths.KeysDir);
            var keyPath = Path.Combine(AppPaths.KeysDir, "id_ed25519");
            if (!File.Exists(keyPath))
            {
                var psi = new ProcessStartInfo("ssh-keygen.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "-t", "ed25519", "-N", "", "-C", $"tilesmith@{Environment.MachineName}", "-f", keyPath }) psi.ArgumentList.Add(a);
                using var p = Process.Start(psi) ?? throw new InvalidOperationException("ssh-keygen isn't available (Windows OpenSSH client).");
                await p.WaitForExitAsync();
                if (p.ExitCode != 0) throw new InvalidOperationException("ssh-keygen failed: " + await p.StandardError.ReadToEndAsync());
            }
            var publicKey = (await File.ReadAllTextAsync(keyPath + ".pub")).Trim();
            var script = $"""
                mkdir -p ~/.ssh && chmod 700 ~/.ssh
                touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys
                grep -qxF '{publicKey}' ~/.ssh/authorized_keys || echo '{publicKey}' >> ~/.ssh/authorized_keys
                echo ok
                """;
            var result = await _host.Server.GetConnection().RunScriptAsync(script, TimeSpan.FromSeconds(20), CancellationToken.None);
            if (!result.Stdout.Contains("ok", StringComparison.Ordinal)) throw new InvalidOperationException(result.Stderr.Trim());

            var previous = (S.Server.AuthMethod, S.Server.KeyPath, S.Server.ProtectedPassword);
            S.Server.AuthMethod = "key";
            S.Server.KeyPath = keyPath;
            S.Server.ProtectedKeyPassphrase = null;
            _host.Server.Reset();
            if (await _host.Server.ConnectAsync())
            {
                S.Server.ProtectedPassword = null;
                PasswordBox.Password = "";
                _host.SaveSettings();
                _loading = true;
                Select(AuthCombo, "key");
                KeyPathBox.Text = keyPath;
                _loading = false;
                UpdateAuthPanels();
                ServerStatusBar.Title = "Now signing in with a key";
                ServerStatusBar.Message = "The password is no longer stored. Key: " + keyPath;
            }
            else
            {
                (S.Server.AuthMethod, S.Server.KeyPath, S.Server.ProtectedPassword) = previous;
                _host.Server.Reset();
                _host.SaveSettings();
            }
        }
        catch (Exception ex)
        {
            ServerStatusBar.Severity = InfoBarSeverity.Error;
            ServerStatusBar.Title = "Couldn't set up the key";
            ServerStatusBar.Message = ex.Message;
        }
        finally
        {
            SetBusy(ConnectRing, UseKeyButton, false);
        }
    }

    private void UpdateServerStatus()
    {
        var server = _host.Server;
        TrustKeyButton.Visibility = server.PendingHostKeyChange is null ? Visibility.Collapsed : Visibility.Visible;
        (ServerStatusBar.Severity, ServerStatusBar.Title) = server.State switch
        {
            ConnectionState.Connected => (InfoBarSeverity.Success, "Connected"),
            ConnectionState.Connecting => (InfoBarSeverity.Informational, "Connecting…"),
            ConnectionState.Error => (InfoBarSeverity.Error, "Can't connect"),
            ConnectionState.NotConfigured => (InfoBarSeverity.Informational, "No server yet"),
            _ => (InfoBarSeverity.Informational, "Not connected"),
        };
        ServerStatusBar.Message = server.State == ConnectionState.NotConfigured
            ? "Optional if you use your Claude or Cursor subscription. Enter your server's address to use Hermes or other AI running there."
            : server.StatusText;
    }

    private static void SetBusy(ProgressRing ring, Button button, bool busy)
    {
        ring.IsActive = busy;
        ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        button.IsEnabled = !busy;
    }
}
