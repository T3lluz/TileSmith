using System.Globalization;
using System.Reflection;

namespace TileSmith.Core.Tiles;

/// <summary>
/// Material Symbols (Rounded, filled) — the icon set Android uses. Widgets name icons in snake_case
/// ("wifi", "dark_mode", "desktop_windows"); this resolves names, common synonyms and legacy names
/// to the glyph's code point.
/// </summary>
public static class Icons
{
    public const string FallbackName = "widgets";

    private static readonly Lazy<Dictionary<string, string>> Codepoints = new(Load);

    /// <summary>Friendly / legacy names → Material names.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["display"] = "desktop_windows", ["monitor"] = "monitor", ["laptop"] = "laptop_windows", ["screen"] = "desktop_windows",
        ["resolution"] = "aspect_ratio", ["brightness"] = "brightness_6", ["sun"] = "light_mode", ["moon"] = "dark_mode",
        ["darkmode"] = "dark_mode", ["theme"] = "contrast", ["volume"] = "volume_up", ["mute"] = "volume_off", ["sound"] = "volume_up",
        ["microphone"] = "mic", ["mic"] = "mic", ["headphones"] = "headphones", ["speaker"] = "speaker", ["music"] = "music_note",
        ["play"] = "play_arrow", ["pause"] = "pause", ["next"] = "skip_next", ["previous"] = "skip_previous", ["stop"] = "stop",
        ["camera"] = "photo_camera", ["screenshot"] = "screenshot_region", ["snip"] = "screenshot_region", ["wifi"] = "wifi",
        ["bluetooth"] = "bluetooth", ["airplane"] = "flight", ["network"] = "lan", ["globe"] = "language", ["web"] = "language",
        ["link"] = "link", ["cloud"] = "cloud", ["server"] = "dns", ["download"] = "download", ["upload"] = "upload",
        ["sync"] = "sync", ["refresh"] = "refresh", ["power"] = "power_settings_new", ["battery"] = "battery_full",
        ["lightning"] = "bolt", ["performance"] = "speed", ["leaf"] = "eco", ["lock"] = "lock", ["sleep"] = "bedtime",
        ["settings"] = "settings", ["wrench"] = "build", ["terminal"] = "terminal", ["code"] = "code", ["keyboard"] = "keyboard",
        ["mouse"] = "mouse", ["clipboard"] = "content_paste", ["copy"] = "content_copy", ["folder"] = "folder",
        ["document"] = "description", ["apps"] = "apps", ["home"] = "home", ["game"] = "sports_esports", ["mail"] = "mail",
        ["chat"] = "chat", ["people"] = "group", ["phone"] = "smartphone", ["calendar"] = "calendar_month", ["clock"] = "schedule",
        ["timer"] = "timer", ["search"] = "search", ["star"] = "star", ["heart"] = "favorite", ["lightbulb"] = "lightbulb",
        ["shield"] = "shield", ["key"] = "key", ["eye"] = "visibility", ["pin"] = "push_pin", ["flag"] = "flag", ["tag"] = "sell",
        ["print"] = "print", ["location"] = "location_on", ["zoom"] = "zoom_in", ["accessibility"] = "accessibility_new",
        ["calculator"] = "calculate", ["info"] = "info", ["add"] = "add", ["edit"] = "edit", ["delete"] = "delete", ["save"] = "save",
        ["share"] = "share", ["send"] = "send", ["checkmark"] = "check", ["cancel"] = "close", ["taskview"] = "view_carousel",
        ["system"] = "computer", ["project"] = "cast", ["color"] = "palette", ["personalize"] = "palette", ["contrast"] = "contrast",
        ["night"] = "nightlight", ["nightlight"] = "nightlight", ["hdr"] = "hdr_on", ["refreshrate"] = "speed", ["fan"] = "mode_fan",
        ["temperature"] = "device_thermostat", ["gpu"] = "memory", ["cpu"] = "memory", ["ram"] = "memory", ["vpn"] = "vpn_key",
        ["docker"] = "deployed_code", ["rocket"] = "rocket_launch", ["ai"] = "auto_awesome", ["sparkle"] = "auto_awesome",
    };

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("TileSmith.Core.Resources.material-symbols.codepoints");
        if (stream is null) return map;
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            var space = line.IndexOf(' ');
            if (space <= 0) continue;
            if (!int.TryParse(line.AsSpan(space + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code)) continue;
            map.TryAdd(line[..space], char.ConvertFromUtf32(code));
        }
        return map;
    }

    /// <summary>Normalizes to a Material Symbols name that exists, or null.</summary>
    public static string? Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var key = name.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (Codepoints.Value.ContainsKey(key)) return key;
        var compact = key.Replace("_", "");
        if (Aliases.TryGetValue(key, out var alias) || Aliases.TryGetValue(compact, out alias)) return alias;
        // PascalCase legacy names: "DarkMode" -> "dark_mode"
        var snake = string.Concat(name.Trim().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
        return Codepoints.Value.ContainsKey(snake) ? snake : null;
    }

    public static bool IsKnown(string? name) => Normalize(name) is not null;

    /// <summary>The glyph string to render with the Material Symbols font.</summary>
    public static string Glyph(string? name, string fallback = FallbackName)
    {
        var resolved = Normalize(name) ?? fallback;
        return Codepoints.Value.TryGetValue(resolved, out var glyph) ? glyph : Codepoints.Value.GetValueOrDefault(FallbackName, "?");
    }

    public static int Count => Codepoints.Value.Count;

    /// <summary>A short, useful subset for the AI prompt (any Material Symbols name works).</summary>
    public static string NamesForPrompt() =>
        "wifi, bluetooth, dark_mode, light_mode, contrast, brightness_6, nightlight, desktop_windows, monitor, laptop_windows, " +
        "aspect_ratio, hdr_on, speed, volume_up, volume_off, mic, mic_off, headphones, music_note, play_arrow, pause, skip_next, " +
        "skip_previous, screenshot_region, photo_camera, videocam, lock, power_settings_new, restart_alt, bedtime, battery_full, " +
        "bolt, eco, flight, lan, dns, cloud, language, link, terminal, code, deployed_code, folder, description, apps, home, " +
        "sports_esports, mail, chat, calendar_month, schedule, timer, search, star, favorite, lightbulb, shield, key, vpn_key, " +
        "visibility, push_pin, print, location_on, keyboard, mouse, content_paste, rocket_launch, auto_awesome, tune, settings, " +
        "build, memory, device_thermostat, mode_fan, cast, palette, sync, refresh, download, upload, notifications, smartphone, " +
        "tv, router, storage, backup, auto_mode, energy_savings_leaf, keyboard_command_key";
}
