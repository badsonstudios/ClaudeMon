namespace ClaudeMon.Tests;

using System.Text.Json;
using ClaudeMon.Models;
using ClaudeMon.UI;

public class TaskbarMetricCycleTests
{
    [Theory]
    [InlineData(TaskbarMetric.Session, TaskbarMetric.Weekly)]
    [InlineData(TaskbarMetric.Weekly, TaskbarMetric.TimeToLimit)]
    [InlineData(TaskbarMetric.TimeToLimit, TaskbarMetric.TimeToReset)]
    [InlineData(TaskbarMetric.TimeToReset, TaskbarMetric.Session)]  // wraps
    public void NextMetric_Numbers_AdvancesThroughTheRing(TaskbarMetric from, TaskbarMetric expected)
    {
        Assert.Equal(expected, Next(from, TaskbarStyle.Numbers));
    }

    [Fact]
    public void NextMetric_Numbers_FourClicksReturnToTheStart()
    {
        // The acceptance criterion is "cycles through the metrics and wraps" — walk the whole
        // ring rather than trusting the pairwise steps above to compose.
        var selection = TaskbarMetricSelection.For(TaskbarMetric.Session);
        var seen = new List<TaskbarMetric>();
        for (var i = 0; i < 4; i++)
        {
            selection = Cycled(selection, TaskbarStyle.Numbers);
            var single = TaskbarMetricCycle.Current(selection, TaskbarStyle.Numbers);
            Assert.NotNull(single);
            seen.Add(single.Value);
        }

        Assert.Equal(
            new[]
            {
                TaskbarMetric.Weekly, TaskbarMetric.TimeToLimit,
                TaskbarMetric.TimeToReset, TaskbarMetric.Session,
            },
            seen);
    }

    [Theory]
    [InlineData(TaskbarMetric.Session, TaskbarMetric.Weekly)]
    [InlineData(TaskbarMetric.Weekly, TaskbarMetric.Session)]  // wraps after two, not four
    public void NextMetric_Bar_CyclesOnlyTheMetricsABarCanDraw(TaskbarMetric from, TaskbarMetric expected)
    {
        Assert.Equal(expected, Next(from, TaskbarStyle.Bar));
    }

    [Theory]
    [InlineData(TaskbarMetric.TimeToLimit)]
    [InlineData(TaskbarMetric.TimeToReset)]
    public void NextMetric_Bar_FromAMetricItCannotDraw_RestartsTheRing(TaskbarMetric from)
    {
        // Reachable by picking a time element in Settings and then switching to the bar style:
        // the gesture must not dead-end on a metric this style has no way to show.
        Assert.Equal(TaskbarMetric.Session, Next(from, TaskbarStyle.Bar));
    }

    [Fact]
    public void NextMetric_Composition_CollapsesToItsFirstMetricBeforeAdvancing()
    {
        // Session + weekly has no position in the ring, so the first click focuses the
        // leftmost element already on screen instead of skipping past it...
        var composed = new TaskbarMetricSelection(
            Session: true, Weekly: true, TimeToLimit: false, TimeToReset: false);
        Assert.Equal(TaskbarMetric.Session, TaskbarMetricCycle.NextMetric(composed, TaskbarStyle.Numbers));

        // ...and the click after that advances normally.
        var collapsed = Cycled(composed, TaskbarStyle.Numbers);
        Assert.Equal(TaskbarMetric.Weekly, TaskbarMetricCycle.NextMetric(collapsed, TaskbarStyle.Numbers));
    }

    [Fact]
    public void NextMetric_Composition_WithoutTheFirstRingMetric_CollapsesToTheNextOneShown()
    {
        var composed = new TaskbarMetricSelection(
            Session: false, Weekly: false, TimeToLimit: true, TimeToReset: true);
        Assert.Equal(
            TaskbarMetric.TimeToLimit, TaskbarMetricCycle.NextMetric(composed, TaskbarStyle.Numbers));
    }

    [Fact]
    public void NextMetric_UnderBar_IgnoresMetricsOutsideItsRing()
    {
        // Weekly + the countdown: the bar is drawing one bar, so it IS at the weekly position
        // and must advance from there. Treating this as a composition would collapse back onto
        // weekly and the gesture would never move.
        var composed = new TaskbarMetricSelection(
            Session: false, Weekly: true, TimeToLimit: false, TimeToReset: true);
        Assert.Equal(TaskbarMetric.Session, TaskbarMetricCycle.NextMetric(composed, TaskbarStyle.Bar));
        Assert.Equal(TaskbarMetric.Weekly, TaskbarMetricCycle.Current(composed, TaskbarStyle.Bar));
    }

    [Fact]
    public void Next_UnderBar_LeavesTheMetricsTheBarCannotDrawAlone()
    {
        // Settings hides the two time rows in Bar mode, so a gesture that can't show them must
        // not switch them off either — flipping back to Numbers should find the composition
        // intact, with only the cycled bar changed.
        var composed = new TaskbarMetricSelection(
            Session: true, Weekly: false, TimeToLimit: true, TimeToReset: true);

        var next = Cycled(composed, TaskbarStyle.Bar);

        Assert.False(next.Session);
        Assert.True(next.Weekly);
        Assert.True(next.TimeToLimit);
        Assert.True(next.TimeToReset);
    }

    [Fact]
    public void Next_UnderBar_KeepsAdvancingRatherThanStalling()
    {
        // The regression the ring-scoped Current guards against: with a Numbers-only metric
        // also on, repeated clicks must still walk session ↔ weekly instead of sticking.
        var selection = new TaskbarMetricSelection(
            Session: true, Weekly: false, TimeToLimit: false, TimeToReset: true);

        selection = Cycled(selection, TaskbarStyle.Bar);
        Assert.True(selection.Weekly);

        selection = Cycled(selection, TaskbarStyle.Bar);
        Assert.True(selection.Session);
        Assert.False(selection.Weekly);
    }

    [Fact]
    public void NextMetric_NothingSelected_StartsAtSession()
    {
        // A hand-edited config can turn every toggle off; the readout falls back to session,
        // so the cycle has to agree with what is actually on screen.
        Assert.Equal(
            TaskbarMetric.Session,
            TaskbarMetricCycle.NextMetric(default, TaskbarStyle.Numbers));
    }

    [Fact]
    public void Next_UnderNumbers_AlwaysProducesASingleMetric()
    {
        // The Numbers ring covers every metric, so there is nothing left outside it to preserve.
        var composed = new TaskbarMetricSelection(
            Session: true, Weekly: true, TimeToLimit: true, TimeToReset: true);
        var next = Cycled(composed, TaskbarStyle.Numbers);

        Assert.Equal(1, next.Count);
        Assert.NotNull(TaskbarMetricCycle.Current(next, TaskbarStyle.Numbers));
    }

    [Fact]
    public void Step_ACompositionBecomesHomeOnTheClickThatCollapsesIt()
    {
        // The bug as reported (#156): session % + time-to-reset, middle-clicked once. Collapsing
        // onto the leftmost element is still right — but the click that takes the composition
        // away is also the click that has to remember it, or there is a saved state in which it
        // is gone and unrecorded.
        var composed = new TaskbarMetricSelection(
            Session: true, Weekly: false, TimeToLimit: false, TimeToReset: true);

        var step = TaskbarMetricCycle.Step(composed, home: null, TaskbarStyle.Numbers);

        Assert.Equal(TaskbarMetricSelection.For(TaskbarMetric.Session), step.Metrics);
        Assert.Equal(composed, step.Home);
        Assert.Equal("session", step.Label);
    }

    [Fact]
    public void Step_AFullLapRestoresTheComposition_WhereTheHomelessRingDestroyedIt()
    {
        var composed = new TaskbarMetricSelection(
            Session: true, Weekly: false, TimeToLimit: false, TimeToReset: true);

        var selection = composed;
        TaskbarMetricSelection? home = null;
        var labels = new List<string>();
        // Two laps, not one: a ring is only a ring if it keeps going round, and the second lap
        // starts from a restored home rather than the original toggles.
        for (var i = 0; i < 10; i++)
        {
            var step = TaskbarMetricCycle.Step(selection, home, TaskbarStyle.Numbers);
            selection = step.Metrics;
            home = step.Home;
            labels.Add(step.Label);
        }

        // The whole ring: focus each metric in turn, then land back on what you had.
        var lap = new[] { "session", "weekly", "to limit", "resets", "custom" };
        Assert.Equal(lap.Concat(lap), labels);
        Assert.Equal(composed, selection);
        Assert.Equal(composed, home);

        // One lap with nothing remembered — precisely the ring as it behaved before this fix —
        // leaves you on session alone, which is the loss that was reported.
        var homeless = composed;
        for (var i = 0; i < 5; i++)
            homeless = Cycled(homeless, TaskbarStyle.Numbers);

        Assert.Equal(TaskbarMetricSelection.For(TaskbarMetric.Session), homeless);
    }

    [Fact]
    public void Step_ACompositionNotLedBySession_StillReachesEveryMetric()
    {
        // The wrap is one step before where the run started, not a fixed place in the ring: a
        // composition led by weekly must not cost you the session stop for the rest of time.
        var composed = new TaskbarMetricSelection(
            Session: false, Weekly: true, TimeToLimit: false, TimeToReset: true);

        var selection = composed;
        TaskbarMetricSelection? home = null;
        var labels = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var step = TaskbarMetricCycle.Step(selection, home, TaskbarStyle.Numbers);
            selection = step.Metrics;
            home = step.Home;
            labels.Add(step.Label);
        }

        Assert.Equal(new[] { "weekly", "to limit", "resets", "session", "custom" }, labels);
        Assert.Equal(composed, selection);
    }

    [Fact]
    public void Step_ResumesACycleFromThePersistedHome()
    {
        // Restarting mid-cycle leaves nothing behind but the two saved values, and Step is a pure
        // function of them — so the wrap still lands on the composition saved before the restart.
        var composed = new TaskbarMetricSelection(
            Session: true, Weekly: false, TimeToLimit: false, TimeToReset: true);

        var step = TaskbarMetricCycle.Step(
            TaskbarMetricSelection.For(TaskbarMetric.TimeToReset), composed, TaskbarStyle.Numbers);

        Assert.Equal(composed, step.Metrics);
        Assert.Equal(composed, step.Home);
        Assert.Equal(TaskbarMetricCycle.HomeLabel, step.Label);
    }

    [Fact]
    public void Step_FromASingleMetric_KeepsTheFourStopRingAndRemembersNothing()
    {
        // A readout the ring can already reach is not a composition to protect: no fifth stop,
        // no remembered home, and four clicks still return you to where you started.
        var selection = TaskbarMetricSelection.SessionOnly;
        TaskbarMetricSelection? home = null;
        for (var i = 0; i < 4; i++)
        {
            var step = TaskbarMetricCycle.Step(selection, home, TaskbarStyle.Numbers);
            selection = step.Metrics;
            home = step.Home;
            Assert.Null(home);
            Assert.Equal(1, selection.Count);
        }

        Assert.Equal(TaskbarMetricSelection.SessionOnly, selection);
    }

    [Fact]
    public void Step_ReAnchorsHomeOnWhateverCompositionIsActuallyOnScreen()
    {
        // Whatever put a composition into the toggles, it is what the user is looking at, so it
        // outranks a remembered value that no longer matches — the gesture heals itself rather
        // than restoring something that was never on screen.
        var stale = new TaskbarMetricSelection(
            Session: true, Weekly: true, TimeToLimit: false, TimeToReset: false);
        var onScreen = new TaskbarMetricSelection(
            Session: false, Weekly: true, TimeToLimit: true, TimeToReset: false);

        var step = TaskbarMetricCycle.Step(onScreen, stale, TaskbarStyle.Numbers);

        Assert.Equal(onScreen, step.Home);
        Assert.Equal(TaskbarMetricSelection.For(TaskbarMetric.Weekly), step.Metrics);
    }

    [Fact]
    public void Step_IgnoresARememberedHomeTheRingCanAlreadyReach()
    {
        // A hand-edited config could name a single metric as home. Honouring it would put a
        // second, identical-looking "session" stop on the ring; it is left stored (harmless, and
        // Settings rewrites it on the next save) but skipped.
        var step = TaskbarMetricCycle.Step(
            TaskbarMetricSelection.For(TaskbarMetric.TimeToReset),
            TaskbarMetricSelection.SessionOnly,
            TaskbarStyle.Numbers);

        Assert.Equal(TaskbarMetricSelection.For(TaskbarMetric.Session), step.Metrics);
        Assert.Equal("session", step.Label);
        Assert.Equal(TaskbarMetricSelection.SessionOnly, step.Home);
    }

    [Fact]
    public void Step_UnderBar_FromAReadoutWithNoBarAtAll_StartsTheRing()
    {
        // Only the two metrics the bar can't draw are on (pick them in Settings under Numbers,
        // then switch style): the readout is falling back to the session bar, and the gesture
        // has to agree with what is on screen rather than dead-ending.
        var timeOnly = new TaskbarMetricSelection(
            Session: false, Weekly: false, TimeToLimit: true, TimeToReset: true);

        var step = TaskbarMetricCycle.Step(timeOnly, home: null, TaskbarStyle.Bar);

        Assert.Equal(
            timeOnly with { Session = true }, step.Metrics);   // the time toggles are left alone
        Assert.Equal("session", step.Label);
        Assert.Null(step.Home);
    }

    [Fact]
    public void Step_UnderBar_RestoringHome_LeavesTheMetricsTheBarCannotDrawAlone()
    {
        // The wrap obeys the same rule as every other step: the bar owns its two metrics, and the
        // time toggles keep whatever they are set to now rather than the stale copy inside a
        // remembered home. Only reachable from a hand-edited config, but the invariant is the
        // point — no gesture may write a toggle the current style's Settings page doesn't show.
        var staleHome = new TaskbarMetricSelection(
            Session: true, Weekly: true, TimeToLimit: false, TimeToReset: false);
        var current = new TaskbarMetricSelection(
            Session: false, Weekly: true, TimeToLimit: true, TimeToReset: true);

        var step = TaskbarMetricCycle.Step(current, staleHome, TaskbarStyle.Bar);

        Assert.Equal(TaskbarMetricCycle.HomeLabel, step.Label);
        Assert.Equal(
            new TaskbarMetricSelection(
                Session: true, Weekly: true, TimeToLimit: true, TimeToReset: true),
            step.Metrics);
        Assert.Equal(step.Metrics, step.Home);
    }

    [Fact]
    public void Step_NothingSelected_StartsTheRingAndKeepsTheRememberedHome()
    {
        var composed = new TaskbarMetricSelection(
            Session: true, Weekly: false, TimeToLimit: false, TimeToReset: true);

        var step = TaskbarMetricCycle.Step(default, composed, TaskbarStyle.Numbers);

        Assert.Equal(TaskbarMetricSelection.For(TaskbarMetric.Session), step.Metrics);
        Assert.Equal(composed, step.Home);
        Assert.Equal("session", step.Label);
    }

    [Fact]
    public void Step_UnderBar_GivesATwoBarCompositionItsOwnStop()
    {
        // Same principle over the bar's two-metric ring: both bars at once is a readout the ring
        // has no stop for, so it becomes home and three clicks bring it back.
        var bothBars = new TaskbarMetricSelection(
            Session: true, Weekly: true, TimeToLimit: false, TimeToReset: false);

        var selection = bothBars;
        TaskbarMetricSelection? home = null;
        var labels = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var step = TaskbarMetricCycle.Step(selection, home, TaskbarStyle.Bar);
            selection = step.Metrics;
            home = step.Home;
            labels.Add(step.Label);
        }

        Assert.Equal(new[] { "session", "weekly", "custom" }, labels);
        Assert.Equal(bothBars, selection);
    }

    [Fact]
    public void Step_UnderBar_KeepsANumbersCompositionAsHomeWithoutGivingItAStop()
    {
        // Switching style mid-cycle: the bar can only draw one metric of this home, so a stop for
        // it would look identical to the session stop. It is kept, not dropped — the Numbers
        // style can draw it, and that is where it gets its stop back.
        var composed = new TaskbarMetricSelection(
            Session: true, Weekly: false, TimeToLimit: false, TimeToReset: true);

        var selection = TaskbarMetricSelection.For(TaskbarMetric.Session);
        TaskbarMetricSelection? home = composed;
        for (var i = 0; i < 2; i++)
        {
            var step = TaskbarMetricCycle.Step(selection, home, TaskbarStyle.Bar);
            selection = step.Metrics;
            home = step.Home;
            Assert.Equal(composed, home);
            Assert.NotEqual(TaskbarMetricCycle.HomeLabel, step.Label);
        }

        // Back under Numbers the stop is there again: session → weekly → to limit → resets → home.
        Assert.Equal(TaskbarMetric.Session, TaskbarMetricCycle.Current(selection, TaskbarStyle.Bar));
        var wrapped = TaskbarMetricCycle.Step(
            TaskbarMetricSelection.For(TaskbarMetric.TimeToReset), home, TaskbarStyle.Numbers);
        Assert.Equal(composed, wrapped.Metrics);
    }

    [Fact]
    public void HomeFor_RemembersOnlyTheReadoutsTheRingCannotReach()
    {
        var composed = new TaskbarMetricSelection(
            Session: true, Weekly: false, TimeToLimit: false, TimeToReset: true);

        Assert.Equal(composed, TaskbarMetricCycle.HomeFor(composed, TaskbarStyle.Numbers));
        Assert.Null(TaskbarMetricCycle.HomeFor(TaskbarMetricSelection.SessionOnly, TaskbarStyle.Numbers));
        Assert.Null(TaskbarMetricCycle.HomeFor(default, TaskbarStyle.Numbers));

        // The bar judges by the bars it can draw: the same composition is one bar plus two
        // metrics it has no way to show, which is what its session stop already looks like.
        Assert.Null(TaskbarMetricCycle.HomeFor(composed, TaskbarStyle.Bar));
        var bothBars = new TaskbarMetricSelection(
            Session: true, Weekly: true, TimeToLimit: false, TimeToReset: false);
        Assert.Equal(bothBars, TaskbarMetricCycle.HomeFor(bothBars, TaskbarStyle.Bar));
    }

    [Fact]
    public void HomeLabel_IsDistinctFromEveryMetricName()
    {
        // The flash is the only feedback the gesture has: "back to yours" must not read as a
        // metric you never picked.
        Assert.DoesNotContain(
            TaskbarMetricCycle.HomeLabel,
            Enum.GetValues<TaskbarMetric>().Select(TaskbarMetricCycle.Label));
    }

    [Fact]
    public void Current_IsNullForCompositionsAndEmptySelections()
    {
        Assert.Null(TaskbarMetricCycle.Current(default, TaskbarStyle.Numbers));
        Assert.Null(TaskbarMetricCycle.Current(
            new TaskbarMetricSelection(true, true, false, false), TaskbarStyle.Numbers));
    }

    [Theory]
    [InlineData(TaskbarMetric.Session)]
    [InlineData(TaskbarMetric.Weekly)]
    [InlineData(TaskbarMetric.TimeToLimit)]
    [InlineData(TaskbarMetric.TimeToReset)]
    public void Current_RoundTripsEverySingleMetricSelection(TaskbarMetric metric)
    {
        Assert.Equal(metric, TaskbarMetricCycle.Current(TaskbarMetricSelection.For(metric), TaskbarStyle.Numbers));
    }

    [Fact]
    public void Select_TurnsOffOnlyTheOtherRingMetrics()
    {
        var all = new TaskbarMetricSelection(true, true, true, true);

        Assert.Equal(
            TaskbarMetricSelection.For(TaskbarMetric.Weekly),
            TaskbarMetricCycle.Select(all, TaskbarMetric.Weekly, TaskbarStyle.Numbers));

        Assert.Equal(
            new TaskbarMetricSelection(false, true, true, true),
            TaskbarMetricCycle.Select(all, TaskbarMetric.Weekly, TaskbarStyle.Bar));
    }

    [Fact]
    public void Selection_With_TogglesOneMetricAndLeavesTheRest()
    {
        var selection = TaskbarMetricSelection.SessionOnly.With(TaskbarMetric.TimeToReset, true);
        Assert.Equal(new TaskbarMetricSelection(true, false, false, true), selection);

        Assert.Equal(
            TaskbarMetricSelection.SessionOnly, selection.With(TaskbarMetric.TimeToReset, false));

        // An undefined value is a no-op rather than a throw or a silent wrong write.
        Assert.Equal(selection, selection.With((TaskbarMetric)99, true));
    }

    [Fact]
    public void Ring_Numbers_HoldsEveryMetricExactlyOnce()
    {
        var ring = TaskbarMetricCycle.Ring(TaskbarStyle.Numbers);
        Assert.Equal(Enum.GetValues<TaskbarMetric>(), ring);
    }

    [Fact]
    public void Ring_Bar_IsTheDrawableSubset()
    {
        Assert.Equal(
            new[] { TaskbarMetric.Session, TaskbarMetric.Weekly },
            TaskbarMetricCycle.Ring(TaskbarStyle.Bar));
    }

    [Theory]
    [InlineData(TaskbarMetric.Session, "session")]
    [InlineData(TaskbarMetric.Weekly, "weekly")]
    [InlineData(TaskbarMetric.TimeToLimit, "to limit")]
    [InlineData(TaskbarMetric.TimeToReset, "resets")]
    public void Label_NamesTheMetric(TaskbarMetric metric, string expected)
    {
        Assert.Equal(expected, TaskbarMetricCycle.Label(metric));
    }

    [Fact]
    public void Label_IsDistinctPerMetric()
    {
        // The flash is the whole discoverability story — two metrics sharing a name would
        // make the gesture look broken.
        var labels = Enum.GetValues<TaskbarMetric>().Select(TaskbarMetricCycle.Label).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Fact]
    public void Selection_For_ShowsOnlyThatMetric()
    {
        var selection = TaskbarMetricSelection.For(TaskbarMetric.Weekly);

        Assert.True(selection.Shows(TaskbarMetric.Weekly));
        Assert.False(selection.Shows(TaskbarMetric.Session));
        Assert.False(selection.Shows(TaskbarMetric.TimeToLimit));
        Assert.False(selection.Shows(TaskbarMetric.TimeToReset));
        Assert.Equal(1, selection.Count);
    }

    [Fact]
    public void OutOfRangeMetric_DegradesInsteadOfThrowing()
    {
        // Nothing writes an undefined value today, but both lookups are total by construction —
        // a click gesture must never be able to take the app down.
        var bogus = (TaskbarMetric)99;
        Assert.False(TaskbarMetricSelection.For(bogus).Shows(bogus));
        Assert.Equal("session", TaskbarMetricCycle.Label(bogus));
    }

    [Fact]
    public void Selection_SerializesWithTheSameCamelCaseKeysAsTheRestOfTheConfig()
    {
        // A whole selection is persisted as TaskbarDisplaySettings.CycleHome (#156), into a file
        // that is camelCase throughout — a hand edit shouldn't meet four PascalCase outliers.
        Assert.Equal(
            """{"session":true,"weekly":false,"timeToLimit":false,"timeToReset":true}""",
            JsonSerializer.Serialize(new TaskbarMetricSelection(true, false, false, true)));
    }

    [Fact]
    public void Selection_SessionOnly_IsTheDefaultReadout()
    {
        Assert.Equal(TaskbarMetricSelection.For(TaskbarMetric.Session), TaskbarMetricSelection.SessionOnly);
        Assert.Equal(TaskbarMetricSelection.SessionOnly, new AppSettings().TaskbarDisplay.Metrics);
    }

    private static TaskbarMetric Next(TaskbarMetric from, TaskbarStyle style) =>
        TaskbarMetricCycle.NextMetric(TaskbarMetricSelection.For(from), style);

    /// <summary>
    /// One click with nothing remembered — the plain ring, and exactly the behaviour the gesture
    /// had before the home stop existed (#156). The tests above that use it are the ones that
    /// must not have changed.
    /// </summary>
    private static TaskbarMetricSelection Cycled(TaskbarMetricSelection from, TaskbarStyle style) =>
        TaskbarMetricCycle.Step(from, home: null, style).Metrics;
}
