using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TileSmith.Core.Tiles;

/// <summary>Small JSON helpers for reading other people's data: lenient parsing and "a.b[0].c" paths.</summary>
public static class JsonPath
{
    /// <summary>Parses text that starts like JSON (an object or an array), with comments and trailing commas allowed.</summary>
    public static bool TryParse(string? text, out JsonNode? node)
    {
        node = null;
        var t = text?.TrimStart() ?? "";
        if (t.Length == 0 || (t[0] != '{' && t[0] != '[')) return false;
        try
        {
            node = JsonNode.Parse(t, documentOptions: JsonDefaults.DocumentOptions);
            return node is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>"a.b[0].c", "a.b.0.c", "[0].name" or "items[-1]" (the last) from <paramref name="node"/>; null when it isn't there.</summary>
    public static JsonNode? Navigate(JsonNode? node, string? path)
    {
        if (node is null) return null;
        if (string.IsNullOrWhiteSpace(path)) return node;
        var sb = new StringBuilder();
        var segments = new List<string>();
        foreach (var c in path.Trim().TrimStart('$'))
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
        foreach (var raw in segments)
        {
            var segment = raw.Trim('"', '\'');
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

    /// <summary>A node as text: strings as they are, numbers and booleans as written, the rest as JSON.</summary>
    public static string? Text(JsonNode? node) => node?.GetValueKind() switch
    {
        null => null,
        JsonValueKind.String => node.GetValue<string>(),
        JsonValueKind.Number => node.ToJsonString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => null,
        _ => node.ToJsonString(),
    };

    public static double? Number(JsonNode? node)
    {
        var t = Text(node)?.Trim().TrimEnd('%');
        return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? d : null;
    }
}
