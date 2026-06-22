namespace ClaudeMon.UI;

using ClaudeMon.Configuration;
using ClaudeMon.Models;

public sealed class SettingsForm : Form
{
    private readonly ConfigManager _configManager;
    private readonly ComboBox _pollIntervalCombo;
    private readonly RadioButton _thresholdRadio;
    private readonly RadioButton _progressiveRadio;
    private readonly Panel _thresholdPanel;
    private readonly Panel _progressivePanel;
    private readonly NumericUpDown _warningThreshold;
    private readonly NumericUpDown _criticalThreshold;
    private readonly NumericUpDown _progressiveStartThreshold;
    private readonly NumericUpDown _sevenDayWarningThreshold;
    private readonly CheckBox _notificationsCheckbox;
    private readonly CheckBox _notifyOnResetCheckbox;
    private readonly CheckBox _taskbarDisplayCheckbox;
    private readonly CheckBox _showSevenDayCheckbox;
    private readonly ComboBox _labelColorCombo;
    private readonly ComboBox _numberColorCombo;
    private readonly CheckBox _runAtStartupCheckbox;
    private readonly CheckBox _checkForUpdatesCheckbox;

    // Dropdown options paired with the preset they map to.
    private static readonly (string Text, TaskbarTextColor Value)[] LabelColorOptions =
    [
        ("White", TaskbarTextColor.White),
        ("Black", TaskbarTextColor.Black),
        ("Light gray", TaskbarTextColor.LightGray),
        ("Dark gray", TaskbarTextColor.DarkGray),
    ];

    private static readonly (string Text, TaskbarTextColor Value)[] NumberColorOptions =
    [
        ("Auto (usage level)", TaskbarTextColor.Auto),
        ("White", TaskbarTextColor.White),
        ("Black", TaskbarTextColor.Black),
        ("Light gray", TaskbarTextColor.LightGray),
        ("Dark gray", TaskbarTextColor.DarkGray),
    ];

    public SettingsForm(ConfigManager configManager)
    {
        _configManager = configManager;

        Text = "ClaudeMon Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size(700, 740);

        // --- Monitoring group ---
        var monitoringGroup = new GroupBox
        {
            Text = "Monitoring",
            Location = new Point(20, 20),
            Size = new Size(660, 80),
        };
        Controls.Add(monitoringGroup);

        var pollLabel = new Label
        {
            Text = "Check usage every:",
            Location = new Point(20, 36),
            AutoSize = true,
        };
        monitoringGroup.Controls.Add(pollLabel);

        _pollIntervalCombo = new ComboBox
        {
            Location = new Point(300, 33),
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _pollIntervalCombo.Items.AddRange(["1 minute", "3 minutes", "5 minutes", "10 minutes"]);
        monitoringGroup.Controls.Add(_pollIntervalCombo);

        // --- Alert Thresholds group ---
        var alertGroup = new GroupBox
        {
            Text = "Alert Thresholds",
            Location = new Point(20, 116),
            Size = new Size(660, 210),
        };
        Controls.Add(alertGroup);

        // Mode radio buttons
        _thresholdRadio = new RadioButton
        {
            Text = "Warning / Critical thresholds",
            Location = new Point(20, 30),
            AutoSize = true,
        };
        _thresholdRadio.CheckedChanged += OnAlertModeChanged;
        alertGroup.Controls.Add(_thresholdRadio);

        _progressiveRadio = new RadioButton
        {
            Text = "Progressive alerts (every 10%)",
            Location = new Point(340, 30),
            AutoSize = true,
        };
        alertGroup.Controls.Add(_progressiveRadio);

        // Threshold panel (warning + critical)
        _thresholdPanel = new Panel
        {
            Location = new Point(0, 58),
            Size = new Size(660, 100),
        };
        alertGroup.Controls.Add(_thresholdPanel);

        var warningLabel = new Label
        {
            Text = "Warning notification at:",
            Location = new Point(20, 12),
            AutoSize = true,
        };
        _thresholdPanel.Controls.Add(warningLabel);

        _warningThreshold = new NumericUpDown
        {
            Location = new Point(300, 9),
            Width = 100,
            Minimum = 10,
            Maximum = 100,
        };
        _thresholdPanel.Controls.Add(_warningThreshold);

        var warningPctLabel = new Label
        {
            Text = "%",
            Location = new Point(410, 12),
            AutoSize = true,
        };
        _thresholdPanel.Controls.Add(warningPctLabel);

        var criticalLabel = new Label
        {
            Text = "Critical notification at:",
            Location = new Point(20, 52),
            AutoSize = true,
        };
        _thresholdPanel.Controls.Add(criticalLabel);

        _criticalThreshold = new NumericUpDown
        {
            Location = new Point(300, 49),
            Width = 100,
            Minimum = 10,
            Maximum = 100,
        };
        _thresholdPanel.Controls.Add(_criticalThreshold);

        var criticalPctLabel = new Label
        {
            Text = "%",
            Location = new Point(410, 52),
            AutoSize = true,
        };
        _thresholdPanel.Controls.Add(criticalPctLabel);

        // Progressive panel (start percentage)
        _progressivePanel = new Panel
        {
            Location = new Point(0, 58),
            Size = new Size(660, 100),
        };
        alertGroup.Controls.Add(_progressivePanel);

        var progressiveLabel = new Label
        {
            Text = "Start alerting at:",
            Location = new Point(20, 12),
            AutoSize = true,
        };
        _progressivePanel.Controls.Add(progressiveLabel);

        _progressiveStartThreshold = new NumericUpDown
        {
            Location = new Point(300, 9),
            Width = 100,
            Minimum = 10,
            Maximum = 90,
            Increment = 10,
        };
        _progressivePanel.Controls.Add(_progressiveStartThreshold);

        var progressivePctLabel = new Label
        {
            Text = "%",
            Location = new Point(410, 12),
            AutoSize = true,
        };
        _progressivePanel.Controls.Add(progressivePctLabel);

        var progressiveHint = new Label
        {
            Text = "Sends a notification at each 10% step above this value.",
            Location = new Point(20, 46),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };
        _progressivePanel.Controls.Add(progressiveHint);

        // 7-day warning — always visible (AlertManager checks it independently of the
        // 5-hour mode above), so it lives in the group rather than a mode panel.
        var sevenDayLabel = new Label
        {
            Text = "7-day warning notification at:",
            Location = new Point(20, 172),
            AutoSize = true,
        };
        alertGroup.Controls.Add(sevenDayLabel);

        _sevenDayWarningThreshold = new NumericUpDown
        {
            Location = new Point(300, 169),
            Width = 100,
            Minimum = 10,
            Maximum = 100,
        };
        alertGroup.Controls.Add(_sevenDayWarningThreshold);

        var sevenDayPctLabel = new Label
        {
            Text = "%",
            Location = new Point(410, 172),
            AutoSize = true,
        };
        alertGroup.Controls.Add(sevenDayPctLabel);

        // --- Taskbar Display group ---
        var taskbarGroup = new GroupBox
        {
            Text = "Taskbar Display",
            Location = new Point(20, 342),
            Size = new Size(660, 172),
        };
        Controls.Add(taskbarGroup);

        _taskbarDisplayCheckbox = new CheckBox
        {
            Text = "Show usage on the Windows taskbar",
            Location = new Point(20, 30),
            AutoSize = true,
        };
        _taskbarDisplayCheckbox.CheckedChanged += OnTaskbarDisplayToggled;
        taskbarGroup.Controls.Add(_taskbarDisplayCheckbox);

        _showSevenDayCheckbox = new CheckBox
        {
            Text = "Also show 7-day usage (5hr / 7day)",
            Location = new Point(40, 60),
            AutoSize = true,
        };
        taskbarGroup.Controls.Add(_showSevenDayCheckbox);

        var labelColorLabel = new Label
        {
            Text = "\"Claude\" label color:",
            Location = new Point(40, 100),
            AutoSize = true,
        };
        taskbarGroup.Controls.Add(labelColorLabel);

        _labelColorCombo = new ComboBox
        {
            Location = new Point(300, 97),
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _labelColorCombo.Items.AddRange(LabelColorOptions.Select(o => (object)o.Text).ToArray());
        taskbarGroup.Controls.Add(_labelColorCombo);

        var numberColorLabel = new Label
        {
            Text = "Percentage color:",
            Location = new Point(40, 134),
            AutoSize = true,
        };
        taskbarGroup.Controls.Add(numberColorLabel);

        _numberColorCombo = new ComboBox
        {
            Location = new Point(300, 131),
            Width = 200,
            DropDownStyle = ComboBoxStyle.DropDownList,
        };
        _numberColorCombo.Items.AddRange(NumberColorOptions.Select(o => (object)o.Text).ToArray());
        taskbarGroup.Controls.Add(_numberColorCombo);

        // --- General group ---
        var generalGroup = new GroupBox
        {
            Text = "General",
            Location = new Point(20, 530),
            Size = new Size(660, 156),
        };
        Controls.Add(generalGroup);

        _notificationsCheckbox = new CheckBox
        {
            Text = "Enable desktop notifications",
            Location = new Point(20, 30),
            AutoSize = true,
        };
        _notificationsCheckbox.CheckedChanged += OnNotificationsToggled;
        generalGroup.Controls.Add(_notificationsCheckbox);

        _notifyOnResetCheckbox = new CheckBox
        {
            Text = "Notify when the rate limit resets",
            Location = new Point(40, 60),
            AutoSize = true,
        };
        generalGroup.Controls.Add(_notifyOnResetCheckbox);

        _runAtStartupCheckbox = new CheckBox
        {
            Text = "Start ClaudeMon when Windows starts",
            Location = new Point(20, 90),
            AutoSize = true,
        };
        generalGroup.Controls.Add(_runAtStartupCheckbox);

        _checkForUpdatesCheckbox = new CheckBox
        {
            Text = "Check for updates automatically",
            Location = new Point(20, 120),
            AutoSize = true,
        };
        generalGroup.Controls.Add(_checkForUpdatesCheckbox);

        // --- Buttons ---
        var okButton = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(500, 696),
            Size = new Size(80, 32),
        };
        okButton.Click += OnOkClicked;
        Controls.Add(okButton);

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(594, 696),
            Size = new Size(80, 32),
        };
        Controls.Add(cancelButton);

        AcceptButton = okButton;
        CancelButton = cancelButton;

        LoadSettings();
    }

    private void OnAlertModeChanged(object? sender, EventArgs e)
    {
        _thresholdPanel.Visible = _thresholdRadio.Checked;
        _progressivePanel.Visible = _progressiveRadio.Checked;
    }

    private void OnNotificationsToggled(object? sender, EventArgs e)
    {
        // Notify-on-reset only matters when notifications are enabled.
        _notifyOnResetCheckbox.Enabled = _notificationsCheckbox.Checked;
    }

    private void OnTaskbarDisplayToggled(object? sender, EventArgs e)
    {
        // The 7-day option and colours only matter when the taskbar display is on.
        _showSevenDayCheckbox.Enabled = _taskbarDisplayCheckbox.Checked;
        _labelColorCombo.Enabled = _taskbarDisplayCheckbox.Checked;
        _numberColorCombo.Enabled = _taskbarDisplayCheckbox.Checked;
    }

    private static void SelectColor(
        ComboBox combo, (string Text, TaskbarTextColor Value)[] options, TaskbarTextColor value)
    {
        var index = Array.FindIndex(options, o => o.Value == value);
        combo.SelectedIndex = index >= 0 ? index : 0;
    }

    private static TaskbarTextColor SelectedColor(
        ComboBox combo, (string Text, TaskbarTextColor Value)[] options)
    {
        var index = combo.SelectedIndex;
        return index >= 0 && index < options.Length ? options[index].Value : options[0].Value;
    }

    private void LoadSettings()
    {
        var settings = _configManager.Settings;

        _pollIntervalCombo.SelectedIndex = settings.PollIntervalMinutes switch
        {
            1 => 0,
            3 => 1,
            5 => 2,
            10 => 3,
            _ => 2,
        };

        _warningThreshold.Value = settings.AlertThresholds.FiveHourWarning;
        _criticalThreshold.Value = settings.AlertThresholds.FiveHourCritical;
        _progressiveStartThreshold.Value = settings.AlertThresholds.ProgressiveStartPct;
        _sevenDayWarningThreshold.Value = settings.AlertThresholds.SevenDayWarning;

        if (settings.AlertThresholds.Mode == AlertMode.Progressive)
            _progressiveRadio.Checked = true;
        else
            _thresholdRadio.Checked = true;

        _thresholdPanel.Visible = _thresholdRadio.Checked;
        _progressivePanel.Visible = _progressiveRadio.Checked;

        _notificationsCheckbox.Checked = settings.Notifications.Enabled;
        _notifyOnResetCheckbox.Checked = settings.Notifications.NotifyOnReset;
        OnNotificationsToggled(null, EventArgs.Empty);
        _taskbarDisplayCheckbox.Checked = settings.TaskbarDisplay.Enabled;
        _showSevenDayCheckbox.Checked = settings.TaskbarDisplay.ShowSevenDay;
        SelectColor(_labelColorCombo, LabelColorOptions, settings.TaskbarDisplay.LabelColor);
        SelectColor(_numberColorCombo, NumberColorOptions, settings.TaskbarDisplay.NumberColor);
        OnTaskbarDisplayToggled(null, EventArgs.Empty);
        _runAtStartupCheckbox.Checked = ConfigManager.IsRunAtStartupEnabled();
        _checkForUpdatesCheckbox.Checked = settings.CheckForUpdates;
    }

    private void OnOkClicked(object? sender, EventArgs e)
    {
        var pollMinutes = _pollIntervalCombo.SelectedIndex switch
        {
            0 => 1,
            1 => 3,
            2 => 5,
            3 => 10,
            _ => 5,
        };

        var newSettings = _configManager.Settings with
        {
            PollIntervalMinutes = pollMinutes,
            AlertThresholds = new AlertThresholds
            {
                Mode = _progressiveRadio.Checked ? AlertMode.Progressive : AlertMode.Threshold,
                FiveHourWarning = (int)_warningThreshold.Value,
                FiveHourCritical = (int)_criticalThreshold.Value,
                SevenDayWarning = (int)_sevenDayWarningThreshold.Value,
                ProgressiveStartPct = (int)_progressiveStartThreshold.Value,
            },
            Notifications = new NotificationSettings
            {
                Enabled = _notificationsCheckbox.Checked,
                NotifyOnReset = _notifyOnResetCheckbox.Checked,
            },
            TaskbarDisplay = new TaskbarDisplaySettings
            {
                Enabled = _taskbarDisplayCheckbox.Checked,
                ShowSevenDay = _showSevenDayCheckbox.Checked,
                LabelColor = SelectedColor(_labelColorCombo, LabelColorOptions),
                NumberColor = SelectedColor(_numberColorCombo, NumberColorOptions),
            },
            CheckForUpdates = _checkForUpdatesCheckbox.Checked,
        };

        _configManager.Update(newSettings);
        ConfigManager.SetRunAtStartup(_runAtStartupCheckbox.Checked);
    }
}
