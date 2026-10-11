using TileSmith.Core.Ai;
using TileSmith.Core.Remote;
using TileSmith.Core.Settings;
using Xunit;

namespace TileSmith.Core.Tests;

/// <summary>
/// Talks to a real server. Opt-in: set TILESMITH_IT_SERVER=user@host (and optionally TILESMITH_IT_KEY=path to a
/// private key, default ~/.ssh/id_ed25519). Skipped otherwise.
/// </summary>
public class ServerIntegrationTests
{
    private static (ServerConnection Server, AppSettings Settings)? Connect()
    {
        var address = Environment.GetEnvironmentVariable("TILESMITH_IT_SERVER");
        if (string.IsNullOrWhiteSpace(address)) return null;
        var key = Environment.GetEnvironmentVariable("TILESMITH_IT_KEY")
                  ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "id_ed25519");
        var settings = new AppSettings { Server = { Address = address, AuthMethod = "key", KeyPath = key } };
        var store = new KnownHostsStore(Path.Combine(Path.GetTempPath(), $"tilesmith-it-{Guid.NewGuid():N}.json"));
        return (new ServerConnection(settings.Server, store), settings);
    }

    [Fact]
    public async Task Finds_hermes_lists_its_models_and_gets_a_reply()
    {
        if (Connect() is not { } c) { Assert.Skip("Set TILESMITH_IT_SERVER to run."); return; }
        using var server = c.Server;
        var ct = TestContext.Current.CancellationToken;

        var found = await EngineDetector.DetectAsync(server, ct);
        Assert.NotNull(found.HermesCommand);
        EngineDetector.Apply(found, c.Settings.Ai);

        var models = await EngineDetector.ListHermesModelsAsync(server, c.Settings.Ai, ct);
        Assert.NotEmpty(models);

        var backend = AiEngineFactory.CreateSingle("hermes", c.Settings, () => server);
        var reply = await backend.CompleteAsync(
            [AiMessage.System("You are a connectivity check."), AiMessage.User("Reply with exactly this JSON and nothing else: {\"ok\": true}")], ct);
        Assert.Contains("ok", reply, StringComparison.OrdinalIgnoreCase);
    }
}
