using System.Text.Json.Nodes;
using TileSmith.Core;
using TileSmith.Core.Actions;
using TileSmith.Core.Ai;
using TileSmith.Core.Remote;
using TileSmith.Core.Widgets;
using Xunit;

namespace TileSmith.Core.Tests;

public class SshTargetTests
{
    [Theory]
    [InlineData("fred@t3lluserver", "fred", "t3lluserver", 22)]
    [InlineData("fred@100.105.77.87:2222", "fred", "100.105.77.87", 2222)]
    [InlineData("ssh fred@host -p 2200", "fred", "host", 2200)]
    [InlineData("ssh://root@box.tail1234.ts.net", "root", "box.tail1234.ts.net", 22)]
    [InlineData("me@[fd7a:115c::1]:22", "me", "fd7a:115c::1", 22)]
    [InlineData("server", null, "server", 22)]
    public void Parses_common_forms(string input, string? user, string host, int port)
    {
        Assert.True(SshTarget.TryParse(input, out var t, out var error), error);
        Assert.Equal(user, t.User);
        Assert.Equal(host, t.Host);
        Assert.Equal(port, t.Port);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fred@host:99999")]
    [InlineData("fred@bad host")]
    public void Rejects_garbage(string input) => Assert.False(SshTarget.TryParse(input, out _, out _));
}

public class AgentProtocolTests
{
    [Fact]
    public void Parses_fenced_tool_call_with_chatter_and_ansi()
    {
        var reply = "\u001b[1mHermes\u001b[0m  ╭─ thinking ─╮\nSure!\n```json\n{\"say\":\"Checking\",\"tool\":\"powershell\",\"args\":{\"script\":\"Get-Date\"}}\n```\nsession: abc";
        var (say, calls) = WidgetAgent.Parse(reply);
        Assert.Equal("Checking", say);
        var call = Assert.Single(calls);
        Assert.Equal("powershell", call.Tool);
        Assert.Equal("Get-Date", call.Args["script"]!.GetValue<string>());
    }

    [Fact]
    public void Parses_batched_calls_and_hermes_tool_call_tags()
    {
        var (_, batch) = WidgetAgent.Parse("{\"say\":\"x\",\"calls\":[{\"tool\":\"read_file\",\"args\":{\"path\":\"a\"}},{\"tool\":\"list_directory\",\"args\":{\"path\":\"b\"}}]}");
        Assert.Equal(2, batch.Count);

        var (_, tagged) = WidgetAgent.Parse("<tool_call>{\"name\": \"finish\", \"arguments\": {\"message\": \"ok\"}}</tool_call>");
        Assert.Equal("finish", Assert.Single(tagged).Tool);
    }

    [Fact]
    public void Bare_widget_json_becomes_save_widget()
    {
        var (_, calls) = WidgetAgent.Parse("{\"title\":\"X\",\"size\":\"small\",\"controls\":[{\"type\":\"button\",\"label\":\"Go\",\"action\":{\"type\":\"power.action\",\"action\":\"lock\"}}]}");
        Assert.Equal("save_widget", Assert.Single(calls).Tool);
    }

    [Fact]
    public void Garbage_yields_no_calls() => Assert.Empty(WidgetAgent.Parse("I can't do that.").Calls);
}

public class ValidatorTests
{
    private static WidgetDefinition Parse(string json) => WidgetDefinition.FromJson(json);

    [Fact]
    public void Resolution_widget_is_valid()
    {
        var w = Parse("""
            {"title":"Resolution","subtitle":"Built-in display","icon":"Display","size":"wide","controls":[{"type":"choice","state":{"type":"display.getResolution","display":"internal"},
             "options":[{"label":"2560 × 1440","value":"2560x1440","action":{"type":"display.setResolution","width":2560,"height":1440,"display":"internal"}},
                        {"label":"1920 × 1200","value":"1920x1200","action":{"type":"display.setResolution","width":"1920","height":1200,"display":"internal"}}]}]}
            """);
        var result = WidgetValidator.Validate(w);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.StartsWith("w_", w.Id);
    }

    [Fact]
    public void Synonyms_and_aliases_are_normalized()
    {
        var w = Parse("""{"title":"T","size":"large","controls":[{"type":"segmented","options":[{"label":"a","action":{"type":"powershell","script":"1"}},{"label":"b","action":{"type":"url","url":"https://x"}}]}]}""");
        var result = WidgetValidator.Validate(w);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal("4x2", w.Size);
        Assert.Equal("choice", w.Controls[0].Type);
        Assert.Equal("shell.powershell", w.Controls[0].Options![0].Action!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Reports_useful_errors()
    {
        var w = Parse("""{"title":"","controls":[{"type":"slider","action":{"type":"audio.setVolume","level":50}},{"type":"toggle","label":"x","state":{"type":"theme.set","mode":"dark"},"on":{"type":"nope"},"off":{"type":"theme.set","mode":"purple"}}]}""");
        var errors = WidgetValidator.Validate(w).Errors;
        Assert.Contains(errors, e => e.Contains("title"));
        Assert.Contains(errors, e => e.Contains("{value}"));
        Assert.Contains(errors, e => e.Contains("doesn't return a value"));
        Assert.Contains(errors, e => e.Contains("\"nope\""));
        Assert.Contains(errors, e => e.Contains("must be one of"));
    }

    [Theory]
    [InlineData("1x1", "slider", "2x1")]   // too small for a slider: raised to its minimum
    [InlineData("small", "toggle", "2x1")] // legacy names still work
    [InlineData("wide", "toggle", "4x1")]
    [InlineData(null, "slider", "4x1")]    // no size: sensible default
    [InlineData("2×2", "toggle", "2x2")]
    public void Sizes_are_normalized(string? size, string type, string expected)
    {
        var control = type == "slider"
            ? """{"type":"slider","action":{"type":"audio.setVolume","level":"{value}"}}"""
            : """{"type":"toggle","label":"x","state":{"type":"theme.get"},"on":{"type":"theme.set","mode":"dark"},"off":{"type":"theme.set","mode":"light"}}""";
        var sizeJson = size is null ? "" : $"\"size\":\"{size}\",";
        var w = Parse($$"""{"title":"V",{{sizeJson}}"controls":[{{control}}]}""");
        var result = WidgetValidator.Validate(w);
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.Equal(expected, w.Size);
    }

    [Theory]
    [InlineData("wifi", "wifi")]
    [InlineData("DarkMode", "dark_mode")]
    [InlineData("Display", "desktop_windows")]
    [InlineData("dark-mode", "dark_mode")]
    [InlineData("definitely_not_an_icon", null)]
    public void Icons_resolve_to_material_symbols(string input, string? expected) => Assert.Equal(expected, Icons.Normalize(input));

    [Fact]
    public void Icon_catalog_is_embedded() => Assert.True(Icons.Count > 3000);

    [Fact]
    public void Default_widgets_are_valid()
    {
        foreach (var w in WidgetStore.Defaults())
        {
            var r = WidgetValidator.Validate(w);
            Assert.True(r.IsValid, w.Title + ": " + string.Join("; ", r.Errors));
        }
    }
}

public class ValueTextTests
{
    [Theory]
    [InlineData("2560 × 1440", "2560x1440")]
    [InlineData("2560X1440", "2560x1440")]
    [InlineData("60.0", "60")]
    [InlineData("Balanced", "balanced")]
    public void Matches_loosely(string a, string b) => Assert.True(ValueText.Matches(a, b));

    [Fact]
    public void Substitutes_value_in_parameters()
    {
        var call = new ActionCall(new JsonObject { ["type"] = "audio.setVolume", ["level"] = "{value}" }, "42");
        Assert.Equal(42, call.GetInt("level"));
    }
}

public class EngineTests
{
    private sealed class NullHost : IActionHost
    {
        public Task<string> RunOnServerAsync(string command, TimeSpan timeout, CancellationToken ct) => Task.FromResult("");
        public void Notify(string title, string? message) { }
        public void SetClipboardText(string text) { }
        public Task PrepareForInputAsync() => Task.CompletedTask;
    }

    [Fact]
    public void Every_catalog_action_has_a_handler() => _ = new ActionEngine(new NullHost());

    [Fact]
    public void Prompt_mentions_every_action()
    {
        var prompt = AgentPrompt.System("- OS: test", []);
        foreach (var a in ActionCatalog.All) Assert.Contains(a.Type, prompt);
    }

    [Fact]
    public async Task Queries_run_on_this_pc()
    {
        var engine = new ActionEngine(new NullHost());
        var resolution = await engine.RunAsync(new JsonObject { ["type"] = "display.getResolution" }, null, TestContext.Current.CancellationToken);
        Assert.Matches(@"^\d+x\d+$", resolution);
        var theme = await engine.RunAsync(new JsonObject { ["type"] = "theme.get" }, null, TestContext.Current.CancellationToken);
        Assert.Contains(theme, new[] { "dark", "light" });
        var ps = await engine.RunAsync(new JsonObject { ["type"] = "shell.powershell", ["script"] = "Write-Output (6*7)" }, null, TestContext.Current.CancellationToken);
        Assert.Equal("42", ps);
    }

    [Fact]
    public void Device_context_lists_display_modes()
    {
        var context = DeviceContext.Collect();
        Assert.Contains("supported modes", context);
    }
}
