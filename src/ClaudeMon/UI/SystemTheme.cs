namespace ClaudeMon.UI;

using System.Runtime.InteropServices;
using Microsoft.Win32;

/// <summary>
/// Reads the current Windows theme so the UI can match it. We follow the Windows "mode"
/// (<c>SystemUsesLightTheme</c>) — the dark/light toggle that drives the taskbar and Start, and
/// what most people mean by "Windows is in dark mode" — for both the overlay tick and the Settings
/// window. (Windows also has a separate <c>AppsUseLightTheme</c>; following the Windows mode matches
/// user expectation better for this taskbar-tied app.) The value is cached briefly so the 500 ms
/// overlay redraw doesn't hit the registry every tick, while still picking up a switch within
/// a few seconds.
/// </summary>
internal static class SystemTheme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const long CacheTtlMs = 5000;

    private static readonly object Gate = new();
    private static (bool Value, long AtMs) _systemLight = (false, long.MinValue);

    /// <summary>
    /// True when the Windows "mode" is light (the taskbar/Start are light) — so the app theme and
    /// white-ish overlay elements should be light. Defaults to false (dark) if it can't be read.
    /// </summary>
    public static bool IsLightWindowsMode() => Cached(ref _systemLight, "SystemUsesLightTheme");

    private static bool Cached(ref (bool Value, long AtMs) slot, string valueName)
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (now - slot.AtMs < CacheTtlMs)
                return slot.Value;

            slot = (ReadBool(valueName), now);
            return slot.Value;
        }
    }

    private static bool ReadBool(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // 1 = light, 0 = dark. Absent ⇒ dark (the Windows default).
            return key?.GetValue(valueName) is int value && value != 0;
        }
        catch
        {
            return false;
        }
    }

    // DWMWA_USE_IMMERSIVE_DARK_MODE: 20 on current builds, 19 on early 1903/1909.
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeOld = 19;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// Paints a window's title bar dark or light to match its body. Belt-and-suspenders on top of
    /// the app-wide colour mode; best-effort and silently ignored on older Windows builds.
    /// </summary>
    public static void ApplyTitleBar(IntPtr handle, bool dark)
    {
        var value = dark ? 1 : 0;
        if (DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeOld, ref value, sizeof(int));
    }
}
