namespace TileSmith.Core.Tiles;

/// <summary>
/// Lays a design out at the tile's real size, in device-independent pixels, before anything is drawn: the frame's
/// padding, then rows by their height weights and cells by their width weights, with even gaps between. A row or cell
/// that would come out smaller than its block can show is dropped (the last row first, then the last cell of a row),
/// so a widget made smaller loses whole blocks rather than squeezing them until their text clips.
/// </summary>
public static class TileLayout
{
    // ── The panel's grid ─────────────────────────────────────────────────────────────
    public const double CellWidth = 118;
    public const double CellHeight = 72;
    public const double CellGap = 10;

    /// <summary>Width of a tile <paramref name="cells"/> wide (4 = the panel's full width at 4 columns).</summary>
    public static double Width(int cells) => cells * CellWidth + (cells - 1) * CellGap;

    public static double Height(int cells) => cells * CellHeight + (cells - 1) * CellGap;

    public static (double W, double H) SizeOf(TileSize size) => (Width(size.Cols), Height(size.Rows));

    // ── Inside a tile ────────────────────────────────────────────────────────────────
    public const double Gap = 6;

    /// <summary>The panel's tile corner radius; blocks inside use <see cref="InnerRadius"/>.</summary>
    public const double Radius = 22;
    public const double InnerRadius = 14;

    public sealed record Cell(TileBlock Block, int Index, double Width, double Height);

    public sealed record Row(double Height, IReadOnlyList<Cell> Cells);

    /// <param name="Solo">One control fills the whole tile (a Quick Settings toggle, a pill slider): no frame padding.</param>
    public sealed record Plan(double Pad, double Gap, IReadOnlyList<Row> Rows, int Dropped, bool Solo)
    {
        public IEnumerable<Cell> Cells => Rows.SelectMany(r => r.Cells);
    }

    public static double Pad(double w, double h) => h < 60 ? 6 : Math.Min(w, h) < 130 ? 10 : 12;

    /// <summary>The least height a block of <paramref name="type"/> can show anything useful in.</summary>
    public static double MinHeight(BlockType type) => type switch
    {
        BlockType.Ring => 44,
        BlockType.Chart => 40,
        BlockType.List => 34,
        BlockType.Metric or BlockType.Badge => 38,
        BlockType.Countdown or BlockType.Ai => 34,
        BlockType.Bar or BlockType.Clock or BlockType.Button => 30,
        BlockType.Toggle => 36,
        BlockType.Slider => 32,
        BlockType.Choice => 30,
        BlockType.Text => 22,
        BlockType.Date => 20,
        BlockType.Image => 36,
        BlockType.Carousel => 96,
        BlockType.Media => 50,
        _ => 30,
    };

    /// <summary>The least width a block of <paramref name="type"/> can show anything useful in.</summary>
    public static double MinWidth(BlockType type) => type switch
    {
        BlockType.Ring or BlockType.Badge => 44,
        BlockType.List or BlockType.Chart or BlockType.Bar or BlockType.Ai => 70,
        BlockType.Carousel or BlockType.Media => 120,
        BlockType.Slider => 100,
        BlockType.Choice => 110,
        BlockType.Toggle => 80,
        BlockType.Image => 40,
        _ => 44,
    };

    /// <summary>Whether a design is one control that is the tile itself (Android's Quick Settings tiles and sliders).</summary>
    public static bool IsSolo(TileDesign design)
    {
        var blocks = design.Blocks;
        return blocks.Count == 1 && blocks[0].Style == BlockStyle.Tile
               && blocks[0].Type is BlockType.Toggle or BlockType.Slider or BlockType.Choice or BlockType.Button or BlockType.Image or BlockType.Carousel;
    }

    public static Plan Layout(TileDesign design, double width, double height)
    {
        var solo = IsSolo(design);
        var pad = solo ? 0 : Pad(width, height);
        var innerW = Math.Max(0, width - 2 * pad);
        var innerH = Math.Max(0, height - 2 * pad);
        var rows = new List<(TileRow Row, int First)>();
        var index = 0;
        foreach (var r in design.Rows)
        {
            if (r.Cells.Count > 0) rows.Add((r, index));
            index += r.Cells.Count;
        }
        var total = rows.Sum(r => r.Row.Cells.Count);

        // drop rows from the bottom until every remaining row gets its minimum height
        while (rows.Count > 1)
        {
            var heights = RowHeights(rows.Select(r => r.Row).ToList(), innerH);
            var ok = true;
            for (var i = 0; i < rows.Count; i++)
            {
                if (heights[i] + 0.5 < rows[i].Row.Cells.Max(c => MinHeight(c.Type))) ok = false;
            }
            if (ok) break;
            rows.RemoveAt(rows.Count - 1);
        }
        var finalHeights = RowHeights(rows.Select(r => r.Row).ToList(), innerH);
        var planned = new List<Row>();
        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i].Row.Cells.ToList();
            while (cells.Count > 1)
            {
                var widths = CellWidths(cells, innerW);
                if (cells.Select((c, ci) => widths[ci] + 0.5 >= MinWidth(c.Type)).All(x => x)) break;
                cells.RemoveAt(cells.Count - 1);
            }
            var w = CellWidths(cells, innerW);
            planned.Add(new Row(finalHeights[i], cells.Select((b, ci) => new Cell(b, rows[i].First + ci, w[ci], finalHeights[i])).ToList()));
        }
        return new Plan(pad, Gap, planned, total - planned.Sum(r => r.Cells.Count), solo);
    }

    private static List<double> RowHeights(IReadOnlyList<TileRow> rows, double innerH)
    {
        if (rows.Count == 0) return [];
        var free = innerH - Gap * (rows.Count - 1);
        double sum = rows.Sum(r => Math.Clamp(r.Height, 1, 3));
        return Snap(rows.Select(r => free * Math.Clamp(r.Height, 1, 3) / sum).ToList(), free);
    }

    private static List<double> CellWidths(IReadOnlyList<TileBlock> cells, double innerW)
    {
        var free = innerW - Gap * (cells.Count - 1);
        double sum = cells.Sum(c => Math.Clamp(c.Weight, 1, 4));
        return Snap(cells.Select(c => free * Math.Clamp(c.Weight, 1, 4) / sum).ToList(), free);
    }

    /// <summary>Whole pixels each, the last one taking the remainder, so the parts add up to the room exactly.</summary>
    private static List<double> Snap(List<double> parts, double total)
    {
        if (parts.Count == 0) return parts;
        var whole = parts.Take(parts.Count - 1).Select(Math.Floor).ToList();
        whole.Add(Math.Max(0, total - whole.Sum()));
        return whole;
    }
}
