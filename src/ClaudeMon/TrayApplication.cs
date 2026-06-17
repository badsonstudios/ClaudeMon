namespace ClaudeMon;

using System.Drawing;
using ClaudeMon.Configuration;
using ClaudeMon.Monitoring;
using ClaudeMon.Services;
using ClaudeMon.UI;

public sealed class TrayApplication : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private readonly UsageMonitor _monitor;
    private readonly ClaudeApiClient _apiClient;
    private readonly ConfigManager _configManager;
    private readonly SynchronizationContext _syncContext;
    private readonly FlyoutPanel _flyout;
    private readonly TaskbarOverlayWindow _taskbarOverlay;
    private readonly AlertManager _alertManager;
    private bool _disposed;

    public TrayApplication()
    {
        _syncContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _configManager = new ConfigManager();
        _configManager.Load();

        _apiClient = new ClaudeApiClient();
        var credentialReader = new CredentialReader();
        _monitor = new UsageMonitor(
            credentialReader, _apiClient, _configManager.Settings.PollInterval);
        _monitor.UsageUpdated += OnUsageUpdated;

        _flyout = new FlyoutPanel();

        _taskbarOverlay = new TaskbarOverlayWindow();
        _taskbarOverlay.SetColors(
            _configManager.Settings.TaskbarDisplay.LabelColor,
            _configManager.Settings.TaskbarDisplay.NumberColor);
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

        _monitor.Start();
    }

    private void OnUsageUpdated(object? sender, UsageUpdatedEventArgs e)
    {
        if (_disposed) return;

        _syncContext.Post(_ =>
        {
            if (_disposed) return;

            if (e.Usage is not null)
            {
                var fiveHour = e.Usage.FiveHour;
                var sevenDay = e.Usage.SevenDay;

                if (fiveHour is not null)
                {
                    var oldIcon = _notifyIcon.Icon;
                    _notifyIcon.Icon = IconRenderer.RenderUsageIcon(fiveHour.UtilizationPct);
                    oldIcon?.Dispose();

                    _taskbarOverlay.UpdateUsage(fiveHour.UtilizationPct);
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
            _flyout.UpdateData(_monitor.LastUsage, _monitor.Status, _monitor.LastUpdated);
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
        menu.Items.Add("About ClaudeMon", null, (_, _) => ShowAbout());
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        return menu;
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
            _taskbarOverlay.SetEnabled(_configManager.Settings.TaskbarDisplay.Enabled);
        }
    }

    private static void ShowAbout()
    {
        var version = typeof(TrayApplication).Assembly.GetName().Version;
        MessageBox.Show(
            $"ClaudeMon v{version?.ToString(3) ?? "0.0.1"}\n\n" +
            "Windows system tray monitor for Claude AI usage.\n\n" +
            "Monitors 5-hour and 7-day rate limits\n" +
            "for Claude Max subscribers.\n\n" +
            "github.com/badsonstudios/ClaudeMon",
            "About ClaudeMon",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    public void Dispose()
    {
        _disposed = true;
        _monitor.Dispose();
        _apiClient.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _contextMenu.Dispose();
        _flyout.Dispose();
        _taskbarOverlay.Dispose();
    }
}
