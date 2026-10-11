using System.Text.Json;
using TileSmith.Core.Remote;
using TileSmith.Core.Settings;

namespace TileSmith.Core.Ai;

/// <summary>
/// Turns settings into a backend: resolves "Automatic", applies the chosen model and, when the chosen AI can't be
/// reached, falls back to the next ready provider.
/// </summary>
public static class AiEngineFactory
{
    /// <summary>Order in which "Automatic" and the fallback chain try providers.</summary>
    private static readonly string[] Preference =
        ["hermes", "claude", "cursor", "anthropic", "openai", "gemini", "openrouter", "xai", "mistral", "deepseek", "groq", "compatible", "ollama", "lmstudio", "command"];

    /// <summary>The provider id "Automatic" (or the user's choice) resolves to.</summary>
    public static string ResolveEngine(AppSettings settings)
    {
        var engine = settings.Ai.Engine;
        if (engine != "auto" && ProviderCatalog.Find(engine) is not null) return engine;
        if (settings.Server.IsConfigured && settings.Ai.DetectedEngine is { } detected && ProviderCatalog.Find(detected) is not null) return detected;
        foreach (var id in Preference)
        {
            if (id is "compatible" or "command") continue; // only when chosen explicitly
            if (ProviderCatalog.Status(ProviderCatalog.Get(id), settings).IsReady) return id;
        }
        return settings.Server.IsConfigured ? "hermes" : "claude";
    }

    public static ProviderInfo ResolveProvider(AppSettings settings) => ProviderCatalog.Get(ResolveEngine(settings));

    public static bool NeedsServer(string engine, AiSettings ai) =>
        ProviderCatalog.Find(engine) is { } p && ProviderCatalog.NeedsServer(p, ai);

    /// <summary>Providers to try in order: the chosen one, then (with fallback on) every other ready one.</summary>
    public static IReadOnlyList<string> Chain(AppSettings settings, string? providerOverride = null)
    {
        var first = providerOverride ?? ResolveEngine(settings);
        var chain = new List<string> { first };
        if (!settings.Ai.AutoFallback) return chain;
        foreach (var id in Preference)
        {
            if (chain.Contains(id)) continue;
            if (id is "compatible" && settings.Ai.DetectedEngine != "compatible") continue;
            if (id is "command") continue;
            if (ProviderCatalog.Status(ProviderCatalog.Get(id), settings).IsReady) chain.Add(id);
        }
        return chain;
    }

    /// <summary>A backend for the chosen (or given) provider, falling back along <see cref="Chain"/>.</summary>
    public static IAiBackend Create(AppSettings settings, Func<ServerConnection> server, string? providerOverride = null, string? modelOverride = null)
    {
        var chain = Chain(settings, providerOverride);
        if (chain.Count == 1) return CreateSingle(chain[0], settings, server, modelOverride);
        return new RoutedBackend(chain.Select((id, i) => (id, (Func<IAiBackend>)(() => CreateSingle(id, settings, server, i == 0 ? modelOverride : null)))).ToList());
    }

    public static IAiBackend CreateSingle(string providerId, AppSettings settings, Func<ServerConnection> server, string? modelOverride = null)
    {
        var ai = settings.Ai;
        var provider = ProviderCatalog.Get(providerId);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(ai.TimeoutSeconds, 30, 1800));
        var model = modelOverride ?? ai.ModelFor(providerId);
        if (string.IsNullOrWhiteSpace(model)) model = null;
        var effort = ai.Providers.TryGetValue(providerId, out var ps) ? ps.Effort : null;

        ServerConnection Server()
        {
            try { return server(); }
            catch (ServerException ex) { throw new AiException(ex.Message, ex, canFallback: true); }
        }

        switch (providerId)
        {
            case "claude":
                return new LocalCliBackend(LocalCli.Claude, model, timeout, effort);
            case "cursor":
                return new LocalCliBackend(LocalCli.Cursor, model, timeout);
            case "hermes":
                return new HermesCliBackend(Server(), string.IsNullOrWhiteSpace(ai.HermesCommand) ? "hermes" : ai.HermesCommand,
                    ai.HermesArgs, ai.HermesQuietFlag, ai.HermesSupportsQueryFile, model, timeout);
            case "command":
                return new CommandBackend(Server(), string.IsNullOrWhiteSpace(ai.CustomCommand)
                    ? throw new AiException("Set the server command in Settings › AI.", canFallback: true) : ai.CustomCommand!, timeout);
            case "compatible":
                return new OpenAiBackend(ai.OpenAiViaTunnel ? $"Custom endpoint" : "Custom endpoint", ai.OpenAiBaseUrl, model,
                    Secrets.Unprotect(ai.ProtectedOpenAiKey), timeout, ai.OpenAiViaTunnel ? Server() : null);
            case "anthropic":
                var anthropicKey = ProviderCatalog.KeyFor(provider, ai) ?? throw new AiException("Add your Anthropic API key in Settings › AI.", canFallback: true);
                return new AnthropicBackend(anthropicKey, model ?? provider.DefaultModel!, effort, timeout, ProviderCatalog.BaseUrlFor(provider, ai));
        }

        var key = ProviderCatalog.KeyFor(provider, ai);
        if (provider.NeedsKey && string.IsNullOrWhiteSpace(key))
            throw new AiException($"Add your {provider.Name} API key in Settings › AI.", canFallback: true);
        return new OpenAiBackend(provider.Name, ProviderCatalog.BaseUrlFor(provider, ai)!, model ?? provider.DefaultModel, key, timeout, headers: provider.Headers);
    }
}

/// <summary>
/// Tries providers in order. When one can't be reached (offline, signed out, rate limited), the next takes over
/// for the rest of the run — the conversation is resent each turn, so switching mid-run is safe.
/// </summary>
public sealed class RoutedBackend(IReadOnlyList<(string Id, Func<IAiBackend> Create)> chain) : IAiBackend
{
    private int _index;
    private IAiBackend? _current;

    /// <summary>(from, to, reason) — raised when a fallback takes over.</summary>
    public event Action<string, string, string>? Switched;

    public string ProviderId => chain[_index].Id;

    public string DisplayName => _current?.DisplayName ?? ProviderCatalog.Find(chain[_index].Id)?.Name ?? "AI";

    public async Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                _current ??= chain[_index].Create();
                return await _current.CompleteAsync(messages, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && CanFallback(ex) && _index + 1 < chain.Count)
            {
                var failed = _current?.DisplayName ?? ProviderCatalog.Find(chain[_index].Id)?.Name ?? chain[_index].Id;
                Log.Warn($"{failed} failed ({ex.Message}); trying {chain[_index + 1].Id}");
                _index++;
                _current = null;
                var next = ProviderCatalog.Find(chain[_index].Id)?.Name ?? chain[_index].Id;
                Switched?.Invoke(failed, next, ex.Message);
            }
        }
    }

    public static bool CanFallback(Exception ex) => ex switch
    {
        AiLoginRequiredException => true,
        AiException a => a.CanFallback,
        ServerException => true,
        HttpRequestException => true,
        _ => false,
    };
}

/// <summary>Model lists per provider: live from the provider, cached on disk so pickers open instantly.</summary>
public static class ModelCatalog
{
    private sealed record Entry(DateTimeOffset Fetched, List<ModelInfo> Models);

    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? _cache;

    private static string CacheFile => Path.Combine(AppPaths.DataDir, "models-cache.json");

    private static Dictionary<string, Entry> Cache
    {
        get
        {
            if (_cache is not null) return _cache;
            try
            {
                _cache = File.Exists(CacheFile)
                    ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CacheFile), JsonDefaults.Options) ?? []
                    : [];
            }
            catch
            {
                _cache = [];
            }
            return _cache;
        }
    }

    /// <summary>What to show right away: the cached list, else the provider's suggestions.</summary>
    public static IReadOnlyList<ModelInfo> Known(ProviderInfo provider)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(provider.Id, out var entry) && entry.Models.Count > 0) return entry.Models;
        }
        return provider.Suggested ?? (provider.Id is "hermes" or "command" ? [ModelInfo.Default()] : []);
    }

    public static DateTimeOffset? LastFetched(ProviderInfo provider)
    {
        lock (Gate) return Cache.TryGetValue(provider.Id, out var e) ? e.Fetched : null;
    }

    /// <summary>Fetches the provider's current model list (network / CLI / SSH) and caches it.</summary>
    public static async Task<IReadOnlyList<ModelInfo>> RefreshAsync(ProviderInfo provider, AppSettings settings, Func<ServerConnection> server, CancellationToken ct)
    {
        var ai = settings.Ai;
        IReadOnlyList<ModelInfo> models;
        switch (provider.Id)
        {
            case "claude":
                return provider.Suggested!;
            case "command":
                return [ModelInfo.Default("Set by the command")];
            case "cursor":
                models = [ModelInfo.Default("Cursor's Auto"), .. await LocalCliBackend.ListCursorModelsAsync(ct)];
                break;
            case "hermes":
                models = await EngineDetector.ListHermesModelsAsync(server(), ai, ct);
                break;
            case "anthropic":
                var key = ProviderCatalog.KeyFor(provider, ai) ?? throw new AiException("Add the API key first.");
                var listed = await new AnthropicBackend(key, provider.DefaultModel!, null, TimeSpan.FromSeconds(20), ProviderCatalog.BaseUrlFor(provider, ai)).ListModelsAsync(ct);
                models = listed.Count > 0 ? listed : provider.Suggested!;
                break;
            default:
                var backend = new OpenAiBackend(provider.Name, ProviderCatalog.BaseUrlFor(provider, ai) ?? "", null, ProviderCatalog.KeyFor(provider, ai),
                    TimeSpan.FromSeconds(20), provider.Id == "compatible" && ai.OpenAiViaTunnel ? server() : null, provider.Headers);
                models = Filter(provider, await backend.ListModelsAsync(ct));
                break;
        }
        lock (Gate)
        {
            Cache[provider.Id] = new Entry(DateTimeOffset.Now, [.. models]);
            try { AtomicFile.WriteAllText(CacheFile, JsonSerializer.Serialize(Cache, JsonDefaults.Options)); }
            catch (Exception ex) { Log.Warn($"Couldn't save the model list: {ex.Message}"); }
        }
        return models;
    }

    /// <summary>Hides models that can't chat (embeddings, speech, images, moderation…).</summary>
    internal static IReadOnlyList<ModelInfo> Filter(ProviderInfo provider, IReadOnlyList<ModelInfo> models)
    {
        if (provider.Kind is ProviderKind.Local or ProviderKind.Custom) return models;
        string[] skip = ["embed", "tts", "whisper", "dall-e", "davinci", "babbage", "moderation", "transcribe", "image", "audio",
                         "realtime", "imagen", "veo", "aqa", "rerank", "guard", "-search", "computer-use", "sora", "lyria", "ocr"];
        return models.Where(m => !skip.Any(s => m.Id.Contains(s, StringComparison.OrdinalIgnoreCase))).ToList();
    }
}
