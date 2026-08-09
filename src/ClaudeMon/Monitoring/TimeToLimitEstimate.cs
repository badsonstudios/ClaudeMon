namespace ClaudeMon.Monitoring;

/// <summary>
/// Why a time-to-limit projection does or doesn't exist. Before issue #158 every non-estimate
/// collapsed into a single <c>null</c>, so the readout's "—" carried three meanings — "you're
/// safe this window", "not enough data", and "the estimate would be noise" — and the good-news
/// case read as a broken feature. The kind keeps those apart all the way to the formatter.
/// </summary>
public enum TimeToLimitKind
{
    /// <summary>
    /// No meaningful estimate: fewer than three samples, a flat or declining trend, no time
    /// base, or a projection so distant it carries no information. Displays as "—".
    /// </summary>
    NoEstimate,

    /// <summary>
    /// The projection lands after the window resets (or the window is resetting right now),
    /// so the cap won't be reached this window. The good-news case — displays as "safe".
    /// </summary>
    Safe,

    /// <summary>Usage is already at 100%. Displays as "at limit".</summary>
    AtLimit,

    /// <summary>A meaningful remaining span; <see cref="TimeToLimitEstimate.Eta"/> is set.</summary>
    Projection,
}

/// <summary>
/// The typed result of <see cref="BurnRate.EstimateTimeToLimit"/>: what kind of answer this is,
/// plus the remaining span when the kind is <see cref="TimeToLimitKind.Projection"/>. A
/// <c>default</c> instance is honestly "no estimate" (<see cref="TimeToLimitKind.NoEstimate"/>
/// is the zero value), so uninitialised readings render as "—" rather than something misleading.
/// Construct via the static instances and the <see cref="Projection(TimeSpan)"/> factory — the
/// positional constructor can't stop a kind/eta mismatch (the formatters render kind-first and
/// tolerate one, but don't create one).
/// </summary>
public readonly record struct TimeToLimitEstimate(TimeToLimitKind Kind, TimeSpan? Eta = null)
{
    public static readonly TimeToLimitEstimate NoEstimate = new(TimeToLimitKind.NoEstimate);

    public static readonly TimeToLimitEstimate Safe = new(TimeToLimitKind.Safe);

    public static readonly TimeToLimitEstimate AtLimit = new(TimeToLimitKind.AtLimit, TimeSpan.Zero);

    public static TimeToLimitEstimate Projection(TimeSpan eta) =>
        new(TimeToLimitKind.Projection, eta);
}
