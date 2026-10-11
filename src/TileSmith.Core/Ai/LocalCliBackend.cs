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
/// TileSmith's agent loop provides the tools.
/// </summary>
public sealed class LocalCliBackend(LocalCli cli, string? model, TimeSpan timeout) : IAiBackend
{
    public string DisplayName => cli == LocalCli.Claude ? "Claude (your subscription)" : "Cursor (your subscription)";

    public static string WorkspaceDir => Path.Combine(AppPaths.DataDir, "agent-workspace");

    public async Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        var exe = Locate(cli) ?? throw new AiException(cli == LocalCli.Claude
            ? "Claude Code isn't installed. Install it from claude.com/code, then sign in."
            : "Cursor Agent CLI isn't installed. Install it from cursor.com/cli, then sign in.");
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
            throw new AiException($"{DisplayName} didn't answer within {timeout.TotalSeconds:0} seconds.");
        }

        var output = JsonExtractor.StripAnsi(await stdout);
        var errors = JsonExtractor.StripAnsi(await stderr);
        Log.Info($"{DisplayName} replied in {sw.Elapsed.TotalSeconds:0.0}s (exit {process.ExitCode})");
        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
        {
            var message = (errors + "\n" + output).Trim();
            if (LooksLikeAuthProblem(message))
                throw new AiLoginRequiredException(cli, $"{DisplayName.Split(' ')[0]} needs you to sign in again. ({FirstLine(message)})");
            throw new AiException($"{DisplayName} failed: {FirstLine(message)}");
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
    }

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

    /// <summary>Finds the CLI: PATH first, then the default install locations.</summary>
    public static string? Locate(LocalCli cli)
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

    /// <summary>Quick sign-in check without spending tokens.</summary>
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
