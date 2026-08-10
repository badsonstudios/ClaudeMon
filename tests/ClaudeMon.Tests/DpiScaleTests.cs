namespace ClaudeMon.Tests;

using ClaudeMon.UI;

public class DpiScaleTests
{
    [Theory]
    [InlineData(96, 1.0f)]     // 100%
    [InlineData(120, 1.25f)]   // 125%
    [InlineData(144, 1.5f)]    // 150%
    [InlineData(192, 2.0f)]    // 200%
    public void FactorForDpi_ScalesRelativeTo96(int dpi, float expected)
    {
        Assert.Equal(expected, DpiScale.FactorForDpi(dpi));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-96)]
    public void FactorForDpi_NonPositiveDpi_FallsBackTo100Percent(int dpi)
    {
        // A window that hasn't been placed yet can report 0 DPI; scaling by zero would
        // collapse every hand-scaled layout to nothing.
        Assert.Equal(1.0f, DpiScale.FactorForDpi(dpi));
    }

    [Theory]
    [InlineData(10, 1.0f, 10)]
    [InlineData(10, 1.5f, 15)]
    [InlineData(7, 1.25f, 9)]    // 8.75 → 9
    [InlineData(-10, 1.5f, -15)]
    public void Scale_RoundsToPhysicalPixels(int logical, float scale, int expected)
    {
        Assert.Equal(expected, DpiScale.Scale(logical, scale));
    }

    [Fact]
    public void Scale_RoundsHalvesAwayFromZero()
    {
        // The whole point of the shared helper: the inline copies it replaced used banker's
        // rounding, so 1-pixel gaps appeared and disappeared between windows at 150%.
        Assert.Equal(5, DpiScale.Scale(3, 1.5f));    // 4.5 → 5, not 4
        Assert.Equal(-5, DpiScale.Scale(-3, 1.5f));  // -4.5 → -5, not -4
    }
}
