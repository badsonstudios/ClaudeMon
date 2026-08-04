namespace ClaudeMon.UI;

/// <summary>
/// What the "Usage &amp; costs" window shows for a given tab (#113). The two tabs share one layout
/// and differ only in what is visible, and a drill-down (#112) survives a trip to the Chart tab —
/// so "is this control on screen?" is a question about the tab <em>and</em> the drill state, which
/// is exactly the kind of thing that rots into a half-hidden "Show all" button.
/// </summary>
internal readonly record struct BreakdownTabVisibility(
    bool Tables,
    bool Chart,
    bool SelectHint,
    bool ModelShowAll,
    bool ProjectShowAll);

/// <summary>
/// The visibility rule behind the window's tab strip, as one pure function so the invariant is
/// unit-tested rather than traced by hand through the form. Mirrors <see cref="BreakdownDrill"/>
/// and <see cref="UsageBreakdownLayout"/> in keeping the decision out of the WinForms code.
/// </summary>
internal static class BreakdownTabView
{
    public const int TablesTab = 0;
    public const int ChartTab = 1;

    /// <summary>
    /// Everything the window shows for <paramref name="selectedTab"/>. The drill flags say which
    /// table a drill-down is currently narrowing; its "Show all" button only belongs on screen
    /// when that table is on screen too. An out-of-range tab index falls back to the tables, which
    /// are the default view and the one that always has something to show.
    /// </summary>
    public static BreakdownTabVisibility For(int selectedTab, bool modelDrilled, bool projectDrilled)
    {
        var chart = selectedTab == ChartTab;
        var tables = !chart;

        return new BreakdownTabVisibility(
            Tables: tables,
            Chart: chart,
            // Only the tables can honour an invitation to select a row.
            SelectHint: tables,
            ModelShowAll: tables && modelDrilled,
            ProjectShowAll: tables && projectDrilled);
    }
}
