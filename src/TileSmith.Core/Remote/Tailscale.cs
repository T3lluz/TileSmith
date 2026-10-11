using System.Diagnostics;
using System.Text.Json;

namespace TileSmith.Core.Remote;

public sealed record TailscalePeer(string Name, string DnsName, string? Ip, string Os, bool Online)
{
    /// <summary>Best address to type into "user@host": the MagicDNS short name, else the 100.x IP.</summary>
    public string Address => !string.IsNullOrEmpty(DnsName) ? DnsName.Split('.')[0] : Ip ?? Name;
}

public sealed record TailscaleStatus(bool Installed, bool Running, string? Self, string? Tailnet, IReadOnlyList<TailscalePeer> Peers);

/// <summary>Reads `tailscale status --json` so servers can be picked from the tailnet.</summary>
public static class Tailscale
{
    public static string? FindCli()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Tailscale", "tailscale.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<TailscaleStatus> GetStatusAsync(CancellationToken ct = default)
    {
        var cli = FindCli();
        if (cli is null) return new TailscaleStatus(false, false, null, null, []);
        try
        {
            var psi = new ProcessStartInfo(cli, "status --json")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi)!;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var json = await p.StandardOutput.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var running = root.TryGetProperty("BackendState", out var state) && state.GetString() == "Running";
            string? self = root.TryGetProperty("Self", out var s) && s.TryGetProperty("HostName", out var h) ? h.GetString() : null;
            string? tailnet = root.TryGetProperty("CurrentTailnet", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("Name", out var tn) ? tn.GetString() : null;
            var peers = new List<TailscalePeer>();
            if (root.TryGetProperty("Peer", out var peerMap) && peerMap.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in peerMap.EnumerateObject())
                {
                    var peer = prop.Value;
                    string Get(string name) => peer.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                    var ip = peer.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array
                        ? ips.EnumerateArray().Select(i => i.GetString()).FirstOrDefault(i => i is not null && !i.Contains(':'))
                        : null;
                    var online = peer.TryGetProperty("Online", out var o) && o.ValueKind == JsonValueKind.True;
                    peers.Add(new TailscalePeer(Get("HostName"), Get("DNSName").TrimEnd('.'), ip, Get("OS"), online));
                }
            }
            var ordered = peers
                .OrderByDescending(x => x.Online)
                .ThenByDescending(x => x.Os.Equals("linux", StringComparison.OrdinalIgnoreCase))
                .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new TailscaleStatus(true, running, self, tailnet, ordered);
        }
        catch (Exception ex)
        {
            Log.Warn($"tailscale status failed: {ex.Message}");
            return new TailscaleStatus(true, false, null, null, []);
        }
    }
}
