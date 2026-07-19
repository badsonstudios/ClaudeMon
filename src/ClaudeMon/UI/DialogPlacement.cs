namespace ClaudeMon.UI;

using System.Drawing;

/// <summary>
/// Deterministic dialog placement. WinForms' <c>FormStartPosition.CenterScreen</c> centers an
/// ownerless form on whichever monitor holds the mouse cursor — so a dialog popped by a
/// background timer (the update prompt) lands on whatever side monitor the cursor was idling
/// on (issue #88). The app's dialogs use <c>Manual</c> and center on the primary monitor
/// instead, where the tray lives.
/// </summary>
internal static class DialogPlacement
{
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
    /// Centers <paramref name="form"/> on the primary monitor's working area. Call from
    /// <c>OnLoad</c> after the DPI-correct relayout so the measured size is final. For the
    /// current callers (Manual start position, no Location assigned) the handle is created
    /// at the default origin — already on the primary — so DeviceDpi is right from the
    /// first relayout and this move never crosses a DPI boundary. Deliberately never
    /// re-centers on DpiChanged: that fires when the user drags the dialog to another
    /// monitor, and re-centering would yank it out of their hand.
    /// </summary>
    public static void CenterOnPrimary(Form form)
    {
        var area = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        form.Location = CenterIn(area, form.Size);
    }
}
