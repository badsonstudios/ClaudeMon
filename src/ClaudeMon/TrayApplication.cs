namespace ClaudeMon;

using System.Diagnostics;
using System.Drawing;
using ClaudeMon.Configuration;
using ClaudeMon.Models;
using ClaudeMon.Monitoring;
using ClaudeMon.Services;
using ClaudeMon.UI;

public sealed class TrayApplication : IDisposable
{
    // How often to check GitHub for a newer release while running.
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);

    // Recent window used to estimate the 5-hour burn rate ("current rate").
    private static readonly TimeSpan BurnRateWindow = TimeSpan.FromMinutes(30);

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
    private readonly TaskbarOverlayManager _taskbarOverlay;
    private readonly AlertManager _alertManager;
    private readonly UpdateChecker _updateChecker;
    private readonly UpdateInstaller _updateInstaller;
    private readonly System.Timers.Timer _updateTimer;
    private readonly CancellationTokenSource _updateCts = new();
    private readonly ToolStripMenuItem _downloadUpdateItem =
        new("Download update...") { Visible = false };
    private string? _updateUrl;
    private string? _latestVersion;
    private string? _installerUrl;
    private string? _checksumUrl;
    private bool _settingsOpen;
    private bool _updateDialogOpen;
    private bool _updateInstallInProgress;
    private volatile bool _disposed;

    private static Version CurrentVersion =>
        typeof(TrayApplication).Assembly.GetName().Version ?? new Version(0, 0);

    // Where "Get the update" / "Download update" land if a release somehow has no html_url.
    private const string ReleasesFallbackUrl = "https://github.com/badsonstudios/ClaudeMon/releases";

    /// <summary>
    /// Formats a version as the 3-part display/comparison form ("0.12.0"). The skip-this-version
    /// comparison matches persisted strings against this, so every version string in the update
    /// flow must come from here.
    /// </summary>
    private static string FormatVersion(Version v) => $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";

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

        _flyout = new FlyoutPanel(_logger);
        // The flyout's gear button opens Settings (the flyout hides itself when it loses focus).
        _flyout.SettingsRequested += (_, _) =>
        {
            _flyout.Hide();
            ShowSettings();
        };

        _taskbarOverlay = new TaskbarOverlayManager(_logger);
        _taskbarOverlay.SetColors(
            _configManager.Settings.TaskbarDisplay.LabelColor,
            _configManager.Settings.TaskbarDisplay.NumberColor);
        _taskbarOverlay.SetStyle(_configManager.Settings.TaskbarDisplay.Style);
        _taskbarOverlay.SetBarWidth(_configManager.Settings.TaskbarDisplay.BarWidth);
        _taskbarOverlay.SetSize(_configManager.Settings.TaskbarDisplay.SizePercent);
        _taskbarOverlay.SetColorMode(_configManager.Settings.ColorMode);
        _taskbarOverlay.SetDisplay(
            _configManager.Settings.TaskbarDisplay.ShowSessionUsage,
            _configManager.Settings.TaskbarDisplay.ShowWeeklyUsage,
            _configManager.Settings.TaskbarDisplay.ShowTimeToReset);
        _taskbarOverlay.SetHorizontalOffsets(
            _configManager.Settings.TaskbarDisplay.PrimaryHorizontalOffset,
            _configManager.Settings.TaskbarDisplay.HorizontalOffset);
        _taskbarOverlay.SetAllMonitors(_configManager.Settings.TaskbarDisplay.AllMonitors);
        _taskbarOverlay.SetEnabled(_configManager.Settings.TaskbarDisplay.Enabled);
        // Clicking any monitor's readout opens the detail flyout at the PRIMARY monitor's tray
        // corner (a deliberate, consistent anchor). Under Per-Monitor-V2 the flyout is crisp and
        // interactive on any monitor, so opening it over the clicked readout is a possible future
        // refinement; for now every readout opens the flyout in the same place.
        _taskbarOverlay.OverlayClicked += (_, _) => ToggleFlyout(PrimaryTrayAnchor());

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
        _updateInstaller = new UpdateInstaller();
        _updateTimer = new System.Timers.Timer(UpdateCheckInterval.TotalMilliseconds)
        {
            AutoReset = true,
        };
        _updateTimer.Elapsed += (_, _) => _ = CheckForUpdatesAsync(manual: false);

        // If the last run launched a silent update, report how it went, and sweep installers
        // that finished updates left in %TEMP% (a running installer can't delete itself).
        NotifyIfJustUpdated();
        UpdateInstaller.CleanUpStaleDownloads();

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
                    // Colour the tray icon by the selected mode (pace-aware by default).
                    var iconColor = IconRenderer.GetUsageColor(
                        fiveHour.UtilizationPct,
                        fiveHour.ElapsedFraction(UsageWindows.FiveHour),
                        _configManager.Settings.ColorMode);

                    var oldIcon = _notifyIcon.Icon;
                    _notifyIcon.Icon = IconRenderer.RenderUsageIcon(fiveHour.UtilizationPct, iconColor);
                    oldIcon?.Dispose();

                    _taskbarOverlay.UpdateUsage(new TaskbarReading(
                        fiveHour.UtilizationPct,
                        fiveHour.ElapsedFraction(UsageWindows.FiveHour),
                        sevenDay?.UtilizationPct,
                        sevenDay?.ElapsedFraction(UsageWindows.SevenDay),
                        fiveHour.ResetAt));
                }

                _notifyIcon.Text = TrayTooltip.Compose(e.Usage, e.Status);

                _alertManager.Check(e.Usage, _configManager.Settings);
            }
            else if (e.Error is not null)
            {
                var oldIcon = _notifyIcon.Icon;
                _notifyIcon.Icon = IconRenderer.RenderErrorIcon();
                oldIcon?.Dispose();
                _notifyIcon.Text = $"ClaudeMon\n{Truncate(e.Error, 100)}";

                // No usage carried with the error means nothing was ever cached (SetError
                // forwards the last reading when there is one) — keep the taskbar readout
                // visibly alive with the waiting marker instead of leaving it blank. With a
                // cached reading the overlay keeps showing the last known numbers, as today.
                _taskbarOverlay.ShowWaiting();
            }
        }, null);
    }

    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ToggleFlyout(Cursor.Position);
    }

    /// <summary>
    /// Anchor at the primary monitor's notification-area corner, where the detail flyout opens.
    /// A click on any taskbar readout opens the flyout here (rather than over the clicked readout)
    /// so it always lands in a single, predictable spot — see the OverlayClicked wiring.
    /// </summary>
    private static Point PrimaryTrayAnchor()
    {
        var workingArea = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
        return new Point(workingArea.Right - 40, workingArea.Bottom);
    }

    /// <summary>
    /// Opens the detail flyout anchored above <paramref name="anchor"/> (or hides it if already
    /// open). Shared by the tray-icon left-click (anchored at the cursor) and a click on a
    /// taskbar readout (anchored at the primary tray corner), so both behave identically.
    /// </summary>
    private void ToggleFlyout(Point anchor)
    {
        if (_flyout.Visible)
        {
            _flyout.Hide();
            return;
        }

        var fiveHourTrend = _history.Recent(TimeSpan.FromHours(5))
            .Select(s => s.FiveHourPct)
            .ToList();

        // Project time-to-limit from the recent burn rate (last 30 min of samples).
        // The latest history sample is recorded from the same poll that set
        // LastUsage, so this current pct and the slope's newest point agree.
        // Pass a null reset when ResetAt is unknown (TimeUntilReset returns Zero
        // for both "unknown" and "already resetting" — only the latter should
        // suppress the estimate).
        TimeSpan? timeToLimit = null;
        var fiveHour = _monitor.LastUsage?.FiveHour;
        if (fiveHour is not null)
        {
            TimeSpan? timeUntilReset = fiveHour.ResetAt is null ? null : fiveHour.TimeUntilReset;
            timeToLimit = BurnRate.EstimateTimeToLimit(
                _history.Recent(BurnRateWindow),
                fiveHour.UtilizationPct,
                timeUntilReset);
        }

        _flyout.UpdateData(
            _monitor.LastUsage, _monitor.Status, _monitor.LastUpdated, fiveHourTrend, timeToLimit,
            _configManager.Settings.ColorMode);
        _flyout.ShowNear(anchor);
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";

    private ContextMenuStrip CreateContextMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh Now", null, async (_, _) => await _monitor.RefreshNowAsync());
        menu.Items.Add("Settings...", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());

        // Hidden until a check finds a newer release; clicking downloads + installs it silently
        // (or opens the release page if the release has no verifiable installer asset).
        _downloadUpdateItem.Click += (_, _) => StartInteractiveUpdateInstall();
        menu.Items.Add(_downloadUpdateItem);
        menu.Items.Add("Check for updates", null, async (_, _) => await CheckForUpdatesAsync(manual: true));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("View logs", null, (_, _) => OpenLogs());
        menu.Items.Add("About ClaudeMon", null, (_, _) => ShowAbout());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        return menu;
    }

    /// <summary>
    /// Queries GitHub for a newer release. When one is found, the update dialog offers
    /// Get / Ignore / Skip-this-version (skip suppresses automatic prompts for that exact
    /// version; see <see cref="UpdatePrompt"/>), and the "Download update" menu item appears
    /// as a persistent affordance either way. On a manual check the user always gets feedback
    /// (the dialog even for a skipped version / up-to-date / failed); automatic checks stay
    /// silent unless a new, unskipped version is found.
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
                    var version = FormatVersion(result.LatestVersion);
                    _updateUrl = result.ReleaseUrl ?? ReleasesFallbackUrl;
                    _latestVersion = version;
                    _installerUrl = result.InstallerUrl;
                    _checksumUrl = result.ChecksumUrl;
                    _downloadUpdateItem.Text = $"Download update (v{version})...";
                    _downloadUpdateItem.Visible = true;

                    // Opted-in automatic install: an automatic check downloads and installs
                    // without prompting (superseding a skipped version — opting into every
                    // update outranks suppressing one prompt). Deferred while Settings or an
                    // update dialog is open — the install restarts the app, which would eat
                    // unsaved edits — and while an install is already running; the next daily
                    // check retries. Manual checks keep the dialog so the user sees feedback.
                    if (!manual && _configManager.Settings.AutoInstallUpdates
                        && result.InstallerUrl is not null && result.ChecksumUrl is not null
                        && !_updateDialogOpen && !_settingsOpen && !_updateInstallInProgress)
                    {
                        _updateInstallInProgress = true;
                        _ = AutoInstallUpdateAsync(version, result.InstallerUrl, result.ChecksumUrl);
                    }
                    // A second dialog can't stack on the open one (the 24h timer keeps ticking
                    // while it's up), and an automatic prompt won't pop over the Settings dialog
                    // — the menu item is already visible and the next check reminds. A manual
                    // check still prompts there (the tray menu stays reachable under ShowDialog,
                    // and asking is explicit intent — same reason it overrides a skipped version).
                    else if (UpdatePrompt.ShouldPrompt(manual, version, _configManager.Settings.IgnoredUpdateVersion)
                        && !_updateDialogOpen && (manual || !_settingsOpen))
                    {
                        ShowUpdateDialog(version);
                    }
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

    /// <summary>
    /// Runs the modal update dialog for <paramref name="version"/> and acts on the choice:
    /// Get downloads and silently installs the update, Skip persists the version so automatic
    /// checks stop prompting for it, Ignore does nothing (the next check reminds again).
    /// </summary>
    private void ShowUpdateDialog(string version)
    {
        _updateDialogOpen = true;
        try
        {
            using var dialog = new UpdateAvailableDialog(FormatVersion(CurrentVersion), version);
            dialog.ShowDialog();

            switch (dialog.Choice)
            {
                case UpdateDialogChoice.GetUpdate:
                    StartInteractiveUpdateInstall();
                    break;
                case UpdateDialogChoice.SkipVersion:
                    // All _configManager.Update calls run on the UI thread (this dialog and
                    // the Settings dialog), so this read-modify-write is safe.
                    _configManager.Update(
                        _configManager.Settings with { IgnoredUpdateVersion = version });
                    break;
            }
        }
        finally
        {
            _updateDialogOpen = false;
        }
    }

    /// <summary>
    /// The user-visible install path ("Get the update" / the tray "Download update" item): runs
    /// the download dialog (progress + cancel), then hands the verified installer to
    /// <see cref="LaunchDownloadedInstaller"/>. A release without a verifiable installer asset
    /// (no installer, or no checksum to verify it against) opens the release page instead, as
    /// does the dialog's failure fallback. Runs on the UI thread.
    /// </summary>
    private void StartInteractiveUpdateInstall()
    {
        if (_latestVersion is null || _installerUrl is null || _checksumUrl is null)
        {
            OpenUpdatePage();
            return;
        }

        // An install is already downloading/launching (auto, or another entry point) — a second
        // one would race it for the same %TEMP% file. Say so rather than silently ignoring the click.
        if (_updateInstallInProgress)
        {
            _notifyIcon.ShowBalloonTip(
                5000, "ClaudeMon update", "An update is already being installed.", ToolTipIcon.Info);
            return;
        }

        // Claim the flag for the whole dialog + launch: ShowDialog pumps messages, so a 24h
        // timer tick can fire mid-download and must see the install as in progress.
        _updateInstallInProgress = true;
        var installerRunning = false;
        try
        {
            using var dialog = new UpdateDownloadDialog(
                _updateInstaller, _installerUrl, _checksumUrl, _latestVersion);
            dialog.ShowDialog();

            switch (dialog.Outcome)
            {
                case UpdateDownloadOutcome.Downloaded:
                    installerRunning = LaunchDownloadedInstaller(dialog.InstallerPath!, _latestVersion);
                    break;
                case UpdateDownloadOutcome.OpenReleasePage:
                    OpenUpdatePage();
                    break;
            }
        }
        finally
        {
            // Keep the claim only while an installer is actually running.
            _updateInstallInProgress = installerRunning;
        }
    }

    /// <summary>
    /// The opted-in automatic path: download + verify with no UI, then install. Failures are
    /// non-fatal — a warning balloon points at the tray menu (where "Download update" is already
    /// showing) and the next daily check retries.
    /// </summary>
    private async Task AutoInstallUpdateAsync(string version, string installerUrl, string checksumUrl)
    {
        try
        {
            var result = await _updateInstaller.DownloadAndVerifyAsync(
                installerUrl, checksumUrl, progress: null, _updateCts.Token);

            _syncContext.Post(_ =>
            {
                if (_disposed) return;

                if (result.InstallerPath is not null)
                {
                    _logger.Info($"Auto-update: installing v{version} silently.");
                    _updateInstallInProgress = LaunchDownloadedInstaller(result.InstallerPath, version);
                }
                else
                {
                    _updateInstallInProgress = false;
                    _logger.Warn($"Auto-update download failed: {result.ErrorMessage}");
                    _notifyIcon.ShowBalloonTip(
                        5000, "ClaudeMon update",
                        $"v{version} couldn't be installed automatically. Use \"Download update\" in the tray menu.",
                        ToolTipIcon.Warning);
                }
            }, null);
        }
        catch (OperationCanceledException)
        {
            // App is shutting down — ignore (the stuck flag dies with the process).
        }
        catch (Exception ex)
        {
            // Fire-and-forget: nothing may escape (same contract as CheckForUpdatesAsync), and
            // the in-progress claim must be released or updates stay wedged until restart.
            _logger.Warn($"Auto-update failed unexpectedly: {ex.Message}");
            _syncContext.Post(_ =>
            {
                if (!_disposed)
                    _updateInstallInProgress = false;
            }, null);
        }
    }

    /// <summary>
    /// Launches a verified installer silently and lets it take over: it stops this process,
    /// installs, and relaunches the new version (releasing the single-instance mutex when this
    /// process dies). <see cref="AppSettings.PendingUpdateVersion"/> is persisted first — before
    /// the installer can kill us — so the relaunched version knows to announce the update. The
    /// argument line pins the installer's startup task to the current registry state so a silent
    /// upgrade can't flip "run at Windows startup" (issue #63). Runs on the UI thread
    /// (_configManager.Update contract).
    /// </summary>
    private bool LaunchDownloadedInstaller(string installerPath, string version)
    {
        _configManager.Update(_configManager.Settings with { PendingUpdateVersion = version });

        var arguments = UpdateInstaller.BuildInstallerArguments(ConfigManager.IsRunAtStartupEnabled());
        var installer = UpdateInstaller.LaunchInstaller(installerPath, arguments);
        if (installer is null)
        {
            // Nothing was installed, so don't leave the "did it land?" marker behind.
            _configManager.Update(_configManager.Settings with { PendingUpdateVersion = null });
            _logger.Warn("Update installer failed to start; falling back to the release page.");
            OpenUpdatePage();
            return false;
        }

        _logger.Info($"Update installer launched for v{version}.");
        WatchInstallerProcess(installer, version);
        return true;
    }

    /// <summary>
    /// A successful install kills this process before the installer exits, so observing the
    /// installer's exit means it aborted before installing (another Inno setup holding the
    /// global setup mutex, AV blocking the temp exe, …) — silently, under /SUPPRESSMSGBOXES.
    /// Without this the in-progress claim and <see cref="AppSettings.PendingUpdateVersion"/>
    /// would stay stuck until the app restarts, wedging every future update attempt.
    /// </summary>
    private void WatchInstallerProcess(Process installer, string version)
    {
        installer.Exited += (_, _) =>
        {
            var exitCode = installer.ExitCode;
            installer.Dispose();
            _syncContext.Post(_ =>
            {
                if (_disposed) return;

                _updateInstallInProgress = false;
                if (_configManager.Settings.PendingUpdateVersion == version)
                    _configManager.Update(_configManager.Settings with { PendingUpdateVersion = null });

                _logger.Warn($"Update installer exited (code {exitCode}) without installing.");
                _notifyIcon.ShowBalloonTip(
                    5000, "ClaudeMon update",
                    $"The v{version} installer didn't complete. Use \"Download update\" in the tray menu to retry.",
                    ToolTipIcon.Warning);
            }, null);
        };
        // Set after subscribing: if the installer already exited, this raises Exited immediately.
        installer.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Startup counterpart of <see cref="LaunchDownloadedInstaller"/>: if the previous run
    /// launched a silent install, announce it when the running version proves it landed, and
    /// clear the marker either way (a mismatch means the installer never finished — the next
    /// update check simply offers the version again).
    /// </summary>
    private void NotifyIfJustUpdated()
    {
        var pending = _configManager.Settings.PendingUpdateVersion;
        if (pending is null)
            return;

        _configManager.Update(_configManager.Settings with { PendingUpdateVersion = null });

        if (string.Equals(pending, FormatVersion(CurrentVersion), StringComparison.OrdinalIgnoreCase))
        {
            _logger.Info($"Updated to v{pending}.");
            _notifyIcon.ShowBalloonTip(
                5000, "ClaudeMon updated", $"You're now on v{pending}.", ToolTipIcon.Info);
        }
        else
        {
            _logger.Warn(
                $"Update to v{pending} did not complete (running v{FormatVersion(CurrentVersion)}).");
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
            // Open the newest log file (today's, or the most recent day's if nothing
            // has been logged yet today); otherwise open the logs folder so the user
            // still lands somewhere useful before anything has been logged.
            if (_logger.LatestExistingFilePath is { } logFile)
            {
                Process.Start(new ProcessStartInfo(logFile) { UseShellExecute = true });
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
        // ShowDialog() is modal only to same-thread windows it disables — not the tray context
        // menu or the taskbar readouts. Without this guard the user could open Settings again
        // (tray menu, or a readout → flyout → gear) on top of the open dialog, stacking two modal
        // SettingsForms that share one config + one overlay preview and revert against each other.
        // All entry points run on the UI thread, so a plain bool is enough.
        if (_settingsOpen)
            return;

        _settingsOpen = true;
        try
        {
            // Pass the live overlays so the taskbar appearance previews on the real taskbar as the
            // visual settings change; the form reverts to the saved values if the dialog is cancelled.
            using var form = new SettingsForm(_configManager, _taskbarOverlay);
            if (form.ShowDialog() == DialogResult.OK)
            {
                _monitor.UpdateInterval(_configManager.Settings.PollInterval);
                _taskbarOverlay.SetColors(
                    _configManager.Settings.TaskbarDisplay.LabelColor,
                    _configManager.Settings.TaskbarDisplay.NumberColor);
                _taskbarOverlay.SetStyle(_configManager.Settings.TaskbarDisplay.Style);
                _taskbarOverlay.SetBarWidth(_configManager.Settings.TaskbarDisplay.BarWidth);
                _taskbarOverlay.SetSize(_configManager.Settings.TaskbarDisplay.SizePercent);
                _taskbarOverlay.SetColorMode(_configManager.Settings.ColorMode);
                _taskbarOverlay.SetDisplay(
                    _configManager.Settings.TaskbarDisplay.ShowSessionUsage,
                    _configManager.Settings.TaskbarDisplay.ShowWeeklyUsage,
                    _configManager.Settings.TaskbarDisplay.ShowTimeToReset);
                _taskbarOverlay.SetHorizontalOffsets(
                    _configManager.Settings.TaskbarDisplay.PrimaryHorizontalOffset,
                    _configManager.Settings.TaskbarDisplay.HorizontalOffset);
                _taskbarOverlay.SetAllMonitors(_configManager.Settings.TaskbarDisplay.AllMonitors);
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
        finally
        {
            _settingsOpen = false;
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
        _updateInstaller.Dispose();
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
