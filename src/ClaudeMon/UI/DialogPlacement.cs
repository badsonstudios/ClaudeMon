namespace ClaudeMon.UI;

using System.Drawing;

/// <summary>
/// Deterministic dialog placement. WinForms' <c>FormStartPosition.CenterScreen</c> centers an
/// ownerless form on whichever monitor holds the mouse cursor — so a dialog popped by a
/// background timer (the update prompt) lands on whatever side monitor the cursor was idling
/// on (issue #88). The app's dialogs use <c>Manual</c> and pick their monitor deliberately
/// instead: most center on the primary monitor, where the tray lives, while the update dialogs
/// follow the foreground window so they open where the user is actually working (issue #108).
/// Nothing here ever reads the cursor position.
/// </summary>
internal static class DialogPlacement
{
    /// <summary>
    /// The maximum number of moves <see cref="PlaceStable"/> will make. Three is ample: in
    /// practice a placement settles after at most one DPI-driven relayout.
    /// </summary>
    private const int DefaultMaxMoves = 3;

    /// <summary>
    /// The top-left that centers a form of <paramref name="size"/> in <paramref name="area"/>,
    /// clamped so the top-left never leaves the area — when the form is larger than the
    /// working area the title bar (and thus the close box) stays reachable.
    /// Pure, for unit tests.
    /// </summary>
    public static Point CenterIn(Rectangle area, Size size)
    {
        var x = area.Left + (area.Width - size.Width) / 2;
        var y = area.Top + (area.Height - size.Height) / 2;
        return new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
    }

    /// <summary>
    /// Centers in <paramref name="area"/>, repeating until the form is observed to actually be
    /// centered there (at most <paramref name="maxMoves"/> moves).
    ///
    /// Moving a window onto a monitor with a different scale factor makes Windows send
    /// <c>WM_DPICHANGED</c>, which the dialogs answer by re-laying out at the new DPI — so the
    /// size changes *after* the position was computed from the old size, leaving the dialog
    /// off-center or straddling the monitor edge. That is the same failure #104 fixed for the
    /// flyout, and the fix is the same: recompute with the final size.
    ///
    /// The check is on the whole <em>bounds</em>, not just the size, because WinForms' own
    /// <c>WM_DPICHANGED</c> handling honours the suggested rectangle and can reposition the
    /// window without resizing it. Comparing sizes alone would return a point the form is not
    /// actually at.
    ///
    /// Guarantees on every exit path:
    /// <list type="bullet">
    /// <item>the returned point is a <see cref="CenterIn"/> result, so it is clamped inside
    /// <paramref name="area"/> and the title bar stays reachable — even if the size never
    /// settles;</item>
    /// <item>the form was moved there. It is centered <em>for the size observed at that
    /// moment</em>; a resize provoked by the final move is not observed, so in the pathological
    /// non-converging case the dialog can still end up off-center (but never off-monitor).</item>
    /// </list>
    ///
    /// Pure apart from the callbacks, so the DPI-relayout behaviour is unit-testable.
    /// </summary>
    /// <param name="area">The target monitor's working area.</param>
    /// <param name="measureBounds">Reads the form's current outer bounds.</param>
    /// <param name="move">Moves the form's top-left. May change what <paramref name="measureBounds"/> returns.</param>
    /// <param name="maxMoves">Move budget; values below 1 are treated as 1.</param>
    internal static Point PlaceStable(
        Rectangle area, Func<Rectangle> measureBounds, Action<Point> move, int maxMoves = DefaultMaxMoves)
    {
        if (maxMoves < 1)
            maxMoves = 1;

        var target = CenterIn(area, measureBounds().Size);
        move(target);

        for (var moves = 1; moves < maxMoves; moves++)
        {
            var bounds = measureBounds();
            var wanted = CenterIn(area, bounds.Size);

            // Right size and actually sitting there: settled.
            if (bounds.Location == wanted)
                return wanted;

            // The move triggered a DPI relayout. Re-center for the new size — this also
            // overwrites the position WinForms chose from the WM_DPICHANGED suggested
            // rectangle, which is intended: only this method decides where the dialog lands.
            target = wanted;
            move(target);
        }

        return target;
    }

    /// <summary>Centers <paramref name="form"/> in <paramref name="area"/>, DPI-stable.</summary>
    internal static void CenterOn(Form form, Rectangle area)
        => PlaceStable(area, () => form.Bounds, p => form.Location = p);

    /// <summary>
    /// Centers <paramref name="form"/> on the primary monitor's working area. Call from
    /// <c>OnLoad</c> after the DPI-correct relayout so the measured size is final.
    /// Deliberately never re-centers on DpiChanged: that fires when the user drags the dialog
    /// to another monitor, and re-centering would yank it out of their hand.
    /// </summary>
    public static void CenterOnPrimary(Form form) => CenterOn(form, PrimaryWorkingArea());

    /// <summary>
    /// The working area of the monitor holding the foreground window — the one the user is
    /// actually working on — or the primary monitor's when there isn't a usable one. Added for
    /// #108: an update dialog that opens on the primary while the user works on a side monitor
    /// reads as "lost" just as surely as one buried behind another window.
    ///
    /// Resolve this once per flow when several dialogs follow one another, so a later dialog
    /// can't land on a different monitor than the one the user just clicked on (see
    /// <c>TrayApplication.ShowUpdateDialog</c>).
    /// </summary>
    public static Rectangle ForegroundWorkingArea()
        => ForegroundMonitor.TryWorkingArea() ?? PrimaryWorkingArea();

    /// <summary>
    /// The area a dialog should center on: <paramref name="requested"/> if the caller supplied
    /// one and it still describes a live monitor, otherwise the foreground window's monitor,
    /// otherwise the primary. The staleness check matters because a supplied area can be
    /// minutes old — the update prompt may sit open (TopMost, ignored) long before the download
    /// dialog reuses its area, and a monitor can be unplugged in between.
    ///
    /// Called once from each dialog's <c>OnLoad</c>, before the window is visible; dragging the
    /// dialog across monitors afterwards never re-centers it.
    /// </summary>
    public static Rectangle ResolveArea(Rectangle? requested)
        => requested is { } area && ForegroundMonitor.IsUsableArea(area)
            ? area
            : ForegroundWorkingArea();

    /// <summary>The primary monitor's working area, with the same fallback WinForms needs.</summary>
    internal static Rectangle PrimaryWorkingArea()
    {
        try
        {
            var screens = Screen.AllScreens;
            var primary = Screen.PrimaryScreen ?? (screens.Length > 0 ? screens[0] : null);
            if (primary is not null)
                return primary.WorkingArea;
        }
        catch
        {
            // Fall through to the hard-coded rectangle below.
        }

        // No monitor at all (or the enumeration failed). Nothing sensible is possible here;
        // return a small rectangle at the origin so placement stays total rather than throwing
        // out of a cosmetic code path.
        return new Rectangle(0, 0, 640, 480);
    }
}
