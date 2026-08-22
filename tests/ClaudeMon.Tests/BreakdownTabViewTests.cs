namespace ClaudeMon.Tests;

using ClaudeMon.UI;

public class BreakdownTabViewTests
{
    [Fact]
    public void TablesTab_ShowsTheTablesAndTheirInvitation()
    {
        var visible = BreakdownTabView.For(
            BreakdownTabView.TablesTab, modelDrilled: false, projectDrilled: false);

        Assert.True(visible.Tables);
        Assert.False(visible.Chart);
        Assert.True(visible.SelectHint);
    }

    [Fact]
    public void ChartTab_HidesTheTablesAndTheInvitationOnlyTheyCanHonour()
    {
        var visible = BreakdownTabView.For(
            BreakdownTabView.ChartTab, modelDrilled: false, projectDrilled: false);

        Assert.False(visible.Tables);
        Assert.True(visible.Chart);
        Assert.False(visible.SelectHint);
    }

    [Fact]
    public void ShowAll_AppearsOnlyForTheDrilledTable()
    {
        var model = BreakdownTabView.For(
            BreakdownTabView.TablesTab, modelDrilled: true, projectDrilled: false);
        Assert.True(model.ModelShowAll);
        Assert.False(model.ProjectShowAll);

        var project = BreakdownTabView.For(
            BreakdownTabView.TablesTab, modelDrilled: false, projectDrilled: true);
        Assert.False(project.ModelShowAll);
        Assert.True(project.ProjectShowAll);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ChartTab_NeverLeavesAShowAllButtonBehind(bool modelDrilled, bool projectDrilled)
    {
        // The drill-down survives a trip to the Chart tab, so its button has to be hidden by the
        // tab rather than by the drill ending — this is the regression that rule exists for.
        var visible = BreakdownTabView.For(BreakdownTabView.ChartTab, modelDrilled, projectDrilled);

        Assert.False(visible.ModelShowAll);
        Assert.False(visible.ProjectShowAll);
    }

    [Fact]
    public void ExactlyOneViewIsEverVisible()
    {
        foreach (var tab in new[]
            { BreakdownTabView.TablesTab, BreakdownTabView.ChartTab, BreakdownTabView.LimitsTab, 7, -1 })
        {
            var visible = BreakdownTabView.For(tab, modelDrilled: true, projectDrilled: true);
            var count = (visible.Tables ? 1 : 0) + (visible.Chart ? 1 : 0) + (visible.Limits ? 1 : 0);
            Assert.True(count == 1, $"tab {tab} showed {count} views");
        }
    }

    [Fact]
    public void LimitsTab_HidesTheTimeframeAndExport_TheOthersKeepThem()
    {
        // The limit log is whole-history: the timeframe combo would promise a scoping it
        // doesn't do, and its CSV export belongs to #68.
        var limits = BreakdownTabView.For(
            BreakdownTabView.LimitsTab, modelDrilled: false, projectDrilled: false);
        Assert.True(limits.Limits);
        Assert.False(limits.Timeframe);
        Assert.False(limits.Export);
        Assert.False(limits.SelectHint);

        foreach (var tab in new[] { BreakdownTabView.TablesTab, BreakdownTabView.ChartTab })
        {
            var visible = BreakdownTabView.For(tab, modelDrilled: false, projectDrilled: false);
            Assert.True(visible.Timeframe);
            Assert.True(visible.Export);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void LimitsTab_NeverLeavesAShowAllButtonBehind(bool modelDrilled, bool projectDrilled)
    {
        var visible = BreakdownTabView.For(BreakdownTabView.LimitsTab, modelDrilled, projectDrilled);

        Assert.False(visible.ModelShowAll);
        Assert.False(visible.ProjectShowAll);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    public void UnknownTab_FallsBackToTheTables(int tab)
    {
        var visible = BreakdownTabView.For(tab, modelDrilled: true, projectDrilled: false);

        Assert.True(visible.Tables);
        Assert.True(visible.ModelShowAll);
    }
}
