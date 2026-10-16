using System.Text.Json.Nodes;

namespace TileSmith.Core.Tiles;

/// <summary>
/// Every design you made or added, and which of them are on the panel, in order. Saved in designs.json; every change
/// saves straight away (there is no Save step anywhere). Not thread-safe: the UI thread owns it.
/// </summary>
public sealed class TileLibrary
{
    private readonly List<TileDesign> _designs = [];
    private readonly List<string> _panel = [];
    private readonly string _file;

    public TileLibrary(string? file = null)
    {
        _file = file ?? DefaultFile;
    }

    public static string DefaultFile => Path.Combine(AppPaths.DataDir, "designs.json");

    /// <summary>Something was added, changed, removed or moved: the id of the design (null for the panel's order).</summary>
    public event Action<string?>? Changed;

    /// <summary>Every design, newest change first.</summary>
    public IReadOnlyList<TileDesign> Designs => _designs.OrderByDescending(d => d.UpdatedAt).ToList();

    /// <summary>The designs on the panel, in the panel's order.</summary>
    public IReadOnlyList<TileDesign> Panel => _panel.Select(Find).OfType<TileDesign>().ToList();

    public TileDesign? Find(string? id) => id is null ? null : _designs.FirstOrDefault(d => d.Id == id);

    public bool IsPlaced(string id) => _panel.Contains(id);

    public int PanelIndexOf(string id) => _panel.IndexOf(id);

    // ── Loading ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads designs.json. On the first start of this version, widgets from 0.4 (widgets.json) are converted; with
    /// neither, the panel starts with a few Quick Settings so it is useful before the AI builds anything.
    /// </summary>
    public void Load(TileEnvironment env)
    {
        _designs.Clear();
        _panel.Clear();
        if (File.Exists(_file))
        {
            try
            {
                var root = JsonNode.Parse(File.ReadAllText(_file), documentOptions: JsonDefaults.DocumentOptions) as JsonObject;
                if (root?["designs"] is JsonArray designs)
                {
                    foreach (var o in designs.OfType<JsonObject>())
                    {
                        try { _designs.Add(TileCodec.Parse(o)); }
                        catch (Exception ex) { Log.Warn($"Skipped an unreadable design: {ex.Message}"); }
                    }
                }
                if (root?["panel"] is JsonArray panel)
                {
                    foreach (var id in panel.Select(TileCodec.WidgetText).OfType<string>())
                    {
                        if (_designs.Any(d => d.Id == id) && !_panel.Contains(id)) _panel.Add(id);
                    }
                }
                return;
            }
            catch (Exception ex)
            {
                Log.Error("designs.json is unreadable; keeping a copy and starting again", ex);
                try { File.Copy(_file, _file + ".bad", overwrite: true); } catch { }
            }
        }

        var legacy = Path.Combine(Path.GetDirectoryName(_file)!, "widgets.json");
        if (File.Exists(legacy))
        {
            try
            {
                var converted = TileMigration.FromWidgetsJson(File.ReadAllText(legacy));
                foreach (var d in converted)
                {
                    _designs.Add(d);
                    _panel.Add(d.Id);
                }
                Log.Info($"Converted {converted.Count} widgets from widgets.json");
                Save();
                return;
            }
            catch (Exception ex)
            {
                Log.Error("Couldn't convert widgets.json", ex);
            }
        }
        foreach (var d in Defaults(env))
        {
            _designs.Add(d);
            _panel.Add(d.Id);
        }
        Save();
    }

    /// <summary>A Quick Settings start: switches, the volume (and brightness on laptops) and shortcuts.</summary>
    public static IReadOnlyList<TileDesign> Defaults(TileEnvironment env)
    {
        var ids = new List<string> { "quick", "volume" };
        if (env.HasBrightness) ids.Add("brightness");
        ids.Add("shortcuts");
        return ids.Select(TileStarters.ById).OfType<TileStarters.Starter>().Where(s => s.IsAvailable(env)).Select(TileStarters.Instantiate).ToList();
    }

    public void Save()
    {
        try
        {
            var root = new JsonObject
            {
                ["v"] = 1,
                ["panel"] = new JsonArray(_panel.Select(id => (JsonNode)id).ToArray()),
                ["designs"] = new JsonArray(_designs.Select(d => (JsonNode)TileCodec.ToJson(d)).ToArray()),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            AtomicFile.WriteAllText(_file, root.ToJsonString(JsonDefaults.Options));
        }
        catch (Exception ex)
        {
            Log.Error("Saving designs failed", ex);
        }
    }

    // ── Changing ─────────────────────────────────────────────────────────────────────

    /// <summary>Adds or replaces a design (its update time is now). Returns what was stored.</summary>
    public TileDesign Upsert(TileDesign design, bool touch = true)
    {
        var d = TileRules.Sanitize(touch ? design with { UpdatedAt = TileDesign.Now() } : design);
        var i = _designs.FindIndex(x => x.Id == d.Id);
        if (i >= 0) _designs[i] = d;
        else _designs.Add(d);
        Save();
        Changed?.Invoke(d.Id);
        return d;
    }

    /// <summary>Removes a design everywhere. Returns it with where it sat on the panel (for Undo).</summary>
    public (TileDesign? Design, int PanelIndex) Delete(string id)
    {
        var d = Find(id);
        if (d is null) return (null, -1);
        var index = _panel.IndexOf(id);
        _designs.Remove(d);
        _panel.Remove(id);
        Save();
        Changed?.Invoke(id);
        return (d, index);
    }

    /// <summary>Puts a design on the panel (at the end, or at <paramref name="index"/>); stores it first when it is new.</summary>
    public void Place(TileDesign design, int index = -1)
    {
        if (Find(design.Id) is null) _designs.Add(TileRules.Sanitize(design));
        _panel.Remove(design.Id);
        _panel.Insert(index < 0 || index > _panel.Count ? _panel.Count : index, design.Id);
        Save();
        Changed?.Invoke(design.Id);
    }

    /// <summary>Takes a design off the panel; it stays in the library (and in the Studio's list). Returns where it was.</summary>
    public int Unplace(string id)
    {
        var index = _panel.IndexOf(id);
        if (index < 0) return -1;
        _panel.RemoveAt(index);
        Save();
        Changed?.Invoke(id);
        return index;
    }

    public void Reorder(IReadOnlyList<string> ids)
    {
        var ordered = ids.Where(_panel.Contains).Distinct().ToList();
        if (ordered.Count != _panel.Count || ordered.SequenceEqual(_panel)) return;
        _panel.Clear();
        _panel.AddRange(ordered);
        Save();
        Changed?.Invoke(null);
    }

    /// <summary>A copy with its own id, placed right after the original when that is on the panel.</summary>
    public TileDesign Duplicate(TileDesign design)
    {
        var now = TileDesign.Now();
        var name = design.Name + " copy";
        var copy = design with { Id = TileDesign.NewId(), Name = name.Length > 40 ? name[..40] : name, CreatedAt = now, UpdatedAt = now, Chat = [] };
        _designs.Add(copy);
        var at = _panel.IndexOf(design.Id);
        if (at >= 0) _panel.Insert(at + 1, copy.Id);
        Save();
        Changed?.Invoke(copy.Id);
        return copy;
    }

    /// <summary>Back to the starter panel (the library keeps every design).</summary>
    public void ResetPanel(TileEnvironment env)
    {
        _panel.Clear();
        foreach (var d in Defaults(env))
        {
            _designs.Add(d);
            _panel.Add(d.Id);
        }
        Save();
        Changed?.Invoke(null);
    }
}
