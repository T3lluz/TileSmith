using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using TileSmith.Core.Actions;
using TileSmith.Core.Ai;
using TileSmith.Core.Native;

namespace TileSmith.Core.Tiles;

/// <summary>
/// Reads designs' sources. Each one keeps its own pace (<see cref="TileRules.BindingMaxAgeSeconds"/>): a pass reads
/// only what is due and keeps the rest; two blocks reading the same thing (in any widget) share one read; a read
/// that fails keeps the last good value rather than blanking the tile. The AI lines go last, so they can see the rest.
/// Values are kept on disk, so the panel opens on the last ones it saw.
/// </summary>
public sealed partial class TileResolver(ITileHost host)
{
    private const double SlackSeconds = 0.6;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan AiTimeout = TimeSpan.FromSeconds(150);

    private readonly object _gate = new();
    private readonly Dictionary<string, TileValue> _values = Load();
    private readonly Dictionary<string, Task<TileValue>> _inflight = [];
    private bool _dirty;

    public static string CacheFile => Path.Combine(AppPaths.DataDir, "tile-values.json");

    /// <summary>Raised (on a worker thread) when a value changed, with its binding id.</summary>
    public event Action<string>? ValueChanged;

    /// <summary>What <paramref name="b"/> read last, if anything.</summary>
    public TileValue? Get(TileBlock b) => TileSources.BindingId(b) is { } id ? Get(id) : null;

    public TileValue? Get(string bindingId)
    {
        lock (_gate) return _values.GetValueOrDefault(bindingId);
    }

    /// <summary>The values <paramref name="d"/>'s blocks read last (no reading).</summary>
    public Dictionary<string, TileValue> Snapshot(TileDesign d)
    {
        var map = new Dictionary<string, TileValue>();
        lock (_gate)
        {
            foreach (var b in d.Blocks)
            {
                if (TileSources.BindingId(b) is { } id && _values.TryGetValue(id, out var v)) map[id] = v;
            }
        }
        return map;
    }

    /// <summary>Forgets a value so the next pass reads it (after a toggle or slider changed it).</summary>
    public void Invalidate(TileBlock b)
    {
        if (TileSources.BindingId(b) is not { } id) return;
        lock (_gate)
        {
            if (_values.TryGetValue(id, out var v)) _values[id] = v with { ReadAt = 0 };
        }
    }

    /// <summary>Puts a value in right away (a toggle shows its new state before Windows reports it).</summary>
    public void Assume(TileBlock b, Func<TileValue, TileValue> change)
    {
        if (TileSources.BindingId(b) is not { } id) return;
        lock (_gate)
        {
            if (_values.TryGetValue(id, out var v)) _values[id] = change(v) with { ReadAt = 0 };
        }
        ValueChanged?.Invoke(id);
    }

    /// <summary>Reads what is due for <paramref name="d"/> (everything when <paramref name="force"/>) and returns its values.</summary>
    public async Task<Dictionary<string, TileValue>> RefreshAsync(TileDesign d, bool force, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var due = new List<(string Id, TileBlock Block)>();
        foreach (var b in d.Blocks)
        {
            if (TileSources.BindingId(b) is not { } id || due.Any(x => x.Id == id)) continue;
            var prev = Get(id);
            var maxAge = TileRules.BindingMaxAgeSeconds(d, b) * 1000L;
            var stale = prev is null || prev.State != TileValueState.Ok || now - prev.ReadAt >= maxAge - SlackSeconds * 1000;
            // the AI is slow and may cost money: only a forced pass (or no answer yet) asks it again early
            if (IsAi(b) && !force && prev is { State: TileValueState.Ok } && now - prev.ReadAt < maxAge) stale = false;
            if (force || stale) due.Add((id, b));
        }
        if (due.Count > 0)
        {
            var (ai, rest) = (due.Where(x => IsAi(x.Block)).ToList(), due.Where(x => !IsAi(x.Block)).ToList());
            await Task.WhenAll(rest.Select(x => ReadSharedAsync(x.Id, x.Block, d, ct)));
            foreach (var x in ai)
            {
                ct.ThrowIfCancellationRequested();
                await ReadSharedAsync(x.Id, x.Block, d, ct);
            }
        }
        return Snapshot(d);
    }

    private static bool IsAi(TileBlock b) => TileSources.Canonical(b.Bind) is "ai.text" or "ai.list";

    /// <summary>One read per binding at a time: a second tile asking for the same thing waits for the first.</summary>
    private async Task<TileValue> ReadSharedAsync(string id, TileBlock b, TileDesign d, CancellationToken ct)
    {
        Task<TileValue> task;
        lock (_gate)
        {
            if (!_inflight.TryGetValue(id, out task!))
            {
                task = ReadAndStoreAsync(id, b, d);
                _inflight[id] = task;
            }
        }
        try
        {
            return await task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // the caller gave up; the read finishes for the next one
            return Get(id) ?? TileValue.Failed("Couldn't read this");
        }
    }

    private async Task<TileValue> ReadAndStoreAsync(string id, TileBlock b, TileDesign d)
    {
        var sw = Stopwatch.StartNew();
        TileValue fresh;
        try
        {
            using var cts = new CancellationTokenSource(IsAi(b) ? AiTimeout : ReadTimeout);
            var context = Snapshot(d);
            fresh = await Task.Run(() => ReadAsync(b, d, context, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            fresh = TileValue.Failed("Took too long");
        }
        catch (Exception ex)
        {
            fresh = TileValue.Failed(IsAi(b) ? AiFailure(ex) : Friendly(ex));
            Log.Warn($"Reading {b.Bind} failed: {ex.Message}");
        }
        finally
        {
            lock (_gate) _inflight.Remove(id);
        }
        if (sw.ElapsedMilliseconds > 3000) Log.Info($"{b.Bind} took {sw.ElapsedMilliseconds} ms");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        TileValue stored;
        var changed = false;
        lock (_gate)
        {
            var previous = _values.GetValueOrDefault(id);
            // a failed read keeps the last good value: the tile never goes blank for a blip
            stored = fresh.State == TileValueState.Error && previous is { State: TileValueState.Ok }
                ? previous with { ReadAt = now - 5_000 }
                : fresh with { ReadAt = now };
            changed = previous is null || !SameShown(previous, stored);
            _values[id] = stored;
            if (TileSources.FloorSeconds(b) >= 60) _dirty = true;
        }
        if (changed) ValueChanged?.Invoke(id);
        return stored;
    }

    private static bool SameShown(TileValue a, TileValue b) =>
        TileValueCodec.ToJson(a with { ReadAt = 0 }).ToJsonString() == TileValueCodec.ToJson(b with { ReadAt = 0 }).ToJsonString();

    private static string AiFailure(Exception ex) => ex switch
    {
        AiException { CanFallback: false } a when a.Message.Length <= 90 => a.Message,
        AiLoginRequiredException => "Sign in to your AI again",
        _ => "Your AI didn't answer",
    };

    private static string Friendly(Exception ex) => ex switch
    {
        ActionException a => a.Message.Length > 90 ? a.Message[..90] + "…" : a.Message,
        AiException a => a.Message.Length > 90 ? a.Message[..90] + "…" : a.Message,
        HttpRequestException h => h.StatusCode is { } code ? $"The site answered {(int)code}" : "Couldn't reach the site",
        _ => ex.Message.Length > 90 ? ex.Message[..90] + "…" : ex.Message,
    };

    // ── Disk ─────────────────────────────────────────────────────────────────────────

    private static Dictionary<string, TileValue> Load()
    {
        try
        {
            return File.Exists(CacheFile) ? TileValueCodec.ParseMap(File.ReadAllText(CacheFile)) : [];
        }
        catch (Exception ex)
        {
            Log.Warn($"Tile values unreadable: {ex.Message}");
            return [];
        }
    }

    /// <summary>Writes the values when something slow (weather, the web, AI lines) changed since the last save.</summary>
    public void Save(bool always = false)
    {
        string json;
        lock (_gate)
        {
            if (!_dirty && !always) return;
            _dirty = false;
            json = TileValueCodec.EncodeMap(_values.Where(kv => kv.Value.State == TileValueState.Ok).ToDictionary());
        }
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            AtomicFile.WriteAllText(CacheFile, json);
        }
        catch (Exception ex)
        {
            Log.Warn($"Saving tile values failed: {ex.Message}");
        }
    }

    // ── Reading one source ───────────────────────────────────────────────────────────

    private async Task<TileValue> ReadAsync(TileBlock b, TileDesign d, IReadOnlyDictionary<string, TileValue> context, CancellationToken ct)
    {
        var src = TileSources.ByKey(b.Bind) ?? throw new ActionException($"There's no source \"{b.Bind}\".");
        var key = src.Key;
        if (key.StartsWith("pc.", StringComparison.Ordinal)) return await ReadPcAsync(key, b, ct);
        if (key.StartsWith("sys.", StringComparison.Ordinal)) return await ReadSettingAsync(key, b, ct);
        if (key.StartsWith("net.", StringComparison.Ordinal)) return await ReadNetworkAsync(key, b, ct);
        if (key.StartsWith("weather.", StringComparison.Ordinal)) return await ReadWeatherAsync(key, b, ct);
        if (key.StartsWith("server.", StringComparison.Ordinal)) return await ReadServerAsync(key, b, ct);
        if (key.StartsWith("web.", StringComparison.Ordinal)) return await ReadWebAsync(key, b, ct);
        return key switch
        {
            "time.clock" => new TileValue { Text = DateTime.Now.ToString("t", CultureInfo.CurrentCulture) },
            "time.date" => new TileValue { Text = DateTime.Now.ToString("ddd d MMM", CultureInfo.CurrentCulture) },
            "time.countdown" => Countdown(b),
            "media.now" => await ReadMediaAsync(),
            "ai.text" or "ai.list" => await ReadAiAsync(key, b, d, context, ct),
            "store.value" => ReadStore(b),
            "ps.value" => await ReadScriptAsync(b, ct),
            "custom.state" => await ReadCustomAsync(b, ct),
            _ => throw new ActionException($"Nothing reads \"{key}\" yet."),
        };
    }

    private static TileValue Countdown(TileBlock b)
    {
        var text = b.Text?.Trim();
        if (string.IsNullOrEmpty(text)) return TileValue.Needs("Set a date");
        DateTime target;
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var timeOfDay) && !text.Contains('-') && timeOfDay < TimeSpan.FromDays(1))
        {
            target = DateTime.Today + timeOfDay;
            if (target < DateTime.Now) target = target.AddDays(1);
        }
        else if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out target))
        {
            return TileValue.Failed($"\"{text}\" isn't a date");
        }
        var time = target.ToString("t", CultureInfo.CurrentCulture);
        var sub = target.Date == DateTime.Today ? time
                : target.TimeOfDay == TimeSpan.Zero ? target.ToString("ddd d MMM", CultureInfo.CurrentCulture)
                : target.ToString("ddd d MMM", CultureInfo.CurrentCulture) + ", " + time;
        return new TileValue { At = new DateTimeOffset(target).ToUnixTimeMilliseconds(), Sub = sub };
    }

    // ── This PC ──────────────────────────────────────────────────────────────────────

    private static async Task<TileValue> ReadPcAsync(string key, TileBlock b, CancellationToken ct)
    {
        switch (key)
        {
            case "pc.cpu":
            {
                var cpu = PcHistory.Sample().Cpu;
                return Percent(cpu, $"{Environment.ProcessorCount} threads");
            }
            case "pc.memory":
            {
                var (total, available) = SystemStats.Memory();
                PcHistory.Sample();
                var used = total - available;
                return Percent(100.0 * used / total, $"{TileValue.FormatBytes(used)} of {TileValue.FormatBytes(total)}");
            }
            case "pc.disk":
            {
                var drive = SystemStats.Drive(b.Query);
                var used = drive.TotalSize - drive.AvailableFreeSpace;
                return Percent(100.0 * used / drive.TotalSize, $"{drive.Name.TrimEnd('\\')} · {TileValue.FormatBytes(drive.AvailableFreeSpace)} free");
            }
            case "pc.uptime":
            {
                var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
                var since = DateTime.Now - up;
                return new TileValue
                {
                    Text = TileValue.FormatDuration(up),
                    Number = Math.Round(up.TotalHours, 1),
                    Unit = "h",
                    Sub = "Since " + (since.Date == DateTime.Today ? since.ToString("t", CultureInfo.CurrentCulture) : since.ToString("ddd", CultureInfo.CurrentCulture)),
                };
            }
            case "pc.battery":
            {
                var (percent, status, hasBattery) = PowerControl.GetBattery();
                if (!hasBattery) return TileValue.EmptyValue("No battery");
                var charging = status is "Charging" or "Plugged in";
                var icon = charging ? "battery_charging_full" : percent >= 95 ? "battery_full" : $"battery_{Math.Clamp((int)Math.Round(percent / 100.0 * 6), 0, 6)}_bar";
                return Percent(percent, status) with { Icon = icon, Active = charging };
            }
            case "pc.cpu_history":
            {
                var s = PcHistory.Sample();
                return History(PcHistory.Cpu, $"{Math.Round(s.Cpu)}%", s.Cpu, "%");
            }
            case "pc.memory_history":
            {
                var s = PcHistory.Sample();
                return History(PcHistory.Memory, $"{Math.Round(s.Memory)}%", s.Memory, "%");
            }
            case "pc.top_cpu" or "pc.top_memory":
            {
                var byCpu = key == "pc.top_cpu";
                var json = await TopProcesses.GetAsync(byCpu ? "cpu" : "memory", Math.Clamp(b.Max ?? 5, 1, 15), ct);
                var rows = JsonNode.Parse(json) as JsonArray ?? [];
                var items = rows.OfType<JsonObject>().Select(r =>
                {
                    var name = Pretty(JsonPath.Text(r["name"]) ?? "?");
                    var memory = TileValue.FormatBytes(JsonPath.Number(r["memory"]) ?? 0);
                    var count = (int)(JsonPath.Number(r["count"]) ?? 1);
                    return byCpu
                        ? new TileItem(name, memory, $"{(JsonPath.Number(r["cpu"]) ?? 0).ToString("0.#", CultureInfo.CurrentCulture)}%")
                        : new TileItem(name, count == 1 ? "1 process" : $"{count} processes", memory);
                }).ToList();
                return new TileValue { Items = items, Number = items.Count };
            }
        }
        throw new ActionException($"Nothing reads \"{key}\" yet.");
    }

    private static TileValue Percent(double value, string? sub) => new()
    {
        Text = $"{Math.Round(value).ToString(CultureInfo.CurrentCulture)}%",
        Number = Math.Round(value, 1),
        Goal = 100,
        Unit = "%",
        Sub = sub,
    };

    private static TileValue History(IReadOnlyList<double> series, string text, double now, string? unit) => new()
    {
        Series = series,
        Text = text,
        Number = Math.Round(now, 1),
        Unit = unit,
        SeriesStart = series.Count >= 2 ? Ago(series.Count * PcHistory.IntervalSeconds) : null,
        SeriesEnd = "Now",
    };

    private static string Ago(double seconds) => seconds >= 90 ? $"{Math.Round(seconds / 60)} min" : $"{Math.Round(seconds)} s";

    /// <summary>"msedge" → "Msedge"; well-known process names get their app's name.</summary>
    private static string Pretty(string process) => process.ToLowerInvariant() switch
    {
        "msedge" => "Edge",
        "chrome" => "Chrome",
        "firefox" => "Firefox",
        "code" => "VS Code",
        "explorer" => "Explorer",
        "ms-teams" or "teams" => "Teams",
        "spotify" => "Spotify",
        "discord" => "Discord",
        "steam" or "steamwebhelper" => "Steam",
        "obs64" => "OBS",
        "devenv" => "Visual Studio",
        "msmpeng" => "Defender",
        "svchost" => "Windows services",
        "dwm" => "Desktop Window Manager",
        "searchhost" => "Search",
        _ => process.Length > 0 ? char.ToUpperInvariant(process[0]) + process[1..] : process,
    };

    // ── Quick settings ───────────────────────────────────────────────────────────────

    private static (string Ssid, DateTime At) _ssid = ("", DateTime.MinValue);

    private static async Task<TileValue> ReadSettingAsync(string key, TileBlock b, CancellationToken ct)
    {
        switch (key)
        {
            case "sys.wifi":
            {
                bool on;
                try { on = await RadioControl.GetAsync("wifi"); }
                catch (ActionException ex) when (ex.Message.Contains("No Wi-Fi", StringComparison.Ordinal)) { return TileValue.EmptyValue("No Wi-Fi"); }
                var ssid = on ? await SsidAsync(ct) : "";
                return new TileValue
                {
                    Active = on,
                    Text = on ? (ssid.Length > 0 ? ssid : "On") : "Off",
                    Sub = on ? (ssid.Length > 0 ? "Connected" : "Not connected") : null,
                    Icon = on ? "wifi" : "wifi_off",
                };
            }
            case "sys.bluetooth":
            {
                bool on;
                try { on = await RadioControl.GetAsync("bluetooth"); }
                catch (ActionException ex) when (ex.Message.Contains("No Bluetooth", StringComparison.Ordinal)) { return TileValue.EmptyValue("No Bluetooth"); }
                return new TileValue { Active = on, Text = on ? "On" : "Off", Icon = on ? "bluetooth" : "bluetooth_disabled" };
            }
            case "sys.dark_mode":
            {
                var dark = ThemeControl.IsDark();
                return new TileValue { Active = dark, Text = dark ? "Dark" : "Light", Icon = dark ? "dark_mode" : "light_mode" };
            }
            case "sys.mute":
            {
                var muted = AudioEndpoint.GetMute();
                return new TileValue { Active = muted, Text = muted ? "Muted" : "Sound on", Icon = muted ? "volume_off" : "volume_up" };
            }
            case "sys.mic":
            {
                bool muted;
                try { muted = AudioEndpoint.GetMute(microphone: true); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return TileValue.EmptyValue("No microphone"); }
                return new TileValue { Active = !muted, Text = muted ? "Muted" : "On", Icon = muted ? "mic_off" : "mic" };
            }
            case "sys.volume":
            {
                var v = AudioEndpoint.GetVolume();
                var muted = AudioEndpoint.GetMute();
                return Percent(v, muted ? "Muted" : null) with
                {
                    Min = 0,
                    Max = 100,
                    Active = !muted,
                    Icon = muted || v == 0 ? "volume_off" : v < 34 ? "volume_mute" : v < 67 ? "volume_down" : "volume_up",
                };
            }
            case "sys.brightness":
            {
                int level;
                try { level = BrightnessControl.Get(); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return TileValue.EmptyValue("No brightness control"); }
                return Percent(level, null) with { Min = 0, Max = 100, Icon = level < 34 ? "brightness_low" : level < 67 ? "brightness_medium" : "brightness_high" };
            }
            case "sys.power_mode":
            {
                var mode = PowerControl.GetMode();
                return new TileValue
                {
                    Text = char.ToUpperInvariant(mode[0]) + mode[1..],
                    Selected = mode,
                    Options = [new("Efficiency", "efficiency", "eco"), new("Balanced", "balanced", "balance"), new("Performance", "performance", "bolt")],
                    Icon = mode switch { "efficiency" => "eco", "performance" => "bolt", _ => "balance" },
                };
            }
            case "sys.resolution":
            {
                var display = DisplayFor(b.Query);
                var current = display.Current;
                var options = display.ModesByResolution().Select(g => g.First()).Take(4)
                    .Select(m => new TileOption($"{m.Width} × {m.Height}", m.Resolution)).ToList();
                if (!options.Any(o => o.Value == current.Resolution)) options.Insert(0, new($"{current.Width} × {current.Height}", current.Resolution));
                return new TileValue
                {
                    Text = $"{current.Width} × {current.Height}",
                    Sub = display.IsInternal ? "Built-in display" : display.FriendlyName,
                    Selected = current.Resolution,
                    Options = options.Take(4).ToList(),
                };
            }
            case "sys.refresh_rate":
            {
                var display = DisplayFor(b.Query);
                var current = display.Current;
                var rates = display.Modes.Where(m => m.Width == current.Width && m.Height == current.Height)
                    .Select(m => m.RefreshRate).Distinct().OrderDescending().Take(4).ToList();
                if (!rates.Contains(current.RefreshRate)) rates.Insert(0, current.RefreshRate);
                return new TileValue
                {
                    Text = $"{current.RefreshRate} Hz",
                    Number = current.RefreshRate,
                    Unit = "Hz",
                    Sub = display.IsInternal ? "Built-in display" : display.FriendlyName,
                    Selected = current.RefreshRate.ToString(CultureInfo.InvariantCulture),
                    Options = rates.Take(4).Select(r => new TileOption($"{r} Hz", r.ToString(CultureInfo.InvariantCulture))).ToList(),
                };
            }
            case "sys.projection":
            {
                var count = DisplayManager.GetDisplays().Count;
                return new TileValue
                {
                    Text = count > 1 ? $"{count} displays" : "One display",
                    Options = [new("PC only", "pcScreenOnly"), new("Duplicate", "duplicate"), new("Extend", "extend"), new("Second", "secondScreenOnly")],
                    Selected = count > 1 ? "extend" : null,
                };
            }
            case "sys.vpn":
            {
                var json = await PowerShellHost.QueryAsync(
                    "@(Get-VpnConnection -ErrorAction SilentlyContinue | Select-Object Name, ConnectionStatus) | ConvertTo-Json -Compress", 10, ct);
                var list = JsonPath.TryParse(json, out var node) ? node switch { JsonArray a => a.OfType<JsonObject>().ToList(), JsonObject o => [o], _ => [] } : [];
                if (list.Count == 0) return TileValue.EmptyValue("No VPN set up in Windows");
                var vpn = list.FirstOrDefault(v => string.Equals(JsonPath.Text(v["Name"]), b.Query?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? list[0];
                var connected = string.Equals(JsonPath.Text(vpn["ConnectionStatus"]), "Connected", StringComparison.OrdinalIgnoreCase);
                return new TileValue { Text = JsonPath.Text(vpn["Name"]), Sub = connected ? "Connected" : "Not connected", Active = connected, Icon = connected ? "vpn_lock" : "vpn_key_off" };
            }
            case "sys.app":
            {
                var name = AppName(b.Query);
                if (name.Length == 0) return TileValue.Needs("Name the app");
                var running = Process.GetProcessesByName(name);
                foreach (var p in running) p.Dispose();
                return new TileValue { Text = Pretty(name), Sub = running.Length > 0 ? "Running" : "Not running", Active = running.Length > 0 };
            }
        }
        throw new ActionException($"Nothing reads \"{key}\" yet.");
    }

    internal static string AppName(string? query)
    {
        var q = query?.Trim().Trim('"') ?? "";
        if (q.Length == 0) return "";
        var file = Path.GetFileName(q);
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
    }

    internal static DisplayInfo DisplayFor(string? query)
    {
        if (!string.IsNullOrWhiteSpace(query)) return DisplayManager.Resolve(query);
        var displays = DisplayManager.GetDisplays();
        return displays.FirstOrDefault(d => d.IsInternal) ?? DisplayManager.Resolve("primary");
    }

    private static async Task<string> SsidAsync(CancellationToken ct)
    {
        if ((DateTime.UtcNow - _ssid.At).TotalSeconds < 30) return _ssid.Ssid;
        var json = JsonNode.Parse(await NetworkInfo.GetAsync(ct));
        var ssid = JsonPath.Text(json?["ssid"]) ?? "";
        _ssid = (ssid, DateTime.UtcNow);
        return ssid;
    }

    // ── Network ──────────────────────────────────────────────────────────────────────

    private static async Task<TileValue> ReadNetworkAsync(string key, TileBlock b, CancellationToken ct)
    {
        switch (key)
        {
            case "net.status" or "net.ip":
            {
                var json = JsonNode.Parse(await NetworkInfo.GetAsync(ct, wifi: key == "net.status"))!;
                var online = JsonPath.Text(json["online"]) == "true";
                var type = JsonPath.Text(json["type"]) ?? "";
                var ssid = JsonPath.Text(json["ssid"]) ?? "";
                var ip = JsonPath.Text(json["ip"]) ?? "";
                if (key == "net.ip") return ip.Length == 0 ? TileValue.EmptyValue("No address") : new TileValue { Text = ip, Sub = type.Length > 0 ? type : null };
                if (ssid.Length > 0) _ssid = (ssid, DateTime.UtcNow);
                var wifi = type == "Wi-Fi";
                var signal = JsonPath.Number(json["signal"]);
                return new TileValue
                {
                    Text = !online || type.Length == 0 ? "Offline" : wifi ? (ssid.Length > 0 ? ssid : "Wi-Fi") : type,
                    Sub = online && ip.Length > 0 ? ip : null,
                    Active = online && type.Length > 0,
                    Icon = !online ? "wifi_off" : wifi ? "wifi" : "lan",
                    Number = wifi && signal > 0 ? signal : null,
                    Goal = wifi && signal > 0 ? 100 : null,
                    Unit = wifi && signal > 0 ? "%" : null,
                };
            }
            case "net.down" or "net.up":
            {
                var s = PcHistory.Sample();
                var bps = key == "net.down" ? s.Down : s.Up;
                return new TileValue { Text = TileValue.FormatBytes(bps) + "/s", Number = Math.Round(bps), Sub = key == "net.down" ? "Download" : "Upload", Icon = key == "net.down" ? "download" : "upload" };
            }
            case "net.speed_history":
            {
                var s = PcHistory.Sample();
                return History(PcHistory.Down, TileValue.FormatBytes(s.Down) + "/s", s.Down, null);
            }
            case "net.ping":
            {
                var target = string.IsNullOrWhiteSpace(b.Query) ? "1.1.1.1" : b.Query.Trim();
                var result = await NetworkProbe.PingAsync(target, 1500);
                if (!double.TryParse(result, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                    return new TileValue { Text = "Offline", Sub = target, Active = false, Icon = "wifi_off" };
                return new TileValue { Text = $"{ms:0} ms", Number = ms, Unit = "ms", Sub = target, Active = true };
            }
        }
        throw new ActionException($"Nothing reads \"{key}\" yet.");
    }

    // ── Now playing ──────────────────────────────────────────────────────────────────

    private static async Task<TileValue> ReadMediaAsync()
    {
        var json = JsonNode.Parse(await NowPlaying.GetAsync(null))!;
        var status = JsonPath.Text(json["status"]) ?? "none";
        var title = JsonPath.Text(json["title"]) ?? "";
        if (status == "none" || title.Length == 0) return new TileValue { Text = null, Sub = null, Active = false, Note = "Nothing playing", Icon = "music_note" };
        var art = JsonPath.Text(json["art"]);
        var app = JsonPath.Text(json["app"]);
        var artist = JsonPath.Text(json["artist"]);
        var album = JsonPath.Text(json["album"]);
        // an app's name only when it reads as one (some players report an id like F0DC299D809B9700)
        var appName = app is { Length: > 1 } a && a.Any(char.IsLower) ? char.ToUpperInvariant(a[0]) + a[1..] : null;
        return new TileValue
        {
            Text = title,
            Sub = !string.IsNullOrWhiteSpace(artist) ? artist : !string.IsNullOrWhiteSpace(album) ? album : appName,
            Image = string.IsNullOrWhiteSpace(art) ? null : art,
            Active = JsonPath.Text(json["playing"]) == "true",
            Icon = "music_note",
            Number = JsonPath.Number(json["position"]),
            Goal = JsonPath.Number(json["duration"]) is > 0 and var d ? d : null,
        };
    }

    // ── Counters, scripts, actions ───────────────────────────────────────────────────

    private static TileValue ReadStore(TileBlock b)
    {
        var key = string.IsNullOrWhiteSpace(b.Query) ? "value" : b.Query.Trim();
        var raw = LocalStore.Get(key, null);
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || raw.Length == 0)
        {
            if (raw.Length == 0) n = 0;
            return new TileValue { Number = n, Text = TileValue.FormatNumber(n), Goal = b.Goal, Active = n != 0 };
        }
        var truthy = ValueText.IsTruthy(raw);
        return new TileValue { Text = raw, Active = truthy, Selected = raw };
    }

    private async Task<TileValue> ReadScriptAsync(TileBlock b, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(b.Cmd)) return TileValue.Needs("No script yet");
        if (!TileApprovals.IsCommandApproved(host.Tiles, null, b.Cmd)) return TileValue.Needs("Allow the script");
        var output = await PowerShellHost.QueryAsync(b.Cmd, 20, ct);
        return TileOutput.Parse(output);
    }

    private async Task<TileValue> ReadCustomAsync(TileBlock b, CancellationToken ct)
    {
        if (b.State is null) return TileValue.EmptyValue("No state");
        var output = (await host.Actions.QueryAsync(b.State, ct))?.Trim() ?? "";
        var on = b.OnValue is { Length: > 0 } onValue ? ValueText.Matches(onValue, output) : ValueText.IsTruthy(output);
        var value = TileOutput.Parse(output);
        return value with { Active = value.Active ?? on, Selected = output };
    }
}

/// <summary>Turns a script's or a command's output into a value: a number, JSON fields, rows, or a line of text.</summary>
public static class TileOutput
{
    public static TileValue Parse(string? output)
    {
        var text = output?.Trim() ?? "";
        if (text.Length == 0) return TileValue.EmptyValue("No output");
        if (JsonPath.TryParse(text, out var node))
        {
            if (node is JsonArray a) return new TileValue { Items = Rows(a), Number = a.Count };
            if (node is JsonObject o)
            {
                if ((o["items"] ?? o["rows"]) is JsonArray items) return FromObject(o) with { Items = Rows(items) };
                return FromObject(o);
            }
        }
        var first = text.Split('\n')[0].Trim();
        if (double.TryParse(first.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            var percent = first.EndsWith('%');
            return new TileValue { Number = n, Text = percent ? $"{TileValue.FormatNumber(n)}%" : TileValue.FormatNumber(n), Unit = percent ? "%" : null, Goal = percent ? 100 : null };
        }
        var number = System.Text.RegularExpressions.Regex.Match(first, @"-?\d+(?:\.\d+)?");
        var shown = text.Length > 300 ? text[..300] + "…" : text;
        return new TileValue
        {
            Text = shown,
            Number = number.Success && double.TryParse(number.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var inText) ? inText : null,
            Active = ValueText.IsTruthy(first) ? true : first.Equals("false", StringComparison.OrdinalIgnoreCase) || first.Equals("off", StringComparison.OrdinalIgnoreCase) ? false : null,
        };
    }

    private static TileValue FromObject(JsonObject o)
    {
        string? S(params string[] keys) => keys.Select(k => JsonPath.Text(JsonPath.Navigate(o, k))).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        var number = JsonPath.Number(o["number"] ?? o["value"]);
        var text = S("text", "title", "label", "name");
        var active = S("active", "on", "running");
        return new TileValue
        {
            Text = text ?? (number is { } n ? TileValue.FormatNumber(n) : null),
            Number = number,
            Goal = JsonPath.Number(o["goal"] ?? o["max"]),
            Unit = S("unit"),
            Sub = S("sub", "subtitle", "detail", "description"),
            Icon = S("icon"),
            Image = S("image", "img", "art"),
            Url = S("url", "link"),
            Active = active is null ? null : ValueText.IsTruthy(active),
        };
    }

    public static List<TileItem> Rows(JsonArray a) => a.Take(30).Select(n => n switch
    {
        JsonObject r => new TileItem(
            JsonPath.Text(r["title"] ?? r["name"] ?? r["label"] ?? r["text"]) ?? "",
            JsonPath.Text(r["sub"] ?? r["subtitle"] ?? r["detail"] ?? r["description"]),
            JsonPath.Text(r["trailing"] ?? r["value"] ?? r["status"] ?? r["time"]),
            JsonPath.Text(r["color"]),
            JsonPath.Text(r["image"] ?? r["img"] ?? r["thumbnail"]),
            JsonPath.Text(r["link"] ?? r["url"]),
            JsonPath.Text(r["icon"])),
        _ => new TileItem(JsonPath.Text(n) ?? ""),
    }).Where(r => r.Title.Length > 0).ToList();
}

/// <summary>
/// Recent CPU, memory and network speed, sampled as tiles read them (at most every two seconds) so history charts
/// fill while the panel is open and keep the last two minutes.
/// </summary>
public static class PcHistory
{
    public const int Points = 60;
    public const double IntervalSeconds = 2;

    private static readonly object Gate = new();
    private static readonly List<double> CpuList = [];
    private static readonly List<double> MemoryList = [];
    private static readonly List<double> DownList = [];
    private static (DateTime At, double Cpu, double Memory, double Down, double Up) _last;
    private static (long Down, long Up, DateTime At)? _bytes;

    public static IReadOnlyList<double> Cpu { get { lock (Gate) return CpuList.ToList(); } }
    public static IReadOnlyList<double> Memory { get { lock (Gate) return MemoryList.ToList(); } }
    public static IReadOnlyList<double> Down { get { lock (Gate) return DownList.ToList(); } }

    /// <summary>The newest sample, taking a new one when the last is older than the interval.</summary>
    public static (double Cpu, double Memory, double Down, double Up) Sample()
    {
        lock (Gate)
        {
            if ((DateTime.UtcNow - _last.At).TotalSeconds < IntervalSeconds * 0.8) return (_last.Cpu, _last.Memory, _last.Down, _last.Up);
            var cpu = SystemStats.CpuPercent();
            var (total, available) = SystemStats.Memory();
            var memory = 100.0 * (total - available) / total;
            var (down, up) = Speed();
            _last = (DateTime.UtcNow, cpu, memory, down, up);
            Push(CpuList, cpu);
            Push(MemoryList, memory);
            Push(DownList, down);
            return (cpu, memory, down, up);
        }
    }

    private static void Push(List<double> list, double value)
    {
        list.Add(Math.Round(value, 1));
        if (list.Count > Points) list.RemoveRange(0, list.Count - Points);
    }

    /// <summary>Bytes per second down and up across the connected adapters since the last call.</summary>
    private static (double Down, double Up) Speed()
    {
        long rx = 0, tx = 0;
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback or System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
            try
            {
                var stats = nic.GetIPStatistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
            catch (System.Net.NetworkInformation.NetworkInformationException)
            {
                // an adapter that went away mid-read
            }
        }
        var now = DateTime.UtcNow;
        var result = (0.0, 0.0);
        if (_bytes is { } prev && (now - prev.At).TotalSeconds is > 0.3 and < 600)
        {
            var seconds = (now - prev.At).TotalSeconds;
            result = (Math.Max(0, (rx - prev.Down) / seconds), Math.Max(0, (tx - prev.Up) / seconds));
        }
        _bytes = (rx, tx, now);
        return result;
    }
}
