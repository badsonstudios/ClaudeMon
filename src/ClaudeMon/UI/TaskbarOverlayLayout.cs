namespace ClaudeMon.UI;

/// <summary>
/// The screen rectangle of a taskbar that <see cref="TaskbarOverlayLayout.Compute"/> needs —
/// its left, top, and right edges. Bundling the coordinates into one value prevents the
/// transposition that a flat list of adjacent <c>int</c> parameters invites.
/// </summary>
internal readonly record struct TaskbarRect(int Left, int Top, int Right);

/// <summary>
/// Pure placement math for the taskbar overlay, factored out of
/// <see cref="TaskbarOverlayWindow"/> so it can be unit-tested without any windowing.
/// Given a taskbar's screen rectangle and (optionally) the left edge of its
/// notification area, it returns where the right-anchored overlay should sit.
/// </summary>
internal static class TaskbarOverlayLayout
{
    /// <summary>
    /// Computes the overlay's top-left corner. The overlay is right-anchored: it ends just
    /// left of the notification area (the clock/tray) when <paramref name="notifyLeft"/> is
    /// known, otherwise at the taskbar's right edge minus <paramref name="rightReserve"/>
    /// (space kept clear for a windowless clock on secondary taskbars). The X is clamped to
    /// the taskbar so a wide or nudged readout never spills off the left edge or past the right.
    /// </summary>
    /// <param name="taskbar">The taskbar's screen rectangle (left/top/right edges).</param>
    /// <param name="notifyLeft">
    /// Left edge of the notification area, or null when it can't be found (common on
    /// secondary taskbars) — in which case the overlay anchors to the taskbar's right edge,
    /// less <paramref name="rightReserve"/>.
    /// </param>
    /// <param name="width">The measured overlay width.</param>
    /// <param name="rightReserve">
    /// Horizontal space to keep clear at the taskbar's right edge for a clock whose window
    /// bounds can't be queried (Windows 11 secondary taskbars). Ignored when
    /// <paramref name="notifyLeft"/> is known.
    /// </param>
    /// <param name="horizontalOffset">
    /// User nudge: positive shifts the overlay right, negative shifts it left. The result is
    /// kept within the taskbar (never off the left edge, never past the right edge).
    /// </param>
    public static (int X, int Y) Compute(
        TaskbarRect taskbar, int? notifyLeft, int width, int rightReserve = 0, int horizontalOffset = 0)
    {
        var anchorRight = notifyLeft ?? (taskbar.Right - rightReserve);
        var x = anchorRight - width + horizontalOffset;

        // Keep the overlay on the taskbar: clamp the right edge first, then the left, so a
        // readout wider than the taskbar still pins to the left rather than off-screen.
        var maxX = taskbar.Right - width;
        if (x > maxX)
            x = maxX;
        if (x < taskbar.Left)
            x = taskbar.Left;
        return (x, taskbar.Top);
    }
}
