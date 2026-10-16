using System.Globalization;
using System.Text.Json.Nodes;

namespace TileSmith.Core.Tiles;

/// <summary>One row of a list block: a title, a line under it and something at the end.</summary>
/// <param name="Color"><c>#RRGGBB</c>: a status's or a service's own colour for the row's mark.</param>
/// <param name="Image">A picture for the row: a thumbnail, a poster (a URL or a file path).</param>
/// <param name="Link">Where a click on the row goes: an article, a video.</param>
/// <param name="Icon">A Material Symbols name for the row's mark (a forecast day's sky).</param>
public sealed record TileItem(
    string Title,
    string? Sub = null,
    string? Trailing = null,
    string? Color = null,
    string? Image = null,
    string? Link = null,
    string? Icon = null);

/// <summary>Whether a source could be read, and if not, why.</summary>
public enum TileValueState { Ok, Empty, Needs, Error }

/// <summary>
/// What a source read, in a shape every block can draw: a number with its goal and unit, words, rows, a series or
/// a moment. Blocks take what they need: a ring <see cref="Number"/> over <see cref="Goal"/>, a metric
/// <see cref="Text"/> (or the number) with <see cref="Unit"/> and <see cref="Sub"/>, a list <see cref="Items"/>, a
/// chart <see cref="Series"/>, a countdown <see cref="At"/>, a toggle <see cref="Active"/>, a choice
/// <see cref="Selected"/> among <see cref="Options"/>.
/// </summary>
public sealed record TileValue
{
    public string? Text { get; init; }
    public double? Number { get; init; }
    public double? Goal { get; init; }
    public string? Unit { get; init; }
    public string? Sub { get; init; }
    /// <summary>A Material Symbols name that fits the value: today's sky, charging, the network type.</summary>
    public string? Icon { get; init; }
    /// <summary>A moment (Unix ms) for countdowns.</summary>
    public long? At { get; init; }
    public IReadOnlyList<TileItem> Items { get; init; } = [];
    public IReadOnlyList<double> Series { get; init; } = [];
    /// <summary>The series' first and last labels, for a chart's ends ("2 min", "Now").</summary>
    public string? SeriesStart { get; init; }
    public string? SeriesEnd { get; init; }
    /// <summary>What the value itself links to (the "open" tap).</summary>
    public string? Url { get; init; }
    /// <summary>One picture for the value: album art, a webcam frame (a URL or a file path).</summary>
    public string? Image { get; init; }
    /// <summary>Whether the thing is on or running now: Wi-Fi on, music playing, online.</summary>
    public bool? Active { get; init; }
    /// <summary>A choice's options, as the source offers them (resolutions, refresh rates).</summary>
    public IReadOnlyList<TileOption> Options { get; init; } = [];
    /// <summary>The option that is in effect now.</summary>
    public string? Selected { get; init; }
    /// <summary>A slider's range, as the source has it.</summary>
    public double? Min { get; init; }
    public double? Max { get; init; }
    public TileValueState State { get; init; } = TileValueState.Ok;
    /// <summary>For Needs / Error / Empty: what to show instead ("Set your town").</summary>
    public string? Note { get; init; }
    public long ReadAt { get; init; }

    /// <summary>The value as one short piece of text: <see cref="Text"/>, else the number formatted.</summary>
    public string? Display => Text ?? (Number is { } n ? FormatNumber(n) : null);

    /// <summary>The value's picture, else its first row's: an image block on a list source shows the newest.</summary>
    public string? Picture => Image ?? Items.FirstOrDefault(i => i.Image is not null)?.Image;

    public double? Fraction(double? goalOverride = null)
    {
        if (Number is not { } n) return null;
        var g = goalOverride ?? Goal;
        if (g is not > 0) return null;
        var lo = Min ?? 0;
        return Math.Clamp((n - lo) / (g.Value - lo), 0, 1);
    }

    public static TileValue Needs(string note) => new() { State = TileValueState.Needs, Note = note };
    public static TileValue EmptyValue(string note) => new() { State = TileValueState.Empty, Note = note };
    public static TileValue Failed(string note) => new() { State = TileValueState.Error, Note = note };

    /// <summary><c>7,412</c>, <c>12.5</c>, <c>1.2M</c>: grouped, with no decimals once it is big.</summary>
    public static string FormatNumber(double v)
    {
        var c = CultureInfo.CurrentCulture;
        if (double.IsNaN(v) || double.IsInfinity(v)) return "—";
        var a = Math.Abs(v);
        if (a >= 1_000_000) return (v / 1_000_000).ToString("0.#", c) + "M";
        if (a >= 100_000) return (v / 1000).ToString("0", c) + "k";
        if (a >= 1000 || v == Math.Floor(v)) return v.ToString("#,0", c);
        if (a >= 10) return v.ToString("0.#", c);
        return v.ToString("0.##", c);
    }

    /// <summary>"12.4 MB", "980 KB": bytes for people.</summary>
    public static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (Math.Abs(bytes) >= 1000 && i < units.Length - 1)
        {
            bytes /= 1024;
            i++;
        }
        return (i == 0 ? bytes.ToString("0", CultureInfo.CurrentCulture) : bytes.ToString(bytes >= 100 ? "0" : "0.#", CultureInfo.CurrentCulture)) + " " + units[i];
    }

    /// <summary>"3d 4h", "2h 15m", "45m", "30s".</summary>
    public static string FormatDuration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = -t;
        if (t.TotalDays >= 1) return t.Hours > 0 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{(int)t.TotalDays}d";
        if (t.TotalHours >= 1) return t.Minutes > 0 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{(int)t.TotalHours}h";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return $"{Math.Max(0, (int)t.TotalSeconds)}s";
    }
}

public static class TileValueCodec
{
    public static JsonObject ToJson(TileValue v)
    {
        var o = new JsonObject();
        if (v.Text is not null) o["t"] = v.Text;
        if (v.Number is { } n && double.IsFinite(n)) o["n"] = n;
        if (v.Goal is { } g && double.IsFinite(g)) o["g"] = g;
        if (v.Unit is not null) o["u"] = v.Unit;
        if (v.Sub is not null) o["s"] = v.Sub;
        if (v.Icon is not null) o["i"] = v.Icon;
        if (v.At is { } at) o["at"] = at;
        if (v.Items.Count > 0)
        {
            o["items"] = new JsonArray(v.Items.Select(r =>
            {
                var row = new JsonObject { ["t"] = r.Title };
                if (r.Sub is not null) row["s"] = r.Sub;
                if (r.Trailing is not null) row["e"] = r.Trailing;
                if (r.Color is not null) row["c"] = r.Color;
                if (r.Image is not null) row["img"] = r.Image;
                if (r.Link is not null) row["l"] = r.Link;
                if (r.Icon is not null) row["i"] = r.Icon;
                return (JsonNode)row;
            }).ToArray());
        }
        if (v.Series.Count > 0) o["series"] = new JsonArray(v.Series.Select(x => (JsonNode?)(double.IsFinite(x) ? JsonValue.Create(x) : null)).ToArray());
        if (v.SeriesStart is not null) o["s0"] = v.SeriesStart;
        if (v.SeriesEnd is not null) o["s1"] = v.SeriesEnd;
        if (v.Url is not null) o["url"] = v.Url;
        if (v.Image is not null) o["img"] = v.Image;
        if (v.Active is { } on) o["on"] = on;
        if (v.Options.Count > 0) o["opts"] = new JsonArray(v.Options.Select(x => (JsonNode)new JsonObject { ["l"] = x.Label, ["v"] = x.Value }).ToArray());
        if (v.Selected is not null) o["sel"] = v.Selected;
        if (v.Min is { } min) o["min"] = min;
        if (v.Max is { } max) o["max"] = max;
        if (v.State != TileValueState.Ok) o["state"] = v.State.ToString();
        if (v.Note is not null) o["note"] = v.Note;
        o["read"] = v.ReadAt;
        return o;
    }

    public static TileValue Parse(JsonObject o)
    {
        string? S(JsonObject x, string k) => TileCodec.Str(x, k);
        return new TileValue
        {
            Text = S(o, "t"),
            Number = TileCodec.Dbl(o, "n"),
            Goal = TileCodec.Dbl(o, "g"),
            Unit = S(o, "u"),
            Sub = S(o, "s"),
            Icon = S(o, "i"),
            At = TileCodec.Long(o, "at"),
            Items = o["items"] is JsonArray items
                ? items.OfType<JsonObject>().Select(r => new TileItem(S(r, "t") ?? "", S(r, "s"), S(r, "e"), S(r, "c"), S(r, "img"), S(r, "l"), S(r, "i"))).ToList()
                : [],
            Series = o["series"] is JsonArray series
                ? series.Select(x => x is null ? double.NaN : double.TryParse(TileCodec.WidgetText(x), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN).ToList()
                : [],
            SeriesStart = S(o, "s0"),
            SeriesEnd = S(o, "s1"),
            Url = S(o, "url"),
            Image = S(o, "img"),
            Active = TileCodec.Bool(o, "on"),
            Options = o["opts"] is JsonArray opts ? opts.OfType<JsonObject>().Select(x => new TileOption(S(x, "l") ?? "", S(x, "v") ?? "")).ToList() : [],
            Selected = S(o, "sel"),
            Min = TileCodec.Dbl(o, "min"),
            Max = TileCodec.Dbl(o, "max"),
            State = Enum.TryParse<TileValueState>(S(o, "state"), true, out var state) ? state : TileValueState.Ok,
            Note = S(o, "note"),
            ReadAt = TileCodec.Long(o, "read") ?? 0,
        };
    }

    public static string EncodeMap(IReadOnlyDictionary<string, TileValue> values)
    {
        var o = new JsonObject();
        foreach (var (k, v) in values) o[k] = ToJson(v);
        return o.ToJsonString();
    }

    public static Dictionary<string, TileValue> ParseMap(string? raw)
    {
        var map = new Dictionary<string, TileValue>();
        if (string.IsNullOrWhiteSpace(raw)) return map;
        try
        {
            if (JsonNode.Parse(raw) is not JsonObject o) return map;
            foreach (var (k, v) in o)
            {
                if (v is JsonObject value) map[k] = Parse(value);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // a damaged cache only costs a re-read
        }
        return map;
    }
}
