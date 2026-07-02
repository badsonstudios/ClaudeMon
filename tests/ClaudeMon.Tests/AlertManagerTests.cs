namespace ClaudeMon.Tests;

using System.Windows.Forms;
using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class AlertManagerTests : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly List<(string Title, string Text, ToolTipIcon Icon)> _notifications;
    private readonly AlertManager _alertManager;

    public AlertManagerTests()
    {
        _notifyIcon = new NotifyIcon();
        _notifications = [];
        _alertManager = new AlertManager(_notifyIcon, (title, text, icon) =>
        {
            _notifications.Add((title, text, icon));
        });
    }

    public void Dispose() => _notifyIcon.Dispose();

    // --- Helpers ---

    private static AppSettings DefaultSettings() => new();

    private static AppSettings DisabledSettings() =>
        new() { Notifications = new NotificationSettings { Enabled = false } };

    private static AppSettings ResetEnabledSettings() =>
        new() { Notifications = new NotificationSettings { Enabled = true, NotifyOnReset = true } };

    private static AppSettings SensitivitySettings(PaceSensitivity sensitivity) =>
        new() { AlertThresholds = new AlertThresholds { PaceSensitivity = sensitivity } };

    private static AppSettings PaceDisabledSettings() =>
        new() { AlertThresholds = new AlertThresholds { PaceAlertsEnabled = false } };

    // A 5-hour bucket whose reset time places "now" at the given fraction through the window, so
    // the pace ratio (usage ÷ elapsed-fraction) is controllable. Default 0.4 ⇒ 60% past pace ≈ 65%.
    private static UsageBucket FiveHourBucket(double pct, double elapsedFraction = 0.4)
    {
        var reset = DateTimeOffset.UtcNow + TimeSpan.FromHours(5 * (1 - elapsedFraction));
        return new UsageBucket(pct, reset);
    }

    private static UsageResponse FiveHour(double pct, double elapsedFraction = 0.4) =>
        new(FiveHourBucket(pct, elapsedFraction), null);

    // A 5-hour bucket with an unknown reset time, so ElapsedFraction is null (no pace signal).
    private static UsageResponse FiveHourNoReset(double pct) =>
        new(new UsageBucket(pct, null), null);

    private static UsageResponse SevenDay(double pct) =>
        new(null, new UsageBucket(pct, DateTimeOffset.UtcNow.AddDays(3)));

    private static UsageResponse Both(double fiveHourPct, double sevenDayPct, double elapsedFraction = 0.4) =>
        new(FiveHourBucket(fiveHourPct, elapsedFraction), new UsageBucket(sevenDayPct, DateTimeOffset.UtcNow.AddDays(3)));

    // ================================================================
    // Notifications disabled
    // ================================================================

    [Fact]
    public void Check_NotificationsDisabled_FiresNothing()
    {
        _alertManager.Check(FiveHour(95), DisabledSettings());
        _alertManager.Check(SevenDay(90), DisabledSettings());
        Assert.Empty(_notifications);
    }

    // ================================================================
    // Near-cap backstop (critical "Almost Out")
    // ================================================================

    [Fact]
    public void NearCap_AtThreshold_FiresCritical()
    {
        _alertManager.Check(FiveHour(90), DefaultSettings());

        Assert.Single(_notifications);
        Assert.Equal("Almost Out", _notifications[0].Title);
        Assert.Equal(ToolTipIcon.Error, _notifications[0].Icon);
        Assert.Contains("90%", _notifications[0].Text);
    }

    [Fact]
    public void NearCap_FiresOnlyOnce()
    {
        _alertManager.Check(FiveHour(92), DefaultSettings());
        _alertManager.Check(FiveHour(95), DefaultSettings());
        Assert.Single(_notifications);
    }

    [Fact]
    public void NearCap_RefiresAfterDroppingClearAndRising()
    {
        _alertManager.Check(FiveHour(92), DefaultSettings());
        Assert.Single(_notifications);

        // Drop well clear of the near-cap and below pace, then rise back.
        _alertManager.Check(FiveHour(30), DefaultSettings());
        _alertManager.Check(FiveHour(92), DefaultSettings());

        Assert.Equal(2, _notifications.Count);
        Assert.All(_notifications, n => Assert.Equal("Almost Out", n.Title));
    }

    [Fact]
    public void NearCap_RespectsCustomThreshold()
    {
        var settings = new AppSettings { AlertThresholds = new AlertThresholds { NearCapWarning = 75 } };

        _alertManager.Check(FiveHour(76), settings);

        Assert.Single(_notifications);
        Assert.Equal("Almost Out", _notifications[0].Title);
    }

    // ================================================================
    // Pace early-warning ("On Track to Run Out")
    // ================================================================

    [Fact]
    public void Pace_OverPace_FiresWarning()
    {
        // 65% used at 40% through the window ⇒ ratio 1.625 ≥ 1.5 (Balanced).
        _alertManager.Check(FiveHour(65, elapsedFraction: 0.4), DefaultSettings());

        Assert.Single(_notifications);
        Assert.Equal("On Track to Run Out", _notifications[0].Title);
        Assert.Equal(ToolTipIcon.Warning, _notifications[0].Icon);
    }

    [Fact]
    public void Pace_OnPace_FiresNothing()
    {
        // 40% used at 40% elapsed ⇒ ratio 1.0, exactly on pace.
        _alertManager.Check(FiveHour(40, elapsedFraction: 0.4), DefaultSettings());
        Assert.Empty(_notifications);
    }

    [Fact]
    public void Pace_JustUnderTrigger_FiresNothing()
    {
        // 55% used at 40% elapsed ⇒ ratio 1.375 < 1.5.
        _alertManager.Check(FiveHour(55, elapsedFraction: 0.4), DefaultSettings());
        Assert.Empty(_notifications);
    }

    [Fact]
    public void Pace_FiresOnlyOnce()
    {
        _alertManager.Check(FiveHour(65, 0.4), DefaultSettings());
        _alertManager.Check(FiveHour(68, 0.4), DefaultSettings());
        Assert.Single(_notifications);
    }

    [Fact]
    public void Pace_RefiresAfterDroppingClearAndRising()
    {
        _alertManager.Check(FiveHour(65, 0.4), DefaultSettings());
        Assert.Single(_notifications);

        // 45% at 40% ⇒ ratio 1.125, clear of 1.5 − 0.15.
        _alertManager.Check(FiveHour(45, 0.4), DefaultSettings());
        _alertManager.Check(FiveHour(65, 0.4), DefaultSettings());

        Assert.Equal(2, _notifications.Count);
        Assert.All(_notifications, n => Assert.Equal("On Track to Run Out", n.Title));
    }

    [Fact]
    public void Pace_BelowUsageFloor_FiresNothing_EvenWhenPaceIsHigh()
    {
        // 15% used at 5% elapsed ⇒ ratio 3.0, but below the 20% usage floor → no cry-wolf.
        _alertManager.Check(FiveHour(15, elapsedFraction: 0.05), DefaultSettings());
        Assert.Empty(_notifications);
    }

    [Fact]
    public void Pace_AtUsageFloorAndOverPace_Fires()
    {
        // 25% used at 10% elapsed ⇒ ratio 2.5 ≥ 1.5, above the floor.
        _alertManager.Check(FiveHour(25, elapsedFraction: 0.10), DefaultSettings());

        Assert.Single(_notifications);
        Assert.Equal("On Track to Run Out", _notifications[0].Title);
    }

    [Fact]
    public void Pace_Disabled_FiresNothing()
    {
        _alertManager.Check(FiveHour(65, 0.4), PaceDisabledSettings());
        Assert.Empty(_notifications);
    }

    [Fact]
    public void Pace_UnknownResetTime_FiresNothing()
    {
        // No reset time ⇒ no elapsed fraction ⇒ no pace signal (and below near-cap).
        _alertManager.Check(FiveHourNoReset(65), DefaultSettings());
        Assert.Empty(_notifications);
    }

    [Theory]
    [InlineData(PaceSensitivity.Early, true)]    // trigger 1.25 — 1.375 ratio fires
    [InlineData(PaceSensitivity.Balanced, false)] // trigger 1.50 — 1.375 ratio does not
    [InlineData(PaceSensitivity.Late, false)]     // trigger 2.00 — 1.375 ratio does not
    public void Pace_Sensitivity_GovernsTrigger(PaceSensitivity sensitivity, bool shouldFire)
    {
        // 55% used at 40% elapsed ⇒ ratio 1.375.
        _alertManager.Check(FiveHour(55, elapsedFraction: 0.4), SensitivitySettings(sensitivity));

        Assert.Equal(shouldFire ? 1 : 0, _notifications.Count);
    }

    // ================================================================
    // Near-cap vs pace interaction
    // ================================================================

    [Fact]
    public void NearCap_SuppressesPace_WhenJumpingStraightToCritical()
    {
        // 95% is over pace AND past the near-cap — only the critical should fire.
        _alertManager.Check(FiveHour(95, 0.4), DefaultSettings());

        Assert.Single(_notifications);
        Assert.Equal("Almost Out", _notifications[0].Title);
    }

    [Fact]
    public void Pace_ThenNearCap_FiresBoth()
    {
        _alertManager.Check(FiveHour(65, 0.4), DefaultSettings());
        Assert.Single(_notifications);
        Assert.Equal("On Track to Run Out", _notifications[0].Title);

        _alertManager.Check(FiveHour(95, 0.4), DefaultSettings());
        Assert.Equal(2, _notifications.Count);
        Assert.Equal("Almost Out", _notifications[1].Title);
    }

    // ================================================================
    // 7-day warning
    // ================================================================

    [Fact]
    public void SevenDay_AtThreshold_FiresWeeklyWarning()
    {
        _alertManager.Check(SevenDay(50), DefaultSettings());

        Assert.Single(_notifications);
        Assert.Equal("Weekly Usage Warning", _notifications[0].Title);
        Assert.Equal(ToolTipIcon.Warning, _notifications[0].Icon);
        Assert.Contains("50%", _notifications[0].Text);
    }

    [Fact]
    public void SevenDay_BelowThreshold_FiresNothing()
    {
        _alertManager.Check(SevenDay(49), DefaultSettings());
        Assert.Empty(_notifications);
    }

    [Fact]
    public void SevenDay_FiresOnlyOnce_ThenRefiresAfterDropAndRise()
    {
        _alertManager.Check(SevenDay(55), DefaultSettings());
        _alertManager.Check(SevenDay(60), DefaultSettings());
        Assert.Single(_notifications);

        _alertManager.Check(SevenDay(30), DefaultSettings());
        _alertManager.Check(SevenDay(60), DefaultSettings());
        Assert.Equal(2, _notifications.Count);
    }

    [Fact]
    public void BothBucketsRisky_FiresPaceAndWeekly()
    {
        _alertManager.Check(Both(65, 55, elapsedFraction: 0.4), DefaultSettings());

        Assert.Equal(2, _notifications.Count);
        Assert.Contains(_notifications, n => n.Title == "On Track to Run Out");
        Assert.Contains(_notifications, n => n.Title == "Weekly Usage Warning");
    }

    // ================================================================
    // Reset notification
    // ================================================================

    [Fact]
    public void Reset_DropFromHighToLow_FiresAndClearsFlags()
    {
        var settings = ResetEnabledSettings();

        // Establish a high previous reading, then drop to a reset level.
        _alertManager.Check(FiveHour(50, 0.4), settings);
        _notifications.Clear();

        _alertManager.Check(FiveHour(3, 0.0), settings);

        Assert.Single(_notifications);
        Assert.Equal("Rate Limit Reset", _notifications[0].Title);
        Assert.Equal(ToolTipIcon.Info, _notifications[0].Icon);
    }

    [Fact]
    public void Reset_Disabled_FiresNothing()
    {
        var settings = DefaultSettings(); // NotifyOnReset defaults to false

        _alertManager.Check(FiveHour(50, 0.4), settings);
        _notifications.Clear();
        _alertManager.Check(FiveHour(3, 0.0), settings);

        Assert.Empty(_notifications);
    }

    [Fact]
    public void Reset_DoesNotRefireOnConsecutiveLowChecks()
    {
        var settings = ResetEnabledSettings();

        _alertManager.Check(FiveHour(50, 0.4), settings);
        _notifications.Clear();

        _alertManager.Check(FiveHour(3, 0.0), settings);
        _alertManager.Check(FiveHour(2, 0.0), settings);
        Assert.Single(_notifications);
    }

    [Fact]
    public void Reset_WithNotifyOff_StillRearmsAlerts()
    {
        // The reset notice is off (default), but a genuine window reset must still re-arm the
        // 5-hour alerts so the next window fires fresh.
        var settings = DefaultSettings();

        _alertManager.Check(FiveHour(95, 0.4), settings);
        Assert.Single(_notifications);
        Assert.Equal("Almost Out", _notifications[0].Title);

        // Window resets — no reset notice (NotifyOnReset off), and no new alert at ~0%.
        _alertManager.Check(FiveHour(2, 0.0), settings);
        Assert.Single(_notifications);

        // The new window goes critical again → fires fresh.
        _alertManager.Check(FiveHour(95, 0.4), settings);
        Assert.Equal(2, _notifications.Count);
        Assert.Equal("Almost Out", _notifications[1].Title);
    }

    [Fact]
    public void Reset_ClearsPaceFlag_AllowingRefire()
    {
        var settings = ResetEnabledSettings();

        // Pace warning fires.
        _alertManager.Check(FiveHour(65, 0.4), settings);
        Assert.Single(_notifications);
        Assert.Equal("On Track to Run Out", _notifications[0].Title);

        // Reset.
        _alertManager.Check(FiveHour(2, 0.0), settings);
        Assert.Equal(2, _notifications.Count);
        Assert.Equal("Rate Limit Reset", _notifications[1].Title);

        // Over pace again in the new window → fires fresh.
        _alertManager.Check(FiveHour(65, 0.4), settings);
        Assert.Equal(3, _notifications.Count);
        Assert.Equal("On Track to Run Out", _notifications[2].Title);
    }

    [Fact]
    public void Reset_ObservedAboveFivePct_StillRearmsPace()
    {
        // Polling is minutes apart, so a reset is often first *observed* already above 5% (e.g.
        // 65% → 12% of the new window). The old absolute "< 5%" floor missed this and left the
        // pace warning latched; a drop-based reset detection re-arms it.
        var settings = ResetEnabledSettings();

        _alertManager.Check(FiveHour(65, 0.4), settings);
        Assert.Single(_notifications);
        Assert.Equal("On Track to Run Out", _notifications[0].Title);

        // Reset observed at 12% (a 53-point drop) — in the dead zone the old code ignored.
        _alertManager.Check(FiveHour(12, 0.05), settings);
        Assert.Equal(2, _notifications.Count);
        Assert.Equal("Rate Limit Reset", _notifications[1].Title);

        // Over pace again in the new window → fires fresh (would stay silent if still latched).
        _alertManager.Check(FiveHour(65, 0.4), settings);
        Assert.Equal(3, _notifications.Count);
        Assert.Equal("On Track to Run Out", _notifications[2].Title);
    }

    // ================================================================
    // Null buckets / edge cases
    // ================================================================

    [Fact]
    public void Check_NullFiveHour_FiresNoFiveHourAlert()
    {
        _alertManager.Check(new UsageResponse(null, null), DefaultSettings());
        Assert.Empty(_notifications);
    }

    [Fact]
    public void Check_NullSevenDay_FiresNoWeeklyAlert()
    {
        _alertManager.Check(FiveHour(10, 0.4), DefaultSettings()); // below floor, no pace alert
        Assert.Empty(_notifications);
    }

    [Fact]
    public void Check_ZeroUsage_FiresNothing()
    {
        _alertManager.Check(FiveHour(0, 0.4), DefaultSettings());
        Assert.Empty(_notifications);
    }
}
