using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TileSmith.Core.Actions;

public sealed class ActionException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// One invocation of an action: its JSON parameters plus the control's current value,
/// which replaces "{value}" anywhere in string parameters.
/// </summary>
public sealed class ActionCall
{
    public ActionCall(JsonObject raw, string? value)
    {
        Raw = raw;
        Value = value;
        var type = raw["type"]?.GetValueKind() == JsonValueKind.String ? raw["type"]!.GetValue<string>() : null;
        Info = ActionCatalog.Find(type) ?? throw new ActionException($"Unknown action \"{type}\".");
    }

    public JsonObject Raw { get; }
    public string? Value { get; }

    /// <summary>Run as a state/data query (read-only): scripts may use the warm PowerShell host.</summary>
    public bool ReadOnly { get; init; }
    public ActionInfo Info { get; }
    public string Type => Info.Type;

    public string? GetString(string name)
    {
        var node = Raw[name];
        if (node is null) return null;
        string? text = node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.Number => node.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => node.ToJsonString(),
        };
        return text is null ? null : Substitute(text, Value);
    }

    public string Require(string name) =>
        GetString(name) is { Length: > 0 } s ? s : throw new ActionException($"{Type}: \"{name}\" is required.");

    public double? GetNumber(string name)
    {
        var s = GetString(name);
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (double.TryParse(s.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        throw new ActionException($"{Type}: \"{name}\" must be a number (got \"{s}\").");
    }

    public int? GetInt(string name) => GetNumber(name) is { } d ? (int)Math.Round(d) : null;

    public int RequireInt(string name) => GetInt(name) ?? throw new ActionException($"{Type}: \"{name}\" is required.");

    public bool? GetBool(string name)
    {
        var s = GetString(name);
        return s is null ? null : ValueText.IsTruthy(s);
    }

    public IReadOnlyList<ActionCall> GetActions(string name) =>
        Raw[name] is JsonArray arr
            ? arr.OfType<JsonObject>().Select(o => new ActionCall(o, Value)).ToList()
            : [];

    public IReadOnlyDictionary<string, string> GetMap(string name)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Raw[name] is JsonObject obj)
        {
            foreach (var (key, node) in obj)
            {
                if (node is null) continue;
                result[key] = Substitute(node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString(), Value);
            }
        }
        return result;
    }

    public static string Substitute(string text, string? value) =>
        value is null ? text : text.Replace("{value}", value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Helpers for comparing state values produced by queries.</summary>
public static class ValueText
{
    private static readonly HashSet<string> Truthy = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "1", "on", "yes", "enabled", "enable", "active", "running", "connected", "muted",
    };

    public static bool IsTruthy(string? value) => value is not null && Truthy.Contains(value.Trim());

    /// <summary>"2560 × 1440" == "2560x1440" == "2560X1440".</summary>
    public static string Normalize(string? value)
    {
        if (value is null) return "";
        var chars = value.Trim().ToLowerInvariant().Replace('×', 'x').Where(c => !char.IsWhiteSpace(c) && c != '"' && c != '\'');
        return new string(chars.ToArray());
    }

    public static bool Matches(string? a, string? b)
    {
        var na = Normalize(a);
        var nb = Normalize(b);
        if (na == nb) return true;
        // numeric comparison: "60" == "60.0"
        return double.TryParse(na, NumberStyles.Float, CultureInfo.InvariantCulture, out var da)
               && double.TryParse(nb, NumberStyles.Float, CultureInfo.InvariantCulture, out var db)
               && Math.Abs(da - db) < 0.0001;
    }

    public static string FormatNumber(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
