namespace ClaudeMon.Tests;

using ClaudeMon.UI;

public class SettingsMetricsTests
{
    [Fact]
    public void ForDpi_96_MatchesBaselineLayout()
    {
        var m = SettingsMetrics.ForDpi(96);

        // At 100% scaling the metrics equal the original hand-tuned pixel layout.
        Assert.Equal(SettingsMetrics.BaseWindowWidth, m.WindowWidth);
        Assert.Equal(SettingsMetrics.BasePad, m.Pad);
        Assert.Equal(SettingsMetrics.BaseTopMargin, m.TopMargin);
        Assert.Equal(SettingsMetrics.BaseWindowWidth - SettingsMetrics.BasePad, m.ContentRight);
        Assert.Equal(SettingsMetrics.BaseControlLeft, m.ControlLeft);
        Assert.Equal(206, m.ComboWidth); // 456 - 250, the pre-fix hand-tuned value
        Assert.Equal(SettingsMetrics.BaseNumericWidth, m.NumericWidth);
        Assert.Equal(SettingsMetrics.BaseToggleWidth, m.ToggleWidth);
        Assert.Equal(SettingsMetrics.BaseToggleHeight, m.ToggleHeight);
        Assert.Equal(SettingsMetrics.BaseRowHeight, m.RowHeight);
        Assert.Equal(SettingsMetrics.BaseHeaderHeight, m.HeaderHeight);
        Assert.Equal(1, m.DividerHeight);
        Assert.Equal(SettingsMetrics.BaseButtonWidth, m.ButtonWidth);
        Assert.Equal(SettingsMetrics.BaseButtonHeight, m.ButtonHeight);
    }

    [Fact]
    public void ForDpi_144_ScalesEveryDimensionBy150Percent()
    {
        var m = SettingsMetrics.ForDpi(144);

        // Nothing is left in fixed pixels — every value scales with DPI.
        Assert.Equal(720, m.WindowWidth);       // 480 * 1.5
        Assert.Equal(36, m.Pad);                // 24 * 1.5
        Assert.Equal(9, m.TopMargin);           // 6 * 1.5
        Assert.Equal(684, m.ContentRight);      // 720 - 36
        Assert.Equal(375, m.ControlLeft);       // 250 * 1.5
        Assert.Equal(309, m.ComboWidth);        // 684 - 375
        Assert.Equal(96, m.NumericWidth);       // 64 * 1.5
        Assert.Equal(60, m.ToggleWidth);        // 40 * 1.5
        Assert.Equal(30, m.ToggleHeight);       // 20 * 1.5
        Assert.Equal(24, m.Indent);             // 16 * 1.5
        Assert.Equal(51, m.RowHeight);          // 34 * 1.5
        Assert.Equal(66, m.HeaderHeight);       // 44 * 1.5
        Assert.Equal(123, m.ButtonWidth);       // 82 * 1.5
        Assert.Equal(45, m.ButtonHeight);       // 30 * 1.5
        Assert.Equal(21, m.ButtonTopGap);       // 14 * 1.5
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(168)] // 175% — the worst midpoint rounding (217.5, 143.5, 304.5)
    [InlineData(192)]
    public void ForDpi_LayoutStaysConsistent(int dpi)
    {
        var m = SettingsMetrics.ForDpi(dpi);

        // The control column fills exactly to the right content edge…
        Assert.Equal(m.ContentRight, m.ControlLeft + m.ComboWidth);
        // …the label area (indent included) never reaches the control column…
        Assert.True(m.Pad + m.Indent < m.ControlLeft);
        // …a numeric plus its suffix gap stays inside the combo column…
        Assert.True(m.NumericWidth + m.SuffixGap < m.ComboWidth);
        // …controls fit vertically within their rows…
        Assert.True(m.ToggleOffset + m.ToggleHeight <= m.RowHeight);
        Assert.True(m.HeaderDividerOffset + m.DividerHeight <= m.HeaderHeight);
        // …and both buttons fit side by side without overlapping.
        Assert.True(m.OkButtonRightOffset - m.ButtonWidth >= m.CancelButtonRightOffset,
            "OK button must end before the Cancel button starts");
        Assert.True(m.CancelButtonRightOffset >= m.ButtonWidth,
            "Cancel button must not extend past the right content edge");
        Assert.True(m.DividerHeight >= 1);
    }

    [Fact]
    public void ForDpi_NonPositiveDpi_FallsBackTo96()
    {
        var fallback = SettingsMetrics.ForDpi(0);
        var baseline = SettingsMetrics.ForDpi(96);

        Assert.Equal(baseline.WindowWidth, fallback.WindowWidth);
        Assert.Equal(baseline.RowHeight, fallback.RowHeight);
    }
}
