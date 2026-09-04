namespace ClaudeMon.UI;

using System.Drawing;

/// <summary>
/// What a taskbar readout's window is actually doing right now: healthy, healthy-by-design
/// (deliberately hidden under a fullscreen app — issue #123), or one of the ways an overlay
/// can still exist while no longer being a readout. Named per failure mode so a user's log
/// after one bad reboot or sleep cycle says which one happened (issue #199).
/// </summary>
internal enum TaskbarOverlayStatus
{
    /// <summary>Visible, topmost, painted, and sitting on its taskbar.</summary>
    Healthy,

    /// <summary>Hidden on purpose because a fullscreen app covers its taskbar (#123).</summary>
    SuppressedForFullscreen,

    /// <summary>The window handle is gone, so nothing repositions or repaints any more.</summary>
    HandleLost,

    /// <summary>The 500 ms keep-alive hasn't run in far too long — the readout is frozen.</summary>
    KeepAliveStalled,

    /// <summary>
    /// There is no usable taskbar to sit on right now, so the readout has hidden itself and is
    /// waiting — an Explorer restart in progress, or a monitor mid-hotplug. A wait state, not a
    /// fault: the readout re-resolves its taskbar every keep-alive tick and reappears by itself,
    /// and if the taskbar is really gone for good the reconcile disposes the readout with it.
    /// </summary>
    TaskbarMissing,

    /// <summary>The window exists but isn't visible on screen.</summary>
    NotVisible,

    /// <summary>The window lost its topmost style, so the taskbar draws over it.</summary>
    LostTopmost,

    /// <summary>The window is nowhere near its taskbar — stale coordinates, or zero-sized.</summary>
    Misplaced,

    /// <summary>The window has never pushed any content, so it's an invisible hole.</summary>
    NotPainted,
}

/// <summary>
/// The live facts about one readout window, gathered by
/// <see cref="TaskbarOverlayWindow.CheckHealth"/> so the rules below stay pure and testable.
/// Bounds are physical screen pixels, as every taskbar coordinate in this app is.
/// </summary>
/// <param name="OwnTaskbarResolved">
/// Whether the readout itself found its taskbar on its last keep-alive tick. Separate from
/// <paramref name="TaskbarFound"/> (the manager's view, moments later): when they disagree the
/// readout has hidden itself on purpose, and reading that as an unexplained invisible window
/// would rebuild a readout that is behaving correctly.
/// </param>
/// <param name="WindowBounds">Null when the window's rectangle couldn't be read at all.</param>
internal readonly record struct TaskbarOverlayFacts(
    bool HandleCreated,
    long MsSinceKeepAlive,
    bool OwnTaskbarResolved,
    bool SuppressedForFullscreen,
    bool TaskbarFound,
    Rectangle TaskbarBounds,
    bool WindowVisible,
    bool WindowTopmost,
    Rectangle? WindowBounds,
    bool HasPainted);

/// <summary>
/// Classifies a readout's health from the facts above, and says which verdicts warrant
/// rebuilding the window.
/// </summary>
/// <remarks>
/// This exists because the self-healing added for #62 only ever noticed an <i>empty</i>
/// overlay set: a readout that existed but was broken (dead taskbar, lost z-order, stale
/// position, frozen keep-alive, never painted) was never healed, and the Settings toggle —
/// which tears everything down and rebuilds it — was the only cure (#199).
/// </remarks>
internal static class TaskbarOverlayHealth
{
    /// <summary>
    /// How long without a keep-alive tick counts as a frozen readout. The loop runs every
    /// 500 ms, so this is sixteen missed ticks. It must stay at or above
    /// <see cref="TaskbarHealPolicy.CheckIntervalMs"/> × <see cref="TaskbarHealPolicy.SystemGapFactor"/>
    /// (asserted by a test): below that there is a band where the health check itself was also
    /// starved — both timers share one message loop — and the honest reading of that is "the
    /// process wasn't running", which the gap detector already handles by re-baselining. Calling
    /// it a stalled readout there would rebuild a Form whose new timer runs on the same stuck
    /// thread. Above the band, a dead keep-alive alongside a live health check is a real fault.
    /// </summary>
    internal const long KeepAliveStallMs = 8000;

    /// <summary>
    /// The first thing wrong with this readout, or <see cref="TaskbarOverlayStatus.Healthy"/>.
    /// Order matters: the cheap "is this window even alive" checks come first, because a dead
    /// or frozen readout's other facts are stale and would otherwise be read as gospel — most
    /// importantly <see cref="TaskbarOverlayFacts.SuppressedForFullscreen"/>, which is only
    /// meaningful if the keep-alive that sets it is still running.
    /// </summary>
    internal static TaskbarOverlayStatus Evaluate(in TaskbarOverlayFacts facts)
    {
        if (!facts.HandleCreated)
            return TaskbarOverlayStatus.HandleLost;

        if (facts.MsSinceKeepAlive > KeepAliveStallMs)
            return TaskbarOverlayStatus.KeepAliveStalled;

        // A degenerate taskbar rect counts as missing: there is nothing to sit on, so the
        // placement check below would have no meaningful answer. The readout's own view comes
        // next and matters just as much — if IT couldn't find the taskbar on its last tick then
        // it hid itself deliberately, and every check below would misread that as a fault.
        if (!facts.TaskbarFound || facts.TaskbarBounds.Width <= 0 || facts.TaskbarBounds.Height <= 0)
            return TaskbarOverlayStatus.TaskbarMissing;

        if (!facts.OwnTaskbarResolved)
            return TaskbarOverlayStatus.TaskbarMissing;

        if (facts.SuppressedForFullscreen)
            return TaskbarOverlayStatus.SuppressedForFullscreen;

        if (!facts.WindowVisible)
            return TaskbarOverlayStatus.NotVisible;

        if (!facts.WindowTopmost)
            return TaskbarOverlayStatus.LostTopmost;

        // Intersection, not containment: the user's horizontal nudge can legitimately push a
        // readout a little past its taskbar's edge, and a rebuild wouldn't change that. This
        // catches the real failure — a readout left at coordinates from a display layout that
        // no longer exists. A zero-sized window intersects nothing, so it lands here too.
        // Unreadable bounds are not evidence of anything, so they skip the check entirely.
        if (facts.WindowBounds is { } bounds && !bounds.IntersectsWith(facts.TaskbarBounds))
            return TaskbarOverlayStatus.Misplaced;

        if (!facts.HasPainted)
            return TaskbarOverlayStatus.NotPainted;

        return TaskbarOverlayStatus.Healthy;
    }

    /// <summary>
    /// Whether this verdict is worth tearing the window down for: it means the window is
    /// present but not doing its job, and a rebuild is exactly the Settings-toggle cure applied
    /// automatically. Three verdicts are excluded, all because a rebuild would be wrong rather
    /// than merely wasteful:
    /// <list type="bullet">
    /// <item><see cref="TaskbarOverlayStatus.Healthy"/> — nothing to do.</item>
    /// <item><see cref="TaskbarOverlayStatus.SuppressedForFullscreen"/> — rebuilding would fight
    /// #123 for as long as a game is open.</item>
    /// <item><see cref="TaskbarOverlayStatus.TaskbarMissing"/> — a wait state. A new window would
    /// have no more taskbar to sit on than this one, and the readout heals itself the moment the
    /// taskbar returns; if it never returns, the reconcile disposes the readout with its monitor.</item>
    /// </list>
    /// </summary>
    internal static bool NeedsRebuild(TaskbarOverlayStatus status) =>
        status is not (TaskbarOverlayStatus.Healthy
            or TaskbarOverlayStatus.SuppressedForFullscreen
            or TaskbarOverlayStatus.TaskbarMissing);
}
