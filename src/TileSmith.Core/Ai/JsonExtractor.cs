using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TileSmith.Core.Ai;

/// <summary>
/// Pulls JSON out of model output: fenced ```json blocks, &lt;tool_call&gt; tags, or the first balanced {…}/[…]
/// in otherwise chatty text. Also strips ANSI colour codes that CLIs like to print.
/// </summary>
public static partial class JsonExtractor
{
    public static string StripAnsi(string text) => AnsiRegex().Replace(text, "");

    public static IEnumerable<JsonNode> Candidates(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        var text = StripAnsi(raw);
        var seen = new HashSet<string>();

        foreach (Match m in FenceRegex().Matches(text))
        {
            var body = m.Groups[1].Value.Trim();
            if (seen.Add(body) && TryParse(body, out var node)) yield return node!;
        }

        foreach (Match m in ToolCallTagRegex().Matches(text))
        {
            var body = m.Groups[1].Value.Trim();
            if (seen.Add(body) && TryParse(body, out var node)) yield return node!;
        }

        foreach (var span in BalancedSpans(text))
        {
            if (seen.Add(span) && TryParse(span, out var node)) yield return node!;
        }
    }

    public static bool TryParse(string text, out JsonNode? node)
    {
        node = null;
        try
        {
            node = JsonNode.Parse(text, documentOptions: JsonDefaults.DocumentOptions);
            return node is JsonObject or JsonArray;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Top-level balanced {...} or [...] spans, string-aware.</summary>
    private static IEnumerable<string> BalancedSpans(string text)
    {
        for (var start = 0; start < text.Length; start++)
        {
            var open = text[start];
            if (open is not ('{' or '[')) continue;
            var end = FindClose(text, start);
            if (end < 0) continue;
            yield return text[start..(end + 1)];
            start = end;
        }
    }

    private static int FindClose(string text, int start)
    {
        var stack = new Stack<char>();
        var inString = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' or ']':
                    if (stack.Count == 0 || stack.Pop() != c) return -1;
                    if (stack.Count == 0) return i;
                    break;
            }
        }
        return -1;
    }

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex AnsiRegex();

    [GeneratedRegex(@"```(?:json|JSON|jsonc)?[ \t]*\r?\n(.*?)\r?\n?```", RegexOptions.Singleline)]
    private static partial Regex FenceRegex();

    [GeneratedRegex(@"<tool_call>(.*?)</tool_call>", RegexOptions.Singleline)]
    private static partial Regex ToolCallTagRegex();
}
