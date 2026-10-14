using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TileSmith.Core.Ai;

/// <summary>What models.dev knows about a model: its real name, when it came out, its context window and price.</summary>
public sealed record DirectoryModel(
    string Id,
    string Name,
    string? Released,
    long? Context,
    double? InputPrice,
    double? OutputPrice,
    bool Reasoning,
    bool Tools,
    bool Deprecated,
    bool TextOut)
{
    public bool Free => InputPrice == 0 && OutputPrice == 0;
}

/// <summary>
/// The open model database at models.dev (the one opencode uses): every provider's current models with names,
/// release dates, context sizes and prices. Downloaded at most once a day and kept trimmed on disk, so pickers show
/// complete, up-to-date lists even for providers that don't publish one (Claude Code) or before a key is added.
/// </summary>
public static partial class ModelDirectory
{
    public const string Url = "https://models.dev/api.json";
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Dictionary<string, List<DirectoryModel>>? _providers;
    private static DateTimeOffset _fetched;

    /// <summary>TileSmith provider id → models.dev provider id.</summary>
    public static readonly IReadOnlyDictionary<string, string> ProviderIds = new Dictionary<string, string>
    {
        ["claude"] = "anthropic",
        ["anthropic"] = "anthropic",
        ["openai"] = "openai",
        ["gemini"] = "google",
        ["openrouter"] = "openrouter",
        ["groq"] = "groq",
        ["mistral"] = "mistral",
        ["xai"] = "xai",
        ["deepseek"] = "deepseek",
        ["opencode"] = "opencode",
    };

    private static string CacheFile => Path.Combine(AppPaths.DataDir, "models-dev.json");

    private sealed record CacheFileData(DateTimeOffset Fetched, Dictionary<string, List<DirectoryModel>> Providers);

    /// <summary>The models of a TileSmith provider, newest first (empty until loaded).</summary>
    public static IReadOnlyList<DirectoryModel> For(string providerId)
    {
        LoadCache();
        if (!ProviderIds.TryGetValue(providerId, out var id) || _providers is null) return [];
        return _providers.TryGetValue(id, out var list) ? list : [];
    }

    public static DirectoryModel? Find(string providerId, string modelId)
    {
        var bare = modelId.Contains('/') && providerId != "openrouter" ? modelId[(modelId.LastIndexOf('/') + 1)..] : modelId;
        return For(providerId).FirstOrDefault(m => string.Equals(m.Id, bare, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Downloads the directory when the copy on disk is older than a day. Never throws.</summary>
    public static async Task EnsureFreshAsync(CancellationToken ct)
    {
        LoadCache();
        if (_providers is not null && DateTimeOffset.Now - _fetched < MaxAge) return;
        await Gate.WaitAsync(ct);
        try
        {
            if (_providers is not null && DateTimeOffset.Now - _fetched < MaxAge) return;
            await using var stream = await Http.GetStreamAsync(Url, ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var parsed = Parse(doc.RootElement);
            if (parsed.Count == 0) return;
            _providers = parsed;
            _fetched = DateTimeOffset.Now;
            try
            {
                AtomicFile.WriteAllText(CacheFile, JsonSerializer.Serialize(new CacheFileData(_fetched, parsed), JsonDefaults.Options));
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't save the model directory: {ex.Message}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"models.dev unavailable: {ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static void LoadCache()
    {
        if (_providers is not null) return;
        try
        {
            if (!File.Exists(CacheFile)) return;
            var data = JsonSerializer.Deserialize<CacheFileData>(File.ReadAllText(CacheFile), JsonDefaults.Options);
            if (data?.Providers is null) return;
            _providers = data.Providers;
            _fetched = data.Fetched;
        }
        catch (Exception ex)
        {
            Log.Warn($"models-dev.json unreadable: {ex.Message}");
        }
    }

    internal static Dictionary<string, List<DirectoryModel>> Parse(JsonElement root)
    {
        var result = new Dictionary<string, List<DirectoryModel>>();
        foreach (var id in ProviderIds.Values.Distinct())
        {
            if (!root.TryGetProperty(id, out var provider) || !provider.TryGetProperty("models", out var models)) continue;
            var list = new List<DirectoryModel>();
            foreach (var m in models.EnumerateObject())
            {
                var e = m.Value;
                string? Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool Bool(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
                double? Price(string name) => e.TryGetProperty("cost", out var c) && c.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
                long? context = e.TryGetProperty("limit", out var l) && l.TryGetProperty("context", out var cx) && cx.ValueKind == JsonValueKind.Number ? cx.GetInt64() : null;
                var textOut = !e.TryGetProperty("modalities", out var mod) || !mod.TryGetProperty("output", out var outs)
                              || outs.EnumerateArray().Any(o => o.GetString() == "text");
                list.Add(new DirectoryModel(
                    Str("id") ?? m.Name,
                    Str("name") ?? m.Name,
                    Str("release_date"),
                    context is > 0 ? context : null,
                    Price("input"),
                    Price("output"),
                    Bool("reasoning"),
                    Bool("tool_call"),
                    Str("status") == "deprecated",
                    textOut));
            }
            result[id] = list.OrderByDescending(x => x.Released ?? "").ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        return result;
    }

    /// <summary>Models worth offering for chat: not deprecated, text out, no image/speech/embedding models, no dated duplicates.</summary>
    public static IEnumerable<DirectoryModel> Chat(IEnumerable<DirectoryModel> models)
    {
        var list = models.Where(m => !m.Deprecated && m.TextOut && (m.Context ?? 1) > 0).ToList();
        var ids = list.Select(m => m.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // "claude-opus-4-5-20251101" next to "claude-opus-4-5": keep the short one
        return list.Where(m => !(DatedRegex().Match(m.Id) is { Success: true } d && ids.Contains(m.Id[..d.Index])));
    }

    /// <summary>A model for the pickers: real name, a short detail line and badges.</summary>
    public static ModelInfo ToModelInfo(DirectoryModel m, string providerId, string? idOverride = null) => new(idOverride ?? m.Id, CleanName(m.Name), Detail(m))
    {
        Free = m.Free,
        Reasoning = m.Reasoning,
        Released = m.Released,
        Context = m.Context,
        Brand = Brands.ForModel(m.Id, providerId),
    };

    /// <summary>Adds what the directory knows to a model a provider listed itself.</summary>
    public static ModelInfo Enrich(ModelInfo model, string providerId)
    {
        if (string.IsNullOrEmpty(model.Id)) return model;
        var known = Find(providerId, model.Id);
        if (known is null) return model with { Brand = model.Brand ?? Brands.ForModel(model.Id, providerId) };
        var nameIsId = string.Equals(model.Name, model.Id, StringComparison.OrdinalIgnoreCase) || model.Name.Length == 0;
        return model with
        {
            Name = nameIsId ? CleanName(known.Name) : model.Name,
            Detail = model.Detail is { Length: > 0 } d && !d.StartsWith("Added", StringComparison.Ordinal) ? d : Detail(known),
            Free = model.Free || known.Free,
            Reasoning = known.Reasoning,
            Released = known.Released,
            Context = known.Context,
            Brand = model.Brand ?? Brands.ForModel(model.Id, providerId),
        };
    }

    private static string CleanName(string name) => name.Replace(" (latest)", "", StringComparison.OrdinalIgnoreCase).Trim();

    public static string Detail(DirectoryModel m)
    {
        var parts = new List<string>();
        if (DateTime.TryParse(m.Released, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            parts.Add(date.ToString("MMM yyyy", CultureInfo.CurrentCulture));
        if (m.Context is { } c) parts.Add(c >= 1_000_000 ? $"{c / 1_000_000.0:0.#}M context" : $"{c / 1000}K context");
        if (m.Free) parts.Add("Free");
        else if (m.InputPrice is { } i && m.OutputPrice is { } o) parts.Add($"${i:0.##} / ${o:0.##}");
        return string.Join(" · ", parts);
    }

    [GeneratedRegex(@"-\d{8}$")]
    private static partial Regex DatedRegex();
}

/// <summary>
/// Which logo (Assets/Brands/{name}.svg, from the LobeHub icon set) belongs to a provider or a model's maker.
/// </summary>
public static class Brands
{
    private static readonly Dictionary<string, string> ByProvider = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude"] = "claude", ["anthropic"] = "anthropic", ["cursor"] = "cursor", ["hermes"] = "nousresearch",
        ["openai"] = "openai", ["gemini"] = "gemini", ["openrouter"] = "openrouter", ["groq"] = "groq",
        ["mistral"] = "mistral", ["xai"] = "grok", ["deepseek"] = "deepseek", ["ollama"] = "ollama",
        ["lmstudio"] = "lmstudio", ["opencode"] = "opencode",
    };

    // checked in order: the first fragment found in the model id wins
    private static readonly (string Fragment, string Brand)[] ByModel =
    [
        ("claude", "claude"), ("fable", "claude"), ("gpt", "openai"), ("codex", "openai"), ("chatgpt", "openai"), ("openai/", "openai"), ("gemini", "gemini"), ("gemma", "google"), ("grok", "grok"),
        ("deepseek", "deepseek"), ("mistral", "mistral"), ("mixtral", "mistral"), ("codestral", "mistral"), ("devstral", "mistral"),
        ("magistral", "mistral"), ("ministral", "mistral"), ("pixtral", "mistral"), ("qwen", "qwen"), ("qwq", "qwen"),
        ("llama", "meta"), ("meta-", "meta"), ("nemotron", "nvidia"), ("nvidia", "nvidia"), ("glm", "zhipu"), ("z-ai", "zhipu"),
        ("minimax", "minimax"), ("kimi", "kimi"), ("moonshot", "kimi"), ("command", "cohere"), ("cohere", "cohere"),
        ("sonar", "perplexity"), ("perplexity", "perplexity"), ("hermes", "nousresearch"), ("nous", "nousresearch"),
        ("mimo", "xiaomimimo"), ("xiaomi", "xiaomimimo"), ("step-", "stepfun"), ("stepfun", "stepfun"), ("longcat", "longcat"),
        ("phi-", "microsoft"), ("microsoft", "microsoft"), ("ernie", "baidu"), ("baidu", "baidu"), ("doubao", "doubao"),
        ("seed", "doubao"), ("jamba", "ai21"), ("lfm", "liquid"), ("liquid", "liquid"), ("big-pickle", "opencode"),
    ];

    public static string? ForProvider(string providerId) => ByProvider.GetValueOrDefault(providerId);

    public static string? ForModel(string modelId, string providerId)
    {
        var id = modelId.ToLowerInvariant();
        foreach (var (fragment, brand) in ByModel)
        {
            if (id.Contains(fragment, StringComparison.Ordinal)) return brand;
        }
        // OpenAI's reasoning line: o1, o3-mini, o4-mini (as a whole name, not a fragment of something else)
        var bare = id[(id.LastIndexOf('/') + 1)..];
        if (bare.Length >= 2 && bare[0] == 'o' && char.IsDigit(bare[1])) return "openai";
        return ForProvider(providerId);
    }
}
