namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;

public sealed class AlertManager
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Action<string, string, ToolTipIcon>? _onNotification;

    // Threshold mode state
    private bool _fiveHourWarningFired;
    private bool _fiveHourCriticalFired;

    // Progressive mode state
    private readonly HashSet<int> _progressiveStepsFired = [];

    // Shared state
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

        if (usage.FiveHour is not null)
        {
            var pct = usage.FiveHour.UtilizationPct;

            // Check for reset (usage dropped significantly)
            if (settings.Notifications.NotifyOnReset
                && _previousFiveHourPct >= 20
                && pct < 5)
            {
                if (!_resetNotificationFired)
                {
                    ShowNotification(
                        "Rate Limit Reset",
                        "Your 5-hour rate limit has reset. Full capacity available.",
                        ToolTipIcon.Info);
                    _resetNotificationFired = true;
                }

                // Clear all firing states since we reset
                _fiveHourWarningFired = false;
                _fiveHourCriticalFired = false;
                _progressiveStepsFired.Clear();
            }
            else
            {
                _resetNotificationFired = false;
            }

            if (thresholds.Mode == AlertMode.Progressive)
                CheckProgressiveAlerts(pct, thresholds);
            else
                CheckThresholdAlerts(pct, thresholds);

            _previousFiveHourPct = pct;
        }

        if (usage.SevenDay is not null)
        {
            var pct = usage.SevenDay.UtilizationPct;

            if (pct >= thresholds.SevenDayWarning && !_sevenDayWarningFired)
            {
                ShowNotification(
                    "Weekly Usage Warning",
                    $"7-day usage at {pct:F0}% — weekly limit approaching.",
                    ToolTipIcon.Warning);
                _sevenDayWarningFired = true;
            }

            if (pct < thresholds.SevenDayWarning)
            {
                _sevenDayWarningFired = false;
            }
        }
    }

    private void CheckThresholdAlerts(double pct, AlertThresholds thresholds)
    {
        // Critical threshold (check first so we don't double-notify)
        if (pct >= thresholds.FiveHourCritical && !_fiveHourCriticalFired)
        {
            ShowNotification(
                "Usage Critical",
                $"5-hour usage at {pct:F0}% — approaching rate limit!",
                ToolTipIcon.Error);
            _fiveHourCriticalFired = true;
            _fiveHourWarningFired = true; // suppress warning too
        }
        // Warning threshold
        else if (pct >= thresholds.FiveHourWarning && !_fiveHourWarningFired)
        {
            ShowNotification(
                "Usage Warning",
                $"5-hour usage at {pct:F0}% — consider slowing down.",
                ToolTipIcon.Warning);
            _fiveHourWarningFired = true;
        }

        // Clear fired states when we drop below thresholds
        if (pct < thresholds.FiveHourWarning)
        {
            _fiveHourWarningFired = false;
            _fiveHourCriticalFired = false;
        }
        else if (pct < thresholds.FiveHourCritical)
        {
            _fiveHourCriticalFired = false;
        }
    }

    private void CheckProgressiveAlerts(double pct, AlertThresholds thresholds)
    {
        for (var step = thresholds.ProgressiveStartPct; step <= 100; step += 10)
        {
            if (pct >= step && _progressiveStepsFired.Add(step))
            {
                var icon = step >= 90 ? ToolTipIcon.Error : ToolTipIcon.Warning;
                var title = step >= 90 ? "Usage Critical" : "Usage Warning";
                ShowNotification(
                    title,
                    $"5-hour usage at {pct:F0}% — passed {step}% threshold.",
                    icon);
            }
            else if (pct < step)
            {
                _progressiveStepsFired.Remove(step);
            }
        }
    }

    private void ShowNotification(string title, string text, ToolTipIcon icon)
    {
        if (_onNotification is not null)
            _onNotification(title, text, icon);
        else
            _notifyIcon.ShowBalloonTip(5000, title, text, icon);
    }
}
