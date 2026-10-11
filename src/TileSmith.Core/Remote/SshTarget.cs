using System.Text.RegularExpressions;

namespace TileSmith.Core.Remote;

/// <summary>Parses what people naturally type: "user@host", "user@100.64.0.1:2222", "ssh user@host -p 2222", "host".</summary>
public sealed partial record SshTarget(string? User, string Host, int Port = 22)
{
    public static bool TryParse(string? input, out SshTarget target, out string? error)
    {
        target = new SshTarget(null, "", 22);
        error = null;
        var text = (input ?? "").Trim();
        if (text.Length == 0)
        {
            error = "Enter an address like user@hostname or user@100.x.y.z.";
            return false;
        }

        if (text.StartsWith("ssh ", StringComparison.OrdinalIgnoreCase)) text = text[4..].Trim();
        if (text.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)) text = text[6..].TrimEnd('/');

        int? port = null;
        var portFlag = PortFlagRegex().Match(text);
        if (portFlag.Success)
        {
            port = int.Parse(portFlag.Groups[1].Value);
            text = text.Remove(portFlag.Index, portFlag.Length).Trim();
        }

        string? user = null;
        var at = text.LastIndexOf('@');
        if (at >= 0)
        {
            user = text[..at].Trim();
            text = text[(at + 1)..].Trim();
            if (user.Length == 0) user = null;
        }

        string host;
        if (text.StartsWith('['))
        {
            var close = text.IndexOf(']');
            if (close < 0) { error = "Missing ] in IPv6 address."; return false; }
            host = text[1..close];
            var rest = text[(close + 1)..];
            if (rest.StartsWith(':') && int.TryParse(rest[1..], out var p6)) port ??= p6;
        }
        else if (text.Count(c => c == ':') == 1)
        {
            var parts = text.Split(':');
            host = parts[0];
            if (!int.TryParse(parts[1], out var p)) { error = $"\"{parts[1]}\" isn't a valid port."; return false; }
            port ??= p;
        }
        else
        {
            host = text; // hostname, IPv4 or bare IPv6
        }

        host = host.Trim().TrimEnd('.');
        if (host.Length == 0 || host.Any(char.IsWhiteSpace))
        {
            error = "That doesn't look like a hostname or IP address.";
            return false;
        }
        if (port is < 1 or > 65535)
        {
            error = "Port must be between 1 and 65535.";
            return false;
        }

        target = new SshTarget(user, host, port ?? 22);
        return true;
    }

    public override string ToString() =>
        (User is null ? "" : User + "@") + (Host.Contains(':') ? $"[{Host}]" : Host) + (Port == 22 ? "" : $":{Port}");

    [GeneratedRegex(@"(?:^|\s)-p\s*(\d+)")]
    private static partial Regex PortFlagRegex();
}
