namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.UI;

public class TaskbarEffectInputsTests
{
    // Half-way through each window, so pace ratio = 2 × utilisation / 100.
    private static readonly DateTimeOffset SessionReset = DateTimeOffset.UtcNow + TimeSpan.FromHours(2.5);
    private static readonly DateTimeOffset WeeklyReset = DateTimeOffset.UtcNow + TimeSpan.FromDays(3.5);

    private static UsageResponse Usage(double session, double weekly, IReadOnlyList<UsageLimit>? limits = null) =>
        new(new UsageBucket(session, SessionReset), new UsageBucket(weekly, WeeklyReset), limits);

    private static TaskbarEffectReading Find(IReadOnlyList<TaskbarEffectReading> readings, string key) =>
        Assert.Single(readings, r => r.Key == key);

    [Fact]
    public void ReadsSessionAndWeeklyFromTheLegacyFields()
    {
        var readings = TaskbarEffectInputs.From(Usage(42, 17), UsageColorMode.Level);

        Assert.Equal(2, readings.Count);
        var session = Find(readings, TaskbarEffectTracker.SessionKey);
        Assert.Equal(42, session.Percent);
        Assert.Equal(SessionReset, session.ResetsAt);
        Assert.Equal(17, Find(readings, TaskbarEffectTracker.WeeklyKey).Percent);
    }

    [Fact]
    public void LevelModeClassifiesByAbsolutePercent()
    {
        var readings = TaskbarEffectInputs.From(Usage(85, 95), UsageColorMode.Level);

        Assert.Equal(UsageLevel.Orange, Find(readings, TaskbarEffectTracker.SessionKey).Level);
        Assert.Equal(UsageLevel.Red, Find(readings, TaskbarEffectTracker.WeeklyKey).Level);
    }

    [Fact]
    public void PaceModeClassifiesByPace()
    {
        // 70% half-way through the window is a pace ratio of 1.4 (orange), while as a level it's
        // yellow — the modes disagree, so this pins which one is used.
        var pace = TaskbarEffectInputs.From(Usage(70, 10), UsageColorMode.Pace);
        var level = TaskbarEffectInputs.From(Usage(70, 10), UsageColorMode.Level);

        Assert.Equal(UsageLevel.Orange, Find(pace, TaskbarEffectTracker.SessionKey).Level);
        Assert.Equal(UsageLevel.Yellow, Find(level, TaskbarEffectTracker.SessionKey).Level);
    }

    [Fact]
    public void TheApiSeverityIsAFloor()
    {
        var limits = new[]
        {
            new UsageLimit("session", "session", 25, "warning", SessionReset),
            new UsageLimit("weekly_all", "weekly", 25, "critical", WeeklyReset),
        };

        var readings = TaskbarEffectInputs.From(Usage(25, 25, limits), UsageColorMode.Level);

        Assert.Equal(UsageLevel.Orange, Find(readings, TaskbarEffectTracker.SessionKey).Level);
        Assert.Equal(UsageLevel.Red, Find(readings, TaskbarEffectTracker.WeeklyKey).Level);
    }

    [Fact]
    public void ScopedWeeklyCapsAreNotJudged()
    {
        var limits = new[]
        {
            new UsageLimit("weekly_scoped", "weekly", 99, "critical", WeeklyReset,
                Scope: new LimitScope(new LimitScopeModel("Opus"))),
        };

        var readings = TaskbarEffectInputs.From(Usage(10, 10, limits), UsageColorMode.Level);

        Assert.All(readings, r => Assert.Equal(UsageLevel.Green, r.Level));
    }

    [Fact]
    public void LimitsStandInForMissingLegacyFields()
    {
        var limits = new[] { new UsageLimit("session", "session", 92, "normal", SessionReset) };

        var readings = TaskbarEffectInputs.From(new UsageResponse(null, null, limits), UsageColorMode.Level);

        var session = Assert.Single(readings);
        Assert.Equal(TaskbarEffectTracker.SessionKey, session.Key);
        Assert.Equal(92, session.Percent);
        Assert.Equal(UsageLevel.Red, session.Level);
    }

    [Fact]
    public void AnEndedWindowIsNotJudged()
    {
        // The API keeps the old, past resets_at until new usage opens a window. In pace mode the
        // level would fall back to absolute (95% → red) the instant the window ended.
        var ended = new UsageResponse(
            new UsageBucket(95, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1)),
            new UsageBucket(10, WeeklyReset));

        var readings = TaskbarEffectInputs.From(ended, UsageColorMode.Pace);

        Assert.Equal(TaskbarEffectTracker.WeeklyKey, Assert.Single(readings).Key);
    }

    [Fact]
    public void AModelScopedSessionEntryDoesNotLendItsSeverity()
    {
        var limits = new[]
        {
            new UsageLimit("session", "session", 25, "critical", SessionReset,
                Scope: new LimitScope(new LimitScopeModel("Opus"))),
        };

        var readings = TaskbarEffectInputs.From(Usage(25, 25, limits), UsageColorMode.Level);

        Assert.Equal(UsageLevel.Green, Find(readings, TaskbarEffectTracker.SessionKey).Level);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(19.9)]
    public void BelowTwentyPercentNothingTriggers(double pct)
    {
        // Early in a window: 5% used a few minutes in is a wild pace ratio (red), and the API may
        // even flag it — still no effect until a fifth of the limit is gone.
        var early = new UsageResponse(
            new UsageBucket(pct, DateTimeOffset.UtcNow + TimeSpan.FromHours(4.9)),
            new UsageBucket(pct, WeeklyReset),
            [new UsageLimit("session", "session", pct, "critical", null)]);

        var readings = TaskbarEffectInputs.From(early, UsageColorMode.Pace);

        Assert.Equal(2, readings.Count); // still tracked, for resets
        Assert.All(readings, r => Assert.Equal(UsageLevel.Green, r.Level));
    }

    [Fact]
    public void FromTwentyPercentTheColourLogicApplies()
    {
        // 20% a few minutes into the window is far ahead of pace.
        var usage = new UsageResponse(
            new UsageBucket(20, DateTimeOffset.UtcNow + TimeSpan.FromHours(4.9)), null);

        var session = Assert.Single(TaskbarEffectInputs.From(usage, UsageColorMode.Pace));

        Assert.Equal(UsageLevel.Red, session.Level);
    }

    [Fact]
    public void NoBucketsMeansNoReadings()
    {
        Assert.Empty(TaskbarEffectInputs.From(new UsageResponse(null, null), UsageColorMode.Pace));
    }
}
