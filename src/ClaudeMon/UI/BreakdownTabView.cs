namespace ClaudeMon.UI;

/// <summary>
/// What the "Usage &amp; costs" window shows for a given tab (#113, #186). The tabs share one
/// layout and differ only in what is visible, and a drill-down (#112) survives a trip to the
/// Chart tab — so "is this control on screen?" is a question about the tab <em>and</em> the
/// drill state, which is exactly the kind of thing that rots into a half-hidden "Show all"
/// button. The Limit history tab (#186) hides the timeframe combo and the Export button too:
/// it is whole-log rather than timeframe-scoped, and its CSV export belongs to #68.
/// </summary>
internal readonly record struct BreakdownTabVisibility(
    bool Tables,
    bool Chart,
    bool Limits,
    bool Timeframe,
    bool Export,
    bool SelectHint,
    bool ModelShowAll,
    bool ProjectShowAll);

/// <summary>
/// The visibility rule behind the window's tab strip, as one pure function so the invariant is
/// unit-tested rather than traced by hand through the form. Mirrors <c>BreakdownDrill</c>
/// and <see cref="UsageBreakdownLayout"/> in keeping the decision out of the WinForms code.
/// </summary>
internal static class BreakdownTabView
{
    public const int TablesTab = 0;
    public const int ChartTab = 1;
    public const int LimitsTab = 2;

    /// <summary>
    /// Everything the window shows for <paramref name="selectedTab"/>. The drill flags say which
    /// table a drill-down is currently narrowing; its "Show all" button only belongs on screen
    /// when that table is on screen too. An out-of-range tab index falls back to the tables, which
    /// are the default view and the one that always has something to show.
    /// </summary>
    public static BreakdownTabVisibility For(int selectedTab, bool modelDrilled, bool projectDrilled)
    {
        var chart = selectedTab == ChartTab;
        var limits = selectedTab == LimitsTab;
        var tables = !chart && !limits;

        return new BreakdownTabVisibility(
            Tables: tables,
            Chart: chart,
            Limits: limits,
            // The limit log is whole-history: a timeframe combo would promise a scoping it
            // doesn't do, and CSV export of it is out of scope (#68 owns export).
            Timeframe: !limits,
            Export: !limits,
            // Only the tables can honour an invitation to select a row.
            SelectHint: tables,
            ModelShowAll: tables && modelDrilled,
            ProjectShowAll: tables && projectDrilled);
    }
}
