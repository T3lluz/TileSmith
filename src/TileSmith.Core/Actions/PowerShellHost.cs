using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace TileSmith.Core.Actions;

/// <summary>
/// Long-lived PowerShell processes for widget queries. Starting powershell.exe costs ~0.5 s and a lot of CPU, which
/// made tiles that refresh every few seconds slow and flaky; two warm hosts answer in milliseconds instead. Each
/// script runs in its own scope, its errors come back separately from its output, and anything a host can't handle
/// (a script that calls exit, a hang) restarts the host and falls back to a one-off process — queries only read, so
/// running one again is harmless. Actions with side effects never come here.
/// </summary>
public sealed class PowerShellHost : IDisposable
{
    private const string Marker = "\u0001TS:";

    // the loop runs inside the host: one base64 script per line in, one marked base64 JSON reply per line out
    private const string Loop = """
        $ProgressPreference = 'SilentlyContinue'
        $ErrorActionPreference = 'Continue'
        $utf8 = New-Object System.Text.UTF8Encoding($false)
        [Console]::OutputEncoding = $utf8
        while ($null -ne ($line = [Console]::In.ReadLine())) {
          $errs = New-Object System.Collections.Generic.List[string]
          $out = ''
          $failed = $false
          try {
            $code = $utf8.GetString([Convert]::FromBase64String($line))
            $sb = [ScriptBlock]::Create($code)
            $out = (& $sb 2>&1 | ForEach-Object {
              if ($_ -is [System.Management.Automation.ErrorRecord]) { $errs.Add($_.ToString()) } else { $_ }
            } | Out-String -Width 4096)
          } catch {
            $failed = $true
            $errs.Add($_.Exception.Message)
          }
          $reply = @{ ok = (-not $failed); out = "$out"; err = ($errs -join "`n") } | ConvertTo-Json -Compress
          [Console]::Out.WriteLine([string][char]1 + 'TS:' + [Convert]::ToBase64String($utf8.GetBytes($reply)))
          [Console]::Out.Flush()
        }
        """;

    private static readonly PowerShellHost[] Pool = [new(), new()];
    private static readonly ConcurrentDictionary<string, bool> NeedsOwnProcess = new();
    private static int _next;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;

    /// <summary>
    /// Runs a read-only script and returns its trimmed output, like <see cref="PowerShellRunner.RunAsync"/> but warm.
    /// </summary>
    public static async Task<string?> QueryAsync(string script, int timeoutSeconds, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows() || NeedsOwnProcess.ContainsKey(script))
            return await PowerShellRunner.RunAsync(script, admin: false, timeoutSeconds, ct);
        // prefer an idle host; otherwise queue on the next one
        var host = Pool.FirstOrDefault(h => h._gate.CurrentCount > 0) ?? Pool[Interlocked.Increment(ref _next) % Pool.Length];
        try
        {
            return await host.RunAsync(script, TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 120)), ct);
        }
        catch (HostLostException)
        {
            // e.g. the script calls exit: from now on it gets a process of its own
            NeedsOwnProcess[script] = true;
            return await PowerShellRunner.RunAsync(script, admin: false, timeoutSeconds, ct);
        }
    }

    public static void ShutdownAll()
    {
        foreach (var host in Pool) host.Dispose();
    }

    private sealed class HostLostException : Exception;

    private async Task<string?> RunAsync(string script, TimeSpan timeout, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var process = EnsureStarted();
            var line = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
            await process.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var hostOutput = new StringBuilder();
            while (true)
            {
                string? read;
                try
                {
                    read = await process.StandardOutput.ReadLineAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Kill();
                    if (ct.IsCancellationRequested) throw;
                    throw new ActionException($"Timed out after {timeout.TotalSeconds:0} s.");
                }
                if (read is null)
                {
                    Kill();
                    throw new HostLostException();
                }
                if (!read.StartsWith(Marker, StringComparison.Ordinal))
                {
                    // Write-Host and friends print straight to the console: that's output too
                    hostOutput.AppendLine(read);
                    continue;
                }
                var reply = JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(read[Marker.Length..])));
                var ok = reply?["ok"]?.GetValue<bool>() ?? false;
                var output = (hostOutput + (reply?["out"]?.ToString() ?? "")).Trim();
                var errors = Clean(reply?["err"]?.ToString());
                if (!ok || (errors is not null && output.Length == 0)) throw new ActionException(errors ?? "PowerShell failed.");
                return output;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private Process EnsureStarted()
    {
        if (_process is { HasExited: false } running) return running;
        _process?.Dispose();
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Loop));
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded }) psi.ArgumentList.Add(arg);
        _process = Process.Start(psi) ?? throw new ActionException("Couldn't start PowerShell.");
        // stderr isn't part of the protocol; keep draining it so the pipe never fills up
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginErrorReadLine();
        return _process;
    }

    private static string? Clean(string? errors)
    {
        if (string.IsNullOrWhiteSpace(errors)) return null;
        var lines = errors.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(4);
        return string.Join(" ", lines);
    }

    private void Kill()
    {
        try { _process?.Kill(entireProcessTree: true); } catch { }
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Kill();
}
