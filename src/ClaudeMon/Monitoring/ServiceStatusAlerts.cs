namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;
using ClaudeMon.Services;

/// <summary>One service-status notification to show as a tray balloon.</summary>
public record ServiceStatusAlertMessage(string Title, string Text, ToolTipIcon Icon);

/// <summary>
/// Decides whether a status transition is worth notifying about. Pure: it takes the latched
/// severity of the incident already accounted for and the current status, and returns the new
/// latch plus the alert to raise, or null.
///
/// The rule is "incident <em>start</em>, then escalation": going from healthy (or unknown) to
/// any non-operational state notifies once, and a further notification only follows if the
/// severity rises (minor → major). A long incident therefore produces one balloon, not one per
/// poll, and recovery is silent — the flyout line simply disappears.
///
/// The latch is returned rather than remembered so the caller can persist it
/// (<see cref="AppSettings.ServiceIncidentLevel"/>): held in memory only, it reset on every
/// restart and a long incident re-announced itself each time the app started (#138).
///
/// Why the latch is keyed on severity rather than on an incident id (#150): no usable id exists.
/// The endpoint this app reads, statuspage's <c>/api/v2/status.json</c>, carries none — the body
/// is a <c>page</c> block (id, name, url, time zone, last-updated) and a <c>status</c> block of
/// <c>indicator</c> plus <c>description</c>, nothing else. Nor would fetching one help:
/// <c>indicator</c> is statuspage's <em>roll-up</em> across every unresolved incident and
/// component, so even with <c>summary.json</c>'s incident list in hand there is no single
/// incident the level belongs to, only an arbitrary pick from however many are open. The two
/// fields already to hand are no better as identity — <c>page.updated_at</c> moves on every edit
/// <em>within</em> an incident (it would re-announce one outage repeatedly, a worse bug than the
/// one it fixes), and <c>description</c> comes from a small canned vocabulary that different
/// incidents share. Severity-keying is therefore the accepted design rather than a stopgap; the
/// gap it leaves is bounded, and documented on <see cref="Evaluate"/>.
///
/// The settings gate lives here too — opt-in toggle, the master notifications switch, and the
/// snooze — so "respects snooze" is testable rather than buried in the tray class.
/// </summary>
internal static class ServiceStatusAlerts
{
    /// <summary>
    /// The latch to persist and the alert to raise for this reading (either may be null).
    ///
    /// The latch follows what was <em>observed</em>, not what was shown, so a suppressed alert
    /// is dropped rather than deferred: unlike a budget threshold, the incident is already
    /// visible on the flyout line the whole time, so re-announcing it once the snooze expires
    /// (or once the toggle is turned on) would be the noisier choice. It also means the latch
    /// tracks the incident faithfully whatever the settings say, so no settings change can leave
    /// it stale and silence a later incident. (A recovery the app never saw still can — that is
    /// the one accepted gap, at the suppression branch below.)
    /// </summary>
    public static (ServiceStatusLevel? Latch, ServiceStatusAlertMessage? Alert) Evaluate(
        ServiceStatusLevel? latch, ServiceStatus? current, NotificationSettings settings, DateTimeOffset now)
    {
        // No reading this time (the status page couldn't be reached): nothing was observed, so
        // whatever was latched still stands.
        if (current is null)
            return (latch, null);

        // Recovery clears the latch — unconditionally, so the next incident always alerts. Note
        // that only an *observed* recovery clears it; see the suppression below.
        if (current.IsOperational)
            return (null, null);

        // Already in an incident at this severity or worse — nothing new to say.
        //
        // The one accepted gap (#149, #150): keyed on severity alone, this line can't tell "the
        // same outage continuing" from "a second outage no worse than the last one seen". When a
        // recovery passes entirely unobserved — the app shut, asleep, or just between polls for
        // one incident's whole tail and the next one's start — the stale latch swallows the new
        // incident's balloon. The cost is exactly that one notification: the flyout still shows
        // the incident line throughout, and a single healthy reading re-arms the latch.
        if (latch is { } seen && current.Level <= seen)
            return (latch, null);

        var suppressed = !settings.NotifyOnServiceIncident || !settings.Enabled || settings.IsSnoozed(now);
        return (current.Level, suppressed ? null : Compose(current));
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
