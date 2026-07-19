namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;

/// <summary>
/// Raises desktop notifications as Claude usage gets risky. The 5-hour alerts are
/// <em>pace-aware</em>: rather than a fixed percentage, the primary warning fires when your
/// usage relative to how far through the reset window you are means you're on track to run out
/// before it resets (see <see cref="Pace"/>). A separate absolute near-cap backstop still fires
/// a critical "almost out" alert near the limit, regardless of pace.
///
/// Weekly alerts cover <em>every</em> weekly bucket the API reports (issue #98) — the overall
/// 7-day cap and each per-model cap ("Fable weekly"), which can run out first — each with its
/// own fired/hysteresis state so one bucket's alert never masks another's. They reuse the
/// existing weekly-warning and near-cap thresholds; see
/// <see cref="LimitDisplay.WeeklyAlertTargets"/> for where the buckets come from.
/// The rate-limit-reset notice is unchanged.
/// </summary>
public sealed class AlertManager
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Action<string, string, ToolTipIcon>? _onNotification;

    // Only warn about pace once a meaningful chunk is used, so a tiny early burst (e.g. 8% in the
    // first few minutes) doesn't cry wolf even though its instantaneous pace is high.
    private const double PaceAlertMinUsagePct = 20.0;

    // Hysteresis so a value hovering right at a trigger doesn't flap fired/cleared each poll.
    private const double NearCapClearMarginPct = 3.0;
    private const double PaceClearMargin = 0.15;

    // A drop of at least this many points signals a window reset. Utilization only accumulates
    // within a fixed window, so a meaningful decrease means the window rolled over. Detecting the
    // drop (rather than an absolute "< 5%" floor) catches resets first observed above 5% — polling
    // is minutes apart, so a reset is routinely seen already at 8-15% of the new window.
    private const double ResetDropPct = 15.0;

    // Whether alerts are currently snoozed (issue #14) — latched from settings at the top of
    // each Check. While snoozed, TryShowNotification suppresses the balloon AND returns false
    // so no "fired" latch is set: for the level-triggered alerts (near-cap, pace, 7-day) the
    // condition still holding when the snooze expires fires the deferred alert on the next
    // poll. The edge-triggered reset NOTICE is the exception — its trigger is the drop
    // itself, so one observed while snoozed is deliberately dropped, not deferred (a stale
    // "your limit reset" balloon an hour later would be noise).
    private bool _snoozed;

    // 5-hour alert state.
    private bool _paceWarningFired;
    private bool _nearCapFired;

    // Per-weekly-bucket alert state, keyed by WeeklyAlertTarget.Key (the bucket's kind, or its
    // model for a per-model cap) so the flags follow the right bucket as percentages — and the
    // tightest-first ordering — change between polls.
    private sealed class WeeklyBucketState
    {
        public bool WarningFired;
        public bool CriticalFired;
    }

    private readonly Dictionary<string, WeeklyBucketState> _weeklyState =
        new(StringComparer.OrdinalIgnoreCase);

    // Alerts raised during the current Check, flushed as ONE balloon at the end. NotifyIcon
    // shows a single balloon at a time — a second call replaces the first instantly — so
    // emitting per alert would destroy all but the last while still latching every one as
    // "fired", losing them permanently. Combining is the same fix the budget alerts use
    // (see TrayApplication.OnLocalScanCompleted).
    private readonly List<(string Title, string Text, ToolTipIcon Icon)> _pending = [];

    // Shared state.
    private bool _resetNotificationFired;
    private double _previousFiveHourPct = -1;

    public AlertManager(NotifyIcon notifyIcon)
        : this(notifyIcon, null)
    {
    }

    internal AlertManager(NotifyIcon notifyIcon, Action<string, string, ToolTipIcon>? onNotification)
    {
        _notifyIcon = notifyIcon;
        _onNotification = onNotification;
    }

    public void Check(UsageResponse usage, AppSettings settings)
    {
        if (!settings.Notifications.Enabled)
            return;

        _pending.Clear();

        // Snoozed alerts still run the whole state machine (reset detection, re-arm and
        // hysteresis logic must not go stale) — only the balloons are held back.
        _snoozed = settings.Notifications.IsSnoozed(DateTimeOffset.UtcNow);

        var thresholds = settings.AlertThresholds;

        if (usage.FiveHour is { } fiveHour)
        {
            var pct = fiveHour.UtilizationPct;

            // A genuine window reset (usage dropped sharply from a meaningful level) re-arms the
            // 5-hour alerts so the next window fires fresh. This is independent of whether the reset
            // *notice* is enabled — only the notification itself is gated on NotifyOnReset.
            var isReset = _previousFiveHourPct >= 20 && pct <= _previousFiveHourPct - ResetDropPct;
            if (isReset)
            {
                if (settings.Notifications.NotifyOnReset && !_resetNotificationFired
                    && TryShowNotification(
                        "Rate Limit Reset",
                        "Your 5-hour rate limit has reset — a fresh window has started.",
                        ToolTipIcon.Info))
                {
                    _resetNotificationFired = true;
                }

                _paceWarningFired = false;
                _nearCapFired = false;
            }
            else
            {
                _resetNotificationFired = false;
            }

            CheckFiveHourAlerts(fiveHour, thresholds);
            _previousFiveHourPct = pct;
        }

        CheckWeeklyAlerts(usage, thresholds);

        Flush();
    }

    /// <summary>
    /// Emits everything raised this poll as a single balloon: one alert shows verbatim,
    /// several are combined under a shared title (worst icon wins) so none is overwritten.
    /// </summary>
    private void Flush()
    {
        if (_pending.Count == 0)
            return;

        var (title, text, icon) = _pending.Count == 1
            ? _pending[0]
            : ("Usage alerts",
               string.Join("\n", _pending.Select(a => a.Text)),
               _pending.Max(a => a.Icon));

        _pending.Clear();

        if (_onNotification is not null)
            _onNotification(title, text, icon);
        else
            _notifyIcon.ShowBalloonTip(5000, title, text, icon);
    }

    private void CheckFiveHourAlerts(UsageBucket fiveHour, AlertThresholds thresholds)
    {
        var pct = fiveHour.UtilizationPct;

        // Near-cap backstop (critical) takes priority — once you're almost out, pace is moot.
        if (pct >= thresholds.NearCapWarning)
        {
            if (!_nearCapFired
                && TryShowNotification(
                    "Almost Out",
                    $"5-hour usage at {pct:F0}% — you're nearly out for this window.",
                    ToolTipIcon.Error))
            {
                _nearCapFired = true;
                _paceWarningFired = true; // don't also fire a pace warning on the way up
            }

            return;
        }

        // Dropped clear of the near-cap (with hysteresis) → let it fire again later.
        if (pct < thresholds.NearCapWarning - NearCapClearMarginPct)
            _nearCapFired = false;

        // Pace early-warning: on track to run out before the window resets.
        if (thresholds.PaceAlertsEnabled && pct >= PaceAlertMinUsagePct
            && fiveHour.ElapsedFraction(UsageWindows.FiveHour) is { } fraction)
        {
            var ratio = Pace.Ratio(pct, fraction);
            var trigger = thresholds.PaceRatioTrigger;

            if (ratio >= trigger)
            {
                if (!_paceWarningFired
                    && TryShowNotification(
                        "On Track to Run Out", PaceMessage(pct, fraction, fiveHour), ToolTipIcon.Warning))
                {
                    _paceWarningFired = true;
                }
            }
            else if (ratio < trigger - PaceClearMargin)
            {
                _paceWarningFired = false;
            }
        }
        else if (pct < PaceAlertMinUsagePct)
        {
            // Below the floor (e.g. just after a reset) — re-arm the pace warning.
            _paceWarningFired = false;
        }
    }

    private static string PaceMessage(double pct, double fraction, UsageBucket fiveHour)
    {
        var elapsedPct = fraction * 100;
        return $"{pct:F0}% used at only {elapsedPct:F0}% through the window — "
            + $"{fiveHour.FormatResetCountdown()}. Slow down to avoid running out early.";
    }

    /// <summary>
    /// Runs the weekly alerts over every weekly bucket the response reports — the overall cap
    /// and each per-model one. Each keeps independent state, so a model cap crossing its
    /// threshold alerts even while the overall cap is already latched.
    /// </summary>
    private void CheckWeeklyAlerts(UsageResponse usage, AlertThresholds thresholds)
    {
        // State for a bucket the API stops reporting is deliberately kept. It is one small
        // entry per bucket ever seen — bounded by the models on the account — whereas dropping
        // it would re-fire an identical alert the moment a bucket blinks out of one poll and
        // returns, which happens far more often than a cap genuinely disappearing. A cap that
        // truly resets re-arms itself by falling below the warning level (see CheckWeeklyBucket).
        foreach (var target in LimitDisplay.WeeklyAlertTargets(usage))
            CheckWeeklyBucket(target, thresholds);
    }

    private void CheckWeeklyBucket(WeeklyAlertTarget target, AlertThresholds thresholds)
    {
        if (!_weeklyState.TryGetValue(target.Key, out var state))
            _weeklyState[target.Key] = state = new WeeklyBucketState();

        var pct = target.Bucket.UtilizationPct;

        // Critical takes priority, mirroring the 5-hour near-cap-over-pace ordering: once a cap
        // is nearly spent the warning level is moot. Anthropic's own "critical" severity is an
        // escalation floor — it raises an at-risk bucket to critical below our numeric near-cap,
        // since the API knows things the percentage alone doesn't say. It only escalates a
        // bucket that already warrants a warning, so a severity blip at 10% used can't produce
        // an incoherent "critical at 10%" balloon.
        var apiCritical = target.Severity == LimitSeverity.Critical && pct >= thresholds.SevenDayWarning;
        if (pct >= thresholds.NearCapWarning || apiCritical)
        {
            if (!state.CriticalFired
                && TryShowNotification(
                    "Weekly Limit Critical", WeeklyMessage(target, pct), ToolTipIcon.Error))
            {
                state.CriticalFired = true;
                state.WarningFired = true; // don't also fire a warning on the way up
            }

            return;
        }

        // Re-arm the critical only once the bucket is clear of BOTH triggers: below the numeric
        // near-cap by the hysteresis margin, and below the level where API severity could
        // escalate it again. Clearing on the numeric margin alone would let a severity that
        // oscillates between warning and critical fire an Error balloon every other poll.
        if (pct < thresholds.NearCapWarning - NearCapClearMarginPct && pct < thresholds.SevenDayWarning)
            state.CriticalFired = false;

        if (pct >= thresholds.SevenDayWarning)
        {
            if (!state.WarningFired
                && TryShowNotification(
                    "Weekly Usage Warning", WeeklyMessage(target, pct), ToolTipIcon.Warning))
            {
                state.WarningFired = true;
            }
        }
        else
        {
            // Below the warning level — which is where a weekly reset lands the bucket — so the
            // next climb alerts fresh. This is why weekly buckets need no drop-detection of
            // their own, unlike the 5-hour window.
            state.WarningFired = false;
        }
    }

    private static string WeeklyMessage(WeeklyAlertTarget target, double pct) =>
        $"{target.Noun} usage at {pct:F0}% — {target.Bucket.FormatResetCountdown()}.";

    /// <summary>
    /// Queues an alert for this poll's balloon unless alerts are snoozed. Returns whether it
    /// will be shown, so callers only latch their "fired" flags for alerts the user actually
    /// sees — a queued alert always reaches <see cref="Flush"/>, so queuing is as good as shown.
    /// </summary>
    private bool TryShowNotification(string title, string text, ToolTipIcon icon)
    {
        if (_snoozed)
            return false;

        _pending.Add((title, text, icon));
        return true;
    }
}
