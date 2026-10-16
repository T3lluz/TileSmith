using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TileSmith.Core.Tiles;

/*
 * A TileSmith widget is a design: a fixed size, a palette, a surface and a bento grid of up to four rows of
 * blocks, each block showing one source (CPU, the weather, what's playing…) or switching one setting (Wi-Fi,
 * volume, the power mode). The AI writes designs as JSON in exactly this shape, the Studio's chips edit them and
 * the panel draws them; TileCodec reads anything an AI sends back (unknown fields dropped, numbers clamped) and
 * TileRules keeps a design inside its size. The same language as DailyDash's Tilesmith, made for the Windows panel.
 */

/// <summary>The sizes a design can take, as panel cells (4 wide is the panel's full width).</summary>
public sealed record TileSize(string Id, string Label, int Cols, int Rows, int MaxRows, int MaxCells, int MaxBlocks)
{
    public static readonly TileSize Icon = new("icon", "Icon", 1, 1, 1, 1, 1);
    public static readonly TileSize Mini = new("mini", "Mini", 2, 1, 1, 2, 2);
    public static readonly TileSize Small = new("small", "Small", 2, 2, 2, 2, 4);
    public static readonly TileSize Tall = new("tall", "Tall", 2, 3, 3, 2, 6);
    public static readonly TileSize Bar = new("bar", "Bar", 4, 1, 1, 4, 4);
    public static readonly TileSize Wide = new("wide", "Wide", 4, 2, 2, 4, 6);
    public static readonly TileSize Large = new("large", "Large", 4, 3, 4, 4, 10);

    public static readonly IReadOnlyList<TileSize> All = [Icon, Mini, Small, Tall, Bar, Wide, Large];

    /// <summary>"2×1".</summary>
    public string Cells => $"{Cols}×{Rows}";

    public bool IsFullWidth => Cols >= 4;

    public static TileSize? ById(string? id)
    {
        var key = id?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(key)) return null;
        var hit = All.FirstOrDefault(s => s.Id == key);
        if (hit is not null) return hit;
        return key.Replace("x", "×").Replace(" ", "") switch
        {
            "1×1" or "tiny" or "button" => Icon,
            "2×1" or "tile" or "pill" => Mini,
            "2×2" or "square" => Small,
            "2×3" or "column" => Tall,
            "4×1" or "strip" or "row" => Bar,
            "4×2" or "medium" or "card" => Wide,
            "4×3" or "4×4" or "big" => Large,
            _ => null,
        };
    }

    /// <summary>The size whose cells are (cols × rows), else the closest that fits inside them.</summary>
    public static TileSize ForCells(int cols, int rows)
    {
        var w = cols >= 4 ? 4 : cols >= 2 ? 2 : 1;
        var exact = All.FirstOrDefault(s => s.Cols == w && s.Rows == rows);
        if (exact is not null) return exact;
        return All.Where(s => s.Cols <= w && s.Rows <= Math.Max(1, rows)).OrderByDescending(s => s.Cols * s.Rows).FirstOrDefault() ?? Mini;
    }

    public override string ToString() => Id;
}

/// <summary>What sits behind the blocks: from the quietest container to the palette's primary.</summary>
public enum TileSurface { Soft, Tonal, Tinted, Vivid }

/// <summary>The colour family a block takes its container and marks from.</summary>
public enum TileTone { Primary, Secondary, Tertiary, Neutral, Error }

/// <summary>How a block is contained: an M3 tile, nothing, a strong fill, or one of the 35 shapes.</summary>
public enum BlockStyle { Tile, Plain, Filled, Shape }

public enum BlockType
{
    /// <summary>A big number or word with its label: CPU, the temperature, a counter.</summary>
    Metric,
    /// <summary>A progress ring with the value inside.</summary>
    Ring,
    /// <summary>A wavy progress bar under a label and value.</summary>
    Bar,
    /// <summary>Bars (or a line) of a series: CPU over the last minutes, temperatures ahead.</summary>
    Chart,
    /// <summary>Rows: forecast days, busiest apps, containers, headlines.</summary>
    List,
    /// <summary>Words: a bound line or your own.</summary>
    Text,
    /// <summary>An icon (or a short value) on one of the M3 shapes.</summary>
    Badge,
    /// <summary>The time, ticking.</summary>
    Clock,
    /// <summary>Today's date.</summary>
    Date,
    /// <summary>A countdown to a time, ticking.</summary>
    Countdown,
    /// <summary>A pill that does something: opens a site or app, presses keys, locks the PC.</summary>
    Button,
    /// <summary>A line your AI writes on each refresh.</summary>
    Ai,
    /// <summary>A picture: album art, a webcam, an image at an address.</summary>
    Image,
    /// <summary>Cards with pictures to flip through.</summary>
    Carousel,
    /// <summary>What's playing, its art, and previous / play-pause / next.</summary>
    Media,
    /// <summary>A Quick Settings switch: Wi-Fi, Bluetooth, dark mode, mute, a VPN.</summary>
    Toggle,
    /// <summary>A pill slider: volume, brightness.</summary>
    Slider,
    /// <summary>Segmented buttons: power mode, resolution, refresh rate.</summary>
    Choice,
}

public static class BlockTypes
{
    public static string Id(BlockType t) => t.ToString().ToLowerInvariant();

    public static string Label(BlockType t) => t switch
    {
        BlockType.Metric => "Number",
        BlockType.Ai => "AI line",
        BlockType.Media => "Player",
        _ => t.ToString(),
    };

    public static BlockType? ById(string? id)
    {
        var key = id?.Trim().ToLowerInvariant().Replace("-", "").Replace(" ", "");
        if (string.IsNullOrEmpty(key)) return null;
        foreach (var t in Enum.GetValues<BlockType>()) if (Id(t) == key) return t;
        return key switch
        {
            "number" or "value" or "stat" or "big" or "status" => BlockType.Metric,
            "progress" or "gauge" or "circle" or "donut" => BlockType.Ring,
            "linear" or "progressbar" or "meter" => BlockType.Bar,
            "graph" or "sparkline" or "bars" or "line" => BlockType.Chart,
            "rows" or "agenda" or "table" or "feed" or "scroll" or "inbox" => BlockType.List,
            "label" or "note" or "title" or "headline" => BlockType.Text,
            "icon" or "shape" => BlockType.Badge,
            "time" => BlockType.Clock,
            "day" => BlockType.Date,
            "timer" => BlockType.Countdown,
            "action" or "link" or "shortcut" => BlockType.Button,
            "brief" or "web" or "search" or "summary" => BlockType.Ai,
            "photo" or "picture" or "thumbnail" or "cover" or "art" or "img" => BlockType.Image,
            "stack" or "cards" or "gallery" or "slides" or "slideshow" or "pager" or "swipe" or "deck" => BlockType.Carousel,
            "player" or "nowplaying" or "music" or "controls" or "playback" => BlockType.Media,
            "switch" or "onoff" or "quicksetting" => BlockType.Toggle,
            "range" or "volume" or "level" => BlockType.Slider,
            "segmented" or "select" or "options" or "picker" or "mode" => BlockType.Choice,
            _ => null,
        };
    }

    /// <summary>Blocks you press: they get the press-shrink and run something.</summary>
    public static bool IsInteractive(BlockType t) => t is BlockType.Button or BlockType.Toggle or BlockType.Slider or BlockType.Choice or BlockType.Media;
}

/// <summary>One option of a choice block: what it shows and the value it sets (or, for widgets from 0.4, its own action).</summary>
public sealed record TileOption(string Label, string Value, string? Icon = null, JsonObject? Action = null);

/// <summary>
/// One block. <see cref="Bind"/> names a source in <see cref="TileSources"/> (<c>pc.cpu</c>); the rest tune it.
/// Parameterised sources read <see cref="Server"/>, <see cref="Cmd"/>, <see cref="Url"/>, <see cref="Path"/>,
/// <see cref="Query"/>, <see cref="Fields"/> and <see cref="Text"/> (the AI line's ask).
/// </summary>
public sealed record TileBlock
{
    public required BlockType Type { get; init; }
    public int Weight { get; init; } = 1;
    public string? Bind { get; init; }
    public string? Label { get; init; }
    public string? Text { get; init; }
    public string? Icon { get; init; }
    public TileShape? Shape { get; init; }
    public TileTone Tone { get; init; } = TileTone.Primary;
    public BlockStyle Style { get; init; } = BlockStyle.Tile;
    /// <summary>A ring's or bar's target, over the source's own.</summary>
    public double? Goal { get; init; }
    /// <summary>List rows (30 at most), carousel cards (12) or chart points.</summary>
    public int? Max { get; init; }
    /// <summary><c>server.*</c>: which server; the configured one when blank.</summary>
    public string? Server { get; init; }
    /// <summary><c>server.cmd</c> / <c>ps.value</c>: the command or script, shown to you before it ever runs.</summary>
    public string? Cmd { get; init; }
    /// <summary><c>web.*</c>: the address; <see cref="Path"/> the JSON path to read.</summary>
    public string? Url { get; init; }
    public string? Path { get; init; }
    /// <summary>An AI line may search the web.</summary>
    public bool Web { get; init; }
    /// <summary>Seconds between refreshes of this block's own source, when slower than the design's.</summary>
    public int? Every { get; init; }
    /// <summary>What a tap does: see <see cref="TileTap"/>.</summary>
    public string? Tap { get; init; }
    /// <summary>A chart's look (<c>bars</c>, <c>line</c>), a list's (<c>scroll</c>, <c>thumbs</c>, <c>plain</c>), an image's (<c>fit</c>).</summary>
    public string? Variant { get; init; }
    /// <summary>What a parameterised source looks for: a town, a drive, an app, a host, a counter's name.</summary>
    public string? Query { get; init; }
    /// <summary><c>web.json</c> lists: where each row's parts sit in one item (<c>title</c>, <c>sub</c>, <c>trailing</c>, <c>image</c>, <c>link</c>).</summary>
    public IReadOnlyDictionary<string, string> Fields { get; init; } = Empty;
    /// <summary>A choice's options (the source's own when empty).</summary>
    public IReadOnlyList<TileOption> Options { get; init; } = [];
    /// <summary>A slider's range (the source's own when unset).</summary>
    public double? From { get; init; }
    public double? To { get; init; }
    public double? Step { get; init; }
    /// <summary>Asks before a tap runs (destructive buttons: restart, empty the bin).</summary>
    public string? Confirm { get; init; }

    // ── Advanced: TileSmith actions, for what no source covers (and widgets from 0.4) ──────────────
    /// <summary>Runs on tap (button), or sets the value (slider: <c>{value}</c>; choice: the option's value).</summary>
    public JsonObject? Action { get; init; }
    /// <summary>A toggle's actions for on and off.</summary>
    public JsonObject? On { get; init; }
    public JsonObject? Off { get; init; }
    /// <summary>The query <c>custom.state</c> reads.</summary>
    public JsonObject? State { get; init; }
    /// <summary>A toggle is on when <c>custom.state</c> reads this (default: any truthy value).</summary>
    public string? OnValue { get; init; }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    public bool Equivalent(TileBlock other) => TileCodec.BlockJson(this).ToJsonString() == TileCodec.BlockJson(other).ToJsonString();
}

public sealed record TileRow(int Height, IReadOnlyList<TileBlock> Cells)
{
    public TileRow(IReadOnlyList<TileBlock> cells) : this(1, cells) { }
}

public sealed record TileChatTurn(bool FromUser, string Text, long At);

public sealed record TileDesign
{
    public string Id { get; init; } = NewId();
    public string Name { get; init; } = "My widget";
    public TileSize Size { get; init; } = TileSize.Small;
    public TilePalette Palette { get; init; } = TilePalette.Accent;
    public TileSurface Surface { get; init; } = TileSurface.Tonal;
    /// <summary>Seconds between refreshes while the panel is open; 0 = as fast as its sources allow.</summary>
    public int Refresh { get; init; }
    /// <summary>The whole widget's tap.</summary>
    public string? Tap { get; init; }
    public IReadOnlyList<TileRow> Rows { get; init; } = [];
    public long CreatedAt { get; init; } = Now();
    public long UpdatedAt { get; init; } = Now();
    /// <summary>The conversation that made it, newest last, so a later comment can pick it up.</summary>
    public IReadOnlyList<TileChatTurn> Chat { get; init; } = [];
    /// <summary>Its AI blocks ask the AI on each refresh; off keeps the last answer.</summary>
    public bool Ai { get; init; } = true;

    public IReadOnlyList<TileBlock> Blocks => Rows.SelectMany(r => r.Cells).ToList();

    public static string NewId() => "tsm_" + Guid.NewGuid().ToString("N")[..12];

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>The block at <paramref name="index"/> in reading order changed with <paramref name="change"/>.</summary>
    public TileDesign WithBlock(int index, Func<TileBlock, TileBlock> change)
    {
        var i = 0;
        return this with { Rows = Rows.Select(r => r with { Cells = r.Cells.Select(b => i++ == index ? change(b) : b).ToList() }).ToList() };
    }
}

/// <summary>
/// A tap target, as a short string the AI can write and the Studio can show:
/// <list type="bullet">
/// <item><c>url:https://…</c> (also <c>ms-settings:</c>, <c>spotify:</c>, any URI), <c>settings:display</c> (Windows Settings pages)</item>
/// <item><c>run:notepad</c> (an app, a path, or <c>shell:AppsFolder\…</c>), <c>folder:%USERPROFILE%\Downloads</c></item>
/// <item><c>keys:Win+Shift+S</c>, <c>media:toggle|next|prev</c>, <c>power:lock|sleep|hibernate|restart|shutdown|signout|displayoff</c></item>
/// <item><c>copy:text</c>, <c>store:water+1</c> / <c>store:water-1</c> / <c>store:water=0</c> / <c>store:done!</c> (flip)</item>
/// <item><c>open</c> (what the value itself links to), <c>refresh</c>, <c>studio</c> (edit this widget)</item>
/// </list>
/// </summary>
public static class TileTap
{
    public const string Refresh = "refresh";
    public const string Studio = "studio";
    public const string Open = "open";

    private static readonly string[] Schemes = ["url", "settings", "run", "folder", "keys", "media", "power", "copy", "store"];
    public static readonly string[] MediaCommands = ["toggle", "play", "pause", "next", "prev"];
    public static readonly string[] PowerCommands = ["lock", "sleep", "hibernate", "restart", "shutdown", "signout", "displayoff"];

    public static string? Normalize(string? raw)
    {
        var t = raw?.Trim() ?? "";
        if (t.Length == 0) return null;
        var lower = t.ToLowerInvariant();
        if (lower is Refresh or Studio or Open) return lower;
        if (lower is "link" or "item") return Open;
        if (lower is "edit" or "app") return Studio;
        if (IsWebLink(t)) return "url:" + t;
        if (lower.StartsWith("ms-settings:", StringComparison.Ordinal)) return "url:" + t;
        var colon = t.IndexOf(':');
        if (colon <= 0) return null;
        var scheme = lower[..colon];
        var rest = t[(colon + 1)..].Trim();
        switch (scheme)
        {
            case "url": return rest.Length > 0 && Uri.TryCreate(rest, UriKind.Absolute, out _) ? "url:" + rest : null;
            case "settings": return "settings:" + rest.ToLowerInvariant().Replace("ms-settings:", "");
            case "run" or "app" or "launch": return rest.Length > 0 ? "run:" + rest : null;
            case "folder": return rest.Length > 0 ? "folder:" + rest : null;
            case "keys" or "key" or "hotkey": return rest.Length > 0 ? "keys:" + rest : null;
            case "media":
            {
                var cmd = rest.ToLowerInvariant() switch
                {
                    "playpause" or "play_pause" or "play-pause" => "toggle",
                    "previous" or "back" => "prev",
                    "skip" => "next",
                    var c => c,
                };
                return MediaCommands.Contains(cmd) ? "media:" + cmd : null;
            }
            case "power":
            {
                var cmd = rest.ToLowerInvariant().Replace("_", "").Replace("-", "").Replace(" ", "") switch
                {
                    "signoff" or "logoff" or "logout" => "signout",
                    "screenoff" or "monitoroff" => "displayoff",
                    "reboot" => "restart",
                    "shutoff" or "poweroff" => "shutdown",
                    var c => c,
                };
                return PowerCommands.Contains(cmd) ? "power:" + cmd : null;
            }
            case "copy": return rest.Length > 0 ? "copy:" + rest : null;
            case "store": return Store.TryParse(rest, out _) ? "store:" + rest.Replace(" ", "") : null;
        }
        // anything else that is a URI ("spotify:", "mailto:", "steam:")
        return Uri.TryCreate(t, UriKind.Absolute, out var uri) && uri.Scheme.Length > 1 && !Schemes.Contains(scheme) ? "url:" + t : null;
    }

    public static bool IsWebLink(string s) => s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    /// <summary>A short description for the Studio's block list ("Opens github.com", "Locks the PC").</summary>
    public static string Describe(string? tap)
    {
        if (string.IsNullOrEmpty(tap)) return "Nothing";
        var colon = tap.IndexOf(':');
        var scheme = colon > 0 ? tap[..colon] : tap;
        var rest = colon > 0 ? tap[(colon + 1)..] : "";
        return scheme switch
        {
            "url" => "Opens " + (Uri.TryCreate(rest, UriKind.Absolute, out var u) && u.Host.Length > 0 ? u.Host : rest),
            "settings" => "Opens Settings › " + (rest.Length > 0 ? rest : "Home"),
            "run" => "Starts " + System.IO.Path.GetFileNameWithoutExtension(rest.Split(' ')[0]),
            "folder" => "Opens " + rest,
            "keys" => "Presses " + rest,
            "media" => rest switch { "next" => "Next track", "prev" => "Previous track", _ => "Play / pause" },
            "power" => rest switch
            {
                "lock" => "Locks the PC",
                "sleep" => "Puts the PC to sleep",
                "hibernate" => "Hibernates the PC",
                "restart" => "Restarts the PC",
                "shutdown" => "Shuts down the PC",
                "signout" => "Signs you out",
                _ => "Turns the screen off",
            },
            "copy" => "Copies “" + rest + "”",
            "store" => "Changes " + (Store.TryParse(rest, out var s) ? s.Key : rest),
            Refresh => "Refreshes it",
            Studio => "Opens it in the Studio",
            Open => "Opens what it shows",
            _ => tap,
        };
    }

    /// <summary><c>water+1</c>, <c>water-1</c>, <c>water=0</c>, <c>done!</c>.</summary>
    public readonly record struct Store(string Key, char Op, double Amount, string? Text)
    {
        public static bool TryParse(string text, out Store store)
        {
            store = default;
            var t = text.Trim();
            if (t.EndsWith('!') && t.Length > 1)
            {
                store = new(t[..^1].Trim(), '!', 0, null);
                return IsKey(store.Key);
            }
            var at = t.IndexOfAny(['+', '-', '=']);
            if (at <= 0 || at == t.Length - 1) return false;
            var key = t[..at].Trim();
            var op = t[at];
            var value = t[(at + 1)..].Trim();
            if (op == '=')
            {
                store = new(key, '=', double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0, value);
                return IsKey(key);
            }
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return false;
            store = new(key, op, amount, null);
            return IsKey(key);
        }

        private static bool IsKey(string key) => key.Length is > 0 and <= 40 && key.All(c => char.IsLetterOrDigit(c) || c is '_' or '.');
    }
}

// ─────────────────────────────────────────────────────────────────
//  RULES
// ─────────────────────────────────────────────────────────────────

public static class TileRules
{
    public const int MaxRefresh = 3600;

    /// <summary>The Studio's refresh picks, in seconds (0 = as fast as the sources allow).</summary>
    public static readonly int[] RefreshChoices = [0, 5, 30, 60, 300, 900];

    /// <summary>The design clamped to its size: rows, blocks per row and blocks in all.</summary>
    public static TileDesign Sanitize(TileDesign d)
    {
        var budget = d.Size.MaxBlocks;
        var rows = new List<TileRow>();
        foreach (var r in d.Rows)
        {
            if (rows.Count >= d.Size.MaxRows || budget <= 0) break;
            var cells = r.Cells.Take(Math.Min(d.Size.MaxCells, budget)).Select(b => b with { Weight = Math.Clamp(b.Weight, 1, 4) }).ToList();
            if (cells.Count == 0) continue;
            budget -= cells.Count;
            rows.Add(new TileRow(Math.Clamp(r.Height, 1, 3), cells));
        }
        var name = d.Name.Trim();
        if (name.Length > 40) name = name[..40];
        return d with
        {
            Name = name.Length == 0 ? "My widget" : name,
            Rows = rows,
            Refresh = Math.Clamp(d.Refresh, 0, MaxRefresh),
            Tap = TileTap.Normalize(d.Tap),
            Chat = d.Chat.TakeLast(24).ToList(),
        };
    }

    /// <summary>
    /// The design moved to <paramref name="size"/>: its blocks in reading order, poured into the new size's rows
    /// (two to a row on the narrow sizes, up to three on the wide ones). What doesn't fit is left out; the count
    /// comes back so the Studio can say so.
    /// </summary>
    public static (TileDesign Design, int Dropped) Reflow(TileDesign d, TileSize size)
    {
        if (size == d.Size) return (d, 0);
        var blocks = d.Blocks;
        var kept = blocks.Take(size.MaxBlocks).ToList();
        int perRow;
        if (size == TileSize.Icon) perRow = 1;
        else if (size == TileSize.Mini || size == TileSize.Small || size == TileSize.Tall) perRow = 2;
        else if (size == TileSize.Bar) perRow = 4;
        else if (size == TileSize.Wide) perRow = kept.Count <= 4 ? Math.Max(2, (kept.Count + 1) / 2) : 3;
        else perRow = kept.Count <= 4 ? 2 : 3;
        // a lone control keeps a row of its own: sliders and choices want the width
        perRow = Math.Clamp(perRow, 1, size.MaxCells);
        var rows = new List<TileRow>();
        var pending = new List<TileBlock>();
        foreach (var b in kept)
        {
            var wantsRow = b.Type is BlockType.Slider or BlockType.Choice or BlockType.Carousel or BlockType.List or BlockType.Media;
            if (wantsRow && size.MaxRows > 1)
            {
                if (pending.Count > 0) { rows.Add(new TileRow(pending)); pending = []; }
                rows.Add(new TileRow([b]));
                continue;
            }
            pending.Add(b);
            if (pending.Count == perRow) { rows.Add(new TileRow(pending)); pending = []; }
        }
        if (pending.Count > 0) rows.Add(new TileRow(pending));
        var oldHeights = d.Rows.Select(r => r.Height).ToList();
        var newRows = rows.Take(size.MaxRows).Select((r, i) => r with { Height = i < oldHeights.Count ? oldHeights[i] : 1 }).ToList();
        var result = Sanitize(d with { Size = size, Rows = newRows });
        return (result, blocks.Count - result.Blocks.Count);
    }

    /// <summary>
    /// How often the design really reads its sources while the panel is open: its own pick, but never faster than
    /// the quickest of them can change (a widget of nothing but weather has no use for a two-second pass).
    /// </summary>
    public static int EffectiveRefreshSeconds(TileDesign d)
    {
        var bound = d.Blocks.Where(b => b.Bind is not null).ToList();
        var quickest = bound.Count == 0 ? 0 : bound.Min(TileSources.FloorSeconds);
        return Math.Clamp(Math.Max(d.Refresh, quickest), 1, MaxRefresh);
    }

    /// <summary>How old <paramref name="b"/>'s data may get before a pass reads it again.</summary>
    public static int BindingMaxAgeSeconds(TileDesign d, TileBlock b) =>
        Math.Max(Math.Max(d.Refresh, TileSources.FloorSeconds(b)), b.Every ?? 0);
}

// ─────────────────────────────────────────────────────────────────
//  JSON
// ─────────────────────────────────────────────────────────────────

public static class TileCodec
{
    private const int Version = 1;

    public static JsonObject ToJson(TileDesign d, bool withChat = true)
    {
        var o = ToAiJson(d);
        o.Insert(0, "v", Version);
        o.Insert(1, "id", d.Id);
        if (!d.Ai) o["ai"] = false;
        o["createdAt"] = d.CreatedAt;
        o["updatedAt"] = d.UpdatedAt;
        if (withChat && d.Chat.Count > 0)
            o["chat"] = new JsonArray(d.Chat.Select(t => (JsonNode)new JsonObject { ["u"] = t.FromUser, ["t"] = t.Text, ["at"] = t.At }).ToArray());
        return o;
    }

    /// <summary>The design as the AI sees and writes it: no ids, times or chat.</summary>
    public static JsonObject ToAiJson(TileDesign d)
    {
        var o = new JsonObject
        {
            ["name"] = d.Name,
            ["size"] = d.Size.Id,
            ["palette"] = d.Palette.Id,
            ["surface"] = Id(d.Surface),
        };
        if (d.Refresh > 0) o["refresh"] = d.Refresh;
        if (d.Tap is not null) o["tap"] = d.Tap;
        o["rows"] = new JsonArray(d.Rows.Select(r => (JsonNode)RowJson(r)).ToArray());
        return o;
    }

    public static string Id(TileSurface s) => s.ToString().ToLowerInvariant();
    public static string Id(TileTone t) => t.ToString().ToLowerInvariant();
    public static string Id(BlockStyle s) => s.ToString().ToLowerInvariant();

    private static JsonObject RowJson(TileRow r)
    {
        var o = new JsonObject();
        if (r.Height != 1) o["h"] = r.Height;
        o["cells"] = new JsonArray(r.Cells.Select(b => (JsonNode)BlockJson(b)).ToArray());
        return o;
    }

    public static JsonObject BlockJson(TileBlock b)
    {
        var o = new JsonObject { ["type"] = BlockTypes.Id(b.Type) };
        if (b.Weight != 1) o["w"] = b.Weight;
        Put(o, "bind", b.Bind);
        Put(o, "label", b.Label);
        Put(o, "text", b.Text);
        Put(o, "icon", b.Icon);
        if (b.Shape is { } shape) o["shape"] = shape.Id;
        if (b.Tone != TileTone.Primary) o["tone"] = Id(b.Tone);
        if (b.Style != BlockStyle.Tile) o["style"] = Id(b.Style);
        if (b.Goal is { } goal) o["goal"] = goal;
        if (b.Max is { } max) o["max"] = max;
        Put(o, "server", b.Server);
        Put(o, "cmd", b.Cmd);
        Put(o, "url", b.Url);
        Put(o, "path", b.Path);
        if (b.Web) o["web"] = true;
        if (b.Every is { } every) o["every"] = every;
        Put(o, "tap", b.Tap);
        Put(o, "variant", b.Variant);
        Put(o, "query", b.Query);
        if (b.Fields.Count > 0) o["fields"] = new JsonObject(b.Fields.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        if (b.Options.Count > 0)
        {
            o["options"] = new JsonArray(b.Options.Select(opt =>
            {
                var oo = new JsonObject { ["label"] = opt.Label };
                if (opt.Value != opt.Label) oo["value"] = opt.Value;
                if (opt.Icon is not null) oo["icon"] = opt.Icon;
                if (opt.Action is not null) oo["action"] = opt.Action.DeepClone();
                return (JsonNode)oo;
            }).ToArray());
        }
        if (b.From is { } from) o["from"] = from;
        if (b.To is { } to) o["to"] = to;
        if (b.Step is { } step) o["step"] = step;
        Put(o, "confirm", b.Confirm);
        if (b.Action is not null) o["action"] = b.Action.DeepClone();
        if (b.On is not null) o["on"] = b.On.DeepClone();
        if (b.Off is not null) o["off"] = b.Off.DeepClone();
        if (b.State is not null) o["state"] = b.State.DeepClone();
        Put(o, "onValue", b.OnValue);
        return o;
    }

    private static void Put(JsonObject o, string key, string? value)
    {
        if (value is not null) o[key] = value;
    }

    /// <summary>
    /// Reads a design, from storage or from an AI's reply. Lenient: a field it doesn't know is skipped, a value out
    /// of range is clamped, a block of an unknown type is dropped. <paramref name="baseDesign"/> supplies what the JSON
    /// leaves out (the id, times and chat when the AI revises a design).
    /// </summary>
    public static TileDesign Parse(JsonObject o, TileDesign? baseDesign = null)
    {
        var now = TileDesign.Now();
        var size = TileSize.ById(Str(o, "size")) ?? baseDesign?.Size ?? TileSize.Small;
        List<TileRow> rows;
        if (o["rows"] is JsonArray rowsJson) rows = rowsJson.OfType<JsonObject>().Select(ParseRow).OfType<TileRow>().ToList();
        else if (o["blocks"] is JsonArray flat) rows = [new TileRow(ParseCells(flat))]; // an AI that sent a flat list of blocks
        else rows = baseDesign?.Rows.ToList() ?? [];
        var chat = o["chat"] is JsonArray chatJson
            ? chatJson.OfType<JsonObject>().Select(t => new TileChatTurn(Bool(t, "u") ?? false, Str(t, "t") ?? "", Long(t, "at") ?? 0)).ToList()
            : baseDesign?.Chat.ToList() ?? [];
        var id = Str(o, "id");
        var name = Str(o, "name") ?? Str(o, "title");
        var tap = o.ContainsKey("tap") ? Str(o, "tap") : baseDesign?.Tap;
        return TileRules.Sanitize(new TileDesign
        {
            Id = id is not null && id.StartsWith("tsm_", StringComparison.Ordinal) ? id : baseDesign?.Id ?? TileDesign.NewId(),
            Name = string.IsNullOrWhiteSpace(name) ? baseDesign?.Name ?? "My widget" : name,
            Size = size,
            Palette = TilePalette.ById(Str(o, "palette")) ?? baseDesign?.Palette ?? TilePalette.Accent,
            Surface = SurfaceById(Str(o, "surface")) ?? baseDesign?.Surface ?? TileSurface.Tonal,
            Refresh = Int(o, "refresh") ?? baseDesign?.Refresh ?? 0,
            Tap = tap,
            Rows = rows,
            CreatedAt = Long(o, "createdAt") ?? baseDesign?.CreatedAt ?? now,
            UpdatedAt = Long(o, "updatedAt") ?? now,
            Chat = chat,
            Ai = o.ContainsKey("ai") ? Bool(o, "ai") ?? true : baseDesign?.Ai ?? true,
        });
    }

    private static TileRow? ParseRow(JsonObject o)
    {
        var cells = o["cells"] as JsonArray ?? o["blocks"] as JsonArray;
        if (cells is null) return null;
        return new TileRow(Int(o, "h") ?? Int(o, "height") ?? 1, ParseCells(cells));
    }

    private static List<TileBlock> ParseCells(JsonArray a) => a.OfType<JsonObject>().Select(ParseBlock).OfType<TileBlock>().ToList();

    /// <summary>The parts of a row a <c>web.json</c> list can map.</summary>
    public static readonly string[] FieldKeys = ["title", "sub", "trailing", "image", "link"];

    public static TileBlock? ParseBlock(JsonObject o)
    {
        if (BlockTypes.ById(Str(o, "type")) is not { } type) return null;
        string? S(string key, int max = 120)
        {
            var v = Str(o, key)?.Trim();
            if (string.IsNullOrEmpty(v) || v == "null") return null;
            return v.Length > max ? v[..max] : v;
        }
        double? D(string key) => Dbl(o, key) is { } d && !double.IsNaN(d) && !double.IsInfinity(d) ? d : null;
        var shape = TileShape.ById(S("shape"));
        var style = StyleById(S("style")) ?? (shape is not null && type == BlockType.Badge ? BlockStyle.Shape : BlockStyle.Tile);
        var icon = S("icon", 40)?.ToLowerInvariant();
        var fields = new Dictionary<string, string>();
        if (o["fields"] is JsonObject f)
        {
            foreach (var k in FieldKeys)
            {
                if (Str(f, k)?.Trim() is { Length: > 0 } v) fields[k] = v.Length > 80 ? v[..80] : v;
            }
        }
        var options = new List<TileOption>();
        if (o["options"] is JsonArray opts)
        {
            foreach (var node in opts.Take(6))
            {
                if (node is JsonObject opt)
                {
                    var label = Str(opt, "label")?.Trim() ?? Str(opt, "value")?.Trim();
                    if (string.IsNullOrEmpty(label)) continue;
                    options.Add(new TileOption(label.Length > 24 ? label[..24] : label, Str(opt, "value")?.Trim() is { Length: > 0 } v ? v : label, Str(opt, "icon"),
                        opt["action"] is JsonObject optAction ? (JsonObject)optAction.DeepClone() : null));
                }
                else if (WidgetText(node) is { Length: > 0 } plain)
                {
                    options.Add(new TileOption(plain, plain));
                }
            }
        }
        var goal = D("goal");
        var max = Int(o, "max");
        var every = Int(o, "every");
        return new TileBlock
        {
            Type = type,
            Weight = Math.Clamp(Int(o, "w") ?? Int(o, "weight") ?? 1, 1, 4),
            Bind = TileSources.Canonical(S("bind", 60)),
            Label = S("label", 40),
            Text = S("text", 400),
            Icon = icon,
            Shape = shape,
            Tone = ToneById(S("tone")) ?? TileTone.Primary,
            Style = style,
            Goal = goal is > 0 ? goal : null,
            // scrolling lists hold up to 30 rows; the rest show what fits
            Max = max is null ? null : Math.Clamp(max.Value, 1, 30),
            Server = S("server", 60),
            Cmd = S("cmd", 600) ?? S("script", 600) ?? S("command", 600),
            Url = S("url", 400) is { } url && (TileTap.IsWebLink(url) || type == BlockType.Image && (url.StartsWith("file:", StringComparison.OrdinalIgnoreCase) || System.IO.Path.IsPathRooted(url))) ? url : null,
            Path = S("path", 120),
            Web = Bool(o, "web") ?? false,
            Every = every is null ? null : Math.Clamp(every.Value, 1, 24 * 3600),
            Tap = TileTap.Normalize(S("tap", 400)),
            Variant = S("variant", 12)?.ToLowerInvariant(),
            Query = S("query", 200) ?? S("q", 200) ?? S("place", 200) ?? S("location", 200) ?? S("app", 200) ?? S("host", 200) ?? S("key", 200),
            Fields = fields,
            Options = options,
            From = D("from") ?? D("min"),
            To = D("to"),
            Step = D("step") is > 0 and var step ? step : null,
            Confirm = S("confirm", 160),
            Action = o["action"] as JsonObject is { } action ? (JsonObject)action.DeepClone() : null,
            On = o["on"] as JsonObject is { } on ? (JsonObject)on.DeepClone() : null,
            Off = o["off"] as JsonObject is { } off ? (JsonObject)off.DeepClone() : null,
            State = o["state"] as JsonObject is { } state ? (JsonObject)state.DeepClone() : null,
            OnValue = S("onValue", 60),
        };
    }

    public static TileSurface? SurfaceById(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "soft" or "surface" or "plain" or "quiet" => TileSurface.Soft,
        "tonal" or "container" or "default" => TileSurface.Tonal,
        "tinted" or "primary_container" or "tint" => TileSurface.Tinted,
        "vivid" or "primary" or "bold" => TileSurface.Vivid,
        _ => null,
    };

    public static TileTone? ToneById(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "primary" or "accent" => TileTone.Primary,
        "secondary" => TileTone.Secondary,
        "tertiary" => TileTone.Tertiary,
        "neutral" or "surface" or "plain" or "grey" or "gray" => TileTone.Neutral,
        "error" or "warning" or "alert" or "red" => TileTone.Error,
        _ => null,
    };

    public static BlockStyle? StyleById(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "tile" or "card" or "container" => BlockStyle.Tile,
        "plain" or "none" or "flat" => BlockStyle.Plain,
        "filled" or "solid" or "vivid" => BlockStyle.Filled,
        "shape" => BlockStyle.Shape,
        _ => null,
    };

    public static List<TileDesign> ParseList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            var a = JsonNode.Parse(raw, documentOptions: JsonDefaults.DocumentOptions) as JsonArray;
            if (a is null) return [];
            var list = new List<TileDesign>();
            foreach (var o in a.OfType<JsonObject>())
            {
                try { list.Add(Parse(o)); }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { Log.Warn($"Skipped an unreadable design: {ex.Message}"); }
            }
            return list;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string EncodeList(IEnumerable<TileDesign> list) =>
        new JsonArray(list.Select(d => (JsonNode)ToJson(d)).ToArray()).ToJsonString(JsonDefaults.Options);

    // ── Lenient JSON reading ────────────────────────────────────────────────────────

    internal static string? WidgetText(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.Number => node.ToJsonString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    internal static string? Str(JsonObject o, string key) => WidgetText(o[key]);

    internal static double? Dbl(JsonObject o, string key) =>
        double.TryParse(Str(o, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    internal static int? Int(JsonObject o, string key) => Dbl(o, key) is { } d ? (int)Math.Round(Math.Clamp(d, int.MinValue, int.MaxValue)) : null;

    internal static long? Long(JsonObject o, string key) => Dbl(o, key) is { } d ? (long)d : null;

    internal static bool? Bool(JsonObject o, string key) => Str(o, key)?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => null,
    };
}
