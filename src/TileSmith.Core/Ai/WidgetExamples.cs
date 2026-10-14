namespace TileSmith.Core.Ai;

/// <summary>
/// Known-good widgets shown to the model. The ones closest to the request go into the prompt, so the model starts
/// from something that works instead of from scratch (all of them pass the validator — see the tests).
/// </summary>
public static class WidgetExamples
{
    public sealed record Example(string Name, string[] Keywords, string Json);

    public static readonly IReadOnlyList<Example> All =
    [
        new("Dark mode toggle (standard control)", ["dark", "light", "theme", "toggle", "switch", "mode"], """
            {"title":"Dark mode","icon":"dark_mode","size":"2x1","controls":[{"type":"toggle","label":"Dark mode","state":{"type":"theme.get"},"onValue":"dark","on":{"type":"theme.set","mode":"dark"},"off":{"type":"theme.set","mode":"light"}}]}
            """),
        new("Power mode choice (standard control)", ["power", "mode", "performance", "battery", "choice", "switch between", "profile"], """
            {"title":"Power mode","icon":"bolt","size":"4x1","controls":[{"type":"choice","state":{"type":"power.getMode"},"options":[{"label":"Efficiency","value":"efficiency","action":{"type":"power.setMode","mode":"efficiency"}},{"label":"Balanced","value":"balanced","action":{"type":"power.setMode","mode":"balanced"}},{"label":"Performance","value":"performance","action":{"type":"power.setMode","mode":"performance"}}]}]}
            """),
        new("Volume slider (standard control)", ["volume", "slider", "sound", "audio", "brightness", "level"], """
            {"title":"Volume","icon":"volume_up","size":"4x1","controls":[{"type":"slider","label":"Volume","icon":"volume_up","min":0,"max":100,"step":1,"state":{"type":"audio.getVolume"},"action":{"type":"audio.setVolume","level":"{value}"},"format":"{value}%"}]}
            """),
        new("Service toggle backed by a script (standard control)", ["vpn", "service", "start", "stop", "script", "toggle", "enable", "disable", "connect"], """
            {"title":"WireGuard","icon":"vpn_key","size":"2x1","controls":[{"type":"toggle","label":"VPN","onText":"Connected","offText":"Off","state":{"type":"shell.powershell","script":"if ((Get-Service 'WireGuardTunnel$home' -ErrorAction SilentlyContinue).Status -eq 'Running') { 'on' } else { 'off' }"},"onValue":"on","on":{"type":"shell.powershell","script":"Start-Service 'WireGuardTunnel$home'","admin":true},"off":{"type":"shell.powershell","script":"Stop-Service 'WireGuardTunnel$home'","admin":true}}]}
            """),
        new("Now playing with cover art (layout)", ["music", "media", "spotify", "playing", "song", "track", "player", "cover", "album", "artist"], """
            {"title":"Now playing","icon":"music_note","size":"4x2","refreshSeconds":3,
             "data":{"media":{"type":"media.nowPlaying"}},
             "layout":{"type":"row","gap":14,"children":[
               {"type":"stack","width":96,"height":96,"radius":16,"background":"accent/15","children":[
                 {"type":"icon","icon":"music_note","size":36,"color":"accent","align":"center","valign":"center"},
                 {"type":"image","src":"{media.art}","fit":"cover","visible":"{media.art}"}]},
               {"type":"column","flex":1,"gap":2,"valign":"center","children":[
                 {"type":"text","text":{"if":"{media.status} == none","then":"Nothing playing","else":"{media.title}"},"size":"title","weight":"semibold"},
                 {"type":"text","text":"{media.artist}","size":"body","color":"text2"},
                 {"type":"text","text":"{media.app}","size":"caption","color":"text3"},
                 {"type":"row","gap":6,"padding":[0,6,0,0],"children":[
                   {"type":"button","icon":"skip_previous","variant":"icon","action":{"type":"media.key","key":"previous"}},
                   {"type":"button","icon":{"if":"{media.playing}","then":"pause","else":"play_arrow"},"variant":"filled","action":{"type":"media.key","key":"playPause"}},
                   {"type":"button","icon":"skip_next","variant":"icon","action":{"type":"media.key","key":"next"}}]}]}]}}
            """),
        new("System dashboard with gauges (layout)", ["cpu", "memory", "ram", "disk", "dashboard", "stats", "usage", "system", "monitor", "load", "gauge"], """
            {"title":"System","icon":"monitoring","size":"4x2","refreshSeconds":2,
             "data":{"pc":{"type":"system.stats"}},
             "layout":{"type":"row","gap":8,"children":[
               {"type":"column","flex":1,"gap":6,"align":"center","children":[
                 {"type":"ring","value":"{pc.cpu}","size":72,"thickness":7,"color":{"if":"{pc.cpu} > 85","then":"error","else":"accent"},"children":[{"type":"text","text":"{pc.cpu|round}%","size":"title","weight":"semibold"}]},
                 {"type":"text","text":"CPU","size":"caption","color":"text2"}]},
               {"type":"column","flex":1,"gap":6,"align":"center","children":[
                 {"type":"ring","value":"{pc.memory}","size":72,"thickness":7,"color":{"if":"{pc.memory} > 85","then":"warning","else":"accent"},"children":[{"type":"text","text":"{pc.memory|round}%","size":"title","weight":"semibold"}]},
                 {"type":"text","text":"Memory · {pc.memoryUsed|bytes}","size":"caption","color":"text2"}]},
               {"type":"column","flex":1,"gap":6,"align":"center","children":[
                 {"type":"ring","value":"{pc.disk}","size":72,"thickness":7,"children":[{"type":"text","text":"{pc.disk|round}%","size":"title","weight":"semibold"}]},
                 {"type":"text","text":"{pc.diskFree|bytes} free","size":"caption","color":"text2"}]}]}}
            """),
        new("Live value with a history line (layout)", ["graph", "chart", "history", "trend", "sparkline", "cpu", "ping", "latency", "temperature", "gpu"], """
            {"title":"CPU","icon":"memory","size":"2x2","refreshSeconds":2,
             "data":{"cpu":{"type":"system.stats","metric":"cpu"}},
             "layout":{"type":"column","gap":4,"children":[
               {"type":"row","gap":6,"children":[{"type":"icon","icon":"memory","size":18,"color":"accent"},{"type":"text","text":"CPU","size":"label","color":"text2","weight":"semibold"}]},
               {"type":"text","text":"{cpu|round}%","size":"display","weight":"semibold"},
               {"type":"spacer"},
               {"type":"sparkline","value":"{cpu}","points":40,"fill":true,"height":44,"min":0,"max":100}]}}
            """),
        new("Countdown (layout)", ["countdown", "days", "until", "timer", "vacation", "birthday", "deadline", "event", "christmas"], """
            {"title":"Vacation","icon":"beach_access","size":"2x1","refreshSeconds":60,
             "data":{"left":{"type":"time.until","when":"2026-12-20T16:00"}},
             "layout":{"type":"row","gap":12,"children":[
               {"type":"stack","width":40,"height":40,"radius":20,"background":"accent/15","valign":"center","children":[{"type":"icon","icon":"beach_access","color":"accent","align":"center","valign":"center"}]},
               {"type":"column","flex":1,"valign":"center","children":[
                 {"type":"text","text":{"if":"{left} > 0","then":"{left|duration}","else":"Today!"},"size":"title","weight":"semibold"},
                 {"type":"text","text":"until vacation","size":"caption","color":"text2"}]}]}}
            """),
        new("Counter kept between runs (layout)", ["counter", "count", "water", "glasses", "tally", "track", "habit", "increment", "score"], """
            {"title":"Water","icon":"water_drop","size":"2x1",
             "data":{"glasses":{"type":"store.get","key":"water.glasses","default":"0"}},
             "layout":{"type":"row","gap":8,"children":[
               {"type":"column","flex":1,"valign":"center","children":[
                 {"type":"text","text":"{glasses} / 8","size":"title","weight":"semibold"},
                 {"type":"text","text":"glasses of water","size":"caption","color":"text2"}]},
               {"type":"button","icon":"remove","variant":"tonal","valign":"center","action":{"type":"store.add","key":"water.glasses","delta":-1,"min":0}},
               {"type":"button","icon":"add","variant":"filled","valign":"center","action":{"type":"store.add","key":"water.glasses","delta":1,"max":20}}]}}
            """),
        new("Is a host up (layout)", ["ping", "server", "online", "offline", "host", "up", "down", "status", "nas", "router", "reachable"], """
            {"title":"NAS","icon":"dns","size":"2x1","refreshSeconds":15,
             "data":{"ping":{"type":"network.ping","host":"192.168.1.10"}},
             "layout":{"type":"row","gap":12,"tap":{"type":"url.open","url":"http://192.168.1.10:5000"},"children":[
               {"type":"stack","width":40,"height":40,"radius":20,"valign":"center","background":{"if":"{ping} == offline","then":"error/20","else":"success/20"},"children":[
                 {"type":"icon","icon":"dns","align":"center","valign":"center","color":{"if":"{ping} == offline","then":"error","else":"success"}}]},
               {"type":"column","flex":1,"valign":"center","children":[
                 {"type":"text","text":"NAS","size":"body","weight":"semibold"},
                 {"type":"text","text":{"if":"{ping} == offline","then":"Offline","else":"Online · {ping} ms"},"size":"caption","color":"text2"}]}]}}
            """),
        new("Battery bar with warning colour (layout)", ["battery", "charge", "percent", "progress", "bar", "level"], """
            {"title":"Battery","icon":"battery_full","size":"2x1","refreshSeconds":60,
             "data":{"bat":{"type":"battery.get","field":"percent"},"state":{"type":"battery.get","field":"status"}},
             "layout":{"type":"column","gap":8,"valign":"center","children":[
               {"type":"row","gap":8,"children":[
                 {"type":"icon","icon":{"if":"{state} == Charging","then":"battery_charging_full","else":"battery_full"},"size":18,"color":"text2"},
                 {"type":"text","text":"{bat}%","size":"body","weight":"semibold","flex":1},
                 {"type":"text","text":"{state}","size":"caption","color":"text3"}]},
               {"type":"progress","value":"{bat}","thickness":8,"color":{"if":"{bat} < 20","then":"error","else":"success"}}]}}
            """),
        new("Weather with a forecast list (layout)", ["weather", "forecast", "temperature", "rain", "sunny", "outside", "degrees", "wind"], """
            {"title":"Weather","icon":"partly_cloudy_day","size":"4x2","refreshSeconds":900,
             "data":{"w":{"type":"weather.get","location":"Oslo"}},
             "layout":{"type":"row","gap":16,"children":[
               {"type":"column","gap":2,"valign":"center","children":[
                 {"type":"row","gap":8,"children":[{"type":"icon","icon":"{w.icon}","size":34,"color":"accent"},{"type":"text","text":"{w.temp}{w.unit}","size":"display","weight":"semibold"}]},
                 {"type":"text","text":"{w.condition}","size":"body","color":"text2"},
                 {"type":"text","text":"{w.place} · H {w.high}° L {w.low}°","size":"caption","color":"text3"}]},
               {"type":"list","items":"{w.days}","max":4,"direction":"row","gap":6,"flex":1,"template":
                 {"type":"column","gap":4,"align":"center","children":[
                   {"type":"text","text":"{item.day}","size":"caption","color":"text2","textAlign":"center"},
                   {"type":"icon","icon":"{item.icon}","size":22,"align":"center"},
                   {"type":"text","text":"{item.high}°","size":"label","weight":"semibold","textAlign":"center"}]}}]}}
            """),
        new("A live list: busiest apps (layout)", ["apps", "processes", "top", "busiest", "using", "list", "memory", "cpu", "task manager"], """
            {"title":"Top apps","icon":"leaderboard","size":"2x2","refreshSeconds":3,
             "data":{"top":{"type":"system.top","by":"cpu","count":4}},
             "layout":{"type":"column","gap":6,"children":[
               {"type":"row","gap":6,"children":[{"type":"icon","icon":"leaderboard","size":16,"color":"accent"},{"type":"text","text":"Busiest apps","size":"label","weight":"semibold","color":"text2"}]},
               {"type":"list","items":"{top}","max":4,"gap":4,"empty":"Nothing busy","template":
                 {"type":"row","gap":8,"children":[
                   {"type":"text","text":"{item.name}","size":"label","flex":1},
                   {"type":"text","text":"{item.cpu|round}%","size":"label","weight":"semibold","color":"accent"}]}}]}}
            """),
        new("Network and live speed (layout)", ["network", "wifi", "wi-fi", "speed", "bandwidth", "download", "upload", "ip", "internet", "ssid"], """
            {"title":"Network","icon":"network_wifi","size":"4x1","refreshSeconds":2,
             "data":{"net":{"type":"network.info"}},
             "layout":{"type":"row","gap":12,"children":[
               {"type":"icon","icon":{"if":"{net.type} == Wi-Fi","then":"wifi","else":"lan"},"size":24,"color":"accent"},
               {"type":"column","flex":1,"valign":"center","children":[
                 {"type":"text","text":{"if":"{net.ssid}","then":"{net.ssid}","else":"{net.name}"},"size":"body","weight":"semibold"},
                 {"type":"text","text":"{net.ip}","size":"caption","color":"text2","mono":true}]},
               {"type":"column","valign":"center","children":[
                 {"type":"text","text":"↓ {net.down|bytes}/s","size":"label","textAlign":"end"},
                 {"type":"text","text":"↑ {net.up|bytes}/s","size":"label","color":"text2","textAlign":"end"}]}]}}
            """),
        new("Several values from one JSON script (layout)", ["script", "json", "info", "several", "details", "gpu", "connection", "docker", "containers", "profile"], """
            {"title":"Network","icon":"lan","size":"4x1","refreshSeconds":30,
             "data":{"net":{"type":"shell.powershell","script":"$c = Get-NetConnectionProfile | Select-Object -First 1; $ip = (Get-NetIPConfiguration | Where-Object { $_.IPv4DefaultGateway } | Select-Object -First 1).IPv4Address.IPAddress; @{ name = $c.Name; ip = $ip; kind = [string]$c.InterfaceAlias } | ConvertTo-Json -Compress"}},
             "layout":{"type":"row","gap":12,"children":[
               {"type":"icon","icon":"lan","size":24,"color":"accent","valign":"center"},
               {"type":"column","flex":1,"valign":"center","children":[
                 {"type":"text","text":"{net.name}","size":"body","weight":"semibold"},
                 {"type":"text","text":"{net.kind}","size":"caption","color":"text2"}]},
               {"type":"text","text":"{net.ip}","size":"body","mono":true,"valign":"center"}]}}
            """),
    ];

    /// <summary>The examples closest to the request (by keyword), always including at least one layout widget.</summary>
    public static IReadOnlyList<Example> For(string? request, int count = 3)
    {
        var text = (request ?? "").ToLowerInvariant();
        var ranked = All
            .Select((e, i) => (e, i, score: e.Keywords.Count(k => text.Contains(k, StringComparison.Ordinal))))
            .OrderByDescending(x => x.score).ThenBy(x => x.i)
            .ToList();
        var picked = ranked.Where(x => x.score > 0).Take(count).Select(x => x.e).ToList();
        if (!picked.Any(e => e.Name.Contains("(layout)", StringComparison.Ordinal)))
            picked.Add(All.First(e => e.Name.StartsWith("System dashboard", StringComparison.Ordinal)));
        if (picked.Count < 2) picked.Insert(0, All[0]);
        return picked;
    }
}
