using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TileSmith.Core.Actions;
using TileSmith.Core.Ai;
using TileSmith.Core.Native;

namespace TileSmith.Core.Tiles;

/// <summary>The sources that reach past this PC: the weather, your server, the web and your AI.</summary>
public sealed partial class TileResolver
{
    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10) })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TileSmith", "0.5"));
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(+https://github.com/T3lluz/TileSmith)"));
        return http;
    }

    // ── Weather (Open-Meteo) ─────────────────────────────────────────────────────────

    private async Task<TileValue> ReadWeatherAsync(string key, TileBlock b, CancellationToken ct)
    {
        var town = string.IsNullOrWhiteSpace(b.Query) ? host.Tiles.HomeTown : b.Query;
        if (string.IsNullOrWhiteSpace(town)) return TileValue.Needs("Set your town");
        var w = JsonNode.Parse(await Weather.GetAsync(town.Trim(), host.Tiles.Imperial ? "imperial" : "metric", ct))!;
        string Deg(JsonNode? n) => $"{(JsonPath.Number(n) ?? 0):0}°";
        var place = JsonPath.Text(w["place"]);
        var condition = JsonPath.Text(w["condition"]);
        var hours = (w["hours"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        switch (key)
        {
            case "weather.now":
                return new TileValue { Number = JsonPath.Number(w["temp"]), Text = Deg(w["temp"]), Unit = "°", Sub = condition, Icon = JsonPath.Text(w["icon"]), Url = WeatherLink(place) };
            case "weather.feels":
                return new TileValue { Number = JsonPath.Number(w["feels"]), Text = Deg(w["feels"]), Unit = "°", Sub = "Feels like" };
            case "weather.high_low":
                return new TileValue { Text = $"{Deg(w["high"])} / {Deg(w["low"])}", Number = JsonPath.Number(w["high"]), Sub = place ?? "High / low", Icon = JsonPath.Text(w["icon"]) };
            case "weather.rain":
            {
                var days = w["days"] as JsonArray;
                var chance = JsonPath.Number(days?[0]?["rain"]) ?? 0;
                var wet = hours.FirstOrDefault(h => (JsonPath.Number(h["rain"]) ?? 0) >= 50);
                var sub = wet is not null ? $"Rain at {JsonPath.Text(wet["time"])}" : chance >= 30 ? "Showers possible" : "Dry today";
                return new TileValue { Number = chance, Goal = 100, Unit = "%", Text = $"{chance:0}%", Sub = sub, Icon = chance >= 50 ? "rainy" : "umbrella" };
            }
            case "weather.wind":
                return new TileValue { Number = JsonPath.Number(w["wind"]), Text = $"{JsonPath.Number(w["wind"]) ?? 0:0} {JsonPath.Text(w["windUnit"])}", Unit = JsonPath.Text(w["windUnit"]), Sub = "Wind", Icon = "air" };
            case "weather.humidity":
                return new TileValue { Number = JsonPath.Number(w["humidity"]), Goal = 100, Unit = "%", Text = $"{JsonPath.Number(w["humidity"]) ?? 0:0}%", Sub = "Humidity" };
            case "weather.sun":
            {
                DateTime? Parse(string k) => DateTime.TryParse(JsonPath.Text(w[k]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t) ? t : null;
                var now = DateTime.Now;
                var (label, at) = Parse("sunriseIso") is { } rise && rise > now ? ("Sunrise", rise)
                                : Parse("sunsetIso") is { } set && set > now ? ("Sunset", set)
                                : ("Sunrise", Parse("sunriseNext") ?? now.AddHours(12));
                return new TileValue { Text = at.ToString("t", CultureInfo.CurrentCulture), Sub = label, Icon = label == "Sunrise" ? "wb_twilight" : "wb_sunny", At = new DateTimeOffset(at).ToUnixTimeMilliseconds() };
            }
            case "weather.hourly":
            {
                var temps = hours.Select(h => JsonPath.Number(h["temp"]) ?? double.NaN).Take(12).ToList();
                return new TileValue { Series = temps, Text = Deg(w["temp"]), Unit = "°", SeriesStart = "Now", SeriesEnd = temps.Count > 1 ? $"+{temps.Count - 1} h" : null };
            }
            case "weather.days":
            {
                var items = (w["days"] as JsonArray)?.OfType<JsonObject>().Select(d =>
                    new TileItem(JsonPath.Text(d["day"]) ?? "", JsonPath.Text(d["condition"]), $"{Deg(d["high"])} / {Deg(d["low"])}", Icon: JsonPath.Text(d["icon"]))).ToList() ?? [];
                return new TileValue { Items = items, Sub = place };
            }
        }
        throw new ActionException($"Nothing reads \"{key}\" yet.");
    }

    private static string? WeatherLink(string? place) =>
        place is null ? null : "https://www.google.com/search?q=" + Uri.EscapeDataString("weather " + place);

    // ── Your server (one script for the stats, shared by every block) ────────────────

    private const string StatsScript =
        "a=$(head -1 /proc/stat); sleep 0.4; b=$(head -1 /proc/stat)\n" +
        "echo \"cpu1 $a\"; echo \"cpu2 $b\"\n" +
        "echo \"load $(cut -d' ' -f1-3 /proc/loadavg)\"\n" +
        "awk '/^MemTotal/{t=$2}/^MemAvailable/{a=$2}END{print \"mem\",t,a}' /proc/meminfo\n" +
        "df -Pk / | awk 'NR==2{print \"disk\",$2,$3,$4}'\n" +
        "echo \"up $(cut -d. -f1 /proc/uptime)\"\n" +
        "echo \"host $(hostname)\"\n";

    private Task<Dictionary<string, string[]>>? _serverStats;
    private DateTime _serverStatsAt;

    private Task<Dictionary<string, string[]>> ServerStatsAsync()
    {
        lock (_gate)
        {
            if (_serverStats is not null && (!_serverStats.IsCompleted || (DateTime.UtcNow - _serverStatsAt).TotalSeconds < 8)) return _serverStats;
            _serverStatsAt = DateTime.UtcNow;
            return _serverStats = Task.Run(async () =>
            {
                var output = await host.RunOnServerAsync(StatsScript, TimeSpan.FromSeconds(15), CancellationToken.None);
                return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    .Where(p => p.Length > 1)
                    .GroupBy(p => p[0])
                    .ToDictionary(g => g.Key, g => g.First()[1..]);
            });
        }
    }

    private async Task<TileValue> ReadServerAsync(string key, TileBlock b, CancellationToken ct)
    {
        if (!host.ServerConfigured) return TileValue.Needs("Set up your server");
        static double D(string[]? parts, int i) => parts is not null && i < parts.Length && double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        switch (key)
        {
            case "server.cmd":
            {
                if (string.IsNullOrWhiteSpace(b.Cmd)) return TileValue.Needs("No command yet");
                if (!TileApprovals.IsCommandApproved(host.Tiles, TileApprovals.ScopeOf(b), b.Cmd)) return TileValue.Needs("Allow the command");
                return TileOutput.Parse(await host.RunOnServerAsync(b.Cmd, TimeSpan.FromSeconds(20), ct));
            }
            case "server.containers":
            {
                var output = await host.RunOnServerAsync(
                    "docker ps -a --format '{{.Names}}|{{.Image}}|{{.State}}|{{.Status}}' 2>/dev/null || sudo -n docker ps -a --format '{{.Names}}|{{.Image}}|{{.State}}|{{.Status}}'",
                    TimeSpan.FromSeconds(20), ct);
                var rows = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(l => l.Split('|'))
                    .Where(p => p.Length >= 4)
                    .Select(p =>
                    {
                        var running = p[2].Equals("running", StringComparison.OrdinalIgnoreCase);
                        var status = p[3].StartsWith("Up ", StringComparison.Ordinal) ? "Up " + ShortAge(p[3][3..]) : char.ToUpperInvariant(p[2][0]) + p[2][1..];
                        return (Running: running, Item: new TileItem(p[0], p[1], status, running ? "#3FA266" : "#E34671"));
                    })
                    .OrderBy(x => x.Running).ThenBy(x => x.Item.Title, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (rows.Count == 0) return TileValue.EmptyValue("No containers (or no Docker)");
                return new TileValue { Items = rows.Select(r => r.Item).ToList(), Number = rows.Count(r => r.Running), Goal = rows.Count, Text = $"{rows.Count(r => r.Running)}/{rows.Count}", Sub = "running" };
            }
        }

        Dictionary<string, string[]> stats;
        try
        {
            stats = await ServerStatsAsync().WaitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && key == "server.status")
        {
            return new TileValue { Text = "Offline", Sub = host.ServerName, Active = false, Icon = "cloud_off" };
        }
        var up = TimeSpan.FromSeconds(D(stats.GetValueOrDefault("up"), 0));
        var name = stats.GetValueOrDefault("host")?.FirstOrDefault() ?? host.ServerName;
        switch (key)
        {
            case "server.status":
                return new TileValue { Text = "Online", Sub = $"{name} · up {TileValue.FormatDuration(up)}", Active = true, Icon = "check_circle" };
            case "server.uptime":
                return new TileValue { Text = TileValue.FormatDuration(up), Sub = name, Number = Math.Round(up.TotalDays, 1), Unit = "d" };
            case "server.cpu":
            {
                var a = stats.GetValueOrDefault("cpu1");
                var c = stats.GetValueOrDefault("cpu2");
                double Total(string[]? p) => p is null ? 0 : p.Skip(1).Take(8).Sum(x => double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0);
                double Idle(string[]? p) => D(p, 4) + D(p, 5);
                var total = Total(c) - Total(a);
                var cpu = total <= 0 ? 0 : 100 * (1 - (Idle(c) - Idle(a)) / total);
                var load = stats.GetValueOrDefault("load");
                return Percent(cpu, load is null ? null : "Load " + string.Join(" ", load));
            }
            case "server.mem":
            {
                var m = stats.GetValueOrDefault("mem");
                var total = D(m, 0) * 1024;
                var available = D(m, 1) * 1024;
                if (total <= 0) return TileValue.Failed("No memory figures");
                return Percent(100 * (total - available) / total, $"{TileValue.FormatBytes(total - available)} of {TileValue.FormatBytes(total)}");
            }
            case "server.disk":
            {
                var d = stats.GetValueOrDefault("disk");
                var size = D(d, 0) * 1024;
                var used = D(d, 1) * 1024;
                var free = D(d, 2) * 1024;
                if (size <= 0) return TileValue.Failed("No disk figures");
                return Percent(100 * used / size, $"{TileValue.FormatBytes(free)} free");
            }
        }
        throw new ActionException($"Nothing reads \"{key}\" yet.");
    }

    /// <summary>"3 days" → "3d", "About an hour" → "1h", "12 minutes" → "12m".</summary>
    private static string ShortAge(string docker)
    {
        var s = docker.ToLowerInvariant().Replace("about an", "1").Replace("about a", "1").Replace("an ", "1 ").Replace("a ", "1 ");
        var m = Regex.Match(s, @"(\d+)\s*(second|minute|hour|day|week|month|year)");
        if (!m.Success) return docker.Split('(')[0].Trim();
        var unit = m.Groups[2].Value switch { "second" => "s", "minute" => "m", "hour" => "h", "day" => "d", "week" => "w", "month" => "mo", _ => "y" };
        return m.Groups[1].Value + unit;
    }

    // ── The web ──────────────────────────────────────────────────────────────────────

    private async Task<TileValue> ReadWebAsync(string key, TileBlock b, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(b.Url)) return TileValue.Needs("No address yet");
        if (!TileApprovals.IsSiteApproved(host.Tiles, b.Url)) return TileValue.Needs("Allow " + TileApprovals.SiteOf(b.Url));
        return key switch
        {
            "web.json" => await ReadJsonAsync(b, ct),
            "web.feed" => await ReadFeedAsync(b, ct),
            "web.image" => await ReadImageAsync(b.Url, ct) is { } file ? new TileValue { Image = file, Text = b.Label, Url = b.Url } : TileValue.Failed("Not a picture"),
            _ => throw new ActionException($"Nothing reads \"{key}\" yet."),
        };
    }

    private static async Task<TileValue> ReadJsonAsync(TileBlock b, CancellationToken ct)
    {
        var text = await Http.GetStringAsync(b.Url, ct);
        if (!JsonPath.TryParse(text, out var root)) return TileValue.Failed("The site didn't send JSON");
        var node = JsonPath.Navigate(root, b.Path);
        if (node is null) return TileValue.Failed($"No \"{b.Path}\" in the answer");
        if (node is JsonArray list)
        {
            var f = b.Fields;
            string? Field(JsonNode item, string k) => f.TryGetValue(k, out var p) ? JsonPath.Text(JsonPath.Navigate(item, p)) : null;
            var items = list.Take(30).OfType<JsonNode>().Select(item => f.Count > 0
                ? new TileItem(Field(item, "title") ?? "", Field(item, "sub"), Field(item, "trailing"), null, Field(item, "image"), Field(item, "link"))
                : TileOutput.Rows([item.DeepClone()]).FirstOrDefault() ?? new TileItem(JsonPath.Text(item) ?? "")).Where(i => i.Title.Length > 0).ToList();
            return new TileValue { Items = items, Number = items.Count };
        }
        if (node is JsonObject o) return TileOutput.Parse(o.ToJsonString());
        var value = JsonPath.Text(node) ?? "";
        return JsonPath.Number(node) is { } n ? new TileValue { Number = n, Text = TileValue.FormatNumber(n) } : new TileValue { Text = value };
    }

    private static async Task<TileValue> ReadFeedAsync(TileBlock b, CancellationToken ct)
    {
        var xml = await Http.GetStringAsync(b.Url, ct);
        var doc = XDocument.Parse(xml);
        XNamespace media = "http://search.yahoo.com/mrss/";
        XNamespace itunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
        XNamespace atom = "http://www.w3.org/2005/Atom";
        var items = new List<TileItem>();
        var rss = doc.Root?.Element("channel");
        if (rss is not null)
        {
            var source = rss.Element("title")?.Value.Trim();
            foreach (var item in rss.Elements("item").Take(30))
            {
                var image = item.Element(media + "thumbnail")?.Attribute("url")?.Value
                            ?? item.Elements(media + "content").FirstOrDefault(e => (e.Attribute("medium")?.Value ?? e.Attribute("type")?.Value ?? "image").StartsWith("image", StringComparison.Ordinal))?.Attribute("url")?.Value
                            ?? item.Element(media + "group")?.Element(media + "thumbnail")?.Attribute("url")?.Value
                            ?? item.Elements("enclosure").FirstOrDefault(e => e.Attribute("type")?.Value.StartsWith("image", StringComparison.Ordinal) == true)?.Attribute("url")?.Value
                            ?? item.Element(itunes + "image")?.Attribute("href")?.Value
                            ?? FirstImage(item.Element("description")?.Value);
                items.Add(new TileItem(Clean(item.Element("title")?.Value), source, Age(item.Element("pubDate")?.Value), null, image, item.Element("link")?.Value.Trim()));
            }
        }
        else if (doc.Root?.Name == atom + "feed")
        {
            var source = doc.Root.Element(atom + "title")?.Value.Trim();
            foreach (var entry in doc.Root.Elements(atom + "entry").Take(30))
            {
                var link = entry.Elements(atom + "link").FirstOrDefault(l => (l.Attribute("rel")?.Value ?? "alternate") == "alternate")?.Attribute("href")?.Value;
                var image = entry.Element(media + "group")?.Element(media + "thumbnail")?.Attribute("url")?.Value
                            ?? entry.Element(media + "thumbnail")?.Attribute("url")?.Value
                            ?? FirstImage(entry.Element(atom + "content")?.Value ?? entry.Element(atom + "summary")?.Value);
                items.Add(new TileItem(Clean(entry.Element(atom + "title")?.Value), source, Age(entry.Element(atom + "published")?.Value ?? entry.Element(atom + "updated")?.Value), null, image, link));
            }
        }
        else
        {
            return TileValue.Failed("Not an RSS or Atom feed");
        }
        items = items.Where(i => i.Title.Length > 0).ToList();
        return items.Count == 0 ? TileValue.EmptyValue("The feed is empty") : new TileValue { Items = items, Number = items.Count, Url = items[0].Link };
    }

    private static string Clean(string? html)
    {
        var text = WebUtility.HtmlDecode(Regex.Replace(html ?? "", "<[^>]+>", "")).Trim();
        return Regex.Replace(text, @"\s+", " ");
    }

    private static string? FirstImage(string? html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        var m = Regex.Match(html, "<img[^>]+src=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;
    }

    private static string? Age(string? date)
    {
        if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            // RFC 822 with a named zone ("GMT", "EST") that .NET doesn't read
            if (date is null || !DateTimeOffset.TryParse(Regex.Replace(date, @"\s[A-Z]{2,4}$", " +0000"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out at)) return null;
        }
        var ago = DateTimeOffset.UtcNow - at;
        return ago.TotalMinutes < 60 ? $"{Math.Max(1, (int)ago.TotalMinutes)}m" : ago.TotalHours < 24 ? $"{(int)ago.TotalHours}h" : ago.TotalDays < 7 ? $"{(int)ago.TotalDays}d" : at.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);
    }

    /// <summary>Downloads a picture to the cache (a new name each time, so the panel shows the new frame).</summary>
    private static async Task<string?> ReadImageAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!type.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && type.Length > 0) return null;
        var dir = Path.Combine(AppPaths.DataDir, "cache");
        Directory.CreateDirectory(dir);
        var stem = "web-" + TileSources.Hash(url);
        foreach (var old in Directory.EnumerateFiles(dir, stem + "-*")) { try { File.Delete(old); } catch (IOException) { } }
        var ext = type switch { "image/png" => ".png", "image/gif" => ".gif", "image/webp" => ".webp", "image/svg+xml" => ".svg", _ => ".jpg" };
        var file = Path.Combine(dir, $"{stem}-{DateTime.UtcNow.Ticks:x}{ext}");
        await using (var output = File.Create(file)) await response.Content.CopyToAsync(output, ct);
        return file;
    }

    // ── AI lines ─────────────────────────────────────────────────────────────────────

    private async Task<TileValue> ReadAiAsync(string key, TileBlock b, TileDesign d, IReadOnlyDictionary<string, TileValue> context, CancellationToken ct)
    {
        if (!d.Ai) return Get(b) is { State: TileValueState.Ok } last ? last : TileValue.EmptyValue("AI lines are off");
        if (string.IsNullOrWhiteSpace(b.Text)) return TileValue.Needs("Write what to ask");
        if (!host.AiReady) return TileValue.Needs("Pick an AI");
        var backend = host.CreateAi();
        var list = key == "ai.list";
        var max = Math.Clamp(b.Max ?? 6, 1, 8);
        var sb = new StringBuilder();
        sb.AppendLine($"You write {(list ? "the rows" : "one line")} of a widget called \"{d.Name}\" on a Windows quick-settings panel.");
        sb.AppendLine($"Now: {DateTime.Now.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)}.");
        if (!string.IsNullOrWhiteSpace(host.Tiles.HomeTown)) sb.AppendLine($"The person lives in {host.Tiles.HomeTown}.");
        var others = d.Blocks.Where(x => x != b && TileSources.BindingId(x) is { } id && context.ContainsKey(id))
            .Select(x => (Label: x.Label ?? TileSources.ByKey(x.Bind)?.Label, Value: context[TileSources.BindingId(x)!]))
            .Where(x => x.Value.State == TileValueState.Ok && x.Value.Display is not null)
            .Select(x => $"- {x.Label}: {x.Value.Display}{(x.Value.Sub is null ? "" : " (" + x.Value.Sub + ")")}")
            .ToList();
        if (others.Count > 0)
        {
            sb.AppendLine("What the widget's other parts show now:");
            foreach (var o in others) sb.AppendLine(o);
        }
        sb.AppendLine();
        sb.AppendLine("The ask: " + b.Text.Trim());
        if (b.Web) sb.AppendLine("Search the web first if you can, so it is current. If you can't, answer from what you know.");
        sb.AppendLine();
        sb.AppendLine(list
            ? $"Answer with only a JSON array of at most {max} rows: [{{\"title\": \"…\", \"sub\": \"…\", \"trailing\": \"…\", \"link\": \"https://…\"}}]. Titles under 40 characters; sub and trailing short or left out; link only when you know a real one."
            : "Answer with only the line: one or two short sentences, at most 150 characters, plain text, no quotes or markdown.");
        var reply = await backend.CompleteAsync([AiMessage.System("You write short, factual text for a desktop widget. Never explain yourself."), AiMessage.User(sb.ToString())], ct);
        reply = JsonExtractor.StripAnsi(reply).Trim();
        if (!list)
        {
            var line = reply.Trim().Trim('"', '“', '”').Replace("**", "");
            line = Regex.Replace(line, @"^```\w*|```$", "").Trim();
            if (line.Length > 220) line = line[..220].TrimEnd() + "…";
            return line.Length == 0 ? TileValue.Failed("The AI didn't answer") : new TileValue { Text = line, Icon = "auto_awesome" };
        }
        foreach (var node in JsonExtractor.Candidates(reply))
        {
            var array = node as JsonArray ?? (node as JsonObject)?["rows"] as JsonArray ?? (node as JsonObject)?["items"] as JsonArray;
            if (array is null) continue;
            var rows = TileOutput.Rows(array).Take(max).Select(r => r with { Link = TileTap.IsWebLink(r.Link ?? "") ? r.Link : null }).ToList();
            if (rows.Count > 0) return new TileValue { Items = rows, Number = rows.Count };
        }
        return TileValue.Failed("The AI's rows weren't readable");
    }
}
