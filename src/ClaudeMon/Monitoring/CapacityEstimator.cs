namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;

/// <summary>
/// The implied-capacity engine (issue #185), pure by construction like
/// <see cref="LimitWindowTracker"/>: it consumes the correlated log's samples one at a time —
/// the same record whether fed live from the poll or streamed from disk at cold start — and
/// maintains, per limit key (kind, scope model), a bounded ring of <b>percent-movement
/// intervals</b>: the limit moved ≥1 point, and these weighted local tokens were burned while
/// it moved. The estimate is the <b>median</b> of the ring's implied capacities — chosen over
/// regression because the enemy here is contamination (usage from claude.ai/mobile/another
/// machine moves the % with no local tokens), and a median shrugs off what a fit would tilt
/// toward.
///
/// Per-poll Δ% is dominated by quantization and scanner lag, so polls are coarsened: an
/// accumulator collects token deltas until the % has moved a full point, then closes into one
/// observation. Intervals that straddle a reset boundary, an observation gap, or an
/// implausible jump are discarded, never emitted. Intervals whose movement local tokens
/// cannot explain are kept in the ring flagged unexplained — excluded from the estimate, but
/// counted against confidence until the ring ages them out.
///
/// <b>Weighted-token unit (v1, fixed and documented):</b> input, output, and cache-write
/// tokens count 1; cache reads count <see cref="CacheReadWeight"/> (0.1), mirroring their
/// ~10× price discount — the best public proxy for how much less they draw on the limit.
/// Fitting per-class weights from the data is deferred.
/// </summary>
internal static class CapacityEstimator
{
    /// <summary>An interval closes once the limit has moved at least this many points.</summary>
    internal const double MinDeltaPct = 1.0;

    /// <summary>A single interval moving more than this means a missed boundary — discard.</summary>
    internal const double MaxDeltaPct = 50.0;

    /// <summary>Percent may jitter down this much without meaning a reset happened.</summary>
    internal const double DownwardJitterPct = 0.25;

    /// <summary>Ring capacity per limit key — bounds memory and ages out stale observations.</summary>
    internal const int RingSize = 48;

    internal const int MinObservations = 6;
    internal const int HighObservations = 12;
    internal const double MaxDispersion = 0.5;
    internal const double HighDispersion = 0.25;
    internal const double MaxUnexplainedFraction = 0.5;

    internal const double CacheReadWeight = 0.1;

    /// <summary>
    /// Cold-start explanation floor: an interval must burn at least this many weighted local
    /// tokens per percentage point to count as explained. Once the ring has data, the floor
    /// becomes <see cref="RelativeExplanationFloor"/> of the ring's median tokens-per-point.
    /// </summary>
    internal const double AbsoluteExplanationFloor = 2_000.0;
    internal const double RelativeExplanationFloor = 0.1;

    /// <summary>A model must contribute this share of an interval's weighted tokens to tag it.</summary>
    internal const double DominantModelShare = 0.8;
    internal const int MinPerModelObservations = 6;

    /// <summary>Advances the state by one sample. Pure: no clock, no I/O; a replayed or out-of-order sample is a no-op.</summary>
    internal static CapacityEstimateState Observe(
        CapacityEstimateState state, LimitLogSample sample, ClaudePlan? plan)
    {
        if (state.LastSampleAt is { } lastAt && sample.Timestamp <= lastAt)
            return state;

        // A plan change means the capacity itself changed: observations made under the old
        // plan would read as drift, so every ring restarts. Only a *change* clears — the
        // first-ever observation just adopts the plan.
        if (state.LastSampleAt is not null && state.Plan != plan)
        {
            state = state with
            {
                Limits = state.Limits
                    .Select(l => l with { Accumulator = null, Ring = new(), TotalObservations = 0, FirstObservedAt = null })
                    .ToList(),
            };
        }

        var deltas = Deltas(state.LastTokens, sample.TokensByModel);
        // Note: a hand-edited config polling slower than MaxObservationGap (the UI caps at
        // 10 minutes; the gap allows 25) makes every poll discontinuous, which silently —
        // and safely — keeps the estimator empty. Hidden, never wrong.
        var continuous = state.LastSampleAt is { } last &&
            sample.Timestamp - last <= LimitWindowTracker.MaxObservationGap;

        // Every known key carries forward, including ones the API no longer reports — a
        // retired scope name keeps its ring in the state file. Bounded in practice by the
        // handful of kinds and models; revisit if scope names ever churn.
        var limits = new List<LimitCapacityState>(state.Limits.Count);
        var byKey = new Dictionary<(string, string), int>();
        foreach (var l in state.Limits)
        {
            // An observation gap makes token attribution ambiguous for every open interval
            // (the scanner back-fills offline burn): discard the accumulators, keep the rings.
            var carried = continuous
                ? l with { Accumulator = Accumulate(l, deltas) }
                : l with { Accumulator = null };
            byKey[KeyOf(l.Kind, l.ScopeModel)] = limits.Count;
            limits.Add(carried);
        }

        foreach (var snap in DedupByKey(sample.Limits))
        {
            if (snap.Percent is not { } pct)
                continue;

            var key = KeyOf(snap.Kind, snap.ScopeModel);
            if (!byKey.TryGetValue(key, out var index))
            {
                byKey[key] = limits.Count;
                limits.Add(new LimitCapacityState { Kind = snap.Kind, ScopeModel = snap.ScopeModel });
                index = limits.Count - 1;
            }

            limits[index] = Advance(limits[index], snap, pct, sample.Timestamp);
        }

        return state with
        {
            Version = CapacityEstimateState.CurrentVersion,
            LastSampleAt = sample.Timestamp,
            LastTokens = sample.TokensByModel is null
                ? state.LastTokens
                : new Dictionary<string, ModelTokens>(sample.TokensByModel, StringComparer.OrdinalIgnoreCase),
            Plan = plan,
            Limits = limits,
        };
    }

    /// <summary>The current per-limit estimates, computed from the rings alone.</summary>
    internal static IReadOnlyList<ImpliedCapacity> Estimates(CapacityEstimateState state)
    {
        var estimates = new List<ImpliedCapacity>();
        foreach (var limit in state.Limits)
        {
            if (WindowLengthOf(limit.Kind) is not { } windowLength)
                continue; // Unknown kinds are tracked but not estimated — no span gate exists for them.

            var qualifying = limit.Ring.Where(o => !o.Unexplained).ToList();
            var unexplained = limit.Ring.Count - qualifying.Count;

            if (qualifying.Count == 0)
            {
                estimates.Add(new ImpliedCapacity(
                    limit.Kind, limit.ScopeModel, 0, null, CapacityConfidence.None,
                    0, unexplained, limit.FirstObservedAt, null));
                continue;
            }

            var capacities = qualifying.Select(o => o.ImpliedCapacity).ToList();
            var median = Median(capacities);
            var dispersion = median > 0
                ? Median(capacities.Select(c => Math.Abs(c - median)).ToList()) / median
                : double.PositiveInfinity;
            // Span is graded from the lifetime first observation, not the ring's oldest
            // survivor: the gate asks "have we watched long enough", and a heavy session
            // that closes 48+ one-point intervals inside one window would otherwise flush
            // the ring's span below the window length and hide the estimate exactly when
            // it's most wanted. Eviction bounds memory; it must not reset the calendar.
            var span = qualifying[^1].End - (limit.FirstObservedAt ?? qualifying[0].Start);
            var unexplainedFraction = (double)unexplained / limit.Ring.Count;

            var confidence = Grade(qualifying.Count, span, windowLength, dispersion, unexplainedFraction);
            var (capacity, equivalentModel) = ModelWeighted(qualifying, median);

            estimates.Add(new ImpliedCapacity(
                limit.Kind, limit.ScopeModel, capacity, equivalentModel, confidence,
                qualifying.Count, unexplained, limit.FirstObservedAt, qualifying[^1].End));
        }

        return estimates;
    }

    // --- Interval accounting ---

    // Folds this poll's deltas into the open accumulator (scoped limits count only their
    // model's tokens). A key with no open accumulator stays baseline-less until it next
    // appears in a payload with a percent.
    private static CapacityAccumulator? Accumulate(
        LimitCapacityState limit, Dictionary<string, ModelTokens>? deltas)
    {
        if (limit.Accumulator is not { } acc || deltas is null || deltas.Count == 0)
            return limit.Accumulator;

        var relevant = RelevantTo(limit, deltas);
        if (relevant.Count == 0)
            return acc;

        var tokens = new Dictionary<string, ModelTokens>(acc.Tokens, StringComparer.OrdinalIgnoreCase);
        foreach (var (model, delta) in relevant)
            tokens[model] = (tokens.TryGetValue(model, out var t) ? t : ModelTokens.Zero).Plus(delta);

        return acc with { Tokens = tokens };
    }

    // One limit's presence in a sample: open a baseline, extend, discard on a boundary, or
    // close an observation. Deltas were already folded by Accumulate — this only decides.
    private static LimitCapacityState Advance(
        LimitCapacityState limit, LimitSnapshot snap, double pct, DateTimeOffset now)
    {
        if (limit.Accumulator is not { } acc)
            return limit with { Accumulator = NewBaseline(snap, pct, now) };

        // Reset boundary mid-interval (resets_at moved beyond jitter, passed, or the percent
        // fell for real): the interval spans two windows, so its delta is unattributable —
        // discard and rebase. This is what keeps negative and absurd capacities impossible.
        // Deliberately conservative: a resets_at flickering between null and set rebases on
        // every flip (such a payload can't be trusted about boundaries), and the first
        // interval after each idle→active transition is always discarded.
        var resetMoved = acc.BaselineResetsAt is { } baseResets
            ? snap.ResetsAt is not { } resets || Magnitude(resets - baseResets) > LimitWindowTracker.ResetJitter
              || baseResets <= now
            : snap.ResetsAt is not null;
        if (resetMoved || pct < acc.BaselinePercent - DownwardJitterPct)
            return limit with { Accumulator = NewBaseline(snap, pct, now) };

        var deltaPct = pct - acc.BaselinePercent;
        if (deltaPct < MinDeltaPct)
            return limit; // Idle or sub-point movement: the interval just keeps extending.

        if (deltaPct > MaxDeltaPct)
            return limit with { Accumulator = NewBaseline(snap, pct, now) }; // Missed boundary.

        var weighted = WeightedTokens(acc.Tokens);
        var observation = new CapacityObservation(
            deltaPct, weighted, DominantModel(acc.Tokens), acc.StartAt, now,
            Unexplained: weighted / deltaPct < ExplanationFloor(limit.Ring));

        var ring = new List<CapacityObservation>(limit.Ring) { observation };
        if (ring.Count > RingSize)
            ring.RemoveRange(0, ring.Count - RingSize);

        return limit with
        {
            Accumulator = NewBaseline(snap, pct, now),
            Ring = ring,
            TotalObservations = limit.TotalObservations + 1,
            FirstObservedAt = limit.FirstObservedAt ?? acc.StartAt,
        };
    }

    private static CapacityAccumulator NewBaseline(LimitSnapshot snap, double pct, DateTimeOffset now) =>
        new(pct, snap.ResetsAt, now, new Dictionary<string, ModelTokens>(StringComparer.OrdinalIgnoreCase));

    // Below this many weighted tokens per percentage point, the movement wasn't this
    // machine's doing. Relative to the ring's own median once one exists, so the floor
    // scales with the plan; absolute during cold start.
    private static double ExplanationFloor(List<CapacityObservation> ring)
    {
        var explained = ring.Where(o => !o.Unexplained).Select(o => o.TokensPerPoint).ToList();
        return explained.Count >= 3
            ? Math.Max(AbsoluteExplanationFloor, RelativeExplanationFloor * Median(explained))
            : AbsoluteExplanationFloor;
    }

    // A scoped weekly limit counts only its model's tokens; everything else counts all.
    // The scope display name ("Opus 4") is matched against transcript model ids
    // ("claude-opus-4-6") by normalizing separators — a failed match yields zero relevant
    // tokens, which makes the intervals unexplained and the estimate hidden, never wrong.
    private static Dictionary<string, ModelTokens> RelevantTo(
        LimitCapacityState limit, Dictionary<string, ModelTokens> deltas)
    {
        if (Normalize(limit.Kind) != "weekly_scoped" || string.IsNullOrWhiteSpace(limit.ScopeModel))
            return deltas;

        var needle = limit.ScopeModel.Trim().ToLowerInvariant().Replace(' ', '-').Replace('.', '-');
        return deltas
            .Where(kv => kv.Key.ToLowerInvariant().Replace('.', '-').Contains(needle))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
    }

    // --- Estimation ---

    private static CapacityConfidence Grade(
        int count, TimeSpan span, TimeSpan windowLength, double dispersion, double unexplainedFraction)
    {
        if (count < MinObservations || span < windowLength || unexplainedFraction > MaxUnexplainedFraction)
            return CapacityConfidence.None;
        if (dispersion > MaxDispersion)
            return CapacityConfidence.Low;
        if (count >= HighObservations && span >= windowLength + windowLength && dispersion <= HighDispersion)
            return CapacityConfidence.High;
        return CapacityConfidence.Medium;
    }

    // Per-model reporting (AC3): models with enough dominant intervals of their own get a
    // per-model capacity. When the most recent such model's rate is established, report in
    // its terms — exact for single-model users, and honest ("≈N Opus tokens") for mixed ones
    // whose models demonstrably consume the limit at different rates. Anything short of that
    // degrades to the blended median. Known v1 blend: confidence is graded on the blended
    // ring's dispersion even when the reported number is a per-model median — a mixed ring
    // can grade Low while each model individually is tight. Deferred with the fitted weights.
    private static (double Capacity, string? EquivalentModel) ModelWeighted(
        List<CapacityObservation> qualifying, double blendedMedian)
    {
        var perModel = qualifying
            .Where(o => o.DominantModel is not null)
            .GroupBy(o => o.DominantModel!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= MinPerModelObservations)
            .ToDictionary(
                g => g.Key,
                g => Median(g.Select(o => o.ImpliedCapacity).ToList()),
                StringComparer.OrdinalIgnoreCase);

        if (perModel.Count == 0)
            return (blendedMedian, null);

        var recent = qualifying.Last(o => o.DominantModel is not null && perModel.ContainsKey(o.DominantModel));
        return (perModel[recent.DominantModel!], recent.DominantModel);
    }

    // --- Shared helpers (the same semantics as LimitWindowTracker) ---

    internal static double WeightedTokens(IReadOnlyDictionary<string, ModelTokens> tokens)
    {
        var weighted = 0.0;
        foreach (var t in tokens.Values)
            weighted += t.InputTokens + t.OutputTokens + t.CacheWriteTokens + CacheReadWeight * t.CacheReadTokens;
        return weighted;
    }

    private static string? DominantModel(Dictionary<string, ModelTokens> tokens)
    {
        var total = WeightedTokens(tokens);
        if (total <= 0)
            return null;

        foreach (var (model, t) in tokens)
        {
            var share = (t.InputTokens + t.OutputTokens + t.CacheWriteTokens + CacheReadWeight * t.CacheReadTokens) / total;
            if (share >= DominantModelShare)
                return model;
        }

        return null;
    }

    private static Dictionary<string, ModelTokens>? Deltas(
        IReadOnlyDictionary<string, ModelTokens>? last,
        IReadOnlyDictionary<string, ModelTokens>? cumulative)
    {
        if (last is null || cumulative is null)
            return null;

        var deltas = new Dictionary<string, ModelTokens>(StringComparer.OrdinalIgnoreCase);
        foreach (var (model, tokens) in cumulative)
        {
            var previous = last.TryGetValue(model, out var p) ? p : ModelTokens.Zero;
            var delta = tokens.DeltaFrom(previous);
            if (!delta.IsZero)
                deltas[model] = delta;
        }

        return deltas;
    }

    private static List<LimitSnapshot> DedupByKey(IReadOnlyList<LimitSnapshot> snapshots)
    {
        var deduped = new List<LimitSnapshot>();
        var indexByKey = new Dictionary<(string, string), int>();
        foreach (var snap in snapshots)
        {
            var key = KeyOf(snap.Kind, snap.ScopeModel);
            if (indexByKey.TryGetValue(key, out var existing))
            {
                if ((snap.Percent ?? 0) > (deduped[existing].Percent ?? 0))
                    deduped[existing] = snap;
            }
            else
            {
                indexByKey[key] = deduped.Count;
                deduped.Add(snap);
            }
        }

        return deduped;
    }

    internal static TimeSpan? WindowLengthOf(string? kind) => Normalize(kind) switch
    {
        "session" => UsageWindows.FiveHour,
        "weekly_all" or "weekly_scoped" => UsageWindows.SevenDay,
        _ => null,
    };

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static (string, string) KeyOf(string? kind, string? scopeModel) =>
        (Normalize(kind), Normalize(scopeModel));

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? "";

    private static TimeSpan Magnitude(TimeSpan span) => span < TimeSpan.Zero ? -span : span;
}
