using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using TileSmith.Core.Actions;

namespace TileSmith.Core.Native;

/// <summary>
/// Weather from Open-Meteo (free, no key): current conditions with a Material icon name and a 5-day forecast.
/// Places are looked up by name once and cached; results are reused for 10 minutes.
/// </summary>
public static class Weather
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly Dictionary<string, (double Lat, double Lon, string Name)> Places = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (DateTime At, string Json)> Recent = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<string> GetAsync(string location, string? units, CancellationToken ct)
    {
        var imperial = string.Equals(units, "imperial", StringComparison.OrdinalIgnoreCase) || string.Equals(units, "fahrenheit", StringComparison.OrdinalIgnoreCase);
        var key = $"{location}|{imperial}";
        lock (Recent)
        {
            if (Recent.TryGetValue(key, out var hit) && DateTime.UtcNow - hit.At < TimeSpan.FromMinutes(10)) return hit.Json;
        }
        var (lat, lon, name) = await ResolveAsync(location, ct);
        var url = "https://api.open-meteo.com/v1/forecast?" +
                  $"latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                  "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day,precipitation" +
                  "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,sunrise,sunset" +
                  "&timezone=auto&forecast_days=5" + (imperial ? "&temperature_unit=fahrenheit&wind_speed_unit=mph" : "");
        var root = JsonNode.Parse(await Http.GetStringAsync(url, ct)) ?? throw new ActionException("The weather service sent nothing.");
        var current = root["current"]!;
        var code = (int)Num(current["weather_code"]);
        var day = Num(current["is_day"]) != 0;
        var daily = root["daily"]!;
        var days = new JsonArray();
        var count = (daily["time"] as JsonArray)?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            var dayCode = (int)Num(daily["weather_code"]?[i]);
            var date = DateTime.Parse(daily["time"]![i]!.GetValue<string>(), CultureInfo.InvariantCulture);
            days.Add(new JsonObject
            {
                ["day"] = i == 0 ? "Today" : date.ToString("ddd", CultureInfo.CurrentCulture),
                ["high"] = Math.Round(Num(daily["temperature_2m_max"]?[i])),
                ["low"] = Math.Round(Num(daily["temperature_2m_min"]?[i])),
                ["rain"] = Math.Round(Num(daily["precipitation_probability_max"]?[i])),
                ["condition"] = Condition(dayCode),
                ["icon"] = Icon(dayCode, true),
            });
        }
        var json = new JsonObject
        {
            ["place"] = name,
            ["temp"] = Math.Round(Num(current["temperature_2m"])),
            ["feels"] = Math.Round(Num(current["apparent_temperature"])),
            ["humidity"] = Math.Round(Num(current["relative_humidity_2m"])),
            ["wind"] = Math.Round(Num(current["wind_speed_10m"])),
            ["precipitation"] = Num(current["precipitation"]),
            ["unit"] = imperial ? "°F" : "°C",
            ["windUnit"] = imperial ? "mph" : "km/h",
            ["condition"] = Condition(code),
            ["icon"] = Icon(code, day),
            ["isDay"] = day,
            ["high"] = days.Count > 0 ? days[0]!["high"]!.DeepClone() : null,
            ["low"] = days.Count > 0 ? days[0]!["low"]!.DeepClone() : null,
            ["sunrise"] = Time(daily["sunrise"]?[0]),
            ["sunset"] = Time(daily["sunset"]?[0]),
            ["days"] = days,
        }.ToJsonString();
        lock (Recent) Recent[key] = (DateTime.UtcNow, json);
        return json;
    }

    private static double Num(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;

    private static string Time(JsonNode? node) =>
        node is not null && DateTime.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
            ? t.ToString("t", CultureInfo.CurrentCulture) : "";

    private static async Task<(double, double, string)> ResolveAsync(string location, CancellationToken ct)
    {
        var text = location.Trim();
        var parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var la)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo))
            return (la, lo, text);
        lock (Places)
        {
            if (Places.TryGetValue(text, out var known)) return known;
        }
        // "Oslo, Norway" → search "Oslo" and prefer a match in that country
        var city = parts[0];
        var url = $"https://geocoding-api.open-meteo.com/v1/search?count=5&language=en&format=json&name={Uri.EscapeDataString(city)}";
        var root = JsonNode.Parse(await Http.GetStringAsync(url, ct));
        var results = (root?["results"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (results.Count == 0) throw new ActionException($"Couldn't find a place called \"{location}\".");
        var hint = parts.Length > 1 ? parts[^1] : null;
        var best = results.FirstOrDefault(r => hint is not null && (r["country"]?.ToString().Contains(hint, StringComparison.OrdinalIgnoreCase) == true
                                                                   || r["country_code"]?.ToString().Equals(hint, StringComparison.OrdinalIgnoreCase) == true
                                                                   || r["admin1"]?.ToString().Contains(hint, StringComparison.OrdinalIgnoreCase) == true))
                   ?? results[0];
        var place = (Num(best["latitude"]), Num(best["longitude"]), best["name"]?.ToString() ?? city);
        lock (Places) Places[text] = place;
        return place;
    }

    /// <summary>WMO weather code → words.</summary>
    public static string Condition(int code) => code switch
    {
        0 => "Clear",
        1 => "Mostly clear",
        2 => "Partly cloudy",
        3 => "Cloudy",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 or 81 => "Showers",
        82 => "Heavy showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "—",
    };

    /// <summary>WMO weather code → Material Symbols name (for an icon element).</summary>
    public static string Icon(int code, bool day) => code switch
    {
        0 or 1 => day ? "sunny" : "clear_night",
        2 => day ? "partly_cloudy_day" : "partly_cloudy_night",
        3 => "cloud",
        45 or 48 => "foggy",
        51 or 53 or 55 or 56 or 57 => "rainy_light",
        61 or 80 => "rainy_light",
        63 or 81 => "rainy",
        65 or 82 or 66 or 67 => "rainy_heavy",
        71 or 73 or 75 or 77 or 85 or 86 => "weather_snowy",
        95 or 96 or 99 => "thunderstorm",
        _ => "cloud",
    };
}

/// <summary>The active network: name, Wi-Fi network and signal, IP address and live up/down speed.</summary>
public static class NetworkInfo
{
    private static readonly object Gate = new();
    private static (long Down, long Up, DateTime At, string Id)? _last;

    public static async Task<string> GetAsync(CancellationToken ct)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork))
            .ThenByDescending(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet)
            .FirstOrDefault();
        var json = new JsonObject { ["online"] = NetworkInterface.GetIsNetworkAvailable(), ["name"] = "", ["type"] = "", ["ip"] = "", ["ssid"] = "", ["signal"] = 0, ["down"] = 0, ["up"] = 0 };
        if (nic is null) return json.ToJsonString();
        var wifi = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
        json["name"] = nic.Name;
        json["type"] = wifi ? "Wi-Fi" : nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "Ethernet" : nic.NetworkInterfaceType.ToString();
        json["ip"] = nic.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";

        var stats = nic.GetIPStatistics();
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            if (_last is { } prev && prev.Id == nic.Id && (now - prev.At).TotalSeconds is > 0.2 and < 600)
            {
                var seconds = (now - prev.At).TotalSeconds;
                json["down"] = Math.Max(0, (long)((stats.BytesReceived - prev.Down) / seconds));
                json["up"] = Math.Max(0, (long)((stats.BytesSent - prev.Up) / seconds));
            }
            _last = (stats.BytesReceived, stats.BytesSent, now, nic.Id);
        }

        if (wifi)
        {
            var (ssid, signal) = await WlanAsync(ct);
            json["ssid"] = ssid;
            json["signal"] = signal;
        }
        return json.ToJsonString();
    }

    private static async Task<(string Ssid, int Signal)> WlanAsync(CancellationToken ct)
    {
        try
        {
            var result = await ProcessRunner.RunAsync("netsh.exe", ["wlan", "show", "interfaces"], TimeSpan.FromSeconds(4), ct);
            string ssid = "";
            var signal = 0;
            foreach (var raw in result.Stdout.Split('\n'))
            {
                var line = raw.Trim();
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var label = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();
                if (label.Equals("SSID", StringComparison.OrdinalIgnoreCase)) ssid = value;
                // the label is translated on non-English Windows, the value always ends in %
                else if (signal == 0 && value.EndsWith('%') && int.TryParse(value.TrimEnd('%'), out var s)) signal = s;
            }
            return (ssid, signal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ("", 0);
        }
    }
}

/// <summary>The busiest apps right now, by CPU or memory (processes of the same app added together).</summary>
public static class TopProcesses
{
    public static async Task<string> GetAsync(string? by, int count, CancellationToken ct)
    {
        count = Math.Clamp(count, 1, 15);
        var cpu = !string.Equals(by, "memory", StringComparison.OrdinalIgnoreCase);
        var first = Snapshot();
        if (cpu) await Task.Delay(500, ct);
        var second = cpu ? Snapshot() : first;
        var cores = Environment.ProcessorCount;
        var rows = second.Values
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var cpuMs = g.Sum(p => first.TryGetValue(p.Id, out var before) && before.Name == p.Name ? (p.Cpu - before.Cpu).TotalMilliseconds : 0);
                return new { name = g.Key, cpu = Math.Round(Math.Max(0, cpuMs / 500.0 / cores * 100), 1), memory = g.Sum(p => p.Memory), count = g.Count() };
            })
            .Where(r => !r.name.Equals("Idle", StringComparison.OrdinalIgnoreCase) && !r.name.Equals("System", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => cpu ? r.cpu : r.memory)
            .Take(count)
            .ToList();
        return JsonSerializer.Serialize(rows);
    }

    private static Dictionary<int, (int Id, string Name, TimeSpan Cpu, long Memory)> Snapshot()
    {
        var map = new Dictionary<int, (int, string, TimeSpan, long)>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                map[p.Id] = (p.Id, p.ProcessName, p.TotalProcessorTime, p.WorkingSet64);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // protected or already gone: skip
            }
            finally
            {
                p.Dispose();
            }
        }
        return map;
    }
}
