using TileSmith.Core.Settings;

namespace TileSmith.Core.Ai;

/// <summary>How TileSmith reaches a provider.</summary>
public enum ProviderKind
{
    /// <summary>An official CLI on this PC, signed in with the user's plan (Claude Code, Cursor).</summary>
    Subscription,

    /// <summary>Runs on the user's SSH server (Hermes Agent, a custom command).</summary>
    Server,

    /// <summary>A hosted API with an API key.</summary>
    Cloud,

    /// <summary>A model server on this PC (Ollama, LM Studio).</summary>
    Local,

    /// <summary>Any OpenAI-compatible endpoint, directly or through the SSH server.</summary>
    Custom,
}

public enum ApiStyle { None, OpenAi, Anthropic }

/// <summary>A model as shown in the pickers.</summary>
public sealed record ModelInfo(string Id, string Name, string? Detail = null)
{
    public static ModelInfo Default(string note = "Provider default") => new("", "Default", note);

    /// <summary>Costs nothing to use (OpenCode's free models, free OpenRouter models…).</summary>
    public bool Free { get; init; }

    /// <summary>Thinks before answering.</summary>
    public bool Reasoning { get; init; }

    /// <summary>Release date as yyyy-MM-dd, when known.</summary>
    public string? Released { get; init; }

    public long? Context { get; init; }

    /// <summary>Logo of the model's maker (Assets/Brands/{Brand}.svg).</summary>
    public string? Brand { get; init; }

    /// <summary>Released in the last two months.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsNew => DateTime.TryParse(Released, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
                         && (DateTime.Today - d).TotalDays is >= 0 and <= 60;
}

/// <summary>Everything the UI and the router need to know about one provider.</summary>
public sealed record ProviderInfo(
    string Id,
    string Name,
    ProviderKind Kind,
    string Tagline,
    string Monogram,
    string Color,
    ApiStyle Api = ApiStyle.None,
    string? BaseUrl = null,
    string? KeyUrl = null,
    string? KeyPrefix = null,
    string? DefaultModel = null,
    IReadOnlyList<ModelInfo>? Suggested = null,
    bool SupportsEffort = false,
    IReadOnlyDictionary<string, string>? Headers = null)
{
    public bool NeedsKey => Kind == ProviderKind.Cloud;

    public string KindLabel => Id == "opencode" ? "Free models" : Kind switch
    {
        ProviderKind.Subscription => "Your subscription",
        ProviderKind.Server => "On your server",
        ProviderKind.Cloud => "API key",
        ProviderKind.Local => "On this PC",
        _ => "Custom",
    };
}

public enum ProviderReadiness { Ready, NeedsKey, NotInstalled, NeedsServer, NeedsSetup }

public sealed record ProviderStatus(ProviderReadiness Readiness, string Text)
{
    public bool IsReady => Readiness == ProviderReadiness.Ready;
}

public static class ProviderCatalog
{
    public static readonly IReadOnlyList<ProviderInfo> All =
    [
        new("claude", "Claude Code", ProviderKind.Subscription, "Your Claude plan, through Claude Code on this PC", "C", "#D97757",
            Suggested:
            [
                ModelInfo.Default("Claude Code's default"),
                new("fable", "Claude Fable", "Most capable"),
                new("opus", "Claude Opus", "Best for building widgets"),
                new("sonnet", "Claude Sonnet", "Fast and capable"),
                new("haiku", "Claude Haiku", "Fastest"),
            ],
            SupportsEffort: true),
        new("cursor", "Cursor", ProviderKind.Subscription, "Your Cursor plan, through the Cursor Agent CLI", "Cu", "#B9BEC7",
            Suggested: [ModelInfo.Default("Cursor's Auto"), new("auto", "Auto", "Cursor picks")]),
        new("opencode", "OpenCode", ProviderKind.Subscription, "Free models (and OpenCode Zen) through the OpenCode app, on this PC or your server", "Oc", "#F5F5F5",
            DefaultModel: "big-pickle",
            Suggested: [new ModelInfo("big-pickle", "Big Pickle", "Free") { Free = true, Brand = "opencode" }]),
        new("hermes", "Hermes Agent", ProviderKind.Server, "Nous Research's agent on your server, over SSH", "H", "#A78BFA"),
        new("anthropic", "Anthropic API", ProviderKind.Cloud, "Claude models with an Anthropic API key", "A", "#D97757",
            Api: ApiStyle.Anthropic, BaseUrl: "https://api.anthropic.com", KeyUrl: "https://console.anthropic.com/settings/keys", KeyPrefix: "sk-ant-",
            DefaultModel: "claude-opus-5-5",
            Suggested:
            [
                new("claude-opus-5-5", "Claude Opus 5.5", "Recommended"),
                new("claude-sonnet-5-5", "Claude Sonnet 5.5", "Fast and capable"),
                new("claude-haiku-5-5", "Claude Haiku 5.5", "Fastest, cheapest"),
                new("claude-fable-5-1", "Claude Fable 5.1", "Most capable"),
            ],
            SupportsEffort: true),
        new("openai", "OpenAI", ProviderKind.Cloud, "GPT models with an OpenAI API key", "O", "#10A37F",
            Api: ApiStyle.OpenAi, BaseUrl: "https://api.openai.com/v1", KeyUrl: "https://platform.openai.com/api-keys", KeyPrefix: "sk-"),
        new("gemini", "Google Gemini", ProviderKind.Cloud, "Gemini models with a Google AI Studio key", "G", "#4F8DF5",
            Api: ApiStyle.OpenAi, BaseUrl: "https://generativelanguage.googleapis.com/v1beta/openai", KeyUrl: "https://aistudio.google.com/apikey"),
        new("openrouter", "OpenRouter", ProviderKind.Cloud, "Hundreds of models behind one key", "OR", "#7C7CF8",
            Api: ApiStyle.OpenAi, BaseUrl: "https://openrouter.ai/api/v1", KeyUrl: "https://openrouter.ai/keys", KeyPrefix: "sk-or-",
            Headers: new Dictionary<string, string> { ["HTTP-Referer"] = "https://github.com/T3lluz/TileSmith", ["X-Title"] = "TileSmith" }),
        new("groq", "Groq", ProviderKind.Cloud, "Open models at very high speed", "Gq", "#F55036",
            Api: ApiStyle.OpenAi, BaseUrl: "https://api.groq.com/openai/v1", KeyUrl: "https://console.groq.com/keys", KeyPrefix: "gsk_"),
        new("mistral", "Mistral", ProviderKind.Cloud, "Mistral's models from La Plateforme", "M", "#FA7A35",
            Api: ApiStyle.OpenAi, BaseUrl: "https://api.mistral.ai/v1", KeyUrl: "https://console.mistral.ai/api-keys"),
        new("xai", "xAI", ProviderKind.Cloud, "Grok models with an xAI key", "X", "#A3A9B3",
            Api: ApiStyle.OpenAi, BaseUrl: "https://api.x.ai/v1", KeyUrl: "https://console.x.ai", KeyPrefix: "xai-"),
        new("deepseek", "DeepSeek", ProviderKind.Cloud, "DeepSeek's chat and reasoning models", "D", "#5B7CFF",
            Api: ApiStyle.OpenAi, BaseUrl: "https://api.deepseek.com/v1", KeyUrl: "https://platform.deepseek.com/api_keys", KeyPrefix: "sk-"),
        new("ollama", "Ollama", ProviderKind.Local, "Models running on this PC with Ollama", "Ol", "#C9CCD1",
            Api: ApiStyle.OpenAi, BaseUrl: "http://127.0.0.1:11434/v1", KeyUrl: "https://ollama.com/download"),
        new("lmstudio", "LM Studio", ProviderKind.Local, "Models served by LM Studio on this PC", "LM", "#8B7CF6",
            Api: ApiStyle.OpenAi, BaseUrl: "http://127.0.0.1:1234/v1", KeyUrl: "https://lmstudio.ai"),
        new("compatible", "Custom endpoint", ProviderKind.Custom, "Any OpenAI-compatible API — vLLM, LiteLLM, the Hermes API server…", "{}", "#81A1C1",
            Api: ApiStyle.OpenAi),
        new("command", "Server command", ProviderKind.Server, "Any CLI on your server that reads a prompt", ">_", "#F1B467"),
    ];

    private static readonly Dictionary<string, ProviderInfo> ById = All.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);

    public static ProviderInfo? Find(string? id) => id is not null && ById.TryGetValue(id, out var p) ? p : null;

    public static ProviderInfo Get(string id) => Find(id) ?? throw new AiException($"Unknown AI provider \"{id}\".");

    /// <summary>The API address in use (the user's override, else the provider's).</summary>
    public static string? BaseUrlFor(ProviderInfo provider, AiSettings ai) => provider.Id switch
    {
        "compatible" => ai.OpenAiBaseUrl,
        _ => ai.Providers.TryGetValue(provider.Id, out var p) && !string.IsNullOrWhiteSpace(p.BaseUrl) ? p.BaseUrl!.Trim() : provider.BaseUrl,
    };

    public static string? KeyFor(ProviderInfo provider, AiSettings ai) => provider.Id switch
    {
        "compatible" => Secrets.Unprotect(ai.ProtectedOpenAiKey),
        _ => ai.Providers.TryGetValue(provider.Id, out var p) ? p.ApiKey : null,
    };

    /// <summary>Whether the provider can be used right now, and what's missing otherwise. Cheap: no network.</summary>
    public static ProviderStatus Status(ProviderInfo provider, AppSettings settings)
    {
        var ai = settings.Ai;
        switch (provider.Id)
        {
            case "claude":
            case "cursor":
                var cli = provider.Id == "claude" ? LocalCli.Claude : LocalCli.Cursor;
                return LocalCliBackend.Locate(cli) is null
                    ? new(ProviderReadiness.NotInstalled, "Not installed")
                    : new(ProviderReadiness.Ready, "Ready");
            case "opencode":
                if (OpenCodeBackend.LocateLocal() is not null) return new(ProviderReadiness.Ready, "Ready");
                if (settings.Server.IsConfigured && ai.OpenCodeOnServer) return new(ProviderReadiness.Ready, "Ready (on your server)");
                return new(ProviderReadiness.NotInstalled, "Install OpenCode");
            case "hermes":
                if (!settings.Server.IsConfigured) return new(ProviderReadiness.NeedsServer, "Connect your server");
                return ai.DetectedEngine == "hermes" || ai.HermesCommand.StartsWith("docker", StringComparison.Ordinal)
                    ? new(ProviderReadiness.Ready, "Ready")
                    : new(ProviderReadiness.NeedsSetup, "Not found yet — press Detect");
            case "command":
                if (!settings.Server.IsConfigured) return new(ProviderReadiness.NeedsServer, "Connect your server");
                return string.IsNullOrWhiteSpace(ai.CustomCommand)
                    ? new(ProviderReadiness.NeedsSetup, "Set the command")
                    : new(ProviderReadiness.Ready, "Ready");
            case "compatible":
                if (string.IsNullOrWhiteSpace(ai.OpenAiBaseUrl)) return new(ProviderReadiness.NeedsSetup, "Set the address");
                if (ai.OpenAiViaTunnel && !settings.Server.IsConfigured) return new(ProviderReadiness.NeedsServer, "Connect your server");
                return new(ProviderReadiness.Ready, "Ready");
        }
        if (provider.Kind == ProviderKind.Local)
        {
            // local servers can't be checked without a request; count them once a model was picked
            return string.IsNullOrWhiteSpace(ai.ModelFor(provider.Id))
                ? new(ProviderReadiness.NeedsSetup, "Pick a model")
                : new(ProviderReadiness.Ready, "Ready");
        }
        return string.IsNullOrWhiteSpace(KeyFor(provider, ai))
            ? new(ProviderReadiness.NeedsKey, "Add an API key")
            : new(ProviderReadiness.Ready, "Ready");
    }

    public static bool NeedsServer(ProviderInfo provider, AiSettings ai) =>
        provider.Id is "hermes" or "command" || (provider.Id == "compatible" && ai.OpenAiViaTunnel);

    /// <summary>A short label for the model in use, e.g. "Opus" or "gpt-5.2" (null = the provider default).</summary>
    public static string ModelLabel(ProviderInfo provider, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return provider.Id == "hermes" ? "Hermes" : provider.Suggested?.FirstOrDefault(m => m.Id == provider.DefaultModel)?.Name ?? provider.Name;
        }
        var known = provider.Suggested?.FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
        if (known is not null) return known.Name;
        var name = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : model;
        return name.Length > 28 ? name[..27] + "…" : name;
    }
}
