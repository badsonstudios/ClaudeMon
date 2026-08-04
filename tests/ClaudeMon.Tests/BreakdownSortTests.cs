namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;
using ClaudeMon.UI;

/// <summary>
/// Click-to-sort for the "Usage &amp; costs" tables (#111). The window itself needs a desktop
/// session, so the ordering is pinned here: the direction toggle, every column sorting on the
/// number behind the cell rather than its formatted text, and the totals row never leaving the
/// bottom.
/// </summary>
public class BreakdownSortTests
{
    private static BreakdownRow Row(
        string name,
        long input = 0,
        long output = 0,
        long cacheWrite = 0,
        long cacheRead = 0,
        double cost = 0,
        bool unpriced = false) =>
        new(name, name, input, output, cacheWrite, cacheRead, cost, unpriced);

    private static IReadOnlyList<string> Names(IReadOnlyList<BreakdownRow> rows) =>
        rows.Select(r => r.DisplayName).ToList();

    private static IReadOnlyList<BreakdownRow> Order(
        IReadOnlyList<BreakdownRow> rows, BreakdownSortColumn column, bool ascending) =>
        BreakdownSort.Order(rows, totals: null, new BreakdownSortState(column, ascending));

    // Four rows whose seven column orderings are all different from each other — so a test
    // asserting on one column can't pass because a different column (a mis-mapped key selector,
    // say) happened to produce the same order. Named for their alphabetical order.
    private static readonly BreakdownRow[] Sample =
    [
        Row("alpha", input: 400, output: 30, cacheWrite: 1_000_000, cacheRead: 2_000_000, cost: 0.50),
        Row("bravo", input: 300, output: 40, cacheWrite: 200_000, cacheRead: 1_500_000, cost: 40.00),
        Row("charlie", input: 200, output: 10, cacheWrite: 5_000_000, cacheRead: 100_000, cost: 4.00),
        Row("delta", input: 100, output: 20, cacheWrite: 3_000_000, cacheRead: 9_000_000, cost: 12.00),
    ];

    [Fact]
    public void DefaultIsCostDescending()
    {
        // What the window opens at — the order LocalUsageStore already hands the rows over in, so
        // sorting doesn't change how the window looks before anything is clicked.
        var state = BreakdownSortState.Default;

        Assert.Equal(BreakdownSortColumn.Cost, state.Column);
        Assert.False(state.Ascending);
        Assert.Equal(["bravo", "delta", "charlie", "alpha"], Names(BreakdownSort.Order(Sample, null, state)));
    }

    [Fact]
    public void ClickingTheSortedColumnReversesTheDirection()
    {
        var descending = BreakdownSortState.Default;
        var ascending = descending.Toggle((int)BreakdownSortColumn.Cost);

        Assert.Equal(new BreakdownSortState(BreakdownSortColumn.Cost, Ascending: true), ascending);
        Assert.Equal(["alpha", "charlie", "delta", "bravo"], Names(BreakdownSort.Order(Sample, null, ascending)));

        // ...and back again on the next click.
        Assert.Equal(descending, ascending.Toggle((int)BreakdownSortColumn.Cost));
    }

    [Fact]
    public void ClickingANumericColumnStartsBigFirst()
    {
        // "Which project burns the most cache-write tokens?" is the question these columns get
        // asked, so the first click answers it rather than showing the smallest three.
        var state = BreakdownSortState.Default.Toggle((int)BreakdownSortColumn.CacheWrite);

        Assert.Equal(new BreakdownSortState(BreakdownSortColumn.CacheWrite, Ascending: false), state);
    }

    [Fact]
    public void ClickingTheNameColumnStartsAlphabetically()
    {
        var state = BreakdownSortState.Default.Toggle((int)BreakdownSortColumn.Name);

        Assert.Equal(new BreakdownSortState(BreakdownSortColumn.Name, Ascending: true), state);
        Assert.Equal(["alpha", "bravo", "charlie", "delta"], Names(BreakdownSort.Order(Sample, null, state)));

        var reversed = state.Toggle((int)BreakdownSortColumn.Name);
        Assert.Equal(["delta", "charlie", "bravo", "alpha"], Names(BreakdownSort.Order(Sample, null, reversed)));
    }

    [Fact]
    public void NameSortIgnoresCase()
    {
        var rows = new[] { Row("Zebra"), Row("apple"), Row("Mango") };

        Assert.Equal(
            ["apple", "Mango", "Zebra"],
            Names(Order(rows, BreakdownSortColumn.Name, ascending: true)));
    }

    [Fact]
    public void AnOutOfRangeColumnLeavesTheStateAlone()
    {
        // Defensive: ColumnClick only ever reports one of the seven real columns.
        var state = BreakdownSortState.Default;

        Assert.Equal(state, state.Toggle(-1));
        Assert.Equal(state, state.Toggle(7));
    }

    // Column indices rather than the enum: InlineData on a public test method can't carry an
    // internal type, and these are the indices ColumnClick reports anyway. Every expected order
    // below is different from every other, so each case can only pass on its own column's values.
    [Theory]
    [InlineData(0, "delta,charlie,bravo,alpha")]  // Name
    [InlineData(1, "alpha,bravo,charlie,delta")]  // Input
    [InlineData(2, "bravo,alpha,delta,charlie")]  // Output
    [InlineData(3, "charlie,delta,alpha,bravo")]  // Cache W
    [InlineData(4, "delta,alpha,bravo,charlie")]  // Cache R
    [InlineData(5, "delta,charlie,alpha,bravo")]  // Tokens
    [InlineData(6, "bravo,delta,charlie,alpha")]  // Cost (est.)
    public void EveryColumnSortsOnItsOwnValues(int column, string expectedDescending)
    {
        var descending = expectedDescending.Split(',');

        Assert.Equal(descending, Names(Order(Sample, (BreakdownSortColumn)column, ascending: false)));
        Assert.Equal(descending.Reverse(), Names(Order(Sample, (BreakdownSortColumn)column, ascending: true)));
    }

    [Fact]
    public void TheColumnEnumMatchesTheTableItSorts()
    {
        // A clicked column index is cast straight to this enum, so its members and their order
        // are load-bearing: reordering them sorts the wrong values under the right header.
        Assert.Equal(
            ["Name", "Input", "Output", "CacheWrite", "CacheRead", "Tokens", "Cost"],
            Enum.GetNames<BreakdownSortColumn>());
        Assert.Equal([0, 1, 2, 3, 4, 5, 6], Enum.GetValues<BreakdownSortColumn>().Select(c => (int)c));
    }

    [Fact]
    public void TokensSortsOnTheSumNotOnAnySingleColumn()
    {
        var rows = new[]
        {
            Row("few-but-big", input: 10, cacheRead: 10_000),  // 10,010 total
            Row("many-but-small", input: 9_000, output: 900),  // 9,900 total
        };

        Assert.Equal(
            ["few-but-big", "many-but-small"],
            Names(Order(rows, BreakdownSortColumn.Tokens, ascending: false)));
    }

    [Fact]
    public void MillionsSortAboveThousands()
    {
        // The regression this whole helper exists for: the cells are formatted for display, and
        // "1.2M" < "900K" as text. Sorting must see 1,200,000 > 900,000.
        var big = Row("big", input: 1_200_000);
        var small = Row("small", input: 900_000);

        Assert.Equal("1.2M", LocalCostText.FormatTokens(big.InputTokens));
        Assert.Equal("900K", LocalCostText.FormatTokens(small.InputTokens));
        Assert.True(
            string.CompareOrdinal("1.2M", "900K") < 0,
            "the formatted text really does compare the wrong way round — that's the bug");

        Assert.Equal(
            ["big", "small"],
            Names(Order([small, big], BreakdownSortColumn.Input, ascending: false)));
    }

    [Fact]
    public void CostSortsOnTheNumberBehindItsNonNumericDisplayForms()
    {
        // The cost cell can read "—" (nothing priced) or "≥$x" (an unpriced model contributed);
        // both still have to land where their number says, not where their text would.
        var priced = Row("priced", cost: 4.00);
        var floor = Row("floor", cost: 12.00, unpriced: true);      // shows "≥$12.00"
        var nothing = Row("nothing", cost: 0.0, unpriced: true);    // shows "—"

        Assert.Equal(
            ["floor", "priced", "nothing"],
            Names(Order([priced, floor, nothing], BreakdownSortColumn.Cost, ascending: false)));
        Assert.Equal(
            ["nothing", "priced", "floor"],
            Names(Order([priced, floor, nothing], BreakdownSortColumn.Cost, ascending: true)));
    }

    [Fact]
    public void TheTotalsRowStaysAtTheBottom()
    {
        // Totals dwarf every body row, so a naive sort would drop it into (usually the top of)
        // the table. It is appended after the sort instead, in both directions and on the name
        // column, where its display name doesn't sort last either.
        var totals = Row("", input: 600, output: 60, cacheWrite: 15_000, cacheRead: 15_000, cost: 16.50);

        foreach (var column in Enum.GetValues<BreakdownSortColumn>())
        {
            foreach (var ascending in new[] { true, false })
            {
                var ordered = BreakdownSort.Order(Sample, totals, new BreakdownSortState(column, ascending));

                Assert.Equal(Sample.Length + 1, ordered.Count);
                Assert.Same(totals, ordered[^1]);
                Assert.DoesNotContain(totals, ordered.Take(ordered.Count - 1));
            }
        }
    }

    [Fact]
    public void OrderWithoutTotalsReturnsOnlyTheBody()
    {
        var ordered = BreakdownSort.Order(Sample, totals: null, BreakdownSortState.Default);

        Assert.Equal(Sample.Length, ordered.Count);
    }

    [Fact]
    public void TiesKeepTheOrderTheyArrivedIn()
    {
        // Every row costs the same, so the incoming order (the store's cost-then-tokens ordering)
        // is what's left to fall back on — a stable sort, not an arbitrary shuffle.
        var rows = new[] { Row("first", cost: 1.00), Row("second", cost: 1.00), Row("third", cost: 1.00) };

        Assert.Equal(["first", "second", "third"], Names(Order(rows, BreakdownSortColumn.Cost, ascending: false)));
        Assert.Equal(["first", "second", "third"], Names(Order(rows, BreakdownSortColumn.Cost, ascending: true)));
    }

    [Fact]
    public void AnEmptyBodyStillGetsItsTotals()
    {
        var totals = Row("", cost: 1.00);

        var ordered = BreakdownSort.Order([], totals, BreakdownSortState.Default);

        Assert.Same(totals, Assert.Single(ordered));
    }
}
