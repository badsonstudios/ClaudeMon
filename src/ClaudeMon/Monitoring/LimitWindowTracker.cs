namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;

/// <summary>
/// The correlated limit log's window state machine (issue #184), pure by construction: no
/// clock, no I/O, no statics beyond constants — one observation in, (sample, finalized
/// windows, new state) out — so every boundary case is unit-testable against synthetic data.
///
/// Windows are identified by (kind, scope model), never by <c>group</c>: the API has already
/// changed its group vocabulary once ("weekly" vs "seven_day"), and identity must survive
/// that. Boundaries are server-authoritative — a window ends when its <c>resets_at</c> moves
/// (beyond jitter) or passes — so a skewed local clock can't corrupt the rollup.
///
/// Token attribution: each observation's per-model growth in the scanner's cumulative totals
/// (clamped at zero — see <see cref="ModelTokens.DeltaFrom"/>) is added to every active
/// window, since every limit governs all usage in its span. A delta that straddles a window
/// boundary goes to the new window — deterministic, with error bounded by one poll interval —
/// except across an observation gap (app closed, machine asleep), where the ambiguous delta
/// is excluded and the affected window flagged incomplete rather than silently wrong.
/// Windows that themselves span the gap absorb the whole delta, which is exactly right: the
/// scanner back-fills offline burn from the transcripts.
/// </summary>
internal static class LimitWindowTracker
{
    /// <summary>
    /// How far <c>resets_at</c> may drift between polls and still mean the same window — the
    /// API can jitter by seconds without anything actually resetting.
    /// </summary>
    internal static readonly TimeSpan ResetJitter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The longest gap between samples that still counts as continuous observation: two
    /// missed polls at the slowest cadence (10 minutes) plus slack, so one transient network
    /// failure can't get windows flagged as gapped. Beyond it the app wasn't watching, and a
    /// window boundary inside the gap makes the adjacent records best-effort, not exact.
    /// </summary>
    internal static readonly TimeSpan MaxObservationGap = TimeSpan.FromMinutes(25);

    internal sealed record TrackResult(
        LimitLogSample Sample,
        IReadOnlyList<LimitWindowRecord> Finalized,
        LimitLogState NewState);

    /// <summary>One successful poll: emits the sample, finalizes any ended windows, advances the state.</summary>
    internal static TrackResult Observe(
        LimitLogState state,
        DateTimeOffset now,
        UsageResponse usage,
        IReadOnlyDictionary<string, ModelTokens>? cumulativeTokens,
        ClaudePlan? plan)
    {
        var snapshots = Snapshots(usage);
        var sample = new LimitLogSample(now, snapshots, cumulativeTokens);

        var deltas = Deltas(state.LastTokens, cumulativeTokens);
        var continuous = state.LastSampleAt is { } lastAt && now - lastAt <= MaxObservationGap;

        var finalized = new List<LimitWindowRecord>();
        var open = new List<ActiveWindowState>();

        // Stored windows not consumed by a snapshot below fall through to the expiry sweep.
        // Built defensively (last write wins) so a duplicate key in a hand-edited state file
        // can't throw out of the poll path.
        var remaining = new Dictionary<(string, string), ActiveWindowState>();
        foreach (var w in state.Windows)
            remaining[KeyOf(w.Kind, w.ScopeModel)] = w;

        foreach (var snap in Dedup(snapshots))
        {
            // No usable reset time: can't tell which window the percent belongs to. Any stored
            // window for this key is left for the sweep to age out.
            if (snap.ResetsAt is not { } resets)
                continue;

            var key = KeyOf(snap.Kind, snap.ScopeModel);
            remaining.TryGetValue(key, out var current);

            if (current is not null && Magnitude(resets - current.ResetsAt) <= ResetJitter)
            {
                remaining.Remove(key);
                if (current.ResetsAt <= now)
                {
                    // Idle expiry: the window ended and the API echoes the old resets_at until
                    // new usage opens the next one (see UsageBucket.IsExpired). Finalize once;
                    // nothing opens until a future resets_at appears.
                    finalized.Add(Finalize(current, plan));
                }
                else
                {
                    open.Add(Update(current, snap, now, deltas, plan));
                }

                continue;
            }

            if (current is not null)
            {
                // resets_at moved: the old window ended at its own reset time.
                remaining.Remove(key);
                finalized.Add(Finalize(current, plan));
            }

            if (resets > now)
                open.Add(Open(snap, resets, now, state.LastSampleAt, continuous, deltas, plan));
        }

        // Sweep: stored windows whose key vanished from the payload (or carried no reset time)
        // still end when their reset time passes. The rest keep accruing burn — the limit not
        // being reported doesn't stop usage counting against it. LastSeenAt is deliberately
        // not touched: it tracks when the limit itself was last reported, which is what the
        // offline staleness rule in Finalize keys off.
        foreach (var w in remaining.Values)
        {
            if (w.ResetsAt <= now)
                finalized.Add(Finalize(w, plan));
            else
                open.Add(w with { TokensByModel = Accumulate(w.TokensByModel, deltas) });
        }

        var newState = state with
        {
            Version = LimitLogState.CurrentVersion,
            LastSampleAt = now,
            // A scanner outage keeps the old baseline, so the delta resumes correctly (spanning
            // the outage) when totals come back rather than rebasing to zero.
            LastTokens = cumulativeTokens is null
                ? state.LastTokens
                : new Dictionary<string, ModelTokens>(cumulativeTokens, StringComparer.OrdinalIgnoreCase),
            Windows = open,
        };

        return new TrackResult(sample, finalized, newState);
    }

    /// <summary>
    /// Startup catch-up: finalizes every stored window whose reset time passed while the app
    /// wasn't running. <see cref="Finalize"/>'s staleness rule flags them incomplete (the last
    /// sample predates the window's end by more than the observation gap).
    /// </summary>
    internal static (IReadOnlyList<LimitWindowRecord> Finalized, LimitLogState NewState) FinalizeExpired(
        LimitLogState state, DateTimeOffset now, ClaudePlan? plan)
    {
        var finalized = new List<LimitWindowRecord>();
        var kept = new List<ActiveWindowState>();
        foreach (var w in state.Windows)
        {
            if (w.ResetsAt <= now)
                finalized.Add(Finalize(w, plan));
            else
                kept.Add(w);
        }

        return (finalized, state with { Windows = kept });
    }

    /// <summary>
    /// The limits as logged: every <c>limits[]</c> entry verbatim, or — for a legacy payload
    /// with no <c>limits[]</c> — the 5-hour/7-day pair synthesized under the canonical kinds,
    /// mirroring <see cref="LimitDisplay.BuildRows"/>'s fallback so window tracking still has
    /// reset times to work from.
    /// </summary>
    internal static IReadOnlyList<LimitSnapshot> Snapshots(UsageResponse usage)
    {
        var limits = usage.Limits;
        if (limits is null || limits.Count == 0)
        {
            var legacy = new List<LimitSnapshot>(2);
            if (usage.FiveHour is not null)
                legacy.Add(new LimitSnapshot(
                    "session", null, usage.FiveHour.UtilizationPct, null, usage.FiveHour.ResetAt, null, null));
            if (usage.SevenDay is not null)
                legacy.Add(new LimitSnapshot(
                    "weekly_all", null, usage.SevenDay.UtilizationPct, null, usage.SevenDay.ResetAt, null, null));
            return legacy;
        }

        return limits
            .Select(l => new LimitSnapshot(
                l.Kind, l.Group, l.Percent, l.Severity, l.ResetsAt, l.IsActive,
                l.Scope?.Model?.DisplayName))
            .ToList();
    }

    // De-dup exact (kind, scope) repeats for tracking, keeping the higher percent — the same
    // rule as LimitDisplay.Dedup, so tracking and display agree on which buckets exist. The
    // sample still logs the payload verbatim; only window tracking dedups.
    private static List<LimitSnapshot> Dedup(IReadOnlyList<LimitSnapshot> snapshots)
    {
        var deduped = new List<LimitSnapshot>();
        var indexByKey = new Dictionary<(string, string), int>();
        foreach (var snap in snapshots)
        {
            var key = KeyOf(snap.Kind, snap.ScopeModel);
            if (indexByKey.TryGetValue(key, out var existing))
            {
                // A percent without a reset time can't drive window tracking, so an entry that
                // carries one always beats an entry that doesn't; between equally usable
                // entries, the higher percent wins (LimitDisplay's rule).
                var incumbent = deduped[existing];
                var replace = incumbent.ResetsAt is null
                    ? snap.ResetsAt is not null || (snap.Percent ?? 0) > (incumbent.Percent ?? 0)
                    : snap.ResetsAt is not null && (snap.Percent ?? 0) > (incumbent.Percent ?? 0);
                if (replace)
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

    // Per-model growth since the last observation, clamped at zero per category. Null when
    // there is nothing to attribute: no totals this poll, or no baseline yet (the very first
    // observation is a baseline, not a burst).
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

    private static ActiveWindowState Update(
        ActiveWindowState w, LimitSnapshot snap, DateTimeOffset now,
        Dictionary<string, ModelTokens>? deltas, ClaudePlan? plan)
    {
        return w with
        {
            PeakPercent = snap.Percent is { } pct ? Math.Max(w.PeakPercent, pct) : w.PeakPercent,
            LastPercent = snap.Percent ?? w.LastPercent,
            LastSeenAt = now,
            SampleCount = w.SampleCount + 1,
            PlanChanged = w.PlanChanged || plan != w.PlanAtStart,
            TokensByModel = Accumulate(w.TokensByModel, deltas),
        };
    }

    private static ActiveWindowState Open(
        LimitSnapshot snap, DateTimeOffset resets, DateTimeOffset now, DateTimeOffset? lastSampleAt,
        bool continuous, Dictionary<string, ModelTokens>? deltas, ClaudePlan? plan)
    {
        var length = WindowLengthOf(snap);
        var start = length is { } len ? resets - len : now;

        // Covered = observation was running when this window opened, so its burn is
        // attributable from the start (the delta straddling the boundary is included; error is
        // bounded by one poll interval). Not covered = the window was already in flight when
        // observation (re)started — the pre-observation burn is unknowable, so the ambiguous
        // delta is excluded and the record flagged, not guessed.
        var covered = continuous && lastSampleAt is { } last && start >= last - ResetJitter;

        return new ActiveWindowState(
            snap.Kind, snap.Group, snap.ScopeModel, resets, start,
            StartApprox: length is null,
            PeakPercent: snap.Percent ?? 0,
            LastPercent: snap.Percent ?? 0,
            LastSeenAt: now,
            SampleCount: 1,
            PlanAtStart: plan,
            PlanChanged: false,
            TokensByModel: covered
                ? Accumulate(new Dictionary<string, ModelTokens>(StringComparer.OrdinalIgnoreCase), deltas)
                : new Dictionary<string, ModelTokens>(StringComparer.OrdinalIgnoreCase),
            Incomplete: !covered,
            IncompleteReason: covered ? null : LimitWindowRecord.ReasonGapSpannedBoundary);
    }

    private static LimitWindowRecord Finalize(ActiveWindowState w, ClaudePlan? plan)
    {
        // If the last sample predates the window's end by more than the observation gap, the
        // app wasn't watching when it ended: peak/last/tokens stop at the gap, so the record
        // is best-effort. Flagged rather than silently wrong (the ticket's posture). An
        // incomplete flag set at open (gap_spanned_boundary) survives with its own reason.
        var end = w.ResetsAt;
        var offline = end - w.LastSeenAt > MaxObservationGap;

        return new LimitWindowRecord(
            w.Kind, w.Group, w.ScopeModel, w.Start, end, w.StartApprox,
            w.PeakPercent, w.LastPercent, w.LastSeenAt, w.SampleCount,
            plan, w.PlanAtStart,
            w.PlanChanged || plan != w.PlanAtStart,
            new Dictionary<string, ModelTokens>(w.TokensByModel, StringComparer.OrdinalIgnoreCase),
            Incomplete: w.Incomplete || offline,
            IncompleteReason: w.IncompleteReason
                ?? (offline ? LimitWindowRecord.ReasonOfflineAtWindowEnd : null));
    }

    private static Dictionary<string, ModelTokens> Accumulate(
        IReadOnlyDictionary<string, ModelTokens> current, Dictionary<string, ModelTokens>? deltas)
    {
        var result = new Dictionary<string, ModelTokens>(current, StringComparer.OrdinalIgnoreCase);
        if (deltas is null)
            return result;

        foreach (var (model, delta) in deltas)
            result[model] = (result.TryGetValue(model, out var t) ? t : ModelTokens.Zero).Plus(delta);

        return result;
    }

    // The canonical window length for a limit, from its kind first and its group as the
    // fallback — the same vocabulary LimitDisplay uses. Null = unknown kind: the window is
    // tracked with an approximate start rather than dropped.
    private static TimeSpan? WindowLengthOf(LimitSnapshot snap)
    {
        var kind = Normalize(snap.Kind);
        var group = Normalize(snap.Group);

        if (kind == "session" || group is "session" or "five_hour")
            return UsageWindows.FiveHour;
        if (kind is "weekly_all" or "weekly_scoped" || group is "weekly" or "seven_day")
            return UsageWindows.SevenDay;
        return null;
    }

    private static (string, string) KeyOf(string? kind, string? scopeModel) =>
        (Normalize(kind), Normalize(scopeModel));

    private static string Normalize(string? value) => value?.Trim().ToLowerInvariant() ?? "";

    private static TimeSpan Magnitude(TimeSpan span) => span < TimeSpan.Zero ? -span : span;
}
