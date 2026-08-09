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
    /// TimeSpan's range. With the reset time unknown, a multi-day "~144h to limit" readout
    /// would be noise wearing an estimate's clothes. With it known, this ceiling running
    /// before the reset comparison is what keeps a near-flat trend classified as "no
    /// estimate" rather than promoted to "safe" (#158) — a projection too absurd to show
    /// is also too absurd to draw conclusions from.
    /// </summary>
    private const double MaxProjectionMinutes = 24 * 60;

    /// <summary>
    /// Estimates the time until the 5-hour window hits 100%, as a typed result (#158): a
    /// <see cref="TimeToLimitKind.Projection"/> with the remaining span,
    /// <see cref="TimeToLimitKind.Safe"/> when the projection lands after the window resets
    /// (you won't reach the cap this window — good news, not a missing estimate), or
    /// <see cref="TimeToLimitKind.NoEstimate"/> when no meaningful estimate can be made:
    /// fewer than three samples, no time span, a flat/declining rate, or a projection so
    /// distant it carries no information (see <see cref="MaxProjectionMinutes"/>).
    /// </summary>
    /// <param name="recent">Recent samples (oldest first) over the burn window.</param>
    /// <param name="currentPct">The latest 5-hour utilization percentage.</param>
    /// <param name="timeUntilReset">Time until the 5-hour window resets, if known.</param>
    public static TimeToLimitEstimate EstimateTimeToLimit(
        IReadOnlyList<UsageSample> recent, double currentPct, TimeSpan? timeUntilReset)
    {
        // Already maxed out — no projection needed.
        if (currentPct >= LimitPct)
            return TimeToLimitEstimate.AtLimit;

        // Three samples is the floor: two points fit any noise perfectly (zero
        // residual), so the slope — and the resulting ETA — would be untrustworthy.
        if (recent is null || recent.Count < 3)
            return TimeToLimitEstimate.NoEstimate;

        var slopePerMinute = SlopePctPerMinute(recent);
        if (slopePerMinute is null or <= 0)
            return TimeToLimitEstimate.NoEstimate;

        var minutesToLimit = (LimitPct - currentPct) / slopePerMinute.Value;

        // NaN first — it compares false against everything, so it has to be excluded
        // explicitly. The upper bound covers infinity as well as the finite-but-absurd
        // projections a near-zero slope produces (see MaxProjectionMinutes). This guard
        // runs before the reset check on purpose: an epsilon-above-flat trend stays "no
        // estimate" like an exactly-flat one, rather than flipping to "safe".
        if (double.IsNaN(minutesToLimit) || minutesToLimit < 0 || minutesToLimit > MaxProjectionMinutes)
            return TimeToLimitEstimate.NoEstimate;

        var eta = TimeSpan.FromMinutes(minutesToLimit);

        // When the reset time is known (non-null), don't project past it: a window that
        // resets first won't reach the cap this window — that's the safe case, not a
        // missing estimate — and one already resetting (reset <= 0) is beaten by the reset
        // no matter the trend. Callers pass null when the reset time is unknown, so a
        // non-null value here is authoritative.
        if (timeUntilReset is { } reset)
        {
            if (reset <= TimeSpan.Zero || eta > reset)
                return TimeToLimitEstimate.Safe;
        }

        return TimeToLimitEstimate.Projection(eta);
    }

    /// <summary>
    /// Formats an estimate for display in the flyout. The safe case gets room to say why
    /// ("safe (resets first)"); the fixed states already say what they mean; only the spans
    /// take the "to limit" suffix.
    /// </summary>
    public static string FormatTimeToLimit(TimeToLimitEstimate estimate) => estimate.Kind switch
    {
        TimeToLimitKind.Safe => "safe (resets first)",
        TimeToLimitKind.Projection when estimate.Eta is { } eta && eta > TimeSpan.Zero =>
            $"{FormatTimeToLimitCompact(estimate)} to limit",
        _ => FormatTimeToLimitCompact(estimate),
    };

    /// <summary>
    /// The same estimate for the taskbar readout's optional element — where the space is a
    /// fraction of the flyout's and the "Claude" label above it supplies the context. The safe
    /// case shows a bare "safe" (lower-case word style, cf. the countdown's "idle"); spans drop
    /// the "to limit" suffix. Shares every state, rounding, and wording decision with
    /// <see cref="FormatTimeToLimit"/>, so the flyout and the readout can never disagree about
    /// the same projection. The leading <c>~</c> is what distinguishes an estimate from the
    /// reset countdown beside it, which is a known clock time.
    /// </summary>
    public static string FormatTimeToLimitCompact(TimeToLimitEstimate estimate) => estimate.Kind switch
    {
        TimeToLimitKind.Safe => "safe",
        TimeToLimitKind.AtLimit => "at limit",
        TimeToLimitKind.Projection when estimate.Eta is { } eta => FormatSpan(eta),
        _ => "—",
    };

    private static string FormatSpan(TimeSpan eta)
    {
        if (eta <= TimeSpan.Zero)
            return "at limit";

        var totalMinutes = (int)Math.Round(eta.TotalMinutes);
        if (totalMinutes < 1)
            return "<1m";

        if (totalMinutes < 60)
            return $"~{totalMinutes}m";

        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return minutes == 0 ? $"~{hours}h" : $"~{hours}h {minutes}m";
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
