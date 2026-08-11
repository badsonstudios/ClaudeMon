namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class ServiceStatusAlertsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    // The opt-in toggle is off by default, so every behaviour test has to turn it on.
    private static readonly NotificationSettings AlertsOn =
        new() { Enabled = true, NotifyOnServiceIncident = true };

    private static (ServiceStatusLevel? Latch, ServiceStatusAlertMessage? Alert) Evaluate(
        ServiceStatusLevel? latch, ServiceStatus? current, NotificationSettings? settings = null) =>
        ServiceStatusAlerts.Evaluate(latch, current, settings ?? AlertsOn, Now);

    private static ServiceStatusAlertMessage? Alert(
        ServiceStatusLevel? latch, ServiceStatus? current, NotificationSettings? settings = null) =>
        Evaluate(latch, current, settings).Alert;

    private static ServiceStatus Status(ServiceStatusLevel level) =>
        new(level, level == ServiceStatusLevel.Operational ? "All Systems Operational" : "Partial System Outage");

    [Fact]
    public void Evaluate_DisabledByDefault_IsSilent()
    {
        // The default settings have NotifyOnServiceIncident off — an incident must stay quiet.
        Assert.Null(Alert(null, Status(ServiceStatusLevel.Critical), new NotificationSettings()));
    }

    [Fact]
    public void Evaluate_NotificationsMasterSwitchOff_IsSilent()
    {
        var settings = new NotificationSettings { Enabled = false, NotifyOnServiceIncident = true };

        Assert.Null(Alert(null, Status(ServiceStatusLevel.Critical), settings));
    }

    [Fact]
    public void Evaluate_Snoozed_IsSilent()
    {
        var settings = AlertsOn with { SnoozeUntil = Now.AddHours(1) };

        Assert.Null(Alert(null, Status(ServiceStatusLevel.Critical), settings));
    }

    [Fact]
    public void Evaluate_ExpiredSnooze_Alerts()
    {
        var settings = AlertsOn with { SnoozeUntil = Now.AddMinutes(-1) };

        Assert.NotNull(Alert(null, Status(ServiceStatusLevel.Critical), settings));
    }

    [Fact]
    public void Evaluate_HealthyToIncident_Alerts()
    {
        var alert = Alert(null, Status(ServiceStatusLevel.Major));

        Assert.NotNull(alert);
        Assert.Contains("Partial System Outage", alert.Text);
        Assert.Equal(ToolTipIcon.Error, alert.Icon);
    }

    [Fact]
    public void Evaluate_FirstEverReadingIsAnIncident_Alerts()
    {
        // Nothing latched (fresh install, or the status page was healthy) still counts as a start.
        Assert.NotNull(Alert(null, Status(ServiceStatusLevel.Minor)));
    }

    [Fact]
    public void Evaluate_OngoingIncidentAtSameLevel_IsSilent()
    {
        Assert.Null(Alert(ServiceStatusLevel.Major, Status(ServiceStatusLevel.Major)));
    }

    [Fact]
    public void Evaluate_IncidentEscalates_AlertsAgain()
    {
        Assert.NotNull(Alert(ServiceStatusLevel.Minor, Status(ServiceStatusLevel.Critical)));
    }

    [Fact]
    public void Evaluate_IncidentImproves_IsSilent()
    {
        Assert.Null(Alert(ServiceStatusLevel.Critical, Status(ServiceStatusLevel.Minor)));
    }

    [Fact]
    public void Evaluate_Recovery_IsSilent()
    {
        // Recovery just makes the flyout line disappear — no "all clear" balloon.
        Assert.Null(Alert(ServiceStatusLevel.Major, Status(ServiceStatusLevel.Operational)));
    }

    [Fact]
    public void Evaluate_NoCurrentStatus_IsSilent()
    {
        Assert.Null(Alert(ServiceStatusLevel.Major, null));
    }

    [Fact]
    public void Evaluate_Maintenance_IsInformational()
    {
        var alert = Alert(null, Status(ServiceStatusLevel.Maintenance));

        Assert.NotNull(alert);
        Assert.Equal(ToolTipIcon.Info, alert.Icon);
        Assert.Contains("maintenance", alert.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_Minor_IsAWarningNotAnError()
    {
        var alert = Alert(null, Status(ServiceStatusLevel.Minor));

        Assert.Equal(ToolTipIcon.Warning, alert?.Icon);
    }

    [Fact]
    public void Evaluate_AlertLinksToTheStatusPage()
    {
        var alert = Alert(null, Status(ServiceStatusLevel.Critical));

        Assert.Contains("status.claude.com", alert?.Text);
    }

    // --- The persisted latch (#138) ---

    [Fact]
    public void Evaluate_IncidentStart_LatchesTheLevel()
    {
        var (latch, alert) = Evaluate(null, Status(ServiceStatusLevel.Major));

        Assert.Equal(ServiceStatusLevel.Major, latch);
        Assert.NotNull(alert);
    }

    [Fact]
    public void Evaluate_RestartDuringTheSameIncident_IsSilent()
    {
        // The whole point of #138: a restart re-reads the status from scratch, and the latch
        // loaded from settings — not an in-memory "previous" — is what keeps it quiet.
        //
        // These inputs are also the accepted gap of #150 in its entirety: a *second* incident at
        // the same level, after a recovery ClaudeMon never saw, arrives here looking exactly like
        // this and is silenced too. There is deliberately no test asserting the opposite, because
        // severity-keyed as it is (see ServiceStatusAlerts' class doc) the two cases are
        // indistinguishable — telling them apart is what an incident id would have bought.
        var (latch, alert) = Evaluate(ServiceStatusLevel.Major, Status(ServiceStatusLevel.Major));

        Assert.Equal(ServiceStatusLevel.Major, latch);
        Assert.Null(alert);
    }

    [Fact]
    public void Evaluate_EscalationAfterARestart_AlertsAndRaisesTheLatch()
    {
        var (latch, alert) = Evaluate(ServiceStatusLevel.Minor, Status(ServiceStatusLevel.Major));

        Assert.Equal(ServiceStatusLevel.Major, latch);
        Assert.NotNull(alert);
    }

    [Fact]
    public void Evaluate_IncidentImproves_KeepsTheHigherLatch()
    {
        // Latching down would let the same incident re-alert if it worsened again.
        var (latch, _) = Evaluate(ServiceStatusLevel.Critical, Status(ServiceStatusLevel.Minor));

        Assert.Equal(ServiceStatusLevel.Critical, latch);
    }

    [Fact]
    public void Evaluate_Recovery_ClearsTheLatch()
    {
        var (latch, _) = Evaluate(ServiceStatusLevel.Critical, Status(ServiceStatusLevel.Operational));

        Assert.Null(latch);
    }

    [Fact]
    public void Evaluate_RecoveryWhileNotificationsAreOff_StillClearsTheLatch()
    {
        // The latch must never outlive its incident, or a later one would be silenced.
        var (latch, _) = Evaluate(
            ServiceStatusLevel.Major, Status(ServiceStatusLevel.Operational), new NotificationSettings());

        Assert.Null(latch);
    }

    [Fact]
    public void Evaluate_SuppressedAlert_StillLatches()
    {
        // Dropped, not deferred: the incident is on the flyout line the whole time, so the
        // snooze expiring (or the toggle going on) must not re-announce it.
        var (latch, alert) = Evaluate(
            null, Status(ServiceStatusLevel.Major), AlertsOn with { SnoozeUntil = Now.AddHours(1) });

        Assert.Equal(ServiceStatusLevel.Major, latch);
        Assert.Null(alert);
    }

    [Fact]
    public void Evaluate_NoCurrentStatus_LeavesTheLatchAlone()
    {
        // An unreachable status page says nothing about the incident — don't clear the latch.
        var (latch, _) = Evaluate(ServiceStatusLevel.Major, null);

        Assert.Equal(ServiceStatusLevel.Major, latch);
    }

    [Fact]
    public void Evaluate_ObservedRecovery_ReArmsForTheNextIncident()
    {
        // The other side of the accepted gap (#150), and why it stays narrow: one healthy
        // reading is enough to clear the latch, so a separate incident that ClaudeMon actually
        // watched end is announced normally even at the same level.
        var (cleared, _) = Evaluate(ServiceStatusLevel.Major, Status(ServiceStatusLevel.Operational));
        var (latch, alert) = Evaluate(cleared, Status(ServiceStatusLevel.Major));

        Assert.NotNull(alert);
        Assert.Equal(ServiceStatusLevel.Major, latch);
    }
}
