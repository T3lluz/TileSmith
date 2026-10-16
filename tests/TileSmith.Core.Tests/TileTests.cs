using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using TileSmith.Core.Actions;
using TileSmith.Core.Ai;
using TileSmith.Core.Settings;
using TileSmith.Core.Tiles;
using Xunit;

namespace TileSmith.Core.Tests;

internal static class Sandbox
{
    /// <summary>Tests never touch the real %AppData%\TileSmith: values, counters and designs go to a temp folder.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tilesmith-tests-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("TILESMITH_DATA_DIR", dir);
    }
}

internal sealed class ScriptedBackend(params string[] replies) : IAiBackend
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

internal sealed class FakeTileHost : ITileHost, IActionHost
{
    public FakeTileHost() => Actions = new ActionEngine(this);
    public ActionEngine Actions { get; }
    public TileSettings Tiles { get; } = new();
    public bool ServerConfigured { get; set; }
    public string ServerName => "box";
    public string ServerOutput { get; set; } = "";
    public Task<string> RunOnServerAsync(string script, TimeSpan timeout, CancellationToken ct) => Task.FromResult(ServerOutput);
    public bool AiReady { get; set; }
    public IAiBackend? Backend { get; set; }
    public IAiBackend CreateAi() => Backend ?? throw new AiException("No AI");
    Task<string> IActionHost.RunOnServerAsync(string command, TimeSpan timeout, CancellationToken ct) => Task.FromResult(ServerOutput);
    public void Notify(string title, string? message) { }
    public void SetClipboardText(string text) { }
    public Task PrepareForInputAsync() => Task.CompletedTask;
}

public class TileCodecTests
{
    private static TileDesign Parse(string json) => TileCodec.Parse(JsonNode.Parse(json)!.AsObject());

    [Fact]
    public void Reads_what_an_ai_writes_and_forgets_nothing_on_the_way_back()
    {
        var d = Parse("""
            {"name":"PC pulse","size":"wide","palette":"mint","surface":"tonal","rows":[
              {"h":1,"cells":[{"type":"ring","bind":"pc.cpu","label":"CPU"},{"type":"metric","bind":"pc.disk","w":2,"query":"D","tone":"tertiary"}]},
              {"cells":[{"type":"chart","bind":"pc.cpu_history","variant":"line"}]}]}
            """);
        Assert.Equal(TileSize.Wide, d.Size);
        Assert.Same(TilePalette.Mint, d.Palette);
        Assert.Equal(3, d.Blocks.Count);
        Assert.Equal("D", d.Blocks[1].Query);
        Assert.Equal(TileTone.Tertiary, d.Blocks[1].Tone);
        var again = TileCodec.Parse(TileCodec.ToJson(d));
        Assert.Equal(TileCodec.ToJson(d).ToJsonString(), TileCodec.ToJson(again).ToJsonString());
    }

    [Fact]
    public void Lenient_about_names_drops_unknown_types_and_clamps()
    {
        var d = Parse("""
            {"name":"x","size":"2x2","palette":"Material You","surface":"bold","rows":[
              {"h":9,"cells":[{"type":"gauge","bind":"cpu","w":12},{"type":"hologram"},{"type":"switch","bind":"wifi"}]},
              {"cells":[{"type":"segmented","bind":"power.mode","options":["Efficiency","Balanced"]}]},
              {"cells":[{"type":"text","text":"too many rows"}]}]}
            """);
        Assert.Equal(TileSize.Small, d.Size);
        Assert.Same(TilePalette.Accent, d.Palette);
        Assert.Equal(TileSurface.Vivid, d.Surface);
        Assert.Equal(2, d.Rows.Count); // a small has two rows
        Assert.Equal(3, d.Rows[0].Height);
        Assert.Equal([BlockType.Ring, BlockType.Toggle], d.Rows[0].Cells.Select(c => c.Type));
        Assert.Equal(4, d.Rows[0].Cells[0].Weight);
        Assert.Equal("pc.cpu", d.Rows[0].Cells[0].Bind);
        Assert.Equal("sys.wifi", d.Rows[0].Cells[1].Bind);
        var choice = d.Rows[1].Cells[0];
        Assert.Equal("sys.power_mode", choice.Bind);
        Assert.Equal(["Efficiency", "Balanced"], choice.Options.Select(o => o.Label));
    }

    [Fact]
    public void A_flat_list_of_blocks_becomes_one_row() =>
        Assert.Single(Parse("""{"size":"bar","blocks":[{"type":"metric","bind":"pc.cpu"},{"type":"metric","bind":"pc.memory"}]}""").Rows);

    [Theory]
    [InlineData("https://github.com", "url:https://github.com")]
    [InlineData("ms-settings:display", "url:ms-settings:display")]
    [InlineData("settings:Bluetooth", "settings:bluetooth")]
    [InlineData("keys:Win+Shift+S", "keys:Win+Shift+S")]
    [InlineData("media:playpause", "media:toggle")]
    [InlineData("power:Shut down", "power:shutdown")]
    [InlineData("power:explode", null)]
    [InlineData("store:water+1", "store:water+1")]
    [InlineData("store:done!", "store:done!")]
    [InlineData("store:+1", null)]
    [InlineData("app", "studio")]
    [InlineData("spotify:playlist:123", "url:spotify:playlist:123")]
    [InlineData("nonsense", null)]
    public void Taps_are_normalized(string raw, string? expected) => Assert.Equal(expected, TileTap.Normalize(raw));

    [Fact]
    public void Reflow_moves_blocks_into_the_new_size_and_counts_what_doesnt_fit()
    {
        var d = TileStarters.Instantiate(TileStarters.ById("pulse")!);
        var (bar, dropped) = TileRules.Reflow(d, TileSize.Bar);
        Assert.Single(bar.Rows);
        Assert.Equal(4, bar.Blocks.Count);
        Assert.Equal(0, dropped);
        var (icon, droppedIcon) = TileRules.Reflow(d, TileSize.Icon);
        Assert.Single(icon.Blocks);
        Assert.Equal(3, droppedIcon);
    }

    [Fact]
    public void Refresh_never_goes_faster_than_the_sources()
    {
        var weather = TileStarters.Instantiate(TileStarters.ById("weather")!) with { Refresh = 0 };
        Assert.Equal(600, TileRules.EffectiveRefreshSeconds(weather));
        var pulse = TileStarters.Instantiate(TileStarters.ById("pulse")!) with { Refresh = 0 };
        Assert.Equal(2, TileRules.EffectiveRefreshSeconds(pulse));
    }
}

public class TileSourceTests
{
    [Fact]
    public void Keys_are_unique_and_every_source_explains_itself()
    {
        Assert.Equal(TileSources.All.Count, TileSources.All.Select(s => s.Key).Distinct().Count());
        foreach (var s in TileSources.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Hint), s.Key);
            Assert.True(s.FloorSeconds >= 1, s.Key);
            Assert.NotEmpty(TileSources.TypesFor(s));
            foreach (var t in TileSources.TypesFor(s)) Assert.True(TileSources.Fits(t, s), $"{s.Key} offers {t} but it doesn't fit");
        }
    }

    [Fact]
    public void Binding_ids_are_shared_and_stable()
    {
        var a = new TileBlock { Type = BlockType.Metric, Bind = "pc.cpu" };
        var b = new TileBlock { Type = BlockType.Ring, Bind = "CPU", Label = "Load" };
        Assert.Equal(TileSources.BindingId(a), TileSources.BindingId(b));
        var oslo = new TileBlock { Type = BlockType.Metric, Bind = "weather.now", Query = "Oslo" };
        var bergen = oslo with { Query = "Bergen" };
        Assert.NotEqual(TileSources.BindingId(oslo), TileSources.BindingId(bergen));
        Assert.Equal("weather.now#" + TileSources.Hash("oslo"), TileSources.BindingId(oslo));
    }

    [Fact]
    public void Controls_have_sources_that_can_change()
    {
        Assert.Equal(SourceControl.Switch, TileSources.ByKey("sys.wifi")!.Control);
        Assert.Equal(SourceControl.Level, TileSources.ByKey("volume")!.Control);
        Assert.Equal(SourceControl.Choice, TileSources.ByKey("sys.resolution")!.Control);
        Assert.False(TileSources.Fits(BlockType.Toggle, TileSources.ByKey("pc.cpu")!));
    }
}

public class TileStarterTests
{
    [Fact]
    public void Every_starter_is_a_design_that_fits_its_size()
    {
        Assert.True(TileStarters.All.Count >= 30);
        foreach (var s in TileStarters.All)
        {
            var d = s.Design;
            Assert.Equal(d.Blocks.Count, TileRules.Sanitize(d).Blocks.Count);
            foreach (var b in d.Blocks)
            {
                if (b.Bind is null) continue;
                var src = TileSources.ByKey(b.Bind);
                Assert.True(src is not null, $"{s.Id}: no source {b.Bind}");
                Assert.True(TileSources.Fits(b.Type, src!), $"{s.Id}: {b.Type} can't show {b.Bind}");
            }
            var (w, h) = TileLayout.SizeOf(d.Size);
            var plan = TileLayout.Layout(d, w, h);
            Assert.True(plan.Dropped == 0, $"{s.Id} loses {plan.Dropped} block(s) at {d.Size.Id}");
            Assert.Contains(s.Category, TileStarters.Categories);
        }
    }

    [Fact]
    public void Requests_find_a_matching_starter()
    {
        Assert.Equal("pulse", TileStarters.Match("a dashboard with my cpu and memory")!.Id);
        Assert.Equal("player", TileStarters.Match("what's playing on Spotify")!.Id);
        Assert.Null(TileStarters.Match("zzz"));
    }

    [Fact]
    public void The_gallery_leaves_out_what_this_pc_lacks()
    {
        var desktop = new TileEnvironment(HasBattery: false, HasBrightness: false, HasWifi: false, Displays: 1, ServerConfigured: false);
        var available = TileStarters.All.Where(s => s.IsAvailable(desktop)).Select(s => s.Id).ToList();
        Assert.DoesNotContain("battery", available);
        Assert.DoesNotContain("brightness", available);
        Assert.DoesNotContain("server", available);
        Assert.Contains("volume", available);
    }
}

public class TileLayoutTests
{
    [Fact]
    public void Widths_and_heights_add_up_to_the_tile()
    {
        var d = TileStarters.ById("weather")!.Design;
        var (w, h) = TileLayout.SizeOf(d.Size);
        var plan = TileLayout.Layout(d, w, h);
        foreach (var row in plan.Rows)
            Assert.Equal(w - 2 * plan.Pad, row.Cells.Sum(c => c.Width) + plan.Gap * (row.Cells.Count - 1), 3);
        Assert.Equal(h - 2 * plan.Pad, plan.Rows.Sum(r => r.Height) + plan.Gap * (plan.Rows.Count - 1), 3);
    }

    [Fact]
    public void A_smaller_tile_drops_whole_blocks_instead_of_squeezing_them()
    {
        var d = TileStarters.ById("weather")!.Design;
        var plan = TileLayout.Layout(d, TileLayout.Width(4), TileLayout.Height(1));
        Assert.True(plan.Dropped > 0);
        Assert.All(plan.Cells, c => Assert.True(c.Height + 0.5 >= TileLayout.MinHeight(c.Block.Type)));
    }

    [Fact]
    public void A_lone_switch_is_the_whole_tile()
    {
        var plan = TileLayout.Layout(TileStarters.ById("wifi")!.Design, TileLayout.Width(2), TileLayout.Height(1));
        Assert.True(plan.Solo);
        Assert.Equal(0, plan.Pad);
    }
}

public class TileAiTests
{
    private static readonly TileAi.Context Context = new("- OS: test", "Oslo", false, null);

    private const string Good = """
        Here you go!
        ```json
        {"reply": "A dashboard with CPU and memory.", "suggestions": ["Add the disk", "Make it green", "Add a CPU graph", "Way too long a suggestion to ever show as a chip in the studio"],
         "design": {"name": "PC", "size": "wide", "palette": "mint", "rows": [{"cells": [{"type": "ring", "bind": "pc.cpu"}, {"type": "ring", "bind": "pc.memory"}]}]}}
        ```
        """;

    [Fact]
    public void Parses_a_good_answer()
    {
        var r = TileAi.Parse(Good, null);
        Assert.Equal("A dashboard with CPU and memory.", r.Reply);
        Assert.Equal(["Add the disk", "Make it green", "Add a CPU graph"], r.Suggestions);
        Assert.Equal(2, r.Design.Blocks.Count);
        Assert.Empty(r.Unknown);
    }

    [Fact]
    public void An_unknown_source_becomes_text_and_is_reported()
    {
        var r = TileAi.Parse("""{"reply":"ok","design":{"size":"mini","rows":[{"cells":[{"type":"metric","bind":"gpu.temp","label":"GPU"}]}]}}""", null);
        Assert.Equal(["gpu.temp"], r.Unknown);
        Assert.Equal(BlockType.Text, r.Design.Blocks[0].Type);
        Assert.Null(r.Design.Blocks[0].Bind);
    }

    [Fact]
    public void A_block_that_cant_show_its_source_is_swapped()
    {
        var r = TileAi.Parse("""{"design":{"size":"small","rows":[{"cells":[{"type":"ring","bind":"pc.top_cpu"},{"type":"chart","bind":"sys.wifi"}]}]}}""", null);
        Assert.Equal([BlockType.List, BlockType.Toggle], r.Design.Blocks.Select(b => b.Type));
    }

    [Fact]
    public void A_revision_keeps_the_designs_id_and_chat()
    {
        var current = TileStarters.Instantiate(TileStarters.ById("pulse")!) with { Chat = [new(true, "a dashboard", 1)] };
        var r = TileAi.Parse(Good, current);
        Assert.Equal(current.Id, r.Design.Id);
        Assert.Single(r.Design.Chat);
    }

    [Fact]
    public async Task Repairs_once_and_keeps_the_better_answer()
    {
        var backend = new ScriptedBackend(
            """{"reply":"ok","design":{"size":"mini","rows":[{"cells":[{"type":"metric","bind":"gpu.temp"}]}]}}""",
            """{"reply":"Fixed","design":{"size":"mini","rows":[{"cells":[{"type":"metric","bind":"ps.value","cmd":"(Get-Date).Second"}]}]}}""");
        var steps = new List<string>();
        var r = await TileAi.DraftAsync(backend, "my gpu temperature", [], Context, null, p => steps.Add(p.Step), TestContext.Current.CancellationToken);
        Assert.Equal("Fixed", r.Reply);
        Assert.Equal("ps.value", r.Design.Blocks[0].Bind);
        Assert.Equal(2, backend.Seen.Count);
        Assert.Contains("gpu.temp", backend.Seen[1][^1].Content);
        Assert.Contains("Fixing a mistake", steps);
    }

    [Fact]
    public async Task Garbage_twice_is_an_error_people_can_read()
    {
        var backend = new ScriptedBackend("I can't build widgets.");
        var ex = await Assert.ThrowsAsync<AiException>(() => TileAi.DraftAsync(backend, "x", [], Context, null, null, TestContext.Current.CancellationToken));
        Assert.Contains("wasn't a design", ex.Message);
    }

    [Fact]
    public void The_prompt_teaches_every_source_size_and_block()
    {
        var (system, user) = TileAi.BuildPrompt("weather in Bergen", null, [new("weather.now", false, "Your town")], Context);
        foreach (var s in TileSources.All.Where(s => !s.Hidden)) Assert.Contains(s.Key, system);
        Assert.DoesNotContain("custom.state", system);
        foreach (var size in TileSize.All) Assert.Contains(size.Id + " ", system);
        foreach (var t in Enum.GetValues<BlockType>()) Assert.Contains("- " + BlockTypes.Id(t), system);
        Assert.Contains("[not set up yet: Your town", system);
        Assert.Contains("Oslo", system);
        Assert.Contains("weather in Bergen", user);
    }

    [Theory]
    [InlineData("""{"reply": "Here""", "Writing what it made")]
    [InlineData("""{"reply": "x", "design": {"name": "Rain check", "si""", "Designing “Rain check”")]
    [InlineData("""{"design": {"rows": [{"cells": [{"type": "ring", "bind": "pc.cpu"}""", "Adding a ring · CPU")]
    [InlineData("""{"design": {"rows": [{"cells": [{"type": "ai", "label": "Headline"}""", "Adding an AI line · Headline")]
    public void Describes_a_streaming_answer(string partial, string expected) => Assert.Equal(expected, TileAi.Describe(partial));
}

public class TileMigrationTests
{
    [Fact]
    public void Widgets_from_0_4_become_designs()
    {
        var designs = TileMigration.FromWidgetsJson("""
            [
              {"title":"Wi-Fi","icon":"wifi","size":"2x1","controls":[{"type":"toggle","label":"Wi-Fi","state":{"type":"radio.get","kind":"wifi"},"onValue":"on","on":{"type":"radio.set","kind":"wifi","state":"on"},"off":{"type":"radio.set","kind":"wifi","state":"off"}}]},
              {"title":"Volume","size":"4x1","controls":[{"type":"slider","label":"Volume","state":{"type":"audio.getVolume"},"action":{"type":"audio.setVolume","level":"{value}"}}]},
              {"title":"Resolution","size":"4x1","controls":[{"type":"choice","state":{"type":"display.getResolution","display":"internal"},"options":[{"label":"2560 × 1440","value":"2560x1440","action":{"type":"display.setResolution","width":2560,"height":1440}},{"label":"1920 × 1200","value":"1920x1200","action":{"type":"display.setResolution","width":1920,"height":1200}}]}]},
              {"title":"Snip","icon":"screenshot_region","size":"1x1","controls":[{"type":"button","label":"Snip","action":{"type":"keys.send","keys":"Win+Shift+S"}}]},
              {"title":"VPN","size":"2x1","controls":[{"type":"toggle","label":"VPN","state":{"type":"shell.powershell","script":"1"},"on":{"type":"shell.powershell","script":"on"},"off":{"type":"shell.powershell","script":"off"}}]},
              {"title":"Dashboard","size":"4x2","prompt":"cpu dashboard","layout":{"type":"text","text":"x"},"controls":[]}
            ]
            """);
        Assert.Equal(6, designs.Count);
        Assert.Equal(("sys.wifi", BlockType.Toggle, TileSize.Mini), (designs[0].Blocks[0].Bind, designs[0].Blocks[0].Type, designs[0].Size));
        Assert.Equal("sys.volume", designs[1].Blocks[0].Bind);
        var choice = designs[2].Blocks[0];
        Assert.Equal(("sys.resolution", "internal"), (choice.Bind, choice.Query));
        Assert.All(choice.Options, o => Assert.Null(o.Action));
        Assert.Equal(("keys:Win+Shift+S", TileSize.Icon), (designs[3].Blocks[0].Tap, designs[3].Size));
        var vpn = designs[4].Blocks[0];
        Assert.Equal("custom.state", vpn.Bind);
        Assert.NotNull(vpn.On);
        Assert.Equal(TileTap.Studio, designs[5].Blocks.Single(b => b.Type == BlockType.Button).Tap);
        Assert.Equal("cpu dashboard", designs[5].Chat[0].Text);
    }
}

public class TilePaletteTests
{
    [Fact]
    public void Every_palette_has_every_role_in_both_modes()
    {
        foreach (var p in TilePalette.All)
        {
            foreach (var dark in new[] { false, true })
            {
                foreach (var role in Enum.GetValues<TileRole>()) _ = p.Color(role, dark, 0x81A1C1);
            }
        }
    }

    [Theory]
    [InlineData(0x81A1C1u)]
    [InlineData(0xF1B467u)]
    [InlineData(0x0078D4u)]
    [InlineData(0x000000u)]
    public void The_accent_scheme_keeps_text_readable(uint accent)
    {
        foreach (var dark in new[] { false, true })
        {
            double C(TileRole a, TileRole b) => Contrast(TilePalette.Accent.Color(a, dark, accent), TilePalette.Accent.Color(b, dark, accent));
            Assert.True(C(TileRole.OnPrimary, TileRole.Primary) >= 4, $"on primary {dark}");
            Assert.True(C(TileRole.OnPrimaryContainer, TileRole.PrimaryContainer) >= 4.5, $"on container {dark}");
            Assert.True(C(TileRole.OnSurface, TileRole.SurfaceContainerHighest) >= 7, $"on surface {dark}");
        }
    }

    private static double Contrast(uint a, uint b)
    {
        static double L(uint c)
        {
            double Ch(uint v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
            return 0.2126 * Ch((c >> 16) & 0xFF) + 0.7152 * Ch((c >> 8) & 0xFF) + 0.0722 * Ch(c & 0xFF);
        }
        var (x, y) = (L(a), L(b));
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }
}

public class TileReadingTests
{
    [Theory]
    [InlineData("42", 42.0, "42")]
    [InlineData("87%", 87.0, "87%")]
    [InlineData("""{"text":"Online","number":3,"sub":"3 peers","active":true}""", 3.0, "Online")]
    public void Script_output_becomes_a_value(string output, double number, string text)
    {
        var v = TileOutput.Parse(output);
        Assert.Equal(number, v.Number);
        Assert.Equal(text, v.Display);
    }

    [Fact]
    public void A_json_array_becomes_rows()
    {
        var v = TileOutput.Parse("""[{"name":"nginx","status":"up"},{"title":"db","sub":"postgres","trailing":"down"}]""");
        Assert.Equal(["nginx", "db"], v.Items.Select(i => i.Title));
        Assert.Equal("up", v.Items[0].Trailing);
    }

    [Fact]
    public async Task Reads_this_pc_and_keeps_a_value_per_binding()
    {
        var host = new FakeTileHost();
        var resolver = new TileResolver(host);
        var d = new TileDesign
        {
            Size = TileSize.Wide,
            Rows = [new TileRow([new TileBlock { Type = BlockType.Metric, Bind = "pc.cpu" }, new TileBlock { Type = BlockType.Metric, Bind = "pc.memory" }]),
                    new TileRow([new TileBlock { Type = BlockType.Countdown, Bind = "time.countdown", Text = "2099-01-01" }, new TileBlock { Type = BlockType.Metric, Bind = "weather.now" }])],
        };
        var values = await resolver.RefreshAsync(d, force: true, TestContext.Current.CancellationToken);
        Assert.InRange(values["pc.cpu"].Number!.Value, 0, 100);
        Assert.Equal(100, values["pc.memory"].Goal);
        Assert.True(values[TileSources.BindingId(d.Blocks[2])!].At > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        // no town set: the weather asks for one instead of failing
        Assert.Equal(TileValueState.Needs, values["weather.now"].State);
    }

    [Fact]
    public async Task Scripts_wait_for_approval()
    {
        var host = new FakeTileHost();
        var resolver = new TileResolver(host);
        var block = new TileBlock { Type = BlockType.Metric, Bind = "ps.value", Cmd = "Write-Output (6*7)" };
        var d = new TileDesign { Size = TileSize.Mini, Rows = [new TileRow([block])] };
        Assert.Equal(TileValueState.Needs, (await resolver.RefreshAsync(d, true, TestContext.Current.CancellationToken))[TileSources.BindingId(block)!].State);
        Assert.Single(TileAsks.Check(d, host));
        TileApprovals.ApproveCommand(host.Tiles, null, block.Cmd!);
        Assert.Empty(TileAsks.Check(d, host));
        Assert.Equal(42, (await resolver.RefreshAsync(d, true, TestContext.Current.CancellationToken))[TileSources.BindingId(block)!].Number);
    }

    [Fact]
    public async Task Counter_buttons_change_their_kept_value()
    {
        var host = new FakeTileHost();
        var resolver = new TileResolver(host);
        var controls = new TileControls(host, resolver);
        var key = "test" + Guid.NewGuid().ToString("N")[..6];
        var counter = new TileBlock { Type = BlockType.Metric, Bind = "store.value", Query = key };
        var d = new TileDesign { Size = TileSize.Mini, Rows = [new TileRow([counter])] };
        Assert.Equal(TapOutcome.Refresh, await controls.TapAsync($"store:{key}+1", null, null, TestContext.Current.CancellationToken));
        await controls.TapAsync($"store:{key}+1", null, null, TestContext.Current.CancellationToken);
        Assert.Equal(2, (await resolver.RefreshAsync(d, true, TestContext.Current.CancellationToken))[TileSources.BindingId(counter)!].Number);
        await controls.TapAsync($"store:{key}=0", null, null, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await resolver.RefreshAsync(d, true, TestContext.Current.CancellationToken))[TileSources.BindingId(counter)!].Number);
    }

    [Fact]
    public async Task Server_stats_come_from_one_script()
    {
        var host = new FakeTileHost
        {
            ServerConfigured = true,
            ServerOutput = "cpu1 cpu 100 0 100 800 0 0 0 0\ncpu2 cpu 150 0 150 900 0 0 0 0\nload 0.41 0.30 0.20\nmem 16000000 4000000\ndisk 100000000 70000000 30000000\nup 1036800\nhost box\n",
        };
        var resolver = new TileResolver(host);
        var d = new TileDesign
        {
            Size = TileSize.Wide,
            Rows = [new TileRow([new TileBlock { Type = BlockType.Ring, Bind = "server.cpu" }, new TileBlock { Type = BlockType.Ring, Bind = "server.mem" }, new TileBlock { Type = BlockType.Metric, Bind = "server.status" }])],
        };
        var values = await resolver.RefreshAsync(d, true, TestContext.Current.CancellationToken);
        Assert.Equal(50, values["server.cpu"].Number);
        Assert.Equal(75, values["server.mem"].Number);
        Assert.Equal("Online", values["server.status"].Text);
        Assert.Contains("12d", values["server.status"].Sub);
    }
}

public class TileLibraryTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), "tilesmith-lib-" + Guid.NewGuid().ToString("N"), "designs.json");

    [Fact]
    public void Starts_with_quick_settings_and_saves_every_change()
    {
        var file = TempFile();
        var lib = new TileLibrary(file);
        lib.Load(TileEnvironment.Everything);
        Assert.Equal(["Quick settings", "Volume", "Brightness", "Shortcuts"], lib.Panel.Select(d => d.Name));

        var pulse = TileStarters.Instantiate(TileStarters.ById("pulse")!);
        lib.Place(pulse, 0);
        var copy = lib.Duplicate(pulse);
        lib.Unplace(lib.Panel[^1].Id);
        lib.Reorder(lib.Panel.Select(d => d.Id).Reverse().ToList());

        var again = new TileLibrary(file);
        again.Load(TileEnvironment.Everything);
        Assert.Equal(lib.Panel.Select(d => d.Id), again.Panel.Select(d => d.Id));
        Assert.Equal(6, again.Designs.Count); // taking a widget off the panel keeps its design
        Assert.Contains(again.Panel, d => d.Id == copy.Id);
    }

    [Fact]
    public void Converts_widgets_json_on_first_start()
    {
        var file = TempFile();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(file)!, "widgets.json"),
            """[{"title":"Lock","icon":"lock","size":"1x1","controls":[{"type":"button","label":"Lock","action":{"type":"power.action","action":"lock"}}]}]""");
        var lib = new TileLibrary(file);
        lib.Load(TileEnvironment.Everything);
        var d = Assert.Single(lib.Panel);
        Assert.Equal("power:lock", d.Blocks[0].Tap);
        Assert.True(File.Exists(file));
    }
}
