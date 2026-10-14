using System.Text;
using System.Text.Json.Nodes;
using TileSmith.Core.Actions;
using TileSmith.Core.Widgets;

namespace TileSmith.Core.Ai;

public sealed record VerifyResult(IReadOnlyList<string> Problems, IReadOnlyList<string> Notes, string Preview);

/// <summary>
/// Runs a widget's read-only queries before it goes on the panel, the way the panel will, and checks that what they
/// print fits how the widget reads it: a toggle that can tell on from off, a slider that gets a number, a choice whose
/// options match, layout placeholders that point at real fields. Most "the widget doesn't work" reports are one of these.
/// </summary>
public static class WidgetVerifier
{
    private static readonly HashSet<string> Falsy = new(StringComparer.OrdinalIgnoreCase)
    {
        "false", "0", "off", "no", "disabled", "disable", "inactive", "stopped", "disconnected", "unmuted", "none", "",
    };

    public static TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public static async Task<VerifyResult> VerifyAsync(WidgetDefinition widget, ActionEngine actions, CancellationToken ct)
    {
        var problems = new List<string>();
        var notes = new List<string>();
        var preview = new StringBuilder();

        // every query the widget will run, started together
        var queries = new List<(string Path, JsonObject Action)>();
        for (var i = 0; i < widget.Controls.Count; i++)
        {
            if (widget.Controls[i].State is { } state) queries.Add(($"controls[{i}].state", state));
        }
        var sources = WidgetLayout.DataSources(widget);
        foreach (var s in sources) queries.Add(($"data.{s.Name}", s.Action));
        if (widget.Layout is not null)
        {
            var n = 0;
            foreach (var el in WidgetLayout.Walk(widget.Layout))
            {
                if (el["state"] is JsonObject st) queries.Add(($"layout element {n} ({WidgetLayout.TypeOf(el)}).state", st));
                n++;
            }
        }
        if (queries.Count == 0) return new VerifyResult(problems, notes, "");

        var results = await Task.WhenAll(queries.Select(q => RunAsync(actions, q.Action, ct)));
        var byAction = new Dictionary<JsonObject, (string? Output, string? Error)>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < queries.Count; i++)
        {
            byAction[queries[i].Action] = results[i];
            if (results[i].Error is { } error)
                problems.Add($"{queries[i].Path} ({queries[i].Action["type"]}) failed when TileSmith ran it: {error}");
        }

        foreach (var c in widget.Controls) CheckControl(c, c.State is null ? null : byAction[c.State].Output, problems, notes, preview);

        if (widget.Layout is not null)
        {
            var data = new BindingData();
            foreach (var s in sources)
            {
                if (byAction[s.Action].Output is { } output) data.Set(s.Name, output);
            }
            foreach (var el in WidgetLayout.Walk(widget.Layout))
            {
                var type = WidgetLayout.TypeOf(el);
                if (el["state"] is JsonObject st && type is "toggle" or "slider" or "choice")
                    CheckControl(ToControl(el, type), byAction[st].Output, problems, notes, preview);
                foreach (var key in new[] { "text", "icon", "src", "value", "label", "visible" })
                {
                    foreach (var template in WidgetLayout.Branches(el[key]))
                    {
                        foreach (var path in Bindings.PathsIn(template))
                        {
                            var (name, _) = Bindings.SplitPath(path);
                            if (!data.Has(name) || data.Resolve(path) is not null) continue;
                            var raw = data.Resolve(name) ?? "";
                            problems.Add($"\"{{{path}}}\" ({type}.{key}) shows nothing: data \"{name}\" printed {Quote(raw)}, which has no \"{path[name.Length..].TrimStart('.')}\". " +
                                         "Make the query print JSON with that field, or read the field it does have.");
                        }
                    }
                }
                if (type is "text" && WidgetLayout.Prop(el, "text", data) is { } text && Bindings.HasPlaceholders(text) && preview.Length < 600)
                    preview.AppendLine($"- text: {Quote(Bindings.Render(text, data))}");
                if (type is "progress" or "ring" && WidgetLayout.Prop(el, "value", data) is { } value)
                {
                    var number = Bindings.Number(value, data);
                    if (number is null && data.Resolve(Bindings.SourcesIn(value).FirstOrDefault() ?? "") is not null)
                        problems.Add($"{type}.value {Quote(value)} isn't a number (got {Quote(Bindings.Value(value, data) ?? "")}). Print just the number.");
                    else if (number is { } d)
                    {
                        var min = WidgetLayout.Num(el["min"]) ?? 0;
                        var max = WidgetLayout.Num(el["max"]) ?? 100;
                        if (d < min || d > max) notes.Add($"{type} value {d} is outside {min}–{max}; set min/max to the value's real range.");
                        preview.AppendLine($"- {type}: {d} of {min}–{max}");
                    }
                }
            }
        }
        return new VerifyResult(problems, notes, preview.ToString().TrimEnd());
    }

    private static ControlDefinition ToControl(JsonObject el, string? type) => new()
    {
        Type = type ?? "",
        Label = WidgetLayout.Text(el["label"]),
        OnValue = WidgetLayout.Text(el["onValue"]),
        Min = WidgetLayout.Num(el["min"]),
        Max = WidgetLayout.Num(el["max"]),
        Options = (el["options"] as JsonArray)?.OfType<JsonObject>()
            .Select(o => new OptionDefinition { Label = WidgetLayout.Text(o["label"]) ?? "", Value = WidgetLayout.Text(o["value"]) }).ToList(),
    };

    private static void CheckControl(ControlDefinition c, string? output, List<string> problems, List<string> notes, StringBuilder preview)
    {
        if (output is null) return;
        var name = $"{c.Type} “{c.Label ?? ""}”";
        switch (c.Type)
        {
            case ControlType.Toggle:
                if (c.OnValue is { Length: > 0 } onValue)
                {
                    var on = ValueText.Matches(onValue, output);
                    preview.AppendLine($"- {name}: state printed {Quote(output)} → shows {(on ? "ON" : "OFF")}");
                    if (!on && output.Contains('\n'))
                        problems.Add($"{name}: state printed several lines ({Quote(output)}); it must print one value to compare with onValue \"{onValue}\".");
                }
                else if (ValueText.IsTruthy(output) || Falsy.Contains(output.Trim()))
                {
                    preview.AppendLine($"- {name}: state printed {Quote(output)} → shows {(ValueText.IsTruthy(output) ? "ON" : "OFF")}");
                }
                else
                {
                    problems.Add($"{name}: state printed {Quote(output)}, which isn't on/off, so the toggle would always look off. " +
                                 "Set \"onValue\" to the exact value that means on, or make the query print true/false.");
                }
                break;

            case ControlType.Slider:
                if (!Bindings.TryNumber(output, out var d))
                    problems.Add($"{name}: state printed {Quote(output)}, but a slider needs a number. Print only the number.");
                else
                {
                    preview.AppendLine($"- {name}: state {d}");
                    if (d < (c.Min ?? 0) || d > (c.Max ?? 100)) notes.Add($"{name}: state {d} is outside min/max {c.Min ?? 0}–{c.Max ?? 100}.");
                }
                break;

            case ControlType.Choice:
                var options = c.Options ?? [];
                var match = options.FindIndex(o => ValueText.Matches(o.EffectiveValue, output));
                if (match >= 0)
                {
                    preview.AppendLine($"- {name}: state printed {Quote(output)} → “{options[match].Label}” is selected");
                    break;
                }
                var message = $"{name}: state printed {Quote(output)}, which matches none of the option values ({string.Join(", ", options.Select(o => Quote(o.EffectiveValue)))}), so nothing is highlighted.";
                // a value of the same shape (another resolution, another number) is simply a setting the choice doesn't offer
                if (options.Any(o => Shape(o.EffectiveValue) == Shape(output))) notes.Add(message + " That's fine if the current setting really is none of them.");
                else problems.Add(message + " Make each option's \"value\" exactly what the state query prints for it.");
                break;

            case ControlType.Status:
                preview.AppendLine($"- {name}: shows {Quote(output)}");
                if (output.Length == 0) notes.Add($"{name}: the state query printed nothing, so it shows “—”.");
                else if (output.Contains('\n')) notes.Add($"{name}: the state printed several lines; only a short single value reads well on a tile.");
                break;
        }
    }

    /// <summary>"2560x1440" → "9x9", "Balanced" → "a", "High performance" → "a a".</summary>
    private static string Shape(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in ValueText.Normalize(text).Length == 0 ? "" : text.Trim().ToLowerInvariant())
        {
            var k = char.IsDigit(ch) ? '9' : char.IsLetter(ch) ? 'a' : char.IsWhiteSpace(ch) ? ' ' : ch;
            if (sb.Length == 0 || sb[^1] != k) sb.Append(k);
        }
        return sb.ToString();
    }

    private static async Task<(string? Output, string? Error)> RunAsync(ActionEngine actions, JsonObject action, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueryTimeout);
        try
        {
            return (await actions.RunAsync(action, null, timeout.Token) ?? "", null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"it didn't answer within {QueryTimeout.TotalSeconds:0} seconds (queries must be quick)");
        }
        catch (ActionException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (null, ex.Message);
        }
    }

    private static string Quote(string text)
    {
        text = text.Replace("\r", "").Replace("\n", "⏎");
        return "\"" + (text.Length > 120 ? text[..120] + "…" : text) + "\"";
    }
}
