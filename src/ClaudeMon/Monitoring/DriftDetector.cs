namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;

/// <summary>
/// The throttle-drift state machine (issue #186), pure by construction like
/// <see cref="LimitWindowTracker"/> and <see cref="CapacityEstimator"/>. It watches #185's
/// implied-capacity estimates — deliberately those, not raw per-window capacities, because the
/// estimator has already filtered out foreign-surface contamination that would read as false
/// drift — and answers the epic's headline question: has capacity for the same plan quietly
/// shrunk?
///
/// Mechanics: at most one point per limit key per UTC day (the ring barely moves between
/// polls, so daily sampling decorrelates the series), only Medium+ estimates recorded. The
/// baseline is the median of the trailing <see cref="BaselineDays"/> days' points under the
/// current plan, recorded after any acknowledgment, excluding today — and at least
/// <see cref="MinBaselinePoints"/> of them, so a cold start can never trigger. Drift = today's
/// confident estimate below baseline × (1 − threshold); one alert per episode, re-armed by
/// recovery above a hysteresis band or by acknowledgment (which excludes the pre-ack points,
/// so the baseline rebuilds from the accepted level). A plan change is structurally excluded
/// twice over: points are plan-filtered here, and #185 clears its rings on a change so no
/// confident estimate even exists for a while.
/// </summary>
internal static class DriftDetector
{
    /// <summary>Fewer confident baseline points than this and the detector stays silent.</summary>
    internal const int MinBaselinePoints = 5;

    /// <summary>Days of points kept; the baseline window plus slack for the daily dedupe.</summary>
    internal const int RetentionDays = 45;

    /// <summary>The trailing window the baseline median is computed over.</summary>
    internal const int BaselineDays = 30;

    /// <summary>
    /// Recovery hysteresis, in percentage points on the threshold: with a 20% threshold and 5
    /// points of hysteresis, drift latches below 80% of baseline and clears only at or above
    /// 85% — so capacity hovering at the trigger can't fire an episode per wobble.
    /// </summary>
    internal const double HysteresisPoints = 5.0;

    /// <summary>
    /// One evaluation pass: records today's confident estimates, prunes old points, and
    /// decides per key whether a drift episode starts, continues, or recovers.
    /// <paramref name="canNotify"/> is the caller's alert gate (master toggle, drift toggle,
    /// snooze): when false the condition is still evaluated but nothing is emitted and the
    /// latch stays unset, so the alert defers to the next gate-open evaluation — the app's
    /// level-triggered convention (a snoozed alert is delayed, not dropped).
    /// </summary>
    internal static (IReadOnlyList<DriftAlertMessage> Alerts, DriftState NewState) Observe(
        DriftState state,
        DateTimeOffset now,
        IReadOnlyList<ImpliedCapacity> estimates,
        ClaudePlan? plan,
        double thresholdPercent,
        bool canNotify)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var alerts = new List<DriftAlertMessage>();
        var keys = new List<DriftKeyState>(state.Keys.Count);
        var seen = new HashSet<(string, string)>();
        foreach (var k in state.Keys)
        {
            if (seen.Add(KeyOf(k.Kind, k.ScopeModel)))
                keys.Add(k);
        }

        foreach (var estimate in estimates)
        {
            var key = KeyOf(estimate.Kind, estimate.ScopeModel);
            var index = keys.FindIndex(k => KeyOf(k.Kind, k.ScopeModel) == key);
            if (index < 0)
            {
                if (estimate.Confidence < CapacityConfidence.Medium)
                    continue;
                keys.Add(new DriftKeyState { Kind = estimate.Kind, ScopeModel = estimate.ScopeModel });
                index = keys.Count - 1;
            }

            var (updated, alert) = Advance(
                keys[index], estimate, plan, today, thresholdPercent, canNotify);
            keys[index] = updated;
            if (alert is not null)
                alerts.Add(alert);
        }

        // Retention prunes every key, not just the ones in this pass's estimates — a scope
        // name the API retires must not keep its points in drift.json forever.
        var cutoff = today.AddDays(-RetentionDays);
        for (var i = 0; i < keys.Count; i++)
        {
            if (keys[i].Points.Any(p => p.Date <= cutoff))
                keys[i] = keys[i] with { Points = keys[i].Points.Where(p => p.Date > cutoff).ToList() };
        }

        return (alerts, state with { Version = DriftState.CurrentVersion, Keys = keys });
    }

    /// <summary>
    /// The user has seen the evidence (opened the Limit history tab): clear every latched
    /// episode and stamp the acknowledgment, so the baseline rebuilds from the accepted level
    /// and only a further material drop fires again. Returns whether anything changed.
    /// </summary>
    internal static (bool Changed, DriftState NewState) Acknowledge(DriftState state, DateTimeOffset now)
    {
        if (!state.Keys.Any(k => k.Notified))
            return (false, state);

        return (true, state with
        {
            Version = DriftState.CurrentVersion,
            Keys = state.Keys
                .Select(k => k.Notified
                    ? k with { Notified = false, AcknowledgedAt = now }
                    : k)
                .ToList(),
        });
    }

    private static (DriftKeyState Updated, DriftAlertMessage? Alert) Advance(
        DriftKeyState key, ImpliedCapacity estimate, ClaudePlan? plan,
        DateOnly today, double thresholdPercent, bool canNotify)
    {
        var points = new List<DriftPoint>(key.Points);
        var confident = estimate.Confidence >= CapacityConfidence.Medium;
        if (confident)
        {
            // At most one point per UTC day; a later poll the same day refines it (last wins).
            points.RemoveAll(p => p.Date == today);
            points.Add(new DriftPoint(today, estimate.CapacityWeightedTokens, plan));
        }

        key = key with { Points = points };

        // The baseline never includes today (a drifting today must not drag its own norm
        // down), never crosses a plan change, and never learns from pre-acknowledgment points.
        // Ack granularity is the whole UTC ack day — including a point refined later that same
        // day — so each acknowledgment costs one extra quiet day; deliberate, since the ack
        // day's estimate is the drifted one being accepted.
        var ackDate = key.AcknowledgedAt is { } ack ? DateOnly.FromDateTime(ack.UtcDateTime) : (DateOnly?)null;
        var baselinePoints = points
            .Where(p => p.Date < today
                && p.Date > today.AddDays(-BaselineDays)
                && p.Plan == plan
                && (ackDate is not { } a || p.Date > a))
            .Select(p => p.Capacity)
            .ToList();

        if (!confident || baselinePoints.Count < MinBaselinePoints)
            return (key, null); // Not enough ground to stand on — latch state untouched.

        var baseline = Median(baselinePoints);
        if (baseline <= 0)
            return (key, null);

        var current = estimate.CapacityWeightedTokens;
        var trigger = baseline * (1 - thresholdPercent / 100.0);
        var recovery = baseline * (1 - (thresholdPercent - HysteresisPoints) / 100.0);

        if (key.Notified)
        {
            // In an episode: only a genuine recovery (above the hysteresis band) re-arms.
            return current >= recovery
                ? (key with { Notified = false }, null)
                : (key, null);
        }

        if (current < trigger)
        {
            if (!canNotify)
                return (key, null); // Deferred: fires on the next gate-open pass if still true.

            var (title, text) = LimitHistoryText.DriftAlert(
                key.Kind, key.ScopeModel, current, baseline);
            return (
                key with { Notified = true },
                new DriftAlertMessage(title, text, key.Kind, key.ScopeModel));
        }

        return (key, null);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private static (string, string) KeyOf(string? kind, string? scopeModel) =>
        (kind?.Trim().ToLowerInvariant() ?? "", scopeModel?.Trim().ToLowerInvariant() ?? "");
}
