namespace ClaudeMon.UI;

/// <summary>
/// Shared logical (96-DPI) → physical-pixel scaling, so every hand-scaled window (the taskbar
/// overlay, the flyout, and the Settings dialog, all of which run <c>AutoScaleMode.None</c> under
/// Per-Monitor-V2) rounds identically. Consolidates what used to be several inline
/// <c>Math.Round(value * dpi/96f)</c> copies that disagreed on rounding mode.
/// </summary>
internal static class DpiScale
{
    /// <summary>The scale factor for a device DPI (96 = 100%); falls back to 1.0 for a non-positive DPI.</summary>
    public static float FactorForDpi(int dpi) => (dpi <= 0 ? 96 : dpi) / 96f;

    /// <summary>Rounds a logical (96-DPI) value to physical pixels at the given scale factor.</summary>
    public static int Scale(int logical, float scale) =>
        (int)System.Math.Round(logical * scale, System.MidpointRounding.AwayFromZero);
}
