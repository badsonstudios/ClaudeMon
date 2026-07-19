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

    // --- limits[] helpers (per-model weekly caps, issue #98) ---

    private static UsageLimit WeeklyAll(double pct, string? severity = null, bool? isActive = true) =>
        new("weekly_all", "weekly", pct, severity, DateTimeOffset.UtcNow.AddDays(3), isActive, null);

    private static UsageLimit WeeklyScoped(
        string? model, double? pct, string? severity = null, bool? isActive = true) =>
        new("weekly_scoped", "weekly", pct, severity, DateTimeOffset.UtcNow.AddDays(2), isActive,
            model is null ? null : new LimitScope(new LimitScopeModel(model)));

    // A realistic multi-bucket response: the top-level fields the API always sends (the 5-hour
    // at a quiet level so only weekly alerts fire) plus the limits[] array under test. Note the
    // overall weekly is deliberately present BOTH as seven_day and as a weekly_all limit —
    // that's the real payload shape, and the source of the double-alert risk #98 had to solve.
    private static UsageResponse WithLimits(params UsageLimit[] limits) =>
        new(FiveHourBucket(10, 0.4), new UsageBucket(60, DateTimeOffset.UtcNow.AddDays(3)), limits);

    // ================================================================
    // Per-model weekly alerts (issue #98)
    // ================================================================

    [Fact]
    public void Weekly_ScopedBucketOverThreshold_FiresWarningNamingTheModel()
    {
        _alertManager.Check(WithLimits(WeeklyAll(10), WeeklyScoped("Fable", 84)), DefaultSettings());

        var alert = Assert.Single(_notifications);
        Assert.Equal("Weekly Usage Warning", alert.Title);
        Assert.Equal(ToolTipIcon.Warning, alert.Icon);
        Assert.Contains("Fable weekly", alert.Text);
        Assert.Contains("84%", alert.Text);
        Assert.Contains("resets", alert.Text); // countdown included
    }

    [Fact]
    public void Weekly_MultipleBucketsCrossTogether_CombineIntoOneBalloon()
    {
        // NotifyIcon shows one balloon at a time, so buckets crossing on the same poll must be
        // combined — firing them separately would show only the last while latching all three,
        // silently losing the rest.
        var response = WithLimits(WeeklyAll(55), WeeklyScoped("Fable", 84), WeeklyScoped("Opus", 60));

        _alertManager.Check(response, DefaultSettings());

        var alert = Assert.Single(_notifications);
        Assert.Equal("Usage alerts", alert.Title);
        Assert.Contains("Fable weekly", alert.Text);
        Assert.Contains("Opus weekly", alert.Text);
        Assert.Contains("7-day usage", alert.Text);

        // A second identical poll adds nothing — each bucket latches independently.
        _alertManager.Check(response, DefaultSettings());
        Assert.Single(_notifications);
    }

    [Fact]
    public void Weekly_CombinedBalloon_UsesTheMostSevereIcon()
    {
        _alertManager.Check(
            WithLimits(WeeklyAll(55), WeeklyScoped("Fable", 95)), DefaultSettings());

        var alert = Assert.Single(_notifications);
        Assert.Equal(ToolTipIcon.Error, alert.Icon); // critical outranks the warning
    }

    [Fact]
    public void Weekly_OneBucketLatched_DoesNotMaskAnother()
    {
        // The overall weekly alerts first; a model cap crossing later must still fire.
        _alertManager.Check(WithLimits(WeeklyAll(55), WeeklyScoped("Fable", 20)), DefaultSettings());
        Assert.Single(_notifications);

        _alertManager.Check(WithLimits(WeeklyAll(56), WeeklyScoped("Fable", 70)), DefaultSettings());

        Assert.Equal(2, _notifications.Count);
        Assert.Contains("Fable weekly", _notifications[1].Text);
    }

    [Fact]
    public void Weekly_ScopedAtNearCap_FiresCritical()
    {
        _alertManager.Check(WithLimits(WeeklyAll(10), WeeklyScoped("Fable", 92)), DefaultSettings());

        var alert = Assert.Single(_notifications);
        Assert.Equal("Weekly Limit Critical", alert.Title);
        Assert.Equal(ToolTipIcon.Error, alert.Icon);
        Assert.Contains("Fable weekly", alert.Text);
    }

    [Fact]
    public void Weekly_ApiCriticalSeverityBelowNearCap_EscalatesToError()
    {
        // Anthropic flags the bucket critical at 60% — below our 90% near-cap. Its judgment is
        // an escalation floor, so this fires the critical alert rather than a plain warning.
        _alertManager.Check(
            WithLimits(WeeklyAll(10), WeeklyScoped("Fable", 60, severity: "critical")),
            DefaultSettings());

        var alert = Assert.Single(_notifications);
        Assert.Equal("Weekly Limit Critical", alert.Title);
        Assert.Equal(ToolTipIcon.Error, alert.Icon);
    }

    [Fact]
    public void Weekly_ApiCriticalSeverity_DoesNotRefireWhileHeld()
    {
        var response = WithLimits(WeeklyAll(10), WeeklyScoped("Fable", 60, severity: "critical"));

        _alertManager.Check(response, DefaultSettings());
        _alertManager.Check(response, DefaultSettings());
        _alertManager.Check(response, DefaultSettings());

        Assert.Single(_notifications);
    }

    [Fact]
    public void Weekly_ApiSeverityOscillating_DoesNotFlapCriticalEveryOtherPoll()
    {
        // Severity flipping between warning and critical at a steady percentage must not
        // produce an Error balloon on every other poll — the critical only re-arms once the
        // bucket drops below the level where severity could escalate it again.
        for (var i = 0; i < 4; i++)
        {
            var severity = i % 2 == 0 ? "critical" : "warning";
            _alertManager.Check(
                WithLimits(WeeklyAll(10), WeeklyScoped("Fable", 60, severity: severity)),
                DefaultSettings());
        }

        Assert.Single(_notifications);
        Assert.Equal("Weekly Limit Critical", _notifications[0].Title);
    }

    [Fact]
    public void Weekly_ApiCriticalWellBelowWarningLevel_DoesNotAlert()
    {
        // A severity blip on a barely-used bucket must not produce "critical at 10%".
        _alertManager.Check(
            WithLimits(WeeklyAll(5), WeeklyScoped("Fable", 10, severity: "critical")),
            DefaultSettings());

        Assert.Empty(_notifications);
    }

    [Fact]
    public void Weekly_ApiCriticalBlipThenGenuineWarningCrossing_StillAlerts()
    {
        // The blip must not latch the warning off: when the bucket later genuinely crosses the
        // warning threshold, the user still hears about it.
        _alertManager.Check(
            WithLimits(WeeklyAll(5), WeeklyScoped("Fable", 10, severity: "critical")),
            DefaultSettings());
        Assert.Empty(_notifications);

        _alertManager.Check(WithLimits(WeeklyAll(5), WeeklyScoped("Fable", 60)), DefaultSettings());

        var alert = Assert.Single(_notifications);
        Assert.Equal("Weekly Usage Warning", alert.Title);
        Assert.Contains("Fable weekly usage at 60%", alert.Text);
    }

    [Fact]
    public void Weekly_ScopedResets_RearmsThatBucketOnly()
    {
        _alertManager.Check(WithLimits(WeeklyAll(10), WeeklyScoped("Fable", 60)), DefaultSettings());
        Assert.Single(_notifications);

        // Fable's weekly window resets (drops below the warning level), then climbs again.
        _alertManager.Check(WithLimits(WeeklyAll(10), WeeklyScoped("Fable", 5)), DefaultSettings());
        _alertManager.Check(WithLimits(WeeklyAll(10), WeeklyScoped("Fable", 60)), DefaultSettings());

        Assert.Equal(2, _notifications.Count);
        Assert.All(_notifications, n => Assert.Contains("Fable weekly", n.Text));
    }

    [Fact]
    public void Weekly_BucketBlinksOutAndReturns_DoesNotRefire()
    {
        // A bucket missing from one poll is far more often a partial response than a cap that
        // genuinely vanished, so its state is kept: the same still-latched alert must not fire
        // again when it reappears unchanged.
        _alertManager.Check(WithLimits(WeeklyAll(55), WeeklyScoped("Fable", 60)), DefaultSettings());
        Assert.Single(_notifications);

        _alertManager.Check(WithLimits(WeeklyAll(55)), DefaultSettings());
        _alertManager.Check(WithLimits(WeeklyAll(55), WeeklyScoped("Fable", 60)), DefaultSettings());

        Assert.Single(_notifications);
    }

    [Fact]
    public void Weekly_BucketReturnsAfterItsWindowReset_FiresFresh()
    {
        // The legitimate re-alert path: the cap comes back genuinely reset (below the warning
        // level), which re-arms it, then climbs again.
        _alertManager.Check(WithLimits(WeeklyAll(10), WeeklyScoped("Fable", 60)), DefaultSettings());
        Assert.Single(_notifications);

        _alertManager.Check(WithLimits(WeeklyAll(10)), DefaultSettings());
        _alertManager.Check(WithLimits(WeeklyAll(10), WeeklyScoped("Fable", 4)), DefaultSettings());
        _alertManager.Check(WithLimits(WeeklyAll(10), WeeklyScoped("Fable", 60)), DefaultSettings());

        Assert.Equal(2, _notifications.Count);
        Assert.All(_notifications, n => Assert.Contains("Fable weekly", n.Text));
    }

    [Fact]
    public void Weekly_OverallBucket_FiresOnceWhenPresentInBothSevenDayAndLimits()
    {
        // The real payload carries the overall weekly twice (top-level seven_day AND weekly_all).
        // Alerts must come from one source only, or every overall weekly warning would double.
        var response = new UsageResponse(
            FiveHourBucket(10, 0.4),
            new UsageBucket(60, DateTimeOffset.UtcNow.AddDays(3)),
            new[] { WeeklyAll(60) });

        _alertManager.Check(response, DefaultSettings());

        var alert = Assert.Single(_notifications);
        Assert.Equal("Weekly Usage Warning", alert.Title);
        Assert.StartsWith("7-day usage", alert.Text);
    }

    [Fact]
    public void Weekly_InactiveCap_StillAlerts()
    {
        // is_active does NOT mean "in force": the live API reports the overall weekly as
        // inactive while it plainly is in force, so gating on the flag would silence the
        // weekly alert this app has always had. Verified against the real payload.
        _alertManager.Check(
            WithLimits(WeeklyAll(60, isActive: false), WeeklyScoped("Fable", 95, isActive: false)),
            DefaultSettings());

        var alert = Assert.Single(_notifications);
        Assert.Contains("7-day usage at 60%", alert.Text);
        Assert.Contains("Fable weekly usage at 95%", alert.Text);
    }

    [Fact]
    public void Weekly_LivePayloadShape_AlertsOnTheModelCap()
    {
        // The exact shape the usage API returns today (group "weekly", overall marked
        // inactive, the model cap carrying the API's own "warning" severity).
        var response = new UsageResponse(
            FiveHourBucket(52, 0.4),
            new UsageBucket(44, DateTimeOffset.UtcNow.AddDays(3)),
            new[]
            {
                new UsageLimit("session", "session", 52, "normal",
                    DateTimeOffset.UtcNow.AddHours(2), false, null),
                new UsageLimit("weekly_all", "weekly", 44, "normal",
                    DateTimeOffset.UtcNow.AddDays(3), false, null),
                new UsageLimit("weekly_scoped", "weekly", 79, "warning",
                    DateTimeOffset.UtcNow.AddDays(2), true,
                    new LimitScope(new LimitScopeModel("Fable"))),
            });

        _alertManager.Check(response, DefaultSettings());

        var alert = Assert.Single(_notifications);
        Assert.Equal("Weekly Usage Warning", alert.Title);
        Assert.Contains("Fable weekly usage at 79%", alert.Text);
    }

    [Fact]
    public void Weekly_NullPercentBucket_DoesNotAlertOrThrow()
    {
        var ex = Record.Exception(() =>
            _alertManager.Check(WithLimits(WeeklyAll(10), WeeklyScoped("Fable", null)), DefaultSettings()));

        Assert.Null(ex);
        Assert.Empty(_notifications);
    }

    [Fact]
    public void Weekly_ScopedWithoutModelName_StillAlerts()
    {
        _alertManager.Check(WithLimits(WeeklyAll(10), WeeklyScoped(null, 60)), DefaultSettings());

        var alert = Assert.Single(_notifications);
        Assert.Contains("model weekly", alert.Text);
    }

    [Fact]
    public void Weekly_Snoozed_ScopedAlertIsDeferredNotDropped()
    {
        var response = WithLimits(WeeklyAll(10), WeeklyScoped("Fable", 60));

        _alertManager.Check(response, SnoozedSettings(TimeSpan.FromHours(1)));
        Assert.Empty(_notifications);

        _alertManager.Check(response, DefaultSettings());

        var alert = Assert.Single(_notifications);
        Assert.Contains("Fable weekly", alert.Text);
    }

    [Fact]
    public void Weekly_SessionLimitInLimitsArray_DoesNotDriveWeeklyAlerts()
    {
        // The session bucket also appears in limits[]; the 5-hour alerts own it, so it must not
        // produce a weekly alert on top (nor be treated as a weekly cap at all).
        var session = new UsageLimit("session", "session", 95, "critical",
            DateTimeOffset.UtcNow.AddHours(2), true, null);

        _alertManager.Check(WithLimits(session, WeeklyAll(10)), DefaultSettings());

        Assert.Empty(_notifications);
    }

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
    public void BothBucketsRisky_CombinesPaceAndWeeklyIntoOneBalloon()
    {
        _alertManager.Check(Both(65, 55, elapsedFraction: 0.4), DefaultSettings());

        // One balloon carrying both — separate ones would leave the user seeing only the last.
        var alert = Assert.Single(_notifications);
        Assert.Equal("Usage alerts", alert.Title);
        Assert.Contains("through the window", alert.Text); // the pace warning
        Assert.Contains("7-day usage at 55%", alert.Text);
    }

    [Fact]
    public void SevenDay_AtNearCap_FiresCritical()
    {
        // The legacy (limits[]-absent) path gained the critical tier the per-model buckets use.
        _alertManager.Check(SevenDay(95), DefaultSettings());

        var alert = Assert.Single(_notifications);
        Assert.Equal("Weekly Limit Critical", alert.Title);
        Assert.Equal(ToolTipIcon.Error, alert.Icon);
        Assert.Contains("7-day usage at 95%", alert.Text);
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

    // ================================================================
    // Snooze (issue #14)
    // ================================================================

    private static AppSettings SnoozedSettings(TimeSpan fromNow, bool notifyOnReset = false) => new()
    {
        Notifications = new NotificationSettings
        {
            NotifyOnReset = notifyOnReset,
            SnoozeUntil = DateTimeOffset.UtcNow + fromNow,
        },
    };

    [Fact]
    public void Check_Snoozed_NearCap_SuppressesNotification()
    {
        _alertManager.Check(FiveHour(95), SnoozedSettings(TimeSpan.FromHours(1)));
        Assert.Empty(_notifications);
    }

    [Fact]
    public void Check_Snoozed_PaceWarning_SuppressedThenDeferred()
    {
        // 60% used at 40% elapsed is past pace; suppressed while snoozed, fires after.
        _alertManager.Check(FiveHour(60, 0.4), SnoozedSettings(TimeSpan.FromHours(1)));
        Assert.Empty(_notifications);

        _alertManager.Check(FiveHour(62, 0.4), DefaultSettings());
        Assert.Single(_notifications);
        Assert.Equal("On Track to Run Out", _notifications[0].Title);
    }

    [Fact]
    public void Check_Snoozed_SevenDay_SuppressesNotification()
    {
        _alertManager.Check(SevenDay(95), SnoozedSettings(TimeSpan.FromHours(1)));
        Assert.Empty(_notifications);
    }

    [Fact]
    public void Check_SnoozeExpiresWithConditionStillTrue_DeferredAlertFires()
    {
        // Alarm-clock semantics: the alert suppressed during the snooze is deferred, not
        // swallowed — the same still-true condition fires on the first unsnoozed poll.
        _alertManager.Check(FiveHour(95), SnoozedSettings(TimeSpan.FromHours(1)));
        Assert.Empty(_notifications);

        _alertManager.Check(FiveHour(96), DefaultSettings());

        Assert.Single(_notifications);
        Assert.Equal("Almost Out", _notifications[0].Title);
    }

    [Fact]
    public void Check_ExpiredSnooze_FiresNormally()
    {
        // A stale SnoozeUntil in the past is simply not a snooze.
        var settings = new AppSettings
        {
            Notifications = new NotificationSettings
            {
                SnoozeUntil = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5),
            },
        };

        _alertManager.Check(FiveHour(95), settings);

        Assert.Single(_notifications);
    }

    [Fact]
    public void Check_Snoozed_ResetStillRearms_AlertsFireAfterResume()
    {
        var snoozed = SnoozedSettings(TimeSpan.FromHours(1), notifyOnReset: true);

        // Near-cap reached while snoozed (suppressed), then the window resets while still
        // snoozed (reset notice suppressed too, but the re-arm must still happen)...
        _alertManager.Check(FiveHour(95), snoozed);
        _alertManager.Check(FiveHour(10, 0.1), snoozed);
        Assert.Empty(_notifications);

        // ...so after resuming, climbing near the cap again fires exactly one fresh alert.
        _alertManager.Check(FiveHour(95), DefaultSettings());
        Assert.Single(_notifications);
        Assert.Equal("Almost Out", _notifications[0].Title);
    }
}
