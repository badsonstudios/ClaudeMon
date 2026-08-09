namespace ClaudeMon.Tests;

using ClaudeMon.UI;

/// <summary>
/// The Settings window's fit-to-monitor math (#139). The window needs a desktop session, so the
/// parts that decide whether it fits — and what its height and top edge do when it doesn't — are
/// pinned here.
/// </summary>
public class SettingsFormLayoutTests
{
    // A typical FixedDialog's title bar + borders at 96 DPI.
    private const int Chrome = 39;
    private const int MinClient = 200;

    [Fact]
    public void ClampClientHeight_LeavesAFittingWindowAlone()
    {
        // The Alerts tab on a 1080p desktop: plenty of room, so nothing changes and nothing scrolls.
        var (height, scroll) = SettingsFormLayout.ClampClientHeight(620, 1040, Chrome, MinClient);

        Assert.Equal(620, height);
        Assert.False(scroll);
    }

    [Fact]
    public void ClampClientHeight_ClampsToTheWorkingAreaLessTheChrome()
    {
        // 12 rows at 150% on a 768-high panel: the window wants more than the monitor has.
        var (height, scroll) = SettingsFormLayout.ClampClientHeight(930, 728, 58, MinClient);

        Assert.Equal(670, height);
        Assert.True(scroll);
    }

    [Fact]
    public void ClampClientHeight_DoesNotScrollWhenTheWindowExactlyFits()
    {
        var (height, scroll) = SettingsFormLayout.ClampClientHeight(1001, 1040, Chrome, MinClient);

        Assert.Equal(1001, height);
        Assert.False(scroll);
    }

    [Fact]
    public void ClampClientHeight_ScrollsOnePixelPastTheFit()
    {
        var (height, scroll) = SettingsFormLayout.ClampClientHeight(1002, 1040, Chrome, MinClient);

        Assert.Equal(1001, height);
        Assert.True(scroll);
    }

    [Theory]
    [InlineData(0)]     // no monitor reported
    [InlineData(-500)]  // a nonsense working area
    [InlineData(30)]    // a working area smaller than the chrome alone
    public void ClampClientHeight_FallsBackToTheFloorForADegenerateWorkingArea(int workingAreaHeight)
    {
        var (height, scroll) = SettingsFormLayout.ClampClientHeight(
            800, workingAreaHeight, Chrome, MinClient);

        Assert.Equal(MinClient, height);
        Assert.True(scroll);
    }

    [Fact]
    public void ClampClientHeight_TreatsNegativeChromeAsNone()
    {
        var (height, scroll) = SettingsFormLayout.ClampClientHeight(800, 700, -40, MinClient);

        Assert.Equal(700, height);
        Assert.True(scroll);
    }

    [Fact]
    public void ClampClientHeight_NeverReturnsANegativeHeight()
    {
        var (height, scroll) = SettingsFormLayout.ClampClientHeight(-100, 1040, Chrome, MinClient);

        Assert.Equal(0, height);
        Assert.False(scroll);
    }

    [Fact]
    public void ClampClientHeight_DoesNotStretchAShortTabUpToTheFloor()
    {
        // The General tab is two rows; the floor guards the clamp, it is not a minimum size.
        var (height, scroll) = SettingsFormLayout.ClampClientHeight(150, 1040, Chrome, MinClient);

        Assert.Equal(150, height);
        Assert.False(scroll);
    }

    [Fact]
    public void ClampClientHeight_KeepsTheFloorEvenWhenItOverflowsTheWorkingArea()
    {
        // Pinning the trade-off in the floor's doc: below chrome + floor the window is deliberately
        // taller than the "monitor", because the alternative is a window too short to use.
        var (height, scroll) = SettingsFormLayout.ClampClientHeight(800, 200, Chrome, MinClient);

        Assert.Equal(MinClient, height);
        Assert.True(scroll);
    }

    [Fact]
    public void ClampTop_LeavesAWindowThatAlreadyFitsWhereItIs()
    {
        Assert.Equal(249, SettingsFormLayout.ClampTop(249, 268, areaTop: 0, areaBottom: 728));
    }

    [Fact]
    public void ClampTop_SlidesAWindowUpOffTheBottomEdge()
    {
        // The 1366x768 case: centered for the two-row General tab, then switched to a tall tab.
        Assert.Equal(125, SettingsFormLayout.ClampTop(249, 603, areaTop: 0, areaBottom: 728));
    }

    [Fact]
    public void ClampTop_PinsAWindowTallerThanTheAreaToTheTopSoItsTitleBarStaysReachable()
    {
        Assert.Equal(0, SettingsFormLayout.ClampTop(249, 900, areaTop: 0, areaBottom: 728));
    }

    [Fact]
    public void ClampTop_RespectsAMonitorThatDoesNotStartAtTheOrigin()
    {
        // A monitor above the primary has a negative top; a taskbar on top gives a positive one.
        Assert.Equal(-1080, SettingsFormLayout.ClampTop(-900, 1200, areaTop: -1080, areaBottom: -40));
        Assert.Equal(1140, SettingsFormLayout.ClampTop(1300, 500, areaTop: 1080, areaBottom: 1640));
    }

    [Fact]
    public void ClampTop_TreatsANegativeHeightAsZero()
    {
        Assert.Equal(249, SettingsFormLayout.ClampTop(249, -50, areaTop: 0, areaBottom: 728));
    }
}
