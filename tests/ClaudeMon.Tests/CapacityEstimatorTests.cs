namespace ClaudeMon.Tests;

using ClaudeMon.Models;
using ClaudeMon.Monitoring;

/// <summary>
/// The implied-capacity engine (issue #185) over synthetic poll sequences — every confounder
/// from the ticket: clean burn, model mix, foreign-device usage, idle gaps, reset straddles,
/// low-sample cold start, quantized percentages, observation gaps, scanner outages, retention
/// dips, cache-read weighting, scoped limits, plan changes.
/// </summary>
public class CapacityEstimatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// A tiny poll simulator: cumulative per-model totals, a clock, and one Observe per Poll —
    /// the same shape the recorder feeds the engine in production.
    /// </summary>
    private sealed class Sim
    {
        public CapacityEstimateState State = new() { Version = CapacityEstimateState.CurrentVersion };
        public DateTimeOffset Now = T0;
        public ClaudePlan? Plan;

        private readonly Dictionary<string, ModelTokens> _cum = new(StringComparer.OrdinalIgnoreCase);

        public void Burn(string model, long input = 0, long output = 0, long cacheWrite = 0, long cacheRead = 0)
        {
            _cum.TryGetValue(model, out var t);
            _cum[model] = (t ?? ModelTokens.Zero).Plus(new ModelTokens(input, output, cacheWrite, cacheRead));
        }

        /// <summary>Drops a model's cumulative total (the scanner's retention prune).</summary>
        public void Prune(string model, long input) =>
            _cum[model] = new ModelTokens(input, 0, 0, 0);

        public void Poll(params LimitSnapshot[] limits) =>
            State = CapacityEstimator.Observe(
                State,
                new LimitLogSample(Now, limits, new Dictionary<string, ModelTokens>(_cum, StringComparer.OrdinalIgnoreCase)),
                Plan);

        public void PollNoTokens(params LimitSnapshot[] limits) =>
            State = CapacityEstimator.Observe(State, new LimitLogSample(Now, limits, null), Plan);

        public void Advance(TimeSpan by) => Now += by;

        public ImpliedCapacity Estimate(string kind, string? model = null) =>
            CapacityEstimator.Estimates(State).Single(e => e.Kind == kind && e.ScopeModel == model);
    }

    private static LimitSnapshot Session(double pct, DateTimeOffset resets) =>
        new("session", "5h", pct, null, resets, null, null);

    private static LimitSnapshot Scoped(double pct, DateTimeOffset resets, string model) =>
        new("weekly_scoped", "weekly", pct, null, resets, null, model);

    /// <summary>
    /// Drives a clean burn through consecutive session windows: every 10 minutes the limit
    /// climbs <paramref name="pctPerPoll"/> and <paramref name="model"/> burns
    /// <paramref name="tokensPerPoll"/> input tokens; windows roll naturally.
    /// </summary>
    private static Sim CleanBurn(
        int polls, double pctPerPoll = 2.0, long tokensPerPoll = 20_000, string model = "claude-opus-4-6")
    {
        var sim = new Sim();
        var resets = T0 + UsageWindows.FiveHour;
        var pct = 0.0;
        for (var i = 0; i < polls; i++)
        {
            if (sim.Now >= resets)
            {
                resets = sim.Now + UsageWindows.FiveHour;
                pct = 0;
            }

            sim.Burn(model, input: tokensPerPoll);
            pct += pctPerPoll;
            sim.Poll(Session(pct, resets));
            sim.Advance(TimeSpan.FromMinutes(10));
        }

        return sim;
    }

    // --- AC6: clean single-model burn ---

    [Fact]
    public void CleanSingleModelBurn_RecoversTheTrueCapacity()
    {
        // 20k tokens per 2 points = 10k weighted tokens per point → 1M per full window.
        var sim = CleanBurn(polls: 70);

        var estimate = sim.Estimate("session");
        Assert.InRange(estimate.CapacityWeightedTokens, 950_000, 1_050_000);
        Assert.True(estimate.Confidence >= CapacityConfidence.Medium);
        Assert.True(estimate.ObservationCount >= CapacityEstimator.MinObservations);
        Assert.Equal(0, estimate.UnexplainedCount);
        Assert.NotNull(estimate.FirstObservedAt);
        Assert.NotNull(estimate.LastObservedAt);
    }

    [Fact]
    public void CleanBurn_SingleDominantModel_ReportsInThatModelsTerms()
    {
        var sim = CleanBurn(polls: 70);

        Assert.Equal("claude-opus-4-6", sim.Estimate("session").EquivalentModel);
    }

    // --- AC6: low-sample cold start ---

    [Fact]
    public void ColdStart_TooFewObservations_IsNoEstimate()
    {
        var sim = CleanBurn(polls: 4); // 3 observations (first poll is the baseline)

        var estimate = sim.Estimate("session");
        Assert.Equal(CapacityConfidence.None, estimate.Confidence);
        Assert.Equal(3, estimate.ObservationCount);
    }

    [Fact]
    public void ColdStart_EnoughObservationsButShortSpan_IsStillNoEstimate()
    {
        // 10 observations inside a single window: n passes, the span gate doesn't — one
        // window's worth of data can't yet vouch for a whole-window number.
        var sim = CleanBurn(polls: 11);

        Assert.Equal(CapacityConfidence.None, sim.Estimate("session").Confidence);
    }

    // --- AC2: foreign-surface usage ---

    [Fact]
    public void ForeignUsage_PercentMovesWithNoLocalTokens_IsExcludedNotFolded()
    {
        var sim = CleanBurn(polls: 70);
        var before = sim.Estimate("session").CapacityWeightedTokens;

        // The phone burns 12 points with zero local tokens, across six polls.
        var resets = sim.Now + UsageWindows.FiveHour;
        var pct = 0.0;
        for (var i = 0; i < 6; i++)
        {
            pct += 2;
            sim.Poll(Session(pct, resets));
            sim.Advance(TimeSpan.FromMinutes(10));
        }

        var estimate = sim.Estimate("session");
        Assert.Equal(before, estimate.CapacityWeightedTokens); // The median never saw them.
        Assert.True(estimate.UnexplainedCount >= 5);
    }

    [Fact]
    public void ForeignUsage_DominatingTheRing_HidesTheEstimate()
    {
        // Mostly-foreign usage: a handful of clean intervals, then a ring-full of unexplained
        // movement. The estimate must hide rather than report a number built on air.
        var sim = CleanBurn(polls: 8);
        var resets = sim.Now + UsageWindows.FiveHour;
        var pct = 0.0;
        for (var i = 0; i < 12; i++)
        {
            pct += 2;
            sim.Poll(Session(pct, resets));
            sim.Advance(TimeSpan.FromMinutes(10));
        }

        Assert.Equal(CapacityConfidence.None, sim.Estimate("session").Confidence);
    }

    // --- AC2: idle intervals ---

    [Fact]
    public void IdlePolls_ContributeNothingAndGrowNoState()
    {
        var sim = new Sim();
        var resets = T0 + UsageWindows.FiveHour;
        for (var i = 0; i < 20; i++)
        {
            sim.Poll(Session(40, resets));
            sim.Advance(TimeSpan.FromMinutes(10));
        }

        var estimate = sim.Estimate("session");
        Assert.Equal(0, estimate.ObservationCount);
        Assert.Equal(CapacityConfidence.None, estimate.Confidence);
        var limit = Assert.Single(sim.State.Limits);
        Assert.Empty(limit.Ring);
    }

    // --- AC2: reset boundaries ---

    [Fact]
    public void ResetStraddle_DiscardsTheIntervalAndNeverGoesNegative()
    {
        var sim = new Sim();
        var resets = T0 + UsageWindows.FiveHour;
        sim.Poll(Session(80, resets)); // baseline
        sim.Advance(TimeSpan.FromMinutes(10));
        sim.Burn("opus", input: 50_000);

        // The window resets mid-interval: percent collapses, resets_at moves.
        sim.Poll(Session(2, sim.Now + UsageWindows.FiveHour));

        var limit = Assert.Single(sim.State.Limits);
        Assert.Empty(limit.Ring); // Discarded, not emitted as a negative/absurd capacity.
        Assert.All(
            CapacityEstimator.Estimates(sim.State),
            e => Assert.True(e.CapacityWeightedTokens >= 0));
    }

    [Fact]
    public void PercentFallingWithoutAResetSignal_RebasesInsteadOfEmitting()
    {
        var sim = new Sim();
        var resets = T0 + UsageWindows.FiveHour;
        sim.Poll(Session(40, resets));
        sim.Advance(TimeSpan.FromMinutes(10));
        sim.Burn("opus", input: 10_000);
        sim.Poll(Session(35, resets)); // fell 5 points, same resets — not jitter

        Assert.Empty(Assert.Single(sim.State.Limits).Ring);
    }

    // --- Quantization, gaps, outages, dips ---

    [Fact]
    public void QuantizedPercent_CoarsensPollsIntoWholePointIntervals()
    {
        // Integer-only reporting: three polls of sub-point burn per reported step. Intervals
        // close only when a whole point has moved, carrying all the accumulated tokens.
        var sim = new Sim();
        var resets = T0 + UsageWindows.FiveHour;
        double[] reported = [0, 0, 0, 1, 1, 1, 2, 2, 2, 3];
        foreach (var pct in reported)
        {
            sim.Burn("opus", input: 4_000);
            sim.Poll(Session(pct, resets));
            sim.Advance(TimeSpan.FromMinutes(10));
        }

        var ring = Assert.Single(sim.State.Limits).Ring;
        Assert.Equal(3, ring.Count); // One per whole-point step, not one per poll.
        Assert.All(ring, o => Assert.True(o.DeltaPercent >= CapacityEstimator.MinDeltaPct));
    }

    [Fact]
    public void ObservationGap_DiscardsTheOpenIntervalAndItsAmbiguousTokens()
    {
        var sim = new Sim();
        var resets = T0 + TimeSpan.FromHours(12); // long window so the gap doesn't cross a reset
        sim.Poll(Scoped(10, resets, "Opus 4"));
        sim.Advance(TimeSpan.FromMinutes(10));
        sim.Burn("claude-opus-4-6", input: 30_000);
        sim.Poll(Scoped(10, resets, "Opus 4"));

        // Half an hour of silence — beyond MaxObservationGap. The 30k already accumulated
        // (and anything burned during the gap) is unattributable.
        sim.Advance(TimeSpan.FromMinutes(30));
        sim.Burn("claude-opus-4-6", input: 30_000);
        sim.Poll(Scoped(13, resets, "Opus 4"));

        Assert.Empty(Assert.Single(sim.State.Limits).Ring);

        // The next clean interval counts only post-gap tokens.
        sim.Advance(TimeSpan.FromMinutes(10));
        sim.Burn("claude-opus-4-6", input: 20_000);
        sim.Poll(Scoped(15, resets, "Opus 4"));
        var observation = Assert.Single(Assert.Single(sim.State.Limits).Ring);
        Assert.Equal(20_000, observation.WeightedTokens);
    }

    [Fact]
    public void ScannerOutage_HoldsTheBaselineSoTheDeltaResumesAcrossIt()
    {
        var sim = new Sim();
        var resets = T0 + UsageWindows.FiveHour;
        sim.Burn("opus", input: 100_000);
        sim.Poll(Session(10, resets));

        // Two polls with no token data at all (tok: null), then totals return, grown.
        sim.Advance(TimeSpan.FromMinutes(10));
        sim.PollNoTokens(Session(10.5, resets));
        sim.Advance(TimeSpan.FromMinutes(10));
        sim.Burn("opus", input: 15_000);
        sim.Poll(Session(11.5, resets));

        var observation = Assert.Single(Assert.Single(sim.State.Limits).Ring);
        Assert.Equal(15_000, observation.WeightedTokens); // measured from the held baseline
    }

    [Fact]
    public void RetentionDip_ClampsToZeroBurnNeverNegative()
    {
        var sim = new Sim();
        var resets = T0 + UsageWindows.FiveHour;
        sim.Burn("opus", input: 500_000);
        sim.Poll(Session(10, resets));

        sim.Advance(TimeSpan.FromMinutes(10));
        sim.Prune("opus", input: 100_000); // 30-day retention aged out old days
        sim.Poll(Session(11, resets));

        var observation = Assert.Single(Assert.Single(sim.State.Limits).Ring);
        Assert.Equal(0, observation.WeightedTokens);
        Assert.True(observation.Unexplained); // zero tokens can't explain a point of movement
    }

    // --- AC2: cache-read weighting ---

    [Fact]
    public void CacheReads_WeighAtOneTenthOfFreshTokens()
    {
        var fresh = new Sim();
        var resets = T0 + UsageWindows.FiveHour;
        fresh.Burn("opus", input: 10_000);
        fresh.Poll(Session(0, resets));
        fresh.Advance(TimeSpan.FromMinutes(10));
        fresh.Burn("opus", input: 10_000);
        fresh.Poll(Session(1, resets));

        var cached = new Sim();
        cached.Burn("opus", cacheRead: 100_000);
        cached.Poll(Session(0, resets));
        cached.Advance(TimeSpan.FromMinutes(10));
        cached.Burn("opus", cacheRead: 100_000); // 100k reads × 0.1 = the same 10k weighted
        cached.Poll(Session(1, resets));

        var freshObs = Assert.Single(Assert.Single(fresh.State.Limits).Ring);
        var cachedObs = Assert.Single(Assert.Single(cached.State.Limits).Ring);
        Assert.Equal(freshObs.WeightedTokens, cachedObs.WeightedTokens);
    }

    // --- AC3: model mix ---

    [Fact]
    public void ModelMix_DetectsPerModelRatesAndReportsTheRecentDominantModel()
    {
        // Ten Opus-dominant intervals at 10k/point, then ten Fable-dominant ones at
        // 30k/point: both models establish a rate, and the capacity is reported in the
        // recently-active model's terms.
        var sim = new Sim();
        var resets = T0 + UsageWindows.FiveHour;
        var pct = 0.0;
        for (var i = 0; i < 20; i++)
        {
            if (sim.Now >= resets)
            {
                resets = sim.Now + UsageWindows.FiveHour;
                pct = 0;
            }

            if (i < 10)
                sim.Burn("claude-opus-4-6", input: 10_000);
            else
                sim.Burn("claude-fable-5", input: 30_000);
            pct += 1;
            sim.Poll(Session(pct, resets));
            sim.Advance(TimeSpan.FromMinutes(20));
        }

        var estimate = sim.Estimate("session");
        Assert.Equal("claude-fable-5", estimate.EquivalentModel);
        Assert.InRange(estimate.CapacityWeightedTokens, 2_900_000, 3_100_000);
    }

    [Fact]
    public void ModelMix_TooFewPerModelObservations_DegradesToBlended()
    {
        var sim = new Sim();
        var resets = T0 + UsageWindows.FiveHour;
        var pct = 0.0;
        for (var i = 0; i < 8; i++)
        {
            if (i % 2 == 0)
                sim.Burn("claude-opus-4-6", input: 10_000);
            else
                sim.Burn("claude-fable-5", input: 30_000);
            pct += 1;
            sim.Poll(Session(pct, resets));
            sim.Advance(TimeSpan.FromMinutes(20));
        }

        var estimate = sim.Estimate("session");
        Assert.Null(estimate.EquivalentModel); // 4 + 3 dominant intervals — neither reaches 6
        Assert.InRange(estimate.CapacityWeightedTokens, 1_000_000, 3_000_000); // between the two
    }

    // --- Scoped weekly limits ---

    [Fact]
    public void ScopedLimit_CountsOnlyTheScopeModelsTokens()
    {
        var sim = new Sim();
        var resets = T0 + UsageWindows.SevenDay;
        sim.Poll(Scoped(10, resets, "Opus 4"));
        sim.Advance(TimeSpan.FromMinutes(10));
        sim.Burn("claude-opus-4-6", input: 25_000);
        sim.Burn("claude-fable-5", input: 999_999); // must not count against the Opus cap
        sim.Poll(Scoped(11, resets, "Opus 4"));

        var observation = Assert.Single(Assert.Single(sim.State.Limits).Ring);
        Assert.Equal(25_000, observation.WeightedTokens);
    }

    [Fact]
    public void ScopedLimit_UnmatchableScopeName_StaysHiddenNeverWrong()
    {
        var sim = new Sim();
        var resets = T0 + UsageWindows.SevenDay;
        sim.Poll(Scoped(10, resets, "Zeta 9"));
        for (var i = 0; i < 8; i++)
        {
            sim.Advance(TimeSpan.FromMinutes(10));
            sim.Burn("claude-opus-4-6", input: 50_000);
            sim.Poll(Scoped(11 + i, resets, "Zeta 9"));
        }

        var estimate = sim.Estimate("weekly_scoped", "Zeta 9");
        Assert.Equal(CapacityConfidence.None, estimate.Confidence);
        Assert.Equal(0, estimate.ObservationCount); // every interval unexplained
        Assert.True(estimate.UnexplainedCount > 0);
    }

    // --- Plan changes, replay, bounds, dispersion ---

    [Fact]
    public void PlanChange_ClearsTheRings()
    {
        var sim = CleanBurn(polls: 30);
        sim.Plan = ClaudePlan.Max20x; // was null throughout the burn

        sim.Advance(TimeSpan.FromMinutes(10));
        sim.Poll(Session(50, sim.Now + UsageWindows.FiveHour));

        Assert.Equal(0, sim.Estimate("session").ObservationCount);
        Assert.Equal(ClaudePlan.Max20x, sim.State.Plan);
    }

    [Fact]
    public void ReplayedOrOutOfOrderSample_IsANoOp()
    {
        var sim = CleanBurn(polls: 10);
        var before = sim.State;

        var stale = new LimitLogSample(
            sim.Now - TimeSpan.FromHours(1),
            [Session(99, sim.Now + UsageWindows.FiveHour)],
            new Dictionary<string, ModelTokens> { ["opus"] = new(1, 1, 1, 1) });

        Assert.Same(before, CapacityEstimator.Observe(before, stale, sim.Plan));
    }

    [Fact]
    public void HeavyBurnSession_FillsTheRingWithoutHidingTheEstimate()
    {
        // A big day: 60 one-point intervals inside a single 5-hour window, after a normal
        // history. The ring evicts past 48, but the span gate grades from the lifetime first
        // observation — the estimate must not vanish exactly when it's most wanted.
        var sim = CleanBurn(polls: 70);

        var resets = sim.Now + UsageWindows.FiveHour;
        var pct = 0.0;
        for (var i = 0; i < 60; i++)
        {
            sim.Burn("claude-opus-4-6", input: 10_000);
            pct += 1;
            sim.Poll(Session(pct, resets));
            sim.Advance(TimeSpan.FromMinutes(5));
        }

        var estimate = sim.Estimate("session");
        Assert.True(estimate.Confidence >= CapacityConfidence.Medium);
        Assert.InRange(estimate.CapacityWeightedTokens, 950_000, 1_050_000);
    }

    [Fact]
    public void Ring_IsBoundedAtRingSize()
    {
        var sim = CleanBurn(polls: 120);

        var limit = Assert.Single(sim.State.Limits);
        Assert.True(limit.Ring.Count <= CapacityEstimator.RingSize);
        // The lifetime basis keeps counting past the ring cap (rollover polls emit nothing).
        Assert.True(limit.TotalObservations > CapacityEstimator.RingSize);
    }

    [Fact]
    public void WildlyDisagreeingIntervals_DemoteConfidenceToLow()
    {
        // Tokens-per-point cycling 5k / 30k / 100k: implied capacities of 0.5M / 3M / 10M
        // give MAD/median ≈ 0.83 — enough observations and span, but no agreement.
        var sim = new Sim();
        var resets = T0 + UsageWindows.FiveHour;
        long[] cycle = [5_000, 30_000, 100_000];
        var pct = 0.0;
        for (var i = 0; i < 35; i++)
        {
            if (sim.Now >= resets)
            {
                resets = sim.Now + UsageWindows.FiveHour;
                pct = 0;
            }

            sim.Burn("opus", input: cycle[i % 3]);
            pct += 1;
            sim.Poll(Session(pct, resets));
            sim.Advance(TimeSpan.FromMinutes(10));
        }

        Assert.Equal(CapacityConfidence.Low, sim.Estimate("session").Confidence);
    }
}
