namespace ClaudeMon.Tests;

using ClaudeMon.UI;

/// <summary>
/// The resize math for the "Usage &amp; costs" window (#110). The window itself needs a desktop
/// session, so the parts that are easy to get wrong — the height split and the column widths at
/// the extremes — are pinned here instead.
/// </summary>
public class UsageBreakdownLayoutTests
{
    // The window's real 96-DPI metrics, so the cases below describe sizes it can actually be at.
    private const int Numeric = 62;
    private const int Cost = 78;
    private const int MinFirst = 120;
    private const int ScrollBar = 17;

    [Fact]
    public void SplitTableHeights_SharesEvenly()
    {
        Assert.Equal((150, 150), UsageBreakdownLayout.SplitTableHeights(300, minHeight: 66));
    }

    [Fact]
    public void SplitTableHeights_GivesTheOddPixelToTheSecondTable()
    {
        var (first, second) = UsageBreakdownLayout.SplitTableHeights(301, minHeight: 66);

        Assert.Equal(150, first);
        Assert.Equal(151, second);
        Assert.Equal(301, first + second);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(131)]   // one pixel under two floors
    [InlineData(-500)]  // a degenerate client size must not produce negative heights
    public void SplitTableHeights_NeverGoesBelowTheFloor(int available)
    {
        var (first, second) = UsageBreakdownLayout.SplitTableHeights(available, minHeight: 66);

        Assert.Equal(66, first);
        Assert.Equal(66, second);
    }

    [Fact]
    public void SplitTableHeights_GrowsBothTablesAsTheWindowGrows()
    {
        var small = UsageBreakdownLayout.SplitTableHeights(300, minHeight: 66);
        var large = UsageBreakdownLayout.SplitTableHeights(900, minHeight: 66);

        Assert.True(large.First > small.First);
        Assert.True(large.Second > small.Second);
    }

    [Fact]
    public void ColumnWidths_FirstColumnAbsorbsTheExtraWidth()
    {
        var widths = UsageBreakdownLayout.ColumnWidths(658, ScrollBar, Numeric, Cost, MinFirst);

        Assert.Equal(253, widths[0]);  // 658 - 17 - (5*62 + 78)
        Assert.Equal([Numeric, Numeric, Numeric, Numeric, Numeric], widths[1..6]);
        Assert.Equal(Cost, widths[6]);
    }

    [Fact]
    public void ColumnWidths_NumericColumnsAreUnaffectedByWidth()
    {
        var narrow = UsageBreakdownLayout.ColumnWidths(525, ScrollBar, Numeric, Cost, MinFirst);
        var wide = UsageBreakdownLayout.ColumnWidths(1900, ScrollBar, Numeric, Cost, MinFirst);

        Assert.Equal(narrow[1..], wide[1..]);
        Assert.True(wide[0] > narrow[0]);
    }

    [Fact]
    public void ColumnWidths_ReservesTheVerticalScrollbar()
    {
        // The reservation is what stops a long 30-day list from forcing a horizontal scrollbar
        // the moment the vertical one appears and shrinks the client area.
        var widths = UsageBreakdownLayout.ColumnWidths(658, ScrollBar, Numeric, Cost, MinFirst);

        Assert.Equal(658 - ScrollBar, widths.Sum());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    [InlineData(-40)]
    public void ColumnWidths_FirstColumnNeverCollapses(int clientWidth)
    {
        var widths = UsageBreakdownLayout.ColumnWidths(clientWidth, ScrollBar, Numeric, Cost, MinFirst);

        Assert.Equal(MinFirst, widths[0]);
        Assert.All(widths, w => Assert.True(w > 0, "no column may collapse to zero"));
    }

    [Fact]
    public void ColumnWidths_AtTheMinimumTableWidth_StillFitOnceTheScrollbarShows()
    {
        // MinTableWidth is what the window's MinimumSize is built from, and the tightest case is
        // the smallest allowed window showing a 30-day list: the vertical scrollbar eats its
        // reserved width and the first column is exactly at its floor, with nothing to spare. If
        // this ever overflows, the user gets a horizontal scrollbar at the minimum size.
        var min = UsageBreakdownLayout.MinTableWidth(ScrollBar, Numeric, Cost, MinFirst);
        var widths = UsageBreakdownLayout.ColumnWidths(min, ScrollBar, Numeric, Cost, MinFirst);

        Assert.Equal(MinFirst, widths[0]);
        Assert.Equal(min - ScrollBar, widths.Sum());
    }

    [Theory]
    [InlineData(525)]   // the minimum allowed table
    [InlineData(658)]   // the default window
    [InlineData(1116)]  // dragged wider
    [InlineData(3778)]  // maximised on a 4K monitor
    public void ColumnWidths_NeverOverflowOnceTheScrollbarShows(int availableWidth)
    {
        // The form always passes the width inside the border, ignoring a visible scrollbar, so
        // the same widths have to survive the scrollbar appearing at every size the window can
        // take. This is the "no horizontal scrollbar" guarantee.
        var widths = UsageBreakdownLayout.ColumnWidths(availableWidth, ScrollBar, Numeric, Cost, MinFirst);

        Assert.True(widths.Sum() <= availableWidth - ScrollBar,
            $"columns ({widths.Sum()}) overflow the {availableWidth - ScrollBar}px viewport left by the scrollbar");
    }

    [Fact]
    public void ColumnWidths_AlwaysReturnsSevenColumns()
    {
        Assert.Equal(7, UsageBreakdownLayout.ColumnWidths(700, ScrollBar, Numeric, Cost, MinFirst).Length);
    }

    [Fact]
    public void NegativeMetricsAreTreatedAsZero()
    {
        // Defensive only — DpiScale.Scale of a positive constant can't go negative — but the
        // clamps exist so a bad input degrades to "nothing reserved" instead of widening the
        // first column past the table or producing a negative minimum.
        var widths = UsageBreakdownLayout.ColumnWidths(700, -17, -62, -78, -120);
        Assert.Equal([700, 0, 0, 0, 0, 0, 0], widths);

        Assert.Equal(0, UsageBreakdownLayout.MinTableWidth(-17, -62, -78, -120));
        Assert.Equal((0, 0), UsageBreakdownLayout.SplitTableHeights(0, minHeight: -5));
    }

    [Fact]
    public void MinTableWidth_CoversEveryColumnPlusTheScrollbar()
    {
        Assert.Equal(
            MinFirst + (5 * Numeric) + Cost + ScrollBar,
            UsageBreakdownLayout.MinTableWidth(ScrollBar, Numeric, Cost, MinFirst));
    }

    [Fact]
    public void MinTableWidth_ScalesWithDpi()
    {
        // At 150% every metric is scaled before it reaches the layout, so the floor scales too —
        // this is what keeps the minimum window size honest on a high-DPI monitor.
        static int Sc(int v) => DpiScale.Scale(v, 1.5f);

        var at150 = UsageBreakdownLayout.MinTableWidth(26, Sc(Numeric), Sc(Cost), Sc(MinFirst));

        Assert.Equal(Sc(MinFirst) + (5 * Sc(Numeric)) + Sc(Cost) + 26, at150);
        Assert.True(at150 > UsageBreakdownLayout.MinTableWidth(ScrollBar, Numeric, Cost, MinFirst));
    }

    [Fact]
    public void DefaultHeight_IsTheChromePlusTwoTables()
    {
        Assert.Equal(400 + 150 + 150, UsageBreakdownLayout.DefaultHeight(400, 150, insertedRow: 0));
    }

    [Fact]
    public void DefaultHeight_InsertedRowComesOutOfTheTables_NotTheWindow()
    {
        // The invariant behind #113's "the window's default size is unchanged": the tab strip is
        // counted inside the chrome, and subtracting it again here is what keeps the window
        // opening at the height it did before the strip existed. Break the pairing and this fails.
        const int row = 42;
        const int chromeWithoutStrip = 400;

        Assert.Equal(
            UsageBreakdownLayout.DefaultHeight(chromeWithoutStrip, 150, insertedRow: 0),
            UsageBreakdownLayout.DefaultHeight(chromeWithoutStrip + row, 150, insertedRow: row));
    }

    [Fact]
    public void DefaultHeight_StillLeavesBothTablesWellAboveTheirFloor()
    {
        // The strip's row is only affordable because the default tables are far taller than the
        // floor SplitTableHeights clamps to; if that ever stops being true the window would open
        // with a clamped table and a gap at the bottom.
        var height = UsageBreakdownLayout.DefaultHeight(400, 150, insertedRow: 42);
        var (first, second) = UsageBreakdownLayout.SplitTableHeights(height - 400, minHeight: 66);

        Assert.True(first > 66 && second > 66, "the default tables must not land on their floor");
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, -10)]
    public void DefaultHeight_IgnoresNegativeMetrics(int table, int row)
    {
        Assert.Equal(400, UsageBreakdownLayout.DefaultHeight(400, table, row));
    }
}
