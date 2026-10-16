using System.Text;

namespace TileSmith.Core.Tiles;

/// <summary>The kind of value a source gives, which decides the blocks it fits.</summary>
public enum ValueKind { Number, Text, List, Series, Time, Image }

/// <summary>What a source can change, besides showing it: a switch, a level or one of a few options.</summary>
public enum SourceControl { None, Switch, Level, Choice }

/// <summary>Something a source can't be read without; <see cref="TileNeeds"/> checks them and the Studio asks for them.</summary>
public enum TileNeed { Location, Server, Ai, Command, Site }

public static class TileNeeds
{
    public static string Label(TileNeed need) => need switch
    {
        TileNeed.Location => "Your town, for the weather",
        TileNeed.Server => "Your server, in Settings › Server",
        TileNeed.Ai => "An AI, in Settings › AI",
        TileNeed.Command => "Your OK to run the command",
        TileNeed.Site => "Your OK to read the site",
        _ => need.ToString(),
    };
}

public enum SourceGroup { Time, Pc, Settings, Network, Media, Weather, Server, Web, Memory, Scripts, Custom }

public static class SourceGroups
{
    public static string Label(SourceGroup g) => g switch
    {
        SourceGroup.Time => "Time",
        SourceGroup.Pc => "This PC",
        SourceGroup.Settings => "Quick settings",
        SourceGroup.Network => "Network",
        SourceGroup.Media => "Now playing",
        SourceGroup.Weather => "Weather",
        SourceGroup.Server => "Your server",
        SourceGroup.Web => "Web & AI",
        SourceGroup.Memory => "Counters & notes",
        SourceGroup.Scripts => "PowerShell",
        _ => "Older widgets",
    };
}

/// <summary>
/// One thing a widget can show (and maybe change). <see cref="FloorSeconds"/> is how often it is worth reading (a
/// refresh any faster reads the same answer); <see cref="Sample"/> fills previews until the real value is in.
/// </summary>
public sealed record TileSource(
    string Key,
    string Label,
    SourceGroup Group,
    ValueKind Kind,
    string Hint,
    int FloorSeconds,
    TileNeed[] Needs,
    string Icon,
    TileValue Sample,
    SourceControl Control = SourceControl.None,
    bool Hidden = false);

/// <summary>
/// Every source a block can bind to. The AI is handed this list (with what is already allowed) and may only use these
/// keys; <see cref="TileResolver"/> reads them.
/// </summary>
public static class TileSources
{
    private static TileValue N(string? text, double? number, double? goal = null, string? unit = null, string? sub = null, string? icon = null) =>
        new() { Text = text, Number = number, Goal = goal, Unit = unit, Sub = sub, Icon = icon };

    private static TileValue Rows(params TileItem[] rows) => new() { Items = rows };

    private static TileValue Line(double[] values, string? start, string? end, string? text, string? unit) =>
        new() { Series = values, SeriesStart = start, SeriesEnd = end, Text = text, Unit = unit };

    private static long InMinutes(long m) => DateTimeOffset.UtcNow.AddMinutes(m).ToUnixTimeMilliseconds();

    private static readonly TileNeed[] None = [];

    private static TileSource S(string key, string label, SourceGroup group, ValueKind kind, int floor, string icon, string hint, TileValue sample,
        TileNeed[]? needs = null, SourceControl control = SourceControl.None, bool hidden = false) =>
        new(key, label, group, kind, hint, floor, needs ?? None, icon, sample, control, hidden);

    public static readonly IReadOnlyList<TileSource> All =
    [
        // ── Time ────────────────────────────────────────────────────────────────
        S("time.clock", "Time", SourceGroup.Time, ValueKind.Time, 60, "schedule", "The time now; use a clock block, it ticks by itself",
            new() { Text = "14:05" }),
        S("time.date", "Date", SourceGroup.Time, ValueKind.Text, 60, "calendar_today", "Today's date; use a date block",
            new() { Text = "Thu 15 Oct" }),
        S("time.countdown", "Countdown to a date", SourceGroup.Time, ValueKind.Time, 60, "timer",
            "Counts down to the date-time in the block's text, as ISO (2026-12-24T18:00) or a time of day (17:00, the next one); label says what for",
            new() { Text = "Christmas", At = InMinutes(60 * 24 * 70) }),

        // ── This PC ─────────────────────────────────────────────────────────────
        S("pc.cpu", "CPU", SourceGroup.Pc, ValueKind.Number, 2, "memory", "Processor load in percent (goal 100), sub the core count",
            N("23%", 23, 100, "%", "8 cores")),
        S("pc.memory", "Memory", SourceGroup.Pc, ValueKind.Number, 3, "memory_alt", "Memory in use in percent (goal 100), sub like 9.2 of 16 GB",
            N("58%", 58, 100, "%", "9.2 of 16 GB")),
        S("pc.disk", "Disk", SourceGroup.Pc, ValueKind.Number, 60, "hard_drive", "A drive's used space in percent (goal 100), sub the free space; query picks the drive letter (default the Windows drive)",
            N("71%", 71, 100, "%", "C: · 210 GB free")),
        S("pc.uptime", "Uptime", SourceGroup.Pc, ValueKind.Text, 60, "history", "How long since Windows started, like 3d 4h",
            new() { Text = "3d 4h", Sub = "Since Monday" }),
        S("pc.battery", "Battery", SourceGroup.Pc, ValueKind.Number, 30, "battery_full", "Battery percent (goal 100), sub Charging / On battery; active while charging. Empty on desktops",
            N("78%", 78, 100, "%", "Charging", "battery_charging_full") with { Active = true }),
        S("pc.cpu_history", "CPU history", SourceGroup.Pc, ValueKind.Series, 2, "monitoring", "CPU percent over the last two minutes, newest last; text the load now",
            Line([12, 18, 22, 15, 40, 33, 21, 19, 28, 23, 17, 23, 31, 26, 20, 24], "2 min", "Now", "23%", "%")),
        S("pc.memory_history", "Memory history", SourceGroup.Pc, ValueKind.Series, 3, "monitoring", "Memory percent over the last two minutes, newest last",
            Line([54, 55, 55, 57, 58, 58, 57, 59, 60, 58, 58, 58], "2 min", "Now", "58%", "%")),
        S("pc.top_cpu", "Busiest apps", SourceGroup.Pc, ValueKind.List, 3, "leaderboard", "The apps using the most CPU right now: name, memory, CPU % at the end; max rows",
            Rows(new TileItem("Chrome", "1.8 GB", "12%"), new TileItem("Code", "940 MB", "6%"), new TileItem("Teams", "620 MB", "3%"), new TileItem("Explorer", "180 MB", "1%"))),
        S("pc.top_memory", "Memory hogs", SourceGroup.Pc, ValueKind.List, 5, "leaderboard", "The apps using the most memory: name, process count, memory at the end; max rows",
            Rows(new TileItem("Chrome", "24 processes", "1.8 GB"), new TileItem("Code", "9 processes", "940 MB"), new TileItem("Teams", "6 processes", "620 MB"))),

        // ── Quick settings: read and change ─────────────────────────────────────
        S("sys.wifi", "Wi-Fi", SourceGroup.Settings, ValueKind.Text, 5, "wifi", "Wi-Fi on or off (a toggle switches it); text the network name when connected. Empty without Wi-Fi",
            new() { Text = "Home", Sub = "Connected", Active = true, Icon = "wifi" }, control: SourceControl.Switch),
        S("sys.bluetooth", "Bluetooth", SourceGroup.Settings, ValueKind.Text, 5, "bluetooth", "Bluetooth on or off (a toggle switches it). Empty without Bluetooth",
            new() { Text = "On", Active = true }, control: SourceControl.Switch),
        S("sys.dark_mode", "Dark mode", SourceGroup.Settings, ValueKind.Text, 5, "dark_mode", "Windows dark mode on or off (a toggle switches apps and Windows)",
            new() { Text = "Dark", Active = true, Icon = "dark_mode" }, control: SourceControl.Switch),
        S("sys.mute", "Mute", SourceGroup.Settings, ValueKind.Text, 2, "volume_off", "Speakers muted or not; active when muted (a toggle mutes)",
            new() { Text = "Sound on", Active = false, Icon = "volume_up" }, control: SourceControl.Switch),
        S("sys.mic", "Microphone", SourceGroup.Settings, ValueKind.Text, 2, "mic", "The microphone; active when it is on, off when muted (a toggle mutes it)",
            new() { Text = "On", Active = true, Icon = "mic" }, control: SourceControl.Switch),
        S("sys.volume", "Volume", SourceGroup.Settings, ValueKind.Number, 2, "volume_up", "Speaker volume 0-100 (a slider sets it)",
            N("42%", 42, 100, "%") with { Min = 0, Max = 100 }, control: SourceControl.Level),
        S("sys.brightness", "Brightness", SourceGroup.Settings, ValueKind.Number, 3, "brightness_6", "Built-in screen brightness 0-100 (a slider sets it). Empty on desktops",
            N("70%", 70, 100, "%") with { Min = 0, Max = 100 }, control: SourceControl.Level),
        S("sys.power_mode", "Power mode", SourceGroup.Settings, ValueKind.Text, 5, "bolt", "Windows power mode: Efficiency, Balanced or Performance (a choice sets it)",
            new() { Text = "Balanced", Selected = "balanced", Options = [new("Efficiency", "efficiency"), new("Balanced", "balanced"), new("Performance", "performance")] },
            control: SourceControl.Choice),
        S("sys.resolution", "Resolution", SourceGroup.Settings, ValueKind.Text, 10, "aspect_ratio", "A display's resolution (a choice switches it; options default to its top resolutions, or list your own with values like 2560x1440); query picks the display: internal, external, primary or 1, 2…",
            new() { Text = "2560 × 1440", Selected = "2560x1440", Options = [new("2560 × 1440", "2560x1440"), new("1920 × 1200", "1920x1200"), new("1920 × 1080", "1920x1080")] },
            control: SourceControl.Choice),
        S("sys.refresh_rate", "Refresh rate", SourceGroup.Settings, ValueKind.Text, 10, "speed", "A display's refresh rate (a choice switches it; options default to its rates, values in Hz like 165); query picks the display",
            new() { Text = "165 Hz", Selected = "165", Options = [new("165 Hz", "165"), new("120 Hz", "120"), new("60 Hz", "60")] },
            control: SourceControl.Choice),
        S("sys.projection", "Projection", SourceGroup.Settings, ValueKind.Text, 30, "cast", "Win+P: PC only, Duplicate, Extend or Second screen only (a choice switches it)",
            new() { Text = "Extend", Options = [new("PC only", "pcScreenOnly"), new("Duplicate", "duplicate"), new("Extend", "extend"), new("Second", "secondScreenOnly")] },
            control: SourceControl.Choice),
        S("sys.vpn", "VPN", SourceGroup.Settings, ValueKind.Text, 10, "vpn_lock", "A VPN connection set up in Windows Settings: connected or not (a toggle connects it); query names it (default the first)",
            new() { Text = "Work VPN", Sub = "Connected", Active = true }, control: SourceControl.Switch),
        S("sys.app", "An app", SourceGroup.Settings, ValueKind.Text, 5, "apps", "Whether an app is running (a toggle starts or closes it); query is its program name, like spotify or obs64",
            new() { Text = "Spotify", Sub = "Running", Active = true }, control: SourceControl.Switch),

        // ── Network ─────────────────────────────────────────────────────────────
        S("net.status", "Network", SourceGroup.Network, ValueKind.Text, 5, "wifi", "The connection: text the Wi-Fi name or Ethernet or Offline, sub the IP address, active when online, number the Wi-Fi signal %",
            new() { Text = "Home", Sub = "192.168.1.24", Active = true, Number = 84, Goal = 100, Unit = "%", Icon = "wifi" }),
        S("net.down", "Download speed", SourceGroup.Network, ValueKind.Number, 2, "download", "Download speed right now, text like 12.4 MB/s (number in bytes per second)",
            new() { Text = "12.4 MB/s", Number = 13_000_000, Sub = "Download", Icon = "download" }),
        S("net.up", "Upload speed", SourceGroup.Network, ValueKind.Number, 2, "upload", "Upload speed right now, text like 1.2 MB/s",
            new() { Text = "1.2 MB/s", Number = 1_250_000, Sub = "Upload", Icon = "upload" }),
        S("net.speed_history", "Network history", SourceGroup.Network, ValueKind.Series, 2, "network_check", "Download speed over the last two minutes (bytes/s), text the speed now",
            Line([1, 3, 2, 8, 12, 6, 4, 9, 14, 11, 5, 7], "2 min", "Now", "7.1 MB/s", null)),
        S("net.ping", "Ping", SourceGroup.Network, ValueKind.Number, 5, "network_ping", "Round trip to a host in ms (text like 14 ms, or Offline); query is the host (default 1.1.1.1); active when it answers",
            new() { Text = "14 ms", Number = 14, Unit = "ms", Active = true, Sub = "1.1.1.1" }),
        S("net.ip", "IP address", SourceGroup.Network, ValueKind.Text, 60, "lan", "This PC's local IP address, sub the adapter",
            new() { Text = "192.168.1.24", Sub = "Wi-Fi" }),

        // ── Now playing ─────────────────────────────────────────────────────────
        S("media.now", "Now playing", SourceGroup.Media, ValueKind.Text, 2, "music_note", "What plays in any app (Spotify, browsers, players): text the track, sub the artist, image the cover art, active while playing. A media block adds previous / play-pause / next",
            new() { Text = "Espresso", Sub = "Sabrina Carpenter", Active = true, Icon = "music_note" }),

        // ── Weather (Open-Meteo, no key) ────────────────────────────────────────
        S("weather.now", "Weather now", SourceGroup.Weather, ValueKind.Number, 600, "partly_cloudy_day", "Temperature now, text like 14°, sub the sky, icon the sky; query is the town (default the town in Settings)",
            N("14°", 14, null, "°", "Partly cloudy", "partly_cloudy_day"), [TileNeed.Location]),
        S("weather.feels", "Feels like", SourceGroup.Weather, ValueKind.Number, 600, "device_thermostat", "Feels-like temperature",
            N("12°", 12, null, "°", "Feels like"), [TileNeed.Location]),
        S("weather.high_low", "High and low", SourceGroup.Weather, ValueKind.Text, 600, "thermostat", "Today's high and low, text like 17° / 9°",
            new() { Text = "17° / 9°", Sub = "High / low" }, [TileNeed.Location]),
        S("weather.rain", "Rain", SourceGroup.Weather, ValueKind.Number, 600, "rainy", "Today's chance of rain in percent (goal 100), text like 70%, sub when it comes",
            N("70%", 70, 100, "%", "Rain at 15:00", "rainy"), [TileNeed.Location]),
        S("weather.wind", "Wind", SourceGroup.Weather, ValueKind.Number, 600, "air", "Wind speed now, text like 6 m/s",
            N("6 m/s", 6, null, "m/s", "Wind", "air"), [TileNeed.Location]),
        S("weather.humidity", "Humidity", SourceGroup.Weather, ValueKind.Number, 600, "humidity_percentage", "Relative humidity in percent (goal 100)",
            N("64%", 64, 100, "%", "Humidity"), [TileNeed.Location]),
        S("weather.sun", "Sunrise and sunset", SourceGroup.Weather, ValueKind.Text, 600, "wb_twilight", "The next sunrise or sunset, text like 18:42, sub which",
            new() { Text = "18:42", Sub = "Sunset", Icon = "wb_twilight" }, [TileNeed.Location]),
        S("weather.hourly", "Next 12 hours", SourceGroup.Weather, ValueKind.Series, 600, "show_chart", "Temperature each hour for the next 12 hours",
            Line([14, 15, 15, 16, 15, 14, 13, 12, 11, 10, 10, 9], "Now", "+12 h", "14°", "°"), [TileNeed.Location]),
        S("weather.days", "Next days", SourceGroup.Weather, ValueKind.List, 600, "calendar_month", "The next days: day, sky, high / low at the end, an icon each",
            Rows(new TileItem("Today", "Rain", "13° / 8°", Icon: "rainy"), new TileItem("Sat", "Sunny", "16° / 7°", Icon: "sunny"), new TileItem("Sun", "Cloudy", "14° / 9°", Icon: "cloud"), new TileItem("Mon", "Showers", "12° / 8°", Icon: "rainy_light")),
            [TileNeed.Location]),

        // ── Your server (SSH, Settings › Server) ────────────────────────────────
        S("server.status", "Server", SourceGroup.Server, ValueKind.Text, 30, "dns", "One word on the server: Online or Offline, sub its name and uptime; active when online",
            new() { Text = "Online", Sub = "t3lluz · up 12d", Active = true, Icon = "check_circle" }, [TileNeed.Server]),
        S("server.cpu", "Server CPU", SourceGroup.Server, ValueKind.Number, 15, "memory", "The server's CPU load percent (goal 100), sub the load average",
            N("23%", 23, 100, "%", "Load 0.41"), [TileNeed.Server]),
        S("server.mem", "Server memory", SourceGroup.Server, ValueKind.Number, 15, "memory_alt", "The server's memory in use percent (goal 100)",
            N("58%", 58, 100, "%", "9.2 of 16 GB"), [TileNeed.Server]),
        S("server.disk", "Server disk", SourceGroup.Server, ValueKind.Number, 60, "hard_drive", "The server's root disk used percent (goal 100), sub the free space",
            N("71%", 71, 100, "%", "210 GB free"), [TileNeed.Server]),
        S("server.uptime", "Server uptime", SourceGroup.Server, ValueKind.Text, 60, "history", "How long the server has been up, like 12d 4h",
            new() { Text = "12d 4h", Sub = "Uptime" }, [TileNeed.Server]),
        S("server.containers", "Containers", SourceGroup.Server, ValueKind.List, 30, "deployed_code", "Docker containers on the server, stopped ones first: name, image, state at the end; number is how many run",
            new() { Number = 31, Goal = 32, Items = [new("qbittorrent", "linuxserver/qbittorrent", "Exited", "#E34671"), new("jellyfin", "jellyfin/jellyfin", "Up 3d", "#3FA266"), new("sonarr", "linuxserver/sonarr", "Up 3d", "#3FA266")] },
            [TileNeed.Server]),
        S("server.cmd", "Command output", SourceGroup.Server, ValueKind.Text, 15, "terminal", "The output of a short read-only shell command on the server (set cmd); you approve it first. A number in the output becomes the value; JSON works like ps.value",
            new() { Text = "42", Sub = "Output" }, [TileNeed.Server, TileNeed.Command]),

        // ── Web & AI ────────────────────────────────────────────────────────────
        S("ai.text", "AI line", SourceGroup.Web, ValueKind.Text, 3600, "auto_awesome", "Your AI answers the block's text as an ask on every refresh (default hourly); web:true lets it search the web; it also sees this widget's other values",
            new() { Text = "Busy morning, then free after 15:00. Rain later, so take a jacket." }, [TileNeed.Ai]),
        S("ai.list", "AI rows", SourceGroup.Web, ValueKind.List, 3600, "auto_awesome", "Your AI answers the block's text with up to 8 rows (title, sub, trailing, link) on every refresh (default hourly); web:true lets it search first: scores, fixtures, headlines, prices, releases, anything",
            Rows(new TileItem("Liverpool 2–1 Arsenal", "Full time", "FT"), new TileItem("Man City – Spurs", "Kick-off", "18:30"), new TileItem("Brentford – Chelsea", "Tomorrow", "16:00")), [TileNeed.Ai]),
        S("web.json", "Any web address", SourceGroup.Web, ValueKind.Text, 300, "public", "Reads a JSON value from url at path (data.items[0].price); when path points at a list, fields maps each row's title, sub, trailing, image and link (paths inside one item) so it fills a list or carousel. You allow the site first. A number becomes the value",
            new() { Text = "1 042", Number = 1042, Sub = "From the web" }, [TileNeed.Site]),
        S("web.feed", "RSS or Atom feed", SourceGroup.Web, ValueKind.List, 900, "rss_feed", "Any RSS or Atom feed at url (news, blogs, podcasts, a subreddit's .rss, a YouTube channel's feeds/videos.xml?channel_id=…): title, source, age, picture; a click opens it. You allow the site first",
            Rows(new TileItem("Windows 11 gets a new Start menu", "The Verge", "3h"), new TileItem("Pixel Drop: October", "The Keyword", "1d")), [TileNeed.Site]),
        S("web.image", "Picture from the web", SourceGroup.Web, ValueKind.Image, 900, "image", "The image at url (a webcam, a radar map, a comic, a chart), fetched again each refresh; you allow the site first",
            new() { Text = "Webcam", Sub = "Live" }, [TileNeed.Site]),

        // ── Counters & notes ────────────────────────────────────────────────────
        S("store.value", "A kept value", SourceGroup.Memory, ValueKind.Number, 1, "add_circle", "A number or note this widget keeps, named in query (water, done); buttons change it with tap store:water+1, store:water-1, store:water=0 or store:done! (flip). goal turns it into progress",
            N(null, 3, 8, null, "glasses today")),

        // ── PowerShell ──────────────────────────────────────────────────────────
        S("ps.value", "PowerShell", SourceGroup.Scripts, ValueKind.Text, 5, "terminal", "Runs a short read-only Windows PowerShell 5.1 query in cmd (you approve it first) for anything no source covers. It prints one value (a number becomes the value), or compact JSON: {\"text\",\"number\",\"goal\",\"unit\",\"sub\",\"icon\",\"active\"} fills those, an array of {\"title\",\"sub\",\"trailing\"} fills a list",
            new() { Text = "42", Sub = "Output" }, [TileNeed.Command]),

        // ── Widgets from TileSmith 0.4 ──────────────────────────────────────────
        S("custom.state", "Action output", SourceGroup.Custom, ValueKind.Text, 3, "tune", "The output of the block's own state action",
            new() { Text = "—" }, hidden: true),
    ];

    private static readonly Dictionary<string, TileSource> ByKeyMap = All.ToDictionary(s => s.Key);

    /// <summary>Names an AI (or an older design) may use for a source that has another key.</summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cpu"] = "pc.cpu",
        ["system.cpu"] = "pc.cpu",
        ["pc.ram"] = "pc.memory",
        ["pc.mem"] = "pc.memory",
        ["memory"] = "pc.memory",
        ["system.memory"] = "pc.memory",
        ["disk"] = "pc.disk",
        ["battery"] = "pc.battery",
        ["phone.battery"] = "pc.battery",
        ["pc.top"] = "pc.top_cpu",
        ["pc.apps"] = "pc.top_cpu",
        ["system.top"] = "pc.top_cpu",
        ["pc.cpu_graph"] = "pc.cpu_history",
        ["volume"] = "sys.volume",
        ["audio.volume"] = "sys.volume",
        ["brightness"] = "sys.brightness",
        ["display.brightness"] = "sys.brightness",
        ["wifi"] = "sys.wifi",
        ["net.wifi"] = "sys.wifi",
        ["bluetooth"] = "sys.bluetooth",
        ["theme"] = "sys.dark_mode",
        ["sys.theme"] = "sys.dark_mode",
        ["dark_mode"] = "sys.dark_mode",
        ["mute"] = "sys.mute",
        ["audio.mute"] = "sys.mute",
        ["mic"] = "sys.mic",
        ["sys.microphone"] = "sys.mic",
        ["power.mode"] = "sys.power_mode",
        ["sys.power"] = "sys.power_mode",
        ["display.resolution"] = "sys.resolution",
        ["display.refresh_rate"] = "sys.refresh_rate",
        ["sys.refresh"] = "sys.refresh_rate",
        ["sys.hz"] = "sys.refresh_rate",
        ["display.projection"] = "sys.projection",
        ["net.speed"] = "net.down",
        ["net.download"] = "net.down",
        ["net.upload"] = "net.up",
        ["network"] = "net.status",
        ["network.info"] = "net.status",
        ["music.now"] = "media.now",
        ["spotify.now"] = "media.now",
        ["media.playing"] = "media.now",
        ["media.nowplaying"] = "media.now",
        ["weather"] = "weather.now",
        ["weather.temp"] = "weather.now",
        ["weather.forecast"] = "weather.days",
        ["weather.week"] = "weather.days",
        ["rss"] = "web.feed",
        ["web.rss"] = "web.feed",
        ["feed.rss"] = "web.feed",
        ["web.list"] = "ai.list",
        ["ai.line"] = "ai.text",
        ["powershell"] = "ps.value",
        ["ps.script"] = "ps.value",
        ["shell.powershell"] = "ps.value",
        ["store.get"] = "store.value",
        ["store.count"] = "store.value",
        ["counter"] = "store.value",
        ["server.memory"] = "server.mem",
        ["server.docker"] = "server.containers",
        ["server.run"] = "server.cmd",
    };

    /// <summary>The key <paramref name="key"/> stands for: itself, or the source it is an old name of.</summary>
    public static string? Canonical(string? key)
    {
        var k = key?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(k)) return null;
        return Aliases.TryGetValue(k, out var to) ? to : k;
    }

    public static TileSource? ByKey(string? key) => Canonical(key) is { } k && ByKeyMap.TryGetValue(k, out var s) ? s : null;

    /// <summary>How often <paramref name="b"/>'s source is worth reading, in seconds; AI and web lines keep to their own pace.</summary>
    public static int FloorSeconds(TileBlock b)
    {
        if (ByKey(b.Bind) is not { } src) return 60;
        return src.Key switch
        {
            "ai.text" or "ai.list" => Math.Max(b.Every ?? 3600, b.Web ? 1800 : 900),
            _ => Math.Max(src.FloorSeconds, b.Every ?? 0),
        };
    }

    /// <summary>
    /// A key for what <paramref name="b"/> reads, so two blocks (in any widget) reading the same thing share one read,
    /// and a parameterised source (a command, an address, an ask, a town) is cached per parameter.
    /// </summary>
    public static string? BindingId(TileBlock b)
    {
        if (ByKey(b.Bind) is not { } src) return null;
        var key = src.Key;
        string?[] parts = key switch
        {
            "server.cmd" => [b.Server, b.Cmd],
            "ps.value" => [b.Cmd],
            "web.json" => [b.Url, b.Path, string.Join(",", b.Fields.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))],
            "ai.text" or "ai.list" => [b.Text, b.Web ? "web" : ""],
            "time.countdown" => [b.Text],
            "web.feed" or "web.image" => [b.Url],
            "custom.state" => [b.State?.ToJsonString()],
            _ when key.StartsWith("server.", StringComparison.Ordinal) => [b.Server],
            _ when key.StartsWith("weather.", StringComparison.Ordinal) => [b.Query?.Trim().ToLowerInvariant()],
            "pc.disk" or "net.ping" or "sys.vpn" or "sys.app" or "store.value" or "sys.resolution" or "sys.refresh_rate" => [b.Query?.Trim().ToLowerInvariant()],
            "pc.top_cpu" or "pc.top_memory" => [],
            _ => [],
        };
        if (parts.All(string.IsNullOrEmpty)) return key;
        return key + "#" + Hash(string.Join("\u0000", parts.Select(p => p ?? "")));
    }

    /// <summary>FNV-1a: a hash that stays the same between runs (string.GetHashCode doesn't), for the value cache on disk.</summary>
    public static string Hash(string text)
    {
        uint h = 2166136261;
        foreach (var by in Encoding.UTF8.GetBytes(text))
        {
            h ^= by;
            h *= 16777619;
        }
        return h.ToString("x8");
    }

    /// <summary>Which blocks make sense for a source, for the AI's instructions and the Studio's checks.</summary>
    public static bool Fits(BlockType type, TileSource source) => type switch
    {
        BlockType.Ring or BlockType.Bar => source.Kind == ValueKind.Number || source.Key == "store.value",
        BlockType.Chart => source.Kind == ValueKind.Series,
        BlockType.List or BlockType.Carousel => source.Kind == ValueKind.List,
        BlockType.Countdown => source.Kind == ValueKind.Time,
        BlockType.Image => source.Kind is ValueKind.Image or ValueKind.List || source.Key == "media.now",
        BlockType.Media => source.Key == "media.now",
        BlockType.Toggle => source.Control == SourceControl.Switch || source.Key is "store.value" or "custom.state",
        BlockType.Slider => source.Control == SourceControl.Level || source.Key is "store.value" or "custom.state",
        BlockType.Choice => source.Control == SourceControl.Choice || source.Key is "store.value" or "custom.state",
        _ => true,
    };

    /// <summary>The block types that show <paramref name="source"/> well, best first: what the Studio's block editor offers.</summary>
    public static IReadOnlyList<BlockType> TypesFor(TileSource? source)
    {
        if (source is null) return [BlockType.Text, BlockType.Button, BlockType.Badge, BlockType.Clock, BlockType.Date];
        if (source.Key is "ai.text") return [BlockType.Ai, BlockType.Text];
        if (source.Key is "time.date") return [BlockType.Date, BlockType.Text];
        if (source.Key is "media.now") return [BlockType.Media, BlockType.Metric, BlockType.Text, BlockType.Image];
        if (source.Key is "store.value") return [BlockType.Metric, BlockType.Ring, BlockType.Bar, BlockType.Badge, BlockType.Toggle];
        return source.Control switch
        {
            SourceControl.Switch => [BlockType.Toggle, BlockType.Metric, BlockType.Badge],
            SourceControl.Level => [BlockType.Slider, BlockType.Ring, BlockType.Bar, BlockType.Metric],
            SourceControl.Choice => [BlockType.Choice, BlockType.Metric, BlockType.Text],
            _ => source.Kind switch
            {
                ValueKind.Number => [BlockType.Metric, BlockType.Ring, BlockType.Bar, BlockType.Badge, BlockType.Text],
                ValueKind.Text => [BlockType.Metric, BlockType.Text, BlockType.Badge],
                ValueKind.List => [BlockType.List, BlockType.Carousel, BlockType.Image, BlockType.Metric],
                ValueKind.Series => [BlockType.Chart, BlockType.Metric],
                ValueKind.Time => source.Key == "time.clock" ? [BlockType.Clock] : [BlockType.Countdown, BlockType.Metric, BlockType.Text],
                ValueKind.Image => [BlockType.Image, BlockType.Text],
                _ => [BlockType.Metric],
            },
        };
    }
}
