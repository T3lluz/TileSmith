using System.Text;

namespace TileSmith.Core.Actions;

public enum ParamType { String, Int, Number, Bool, Enum, Actions, Object }

[Flags]
public enum ActionTraits
{
    None = 0,
    Query = 1,        // returns a value; usable as a control's "state"
    Script = 2,       // runs arbitrary user/AI supplied code
    Server = 4,       // runs on the connected SSH server
    Network = 8,      // talks to the network
    Input = 16,       // sends keystrokes to the foreground window (panel hides first)
    Destructive = 32, // shuts down, signs out, kills processes...
}

public sealed record ParamInfo(string Name, ParamType Type, bool Required, string Description, string[]? Values = null);

public sealed record ActionInfo(
    string Type,
    string Category,
    string Description,
    ParamInfo[] Params,
    string? Returns = null,
    ActionTraits Traits = ActionTraits.None)
{
    public bool IsQuery => Traits.HasFlag(ActionTraits.Query);
}

/// <summary>
/// Single source of truth for every built-in action: used to validate widgets, to describe
/// actions in the UI and to teach the AI what it can use.
/// </summary>
public static class ActionCatalog
{
    private static ParamInfo Req(string name, ParamType type, string description, params string[] values) =>
        new(name, type, true, description, values.Length > 0 ? values : null);

    private static ParamInfo Opt(string name, ParamType type, string description, params string[] values) =>
        new(name, type, false, description, values.Length > 0 ? values : null);

    private static readonly ParamInfo DisplayParam = Opt("display", ParamType.String,
        "which display: \"primary\" (default), \"internal\" (the laptop's built-in panel), \"external\", a number 1..n, or a device name like \\\\.\\DISPLAY1");

    public static readonly IReadOnlyList<ActionInfo> All =
    [
        // ── Display ─────────────────────────────────────────────────────────
        new("display.setResolution", "Display", "Change a display's resolution (validated against the modes the display supports).",
            [Req("width", ParamType.Int, "pixels"), Req("height", ParamType.Int, "pixels"),
             Opt("refreshRate", ParamType.Int, "Hz; omit to keep the current rate if possible, otherwise the highest available"), DisplayParam]),
        new("display.getResolution", "Display", "Current resolution of a display.", [DisplayParam],
            "\"WIDTHxHEIGHT\", e.g. \"2560x1440\"", ActionTraits.Query),
        new("display.setRefreshRate", "Display", "Change a display's refresh rate at its current resolution.",
            [Req("hz", ParamType.Int, "refresh rate in Hz"), DisplayParam]),
        new("display.getRefreshRate", "Display", "Current refresh rate of a display.", [DisplayParam],
            "Hz as a number, e.g. \"165\"", ActionTraits.Query),
        new("display.setBrightness", "Display", "Set the built-in screen brightness (laptops).",
            [Req("level", ParamType.Int, "0-100")]),
        new("display.getBrightness", "Display", "Built-in screen brightness.", [], "0-100", ActionTraits.Query),
        new("display.project", "Display", "Switch multi-monitor projection mode (like Win+P).",
            [Req("mode", ParamType.Enum, "projection mode", "pcScreenOnly", "duplicate", "extend", "secondScreenOnly")]),

        // ── Audio ───────────────────────────────────────────────────────────
        new("audio.setVolume", "Audio", "Set the default speaker volume.", [Req("level", ParamType.Int, "0-100")]),
        new("audio.getVolume", "Audio", "Default speaker volume.", [], "0-100", ActionTraits.Query),
        new("audio.changeVolume", "Audio", "Raise or lower the volume.", [Req("delta", ParamType.Int, "e.g. 10 or -10")]),
        new("audio.setMute", "Audio", "Mute or unmute the speakers.", [Req("mute", ParamType.Enum, "", "true", "false", "toggle")]),
        new("audio.getMute", "Audio", "Whether speakers are muted.", [], "\"true\" or \"false\"", ActionTraits.Query),
        new("audio.setMicMute", "Audio", "Mute or unmute the default microphone.", [Req("mute", ParamType.Enum, "", "true", "false", "toggle")]),
        new("audio.getMicMute", "Audio", "Whether the default microphone is muted.", [], "\"true\" or \"false\"", ActionTraits.Query),

        // ── Appearance ──────────────────────────────────────────────────────
        new("theme.set", "Appearance", "Switch Windows between dark and light mode.",
            [Req("mode", ParamType.Enum, "", "dark", "light", "toggle"),
             Opt("scope", ParamType.Enum, "default both", "both", "apps", "system")]),
        new("theme.get", "Appearance", "Current app theme.", [], "\"dark\" or \"light\"", ActionTraits.Query),

        // ── Power ───────────────────────────────────────────────────────────
        new("power.setMode", "Power", "Set the Windows 11 power mode (Settings › Power › Power mode).",
            [Req("mode", ParamType.Enum, "", "efficiency", "balanced", "performance")]),
        new("power.getMode", "Power", "Current power mode.", [], "\"efficiency\", \"balanced\" or \"performance\"", ActionTraits.Query),
        new("power.setPlan", "Power", "Activate a classic power plan by name or GUID (see the plan list under This PC).",
            [Opt("name", ParamType.String, "plan name, e.g. \"High performance\""), Opt("guid", ParamType.String, "plan GUID")]),
        new("power.getPlan", "Power", "Name of the active power plan.", [], "plan name", ActionTraits.Query),
        new("power.action", "Power", "Lock, sleep, hibernate, shut down, restart, sign out or turn off the display.",
            [Req("action", ParamType.Enum, "", "lock", "sleep", "hibernate", "shutdown", "restart", "signOut", "displayOff")],
            null, ActionTraits.Destructive),
        new("battery.get", "Power", "Battery information.",
            [Opt("field", ParamType.Enum, "default percent", "percent", "status")],
            "percent: \"0-100\"; status: \"Charging\", \"Plugged in\" or \"On battery\"", ActionTraits.Query),

        // ── Connectivity ────────────────────────────────────────────────────
        new("radio.set", "Connectivity", "Turn Wi-Fi or Bluetooth on/off.",
            [Req("kind", ParamType.Enum, "", "wifi", "bluetooth"), Req("state", ParamType.Enum, "", "on", "off", "toggle")]),
        new("radio.get", "Connectivity", "Whether Wi-Fi or Bluetooth is on.",
            [Req("kind", ParamType.Enum, "", "wifi", "bluetooth")], "\"on\" or \"off\"", ActionTraits.Query),

        // ── Input ───────────────────────────────────────────────────────────
        new("keys.send", "Input", "Press a keyboard shortcut. The panel hides first so keys reach the window the user was using.",
            [Req("keys", ParamType.String, "e.g. \"Win+Shift+S\", \"Ctrl+Shift+Esc\", \"Alt+Tab\", \"F11\", \"PrintScreen\"")],
            null, ActionTraits.Input),
        new("text.type", "Input", "Type text into the window the user was using.", [Req("text", ParamType.String, "text to type")],
            null, ActionTraits.Input),
        new("media.key", "Input", "Control media playback.",
            [Req("key", ParamType.Enum, "", "playPause", "next", "previous", "stop")]),
        new("clipboard.setText", "Input", "Copy text to the clipboard.", [Req("text", ParamType.String, "text")]),

        // ── Apps & links ────────────────────────────────────────────────────
        new("app.launch", "Apps", "Start a program, script or file.",
            [Req("path", ParamType.String, "exe name on PATH (e.g. \"notepad\"), full path, or shell:AppsFolder\\<AUMID>"),
             Opt("args", ParamType.String, "command-line arguments"), Opt("workingDir", ParamType.String, "working directory"),
             Opt("admin", ParamType.Bool, "run as administrator (UAC prompt)")]),
        new("app.close", "Apps", "Close all windows of a running program.",
            [Req("name", ParamType.String, "process name without .exe, e.g. \"spotify\""), Opt("force", ParamType.Bool, "kill instead of asking it to close")],
            null, ActionTraits.Destructive),
        new("app.isRunning", "Apps", "Whether a program is running.",
            [Req("name", ParamType.String, "process name without .exe")], "\"true\" or \"false\"", ActionTraits.Query),
        new("url.open", "Apps", "Open a URL or URI: https://…, mailto:, ms-settings:display, spotify:, etc.",
            [Req("url", ParamType.String, "the URL/URI")]),
        new("folder.open", "Apps", "Open a folder in File Explorer. Environment variables like %USERPROFILE% are expanded.",
            [Req("path", ParamType.String, "folder path")]),

        // ── Scripts, server and web ─────────────────────────────────────────
        new("shell.powershell", "Scripts", "Run a Windows PowerShell 5.1 script on this PC. Use only when no built-in action fits.",
            [Req("script", ParamType.String, "PowerShell code; non-interactive; for queries print only the value"),
             Opt("admin", ParamType.Bool, "run elevated (shows a UAC prompt each time)"),
             Opt("timeoutSeconds", ParamType.Int, "default 30")],
            "the script's standard output, trimmed", ActionTraits.Query | ActionTraits.Script),
        new("shell.cmd", "Scripts", "Run a cmd.exe command line on this PC.",
            [Req("command", ParamType.String, "command line"), Opt("timeoutSeconds", ParamType.Int, "default 30")],
            "standard output, trimmed", ActionTraits.Query | ActionTraits.Script),
        new("server.run", "Server", "Run a shell command on the user's connected SSH server (the machine the AI runs on).",
            [Req("command", ParamType.String, "bash command"), Opt("timeoutSeconds", ParamType.Int, "default 30")],
            "standard output, trimmed", ActionTraits.Query | ActionTraits.Server),
        new("http.request", "Web", "Send an HTTP request (home automation, webhooks, APIs).",
            [Req("url", ParamType.String, "URL"), Opt("method", ParamType.Enum, "default GET", "GET", "POST", "PUT", "PATCH", "DELETE"),
             Opt("body", ParamType.String, "request body"), Opt("contentType", ParamType.String, "default application/json when a body is set"),
             Opt("headers", ParamType.Object, "object of header name → value")],
            "response body (trimmed)", ActionTraits.Query | ActionTraits.Network),

        // ── Live data (instant, no scripts: good for dashboards that refresh every few seconds) ─────
        new("system.stats", "Live data", "CPU, memory, disk and uptime of this PC.",
            [Opt("metric", ParamType.Enum, "one value; omit to get them all as JSON {cpu, memory, memoryUsed, memoryTotal, disk, diskFree, diskTotal, uptime}",
                 "cpu", "memory", "memoryUsed", "memoryTotal", "disk", "diskFree", "diskTotal", "uptime", "processes"),
             Opt("drive", ParamType.String, "drive for disk values, default the Windows drive, e.g. \"D\"")],
            "cpu/memory/disk in percent (\"12.5\"), *Used/*Free/*Total in bytes, uptime in seconds", ActionTraits.Query),
        new("media.nowPlaying", "Live data", "What's playing in any app that shows in Windows' media flyout (Spotify, browsers, players).",
            [Opt("field", ParamType.Enum, "one value; omit to get JSON {title, artist, album, app, status, playing, art, position, duration}",
                 "title", "artist", "album", "app", "status", "playing", "art", "position", "duration")],
            "status is \"playing\", \"paused\" or \"none\"; art is a path to the cover image (use it as an image src); position/duration in seconds",
            ActionTraits.Query),
        new("time.now", "Live data", "The current date/time.", [Opt("format", ParamType.String, ".NET format, e.g. \"HH:mm\", \"dddd d MMMM\"; omit for ISO 8601")],
            "the formatted time", ActionTraits.Query),
        new("time.until", "Live data", "Seconds until a date/time (negative once it has passed) — countdowns; show with the |duration filter.",
            [Req("when", ParamType.String, "\"2026-12-24\", \"2026-12-24T18:00\" or a time of day \"18:00\"")], "seconds", ActionTraits.Query),
        new("network.ping", "Live data", "Ping a host.", [Req("host", ParamType.String, "host name or IP"), Opt("timeoutMs", ParamType.Int, "default 1500")],
            "round-trip time in ms, or \"offline\"", ActionTraits.Query | ActionTraits.Network),

        // ── Widget memory: values kept between runs (counters, checklists, a chosen mode) ─────────
        new("store.get", "Memory", "Read a value TileSmith keeps for your widgets.",
            [Req("key", ParamType.String, "name, e.g. \"water.glasses\""), Opt("default", ParamType.String, "value when it was never set")],
            "the stored value", ActionTraits.Query),
        new("store.set", "Memory", "Remember a value ({value} works for sliders and choices).",
            [Req("key", ParamType.String, "name"), Req("value", ParamType.String, "value to keep")], "the new value"),
        new("store.toggle", "Memory", "Flip a true/false value.", [Req("key", ParamType.String, "name")], "the new value"),
        new("store.add", "Memory", "Add to a number (counters).",
            [Req("key", ParamType.String, "name"), Req("delta", ParamType.Number, "e.g. 1 or -1"), Opt("min", ParamType.Number, "lowest allowed"), Opt("max", ParamType.Number, "highest allowed")],
            "the new number"),

        // ── Flow ────────────────────────────────────────────────────────────
        new("notify", "Flow", "Show a Windows notification.",
            [Req("title", ParamType.String, "title"), Opt("message", ParamType.String, "body text")]),
        new("sequence", "Flow", "Run several actions in order (stops at the first failure).",
            [Req("steps", ParamType.Actions, "array of actions")]),
        new("delay", "Flow", "Wait before the next step of a sequence.", [Req("ms", ParamType.Int, "milliseconds (max 10000)")]),
    ];

    private static readonly Dictionary<string, ActionInfo> ByType =
        All.ToDictionary(a => a.Type, StringComparer.OrdinalIgnoreCase);

    /// <summary>Common names models invent for built-in actions.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["powershell"] = "shell.powershell",
        ["ps"] = "shell.powershell",
        ["script"] = "shell.powershell",
        ["cmd"] = "shell.cmd",
        ["shell"] = "shell.cmd",
        ["ssh"] = "server.run",
        ["server.exec"] = "server.run",
        ["url"] = "url.open",
        ["open"] = "url.open",
        ["openUrl"] = "url.open",
        ["launch"] = "app.launch",
        ["run"] = "app.launch",
        ["hotkey"] = "keys.send",
        ["keys"] = "keys.send",
        ["sendKeys"] = "keys.send",
        ["display.resolution"] = "display.setResolution",
        ["display.setMode"] = "display.setResolution",
        ["resolution.set"] = "display.setResolution",
        ["resolution.get"] = "display.getResolution",
        ["volume.set"] = "audio.setVolume",
        ["volume.get"] = "audio.getVolume",
        ["http"] = "http.request",
        ["webhook"] = "http.request",
    };

    public static ActionInfo? Find(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        if (ByType.TryGetValue(type.Trim(), out var info)) return info;
        return Aliases.TryGetValue(type.Trim(), out var canonical) ? ByType[canonical] : null;
    }

    /// <summary>Compact reference used in the AI prompt.</summary>
    public static string DescribeForPrompt()
    {
        var sb = new StringBuilder();
        foreach (var group in All.GroupBy(a => a.Category))
        {
            sb.Append("### ").AppendLine(group.Key);
            if (group.Key == "Live data")
                sb.AppendLine("Any query action also takes \"select\": a path into JSON output (\"cpu\", \"items[0].name\") that becomes its result.");
            foreach (var a in group)
            {
                sb.Append("- ").Append(a.Type).Append(' ');
                sb.Append('{');
                sb.Append(string.Join(", ", a.Params.Select(DescribeParam)));
                sb.Append('}');
                sb.Append(" — ").Append(a.Description);
                if (a.Returns is not null) sb.Append(" Returns ").Append(a.Returns).Append('.');
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    private static string DescribeParam(ParamInfo p)
    {
        var type = p.Type switch
        {
            ParamType.Enum when p.Values is not null => string.Join("|", p.Values.Select(v => $"\"{v}\"")),
            ParamType.Actions => "Action[]",
            ParamType.Object => "object",
            ParamType.Int => "int",
            ParamType.Number => "number",
            ParamType.Bool => "bool",
            _ => "string",
        };
        var text = $"{p.Name}{(p.Required ? "" : "?")}: {type}";
        if (!string.IsNullOrEmpty(p.Description)) text += $" ({p.Description})";
        return text;
    }
}
