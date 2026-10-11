using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace TileSmith.Core.Widgets;

/// <summary>A widget's footprint on the panel grid, in cells (Android launcher style). Width 4 = full width.</summary>
public readonly record struct CellSize(int W, int H)
{
    public override string ToString() => $"{W}x{H}";
    public bool IsFullWidth => W >= 4;
}

public static class WidgetSize
{
    public const int MaxWidth = 4;
    public const int MaxHeight = 3;

    /// <summary>"2x1", "2×1", "2 x 1", plus the older names small / medium / wide.</summary>
    public static bool TryParse(string? text, out CellSize size)
    {
        size = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().ToLowerInvariant().Replace('×', 'x').Replace(" ", "");
        switch (t)
        {
            case "small" or "tile" or "s": size = new(2, 1); return true;
            case "icon" or "tiny" or "xs": size = new(1, 1); return true;
            case "medium" or "square" or "m": size = new(2, 2); return true;
            case "wide" or "bar" or "full" or "l": size = new(4, 1); return true;
            case "large" or "big" or "xl": size = new(4, 2); return true;
        }
        var parts = t.Split('x');
        if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w >= 1 && h >= 1)
        {
            size = new(Math.Min(w, MaxWidth), Math.Min(h, MaxHeight));
            return true;
        }
        return false;
    }

    public static CellSize Parse(string? text, WidgetDefinition widget) =>
        TryParse(text, out var size) ? size : Default(widget);

    /// <summary>The smallest footprint the widget's content fits in (see <see cref="TileLayout"/>).</summary>
    public static CellSize Minimum(WidgetDefinition widget) => TileLayout.Minimum(widget);

    /// <summary>What a widget gets when nobody picked a size.</summary>
    public static CellSize Default(WidgetDefinition widget)
    {
        var preferred = Preferred(widget);
        var minimum = Minimum(widget);
        return new(Math.Max(preferred.W, minimum.W), Math.Max(preferred.H, minimum.H));
    }

    private static CellSize Preferred(WidgetDefinition widget)
    {
        var controls = widget.Controls;
        if (controls.Count == 1)
        {
            var c = controls[0];
            return c.Type switch
            {
                ControlType.Slider => new(4, 1),
                ControlType.Choice => (c.Options?.Count ?? 0) <= 4 ? new(4, 1) : new(4, 2),
                _ => new(2, 1),
            };
        }
        return new(4, controls.Count >= 4 ? 3 : 2);
    }
}

public static class ControlType
{
    public const string Button = "button";
    public const string Toggle = "toggle";
    public const string Choice = "choice";
    public const string Slider = "slider";
    public const string Status = "status";

    public static readonly string[] All = [Button, Toggle, Choice, Slider, Status];
}

/// <summary>
/// A tile on the panel. This is the JSON document the AI writes and the user can edit by hand.
/// Actions are kept as raw JSON objects: {"type": "display.setResolution", "width": 2560, ...}.
/// </summary>
public sealed class WidgetDefinition
{
    public int Schema { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Subtitle { get; set; }
    public string? Icon { get; set; }
    /// <summary>Footprint in grid cells, "WxH": 1x1, 2x1, 2x2, 4x1, 4x2 (4 = full width).</summary>
    public string Size { get; set; } = "";

    [JsonIgnore]
    public CellSize Cells => WidgetSize.Parse(Size, this);
    public int? RefreshSeconds { get; set; }
    public List<ControlDefinition> Controls { get; set; } = [];

    /// <summary>The request that produced this widget (kept so "edit with AI" has context).</summary>
    public string? Prompt { get; set; }

    public WidgetDefinition Clone() => FromJson(ToJson());

    public string ToJson() => JsonSerializer.Serialize(this, JsonDefaults.Options);

    public static WidgetDefinition FromJson(string json) =>
        JsonSerializer.Deserialize<WidgetDefinition>(json, JsonDefaults.Options)
        ?? throw new JsonException("Widget JSON was empty.");

    public static string NewId() => "w_" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>All actions referenced by this widget, including state queries and nested sequence steps.</summary>
    public IEnumerable<JsonObject> AllActions()
    {
        foreach (var c in Controls)
        {
            foreach (var a in c.OwnActions())
            {
                foreach (var nested in Flatten(a)) yield return nested;
            }
        }
    }

    private static IEnumerable<JsonObject> Flatten(JsonObject action)
    {
        yield return action;
        if (action["steps"] is JsonArray steps)
        {
            foreach (var step in steps.OfType<JsonObject>())
            {
                foreach (var nested in Flatten(step)) yield return nested;
            }
        }
    }
}

public sealed class ControlDefinition
{
    public string Type { get; set; } = ControlType.Button;
    public string? Label { get; set; }
    public string? Icon { get; set; }

    /// <summary>"accent" renders the button in the accent color.</summary>
    public string? Style { get; set; }

    /// <summary>button / slider: the action to run. slider replaces {value}.</summary>
    public JsonObject? Action { get; set; }

    /// <summary>toggle: actions for switching on and off.</summary>
    public JsonObject? On { get; set; }
    public JsonObject? Off { get; set; }

    /// <summary>toggle / choice / slider / status: query action whose output is the current state.</summary>
    public JsonObject? State { get; set; }

    /// <summary>toggle: the state value meaning "on" (default: any truthy value).</summary>
    public string? OnValue { get; set; }

    /// <summary>toggle: secondary text under the label when on / off (default "On" / "Off").</summary>
    public string? OnText { get; set; }
    public string? OffText { get; set; }

    public List<OptionDefinition>? Options { get; set; }

    public double? Min { get; set; }
    public double? Max { get; set; }
    public double? Step { get; set; }

    /// <summary>slider / status display format, e.g. "{value}%".</summary>
    public string? Format { get; set; }

    /// <summary>button: if set, ask the user to confirm with this text before running.</summary>
    public string? Confirm { get; set; }

    public IEnumerable<JsonObject> OwnActions()
    {
        if (Action is not null) yield return Action;
        if (On is not null) yield return On;
        if (Off is not null) yield return Off;
        if (State is not null) yield return State;
        if (Options is not null)
        {
            foreach (var o in Options)
            {
                if (o.Action is not null) yield return o.Action;
            }
        }
    }
}

public sealed class OptionDefinition
{
    public string Label { get; set; } = "";
    public string? Value { get; set; }
    public string? Icon { get; set; }
    public JsonObject? Action { get; set; }

    [JsonIgnore]
    public string EffectiveValue => string.IsNullOrWhiteSpace(Value) ? Label : Value!;
}
