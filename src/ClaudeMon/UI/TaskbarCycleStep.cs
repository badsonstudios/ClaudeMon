namespace ClaudeMon.UI;

using ClaudeMon.Models;

/// <summary>
/// One click of the click-to-cycle gesture, as computed by <see cref="TaskbarMetricCycle.Step"/>:
/// the readout to show, the composition to remember as home, and the name to flash. Bundled so
/// the caller only has to persist and display what it is handed — the decisions all stay in the
/// pure state machine.
/// </summary>
/// <param name="Metrics">The display toggles the readout lands on.</param>
/// <param name="Home">
/// The composition the ring wraps back to, or <c>null</c> when there is nothing worth
/// remembering. Carried through unchanged when this click neither anchors nor restores it, so
/// persisting it is always the right thing to do.
/// </param>
/// <param name="Label">The short name to flash on the readout (see <see cref="TaskbarMetricCycle.Label"/>).</param>
internal readonly record struct TaskbarCycleStep(
    TaskbarMetricSelection Metrics,
    TaskbarMetricSelection? Home,
    string Label);
