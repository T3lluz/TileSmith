using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using TileSmith.Core.Native;

namespace TileSmith.Core.Actions;

/// <summary>Things actions need from the app shell.</summary>
public interface IActionHost
{
    /// <summary>Runs a command on the connected SSH server and returns stdout.</summary>
    Task<string> RunOnServerAsync(string command, TimeSpan timeout, CancellationToken ct);

    void Notify(string title, string? message);

    void SetClipboardText(string text);

    /// <summary>Hide the panel and give focus back to the previous window before simulated input.</summary>
    Task PrepareForInputAsync();
}

/// <summary>Executes widget actions. Every handler returns the action's output (or null).</summary>
public sealed class ActionEngine
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private readonly IActionHost _host;
    private readonly Dictionary<string, Func<ActionCall, CancellationToken, Task<string?>>> _handlers;

    public ActionEngine(IActionHost host)
    {
        _host = host;
        _handlers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["display.setResolution"] = (c, _) => Sync(() => DisplayManager.SetResolution(c.GetString("display"), c.RequireInt("width"), c.RequireInt("height"), c.GetInt("refreshRate"))),
            ["display.getResolution"] = (c, _) => Value(() => DisplayManager.Resolve(c.GetString("display")).Current.Resolution),
            ["display.setRefreshRate"] = (c, _) => Sync(() => DisplayManager.SetRefreshRate(c.GetString("display"), c.RequireInt("hz"))),
            ["display.getRefreshRate"] = (c, _) => Value(() => DisplayManager.Resolve(c.GetString("display")).Current.RefreshRate.ToString(CultureInfo.InvariantCulture)),
            ["display.setBrightness"] = (c, _) => Sync(() => BrightnessControl.Set(c.RequireInt("level"))),
            ["display.getBrightness"] = (_, _) => Value(() => BrightnessControl.Get().ToString(CultureInfo.InvariantCulture)),
            ["display.project"] = (c, _) => Sync(() => Project(c.Require("mode"))),

            ["audio.setVolume"] = (c, _) => Sync(() => AudioEndpoint.SetVolume(c.RequireInt("level"))),
            ["audio.getVolume"] = (_, _) => Value(() => AudioEndpoint.GetVolume().ToString(CultureInfo.InvariantCulture)),
            ["audio.changeVolume"] = (c, _) => Sync(() => AudioEndpoint.SetVolume(AudioEndpoint.GetVolume() + c.RequireInt("delta"))),
            ["audio.setMute"] = (c, _) => Sync(() => AudioEndpoint.SetMute(Toggle(c.Require("mute"), () => AudioEndpoint.GetMute()))),
            ["audio.getMute"] = (_, _) => Value(() => Bool(AudioEndpoint.GetMute())),
            ["audio.setMicMute"] = (c, _) => Sync(() => AudioEndpoint.SetMute(Toggle(c.Require("mute"), () => AudioEndpoint.GetMute(true)), microphone: true)),
            ["audio.getMicMute"] = (_, _) => Value(() => Bool(AudioEndpoint.GetMute(true))),

            ["theme.set"] = (c, _) => Sync(() => ThemeControl.Set(c.Require("mode").ToLowerInvariant() switch
            {
                "dark" => true,
                "light" => false,
                _ => !ThemeControl.IsDark(),
            }, (c.GetString("scope") ?? "both").ToLowerInvariant())),
            ["theme.get"] = (_, _) => Value(() => ThemeControl.IsDark() ? "dark" : "light"),

            ["power.setMode"] = (c, _) => Sync(() => PowerControl.SetMode(c.Require("mode"))),
            ["power.getMode"] = (_, _) => Value(PowerControl.GetMode),
            ["power.setPlan"] = (c, _) => Sync(() => PowerControl.SetPlan(c.GetString("name"), c.GetString("guid"))),
            ["power.getPlan"] = (_, _) => Value(PowerControl.GetPlan),
            ["power.action"] = (c, _) => Sync(() => PowerControl.PerformAction(c.Require("action"))),
            ["battery.get"] = (c, _) => Value(() =>
            {
                var b = PowerControl.GetBattery();
                return (c.GetString("field") ?? "percent").Equals("status", StringComparison.OrdinalIgnoreCase)
                    ? b.Status
                    : b.Percent.ToString(CultureInfo.InvariantCulture);
            }),

            ["radio.set"] = async (c, _) => { await RadioControl.SetAsync(c.Require("kind"), c.Require("state")); return null; },
            ["radio.get"] = async (c, _) => await RadioControl.GetAsync(c.Require("kind")) ? "on" : "off",

            ["keys.send"] = async (c, _) => { await _host.PrepareForInputAsync(); KeyboardInput.SendShortcut(c.Require("keys")); return null; },
            ["text.type"] = async (c, _) => { await _host.PrepareForInputAsync(); KeyboardInput.TypeText(c.Require("text")); return null; },
            ["media.key"] = (c, _) => Sync(() => KeyboardInput.SendKey(c.Require("key").ToLowerInvariant() switch
            {
                "next" => (ushort)0xB0,
                "previous" or "prev" => (ushort)0xB1,
                "stop" => (ushort)0xB2,
                _ => (ushort)0xB3,
            })),
            ["clipboard.setText"] = (c, _) => Sync(() => _host.SetClipboardText(c.Require("text"))),

            ["app.launch"] = (c, _) => Sync(() => Launch(c.Require("path"), c.GetString("args"), c.GetString("workingDir"), c.GetBool("admin") == true)),
            ["app.close"] = (c, _) => Sync(() => CloseApp(c.Require("name"), c.GetBool("force") == true)),
            ["app.isRunning"] = (c, _) => Value(() => Bool(Process.GetProcessesByName(ProcessName(c.Require("name"))).Length > 0)),
            ["url.open"] = (c, _) => Sync(() => ShellOpen(c.Require("url"))),
            ["folder.open"] = (c, _) => Sync(() => ShellOpen(Environment.ExpandEnvironmentVariables(c.Require("path")))),

            ["shell.powershell"] = (c, ct) => c.ReadOnly && c.GetBool("admin") != true
                ? PowerShellHost.QueryAsync(c.Require("script"), c.GetInt("timeoutSeconds") ?? 30, ct)!
                : PowerShellRunner.RunAsync(c.Require("script"), c.GetBool("admin") == true, c.GetInt("timeoutSeconds") ?? 30, ct)!,
            ["shell.cmd"] = (c, ct) => RunCmdAsync(c.Require("command"), c.GetInt("timeoutSeconds") ?? 30, ct)!,
            ["server.run"] = async (c, ct) => (await _host.RunOnServerAsync(c.Require("command"), TimeSpan.FromSeconds(c.GetInt("timeoutSeconds") ?? 30), ct)).Trim(),
            ["http.request"] = HttpRequestAsync,

            ["system.stats"] = (c, _) => Value(() => SystemStats.Get(c.GetString("metric"), c.GetString("drive"))),
            ["media.nowPlaying"] = async (c, _) => await NowPlaying.GetAsync(c.GetString("field")),
            ["time.now"] = (c, _) => Value(() => Clock.Now(c.GetString("format"))),
            ["time.until"] = (c, _) => Value(() => Clock.Until(c.Require("when"))),
            ["network.ping"] = async (c, _) => await NetworkProbe.PingAsync(c.Require("host"), c.GetInt("timeoutMs") ?? 1500),
            ["weather.get"] = async (c, ct) => await Weather.GetAsync(c.Require("location"), c.GetString("units"), ct),
            ["network.info"] = async (_, ct) => await NetworkInfo.GetAsync(ct),
            ["system.top"] = async (c, ct) => await TopProcesses.GetAsync(c.GetString("by"), c.GetInt("count") ?? 5, ct),
            ["store.get"] = (c, _) => Value(() => LocalStore.Get(c.Require("key"), c.GetString("default"))),
            ["store.set"] = (c, _) => Value(() => LocalStore.Set(c.Require("key"), c.GetString("value") ?? "")),
            ["store.toggle"] = (c, _) => Value(() => LocalStore.Toggle(c.Require("key"))),
            ["store.add"] = (c, _) => Value(() => LocalStore.Add(c.Require("key"), c.GetNumber("delta") ?? 1, c.GetNumber("min"), c.GetNumber("max"))),

            ["notify"] = (c, _) => Sync(() => _host.Notify(c.Require("title"), c.GetString("message"))),
            ["sequence"] = async (c, ct) =>
            {
                string? last = null;
                foreach (var step in c.GetActions("steps")) last = await RunAsync(step, ct);
                return last;
            },
            ["delay"] = async (c, ct) => { await Task.Delay(Math.Clamp(c.RequireInt("ms"), 0, 10_000), ct); return null; },
        };

        var missing = ActionCatalog.All.Select(a => a.Type).Where(t => !_handlers.ContainsKey(t)).ToList();
        if (missing.Count > 0) throw new InvalidOperationException("Missing action handlers: " + string.Join(", ", missing));
    }

    public Task<string?> RunAsync(JsonObject action, string? value, CancellationToken ct = default) =>
        RunAsync(new ActionCall(action, value), ct);

    /// <summary>Runs a state or data query (read-only), on the warm PowerShell host when it's a script.</summary>
    public Task<string?> QueryAsync(JsonObject action, CancellationToken ct = default) =>
        RunAsync(new ActionCall(action, null) { ReadOnly = true }, ct);

    public async Task<string?> RunAsync(ActionCall call, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            // keep the UI thread free: some APIs (WMI, display mode changes, powercfg) block
            var result = await Task.Run(() => _handlers[call.Type](call, ct), ct);
            if (!call.Info.IsQuery) Log.Info($"Action {call.Type} ok in {sw.ElapsedMilliseconds} ms");
            result = result?.Trim();
            if (call.GetString("select") is { Length: > 0 } select && result is not null) result = Select(result, select, call.Type);
            return result;
        }
        catch (ActionException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"Action {call.Type} failed", ex);
            throw new ActionException($"{call.Info.Description.TrimEnd('.')} failed: {ex.Message}", ex);
        }
    }

    /// <summary>"select": pick one field out of JSON output ("cpu", "items[0].name").</summary>
    internal static string Select(string output, string path, string type)
    {
        if (!TileSmith.Core.Widgets.JsonExtractorLite.TryParse(output, out var root))
            throw new ActionException($"{type}: \"select\" needs JSON output, but got \"{(output.Length > 80 ? output[..80] + "…" : output)}\".");
        var node = TileSmith.Core.Widgets.Bindings.Navigate(root, path)
                   ?? throw new ActionException($"{type}: the output has no \"{path}\".");
        return node.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.String => node.GetValue<string>(),
            System.Text.Json.JsonValueKind.Null => "",
            _ => node.ToJsonString(),
        };
    }

    private static Task<string?> Sync(Action action)
    {
        action();
        return Task.FromResult<string?>(null);
    }

    private static Task<string?> Value(Func<string> func) => Task.FromResult<string?>(func());

    private static string Bool(bool b) => b ? "true" : "false";

    private static bool Toggle(string requested, Func<bool> current) => requested.ToLowerInvariant() switch
    {
        "true" or "on" or "1" or "yes" => true,
        "false" or "off" or "0" or "no" => false,
        _ => !current(),
    };

    private static void Project(string mode)
    {
        var arg = mode.ToLowerInvariant() switch
        {
            "pcscreenonly" or "internal" => "/internal",
            "duplicate" or "clone" => "/clone",
            "extend" => "/extend",
            "secondscreenonly" or "external" => "/external",
            _ => throw new ActionException($"Unknown projection mode \"{mode}\"."),
        };
        Process.Start(new ProcessStartInfo("DisplaySwitch.exe", arg) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }

    private static string ProcessName(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    private static void CloseApp(string name, bool force)
    {
        var processes = Process.GetProcessesByName(ProcessName(name));
        if (processes.Length == 0) throw new ActionException($"{name} isn't running.");
        foreach (var p in processes)
        {
            using (p)
            {
                try
                {
                    if (force || !p.CloseMainWindow()) p.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { /* already gone */ }
            }
        }
    }

    private static void Launch(string path, string? args, string? workingDir, bool admin)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path);
        var psi = new ProcessStartInfo(expanded)
        {
            UseShellExecute = true,
            Arguments = args ?? "",
            WorkingDirectory = workingDir is null ? "" : Environment.ExpandEnvironmentVariables(workingDir),
        };
        if (admin) psi.Verb = "runas";
        if (expanded.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            psi = new ProcessStartInfo("explorer.exe", expanded) { UseShellExecute = true };
        }
        try
        {
            Process.Start(psi)?.Dispose();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new ActionException("Cancelled at the administrator prompt.");
        }
        catch (Win32Exception ex)
        {
            throw new ActionException($"Couldn't start \"{path}\": {ex.Message}");
        }
    }

    private static void ShellOpen(string target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception ex)
        {
            throw new ActionException($"Couldn't open \"{target}\": {ex.Message}");
        }
    }

    private static async Task<string?> RunCmdAsync(string command, int timeoutSeconds, CancellationToken ct)
    {
        var result = await ProcessRunner.RunAsync("cmd.exe", ["/d", "/s", "/c", command], TimeSpan.FromSeconds(timeoutSeconds), ct);
        if (result.ExitCode != 0 && string.IsNullOrWhiteSpace(result.Stdout))
            throw new ActionException(FirstLines(result.Stderr) ?? $"Command exited with code {result.ExitCode}.");
        return result.Stdout.Trim();
    }

    private static async Task<string?> HttpRequestAsync(ActionCall c, CancellationToken ct)
    {
        var method = new HttpMethod((c.GetString("method") ?? "GET").ToUpperInvariant());
        using var request = new HttpRequestMessage(method, c.Require("url"));
        var body = c.GetString("body");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(c.GetString("contentType") ?? "application/json");
        }
        foreach (var (name, value) in c.GetMap("headers"))
        {
            if (!request.Headers.TryAddWithoutValidation(name, value)) request.Content?.Headers.TryAddWithoutValidation(name, value);
        }
        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new ActionException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {FirstLines(text) ?? ""}".Trim());
        return text.Length > 4000 ? text[..4000] : text.Trim();
    }

    internal static string? FirstLines(string? text, int lines = 3)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Take(lines);
        return string.Join(" ", parts);
    }
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string file, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new ActionException($"Couldn't start {file}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new ActionException($"Timed out after {timeout.TotalSeconds:0} s.");
        }
        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }
}

public static class PowerShellRunner
{
    private const string Prelude =
        "$ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[System.Text.Encoding]::UTF8; ";

    /// <summary>Runs a script and returns exit code, stdout and stderr without interpreting them.</summary>
    public static async Task<ProcessResult> RunRawAsync(string script, bool admin, int timeoutSeconds, CancellationToken ct)
    {
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 600));
        // already elevated (Run as administrator): no UAC prompt needed
        if (admin && !Elevation.IsElevated) return await RunElevatedAsync(script, timeout, ct);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Prelude + script));
        var result = await ProcessRunner.RunAsync("powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", encoded], timeout, ct);
        return result with { Stderr = CleanStderr(result.Stderr) ?? "" };
    }

    /// <summary>Widget flavour: returns trimmed stdout, throws a friendly error on failure.</summary>
    public static async Task<string?> RunAsync(string script, bool admin, int timeoutSeconds, CancellationToken ct)
    {
        var result = await RunRawAsync(script, admin, timeoutSeconds, ct);
        var stderr = string.IsNullOrWhiteSpace(result.Stderr) ? null : result.Stderr;
        if (result.ExitCode != 0 || (stderr is not null && string.IsNullOrWhiteSpace(result.Stdout)))
            throw new ActionException(stderr ?? $"PowerShell exited with code {result.ExitCode}.");
        return result.Stdout.Trim();
    }

    private static async Task<ProcessResult> RunElevatedAsync(string script, TimeSpan timeout, CancellationToken ct)
    {
        var outFile = Path.Combine(Path.GetTempPath(), $"tilesmith-{Guid.NewGuid():N}.txt");
        var wrapped = Prelude + $"$__code = 0; try {{ & {{ {script} }} *>&1 | Out-File -Encoding utf8 -FilePath '{outFile}'; if (-not $?) {{ $__code = 1 }} }} " +
                      $"catch {{ $_ | Out-File -Append -Encoding utf8 -FilePath '{outFile}'; $__code = 1 }}; exit $__code";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
        var psi = new ProcessStartInfo("powershell.exe", $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -EncodedCommand {encoded}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi) ?? throw new ActionException("Couldn't start PowerShell.");
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await p.WaitForExitAsync(cts.Token);
            var output = File.Exists(outFile) ? (await File.ReadAllTextAsync(outFile, ct)).Trim() : "";
            return p.ExitCode == 0 ? new ProcessResult(0, output, "") : new ProcessResult(p.ExitCode, "", output);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new ActionException("Cancelled at the administrator prompt.");
        }
        finally
        {
            try { File.Delete(outFile); } catch { }
        }
    }

    private static string? CleanStderr(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;
        // PowerShell CLIXML progress noise
        if (stderr.Contains("#< CLIXML", StringComparison.Ordinal) && !stderr.Contains("<S S=\"Error\">", StringComparison.Ordinal)) return null;
        var lines = stderr.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.StartsWith("+", StringComparison.Ordinal) && !l.StartsWith("At line:", StringComparison.Ordinal)
                        && !l.StartsWith("CategoryInfo", StringComparison.Ordinal) && !l.StartsWith("FullyQualifiedErrorId", StringComparison.Ordinal))
            .Take(6);
        var text = string.Join(" ", lines).Trim();
        return text.Length == 0 ? null : text;
    }
}
