namespace ClaudeMon.UI;

/// <summary>
/// The resize math behind the "Usage &amp; costs" window (#110): how the height left over after
/// the fixed chrome is shared between the two tables, and how each table's columns divide up its
/// width. Kept as a pure computation (no WinForms) so the behaviour is unit-testable, mirroring
/// <see cref="TabStripLayout"/> and <see cref="TaskbarBarLayout"/>. Every input and output is
/// physical pixels — <see cref="UsageBreakdownForm"/> scales its logical (96-DPI) metrics via
/// <see cref="DpiScale"/> before calling in.
/// </summary>
internal static class UsageBreakdownLayout
{
    /// <summary>
    /// Splits <paramref name="available"/> between the two tables, giving the odd pixel to the
    /// second one. Both are floored at <paramref name="minHeight"/>: the window's
    /// <c>MinimumSize</c> normally keeps <paramref name="available"/> above <c>2 × minHeight</c>,
    /// but the floor means an unexpectedly tall chrome (a wrapped hint line, an odd system font)
    /// still leaves usable headers instead of collapsing a table to nothing.
    /// </summary>
    public static (int First, int Second) SplitTableHeights(int available, int minHeight)
    {
        if (minHeight < 0)
            minHeight = 0;
        if (available < 2 * minHeight)
            return (minHeight, minHeight);

        var first = available / 2;
        return (first, available - first);
    }

    /// <summary>
    /// The seven column widths for one table. The six numeric columns keep their scaled widths
    /// and the first column absorbs everything else, so the tables grow horizontally with the
    /// window. The vertical scrollbar's width is reserved up front so a long 30-day list doesn't
    /// force a horizontal scrollbar the moment the vertical one appears. The first column is
    /// floored at <paramref name="minFirst"/> so it can never collapse.
    /// </summary>
    /// <param name="availableWidth">
    /// The table's width inside its border, <em>ignoring</em> any visible vertical scrollbar —
    /// that width is what this method reserves, so passing a scrollbar-adjusted client width
    /// would deduct it twice and leave a dead strip.
    /// </param>
    public static int[] ColumnWidths(int availableWidth, int scrollBarWidth, int numeric, int cost, int minFirst)
    {
        if (numeric < 0)
            numeric = 0;
        if (cost < 0)
            cost = 0;
        if (minFirst < 0)
            minFirst = 0;

        var reserved = Math.Max(0, scrollBarWidth) + (numeric * 5) + cost;
        var first = Math.Max(minFirst, availableWidth - reserved);
        return [first, numeric, numeric, numeric, numeric, numeric, cost];
    }

    /// <summary>
    /// The available width at which <see cref="ColumnWidths"/> stops squeezing the first column —
    /// i.e. the narrowest table that still renders every header at its natural width. Drives the
    /// window's minimum width, and is exactly wide enough that a table showing its vertical
    /// scrollbar still fits its clamped columns without a horizontal one.
    /// </summary>
    public static int MinTableWidth(int scrollBarWidth, int numeric, int cost, int minFirst) =>
        Math.Max(0, minFirst) + (Math.Max(0, numeric) * 5) + Math.Max(0, cost) + Math.Max(0, scrollBarWidth);
}
