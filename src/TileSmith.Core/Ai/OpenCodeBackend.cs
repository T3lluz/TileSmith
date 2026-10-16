using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TileSmith.Core.Remote;

namespace TileSmith.Core.Ai;

/// <summary>
/// OpenCode (opencode.ai): its free models — and OpenCode Zen when signed in — through the official <c>opencode</c>
/// CLI, on this PC or on the user's server. The free tier only answers requests made by OpenCode itself, so TileSmith
/// runs <c>opencode run</c> rather than calling the API. OpenCode's own tools stay listed (the free tier expects them)
/// but every permission is "ask", which a non-interactive run rejects, so the model can only answer in text.
/// </summary>
public sealed class OpenCodeBackend(string? model, TimeSpan timeout, ServerConnection? server) : IAiBackend
{
    public const string Config = """{"permission":{"*":"ask"}}""";
    public const string ModelsUrl = "https://opencode.ai/inference/v1/models";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private string Model => string.IsNullOrWhiteSpace(model) ? "opencode/big-pickle" : model!.Contains('/') ? model! : "opencode/" + model;

    public string DisplayName => $"{ProviderCatalog.ModelLabel(ProviderCatalog.Get("opencode"), Model[(Model.IndexOf('/') + 1)..])} · OpenCode";

    public async Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        var prompt = Transcript.Render(messages) + "\n(OpenCode's own tools are switched off here: answer only with the JSON.)\n";
        var output = server is null ? await RunLocalAsync(prompt, ct) : await RunOnServerAsync(prompt, ct);
        return ParseEvents(output);
    }

    private async Task<string> RunLocalAsync(string prompt, CancellationToken ct)
    {
        var exe = LocateLocal() ?? throw new AiException("OpenCode isn't installed on this PC. Install it from opencode.ai (free), or run it on your server.", canFallback: true);
        var workspace = Path.Combine(AppPaths.DataDir, "agent-workspace");
        Directory.CreateDirectory(workspace);
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
            WorkingDirectory = workspace,
        };
        foreach (var arg in new[] { "run", "-m", Model, "--format", "json" }) psi.ArgumentList.Add(arg);
        psi.Environment["OPENCODE_CONFIG_CONTENT"] = Config;
        psi.Environment["NO_COLOR"] = "1";

        var sw = Stopwatch.StartNew();
        using var process = Process.Start(psi) ?? throw new AiException($"Couldn't start {exe}.", canFallback: true);
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteAsync(prompt.AsMemory(), ct);
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
            throw new AiException($"OpenCode didn't answer within {timeout.TotalSeconds:0} seconds.", canFallback: true);
        }
        var output = await stdout;
        Log.Info($"OpenCode ({Model}) replied in {sw.Elapsed.TotalSeconds:0.0}s (exit {process.ExitCode})");
        if (string.IsNullOrWhiteSpace(output))
            throw new AiException("OpenCode failed: " + FirstLine(JsonExtractor.StripAnsi(await stderr)), canFallback: true);
        return output;
    }

    private Task<string> RunOnServerAsync(string prompt, CancellationToken ct)
    {
        // an empty folder as the working directory, so OpenCode doesn't pick up a project's instructions
        var invoke = $"w=\"$d/opencode\"; mkdir -p \"$w\"; cd \"$w\" && OPENCODE_CONFIG_CONTENT={Shell.Quote(Config)} opencode run -m {Shell.Quote(Model)} --format json < \"$f\"";
        return RemotePrompt.RunAsync(server!, prompt, invoke, timeout, "OpenCode", ct);
    }

    /// <summary>Joins the text parts of <c>opencode run --format json</c> events; errors become exceptions.</summary>
    internal static string ParseEvents(string output)
    {
        var text = new StringBuilder();
        var toolTried = false;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('{')) continue;
            JsonNode? e;
            try { e = JsonNode.Parse(line); }
            catch (JsonException) { continue; }
            switch (e?["type"]?.ToString())
            {
                case "text":
                    text.Append(e["part"]?["text"]?.ToString());
                    break;
                case "tool_use":
                    toolTried = true;
                    break;
                case "error":
                    var message = e["error"]?["data"]?["message"]?.ToString() ?? e["error"]?["name"]?.ToString() ?? "unknown error";
                    var auth = message.Contains("auth", StringComparison.OrdinalIgnoreCase) || message.Contains("API key", StringComparison.OrdinalIgnoreCase)
                               || message.Contains("sign in", StringComparison.OrdinalIgnoreCase) || message.Contains("credit", StringComparison.OrdinalIgnoreCase);
                    throw new AiException("OpenCode: " + message + (auth ? " Free models work without an account; paid ones need `opencode auth login`." : ""), canFallback: true);
            }
        }
        if (text.Length > 0) return text.ToString();
        return toolTried ? "(OpenCode tried to use its own tools instead of replying.)" : throw new AiException("OpenCode returned nothing.", canFallback: true);
    }

    /// <summary>The opencode CLI on this PC: PATH, then the installer's and npm's usual places.</summary>
    public static string? LocateLocal()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] names = ["opencode.exe", "opencode.cmd"];
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
        string[] extra =
        [
            Path.Combine(home, ".opencode", "bin", "opencode.exe"),
            Path.Combine(roaming, "npm", "opencode.cmd"),
            Path.Combine(home, "scoop", "shims", "opencode.exe"),
            Path.Combine(local, "Microsoft", "WinGet", "Links", "opencode.exe"),
            Path.Combine(home, ".bun", "bin", "opencode.exe"),
        ];
        return extra.FirstOrDefault(File.Exists);
    }

    /// <summary>OpenCode's current models (public list), free ones first, named and dated from models.dev.</summary>
    public static async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct)
    {
        var json = await Http.GetStringAsync(ModelsUrl, ct);
        var ids = OpenAiBackend.ParseModelList(json).Select(m => m.Id).ToList();
        var models = ids.Select(id =>
        {
            var known = ModelDirectory.Find("opencode", id);
            var free = known?.Free == true || id.EndsWith("-free", StringComparison.OrdinalIgnoreCase) || id == "big-pickle";
            var info = known is not null ? ModelDirectory.ToModelInfo(known, "opencode") : new ModelInfo(id, id, free ? "Free" : null) { Brand = Brands.ForModel(id, "opencode") };
            return info with { Free = free, Detail = free && !(info.Detail ?? "").Contains("Free", StringComparison.Ordinal) ? "Free · " + info.Detail : info.Detail };
        });
        return models.OrderByDescending(m => m.Free).ThenByDescending(m => m.Released ?? "").ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => l.Length > 3) ?? "no output";
}
