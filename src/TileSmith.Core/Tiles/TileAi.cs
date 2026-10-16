using System.Text;
using System.Text.Json.Nodes;
using TileSmith.Core.Ai;

namespace TileSmith.Core.Tiles;

/// <summary>
/// The AI half of TileSmith: a first draft from a description, then a revision for every comment.
///
/// The model is handed the whole design language (sizes, palettes, surfaces, the 35 shapes, the block types and every
/// source with what is already allowed) and must answer with one JSON object, <c>{"reply", "suggestions", "design"}</c>,
/// in exactly the shape <see cref="TileCodec"/> reads. It is told never to answer "that's not possible": anything
/// TileSmith has no source for goes through PowerShell, the web sources and the AI's own lines and rows. Whatever
/// comes back is parsed leniently and clamped to its size; an answer that isn't a design, or that binds a source that
/// doesn't exist, goes back once with what was wrong (<see cref="RepairPrompt"/>) before a block falls back to text.
/// One call per message (two at most): no agent loop, no tools, so a widget arrives in seconds and always draws.
/// </summary>
public static class TileAi
{
    public sealed record Result(string Reply, TileDesign Design, IReadOnlyList<string> Notes, IReadOnlyList<string> Suggestions, IReadOnlyList<string> Unknown);

    /// <summary>What each source's availability looks like to the model; <paramref name="Extra"/> is a hint on how to use it now.</summary>
    public sealed record SourceState(string Key, bool Ready, string? Missing = null, string? Extra = null);

    /// <summary>The Studio's live line while the AI works.</summary>
    public sealed record Progress(string Step, string? Detail = null);

    /// <summary>What the model is told about this PC (displays, battery…) and the person (their town).</summary>
    public sealed record Context(string Device, string? HomeTown, bool ServerConfigured, string? ServerName);

    public static async Task<Result> DraftAsync(
        IAiBackend backend,
        string prompt,
        IReadOnlyList<SourceState> sources,
        Context context,
        TileDesign? previous,
        Action<Progress>? onProgress,
        CancellationToken ct)
    {
        var (system, user) = BuildPrompt(prompt, previous, sources, context);
        onProgress?.Invoke(new Progress(previous is null ? "Reading what you asked" : "Reading your widget"));
        onProgress?.Invoke(new Progress("Thinking", backend.DisplayName));
        var raw = await backend.CompleteAsync([AiMessage.System(system), AiMessage.User(user)], ct);
        onProgress?.Invoke(new Progress("Checking the design"));
        Result? got = null;
        Exception? first = null;
        try { got = Parse(raw, previous); }
        catch (Exception ex) when (ex is not OperationCanceledException) { first = ex; }
        var problems = got is null
            ? [first?.Message ?? "The answer wasn't a design."]
            : got.Unknown.Select(u => $"There is no source \"{u}\".").ToList();
        if (problems.Count == 0) return got!;

        // one more go with what was wrong; the better of the two answers wins
        onProgress?.Invoke(new Progress("Fixing a mistake", problems[0].Length > 60 ? problems[0][..60] : problems[0]));
        Result? again = null;
        try
        {
            var repaired = await backend.CompleteAsync([AiMessage.System(system), AiMessage.User(user), AiMessage.Assistant(Trim(raw, 6000)), AiMessage.User(RepairPrompt(problems))], ct);
            again = Parse(repaired, previous);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && got is not null)
        {
            Log.Warn($"The repair answer failed too: {ex.Message}");
        }
        if (again is not null && (got is null || again.Unknown.Count < got.Unknown.Count)) return again;
        if (got is not null) return got;
        throw first ?? new AiException("The AI's answer wasn't a design. Try again, or say it another way.");
    }

    // ── The prompt ───────────────────────────────────────────────────────────────────

    public static (string System, string User) BuildPrompt(string request, TileDesign? current, IReadOnlyList<SourceState> sources, Context context)
    {
        var sb = new StringBuilder(Language);
        sb.Append("\n\nSOURCES (bind a block to one by its key; only these exist):\n");
        var state = sources.ToDictionary(s => s.Key);
        foreach (var group in TileSources.All.Where(s => !s.Hidden).GroupBy(s => s.Group))
        {
            sb.Append('[').Append(SourceGroups.Label(group.Key)).Append("]\n");
            foreach (var s in group)
            {
                sb.Append("- ").Append(s.Key).Append(" (").Append(s.Control != SourceControl.None ? s.Control.ToString().ToLowerInvariant() : s.Kind.ToString().ToLowerInvariant()).Append("): ").Append(s.Hint);
                if (state.TryGetValue(s.Key, out var st))
                {
                    if (!st.Ready) sb.Append(" [not set up yet: ").Append(st.Missing ?? "needs setting up").Append("; fine to use, TileSmith will ask]");
                    if (st.Extra is not null) sb.Append(" [").Append(st.Extra).Append(']');
                }
                sb.Append('\n');
            }
        }

        sb.Append("\nEXAMPLES of good designs:\n");
        var examples = Examples.Select(e => (e.What, TileStarters.ById(e.Id))).ToList();
        if (current is null && TileStarters.Match(request) is { } matched && examples.All(e => e.Item2?.Id != matched.Id))
            examples.Add(($"{Cap(matched.Design.Size.Id)}, {matched.Prompt.ToLowerInvariant()}", matched));
        foreach (var (what, starter) in examples)
        {
            if (starter is null) continue;
            sb.Append(what).Append(":\n").Append(TileCodec.ToAiJson(starter.Design).ToJsonString()).Append('\n');
        }

        sb.Append("\nTHIS PC:\n").Append(context.Device.Trim()).Append('\n');
        sb.Append(string.IsNullOrWhiteSpace(context.HomeTown)
            ? "- No town is set for the weather yet: when they name a place, put it in the weather blocks' \"query\"; otherwise TileSmith asks them.\n"
            : $"- Their town for the weather is {context.HomeTown}: weather blocks use it when \"query\" is left out.\n");
        sb.Append(context.ServerConfigured
            ? $"- Their server \"{context.ServerName}\" is set up (server.* sources work).\n"
            : "- No server is set up (server.* sources would ask them to set one up first).\n");

        var user = new StringBuilder();
        if (current is null)
        {
            user.Append("Make a new widget for this request:\n").Append(request.Trim()).Append('\n');
        }
        else
        {
            user.Append("The current design:\n").Append(TileCodec.ToAiJson(current).ToJsonString()).Append('\n');
            var history = current.Chat.TakeLast(8).ToList();
            if (history.Count > 0)
            {
                user.Append("\nThe conversation so far:\n");
                foreach (var t in history) user.Append(t.FromUser ? "Person: " : "You: ").Append(Trim(t.Text, 300)).Append('\n');
            }
            user.Append("\nTheir new comment. Change the design to do what it asks and keep everything else as it is:\n").Append(request.Trim()).Append('\n');
        }
        user.Append("\nAnswer with only the JSON object.");
        return (sb.ToString(), user.ToString());
    }

    /// <summary>What was wrong with the last answer.</summary>
    public static string RepairPrompt(IReadOnlyList<string> problems)
    {
        var sb = new StringBuilder("Your last answer had these problems:\n");
        foreach (var p in problems) sb.Append("- ").Append(p).Append('\n');
        sb.Append("Fix them. Use only the sources and block types listed; for anything TileSmith has no source for, use ps.value, web.feed, ");
        sb.Append("web.json, web.image, ai.list, or an ai block with \"web\": true. Answer with the whole JSON object only.");
        return sb.ToString();
    }

    private static readonly (string What, string Id)[] Examples =
    [
        ("Wide, a glance at the day", "today"),
        ("Wide, a system dashboard", "pulse"),
        ("Wide, sound controls", "sound"),
        ("Large, a news feed to scroll", "news"),
    ];

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private const string Language = """
        You are TileSmith, the widget designer inside TileSmith, a quick-settings panel for Windows 11 that opens from the
        taskbar corner. You turn what the person asks for into a widget (a tile on the panel) in Material 3 Expressive,
        and revise it when they comment.

        You can build almost anything. Use TileSmith's own source when there is one: they are instant and always work.
        When there isn't, ps.value runs a short read-only PowerShell query on this PC (the person approves it once),
        server.cmd does the same on their server, web.feed reads any RSS or Atom feed, web.json any JSON API, web.image
        any picture, and ai.list (rows) or an ai block (a line) with "web": true answers anything you would search for:
        scores, fixtures, prices, releases, opening hours. NEVER answer that something can't be done: build the closest
        widget that works, and say in the reply what it shows and what the person must allow. Ask nothing back; make
        your best guess and let them comment.

        Answer with ONE JSON object and nothing else:
        {"reply": "<one or two short, friendly sentences in the person's language: what you made or changed, and anything they must allow>",
         "suggestions": ["<two or three next changes they might like, 2 to 6 words each, in their language>"],
         "design": {"name": "<2-3 words>", "size": "...", "palette": "...", "surface": "...", "refresh": <seconds; leave it out to go as fast as the sources allow>, "rows": [{"h": 1, "cells": [<block>, ...]}, ...]}}

        SIZES (panel cells of 118 x 72 px with 10 px gaps; the size limits rows and blocks, never exceed it; the person can resize it later):
        - icon 1x1: 1 block (a one-click button or a Quick Settings switch)
        - mini 2x1: 1 row, up to 2 blocks
        - small 2x2: up to 2 rows, 2 blocks per row, 4 in all
        - tall 2x3: up to 3 rows, 2 per row, 6 in all
        - bar 4x1 (the panel's full width, one row): up to 4 blocks; sliders and choices live here
        - wide 4x2: up to 2 rows, up to 4 per row, 6 in all
        - large 4x3: up to 4 rows, up to 4 per row, 10 in all
        Row "h" (1-3) is its share of the height; a block's "w" (1-4) is its share of the row's width. A one-row size is
        72 px tall: one big value with its label, or a ring or badge beside a few words. Lists, carousels and players
        want wide or large and most of the height. A widget that is one toggle, slider, choice or button (style tile)
        becomes the whole tile, like Android's Quick Settings.

        PALETTES: accent (the default: TileSmith's accent colour), ocean, berry, forest, sunset, lemon, lavender, mint,
        racing, sand, mono. Pick one that suits the subject when they don't say; switches and sliders look best on accent.
        SURFACES (the widget's background): soft, tonal (default), tinted (palette-coloured), vivid (bold primary).
        TONES for a block: primary (default), secondary, tertiary, neutral, error. Two or three tones per widget at most.
        STYLES for a block: tile (default: an M3 container), plain (no container, on the background),
        filled (a strong primary fill: use for the one hero at most), shape (sits on one of the 35 M3 shapes).
        SHAPES (for style "shape" and badges): circle, square, slanted, arch, fan, arrow, semi_circle, oval, pill,
        triangle, diamond, clam_shell, pentagon, gem, sunny, very_sunny, cookie4, cookie6, cookie7, cookie9, cookie12,
        ghostish, clover4, clover8, burst, soft_burst, boom, soft_boom, flower, puffy, puffy_diamond, pixel_circle,
        pixel_triangle, bun, heart. Round ones (cookie9, sunny, cookie12, flower, clover4, soft_burst, puffy_diamond)
        hold an icon or number best.

        BLOCKS: {"type": ..., "bind": "<source key>", "label": "<short, 14 chars max>", "w": 1, "tone": ..., "style": ..., ...}
        - metric: a big number or word with its label (any source)
        - ring: a progress ring with the number inside (number sources with a goal; "goal" overrides it)
        - bar: a wavy progress bar under its label and value (number sources with a goal)
        - chart: bars of a series, or "variant": "line" (series sources)
        - list: rows (list sources; "max" rows). "variant": "scroll" scrolls inside the tile (feeds, long lists),
          "thumbs" always shows pictures, "plain" never does. Rows with links open on click.
        - carousel: cards with pictures to flip through (list sources with pictures: web.feed, web.json with fields.image,
          ai.list). "max" cards (default 10). Give it a whole row, usually the whole widget.
        - image: a picture filling its tile (web.image, media.now's cover art, a list source's newest), or "url" with no
          bind for a fixed image address. "label" captions it; "variant": "fit" shows all of it.
        - media: the player for media.now: cover art, track, artist, and previous / play-pause / next.
        - text: words; "text" for your own, or bind a text source
        - badge: an icon ("icon") or a short value on a shape ("shape")
        - clock: the time, ticking (bind time.clock; "text" may be a .NET format like "HH:mm:ss")
        - date: today's date (bind time.date)
        - countdown: counts down to a time source (time.countdown with "text" as an ISO date-time or a time of day, weather.sun)
        - toggle: a Quick Settings switch for a switch source (sys.wifi, sys.bluetooth, sys.dark_mode, sys.mute, sys.mic,
          sys.vpn, sys.app, or store.value for a done/not-done flag): icon, label and On/Off, filled with the accent when on
        - slider: a pill slider for a level source (sys.volume, sys.brightness; store.value with "from", "to", "step")
        - choice: segmented buttons for a choice source (sys.power_mode, sys.resolution, sys.refresh_rate, sys.projection);
          "options": [{"label": "1440p", "value": "2560x1440"}, ...] picks which and how they read (2 to 4); leave it out
          for the source's own (a display's top resolutions, its refresh rates)
        - button: a pill that does something: "label", "icon", "tap", and "confirm": "<question>" for anything destructive
        - ai: a line your AI writes each refresh: bind ai.text, put the ask in "text", "web": true to search the web,
          "every": seconds between answers (3600 or more; 1800 at least with web). For several rows, a list bound to ai.list.
        Extra fields: "query" (what a source looks for: a town, a drive letter, a host, a VPN's or an app's name, a kept
        value's name), "cmd" (ps.value: the PowerShell; server.cmd: the shell command), "url" and "path" (web.*), "fields"
        (web.json lists: {"title": "name", "sub": "author", "trailing": "price", "image": "thumb.url", "link": "url"},
        paths inside one item), "goal", "max", "every", "tap".
        TAPS ("tap" on a button, any block or the whole widget): "url:https://…" (or any URI: ms-settings:display, spotify:,
        steam://open/games), "settings:display" (a Windows Settings page), "run:notepad" (an app by name or path, or
        shell:AppsFolder\…), "folder:%USERPROFILE%\Downloads", "keys:Win+Shift+S", "media:toggle|next|prev",
        "power:lock|sleep|hibernate|restart|shutdown|signout|displayoff", "copy:<text>", "store:water+1", "store:water-1",
        "store:water=0", "store:done!" (flip), "open" (what the value links to), "refresh".
        ICONS: any Material Symbols name in snake_case: wifi, bluetooth, dark_mode, volume_up, mic, brightness_6, bolt,
        memory, monitoring, hard_drive, battery_full, lan, download, music_note, play_arrow, partly_cloudy_day, rainy,
        thermostat, schedule, timer, calendar_today, celebration, rocket_launch, terminal, code, dns, deployed_code,
        public, rss_feed, newspaper, sports_esports, sports_soccer, coffee, water_drop, fitness_center, mail, chat,
        lock, power_settings_new, screenshot_region, folder_open, settings, apps, home, star, favorite, auto_awesome.

        GOOD DESIGN (Material 3 Expressive):
        - One hero per widget: the thing they care most about gets the most room (a wider "w", a taller row, a ring,
          a big metric, a carousel or a player). Everything else supports it.
        - Contrast: mix a shape badge or a ring with flat tiles; mix tones; never make every block the same type.
        - Few words: labels are 1-2 words; the data speaks. Never repeat what a source already says as a label.
        - Fit the size: fewer, bigger blocks beat many small ones. An icon holds 1 block, a mini or bar holds 2-3 well.
        - Switches people flip often (Wi-Fi, dark mode, mute, a VPN) are toggles; things they set are sliders and choices.
        - Things that change every second (time, countdowns) use clock or countdown blocks, which tick by themselves.
        - Use TileSmith's own sources first. A ps.value script only when nothing else reads it: short, read-only, Windows
          PowerShell 5.1 (no ?:, ??, && or ||; Get-CimInstance, not Get-WmiObject), printing one value or compact JSON
          (ConvertTo-Json -Compress). Say in the reply that they approve it first.
        - refresh: leave it out (as fast as the sources allow: CPU every 2 s, weather every 10 min). Set it only when they
          ask for a slower pace, in seconds.
        """;

    // ── The answer ───────────────────────────────────────────────────────────────────

    public static Result Parse(string raw, TileDesign? previous)
    {
        JsonObject? root = null;
        JsonObject? designJson = null;
        foreach (var node in JsonExtractor.Candidates(raw))
        {
            if (node is not JsonObject o) continue;
            if (o["design"] is JsonObject d) { root = o; designJson = d; break; }
            if (o.ContainsKey("rows") || o.ContainsKey("blocks")) { root = o; designJson = o; break; }
        }
        if (root is null) throw new AiException("The AI's answer wasn't a design. Try again, or say it another way.");
        if (designJson is null) throw new AiException("The AI answered without a design. Try again.");

        var notes = new List<string>();
        var unknown = new List<string>();
        designJson = (JsonObject)designJson.DeepClone();
        var before = 0;
        if (designJson["rows"] is JsonArray rows)
        {
            foreach (var row in rows.OfType<JsonObject>())
            {
                var cells = row["cells"] as JsonArray ?? row["blocks"] as JsonArray;
                if (cells is null) continue;
                foreach (var b in cells.OfType<JsonObject>())
                {
                    before++;
                    var bind = TileCodec.Str(b, "bind")?.Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(bind) || bind == "null") continue;
                    var src = TileSources.ByKey(bind);
                    if (src is null || src.Hidden)
                    {
                        unknown.Add(bind);
                        notes.Add($"There's no \"{bind}\" source; that block shows its text instead.");
                        b.Remove("bind");
                        b["type"] = "text";
                        if (!b.ContainsKey("text")) b["text"] = TileCodec.Str(b, "label") ?? bind;
                        continue;
                    }
                    // a block that can't show its source becomes the one that shows it best
                    if (BlockTypes.ById(TileCodec.Str(b, "type")) is { } type && !TileSources.Fits(type, src))
                    {
                        var better = TileSources.TypesFor(src)[0];
                        b["type"] = BlockTypes.Id(better);
                        Log.Info($"TileAi: {BlockTypes.Id(type)} can't show {src.Key}; using {BlockTypes.Id(better)}");
                    }
                }
            }
        }
        var parsed = TileCodec.Parse(designJson, previous);
        var after = parsed.Blocks.Count;
        if (after < before) notes.Add($"{before - after} block{(before - after == 1 ? "" : "s")} didn't fit a {parsed.Size.Label.ToLowerInvariant()} widget.");
        if (after == 0) throw new AiException("The AI's design had no blocks. Try describing it again.");
        var reply = TileCodec.Str(root, "reply")?.Trim();
        if (string.IsNullOrEmpty(reply)) reply = previous is null ? "Here's your widget." : "Done.";
        var suggestions = (root["suggestions"] as JsonArray)?.Select(n => TileCodec.WidgetText(n)?.Trim())
            .OfType<string>().Where(s => s.Length is > 0 and <= 48).Distinct().Take(3).ToList() ?? [];
        return new Result(Trim(reply, 400), parsed, notes, suggestions, unknown.Distinct().ToList());
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// Reads a streamed or finished answer for the Studio's live line: the design's name, then each block as it
    /// starts ("Adding a ring · CPU"). Pure, so the tests can pin it.
    /// </summary>
    public static string? Describe(string partial)
    {
        var at = partial.IndexOf("\"design\"", StringComparison.Ordinal);
        if (at < 0) return partial.Contains("\"reply\"", StringComparison.Ordinal) ? "Writing what it made" : null;
        var design = partial[at..];
        var types = System.Text.RegularExpressions.Regex.Matches(design, "\"type\"\\s*:\\s*\"([a-z_]+)\"");
        if (types.Count == 0)
        {
            var name = System.Text.RegularExpressions.Regex.Match(design, "\"name\"\\s*:\\s*\"([^\"]{1,40})\"");
            return name.Success ? $"Designing “{name.Groups[1].Value}”" : "Laying it out";
        }
        var last = types[^1];
        var type = BlockTypes.ById(last.Groups[1].Value);
        var start = Math.Max(0, design.LastIndexOf('{', last.Index));
        var block = design[start..];
        var label = System.Text.RegularExpressions.Regex.Match(block, "\"label\"\\s*:\\s*\"([^\"]{1,30})\"");
        var bind = System.Text.RegularExpressions.Regex.Match(block, "\"bind\"\\s*:\\s*\"([a-z0-9._]+)\"");
        var what = label.Success ? label.Groups[1].Value : bind.Success ? TileSources.ByKey(bind.Groups[1].Value)?.Label : null;
        var noun = type is { } t ? BlockTypes.Label(t) : "block";
        noun = noun.Length > 1 && char.IsUpper(noun[1]) ? noun : noun.ToLowerInvariant();
        var article = "aeiou".Contains(char.ToLowerInvariant(noun[0])) ? "an" : "a";
        return $"Adding {article} {noun}" + (!string.IsNullOrWhiteSpace(what) && !what.Equals(noun, StringComparison.OrdinalIgnoreCase) ? " · " + what : "");
    }
}
