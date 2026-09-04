namespace ClaudeMon.UI;

using System.Drawing;

/// <summary>
/// Deterministic dialog placement, and the fit-to-monitor math that goes with it. WinForms'
/// <c>FormStartPosition.CenterScreen</c> centers an ownerless form on whichever monitor holds the
/// mouse cursor — so a dialog popped by a background timer (the update prompt) lands on whatever
/// side monitor the cursor was idling on (issue #88). The app's dialogs use <c>Manual</c> and pick
/// their monitor deliberately instead: the small, dismissable dialogs (About, Settings) center on
/// the primary monitor, where the tray lives, while the windows the user goes on to work in — the
/// update dialogs (issue #108) and the Usage &amp; costs window (issue #116) — follow the
/// foreground window so they open on the screen the user is actually looking at. Nothing here
/// ever reads the cursor position.
///
/// The size half of the same question lives here too (<see cref="ClampClientHeight"/> /
/// <see cref="ClampTop"/>, moved out of a <c>SettingsFormLayout</c> helper in #153, joined by
/// <see cref="ClampMinimumSize"/> in #172): every dialog in the app sizes itself from its content,
/// so on a short display or at a large scale factor any of them can want more room than the
/// monitor has — to open at, and, where the window is resizable, to shrink to. Deciding how big a
/// window may be and deciding where it lands are one question — <see cref="CenterIn"/> already
/// carries the same "keep the title bar reachable" clamp — so one type owns both rather than each
/// form growing its own copy.
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
    /// The client height a self-sizing window should take, and whether its content must scroll to
    /// stay reachable. Returns <paramref name="contentHeight"/> unchanged whenever the whole window
    /// fits the monitor — the common case, where nothing about the dialog changes.
    /// Pure, for unit tests. Every value is physical pixels.
    /// </summary>
    /// <param name="contentHeight">The client height the laid-out content wants.</param>
    /// <param name="workingAreaHeight">
    /// The height of the working area of the monitor the window is on — the desktop minus the
    /// taskbar, so a maximal window still leaves the taskbar clickable.
    /// </param>
    /// <param name="nonClientHeight">
    /// The window's chrome: title bar plus borders. Subtracted because the working area limits the
    /// <em>outer</em> window, while the size the form controls is the client area.
    /// </param>
    /// <param name="minClientHeight">
    /// A floor on what is left for the client area. It bites whenever the working area is smaller
    /// than the chrome plus this floor — i.e. a bogus or unavailable monitor, where the honest
    /// answer would be a zero-height window — and there it deliberately returns a window
    /// <em>taller</em> than the working area, on the grounds that a usable window hanging off a
    /// nonsense monitor beats a correct one nobody can read. It is not a floor on the returned
    /// height: a two-row tab (or a short dialog) legitimately produces a short window, and forcing
    /// those up to a minimum would be a visible change on every screen.
    /// </param>
    public static (int ClientHeight, bool Scroll) ClampClientHeight(
        int contentHeight, int workingAreaHeight, int nonClientHeight, int minClientHeight)
    {
        contentHeight = Math.Max(0, contentHeight);
        var available = Math.Max(
            Math.Max(0, minClientHeight),
            workingAreaHeight - Math.Max(0, nonClientHeight));

        var clientHeight = Math.Min(contentHeight, available);
        return (clientHeight, contentHeight > clientHeight);
    }

    /// <summary>
    /// The smallest size a resizable window may refuse to shrink past, clamped so it never exceeds
    /// the monitor's working area. Returns <paramref name="minimum"/> unchanged whenever it already
    /// fits — the common case, where nothing about the window changes.
    ///
    /// The companion to <see cref="ClampClientHeight"/>, and the one case that clamp cannot save
    /// (#172): capping the size a window <em>opens</em> at is no help if its floor is still bigger
    /// than the screen, because then the user cannot drag it down to fit either. Both dimensions are
    /// clamped — a window whose minimum width comes from its table columns can be too wide for a
    /// small panel at a large scale factor just as easily as it is too tall.
    ///
    /// Every value is physical pixels, and both are <em>outer</em> window sizes: that is what
    /// <c>Form.MinimumSize</c> means, and what the working area limits.
    /// Pure, for unit tests.
    /// </summary>
    /// <param name="minimum">The outer window size the content says the window needs.</param>
    /// <param name="workingArea">
    /// The size of the working area of the monitor the window is on. A zero or negative dimension
    /// is nothing to measure against, so it leaves that dimension of <paramref name="minimum"/>
    /// alone rather than clamping the floor to nothing — totality for a pure function, not a policy
    /// for a failed monitor lookup, which <see cref="WorkingAreaFor"/> already answers with the
    /// primary monitor's area. That is the opposite of <see cref="ClampClientHeight"/>'s response
    /// to a degenerate area, and deliberately so: that one has to choose a size, while this one is
    /// only ever *lowering* a floor, so declining to act is the status quo rather than a window
    /// nobody can read.
    /// </param>
    public static Size ClampMinimumSize(Size minimum, Size workingArea) =>
        new(ClampMinimum(minimum.Width, workingArea.Width),
            ClampMinimum(minimum.Height, workingArea.Height));

    // Negative is never a legal Form.MinimumSize (the setter throws), so the clamp is also the
    // place that guarantees one dimension of a nonsense measurement can't take a window down.
    private static int ClampMinimum(int minimum, int available)
    {
        minimum = Math.Max(0, minimum);
        return available > 0 ? Math.Min(minimum, available) : minimum;
    }

    /// <summary>
    /// The top edge that keeps a window of <paramref name="outerHeight"/> inside the working area
    /// vertically: unchanged when it already fits, slid up when it would hang off the bottom, and
    /// pinned to the top of the area when it is taller than the area (so the title bar — and the
    /// close box — stay reachable, matching <see cref="CenterIn"/>'s guarantee).
    ///
    /// This is the other half of the fit, and on most screens the half that actually fires:
    /// <see cref="ClampClientHeight"/> alone would still let a window grow past the bottom of the
    /// screen after it was placed, because <c>ClientSize</c> grows downwards from a fixed top —
    /// Settings switching from a two-row tab to a twelve-row one, or any hand-scaled dialog being
    /// dragged onto a higher-DPI monitor and re-laying out larger. Deliberately a shift, never a
    /// re-centre: re-centring a dialog the user may be dragging or reading would yank it out from
    /// under them (the same reason <see cref="CenterOnPrimary"/> never re-runs on a DPI change).
    /// Pure, for unit tests.
    /// </summary>
    public static int ClampTop(int top, int outerHeight, int areaTop, int areaBottom) =>
        Math.Max(areaTop, Math.Min(top, areaBottom - Math.Max(0, outerHeight)));

    /// <summary>
    /// The working area a form's clamps should measure against: the monitor the form is on once it
    /// has a window, the primary monitor's before that. Those agree for the first layout — the
    /// dialogs open centered on a monitor chosen in <c>OnLoad</c> and an as-yet-unplaced form sits
    /// at the origin, which is on the primary by definition — so the layout pass that runs before
    /// the window is shown already clamps against the monitor the user will see it on.
    /// </summary>
    internal static Rectangle WorkingAreaFor(Form form)
    {
        try
        {
            if (form.IsHandleCreated)
                return Screen.FromControl(form).WorkingArea;
        }
        catch
        {
            // Monitor enumeration can fail in odd session states; fall through to the primary.
        }

        return PrimaryWorkingArea();
    }

    /// <summary>
    /// Sizes <paramref name="form"/> to the content it just laid out, fitted to the monitor it is
    /// on: the height is capped by <see cref="ClampClientHeight"/>, the overflow becomes a
    /// vertical scrollbar rather than content falling off the bottom, and the window is slid back
    /// under the bottom edge by <see cref="ClampTop"/>. Every one of the app's windows sizes itself
    /// from its content, so all of them shared the same latent overflow on a short display or at a
    /// large scale factor (#139 for Settings, #153 for the rest) — this is the one place that
    /// answers it.
    ///
    /// Call it at the end of a form's relayout, having reset the scroll offset at the start of it
    /// (writing control tops while scrolled offsets them all — see <c>SettingsForm.Relayout</c>).
    /// Returns whether the content had to scroll, which callers that preserve a scroll position
    /// across a relayout need.
    /// </summary>
    /// <param name="clientWidth">The client width the form wants; never clamped (only height is).</param>
    /// <param name="contentHeight">The client height the laid-out content wants.</param>
    /// <param name="minClientHeight">
    /// The floor described on <see cref="ClampClientHeight"/> — a guard for a degenerate working
    /// area, not a minimum window size.
    /// </param>
    internal static bool FitToMonitor(Form form, int clientWidth, int contentHeight, int minClientHeight)
    {
        var area = WorkingAreaFor(form);
        var (clientHeight, scroll) = ClampClientHeight(
            contentHeight, area.Height, form.Height - form.ClientSize.Height, minClientHeight);

        // Setting a non-empty AutoScrollMinSize turns AutoScroll on by itself; the assignment after
        // it is what turns it back off once the content fits again. A zero width asks for no
        // horizontal scrolling, so the scrollbar comes out of the client width — which every caller
        // has room for in its right-hand padding.
        form.AutoScrollMinSize = scroll ? new Size(0, contentHeight) : Size.Empty;
        form.AutoScroll = scroll;
        form.ClientSize = new Size(clientWidth, clientHeight);

        // Only once the window exists — before that, Top is meaningless and the caller's OnLoad
        // placement does the opening position anyway.
        if (form.IsHandleCreated)
            form.Top = ClampTop(form.Top, form.Height, area.Top, area.Bottom);

        return scroll;
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
