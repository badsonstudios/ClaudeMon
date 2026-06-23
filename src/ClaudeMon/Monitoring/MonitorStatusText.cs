namespace ClaudeMon.Monitoring;

/// <summary>
/// Single source of truth for the user-facing wording of monitor status, so the
/// tray tooltip, flyout, and About dialog all describe the same state identically.
/// </summary>
public static class MonitorStatusText
{
    /// <summary>
    /// Shown when the Claude Code OAuth token has expired. Guides the user to the
    /// fix (re-authenticate in Claude Code) — ClaudeMon never re-auths on its own.
    /// </summary>
    public const string SignInExpired = "Sign-in expired — run Claude Code to refresh";

    /// <summary>Short, human-readable label for a monitor status.</summary>
    public static string Describe(MonitorStatus status) => status switch
    {
        MonitorStatus.Connected => "Connected",
        MonitorStatus.RateLimited => "Rate limited",
        MonitorStatus.AuthError => "Sign-in expired",
        MonitorStatus.Offline => "Offline",
        _ => "Connecting…",
    };
}
