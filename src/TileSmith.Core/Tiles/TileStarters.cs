namespace TileSmith.Core.Tiles;

/// <summary>What this PC has, for the gallery: a brightness slider makes no sense on a desktop.</summary>
public sealed record TileEnvironment(bool HasBattery, bool HasBrightness, bool HasWifi, int Displays, bool ServerConfigured)
{
    public static readonly TileEnvironment Everything = new(true, true, true, 2, true);
}

/// <summary>
/// Hand-made designs: the gallery's one-tap widgets, the draft shown while the AI works, and the AI's examples of
/// what good looks like.
/// </summary>
public static class TileStarters
{
    public sealed record Starter(
        string Id,
        string Category,
        string Title,
        string Prompt,
        string[] Keywords,
        TileDesign Design,
        Func<TileEnvironment, bool>? Available = null)
    {
        public bool IsAvailable(TileEnvironment env) => Available?.Invoke(env) ?? true;
    }

    public static readonly string[] Categories = ["Quick settings", "Shortcuts", "This PC", "Weather & time", "Media & web", "Server"];

    private static TileDesign D(string name, TileSize size, TilePalette palette, TileSurface surface, int refresh, params TileRow[] rows) =>
        new()
        {
            Id = "starter_" + name.ToLowerInvariant().Replace(' ', '_'),
            Name = name,
            Size = size,
            Palette = palette,
            Surface = surface,
            Refresh = refresh,
            Rows = rows,
            CreatedAt = 0,
            UpdatedAt = 0,
        };

    private static TileRow Row(int h, params TileBlock[] cells) => new(h, cells);

    private static TileBlock B(BlockType type, string? bind = null) => new() { Type = type, Bind = bind };

    private static TileBlock Toggle(string bind, string label, string? icon = null) => new() { Type = BlockType.Toggle, Bind = bind, Label = label, Icon = icon };

    private static TileBlock Button(string label, string icon, string tap, string? confirm = null) =>
        new() { Type = BlockType.Button, Label = label, Icon = icon, Tap = tap, Confirm = confirm };

    private static Starter Shortcut(string id, string title, string icon, string tap, string prompt, string[] keywords, string? confirm = null) =>
        new(id, "Shortcuts", title, prompt, keywords,
            D(title, TileSize.Icon, TilePalette.Accent, TileSurface.Tonal, 0, Row(1, Button(title, icon, tap, confirm))));

    public static readonly IReadOnlyList<Starter> All =
    [
        // ── Quick settings ──────────────────────────────────────────────────────
        new("quick", "Quick settings", "Quick settings", "Wi-Fi, Bluetooth, dark mode and the microphone as four switches",
            ["quick settings", "toggles", "switches", "wifi and bluetooth", "control center", "action center"],
            D("Quick settings", TileSize.Wide, TilePalette.Accent, TileSurface.Soft, 0,
                Row(1, Toggle("sys.wifi", "Wi-Fi"), Toggle("sys.bluetooth", "Bluetooth")),
                Row(1, Toggle("sys.dark_mode", "Dark mode"), Toggle("sys.mic", "Microphone")))),
        new("wifi", "Quick settings", "Wi-Fi", "A switch for Wi-Fi", ["wifi", "wi-fi", "wireless", "wlan"],
            D("Wi-Fi", TileSize.Mini, TilePalette.Accent, TileSurface.Tonal, 0, Row(1, Toggle("sys.wifi", "Wi-Fi"))),
            env => env.HasWifi),
        new("bluetooth", "Quick settings", "Bluetooth", "A switch for Bluetooth", ["bluetooth", "bt"],
            D("Bluetooth", TileSize.Mini, TilePalette.Accent, TileSurface.Tonal, 0, Row(1, Toggle("sys.bluetooth", "Bluetooth")))),
        new("dark", "Quick settings", "Dark mode", "A switch between dark and light mode", ["dark mode", "light mode", "theme", "dark"],
            D("Dark mode", TileSize.Mini, TilePalette.Accent, TileSurface.Tonal, 0, Row(1, Toggle("sys.dark_mode", "Dark mode")))),
        new("mic", "Quick settings", "Microphone", "Mute the microphone in one click", ["microphone", "mic", "mute mic"],
            D("Microphone", TileSize.Mini, TilePalette.Accent, TileSurface.Tonal, 0, Row(1, Toggle("sys.mic", "Microphone")))),
        new("mute", "Quick settings", "Mute", "Mute the speakers", ["mute", "silence", "sound off"],
            D("Mute", TileSize.Icon, TilePalette.Accent, TileSurface.Tonal, 0, Row(1, Toggle("sys.mute", "Mute", "volume_off")))),
        new("volume", "Quick settings", "Volume", "A full-width volume slider", ["volume", "sound", "loudness", "audio"],
            D("Volume", TileSize.Bar, TilePalette.Accent, TileSurface.Tonal, 0, Row(1, B(BlockType.Slider, "sys.volume") with { Label = "Volume" }))),
        new("brightness", "Quick settings", "Brightness", "A full-width brightness slider", ["brightness", "screen brightness", "dim"],
            D("Brightness", TileSize.Bar, TilePalette.Accent, TileSurface.Tonal, 0, Row(1, B(BlockType.Slider, "sys.brightness") with { Label = "Brightness" })),
            env => env.HasBrightness),
        new("sound", "Quick settings", "Sound", "Volume as a slider with mute and the microphone under it",
            ["sound panel", "audio controls", "volume and mute"],
            D("Sound", TileSize.Wide, TilePalette.Accent, TileSurface.Soft, 0,
                Row(1, B(BlockType.Slider, "sys.volume") with { Label = "Volume" }),
                Row(1, Toggle("sys.mute", "Mute"), Toggle("sys.mic", "Microphone")))),
        new("powermode", "Quick settings", "Power mode", "Efficiency, balanced or performance", ["power mode", "performance", "efficiency", "battery saver", "balanced"],
            D("Power mode", TileSize.Bar, TilePalette.Accent, TileSurface.Tonal, 0, Row(1, B(BlockType.Choice, "sys.power_mode") with { Label = "Power mode" }))),
        new("resolution", "Quick settings", "Resolution", "Switch between your screen's resolutions", ["resolution", "screen size", "1080p", "1440p", "4k", "display mode"],
            D("Resolution", TileSize.Bar, TilePalette.Accent, TileSurface.Tonal, 0, Row(1, B(BlockType.Choice, "sys.resolution") with { Label = "Resolution" }))),
        new("refresh", "Quick settings", "Refresh rate", "Switch between your screen's refresh rates", ["refresh rate", "hz", "144hz", "165hz", "60hz"],
            D("Refresh rate", TileSize.Bar, TilePalette.Accent, TileSurface.Tonal, 0, Row(1, B(BlockType.Choice, "sys.refresh_rate") with { Label = "Refresh rate" }))),
        new("projection", "Quick settings", "Projection", "PC only, duplicate or extend (like Win+P)", ["projection", "second screen", "duplicate", "extend", "mirror", "projector"],
            D("Projection", TileSize.Bar, TilePalette.Accent, TileSurface.Tonal, 0, Row(1, B(BlockType.Choice, "sys.projection") with { Label = "Projection" })),
            env => env.Displays > 1),

        // ── Shortcuts ───────────────────────────────────────────────────────────
        new("shortcuts", "Shortcuts", "Shortcuts", "Snip, files, settings and lock in one row", ["shortcuts", "quick actions", "launcher", "buttons"],
            D("Shortcuts", TileSize.Bar, TilePalette.Accent, TileSurface.Soft, 0,
                Row(1, Button("Snip", "screenshot_region", "keys:Win+Shift+S"), Button("Files", "folder_open", "keys:Win+E"),
                    Button("Settings", "settings", "settings:"), Button("Lock", "lock", "power:lock")))),
        Shortcut("lock", "Lock", "lock", "power:lock", "Lock the PC", ["lock", "lock screen"]),
        Shortcut("snip", "Snip", "screenshot_region", "keys:Win+Shift+S", "Take a screenshot of an area", ["snip", "screenshot", "screen grab", "capture"]),
        Shortcut("sleep", "Sleep", "bedtime", "power:sleep", "Put the PC to sleep", ["sleep", "suspend"], "Put the PC to sleep now?"),
        Shortcut("restart", "Restart", "restart_alt", "power:restart", "Restart the PC (asks first)", ["restart", "reboot"], "Restart the PC now? Unsaved work will be lost."),
        Shortcut("shutdown", "Shut down", "power_settings_new", "power:shutdown", "Turn the PC off (asks first)", ["shut down", "shutdown", "power off", "turn off"], "Shut down the PC now? Unsaved work will be lost."),
        Shortcut("screenoff", "Screen off", "desktop_access_disabled", "power:displayoff", "Turn the screen off until you move the mouse", ["screen off", "display off", "monitor off"]),
        Shortcut("taskmgr", "Tasks", "monitoring", "keys:Ctrl+Shift+Esc", "Open Task Manager", ["task manager", "processes"]),
        Shortcut("downloads", "Downloads", "download", "folder:%USERPROFILE%\\Downloads", "Open your Downloads folder", ["downloads", "download folder"]),
        Shortcut("clipboard", "Clipboard", "content_paste", "keys:Win+V", "Open clipboard history", ["clipboard", "paste history"]),
        Shortcut("emoji", "Emoji", "mood", "keys:Win+.", "Open the emoji panel", ["emoji", "emoticons"]),
        Shortcut("calc", "Calculator", "calculate", "run:calc", "Open Calculator", ["calculator", "calc"]),

        // ── This PC ─────────────────────────────────────────────────────────────
        new("pulse", "This PC", "PC pulse", "CPU and memory as rings, the disk, and CPU over the last two minutes",
            ["dashboard", "system", "cpu", "memory", "ram", "disk", "performance", "monitor", "usage", "load", "gauges"],
            D("PC pulse", TileSize.Wide, TilePalette.Mint, TileSurface.Tonal, 2,
                Row(1, B(BlockType.Ring, "pc.cpu") with { Label = "CPU" }, B(BlockType.Ring, "pc.memory") with { Label = "Memory", Tone = TileTone.Tertiary },
                    B(BlockType.Metric, "pc.disk") with { Label = "Disk", Weight = 2, Tone = TileTone.Secondary }),
                Row(1, B(BlockType.Chart, "pc.cpu_history") with { Label = "CPU, 2 min", Variant = "line" }))),
        new("cpu", "This PC", "CPU", "CPU load with a line of the last two minutes", ["cpu", "processor", "cpu graph", "cpu load"],
            D("CPU", TileSize.Mini, TilePalette.Mint, TileSurface.Tonal, 2,
                Row(1, B(BlockType.Metric, "pc.cpu") with { Label = "CPU", Style = BlockStyle.Plain }, B(BlockType.Chart, "pc.cpu_history") with { Style = BlockStyle.Plain, Variant = "line" }))),
        new("apps", "This PC", "Busiest apps", "The apps using the most CPU right now", ["busiest apps", "top apps", "processes", "what's using", "cpu hogs"],
            D("Busiest apps", TileSize.Small, TilePalette.Mint, TileSurface.Tonal, 3, Row(1, B(BlockType.List, "pc.top_cpu") with { Label = "Busiest apps", Max = 4 }))),
        new("battery", "This PC", "Battery", "Battery as a ring with how long Windows has been up",
            ["battery", "charge", "charging", "power left"],
            D("Battery", TileSize.Mini, TilePalette.Accent, TileSurface.Tonal, 30,
                Row(1, B(BlockType.Ring, "pc.battery") with { Style = BlockStyle.Plain }, B(BlockType.Metric, "pc.uptime") with { Label = "Up", Tone = TileTone.Secondary })),
            env => env.HasBattery),
        new("network", "This PC", "Network", "The connection, ping and live download speed",
            ["network", "internet", "wifi name", "ip address", "speed", "bandwidth", "download speed", "ping", "latency"],
            D("Network", TileSize.Wide, TilePalette.Ocean, TileSurface.Tonal, 2,
                Row(1, B(BlockType.Metric, "net.status") with { Label = "Network", Weight = 2 }, B(BlockType.Metric, "net.ping") with { Label = "Ping", Tone = TileTone.Tertiary }),
                Row(1, B(BlockType.Chart, "net.speed_history") with { Label = "Download", Weight = 2, Variant = "line" }, B(BlockType.Metric, "net.down") with { Label = "Down", Style = BlockStyle.Plain }))),

        // ── Weather & time ──────────────────────────────────────────────────────
        new("today", "Weather & time", "Today", "The time, the date and the weather where you are",
            ["day", "morning", "today", "glance", "clock", "time", "date"],
            D("Today", TileSize.Wide, TilePalette.Accent, TileSurface.Tonal, 60,
                Row(1, B(BlockType.Clock, "time.clock") with { Weight = 2, Style = BlockStyle.Plain },
                    B(BlockType.Badge, "weather.now") with { Shape = TileShape.Sunny, Style = BlockStyle.Shape, Tone = TileTone.Tertiary }),
                Row(1, B(BlockType.Date, "time.date") with { Weight = 2, Style = BlockStyle.Plain }, B(BlockType.Metric, "weather.rain") with { Label = "Rain" }))),
        new("weather", "Weather & time", "Weather", "The weather now, the next hours and the next days",
            ["weather", "forecast", "temperature", "next days", "week"],
            D("Weather", TileSize.Large, TilePalette.Ocean, TileSurface.Tonal, 600,
                Row(2, B(BlockType.Metric, "weather.now") with { Shape = TileShape.Cookie12, Style = BlockStyle.Shape, Weight = 2 },
                    B(BlockType.Metric, "weather.high_low") with { Label = "Today", Weight = 2, Style = BlockStyle.Plain },
                    B(BlockType.Chart, "weather.hourly") with { Weight = 3, Style = BlockStyle.Plain, Variant = "line" }),
                Row(3, B(BlockType.List, "weather.days") with { Max = 5 }))),
        new("rain", "Weather & time", "Rain check", "Will it rain today, the temperature now and the next hours",
            ["rain", "umbrella", "precipitation", "will it rain"],
            D("Rain check", TileSize.Bar, TilePalette.Ocean, TileSurface.Tonal, 600,
                Row(1, B(BlockType.Badge, "weather.now") with { Shape = TileShape.Cookie12, Style = BlockStyle.Shape },
                    B(BlockType.Metric, "weather.rain") with { Weight = 2, Style = BlockStyle.Plain, Label = "Rain" },
                    B(BlockType.Chart, "weather.hourly") with { Weight = 2, Style = BlockStyle.Plain, Variant = "line" }))),
        new("countdown", "Weather & time", "Countdown", "A countdown to a day that matters",
            ["countdown", "days until", "how long until", "christmas", "birthday", "vacation", "deadline"],
            D("Countdown", TileSize.Small, TilePalette.Berry, TileSurface.Tinted, 60,
                Row(1, B(BlockType.Badge) with { Icon = "celebration", Shape = TileShape.SoftBurst, Style = BlockStyle.Shape },
                    B(BlockType.Countdown, "time.countdown") with { Text = "2026-12-24T18:00", Label = "Christmas", Style = BlockStyle.Plain, Weight = 2 }))),
        new("water", "Weather & time", "Water", "Count glasses of water with plus and minus buttons",
            ["water", "counter", "count", "tally", "glasses", "habit", "drink"],
            D("Water", TileSize.Small, TilePalette.Ocean, TileSurface.Tonal, 0,
                Row(2, B(BlockType.Ring, "store.value") with { Query = "water", Goal = 8, Label = "Glasses" }),
                Row(1, Button("", "remove", "store:water-1") with { Tone = TileTone.Secondary }, Button("", "add", "store:water+1")))),

        // ── Media & web ─────────────────────────────────────────────────────────
        new("player", "Media & web", "Now playing", "What's playing, with the cover art and previous, play/pause and next",
            ["music", "spotify", "playing", "song", "player", "podcast", "now playing", "track", "album", "media controls"],
            D("Now playing", TileSize.Wide, TilePalette.Accent, TileSurface.Tonal, 2, Row(1, B(BlockType.Media, "media.now")))),
        new("headline", "Media & web", "Tech headline", "Today's most important tech headline, searched on the web",
            ["headline", "tech news", "web", "search", "stocks", "price", "bitcoin"],
            D("Tech headline", TileSize.Wide, TilePalette.Lavender, TileSurface.Tinted, 3600,
                Row(1, B(BlockType.Badge) with { Icon = "public", Shape = TileShape.Flower, Style = BlockStyle.Shape },
                    B(BlockType.Ai, "ai.text") with { Weight = 3, Text = "The single most important tech headline today, in one sentence.", Web = true, Every = 7200, Style = BlockStyle.Plain }))),
        new("news", "Media & web", "Headlines", "The latest headlines from a news feed, with pictures, to scroll through",
            ["news", "headlines", "rss", "feed", "articles", "bbc", "nrk", "blog"],
            D("Headlines", TileSize.Large, TilePalette.Ocean, TileSurface.Tonal, 900,
                Row(1, B(BlockType.List, "web.feed") with { Url = "https://feeds.bbci.co.uk/news/rss.xml", Variant = "scroll", Max = 20, Label = "BBC News" }))),

        // ── Server ──────────────────────────────────────────────────────────────
        new("server", "Server", "Server pulse", "Your server: online or not, CPU and memory, disk and uptime",
            ["server", "homelab", "ssh", "linux", "nas", "server status"],
            D("Server pulse", TileSize.Wide, TilePalette.Mint, TileSurface.Tonal, 15,
                Row(1, B(BlockType.Metric, "server.status") with { Weight = 2, Style = BlockStyle.Filled },
                    B(BlockType.Ring, "server.cpu") with { Label = "CPU" }, B(BlockType.Ring, "server.mem") with { Label = "Memory", Tone = TileTone.Tertiary }),
                Row(1, B(BlockType.Bar, "server.disk") with { Label = "Disk", Weight = 2 }, B(BlockType.Metric, "server.uptime") with { Label = "Up", Style = BlockStyle.Plain })),
            env => env.ServerConfigured),
        new("containers", "Server", "Containers", "The Docker containers on your server, stopped ones first",
            ["docker", "containers", "services", "compose"],
            D("Containers", TileSize.Tall, TilePalette.Mint, TileSurface.Tonal, 30,
                Row(1, B(BlockType.List, "server.containers") with { Label = "Containers", Variant = "scroll", Max = 30 })),
            env => env.ServerConfigured),
    ];

    public static Starter? ById(string? id) => All.FirstOrDefault(s => s.Id == id);

    /// <summary>A fresh copy of <paramref name="s"/>'s design to keep: its own id, now as its time.</summary>
    public static TileDesign Instantiate(Starter s)
    {
        var now = TileDesign.Now();
        return s.Design with { Id = TileDesign.NewId(), CreatedAt = now, UpdatedAt = now };
    }

    /// <summary>The starter whose words best match <paramref name="prompt"/>, for the draft shown while the AI works.</summary>
    public static Starter? Match(string prompt)
    {
        var p = prompt.ToLowerInvariant();
        var best = All.Select(s => (s, Score: s.Keywords.Count(p.Contains))).Where(x => x.Score > 0).OrderByDescending(x => x.Score).FirstOrDefault();
        return best.s;
    }

    /// <summary>The sites the starters read, which the gallery may allow on their own.</summary>
    public static IEnumerable<string> SitesOf(TileDesign d) => d.Blocks.Select(b => b.Url).OfType<string>();
}
