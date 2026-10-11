using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Renci.SshNet;
using Renci.SshNet.Common;
using TileSmith.Core.Settings;

namespace TileSmith.Core.Remote;

public sealed record CommandResult(int ExitCode, string Stdout, string Stderr, TimeSpan Elapsed)
{
    public bool Success => ExitCode == 0;
}

public sealed class ServerException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class HostKeyChangedException(string host, string expected, string actual)
    : Exception($"The identity of {host} changed. Expected SHA256:{expected}, got SHA256:{actual}.")
{
    public string Host { get; } = host;
    public string Actual { get; } = actual;
}

/// <summary>Trust-on-first-use store of server host key fingerprints.</summary>
public sealed class KnownHostsStore
{
    private readonly string _file;
    private readonly Dictionary<string, string> _entries;

    public KnownHostsStore(string file)
    {
        _file = file;
        try
        {
            _entries = File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? []
                : [];
        }
        catch
        {
            _entries = [];
        }
    }

    private static string Key(string host, int port) => $"{host.ToLowerInvariant()}:{port}";

    public string? Get(string host, int port) => _entries.GetValueOrDefault(Key(host, port));

    public void Trust(string host, int port, string fingerprint)
    {
        lock (_entries)
        {
            _entries[Key(host, port)] = fingerprint;
            AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(_entries, JsonDefaults.Options));
        }
    }
}

/// <summary>
/// One SSH connection to the user's server (usually reached over Tailscale).
/// Runs commands and opens local port forwards for HTTP APIs that only listen on the server's localhost.
/// </summary>
public sealed class ServerConnection : IDisposable
{
    private readonly SshTarget _target;
    private readonly ServerProfile _profile;
    private readonly string? _password;
    private readonly KnownHostsStore _knownHosts;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly Dictionary<string, ForwardedPortLocal> _tunnels = [];
    private SshClient? _client;
    private string? _pendingMismatch;

    public ServerConnection(ServerProfile profile, KnownHostsStore knownHosts)
    {
        if (!SshTarget.TryParse(profile.Address, out var target, out var error)) throw new ServerException(error!);
        if (target.User is null) throw new ServerException("Add a user name to the address, like you@" + target.Host + ".");
        _target = target;
        _profile = profile;
        _knownHosts = knownHosts;
        _password = Secrets.Unprotect(profile.ProtectedPassword);
    }

    public SshTarget Target => _target;
    public bool IsConnected => _client?.IsConnected == true;
    public string? HostKeyFingerprint { get; private set; }
    public string? ServerVersion => _client?.ConnectionInfo?.ServerVersion;

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (IsConnected) return;
        await _connectLock.WaitAsync(ct);
        try
        {
            if (IsConnected) return;
            _client?.Dispose();
            _client = null;

            await EnsureReachableAsync(ct);

            var client = new SshClient(BuildConnectionInfo()) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
            client.HostKeyReceived += OnHostKeyReceived;
            _pendingMismatch = null;
            try
            {
                await client.ConnectAsync(ct);
            }
            catch (SshConnectionException) when (_pendingMismatch is not null)
            {
                client.Dispose();
                throw new HostKeyChangedException(_target.Host, _knownHosts.Get(_target.Host, _target.Port) ?? "?", _pendingMismatch);
            }
            catch (SshAuthenticationException ex)
            {
                client.Dispose();
                throw new ServerException(_profile.AuthMethod == "key"
                    ? $"The server rejected the SSH key for {_target.User}. ({ex.Message})"
                    : $"Wrong user name or password for {_target.User}@{_target.Host}.", ex);
            }
            catch (Exception ex) when (ex is SocketException or SshConnectionException or SshOperationTimeoutException)
            {
                client.Dispose();
                throw new ServerException($"Couldn't open an SSH session with {_target.Host}: {ex.Message}", ex);
            }
            _client = client;
            Log.Info($"SSH connected to {_target} ({ServerVersion})");
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var fp = e.FingerPrintSHA256;
        HostKeyFingerprint = fp;
        var known = _knownHosts.Get(_target.Host, _target.Port);
        if (known is null)
        {
            _knownHosts.Trust(_target.Host, _target.Port, fp);
            Log.Info($"Trusted new host key for {_target.Host}: SHA256:{fp}");
            e.CanTrust = true;
        }
        else if (known == fp)
        {
            e.CanTrust = true;
        }
        else
        {
            _pendingMismatch = fp;
            e.CanTrust = false;
        }
    }

    /// <summary>Accept a changed host key after the user confirmed it.</summary>
    public void TrustHostKey(string fingerprint) => _knownHosts.Trust(_target.Host, _target.Port, fingerprint);

    private ConnectionInfo BuildConnectionInfo()
    {
        var methods = new List<AuthenticationMethod>();
        if (_profile.AuthMethod == "key")
        {
            var path = Environment.ExpandEnvironmentVariables(_profile.KeyPath ?? "");
            if (!File.Exists(path)) throw new ServerException($"SSH key not found: {path}");
            var passphrase = Secrets.Unprotect(_profile.ProtectedKeyPassphrase);
            try
            {
                methods.Add(new PrivateKeyAuthenticationMethod(_target.User!, new PrivateKeyFile(path, passphrase)));
            }
            catch (Exception ex) when (ex is SshException or InvalidOperationException or ArgumentException)
            {
                throw new ServerException($"Couldn't read the SSH key ({ex.Message}). Is the passphrase right?", ex);
            }
        }
        else
        {
            if (string.IsNullOrEmpty(_password)) throw new ServerException("Enter the password for the server in Settings.");
            methods.Add(new PasswordAuthenticationMethod(_target.User!, _password));
            var keyboard = new KeyboardInteractiveAuthenticationMethod(_target.User!);
            keyboard.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts) prompt.Response = _password;
            };
            methods.Add(keyboard);
        }

        return new ConnectionInfo(_target.Host, _target.Port, _target.User!, [.. methods])
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
    }

    private async Task EnsureReachableAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        try
        {
            await tcp.ConnectAsync(_target.Host, _target.Port, timeout.Token);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.HostNotFound || ex.SocketErrorCode == SocketError.NoData)
        {
            throw new ServerException($"Can't find \"{_target.Host}\". If it's a Tailscale name, check that Tailscale is connected.", ex);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new ServerException($"Can't reach {_target.Host}:{_target.Port}. Is the server on and are you on the same tailnet?", ex);
        }
    }

    public async Task<CommandResult> RunAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        await ConnectAsync(ct);
        var sw = Stopwatch.StartNew();
        using var cmd = _client!.CreateCommand(command, Encoding.UTF8);
        cmd.CommandTimeout = timeout;
        try
        {
            await cmd.ExecuteAsync(ct);
        }
        catch (SshOperationTimeoutException ex)
        {
            throw new ServerException($"The server didn't answer within {timeout.TotalSeconds:0} seconds.", ex);
        }
        catch (SshConnectionException ex)
        {
            _client?.Dispose();
            _client = null;
            throw new ServerException("Lost the connection to the server: " + ex.Message, ex);
        }
        return new CommandResult(cmd.ExitStatus ?? -1, cmd.Result, cmd.Error, sw.Elapsed);
    }

    /// <summary>
    /// Runs a bash script without any quoting problems: the script is base64-encoded and piped into a login shell,
    /// so it works whatever the user's login shell is (bash, zsh, fish).
    /// </summary>
    public Task<CommandResult> RunScriptAsync(string bashScript, TimeSpan timeout, CancellationToken ct)
    {
        // Wrap in a function so commands reading stdin can't swallow the rest of the script.
        var wrapped = "__tilesmith_main() {\n" + bashScript + "\n}\n__tilesmith_main </dev/null\n";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(wrapped));
        return RunAsync($"printf '%s' '{b64}' | base64 -d | bash -l", timeout, ct);
    }

    /// <summary>Forwards a free local port to host:port as seen from the server. Returns the local port.</summary>
    public async Task<int> OpenTunnelAsync(string remoteHost, int remotePort, CancellationToken ct)
    {
        await ConnectAsync(ct);
        var key = $"{remoteHost}:{remotePort}";
        lock (_tunnels)
        {
            if (_tunnels.TryGetValue(key, out var existing) && existing.IsStarted) return (int)existing.BoundPort;
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var forward = new ForwardedPortLocal("127.0.0.1", (uint)localPort, remoteHost, (uint)remotePort);
        _client!.AddForwardedPort(forward);
        forward.Start();
        lock (_tunnels) _tunnels[key] = forward;
        Log.Info($"Tunnel 127.0.0.1:{localPort} -> {key}");
        return localPort;
    }

    public void Dispose()
    {
        lock (_tunnels)
        {
            foreach (var t in _tunnels.Values)
            {
                try { t.Stop(); } catch { }
                t.Dispose();
            }
            _tunnels.Clear();
        }
        try { _client?.Disconnect(); } catch { }
        _client?.Dispose();
        _client = null;
    }
}
