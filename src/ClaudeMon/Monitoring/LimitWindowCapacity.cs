namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;

/// <summary>How much a per-window capacity figure can be trusted (issue #186).</summary>
internal enum WindowCapacityQuality
{
    /// <summary>A complete window that filled far enough for the extrapolation to mean something.</summary>
    Confident,

    /// <summary>Derivable, but from a lightly-used or plan-straddling window — drawn dimmed.</summary>
    Low,
}

/// <summary>One finalized window with its derived capacity figures, feeding the history tab.</summary>
internal sealed record LimitHistoryRow(
    LimitWindowRecord Record,
    double WeightedTokens,
    double? ImpliedCapacity,
    WindowCapacityQuality? Quality);

/// <summary>
/// Pure per-window capacity derivation (issue #186): a finalized <see cref="LimitWindowRecord"/>
/// already carries tokens-by-model and how far the window filled, so
/// weighted tokens ÷ (peak % / 100) is that window's own implied capacity — no new
/// persistence, the forever-log stays the source of truth. Quality reflects how much the
/// extrapolation is stretching: below <see cref="ConfidentPercentFloor"/> the multiplier
/// amplifies noise too much to chart solid, and below <see cref="MinPercentFloor"/> (or for
/// incomplete windows) no figure is derived at all.
/// </summary>
internal static class LimitWindowCapacity
{
    /// <summary>A window must have filled this far for its capacity to chart as confident.</summary>
    internal const double ConfidentPercentFloor = 15.0;

    /// <summary>Below this, extrapolating to 100% multiplies noise more than 20× — no figure.</summary>
    internal const double MinPercentFloor = 5.0;

    /// <summary>The history row for one finalized window, with capacity when derivable.</summary>
    internal static LimitHistoryRow RowFor(LimitWindowRecord record)
    {
        var weighted = CapacityEstimator.WeightedTokens(record.TokensByModel);
        var pct = Math.Max(record.PeakPercent, record.LastPercent);

        if (record.Incomplete || pct < MinPercentFloor || weighted <= 0)
            return new LimitHistoryRow(record, weighted, null, null);

        var quality = !record.PlanChanged && pct >= ConfidentPercentFloor
            ? WindowCapacityQuality.Confident
            : WindowCapacityQuality.Low;

        return new LimitHistoryRow(record, weighted, weighted / (pct / 100.0), quality);
    }

    /// <summary>
    /// Drops duplicate window records — the log's delivery is at-least-once (a crash between
    /// append and state save re-emits a record), and its own schema doc tells readers to dedupe
    /// on (kind, model, end). First occurrence wins; order is preserved.
    /// </summary>
    internal static List<LimitWindowRecord> Dedupe(IEnumerable<LimitWindowRecord> records)
    {
        var seen = new HashSet<(string, string, DateTimeOffset)>();
        var deduped = new List<LimitWindowRecord>();
        foreach (var record in records)
        {
            if (seen.Add((Normalize(record.Kind), Normalize(record.ScopeModel), record.End)))
                deduped.Add(record);
        }

        return deduped;
    }

    /// <summary>
    /// Where the plan changed along a chronological record sequence — the chart's annotation
    /// markers. A transition lands on the first record observed under the new plan; a record
    /// whose plan changed mid-window marks itself.
    /// </summary>
    internal static IReadOnlyList<(int Index, ClaudePlan? Plan)> PlanTransitions(
        IReadOnlyList<LimitWindowRecord> chronological)
    {
        var transitions = new List<(int, ClaudePlan?)>();
        for (var i = 0; i < chronological.Count; i++)
        {
            var record = chronological[i];
            if (record.PlanChanged || (i > 0 && record.Plan != chronological[i - 1].Plan))
                transitions.Add((i, record.Plan));
        }

        return transitions;
    }

    /// <summary>
    /// The raw (unweighted) token total for one window — what the table's Tokens cell shows
    /// and sorts by: it answers "how much ran", while weighting is the capacity math's concern.
    /// </summary>
    internal static long RawTotal(LimitWindowRecord record) =>
        record.TokensByModel.Values.Sum(t =>
            t.InputTokens + t.OutputTokens + t.CacheWriteTokens + t.CacheReadTokens);

    /// <summary>The model that carried the most raw tokens in a window, or null when none did.</summary>
    internal static string? TopModel(LimitWindowRecord record) =>
        record.TokensByModel.Count == 0
            ? null
            : record.TokensByModel
                .OrderByDescending(kv =>
                    kv.Value.InputTokens + kv.Value.OutputTokens
                    + kv.Value.CacheWriteTokens + kv.Value.CacheReadTokens)
                .First().Key;

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? "";
}
