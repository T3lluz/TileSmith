using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TileSmith.Core.Actions;
using TileSmith.Core.Widgets;

namespace TileSmith.Core.Ai;

public enum StepState { Running, Done, Failed, Skipped }

/// <summary>One visible step of the agent's work, shown live in the panel.</summary>
public sealed class AgentStep
{
    public required string Tool { get; init; }
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public string? Output { get; set; }
    public StepState State { get; set; } = StepState.Running;
}

public enum AgentPermission
{
    /// <summary>Run everything; admin actions still show Windows' UAC prompt.</summary>
    Auto,

    /// <summary>Ask before commands, file writes and state-changing tests.</summary>
    AskForChanges,
}

public interface IAgentHost
{
    AgentPermission Permission { get; }
    IReadOnlyList<WidgetDefinition> GetWidgets();
    Task<string> SaveWidgetAsync(WidgetDefinition widget, CancellationToken ct);
    Task<string> RemoveWidgetAsync(string id, CancellationToken ct);
    Task<string> MoveWidgetAsync(string id, int position, CancellationToken ct);
    Task<string> ResizeWidgetAsync(string id, string size, CancellationToken ct);
    Task<bool> ApproveAsync(string title, string detail, CancellationToken ct);
    Task<string> AskUserAsync(string question, IReadOnlyList<string> options, CancellationToken ct);
    void OnStatus(string text);
    void OnSay(string text);
    void OnStepStarted(AgentStep step);
    void OnStepFinished(AgentStep step);
}

public sealed record AgentResult(IReadOnlyList<WidgetDefinition> SavedWidgets, string? Message, int Turns, TimeSpan Elapsed);

internal sealed record ToolCall(string Tool, JsonObject Args);

/// <summary>
/// The opencode-style loop: the model (on the server or a local subscription CLI) decides; TileSmith runs the
/// tools on this PC and feeds the results back until the widget is saved.
/// </summary>
public sealed class WidgetAgent(IAiBackend backend, ActionEngine actions, IAgentHost host, Func<string> deviceContext)
{
    public int MaxTurns { get; init; } = 14;

    /// <summary>
    /// The conversation so far (Widget Studio): when given, each run continues it instead of starting over, so the
    /// model remembers what was built and why. Filled in as the run goes.
    /// </summary>
    public List<AiMessage>? Conversation { get; init; }

    /// <summary>Extra system instructions for this run (e.g. that the user sees a live preview).</summary>
    public string? Context { get; init; }
    private const int MaxToolOutput = 6000;

    /// <summary>Run the widget's queries before saving and send problems back (see <see cref="WidgetVerifier"/>).</summary>
    public bool VerifyBeforeSave { get; init; } = true;

    // problems already shown to the model: if it saves the same thing again it knows better (e.g. the server is
    // offline right now), so the second time they don't block the save
    private readonly HashSet<string> _reported = [];

    public async Task<AgentResult> RunAsync(string request, string? sizePreference, WidgetDefinition? editing, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        _reported.Clear();
        host.OnStatus("Looking at your PC…");
        var messages = Conversation ?? [];
        if (messages.Count == 0)
        {
            var context = await Task.Run(deviceContext, ct);
            var system = AgentPrompt.System(context, host.GetWidgets(), request);
            if (!string.IsNullOrWhiteSpace(Context)) system += "\n\n# Where you are\n" + Context;
            messages.Add(AiMessage.System(system));
        }
        messages.Add(AiMessage.User(AgentPrompt.Task(request, sizePreference, editing)));

        var saved = new List<WidgetDefinition>();
        var formatErrors = 0;

        for (var turn = 1; turn <= MaxTurns; turn++)
        {
            ct.ThrowIfCancellationRequested();
            host.OnStatus(turn == 1 ? $"Asking {backend.DisplayName}…" : $"{backend.DisplayName} is thinking…");
            var reply = await backend.CompleteAsync(messages, ct);
            messages.Add(AiMessage.Assistant(Trim(reply, 12000)));

            var (say, calls) = Parse(reply);
            if (calls.Count == 0)
            {
                if (++formatErrors >= 3) throw new AiException("The model kept replying in the wrong format. Try again or pick another model.");
                messages.Add(AiMessage.User("""
                    That reply wasn't a tool call. Reply with exactly one ```json block like
                    {"say": "...", "tool": "save_widget", "args": {"widget": {...}}}
                    """));
                continue;
            }
            if (!string.IsNullOrWhiteSpace(say)) host.OnSay(say!);

            var results = new StringBuilder();
            var finished = false;
            string? finishMessage = null;
            for (var i = 0; i < calls.Count; i++)
            {
                var call = calls[i];
                var outcome = await ExecuteAsync(call, saved, ct);
                results.AppendLine($"[{i + 1}] {call.Tool}: {outcome.Text}");
                results.AppendLine();
                if (outcome.Ends)
                {
                    finished = true;
                    finishMessage = outcome.Message ?? say;
                    break;
                }
            }

            if (finished) return new AgentResult(saved, finishMessage, turn, sw.Elapsed);
            messages.Add(AiMessage.User("Tool results:\n" + Trim(results.ToString(), 20000)));
        }

        if (saved.Count > 0) return new AgentResult(saved, null, MaxTurns, sw.Elapsed);
        throw new AiException($"The agent didn't finish within {MaxTurns} steps.");
    }

    private sealed record Outcome(string Text, bool Ends = false, string? Message = null);

    private async Task<Outcome> ExecuteAsync(ToolCall call, List<WidgetDefinition> saved, CancellationToken ct)
    {
        var a = call.Args;
        switch (call.Tool)
        {
            case "powershell":
            {
                var script = Str(a, "script") ?? Str(a, "command") ?? "";
                if (script.Length == 0) return new Outcome("error: \"script\" is required");
                var admin = Bool(a, "admin");
                var step = Start(call.Tool, admin ? "Running PowerShell as administrator" : "Running PowerShell", script);
                if (!await ApproveIfNeeded(step, $"Run this PowerShell{(admin ? " as administrator" : "")}?", script, ct)) return Denied(step);
                var result = await PowerShellRunner.RunRawAsync(script, admin, Int(a, "timeoutSeconds") ?? 60, ct);
                var text = $"exit {result.ExitCode}\n--- stdout ---\n{result.Stdout.TrimEnd()}\n--- stderr ---\n{result.Stderr.TrimEnd()}";
                return Finish(step, Trim(text, MaxToolOutput), result.ExitCode == 0);
            }

            case "read_file":
            {
                var path = Expand(Str(a, "path"));
                var step = Start(call.Tool, $"Reading {Path.GetFileName(path)}", path);
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists) return Finish(step, $"error: file not found: {path}", false);
                    using var reader = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
                    var buffer = new char[64 * 1024];
                    var read = await reader.ReadBlockAsync(buffer, ct);
                    var text = new string(buffer, 0, read);
                    if (info.Length > 64 * 1024) text += "\n…(truncated)";
                    return Finish(step, text, true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Finish(step, "error: " + ex.Message, false);
                }
            }

            case "write_file":
            {
                var path = Expand(Str(a, "path"));
                var content = Str(a, "content") ?? "";
                var admin = Bool(a, "admin");
                var step = Start(call.Tool, $"Writing {Path.GetFileName(path)}", content);
                if (!await ApproveIfNeeded(step, $"Write {path}?", content, ct)) return Denied(step);
                try
                {
                    if (admin)
                    {
                        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
                        var ps = $"$p='{path.Replace("'", "''")}'; New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null; " +
                                 $"[IO.File]::WriteAllBytes($p, [Convert]::FromBase64String('{b64}'))";
                        var r = await PowerShellRunner.RunRawAsync(ps, true, 60, ct);
                        return Finish(step, r.ExitCode == 0 ? $"wrote {content.Length} chars to {path}" : "error: " + r.Stderr, r.ExitCode == 0);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
                    return Finish(step, $"wrote {content.Length} chars to {path}", true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return Finish(step, $"error: {ex.Message} (retry with admin=true if this needs administrator rights)", false);
                }
            }

            case "list_directory":
            {
                var path = Expand(Str(a, "path"));
                var step = Start(call.Tool, $"Looking in {path}", null);
                try
                {
                    var dir = new DirectoryInfo(path);
                    var entries = dir.EnumerateFileSystemInfos().Take(300)
                        .Select(e => e is DirectoryInfo ? $"[dir]  {e.Name}" : $"{((FileInfo)e).Length,10:N0}  {e.Name}");
                    return Finish(step, string.Join('\n', entries), true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return Finish(step, "error: " + ex.Message, false);
                }
            }

            case "test_action":
            {
                if (a["action"] is not JsonObject action) return new Outcome("error: \"action\" must be an action object");
                var value = Str(a, "value");
                ActionInfo? info = ActionCatalog.Find(action["type"]?.ToString());
                var step = Start(call.Tool, $"Testing {info?.Type ?? "action"}", action.ToJsonString());
                if (info is null) return Finish(step, "error: unknown action type", false);
                if (!info.IsQuery && !await ApproveIfNeeded(step, $"Run {info.Type} to test it?", action.ToJsonString(), ct)) return Denied(step);
                var errors = new List<string>();
                WidgetValidator.ValidateAction(action, "action", errors, [], required: true, query: false);
                if (errors.Count > 0) return Finish(step, "invalid action: " + string.Join("; ", errors), false);
                try
                {
                    var output = info.IsQuery && value is null ? await actions.QueryAsync(action, ct) : await actions.RunAsync(action, value, ct);
                    return Finish(step, "ok" + (string.IsNullOrEmpty(output) ? "" : ", output: " + Trim(output, MaxToolOutput)), true);
                }
                catch (ActionException ex)
                {
                    return Finish(step, "error: " + ex.Message, false);
                }
            }

            case "ask_user":
            {
                var question = Str(a, "question") ?? "Which do you prefer?";
                var options = a["options"] is JsonArray arr ? arr.Select(o => o?.ToString() ?? "").Where(o => o.Length > 0).Take(4).ToList() : [];
                var step = Start(call.Tool, question, null);
                var answer = await host.AskUserAsync(question, options, ct);
                return Finish(step, $"the user answered: {answer}", true);
            }

            case "save_widget":
            {
                var node = a["widget"] as JsonObject ?? (a.ContainsKey("controls") ? a : null);
                if (node is null) return new Outcome("error: \"widget\" object is required");
                WidgetDefinition widget;
                try
                {
                    widget = node.Deserialize<WidgetDefinition>(JsonDefaults.Options) ?? throw new JsonException("empty");
                }
                catch (JsonException ex)
                {
                    return new Outcome($"error: the widget JSON doesn't match the schema ({ex.Message})");
                }
                var validation = WidgetValidator.Validate(widget);
                if (!validation.IsValid)
                {
                    var invalid = Start(call.Tool, $"Checking “{widget.Title}”", null);
                    return Finish(invalid, "not saved, fix these problems:\n- " + string.Join("\n- ", validation.Errors), false);
                }

                var notes = new List<string>(validation.Warnings);
                var preview = "";
                if (VerifyBeforeSave && widget.AllActions().Any())
                {
                    var check = Start("verify", $"Trying “{widget.Title}” on your PC", null);
                    var verified = await WidgetVerifier.VerifyAsync(validation.Widget, actions, ct);
                    preview = verified.Preview;
                    var fresh = verified.Problems.Where(p => _reported.Add(p)).ToList();
                    if (fresh.Count > 0)
                    {
                        var text = "not saved — TileSmith ran the widget's queries and found problems:\n- " + string.Join("\n- ", fresh) +
                                   (preview.Length > 0 ? "\nWhat the tile would show now:\n" + preview : "") +
                                   "\nFix them and save again. (If a problem is only temporary — e.g. the server is offline right now — save the same widget again and it will be accepted.)";
                        return Finish(check, text, false);
                    }
                    notes.AddRange(verified.Notes);
                    notes.AddRange(verified.Problems.Select(p => "Still: " + p));
                    Finish(check, preview.Length > 0 ? preview : "ok", true);
                }

                var step = Start(call.Tool, $"Adding “{widget.Title}” to the panel", null);
                var status = await host.SaveWidgetAsync(validation.Widget, ct);
                saved.Add(validation.Widget);
                var warnings = notes.Count > 0 ? " Notes: " + string.Join(" ", notes) : "";
                var done = a["done"] is null || Bool(a, "done");
                return Finish(step, $"{status} (id {validation.Widget.Id}).{warnings}" + (preview.Length > 0 ? "\nIt shows:\n" + preview : ""), true, ends: done);
            }

            case "remove_widget":
            {
                var step = Start(call.Tool, "Removing a widget", Str(a, "id"));
                return Finish(step, await host.RemoveWidgetAsync(Str(a, "id") ?? "", ct), true);
            }

            case "move_widget":
            {
                var step = Start(call.Tool, "Moving a widget", Str(a, "id"));
                return Finish(step, await host.MoveWidgetAsync(Str(a, "id") ?? "", Int(a, "position") ?? 0, ct), true);
            }

            case "resize_widget":
            {
                var step = Start(call.Tool, "Resizing a widget", Str(a, "id"));
                return Finish(step, await host.ResizeWidgetAsync(Str(a, "id") ?? "", Str(a, "size") ?? "4x1", ct), true);
            }

            case "list_widgets":
            {
                var list = host.GetWidgets().Select((w, i) => $"{i}. {w.Title} — id {w.Id}, {w.Size}");
                return new Outcome(string.Join('\n', list));
            }

            case "get_widget":
            {
                var w = host.GetWidgets().FirstOrDefault(x => x.Id == Str(a, "id"));
                return new Outcome(w?.ToJson() ?? "error: no widget with that id");
            }

            case "finish":
                return new Outcome("done", Ends: true, Message: Str(a, "message"));

            default:
                return new Outcome($"error: unknown tool \"{call.Tool}\". Available: powershell, read_file, write_file, list_directory, test_action, ask_user, save_widget, remove_widget, move_widget, resize_widget, finish.");
        }
    }

    private AgentStep Start(string tool, string title, string? detail)
    {
        var step = new AgentStep { Tool = tool, Title = title, Detail = detail };
        host.OnStepStarted(step);
        return step;
    }

    private Outcome Finish(AgentStep step, string text, bool ok, bool ends = false)
    {
        step.Output = text;
        step.State = ok ? StepState.Done : StepState.Failed;
        host.OnStepFinished(step);
        return new Outcome(text, ends);
    }

    private Outcome Denied(AgentStep step)
    {
        step.State = StepState.Skipped;
        step.Output = "The user declined.";
        host.OnStepFinished(step);
        return new Outcome("the user declined this step — find another way or ask.");
    }

    private async Task<bool> ApproveIfNeeded(AgentStep step, string title, string detail, CancellationToken ct) =>
        host.Permission == AgentPermission.Auto || await host.ApproveAsync(title, detail, ct);

    /// <summary>Accepts {"tool","args"}, {"calls":[…]}, [...], {"name","arguments"} and bare widget JSON.</summary>
    internal static (string? Say, List<ToolCall> Calls) Parse(string reply)
    {
        foreach (var node in JsonExtractor.Candidates(reply))
        {
            var calls = new List<ToolCall>();
            string? say = null;
            if (node is JsonObject obj)
            {
                say = Str(obj, "say") ?? Str(obj, "thought") ?? Str(obj, "message");
                if ((obj["calls"] ?? obj["tool_calls"]) is JsonArray many)
                {
                    calls.AddRange(many.OfType<JsonObject>().Select(ToCall).OfType<ToolCall>());
                }
                else if (ToCall(obj) is { } single)
                {
                    calls.Add(single);
                }
                else if (obj.ContainsKey("controls") && obj.ContainsKey("title"))
                {
                    calls.Add(new ToolCall("save_widget", new JsonObject { ["widget"] = obj.DeepClone() }));
                }
            }
            else if (node is JsonArray arr)
            {
                calls.AddRange(arr.OfType<JsonObject>().Select(ToCall).OfType<ToolCall>());
            }
            if (calls.Count > 0) return (say, calls);
        }
        return (null, []);
    }

    private static ToolCall? ToCall(JsonObject o)
    {
        var tool = Str(o, "tool") ?? Str(o, "name") ?? (o["function"] as JsonObject)?["name"]?.ToString();
        if (string.IsNullOrWhiteSpace(tool)) return null;
        var argsNode = o["args"] ?? o["arguments"] ?? o["parameters"] ?? (o["function"] as JsonObject)?["arguments"];
        if (argsNode?.GetValueKind() == JsonValueKind.String && JsonExtractor.TryParse(argsNode.GetValue<string>(), out var parsed))
            argsNode = parsed;
        var args = argsNode is JsonObject ao ? (JsonObject)ao.DeepClone() : new JsonObject();
        return new ToolCall(tool.Trim().ToLowerInvariant(), args);
    }

    private static string? Str(JsonObject o, string key)
    {
        var n = o[key];
        if (n is null) return null;
        return n.GetValueKind() == JsonValueKind.String ? n.GetValue<string>() : n.ToJsonString();
    }

    private static bool Bool(JsonObject o, string key) => ValueText.IsTruthy(Str(o, key));

    private static int? Int(JsonObject o, string key) => int.TryParse(Str(o, key), out var i) ? i : null;

    private static string Expand(string? path) => Environment.ExpandEnvironmentVariables((path ?? "").Trim().Trim('"'));

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "\n…(truncated)";
}
