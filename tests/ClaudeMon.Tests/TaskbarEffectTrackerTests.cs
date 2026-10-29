namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.UI;

public class TaskbarEffectTrackerTests
{
    private const string Session = TaskbarEffectTracker.SessionKey;
    private const string Weekly = TaskbarEffectTracker.WeeklyKey;

    private static readonly DateTimeOffset SessionReset = new(2026, 10, 29, 15, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WeeklyReset = new(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

    private static TaskbarEffectReading S(UsageLevel level, double pct = 50, DateTimeOffset? resets = null) =>
        new(Session, level, pct, resets ?? SessionReset);

    private static TaskbarEffectReading W(UsageLevel level, double pct = 50, DateTimeOffset? resets = null) =>
        new(Weekly, level, pct, resets ?? WeeklyReset);

    private static TaskbarEffectDecision Observe(TaskbarEffectTracker tracker, params TaskbarEffectReading[] readings) =>
        tracker.Observe(readings);

    [Theory]
    [InlineData(UsageLevel.Green, TaskbarLoopingEffect.None)]
    [InlineData(UsageLevel.Yellow, TaskbarLoopingEffect.LightSmoke)]
    [InlineData(UsageLevel.Orange, TaskbarLoopingEffect.HeavySmoke)]
    [InlineData(UsageLevel.Red, TaskbarLoopingEffect.Fire)]
    public void EachLevelMapsToItsEffect(UsageLevel level, TaskbarLoopingEffect expected)
    {
        var decision = Observe(new TaskbarEffectTracker(), S(level));

        Assert.Equal(expected, decision.Looping);
        Assert.False(decision.PlayNuke);
    }

    [Fact]
    public void NoReadingsMeansNoEffect()
    {
        Assert.Equal(TaskbarLoopingEffect.None, Observe(new TaskbarEffectTracker()).Looping);
    }

    [Theory]
    [InlineData(UsageLevel.Orange, UsageLevel.Red, TaskbarLoopingEffect.Fire)]
    [InlineData(UsageLevel.Red, UsageLevel.Green, TaskbarLoopingEffect.Fire)]
    [InlineData(UsageLevel.Yellow, UsageLevel.Orange, TaskbarLoopingEffect.HeavySmoke)]
    [InlineData(UsageLevel.Green, UsageLevel.Yellow, TaskbarLoopingEffect.LightSmoke)]
    [InlineData(UsageLevel.Green, UsageLevel.Green, TaskbarLoopingEffect.None)]
    public void TheWorstLimitWins(UsageLevel session, UsageLevel weekly, TaskbarLoopingEffect expected)
    {
        Assert.Equal(expected, Observe(new TaskbarEffectTracker(), S(session), W(weekly)).Looping);
    }

    [Fact]
    public void DismissingLightSmokeLeavesHeavySmokeAndFireArmed()
    {
        var tracker = new TaskbarEffectTracker();
        Observe(tracker, S(UsageLevel.Yellow));

        Assert.True(tracker.Dismiss());
        Assert.Equal(TaskbarLoopingEffect.None, Observe(tracker, S(UsageLevel.Yellow)).Looping);

        Assert.Equal(TaskbarLoopingEffect.HeavySmoke, Observe(tracker, S(UsageLevel.Orange)).Looping);
        Assert.Equal(TaskbarLoopingEffect.Fire, Observe(tracker, S(UsageLevel.Red)).Looping);
    }

    [Fact]
    public void DismissingHeavySmokeSilencesBothSmokesButFireStillAppears()
    {
        var tracker = new TaskbarEffectTracker();
        Observe(tracker, S(UsageLevel.Orange));

        Assert.True(tracker.Dismiss());
        Assert.Equal(TaskbarLoopingEffect.None, tracker.Current);
        Assert.Equal(TaskbarLoopingEffect.None, Observe(tracker, S(UsageLevel.Orange)).Looping);
        Assert.Equal(TaskbarLoopingEffect.None, Observe(tracker, S(UsageLevel.Yellow)).Looping);

        Assert.Equal(TaskbarLoopingEffect.Fire, Observe(tracker, S(UsageLevel.Red)).Looping);
    }

    [Fact]
    public void DismissingFireSilencesEverythingForTheWindow()
    {
        var tracker = new TaskbarEffectTracker();
        Observe(tracker, S(UsageLevel.Red));

        Assert.True(tracker.Dismiss());

        Assert.Equal(TaskbarLoopingEffect.None, Observe(tracker, S(UsageLevel.Red)).Looping);
        Assert.Equal(TaskbarLoopingEffect.None, Observe(tracker, S(UsageLevel.Orange)).Looping);
        Assert.Equal(TaskbarLoopingEffect.None, Observe(tracker, S(UsageLevel.Yellow)).Looping);
        Assert.Equal(TaskbarLoopingEffect.None, Observe(tracker, S(UsageLevel.Red)).Looping);
    }

    [Fact]
    public void DismissingWithNothingShowingChangesNothing()
    {
        var tracker = new TaskbarEffectTracker();
        Observe(tracker, S(UsageLevel.Green));
        tracker.MarkSaved();

        Assert.False(tracker.Dismiss());
        Assert.False(tracker.HasUnsavedChanges);

        // Nothing was silenced, so a later yellow still smokes.
        Assert.Equal(TaskbarLoopingEffect.LightSmoke, Observe(tracker, S(UsageLevel.Yellow)).Looping);
    }

    [Fact]
    public void DismissSilencesEveryContributingLimit_NotJustTheWorst()
    {
        // Session on fire, weekly smoking: one click must not reveal the weekly smoke.
        var tracker = new TaskbarEffectTracker();
        Observe(tracker, S(UsageLevel.Red), W(UsageLevel.Orange));

        tracker.Dismiss();

        Assert.Equal(TaskbarLoopingEffect.None, tracker.Current);
        // The weekly only had smoke dismissed — it can still catch fire.
        Assert.Equal(TaskbarLoopingEffect.Fire, Observe(tracker, S(UsageLevel.Red), W(UsageLevel.Red)).Looping);
    }

    [Fact]
    public void AResetReArmsADismissedEffect()
    {
        var tracker = new TaskbarEffectTracker();
        Observe(tracker, S(UsageLevel.Red));
        tracker.Dismiss();

        var next = SessionReset.AddHours(5);
        Assert.Equal(TaskbarLoopingEffect.Fire, Observe(tracker, S(UsageLevel.Red, resets: next)).Looping);
    }

    [Fact]
    public void ResetsAtJitterIsNotAReset()
    {
        var tracker = new TaskbarEffectTracker();
        Observe(tracker, S(UsageLevel.Orange));
        tracker.Dismiss();

        // The API's resets_at wobbles by seconds between polls within one window.
        var jittered = SessionReset.AddSeconds(30);
        Assert.Equal(TaskbarLoopingEffect.None, Observe(tracker, S(UsageLevel.Orange, resets: jittered)).Looping);
    }

    [Fact]
    public void AnUnknownResetTimeIsNotAReset()
    {
        var tracker = new TaskbarEffectTracker();
        Observe(tracker, S(UsageLevel.Red, pct: 100));
        tracker.Dismiss();

        var unknown = Observe(tracker, new TaskbarEffectReading(Session, UsageLevel.Red, 100, null));
        Assert.Equal(TaskbarLoopingEffect.None, unknown.Looping);
        Assert.False(unknown.PlayNuke);

        var back = Observe(tracker, S(UsageLevel.Red, pct: 100));
        Assert.Equal(TaskbarLoopingEffect.None, back.Looping);
        Assert.False(back.PlayNuke);
    }

    [Fact]
    public void OneLimitResettingDoesNotReArmTheOther()
    {
        var tracker = new TaskbarEffectTracker();
        Observe(tracker, S(UsageLevel.Red), W(UsageLevel.Red));
        tracker.Dismiss();

        var decision = Observe(tracker, S(UsageLevel.Green, resets: SessionReset.AddHours(5)), W(UsageLevel.Red));
        Assert.Equal(TaskbarLoopingEffect.None, decision.Looping);
    }

    [Fact]
    public void TheNukePlaysOncePerWindow()
    {
        var tracker = new TaskbarEffectTracker();

        Assert.False(Observe(tracker, S(UsageLevel.Red, pct: 99)).PlayNuke);
        Assert.True(Observe(tracker, S(UsageLevel.Red, pct: 100)).PlayNuke);
        Assert.False(Observe(tracker, S(UsageLevel.Red, pct: 100)).PlayNuke);
        Assert.False(Observe(tracker, S(UsageLevel.Red, pct: 100)).PlayNuke);
    }

    [Fact]
    public void FireCarriesOnAfterTheNuke()
    {
        var decision = Observe(new TaskbarEffectTracker(), S(UsageLevel.Red, pct: 100));

        Assert.True(decision.PlayNuke);
        Assert.Equal(TaskbarLoopingEffect.Fire, decision.Looping);
    }

    [Fact]
    public void TheNukePlaysOncePerLimit()
    {
        var tracker = new TaskbarEffectTracker();

        Assert.True(Observe(tracker, S(UsageLevel.Red, pct: 100), W(UsageLevel.Yellow, pct: 70)).PlayNuke);
        Assert.True(Observe(tracker, S(UsageLevel.Red, pct: 100), W(UsageLevel.Red, pct: 100)).PlayNuke);
        Assert.False(Observe(tracker, S(UsageLevel.Red, pct: 100), W(UsageLevel.Red, pct: 100)).PlayNuke);
    }

    [Fact]
    public void TheNukeReArmsAfterAReset()
    {
        var tracker = new TaskbarEffectTracker();
        Assert.True(Observe(tracker, S(UsageLevel.Red, pct: 100)).PlayNuke);

        Assert.True(Observe(tracker, S(UsageLevel.Red, pct: 100, resets: SessionReset.AddHours(5))).PlayNuke);
    }

    [Fact]
    public void RestoredStateDoesNotReplayTheNukeInTheSameWindow()
    {
        var first = new TaskbarEffectTracker();
        Assert.True(Observe(first, S(UsageLevel.Red, pct: 100)).PlayNuke);
        first.Dismiss();

        // An app restart: the saved state comes back through the settings file.
        var restarted = new TaskbarEffectTracker(first.GetState());
        var decision = Observe(restarted, S(UsageLevel.Red, pct: 100));

        Assert.False(decision.PlayNuke);
        Assert.Equal(TaskbarLoopingEffect.None, decision.Looping);
    }

    [Fact]
    public void RestoredStateFromAnOldWindowReArms()
    {
        var first = new TaskbarEffectTracker();
        Observe(first, S(UsageLevel.Red, pct: 100));
        first.Dismiss();

        var restarted = new TaskbarEffectTracker(first.GetState());
        var decision = Observe(restarted, S(UsageLevel.Red, pct: 100, resets: SessionReset.AddHours(5)));

        Assert.True(decision.PlayNuke);
        Assert.Equal(TaskbarLoopingEffect.Fire, decision.Looping);
    }

    [Fact]
    public void ALimitMissingFromAPollStopsContributingButKeepsItsMemory()
    {
        var tracker = new TaskbarEffectTracker();
        Observe(tracker, S(UsageLevel.Green), W(UsageLevel.Red, pct: 100));
        tracker.Dismiss();

        Assert.Equal(TaskbarLoopingEffect.None, Observe(tracker, S(UsageLevel.Green)).Looping);

        var back = Observe(tracker, S(UsageLevel.Green), W(UsageLevel.Red, pct: 100));
        Assert.Equal(TaskbarLoopingEffect.None, back.Looping);
        Assert.False(back.PlayNuke);
    }

    [Fact]
    public void AfterAnEmptyObservationAClickDismissesNothing()
    {
        // Signed out / no reading: the effect is off screen, so a click (likely — the user is
        // poking at "sign-in expired") must not silently dismiss it for the rest of the window.
        var tracker = new TaskbarEffectTracker();
        Observe(tracker, S(UsageLevel.Orange));
        Observe(tracker);

        Assert.False(tracker.Dismiss());
        Assert.Equal(TaskbarLoopingEffect.HeavySmoke, Observe(tracker, S(UsageLevel.Orange)).Looping);
    }

    [Fact]
    public void OnlyPersistableChangesNeedSaving()
    {
        var tracker = new TaskbarEffectTracker();
        Observe(tracker, S(UsageLevel.Green));
        Assert.True(tracker.HasUnsavedChanges); // a new limit's window was recorded
        tracker.MarkSaved();

        // A level change alone is re-derived every poll — nothing to write.
        Observe(tracker, S(UsageLevel.Orange));
        Assert.False(tracker.HasUnsavedChanges);

        tracker.Dismiss();
        Assert.True(tracker.HasUnsavedChanges);
        tracker.MarkSaved();

        Observe(tracker, S(UsageLevel.Red, pct: 100));
        Assert.True(tracker.HasUnsavedChanges); // the nuke played
    }
}
