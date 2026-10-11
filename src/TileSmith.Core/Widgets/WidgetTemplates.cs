using System.Text.Json;
using TileSmith.Core.Native;

namespace TileSmith.Core.Widgets;

/// <summary>A ready-made widget in the gallery: added with one tap, no AI needed.</summary>
public sealed record WidgetTemplate(string Id, string Category, string Name, string Description, Func<WidgetDefinition> Create)
{
    public WidgetDefinition Build()
    {
        var widget = Create();
        widget.Id = WidgetDefinition.NewId();
        WidgetValidator.Validate(widget);
        return widget;
    }
}

public static class WidgetTemplates
{
    public static readonly string[] Categories = ["Essentials", "Display", "Sound", "Power", "Apps & shortcuts", "System info", "Server"];

    /// <summary>Every template that makes sense on this PC (display ones are built from the modes it supports).</summary>
    public static IReadOnlyList<WidgetTemplate> ForThisPc(bool serverConfigured)
    {
        var list = new List<WidgetTemplate>();
        void Add(string id, string category, string name, string description, string json) =>
            list.Add(new WidgetTemplate(id, category, name, description, () => WidgetDefinition.FromJson(json)));

        // ── Essentials ─────────────────────────────────────────────────────────────────
        Add("wifi", "Essentials", "Wi-Fi", "Turn Wi-Fi on or off", """
            {"title":"Wi-Fi","icon":"wifi","size":"2x1","controls":[{"type":"toggle","label":"Wi-Fi","state":{"type":"radio.get","kind":"wifi"},"onValue":"on","on":{"type":"radio.set","kind":"wifi","state":"on"},"off":{"type":"radio.set","kind":"wifi","state":"off"}}]}
            """);
        Add("bluetooth", "Essentials", "Bluetooth", "Turn Bluetooth on or off", """
            {"title":"Bluetooth","icon":"bluetooth","size":"2x1","controls":[{"type":"toggle","label":"Bluetooth","state":{"type":"radio.get","kind":"bluetooth"},"onValue":"on","on":{"type":"radio.set","kind":"bluetooth","state":"on"},"off":{"type":"radio.set","kind":"bluetooth","state":"off"}}]}
            """);
        Add("dark", "Essentials", "Dark mode", "Switch Windows between dark and light", """
            {"title":"Dark mode","icon":"dark_mode","size":"2x1","controls":[{"type":"toggle","label":"Dark mode","state":{"type":"theme.get"},"onValue":"dark","on":{"type":"theme.set","mode":"dark"},"off":{"type":"theme.set","mode":"light"}}]}
            """);
        Add("lock", "Essentials", "Lock", "Lock the PC", """
            {"title":"Lock","icon":"lock","size":"1x1","controls":[{"type":"button","label":"Lock","action":{"type":"power.action","action":"lock"}}]}
            """);
        Add("snip", "Essentials", "Snip", "Take a screenshot of an area", """
            {"title":"Snip","icon":"screenshot_region","size":"1x1","controls":[{"type":"button","label":"Snip","action":{"type":"keys.send","keys":"Win+Shift+S"}}]}
            """);

        // ── Display ────────────────────────────────────────────────────────────────────
        if (Safe(BrightnessControl.IsSupported))
        {
            Add("brightness", "Display", "Brightness", "A full-width brightness slider", """
                {"title":"Brightness","icon":"brightness_6","size":"4x1","controls":[{"type":"slider","label":"Brightness","icon":"brightness_6","min":0,"max":100,"step":1,"state":{"type":"display.getBrightness"},"action":{"type":"display.setBrightness","level":"{value}"},"format":"{value}%"}]}
                """);
        }
        try
        {
            var displays = DisplayManager.GetDisplays();
            var main = displays.FirstOrDefault(d => d.IsInternal) ?? displays.FirstOrDefault(d => d.IsPrimary) ?? displays.FirstOrDefault();
            if (main is not null)
            {
                var selector = main.IsInternal ? "internal" : "primary";
                var resolutions = main.ModesByResolution().Select(g => g.Key).Take(3).ToList();
                if (resolutions.Count >= 2)
                {
                    var options = resolutions.Select(r =>
                    {
                        var parts = r.Split('x');
                        return new { label = $"{parts[0]} × {parts[1]}", value = r, action = new { type = "display.setResolution", width = int.Parse(parts[0]), height = int.Parse(parts[1]), display = selector } };
                    });
                    var json = JsonSerializer.Serialize(new
                    {
                        title = "Resolution",
                        subtitle = main.IsInternal ? "Built-in display" : main.FriendlyName,
                        icon = "aspect_ratio",
                        size = "4x1",
                        controls = new[] { new { type = "choice", state = new { type = "display.getResolution", display = selector }, options } },
                    });
                    Add("resolution", "Display", "Resolution", $"Switch between {string.Join(", ", resolutions.Select(r => r.Replace("x", " × ")))}", json);
                }
                var rates = main.Modes.Where(m => m.Width == main.Current.Width && m.Height == main.Current.Height)
                    .Select(m => m.RefreshRate).Distinct().OrderDescending().Take(4).ToList();
                if (rates.Count >= 2)
                {
                    var json = JsonSerializer.Serialize(new
                    {
                        title = "Refresh rate",
                        icon = "speed",
                        size = "4x1",
                        controls = new[]
                        {
                            new
                            {
                                type = "choice",
                                state = new { type = "display.getRefreshRate", display = selector },
                                options = rates.Select(r => new { label = $"{r} Hz", value = r.ToString(), action = new { type = "display.setRefreshRate", hz = r, display = selector } }),
                            },
                        },
                    });
                    Add("refresh", "Display", "Refresh rate", $"Switch between {string.Join(", ", rates.Select(r => r + " Hz"))}", json);
                }
                if (displays.Count > 1 || !main.IsInternal)
                {
                    Add("project", "Display", "Projection", "PC only, mirror or extend (like Win+P)", """
                        {"title":"Projection","icon":"cast","size":"4x1","controls":[{"type":"choice","options":[{"label":"PC only","icon":"laptop","action":{"type":"display.project","mode":"pcScreenOnly"}},{"label":"Mirror","action":{"type":"display.project","mode":"duplicate"}},{"label":"Extend","action":{"type":"display.project","mode":"extend"}},{"label":"Second","action":{"type":"display.project","mode":"secondScreenOnly"}}]}]}
                        """);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Display templates skipped: {ex.Message}");
        }
        Add("display-off", "Display", "Screen off", "Turn the display off until you move the mouse", """
            {"title":"Screen off","icon":"desktop_access_disabled","size":"1x1","controls":[{"type":"button","label":"Screen off","action":{"type":"power.action","action":"displayOff"}}]}
            """);

        // ── Sound ──────────────────────────────────────────────────────────────────────
        Add("volume", "Sound", "Volume", "A full-width volume slider", """
            {"title":"Volume","icon":"volume_up","size":"4x1","controls":[{"type":"slider","label":"Volume","icon":"volume_up","min":0,"max":100,"step":1,"state":{"type":"audio.getVolume"},"action":{"type":"audio.setVolume","level":"{value}"},"format":"{value}%"}]}
            """);
        Add("mute", "Sound", "Mute", "Mute or unmute the speakers", """
            {"title":"Mute","icon":"volume_off","size":"1x1","controls":[{"type":"toggle","label":"Mute","state":{"type":"audio.getMute"},"on":{"type":"audio.setMute","mute":"true"},"off":{"type":"audio.setMute","mute":"false"}}]}
            """);
        Add("mic", "Sound", "Microphone", "Mute your microphone in one tap", """
            {"title":"Microphone","icon":"mic","size":"2x1","controls":[{"type":"toggle","label":"Microphone","onText":"On","offText":"Muted","state":{"type":"audio.getMicMute"},"onValue":"false","on":{"type":"audio.setMicMute","mute":"false"},"off":{"type":"audio.setMicMute","mute":"true"}}]}
            """);
        Add("media", "Sound", "Media controls", "Previous, play/pause and next", """
            {"title":"Media","icon":"play_circle","size":"2x2","controls":[{"type":"button","label":"","icon":"skip_previous","action":{"type":"media.key","key":"previous"}},{"type":"button","label":"","icon":"play_pause","style":"accent","action":{"type":"media.key","key":"playPause"}},{"type":"button","label":"","icon":"skip_next","action":{"type":"media.key","key":"next"}}]}
            """);

        // ── Power ──────────────────────────────────────────────────────────────────────
        Add("powermode", "Power", "Power mode", "Efficiency, balanced or performance", """
            {"title":"Power mode","icon":"bolt","size":"4x1","controls":[{"type":"choice","state":{"type":"power.getMode"},"options":[{"label":"Efficiency","value":"efficiency","action":{"type":"power.setMode","mode":"efficiency"}},{"label":"Balanced","value":"balanced","action":{"type":"power.setMode","mode":"balanced"}},{"label":"Performance","value":"performance","action":{"type":"power.setMode","mode":"performance"}}]}]}
            """);
        if (Safe(() => PowerControl.GetBattery().HasBattery))
        {
            Add("battery", "Power", "Battery", "Charge level, refreshed every minute", """
                {"title":"Battery","icon":"battery_full","size":"2x1","refreshSeconds":60,"controls":[{"type":"status","label":"Battery","state":{"type":"battery.get","field":"percent"},"format":"{value}%"}]}
                """);
        }
        Add("sleep", "Power", "Sleep", "Put the PC to sleep", """
            {"title":"Sleep","icon":"bedtime","size":"1x1","controls":[{"type":"button","label":"Sleep","confirm":"Put the PC to sleep now?","action":{"type":"power.action","action":"sleep"}}]}
            """);
        Add("restart", "Power", "Restart", "Restart the PC (asks first)", """
            {"title":"Restart","icon":"restart_alt","size":"1x1","controls":[{"type":"button","label":"Restart","confirm":"Restart the PC now? Unsaved work will be lost.","action":{"type":"power.action","action":"restart"}}]}
            """);
        Add("shutdown", "Power", "Shut down", "Turn the PC off (asks first)", """
            {"title":"Shut down","icon":"power_settings_new","size":"1x1","controls":[{"type":"button","label":"Shut down","confirm":"Shut down the PC now? Unsaved work will be lost.","action":{"type":"power.action","action":"shutdown"}}]}
            """);

        // ── Apps & shortcuts ───────────────────────────────────────────────────────────
        Add("taskmgr", "Apps & shortcuts", "Task Manager", "Open Task Manager", """
            {"title":"Task Manager","icon":"monitoring","size":"1x1","controls":[{"type":"button","label":"Tasks","action":{"type":"keys.send","keys":"Ctrl+Shift+Esc"}}]}
            """);
        Add("explorer", "Apps & shortcuts", "File Explorer", "Open a new Explorer window", """
            {"title":"Files","icon":"folder_open","size":"1x1","controls":[{"type":"button","label":"Files","action":{"type":"keys.send","keys":"Win+E"}}]}
            """);
        Add("downloads", "Apps & shortcuts", "Downloads", "Open your Downloads folder", """
            {"title":"Downloads","icon":"download","size":"1x1","controls":[{"type":"button","label":"Downloads","action":{"type":"folder.open","path":"%USERPROFILE%\\Downloads"}}]}
            """);
        Add("clipboard", "Apps & shortcuts", "Clipboard history", "Open clipboard history (Win+V)", """
            {"title":"Clipboard","icon":"content_paste","size":"1x1","controls":[{"type":"button","label":"Clipboard","action":{"type":"keys.send","keys":"Win+V"}}]}
            """);
        Add("emoji", "Apps & shortcuts", "Emoji", "Open the emoji panel", """
            {"title":"Emoji","icon":"mood","size":"1x1","controls":[{"type":"button","label":"Emoji","action":{"type":"keys.send","keys":"Win+."}}]}
            """);
        Add("calc", "Apps & shortcuts", "Calculator", "Open Calculator", """
            {"title":"Calculator","icon":"calculate","size":"1x1","controls":[{"type":"button","label":"Calculator","action":{"type":"app.launch","path":"calc"}}]}
            """);
        Add("settings", "Apps & shortcuts", "Windows Settings", "Open Windows Settings", """
            {"title":"Settings","icon":"settings","size":"1x1","controls":[{"type":"button","label":"Settings","action":{"type":"url.open","url":"ms-settings:"}}]}
            """);
        Add("recycle", "Apps & shortcuts", "Empty recycle bin", "Free up space (asks first)", """
            {"title":"Recycle bin","icon":"delete_sweep","size":"2x1","subtitle":"Empty it","controls":[{"type":"button","label":"Empty bin","confirm":"Permanently delete everything in the recycle bin?","action":{"type":"shell.powershell","script":"Clear-RecycleBin -Force -ErrorAction SilentlyContinue"}}]}
            """);

        // ── System info ────────────────────────────────────────────────────────────────
        Add("cpu", "System info", "CPU load", "Processor load, every 5 seconds", """
            {"title":"CPU","icon":"memory","size":"2x1","refreshSeconds":5,"controls":[{"type":"status","label":"CPU load","state":{"type":"shell.powershell","script":"[math]::Round((Get-CimInstance Win32_Processor | Measure-Object -Property LoadPercentage -Average).Average)"},"format":"{value}%"}]}
            """);
        Add("ram", "System info", "Memory", "Memory in use, every 10 seconds", """
            {"title":"Memory","icon":"memory_alt","size":"2x1","refreshSeconds":10,"controls":[{"type":"status","label":"Memory in use","state":{"type":"shell.powershell","script":"$o = Get-CimInstance Win32_OperatingSystem; [math]::Round(100 - $o.FreePhysicalMemory * 100 / $o.TotalVisibleMemorySize)"},"format":"{value}%"}]}
            """);
        Add("ip", "System info", "IP address", "This PC's local IP address", """
            {"title":"IP address","icon":"lan","size":"2x1","refreshSeconds":60,"controls":[{"type":"status","label":"Local IP","state":{"type":"shell.powershell","script":"(Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway } | Select-Object -First 1).IPv4Address.IPAddress"}}]}
            """);
        Add("uptime", "System info", "Uptime", "How long since the last restart", """
            {"title":"Uptime","icon":"schedule","size":"2x1","refreshSeconds":60,"controls":[{"type":"status","label":"Uptime","state":{"type":"shell.powershell","script":"$u = (Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime; if ($u.Days -gt 0) { '{0}d {1}h' -f $u.Days, $u.Hours } else { '{0}h {1}m' -f $u.Hours, $u.Minutes }"}}]}
            """);

        // ── Server ─────────────────────────────────────────────────────────────────────
        if (serverConfigured)
        {
            Add("server-uptime", "Server", "Server status", "Your server's uptime and load", """
                {"title":"Server","icon":"dns","size":"4x2","refreshSeconds":30,"controls":[{"type":"status","label":"Uptime","state":{"type":"server.run","command":"uptime -p | sed 's/^up //'"}},{"type":"status","label":"Load","state":{"type":"server.run","command":"cut -d' ' -f1-3 /proc/loadavg"}},{"type":"status","label":"Disk free","state":{"type":"server.run","command":"df -h / | awk 'NR==2{print $4\" of \"$2}'"}}]}
                """);
            Add("server-docker", "Server", "Containers", "How many Docker containers are running", """
                {"title":"Containers","icon":"deployed_code","size":"2x1","refreshSeconds":60,"controls":[{"type":"status","label":"Running","state":{"type":"server.run","command":"docker ps -q | wc -l"},"format":"{value} running"}]}
                """);
        }
        return list;
    }

    private static bool Safe(Func<bool> check)
    {
        try { return check(); }
        catch { return false; }
    }
}
