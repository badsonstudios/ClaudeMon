namespace ClaudeMon.Tests;

using System.Text.Json;
using ClaudeMon.Models;
using ClaudeMon.Monitoring;

/// <summary>
/// The throttle-drift state machine (issue #186) against synthetic estimate series: baseline,
/// trigger, episode dedupe, hysteresis re-arm, plan-change exclusion, low-confidence
/// exclusion, gating deferral, retention, and restart persistence.
/// </summary>
public class DriftDetectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static ImpliedCapacity Estimate(
        double capacity, CapacityConfidence confidence = CapacityConfidence.Medium,
        string kind = "session") =>
        new(kind, null, capacity, null, confidence, 12, 0, null, null);

    private static DriftState Fresh => new() { Version = DriftState.CurrentVersion };

    /// <summary>Feeds one estimate per day for <paramref name="days"/> days; returns final state + all alerts.</summary>
    private static (DriftState State, List<DriftAlertMessage> Alerts) Run(
        DriftState state, DateTimeOffset start, int days, Func<int, double> capacityOf,
        ClaudePlan? plan = null, double threshold = 20, bool canNotify = true)
    {
        var alerts = new List<DriftAlertMessage>();
        for (var day = 0; day < days; day++)
        {
            var (dayAlerts, next) = DriftDetector.Observe(
                state, start + TimeSpan.FromDays(day), [Estimate(capacityOf(day))],
                plan, threshold, canNotify);
            alerts.AddRange(dayAlerts);
            state = next;
        }

        return (state, alerts);
    }

    [Fact]
    public void StableSeries_NeverAlerts()
    {
        // 30 days wobbling ±5% around 60M: well inside a 20% threshold.
        var (state, alerts) = Run(Fresh, T0, 30,
            day => 60_000_000 * (1 + (day % 2 == 0 ? 0.05 : -0.05)));

        Assert.Empty(alerts);
        var key = Assert.Single(state.Keys);
        Assert.False(key.Notified);
        Assert.True(key.Points.Count >= 29);
    }

    [Fact]
    public void MaterialDrop_AlertsExactlyOncePerEpisode()
    {
        // Ten stable days, then capacity steps down 30% and stays there for ten more.
        var (_, alerts) = Run(Fresh, T0, 20,
            day => day < 10 ? 60_000_000 : 42_000_000);

        var alert = Assert.Single(alerts);
        Assert.Contains("Session (5-hour)", alert.Title);
        Assert.Contains("30% below", alert.Text);
    }

    [Fact]
    public void Recovery_ReArmsForASecondEpisode()
    {
        // Drift → recover fully → drift again: two episodes, two alerts.
        var (_, alerts) = Run(Fresh, T0, 30, day => day switch
        {
            < 10 => 60_000_000, // baseline builds
            < 13 => 40_000_000, // episode 1
            < 22 => 60_000_000, // full recovery
            _ => 40_000_000,    // episode 2
        });

        Assert.Equal(2, alerts.Count);
    }

    [Fact]
    public void HoveringInsideTheHysteresisBand_StaysLatched()
    {
        // After the alert, capacity climbs to just under the recovery band (baseline × 0.85
        // for a 20% threshold): the episode must not re-arm, so no second alert when it dips.
        var (_, alerts) = Run(Fresh, T0, 30, day => day switch
        {
            < 10 => 100_000_000,
            < 13 => 70_000_000,  // −30%: alert
            < 20 => 84_000_000,  // between trigger (80M) and recovery (85M): still latched
            _ => 70_000_000,     // dips again — same episode, no new alert
        });

        Assert.Single(alerts);
    }

    [Fact]
    public void ExactlyAtTheTrigger_DoesNotFire_JustBelowDoes()
    {
        // Trigger is strict '<': exactly 80% of a 100M baseline stays quiet.
        var (_, atTrigger) = Run(Fresh, T0, 15, day => day < 10 ? 100_000_000 : 80_000_000);
        Assert.Empty(atTrigger);

        var (_, below) = Run(Fresh, T0, 15, day => day < 10 ? 100_000_000 : 79_900_000);
        Assert.Single(below);
    }

    [Fact]
    public void PlanChange_IsNotThrottling()
    {
        // Ten days on Max 20x, then a downgrade to Pro with a 5× capacity drop. Old-plan
        // points are excluded from the new plan's baseline, so nothing can fire until the
        // new plan accumulates its own baseline — and then the lower level IS the baseline.
        var state = Fresh;
        var alerts = new List<DriftAlertMessage>();
        for (var day = 0; day < 25; day++)
        {
            var plan = day < 10 ? ClaudePlan.Max20x : ClaudePlan.Pro;
            var capacity = day < 10 ? 300_000_000.0 : 60_000_000.0;
            var (dayAlerts, next) = DriftDetector.Observe(
                state, T0 + TimeSpan.FromDays(day), [Estimate(capacity)], plan, 20, true);
            alerts.AddRange(dayAlerts);
            state = next;
        }

        Assert.Empty(alerts);
    }

    [Fact]
    public void LowConfidenceEstimates_NeverEnterTheBaselineOrTrigger()
    {
        // Days of confident 60M, then a Low-confidence 30M reading: no alert (the current
        // estimate must itself be confident), and the 30M never poisons the baseline.
        var state = Run(Fresh, T0, 10, _ => 60_000_000).State;

        var (alerts, next) = DriftDetector.Observe(
            state, T0 + TimeSpan.FromDays(10),
            [Estimate(30_000_000, CapacityConfidence.Low)], null, 20, true);

        Assert.Empty(alerts);
        var key = Assert.Single(next.Keys);
        Assert.DoesNotContain(key.Points, p => p.Capacity == 30_000_000);
    }

    [Fact]
    public void TooFewBaselinePoints_StaysQuiet()
    {
        // Four confident days then a 50% drop: below MinBaselinePoints, no ground to stand on.
        var (_, alerts) = Run(Fresh, T0, 5, day => day < 4 ? 60_000_000 : 30_000_000);
        Assert.Empty(alerts);
    }

    [Fact]
    public void MultipleObservationsPerDay_RecordOnePoint_LastWins()
    {
        var state = Fresh;
        foreach (var capacity in new[] { 50_000_000.0, 55_000_000.0, 60_000_000.0 })
        {
            state = DriftDetector.Observe(
                state, T0 + TimeSpan.FromHours(capacity / 10_000_000), [Estimate(capacity)],
                null, 20, true).NewState;
        }

        var key = Assert.Single(state.Keys);
        var point = Assert.Single(key.Points);
        Assert.Equal(60_000_000, point.Capacity);
    }

    [Fact]
    public void Retention_PrunesOldPoints_BaselineIsThirtyDays()
    {
        var (state, _) = Run(Fresh, T0, 60, _ => 60_000_000);

        var key = Assert.Single(state.Keys);
        Assert.All(key.Points, p =>
            Assert.True(p.Date > DateOnly.FromDateTime((T0 + TimeSpan.FromDays(59)).UtcDateTime)
                .AddDays(-DriftDetector.RetentionDays)));
    }

    [Fact]
    public void Acknowledgment_QuietsTheEpisodeAndRebasesTheBaseline()
    {
        // Alert fires; the user opens the tab (ack). The pre-ack points are excluded, so the
        // accepted lower level becomes the norm once enough post-ack days accumulate — and a
        // FURTHER material drop below that fires a new episode.
        var (state, alerts) = Run(Fresh, T0, 13, day => day < 10 ? 100_000_000 : 70_000_000);
        Assert.Single(alerts);

        var ackAt = T0 + TimeSpan.FromDays(12) + TimeSpan.FromHours(1);
        var (changed, acked) = DriftDetector.Acknowledge(state, ackAt);
        Assert.True(changed);
        Assert.False(Assert.Single(acked.Keys).Notified);

        // Days 13–19 at the accepted 70M rebuild the post-ack baseline quietly...
        var (rebuilt, quiet) = Run(acked, T0 + TimeSpan.FromDays(13), 7, _ => 70_000_000);
        Assert.Empty(quiet);

        // ...and a further 30% drop below the accepted level is a new episode.
        var (_, again) = Run(rebuilt, T0 + TimeSpan.FromDays(20), 3, _ => 49_000_000);
        Assert.Single(again);
    }

    [Fact]
    public void Acknowledge_WithNoEpisode_ChangesNothing()
    {
        var (state, _) = Run(Fresh, T0, 10, _ => 60_000_000);
        var (changed, _) = DriftDetector.Acknowledge(state, T0 + TimeSpan.FromDays(10));
        Assert.False(changed);
    }

    [Fact]
    public void GatedAlert_IsDeferredNotDropped()
    {
        // Drift begins while alerts are gated (snoozed / toggled off): nothing fires and the
        // latch stays unset — then the first gate-open evaluation with the condition still
        // true fires it.
        var (state, gated) = Run(Fresh, T0, 13,
            day => day < 10 ? 100_000_000 : 70_000_000, canNotify: false);
        Assert.Empty(gated);
        Assert.False(Assert.Single(state.Keys).Notified);

        var (alerts, _) = DriftDetector.Observe(
            state, T0 + TimeSpan.FromDays(13), [Estimate(70_000_000)], null, 20, canNotify: true);
        Assert.Single(alerts);
    }

    [Fact]
    public void Threshold_IsConfigurable()
    {
        // A 25% drop: quiet at a 40% threshold, loud at 10%.
        var (_, strict) = Run(Fresh, T0, 13,
            day => day < 10 ? 100_000_000 : 75_000_000, threshold: 40);
        Assert.Empty(strict);

        var (_, sensitive) = Run(Fresh, T0, 13,
            day => day < 10 ? 100_000_000 : 75_000_000, threshold: 10);
        Assert.Single(sensitive);
    }

    [Fact]
    public void KeysAreIndependent_AndScopedByModel()
    {
        var state = Fresh;
        for (var day = 0; day < 13; day++)
        {
            var session = Estimate(day < 10 ? 60_000_000 : 40_000_000);
            var weekly = new ImpliedCapacity(
                "weekly_scoped", "Opus 4", 300_000_000, null, CapacityConfidence.High, 12, 0, null, null);
            state = DriftDetector.Observe(
                state, T0 + TimeSpan.FromDays(day), [session, weekly], null, 20, true).NewState;
        }

        // The session key latched; the healthy scoped weekly did not.
        Assert.True(state.Keys.Single(k => k.Kind == "session").Notified);
        Assert.False(state.Keys.Single(k => k.Kind == "weekly_scoped").Notified);
    }

    [Fact]
    public void State_SurvivesAJsonRoundTrip_WithoutReAlerting()
    {
        var (state, alerts) = Run(Fresh, T0, 13, day => day < 10 ? 100_000_000 : 70_000_000);
        Assert.Single(alerts);

        var reloaded = JsonSerializer.Deserialize<DriftState>(JsonSerializer.Serialize(state))!;

        // Still in drift after the restart: the persisted latch keeps it to one alert.
        var (_, after) = Run(reloaded, T0 + TimeSpan.FromDays(13), 5, _ => 70_000_000);
        Assert.Empty(after);
    }
}
