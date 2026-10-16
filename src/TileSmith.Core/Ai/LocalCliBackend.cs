using System.Diagnostics;
using System.Text;

namespace TileSmith.Core.Ai;

public enum LocalCli { Claude, Cursor }

public sealed class AiLoginRequiredException(LocalCli cli, string message) : Exception(message)
{
    public LocalCli Cli { get; } = cli;
}

/// <summary>
/// Uses the official Claude Code / Cursor Agent CLIs installed on this PC, signed in with the user's own
/// subscription. TileSmith never handles those credentials: the CLIs do their own OAuth sign-in.
/// The prompt goes through stdin (no command-line length limits) and the CLI's own tools are disabled —
/// TileSmith only needs text back.
/// </summary>
public sealed record CliAccount(bool Installed, bool SignedIn, string Summary);

public sealed class LocalCliBackend(LocalCli cli, string? model, TimeSpan timeout, string? effort = null) : IAiBackend
{
    public string DisplayName => string.IsNullOrWhiteSpace(model)
        ? cli == LocalCli.Claude ? "Claude Code" : "Cursor"
        : ProviderCatalog.ModelLabel(ProviderCatalog.Get(cli == LocalCli.Claude ? "claude" : "cursor"), model) + (cli == LocalCli.Claude ? "" : " · Cursor");

    private string Product => cli == LocalCli.Claude ? "Claude Code" : "Cursor";

    public static string WorkspaceDir => Path.Combine(AppPaths.DataDir, "agent-workspace");

    public async Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        var exe = Locate(cli) ?? throw new AiException(cli == LocalCli.Claude
            ? "Claude Code isn't installed. Install it from claude.com/code, then sign in."
            : "Cursor Agent CLI isn't installed. Install it from cursor.com/cli, then sign in.", canFallback: true);
        Directory.CreateDirectory(WorkspaceDir);

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = WorkspaceDir,
        };
        foreach (var arg in Arguments()) psi.ArgumentList.Add(arg);
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["NO_OPEN_BROWSER"] = "1";

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new AiException($"Couldn't start {exe}.");
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteAsync(Transcript.Render(messages).AsMemory(), ct);
        process.StandardInput.Close();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            throw new AiException($"{Product} didn't answer within {timeout.TotalSeconds:0} seconds.", canFallback: true);
        }

        var output = JsonExtractor.StripAnsi(await stdout);
        var errors = JsonExtractor.StripAnsi(await stderr);
        Log.Info($"{DisplayName} replied in {sw.Elapsed.TotalSeconds:0.0}s (exit {process.ExitCode})");
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            var message = (errors + "\n" + output).Trim();
            if (LooksLikeAuthProblem(message))
                throw new AiLoginRequiredException(cli, $"{Product} needs you to sign in again. ({FirstLine(message)})");
            throw new AiException($"{Product} failed: {FirstLine(message)}", canFallback: LooksLikeLimit(message));
        }
        return output;
    }

    private IEnumerable<string> Arguments()
    {
        if (cli == LocalCli.Claude)
        {
            yield return "-p";
            yield return "--output-format";
            yield return "text";
            yield return "--tools";
            yield return "";
            yield return "--no-session-persistence";
            yield return "--strict-mcp-config";
        }
        else
        {
            yield return "-p";
            yield return "--output-format";
            yield return "text";
            yield return "--mode";
            yield return "ask";
            yield return "--trust";
            yield return "--workspace";
            yield return WorkspaceDir;
        }
        if (!string.IsNullOrWhiteSpace(model))
        {
            yield return "--model";
            yield return model!;
        }
        if (cli == LocalCli.Claude && !string.IsNullOrWhiteSpace(effort))
        {
            yield return "--effort";
            yield return effort!;
        }
    }

    private static bool LooksLikeLimit(string text) =>
        text.Contains("limit", StringComparison.OrdinalIgnoreCase) || text.Contains("quota", StringComparison.OrdinalIgnoreCase)
        || text.Contains("overloaded", StringComparison.OrdinalIgnoreCase) || text.Contains("network", StringComparison.OrdinalIgnoreCase)
        || text.Contains("ECONN", StringComparison.Ordinal) || text.Contains("timed out", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeAuthProblem(string text) =>
        text.Contains("authenticat", StringComparison.OrdinalIgnoreCase)
        || text.Contains("login", StringComparison.OrdinalIgnoreCase)
        || text.Contains("log in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("sign in", StringComparison.OrdinalIgnoreCase)
        || text.Contains("OAuth", StringComparison.OrdinalIgnoreCase)
        || text.Contains("unpaid invoice", StringComparison.OrdinalIgnoreCase)
        || text.Contains("API key", StringComparison.OrdinalIgnoreCase);

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.Length > 3) ?? text;

    private static readonly Dictionary<LocalCli, (string? Path, DateTime At)> Located = [];

    /// <summary>Finds the CLI: PATH first, then the default install locations (remembered for a few seconds).</summary>
    public static string? Locate(LocalCli cli)
    {
        lock (Located)
        {
            if (Located.TryGetValue(cli, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromSeconds(10)) return hit.Path;
        }
        var path = LocateCore(cli);
        lock (Located) Located[cli] = (path, DateTime.UtcNow);
        return path;
    }

    private static string? LocateCore(LocalCli cli)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string[] names = cli == LocalCli.Claude ? ["claude.exe", "claude.cmd"] : ["cursor-agent.cmd", "agent.cmd", "cursor-agent.exe"];
        string[] extra = cli == LocalCli.Claude
            ? [Path.Combine(home, ".local", "bin", "claude.exe"), Path.Combine(roaming, "npm", "claude.cmd"), Path.Combine(local, "Programs", "claude", "claude.exe")]
            : [Path.Combine(local, "cursor-agent", "cursor-agent.cmd"), Path.Combine(local, "cursor-agent", "agent.cmd")];

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        return extra.FirstOrDefault(File.Exists);
    }

    /// <summary>Opens a terminal running the CLI's own sign-in flow (browser OAuth handled by the CLI).</summary>
    public static void StartLogin(LocalCli cli)
    {
        var exe = Locate(cli) ?? throw new AiException("The CLI isn't installed.");
        var args = cli == LocalCli.Claude ? "auth login" : "login";
        Process.Start(new ProcessStartInfo("cmd.exe", $"/k \"\"{exe}\" {args}\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        })?.Dispose();
    }

    /// <summary>Signed in or not, in words for the settings page (no tokens spent).</summary>
    public static async Task<CliAccount> GetAccountStatusAsync(LocalCli cli, CancellationToken ct)
    {
        string? output;
        try
        {
            output = await GetAccountAsync(cli, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new CliAccount(true, false, "Couldn't check the sign-in: " + ex.Message);
        }
        if (output is null) return new CliAccount(false, false, "Not installed on this PC");
        return ParseAccount(cli, output);
    }

    internal static CliAccount ParseAccount(LocalCli cli, string output)
    {
        if (cli == LocalCli.Claude && output.TrimStart().StartsWith('{'))
        {
            try
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(output);
                var signedIn = json?["loggedIn"]?.GetValue<bool>() == true;
                if (!signedIn) return new CliAccount(true, false, "Not signed in");
                var plan = json?["subscriptionType"]?.ToString();
                var email = json?["email"]?.ToString();
                var planText = string.IsNullOrWhiteSpace(plan) ? null : $"Claude {char.ToUpperInvariant(plan[0])}{plan[1..]}";
                var parts = new[] { email, planText }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
                return new CliAccount(true, true, "Signed in" + (parts.Count > 0 ? " · " + string.Join(" · ", parts) : ""));
            }
            catch (System.Text.Json.JsonException) { }
        }
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        line = line.TrimStart('✓', '✔', ' ');
        var loggedIn = (line.Contains("logged in", StringComparison.OrdinalIgnoreCase) || line.Contains("signed in", StringComparison.OrdinalIgnoreCase))
                       && !line.Contains("not logged in", StringComparison.OrdinalIgnoreCase) && !line.Contains("not signed in", StringComparison.OrdinalIgnoreCase);
        return new CliAccount(true, loggedIn, loggedIn ? line.Replace("Logged in as", "Signed in as", StringComparison.OrdinalIgnoreCase) : "Not signed in");
    }

    /// <summary>The models the Cursor Agent CLI offers (<c>--list-models</c>).</summary>
    public static async Task<IReadOnlyList<ModelInfo>> ListCursorModelsAsync(CancellationToken ct)
    {
        var exe = Locate(LocalCli.Cursor) ?? throw new AiException("Cursor Agent CLI isn't installed.");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("--list-models");
        psi.Environment["NO_COLOR"] = "1";
        using var p = Process.Start(psi) ?? throw new AiException("Couldn't start the Cursor Agent CLI.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(40));
        var output = await p.StandardOutput.ReadToEndAsync(cts.Token);
        await p.WaitForExitAsync(cts.Token);
        return ParseCursorModels(output);
    }

    internal static IReadOnlyList<ModelInfo> ParseCursorModels(string output)
    {
        var list = new List<ModelInfo>();
        foreach (var raw in JsonExtractor.StripAnsi(output).Split('\n'))
        {
            var line = new string(raw.Where(ch => ch is not ('​' or '‌' or '‍' or '﻿')).ToArray()).Trim();
            var dash = line.IndexOf(" - ", StringComparison.Ordinal);
            if (dash <= 0) continue;
            var id = line[..dash].Trim();
            if (id.Contains(' ')) continue;
            var name = System.Text.RegularExpressions.Regex.Replace(line[(dash + 3)..].Trim(), @"\s{2,}", " ");
            string? detail = null;
            if (name.EndsWith("(default)", StringComparison.OrdinalIgnoreCase)) { name = name[..^9].Trim(); detail = "Default"; }
            list.Add(new ModelInfo(id, name, detail));
        }
        return list;
    }

    /// <summary>Raw sign-in status output, or null when the CLI isn't installed.</summary>
    public static async Task<string?> GetAccountAsync(LocalCli cli, CancellationToken ct)
    {
        var exe = Locate(cli);
        if (exe is null) return null;
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (cli == LocalCli.Claude) { psi.ArgumentList.Add("auth"); psi.ArgumentList.Add("status"); }
        else psi.ArgumentList.Add("status");
        psi.Environment["NO_COLOR"] = "1";
        using var p = Process.Start(psi);
        if (p is null) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var output = await p.StandardOutput.ReadToEndAsync(cts.Token) + await p.StandardError.ReadToEndAsync(cts.Token);
        await p.WaitForExitAsync(cts.Token);
        return JsonExtractor.StripAnsi(output).Trim();
    }
}
