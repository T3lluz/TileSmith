using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TileSmith.Core.Actions;

namespace TileSmith.Core.Widgets;

/// <summary>A named live value of a layout widget.</summary>
public sealed record DataSource(string Name, JsonObject Action, int? Every);

/// <summary>
/// The styling rules for layout widgets: colours, type sizes and weights come from the panel's design tokens (so
/// tiles follow light/dark mode and the accent colour); raw values are allowed within limits.
/// </summary>
public static partial class StyleTokens
{
    public static readonly string[] Colors =
        ["accent", "onAccent", "text", "text2", "text3", "surface", "card", "well", "success", "warning", "error", "white", "black", "transparent"];

    public static readonly Dictionary<string, double> FontSizes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["caption"] = 11, ["label"] = 12.5, ["body"] = 13.5, ["title"] = 16, ["headline"] = 22, ["display"] = 32, ["hero"] = 44,
    };

    public static readonly string[] Weights = ["regular", "medium", "semibold", "bold"];

    public const double MinFont = 9, MaxFont = 56;

    /// <summary>"accent", "text2/60" (60% opacity), "#3FA266", "#803FA266".</summary>
    public static bool IsColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (v.StartsWith('#')) return HexRegex().IsMatch(v);
        var slash = v.IndexOf('/');
        var name = slash < 0 ? v : v[..slash];
        if (slash >= 0 && !(int.TryParse(v[(slash + 1)..], out var alpha) && alpha is >= 0 and <= 100)) return false;
        return Colors.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Font size in px from a token or a number (null when invalid).</summary>
    public static double? FontSize(JsonNode? node, double fallback)
    {
        if (node is null) return fallback;
        var text = WidgetLayout.Text(node);
        if (text is null) return null;
        if (FontSizes.TryGetValue(text, out var px)) return px;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Math.Clamp(d, MinFont, MaxFont) : null;
    }

    /// <summary>Relative luminance of a #hex colour (null for tokens, which follow the theme).</summary>
    public static double? HexLuminance(string? value)
    {
        if (value is null || !value.StartsWith('#') || !HexRegex().IsMatch(value)) return null;
        var hex = value[1..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length == 8) hex = hex[2..];
        double Channel(int i)
        {
            var c = int.Parse(hex.AsSpan(i, 2), NumberStyles.HexNumber) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }

    public static double Contrast(double l1, double l2) => (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);

    [GeneratedRegex("^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexRegex();
}

/// <summary>
/// Layout widgets: a tree of elements the AI composes freely (like Android's Glance / KWGT or a generative-UI
/// catalog), within rules that keep tiles consistent and readable. Shared by the validator, the renderer and the prompt.
/// </summary>
public static partial class WidgetLayout
{
    public const int MaxDepth = 7;
    public const int MaxElements = 60;
    public const double RootPadding = 14;

    public static readonly string[] Containers = ["row", "column", "stack", "list"];
    public static readonly string[] Content = ["text", "icon", "image", "progress", "ring", "sparkline", "spacer", "divider"];
    public static readonly string[] Interactive = ["button", "toggle", "slider", "choice"];
    public static IEnumerable<string> AllTypes => Containers.Concat(Content).Concat(Interactive);

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hstack"] = "row", ["horizontal"] = "row", ["vstack"] = "column", ["col"] = "column", ["vertical"] = "column",
        ["box"] = "stack", ["overlay"] = "stack", ["zstack"] = "stack", ["layer"] = "stack",
        ["label"] = "text", ["value"] = "text", ["img"] = "image", ["picture"] = "image", ["bar"] = "progress",
        ["progressbar"] = "progress", ["gauge"] = "ring", ["donut"] = "ring", ["chart"] = "sparkline", ["graph"] = "sparkline",
        ["switch"] = "toggle", ["segmented"] = "choice", ["select"] = "choice", ["space"] = "spacer", ["separator"] = "divider",
    };

    private static readonly string[] CommonProps =
        ["type", "id", "visible", "tap", "tooltip", "width", "height", "flex", "padding", "background", "radius", "border", "opacity", "align", "valign"];

    private static readonly Dictionary<string, string[]> OwnProps = new()
    {
        ["row"] = ["children", "gap"],
        ["column"] = ["children", "gap"],
        ["stack"] = ["children"],
        ["list"] = ["items", "template", "max", "direction", "gap", "empty"],
        ["text"] = ["text", "size", "weight", "color", "maxLines", "textAlign", "mono"],
        ["icon"] = ["icon", "size", "color"],
        ["image"] = ["src", "fit"],
        ["progress"] = ["value", "min", "max", "color", "track", "thickness"],
        ["ring"] = ["value", "min", "max", "color", "track", "thickness", "size", "children"],
        ["sparkline"] = ["value", "color", "fill", "points", "min", "max"],
        ["spacer"] = ["size"],
        ["divider"] = ["color"],
        ["button"] = ["label", "icon", "action", "confirm", "variant", "style", "color"],
        ["toggle"] = ["label", "icon", "state", "onValue", "on", "off", "action", "onText", "offText", "variant"],
        ["slider"] = ["label", "icon", "state", "action", "min", "max", "step", "format"],
        ["choice"] = ["label", "state", "options"],
    };

    private static readonly string[] ColorProps = ["color", "background", "track", "border"];

    /// <summary>Canonical element type ("hstack" → "row"), or null.</summary>
    public static string? TypeOf(JsonObject el)
    {
        var t = Text(el["type"])?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        if (AllTypes.Contains(t, StringComparer.OrdinalIgnoreCase)) return t.ToLowerInvariant();
        return Synonyms.TryGetValue(t, out var s) ? s : null;
    }

    public static IEnumerable<JsonObject> Children(JsonObject el) =>
        el["children"] is JsonArray arr ? arr.OfType<JsonObject>() : el["template"] is JsonObject template ? [template] : [];

    /// <summary>The data path a list repeats over: "{weather.days}" → "weather.days".</summary>
    public static string? ListPath(JsonObject list) =>
        Bindings.PathsIn(Text(list["items"])).FirstOrDefault();

    /// <summary>
    /// One copy of a list's template for item <paramref name="index"/>: "{item.x}" becomes "{path[index].x}" and
    /// "{index}" the 1-based position, everywhere in the copy.
    /// </summary>
    public static JsonObject Instantiate(JsonObject template, string path, int index)
    {
        var json = template.ToJsonString()
            .Replace("{item}", "{" + path + "[" + index + "]}", StringComparison.Ordinal)
            .Replace("{item.", "{" + path + "[" + index + "].", StringComparison.Ordinal)
            .Replace("{item|", "{" + path + "[" + index + "]|", StringComparison.Ordinal)
            .Replace("{index}", (index + 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return JsonNode.Parse(json)!.AsObject();
    }

    /// <summary>Every element, depth first.</summary>
    public static IEnumerable<JsonObject> Walk(JsonObject root)
    {
        var stack = new Stack<JsonObject>();
        stack.Push(root);
        var guard = 0;
        while (stack.Count > 0 && guard++ < 500)
        {
            var el = stack.Pop();
            yield return el;
            foreach (var c in Children(el).Reverse()) stack.Push(c);
        }
    }

    public static IEnumerable<JsonObject> ActionsOf(JsonObject el)
    {
        foreach (var key in new[] { "tap", "action", "on", "off", "state" })
        {
            if (el[key] is JsonObject a) yield return a;
        }
        if (el["options"] is JsonArray options)
        {
            foreach (var o in options.OfType<JsonObject>())
            {
                if (o["action"] is JsonObject a) yield return a;
            }
        }
    }

    public static IReadOnlyList<DataSource> DataSources(WidgetDefinition widget)
    {
        var list = new List<DataSource>();
        if (widget.Data is null) return list;
        foreach (var (name, node) in widget.Data)
        {
            if (node is not JsonObject obj) continue;
            if (obj["action"] is JsonObject action)
            {
                int? every = int.TryParse(Text(obj["every"]), NumberStyles.Integer, CultureInfo.InvariantCulture, out var e) ? Math.Max(1, e) : null;
                list.Add(new DataSource(name, action, every));
            }
            else if (obj["type"] is not null)
            {
                list.Add(new DataSource(name, obj, null));
            }
        }
        return list;
    }

    /// <summary>String value of a JSON node (numbers and bools as text).</summary>
    public static string? Text(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.Number => node.GetValue<JsonElement>().GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null,
    };

    public static double? Num(JsonNode? node) =>
        double.TryParse(Text(node), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>
    /// A property that may depend on data: a plain value, or {"if": "{cpu} > 80", "then": "error", "else": "accent"}
    /// (branches may nest). Returns the chosen text.
    /// </summary>
    public static string? Prop(JsonObject el, string key, BindingData data) => Choose(el[key], data);

    public static string? Choose(JsonNode? node, BindingData data)
    {
        for (var guard = 0; guard < 8 && node is JsonObject cond && cond.ContainsKey("if"); guard++)
            node = Bindings.Test(Text(cond["if"]), data) ? cond["then"] : cond["else"];
        return Text(node);
    }

    /// <summary>All plain values a property can take (both branches of conditionals), for validation.</summary>
    public static IEnumerable<string> Branches(JsonNode? node)
    {
        if (node is JsonObject cond && cond.ContainsKey("if"))
        {
            foreach (var b in Branches(cond["then"])) yield return b;
            foreach (var b in Branches(cond["else"])) yield return b;
            yield break;
        }
        if (Text(node) is { } t) yield return t;
    }

    private static IEnumerable<string> Conditions(JsonNode? node)
    {
        if (node is not JsonObject cond || !cond.ContainsKey("if")) yield break;
        if (Text(cond["if"]) is { } c) yield return c;
        foreach (var n in Conditions(cond["then"])) yield return n;
        foreach (var n in Conditions(cond["else"])) yield return n;
    }

    // ── Validation ──────────────────────────────────────────────────────────────────

    public static void Validate(WidgetDefinition w, List<string> errors, List<string> warnings)
    {
        var sources = ValidateData(w, errors, warnings);
        if (w.Layout is null) return;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var count = 0;
        ValidateElement(w.Layout, "layout", 1, sources, used, ref count, errors, warnings);
        if (count > MaxElements) errors.Add($"layout has {count} elements; keep it under {MaxElements} — split it into two widgets.");
        foreach (var s in sources.Where(s => !used.Contains(s)))
            warnings.Add($"data \"{s}\" isn't used by the layout.");
    }

    private static HashSet<string> ValidateData(WidgetDefinition w, List<string> errors, List<string> warnings)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (w.Data is null) return names;
        foreach (var (name, node) in w.Data)
        {
            if (!NameRegex().IsMatch(name) || name.Equals("value", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"data name \"{name}\" must be a simple identifier like cpu or nowPlaying (and not \"value\").");
                continue;
            }
            names.Add(name);
            if (node is not JsonObject obj)
            {
                errors.Add($"data.{name} must be a query action object.");
                continue;
            }
            var action = obj["action"] as JsonObject ?? obj;
            if (obj["every"] is { } every && Num(every) is not (>= 1 and <= 86400))
                errors.Add($"data.{name}.every must be a number of seconds between 1 and 86400.");
            WidgetValidator.ValidateAction(action, $"data.{name}", errors, warnings, required: true, query: true);
        }
        if (w.Data.Count > 12) errors.Add("A widget can have at most 12 data sources.");
        return names;
    }

    private static void ValidateElement(JsonObject el, string path, int depth, HashSet<string> sources, HashSet<string> used, ref int count,
        List<string> errors, List<string> warnings)
    {
        count++;
        if (depth > MaxDepth)
        {
            errors.Add($"{path}: layouts can be at most {MaxDepth} levels deep.");
            return;
        }
        var type = TypeOf(el);
        if (type is null)
        {
            errors.Add($"{path}.type \"{Text(el["type"])}\" isn't an element. Use one of: {string.Join(", ", AllTypes)}.");
            return;
        }
        el["type"] = type;

        // template sources must exist
        foreach (var (key, node) in el.ToList())
        {
            if (key is "children" or "template" or "options" or "tap" or "action" or "on" or "off" or "state") continue;
            foreach (var text in Branches(node).Concat(Conditions(node)))
            {
                foreach (var s in Bindings.SourcesIn(text))
                {
                    used.Add(s);
                    if (!sources.Contains(s) && !(InTemplate(path) && s is "item" or "index"))
                        errors.Add($"{path}.{key} uses \"{{{s}}}\" but there is no data \"{s}\"{(sources.Count > 0 ? $" (data: {string.Join(", ", sources)})" : " — add it under \"data\"")}.");
                }
            }
        }

        var allowed = CommonProps.Concat(OwnProps[type]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = el.Select(kv => kv.Key).Where(k => !allowed.Contains(k)).ToList();
        if (unknown.Count > 0) warnings.Add($"{path} ({type}): {string.Join(", ", unknown.Select(u => $"\"{u}\""))} is not a property of {type} and is ignored.");

        foreach (var key in ColorProps)
        {
            foreach (var value in Branches(el[key]))
            {
                if (!StyleTokens.IsColor(value))
                    errors.Add($"{path}.{key} \"{value}\" isn't a colour. Use a token ({string.Join(", ", StyleTokens.Colors)}), token/opacity like \"accent/20\", or #RRGGBB.");
            }
        }
        CheckNumber(el, "padding", 0, 32, path, errors, allowArray: true);
        CheckNumber(el, "gap", 0, 32, path, errors);
        CheckNumber(el, "radius", 0, 48, path, errors);
        CheckNumber(el, "flex", 0, 12, path, errors);
        CheckNumber(el, "opacity", 0, 1, path, errors);
        foreach (var dim in new[] { "width", "height" })
        {
            var t = Text(el[dim]);
            if (el[dim] is not null && t != "fill" && Num(el[dim]) is not (>= 0 and <= 520))
                errors.Add($"{path}.{dim} must be a number of px (0–520) or \"fill\".");
        }
        foreach (var key in new[] { "align", "valign", "textAlign" })
        {
            var v = Text(el[key]);
            if (v is not null && v is not ("start" or "center" or "end" or "stretch" or "left" or "right" or "top" or "bottom"))
                errors.Add($"{path}.{key} must be start, center, end or stretch.");
        }
        if (el["tap"] is JsonObject tap) WidgetValidator.ValidateAction(tap, $"{path}.tap", errors, warnings, required: true, query: false);
        else if (el["tap"] is not null) errors.Add($"{path}.tap must be an action object.");

        switch (type)
        {
            case "row" or "column" or "stack":
                if (el["children"] is not JsonArray arr || arr.Count == 0) errors.Add($"{path}: a {type} needs \"children\".");
                break;
            case "list":
                if (ListPath(el) is not { } listPath || Bindings.PathsIn(Text(el["items"])).Count() != 1)
                    errors.Add($"{path}.items must be one data value that is a JSON array, like \"{{top}}\" or \"{{weather.days}}\".");
                else if (!sources.Contains(Bindings.SplitPath(listPath).Name))
                    errors.Add($"{path}.items uses \"{{{listPath}}}\" but there is no data \"{Bindings.SplitPath(listPath).Name}\".");
                else used.Add(Bindings.SplitPath(listPath).Name);
                if (el["template"] is not JsonObject) errors.Add($"{path}: a list needs \"template\": the element shown for each item (use {{item.field}} inside it).");
                CheckNumber(el, "max", 1, 20, path, errors);
                if (Text(el["direction"]) is { } direction && direction is not ("column" or "row")) errors.Add($"{path}.direction must be column or row.");
                break;
            case "text":
                if (!Branches(el["text"]).Any()) errors.Add($"{path}: text needs \"text\".");
                if (StyleTokens.FontSize(el["size"], 13) is null)
                    errors.Add($"{path}.size must be {string.Join("/", StyleTokens.FontSizes.Keys)} or a number {StyleTokens.MinFont}–{StyleTokens.MaxFont}.");
                if (Text(el["weight"]) is { } weight && !StyleTokens.Weights.Contains(weight, StringComparer.OrdinalIgnoreCase))
                    errors.Add($"{path}.weight must be one of {string.Join(", ", StyleTokens.Weights)}.");
                CheckNumber(el, "maxLines", 1, 6, path, errors);
                CheckContrast(el, path, warnings);
                break;
            case "icon":
                if (!Branches(el["icon"]).Any()) errors.Add($"{path}: icon needs \"icon\" (a Material Symbols name).");
                foreach (var name in Branches(el["icon"]).Where(n => !Bindings.HasPlaceholders(n)))
                {
                    if (Icons.Normalize(name) is null) warnings.Add($"{path}: unknown icon \"{name}\" — a default icon is shown.");
                }
                if (el["icon"] is JsonValue && Text(el["icon"]) is { } iconName && !Bindings.HasPlaceholders(iconName) && Icons.Normalize(iconName) is { } normalized)
                    el["icon"] = normalized;
                CheckNumber(el, "size", 8, 96, path, errors);
                break;
            case "image":
                if (!Branches(el["src"]).Any()) errors.Add($"{path}: image needs \"src\" (https URL, file path or data URI).");
                if (Text(el["fit"]) is { } fit && fit is not ("cover" or "contain" or "fill"))
                    errors.Add($"{path}.fit must be cover, contain or fill.");
                break;
            case "progress" or "ring" or "sparkline":
                if (el["value"] is null) errors.Add($"{path}: {type} needs \"value\" (a number or \"{{name}}\").");
                if (type == "sparkline")
                {
                    var v = Text(el["value"]) ?? "";
                    if (Bindings.PathsIn(v).Count() != 1) errors.Add($"{path}.value must be exactly one data value like \"{{cpu}}\" — the line shows its recent history.");
                    CheckNumber(el, "points", 5, BindingData.HistoryLength, path, errors);
                }
                if (Num(el["min"]) is { } min && Num(el["max"]) is { } max && min >= max) errors.Add($"{path}: min must be less than max.");
                CheckNumber(el, "thickness", 1, 24, path, errors);
                if (type == "ring") CheckNumber(el, "size", 24, 200, path, errors);
                break;
            case "button":
                if (!Branches(el["label"]).Any() && !Branches(el["icon"]).Any()) errors.Add($"{path}: a button needs \"label\" or \"icon\".");
                WidgetValidator.ValidateAction(el["action"] as JsonObject, $"{path}.action", errors, warnings, required: true, query: false);
                if (Text(el["variant"]) is { } variant && variant is not ("filled" or "tonal" or "outline" or "ghost" or "icon"))
                    errors.Add($"{path}.variant must be filled, tonal, outline, ghost or icon.");
                break;
            case "toggle":
                if (el["on"] is null && el["off"] is null && el["action"] is JsonObject single)
                {
                    WidgetValidator.ValidateAction(single, $"{path}.action", errors, warnings, required: true, query: false);
                }
                else
                {
                    WidgetValidator.ValidateAction(el["on"] as JsonObject, $"{path}.on", errors, warnings, required: true, query: false);
                    WidgetValidator.ValidateAction(el["off"] as JsonObject, $"{path}.off", errors, warnings, required: true, query: false);
                }
                if (el["state"] is null) warnings.Add($"{path}: toggle has no \"state\", so it can't show whether it's on.");
                WidgetValidator.ValidateAction(el["state"] as JsonObject, $"{path}.state", errors, warnings, required: false, query: true);
                if (Text(el["variant"]) is { } tv && tv is not ("switch" or "tile" or "chip"))
                    errors.Add($"{path}.variant must be switch, tile or chip.");
                break;
            case "slider":
                WidgetValidator.ValidateAction(el["action"] as JsonObject, $"{path}.action", errors, warnings, required: true, query: false);
                if (el["action"] is JsonObject sa && !sa.ToJsonString().Contains("{value}", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{path}.action must use \"{{value}}\" so the slider position is passed in.");
                WidgetValidator.ValidateAction(el["state"] as JsonObject, $"{path}.state", errors, warnings, required: false, query: true);
                if (Num(el["min"]) is { } smin && Num(el["max"]) is { } smax && smin >= smax) errors.Add($"{path}: min must be less than max.");
                break;
            case "choice":
                var options = el["options"] as JsonArray;
                if (options is null || options.Count < 2) errors.Add($"{path}: a choice needs at least 2 options.");
                else if (options.Count > 6) errors.Add($"{path}: a choice can have at most 6 options.");
                for (var i = 0; i < (options?.Count ?? 0); i++)
                {
                    if (options![i] is not JsonObject o) { errors.Add($"{path}.options[{i}] must be an object."); continue; }
                    if (string.IsNullOrWhiteSpace(Text(o["label"])) && string.IsNullOrWhiteSpace(Text(o["icon"]))) errors.Add($"{path}.options[{i}] needs a label.");
                    WidgetValidator.ValidateAction(o["action"] as JsonObject, $"{path}.options[{i}].action", errors, warnings, required: true, query: false);
                }
                WidgetValidator.ValidateAction(el["state"] as JsonObject, $"{path}.state", errors, warnings, required: false, query: true);
                break;
        }

        if (type == "list" && el["template"] is JsonObject itemTemplate)
            ValidateElement(itemTemplate, $"{path}.template", depth + 1, sources, used, ref count, errors, warnings);
        var i2 = 0;
        if (el["children"] is JsonArray children)
        {
            if (type is not ("row" or "column" or "stack" or "ring")) warnings.Add($"{path}: a {type} can't have children; they are ignored.");
            foreach (var child in children)
            {
                if (child is JsonObject c) ValidateElement(c, $"{path}.children[{i2}]", depth + 1, sources, used, ref count, errors, warnings);
                else errors.Add($"{path}.children[{i2}] must be an element object.");
                i2++;
            }
        }
    }

    private static bool InTemplate(string path) => path.Contains(".template", StringComparison.Ordinal);

    private static void CheckNumber(JsonObject el, string key, double min, double max, string path, List<string> errors, bool allowArray = false)
    {
        var node = el[key];
        if (node is null) return;
        if (allowArray && node is JsonArray arr)
        {
            if (arr.Count is not (2 or 4) || arr.Any(n => Num(n) is not { } d || d < min || d > max))
                errors.Add($"{path}.{key} must be a number or [horizontal, vertical] / [left, top, right, bottom], each {min}–{max}.");
            return;
        }
        if (Num(node) is not { } v || v < min || v > max) errors.Add($"{path}.{key} must be a number from {min} to {max}.");
    }

    private static void CheckContrast(JsonObject text, string path, List<string> warnings)
    {
        // only fixed colours can be checked; tokens are tuned for both themes
        var fg = Branches(text["color"]).Select(StyleTokens.HexLuminance).FirstOrDefault(l => l is not null);
        var bg = Branches(text["background"]).Select(StyleTokens.HexLuminance).FirstOrDefault(l => l is not null);
        if (fg is { } f && bg is { } b && StyleTokens.Contrast(f, b) < 3)
            warnings.Add($"{path}: the text colour is hard to read on its background (contrast {StyleTokens.Contrast(f, b):0.0}:1, aim for 4.5:1).");
    }

    // ── Size ────────────────────────────────────────────────────────────────────────

    /// <summary>Natural size of an element in px (an estimate: text is measured by character count).</summary>
    public static (double W, double H) Estimate(JsonObject el)
    {
        var type = TypeOf(el) ?? "text";
        var pad = Padding(el);
        (double W, double H) inner;
        switch (type)
        {
            case "row":
            {
                var kids = Children(el).Select(Estimate).ToList();
                var gap = Num(el["gap"]) ?? 8;
                inner = (kids.Sum(k => k.W) + gap * Math.Max(0, kids.Count - 1), kids.Count == 0 ? 0 : kids.Max(k => k.H));
                break;
            }
            case "column":
            {
                var kids = Children(el).Select(Estimate).ToList();
                var gap = Num(el["gap"]) ?? 6;
                inner = (kids.Count == 0 ? 0 : kids.Max(k => k.W), kids.Sum(k => k.H) + gap * Math.Max(0, kids.Count - 1));
                break;
            }
            case "list":
            {
                var item = el["template"] is JsonObject t ? Estimate(t) : (0, 0);
                var n = Num(el["max"]) ?? 5;
                var gap = Num(el["gap"]) ?? 6;
                inner = Text(el["direction"]) == "row"
                    ? (item.W * n + gap * (n - 1), item.H)
                    : (item.W, item.H * n + gap * (n - 1));
                break;
            }
            case "stack":
            {
                var kids = Children(el).Select(Estimate).ToList();
                inner = (kids.Count == 0 ? 0 : kids.Max(k => k.W), kids.Count == 0 ? 0 : kids.Max(k => k.H));
                break;
            }
            case "text":
            {
                var size = StyleTokens.FontSize(el["size"], 13) ?? 13;
                var lines = Num(el["maxLines"]) ?? 1;
                var sample = Branches(el["text"]).OrderByDescending(t => t.Length).FirstOrDefault() ?? "";
                // placeholders count as ~5 characters; long text gets cut off with … so it never needs more than ~14
                var chars = Math.Min(14, PlaceholderLength(sample));
                inner = (chars * size * 0.55, Math.Ceiling(size * 1.33) * lines);
                break;
            }
            case "icon":
            {
                var size = Num(el["size"]) ?? 20;
                inner = (size, size);
                break;
            }
            case "image": inner = (48, 48); break;
            case "progress": inner = (40, Num(el["thickness"]) ?? 6); break;
            case "ring":
            {
                var size = Num(el["size"]) ?? 56;
                inner = (size, size);
                break;
            }
            case "sparkline": inner = (60, 28); break;
            case "spacer": inner = (Num(el["size"]) ?? 0, Num(el["size"]) ?? 0); break;
            case "divider": inner = (1, 1); break;
            case "button":
            {
                var label = Branches(el["label"]).OrderByDescending(t => t.Length).FirstOrDefault() ?? "";
                var iconOnly = label.Length == 0 || Text(el["variant"]) == "icon";
                inner = iconOnly ? (36, 36) : (Math.Min(14, label.Length) * 7 + (el["icon"] is null ? 28 : 50), 36);
                break;
            }
            case "toggle":
            {
                var variant = Text(el["variant"]) ?? "switch";
                var label = Text(el["label"]) ?? "";
                inner = variant == "tile" ? (TileLayout.Width(2) - 2 * RootPadding, 56) : (Math.Min(14, label.Length) * 7 + 56, 32);
                break;
            }
            case "slider": inner = (120, 40); break;
            case "choice":
            {
                var opts = (el["options"] as JsonArray)?.OfType<JsonObject>().Select(o => Text(o["label"]) ?? "").ToList() ?? [];
                var widest = opts.Count == 0 ? 0 : opts.Max(o => o.Length) * TileLayout.CharWidth;
                inner = (opts.Count * (widest + 20) + 6, 36 + (string.IsNullOrWhiteSpace(Text(el["label"])) ? 0 : 22));
                break;
            }
            default: inner = (0, 0); break;
        }
        var w = Num(el["width"]) ?? inner.W + pad.L + pad.R;
        var h = Num(el["height"]) ?? inner.H + pad.T + pad.B;
        return (w, h);
    }

    private static int PlaceholderLength(string text) =>
        PlaceholderRegexLite().Replace(text, "xxxxx").Length;

    public static (double L, double T, double R, double B) Padding(JsonObject el, double fallback = 0)
    {
        var node = el["padding"];
        if (node is JsonArray arr)
        {
            var n = arr.Select(x => Num(x) ?? 0).ToArray();
            return n.Length == 4 ? (n[0], n[1], n[2], n[3]) : n.Length == 2 ? (n[0], n[1], n[0], n[1]) : (fallback, fallback, fallback, fallback);
        }
        var p = Num(node) ?? fallback;
        return (p, p, p, p);
    }

    /// <summary>The smallest footprint a layout fits in, with the tile's own padding around it.</summary>
    public static CellSize Minimum(WidgetDefinition widget)
    {
        if (widget.Layout is null) return new(1, 1);
        var (w, h) = Estimate(widget.Layout);
        if (widget.Layout["padding"] is null)
        {
            w += 2 * RootPadding;
            h += 2 * RootPadding;
        }
        var cols = w <= TileLayout.Width(1) + 2 ? 1 : w <= TileLayout.Width(2) + 2 ? 2 : 4;
        return new(cols, TileLayout.RowsFor(h - 4));
    }

    // ── Prompt ──────────────────────────────────────────────────────────────────────

    public static string DescribeForPrompt() => $$"""
        ## Layout widgets — design your own tile
        When the standard controls can't show what the user wants (a dashboard, now playing with cover art, gauges, a
        countdown, a checklist, several values with their own styling, a custom look), give the widget a "layout" instead:
        a tree of elements. Add "data" for the live values it shows. Keep "controls": [] for layout widgets.

        "data": { "<name>": Action, "<name>": {"action": Action, "every": seconds} }   // query actions; refreshed when the panel
            opens, after any tap, and every "every" (or the widget's "refreshSeconds") seconds while it is open.
            Put several values in ONE source: a script/query that prints JSON, read as "{name.field}" / "{name.items[0].x}".
        Text and most properties can use "{name}" placeholders with filters: |round |round:1 |percent (0.42→42) |bytes
            |duration (seconds→"3h 12m") |upper |lower |first (first line) |max:20 (shorten) |default:text.
        Conditional values: {"if": "{cpu} > 80", "then": "error", "else": "accent"} — conditions: "{x}" (has a value that isn't empty/false/0/off/none), "!{x}",
            ==, !=, >, <, >=, <=, contains, joined with && or ||.

        Elements (every element also takes: visible (condition), tap (Action: makes it clickable), tooltip, width/height (px or
        "fill"), flex (share of the free space in a row/column), padding (n or [h,v] or [l,t,r,b]), background, radius, border
        (colour), opacity, align/valign (start|center|end|stretch)):
        - row {children, gap} · column {children, gap} · stack {children} (layers on top of each other, e.g. text over an image)
        - list {items: "{name}" (a JSON array value), template: Element, max (1–20, default 5), direction: column|row, gap, empty: text}
            — repeats the template for each item; inside it use {item}, {item.field} and {index} (1, 2, 3…).
            For forecasts, top apps, containers, recent files… e.g. items "{weather.days}", template a row with {item.day} and {item.high}.
        - text {text, size, weight: regular|medium|semibold|bold, color, maxLines (1–6), textAlign, mono: bool}
        - icon {icon (Material Symbols name, may be a placeholder or conditional), size (px, default 20), color}
        - image {src: https URL | file path | data URI | "{media.art}", fit: cover|contain|fill}  (set width/height, radius)
        - progress {value, min=0, max=100, color, track, thickness} — a rounded bar
        - ring {value, min, max, size, thickness, color, track, children} — circular gauge; children sit in its middle
        - sparkline {value: "{name}" one number, points (5–60), color, fill: bool} — line of its recent values
        - spacer {size?} (no size = takes the free space) · divider {color}
        - button {label?, icon?, action, confirm?, variant: filled|tonal|outline|ghost|icon}
        - toggle {label?, icon?, state, onValue?, on, off, onText?, offText?, variant: switch|tile|chip}
        - slider {label?, icon?, min, max, step, state, action (uses {value}), format}
        - choice {label?, state, options:[{label, value, icon?, action}]} — segmented buttons

        Styling rules (they keep tiles native in light and dark mode — follow them):
        - Colours: tokens {{string.Join(", ", StyleTokens.Colors)}}; "token/NN" for NN% opacity ("accent/15" for a soft
          tint). #RRGGBB only for brand or meaningful colours (Spotify green, a status colour); then keep text readable.
        - Type: size tokens {{string.Join(", ", StyleTokens.FontSizes.Select(kv => $"{kv.Key} {kv.Value}"))}} px (or a number 9–56).
          One big number or title per tile; secondary text in text2/text3 at caption/label size.
        - The tile is already a rounded card (surface colour, {{RootPadding}} px padding) — don't add a background to the root unless you
          want a different fill (e.g. "accent" for an "on" state or a gradient-like brand colour). Inner chips/pills: radius 10–20.
        - Spacing: gaps 4–12 px. Align things to a clear grid; leave breathing room rather than filling every pixel.
        - Size the tile to the content using the cell sizes below; everything that doesn't fit is cut off.
        """;

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex PlaceholderRegexLite();
}
