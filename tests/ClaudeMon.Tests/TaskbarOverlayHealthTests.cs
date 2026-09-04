namespace ClaudeMon.Tests;

using System.Drawing;
using ClaudeMon.UI;

public class TaskbarOverlayHealthTests
{
    // A 1920x1080 monitor's 48px bottom taskbar, and a readout sitting at its right end.
    private static readonly Rectangle Taskbar = new(0, 1032, 1920, 48);
    private static readonly Rectangle Readout = new(1600, 1032, 90, 48);

    /// <summary>A readout that is doing its job; each test spoils exactly one fact.</summary>
    private static TaskbarOverlayFacts Healthy() => new(
        HandleCreated: true,
        MsSinceKeepAlive: 500,
        OwnTaskbarResolved: true,
        SuppressedForFullscreen: false,
        TaskbarFound: true,
        TaskbarBounds: Taskbar,
        WindowVisible: true,
        WindowTopmost: true,
        WindowBounds: Readout,
        HasPainted: true);

    [Fact]
    public void Healthy_WhenEverythingIsInPlace()
    {
        Assert.Equal(TaskbarOverlayStatus.Healthy, TaskbarOverlayHealth.Evaluate(Healthy()));
    }

    [Fact]
    public void HandleLost_WhenTheWindowHandleIsGone()
    {
        var facts = Healthy() with { HandleCreated = false };
        Assert.Equal(TaskbarOverlayStatus.HandleLost, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Fact]
    public void KeepAliveStalled_WhenTheLoopHasNotRunInTooLong()
    {
        var facts = Healthy() with { MsSinceKeepAlive = TaskbarOverlayHealth.KeepAliveStallMs + 1 };
        Assert.Equal(TaskbarOverlayStatus.KeepAliveStalled, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Fact]
    public void Healthy_AtExactlyTheStallThreshold()
    {
        // The threshold is "more than", so a tick landing right on it is still a live loop.
        var facts = Healthy() with { MsSinceKeepAlive = TaskbarOverlayHealth.KeepAliveStallMs };
        Assert.Equal(TaskbarOverlayStatus.Healthy, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Fact]
    public void TaskbarMissing_WhenTheReadoutCannotResolveItsTaskbar()
    {
        var facts = Healthy() with { TaskbarFound = false, TaskbarBounds = Rectangle.Empty };
        Assert.Equal(TaskbarOverlayStatus.TaskbarMissing, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Theory]
    [InlineData(0, 48)]
    [InlineData(1920, 0)]
    [InlineData(0, 0)]
    public void TaskbarMissing_WhenTheTaskbarRectIsDegenerate(int width, int height)
    {
        // A found-but-zero-sized taskbar gives the placement check nothing to answer, so it is
        // reported as missing rather than turning every readout into a "misplaced" rebuild.
        var facts = Healthy() with { TaskbarBounds = new Rectangle(0, 1032, width, height) };
        Assert.Equal(TaskbarOverlayStatus.TaskbarMissing, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Fact]
    public void TaskbarMissing_WhenTheReadoutItselfCouldNotFindItsTaskbar()
    {
        // The manager's enumeration found it a moment later, but the readout hid itself on its
        // own tick. That is a readout behaving correctly, not an unexplained invisible window —
        // this is the ordering that stops an Explorer restart from triggering rebuilds.
        var facts = Healthy() with { OwnTaskbarResolved = false, WindowVisible = false };
        Assert.Equal(TaskbarOverlayStatus.TaskbarMissing, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Fact]
    public void TaskbarMissing_IsAWaitStateAndNeverRebuilds()
    {
        // A new window would have no more taskbar to sit on than this one.
        Assert.False(TaskbarOverlayHealth.NeedsRebuild(TaskbarOverlayStatus.TaskbarMissing));
    }

    [Fact]
    public void AMissingTaskbarOutranksAStaleFullscreenVerdict()
    {
        // Both flags are set by the same tick, and the taskbar branch returns before the
        // fullscreen test runs — so a suppression flag left over from a previous tick must not
        // be reported while there is no taskbar at all.
        var facts = Healthy() with { OwnTaskbarResolved = false, SuppressedForFullscreen = true };
        Assert.Equal(TaskbarOverlayStatus.TaskbarMissing, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Fact]
    public void SuppressedForFullscreen_IsReportedAndIsNotAFault()
    {
        var facts = Healthy() with { SuppressedForFullscreen = true, WindowVisible = false };
        var status = TaskbarOverlayHealth.Evaluate(facts);

        Assert.Equal(TaskbarOverlayStatus.SuppressedForFullscreen, status);
        Assert.False(TaskbarOverlayHealth.NeedsRebuild(status));
    }

    [Fact]
    public void StaleSuppressionCannotMaskADeadReadout()
    {
        // The suppression flag is only set by the keep-alive, so a frozen loop leaves it stale.
        // The liveness checks must outrank it or a stuck flag becomes a permanent "all fine".
        var facts = Healthy() with
        {
            SuppressedForFullscreen = true,
            MsSinceKeepAlive = TaskbarOverlayHealth.KeepAliveStallMs + 1,
        };
        Assert.Equal(TaskbarOverlayStatus.KeepAliveStalled, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Fact]
    public void NotVisible_WhenTheWindowIsHiddenWithNoFullscreenAppToExplainIt()
    {
        var facts = Healthy() with { WindowVisible = false };
        Assert.Equal(TaskbarOverlayStatus.NotVisible, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Fact]
    public void LostTopmost_WhenTheWindowFellOutOfTheTopmostBand()
    {
        var facts = Healthy() with { WindowTopmost = false };
        Assert.Equal(TaskbarOverlayStatus.LostTopmost, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Fact]
    public void Misplaced_WhenTheWindowIsLeftOnAMonitorThatMoved()
    {
        // Coordinates from a display layout that no longer exists — the readout is on screen,
        // just nowhere near its taskbar.
        var facts = Healthy() with { WindowBounds = new Rectangle(3000, 1400, 90, 48) };
        Assert.Equal(TaskbarOverlayStatus.Misplaced, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Fact]
    public void Misplaced_WhenTheWindowHasNoSize()
    {
        var facts = Healthy() with { WindowBounds = new Rectangle(1600, 1032, 0, 0) };
        Assert.Equal(TaskbarOverlayStatus.Misplaced, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Fact]
    public void UnreadableWindowBoundsAreNotEvidenceOfAnything()
    {
        // A failed GetWindowRect must fail toward healthy — a diagnostic that couldn't run is
        // not grounds for tearing a working readout down.
        var facts = Healthy() with { WindowBounds = null };
        Assert.Equal(TaskbarOverlayStatus.Healthy, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Fact]
    public void Healthy_WhenAUserNudgeOverhangsTheTaskbarEdge()
    {
        // The horizontal offset can legitimately push the readout past the taskbar's end;
        // partial overlap is deliberately good enough, since a rebuild wouldn't change it.
        var facts = Healthy() with { WindowBounds = new Rectangle(1880, 1032, 90, 48) };
        Assert.Equal(TaskbarOverlayStatus.Healthy, TaskbarOverlayHealth.Evaluate(facts));
    }

    [Fact]
    public void NotPainted_WhenNoContentHasEverReachedTheScreen()
    {
        var facts = Healthy() with { HasPainted = false };
        Assert.Equal(TaskbarOverlayStatus.NotPainted, TaskbarOverlayHealth.Evaluate(facts));
    }

    // The statuses are internal, so these iterate rather than using [InlineData] — an internal
    // type can't appear in the signature of the public methods xUnit discovers.
    [Fact]
    public void EveryFaultWarrantsARebuild()
    {
        // Enumerated rather than listed, so a status added later without a rebuild decision
        // fails here instead of silently becoming a state nothing ever heals.
        foreach (var status in Enum.GetValues<TaskbarOverlayStatus>())
        {
            if (status is TaskbarOverlayStatus.Healthy
                or TaskbarOverlayStatus.SuppressedForFullscreen
                or TaskbarOverlayStatus.TaskbarMissing)
                continue;

            Assert.True(TaskbarOverlayHealth.NeedsRebuild(status), $"{status} should warrant a rebuild");
        }
    }

    [Fact]
    public void TheNonFaultStatesNeverRebuild()
    {
        Assert.False(TaskbarOverlayHealth.NeedsRebuild(TaskbarOverlayStatus.Healthy));
        Assert.False(TaskbarOverlayHealth.NeedsRebuild(TaskbarOverlayStatus.SuppressedForFullscreen));
        Assert.False(TaskbarOverlayHealth.NeedsRebuild(TaskbarOverlayStatus.TaskbarMissing));
    }

    [Fact]
    public void TheStallThresholdSitsAboveTheSystemGapThreshold()
    {
        // Both timers share one message loop. Below the gap threshold a stale keep-alive means
        // the health check was starved too — the gap detector's job, not a rebuild's — and
        // rebuilding a Form whose timer runs on the same stuck thread could never help.
        Assert.True(
            TaskbarOverlayHealth.KeepAliveStallMs
                >= (long)TaskbarHealPolicy.CheckIntervalMs * TaskbarHealPolicy.SystemGapFactor,
            "the keep-alive stall threshold must not fire inside the system-gap band");
    }
}
