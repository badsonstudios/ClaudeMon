namespace ClaudeMon.UI;

using System.Drawing;

/// <summary>
/// DPI-scaled layout values for <see cref="FlyoutPanel"/>. Every dimension is
/// derived from a base (96-DPI) constant multiplied by the current DPI scale, so
/// the hand-drawn flyout lays out correctly at any display scaling instead of
/// drawing DPI-scaled fonts into a fixed-pixel box (which collapses/overlaps the
/// content on high-DPI displays).
///
/// This type is pure (no UI dependency) so the layout math is unit-testable.
/// </summary>
public sealed class FlyoutMetrics
{
    // Base values at 96 DPI (the original hand-tuned pixel layout).
    internal const int BaseLeftInset = 14;
    internal const int BaseTopPadding = 12;
    internal const int BaseTitleAdvance = 28;
    internal const int BaseRowAdvance = 42;
    internal const int BaseAuthMessageHeight = 44;
    internal const int BaseNoDataAdvance = 20;
    internal const int BaseStatusGap = 4;
    internal const int BaseStatusLineHeight = 18;
    internal const int BaseBottomPadding = 14;
    internal const int BaseWidth = 280;
    // Keep the bar height even: the progress bar's corner radius is BarHeight / 2
    // (integer), so an odd value would truncate and lose a pixel of roundness.
    internal const int BaseBarHeight = 8;
    internal const int BaseLabelToBarGap = 18;
    internal const int BaseBarBottomGap = 2;
    internal const int BaseSparklineGap = 8;
    internal const int BaseSparklineHeight = 26;
    internal const int BaseForecastGap = 6;
    internal const int BaseForecastHeight = 16;

    public int LeftInset { get; }
    public int TopPadding { get; }
    public int TitleAdvance { get; }
    public int RowAdvance { get; }
    public int AuthMessageHeight { get; }
    public int NoDataAdvance { get; }
    public int StatusGap { get; }
    public int StatusLineHeight { get; }
    public int BottomPadding { get; }
    public int Width { get; }
    public int BarHeight { get; }
    public int LabelToBarGap { get; }
    public int BarBottomGap { get; }
    public int SparklineGap { get; }
    public int SparklineHeight { get; }
    public int ForecastGap { get; }
    public int ForecastHeight { get; }

    private FlyoutMetrics(int dpi)
    {
        var scale = dpi / 96f;
        int S(int baseValue) => DpiScale.Scale(baseValue, scale);

        LeftInset = S(BaseLeftInset);
        TopPadding = S(BaseTopPadding);
        TitleAdvance = S(BaseTitleAdvance);
        RowAdvance = S(BaseRowAdvance);
        AuthMessageHeight = S(BaseAuthMessageHeight);
        NoDataAdvance = S(BaseNoDataAdvance);
        StatusGap = S(BaseStatusGap);
        StatusLineHeight = S(BaseStatusLineHeight);
        BottomPadding = S(BaseBottomPadding);
        Width = S(BaseWidth);
        BarHeight = S(BaseBarHeight);
        LabelToBarGap = S(BaseLabelToBarGap);
        BarBottomGap = S(BaseBarBottomGap);
        SparklineGap = S(BaseSparklineGap);
        SparklineHeight = S(BaseSparklineHeight);
        ForecastGap = S(BaseForecastGap);
        ForecastHeight = S(BaseForecastHeight);
    }

    /// <summary>Builds the scaled metrics for the given device DPI (96 = 100%).</summary>
    public static FlyoutMetrics ForDpi(int dpi) => new(dpi <= 0 ? 96 : dpi);

    /// <summary>
    /// The exact size the flyout needs to draw the given content without
    /// clipping or overlap. The form is sized to this so the box can never be
    /// shorter than what the paint code draws.
    /// </summary>
    public Size ContentSize(bool isAuthError, bool hasFiveHour, bool hasSevenDay, bool hasHistory = false)
    {
        int body;
        if (isAuthError)
        {
            body = AuthMessageHeight;
        }
        else
        {
            var rows = (hasFiveHour ? 1 : 0) + (hasSevenDay ? 1 : 0);
            body = rows > 0 ? rows * RowAdvance : NoDataAdvance;
        }

        // The sparkline band is only present with usable data (not in the
        // auth-expired state, and only when there is history to draw).
        if (hasHistory && !isAuthError)
            body += SparklineGap + SparklineHeight;

        // The burn-rate forecast line accompanies the 5-hour usage display.
        if (hasFiveHour && !isAuthError)
            body += ForecastGap + ForecastHeight;

        var height = TopPadding + TitleAdvance + body + StatusGap + StatusLineHeight + BottomPadding;
        return new Size(Width, height);
    }
}
