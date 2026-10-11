using System.Text;
using TileSmith.Core.Remote;
using TileSmith.Core.Settings;

namespace TileSmith.Core.Ai;

public sealed record DetectedEngines(
    string? Os,
    string? HermesPath,
    string? HermesCommand,
    string? HermesVersion,
    bool HermesQueryFile,
    string? HermesQuietFlag,
    bool HermesApi,
    bool OllamaApi,
    IReadOnlyList<string> OllamaModels,
    IReadOnlyList<string> OtherClis)
{
    public string? Recommended => HermesCommand is not null ? "hermes" : OllamaApi ? "openai" : null;

    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (HermesCommand is not null)
            {
                var name = HermesVersion is { } v && v.StartsWith("Hermes", StringComparison.OrdinalIgnoreCase) ? v : $"Hermes Agent {HermesVersion}".Trim();
                parts.Add(name + (HermesCommand.StartsWith("docker", StringComparison.Ordinal) ? " (Docker)" : ""));
            }
            if (HermesApi) parts.Add("Hermes API server (:8642)");
            if (OllamaApi) parts.Add($"Ollama ({OllamaModels.Count} models)");
            parts.AddRange(OtherClis);
            return parts.Count == 0 ? "No AI engines found on the server." : "Found: " + string.Join(", ", parts) + ".";
        }
    }
}

/// <summary>Looks for AI engines on the server in one SSH round trip.</summary>
public static class EngineDetector
{
    private const string Script = """
        echo "os=$(. /etc/os-release 2>/dev/null; echo "$PRETTY_NAME")"
        hp=$(command -v hermes 2>/dev/null)
        echo "hermes_path=$hp"
        hcmd=""
        # a native install that actually starts (stale launchers from old installs are common)…
        if [ -n "$hp" ] && timeout 20 hermes --version >/dev/null 2>&1; then hcmd="hermes"; fi
        # …otherwise the official Docker image (nousresearch/hermes-agent)
        if [ -z "$hcmd" ] && command -v docker >/dev/null 2>&1; then
          dc=$(docker ps --format '{{.Names}} {{.Image}}' 2>/dev/null | grep -i 'hermes-agent' | head -n1 | cut -d' ' -f1)
          [ -n "$dc" ] && hcmd="docker exec -i $dc hermes"
        fi
        echo "hermes_cmd=$hcmd"
        if [ -n "$hcmd" ]; then
          echo "hermes_version=$(timeout 30 $hcmd --version 2>/dev/null | head -n1)"
          echo "hermes_help=$(timeout 30 $hcmd chat --help 2>&1 | base64 -w0)"
        fi
        if command -v curl >/dev/null 2>&1; then
          curl -s -m 2 -o /dev/null -w "hermes_api=%{http_code}\n" http://127.0.0.1:8642/health
          echo "ollama_tags=$(curl -s -m 2 http://127.0.0.1:11434/api/tags | base64 -w0)"
        fi
        for c in claude codex opencode llm gemini cursor-agent ollama; do
          p=$(command -v $c 2>/dev/null); [ -n "$p" ] && echo "cli_$c=$p"
        done
        true
        """;

    public static async Task<DetectedEngines> DetectAsync(ServerConnection server, CancellationToken ct)
    {
        var result = await server.RunScriptAsync(RemotePrompt.PathSetup + Script, TimeSpan.FromSeconds(60), ct);
        var map = new Dictionary<string, string>();
        foreach (var line in result.Stdout.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq > 0) map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }

        string? Get(string key) => map.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        string Decode(string? b64)
        {
            if (b64 is null) return "";
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(b64)); }
            catch (FormatException) { return ""; }
        }

        var help = Decode(Get("hermes_help"));
        var quiet = help.Contains("-Q", StringComparison.Ordinal) ? "-Q" : help.Contains("--quiet", StringComparison.Ordinal) ? "--quiet" : null;
        var queryFile = help.Contains("--query-file", StringComparison.Ordinal);

        var models = new List<string>();
        var tags = Decode(Get("ollama_tags"));
        if (tags.Contains("\"models\"", StringComparison.Ordinal))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(tags);
                models.AddRange(doc.RootElement.GetProperty("models").EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString()).OfType<string>());
            }
            catch { }
        }

        var others = map.Where(kv => kv.Key.StartsWith("cli_", StringComparison.Ordinal) && kv.Key != "cli_ollama")
            .Select(kv => kv.Key[4..]).ToList();

        return new DetectedEngines(
            Get("os"),
            Get("hermes_path"),
            Get("hermes_cmd"),
            Get("hermes_version"),
            queryFile,
            quiet,
            Get("hermes_api") is "200" or "401",
            tags.Contains("\"models\"", StringComparison.Ordinal),
            models,
            others);
    }

    /// <summary>Stores what was found so "Automatic" knows what to use.</summary>
    public static void Apply(DetectedEngines found, AiSettings ai)
    {
        ai.DetectedEngine = found.Recommended;
        ai.DetectedSummary = found.Summary;
        ai.HermesQuietFlag = found.HermesQuietFlag;
        ai.HermesSupportsQueryFile = found.HermesQueryFile;
        // keep a command the user typed themselves; otherwise use what actually works on the server
        if (found.HermesCommand is not null && (string.IsNullOrWhiteSpace(ai.HermesCommand) || ai.HermesCommand == "hermes" || ai.HermesCommand.StartsWith("docker exec", StringComparison.Ordinal)))
            ai.HermesCommand = found.HermesCommand;
        if (found.Recommended == "openai" && found.OllamaApi && ai.OpenAiBaseUrl.Contains(":8642", StringComparison.Ordinal))
        {
            ai.OpenAiBaseUrl = "http://127.0.0.1:11434/v1";
            ai.OpenAiModel ??= found.OllamaModels.FirstOrDefault();
        }
    }
}

/// <summary>Turns settings into a backend.</summary>
public static class AiEngineFactory
{
    public static readonly (string Id, string Name, string Description)[] Engines =
    [
        ("auto", "Automatic", "Uses what was found on your server, otherwise your Claude or Cursor subscription"),
        ("hermes", "Hermes Agent (server)", "Runs `hermes chat` on your server over SSH"),
        ("openai", "OpenAI-compatible API", "Hermes API server, Ollama, vLLM, LM Studio, OpenRouter, Anthropic…"),
        ("command", "Custom command (server)", "Any CLI on the server that reads a prompt"),
        ("claude", "Claude (your subscription)", "Uses Claude Code on this PC, signed in with your Claude plan"),
        ("cursor", "Cursor (your subscription)", "Uses the Cursor Agent CLI on this PC, signed in with your Cursor plan"),
    ];

    public static string ResolveEngine(AppSettings settings)
    {
        var engine = settings.Ai.Engine;
        if (engine != "auto") return engine;
        if (settings.Server.IsConfigured && settings.Ai.DetectedEngine is { } detected) return detected;
        if (settings.Server.IsConfigured && settings.Ai.DetectedEngine is null) return "hermes";
        if (LocalCliBackend.Locate(LocalCli.Claude) is not null) return "claude";
        if (LocalCliBackend.Locate(LocalCli.Cursor) is not null) return "cursor";
        return "hermes";
    }

    public static bool NeedsServer(string engine, AiSettings ai) =>
        engine is "hermes" or "command" || (engine == "openai" && ai.OpenAiViaTunnel);

    public static IAiBackend Create(AppSettings settings, Func<ServerConnection> server, string? modelOverride = null)
    {
        var ai = settings.Ai;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(ai.TimeoutSeconds, 30, 1800));
        var engine = ResolveEngine(settings);
        return engine switch
        {
            "hermes" => new HermesCliBackend(server(), string.IsNullOrWhiteSpace(ai.HermesCommand) ? "hermes" : ai.HermesCommand,
                ai.HermesArgs, ai.HermesQuietFlag, ai.HermesSupportsQueryFile, timeout),
            "openai" => new OpenAiBackend(ai.OpenAiViaTunnel ? server() : null, ai.OpenAiBaseUrl, ai.OpenAiViaTunnel,
                modelOverride ?? ai.OpenAiModel, Secrets.Unprotect(ai.ProtectedOpenAiKey), timeout),
            "command" => new CommandBackend(server(), string.IsNullOrWhiteSpace(ai.CustomCommand)
                ? throw new AiException("Set the custom command in Settings › Server & AI.") : ai.CustomCommand!, timeout),
            "claude" => new LocalCliBackend(LocalCli.Claude, modelOverride ?? ai.LocalModel, timeout),
            "cursor" => new LocalCliBackend(LocalCli.Cursor, modelOverride ?? ai.LocalModel, timeout),
            _ => throw new AiException($"Unknown AI engine \"{engine}\"."),
        };
    }
}
