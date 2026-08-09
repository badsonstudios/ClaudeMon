namespace ClaudeMon.UI;

using ClaudeMon.Models;

/// <summary>
/// The click-to-cycle state machine behind middle-click (and Ctrl+left-click) on the taskbar
/// readout (issue #71). Pure, so the ring order and the collapse rule are unit-testable
/// without a window — the overlay itself only reports the gesture.
/// </summary>
/// <remarks>
/// The gesture is a shortcut over the same display toggles Settings edits, not a parallel
/// setting: cycling writes the toggles, so what you see is always what Settings shows.
/// Two rules make a four-way ring work against toggles that can express any combination:
/// <list type="bullet">
/// <item><description>
/// <b>Collapse, then advance.</b> A readout showing several metrics (or none) has no position
/// in the ring, so the first click collapses it to its first ring metric — the leftmost thing
/// already on screen — rather than skipping past it. Every click after that advances by one.
/// </description></item>
/// <item><description>
/// <b>Only what the style can draw.</b> The Bar style draws no text, so its ring is the two
/// metrics it can actually render as a bar; the time metrics would cycle to a readout that
/// looks identical. A selection outside the current style's ring (set in Settings, or carried
/// over from the Numbers style) restarts the ring rather than sticking — and is left switched
/// on, so switching back to Numbers restores the composition the bar was never showing.
/// </description></item>
/// </list>
/// </remarks>
internal static class TaskbarMetricCycle
{
    private static readonly TaskbarMetric[] NumbersRing =
    {
        TaskbarMetric.Session,
        TaskbarMetric.Weekly,
        TaskbarMetric.TimeToLimit,
        TaskbarMetric.TimeToReset,
    };

    // The bar has no number row: the time metrics have nothing to draw there, and the bar's
    // own time tick already encodes the reset window. Cycling through them would look like
    // the gesture had stopped working, so the bar's ring is the two bars it can draw.
    private static readonly TaskbarMetric[] BarRing =
    {
        TaskbarMetric.Session,
        TaskbarMetric.Weekly,
    };

    /// <summary>The metrics a given readout style cycles through, in order.</summary>
    public static IReadOnlyList<TaskbarMetric> Ring(TaskbarStyle style) =>
        style == TaskbarStyle.Bar ? BarRing : NumbersRing;

    /// <summary>
    /// The metric a readout is currently leading with, or <c>null</c> when it shows several (or
    /// none at all) — the states that have no position in the ring. Judged within
    /// <paramref name="style"/>'s ring: under the Bar style, metrics it can't draw aren't on
    /// screen, so they can't be what the reader is looking at either.
    /// </summary>
    public static TaskbarMetric? Current(TaskbarMetricSelection selection, TaskbarStyle style)
    {
        TaskbarMetric? found = null;
        foreach (var metric in Ring(style))
        {
            if (!selection.Shows(metric))
                continue;
            if (found is not null)
                return null;
            found = metric;
        }

        return found;
    }

    /// <summary>
    /// The metric one cycle step on from <paramref name="current"/> under
    /// <paramref name="style"/> — see the collapse and style rules on the class.
    /// </summary>
    public static TaskbarMetric NextMetric(TaskbarMetricSelection current, TaskbarStyle style)
    {
        var ring = Ring(style);

        if (Current(current, style) is { } single)
        {
            // Current only ever names a ring member, so the index is always found; a
            // hypothetical -1 would wrap to ring[0], which is the right fallback anyway.
            return ring[(IndexIn(ring, single) + 1) % ring.Count];
        }

        // A composition (or an empty selection): collapse onto the first ring metric it
        // already shows, so the first click focuses what you were looking at rather than
        // jumping past it. Nothing on screen at all starts the ring from the beginning.
        foreach (var metric in ring)
        {
            if (current.Shows(metric))
                return metric;
        }

        return ring[0];
    }

    /// <summary>
    /// <paramref name="current"/> with <paramref name="metric"/> as the only one of this
    /// style's ring metrics shown. Metrics outside the ring keep their setting: the Bar style
    /// hides the two time toggles' Settings rows entirely, so a gesture that can't show them
    /// must not silently switch them off behind the user's back either.
    /// </summary>
    public static TaskbarMetricSelection Select(
        TaskbarMetricSelection current, TaskbarMetric metric, TaskbarStyle style)
    {
        var result = current;
        foreach (var ringMetric in Ring(style))
            result = result.With(ringMetric, ringMetric == metric);

        return result;
    }

    /// <summary>The selection one cycle step on from <paramref name="current"/>.</summary>
    public static TaskbarMetricSelection Next(TaskbarMetricSelection current, TaskbarStyle style) =>
        Select(current, NextMetric(current, style), style);

    /// <summary>
    /// The short name flashed on the readout after a cycle so the gesture (and what it just
    /// selected) is discoverable. Lower-case to match the readout's other words
    /// (<c>idle</c>), and short enough not to stretch the overlay noticeably.
    /// </summary>
    public static string Label(TaskbarMetric metric) => metric switch
    {
        TaskbarMetric.Session => "session",
        TaskbarMetric.Weekly => "weekly",
        TaskbarMetric.TimeToLimit => "to limit",
        TaskbarMetric.TimeToReset => "resets",
        _ => "session",
    };

    // IReadOnlyList has no IndexOf, and List/Array.IndexOf would mean handing out (or
    // allocating) a mutable copy of the shared ring.
    private static int IndexIn(IReadOnlyList<TaskbarMetric> ring, TaskbarMetric metric)
    {
        for (var i = 0; i < ring.Count; i++)
        {
            if (ring[i] == metric)
                return i;
        }

        return -1;
    }
}
