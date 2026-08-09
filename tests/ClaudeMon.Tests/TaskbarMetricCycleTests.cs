namespace ClaudeMon.Tests;

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
            selection = TaskbarMetricCycle.Next(selection, TaskbarStyle.Numbers);
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
        var collapsed = TaskbarMetricCycle.Next(composed, TaskbarStyle.Numbers);
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

        var next = TaskbarMetricCycle.Next(composed, TaskbarStyle.Bar);

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

        selection = TaskbarMetricCycle.Next(selection, TaskbarStyle.Bar);
        Assert.True(selection.Weekly);

        selection = TaskbarMetricCycle.Next(selection, TaskbarStyle.Bar);
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
        var next = TaskbarMetricCycle.Next(composed, TaskbarStyle.Numbers);

        Assert.Equal(1, next.Count);
        Assert.NotNull(TaskbarMetricCycle.Current(next, TaskbarStyle.Numbers));
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
    public void Selection_SessionOnly_IsTheDefaultReadout()
    {
        Assert.Equal(TaskbarMetricSelection.For(TaskbarMetric.Session), TaskbarMetricSelection.SessionOnly);
        Assert.Equal(TaskbarMetricSelection.SessionOnly, new AppSettings().TaskbarDisplay.Metrics);
    }

    private static TaskbarMetric Next(TaskbarMetric from, TaskbarStyle style) =>
        TaskbarMetricCycle.NextMetric(TaskbarMetricSelection.For(from), style);
}
