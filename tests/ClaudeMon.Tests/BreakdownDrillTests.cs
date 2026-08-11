namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Services;

/// <summary>
/// Drilling one "Usage &amp; costs" row into its counterparts (#112). The window needs a desktop
/// session, so the behaviour that matters is pinned here: both directions off the same pairs, the
/// rows summing to the row that was drilled into, and which table a state change has to rebuild.
/// </summary>
public class BreakdownDrillTests
{
    private static BreakdownPair Pair(
        string project, string model, long input = 0, double cost = 0, bool unpriced = false) =>
        new(project, $"C:\\{project}", model, new LocalDayTotals
        {
            InputTokens = input,
            CostUsd = cost,
            HasUnpricedModels = unpriced,
        });

    // proj-a ran both models, proj-b only fable — the same shape the store tests build from
    // transcripts, minus the transcripts.
    private static LocalUsageBreakdown Breakdown(params BreakdownPair[] pairs)
    {
        var from = new DateOnly(2026, 8, 1);
        return new LocalUsageBreakdown(
            from, from,
            [new BreakdownRow("claude-fable-5", "claude-fable-5", 0, 0, 0, 0, 0, false)],
            [new BreakdownRow("proj-a", @"C:\proj-a", 0, 0, 0, 0, 0, false)],
            new BreakdownRow("total", "Total", 0, 0, 0, 0, 0, false))
        {
            Pairs = pairs,
        };
    }

    private static LocalUsageBreakdown Sample() => Breakdown(
        Pair("proj-a", "claude-fable-5", input: 100, cost: 1.0),
        Pair("proj-a", "claude-opus-5", input: 200, cost: 5.0, unpriced: true),
        Pair("proj-b", "claude-fable-5", input: 400, cost: 4.0));

    [Fact]
    public void For_Model_IsThatModelSplitAcrossProjects()
    {
        var drill = BreakdownDrill.For(Sample(), BreakdownAxis.Model, "claude-fable-5");

        Assert.NotNull(drill);
        Assert.Equal(BreakdownAxis.Model, drill.Axis);
        // The projects that ran fable, each with only its fable usage — proj-a's opus tokens
        // belong to the opus drill-down, not this one.
        Assert.Equal(["proj-b", "proj-a"], drill.Rows.Select(r => r.Key));
        Assert.Equal(@"C:\proj-a", Assert.Single(drill.Rows, r => r.Key == "proj-a").DisplayName);
        Assert.Equal(500, drill.Totals.InputTokens);
        Assert.Equal(5.0, drill.Totals.CostUsd, precision: 10);
        Assert.False(drill.Totals.HasUnpricedModels);
    }

    [Fact]
    public void For_Project_IsThatProjectSplitAcrossModels()
    {
        var drill = BreakdownDrill.For(Sample(), BreakdownAxis.Project, "proj-a");

        Assert.NotNull(drill);
        Assert.Equal(BreakdownAxis.Project, drill.Axis);
        // Model rows show the model id as their name — there is no path to resolve.
        Assert.Equal(["claude-opus-5", "claude-fable-5"], drill.Rows.Select(r => r.DisplayName));
        Assert.Equal(300, drill.Totals.InputTokens);
        Assert.Equal(6.0, drill.Totals.CostUsd, precision: 10);
        // The unpriced flag rides along, so the drilled table's cost reads as a floor too.
        Assert.True(drill.Totals.HasUnpricedModels);
    }

    [Fact]
    public void For_RowsSumToTheTotals()
    {
        var drill = BreakdownDrill.For(Sample(), BreakdownAxis.Model, "claude-fable-5");

        Assert.NotNull(drill);
        Assert.Equal(drill.Totals.TotalTokens, drill.Rows.Sum(r => r.TotalTokens));
        Assert.Equal(drill.Totals.CostUsd, drill.Rows.Sum(r => r.CostUsd), precision: 10);
    }

    [Fact]
    public void For_OrdersRowsByCostThenTokensDescending()
    {
        var drill = BreakdownDrill.For(
            Breakdown(
                Pair("proj-a", "m", input: 10, cost: 1.0),
                Pair("proj-b", "m", input: 999, cost: 0.0),
                Pair("proj-c", "m", input: 1, cost: 0.0)),
            BreakdownAxis.Model, "m");

        // Cost first, then tokens for the two that cost nothing — the store's own table order.
        Assert.Equal(["proj-a", "proj-b", "proj-c"], drill!.Rows.Select(r => r.Key));
    }

    [Fact]
    public void For_SingleCounterpart_StillDrills()
    {
        var drill = BreakdownDrill.For(Sample(), BreakdownAxis.Project, "proj-b");

        var row = Assert.Single(drill!.Rows);
        Assert.Equal("claude-fable-5", row.Key);
        Assert.Equal(row.TotalTokens, drill.Totals.TotalTokens);
    }

    [Fact]
    public void For_MatchesKeysCaseInsensitively()
    {
        // The store merges its rows and pairs case-insensitively, so a click must too.
        Assert.Equal(2, BreakdownDrill.For(Sample(), BreakdownAxis.Project, "PROJ-A")!.Rows.Count);
    }

    [Fact]
    public void For_UnknownKeyOrNoBreakdown_IsNothingToDrillInto()
    {
        // A key with no usage in range (what a narrowed timeframe looks like) drills into nothing
        // rather than into an empty table.
        Assert.Null(BreakdownDrill.For(Sample(), BreakdownAxis.Model, "claude-never-used"));
        Assert.Null(BreakdownDrill.For(null, BreakdownAxis.Model, "claude-fable-5"));
    }

    [Fact]
    public void Filtering_AppliesToTheOppositeAxis()
    {
        var drill = BreakdownDrill.For(Sample(), BreakdownAxis.Model, "claude-fable-5");

        // A selected model narrows the projects, and leaves the model table showing everything.
        Assert.Null(BreakdownDrill.Filtering(drill, BreakdownAxis.Model));
        Assert.Same(drill, BreakdownDrill.Filtering(drill, BreakdownAxis.Project));
        Assert.Null(BreakdownDrill.Filtering(null, BreakdownAxis.Project));
    }

    [Fact]
    public void DisplayName_IsTheDrilledRowsOwnName()
    {
        var breakdown = Sample();

        // A project's real path rather than the directory key that was clicked (matched
        // case-insensitively, as everywhere else); a model has no path, so it is its own name.
        Assert.Equal(
            @"C:\proj-a",
            BreakdownDrill.DisplayName(breakdown, BreakdownDrill.For(breakdown, BreakdownAxis.Project, "PROJ-A")!));
        Assert.Equal(
            "claude-fable-5",
            BreakdownDrill.DisplayName(breakdown, BreakdownDrill.For(breakdown, BreakdownAxis.Model, "claude-fable-5")!));
    }

    [Fact]
    public void DisplayName_RowNotInTheBreakdown_FallsBackToTheKey()
    {
        // proj-b is in the pairs but not in this breakdown's project rows — the shape a drill-down
        // outliving its row (a narrowed timeframe) leaves behind.
        var drill = BreakdownDrill.For(Sample(), BreakdownAxis.Project, "proj-b")!;

        Assert.Equal("proj-b", BreakdownDrill.DisplayName(Sample(), drill));
        Assert.Equal("proj-b", BreakdownDrill.DisplayName(null, drill));
    }

    [Fact]
    public void Rebuild_OnlyTouchesTheTableWhoseRowsChanged()
    {
        var breakdown = Sample();
        var model = BreakdownDrill.For(breakdown, BreakdownAxis.Model, "claude-fable-5");
        var otherModel = BreakdownDrill.For(breakdown, BreakdownAxis.Model, "claude-opus-5");
        var project = BreakdownDrill.For(breakdown, BreakdownAxis.Project, "proj-a");

        // Starting a model drill-down filters the projects; the model table keeps its rows (and
        // with them the selection that started it).
        Assert.Equal((false, true), BreakdownDrill.Rebuild(null, model));
        Assert.Equal((false, true), BreakdownDrill.Rebuild(model, null));
        // Switching to another model re-filters the projects only.
        Assert.Equal((false, true), BreakdownDrill.Rebuild(model, otherModel));
        // Handing the drill-down to the other table swaps which one is filtered, so both change.
        Assert.Equal((true, true), BreakdownDrill.Rebuild(model, project));
        Assert.Equal((true, false), BreakdownDrill.Rebuild(null, project));
        Assert.Equal((false, false), BreakdownDrill.Rebuild(null, null));
    }

    [Fact]
    public void Same_ComparesTheDrilledRowNotTheResults()
    {
        var breakdown = Sample();
        var model = BreakdownDrill.For(breakdown, BreakdownAxis.Model, "claude-fable-5");
        var again = BreakdownDrill.For(breakdown, BreakdownAxis.Model, "CLAUDE-FABLE-5");
        var project = BreakdownDrill.For(breakdown, BreakdownAxis.Project, "proj-a");

        Assert.True(BreakdownDrill.Same(model, again));
        Assert.True(BreakdownDrill.Same(null, null));
        Assert.False(BreakdownDrill.Same(model, project));
        Assert.False(BreakdownDrill.Same(model, null));
        Assert.False(BreakdownDrill.Same(null, model));
    }
}
