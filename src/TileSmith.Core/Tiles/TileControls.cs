using System.Globalization;
using System.Text.Json.Nodes;
using TileSmith.Core.Actions;
using TileSmith.Core.Native;

namespace TileSmith.Core.Tiles;

/// <summary>What a tap asked the UI to do itself.</summary>
public enum TapOutcome { Done, Refresh, Studio, Nothing }

/// <summary>
/// The doing half of widgets: toggles, sliders and choices change what their source reads (Wi-Fi, the volume, the
/// power mode…), buttons and taps run their target. Everything goes through TileSmith's action engine, so it is
/// logged and runs off the UI thread; the value is assumed at once and read again shortly after.
/// </summary>
public sealed class TileControls(ITileHost host, TileResolver resolver)
{
    private ActionEngine Actions => host.Actions;

    private static JsonObject A(string type, params (string Key, JsonNode? Value)[] args)
    {
        var o = new JsonObject { ["type"] = type };
        foreach (var (k, v) in args) o[k] = v;
        return o;
    }

    public async Task SetSwitchAsync(TileBlock b, bool on, CancellationToken ct = default)
    {
        resolver.Assume(b, v => v with { Active = on });
        var key = TileSources.Canonical(b.Bind);
        switch (key)
        {
            case "sys.wifi": await Actions.RunAsync(A("radio.set", ("kind", "wifi"), ("state", on ? "on" : "off")), null, ct); break;
            case "sys.bluetooth": await Actions.RunAsync(A("radio.set", ("kind", "bluetooth"), ("state", on ? "on" : "off")), null, ct); break;
            case "sys.dark_mode": await Actions.RunAsync(A("theme.set", ("mode", on ? "dark" : "light")), null, ct); break;
            case "sys.mute": await Actions.RunAsync(A("audio.setMute", ("mute", on ? "true" : "false")), null, ct); break;
            case "sys.mic": await Actions.RunAsync(A("audio.setMicMute", ("mute", on ? "false" : "true")), null, ct); break;
            case "sys.app":
            {
                var name = TileResolver.AppName(b.Query);
                if (name.Length == 0) throw new ActionException("Name the app first.");
                await Actions.RunAsync(on ? A("app.launch", ("path", b.Query!.Trim())) : A("app.close", ("name", name)), null, ct);
                break;
            }
            case "sys.vpn":
            {
                var name = b.Query?.Trim().Replace("'", "''") ?? "";
                var script = $"$n = '{name}'; if (-not $n) {{ $n = (Get-VpnConnection | Select-Object -First 1).Name }}; " +
                             (on ? "rasdial \"$n\" | Out-String" : "rasdial \"$n\" /disconnect | Out-String");
                await Actions.RunAsync(A("shell.powershell", ("script", script), ("timeoutSeconds", 30)), null, ct);
                break;
            }
            case "store.value":
                LocalStore.Set(StoreKey(b), on ? "true" : "false");
                break;
            default:
                if (b.On is not null || b.Off is not null)
                {
                    if ((on ? b.On : b.Off) is { } action) await Actions.RunAsync(action, on ? "true" : "false", ct);
                }
                else if (b.Action is not null)
                {
                    await Actions.RunAsync(b.Action, on ? "true" : "false", ct);
                }
                else
                {
                    throw new ActionException($"{b.Label ?? "This switch"} has nothing to switch.");
                }
                break;
        }
        await SettleAsync(b);
    }

    public async Task SetLevelAsync(TileBlock b, double value, CancellationToken ct = default)
    {
        resolver.Assume(b, v => v with { Number = value, Text = $"{Math.Round(value)}{(v.Unit == "%" ? "%" : "")}" });
        var level = (int)Math.Round(value);
        switch (TileSources.Canonical(b.Bind))
        {
            case "sys.volume": await Actions.RunAsync(A("audio.setVolume", ("level", level)), null, ct); break;
            case "sys.brightness": await Actions.RunAsync(A("display.setBrightness", ("level", level)), null, ct); break;
            case "store.value": LocalStore.Set(StoreKey(b), ValueText.FormatNumber(value)); break;
            default:
                if (b.Action is null) throw new ActionException($"{b.Label ?? "This slider"} has nothing to set.");
                await Actions.RunAsync(b.Action, ValueText.FormatNumber(value), ct);
                break;
        }
        resolver.Invalidate(b);
    }

    public async Task ChooseAsync(TileBlock b, TileOption option, CancellationToken ct = default)
    {
        resolver.Assume(b, v => v with { Selected = option.Value, Text = option.Label });
        if (option.Action is not null)
        {
            await Actions.RunAsync(option.Action, option.Value, ct);
            await SettleAsync(b);
            return;
        }
        switch (TileSources.Canonical(b.Bind))
        {
            case "sys.power_mode":
                await Actions.RunAsync(A("power.setMode", ("mode", option.Value)), null, ct);
                break;
            case "sys.resolution":
            {
                var parts = option.Value.ToLowerInvariant().Replace('×', 'x').Replace(" ", "").Split('x');
                if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h))
                    throw new ActionException($"\"{option.Value}\" isn't a resolution like 2560x1440.");
                await Actions.RunAsync(A("display.setResolution", ("width", w), ("height", h), ("display", Display(b))), null, ct);
                break;
            }
            case "sys.refresh_rate":
            {
                if (!int.TryParse(new string(option.Value.TakeWhile(char.IsDigit).ToArray()), out var hz))
                    throw new ActionException($"\"{option.Value}\" isn't a refresh rate.");
                await Actions.RunAsync(A("display.setRefreshRate", ("hz", hz), ("display", Display(b))), null, ct);
                break;
            }
            case "sys.projection":
                await Actions.RunAsync(A("display.project", ("mode", option.Value)), null, ct);
                break;
            case "sys.dark_mode":
                await Actions.RunAsync(A("theme.set", ("mode", option.Value.Contains("dark", StringComparison.OrdinalIgnoreCase) ? "dark" : "light")), null, ct);
                break;
            case "store.value":
                LocalStore.Set(StoreKey(b), option.Value);
                break;
            default:
                if (b.Action is null) throw new ActionException($"{b.Label ?? "This choice"} has nothing to set.");
                await Actions.RunAsync(b.Action, option.Value, ct);
                break;
        }
        await SettleAsync(b);
    }

    /// <summary>Runs a button's (or a tile's, or a row's) tap. Studio and refresh come back for the UI to do.</summary>
    public async Task<TapOutcome> TapAsync(string? tap, TileBlock? b, TileValue? value, CancellationToken ct = default)
    {
        if (b?.Action is not null && b.Type == BlockType.Button)
        {
            await Actions.RunAsync(b.Action, null, ct);
            return TapOutcome.Done;
        }
        var t = TileTap.Normalize(tap);
        if (t is null) return TapOutcome.Nothing;
        switch (t)
        {
            case TileTap.Refresh: return TapOutcome.Refresh;
            case TileTap.Studio: return TapOutcome.Studio;
            case TileTap.Open:
                var link = value?.Url ?? value?.Items.FirstOrDefault(i => i.Link is not null)?.Link;
                if (link is null) return TapOutcome.Nothing;
                await Actions.RunAsync(A("url.open", ("url", link)), null, ct);
                return TapOutcome.Done;
        }
        var colon = t.IndexOf(':');
        var scheme = t[..colon];
        var rest = t[(colon + 1)..];
        switch (scheme)
        {
            case "url": await Actions.RunAsync(A("url.open", ("url", rest)), null, ct); break;
            case "settings": await Actions.RunAsync(A("url.open", ("url", "ms-settings:" + rest)), null, ct); break;
            case "folder": await Actions.RunAsync(A("folder.open", ("path", rest)), null, ct); break;
            case "keys": await Actions.RunAsync(A("keys.send", ("keys", rest)), null, ct); break;
            case "copy": await Actions.RunAsync(A("clipboard.setText", ("text", rest)), null, ct); break;
            case "run":
            {
                var (path, args) = SplitCommand(rest);
                await Actions.RunAsync(args is null ? A("app.launch", ("path", path)) : A("app.launch", ("path", path), ("args", args)), null, ct);
                break;
            }
            case "media":
                await Actions.RunAsync(A("media.key", ("key", rest switch { "next" => "next", "prev" => "previous", _ => "playPause" })), null, ct);
                if (b is not null) resolver.Invalidate(b);
                break;
            case "power":
                await Actions.RunAsync(A("power.action", ("action", rest switch { "signout" => "signOut", "displayoff" => "displayOff", var x => x })), null, ct);
                break;
            case "store":
            {
                if (!TileTap.Store.TryParse(rest, out var s)) return TapOutcome.Nothing;
                switch (s.Op)
                {
                    case '+': LocalStore.Add(s.Key, s.Amount, null, null); break;
                    case '-': LocalStore.Add(s.Key, -s.Amount, null, null); break;
                    case '=': LocalStore.Set(s.Key, s.Text ?? ValueText.FormatNumber(s.Amount)); break;
                    default: LocalStore.Toggle(s.Key); break;
                }
                return TapOutcome.Refresh;
            }
            default:
                return TapOutcome.Nothing;
        }
        return TapOutcome.Done;
    }

    /// <summary>A row's own link (an article, a video): opened in the browser.</summary>
    public Task OpenLinkAsync(string link, CancellationToken ct = default) =>
        TileTap.IsWebLink(link) || link.Contains(':') ? Actions.RunAsync(A("url.open", ("url", link)), null, ct) : Task.CompletedTask;

    /// <summary>The player's buttons.</summary>
    public async Task MediaAsync(string command, TileBlock b, CancellationToken ct = default)
    {
        await Actions.RunAsync(A("media.key", ("key", command switch { "next" => "next", "prev" => "previous", _ => "playPause" })), null, ct);
        if (command is "toggle") resolver.Assume(b, v => v with { Active = !(v.Active ?? false) });
        resolver.Invalidate(b);
    }

    private static string StoreKey(TileBlock b) => string.IsNullOrWhiteSpace(b.Query) ? "value" : b.Query.Trim();

    private static string Display(TileBlock b) => string.IsNullOrWhiteSpace(b.Query) ? (TileResolver.DisplayFor(null).IsInternal ? "internal" : "primary") : b.Query.Trim();

    /// <summary>Windows takes a moment to report a change (radios especially): read again a little later.</summary>
    private async Task SettleAsync(TileBlock b)
    {
        await Task.Delay(350);
        resolver.Invalidate(b);
    }

    /// <summary>"code --new-window" → (code, --new-window); a path that exists as it is stays whole.</summary>
    internal static (string Path, string? Args) SplitCommand(string command)
    {
        var c = command.Trim();
        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            if (end > 0) return (c[1..end], c[(end + 1)..].Trim() is { Length: > 0 } a ? a : null);
        }
        if (File.Exists(Environment.ExpandEnvironmentVariables(c)) || c.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return (c, null);
        var space = c.IndexOf(' ');
        return space < 0 ? (c, null) : (c[..space], c[(space + 1)..].Trim());
    }

    internal static string Format(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
