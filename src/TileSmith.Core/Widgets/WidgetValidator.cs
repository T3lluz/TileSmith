using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TileSmith.Core.Actions;

namespace TileSmith.Core.Widgets;

public sealed record ValidationResult(WidgetDefinition Widget, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Checks a widget against the schema and the action catalog, and fixes harmless deviations
/// (synonyms, sizes, missing ids) so that small AI slips don't cost a full retry.
/// </summary>
public static class WidgetValidator
{
    private static readonly Dictionary<string, string> ControlSynonyms = new(StringComparer.OrdinalIgnoreCase)
    {
        ["button"] = ControlType.Button, ["action"] = ControlType.Button,
        ["toggle"] = ControlType.Toggle, ["switch"] = ControlType.Toggle, ["checkbox"] = ControlType.Toggle,
        ["choice"] = ControlType.Choice, ["segmented"] = ControlType.Choice, ["select"] = ControlType.Choice,
        ["radio"] = ControlType.Choice, ["dropdown"] = ControlType.Choice, ["choices"] = ControlType.Choice, ["options"] = ControlType.Choice,
        ["slider"] = ControlType.Slider, ["range"] = ControlType.Slider,
        ["status"] = ControlType.Status, ["label"] = ControlType.Status, ["text"] = ControlType.Status,
        ["display"] = ControlType.Status, ["info"] = ControlType.Status, ["value"] = ControlType.Status,
    };

    public static ValidationResult Validate(WidgetDefinition widget)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var w = widget;

        if (string.IsNullOrWhiteSpace(w.Id)) w.Id = WidgetDefinition.NewId();
        w.Schema = 1;

        w.Title = (w.Title ?? "").Trim();
        if (w.Title.Length == 0) errors.Add("\"title\" is required.");
        else if (w.Title.Length > 32) { w.Title = w.Title[..32].TrimEnd(); warnings.Add("Title was shortened."); }

        if (w.Icon is not null)
        {
            var icon = Icons.Normalize(w.Icon);
            if (icon is null) warnings.Add($"Unknown icon \"{w.Icon}\" — using a default icon.");
            w.Icon = icon ?? w.Icon;
        }

        if (w.RefreshSeconds is { } r && r is > 0 and < 2) w.RefreshSeconds = 2;
        if (w.RefreshSeconds is <= 0) w.RefreshSeconds = null;

        w.Controls ??= [];
        if (w.HasLayout)
        {
            if (w.Layout!.Count == 0) errors.Add("\"layout\" is empty — give it a root element like {\"type\":\"column\",\"children\":[…]}.");
            if (w.Controls.Count > 0)
            {
                warnings.Add("\"controls\" are ignored because the widget has a \"layout\" — put buttons/toggles inside the layout.");
                w.Controls = [];
            }
            WidgetLayout.Validate(w, errors, warnings);
        }
        else if (w.Controls.Count == 0)
        {
            errors.Add("\"controls\" must contain at least one control (or give the widget a \"layout\").");
        }
        else if (w.Controls.Count > 6)
        {
            errors.Add("A widget can have at most 6 controls — split it into several widgets.");
        }

        if (!w.HasLayout && w.Data is not null) warnings.Add("\"data\" is only used by layout widgets.");

        for (var i = 0; i < w.Controls.Count; i++)
        {
            ValidateControl(w.Controls[i], $"controls[{i}]", errors, warnings);
        }

        if (errors.Count == 0) FitSize(w, errors, warnings);
        foreach (var c in w.Controls)
        {
            if (c.Icon is not null) c.Icon = Icons.Normalize(c.Icon) ?? c.Icon;
            if (c.Options is null) continue;
            foreach (var o in c.Options)
            {
                if (o.Icon is not null) o.Icon = Icons.Normalize(o.Icon) ?? o.Icon;
            }
        }

        return new ValidationResult(w, errors, warnings);
    }

    /// <summary>
    /// Makes sure the content fits the tile: grows the size when the controls need more room, and points out labels
    /// and options that would be cut off, so the AI can shorten them.
    /// </summary>
    private static void FitSize(WidgetDefinition w, List<string> errors, List<string> warnings)
    {
        var requested = WidgetSize.TryParse(w.Size, out var parsed) ? parsed : WidgetSize.Default(w);
        var minimum = TileLayout.Minimum(w);
        if (minimum.H > WidgetSize.MaxHeight && w.HasLayout)
        {
            errors.Add($"This layout needs about {WidgetLayout.Estimate(w.Layout!).H + 2 * WidgetLayout.RootPadding:0} px of height, but a tile is at most " +
                       $"{TileLayout.Height(WidgetSize.MaxHeight):0} px ({WidgetSize.MaxHeight} rows). Use fewer rows, smaller text, or put things side by side.");
            return;
        }
        if (minimum.H > WidgetSize.MaxHeight)
        {
            errors.Add($"These controls need about {TileLayout.CardHeight(w):0} px of height, but a tile is at most {WidgetSize.MaxHeight} rows " +
                       $"({TileLayout.Height(WidgetSize.MaxHeight):0} px). Split it into two widgets or remove a control.");
            return;
        }
        var size = new CellSize(Math.Max(requested.W, minimum.W), Math.Max(requested.H, minimum.H));
        if (size.W == 3) size = size with { W = 4 };
        if (size != requested && WidgetSize.TryParse(w.Size, out _))
            warnings.Add($"Size {requested} was too small for this content (it needs at least {minimum}) — saved as {size}.");
        w.Size = size.ToString();
        if (w.HasLayout) return; // free layouts are measured as a whole (see WidgetLayout.Minimum)

        var single = TileLayout.IsSingle(w, size);
        var limit = TileLayout.LabelLimit(size, single);
        var titleLimit = single ? limit : (int)((TileLayout.Width(size.W) - 2 * TileLayout.CardPaddingX - 30) / TileLayout.CharWidth);
        if (w.Title.Length > titleLimit && !(single && w.Controls[0].Label is { Length: > 0 }))
            warnings.Add($"Title “{w.Title}” is longer than fits at {size} (about {titleLimit} characters) and will be cut off.");
        foreach (var c in w.Controls)
        {
            if (c.Type != ControlType.Choice && c.Label is { } label && label.Length > limit)
                warnings.Add($"Label “{label}” is longer than fits at {size} (about {limit} characters) and will be cut off.");
            if (c.Type != ControlType.Choice || c.Options is not { Count: > 0 } options) continue;
            var available = single
                ? size.W >= 3 ? TileLayout.Width(size.W) - 2 * TileLayout.CardPaddingX - 150 : TileLayout.Width(size.W) - 2 * TileLayout.CardPaddingX
                : TileLayout.Width(size.W) - 2 * TileLayout.CardPaddingX;
            if (TileLayout.ChoiceWidth(c) > available)
            {
                var perOption = Math.Max(2, (int)((available - 6) / options.Count - 20 - (options.Any(o => !string.IsNullOrWhiteSpace(o.Icon)) ? 22 : 0)) / (int)TileLayout.CharWidth);
                warnings.Add($"The options of “{c.Label ?? w.Title}” don't fit side by side at {size}: keep each label to about {perOption} characters, or use fewer options.");
            }
        }
    }

    private static void ValidateControl(ControlDefinition c, string path, List<string> errors, List<string> warnings)
    {
        if (!ControlSynonyms.TryGetValue(c.Type?.Trim() ?? "", out var type))
        {
            errors.Add($"{path}.type \"{c.Type}\" is not supported. Use one of: {string.Join(", ", ControlType.All)}.");
            return;
        }
        c.Type = type;

        switch (type)
        {
            case ControlType.Button:
                // icon-only buttons are fine (media controls)
                if (string.IsNullOrWhiteSpace(c.Icon)) RequireLabel(c, path, errors);
                ValidateAction(c.Action, $"{path}.action", errors, warnings, required: true, query: false);
                break;

            case ControlType.Toggle:
                RequireLabel(c, path, errors);
                if (c.On is null && c.Off is null && c.Action is not null)
                {
                    // single action receiving {value} = "true"/"false"
                    ValidateAction(c.Action, $"{path}.action", errors, warnings, required: true, query: false);
                }
                else
                {
                    ValidateAction(c.On, $"{path}.on", errors, warnings, required: true, query: false);
                    ValidateAction(c.Off, $"{path}.off", errors, warnings, required: true, query: false);
                }
                if (c.State is null) warnings.Add($"{path}: toggle has no \"state\", so it can't show whether it's on.");
                ValidateAction(c.State, $"{path}.state", errors, warnings, required: false, query: true);
                break;

            case ControlType.Choice:
                c.Options ??= [];
                // report everything at once so the model can fix all problems in one go
                if (c.Options.Count < 2) errors.Add($"{path}: a choice needs at least 2 options.");
                if (c.Options.Count > 6) errors.Add($"{path}: a choice can have at most 6 options.");
                for (var i = 0; i < c.Options.Count; i++)
                {
                    var o = c.Options[i];
                    if (string.IsNullOrWhiteSpace(o.Label)) errors.Add($"{path}.options[{i}].label is required.");
                    ValidateAction(o.Action, $"{path}.options[{i}].action", errors, warnings, required: true, query: false);
                }
                ValidateAction(c.State, $"{path}.state", errors, warnings, required: false, query: true);
                break;

            case ControlType.Slider:
                c.Min ??= 0;
                c.Max ??= 100;
                c.Step ??= 1;
                if (c.Min >= c.Max) errors.Add($"{path}: min must be less than max.");
                if (c.Step <= 0) c.Step = 1;
                ValidateAction(c.Action, $"{path}.action", errors, warnings, required: true, query: false);
                if (c.Action is not null && !c.Action.ToJsonString().Contains("{value}", StringComparison.OrdinalIgnoreCase))
                    errors.Add($"{path}.action must use \"{{value}}\" somewhere so the slider position is passed in.");
                ValidateAction(c.State, $"{path}.state", errors, warnings, required: false, query: true);
                break;

            case ControlType.Status:
                RequireLabel(c, path, errors);
                ValidateAction(c.State, $"{path}.state", errors, warnings, required: true, query: true);
                break;
        }
    }

    private static void RequireLabel(ControlDefinition c, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(c.Label)) errors.Add($"{path}.label is required.");
    }

    internal static void ValidateAction(JsonObject? action, string path, List<string> errors, List<string> warnings, bool required, bool query)
    {
        if (action is null)
        {
            if (required) errors.Add($"{path} is required.");
            return;
        }

        var typeNode = action["type"];
        var type = typeNode?.GetValueKind() == JsonValueKind.String ? typeNode.GetValue<string>() : null;
        var info = ActionCatalog.Find(type);
        if (info is null)
        {
            errors.Add($"{path}.type \"{type}\" is not a known action. Use a built-in action or shell.powershell.");
            return;
        }
        if (!string.Equals(type, info.Type, StringComparison.Ordinal)) action["type"] = info.Type; // canonicalize alias/casing

        if (query && !info.IsQuery)
            errors.Add($"{path}: \"{info.Type}\" doesn't return a value, so it can't be used as state. Use a get… action or shell.powershell that prints the value.");

        foreach (var p in info.Params)
        {
            var node = action[p.Name];
            if (node is null)
            {
                if (p.Required) errors.Add($"{path}: \"{info.Type}\" requires \"{p.Name}\".");
                continue;
            }
            var problem = CheckParam(p, node, path, errors, warnings);
            if (problem is not null) errors.Add($"{path}.{p.Name}: {problem}");
        }

        if (info.Type == "power.setPlan" && action["name"] is null && action["guid"] is null)
            errors.Add($"{path}: power.setPlan needs \"name\" or \"guid\".");
    }

    private static string? CheckParam(ParamInfo p, JsonNode node, string path, List<string> errors, List<string> warnings)
    {
        var kind = node.GetValueKind();
        var text = kind == JsonValueKind.String ? node.GetValue<string>() : null;
        // "{value}" (sliders, toggles) and data placeholders in layout widgets ("{item.pid}") are filled in when it runs
        var isPlaceholder = text?.Contains("{value}", StringComparison.OrdinalIgnoreCase) == true || Bindings.HasPlaceholders(text);

        switch (p.Type)
        {
            case ParamType.Int:
            case ParamType.Number:
                if (kind == JsonValueKind.Number || isPlaceholder) return null;
                if (text is not null && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return null;
                return "must be a number";

            case ParamType.Bool:
                if (kind is JsonValueKind.True or JsonValueKind.False || isPlaceholder) return null;
                if (text is not null && (bool.TryParse(text, out _) || text is "0" or "1")) return null;
                return "must be true or false";

            case ParamType.Enum:
                var value = kind switch
                {
                    JsonValueKind.String => text,
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => node.ToJsonString(),
                };
                if (isPlaceholder || p.Values is null) return null;
                var match = p.Values.FirstOrDefault(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase));
                return match is null ? $"must be one of {string.Join(", ", p.Values)}" : null;

            case ParamType.Actions:
                if (node is not JsonArray arr || arr.Count == 0) return "must be a non-empty array of actions";
                for (var i = 0; i < arr.Count; i++)
                {
                    if (arr[i] is JsonObject step) ValidateAction(step, $"{path}.{p.Name}[{i}]", errors, warnings, required: true, query: false);
                    else errors.Add($"{path}.{p.Name}[{i}] must be an action object");
                }
                return null;

            case ParamType.Object:
                return node is JsonObject ? null : "must be an object";

            default:
                if (kind is JsonValueKind.String or JsonValueKind.Number) return null;
                return "must be a string";
        }
    }
}
