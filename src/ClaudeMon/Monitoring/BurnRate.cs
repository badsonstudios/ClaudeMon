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
    /// Estimates the time until the 5-hour window hits 100%, or <c>null</c> when no
    /// meaningful estimate can be made: fewer than two samples, no time span,
    /// a flat/declining rate, or a projection that lands after the window resets
    /// (you won't reach the cap this window).
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
        if (double.IsNaN(minutesToLimit) || double.IsInfinity(minutesToLimit) || minutesToLimit < 0)
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
