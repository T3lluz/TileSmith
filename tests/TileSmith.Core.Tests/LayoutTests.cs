using System.Text.Json.Nodes;
using TileSmith.Core.Actions;
using TileSmith.Core.Ai;
using TileSmith.Core.Widgets;
using Xunit;

namespace TileSmith.Core.Tests;

/// <summary>Server commands answered from a table, so queries are predictable on any machine.</summary>
internal sealed class TableActionHost(Dictionary<string, string> outputs) : IActionHost
{
    public Task<string> RunOnServerAsync(string command, TimeSpan timeout, CancellationToken ct) =>
        outputs.TryGetValue(command, out var output) ? Task.FromResult(output) : throw new ActionException($"no such command: {command}");
    public void Notify(string title, string? message) { }
    public void SetClipboardText(string text) { }
    public Task PrepareForInputAsync() => Task.CompletedTask;
}

public class BindingTests
{
    private static BindingData Data(params (string Name, string Value)[] values)
    {
        var data = new BindingData();
        foreach (var (name, value) in values) data.Set(name, value);
        return data;
    }

    [Fact]
    public void Renders_values_fields_and_filters()
    {
        var data = Data(("cpu", "12.64"), ("pc", """{"memoryUsed": 8589934592, "disks": [{"name": "C", "free": 1.5}], "uptime": 7500}"""), ("name", "spotify"));
        Assert.Equal("CPU 13%", Bindings.Render("CPU {cpu|round}%", data));
        Assert.Equal("8 GB", Bindings.Render("{pc.memoryUsed|bytes}", data));
        Assert.Equal("C: 1.5", Bindings.Render("{pc.disks[0].name}: {pc.disks.0.free}", data));
        Assert.Equal("2h 5m", Bindings.Render("{pc.uptime|duration}", data));
        Assert.Equal("SPOTIFY", Bindings.Render("{name|upper}", data));
        Assert.Equal("—", Bindings.Render("{missing}", data));
        Assert.Equal("n/a", Bindings.Render("{pc.nothing|default:n/a}", data));
        Assert.Equal(12.64, Bindings.Number("{cpu}", data));
    }

    [Theory]
    [InlineData("{cpu} > 80", false)]
    [InlineData("{cpu} >= 12", true)]
    [InlineData("{state} == on", true)]
    [InlineData("{state} != on", false)]
    [InlineData("{title} contains love", true)]
    [InlineData("{state}", true)]
    [InlineData("!{empty}", true)]
    [InlineData("{empty} == ''", true)]
    [InlineData("{cpu} > 80 || {state} == on", true)]
    [InlineData("{cpu} > 80 && {state} == on", false)]
    [InlineData("{missing} == on", false)]
    public void Tests_conditions(string condition, bool expected)
    {
        var data = Data(("cpu", "12.6"), ("state", "On"), ("title", "All You Need Is Love"), ("empty", ""));
        Assert.Equal(expected, Bindings.Test(condition, data));
    }

    [Fact]
    public void Conditional_properties_pick_a_branch()
    {
        var node = JsonNode.Parse("""{"color": {"if": "{cpu} > 80", "then": "error", "else": {"if": "{cpu} > 50", "then": "warning", "else": "accent"}}}""")!.AsObject();
        Assert.Equal("warning", WidgetLayout.Prop(node, "color", Data(("cpu", "60"))));
        Assert.Equal("accent", WidgetLayout.Prop(node, "color", Data(("cpu", "10"))));
        Assert.Equal(["error", "warning", "accent"], WidgetLayout.Branches(node["color"]).ToArray());
    }
}

public class LayoutValidationTests
{
    [Fact]
    public void Every_prompt_example_is_valid()
    {
        foreach (var example in WidgetExamples.All)
        {
            var widget = WidgetDefinition.FromJson(example.Json);
            var result = WidgetValidator.Validate(widget);
            Assert.True(result.IsValid, $"{example.Name}: {string.Join(" ", result.Errors)}");
            Assert.DoesNotContain(result.Warnings, w => w.Contains("ignored") || w.Contains("isn't used") || w.Contains("too small"));
        }
    }

    [Fact]
    public void Picks_examples_close_to_the_request()
    {
        Assert.Contains(WidgetExamples.For("show what's playing on spotify"), e => e.Name.StartsWith("Now playing"));
        Assert.Contains(WidgetExamples.For("a countdown to christmas"), e => e.Name.StartsWith("Countdown"));
        // always at least one layout example to copy the format from
        Assert.Contains(WidgetExamples.For("toggle dark mode"), e => e.Name.Contains("(layout)"));
    }

    private static ValidationResult Validate(string json) => WidgetValidator.Validate(WidgetDefinition.FromJson(json));

    [Fact]
    public void Reports_unknown_data_bad_colours_and_bad_elements_at_once()
    {
        var result = Validate("""
            {"title":"X","size":"2x1","data":{"cpu":{"type":"system.stats","metric":"cpu"}},
             "layout":{"type":"column","children":[
               {"type":"text","text":"{gpu}","color":"purple-ish"},
               {"type":"blink"},
               {"type":"progress"}]}}
            """);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("no data \"gpu\""));
        Assert.Contains(result.Errors, e => e.Contains("isn't a colour"));
        Assert.Contains(result.Errors, e => e.Contains("\"blink\" isn't an element"));
        Assert.Contains(result.Errors, e => e.Contains("needs \"value\""));
        Assert.Contains(result.Warnings, w => w.Contains("data \"cpu\" isn't used"));
    }

    [Fact]
    public void Canonicalizes_synonyms_and_icons()
    {
        var result = Validate("""{"title":"X","layout":{"type":"hstack","children":[{"type":"label","text":"Hi"},{"type":"icon","icon":"moon"}]}}""");
        Assert.True(result.IsValid, string.Join(" ", result.Errors));
        var root = result.Widget.Layout!;
        Assert.Equal("row", root["type"]!.ToString());
        Assert.Equal("text", root["children"]![0]!["type"]!.ToString());
        Assert.Equal("dark_mode", root["children"]![1]!["icon"]!.ToString());
        Assert.Empty(result.Widget.Controls);
    }

    [Fact]
    public void Grows_a_layout_that_does_not_fit_and_rejects_one_that_never_will()
    {
        var tall = Validate("""
            {"title":"X","size":"2x1","layout":{"type":"column","children":[
              {"type":"text","text":"One","size":"headline"},{"type":"text","text":"Two","size":"headline"},{"type":"text","text":"Three","size":"headline"}]}}
            """);
        Assert.True(tall.IsValid, string.Join(" ", tall.Errors));
        Assert.True(tall.Widget.Cells.H >= 2, tall.Widget.Size);

        var huge = Validate("""{"title":"X","layout":{"type":"column","children":[{"type":"ring","value":1,"size":200},{"type":"ring","value":1,"size":200}]}}""");
        Assert.False(huge.IsValid);
        Assert.Contains(huge.Errors, e => e.Contains("at most"));
    }

    [Fact]
    public void Layout_actions_are_validated_and_listed()
    {
        var result = Validate("""
            {"title":"X","data":{"n":{"type":"store.get","key":"n"}},"layout":{"type":"row","children":[
              {"type":"text","text":"{n}"},
              {"type":"button","icon":"add","action":{"type":"store.add","key":"n","delta":1}},
              {"type":"button","icon":"bolt","action":{"type":"power.setMode","mode":"turbo"}}]}}
            """);
        Assert.Contains(result.Errors, e => e.Contains("must be one of efficiency"));
        Assert.Contains(result.Widget.AllActions(), a => a["type"]!.ToString() == "store.add");
        Assert.Contains(result.Widget.AllActions(), a => a["type"]!.ToString() == "store.get");
    }

    [Fact]
    public void Standard_widgets_still_need_controls()
    {
        var result = Validate("""{"title":"X","controls":[]}""");
        Assert.Contains(result.Errors, e => e.Contains("at least one control"));
    }
}

public class VerifierTests
{
    private static ActionEngine Engine(params (string Command, string Output)[] outputs) =>
        new(new TableActionHost(outputs.ToDictionary(o => o.Command, o => o.Output)));

    private static async Task<VerifyResult> Verify(string json, ActionEngine engine)
    {
        var widget = WidgetDefinition.FromJson(json);
        Assert.True(WidgetValidator.Validate(widget).IsValid);
        return await WidgetVerifier.VerifyAsync(widget, engine, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Toggle_must_tell_on_from_off()
    {
        const string toggle = """{"title":"VPN","controls":[{"type":"toggle","label":"VPN","state":{"type":"server.run","command":"vpn"},"on":{"type":"server.run","command":"up"},"off":{"type":"server.run","command":"down"}}]}""";
        var bad = await Verify(toggle, Engine(("vpn", "Status: Connected (wg0)")));
        Assert.Contains(bad.Problems, p => p.Contains("isn't on/off"));

        var good = await Verify(toggle, Engine(("vpn", "connected")));
        Assert.Empty(good.Problems);
        Assert.Contains("shows ON", good.Preview);
    }

    [Fact]
    public async Task Failing_queries_and_missing_fields_are_problems()
    {
        var result = await Verify("""
            {"title":"GPU","data":{"gpu":{"type":"server.run","command":"gpu"},"fan":{"type":"server.run","command":"fan"}},
             "layout":{"type":"column","children":[{"type":"text","text":"{gpu.temp}°"},{"type":"text","text":"{gpu.load}%"},{"type":"text","text":"{fan}"}]}}
            """, Engine(("gpu", """{"temperature": 61, "load": 40}""")));
        Assert.Contains(result.Problems, p => p.Contains("{gpu.temp}") && p.Contains("has no \"temp\""));
        Assert.DoesNotContain(result.Problems, p => p.Contains("{gpu.load}"));
        Assert.Contains(result.Problems, p => p.Contains("data.fan") && p.Contains("failed"));
        Assert.Contains("40%", result.Preview);
    }

    [Fact]
    public async Task Choice_mismatch_is_a_note_for_a_value_of_the_same_kind_only()
    {
        const string choice = """{"title":"Mode","size":"4x1","controls":[{"type":"choice","state":{"type":"server.run","command":"mode"},"options":[{"label":"Eco","value":"eco","action":{"type":"server.run","command":"x"}},{"label":"Fast","value":"fast","action":{"type":"server.run","command":"y"}}]}]}""";
        var sameKind = await Verify(choice, Engine(("mode", "balanced")));
        Assert.Empty(sameKind.Problems);
        Assert.Contains(sameKind.Notes, n => n.Contains("matches none"));

        var wrongKind = await Verify(choice, Engine(("mode", "Current mode: 2 (fast)")));
        Assert.Contains(wrongKind.Problems, p => p.Contains("matches none"));
    }

    [Fact]
    public async Task Select_picks_a_field_from_json_output()
    {
        var engine = Engine(("stats", """{"gpu": {"temp": 61}}"""));
        var action = JsonNode.Parse("""{"type":"server.run","command":"stats","select":"gpu.temp"}""")!.AsObject();
        Assert.Equal("61", await engine.RunAsync(action, null, TestContext.Current.CancellationToken));
    }
}
