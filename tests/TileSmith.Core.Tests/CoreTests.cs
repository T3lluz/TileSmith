using System.Text.Json.Nodes;
using TileSmith.Core;
using TileSmith.Core.Actions;
using TileSmith.Core.Ai;
using TileSmith.Core.Remote;
using TileSmith.Core.Tiles;
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

public class JsonExtractorTests
{
    [Fact]
    public void Finds_fenced_json_among_chatter_and_ansi()
    {
        var reply = "\u001b[1mHermes\u001b[0m  ╭─ thinking ─╮\nSure!\n```json\n{\"reply\":\"Done\",\"design\":{\"rows\":[]}}\n```\nsession: abc";
        var node = Assert.IsType<JsonObject>(JsonExtractor.Candidates(reply).First());
        Assert.Equal("Done", node["reply"]!.GetValue<string>());
    }

    [Fact]
    public void Finds_bare_and_tagged_json() =>
        Assert.Equal(2, JsonExtractor.Candidates("<tool_call>{\"a\": 1}</tool_call> then {\"b\": [1, 2]}").Count());

    [Fact]
    public void Garbage_yields_nothing() => Assert.Empty(JsonExtractor.Candidates("I can't do that."));

    [Theory]
    [InlineData("data.items[0].price", "4.2")]
    [InlineData("data.items.1.price", "7")]
    [InlineData("data.items[-1].name", "b")]
    [InlineData("data.missing", null)]
    public void Json_paths_navigate(string path, string? expected)
    {
        Assert.True(JsonPath.TryParse("""{"data":{"items":[{"name":"a","price":4.2},{"name":"b","price":7}]}}""", out var root));
        Assert.Equal(expected, JsonPath.Text(JsonPath.Navigate(root, path)));
    }
}

public class IconTests
{
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
    public void Every_icon_the_app_names_exists()
    {
        var named = TileSources.All.Select(s => s.Icon)
            .Concat(TileSources.All.Select(s => s.Sample.Icon).OfType<string>())
            .Concat(TileStarters.All.SelectMany(s => s.Design.Blocks).Select(b => b.Icon).OfType<string>())
            .Concat(["wifi_off", "bluetooth_disabled", "light_mode", "volume_mute", "volume_down", "mic_off", "brightness_low", "brightness_medium",
                     "brightness_high", "eco", "balance", "vpn_key_off", "battery_charging_full", "battery_3_bar", "cloud_off", "umbrella", "wb_sunny",
                     "skip_previous", "skip_next", "play_arrow", "pause", "auto_awesome", "key", "warning", "check_circle"]);
        foreach (var icon in named.Distinct()) Assert.True(Icons.IsKnown(icon), icon);
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
    public void Select_reads_a_field_of_json_output() =>
        Assert.Equal("b", ActionEngine.Select("""{"items":[{"name":"a"},{"name":"b"}]}""", "items[1].name", "test"));

    [Fact]
    public void Device_context_lists_display_modes()
    {
        var context = DeviceContext.Collect();
        Assert.Contains("supported modes", context);
    }
}
