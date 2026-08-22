namespace ClaudeMon.Services;

using ClaudeMon.Monitoring;

/// <summary>
/// The eight columns of the Limit history table, in the order the form adds them — a clicked
/// column index casts straight to this (the <see cref="BreakdownSortColumn"/> pattern).
/// </summary>
internal enum LimitWindowColumn
{
    Start = 0,
    End = 1,
    Kind = 2,
    Peak = 3,
    Tokens = 4,
    TopModel = 5,
    Capacity = 6,
    Plan = 7,
}

/// <summary>Which column the Limit history table is sorted by, and in which direction.</summary>
internal readonly record struct LimitWindowSortState(LimitWindowColumn Column, bool Ascending)
{
    /// <summary>Newest windows first — the question the tab opens on is "what happened lately".</summary>
    public static LimitWindowSortState Default => new(LimitWindowColumn.End, Ascending: false);

    /// <summary>Click semantics, matching <see cref="BreakdownSortState.Toggle"/> exactly.</summary>
    public LimitWindowSortState Toggle(int columnIndex)
    {
        var column = (LimitWindowColumn)columnIndex;
        if (!Enum.IsDefined(column))
            return this;

        return column == Column
            ? this with { Ascending = !Ascending }
            : new LimitWindowSortState(column, NaturalAscending(column));
    }

    // Text columns read best A→Z; time and numeric columns answer "what's recent / biggest",
    // so they start newest/largest-first.
    private static bool NaturalAscending(LimitWindowColumn column) =>
        column is LimitWindowColumn.Kind or LimitWindowColumn.TopModel or LimitWindowColumn.Plan;
}

/// <summary>
/// Row ordering for the Limit history table (issue #186) — sorts the underlying record values
/// rather than the formatted cell text, the <see cref="BreakdownSort"/> pattern. Windows
/// without a derivable capacity sort below every window with one, whichever direction.
/// </summary>
internal static class LimitWindowSort
{
    private static readonly StringComparer TextComparer = StringComparer.InvariantCultureIgnoreCase;

    public static IReadOnlyList<LimitHistoryRow> Order(
        IReadOnlyList<LimitHistoryRow> rows, LimitWindowSortState state)
    {
        return state.Column switch
        {
            LimitWindowColumn.Start => ByValue(rows, state, r => r.Record.Start),
            LimitWindowColumn.Kind => ByText(rows, state,
                r => LimitHistoryText.KindLabel(r.Record.Kind, r.Record.ScopeModel)),
            LimitWindowColumn.Peak => ByValue(rows, state, r => r.Record.PeakPercent),
            // Raw total, not weighted tokens — the number the cell actually shows.
            LimitWindowColumn.Tokens => ByValue(rows, state, r => LimitWindowCapacity.RawTotal(r.Record)),
            LimitWindowColumn.TopModel => ByText(rows, state,
                r => LimitWindowCapacity.TopModel(r.Record) ?? ""),
            // Capacity-less rows pin below regardless of direction: "—" is an absence, not a zero.
            LimitWindowColumn.Capacity => rows
                .OrderBy(r => r.ImpliedCapacity is null ? 1 : 0)
                .ThenBy(r => state.Ascending
                    ? r.ImpliedCapacity ?? double.MaxValue
                    : -(r.ImpliedCapacity ?? double.MinValue))
                .ToList(),
            LimitWindowColumn.Plan => ByText(rows, state, r => LimitHistoryText.PlanText(r.Record)),
            _ => ByValue(rows, state, r => r.Record.End),
        };
    }

    private static List<LimitHistoryRow> ByValue<T>(
        IReadOnlyList<LimitHistoryRow> rows, LimitWindowSortState state, Func<LimitHistoryRow, T> key) =>
        (state.Ascending ? rows.OrderBy(key) : rows.OrderByDescending(key)).ToList();

    private static List<LimitHistoryRow> ByText(
        IReadOnlyList<LimitHistoryRow> rows, LimitWindowSortState state, Func<LimitHistoryRow, string> key) =>
        (state.Ascending ? rows.OrderBy(key, TextComparer) : rows.OrderByDescending(key, TextComparer))
            .ToList();

}
