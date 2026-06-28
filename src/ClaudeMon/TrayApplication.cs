namespace ClaudeMon;

using System.Diagnostics;
using System.Drawing;
using ClaudeMon.Configuration;
using ClaudeMon.Monitoring;
using ClaudeMon.Services;
using ClaudeMon.UI;

public sealed class TrayApplication : IDisposable
{
    // How often to check GitHub for a newer release while running.
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly UsageMonitor _monitor;
    private readonly ClaudeApiClient _apiClient;
    private readonly TokenRefresher _tokenRefresher;
    private readonly Logger _logger;
    private readonly UsageHistoryStore _history;
    private readonly ConfigManager _configManager;
    private readonly SynchronizationContext _syncContext;
    private readonly FlyoutPanel _flyout;
    private readonly TaskbarOverlayWindow _taskbarOverlay;
    private readonly AlertManager _alertManager;
    private readonly UpdateChecker _updateChecker;
    private readonly System.Timers.Timer _updateTimer;
    private readonly CancellationTokenSource _updateCts = new();
    private readonly ToolStripMenuItem _downloadUpdateItem =
        new("Download update...") { Visible = false };
    private string? _updateUrl;
    private volatile bool _disposed;

    private static Version CurrentVersion =>
        typeof(TrayApplication).Assembly.GetName().Version ?? new Version(0, 0);

    public TrayApplication()
    {
        _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _configManager = new ConfigManager();
        _configManager.Load();

        _logger = new Logger();
        _logger.Info($"ClaudeMon {CurrentVersion} starting.");

        _history = new UsageHistoryStore();
        _history.Load();

        _apiClient = new ClaudeApiClient();
        _tokenRefresher = new TokenRefresher();
        var credentialReader = new CredentialReader();
        _monitor = new UsageMonitor(
            credentialReader, _apiClient, _configManager.Settings.PollInterval,
            _tokenRefresher, _logger, _history);
        _monitor.UsageUpdated += OnUsageUpdated;

        _flyout = new FlyoutPanel();

        _taskbarOverlay = new TaskbarOverlayWindow();
        _taskbarOverlay.SetColors(
            _configManager.Settings.TaskbarDisplay.LabelColor,
            _configManager.Settings.TaskbarDisplay.NumberColor);
        _taskbarOverlay.SetShowSevenDay(_configManager.Settings.TaskbarDisplay.ShowSevenDay);
        _taskbarOverlay.SetEnabled(_configManager.Settings.TaskbarDisplay.Enabled);

        _contextMenu = CreateContextMenu();
        _notifyIcon = new NotifyIcon
        {
            Icon = IconRenderer.RenderErrorIcon(),
            Text = "ClaudeMon - Starting...",
            Visible = true,
            ContextMenuStrip = _contextMenu,
        };
        _notifyIcon.MouseClick += OnTrayMouseClick;
        _alertManager = new AlertManager(_notifyIcon);

        _updateChecker = new UpdateChecker();
        _updateTimer = new System.Timers.Timer(UpdateCheckInterval.TotalMilliseconds)
        {
            AutoReset = true,
        };
        _updateTimer.Elapsed += (_, _) => _ = CheckForUpdatesAsync(manual: false);

        _monitor.Start();

        if (_configManager.Settings.CheckForUpdates)
        {
            _updateTimer.Start();
            _ = CheckForUpdatesAsync(manual: false);
        }
    }

    private void OnUsageUpdated(object? sender, UsageUpdatedEventArgs e)
    {
        if (_disposed) return;

        _syncContext.Post(_ =>
        {
            if (_disposed) return;

            // Auth expired: show an actionable message instead of usage numbers.
            // SetError carries the last (now-stale) usage, so this must precede the
            // usage branch or we'd render old percentages with no hint of what's wrong.
            if (e.Status == MonitorStatus.AuthError)
            {
                var oldIcon = _notifyIcon.Icon;
                _notifyIcon.Icon = IconRenderer.RenderErrorIcon();
                oldIcon?.Dispose();
                _notifyIcon.Text = $"ClaudeMon\n{MonitorStatusText.SignInExpired}";
                _taskbarOverlay.ShowSignInExpired();
                return;
            }

            if (e.Usage is not null)
            {
                var fiveHour = e.Usage.FiveHour;
                var sevenDay = e.Usage.SevenDay;

                if (fiveHour is not null)
                {
                    var oldIcon = _notifyIcon.Icon;
                    _notifyIcon.Icon = IconRenderer.RenderUsageIcon(fiveHour.UtilizationPct);
                    oldIcon?.Dispose();

                    _taskbarOverlay.UpdateUsage(fiveHour.UtilizationPct, sevenDay?.UtilizationPct);
                }

                var lines = new List<string> { "ClaudeMon" };

                if (fiveHour is not null)
                    lines.Add($"5hr: {fiveHour.UtilizationPct:F0}% ({fiveHour.FormatResetCountdown()})");

                if (sevenDay is not null)
                    lines.Add($"7day: {sevenDay.UtilizationPct:F0}% ({sevenDay.FormatResetCountdown()})");

                if (e.Status != MonitorStatus.Connected)
                    lines.Add($"[{e.Status}]");

                _notifyIcon.Text = string.Join("\n", lines);

                _alertManager.Check(e.Usage, _configManager.Settings);
            }
            else if (e.Error is not null)
            {
                var oldIcon = _notifyIcon.Icon;
                _notifyIcon.Icon = IconRenderer.RenderErrorIcon();
                oldIcon?.Dispose();
                _notifyIcon.Text = $"ClaudeMon\n{Truncate(e.Error, 100)}";
            }
        }, null);
    }

    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;

        if (_flyout.Visible)
        {
            _flyout.Hide();
        }
        else
        {
            var fiveHourTrend = _history.Recent(TimeSpan.FromHours(5))
                .Select(s => s.FiveHourPct)
                .ToList();
            _flyout.UpdateData(_monitor.LastUsage, _monitor.Status, _monitor.LastUpdated, fiveHourTrend);
            _flyout.ShowNear(Cursor.Position);
        }
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh Now", null, async (_, _) => await _monitor.RefreshNowAsync());
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());

        // Hidden until a check finds a newer release; clicking opens the release page.
        _downloadUpdateItem.Click += (_, _) => OpenUpdatePage();
        menu.Items.Add(_downloadUpdateItem);
        menu.Items.Add("Check for updates", null, async (_, _) => await CheckForUpdatesAsync(manual: true));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("View logs", null, (_, _) => OpenLogs());
        menu.Items.Add("About ClaudeMon", null, (_, _) => ShowAbout());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        return menu;
    }

    /// <summary>
    /// Queries GitHub for a newer release. On a manual check the user always gets
    /// feedback (update / up-to-date / failed); automatic checks stay silent unless a
    /// new version is found, and only balloon once per version (tracked in config).
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        // Always invoked fire-and-forget (timer tick / menu click), so it must never let
        // an exception escape. CheckAsync is already non-throwing, but guard regardless.
        try
        {
            var result = await _updateChecker.CheckAsync(CurrentVersion, _updateCts.Token);

            _syncContext.Post(_ =>
            {
                if (_disposed) return;

                if (result.UpdateAvailable && result.LatestVersion is not null)
                {
                    var v = result.LatestVersion;
                    var version = $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
                    _updateUrl = result.ReleaseUrl;
                    _downloadUpdateItem.Text = $"Download update (v{version})...";
                    _downloadUpdateItem.Visible = true;

                    // All _configManager.Update calls run on the UI thread (this posted
                    // callback and the Settings dialog), so this read-modify-write is safe.
                    var alreadyNotified = _configManager.Settings.LastNotifiedVersion == version;
                    if (manual || !alreadyNotified)
                    {
                        _notifyIcon.ShowBalloonTip(
                            5000,
                            "Update available",
                            $"ClaudeMon v{version} is available. Use \"Download update\" in the menu.",
                            ToolTipIcon.Info);
                    }

                    if (!alreadyNotified)
                        _configManager.Update(_configManager.Settings with { LastNotifiedVersion = version });
                }
                else if (manual && result.ErrorMessage is null)
                {
                    _notifyIcon.ShowBalloonTip(
                        5000, "ClaudeMon", "You're on the latest version.", ToolTipIcon.Info);
                }
                else if (manual)
                {
                    _notifyIcon.ShowBalloonTip(
                        5000, "Update check failed", "Couldn't check for updates right now.", ToolTipIcon.Warning);
                }
            }, null);
        }
        catch (OperationCanceledException)
        {
            // App is shutting down — ignore.
        }
        catch
        {
            // Update checks are best-effort; never crash the app over one.
        }
    }

    private void OpenUpdatePage()
    {
        // Only ever shell out to an http(s) URL (GitHub's release page), never an
        // arbitrary scheme, even though the value comes from a trusted HTTPS response.
        if (!Uri.TryCreate(_updateUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // Best-effort: if the shell can't open the URL there's nothing useful to do.
        }
    }

    private void OpenLogs()
    {
        try
        {
            // Open the log file if it exists; otherwise open the logs folder so the
            // user still lands somewhere useful before anything has been logged.
            if (File.Exists(_logger.FilePath))
            {
                Process.Start(new ProcessStartInfo(_logger.FilePath) { UseShellExecute = true });
            }
            else
            {
                Directory.CreateDirectory(_logger.DirectoryPath);
                Process.Start(new ProcessStartInfo(_logger.DirectoryPath) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            // Best-effort: record why the shell couldn't open the log (path is
            // app-controlled, so this is a rare environment issue, not user input).
            _logger.Warn($"Could not open logs: {ex.Message}");
        }
    }

    private void ShowSettings()
    {
        using var form = new SettingsForm(_configManager);
        if (form.ShowDialog() == DialogResult.OK)
        {
            _monitor.UpdateInterval(_configManager.Settings.PollInterval);
            _taskbarOverlay.SetColors(
                _configManager.Settings.TaskbarDisplay.LabelColor,
                _configManager.Settings.TaskbarDisplay.NumberColor);
            _taskbarOverlay.SetShowSevenDay(_configManager.Settings.TaskbarDisplay.ShowSevenDay);
            _taskbarOverlay.SetEnabled(_configManager.Settings.TaskbarDisplay.Enabled);

            // Start/stop update checks live to match the toggle; check immediately when
            // newly enabled so the user doesn't wait up to a day for the first result.
            if (_configManager.Settings.CheckForUpdates)
            {
                if (!_updateTimer.Enabled)
                {
                    _updateTimer.Start();
                    _ = CheckForUpdatesAsync(manual: false);
                }
            }
            else
            {
                _updateTimer.Stop();
            }
        }
    }

    private void ShowAbout()
    {
        var version = typeof(TrayApplication).Assembly.GetName().Version;
        MessageBox.Show(
            $"ClaudeMon v{version?.ToString(3) ?? "0.0.1"}\n\n" +
            "Windows system tray monitor for Claude AI usage.\n\n" +
            "Monitors 5-hour and 7-day rate limits\n" +
            "for Claude Max subscribers.\n\n" +
            $"Status: {MonitorStatusText.Describe(_monitor.Status)}\n\n" +
            "github.com/badsonstudios/ClaudeMon",
            "About ClaudeMon",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    public void Dispose()
    {
        _disposed = true;
        _logger.Info("ClaudeMon shutting down.");
        _updateTimer.Stop();
        _updateCts.Cancel();
        _updateTimer.Dispose();
        _updateCts.Dispose();
        _updateChecker.Dispose();
        _monitor.Dispose();
        _apiClient.Dispose();
        _tokenRefresher.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _flyout.Dispose();
        _taskbarOverlay.Dispose();
    }
}
