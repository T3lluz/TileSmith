using System.Text.Json.Nodes;

namespace TileSmith.Core.Tiles;

/// <summary>
/// Turns widgets from TileSmith 0.4 (widgets.json) into designs. Switches, sliders, choices and buttons that drive
/// Windows' own settings become the matching sources (sys.wifi, sys.volume…); anything else keeps its actions as
/// they were. Free-form layouts can't be translated faithfully, so they become a card that offers to rebuild them in
/// the Studio from the request that made them. widgets.json itself is left alone.
/// </summary>
public static class TileMigration
{
    public static List<TileDesign> FromWidgetsJson(string json)
    {
        var list = new List<TileDesign>();
        if (JsonNode.Parse(json, documentOptions: JsonDefaults.DocumentOptions) is not JsonArray widgets) return list;
        foreach (var w in widgets.OfType<JsonObject>())
        {
            try
            {
                if (Convert(w) is { } d) list.Add(d);
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't convert widget \"{TileCodec.Str(w, "title")}\": {ex.Message}");
            }
        }
        return list;
    }

    public static TileDesign? Convert(JsonObject w)
    {
        var title = TileCodec.Str(w, "title")?.Trim();
        if (string.IsNullOrEmpty(title)) title = "Widget";
        var icon = TileCodec.Str(w, "icon");
        var (cols, rowsCount) = Cells(TileCodec.Str(w, "size"));
        var refresh = TileCodec.Int(w, "refreshSeconds") ?? 0;
        var now = TileDesign.Now();

        if (w["layout"] is JsonObject)
        {
            // a free-form layout can't be translated faithfully: one click rebuilds it in the Studio from its request
            var prompt = TileCodec.Str(w, "prompt");
            return new TileDesign
            {
                Name = title,
                Size = TileSize.Mini,
                Rows = [new TileRow([new TileBlock { Type = BlockType.Button, Label = "Rebuild " + title, Icon = icon ?? "auto_awesome", Tap = TileTap.Studio }])],
                Chat = prompt is null ? [] : [new TileChatTurn(true, prompt, now), new TileChatTurn(false, "This widget was a free-form layout from TileSmith 0.4. Say \"rebuild it\" and I'll make it again.", now)],
                CreatedAt = now,
                UpdatedAt = now,
            };
        }

        var controls = (w["controls"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (controls.Count == 0) return null;
        var blocks = controls.Select(c => Block(c, title, icon)).ToList();
        // a lone value sits on the widget itself, not in a card inside the card
        if (blocks.Count == 1 && blocks[0].Type == BlockType.Metric) blocks[0] = blocks[0] with { Style = BlockStyle.Plain };

        // one control: it is the tile; several: a card with a row each (buttons side by side, as 0.4 drew them)
        var rows = new List<TileRow>();
        var buttons = new List<TileBlock>();
        foreach (var b in blocks)
        {
            if (b.Type == BlockType.Button)
            {
                buttons.Add(b);
                if (buttons.Count == 4) { rows.Add(new TileRow(buttons)); buttons = []; }
                continue;
            }
            if (buttons.Count > 0) { rows.Add(new TileRow(buttons)); buttons = []; }
            rows.Add(new TileRow([b]));
        }
        if (buttons.Count > 0) rows.Add(new TileRow(buttons));
        var target = TileSize.ForCells(cols, rowsCount);
        if (rows.Count > target.MaxRows || rows.Max(r => r.Cells.Count) > target.MaxCells)
            target = rows.Count <= 2 && rows.Max(r => r.Cells.Count) <= 4 ? TileSize.Wide : TileSize.Large;
        return TileRules.Sanitize(new TileDesign
        {
            Name = title,
            Size = target,
            Surface = blocks.Count == 1 ? TileSurface.Tonal : TileSurface.Soft,
            Refresh = Math.Clamp(refresh, 0, TileRules.MaxRefresh),
            Rows = rows,
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private static (int Cols, int Rows) Cells(string? size)
    {
        var t = (size ?? "").Trim().ToLowerInvariant().Replace('×', 'x').Replace(" ", "");
        var parts = t.Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out var c) && int.TryParse(parts[1], out var r)) return (Math.Clamp(c, 1, 4), Math.Clamp(r, 1, 3));
        return t switch { "small" => (2, 1), "medium" => (2, 2), "wide" => (4, 1), "large" => (4, 2), _ => (2, 1) };
    }

    private static string? Type(JsonObject? action) => action is null ? null : TileCodec.Str(action, "type");

    private static TileBlock Block(JsonObject c, string title, string? widgetIcon)
    {
        var type = TileCodec.Str(c, "type")?.ToLowerInvariant();
        var label = TileCodec.Str(c, "label") is { Length: > 0 } l ? l : title;
        var icon = TileCodec.Str(c, "icon") ?? widgetIcon;
        var state = c["state"] as JsonObject;
        var action = c["action"] as JsonObject;
        var stateType = Type(state);
        JsonObject? Clone(JsonNode? n) => n as JsonObject is { } o ? (JsonObject)o.DeepClone() : null;

        switch (type)
        {
            case "toggle":
            {
                var bind = stateType switch
                {
                    "radio.get" => TileCodec.Str(state!, "kind") == "bluetooth" ? "sys.bluetooth" : "sys.wifi",
                    "theme.get" => "sys.dark_mode",
                    "audio.getMute" => "sys.mute",
                    "audio.getMicMute" => "sys.mic",
                    _ => null,
                };
                if (bind is not null) return new TileBlock { Type = BlockType.Toggle, Bind = bind, Label = label, Icon = icon };
                if (stateType == "app.isRunning")
                    return new TileBlock { Type = BlockType.Toggle, Bind = "sys.app", Query = TileCodec.Str(state!, "name"), Label = label, Icon = icon };
                return new TileBlock
                {
                    Type = BlockType.Toggle,
                    Bind = state is null ? null : "custom.state",
                    Label = label,
                    Icon = icon,
                    State = Clone(state),
                    OnValue = TileCodec.Str(c, "onValue"),
                    On = Clone(c["on"]),
                    Off = Clone(c["off"]),
                    Action = Clone(action),
                };
            }
            case "slider":
            {
                var bind = stateType switch { "audio.getVolume" => "sys.volume", "display.getBrightness" => "sys.brightness", _ => null };
                if (bind is not null) return new TileBlock { Type = BlockType.Slider, Bind = bind, Label = label, Icon = icon };
                return new TileBlock
                {
                    Type = BlockType.Slider,
                    Bind = state is null ? null : "custom.state",
                    Label = label,
                    Icon = icon,
                    State = Clone(state),
                    Action = Clone(action),
                    From = TileCodec.Dbl(c, "min"),
                    To = TileCodec.Dbl(c, "max"),
                    Step = TileCodec.Dbl(c, "step"),
                };
            }
            case "choice":
            {
                var options = (c["options"] as JsonArray)?.OfType<JsonObject>().Select(o =>
                {
                    var optLabel = TileCodec.Str(o, "label") ?? "";
                    var value = TileCodec.Str(o, "value") is { Length: > 0 } v ? v : optLabel;
                    return new TileOption(optLabel, value, TileCodec.Str(o, "icon"), Clone(o["action"]));
                }).ToList() ?? [];
                var firstAction = Type(options.FirstOrDefault()?.Action);
                var bind = stateType switch
                {
                    "power.getMode" => "sys.power_mode",
                    "display.getResolution" => "sys.resolution",
                    "display.getRefreshRate" => "sys.refresh_rate",
                    null when firstAction == "display.project" => "sys.projection",
                    null => null,
                    _ => "custom.state",
                };
                if (bind is "sys.power_mode" or "sys.resolution" or "sys.refresh_rate" or "sys.projection")
                {
                    // the source switches by value; the 0.4 actions aren't needed
                    var query = state is null ? null : TileCodec.Str(state, "display");
                    var values = bind == "sys.projection"
                        ? options.Select(o => o with { Value = TileCodec.Str(o.Action!, "mode") ?? o.Value, Action = null }).ToList()
                        : options.Select(o => o with { Action = null }).ToList();
                    return new TileBlock { Type = BlockType.Choice, Bind = bind, Label = label, Icon = icon, Options = values, Query = query };
                }
                return new TileBlock { Type = BlockType.Choice, Bind = bind, Label = label, Icon = icon, Options = options, State = Clone(state) };
            }
            case "status":
            {
                if (stateType == "battery.get") return new TileBlock { Type = BlockType.Metric, Bind = "pc.battery", Label = label, Icon = icon };
                if (stateType == "system.stats" && TileCodec.Str(state!, "metric") is "cpu" or "memory" or "disk")
                    return new TileBlock { Type = BlockType.Metric, Bind = "pc." + TileCodec.Str(state!, "metric"), Label = label, Icon = icon };
                return new TileBlock { Type = BlockType.Metric, Bind = state is null ? null : "custom.state", Label = label, Icon = icon, State = Clone(state), Text = state is null ? label : null };
            }
            default:
            {
                var tap = action is null ? null : TapFor(action);
                return new TileBlock
                {
                    Type = BlockType.Button,
                    Label = TileCodec.Str(c, "label") is { Length: > 0 } bl ? bl : (icon is null ? title : null),
                    Icon = icon,
                    Tap = tap,
                    Action = tap is null ? Clone(action) : null,
                    Confirm = TileCodec.Str(c, "confirm"),
                    Style = TileCodec.Str(c, "style") == "accent" ? BlockStyle.Filled : BlockStyle.Tile,
                };
            }
        }
    }

    /// <summary>A 0.4 action as a tap target, when one says the same thing.</summary>
    public static string? TapFor(JsonObject action)
    {
        string? S(string k) => TileCodec.Str(action, k);
        return Type(action) switch
        {
            "keys.send" => S("keys") is { } keys ? TileTap.Normalize("keys:" + keys) : null,
            "url.open" => S("url") is { } url ? TileTap.Normalize(url.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) ? "url:" + url : url) : null,
            "folder.open" => S("path") is { } path ? "folder:" + path : null,
            "app.launch" when S("admin") is not "true" && S("workingDir") is null => S("path") is { } p ? "run:" + (S("args") is { Length: > 0 } a ? (p.Contains(' ') ? $"\"{p}\" {a}" : $"{p} {a}") : p) : null,
            "power.action" => S("action") is { } a ? TileTap.Normalize("power:" + a) : null,
            "media.key" => S("key") switch { "next" => "media:next", "previous" => "media:prev", "playPause" => "media:toggle", _ => null },
            "clipboard.setText" => S("text") is { } t ? "copy:" + t : null,
            _ => null,
        };
    }
}
