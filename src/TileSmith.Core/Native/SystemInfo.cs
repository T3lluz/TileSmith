using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Windows.Media.Control;
using Windows.Storage.Streams;
using TileSmith.Core.Actions;

namespace TileSmith.Core.Native;

/// <summary>CPU, memory, disk and uptime without PowerShell: instant, so dashboards can refresh every second or two.</summary>
public static class SystemStats
{
    private static readonly object Gate = new();
    private static (ulong Idle, ulong Total, DateTime At)? _last;
    private static double _lastCpu;

    /// <summary>Processor load in percent since the previous call (sampled over 250 ms the first time).</summary>
    public static double CpuPercent()
    {
        lock (Gate)
        {
            var now = Sample();
            if (_last is not { } prev || (DateTime.UtcNow - prev.At).TotalMilliseconds < 200)
            {
                if (_last is { } p && (DateTime.UtcNow - p.At).TotalMilliseconds < 200 && _lastCpu > 0) return _lastCpu;
                Thread.Sleep(250);
                prev = now;
                now = Sample();
            }
            var idle = now.Idle - prev.Idle;
            var total = now.Total - prev.Total;
            _last = now;
            _lastCpu = total == 0 ? 0 : Math.Clamp(100.0 * (total - idle) / total, 0, 100);
            return _lastCpu;
        }
    }

    private static (ulong Idle, ulong Total, DateTime At) Sample()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new ActionException("Windows didn't report CPU times.");
        // kernel time includes idle time
        return (idle, kernel + user, DateTime.UtcNow);
    }

    public static (ulong Total, ulong Available) Memory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new ActionException("Windows didn't report memory use.");
        return (status.ullTotalPhys, status.ullAvailPhys);
    }

    public static DriveInfo Drive(string? name)
    {
        var letter = string.IsNullOrWhiteSpace(name) ? Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\" : name.Trim();
        if (letter.Length == 1) letter += ":\\";
        else if (letter.Length == 2 && letter[1] == ':') letter += "\\";
        var drive = new DriveInfo(letter);
        if (!drive.IsReady) throw new ActionException($"Drive {letter} isn't ready.");
        return drive;
    }

    /// <summary>One value (<paramref name="metric"/>) or, without one, everything as JSON.</summary>
    public static string Get(string? metric, string? drive)
    {
        string N(double d) => Math.Round(d, 1).ToString(CultureInfo.InvariantCulture);
        switch ((metric ?? "").Trim().ToLowerInvariant())
        {
            case "cpu": return N(CpuPercent());
            case "memory":
            {
                var (total, available) = Memory();
                return N(100.0 * (total - available) / total);
            }
            case "memoryused": { var (t, a) = Memory(); return (t - a).ToString(CultureInfo.InvariantCulture); }
            case "memorytotal": return Memory().Total.ToString(CultureInfo.InvariantCulture);
            case "disk":
            {
                var d = Drive(drive);
                return N(100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize);
            }
            case "diskfree": return Drive(drive).AvailableFreeSpace.ToString(CultureInfo.InvariantCulture);
            case "disktotal": return Drive(drive).TotalSize.ToString(CultureInfo.InvariantCulture);
            case "uptime": return (Environment.TickCount64 / 1000).ToString(CultureInfo.InvariantCulture);
            case "processes": return System.Diagnostics.Process.GetProcesses().Length.ToString(CultureInfo.InvariantCulture);
            case "":
            {
                var (total, available) = Memory();
                var d = Drive(drive);
                var json = new JsonObject
                {
                    ["cpu"] = Math.Round(CpuPercent(), 1),
                    ["memory"] = Math.Round(100.0 * (total - available) / total, 1),
                    ["memoryUsed"] = total - available,
                    ["memoryTotal"] = total,
                    ["disk"] = Math.Round(100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize, 1),
                    ["diskFree"] = d.AvailableFreeSpace,
                    ["diskTotal"] = d.TotalSize,
                    ["uptime"] = Environment.TickCount64 / 1000,
                };
                return json.ToJsonString();
            }
            default:
                throw new ActionException($"Unknown metric \"{metric}\".");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}

/// <summary>What's playing in any app that shows in Windows' media flyout (Spotify, browsers, players).</summary>
public static class NowPlaying
{
    private static string? _artKey;

    public static string ArtFile => Path.Combine(AppPaths.DataDir, "cache", "now-playing.png");

    public static async Task<string> GetAsync(string? field)
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = manager.GetCurrentSession();
        var json = new JsonObject { ["playing"] = false, ["status"] = "none", ["title"] = "", ["artist"] = "", ["album"] = "", ["app"] = "", ["art"] = "" };
        if (session is not null)
        {
            var info = session.GetPlaybackInfo();
            var props = await session.TryGetMediaPropertiesAsync();
            var status = info.PlaybackStatus.ToString().ToLowerInvariant();
            json["playing"] = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            json["status"] = status;
            json["title"] = props?.Title ?? "";
            json["artist"] = props?.Artist ?? "";
            json["album"] = props?.AlbumTitle ?? "";
            json["app"] = AppName(session.SourceAppUserModelId);
            json["art"] = props?.Thumbnail is { } thumb ? await SaveArtAsync(thumb, $"{props.Title}|{props.Artist}") : "";
            var timeline = session.GetTimelineProperties();
            json["position"] = Math.Round(timeline.Position.TotalSeconds);
            json["duration"] = Math.Round((timeline.EndTime - timeline.StartTime).TotalSeconds);
        }
        if (string.IsNullOrWhiteSpace(field)) return json.ToJsonString();
        var node = json[field.Trim()] ?? json.FirstOrDefault(kv => string.Equals(kv.Key, field.Trim(), StringComparison.OrdinalIgnoreCase)).Value
            ?? throw new ActionException($"Unknown field \"{field}\" — use title, artist, album, app, status, playing, art, position or duration.");
        return node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
    }

    private static string AppName(string aumid)
    {
        if (string.IsNullOrWhiteSpace(aumid)) return "";
        var name = aumid.Split('!')[0];
        name = Path.GetFileNameWithoutExtension(name.Split('_')[0]);
        var dot = name.LastIndexOf('.');
        return dot >= 0 && dot < name.Length - 1 ? name[(dot + 1)..] : name;
    }

    private static async Task<string> SaveArtAsync(IRandomAccessStreamReference thumb, string key)
    {
        try
        {
            if (key == _artKey && File.Exists(ArtFile)) return ArtFile;
            using var stream = await thumb.OpenReadAsync();
            using var input = stream.AsStreamForRead();
            Directory.CreateDirectory(Path.GetDirectoryName(ArtFile)!);
            // a new file name per track so the panel's image cache shows the new cover
            foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(ArtFile)!, "now-playing*.png")) { try { File.Delete(old); } catch (IOException) { } }
            var file = Path.Combine(Path.GetDirectoryName(ArtFile)!, $"now-playing-{(uint)key.GetHashCode():x8}.png");
            await using (var output = File.Create(file)) await input.CopyToAsync(output);
            _artKey = key;
            return file;
        }
        catch (Exception ex)
        {
            Log.Warn($"Album art unavailable: {ex.Message}");
            return "";
        }
    }
}

/// <summary>Small values widgets keep between runs (counters, checklists, a chosen mode) — no scripts needed.</summary>
public static class LocalStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, string>? _values;

    public static string FilePath => Path.Combine(AppPaths.DataDir, "store.json");

    private static Dictionary<string, string> Values()
    {
        if (_values is not null) return _values;
        try
        {
            _values = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []
                : [];
        }
        catch (Exception ex)
        {
            Log.Warn($"store.json unreadable: {ex.Message}");
            _values = [];
        }
        _values = new Dictionary<string, string>(_values, StringComparer.OrdinalIgnoreCase);
        return _values;
    }

    public static string Get(string key, string? fallback)
    {
        lock (Gate) return Values().TryGetValue(key, out var v) ? v : fallback ?? "";
    }

    public static string Set(string key, string value)
    {
        lock (Gate)
        {
            Values()[key] = value;
            Save();
            return value;
        }
    }

    public static string Toggle(string key)
    {
        lock (Gate) return Set(key, ValueText.IsTruthy(Get(key, "false")) ? "false" : "true");
    }

    public static string Add(string key, double delta, double? min, double? max)
    {
        lock (Gate)
        {
            var current = double.TryParse(Get(key, "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            var next = current + delta;
            if (min is { } lo) next = Math.Max(lo, next);
            if (max is { } hi) next = Math.Min(hi, next);
            return Set(key, ValueText.FormatNumber(next));
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_values, JsonDefaults.Options));
        }
        catch (Exception ex)
        {
            Log.Warn($"Saving store.json failed: {ex.Message}");
        }
    }
}

public static class NetworkProbe
{
    /// <summary>Round-trip time in ms, or "offline".</summary>
    public static async Task<string> PingAsync(string host, int timeoutMs)
    {
        using var ping = new Ping();
        try
        {
            var reply = await ping.SendPingAsync(host.Trim(), Math.Clamp(timeoutMs, 100, 10_000));
            return reply.Status == IPStatus.Success ? reply.RoundtripTime.ToString(CultureInfo.InvariantCulture) : "offline";
        }
        catch (PingException)
        {
            return "offline";
        }
    }
}

public static class Clock
{
    /// <summary>Now, formatted with a .NET format string ("HH:mm", "dddd d MMM"), or ISO 8601.</summary>
    public static string Now(string? format)
    {
        var now = DateTime.Now;
        if (string.IsNullOrWhiteSpace(format)) return now.ToString("s", CultureInfo.InvariantCulture);
        try { return now.ToString(format, CultureInfo.CurrentCulture); }
        catch (FormatException) { throw new ActionException($"\"{format}\" isn't a valid date format."); }
    }

    /// <summary>Seconds until a date/time (negative once it passed). "2026-12-24", "2026-12-24T18:00", "18:00" (today/tomorrow).</summary>
    public static string Until(string when)
    {
        var text = when.Trim();
        DateTime target;
        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var timeOfDay) && !text.Contains('-') && timeOfDay < TimeSpan.FromDays(1))
        {
            target = DateTime.Today + timeOfDay;
            if (target < DateTime.Now) target = target.AddDays(1);
        }
        else if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out target))
        {
            throw new ActionException($"\"{when}\" isn't a date — use e.g. \"2026-12-24\", \"2026-12-24T18:00\" or \"18:00\".");
        }
        return Math.Round((target - DateTime.Now).TotalSeconds).ToString(CultureInfo.InvariantCulture);
    }
}
