using System.Text.Json;
using TileSmith.Core.Native;

namespace TileSmith.Core.Widgets;

public static class WidgetStore
{
    public static List<WidgetDefinition> Load()
    {
        try
        {
            if (File.Exists(AppPaths.WidgetsFile))
            {
                var list = JsonSerializer.Deserialize<List<WidgetDefinition>>(File.ReadAllText(AppPaths.WidgetsFile), JsonDefaults.Options) ?? [];
                foreach (var w in list) WidgetValidator.Validate(w);
                return list;
            }
        }
        catch (Exception ex)
        {
            Log.Error("widgets.json is unreadable; keeping a backup and starting fresh", ex);
            try { File.Copy(AppPaths.WidgetsFile, AppPaths.WidgetsFile + ".bad", overwrite: true); } catch { }
        }
        return Defaults();
    }

    public static void Save(IEnumerable<WidgetDefinition> widgets)
    {
        try
        {
            AtomicFile.WriteAllText(AppPaths.WidgetsFile, JsonSerializer.Serialize(widgets.ToList(), JsonDefaults.Options));
        }
        catch (Exception ex)
        {
            Log.Error("Saving widgets failed", ex);
        }
    }

    public static List<WidgetDefinition> Import(string json)
    {
        var trimmed = json.TrimStart();
        var list = trimmed.StartsWith('[')
            ? JsonSerializer.Deserialize<List<WidgetDefinition>>(json, JsonDefaults.Options) ?? []
            : [WidgetDefinition.FromJson(json)];
        foreach (var w in list)
        {
            w.Id = WidgetDefinition.NewId();
            var result = WidgetValidator.Validate(w);
            if (!result.IsValid) throw new InvalidDataException($"“{w.Title}”: {string.Join(" ", result.Errors)}");
        }
        return list;
    }

    /// <summary>A Quick Settings–like starter set so the panel is useful before the AI builds anything.</summary>
    public static List<WidgetDefinition> Defaults()
    {
        var json = """
        [
          {"title":"Wi-Fi","icon":"wifi","size":"2x1","controls":[{"type":"toggle","label":"Wi-Fi","state":{"type":"radio.get","kind":"wifi"},"onValue":"on","on":{"type":"radio.set","kind":"wifi","state":"on"},"off":{"type":"radio.set","kind":"wifi","state":"off"}}]},
          {"title":"Bluetooth","icon":"bluetooth","size":"2x1","controls":[{"type":"toggle","label":"Bluetooth","state":{"type":"radio.get","kind":"bluetooth"},"onValue":"on","on":{"type":"radio.set","kind":"bluetooth","state":"on"},"off":{"type":"radio.set","kind":"bluetooth","state":"off"}}]},
          {"title":"Volume","icon":"volume_up","size":"2x1","controls":[{"type":"slider","label":"Volume","min":0,"max":100,"step":1,"icon":"volume_up","state":{"type":"audio.getVolume"},"action":{"type":"audio.setVolume","level":"{value}"},"format":"{value}%"}]},
          {"title":"Brightness","icon":"brightness_6","size":"2x1","controls":[{"type":"slider","label":"Brightness","min":0,"max":100,"step":1,"icon":"brightness_6","state":{"type":"display.getBrightness"},"action":{"type":"display.setBrightness","level":"{value}"},"format":"{value}%"}]},
          {"title":"Dark mode","icon":"dark_mode","size":"1x1","controls":[{"type":"toggle","label":"Dark","state":{"type":"theme.get"},"onValue":"dark","on":{"type":"theme.set","mode":"dark"},"off":{"type":"theme.set","mode":"light"}}]},
          {"title":"Mute","icon":"volume_off","size":"1x1","controls":[{"type":"toggle","label":"Mute","state":{"type":"audio.getMute"},"on":{"type":"audio.setMute","mute":"true"},"off":{"type":"audio.setMute","mute":"false"}}]},
          {"title":"Snip","icon":"screenshot_region","size":"1x1","controls":[{"type":"button","label":"Snip","action":{"type":"keys.send","keys":"Win+Shift+S"}}]},
          {"title":"Lock","icon":"lock","size":"1x1","controls":[{"type":"button","label":"Lock","action":{"type":"power.action","action":"lock"}}]}
        ]
        """;
        var list = JsonSerializer.Deserialize<List<WidgetDefinition>>(json, JsonDefaults.Options)!;
        if (!SafeIsBrightnessSupported()) list.RemoveAll(w => w.Title == "Brightness");
        foreach (var w in list) WidgetValidator.Validate(w);
        return list;
    }

    private static bool SafeIsBrightnessSupported()
    {
        try { return BrightnessControl.IsSupported(); }
        catch { return false; }
    }
}
