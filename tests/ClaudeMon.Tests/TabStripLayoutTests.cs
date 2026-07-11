namespace ClaudeMon.Tests;

using System.Drawing;
using ClaudeMon.UI;

public class TabStripLayoutTests
{
    [Fact]
    public void TabBounds_SizesEachTabFromLabelPlusPadding()
    {
        var bounds = TabStripLayout.TabBounds([30, 50], padding: 10, gap: 6, height: 36);

        Assert.Equal(new Rectangle(0, 0, 50, 36), bounds[0]);   // 30 + 2*10
        Assert.Equal(new Rectangle(56, 0, 70, 36), bounds[1]);  // after 50 + gap 6
    }

    [Fact]
    public void TabBounds_EmptyLabels_YieldsNoTabs()
    {
        Assert.Empty(TabStripLayout.TabBounds([], padding: 10, gap: 6, height: 36));
    }

    [Fact]
    public void TabBounds_TabsNeverOverlap()
    {
        var bounds = TabStripLayout.TabBounds([12, 40, 25, 33], padding: 8, gap: 4, height: 30);

        for (var i = 1; i < bounds.Count; i++)
            Assert.True(bounds[i].Left > bounds[i - 1].Right, "tabs must be separated by the gap");
    }

    [Theory]
    [InlineData(0, 18, 0)]    // inside the first tab
    [InlineData(60, 18, 1)]   // inside the second tab
    [InlineData(52, 18, -1)]  // in the gap between tabs
    [InlineData(500, 18, -1)] // right of every tab
    [InlineData(10, 40, -1)]  // below the strip
    [InlineData(-1, 18, -1)]  // left of the first tab
    public void HitTest_MapsPointsToTabs(int x, int y, int expected)
    {
        // Two tabs: [0,50) and [56,126) at height 36 (same geometry as the sizing test above).
        var bounds = TabStripLayout.TabBounds([30, 50], padding: 10, gap: 6, height: 36);

        Assert.Equal(expected, TabStripLayout.HitTest(bounds, new Point(x, y)));
    }
}
