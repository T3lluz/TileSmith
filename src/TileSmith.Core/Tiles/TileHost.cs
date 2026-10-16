using TileSmith.Core.Actions;
using TileSmith.Core.Ai;
using TileSmith.Core.Settings;

namespace TileSmith.Core.Tiles;

/// <summary>What reading and running widgets needs from the app: actions, the server, settings and the AI.</summary>
public interface ITileHost
{
    ActionEngine Actions { get; }

    TileSettings Tiles { get; }

    bool ServerConfigured { get; }

    /// <summary>"t3lluz": the server's host name, for subtitles.</summary>
    string ServerName { get; }

    /// <summary>Runs a POSIX shell script on the server and returns stdout (throws when the server can't be reached).</summary>
    Task<string> RunOnServerAsync(string script, TimeSpan timeout, CancellationToken ct);

    /// <summary>An AI is set up and ready.</summary>
    bool AiReady { get; }

    /// <summary>The AI for AI lines and the designer (with fallback), or an exception saying what to set up.</summary>
    IAiBackend CreateAi();
}

/// <summary>The scripts and sites a person allowed widgets to use (kept in settings).</summary>
public static class TileApprovals
{
    public static string CommandKey(string? server, string cmd) => TileSources.Hash((server ?? "") + "\u0000" + cmd.Trim());

    /// <summary>Where a block's command runs: "server" (or the server it names) for server.cmd, null for this PC.</summary>
    public static string? ScopeOf(TileBlock b) => TileSources.Canonical(b.Bind) == "server.cmd" ? b.Server ?? "server" : null;

    public static bool IsCommandApproved(TileSettings s, string? server, string? cmd) =>
        !string.IsNullOrWhiteSpace(cmd) && s.ApprovedCommands.Contains(CommandKey(server, cmd));

    public static void ApproveCommand(TileSettings s, string? server, string cmd)
    {
        var key = CommandKey(server, cmd);
        if (!s.ApprovedCommands.Contains(key)) s.ApprovedCommands.Add(key);
    }

    public static string? SiteOf(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp) ? u.Host.ToLowerInvariant() : null;

    public static bool IsSiteApproved(TileSettings s, string? url) => SiteOf(url) is { } host && s.ApprovedSites.Contains(host, StringComparer.OrdinalIgnoreCase);

    public static void ApproveSite(TileSettings s, string url)
    {
        if (SiteOf(url) is { } host && !s.ApprovedSites.Contains(host, StringComparer.OrdinalIgnoreCase)) s.ApprovedSites.Add(host);
    }
}

/// <summary>Something a design still needs before all of it works, as the Studio shows it.</summary>
public sealed record TileAsk(TileNeed Need, string Title, string? Detail = null, string? Cmd = null, string? Url = null, string? Server = null);

public static class TileAsks
{
    /// <summary>What <paramref name="d"/> still needs on this PC, once each.</summary>
    public static IReadOnlyList<TileAsk> Check(TileDesign d, ITileHost host)
    {
        var asks = new List<TileAsk>();
        var s = host.Tiles;
        foreach (var b in d.Blocks)
        {
            if (TileSources.ByKey(b.Bind) is not { } src) continue;
            foreach (var need in src.Needs)
            {
                TileAsk? ask = need switch
                {
                    TileNeed.Location when string.IsNullOrWhiteSpace(b.Query) && string.IsNullOrWhiteSpace(s.HomeTown) =>
                        new TileAsk(need, "Set your town", "The weather needs to know where you are."),
                    TileNeed.Server when !host.ServerConfigured => new TileAsk(need, "Set up your server", "Server widgets read it over SSH."),
                    TileNeed.Ai when !host.AiReady => new TileAsk(need, "Pick an AI", "AI lines ask your AI on each refresh."),
                    TileNeed.Command when !string.IsNullOrWhiteSpace(b.Cmd) && !TileApprovals.IsCommandApproved(s, TileApprovals.ScopeOf(b), b.Cmd) =>
                        new TileAsk(need, src.Key == "server.cmd" ? "Allow the server command" : "Allow the script", b.Cmd, Cmd: b.Cmd, Server: TileApprovals.ScopeOf(b)),
                    TileNeed.Site when TileApprovals.SiteOf(b.Url) is { } site && !TileApprovals.IsSiteApproved(s, b.Url) =>
                        new TileAsk(need, "Read " + site, b.Url, Url: b.Url),
                    _ => null,
                };
                if (ask is not null && !asks.Any(a => a.Need == ask.Need && a.Cmd == ask.Cmd && TileApprovals.SiteOf(a.Url) == TileApprovals.SiteOf(ask.Url))) asks.Add(ask);
            }
        }
        return asks;
    }
}
