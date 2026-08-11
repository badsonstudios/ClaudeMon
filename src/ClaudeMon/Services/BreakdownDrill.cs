namespace ClaudeMon.Services;

using ClaudeMon.Models;

/// <summary>
/// Drilling one row of a "Usage &amp; costs" table into its counterparts (#112): the projects a
/// model ran in, or the models a project used. Both directions are the same slice of
/// <see cref="LocalUsageBreakdown.Pairs"/> with the axes swapped.
///
/// A pure computation over the breakdown the window already holds — not a second query — so the
/// drill-down can never disagree with the tables it came from, however long the window has been
/// open. Pure (no WinForms) and unit-testable, mirroring <see cref="BreakdownSort"/>.
///
/// Lives beside <see cref="BreakdownCsv"/> rather than in <c>UI</c> for the same reason
/// <see cref="BreakdownSort"/> does: the scope is no longer only the tables' — the CSV export
/// applies the same drill-down, so a file exported while drilled holds the rows that were on
/// screen rather than the breakdown they were drilled from (#168).
/// </summary>
internal static class BreakdownDrill
{
    /// <summary>
    /// The counterpart rows for <paramref name="key"/> on <paramref name="axis"/>, or null when
    /// there is nothing to drill into — no breakdown, or a key with no usage in it (which is what
    /// a model or project that drops out of range when the timeframe narrows looks like).
    /// Rows are ordered like the store orders the main tables: cost, then tokens, descending.
    /// </summary>
    public static LocalUsageDrillDown? For(LocalUsageBreakdown? breakdown, BreakdownAxis axis, string key)
    {
        if (breakdown is null)
            return null;

        var rows = new List<BreakdownRow>();
        var totals = new BreakdownRow("total", "Total", 0, 0, 0, 0, 0.0, false);

        foreach (var pair in breakdown.Pairs)
        {
            // Case-insensitively, because that is how the store merges both the axis rows and the
            // pairs — the row the user clicked may differ from a pair's key only in case.
            var selected = axis == BreakdownAxis.Model ? pair.ModelKey : pair.ProjectKey;
            if (!string.Equals(selected, key, StringComparison.OrdinalIgnoreCase))
                continue;

            rows.Add(axis == BreakdownAxis.Model
                ? Row(pair.ProjectKey, pair.ProjectDisplayName, pair.Totals)
                : Row(pair.ModelKey, pair.ModelKey, pair.Totals));
            totals = Add(totals, pair.Totals);
        }

        if (rows.Count == 0)
            return null;

        rows.Sort(static (a, b) => b.CostUsd != a.CostUsd
            ? b.CostUsd.CompareTo(a.CostUsd)
            : b.TotalTokens.CompareTo(a.TotalTokens));

        return new LocalUsageDrillDown(axis, key, rows, totals);
    }

    /// <summary>
    /// Which of the two tables have to be rebuilt when the drill-down goes from
    /// <paramref name="previous"/> to <paramref name="next"/>. A table's rows only change when the
    /// drill-down filtering <em>it</em> changes — a drill-down filters the table on the
    /// <em>other</em> axis — so the table holding the selection keeps its items, and with them the
    /// row the user just clicked.
    /// </summary>
    public static (bool Model, bool Project) Rebuild(
        LocalUsageDrillDown? previous, LocalUsageDrillDown? next) =>
        (Filtering(previous, BreakdownAxis.Model) != Filtering(next, BreakdownAxis.Model),
         Filtering(previous, BreakdownAxis.Project) != Filtering(next, BreakdownAxis.Project));

    /// <summary>
    /// The drill-down narrowing the <paramref name="axis"/> table, if any: a selected model
    /// narrows the projects, and a selected project narrows the models.
    /// </summary>
    public static LocalUsageDrillDown? Filtering(LocalUsageDrillDown? drill, BreakdownAxis axis) =>
        drill is not null && drill.Axis != axis ? drill : null;

    /// <summary>
    /// The drilled-into row's own display name — a project's real path rather than its directory
    /// key. Falls back to the key when the row is no longer in <paramref name="breakdown"/>, which
    /// is what a row dropping out of a narrowed timeframe looks like. Shared by the window's
    /// headings and the CSV export, so both name the scope the same way.
    /// </summary>
    public static string DisplayName(LocalUsageBreakdown? breakdown, LocalUsageDrillDown drill)
    {
        var rows = drill.Axis == BreakdownAxis.Model ? breakdown?.ByModel : breakdown?.ByProject;
        return rows?.FirstOrDefault(
            r => string.Equals(r.Key, drill.Key, StringComparison.OrdinalIgnoreCase))?.DisplayName
            ?? drill.Key;
    }

    /// <summary>Whether two drill-downs point at the same row — or both at nothing.</summary>
    public static bool Same(LocalUsageDrillDown? a, LocalUsageDrillDown? b) =>
        a is null || b is null
            ? a is null && b is null
            : a.Axis == b.Axis && string.Equals(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);

    private static BreakdownRow Row(string key, string display, LocalDayTotals cell) =>
        new(key, display,
            cell.InputTokens, cell.OutputTokens, cell.CacheWriteTokens, cell.CacheReadTokens,
            cell.CostUsd, cell.HasUnpricedModels);

    private static BreakdownRow Add(BreakdownRow row, LocalDayTotals cell) => row with
    {
        InputTokens = row.InputTokens + cell.InputTokens,
        OutputTokens = row.OutputTokens + cell.OutputTokens,
        CacheWriteTokens = row.CacheWriteTokens + cell.CacheWriteTokens,
        CacheReadTokens = row.CacheReadTokens + cell.CacheReadTokens,
        CostUsd = row.CostUsd + cell.CostUsd,
        HasUnpricedModels = row.HasUnpricedModels || cell.HasUnpricedModels,
    };
}
