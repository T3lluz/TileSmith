using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TileSmith.Core.Settings;

public sealed class AppSettings
{
    public ServerProfile Server { get; set; } = new();
    public AiSettings Ai { get; set; } = new();

    /// <summary>solid = Android-style rounded panel (default) · glass = Windows acrylic.</summary>
    public string PanelStyle { get; set; } = "solid";

    /// <summary>steel (DailyDash) | windows | blue | teal | green | amber | coral | pink | violet | #RRGGBB</summary>
    public string Accent { get; set; } = "steel";

    /// <summary>Grid columns: 0 = automatic (4, widening to 5 when there are many widgets), or 3–5.</summary>
    public int PanelColumns { get; set; }

    /// <summary>system | light | dark</summary>
    public string Theme { get; set; } = "system";

    public bool LaunchAtStartup { get; set; } = true;
    public string Hotkey { get; set; } = "Win+Shift+Q";

    /// <summary>True once the user picked a shortcut themselves (then we never swap it automatically).</summary>
    public bool HotkeyChosenByUser { get; set; }
    public bool HotkeyEnabled { get; set; } = true;
    public bool KeepIconVisible { get; set; } = true;

    public bool AutoCheckUpdates { get; set; } = true;
    public bool AutoDownloadUpdates { get; set; }
    public string? LastNotifiedVersion { get; set; }

    public bool FirstRunCompleted { get; set; }
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
    /// <summary>auto | hermes | openai | command</summary>
    public string Engine { get; set; } = "auto";

    public string HermesCommand { get; set; } = "hermes";
    public string? HermesArgs { get; set; }

    public string OpenAiBaseUrl { get; set; } = "http://127.0.0.1:8642/v1";
    public bool OpenAiViaTunnel { get; set; } = true;
    public string? OpenAiModel { get; set; }
    public string? ProtectedOpenAiKey { get; set; }

    /// <summary>Custom CLI. "{file}" is replaced by the prompt file; otherwise the prompt is piped to stdin.</summary>
    public string? CustomCommand { get; set; }

    /// <summary>Model for the Claude / Cursor CLIs (empty = the CLI's default).</summary>
    public string? LocalModel { get; set; }

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
                return JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings file is unreadable; starting with defaults", ex);
            try { File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".bad", overwrite: true); } catch { }
        }
        return new AppSettings();
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
