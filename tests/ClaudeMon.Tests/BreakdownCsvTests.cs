namespace ClaudeMon.Tests;

using System.Globalization;
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

    private static BreakdownPair Pair(string project, string model, long input, double cost) =>
        new(project, $@"C:\{project}", model, new LocalDayTotals { InputTokens = input, CostUsd = cost });

    /// <summary>
    /// A breakdown whose pairs really do add up to its rows — fable ran in both projects, opus only
    /// in proj-a — so a drill-down taken off it carries the selected row's own totals rather than
    /// numbers typed in next to them. Only input tokens are used, to keep the sums readable.
    /// </summary>
    private static LocalUsageBreakdown Cross() =>
        new(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 19),
            [Row("claude-fable-5", "claude-fable-5", 500, 0, 0, 0, 5.0),
             Row("claude-opus-5", "claude-opus-5", 200, 0, 0, 0, 5.0)],
            [Row("proj-a", @"C:\proj-a", 300, 0, 0, 0, 6.0),
             Row("proj-b", @"C:\proj-b", 400, 0, 0, 0, 4.0)],
            Row("total", "Total", 700, 0, 0, 0, 10.0))
        {
            Pairs =
            [
                Pair("proj-a", "claude-fable-5", 100, 1.0),
                Pair("proj-b", "claude-fable-5", 400, 4.0),
                Pair("proj-a", "claude-opus-5", 200, 5.0),
            ],
        };

    private static LocalUsageDrillDown Drill(LocalUsageBreakdown breakdown, BreakdownAxis axis, string key) =>
        BreakdownDrill.For(breakdown, axis, key) ?? throw new InvalidOperationException($"no drill for {key}");

    /// <summary>A drill-down that only has to name a scope — for the file-name tests.</summary>
    private static LocalUsageDrillDown Scope(BreakdownAxis axis, string key) =>
        new(axis, key, [], Row("total", "Total"));

    // Three rows whose name, token and cost orders are all different from each other, so an
    // export-order test can only pass on the column it actually asked for.
    private static readonly BreakdownRow[] SortSample =
    [
        Row("alpha", "alpha", input: 300, cost: 1.0),
        Row("bravo", "bravo", input: 100, cost: 3.0),
        Row("charlie", "charlie", input: 200, cost: 2.0),
    ];

    /// <summary>
    /// The export as the window writes it: whatever the two tables are showing, in whatever order
    /// they are showing it, narrowed by whatever is drilled into.
    /// </summary>
    private static string Compose(
        LocalUsageBreakdown breakdown, LocalUsageDrillDown? drill = null,
        BreakdownSortState? modelSort = null, BreakdownSortState? projectSort = null) =>
        BreakdownCsv.Compose(
            breakdown,
            drill,
            modelSort ?? BreakdownSortState.Default,
            projectSort ?? BreakdownSortState.Default);

    private static string[] Lines(string csv) =>
        csv.TrimEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    private static IEnumerable<string> RowsIn(string csv, string section) =>
        Lines(csv).Skip(1).Where(l => l.StartsWith(section + ",", StringComparison.Ordinal));

    /// <summary>The Name field of every row of one section, in file order.</summary>
    private static IReadOnlyList<string> NamesIn(string csv, string section) =>
        RowsIn(csv, section).Select(l => l.Split(',')[1]).ToList();

    /// <summary>One numeric column of every row of a section, parsed back out of the file.</summary>
    private static IReadOnlyList<double> ColumnIn(string csv, string section, int column) =>
        RowsIn(csv, section)
            .Select(l => double.Parse(l.Split(',')[column], CultureInfo.InvariantCulture))
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
                var lines = Lines(Compose(breakdown, modelSort: state, projectSort: state));

                // Header + three models + three projects + the one total, which is the last line.
                Assert.Equal(8, lines.Length);
                Assert.StartsWith("total,,", lines[^1]);
                Assert.DoesNotContain(lines[1..^1], l => l.StartsWith("total,", StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void Compose_NoDrill_HasNoScopeRow()
    {
        // #168 must be invisible to an ordinary export: header, both full tables, one total.
        var csv = Compose(Cross());

        Assert.DoesNotContain("drill", csv, StringComparison.Ordinal);
        Assert.Equal(6, Lines(csv).Length);
        Assert.Equal(["claude-fable-5", "claude-opus-5"], NamesIn(csv, "model"));
        Assert.Equal([@"C:\proj-a", @"C:\proj-b"], NamesIn(csv, "project"));
    }

    [Fact]
    public void Compose_DrilledIntoAModel_WritesThatModelsProjects()
    {
        var breakdown = Cross();
        var csv = Compose(breakdown, Drill(breakdown, BreakdownAxis.Model, "claude-fable-5"));

        // The model table holds the selection and still shows everything...
        Assert.Equal(["claude-fable-5", "claude-opus-5"], NamesIn(csv, "model"));
        // ...while the project table is the projects that ran fable — proj-a with only its fable
        // usage (its 200 opus tokens belong to the opus row, not to this view).
        Assert.Equal([@"C:\proj-b", @"C:\proj-a"], NamesIn(csv, "project"));
        Assert.Equal([400.0, 100.0], ColumnIn(csv, "project", 2));
    }

    [Fact]
    public void Compose_DrilledIntoAProject_WritesThatProjectsModels()
    {
        var breakdown = Cross();
        var csv = Compose(breakdown, Drill(breakdown, BreakdownAxis.Project, "proj-a"));

        // Mirror image: the models proj-a used, and the untouched project table.
        Assert.Equal(["claude-opus-5", "claude-fable-5"], NamesIn(csv, "model"));
        Assert.Equal([200.0, 100.0], ColumnIn(csv, "model", 2));
        Assert.Equal([@"C:\proj-a", @"C:\proj-b"], NamesIn(csv, "project"));
    }

    [Fact]
    public void Compose_Drilled_ScopeRowIsTheSelectedRowAndTheDrilledTablesTotal()
    {
        var breakdown = Cross();
        var csv = Compose(breakdown, Drill(breakdown, BreakdownAxis.Model, "claude-fable-5"));
        var lines = Lines(csv);

        // The scope row leads the file, names what was drilled into, and — field for field — is the
        // selected row of the other table, so the file can't claim a total the window didn't show.
        var scope = Assert.Single(lines, l => l.StartsWith("drill-", StringComparison.Ordinal));
        Assert.Equal(lines[1], scope);
        Assert.StartsWith("drill-model,claude-fable-5,500,0,0,0,500,5.0,false", scope);

        var selected = Assert.Single(
            RowsIn(csv, "model"), l => l.StartsWith("model,claude-fable-5,", StringComparison.Ordinal));
        Assert.Equal(selected["model,".Length..], scope["drill-model,".Length..]);

        // ...and the drilled table sums to exactly it.
        Assert.Equal(500.0, ColumnIn(csv, "project", 2).Sum());
        Assert.Equal(5.0, ColumnIn(csv, "project", 7).Sum(), precision: 10);
    }

    [Fact]
    public void Compose_Drilled_ScopeRowNamesTheProjectPathAndCarriesTheUnpricedFlag()
    {
        var breakdown = Cross() with
        {
            ByProject = [Row("proj-a", @"C:\proj-a", 300, 0, 0, 0, 6.0, unpriced: true)],
            Pairs = [new BreakdownPair("proj-a", @"C:\proj-a", "m", new LocalDayTotals
            {
                InputTokens = 300,
                CostUsd = 6.0,
                HasUnpricedModels = true,
            })],
        };
        var csv = Compose(breakdown, Drill(breakdown, BreakdownAxis.Project, "proj-a"));

        // The real path, as the window's heading shows it — not the "proj-a" directory key that was
        // clicked — and the cost still reads as a floor.
        Assert.StartsWith(@"drill-project,C:\proj-a,300,0,0,0,300,6.0,true", Lines(csv)[1]);
    }

    [Fact]
    public void Compose_Drilled_TotalRowStaysTheWholeTimeframe()
    {
        // The undrilled table still shows the grand total on screen, so the file keeps writing it —
        // the scope row above is what says the other table is narrower than that.
        var breakdown = Cross();
        var lines = Lines(Compose(breakdown, Drill(breakdown, BreakdownAxis.Model, "claude-fable-5")));

        Assert.StartsWith("total,,700,0,0,0,700,10.0,false", lines[^1]);
    }

    [Fact]
    public void Compose_Drilled_WritesTheDrilledRowsInTheOnScreenSort()
    {
        // #119 and #168 together: the drilled rows go through the drilled table's own sort.
        var breakdown = Cross();
        var csv = Compose(
            breakdown, Drill(breakdown, BreakdownAxis.Model, "claude-fable-5"),
            projectSort: new BreakdownSortState(BreakdownSortColumn.Name, Ascending: true));

        // Cost-descending would have put proj-b first.
        Assert.Equal([@"C:\proj-a", @"C:\proj-b"], NamesIn(csv, "project"));
    }

    [Theory]
    [InlineData(BreakdownAxis.Model, "claude-fable-5", "-model-claude-fable-5")]
    [InlineData(BreakdownAxis.Project, "C--Projects-ClaudeMon", "-project-c-projects-claudemon")]
    // Separators, spaces and punctuation collapse to single dashes; a key with nothing to slug
    // still says which axis it was.
    [InlineData(BreakdownAxis.Project, @"C:\Odd, Path\Proj", "-project-c-odd-path-proj")]
    [InlineData(BreakdownAxis.Model, "///", "-model")]
    [InlineData(BreakdownAxis.Model, "", "-model")]
    public void FileNameScope_NamesTheDrilledRow(BreakdownAxis axis, string key, string expected)
    {
        Assert.Equal(expected, BreakdownCsv.FileNameScope(Scope(axis, key)));
    }

    [Fact]
    public void FileNameScope_NoDrill_IsEmpty()
    {
        // An ordinary export keeps the name it always had.
        Assert.Equal("", BreakdownCsv.FileNameScope(null));
    }

    [Fact]
    public void FileNameScope_LongKey_IsClipped()
    {
        var scope = BreakdownCsv.FileNameScope(Scope(BreakdownAxis.Project, new string('a', 60)));

        Assert.Equal("-project-" + new string('a', 40), scope);
        // And the clip never leaves a trailing dash hanging off the name.
        Assert.Equal(
            "-project-" + new string('a', 39),
            BreakdownCsv.FileNameScope(Scope(BreakdownAxis.Project, new string('a', 39) + @"\deep\path")));
    }
}
