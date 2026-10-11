using TileSmith.Core.Actions;
using TileSmith.Core.Ai;
using TileSmith.Core.Widgets;
using Xunit;

namespace TileSmith.Core.Tests;

/// <summary>Runs the real agent loop against a scripted "model" and real tools on this PC.</summary>
public class AgentLoopTests
{
    private sealed class ScriptedBackend(params string[] replies) : IAiBackend
    {
        private int _turn;
        public List<IReadOnlyList<AiMessage>> Seen { get; } = [];
        public string DisplayName => "scripted";

        public Task<string> CompleteAsync(IReadOnlyList<AiMessage> messages, CancellationToken ct)
        {
            Seen.Add([.. messages]);
            return Task.FromResult(replies[Math.Min(_turn++, replies.Length - 1)]);
        }
    }

    private sealed class FakeHost : IAgentHost
    {
        public List<WidgetDefinition> Widgets { get; } = [];
        public List<AgentStep> Steps { get; } = [];
        public AgentPermission Permission => AgentPermission.Auto;
        public IReadOnlyList<WidgetDefinition> GetWidgets() => Widgets;

        public Task<string> SaveWidgetAsync(WidgetDefinition widget, CancellationToken ct)
        {
            Widgets.RemoveAll(w => w.Id == widget.Id);
            Widgets.Add(widget);
            return Task.FromResult("added to the panel");
        }

        public Task<string> RemoveWidgetAsync(string id, CancellationToken ct) => Task.FromResult("removed");
        public Task<string> MoveWidgetAsync(string id, int position, CancellationToken ct) => Task.FromResult("moved");
        public Task<string> ResizeWidgetAsync(string id, string size, CancellationToken ct) => Task.FromResult("resized");
        public Task<bool> ApproveAsync(string title, string detail, CancellationToken ct) => Task.FromResult(true);
        public Task<string> AskUserAsync(string question, IReadOnlyList<string> options, CancellationToken ct) => Task.FromResult(options[0]);
        public void OnStatus(string text) { }
        public void OnSay(string text) { }
        public void OnStepStarted(AgentStep step) => Steps.Add(step);
        public void OnStepFinished(AgentStep step) { }
    }

    private sealed class NullActionHost : IActionHost
    {
        public Task<string> RunOnServerAsync(string command, TimeSpan timeout, CancellationToken ct) => Task.FromResult("");
        public void Notify(string title, string? message) { }
        public void SetClipboardText(string text) { }
        public Task PrepareForInputAsync() => Task.CompletedTask;
    }

    [Fact]
    public async Task Builds_the_resolution_widget_in_three_turns()
    {
        var backend = new ScriptedBackend(
            // turn 1: inspect the PC with a real query
            """
            Let me check first.
            ```json
            {"say":"Checking your display","tool":"test_action","args":{"action":{"type":"display.getResolution","display":"internal"}}}
            ```
            """,
            // turn 2: a typo'd widget → validation errors come back
            """
            ```json
            {"say":"Adding it","tool":"save_widget","args":{"widget":{"title":"Resolution","icon":"aspect_ratio","size":"4x1","controls":[{"type":"choice","options":[{"label":"2560 × 1440","action":{"type":"display.setResolution","width":2560}}]}]}}}
            ```
            """,
            // turn 3: the fixed widget
            """
            ```json
            {"say":"Fixed it","tool":"save_widget","args":{"widget":{"title":"Resolution","subtitle":"Built-in display","icon":"aspect_ratio","size":"4x1","controls":[{"type":"choice","state":{"type":"display.getResolution","display":"internal"},"options":[{"label":"2560 × 1440","value":"2560x1440","action":{"type":"display.setResolution","width":2560,"height":1440,"display":"internal"}},{"label":"1920 × 1200","value":"1920x1200","action":{"type":"display.setResolution","width":1920,"height":1200,"display":"internal"}}]}]}}}
            ```
            """);
        var host = new FakeHost();
        var agent = new WidgetAgent(backend, new ActionEngine(new NullActionHost()), host, () => "- Displays: test");

        var result = await agent.RunAsync("switch between 2560x1440 and 1920x1200 on my laptop display", "auto", null, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Turns);
        var widget = Assert.Single(host.Widgets);
        Assert.Equal("Resolution", widget.Title);
        Assert.Equal("4x1", widget.Size);
        Assert.Equal(2, widget.Controls[0].Options!.Count);

        // the real query ran and its result went back to the model
        Assert.Matches(@"output: \d+x\d+", backend.Seen[1].Last().Content);
        // the validator's complaints reached the model before it fixed the widget
        Assert.Contains("requires \"height\"", backend.Seen[2].Last().Content);
        Assert.Contains("at least 2 options", backend.Seen[2].Last().Content);
    }

    [Fact]
    public async Task Recovers_from_chatty_replies_and_gives_up_after_three()
    {
        var backend = new ScriptedBackend("Sure, I'd love to help!", "Here you go!", "Hmm.");
        var agent = new WidgetAgent(backend, new ActionEngine(new NullActionHost()), new FakeHost(), () => "");
        var ex = await Assert.ThrowsAsync<AiException>(() => agent.RunAsync("x", null, null, TestContext.Current.CancellationToken));
        Assert.Contains("wrong format", ex.Message);
    }

    [Fact]
    public async Task Runs_powershell_and_asks_the_user()
    {
        var backend = new ScriptedBackend(
            """{"say":"Looking","calls":[{"tool":"powershell","args":{"script":"Write-Output 'hello from PS'"}},{"tool":"ask_user","args":{"question":"Size?","options":["Small","Big"]}}]}""",
            """{"tool":"finish","args":{"message":"All good"}}""");
        var host = new FakeHost();
        var agent = new WidgetAgent(backend, new ActionEngine(new NullActionHost()), host, () => "");
        var result = await agent.RunAsync("x", null, null, TestContext.Current.CancellationToken);
        Assert.Equal("All good", result.Message);
        var feedback = backend.Seen[1].Last().Content;
        Assert.Contains("hello from PS", feedback);
        Assert.Contains("the user answered: Small", feedback);
    }
}
