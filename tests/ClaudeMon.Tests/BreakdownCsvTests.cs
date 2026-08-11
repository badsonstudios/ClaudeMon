namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Services;

public class BreakdownCsvTests
{
    private static BreakdownRow Row(
        string key, string display, long input = 100, long output = 200,
        long cw = 10, long cr = 1000, double cost = 1.25, bool unpriced = false) =>
        new(key, display, input, output, cw, cr, cost, unpriced);

    private static LocalUsageBreakdown Breakdown(
        IReadOnlyList<BreakdownRow>? models = null, IReadOnlyList<BreakdownRow>? projects = null) =>
        new(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 19),
            models ?? [Row("claude-fable-5", "claude-fable-5")],
            projects ?? [Row("proj-a", @"C:\Projects\A")],
            Row("total", "Total", cost: 2.5));

    // Three rows whose name, token and cost orders are all different from each other, so an
    // export-order test can only pass on the column it actually asked for.
    private static readonly BreakdownRow[] SortSample =
    [
        Row("alpha", "alpha", input: 300, cost: 1.0),
        Row("bravo", "bravo", input: 100, cost: 3.0),
        Row("charlie", "charlie", input: 200, cost: 2.0),
    ];

    /// <summary>The export as the window writes it, in whatever order the two tables are showing.</summary>
    private static string Compose(
        LocalUsageBreakdown breakdown,
        BreakdownSortState? modelSort = null, BreakdownSortState? projectSort = null) =>
        BreakdownCsv.Compose(
            breakdown,
            modelSort ?? BreakdownSortState.Default,
            projectSort ?? BreakdownSortState.Default);

    private static string[] Lines(string csv) =>
        csv.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    /// <summary>The Name field of every row of one section, in file order.</summary>
    private static IReadOnlyList<string> NamesIn(string csv, string section) =>
        Lines(csv).Skip(1)
            .Where(l => l.StartsWith(section + ",", StringComparison.Ordinal))
            .Select(l => l.Split(',')[1])
            .ToList();

    [Fact]
    public void Compose_HeaderAndSectionsAndTotals()
    {
        var lines = Lines(Compose(Breakdown()));

        Assert.Equal(BreakdownCsv.Header, lines[0]);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("model,claude-fable-5,100,200,10,1000,1310,1.25,false", lines[1]);
        Assert.StartsWith(@"project,C:\Projects\A,", lines[2]);
        // Totals row has an empty name.
        Assert.StartsWith("total,,", lines[3]);
        Assert.Contains(",2.5,", lines[3]);
    }

    [Fact]
    public void Compose_InvariantDecimals()
    {
        var csv = Compose(Breakdown(
            models: [Row("m", "m", cost: 41.2372)]));

        Assert.Contains(",41.2372,", csv);
        Assert.DoesNotContain(",41,2372,", csv);
    }

    [Fact]
    public void Compose_UnpricedRow_FlaggedWithCostFloor()
    {
        var csv = Compose(Breakdown(
            models: [Row("m", "m", cost: 3.0, unpriced: true)]));

        Assert.Contains(",3.0,true", csv);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("has,comma", "\"has,comma\"")]
    [InlineData("has\"quote", "\"has\"\"quote\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    public void EscapeField_Rfc4180(string value, string expected)
    {
        Assert.Equal(expected, BreakdownCsv.EscapeField(value));
    }

    [Theory]
    [InlineData("=cmd|whatever", "'=cmd|whatever")]
    [InlineData("+1234", "'+1234")]
    [InlineData("@import", "'@import")]
    [InlineData("-leading-dash", "'-leading-dash")]
    public void EscapeField_FormulaTriggers_NeutralizedForExcel(string value, string expected)
    {
        Assert.Equal(expected, BreakdownCsv.EscapeField(value));
    }

    [Fact]
    public void Compose_ProjectPathWithComma_Quoted()
    {
        var csv = Compose(Breakdown(
            projects: [Row("p", @"C:\Odd, Path\Proj")]));

        Assert.Contains("\"C:\\Odd, Path\\Proj\"", csv);
    }

    [Fact]
    public void Compose_DefaultOrder_IsCostDescending()
    {
        // The order LocalUsageStore hands the rows over in, and the order the window opens at —
        // so exporting without touching a header writes the file it always did.
        var csv = Compose(Breakdown(models: SortSample, projects: SortSample));

        Assert.Equal(["bravo", "charlie", "alpha"], NamesIn(csv, "model"));
        Assert.Equal(["bravo", "charlie", "alpha"], NamesIn(csv, "project"));
    }

    [Fact]
    public void Compose_WritesEachSectionInItsOwnTablesOrder()
    {
        // #119: the two tables sort independently, so the export can't have one order. Models by
        // name A→Z, projects by input tokens big-first — neither of which is the default.
        var csv = Compose(
            Breakdown(models: SortSample, projects: SortSample),
            modelSort: new BreakdownSortState(BreakdownSortColumn.Name, Ascending: true),
            projectSort: new BreakdownSortState(BreakdownSortColumn.Input, Ascending: false));

        Assert.Equal(["alpha", "bravo", "charlie"], NamesIn(csv, "model"));
        Assert.Equal(["alpha", "charlie", "bravo"], NamesIn(csv, "project"));
    }

    [Fact]
    public void Compose_ReversedSort_ReversesTheRows()
    {
        var breakdown = Breakdown(models: SortSample, projects: SortSample);
        var ascending = BreakdownSortState.Default.Toggle((int)BreakdownSortColumn.Cost);

        Assert.Equal(
            ["alpha", "charlie", "bravo"],
            NamesIn(Compose(breakdown, modelSort: ascending), "model"));
    }

    [Fact]
    public void Compose_TotalsRowStaysLast_WhateverTheSort()
    {
        var breakdown = Breakdown(models: SortSample, projects: SortSample);

        foreach (var column in Enum.GetValues<BreakdownSortColumn>())
        {
            foreach (var ascending in new[] { true, false })
            {
                var state = new BreakdownSortState(column, ascending);
                var lines = Lines(Compose(breakdown, state, state));

                // Header + three models + three projects + the one total, which is the last line.
                Assert.Equal(8, lines.Length);
                Assert.StartsWith("total,,", lines[^1]);
                Assert.DoesNotContain(lines[1..^1], l => l.StartsWith("total,", StringComparison.Ordinal));
            }
        }
    }
}
