using TileSmith.Core.Ai;
using TileSmith.Core.Settings;
using TileSmith.Core.Tiles;
using Xunit;

namespace TileSmith.Core.Tests;

/// <summary>
/// Asks a real AI for designs. Opt-in: set TILESMITH_IT_AI to a provider id that works on this PC without settings
/// (claude or cursor: their CLIs sign in themselves). Skipped otherwise.
/// </summary>
public class AiIntegrationTests
{
    private static IAiBackend? Backend()
    {
        var provider = Environment.GetEnvironmentVariable("TILESMITH_IT_AI");
        if (string.IsNullOrWhiteSpace(provider)) return null;
        return AiEngineFactory.CreateSingle(provider.Trim(), new AppSettings(), () => throw new InvalidOperationException("no server"));
    }

    private static readonly TileAi.Context Context = new(DeviceContext.Collect(), "Oslo", false, null);

    /// <summary>Where the designs go when TILESMITH_IT_AI_OUT names a file (to look at them in the app's --capture).</summary>
    private static readonly List<TileDesign> Made = [];

    private static void Print(string what, TileAi.Result r)
    {
        Made.Add(r.Design);
        if (Environment.GetEnvironmentVariable("TILESMITH_IT_AI_OUT") is { Length: > 0 } file) File.WriteAllText(file, TileCodec.EncodeList(Made));
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"── {what}: {r.Reply}");
        output?.WriteLine("suggestions: " + string.Join(" | ", r.Suggestions));
        output?.WriteLine(TileCodec.ToAiJson(r.Design).ToJsonString(JsonDefaults.Options));
    }

    [Fact]
    public async Task Designs_and_revises_widgets_with_a_real_model()
    {
        if (Backend() is not { } backend) { Assert.Skip("Set TILESMITH_IT_AI (claude or cursor) to run."); return; }
        var ct = TestContext.Current.CancellationToken;

        var dashboard = await TileAi.DraftAsync(backend, "A dashboard with CPU and memory as rings and a CPU graph", [], Context, null, null, ct);
        Print("dashboard", dashboard);
        Assert.Empty(dashboard.Unknown);
        Assert.Contains(dashboard.Design.Blocks, b => b.Bind == "pc.cpu" || b.Bind == "pc.cpu_history");
        Assert.All(dashboard.Design.Blocks.Where(b => b.Bind is not null), b => Assert.True(TileSources.Fits(b.Type, TileSources.ByKey(b.Bind)!)));

        var withChat = dashboard.Design with { Chat = [new(true, "A dashboard with CPU and memory as rings and a CPU graph", 1), new(false, dashboard.Reply, 2)] };
        var compact = await TileAi.DraftAsync(backend, "Make it a mini tile with just the CPU ring and the graph", [], Context, withChat, null, ct);
        Print("compact", compact);
        Assert.Equal(dashboard.Design.Id, compact.Design.Id);
        Assert.True(compact.Design.Size.MaxBlocks <= dashboard.Design.Size.MaxBlocks);

        var gpu = await TileAi.DraftAsync(backend, "My GPU temperature and load", [], Context, null, null, ct);
        Print("gpu", gpu);
        Assert.Empty(gpu.Unknown);

        var toggles = await TileAi.DraftAsync(backend, "Switches for Wi-Fi and dark mode, and the volume as a slider", [], Context, null, null, ct);
        Print("toggles", toggles);
        Assert.Contains(toggles.Design.Blocks, b => b.Type == BlockType.Toggle && b.Bind == "sys.wifi");
        Assert.Contains(toggles.Design.Blocks, b => b.Type == BlockType.Slider && b.Bind == "sys.volume");
    }
}
