namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class ServiceStatusAlertsTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 9, 12, 0, 0, TimeSpan.Zero);

    // The opt-in toggle is off by default, so every behaviour test has to turn it on.
    private static readonly NotificationSettings AlertsOn =
        new() { Enabled = true, NotifyOnServiceIncident = true };

    private static ServiceStatusAlertMessage? Evaluate(
        ServiceStatus? previous, ServiceStatus? current, NotificationSettings? settings = null) =>
        ServiceStatusAlerts.Evaluate(previous, current, settings ?? AlertsOn, Now);

    private static ServiceStatus Status(ServiceStatusLevel level) =>
        new(level, level == ServiceStatusLevel.Operational ? "All Systems Operational" : "Partial System Outage");

    [Fact]
    public void Evaluate_DisabledByDefault_IsSilent()
    {
        // The default settings have NotifyOnServiceIncident off — an incident must stay quiet.
        Assert.Null(Evaluate(null, Status(ServiceStatusLevel.Critical), new NotificationSettings()));
    }

    [Fact]
    public void Evaluate_NotificationsMasterSwitchOff_IsSilent()
    {
        var settings = new NotificationSettings { Enabled = false, NotifyOnServiceIncident = true };

        Assert.Null(Evaluate(null, Status(ServiceStatusLevel.Critical), settings));
    }

    [Fact]
    public void Evaluate_Snoozed_IsSilent()
    {
        var settings = AlertsOn with { SnoozeUntil = Now.AddHours(1) };

        Assert.Null(Evaluate(null, Status(ServiceStatusLevel.Critical), settings));
    }

    [Fact]
    public void Evaluate_ExpiredSnooze_Alerts()
    {
        var settings = AlertsOn with { SnoozeUntil = Now.AddMinutes(-1) };

        Assert.NotNull(Evaluate(null, Status(ServiceStatusLevel.Critical), settings));
    }

    [Fact]
    public void Evaluate_HealthyToIncident_Alerts()
    {
        var alert = Evaluate(
            Status(ServiceStatusLevel.Operational), Status(ServiceStatusLevel.Major));

        Assert.NotNull(alert);
        Assert.Contains("Partial System Outage", alert.Text);
        Assert.Equal(ToolTipIcon.Error, alert.Icon);
    }

    [Fact]
    public void Evaluate_FirstEverReadingIsAnIncident_Alerts()
    {
        // Nothing known before (app just started mid-incident) still counts as a start.
        Assert.NotNull(Evaluate(null, Status(ServiceStatusLevel.Minor)));
    }

    [Fact]
    public void Evaluate_OngoingIncidentAtSameLevel_IsSilent()
    {
        Assert.Null(Evaluate(
            Status(ServiceStatusLevel.Major), Status(ServiceStatusLevel.Major)));
    }

    [Fact]
    public void Evaluate_IncidentEscalates_AlertsAgain()
    {
        Assert.NotNull(Evaluate(
            Status(ServiceStatusLevel.Minor), Status(ServiceStatusLevel.Critical)));
    }

    [Fact]
    public void Evaluate_IncidentImproves_IsSilent()
    {
        Assert.Null(Evaluate(
            Status(ServiceStatusLevel.Critical), Status(ServiceStatusLevel.Minor)));
    }

    [Fact]
    public void Evaluate_Recovery_IsSilent()
    {
        // Recovery just makes the flyout line disappear — no "all clear" balloon.
        Assert.Null(Evaluate(
            Status(ServiceStatusLevel.Major), Status(ServiceStatusLevel.Operational)));
    }

    [Fact]
    public void Evaluate_NoCurrentStatus_IsSilent()
    {
        Assert.Null(Evaluate(Status(ServiceStatusLevel.Major), null));
    }

    [Fact]
    public void Evaluate_Maintenance_IsInformational()
    {
        var alert = Evaluate(null, Status(ServiceStatusLevel.Maintenance));

        Assert.NotNull(alert);
        Assert.Equal(ToolTipIcon.Info, alert.Icon);
        Assert.Contains("maintenance", alert.Title, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_Minor_IsAWarningNotAnError()
    {
        var alert = Evaluate(null, Status(ServiceStatusLevel.Minor));

        Assert.Equal(ToolTipIcon.Warning, alert?.Icon);
    }

    [Fact]
    public void Evaluate_AlertLinksToTheStatusPage()
    {
        var alert = Evaluate(null, Status(ServiceStatusLevel.Critical));

        Assert.Contains("status.claude.com", alert?.Text);
    }
}
