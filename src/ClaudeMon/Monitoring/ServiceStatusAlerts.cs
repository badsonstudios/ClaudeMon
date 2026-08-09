namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;
using ClaudeMon.Services;

/// <summary>One service-status notification to show as a tray balloon.</summary>
public record ServiceStatusAlertMessage(string Title, string Text, ToolTipIcon Icon);

/// <summary>
/// Decides whether a status transition is worth notifying about. Pure: it takes the previous
/// and current status and returns the alert to raise, or null.
///
/// The rule is "incident <em>start</em>, then escalation": going from healthy (or unknown) to
/// any non-operational state notifies once, and a further notification only follows if the
/// severity rises (minor → major). A long incident therefore produces one balloon, not one per
/// poll, and recovery is silent — the flyout line simply disappears.
///
/// The settings gate lives here too — opt-in toggle, the master notifications switch, and the
/// snooze — so "respects snooze" is testable rather than buried in the tray class.
/// </summary>
internal static class ServiceStatusAlerts
{
    /// <summary>
    /// The alert to raise for this transition, or null for none. A snoozed alert is dropped
    /// rather than deferred: unlike a budget threshold, the incident is already visible on the
    /// flyout line the whole time, so re-announcing it after the snooze would be the noisier
    /// choice.
    /// </summary>
    public static ServiceStatusAlertMessage? Evaluate(
        ServiceStatus? previous, ServiceStatus? current, NotificationSettings settings, DateTimeOffset now)
    {
        if (!settings.NotifyOnServiceIncident || !settings.Enabled || settings.IsSnoozed(now))
            return null;

        if (current is null || current.IsOperational)
            return null;

        // Already in an incident at this severity or worse — nothing new to say.
        if (previous is not null && !previous.IsOperational && current.Level <= previous.Level)
            return null;

        return Compose(current);
    }

    private static ServiceStatusAlertMessage Compose(ServiceStatus status)
    {
        var icon = status.Level switch
        {
            ServiceStatusLevel.Maintenance => ToolTipIcon.Info,
            ServiceStatusLevel.Minor => ToolTipIcon.Warning,
            _ => ToolTipIcon.Error,
        };

        var title = status.Level == ServiceStatusLevel.Maintenance
            ? "Anthropic maintenance"
            : "Anthropic service issue";

        // Trimmed like the flyout line: the description is the status page's text, and a balloon
        // silently truncates a long one anyway.
        return new ServiceStatusAlertMessage(
            title,
            $"{ServiceStatusText.Trim(status.Description)} — see {ServiceStatusClient.StatusPageUrl}",
            icon);
    }
}
