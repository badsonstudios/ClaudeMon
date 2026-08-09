namespace ClaudeMon.UI;

/// <summary>
/// How the Settings window fits itself onto the monitor (#139). <see cref="SettingsForm"/> sizes
/// itself purely from its content — the tab you are on decides the height — and it grows
/// <em>downwards</em> from wherever it was centered for the tab it opened on. Both halves of that
/// run off the bottom of the screen on a short display or at a large scale factor, leaving the
/// OK/Cancel row somewhere below the taskbar with no way to reach it. So there are two decisions
/// here: how tall the window is allowed to be (and whether the content therefore has to scroll),
/// and where its top edge has to move to so that height lands on screen. Pure (no WinForms), so
/// the cases that matter are unit-testable, mirroring <see cref="UsageBreakdownLayout"/> and
/// <see cref="TabStripLayout"/>; the form only feeds it measurements it has taken.
/// Every value is physical pixels.
/// </summary>
internal static class SettingsFormLayout
{
    /// <summary>
    /// The client height the window should take, and whether its content must scroll to stay
    /// reachable. Returns <paramref name="contentHeight"/> unchanged whenever the whole window
    /// fits the monitor — the common case, where nothing about the dialog changes.
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
    /// height: a two-row tab legitimately produces a short window, and forcing those up to a
    /// minimum would be a visible change on every screen.
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
    /// The top edge that keeps a window of <paramref name="outerHeight"/> inside the working area
    /// vertically: unchanged when it already fits, slid up when it would hang off the bottom, and
    /// pinned to the top of the area when it is taller than the area (so the title bar — and the
    /// close box — stay reachable, matching <see cref="DialogPlacement.CenterIn"/>'s guarantee).
    ///
    /// This is the other half of the fix, and on most screens the half that actually fires:
    /// <see cref="ClampClientHeight"/> alone would still let a window that opened on a two-row tab
    /// grow past the bottom of the screen when you switch to a twelve-row one, because
    /// <c>ClientSize</c> grows downwards from a fixed top. Deliberately a shift, never a re-centre:
    /// re-centring a dialog the user may be dragging or reading would yank it out from under them
    /// (the same reason <see cref="DialogPlacement.CenterOnPrimary"/> never re-runs on a DPI change).
    /// </summary>
    public static int ClampTop(int top, int outerHeight, int areaTop, int areaBottom) =>
        Math.Max(areaTop, Math.Min(top, areaBottom - Math.Max(0, outerHeight)));
}
