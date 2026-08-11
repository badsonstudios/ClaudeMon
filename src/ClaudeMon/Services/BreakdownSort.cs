namespace ClaudeMon.Services;

using ClaudeMon.Models;

/// <summary>
/// The seven columns of a breakdown table, in the order
/// <c>UsageBreakdownForm</c> adds them — so a clicked column index casts
/// straight to this.
/// </summary>
internal enum BreakdownSortColumn
{
    Name = 0,
    Input = 1,
    Output = 2,
    CacheWrite = 3,
    CacheRead = 4,
    Tokens = 5,
    Cost = 6,
}

/// <summary>Which column one breakdown table is sorted by, and in which direction.</summary>
internal readonly record struct BreakdownSortState(BreakdownSortColumn Column, bool Ascending)
{
    /// <summary>
    /// The order a table opens in — cost descending, which is exactly the order
    /// <c>LocalUsageStore</c> hands the rows over in, so opening the window looks
    /// the same as it did before sorting existed.
    /// </summary>
    public static BreakdownSortState Default => new(BreakdownSortColumn.Cost, Ascending: false);

    /// <summary>
    /// The state after a click on <paramref name="columnIndex"/>: clicking the column that is
    /// already sorted reverses it, clicking any other switches to it in its natural direction.
    /// An index outside the table's columns leaves the state untouched.
    /// </summary>
    public BreakdownSortState Toggle(int columnIndex)
    {
        var column = (BreakdownSortColumn)columnIndex;
        if (!Enum.IsDefined(column))
            return this;

        return column == Column
            ? this with { Ascending = !Ascending }
            : new BreakdownSortState(column, NaturalAscending(column));
    }

    // Names read best A→Z; every numeric column is really the question "who used the most?",
    // so those start big-first — which also keeps the opening cost-descending order.
    private static bool NaturalAscending(BreakdownSortColumn column) => column == BreakdownSortColumn.Name;
}

/// <summary>
/// Row ordering for the "Usage &amp; costs" tables (#111). Sorts the underlying
/// <see cref="BreakdownRow"/> numbers rather than the formatted cell text —
/// <c>LocalCostText.FormatTokens</c> renders "1.2M" and "900K", which compare the wrong way
/// round as strings — and always leaves the totals row at the bottom, where it belongs
/// regardless of how the body is ordered. Pure (no WinForms) so the behaviour is unit-testable,
/// mirroring <c>UsageBreakdownLayout</c>.
///
/// Lives beside <see cref="BreakdownCsv"/> rather than in <c>UI</c> because the order is no
/// longer only the table's: the CSV export writes its rows through this same helper, so a file
/// exported after a header click matches what was on screen (#119).
/// </summary>
internal static class BreakdownSort
{
    // Linguistic rather than ordinal so accented names land next to their plain spelling, but
    // invariant rather than current-culture so the same data always sorts the same way.
    private static readonly StringComparer NameComparer = StringComparer.InvariantCultureIgnoreCase;

    /// <summary>
    /// The rows one table shows, top to bottom: <paramref name="rows"/> sorted per
    /// <paramref name="state"/>, then <paramref name="totals"/> appended last when there is one.
    /// Ties keep the order they arrived in (LINQ's sorts are stable), so equal cells fall back to
    /// the store's cost-then-tokens ordering.
    /// </summary>
    public static IReadOnlyList<BreakdownRow> Order(
        IReadOnlyList<BreakdownRow> rows, BreakdownRow? totals, BreakdownSortState state)
    {
        var ordered = new List<BreakdownRow>(rows.Count + 1);
        ordered.AddRange(Sort(rows, state));
        if (totals is not null)
            ordered.Add(totals);
        return ordered;
    }

    private static IEnumerable<BreakdownRow> Sort(IReadOnlyList<BreakdownRow> rows, BreakdownSortState state)
    {
        switch (state.Column)
        {
            case BreakdownSortColumn.Name:
                return state.Ascending
                    ? rows.OrderBy(r => r.DisplayName, NameComparer)
                    : rows.OrderByDescending(r => r.DisplayName, NameComparer);

            // The cost cell has non-numeric display forms ("—" when nothing priced, "≥$x" when an
            // unpriced model contributed), so it too has to be ordered by the number behind it.
            case BreakdownSortColumn.Cost:
                return state.Ascending
                    ? rows.OrderBy(r => r.CostUsd)
                    : rows.OrderByDescending(r => r.CostUsd);

            default:
                var tokens = TokensOf(state.Column);
                return state.Ascending
                    ? rows.OrderBy(tokens)
                    : rows.OrderByDescending(tokens);
        }
    }

    private static Func<BreakdownRow, long> TokensOf(BreakdownSortColumn column) => column switch
    {
        BreakdownSortColumn.Input => r => r.InputTokens,
        BreakdownSortColumn.Output => r => r.OutputTokens,
        BreakdownSortColumn.CacheWrite => r => r.CacheWriteTokens,
        BreakdownSortColumn.CacheRead => r => r.CacheReadTokens,
        _ => r => r.TotalTokens,
    };
}
