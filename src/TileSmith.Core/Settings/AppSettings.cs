using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TileSmith.Core.Settings;

public sealed class AppSettings
{
    /// <summary>Settings format. 2 = per-provider AI settings (0.2.0).</summary>
    public int Version { get; set; }

    public ServerProfile Server { get; set; } = new();
    public AiSettings Ai { get; set; } = new();

    /// <summary>glass = Liquid Glass (default: see-through, blurred, glass tiles) · solid = opaque rounded panel.</summary>
    public string PanelStyle { get; set; } = "glass";

    /// <summary>steel (DailyDash) | windows | blue | teal | green | amber | coral | pink | violet | #RRGGBB</summary>
    public string Accent { get; set; } = "steel";

    /// <summary>Grid columns: 0 = automatic (4, widening to 5 when there are many widgets), or 3–5.</summary>
    public int PanelColumns { get; set; }

    /// <summary>system | light | dark</summary>
    public string Theme { get; set; } = "system";

    public bool LaunchAtStartup { get; set; } = true;

    /// <summary>Start TileSmith with administrator rights (one UAC prompt at start; admin scripts then run without one).</summary>
    public bool RunAsAdmin { get; set; }
    public string Hotkey { get; set; } = "Win+Shift+Q";

    /// <summary>True once the user picked a shortcut themselves (then we never swap it automatically).</summary>
    public bool HotkeyChosenByUser { get; set; }
    public bool HotkeyEnabled { get; set; } = true;
    public bool KeepIconVisible { get; set; } = true;

    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoDownloadUpdates { get; set; }
    public string? LastNotifiedVersion { get; set; }

    public bool FirstRunCompleted { get; set; }

    /// <summary>Calmer motion: shorter, smaller animations and no tile cascade.</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>Prompts sent from the panel, newest first (Up arrow in the composer recalls them).</summary>
    public List<string> PromptHistory { get; set; } = [];

    /// <summary>3 = Liquid Glass became the panel's default look (0.4.0).</summary>
    public const int CurrentVersion = 3;
}

public sealed class ServerProfile
{
    /// <summary>"user@host[:port]" as typed by the user.</summary>
    public string Address { get; set; } = "";

    /// <summary>password | key</summary>
    public string AuthMethod { get; set; } = "password";

    public string? KeyPath { get; set; }

    /// <summary>DPAPI-protected (current Windows user only).</summary>
    public string? ProtectedPassword { get; set; }

    public string? ProtectedKeyPassphrase { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Address);
}

public sealed class AiSettings
{
    /// <summary>The chosen provider: auto, or an id from <c>ProviderCatalog</c> (claude, cursor, hermes, anthropic, openai…).</summary>
    public string Engine { get; set; } = "auto";

    public string HermesCommand { get; set; } = "hermes";
    public string? HermesArgs { get; set; }

    /// <summary>The "Custom endpoint" provider (id "compatible"): any OpenAI-compatible API, optionally through SSH.</summary>
    public string OpenAiBaseUrl { get; set; } = "http://127.0.0.1:8642/v1";
    public bool OpenAiViaTunnel { get; set; } = true;
    public string? OpenAiModel { get; set; }
    public string? ProtectedOpenAiKey { get; set; }

    /// <summary>Custom CLI. "{file}" is replaced by the prompt file; otherwise the prompt is piped to stdin.</summary>
    public string? CustomCommand { get; set; }

    /// <summary>Before 0.2: one model for the Claude / Cursor CLIs. Migrated into <see cref="Providers"/>.</summary>
    public string? LocalModel { get; set; }

    /// <summary>Per-provider model, API key, address and effort, keyed by provider id.</summary>
    public Dictionary<string, ProviderSettings> Providers { get; set; } = [];

    /// <summary>When the chosen AI can't be reached (offline, signed out, rate limited), try the next ready one.</summary>
    public bool AutoFallback { get; set; } = true;

    /// <summary>The server has the opencode CLI (found by Detect): OpenCode's free models can run there.</summary>
    public bool OpenCodeOnServer { get; set; }

    /// <summary>Hermes's own default model and the OpenAI-compatible endpoint it uses (found by Detect).</summary>
    public string? HermesDefaultModel { get; set; }
    public string? HermesBaseUrl { get; set; }

    /// <summary>"provider/model" pairs picked recently, newest first (shown at the top of the model picker).</summary>
    public List<string> RecentModels { get; set; } = [];

    public ProviderSettings For(string providerId)
    {
        if (!Providers.TryGetValue(providerId, out var p)) Providers[providerId] = p = new ProviderSettings();
        return p;
    }

    public string? ModelFor(string providerId) => providerId switch
    {
        "compatible" => OpenAiModel,
        _ => Providers.TryGetValue(providerId, out var p) ? p.Model : null,
    };

    public void SetModel(string providerId, string? model)
    {
        model = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (providerId == "compatible") OpenAiModel = model;
        else For(providerId).Model = model;
        if (model is null) return;
        var key = providerId + "/" + model;
        RecentModels.Remove(key);
        RecentModels.Insert(0, key);
        if (RecentModels.Count > 6) RecentModels.RemoveRange(6, RecentModels.Count - 6);
    }

    /// <summary>auto = run tools freely (admin still shows UAC); ask = confirm commands and file writes.</summary>
    public string Permission { get; set; } = "auto";

    /// <summary>Per-reply timeout.</summary>
    public int TimeoutSeconds { get; set; } = 240;

    /// <summary>Result of the last engine detection (what "auto" resolves to).</summary>
    public string? DetectedEngine { get; set; }
    public string? DetectedSummary { get; set; }
    public string? HermesQuietFlag { get; set; }
    public bool HermesSupportsQueryFile { get; set; } = true;
}

public sealed class ProviderSettings
{
    /// <summary>Model id (empty = the provider's default).</summary>
    public string? Model { get; set; }

    /// <summary>DPAPI-protected API key.</summary>
    public string? ProtectedKey { get; set; }

    /// <summary>Overrides the provider's API address (local servers on another port, proxies).</summary>
    public string? BaseUrl { get; set; }

    /// <summary>low | medium | high | xhigh | max — for providers that support it (Claude). Null = default.</summary>
    public string? Effort { get; set; }

    /// <summary>The decrypted key — never written to disk.</summary>
    [JsonIgnore]
    public string? ApiKey => Secrets.Unprotect(ProtectedKey);
}

/// <summary>Encrypts secrets with Windows DPAPI so they're only readable by this Windows account.</summary>
public static class Secrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TileSmith.v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Warn("Couldn't decrypt a stored secret; it will need to be entered again.");
            return null;
        }
    }
}

public static class SettingsStore
{
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                return Migrate(JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options) ?? new AppSettings());
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings file is unreadable; starting with defaults", ex);
            try { File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bad", overwrite: true); } catch { }
        }
        return new AppSettings { Version = AppSettings.CurrentVersion };
    }

    /// <summary>Brings settings written by older versions up to date.</summary>
    public static AppSettings Migrate(AppSettings settings)
    {
        if (settings.Version < 2)
        {
            var ai = settings.Ai;
            // 0.1 called the OpenAI-compatible endpoint "openai"; that id now means api.openai.com
            if (ai.Engine == "openai") ai.Engine = "compatible";
            if (ai.DetectedEngine == "openai") ai.DetectedEngine = "compatible";
            if (!string.IsNullOrWhiteSpace(ai.LocalModel))
            {
                ai.For("claude").Model ??= ai.LocalModel;
                ai.For("cursor").Model ??= ai.LocalModel;
                ai.LocalModel = null;
            }
            settings.Version = 2;
        }
        if (settings.Version < 3)
        {
            // 0.4 redesigned the panel around Liquid Glass; everyone gets it once (Solid is one click away)
            settings.PanelStyle = "glass";
            settings.Version = 3;
        }
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            AtomicFile.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, JsonDefaults.Options));
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }
    }
}
