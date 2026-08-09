namespace ClaudeMon.Models;

/// <summary>
/// A metric the taskbar readout can lead with. The declaration order is also the
/// click-to-cycle ring order (issue #71): session → weekly → time-to-limit → reset
/// countdown → back to session. A readout showing several of them at once is a stop on that
/// ring too, but isn't a metric — see <c>UI.TaskbarMetricCycle.HomeFor</c> (issue #156).
/// </summary>
public enum TaskbarMetric
{
    /// <summary>Session (5-hour) utilization percentage.</summary>
    Session,

    /// <summary>Weekly (7-day) utilization percentage.</summary>
    Weekly,

    /// <summary>Projected time until the 5-hour window reaches 100% at the current burn rate.</summary>
    TimeToLimit,

    /// <summary>Countdown to the 5-hour window's reset.</summary>
    TimeToReset,
}
