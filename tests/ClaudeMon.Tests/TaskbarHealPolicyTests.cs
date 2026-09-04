namespace ClaudeMon.Tests;

using ClaudeMon.UI;
using Microsoft.Win32;

public class TaskbarHealPolicyTests
{
    private const TaskbarOverlayStatus Broken = TaskbarOverlayStatus.NotVisible;

    [Fact]
    public void DoesNotRebuildOnASingleBadCheck()
    {
        // One bad check can be a race with Explorer mid-restack; the readout gets another tick.
        Assert.False(TaskbarHealPolicy.ShouldRebuild(Broken, 1, msSinceLastRebuild: null));
    }

    [Fact]
    public void RebuildsOnceTheToleranceIsReached()
    {
        Assert.True(TaskbarHealPolicy.ShouldRebuild(
            Broken, TaskbarHealPolicy.UnhealthyChecksBeforeRebuild, msSinceLastRebuild: null));
    }

    [Fact]
    public void KeepsRebuildingOnceWellPastTheTolerance()
    {
        Assert.True(TaskbarHealPolicy.ShouldRebuild(Broken, 25, msSinceLastRebuild: null));
    }

    [Fact]
    public void NeverRebuildsAHealthyReadout()
    {
        // Suppression matters most: rebuilding there would fight #123 for as long as a game is open.
        Assert.False(TaskbarHealPolicy.ShouldRebuild(
            TaskbarOverlayStatus.Healthy, 99, msSinceLastRebuild: null));
        Assert.False(TaskbarHealPolicy.ShouldRebuild(
            TaskbarOverlayStatus.SuppressedForFullscreen, 99, msSinceLastRebuild: null));
    }

    [Fact]
    public void HoldsOffWhileTheRebuildCooldownIsRunning()
    {
        Assert.False(TaskbarHealPolicy.ShouldRebuild(
            Broken, 10, TaskbarHealPolicy.RebuildCooldownMs - 1));
    }

    [Fact]
    public void RebuildsAgainOnceTheCooldownHasElapsed()
    {
        Assert.True(TaskbarHealPolicy.ShouldRebuild(
            Broken, 10, TaskbarHealPolicy.RebuildCooldownMs));
    }

    [Fact]
    public void AFreshFaultEpisodeIsAlwaysLogged()
    {
        Assert.True(TaskbarHealPolicy.ShouldLogFault(msSinceLastFaultLog: null));
    }

    [Fact]
    public void ARepeatedFaultIsDampened()
    {
        Assert.False(TaskbarHealPolicy.ShouldLogFault(TaskbarHealPolicy.FaultLogIntervalMs - 1));
        Assert.True(TaskbarHealPolicy.ShouldLogFault(TaskbarHealPolicy.FaultLogIntervalMs));
    }

    [Fact]
    public void EveryRebuildAttemptStillGetsALoggedFaultInFrontOfIt()
    {
        // The fault dampener must stay finer-grained than the rebuild cooldown, or a rebuild
        // could appear in the log with no explanation of what it was for.
        Assert.True(TaskbarHealPolicy.FaultLogIntervalMs < TaskbarHealPolicy.RebuildCooldownMs);
    }

    [Fact]
    public void ANormalCheckIntervalIsNotASystemGap()
    {
        Assert.False(TaskbarHealPolicy.IsSystemGap(TaskbarHealPolicy.CheckIntervalMs));
    }

    [Fact]
    public void ALateTickIsNotASystemGap()
    {
        // A busy machine can miss a tick; that isn't evidence the process was suspended.
        Assert.False(TaskbarHealPolicy.IsSystemGap(TaskbarHealPolicy.CheckIntervalMs * 2));
    }

    [Fact]
    public void AnHourWithNoCheckIsASystemGap()
    {
        // The tick source counts through suspend, so a sleep shows up as a huge gap.
        Assert.True(TaskbarHealPolicy.IsSystemGap(60 * 60 * 1000));
    }

    [Fact]
    public void SystemGapThresholdIsInclusive()
    {
        Assert.True(TaskbarHealPolicy.IsSystemGap(
            (long)TaskbarHealPolicy.CheckIntervalMs * TaskbarHealPolicy.SystemGapFactor));
    }

    [Fact]
    public void SettleScheduleRunsThenStops()
    {
        for (var attempt = 0; attempt < TaskbarHealPolicy.SettleAttempts; attempt++)
            Assert.NotNull(TaskbarHealPolicy.SettleIntervalMs(attempt));

        Assert.Null(TaskbarHealPolicy.SettleIntervalMs(TaskbarHealPolicy.SettleAttempts));
    }

    [Fact]
    public void SettleScheduleIgnoresANegativeAttempt()
    {
        Assert.Null(TaskbarHealPolicy.SettleIntervalMs(-1));
    }

    [Fact]
    public void SettleScheduleIsFrontLoadedAndFinishesWithinAFewSeconds()
    {
        var elapsed = 0;
        var previous = 0;
        for (var attempt = 0; attempt < TaskbarHealPolicy.SettleAttempts; attempt++)
        {
            var interval = TaskbarHealPolicy.SettleIntervalMs(attempt)!.Value;
            Assert.True(interval > 0);
            // Non-decreasing: check often while healing is cheap, then back off.
            Assert.True(interval >= previous);
            previous = interval;
            elapsed += interval;
        }

        // First re-check inside a second, whole window done inside the ticket's "a few seconds".
        Assert.True(TaskbarHealPolicy.SettleIntervalMs(0)!.Value <= 1000);
        Assert.InRange(elapsed, 5_000, 15_000);
    }

    [Theory]
    [InlineData(SessionSwitchReason.SessionUnlock)]
    [InlineData(SessionSwitchReason.ConsoleConnect)]
    [InlineData(SessionSwitchReason.RemoteConnect)]
    [InlineData(SessionSwitchReason.SessionLogon)]
    public void TheDesktopComingBackTriggersAHeal(SessionSwitchReason reason)
    {
        Assert.True(TaskbarHealPolicy.IsResumeLike(reason));
    }

    [Theory]
    [InlineData(SessionSwitchReason.SessionLock)]
    [InlineData(SessionSwitchReason.ConsoleDisconnect)]
    [InlineData(SessionSwitchReason.RemoteDisconnect)]
    [InlineData(SessionSwitchReason.SessionLogoff)]
    [InlineData(SessionSwitchReason.SessionRemoteControl)]
    public void TheDesktopGoingAwayDoesNot(SessionSwitchReason reason)
    {
        // Nothing to heal while the desktop is gone — the matching reconnect covers the return.
        Assert.False(TaskbarHealPolicy.IsResumeLike(reason));
    }
}
