using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TileSmith.Core.Actions;

namespace TileSmith.Core.Widgets;

/// <summary>
/// The values a layout widget shows: the latest output of each of its "data" queries, by name. Outputs that are JSON
/// can be read field by field ("{gpu.temp}", "{disks[0].free}"), so one script can feed a whole card.
/// </summary>
public sealed class BindingData
{
    private readonly Dictionary<string, string> _raw = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, JsonNode?> _parsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<double>> _history = new(StringComparer.OrdinalIgnoreCase);

    public const int HistoryLength = 60;

    public void Set(string name, string? value)
    {
        value = value?.Trim() ?? "";
        _raw[name] = value;
        _parsed.Remove(name);
    }

    public bool Has(string name) => _raw.ContainsKey(name);

    /// <summary>Recent numbers recorded with <see cref="Track"/> (sparklines), oldest first.</summary>
    public IReadOnlyList<double> History(string path) => _history.TryGetValue(path, out var list) ? list : [];

    /// <summary>Records a number for a sparkline.</summary>
    public void Track(string path, double value)
    {
        if (!_history.TryGetValue(path, out var list)) _history[path] = list = [];
        list.Add(value);
        if (list.Count > HistoryLength) list.RemoveAt(0);
    }

    /// <summary>"name", "name.field", "name.list[0].field" → text, or null when unknown (not loaded yet / no such field).</summary>
    public string? Resolve(string path)
    {
        var (name, rest) = Bindings.SplitPath(path);
        if (!_raw.TryGetValue(name, out var raw)) return null;
        if (rest.Length == 0) return raw;
        if (!_parsed.TryGetValue(name, out var root))
        {
            JsonExtractorLite.TryParse(raw, out root);
            _parsed[name] = root;
        }
        var node = Bindings.Navigate(root, rest);
        if (node is null) return null;
        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.Number => node.GetValue<JsonElement>().GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "",
            _ => node.ToJsonString(),
        };
    }
}

internal static class JsonExtractorLite
{
    public static bool TryParse(string text, out JsonNode? node)
    {
        node = null;
        var t = text.TrimStart();
        if (t.Length == 0 || (t[0] != '{' && t[0] != '[')) return false;
        try
        {
            node = JsonNode.Parse(text, documentOptions: JsonDefaults.DocumentOptions);
            return node is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>
/// Templates and conditions in layout widgets: "{cpu}%", "{mem.used|bytes}", "{vpn} == on", "{cpu} > 80 &amp;&amp; {fan}".
/// Small on purpose: the model can't run code in the panel, only point at values and format them.
/// </summary>
public static partial class Bindings
{
    public const string Missing = "—";

    /// <summary>Names of the data sources a template uses ("{gpu.temp|round}" → "gpu").</summary>
    public static IEnumerable<string> SourcesIn(string? template)
    {
        if (string.IsNullOrEmpty(template)) yield break;
        foreach (Match m in PlaceholderRegex().Matches(template))
        {
            var (name, _) = SplitPath(m.Groups[1].Value.Split('|')[0].Trim());
            if (name.Length > 0) yield return name;
        }
    }

    /// <summary>Paths a template reads ("{gpu.temp|round}" → "gpu.temp").</summary>
    public static IEnumerable<string> PathsIn(string? template)
    {
        if (string.IsNullOrEmpty(template)) yield break;
        foreach (Match m in PlaceholderRegex().Matches(template)) yield return m.Groups[1].Value.Split('|')[0].Trim();
    }

    public static bool HasPlaceholders(string? text) => !string.IsNullOrEmpty(text) && PlaceholderRegex().IsMatch(text);

    /// <summary>Fills a template. Unknown values become "—".</summary>
    public static string Render(string? template, BindingData data)
    {
        if (string.IsNullOrEmpty(template)) return "";
        return PlaceholderRegex().Replace(template, m =>
        {
            var parts = m.Groups[1].Value.Split('|');
            var value = data.Resolve(parts[0].Trim());
            for (var i = 1; i < parts.Length; i++) value = ApplyFilter(value, parts[i].Trim());
            return value ?? Missing;
        });
    }

    /// <summary>A template that is exactly one placeholder resolves to its raw value (numbers for progress bars).</summary>
    public static string? Value(string? template, BindingData data)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;
        var t = template.Trim();
        var single = PlaceholderRegex().Match(t);
        if (single.Success && single.Length == t.Length)
        {
            var parts = single.Groups[1].Value.Split('|');
            var value = data.Resolve(parts[0].Trim());
            for (var i = 1; i < parts.Length; i++) value = ApplyFilter(value, parts[i].Trim());
            return value;
        }
        return Render(t, data);
    }

    public static double? Number(string? template, BindingData data) =>
        TryNumber(Value(template, data), out var d) ? d : null;

    /// <summary>
    /// "{x}" (truthy), "!{x}", "{a} == on", "{a} != off", "{n} &gt; 80", "&lt;", "&gt;=", "&lt;=", "{s} contains text",
    /// joined with &amp;&amp; or ||. Unknown values make a comparison false.
    /// </summary>
    public static bool Test(string? condition, BindingData data)
    {
        if (string.IsNullOrWhiteSpace(condition)) return true;
        foreach (var any in condition.Split("||"))
        {
            if (any.Split("&&").All(all => TestOne(all.Trim(), data))) return true;
        }
        return false;
    }

    private static bool TestOne(string clause, BindingData data)
    {
        if (clause.Length == 0) return true;
        var m = ComparisonRegex().Match(clause);
        if (!m.Success)
        {
            var negate = clause.StartsWith('!');
            var value = Value(negate ? clause[1..].Trim() : clause, data);
            var truthy = IsSet(value);
            return negate ? !truthy : truthy;
        }
        var left = Value(m.Groups[1].Value.Trim(), data);
        var rightText = m.Groups[3].Value.Trim();
        var right = rightText is "''" or "\"\"" ? "" : Value(rightText.Trim('"', '\''), data);
        if (left is null || right is null) return false;
        var op = m.Groups[2].Value.ToLowerInvariant();
        if (op == "contains") return left.Contains(right, StringComparison.OrdinalIgnoreCase);
        var numeric = TryNumber(left, out var l) & TryNumber(right, out var r);
        return op switch
        {
            "==" => ValueText.Matches(left, right),
            "!=" => !ValueText.Matches(left, right),
            ">" => numeric && l > r,
            "<" => numeric && l < r,
            ">=" => numeric && l >= r,
            "<=" => numeric && l <= r,
            _ => false,
        };
    }

    private static readonly HashSet<string> Unset = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "false", "0", "off", "no", "none", "null", "disabled", "inactive", "stopped", "disconnected", Missing,
    };

    /// <summary>A bare condition "{x}" holds when x has a value that doesn't mean no/off.</summary>
    public static bool IsSet(string? value) => value is not null && !Unset.Contains(value.Trim());

    public static string? ApplyFilter(string? value, string filter)
    {
        var colon = filter.IndexOf(':');
        var name = (colon < 0 ? filter : filter[..colon]).Trim().ToLowerInvariant();
        var arg = colon < 0 ? null : filter[(colon + 1)..].Trim();
        if (name == "default") return string.IsNullOrEmpty(value) ? arg ?? "" : value;
        if (value is null) return null;
        switch (name)
        {
            case "upper": return value.ToUpperInvariant();
            case "lower": return value.ToLowerInvariant();
            case "trim": return value.Trim();
            case "first": return value.Split('\n')[0].Trim();
            case "round":
            {
                if (!TryNumber(value, out var d)) return value;
                var digits = int.TryParse(arg, out var n) ? Math.Clamp(n, 0, 6) : 0;
                return Math.Round(d, digits, MidpointRounding.AwayFromZero).ToString("F" + digits, CultureInfo.InvariantCulture);
            }
            case "percent":
                // 0.42 → 42
                return TryNumber(value, out var p) ? Math.Round(p * 100).ToString(CultureInfo.InvariantCulture) : value;
            case "bytes":
                return TryNumber(value, out var b) ? FormatBytes(b) : value;
            case "duration":
                // seconds → "2d 4h", "3h 12m", "4m 05s"
                return TryNumber(value, out var s) ? FormatDuration(s) : value;
            case "max":
                // shorten: "{title|max:24}"
                return int.TryParse(arg, out var max) && max > 1 && value.Length > max ? value[..(max - 1)].TrimEnd() + "…" : value;
            default:
                return value;
        }
    }

    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB"];
        var i = 0;
        var v = Math.Abs(bytes);
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        var text = v >= 100 || i == 0 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);
        return (bytes < 0 ? "-" : "") + text + " " + units[i];
    }

    public static string FormatDuration(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{t.Minutes}m {t.Seconds:00}s";
        return $"{t.Seconds}s";
    }

    public static bool TryNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim().TrimEnd('%').Trim();
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    internal static (string Name, string Tail) SplitPath(string path)
    {
        path = path.Trim();
        var end = 0;
        while (end < path.Length && path[end] != '.' && path[end] != '[') end++;
        return (path[..end], path[end..].TrimStart('.'));
    }

    /// <summary>"a.b[0].c" or "a.b.0.c" from <paramref name="node"/>.</summary>
    public static JsonNode? Navigate(JsonNode? node, string path)
    {
        if (node is null) return null;
        var sb = new StringBuilder();
        var segments = new List<string>();
        foreach (var c in path)
        {
            if (c is '.' or '[' or ']')
            {
                if (sb.Length > 0) segments.Add(sb.ToString());
                sb.Clear();
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) segments.Add(sb.ToString());
        foreach (var segment in segments)
        {
            node = node switch
            {
                JsonObject o => o.FirstOrDefault(kv => string.Equals(kv.Key, segment, StringComparison.OrdinalIgnoreCase)).Value,
                JsonArray a when int.TryParse(segment, out var i) => i >= 0 && i < a.Count ? a[i] : i < 0 && -i <= a.Count ? a[a.Count + i] : null,
                _ => null,
            };
            if (node is null) return null;
        }
        return node;
    }

    [GeneratedRegex(@"\{([A-Za-z_][A-Za-z0-9_]*(?:(?:\.[A-Za-z0-9_\-]+)|(?:\[-?\d+\]))*(?:\s*\|[^{}|]+)*)\}")]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"^(.+?)\s*(==|!=|>=|<=|>|<|\bcontains\b)\s*(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex ComparisonRegex();
}
