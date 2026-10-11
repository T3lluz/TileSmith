using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TileSmith.Core.Remote;

namespace TileSmith.Core.Ai;

public sealed record AiMessage(string Role, string Content)
{
    public static AiMessage System(string text) => new("system", text);
    public static AiMessage User(string text) => new("user", text);
    public static AiMessage Assistant(string text) => new("assistant", text);
}

public sealed class AiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A language model reachable through the user's server.</summary>
public interface IAiBackend
{
    /// <summary>Short name shown in the UI, e.g. "Hermes on t3lluserver".</summary>
    string DisplayName { get; }

    Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct);
}

/// <summary>Renders a conversation as one prompt for stateless one-shot CLIs.</summary>
public static class Transcript
{
    /// <summary>Keeps one-shot prompts under the 128 KB single-argument limit of Linux (`-q "…"`).</summary>
    public const int MaxChars = 100_000;

    public static string Render(IReadOnlyList<AiMessage> messages)
    {
        var text = RenderCore(messages);
        if (text.Length <= MaxChars) return text;
        // shrink the oldest turns first; the latest results matter most
        var shrunk = messages.ToList();
        for (var i = 1; i < shrunk.Count - 2 && RenderCore(shrunk).Length > MaxChars; i++)
        {
            var m = shrunk[i];
            if (m.Role != "system" && m.Content.Length > 1500) shrunk[i] = m with { Content = m.Content[..1500] + "\n…(older output trimmed)" };
        }
        text = RenderCore(shrunk);
        return text.Length <= MaxChars ? text : text[..MaxChars];
    }

    private static string RenderCore(IReadOnlyList<AiMessage> messages)
    {
        var sb = new StringBuilder();
        var system = messages.Where(m => m.Role == "system").Select(m => m.Content);
        sb.AppendLine(string.Join("\n\n", system));
        var rest = messages.Where(m => m.Role != "system").ToList();
        if (rest.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine("=== Conversation so far ===");
        }
        foreach (var m in rest)
        {
            sb.AppendLine();
            sb.AppendLine(m.Role == "assistant" ? "[YOU]" : "[USER / TOOL RESULTS]");
            sb.AppendLine(m.Content);
        }
        sb.AppendLine();
        sb.AppendLine("=== Your turn: reply with one ```json tool call block only ===");
        return sb.ToString();
    }
}

/// <summary>
/// Nous Research Hermes Agent, driven over SSH in single-query mode:
/// <c>hermes chat --query-file FILE</c> (falls back to <c>-q "$(cat FILE)"</c> on older versions).
/// </summary>
public sealed class HermesCliBackend(ServerConnection server, string command, string? extraArgs, string? quietFlag, bool supportsQueryFile, TimeSpan timeout) : IAiBackend
{
    public string DisplayName => $"Hermes on {server.Target.Host}";

    public async Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        var prompt = Transcript.Render(messages);
        var invoke = supportsQueryFile
            ? $"{command} chat --query-file \"$f\""
            : $"{command} chat -q \"$(cat \"$f\")\"";
        if (!string.IsNullOrWhiteSpace(quietFlag)) invoke += " " + quietFlag;
        if (!string.IsNullOrWhiteSpace(extraArgs)) invoke += " " + extraArgs;
        return await RemotePrompt.RunAsync(server, prompt, invoke, timeout, "Hermes", ct);
    }
}

/// <summary>Any CLI on the server: "{file}" is replaced by the prompt file, otherwise the prompt is piped to stdin.</summary>
public sealed class CommandBackend(ServerConnection server, string template, TimeSpan timeout) : IAiBackend
{
    public string DisplayName => $"{template.Split(' ')[0]} on {server.Target.Host}";

    public async Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        var prompt = Transcript.Render(messages);
        var invoke = template.Contains("{file}", StringComparison.Ordinal)
            ? template.Replace("{file}", "\"$f\"", StringComparison.Ordinal)
            : template + " < \"$f\"";
        return await RemotePrompt.RunAsync(server, prompt, invoke, timeout, template.Split(' ')[0], ct);
    }
}

internal static class RemotePrompt
{
    public const string PathSetup =
        "export PATH=\"$HOME/.local/bin:$HOME/.hermes/bin:$HOME/bin:$HOME/.cargo/bin:$HOME/.npm-global/bin:$HOME/.bun/bin:/usr/local/bin:$PATH\"\n" +
        "[ -s \"$HOME/.nvm/nvm.sh\" ] && . \"$HOME/.nvm/nvm.sh\" >/dev/null 2>&1\n";

    public static async Task<string> RunAsync(ServerConnection server, string prompt, string invoke, TimeSpan timeout, string name, CancellationToken ct)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(prompt));
        var script = PathSetup +
                     "d=\"${XDG_CACHE_HOME:-$HOME/.cache}/tilesmith\"; mkdir -p \"$d\"; f=\"$d/prompt-$$.txt\"\n" +
                     $"printf '%s' '{b64}' | base64 -d > \"$f\"\n" +
                     "cd \"$HOME\"\n" +
                     $"{invoke}\n" +
                     "rc=$?; rm -f \"$f\"; return $rc";
        var result = await server.RunScriptAsync(script, timeout, ct);
        Log.Info($"{name} replied in {result.Elapsed.TotalSeconds:0.0}s (exit {result.ExitCode}, {result.Stdout.Length} chars)");
        if (result.ExitCode == 127)
            throw new AiException($"\"{invoke.Split(' ')[0]}\" isn't installed on the server (or not on PATH). Check Settings › Server & AI.");
        if (!result.Success && string.IsNullOrWhiteSpace(result.Stdout))
            throw new AiException($"{name} failed (exit {result.ExitCode}): {Tail(result.Stderr)}");
        if (string.IsNullOrWhiteSpace(result.Stdout))
            throw new AiException($"{name} returned nothing. {Tail(result.Stderr)}".Trim());
        return result.Stdout;
    }

    private static string Tail(string text)
    {
        var lines = JsonExtractor.StripAnsi(text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines.TakeLast(3));
    }
}

/// <summary>
/// OpenAI-compatible Chat Completions (Hermes Agent API server, Ollama, vLLM, LM Studio, LiteLLM, OpenRouter…).
/// When the endpoint only listens on the server's localhost, traffic goes through an SSH tunnel.
/// </summary>
public sealed class OpenAiBackend(ServerConnection? server, string baseUrl, bool viaTunnel, string? model, string? apiKey, TimeSpan timeout) : IAiBackend
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public string DisplayName => (model ?? "model") + (server is null ? "" : $" on {server.Target.Host}");

    public async Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        var url = await ResolveBaseAsync(ct);
        var chosen = model;
        if (string.IsNullOrWhiteSpace(chosen)) chosen = (await ListModelsAsync(url, ct)).FirstOrDefault() ?? "default";

        var body = new JsonObject
        {
            ["model"] = chosen,
            ["temperature"] = 0.2,
            ["stream"] = false,
            ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url + "/chat/completions")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var sw = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AiException($"The model didn't answer within {timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            throw new AiException($"Couldn't reach {baseUrl}: {ex.Message}", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new AiException(response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "The API rejected the key (401). Check the API key in Settings."
                    : $"API error {(int)response.StatusCode}: {Shorten(text)}");
            }
            Log.Info($"{DisplayName} replied in {sw.Elapsed.TotalSeconds:0.0}s");
            try
            {
                var json = JsonNode.Parse(text);
                var content = json?["choices"]?[0]?["message"]?["content"];
                return content?.GetValueKind() == JsonValueKind.String ? content.GetValue<string>() : content?.ToJsonString() ?? "";
            }
            catch (JsonException)
            {
                throw new AiException("The API returned something that isn't JSON: " + Shorten(text));
            }
        }
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => await ListModelsAsync(await ResolveBaseAsync(ct), ct);

    private async Task<IReadOnlyList<string>> ListModelsAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url + "/models");
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await Http.SendAsync(request, cts.Token);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cts.Token);
        return json?["data"] is JsonArray data
            ? data.Select(d => d?["id"]?.GetValue<string>()).OfType<string>().ToList()
            : [];
    }

    private async Task<string> ResolveBaseAsync(CancellationToken ct)
    {
        var uri = new Uri(baseUrl.TrimEnd('/'));
        if (!viaTunnel || server is null) return uri.ToString().TrimEnd('/');
        var localPort = await server.OpenTunnelAsync(uri.Host, uri.Port, ct);
        var builder = new UriBuilder(uri) { Host = "127.0.0.1", Port = localPort, Scheme = "http" };
        return builder.Uri.ToString().TrimEnd('/');
    }

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
