using System.Text.Json;
using System.Text.Json.Nodes;
using TileSmith.Core.Actions;
using TileSmith.Core.Ai;
using TileSmith.Core.Native;
using TileSmith.Core.Settings;
using TileSmith.Core.Widgets;
using Xunit;

namespace TileSmith.Core.Tests;

public class ModelDirectoryTests
{
    private const string Sample = """
        {
          "anthropic": {"id":"anthropic","models":{
            "claude-opus-5-5": {"id":"claude-opus-5-5","name":"Claude Opus 5.5","release_date":"2026-09-22","reasoning":true,"tool_call":true,
                                "modalities":{"input":["text"],"output":["text"]},"limit":{"context":1000000,"output":128000},"cost":{"input":5,"output":25}},
            "claude-haiku-4-5": {"id":"claude-haiku-4-5","name":"Claude Haiku 4.5 (latest)","release_date":"2025-10-15","limit":{"context":200000},"cost":{"input":1,"output":5}},
            "claude-haiku-4-5-20251001": {"id":"claude-haiku-4-5-20251001","name":"Claude Haiku 4.5","release_date":"2025-10-15","limit":{"context":200000}},
            "claude-old": {"id":"claude-old","name":"Old","status":"deprecated","release_date":"2024-01-01"}
          }},
          "opencode": {"id":"opencode","models":{
            "big-pickle": {"id":"big-pickle","name":"Big Pickle","release_date":"2025-10-17","cost":{"input":0,"output":0},"limit":{"context":200000}},
            "gpt-image": {"id":"gpt-image","name":"Image","modalities":{"input":["text"],"output":["image"]}}
          }},
          "someone-else": {"id":"someone-else","models":{}}
        }
        """;

    [Fact]
    public void Parses_names_dates_prices_and_drops_what_cant_chat()
    {
        using var doc = JsonDocument.Parse(Sample);
        var parsed = ModelDirectory.Parse(doc.RootElement);
        Assert.False(parsed.ContainsKey("someone-else"));

        var claude = ModelDirectory.Chat(parsed["anthropic"]).ToList();
        Assert.Equal(["claude-opus-5-5", "claude-haiku-4-5"], claude.Select(m => m.Id)); // newest first, no deprecated, no dated twin
        var opus = ModelDirectory.ToModelInfo(claude[0], "claude");
        Assert.Equal("Claude Opus 5.5", opus.Name);
        Assert.Equal("claude", opus.Brand);
        Assert.True(opus.Reasoning);
        Assert.Contains("1M context", opus.Detail);
        Assert.Contains("$5 / $25", opus.Detail);
        Assert.Equal("Claude Haiku 4.5", ModelDirectory.ToModelInfo(claude[1], "claude").Name); // "(latest)" dropped

        var free = ModelDirectory.Chat(parsed["opencode"]).ToList();
        Assert.Equal(["big-pickle"], free.Select(m => m.Id)); // image model hidden
        Assert.True(ModelDirectory.ToModelInfo(free[0], "opencode").Free);
    }

    [Theory]
    [InlineData("claude-opus-5-5", "anthropic", "claude")]
    [InlineData("anthropic/claude-sonnet-5-5", "openrouter", "claude")]
    [InlineData("gpt-5.4", "openai", "openai")]
    [InlineData("o4-mini", "openai", "openai")]
    [InlineData("google/gemini-3-pro", "openrouter", "gemini")]
    [InlineData("qwen3.6-plus-free", "opencode", "qwen")]
    [InlineData("nemotron-3-ultra-free", "opencode", "nvidia")]
    [InlineData("big-pickle", "opencode", "opencode")]
    [InlineData("some-unknown-model", "groq", "groq")]
    [InlineData("glm-5", "openrouter", "zhipu")]
    public void Picks_the_makers_logo(string model, string provider, string brand) =>
        Assert.Equal(brand, Brands.ForModel(model, provider));
}

public class OpenCodeTests
{
    [Fact]
    public void Joins_the_text_of_json_events()
    {
        var output = """
            {"type":"step_start","part":{"type":"step-start"}}
            {"type":"text","part":{"type":"text","text":"thinking…</think>```json\n{\"tool\":\"finish\",\"args\":{}}\n```"}}
            {"type":"step_finish","part":{"type":"step-finish"}}
            """;
        var text = OpenCodeBackend.ParseEvents(output);
        Assert.Contains("\"tool\":\"finish\"", text);
        Assert.NotEmpty(WidgetAgentParse(text));
    }

    [Fact]
    public void Free_tier_errors_become_fallbackable_ai_errors()
    {
        var output = """{"type":"error","error":{"name":"APIError","data":{"message":"OpenCode's free tier can only be used from within OpenCode."}}}""";
        var ex = Assert.Throws<AiException>(() => OpenCodeBackend.ParseEvents(output));
        Assert.True(ex.CanFallback);
        Assert.Contains("free tier", ex.Message);
    }

    [Fact]
    public void A_rejected_tool_call_asks_the_model_again_instead_of_failing()
    {
        var output = """{"type":"tool_use","part":{"type":"tool","tool":"read","state":{"status":"error"}}}""";
        Assert.Contains("own tools", OpenCodeBackend.ParseEvents(output));
    }

    [Fact]
    public void OpenCode_is_a_provider_with_free_models()
    {
        var provider = ProviderCatalog.Get("opencode");
        Assert.Equal("Free models", provider.KindLabel);
        Assert.Equal("opencode", Brands.ForProvider("opencode"));
    }

    private static IReadOnlyList<object> WidgetAgentParse(string text) =>
        [.. JsonExtractor.Candidates(text)];
}

public class ListElementTests
{
    private static BindingData Data(string name, string json)
    {
        var data = new BindingData();
        data.Set(name, json);
        return data;
    }

    [Fact]
    public void Instantiates_items_with_their_data_paths()
    {
        var template = JsonNode.Parse("""{"type":"row","children":[{"type":"text","text":"{index}. {item.name}"},{"type":"text","text":"{item.cpu|round}%"}]}""")!.AsObject();
        var first = WidgetLayout.Instantiate(template, "top", 0).ToJsonString();
        Assert.Contains("1. {top[0].name}", first);
        Assert.Contains("{top[0].cpu|round}%", first);

        var data = Data("top", """[{"name":"chrome","cpu":12.6},{"name":"code","cpu":3}]""");
        Assert.Equal(2, data.Length("top"));
        Assert.Equal("1. chrome", Bindings.Render("1. {top[0].name}", data));
        Assert.Null(data.Length("top[0].name"));
    }

    [Fact]
    public void Validates_items_and_allows_item_placeholders_only_inside_the_template()
    {
        var ok = WidgetValidator.Validate(WidgetDefinition.FromJson("""
            {"title":"Top","data":{"top":{"type":"system.top"}},"layout":{"type":"list","items":"{top}","template":{"type":"text","text":"{item.name}"}}}
            """));
        Assert.True(ok.IsValid, string.Join(" ", ok.Errors));

        var bad = WidgetValidator.Validate(WidgetDefinition.FromJson("""
            {"title":"Top","data":{"top":{"type":"system.top"}},"layout":{"type":"column","children":[
              {"type":"text","text":"{item.name}"},
              {"type":"list","items":"{nope}","template":{"type":"text","text":"x"}},
              {"type":"list","items":"{top}"}]}}
            """));
        Assert.Contains(bad.Errors, e => e.Contains("no data \"item\""));
        Assert.Contains(bad.Errors, e => e.Contains("no data \"nope\""));
        Assert.Contains(bad.Errors, e => e.Contains("needs \"template\""));
    }

    [Fact]
    public async Task Verifier_checks_that_items_are_an_array_and_the_fields_exist()
    {
        var engine = new ActionEngine(new TableActionHost(new() { ["ps"] = """{"name":"only one"}""", ["arr"] = """[{"name":"a"}]""" }));
        var notArray = WidgetDefinition.FromJson("""
            {"title":"X","data":{"p":{"type":"server.run","command":"ps"}},"layout":{"type":"list","items":"{p}","template":{"type":"text","text":"{item.name}"}}}
            """);
        WidgetValidator.Validate(notArray);
        var r1 = await WidgetVerifier.VerifyAsync(notArray, engine, TestContext.Current.CancellationToken);
        Assert.Contains(r1.Problems, p => p.Contains("isn't a JSON array"));

        var wrongField = WidgetDefinition.FromJson("""
            {"title":"X","data":{"p":{"type":"server.run","command":"arr"}},"layout":{"type":"list","items":"{p}","template":{"type":"text","text":"{item.title}"}}}
            """);
        WidgetValidator.Validate(wrongField);
        var r2 = await WidgetVerifier.VerifyAsync(wrongField, engine, TestContext.Current.CancellationToken);
        Assert.Contains(r2.Problems, p => p.Contains("{p[0].title}"));
    }
}

public class ActionPlaceholderTests
{
    [Fact]
    public void Fills_data_placeholders_but_keeps_value()
    {
        var data = new BindingData();
        data.Set("top", """[{"name":"chrome","pid":42}]""");
        var action = JsonNode.Parse("""{"type":"app.close","name":"{top[0].name}","note":"{value} stays","steps":[{"x":"{top[0].pid}"}]}""")!.AsObject();
        var resolved = Bindings.ResolveAction(action, data);
        Assert.Equal("chrome", resolved["name"]!.ToString());
        Assert.Equal("{value} stays", resolved["note"]!.ToString());
        Assert.Equal("42", resolved["steps"]![0]!["x"]!.ToString());
        Assert.Equal("{top[0].name}", action["name"]!.ToString()); // the widget itself is untouched
    }

    [Fact]
    public void Number_params_may_be_placeholders()
    {
        var result = WidgetValidator.Validate(WidgetDefinition.FromJson("""
            {"title":"X","data":{"n":{"type":"store.get","key":"n"}},"layout":{"type":"button","label":"Go","action":{"type":"store.add","key":"n","delta":"{n}"}}}
            """));
        Assert.True(result.IsValid, string.Join(" ", result.Errors));
    }
}

public class WeatherAndSettingsTests
{
    [Theory]
    [InlineData(0, true, "Clear", "sunny")]
    [InlineData(0, false, "Clear", "clear_night")]
    [InlineData(2, true, "Partly cloudy", "partly_cloudy_day")]
    [InlineData(63, true, "Rain", "rainy")]
    [InlineData(75, true, "Heavy snow", "weather_snowy")]
    [InlineData(95, true, "Thunderstorm", "thunderstorm")]
    public void Weather_codes_have_words_and_icons(int code, bool day, string condition, string icon)
    {
        Assert.Equal(condition, Weather.Condition(code));
        Assert.Equal(icon, Weather.Icon(code, day));
        Assert.NotNull(Icons.Normalize(icon));
    }

    [Fact]
    public void Everyone_moves_to_liquid_glass_once()
    {
        var old = SettingsStore.Migrate(new AppSettings { Version = 2, PanelStyle = "solid" });
        Assert.Equal("glass", old.PanelStyle);
        Assert.Equal(AppSettings.CurrentVersion, old.Version);

        var chosen = SettingsStore.Migrate(new AppSettings { Version = AppSettings.CurrentVersion, PanelStyle = "solid" });
        Assert.Equal("solid", chosen.PanelStyle);
    }

    [Fact]
    public void New_actions_are_in_the_catalog_and_the_prompt()
    {
        foreach (var type in new[] { "weather.get", "network.info", "system.top", "system.stats", "media.nowPlaying" })
            Assert.True(ActionCatalog.Find(type)?.IsQuery, type);
        var prompt = AgentPrompt.System("- OS: test", [], "a weather widget");
        Assert.Contains("weather.get", prompt);
        Assert.Contains("list {items", prompt);
        Assert.Contains("Weather with a forecast list", prompt); // the closest example came along
    }
}

/// <summary>The warm PowerShell host (Windows only, like the other PowerShell tests).</summary>
public class PowerShellHostTests
{
    [Fact]
    public async Task Answers_queries_and_survives_scripts_that_exit()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("42", await PowerShellHost.QueryAsync("Write-Output 42", 20, ct));
        var mixed = await PowerShellHost.QueryAsync("Write-Host 'hello'; 'world'", 20, ct);
        Assert.Contains("hello", mixed);
        Assert.EndsWith("world", mixed);
        await Assert.ThrowsAsync<ActionException>(() => PowerShellHost.QueryAsync("Get-Item 'C:\\definitely-not-here-tilesmith' -ErrorAction Stop", 20, ct));
        // exit would end the shared host: the script runs on its own instead and the host keeps working
        Assert.Equal("bye", await PowerShellHost.QueryAsync("Write-Output 'bye'; exit 0", 20, ct));
        Assert.Equal("7", await PowerShellHost.QueryAsync("3 + 4", 20, ct));
    }
}
