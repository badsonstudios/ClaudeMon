namespace ClaudeMon.UI;

using Microsoft.Win32;

/// <summary>
/// When <see cref="TaskbarOverlayManager"/> acts on a health verdict: how many bad checks in
/// a row justify rebuilding a readout, how often the same readout may be rebuilt, what counts
/// as "the process wasn't running in between", and the settling schedule used after the system
/// changed underneath us. Pure so the timing policy is unit-testable without a desktop (#199).
/// </summary>
internal static class TaskbarHealPolicy
{
    /// <summary>How often the manager re-enumerates taskbars and re-checks readout health.</summary>
    internal const int CheckIntervalMs = 2000;

    /// <summary>
    /// Consecutive unhealthy checks before a rebuild. One bad check can be a race with
    /// Explorer mid-restack or mid-hotplug; two in a row (~2–4 s) is a broken readout.
    /// </summary>
    internal const int UnhealthyChecksBeforeRebuild = 2;

    /// <summary>
    /// Minimum gap between rebuilds of the same readout. A monitor whose readout can't be
    /// made healthy — a taskbar we simply can't sit on — must not turn into a window-churn
    /// loop that recreates a Form every few seconds for the rest of the session.
    /// </summary>
    internal const int RebuildCooldownMs = 30_000;

    /// <summary>
    /// How many check intervals must elapse between two checks before we conclude the process
    /// wasn't running in between rather than merely being late.
    /// </summary>
    internal const int SystemGapFactor = 3;

    /// <summary>
    /// Minimum gap between fault log lines for the same readout. A readout that can't be healed
    /// is checked every 2 seconds but may only be rebuilt every 30, so without this a single
    /// stuck monitor writes hundreds of WARNs an hour — and a log nobody can read is not a
    /// diagnostic. Comfortably finer-grained than the rebuild cooldown, so every rebuild
    /// attempt still has a logged fault in front of it.
    /// </summary>
    internal const int FaultLogIntervalMs = 10_000;

    /// <summary>
    /// Intervals between successive settle re-checks after the system changed under us, so the
    /// checks land ~0.5 s, 1.5 s, 3 s, 6 s and 10 s after the event. Resume and unlock are not
    /// instants: monitors, DPI, and the taskbar itself keep churning for several seconds
    /// afterwards, so a single reconcile at the event can be both too early to see the final
    /// layout and too late to matter. The schedule is front-loaded (heal fast when it's easy)
    /// and finishes inside the ticket's "within a few seconds".
    /// </summary>
    private static readonly int[] SettleIntervals = [500, 1000, 1500, 3000, 4000];

    /// <summary>How many settle re-checks follow a heal.</summary>
    internal static int SettleAttempts => SettleIntervals.Length;

    /// <summary>
    /// How long to wait before settle re-check number <paramref name="attempt"/> (0-based),
    /// or null once the settling window is over and the steady 2-second health check takes back over.
    /// </summary>
    internal static int? SettleIntervalMs(int attempt) =>
        attempt >= 0 && attempt < SettleIntervals.Length ? SettleIntervals[attempt] : null;

    /// <summary>
    /// Whether a readout in this state, failing for this long, may be rebuilt now.
    /// <paramref name="msSinceLastRebuild"/> is null when it has never been rebuilt.
    /// </summary>
    internal static bool ShouldRebuild(
        TaskbarOverlayStatus status, int consecutiveUnhealthyChecks, long? msSinceLastRebuild) =>
        TaskbarOverlayHealth.NeedsRebuild(status)
        && consecutiveUnhealthyChecks >= UnhealthyChecksBeforeRebuild
        && (msSinceLastRebuild is not { } since || since >= RebuildCooldownMs);

    /// <summary>
    /// Whether a readout's continuing fault may be logged again.
    /// <paramref name="msSinceLastFaultLog"/> is null when this is a fresh episode.
    /// </summary>
    internal static bool ShouldLogFault(long? msSinceLastFaultLog) =>
        msSinceLastFaultLog is not { } since || since >= FaultLogIntervalMs;

    /// <summary>
    /// Whether the gap between two health checks is too big to be scheduling jitter — the
    /// machine slept, hibernated, or the process was frozen. This is deliberately independent
    /// of <see cref="SystemEvents.PowerModeChanged"/>: the tick source keeps counting through
    /// sleep while our timers don't, so the gap is observable even on machines where the power
    /// event never arrives (modern standby) or arrives after the readouts are already broken.
    /// </summary>
    internal static bool IsSystemGap(long msSinceLastCheck) =>
        msSinceLastCheck >= (long)CheckIntervalMs * SystemGapFactor;

    /// <summary>
    /// Session-switch reasons that mean "our desktop just came back" — the moments when the
    /// shell may have rebuilt the taskbar, or the display adapter re-initialised, while our
    /// overlay windows carried on existing. Lock and disconnect are not included: nothing to
    /// heal while the desktop is away, and the matching reconnect covers the return.
    /// </summary>
    internal static bool IsResumeLike(SessionSwitchReason reason) =>
        reason is SessionSwitchReason.SessionUnlock
            or SessionSwitchReason.ConsoleConnect
            or SessionSwitchReason.RemoteConnect
            or SessionSwitchReason.SessionLogon;
}
