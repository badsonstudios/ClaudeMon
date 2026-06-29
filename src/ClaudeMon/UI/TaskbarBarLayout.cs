namespace ClaudeMon.UI;

/// <summary>
/// The pixel geometry of a single taskbar usage bar: how far the coloured fill extends, where
/// the time-in-window tick sits, and where the hour/day divider lines fall. Kept as a pure
/// computation (no GDI) so the value→pixel mapping is unit-testable, mirroring
/// <see cref="TaskbarOverlayLayout"/>.
/// </summary>
public readonly record struct TaskbarBarGeometry(int FillWidth, int? TickX, IReadOnlyList<int> DividerXs);

public static class TaskbarBarLayout
{
    /// <summary>
    /// Computes the fill width, time-tick position, and divider positions for a bar of the given
    /// track width. <paramref name="pct"/> is clamped to 0..100 so an over-limit reading fills
    /// the whole track (never past it); <paramref name="windowFraction"/> (0..1, or null when the
    /// reset time is unknown) places the tick; <paramref name="segments"/> is the number of equal
    /// divisions (5 for the 5-hour bar, 7 for the 7-day bar) and yields <c>segments - 1</c>
    /// interior dividers.
    /// </summary>
    public static TaskbarBarGeometry Compute(int trackWidth, double pct, double? windowFraction, int segments)
    {
        if (trackWidth < 0)
            trackWidth = 0;

        var fill = (int)Math.Round(trackWidth * Math.Clamp(pct, 0, 100) / 100.0);

        int? tick = windowFraction is { } f
            ? (int)Math.Round(trackWidth * Math.Clamp(f, 0.0, 1.0))
            : null;

        var dividers = new List<int>(Math.Max(0, segments - 1));
        for (var i = 1; i < segments; i++)
            dividers.Add((int)Math.Round(trackWidth * (i / (double)segments)));

        return new TaskbarBarGeometry(fill, tick, dividers);
    }
}
