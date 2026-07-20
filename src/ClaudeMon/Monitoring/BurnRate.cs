namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;

/// <summary>
/// Projects when the 5-hour usage window will reach 100% at the current rate,
/// using the recent slope of recorded usage samples. Pure and side-effect free
/// so the projection math is unit-testable.
/// </summary>
public static class BurnRate
{
    private const double LimitPct = 100.0;

    /// <summary>
    /// Ceiling on a projection before it stops being an estimate and starts being noise.
    /// A trend that is flat to within floating-point error still produces a minuscule
    /// *positive* slope, and dividing the remaining headroom by it yields a finite but
    /// enormous number of minutes — one that <see cref="TimeSpan.FromMinutes"/> cannot
    /// represent, which crashed the app when the flyout opened (issue #100).
    ///
    /// 24 hours: nearly five times the window being projected, so it can't discard an
    /// estimate anyone would act on, while still being six orders of magnitude inside
    /// TimeSpan's range. It only ever applies when the reset time is unknown — a known
    /// reset is a tighter bound already (see the caller-supplied check below) — and there
    /// a multi-day "~144h to limit" readout would be noise wearing an estimate's clothes.
    /// </summary>
    private const double MaxProjectionMinutes = 24 * 60;

    /// <summary>
    /// Estimates the time until the 5-hour window hits 100%, or <c>null</c> when no
    /// meaningful estimate can be made: fewer than two samples, no time span,
    /// a flat/declining rate, a projection so distant it carries no information
    /// (see <see cref="MaxProjectionMinutes"/>), or one that lands after the window
    /// resets (you won't reach the cap this window).
    /// </summary>
    /// <param name="recent">Recent samples (oldest first) over the burn window.</param>
    /// <param name="currentPct">The latest 5-hour utilization percentage.</param>
    /// <param name="timeUntilReset">Time until the 5-hour window resets, if known.</param>
    public static TimeSpan? EstimateTimeToLimit(
        IReadOnlyList<UsageSample> recent, double currentPct, TimeSpan? timeUntilReset)
    {
        // Already maxed out — no projection needed.
        if (currentPct >= LimitPct)
            return TimeSpan.Zero;

        // Three samples is the floor: two points fit any noise perfectly (zero
        // residual), so the slope — and the resulting ETA — would be untrustworthy.
        if (recent is null || recent.Count < 3)
            return null;

        var slopePerMinute = SlopePctPerMinute(recent);
        if (slopePerMinute is null or <= 0)
            return null;

        var minutesToLimit = (LimitPct - currentPct) / slopePerMinute.Value;

        // NaN first — it compares false against everything, so it has to be excluded
        // explicitly. The upper bound covers infinity as well as the finite-but-absurd
        // projections a near-zero slope produces (see MaxProjectionMinutes).
        if (double.IsNaN(minutesToLimit) || minutesToLimit < 0 || minutesToLimit > MaxProjectionMinutes)
            return null;

        var eta = TimeSpan.FromMinutes(minutesToLimit);

        // When the reset time is known (non-null), don't project past it: a window
        // that resets first won't reach the cap, and one that is already resetting
        // (reset <= 0) has no meaningful "time to limit". Callers pass null when the
        // reset time is unknown, so a non-null value here is authoritative.
        if (timeUntilReset is { } reset)
        {
            if (reset <= TimeSpan.Zero || eta > reset)
                return null;
        }

        return eta;
    }

    /// <summary>Formats an estimate for display in the flyout.</summary>
    public static string FormatTimeToLimit(TimeSpan? eta)
    {
        if (eta is null)
            return "—";

        var value = eta.Value;
        if (value <= TimeSpan.Zero)
            return "at limit";

        var totalMinutes = (int)Math.Round(value.TotalMinutes);
        if (totalMinutes < 1)
            return "<1m to limit";

        if (totalMinutes < 60)
            return $"~{totalMinutes}m to limit";

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return minutes == 0
            ? $"~{hours}h to limit"
            : $"~{hours}h {minutes}m to limit";
    }

    // Least-squares slope of utilization (percent) over time (minutes). Returns
    // null when the samples share a single instant (no time span to divide by).
    private static double? SlopePctPerMinute(IReadOnlyList<UsageSample> samples)
    {
        var origin = samples[0].Timestamp;
        double n = samples.Count;
        double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;

        foreach (var s in samples)
        {
            var x = (s.Timestamp - origin).TotalMinutes;
            var y = s.FiveHourPct;
            sumX += x;
            sumY += y;
            sumXy += x * y;
            sumXx += x * x;
        }

        var denominator = (n * sumXx) - (sumX * sumX);
        if (denominator == 0)
            return null;

        return ((n * sumXy) - (sumX * sumY)) / denominator;
    }
}
