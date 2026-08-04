namespace ClaudeMon.UI;

using System.Runtime.InteropServices;

/// <summary>
/// Draws the sort arrow on a <see cref="ListView"/> column header. WinForms has no property for
/// this: <c>ListView.Sorting</c> shows an arrow only when the control does the sorting itself,
/// which sorts by the displayed text — exactly what the breakdown tables must not do. So the
/// arrow is set on the underlying header control directly, which means the visual style draws it
/// and it matches whatever theme the header itself is rendered in.
/// Best-effort: a missing handle or a header that refuses the message is simply left alone.
/// </summary>
internal static class ListViewSortIndicator
{
    private const int LVM_FIRST = 0x1000;
    private const int LVM_GETHEADER = LVM_FIRST + 31;

    private const int HDM_FIRST = 0x1200;
    private const int HDM_GETITEMW = HDM_FIRST + 11;
    private const int HDM_SETITEMW = HDM_FIRST + 12;

    private const int HDI_FORMAT = 0x0004;
    private const int HDF_SORTUP = 0x0400;
    private const int HDF_SORTDOWN = 0x0200;

    /// <summary>
    /// Puts an up (ascending) or down (descending) arrow on <paramref name="column"/> and clears
    /// it from every other column. Assigning column widths doesn't disturb the arrow (that goes
    /// through a different message), but recreating the list's handle would drop it — so the
    /// caller re-applies after every fill and once on load, and must not toggle a ListView
    /// property that forces a handle recreation while the window is open.
    /// </summary>
    public static void Apply(ListView list, int column, bool ascending)
    {
        // Reading list.Handle would force the handle into existence mid-construction; the form
        // re-applies from OnLoad once there really is a header to draw on.
        if (!list.IsHandleCreated)
            return;

        var header = SendMessage(list.Handle, LVM_GETHEADER, IntPtr.Zero, IntPtr.Zero);
        if (header == IntPtr.Zero)
            return;

        for (var i = 0; i < list.Columns.Count; i++)
        {
            var item = new HDITEM { mask = HDI_FORMAT };
            // Read-modify-write rather than assigning a format outright: fmt also carries the
            // column's text alignment, which the right-aligned numeric columns depend on.
            if (SendMessage(header, HDM_GETITEMW, (IntPtr)i, ref item) == IntPtr.Zero)
                continue;

            item.fmt &= ~(HDF_SORTUP | HDF_SORTDOWN);
            if (i == column)
                item.fmt |= ascending ? HDF_SORTUP : HDF_SORTDOWN;

            SendMessage(header, HDM_SETITEMW, (IntPtr)i, ref item);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref HDITEM lParam);

    // Only mask and fmt are ever set here; the rest of the layout has to be present so the
    // header control writes fmt at the offset it expects.
    [StructLayout(LayoutKind.Sequential)]
    private struct HDITEM
    {
        public int mask;
        public int cxy;
        public IntPtr pszText;
        public IntPtr hbm;
        public int cchTextMax;
        public int fmt;
        public IntPtr lParam;
        public int iImage;
        public int iOrder;
        public uint type;
        public IntPtr pvFilter;
        public uint state;
    }
}
