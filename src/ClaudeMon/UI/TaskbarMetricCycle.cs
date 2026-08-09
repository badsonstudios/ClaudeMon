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
/// Three rules make a small ring work against toggles that can express any combination:
/// <list type="bullet">
/// <item><description>
/// <b>Collapse, then advance.</b> A readout showing several metrics (or none) has no position
/// in the ring, so the first click collapses it to its first ring metric — the leftmost thing
/// already on screen — rather than skipping past it. Every click after that advances by one.
/// </description></item>
/// <item><description>
/// <b>Your composition is home.</b> Collapsing a composition would otherwise destroy it: the
/// toggles are written and saved on the spot, and no amount of further clicking gets a
/// multi-element readout back (issue #156 — reported the first time it met real use). So the
/// composition you cycled away from is remembered, and the ring gains a stop for it, after the
/// metric that would have taken you back to where the run started:
/// <c>[your composition] → session → weekly → to limit → resets → [your composition]</c> for a
/// composition led by session. Cycling is then a temporary focus, not an edit. A readout the
/// ring can already reach is not worth a second identical-looking stop, so a single-metric
/// readout keeps the plain four-stop ring — see <see cref="HomeFor"/>.
/// </description></item>
/// <item><description>
/// <b>Only what the style can draw.</b> The Bar style draws no text, so its ring is the two
/// metrics it can actually render as a bar; the time metrics would cycle to a readout that
/// looks identical. A selection outside the current style's ring (set in Settings, or carried
/// over from the Numbers style) restarts the ring rather than sticking — and is left switched
/// on, so switching back to Numbers restores the composition the bar was never showing. The
/// same reasoning governs home: a remembered composition the current style can only draw as one
/// metric is kept, but doesn't earn a stop until the style that can draw it comes back.
/// </description></item>
/// </list>
/// </remarks>
internal static class TaskbarMetricCycle
{
    /// <summary>
    /// The name flashed when a wrap restores your own composition — the usual word for an
    /// arrangement you made yourself, and not readable as one of the metric names.
    /// </summary>
    public const string HomeLabel = "custom";

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
        if (Current(current, style) is { } single)
            return Advance(single, style);

        // A composition (or an empty selection): collapse onto the first ring metric it
        // already shows, so the first click focuses what you were looking at rather than
        // jumping past it. Nothing on screen at all starts the ring from the beginning.
        return FirstShown(current, style) ?? Ring(style)[0];
    }

    /// <summary>
    /// The composition <paramref name="selection"/> would be remembered as if you cycled away
    /// from it now, or <c>null</c> when it isn't worth remembering: two or more of this style's
    /// ring metrics at once is a readout the ring has no stop for, while one metric already
    /// <em>is</em> a stop and none draws as the style's own fallback (session), which is a stop
    /// too. Giving either of those a home stop would put two stops with identical toggles on the
    /// ring and make the gesture look stuck. (Only identical <em>toggles</em> — an element with
    /// no data yet draws as nothing, so two stops can still look alike before the first poll;
    /// the name flash is what tells them apart, and deciding the ring from live data would make
    /// it flicker.)
    /// </summary>
    public static TaskbarMetricSelection? HomeFor(TaskbarMetricSelection selection, TaskbarStyle style)
    {
        var shown = 0;
        foreach (var metric in Ring(style))
        {
            if (selection.Shows(metric))
                shown++;
        }

        return shown > 1 ? selection : null;
    }

    /// <summary>
    /// One click of the gesture: the readout it lands on, the composition to remember as home,
    /// and the name to flash. The whole state machine — see the rules on the class.
    /// </summary>
    /// <param name="current">What the readout is showing now.</param>
    /// <param name="home">The composition remembered by an earlier click, if any.</param>
    /// <param name="style">The readout style, which decides the ring.</param>
    public static TaskbarCycleStep Step(
        TaskbarMetricSelection current, TaskbarMetricSelection? home, TaskbarStyle style)
    {
        // Cycling away from a composition is what remembers it, so the click that collapses the
        // readout is also the click that records what to come back to — there is no window in
        // which the composition is gone but unremembered. It re-anchors on every trip through
        // home, and on anything else multi-element that got into the toggles, so what you are
        // actually looking at always wins over a stale remembered value. (FirstShown can't come
        // back empty here — two ring metrics are showing — but taking the value through the
        // pattern beats a null-forgiving operator.)
        if (HomeFor(current, style) is { } anchor && FirstShown(anchor, style) is { } begin)
            return Focus(current, begin, style, anchor);

        // A full lap. The stop after the last ring metric is the composition the run began from,
        // put back rather than left destroyed — see Restore for the one thing it doesn't touch.
        // "Last" is relative to where the run started (home's leftmost metric), so every ring
        // metric is still reachable even when the composition doesn't lead with session.
        if (home is { } remembered
            && HomeFor(remembered, style) is not null
            && FirstShown(remembered, style) is { } start
            && Current(current, style) is { } single
            && Advance(single, style) == start)
        {
            var restored = Restore(current, remembered, style);
            return new TaskbarCycleStep(restored, restored, HomeLabel);
        }

        // Plain ring step. Home rides along untouched: a composition this style can't draw as
        // more than one metric (a Numbers composition while the Bar style is on) has no stop
        // here, but must survive to have one again when the style comes back.
        return Focus(current, NextMetric(current, style), style, home);
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

    /// <summary>
    /// <paramref name="current"/> with this style's ring metrics put back to what
    /// <paramref name="home"/> had. Metrics outside the ring keep their current setting for the
    /// same reason <see cref="Select"/> leaves them alone: cycling never touched them, so the
    /// live values are the user's and the remembered ones are only a stale copy of them.
    /// </summary>
    private static TaskbarMetricSelection Restore(
        TaskbarMetricSelection current, TaskbarMetricSelection home, TaskbarStyle style)
    {
        var result = current;
        foreach (var ringMetric in Ring(style))
            result = result.With(ringMetric, home.Shows(ringMetric));

        return result;
    }

    private static TaskbarCycleStep Focus(
        TaskbarMetricSelection current,
        TaskbarMetric metric,
        TaskbarStyle style,
        TaskbarMetricSelection? home) =>
        new(Select(current, metric, style), home, Label(metric));

    /// <summary>The ring metric one place on from <paramref name="metric"/>, wrapping.</summary>
    private static TaskbarMetric Advance(TaskbarMetric metric, TaskbarStyle style)
    {
        // Callers only ever pass a ring member, so the index is always found; a hypothetical -1
        // would wrap to ring[0], which is the right fallback anyway.
        var ring = Ring(style);
        return ring[(IndexIn(ring, metric) + 1) % ring.Count];
    }

    /// <summary>
    /// The leftmost of this style's ring metrics that <paramref name="selection"/> shows, or
    /// <c>null</c> when it shows none of them.
    /// </summary>
    private static TaskbarMetric? FirstShown(TaskbarMetricSelection selection, TaskbarStyle style)
    {
        foreach (var metric in Ring(style))
        {
            if (selection.Shows(metric))
                return metric;
        }

        return null;
    }

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
