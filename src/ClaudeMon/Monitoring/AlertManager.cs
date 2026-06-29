namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;

/// <summary>
/// Raises desktop notifications as Claude usage gets risky. The 5-hour alerts are
/// <em>pace-aware</em>: rather than a fixed percentage, the primary warning fires when your
/// usage relative to how far through the reset window you are means you're on track to run out
/// before it resets (see <see cref="Pace"/>). A separate absolute near-cap backstop still fires
/// a critical "almost out" alert near the limit, regardless of pace. The 7-day warning and the
/// rate-limit-reset notice are unchanged.
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

    // 5-hour alert state.
    private bool _paceWarningFired;
    private bool _nearCapFired;

    // Shared state.
    private bool _sevenDayWarningFired;
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

        var thresholds = settings.AlertThresholds;

        if (usage.FiveHour is { } fiveHour)
        {
            var pct = fiveHour.UtilizationPct;

            // A genuine window reset (usage dropped sharply from a meaningful level to near-zero)
            // re-arms the 5-hour alerts so the next window fires fresh. This is independent of
            // whether the reset *notice* is enabled — only the notification itself is gated on
            // NotifyOnReset.
            var isReset = _previousFiveHourPct >= 20 && pct < 5;
            if (isReset)
            {
                if (settings.Notifications.NotifyOnReset && !_resetNotificationFired)
                {
                    ShowNotification(
                        "Rate Limit Reset",
                        "Your 5-hour rate limit has reset. Full capacity available.",
                        ToolTipIcon.Info);
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

        if (usage.SevenDay is { } sevenDay)
            CheckSevenDayAlert(sevenDay.UtilizationPct, thresholds);
    }

    private void CheckFiveHourAlerts(UsageBucket fiveHour, AlertThresholds thresholds)
    {
        var pct = fiveHour.UtilizationPct;

        // Near-cap backstop (critical) takes priority — once you're almost out, pace is moot.
        if (pct >= thresholds.NearCapWarning)
        {
            if (!_nearCapFired)
            {
                ShowNotification(
                    "Almost Out",
                    $"5-hour usage at {pct:F0}% — you're nearly out for this window.",
                    ToolTipIcon.Error);
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
                if (!_paceWarningFired)
                {
                    ShowNotification("On Track to Run Out", PaceMessage(pct, fraction, fiveHour), ToolTipIcon.Warning);
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

    private void CheckSevenDayAlert(double pct, AlertThresholds thresholds)
    {
        if (pct >= thresholds.SevenDayWarning && !_sevenDayWarningFired)
        {
            ShowNotification(
                "Weekly Usage Warning",
                $"7-day usage at {pct:F0}% — weekly limit approaching.",
                ToolTipIcon.Warning);
            _sevenDayWarningFired = true;
        }

        if (pct < thresholds.SevenDayWarning)
            _sevenDayWarningFired = false;
    }

    private void ShowNotification(string title, string text, ToolTipIcon icon)
    {
        if (_onNotification is not null)
            _onNotification(title, text, icon);
        else
            _notifyIcon.ShowBalloonTip(5000, title, text, icon);
    }
}
