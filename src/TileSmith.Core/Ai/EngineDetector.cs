using System.Text;
using System.Text.RegularExpressions;
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
    string? HermesModel,
    string? HermesBaseUrl,
    bool HermesApi,
    bool OllamaApi,
    IReadOnlyList<string> OllamaModels,
    IReadOnlyList<string> OtherClis)
{
    public string? Recommended => HermesCommand is not null ? "hermes" : OllamaApi ? "compatible" : null;

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
public static partial class EngineDetector
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
          echo "hermes_version=$(timeout 30 $hcmd --version </dev/null 2>/dev/null | head -n1)"
          echo "hermes_help=$(timeout 30 $hcmd chat --help </dev/null 2>&1 | base64 -w0)"
          echo "hermes_config=$(timeout 30 $hcmd config show </dev/null 2>/dev/null | grep -m1 -E '^[[:space:]]*Model:' | base64 -w0)"
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
        var result = await server.RunScriptAsync(RemotePrompt.PathSetup + Script, TimeSpan.FromSeconds(90), ct);
        if (!string.IsNullOrWhiteSpace(result.Stderr) && result.Stdout.Length == 0)
            Log.Warn("Engine detection printed only errors: " + result.Stderr.Trim());
        return Parse(result.Stdout);
    }

    internal static DetectedEngines Parse(string stdout)
    {
        var map = new Dictionary<string, string>();
        foreach (var line in stdout.Split('\n'))
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
        var (hermesModel, hermesBase) = ParseHermesModel(Decode(Get("hermes_config")));

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
            hermesModel,
            hermesBase,
            Get("hermes_api") is "200" or "401",
            tags.Contains("\"models\"", StringComparison.Ordinal),
            models,
            others);
    }

    /// <summary>
    /// Reads the "Model:" line of <c>hermes config show</c>: either a plain name or a Python dict like
    /// {'provider': 'custom', 'default': 'cursor:auto', 'base_url': 'http://172.30.0.1:8378/v1'}.
    /// </summary>
    internal static (string? Model, string? BaseUrl) ParseHermesModel(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return (null, null);
        var value = line.Trim();
        var colon = value.IndexOf(':');
        if (value.StartsWith("Model", StringComparison.OrdinalIgnoreCase) && colon > 0) value = value[(colon + 1)..].Trim();
        if (!value.StartsWith('{')) return (value.Length > 0 ? value : null, null);
        string? Field(string name)
        {
            var m = Regex.Match(value, $"['\"]{name}['\"]\\s*:\\s*['\"]([^'\"]+)['\"]");
            return m.Success ? m.Groups[1].Value : null;
        }
        return (Field("default") ?? Field("model") ?? Field("name"), Field("base_url"));
    }

    /// <summary>
    /// The models Hermes can use: the /models list of the OpenAI-compatible endpoint it's configured with, read on
    /// the server (the endpoint is usually only reachable from there).
    /// </summary>
    public static async Task<IReadOnlyList<ModelInfo>> ListHermesModelsAsync(ServerConnection server, AiSettings ai, CancellationToken ct)
    {
        var list = new List<ModelInfo>
        {
            ModelInfo.Default(ai.HermesDefaultModel is { } d ? $"Hermes's setting: {d}" : "Hermes's own setting"),
        };
        if (string.IsNullOrWhiteSpace(ai.HermesBaseUrl)) return list;
        var url = ai.HermesBaseUrl!.TrimEnd('/') + "/models";
        var container = ai.HermesCommand.StartsWith("docker exec", StringComparison.Ordinal)
            ? ai.HermesCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(p => p.StartsWith('-') || p is "docker" or "exec").FirstOrDefault()
            : null;
        // from the host first; an endpoint on the container's own localhost is only reachable from inside it
        var script = $"""
            out=$(curl -s -m 6 {Shell.Quote(url)} 2>/dev/null)
            if [ -z "$out" ] && [ -n {Shell.Quote(container ?? "")} ]; then
              out=$(docker exec {Shell.Quote(container ?? "x")} python3 -c 'import sys,urllib.request;print(urllib.request.urlopen(sys.argv[1],timeout=6).read().decode())' {Shell.Quote(url)} 2>/dev/null)
            fi
            printf '%s' "$out"
            """;
        var result = await server.RunScriptAsync(script, TimeSpan.FromSeconds(30), ct);
        try
        {
            list.AddRange(OpenAiBackend.ParseModelList(result.Stdout));
        }
        catch (System.Text.Json.JsonException)
        {
            Log.Warn($"Hermes's model endpoint returned something unexpected: {result.Stdout[..Math.Min(200, result.Stdout.Length)]}");
        }
        return list;
    }

    /// <summary>Stores what was found so "Automatic" knows what to use.</summary>
    public static void Apply(DetectedEngines found, AiSettings ai)
    {
        ai.DetectedEngine = found.Recommended;
        ai.OpenCodeOnServer = found.OtherClis.Contains("opencode");
        ai.DetectedSummary = found.Summary;
        ai.HermesQuietFlag = found.HermesQuietFlag;
        ai.HermesSupportsQueryFile = found.HermesQueryFile;
        ai.HermesDefaultModel = found.HermesModel;
        ai.HermesBaseUrl = found.HermesBaseUrl;
        // keep a command the user typed themselves; otherwise use what actually works on the server
        if (found.HermesCommand is not null && (string.IsNullOrWhiteSpace(ai.HermesCommand) || ai.HermesCommand == "hermes" || ai.HermesCommand.StartsWith("docker exec", StringComparison.Ordinal)))
            ai.HermesCommand = found.HermesCommand;
        if (found.Recommended == "compatible" && found.OllamaApi && ai.OpenAiBaseUrl.Contains(":8642", StringComparison.Ordinal))
        {
            ai.OpenAiBaseUrl = "http://127.0.0.1:11434/v1";
            ai.OpenAiModel ??= found.OllamaModels.FirstOrDefault();
        }
    }
}
