namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

public class LimitWindowCapacityTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    private static LimitWindowRecord Window(
        double peak = 40, double last = 38, bool incomplete = false, bool planChanged = false,
        Dictionary<string, ModelTokens>? tokens = null, DateTimeOffset? end = null,
        string kind = "session", string? model = null) =>
        new(kind, null, model, (end ?? T0) - UsageWindows.FiveHour, end ?? T0, false,
            peak, last, end ?? T0, 10, ClaudePlan.Max20x, ClaudePlan.Max20x, planChanged,
            tokens ?? new Dictionary<string, ModelTokens> { ["opus"] = new(400_000, 0, 0, 0) },
            incomplete, null);

    [Fact]
    public void RowFor_DerivesCapacityFromWeightedTokensAndPeak()
    {
        // 400k weighted tokens at a 40% peak → 1M implied capacity.
        var row = LimitWindowCapacity.RowFor(Window());

        Assert.Equal(400_000, row.WeightedTokens);
        Assert.Equal(1_000_000, row.ImpliedCapacity!.Value, precision: 0);
        Assert.Equal(WindowCapacityQuality.Confident, row.Quality);
    }

    [Fact]
    public void RowFor_HonorsTheCacheReadWeight()
    {
        var row = LimitWindowCapacity.RowFor(Window(tokens: new Dictionary<string, ModelTokens>
        {
            ["opus"] = new(0, 0, 0, 4_000_000), // 4M cache reads × 0.1 = 400k weighted
        }));

        Assert.Equal(400_000, row.WeightedTokens);
        Assert.Equal(1_000_000, row.ImpliedCapacity!.Value, precision: 0);
    }

    [Fact]
    public void RowFor_QualityFollowsTheFloors()
    {
        // Confident at/above 15%, Low between 5 and 15, nothing below 5.
        Assert.Equal(WindowCapacityQuality.Confident, LimitWindowCapacity.RowFor(Window(peak: 15, last: 15)).Quality);
        Assert.Equal(WindowCapacityQuality.Low, LimitWindowCapacity.RowFor(Window(peak: 8, last: 8)).Quality);
        Assert.Null(LimitWindowCapacity.RowFor(Window(peak: 4, last: 4)).ImpliedCapacity);
    }

    [Fact]
    public void RowFor_IncompleteOrTokenlessWindows_GetNoCapacity()
    {
        Assert.Null(LimitWindowCapacity.RowFor(Window(incomplete: true)).ImpliedCapacity);
        Assert.Null(LimitWindowCapacity.RowFor(
            Window(tokens: new Dictionary<string, ModelTokens>())).ImpliedCapacity);
    }

    [Fact]
    public void RowFor_PlanChangedWindow_IsLowQualityNotConfident()
    {
        Assert.Equal(WindowCapacityQuality.Low, LimitWindowCapacity.RowFor(Window(planChanged: true)).Quality);
    }

    [Fact]
    public void RowFor_UsesThePeakWhenLastFellBack()
    {
        // Percent can dip late in a window (server-side recalcs); the max is the honest floor.
        var row = LimitWindowCapacity.RowFor(Window(peak: 50, last: 20));
        Assert.Equal(800_000, row.ImpliedCapacity!.Value, precision: 0);
    }

    [Fact]
    public void Dedupe_DropsAtLeastOnceDuplicates_KeepsDistinctWindows()
    {
        var a = Window(end: T0);
        var duplicate = Window(end: T0, peak: 41); // same (kind, model, end) — a crash re-emit
        var b = Window(end: T0 + TimeSpan.FromHours(5));
        var scoped = Window(end: T0, model: "Opus 4", kind: "weekly_scoped");

        var deduped = LimitWindowCapacity.Dedupe([a, duplicate, b, scoped]);

        Assert.Equal(3, deduped.Count);
        Assert.Same(a, deduped[0]); // first occurrence wins
    }

    [Fact]
    public void PlanTransitions_MarkChangesBetweenRecordsAndMidWindowChanges()
    {
        var records = new List<LimitWindowRecord>
        {
            Window(end: T0),
            Window(end: T0 + TimeSpan.FromHours(5)),
            Window(end: T0 + TimeSpan.FromHours(10)) with { Plan = ClaudePlan.Pro },
            Window(end: T0 + TimeSpan.FromHours(15)) with { Plan = ClaudePlan.Pro, PlanChanged = true },
        };

        var transitions = LimitWindowCapacity.PlanTransitions(records);

        Assert.Equal(2, transitions.Count);
        Assert.Equal(2, transitions[0].Index); // Max20x → Pro between records
        Assert.Equal(3, transitions[1].Index); // mid-window change marks itself
    }
}
