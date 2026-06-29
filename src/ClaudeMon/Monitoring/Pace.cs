namespace ClaudeMon.Monitoring;

/// <summary>
/// "Pace" is how fast usage is being consumed relative to how far through the reset window you
/// are. A ratio of 1 means usage and elapsed time are in lockstep; greater than 1 means you're
/// burning faster than the clock — on track to run out before the window resets. A ratio of
/// <c>r</c> implies you'd hit 100% at <c>1/r</c> of the window.
/// </summary>
public static class Pace
{
    // Below this fraction of the window (~the first 15 min of a 5-hour window) the time base is
    // floored, so a few percent of early usage doesn't read as an extreme pace.
    public const double MinTimeFraction = 0.05;

    /// <summary>
    /// Pace ratio for <paramref name="usagePercent"/> at the given window-elapsed fraction:
    /// usageFraction / max(windowFraction, <see cref="MinTimeFraction"/>). Returns 0 when usage is 0.
    /// </summary>
    public static double Ratio(double usagePercent, double windowFraction)
    {
        var usage = Math.Clamp(usagePercent, 0.0, 100.0) / 100.0;
        var time = Math.Max(windowFraction, MinTimeFraction);
        return usage / time;
    }
}
