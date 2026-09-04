namespace ClaudeMon.Tests;

using ClaudeMon.UI;

public class TaskbarHealTrackerTests
{
    private const string Device = @"\\.\DISPLAY1";
    private const string Other = @"\\.\DISPLAY2";
    private const TaskbarOverlayStatus Broken = TaskbarOverlayStatus.NotVisible;
    private const TaskbarOverlayStatus AlsoBroken = TaskbarOverlayStatus.Misplaced;

    /// <summary>Feed the tracker <paramref name="count"/> identical verdicts, 2 s apart.</summary>
    private static TaskbarHealVerdict Observe(
        TaskbarHealTracker tracker, TaskbarOverlayStatus status, int count, ref long now)
    {
        var verdict = default(TaskbarHealVerdict);
        for (var i = 0; i < count; i++)
        {
            verdict = tracker.Observe(Device, status, now);
            now += TaskbarHealPolicy.CheckIntervalMs;
        }

        return verdict;
    }

    [Fact]
    public void AHealthyReadoutIsKeptAndSaysNothing()
    {
        var tracker = new TaskbarHealTracker();
        var verdict = tracker.Observe(Device, TaskbarOverlayStatus.Healthy, 1000);

        Assert.False(verdict.Rebuild);
        Assert.Equal(TaskbarHealLog.None, verdict.Log);
    }

    [Fact]
    public void OneBadCheckLogsTheFaultButKeepsTheReadout()
    {
        var tracker = new TaskbarHealTracker();
        var verdict = tracker.Observe(Device, Broken, 1000);

        Assert.False(verdict.Rebuild);
        Assert.Equal(TaskbarHealLog.Fault, verdict.Log);
        Assert.Equal(1, verdict.ConsecutiveUnhealthy);
    }

    [Fact]
    public void TheSecondConsecutiveBadCheckRebuilds()
    {
        var tracker = new TaskbarHealTracker();
        var now = 1000L;
        var verdict = Observe(tracker, Broken, TaskbarHealPolicy.UnhealthyChecksBeforeRebuild, ref now);

        Assert.True(verdict.Rebuild);
        Assert.Equal(TaskbarHealLog.Rebuilding, verdict.Log);
        Assert.Equal(TaskbarHealPolicy.UnhealthyChecksBeforeRebuild, verdict.ConsecutiveUnhealthy);
    }

    [Fact]
    public void ARecoveryBetweenFaultsResetsTheStrikes()
    {
        var tracker = new TaskbarHealTracker();
        var now = 1000L;

        Observe(tracker, Broken, 1, ref now);
        var recovered = Observe(tracker, TaskbarOverlayStatus.Healthy, 1, ref now);
        var faultAgain = Observe(tracker, Broken, 1, ref now);

        Assert.Equal(TaskbarHealLog.Recovered, recovered.Log);
        Assert.Equal(Broken, recovered.PreviousStatus);
        Assert.False(faultAgain.Rebuild);
        Assert.Equal(1, faultAgain.ConsecutiveUnhealthy);
    }

    [Fact]
    public void TwoBadChecksSeparatedByAGoodOneNeverRebuild()
    {
        // The point of the tolerance: a readout that keeps recovering is not a broken readout,
        // however many isolated bad ticks it racks up.
        var tracker = new TaskbarHealTracker();
        var now = 1000L;

        for (var i = 0; i < 20; i++)
        {
            Assert.False(Observe(tracker, Broken, 1, ref now).Rebuild);
            Assert.False(Observe(tracker, TaskbarOverlayStatus.Healthy, 1, ref now).Rebuild);
        }
    }

    [Fact]
    public void TheCooldownStopsARebuildLoop()
    {
        var tracker = new TaskbarHealTracker();
        var now = 1000L;

        Assert.True(Observe(tracker, Broken, 2, ref now).Rebuild);

        // Keep failing every 2 s for the whole cooldown: exactly one more rebuild, at the end.
        var rebuilds = 0;
        var deadline = now + TaskbarHealPolicy.RebuildCooldownMs;
        while (now <= deadline)
        {
            if (Observe(tracker, Broken, 1, ref now).Rebuild)
                rebuilds++;
        }

        Assert.Equal(1, rebuilds);
    }

    [Fact]
    public void AFaultThatCannotBeHealedDoesNotFloodTheLog()
    {
        var tracker = new TaskbarHealTracker();
        var now = 1000L;
        var faultLines = 0;

        // Five minutes of a permanently broken readout, checked every 2 s.
        for (var i = 0; i < 150; i++)
        {
            if (Observe(tracker, Broken, 1, ref now).Log == TaskbarHealLog.Fault)
                faultLines++;
        }

        // Roughly one line per dampening interval, not one per check.
        Assert.InRange(faultLines, 1, 300_000 / TaskbarHealPolicy.FaultLogIntervalMs + 2);
    }

    [Fact]
    public void FlappingBetweenTwoFaultsIsAlsoDampened()
    {
        // The hazard a status-change-only dampener misses: every tick is a "new" status.
        var tracker = new TaskbarHealTracker();
        var now = 1000L;
        var faultLines = 0;

        for (var i = 0; i < 20; i++)
        {
            var verdict = tracker.Observe(Device, i % 2 == 0 ? Broken : AlsoBroken, now);
            now += TaskbarHealPolicy.CheckIntervalMs;
            if (verdict.Log == TaskbarHealLog.Fault)
                faultLines++;
        }

        Assert.InRange(faultLines, 1, 5);
    }

    [Fact]
    public void ClearingStrikesStopsAPreHealObservationCountingTowardsARebuild()
    {
        var tracker = new TaskbarHealTracker();
        var now = 1000L;

        Observe(tracker, Broken, 1, ref now);
        tracker.ClearStrikes();

        // The resume didn't break anything; this is strike one all over again.
        var verdict = Observe(tracker, Broken, 1, ref now);
        Assert.False(verdict.Rebuild);
        Assert.Equal(1, verdict.ConsecutiveUnhealthy);
    }

    [Fact]
    public void ClearingStrikesKeepsTheRebuildCooldown()
    {
        // A resume is not a licence to churn windows — the cooldown must survive it.
        var tracker = new TaskbarHealTracker();
        var now = 1000L;
        Assert.True(Observe(tracker, Broken, 2, ref now).Rebuild);

        tracker.ClearStrikes();
        Assert.False(Observe(tracker, Broken, 2, ref now).Rebuild);
    }

    [Fact]
    public void ADepartedDeviceForgetsItsHistory()
    {
        var tracker = new TaskbarHealTracker();
        var now = 1000L;
        Assert.True(Observe(tracker, Broken, 2, ref now).Rebuild);

        // Monitor unplugged and plugged back in: the cooldown from the old episode must not
        // silently protect the new readout from being healed.
        tracker.RetainOnly(new[] { Other });

        Assert.True(Observe(tracker, Broken, 2, ref now).Rebuild);
    }

    [Fact]
    public void RetainOnlyKeepsTheDevicesStillPresent()
    {
        var tracker = new TaskbarHealTracker();
        var now = 1000L;
        Assert.True(Observe(tracker, Broken, 2, ref now).Rebuild);

        tracker.RetainOnly(new[] { Device, Other });

        Assert.False(Observe(tracker, Broken, 2, ref now).Rebuild);
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var tracker = new TaskbarHealTracker();
        var now = 1000L;
        Assert.True(Observe(tracker, Broken, 2, ref now).Rebuild);

        tracker.Clear();

        Assert.True(Observe(tracker, Broken, 2, ref now).Rebuild);
    }

    [Fact]
    public void DevicesAreTrackedIndependently()
    {
        var tracker = new TaskbarHealTracker();

        Assert.False(tracker.Observe(Device, Broken, 1000).Rebuild);
        Assert.False(tracker.Observe(Other, Broken, 1000).Rebuild);
        // Second strike on the first device only — the other one is still on strike one.
        Assert.True(tracker.Observe(Device, Broken, 3000).Rebuild);
        Assert.True(tracker.Observe(Other, Broken, 3000).Rebuild);
    }

    [Fact]
    public void SuppressionAndWaitingAreLoggedOnceAndNeverRebuild()
    {
        var tracker = new TaskbarHealTracker();
        var now = 1000L;

        var first = Observe(tracker, TaskbarOverlayStatus.SuppressedForFullscreen, 1, ref now);
        var repeat = Observe(tracker, TaskbarOverlayStatus.SuppressedForFullscreen, 20, ref now);
        var waiting = Observe(tracker, TaskbarOverlayStatus.TaskbarMissing, 20, ref now);

        Assert.Equal(TaskbarHealLog.Suppressed, first.Log);
        Assert.Equal(TaskbarHealLog.None, repeat.Log);
        Assert.Equal(TaskbarHealLog.None, waiting.Log);
        Assert.False(first.Rebuild);
        Assert.False(repeat.Rebuild);
        Assert.False(waiting.Rebuild);
    }

    [Fact]
    public void ComingBackFromASuppressedStateIsNotLoggedAsARecovery()
    {
        // Nothing was ever wrong, so "healthy again" would be noise.
        var tracker = new TaskbarHealTracker();
        var now = 1000L;

        Observe(tracker, TaskbarOverlayStatus.SuppressedForFullscreen, 1, ref now);
        Assert.Equal(TaskbarHealLog.None, Observe(tracker, TaskbarOverlayStatus.Healthy, 1, ref now).Log);
    }

    [Fact]
    public void AWaitingReadoutIsLoggedWhenItStartsWaiting()
    {
        var tracker = new TaskbarHealTracker();
        var now = 1000L;

        Observe(tracker, TaskbarOverlayStatus.Healthy, 1, ref now);
        Assert.Equal(TaskbarHealLog.Waiting, Observe(tracker, TaskbarOverlayStatus.TaskbarMissing, 1, ref now).Log);
    }

    [Fact]
    public void ARebuiltReadoutStartsWithACleanRecord()
    {
        var tracker = new TaskbarHealTracker();
        var now = 1000L;
        Assert.True(Observe(tracker, Broken, 2, ref now).Rebuild);

        // The replacement is healthy: no "healthy again" line, because as far as the record is
        // concerned nothing was ever wrong with it.
        Assert.Equal(TaskbarHealLog.None, Observe(tracker, TaskbarOverlayStatus.Healthy, 1, ref now).Log);
    }
}
