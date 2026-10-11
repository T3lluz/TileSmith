namespace TileSmith.Core.Widgets;

/// <summary>
/// The panel's grid and the space each part of a tile takes, in device-independent pixels. The renderer draws
/// with these numbers, the validator checks widgets against them and the AI is told about them, so a widget that
/// passes validation always fits its tile.
/// </summary>
public static class TileLayout
{
    public const double CellWidth = 118;
    public const double CellHeight = 72;
    public const double Gap = 10;
    public const int Columns = 4;

    // card tiles (more than one control, or anything taller than one row)
    public const double CardPaddingX = 14;
    public const double CardPaddingTop = 12;
    public const double CardPaddingBottom = 12;
    public const double CardHeader = 22;
    public const double CardHeaderWithSubtitle = 38;
    public const double RowGap = 8;
    public const double ToggleRow = 32;
    public const double StatusRow = 22;
    public const double SliderRow = 40;
    public const double ChoiceRow = 36;
    public const double ChoiceLabel = 18;
    public const double ButtonRow = 40;

    /// <summary>Roughly how wide text is at the tile font sizes (Segoe UI Variable, 12.5–13 px).</summary>
    public const double CharWidth = 7.2;

    public static double Width(int cells) => cells * CellWidth + (cells - 1) * Gap;
    public static double Height(int cells) => cells * CellHeight + (cells - 1) * Gap;

    /// <summary>Rows of controls inside a card: consecutive buttons share a row.</summary>
    public static IReadOnlyList<IReadOnlyList<ControlDefinition>> CardRows(WidgetDefinition widget)
    {
        var rows = new List<IReadOnlyList<ControlDefinition>>();
        var buttons = new List<ControlDefinition>();
        foreach (var c in widget.Controls)
        {
            if (c.Type == ControlType.Button)
            {
                buttons.Add(c);
                if (buttons.Count == 4) { rows.Add(buttons); buttons = []; }
                continue;
            }
            if (buttons.Count > 0) { rows.Add(buttons); buttons = []; }
            rows.Add([c]);
        }
        if (buttons.Count > 0) rows.Add(buttons);
        return rows;
    }

    /// <summary>Whether the widget is drawn as a single control filling the tile (not as a card).</summary>
    public static bool IsSingle(WidgetDefinition widget, CellSize size)
    {
        if (widget.Controls.Count != 1) return false;
        var c = widget.Controls[0];
        return c.Type switch
        {
            ControlType.Toggle or ControlType.Button => true,
            ControlType.Slider or ControlType.Choice or ControlType.Status => size.H == 1,
            _ => false,
        };
    }

    public static double RowHeight(IReadOnlyList<ControlDefinition> row, WidgetDefinition widget)
    {
        var c = row[0];
        return c.Type switch
        {
            ControlType.Button => ButtonRow,
            ControlType.Toggle => ToggleRow,
            ControlType.Slider => SliderRow,
            ControlType.Choice => ChoiceRow + (HasOwnLabel(c, widget) ? ChoiceLabel + 4 : 0),
            _ => StatusRow,
        };
    }

    public static bool HasOwnLabel(ControlDefinition c, WidgetDefinition widget) =>
        !string.IsNullOrWhiteSpace(c.Label) && !string.Equals(c.Label, widget.Title, StringComparison.OrdinalIgnoreCase);

    /// <summary>Height a card needs for all of its rows.</summary>
    public static double CardHeight(WidgetDefinition widget)
    {
        var rows = CardRows(widget);
        var header = string.IsNullOrWhiteSpace(widget.Subtitle) ? CardHeader : CardHeaderWithSubtitle;
        var content = rows.Sum(r => RowHeight(r, widget)) + RowGap * rows.Count; // a gap under the header and between rows
        return CardPaddingTop + header + content + CardPaddingBottom;
    }

    /// <summary>Grid rows needed for a height in pixels.</summary>
    public static int RowsFor(double height) => Math.Max(1, (int)Math.Ceiling((height + Gap - 0.5) / (CellHeight + Gap)));

    /// <summary>Width a segmented choice needs so every option label fits on one line.</summary>
    public static double ChoiceWidth(ControlDefinition c)
    {
        var options = c.Options ?? [];
        if (options.Count == 0) return 0;
        var widest = options.Max(o => (o.Label?.Length ?? 0) * CharWidth + (string.IsNullOrWhiteSpace(o.Icon) ? 0 : 22));
        return options.Count * (widest + 20) + 6;
    }

    /// <summary>The smallest footprint the widget's content fits in.</summary>
    public static CellSize Minimum(WidgetDefinition widget)
    {
        var controls = widget.Controls;
        if (controls.Count == 0) return new(1, 1);
        if (controls.Count == 1)
        {
            var c = controls[0];
            switch (c.Type)
            {
                case ControlType.Toggle or ControlType.Button:
                    return new(1, 1);
                case ControlType.Slider:
                    return new(2, 1);
                case ControlType.Status:
                    return new(1, 1);
                case ControlType.Choice:
                    // one-row bar: 2 wide puts a small label over the segments, 4 wide puts the title beside them
                    var need = ChoiceWidth(c);
                    if (need <= Width(2) - 2 * CardPaddingX) return new(2, 1);
                    if (need <= Width(4) - 2 * CardPaddingX - 150) return new(4, 1);
                    break;
            }
        }
        // a card: wide enough for its widest row, tall enough for all rows
        var width = 2;
        foreach (var row in CardRows(widget))
        {
            var c = row[0];
            var needed = c.Type switch
            {
                ControlType.Choice => ChoiceWidth(c),
                ControlType.Button => row.Count * 64 + (row.Count - 1) * 8,
                _ => 0,
            };
            if (needed > Width(2) - 2 * CardPaddingX) width = 4;
        }
        return new(width, RowsFor(CardHeight(widget)));
    }

    /// <summary>Most characters a label can show at a size before it's cut off with "…".</summary>
    public static int LabelLimit(CellSize size, bool single) => (size.W, single) switch
    {
        (1, _) => 13,
        (2, true) => 18,
        (2, false) => 22,
        _ => 40,
    };

    /// <summary>Plain-language description of every size for the AI prompt.</summary>
    public static string DescribeForPrompt() => $$"""
        The panel is a grid of {{Columns}} columns. A cell is {{CellWidth}} px wide and {{CellHeight}} px tall with {{Gap}} px gaps,
        so a full-width tile is {{Width(4)}} px. Exact tile sizes and what fits:
        - "1x1" ({{Width(1)}}×{{Height(1)}}): ONE button or toggle — an icon over a label of at most 13 characters.
        - "2x1" ({{Width(2)}}×{{Height(1)}}): ONE toggle/button as a Quick Settings tile (icon, label ≤ 18 chars, on/off text ≤ 20 chars),
          or ONE status value (≤ 16 chars), or ONE slider, or ONE choice whose options fit in {{Width(2) - 2 * CardPaddingX}} px
          (2 options of ≤ 10 chars, or 3 of ≤ 6 chars).
        - "4x1" ({{Width(4)}}×{{Height(1)}}): ONE slider, ONE status with a longer value, or ONE choice — title on the left, segments on the
          right in about {{Width(4) - 2 * CardPaddingX - 150}} px: 3 options of ≤ 11 chars ("2560 × 1440"), 4 of ≤ 8, or 5 of ≤ 6.
        - "2x2" ({{Width(2)}}×{{Height(2)}}) and "4x2" ({{Width(4)}}×{{Height(2)}}): a card — title (+ optional subtitle) and rows of controls.
        - "2x3" / "4x3" (… ×{{Height(3)}}): a taller card for up to about 5 rows. Nothing is taller than 3 rows.
        Card rows: toggle {{ToggleRow}} px, status {{StatusRow}} px, slider {{SliderRow}} px, choice {{ChoiceRow}} px (+{{ChoiceLabel + 4}} px when it has
        its own label), a row of up to 4 buttons {{ButtonRow}} px; {{RowGap}} px between rows; title {{CardHeader}} px ({{CardHeaderWithSubtitle}} with a subtitle);
        {{CardPaddingTop + CardPaddingBottom}} px padding. So a 2-row card holds about 2 control rows and a 3-row card about 4.
        Text is about {{CharWidth}} px per character: keep labels short, sentence case, no trailing punctuation.
        TileSmith measures every widget you save: if the content needs more room it grows the tile and tells you, and if it
        can't fit in 3 rows the save fails — then split it into two widgets or drop a control.
        """;
}
