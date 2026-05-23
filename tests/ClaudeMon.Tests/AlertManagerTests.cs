namespace ClaudeMon.Tests;

using System.Windows.Forms;
using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class AlertManagerTests : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly List<(string Title, string Text, ToolTipIcon Icon)> _notifications;
    private readonly AlertManager _alertManager;
    private readonly DateTimeOffset _futureReset = DateTimeOffset.UtcNow.AddHours(3);

    public AlertManagerTests()
    {
        _notifyIcon = new NotifyIcon();
        _notifications = [];
        _alertManager = new AlertManager(_notifyIcon, (title, text, icon) =>
        {
            _notifications.Add((title, text, icon));
        });
    }

    public void Dispose()
    {
        _notifyIcon.Dispose();
    }

    // --- Helper methods ---

    private static AppSettings DefaultSettings() => new();

    private static AppSettings DisabledSettings() =>
        new() { Notifications = new NotificationSettings { Enabled = false } };

    private static AppSettings ResetEnabledSettings() =>
        new() { Notifications = new NotificationSettings { Enabled = true, NotifyOnReset = true } };

    private static AppSettings ProgressiveSettings(int startPct = 70) =>
        new()
        {
            AlertThresholds = new AlertThresholds
            {
                Mode = AlertMode.Progressive,
                ProgressiveStartPct = startPct,
            },
        };

    private UsageResponse FiveHourUsage(double pct) =>
        new(new UsageBucket(pct, _futureReset), null);

    private UsageResponse SevenDayUsage(double pct) =>
        new(null, new UsageBucket(pct, _futureReset));

    private UsageResponse BothUsage(double fiveHourPct, double sevenDayPct) =>
        new(new UsageBucket(fiveHourPct, _futureReset), new UsageBucket(sevenDayPct, _futureReset));

    // ================================================================
    // 1. No notification when notifications are disabled in settings
    // ================================================================

    [Fact]
    public void Check_NotificationsDisabled_NoNotificationFired()
    {
        var settings = DisabledSettings();
        var usage = FiveHourUsage(99);

        _alertManager.Check(usage, settings);

        Assert.Empty(_notifications);
    }

    [Fact]
    public void Check_NotificationsDisabledWith7DayAboveThreshold_NoNotificationFired()
    {
        var settings = DisabledSettings();
        var usage = SevenDayUsage(90);

        _alertManager.Check(usage, settings);

        Assert.Empty(_notifications);
    }

    // ================================================================
    // 2. Warning notification fires when 5-hour usage crosses warning threshold
    // ================================================================

    [Fact]
    public void Check_FiveHourAtWarningThreshold_FiresWarningNotification()
    {
        var settings = DefaultSettings();
        var usage = FiveHourUsage(50);

        _alertManager.Check(usage, settings);

        Assert.Single(_notifications);
        Assert.Equal("Usage Warning", _notifications[0].Title);
        Assert.Equal(ToolTipIcon.Warning, _notifications[0].Icon);
        Assert.Contains("50%", _notifications[0].Text);
    }

    [Fact]
    public void Check_FiveHourAboveWarningThreshold_FiresWarningNotification()
    {
        var settings = DefaultSettings();
        var usage = FiveHourUsage(65);

        _alertManager.Check(usage, settings);

        Assert.Single(_notifications);
        Assert.Equal("Usage Warning", _notifications[0].Title);
    }

    [Fact]
    public void Check_FiveHourBelowWarningThreshold_NoNotificationFired()
    {
        var settings = DefaultSettings();
        var usage = FiveHourUsage(49);

        _alertManager.Check(usage, settings);

        Assert.Empty(_notifications);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(65)]
    [InlineData(79)]
    public void Check_FiveHourAtOrAboveWarningBelowCritical_FiresWarningOnly(double pct)
    {
        var settings = DefaultSettings();
        var usage = FiveHourUsage(pct);

        _alertManager.Check(usage, settings);

        Assert.Single(_notifications);
        Assert.Equal("Usage Warning", _notifications[0].Title);
        Assert.Equal(ToolTipIcon.Warning, _notifications[0].Icon);
    }

    // ================================================================
    // 3. Critical notification fires when 5-hour usage crosses critical threshold
    // ================================================================

    [Fact]
    public void Check_FiveHourAtCriticalThreshold_FiresCriticalNotification()
    {
        var settings = DefaultSettings();
        var usage = FiveHourUsage(80);

        _alertManager.Check(usage, settings);

        Assert.Single(_notifications);
        Assert.Equal("Usage Critical", _notifications[0].Title);
        Assert.Equal(ToolTipIcon.Error, _notifications[0].Icon);
        Assert.Contains("80%", _notifications[0].Text);
    }

    [Fact]
    public void Check_FiveHourAboveCriticalThreshold_FiresCriticalNotification()
    {
        var settings = DefaultSettings();
        var usage = FiveHourUsage(100);

        _alertManager.Check(usage, settings);

        Assert.Single(_notifications);
        Assert.Equal("Usage Critical", _notifications[0].Title);
        Assert.Contains("100%", _notifications[0].Text);
    }

    // ================================================================
    // 4. 7-day warning notification fires when crossing threshold
    // ================================================================

    [Fact]
    public void Check_SevenDayAtWarningThreshold_FiresWeeklyWarning()
    {
        var settings = DefaultSettings();
        var usage = SevenDayUsage(50);

        _alertManager.Check(usage, settings);

        Assert.Single(_notifications);
        Assert.Equal("Weekly Usage Warning", _notifications[0].Title);
        Assert.Equal(ToolTipIcon.Warning, _notifications[0].Icon);
        Assert.Contains("50%", _notifications[0].Text);
    }

    [Fact]
    public void Check_SevenDayBelowWarningThreshold_NoNotificationFired()
    {
        var settings = DefaultSettings();
        var usage = SevenDayUsage(49);

        _alertManager.Check(usage, settings);

        Assert.Empty(_notifications);
    }

    [Fact]
    public void Check_SevenDayAboveWarningThreshold_FiresWeeklyWarning()
    {
        var settings = DefaultSettings();
        var usage = SevenDayUsage(65);

        _alertManager.Check(usage, settings);

        Assert.Single(_notifications);
        Assert.Equal("Weekly Usage Warning", _notifications[0].Title);
    }

    // ================================================================
    // 5. Deduplication: notifications don't re-fire on consecutive checks
    // ================================================================

    [Fact]
    public void Check_FiveHourWarningTwice_FiresOnlyOnce()
    {
        var settings = DefaultSettings();

        _alertManager.Check(FiveHourUsage(55), settings);
        _alertManager.Check(FiveHourUsage(60), settings);

        Assert.Single(_notifications);
    }

    [Fact]
    public void Check_FiveHourCriticalTwice_FiresOnlyOnce()
    {
        var settings = DefaultSettings();

        _alertManager.Check(FiveHourUsage(85), settings);
        _alertManager.Check(FiveHourUsage(90), settings);

        Assert.Single(_notifications);
    }

    [Fact]
    public void Check_SevenDayWarningTwice_FiresOnlyOnce()
    {
        var settings = DefaultSettings();

        _alertManager.Check(SevenDayUsage(55), settings);
        _alertManager.Check(SevenDayUsage(60), settings);

        Assert.Single(_notifications);
    }

    [Fact]
    public void Check_FiveHourWarningMultipleChecksAtSameLevel_FiresOnlyOnce()
    {
        var settings = DefaultSettings();

        _alertManager.Check(FiveHourUsage(65), settings);
        _alertManager.Check(FiveHourUsage(65), settings);
        _alertManager.Check(FiveHourUsage(65), settings);

        Assert.Single(_notifications);
    }

    // ================================================================
    // 6. Notifications re-fire after usage drops and crosses again
    // ================================================================

    [Fact]
    public void Check_FiveHourWarningDropsAndRises_FiresTwice()
    {
        var settings = DefaultSettings();

        // First crossing
        _alertManager.Check(FiveHourUsage(65), settings);
        Assert.Single(_notifications);

        // Drop below warning threshold
        _alertManager.Check(FiveHourUsage(30), settings);

        // Second crossing
        _alertManager.Check(FiveHourUsage(65), settings);
        Assert.Equal(2, _notifications.Count);
        Assert.All(_notifications, n => Assert.Equal("Usage Warning", n.Title));
    }

    [Fact]
    public void Check_FiveHourCriticalDropsAndRises_FiresTwice()
    {
        var settings = DefaultSettings();

        // First crossing
        _alertManager.Check(FiveHourUsage(85), settings);
        Assert.Single(_notifications);

        // Drop below critical but stay above warning
        _alertManager.Check(FiveHourUsage(65), settings);

        // Second crossing
        _alertManager.Check(FiveHourUsage(90), settings);
        Assert.Equal(2, _notifications.Count);
        Assert.All(_notifications, n => Assert.Equal("Usage Critical", n.Title));
    }

    [Fact]
    public void Check_SevenDayWarningDropsAndRises_FiresTwice()
    {
        var settings = DefaultSettings();

        _alertManager.Check(SevenDayUsage(55), settings);
        Assert.Single(_notifications);

        _alertManager.Check(SevenDayUsage(30), settings);

        _alertManager.Check(SevenDayUsage(60), settings);
        Assert.Equal(2, _notifications.Count);
    }

    // ================================================================
    // 7. Critical notification suppresses warning (no double-notify)
    // ================================================================

    [Fact]
    public void Check_FiveHourJumpsToCritical_FiresCriticalOnly()
    {
        var settings = DefaultSettings();
        var usage = FiveHourUsage(85);

        _alertManager.Check(usage, settings);

        Assert.Single(_notifications);
        Assert.Equal("Usage Critical", _notifications[0].Title);
        Assert.Equal(ToolTipIcon.Error, _notifications[0].Icon);
    }

    [Fact]
    public void Check_FiveHourCriticalSuppressesSubsequentWarning_NoDoubleNotify()
    {
        var settings = DefaultSettings();

        // Jump directly to critical
        _alertManager.Check(FiveHourUsage(85), settings);
        Assert.Single(_notifications);
        Assert.Equal("Usage Critical", _notifications[0].Title);

        // Drop to warning range -- warning flag was already set by critical
        _alertManager.Check(FiveHourUsage(65), settings);

        // Should not fire a warning because warning was already marked fired when critical fired
        Assert.Single(_notifications);
    }

    [Fact]
    public void Check_FiveHourWarningThenCritical_FiresBoth()
    {
        var settings = DefaultSettings();

        // First: warning
        _alertManager.Check(FiveHourUsage(65), settings);
        Assert.Single(_notifications);
        Assert.Equal("Usage Warning", _notifications[0].Title);

        // Then: critical
        _alertManager.Check(FiveHourUsage(85), settings);
        Assert.Equal(2, _notifications.Count);
        Assert.Equal("Usage Critical", _notifications[1].Title);
    }

    // ================================================================
    // 8. Reset notification fires when usage drops from >=20% to <5%
    // ================================================================

    [Fact]
    public void Check_UsageDropsFromHighToLow_FiresResetNotification()
    {
        var settings = ResetEnabledSettings();

        // First check at high usage to establish previous percentage
        _alertManager.Check(FiveHourUsage(50), settings);
        _notifications.Clear();

        // Drop to below 5%
        _alertManager.Check(FiveHourUsage(3), settings);

        Assert.Single(_notifications);
        Assert.Equal("Rate Limit Reset", _notifications[0].Title);
        Assert.Equal(ToolTipIcon.Info, _notifications[0].Icon);
        Assert.Contains("reset", _notifications[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Check_UsageDropsButNotifyOnResetDisabled_NoResetNotification()
    {
        var settings = DefaultSettings(); // NotifyOnReset defaults to false

        _alertManager.Check(FiveHourUsage(50), settings);
        _notifications.Clear();

        _alertManager.Check(FiveHourUsage(3), settings);

        Assert.Empty(_notifications);
    }

    [Fact]
    public void Check_UsageDropsFromLowToLow_NoResetNotification()
    {
        var settings = ResetEnabledSettings();

        // Previous was 10% (below 20 threshold)
        _alertManager.Check(FiveHourUsage(10), settings);
        _notifications.Clear();

        // Drop to 3% -- but previous was below 20, so no reset
        _alertManager.Check(FiveHourUsage(3), settings);

        Assert.Empty(_notifications);
    }

    [Fact]
    public void Check_UsageDropsFromHighButNotBelowFive_NoResetNotification()
    {
        var settings = ResetEnabledSettings();

        _alertManager.Check(FiveHourUsage(50), settings);
        _notifications.Clear();

        // Drop to 6% -- not below 5%
        _alertManager.Check(FiveHourUsage(6), settings);

        Assert.Empty(_notifications);
    }

    [Fact]
    public void Check_ResetNotificationDoesNotReFireOnConsecutiveChecks()
    {
        var settings = ResetEnabledSettings();

        _alertManager.Check(FiveHourUsage(50), settings);
        _notifications.Clear();

        // First reset notification
        _alertManager.Check(FiveHourUsage(3), settings);
        Assert.Single(_notifications);

        // Second check still below 5% -- should not re-fire
        _alertManager.Check(FiveHourUsage(2), settings);
        Assert.Single(_notifications);
    }

    [Fact]
    public void Check_ResetClearsWarningAndCriticalFlags_AllowsRefire()
    {
        var settings = ResetEnabledSettings();

        // Fire a warning
        _alertManager.Check(FiveHourUsage(65), settings);
        Assert.Single(_notifications);
        Assert.Equal("Usage Warning", _notifications[0].Title);

        // Usage drops to reset level
        _alertManager.Check(FiveHourUsage(2), settings);
        Assert.Equal(2, _notifications.Count);
        Assert.Equal("Rate Limit Reset", _notifications[1].Title);

        // Usage rises back to warning -- should fire again because reset cleared the flags
        _alertManager.Check(FiveHourUsage(65), settings);
        Assert.Equal(3, _notifications.Count);
        Assert.Equal("Usage Warning", _notifications[2].Title);
    }

    // ================================================================
    // Edge cases and combined scenarios
    // ================================================================

    [Fact]
    public void Check_NullFiveHourBucket_NoNotificationFired()
    {
        var settings = DefaultSettings();
        var usage = new UsageResponse(null, null);

        _alertManager.Check(usage, settings);

        Assert.Empty(_notifications);
    }

    [Fact]
    public void Check_NullSevenDayBucket_NoSevenDayNotification()
    {
        var settings = DefaultSettings();
        var usage = FiveHourUsage(30); // no seven-day bucket, below warning threshold

        _alertManager.Check(usage, settings);

        Assert.Empty(_notifications);
    }

    [Fact]
    public void Check_BothBucketsAboveThreshold_FiresBothNotifications()
    {
        var settings = DefaultSettings();
        var usage = BothUsage(65, 55);

        _alertManager.Check(usage, settings);

        Assert.Equal(2, _notifications.Count);
        Assert.Contains(_notifications, n => n.Title == "Usage Warning");
        Assert.Contains(_notifications, n => n.Title == "Weekly Usage Warning");
    }

    [Fact]
    public void Check_CustomThresholds_UsesProvidedValues()
    {
        var settings = new AppSettings
        {
            AlertThresholds = new AlertThresholds
            {
                FiveHourWarning = 50,
                FiveHourCritical = 75,
                SevenDayWarning = 40,
            },
        };

        // 55% is above the custom warning threshold of 50
        _alertManager.Check(FiveHourUsage(55), settings);

        Assert.Single(_notifications);
        Assert.Equal("Usage Warning", _notifications[0].Title);
    }

    [Fact]
    public void Check_CustomCriticalThreshold_FiresCriticalAtCustomLevel()
    {
        var settings = new AppSettings
        {
            AlertThresholds = new AlertThresholds
            {
                FiveHourWarning = 50,
                FiveHourCritical = 75,
            },
        };

        _alertManager.Check(FiveHourUsage(76), settings);

        Assert.Single(_notifications);
        Assert.Equal("Usage Critical", _notifications[0].Title);
    }

    [Fact]
    public void Check_FiveHourExactlyAtBoundary_UsageDropFromWarningToCriticalDrop_RefiresCorrectly()
    {
        var settings = DefaultSettings();

        // Go to critical
        _alertManager.Check(FiveHourUsage(85), settings);
        Assert.Single(_notifications);
        Assert.Equal("Usage Critical", _notifications[0].Title);

        // Drop to warning range (clears critical flag but not warning)
        _alertManager.Check(FiveHourUsage(65), settings);
        Assert.Single(_notifications); // no new notification

        // Rise back to critical
        _alertManager.Check(FiveHourUsage(90), settings);
        Assert.Equal(2, _notifications.Count);
        Assert.Equal("Usage Critical", _notifications[1].Title);
    }

    [Fact]
    public void Check_ZeroPercent_NoNotificationFired()
    {
        var settings = DefaultSettings();

        _alertManager.Check(FiveHourUsage(0), settings);

        Assert.Empty(_notifications);
    }

    [Fact]
    public void Check_FiveHourDropsBelowWarning_ClearsBothFlags()
    {
        var settings = DefaultSettings();

        // Fire critical
        _alertManager.Check(FiveHourUsage(85), settings);
        Assert.Single(_notifications);

        // Drop below warning threshold entirely
        _alertManager.Check(FiveHourUsage(30), settings);

        // Both warning and critical should re-fire if crossed again
        _alertManager.Check(FiveHourUsage(65), settings);
        Assert.Equal(2, _notifications.Count);
        Assert.Equal("Usage Warning", _notifications[1].Title);
    }

    // ================================================================
    // Progressive mode: fires at each 10% step above start threshold
    // ================================================================

    [Fact]
    public void Progressive_AtStartThreshold_FiresNotification()
    {
        var settings = ProgressiveSettings(70);

        _alertManager.Check(FiveHourUsage(70), settings);

        Assert.Single(_notifications);
        Assert.Equal("Usage Warning", _notifications[0].Title);
        Assert.Contains("70%", _notifications[0].Text);
    }

    [Fact]
    public void Progressive_BelowStartThreshold_NoNotification()
    {
        var settings = ProgressiveSettings(70);

        _alertManager.Check(FiveHourUsage(69), settings);

        Assert.Empty(_notifications);
    }

    [Fact]
    public void Progressive_RisesThrough_FiresAtEachStep()
    {
        var settings = ProgressiveSettings(70);

        // Hits 70% step
        _alertManager.Check(FiveHourUsage(72), settings);
        Assert.Single(_notifications);

        // Hits 80% step
        _alertManager.Check(FiveHourUsage(82), settings);
        Assert.Equal(2, _notifications.Count);

        // Hits 90% step
        _alertManager.Check(FiveHourUsage(92), settings);
        Assert.Equal(3, _notifications.Count);
    }

    [Fact]
    public void Progressive_JumpsToHigh_FiresAllCrossedSteps()
    {
        var settings = ProgressiveSettings(70);

        // Jump straight to 95% -- should fire 70, 80, and 90 steps
        _alertManager.Check(FiveHourUsage(95), settings);

        Assert.Equal(3, _notifications.Count);
    }

    [Fact]
    public void Progressive_SameStepTwice_FiresOnlyOnce()
    {
        var settings = ProgressiveSettings(70);

        _alertManager.Check(FiveHourUsage(75), settings);
        _alertManager.Check(FiveHourUsage(78), settings);

        Assert.Single(_notifications);
    }

    [Fact]
    public void Progressive_DropsAndRises_RefiresStep()
    {
        var settings = ProgressiveSettings(70);

        // Fire 70% step
        _alertManager.Check(FiveHourUsage(75), settings);
        Assert.Single(_notifications);

        // Drop below 70%
        _alertManager.Check(FiveHourUsage(65), settings);

        // Rise again -- should re-fire
        _alertManager.Check(FiveHourUsage(75), settings);
        Assert.Equal(2, _notifications.Count);
    }

    [Fact]
    public void Progressive_At90Percent_UsesErrorIcon()
    {
        var settings = ProgressiveSettings(70);

        // Get past 70 and 80 first
        _alertManager.Check(FiveHourUsage(72), settings);
        _alertManager.Check(FiveHourUsage(82), settings);

        // 90% step should use Error icon
        _alertManager.Check(FiveHourUsage(92), settings);

        Assert.Equal(3, _notifications.Count);
        Assert.Equal(ToolTipIcon.Warning, _notifications[0].Icon);
        Assert.Equal(ToolTipIcon.Warning, _notifications[1].Icon);
        Assert.Equal(ToolTipIcon.Error, _notifications[2].Icon);
        Assert.Equal("Usage Critical", _notifications[2].Title);
    }

    [Fact]
    public void Progressive_SevenDayStillWorks()
    {
        var settings = ProgressiveSettings(70);
        var usage = BothUsage(75, 55);

        _alertManager.Check(usage, settings);

        Assert.Equal(2, _notifications.Count);
        Assert.Contains(_notifications, n => n.Title == "Usage Warning");
        Assert.Contains(_notifications, n => n.Title == "Weekly Usage Warning");
    }

    [Fact]
    public void Progressive_CustomStartPct_UsesProvidedValue()
    {
        var settings = ProgressiveSettings(50);

        // 55% is above the start of 50%
        _alertManager.Check(FiveHourUsage(55), settings);
        Assert.Single(_notifications);

        // 62% is above 60% step
        _alertManager.Check(FiveHourUsage(62), settings);
        Assert.Equal(2, _notifications.Count);
    }

    [Fact]
    public void Progressive_ResetClearsSteps()
    {
        var settings = new AppSettings
        {
            AlertThresholds = new AlertThresholds
            {
                Mode = AlertMode.Progressive,
                ProgressiveStartPct = 70,
            },
            Notifications = new NotificationSettings { Enabled = true, NotifyOnReset = true },
        };

        // Fire 70% step
        _alertManager.Check(FiveHourUsage(75), settings);
        Assert.Single(_notifications);

        // Reset
        _alertManager.Check(FiveHourUsage(2), settings);
        Assert.Equal(2, _notifications.Count);
        Assert.Equal("Rate Limit Reset", _notifications[1].Title);

        // Rise again -- should re-fire because reset cleared steps
        _alertManager.Check(FiveHourUsage(75), settings);
        Assert.Equal(3, _notifications.Count);
    }
}
