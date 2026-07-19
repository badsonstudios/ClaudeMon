namespace ClaudeMon.Tests;

using ClaudeMon.UI;

public class FlyoutMetricsTests
{
    [Fact]
    public void ForDpi_96_MatchesBaselineLayout()
    {
        var m = FlyoutMetrics.ForDpi(96);

        // At 100% scaling the metrics equal the original hand-tuned pixel layout.
        Assert.Equal(FlyoutMetrics.BaseLeftInset, m.LeftInset);
        Assert.Equal(FlyoutMetrics.BaseTopPadding, m.TopPadding);
        Assert.Equal(FlyoutMetrics.BaseTitleAdvance, m.TitleAdvance);
        Assert.Equal(FlyoutMetrics.BaseRowAdvance, m.RowAdvance);
        Assert.Equal(FlyoutMetrics.BaseAuthMessageHeight, m.AuthMessageHeight);
        Assert.Equal(FlyoutMetrics.BaseWidth, m.Width);
        Assert.Equal(FlyoutMetrics.BaseBarHeight, m.BarHeight);
    }

    [Fact]
    public void ForDpi_144_ScalesEveryDimensionBy150Percent()
    {
        var m = FlyoutMetrics.ForDpi(144);

        // Nothing is left in fixed pixels — every value scales with DPI.
        Assert.Equal(21, m.LeftInset);          // 14 * 1.5
        Assert.Equal(18, m.TopPadding);         // 12 * 1.5
        Assert.Equal(42, m.TitleAdvance);       // 28 * 1.5
        Assert.Equal(63, m.RowAdvance);         // 42 * 1.5
        Assert.Equal(66, m.AuthMessageHeight);  // 44 * 1.5
        Assert.Equal(420, m.Width);             // 280 * 1.5
        Assert.Equal(12, m.BarHeight);          // 8 * 1.5
        Assert.Equal(27, m.LabelToBarGap);      // 18 * 1.5
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void ContentSize_WidthScalesWithDpi(int dpi)
    {
        var m = FlyoutMetrics.ForDpi(dpi);
        var size = m.ContentSize(isAuthError: false, usageRows: 2, hasForecast: true);

        Assert.Equal(m.Width, size.Width);
        Assert.True(size.Width > 0 && size.Height > 0);
    }

    [Fact]
    public void ContentSize_HeightGrowsWithMoreRows()
    {
        var m = FlyoutMetrics.ForDpi(96);

        var none = m.ContentSize(false, usageRows: 0).Height;
        var one = m.ContentSize(false, usageRows: 1, hasForecast: true).Height;
        var two = m.ContentSize(false, usageRows: 2, hasForecast: true).Height;

        Assert.True(one > none, "one row should be taller than the no-data placeholder");
        Assert.True(two > one, "two rows should be taller than one");
        // The second row adds exactly one row advance.
        Assert.Equal(m.RowAdvance, two - one);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ContentSize_EachExtraLimitRowAddsOneRowAdvance(int rows)
    {
        // limits[] can carry more than the classic two buckets (e.g. per-model weekly caps);
        // the flyout must grow by exactly one row advance per extra bar.
        var m = FlyoutMetrics.ForDpi(96);

        var fewer = m.ContentSize(false, usageRows: rows - 1, hasForecast: true).Height;
        var more = m.ContentSize(false, usageRows: rows, hasForecast: true).Height;

        Assert.Equal(m.RowAdvance, more - fewer);
    }

    [Fact]
    public void ContentSize_AlwaysContainsLastDrawnElement()
    {
        // The box must be at least as tall as the bottom of the status line so the
        // content can never be clipped/overlapped (the bug this fix addresses).
        foreach (var dpi in new[] { 96, 120, 144, 192 })
        {
            var m = FlyoutMetrics.ForDpi(dpi);

            foreach (var (auth, rows, forecast) in new[]
            {
                (true, 0, false),
                (false, 2, true),
                (false, 1, true),
                (false, 4, true),
                (false, 0, false),
            })
            {
                var body = auth
                    ? m.AuthMessageHeight
                    : rows > 0
                        ? rows * m.RowAdvance
                        : m.NoDataAdvance;

                var statusBottom = m.TopPadding + m.TitleAdvance + body + m.StatusGap + m.StatusLineHeight;
                var height = m.ContentSize(auth, rows, forecast).Height;

                Assert.True(height >= statusBottom,
                    $"dpi={dpi} auth={auth} rows={rows} forecast={forecast}: height {height} < content bottom {statusBottom}");
            }
        }
    }

    [Fact]
    public void ContentSize_96Dpi_TwoRows_PinsBaselineHeight()
    {
        // Locks the at-100% layout. Base rows: 12 + 28 + (42*2) + 4 + 18 + 14 = 160,
        // plus the 5-hour forecast band (6 + 16) = 182.
        var m = FlyoutMetrics.ForDpi(96);
        Assert.Equal(182, m.ContentSize(false, usageRows: 2, hasForecast: true).Height);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void RowInternalAdvances_FitWithinRowAdvance(int dpi)
    {
        // Within a row the paint code advances LabelToBarGap + BarHeight +
        // BarBottomGap, then draws the reset-countdown line; the caller then
        // advances by RowAdvance to the next row. The reset line must start before
        // the next row does (with headroom for its text), so the internal advance
        // has to stay strictly under RowAdvance at every DPI — else rows overlap.
        var m = FlyoutMetrics.ForDpi(dpi);
        var resetLineOffset = m.LabelToBarGap + m.BarHeight + m.BarBottomGap;

        Assert.True(resetLineOffset < m.RowAdvance,
            $"dpi={dpi}: reset line offset {resetLineOffset} not under RowAdvance {m.RowAdvance}");
        // And there must be a meaningful gap left for the reset text line itself.
        Assert.True(m.RowAdvance - resetLineOffset >= m.BarHeight,
            $"dpi={dpi}: only {m.RowAdvance - resetLineOffset}px left for the reset line");
    }

    [Fact]
    public void ContentSize_WithHistory_AddsSparklineBand()
    {
        var m = FlyoutMetrics.ForDpi(96);

        var without = m.ContentSize(false, usageRows: 2, hasForecast: true, hasHistory: false).Height;
        var with = m.ContentSize(false, usageRows: 2, hasForecast: true, hasHistory: true).Height;

        Assert.Equal(m.SparklineGap + m.SparklineHeight, with - without);
    }

    [Fact]
    public void ContentSize_AuthError_IgnoresHistory()
    {
        var m = FlyoutMetrics.ForDpi(96);

        // No sparkline in the sign-in-expired state, even if history exists.
        var withHistory = m.ContentSize(isAuthError: true, 0, false, hasHistory: true).Height;
        var withoutHistory = m.ContentSize(isAuthError: true, 0, false, hasHistory: false).Height;

        Assert.Equal(withoutHistory, withHistory);
    }

    [Fact]
    public void ContentSize_WithFiveHour_AddsForecastBand()
    {
        var m = FlyoutMetrics.ForDpi(96);

        // The forecast line accompanies the 5-hour display. Compare the 5-hour case
        // against a no-data flyout, isolating the forecast + the one usage row.
        var withFiveHour = m.ContentSize(false, usageRows: 1, hasForecast: true).Height;
        var noData = m.ContentSize(false, usageRows: 0).Height;

        var expectedDelta = (m.RowAdvance - m.NoDataAdvance) + m.ForecastGap + m.ForecastHeight;
        Assert.Equal(expectedDelta, withFiveHour - noData);
    }

    [Fact]
    public void ContentSize_AuthError_HasNoForecastBand()
    {
        var m = FlyoutMetrics.ForDpi(96);

        // Auth-error height must not include the forecast band regardless of flags.
        var authNoData = m.ContentSize(isAuthError: true, 0).Height;
        var authFiveHour = m.ContentSize(isAuthError: true, 1, hasForecast: true).Height;

        Assert.Equal(authNoData, authFiveHour);
    }

    [Fact]
    public void ContentSize_WithLocalCost_AddsLocalCostBand()
    {
        var m = FlyoutMetrics.ForDpi(96);

        var without = m.ContentSize(false, usageRows: 2, hasForecast: true, hasHistory: true).Height;
        var with = m.ContentSize(false, usageRows: 2, hasForecast: true, hasHistory: true, hasLocalCost: true).Height;

        Assert.Equal(m.LocalCostGap + m.LocalCostHeight, with - without);
    }

    [Fact]
    public void ContentSize_AuthError_HasNoLocalCostBand()
    {
        var m = FlyoutMetrics.ForDpi(96);

        var without = m.ContentSize(isAuthError: true, 0).Height;
        var with = m.ContentSize(isAuthError: true, 0, hasLocalCost: true).Height;

        Assert.Equal(without, with);
    }

    [Fact]
    public void ForDpi_NonPositiveDpi_FallsBackTo96()
    {
        var fallback = FlyoutMetrics.ForDpi(0);
        var baseline = FlyoutMetrics.ForDpi(96);

        Assert.Equal(baseline.Width, fallback.Width);
        Assert.Equal(baseline.RowAdvance, fallback.RowAdvance);
    }
}
