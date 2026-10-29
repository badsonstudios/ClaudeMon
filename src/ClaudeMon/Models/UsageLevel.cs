namespace ClaudeMon.Models;

/// <summary>
/// How worrying a limit's usage is — the classification behind every usage colour (tray icon,
/// taskbar, flyout bars) and the taskbar escalation effects. Ordered mildest first, so levels
/// compare with <c>&lt;</c>/<c>&gt;</c>.
/// </summary>
public enum UsageLevel
{
    Green,
    Yellow,
    Orange,
    Red,
}
