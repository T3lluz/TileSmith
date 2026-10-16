using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
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

/// <param name="canFallback">The AI couldn't be reached or used (offline, signed out, rate limited, not installed):
/// another provider may well succeed. False for problems another provider would hit too.</param>
public sealed class AiException(string message, Exception? inner = null, bool canFallback = false) : Exception(message, inner)
{
    public bool CanFallback { get; } = canFallback;
}

/// <summary>A language model TileSmith can ask for the next step.</summary>
public interface IAiBackend
{
    /// <summary>Short name shown in the UI, e.g. "Claude Opus" or "Hermes on t3lluserver".</summary>
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
            sb.AppendLine(m.Role == "assistant" ? "[YOU]" : "[USER]");
            sb.AppendLine(m.Content);
        }
        sb.AppendLine();
        sb.AppendLine("=== Your turn: reply with the JSON only ===");
        return sb.ToString();
    }
}

/// <summary>
/// Nous Research Hermes Agent, driven over SSH in single-query mode:
/// <c>hermes chat --query-file FILE</c>, or <c>-q "$(cat FILE)"</c> on versions without --query-file.
/// <paramref name="model"/> is passed as <c>-m</c> (e.g. one of the models of the endpoint Hermes is configured with).
/// </summary>
public sealed class HermesCliBackend(ServerConnection server, string command, string? extraArgs, string? quietFlag, bool supportsQueryFile, string? model, TimeSpan timeout) : IAiBackend
{
    public string DisplayName => string.IsNullOrWhiteSpace(model) ? $"Hermes on {server.Target.Host}" : $"Hermes · {ProviderCatalog.ModelLabel(ProviderCatalog.Get("hermes"), model)}";

    public async Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        var prompt = Transcript.Render(messages);
        var invoke = supportsQueryFile
            ? $"{command} chat --query-file \"$f\""
            : $"{command} chat -q \"$(cat \"$f\")\"";
        if (!string.IsNullOrWhiteSpace(quietFlag)) invoke += " " + quietFlag;
        if (!string.IsNullOrWhiteSpace(model)) invoke += " -m " + Shell.Quote(model!);
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

public static class Shell
{
    /// <summary>Single-quotes a value for bash.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}

internal static class RemotePrompt
{
    public const string PathSetup =
        "export PATH=\"$HOME/.local/bin:$HOME/.hermes/bin:$HOME/bin:$HOME/.cargo/bin:$HOME/.npm-global/bin:$HOME/.bun/bin:/usr/local/bin:/snap/bin:$PATH\"\n" +
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
        CommandResult result;
        try
        {
            result = await server.RunScriptAsync(script, timeout, ct);
        }
        catch (ServerException ex)
        {
            throw new AiException(ex.Message, ex, canFallback: true);
        }
        Log.Info($"{name} replied in {result.Elapsed.TotalSeconds:0.0}s (exit {result.ExitCode}, {result.Stdout.Length} chars)");
        if (result.ExitCode == 127)
            throw new AiException($"\"{invoke.Split(' ')[0]}\" isn't installed on the server (or not on PATH). Open Settings › Server and press Detect.", canFallback: true);
        if (!result.Success && string.IsNullOrWhiteSpace(result.Stdout))
            throw new AiException($"{name} failed (exit {result.ExitCode}): {Tail(result.Stderr)}", canFallback: true);
        if (string.IsNullOrWhiteSpace(result.Stdout))
            throw new AiException($"{name} returned nothing. {Tail(result.Stderr)}".Trim(), canFallback: true);
        return result.Stdout;
    }

    private static string Tail(string text)
    {
        var lines = JsonExtractor.StripAnsi(text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(" ", lines.TakeLast(3));
    }
}

/// <summary>
/// OpenAI-style Chat Completions — OpenAI, Gemini, OpenRouter, Groq, Mistral, xAI, DeepSeek, Ollama, LM Studio, vLLM,
/// the Hermes API server… When an endpoint only listens on the server's localhost, traffic goes through an SSH tunnel.
/// </summary>
public sealed class OpenAiBackend(
    string name,
    string baseUrl,
    string? model,
    string? apiKey,
    TimeSpan timeout,
    ServerConnection? tunnelVia = null,
    IReadOnlyDictionary<string, string>? headers = null) : IAiBackend
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private bool _noTemperature = model is not null && IsReasoningModel(model);

    public string DisplayName => string.IsNullOrWhiteSpace(model) ? name : $"{ShortModel(model!)} · {name}";

    public async Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        var url = await ResolveBaseAsync(ct);
        var chosen = model;
        if (string.IsNullOrWhiteSpace(chosen)) chosen = (await ListModelsAsync(url, ct)).FirstOrDefault()?.Id ?? "default";

        for (var attempt = 0; ; attempt++)
        {
            var body = new JsonObject
            {
                ["model"] = chosen,
                ["stream"] = false,
                ["messages"] = new JsonArray(messages.Select(m => (JsonNode)new JsonObject { ["role"] = m.Role, ["content"] = m.Content }).ToArray()),
            };
            if (!_noTemperature) body["temperature"] = 0.2;

            using var request = Request(HttpMethod.Post, url + "/chat/completions");
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

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
                throw new AiException($"{name} didn't answer within {timeout.TotalSeconds:0} seconds.", canFallback: true);
            }
            catch (HttpRequestException ex)
            {
                throw new AiException($"Couldn't reach {name} ({Host(url)}): {ex.Message}", ex, canFallback: true);
            }

            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    // reasoning models reject sampling parameters: retry once without them
                    if (response.StatusCode == HttpStatusCode.BadRequest && !_noTemperature && attempt == 0 && text.Contains("temperature", StringComparison.OrdinalIgnoreCase))
                    {
                        _noTemperature = true;
                        continue;
                    }
                    throw Error(response.StatusCode, text);
                }
                Log.Info($"{DisplayName} replied in {sw.Elapsed.TotalSeconds:0.0}s");
                return ParseContent(text);
            }
        }
    }

    private Exception Error(HttpStatusCode status, string body)
    {
        var message = ErrorMessage(body);
        return status switch
        {
            HttpStatusCode.Unauthorized => new AiException($"{name} rejected the API key (401). Check it in Settings › AI.", canFallback: true),
            HttpStatusCode.Forbidden => new AiException($"{name} refused access (403): {message}", canFallback: true),
            HttpStatusCode.NotFound => new AiException($"{name} doesn't know the model \"{model}\" (404). Pick another one in Settings › AI.", canFallback: true),
            HttpStatusCode.PaymentRequired => new AiException($"{name} says the account is out of credit (402).", canFallback: true),
            HttpStatusCode.TooManyRequests => new AiException($"{name} is rate limiting this key (429). Try again in a minute.", canFallback: true),
            >= HttpStatusCode.InternalServerError => new AiException($"{name} had a server error ({(int)status}): {message}", canFallback: true),
            _ => new AiException($"{name} error {(int)status}: {message}"),
        };
    }

    internal static string ParseContent(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            throw new AiException("The API returned something that isn't JSON: " + Shorten(json));
        }
        var content = root?["choices"]?[0]?["message"]?["content"];
        return content switch
        {
            null => throw new AiException("The API reply had no message: " + Shorten(json)),
            JsonArray parts => string.Concat(parts.Select(p => p?["text"]?.GetValue<string>() ?? "")),
            _ when content.GetValueKind() == JsonValueKind.String => content.GetValue<string>(),
            _ => content.ToJsonString(),
        };
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var message = node?["error"]?["message"]?.ToString() ?? node?["error"]?.ToString() ?? node?["message"]?.ToString();
            if (!string.IsNullOrWhiteSpace(message)) return Shorten(message);
        }
        catch (JsonException) { }
        return Shorten(body);
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) => await ListModelsAsync(await ResolveBaseAsync(ct), ct);

    private async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(string url, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, url + "/models");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(12));
        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            throw new AiException($"Couldn't reach {name} ({Host(url)}). Is it running?", ex, canFallback: true);
        }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode) throw Error(response.StatusCode, text);
            return ParseModelList(text);
        }
    }

    /// <summary>Reads an OpenAI-style /models reply (also OpenRouter's richer one), newest first when dates are given.</summary>
    public static IReadOnlyList<ModelInfo> ParseModelList(string json)
    {
        var root = JsonNode.Parse(json);
        var data = root?["data"] as JsonArray ?? root?["models"] as JsonArray ?? root as JsonArray;
        if (data is null) return [];
        var list = new List<(ModelInfo Model, long Created)>();
        foreach (var item in data.OfType<JsonObject>())
        {
            var id = item["id"]?.ToString() ?? item["name"]?.ToString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (id.StartsWith("models/", StringComparison.Ordinal)) id = id[7..]; // Gemini
            var display = item["name"]?.ToString() is { Length: > 0 } n && n != id && !n.StartsWith("models/", StringComparison.Ordinal) ? n : id;
            var context = item["context_length"] ?? item["context_window"] ?? item["max_context_length"];
            string? detail = null;
            if (context is not null && long.TryParse(context.ToString(), out var tokens) && tokens > 0) detail = tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:0.#}M context" : $"{tokens / 1000}K context";
            var created = item["created"] is { } c && long.TryParse(c.ToString(), out var t) ? t : 0;
            // OpenRouter: ":free" variants and zero pricing
            var free = id.EndsWith(":free", StringComparison.OrdinalIgnoreCase)
                       || (item["pricing"] is JsonObject price && price["prompt"]?.ToString() is "0" && price["completion"]?.ToString() is "0");
            if (free) detail = detail is null ? "Free" : "Free · " + detail;
            list.Add((new ModelInfo(id, display, detail) { Free = free }, created));
        }
        var ordered = list.Any(x => x.Created > 0)
            ? list.OrderByDescending(x => x.Created).ThenBy(x => x.Model.Id, StringComparer.OrdinalIgnoreCase)
            : list.OrderBy(x => x.Model.Id, StringComparer.OrdinalIgnoreCase);
        return ordered.Select(x => x.Model).DistinctBy(m => m.Id).ToList();
    }

    private HttpRequestMessage Request(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(apiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        if (headers is not null)
        {
            foreach (var (key, value) in headers) request.Headers.TryAddWithoutValidation(key, value);
        }
        return request;
    }

    private async Task<string> ResolveBaseAsync(CancellationToken ct)
    {
        Uri uri;
        try
        {
            uri = new Uri(baseUrl.Trim().TrimEnd('/'));
        }
        catch (UriFormatException)
        {
            throw new AiException($"\"{baseUrl}\" isn't a valid address. Use something like http://127.0.0.1:11434/v1.");
        }
        if (tunnelVia is null) return uri.ToString().TrimEnd('/');
        int localPort;
        try
        {
            localPort = await tunnelVia.OpenTunnelAsync(uri.Host, uri.Port, ct);
        }
        catch (ServerException ex)
        {
            throw new AiException(ex.Message, ex, canFallback: true);
        }
        var builder = new UriBuilder(uri) { Host = "127.0.0.1", Port = localPort, Scheme = "http" };
        return builder.Uri.ToString().TrimEnd('/');
    }

    /// <summary>o1/o3/o4 and gpt-5 style reasoning models only accept the default temperature.</summary>
    private static bool IsReasoningModel(string model)
    {
        var m = model.ToLowerInvariant();
        var name = m.Contains('/') ? m[(m.LastIndexOf('/') + 1)..] : m;
        return name.StartsWith("o1", StringComparison.Ordinal) || name.StartsWith("o3", StringComparison.Ordinal)
               || name.StartsWith("o4", StringComparison.Ordinal) || name.StartsWith("gpt-5", StringComparison.Ordinal)
               || name.Contains("reasoner", StringComparison.Ordinal);
    }

    private static string ShortModel(string model) => model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model;

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Authority : url;

    private static string Shorten(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
