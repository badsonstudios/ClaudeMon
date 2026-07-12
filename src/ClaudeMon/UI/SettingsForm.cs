namespace ClaudeMon.UI;

using System.Drawing;
using ClaudeMon.Configuration;
using ClaudeMon.Models;

/// <summary>
/// The settings dialog: a <see cref="TabStrip"/> (General / Alerts / Taskbar / Updates) over a
/// single right-aligned control column, with toggle switches for booleans and OK/Cancel shared
/// below the tab content. Rows are tracked in <see cref="_rows"/> tagged with their tab and an
/// optional visibility predicate — sub-options <em>collapse</em> when their parent toggle is off —
/// and <see cref="Relayout"/> positions the active tab's visible rows and sizes the window to
/// them (so the dialog height follows the current tab). The app-wide dark mode
/// (<c>Application.SetColorMode</c> in Program.cs) themes the standard controls; this form only
/// adds the accents and the custom <see cref="ToggleSwitch"/>/<see cref="TabStrip"/> controls.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly ConfigManager _configManager;

    // The live overlays, so the taskbar appearance previews on the real taskbar as the visual
    // settings change. Null in contexts without overlays. Reverted from _originalTaskbar on cancel.
    private readonly TaskbarOverlayManager? _overlayPreview;
    private TaskbarDisplaySettings _originalTaskbar = new();

    // True while the constructor + LoadSettings seed the controls, so their change events don't
    // relayout or fire live previews until the saved values are in place.
    private bool _loading = true;

    private readonly TabStrip _tabStrip;
    private readonly ComboBox _pollIntervalCombo;
    private readonly ToggleSwitch _notificationsToggle;
    private readonly ToggleSwitch _paceAlertsToggle;
    private readonly ComboBox _paceSensitivityCombo;
    private readonly NumericUpDown _nearCapNumeric;
    private readonly NumericUpDown _sevenDayWarningNumeric;
    private readonly ToggleSwitch _notifyOnResetToggle;
    private readonly ToggleSwitch _taskbarToggle;
    private readonly ComboBox _styleCombo;
    private readonly ComboBox _barWidthCombo;
    private readonly NumericUpDown _sizeNumeric;
    private readonly ToggleSwitch _showSessionToggle;
    private readonly ToggleSwitch _showWeeklyToggle;
    private readonly ToggleSwitch _showTimeToResetToggle;
    private readonly ComboBox _labelColorCombo;
    private readonly ComboBox _numberColorCombo;
    private readonly NumericUpDown _primaryOffsetNumeric;
    private readonly ToggleSwitch _allMonitorsToggle;
    private readonly NumericUpDown _secondaryOffsetNumeric;
    private readonly ToggleSwitch _runAtStartupToggle;
    private readonly ToggleSwitch _checkForUpdatesToggle;
    private readonly Button _okButton;
    private readonly Button _cancelButton;

    // --- Layout metrics ---
    private const int Pad = 24;          // horizontal + bottom margin
    private const int TopMargin = 6;     // smaller top margin so the tab strip sits near the top
    private const int ContentRight = 480 - Pad; // 456
    private const int ControlLeft = 250;
    private const int ComboWidth = ContentRight - ControlLeft; // 206
    private const int NumericWidth = 64;
    private const int ToggleWidth = 40;
    private const int TabStripHeight = 36;
    private const int TabContentGap = 12; // between the tab strip's baseline and the first row

    // Light or dark accents/controls, matching the Windows app theme.
    private readonly Theme _theme = Theme.Current;

    // The font this form owns. WinForms does NOT dispose a Font you assign to a control, so
    // without this it'd leak a handle per dialog open; disposed in Dispose.
    private readonly Font _baseFont = new("Segoe UI", 9.75f);

    // An ordered layout row: its controls (each with a vertical offset within the row), the row
    // height, the tab it lives on, and an optional visibility predicate (null = always shown
    // while its tab is active).
    private sealed class RowDef
    {
        public required (Control Control, int OffsetY)[] Items;
        public required int Height;
        public required int Tab;
        public Func<bool>? Visible;
    }

    // The tab the Add*Row helpers stamp onto new rows while the constructor builds each tab.
    private int _currentTab;

    private readonly List<RowDef> _rows = [];

    // Logical (96-DPI) horizontal geometry per control: (control, left, width, height). Width/height
    // 0 means "leave as-is" (labels/combos auto-size their height from the font). Applied scaled by
    // the monitor DPI in Relayout, because AutoScaleMode.None means WinForms won't scale our manual
    // layout for us. The layout constants above are all logical (96-DPI) values.
    private readonly List<(Control Control, int Left, int Width, int Height)> _hspec = [];

    private int Sc(int value) => DpiScale.Scale(value, DeviceDpi / 96f);

    private static readonly (string Text, PaceSensitivity Value)[] PaceSensitivityOptions =
    [
        ("Early — cautious", PaceSensitivity.Early),
        ("Balanced", PaceSensitivity.Balanced),
        ("Late — only when well over", PaceSensitivity.Late),
    ];

    private static readonly (string Text, TaskbarStyle Value)[] StyleOptions =
    [
        // The composition (session/weekly/countdown) is described by the display toggles below.
        ("Numbers", TaskbarStyle.Numbers),
        ("Bar + time tick", TaskbarStyle.Bar),
    ];

    private static readonly (string Text, TaskbarBarWidth Value)[] BarWidthOptions =
    [
        ("Compact", TaskbarBarWidth.Compact),
        ("Standard", TaskbarBarWidth.Standard),
        ("Wide", TaskbarBarWidth.Wide),
        ("Extra wide", TaskbarBarWidth.ExtraWide),
    ];

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

    public SettingsForm(ConfigManager configManager, TaskbarOverlayManager? overlayPreview = null)
    {
        _configManager = configManager;
        _overlayPreview = overlayPreview;

        Text = "ClaudeMon Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        // This form is laid out manually (a vertical cursor + collapsing rows), so WinForms' own
        // auto-scaling can't help — it would fight our repeated Relayout(). We take full control and
        // scale every metric by the monitor DPI ourselves (see Sc/Relayout). Point-sized fonts still
        // scale with DeviceDpi on their own.
        AutoScaleMode = AutoScaleMode.None;
        Font = _baseFont;
        // Background + control colours come from the app-wide dark mode (Program.cs).
        ClientSize = new Size(480, 600);

        // The tab headers; each row below is stamped with the tab it lives on via _currentTab.
        _tabStrip = new TabStrip("General", "Alerts", "Taskbar", "Updates")
        {
            AccessibleName = "Settings sections",
        };
        Controls.Add(_tabStrip);
        _hspec.Add((_tabStrip, Pad, ContentRight - Pad, TabStripHeight));

        // --- General tab ---
        _currentTab = 0;
        _runAtStartupToggle = AddToggleRow("Start ClaudeMon when Windows starts");
        // 2 minutes is the floor: polling every minute made the API refresh fail every other
        // request (see AppSettings.PollIntervalMinutes).
        _pollIntervalCombo = AddComboRow("Check usage every", ["2 minutes", "3 minutes", "5 minutes", "10 minutes"]);

        // --- Alerts tab ---
        _currentTab = 1;
        _notificationsToggle = AddToggleRow("Enable desktop notifications");
        bool AlertsOn() => _notificationsToggle.Checked;
        _paceAlertsToggle = AddToggleRow("Warn when on track to run out", indent: true, visible: AlertsOn);
        _paceSensitivityCombo = AddComboRow("Sensitivity", PaceSensitivityOptions.Select(o => o.Text),
            indent: true, visible: () => AlertsOn() && _paceAlertsToggle.Checked);
        _nearCapNumeric = AddNumericRow("Critical alert near the limit at", 50, 100, indent: true, visible: AlertsOn);
        _sevenDayWarningNumeric = AddNumericRow("Weekly (7-day) warning at", 10, 100, indent: true, visible: AlertsOn);
        _notifyOnResetToggle = AddToggleRow("Notify when the limit resets", indent: true, visible: AlertsOn);

        // --- Taskbar tab ---
        _currentTab = 2;
        _taskbarToggle = AddToggleRow("Show usage on the Windows taskbar");
        bool TaskbarOn() => _taskbarToggle.Checked;
        bool IsBar() => SelectedOption(_styleCombo, StyleOptions) == TaskbarStyle.Bar;
        _styleCombo = AddComboRow("Style", StyleOptions.Select(o => o.Text), indent: true, visible: TaskbarOn);
        _barWidthCombo = AddComboRow("Bar width", BarWidthOptions.Select(o => o.Text),
            indent: true, visible: () => TaskbarOn() && IsBar());
        _sizeNumeric = AddNumericRow("Size", 25, 150, indent: true, visible: TaskbarOn);
        _sizeNumeric.Increment = 5;
        _primaryOffsetNumeric = AddNumericRow("Position (− left / + right)", -300, 300,
            indent: true, visible: TaskbarOn, suffix: null);
        _primaryOffsetNumeric.Increment = 2;
        _showSessionToggle = AddToggleRow("Show session (5-hour) usage", indent: true, visible: TaskbarOn);
        _showWeeklyToggle = AddToggleRow("Show weekly (7-day) usage", indent: true, visible: TaskbarOn);
        // The countdown is a Numbers-style element; the bar has its own time tick, so the row
        // hides in Bar mode rather than offering a toggle that does nothing there.
        _showTimeToResetToggle = AddToggleRow("Show time left to reset", indent: true,
            visible: () => TaskbarOn() && !IsBar());
        _labelColorCombo = AddComboRow("\"Claude\" label color", LabelColorOptions.Select(o => o.Text),
            indent: true, visible: () => TaskbarOn() && !IsBar());
        _numberColorCombo = AddComboRow("Percentage color", NumberColorOptions.Select(o => o.Text),
            indent: true, visible: () => TaskbarOn() && !IsBar());
        _allMonitorsToggle = AddToggleRow("Show on secondary monitors", indent: true, visible: TaskbarOn);
        _secondaryOffsetNumeric = AddNumericRow("Secondary position (− left / + right)", -300, 300,
            indent: true, visible: () => TaskbarOn() && _allMonitorsToggle.Checked, suffix: null);
        _secondaryOffsetNumeric.Increment = 2;

        // --- Updates tab ---
        _currentTab = 3;
        _checkForUpdatesToggle = AddToggleRow("Check for updates automatically");

        // --- Buttons ---
        // Position/size are applied — DPI-scaled — by Relayout from _hspec.
        _okButton = MakeButton("OK", DialogResult.OK);
        _okButton.Click += OnOkClicked;
        Controls.Add(_okButton);
        _hspec.Add((_okButton, ContentRight - 174, 82, 30)); // buttons are 82x30 logical

        _cancelButton = MakeButton("Cancel", DialogResult.Cancel);
        Controls.Add(_cancelButton);
        _hspec.Add((_cancelButton, ContentRight - 84, 82, 30));

        AcceptButton = _okButton;
        CancelButton = _cancelButton;

        WireEvents();
        LoadSettings();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Match the title bar to the body (the app-wide colour mode usually handles this; this
        // makes it certain on Win10 20H1+/Win11).
        SystemTheme.ApplyTitleBar(Handle, _theme.IsDark);
    }

    // --- Layout helpers (controls are positioned by Relayout, not here) ---

    private ToggleSwitch AddToggleRow(string text, bool indent = false, Func<bool>? visible = null)
    {
        var label = new Label { Text = text, AutoSize = true };
        var toggle = new ToggleSwitch();
        Controls.Add(label);
        Controls.Add(toggle);
        _rows.Add(new RowDef { Items = [(label, 8), (toggle, 7)], Height = 34, Tab = _currentTab, Visible = visible });
        _hspec.Add((label, indent ? Pad + 16 : Pad, 0, 0));
        _hspec.Add((toggle, ContentRight - ToggleWidth, ToggleWidth, 20)); // ToggleSwitch is 40x20 logical
        return toggle;
    }

    private ComboBox AddComboRow(string label, IEnumerable<string> items, bool indent = false, Func<bool>? visible = null)
    {
        var lbl = new Label { Text = label, AutoSize = true };
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(items.Select(i => (object)i).ToArray());
        Controls.Add(lbl);
        Controls.Add(combo);
        _rows.Add(new RowDef { Items = [(lbl, 6), (combo, 3)], Height = 34, Tab = _currentTab, Visible = visible });
        _hspec.Add((lbl, indent ? Pad + 16 : Pad, 0, 0));
        _hspec.Add((combo, ControlLeft, ComboWidth, 0));
        return combo;
    }

    private NumericUpDown AddNumericRow(
        string label, int min, int max, bool indent = false, Func<bool>? visible = null, string? suffix = "%")
    {
        var lbl = new Label { Text = label, AutoSize = true };
        var numeric = new ThemedNumericUpDown { Minimum = min, Maximum = max };
        Controls.Add(lbl);
        Controls.Add(numeric);
        _hspec.Add((lbl, indent ? Pad + 16 : Pad, 0, 0));
        _hspec.Add((numeric, ControlLeft, NumericWidth, 0));

        var items = new List<(Control, int)> { (lbl, 6), (numeric, 3) };
        if (suffix is not null)
        {
            var sfx = new Label
            {
                Text = suffix,
                AutoSize = true,
                ForeColor = _theme.HintText,
            };
            Controls.Add(sfx);
            items.Add((sfx, 6));
            _hspec.Add((sfx, ControlLeft + NumericWidth + 6, 0, 0));
        }

        _rows.Add(new RowDef { Items = items.ToArray(), Height = 34, Tab = _currentTab, Visible = visible });
        return numeric;
    }

    private Button MakeButton(string text, DialogResult result)
    {
        var button = new Button
        {
            Text = text,
            DialogResult = result,
            Size = new Size(82, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ButtonBack,
            ForeColor = _theme.ButtonText,
        };
        button.FlatAppearance.BorderColor = _theme.ButtonBorder;
        return button;
    }

    // Walks the rows top to bottom, hides the inactive tabs' rows and the collapsed ones,
    // positions the visible ones below the tab strip, then places the buttons and sizes the
    // window to fit (so both collapsing a sub-option and switching tabs resize the dialog).
    // Every metric is scaled from its logical (96-DPI) value by the current monitor DPI (Sc),
    // since AutoScaleMode.None means we own all scaling.
    private void Relayout()
    {
        // Horizontal placement + control sizes (scaled from the logical spec captured at build time).
        foreach (var (control, left, width, height) in _hspec)
        {
            control.Left = Sc(left);
            if (width > 0)
                control.Width = Sc(width);
            if (height > 0)
                control.Height = Sc(height);
        }

        _tabStrip.Top = Sc(TopMargin);

        var y = _tabStrip.Top + _tabStrip.Height + Sc(TabContentGap);
        foreach (var row in _rows)
        {
            var visible = row.Tab == _tabStrip.SelectedIndex && (row.Visible?.Invoke() ?? true);
            foreach (var (control, offsetY) in row.Items)
            {
                control.Visible = visible;
                if (visible)
                    control.Top = y + Sc(offsetY);
            }

            if (visible)
                y += Sc(row.Height);
        }

        y += Sc(14);
        _okButton.Top = y;
        _cancelButton.Top = y;
        ClientSize = new Size(Sc(480), y + _okButton.Height + Sc(Pad));
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // DeviceDpi is only reliable once the handle exists; the constructor's Relayout ran at the
        // default DPI, so redo it here (before first paint) at the real monitor DPI.
        Relayout();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        // Re-fit if the dialog is dragged to a monitor with a different scale.
        Relayout();
    }

    protected override void Dispose(bool disposing)
    {
        // Dispose child controls first, then the fonts they were using (a control never disposes an
        // assigned Font itself).
        base.Dispose(disposing);
        if (disposing)
            _baseFont.Dispose();
    }

    // --- Events ---

    private void WireEvents()
    {
        // Switching tabs swaps which rows are visible (and re-fits the window height).
        _tabStrip.SelectedIndexChanged += (_, _) => RelayoutLive();

        // Collapse/expand on the gating toggles; some also live-preview the taskbar appearance.
        _notificationsToggle.CheckedChanged += (_, _) => RelayoutLive();
        _paceAlertsToggle.CheckedChanged += (_, _) => RelayoutLive();
        _taskbarToggle.CheckedChanged += (_, _) =>
        {
            RelayoutLive();
            Preview(() => _overlayPreview!.SetEnabled(_taskbarToggle.Checked));
        };
        _styleCombo.SelectedIndexChanged += (_, _) =>
        {
            RelayoutLive();
            Preview(() => _overlayPreview!.SetStyle(SelectedOption(_styleCombo, StyleOptions)));
        };
        _allMonitorsToggle.CheckedChanged += (_, _) =>
        {
            RelayoutLive();
            Preview(() => _overlayPreview!.SetAllMonitors(_allMonitorsToggle.Checked));
        };

        // Live-preview only (no layout impact).
        _barWidthCombo.SelectedIndexChanged += (_, _) =>
            Preview(() => _overlayPreview!.SetBarWidth(SelectedOption(_barWidthCombo, BarWidthOptions)));
        _sizeNumeric.ValueChanged += (_, _) =>
            Preview(() => _overlayPreview!.SetSize((int)_sizeNumeric.Value));
        _showSessionToggle.CheckedChanged += (_, _) => PreviewDisplay();
        _showWeeklyToggle.CheckedChanged += (_, _) => PreviewDisplay();
        _showTimeToResetToggle.CheckedChanged += (_, _) => PreviewDisplay();
        _labelColorCombo.SelectedIndexChanged += (_, _) => PreviewColors();
        _numberColorCombo.SelectedIndexChanged += (_, _) => PreviewColors();
        _primaryOffsetNumeric.ValueChanged += (_, _) => PreviewOffsets();
        _secondaryOffsetNumeric.ValueChanged += (_, _) => PreviewOffsets();
    }

    private void RelayoutLive()
    {
        if (!_loading)
            Relayout();
    }

    private void Preview(Action apply)
    {
        if (_loading || _overlayPreview is null)
            return;

        apply();
    }

    private void PreviewColors() => Preview(() => _overlayPreview!.SetColors(
        SelectedOption(_labelColorCombo, LabelColorOptions),
        SelectedOption(_numberColorCombo, NumberColorOptions)));

    private void PreviewDisplay() => Preview(() => _overlayPreview!.SetDisplay(
        _showSessionToggle.Checked, _showWeeklyToggle.Checked, _showTimeToResetToggle.Checked));

    private void PreviewOffsets() => Preview(() => _overlayPreview!.SetHorizontalOffsets(
        (int)_primaryOffsetNumeric.Value, (int)_secondaryOffsetNumeric.Value));

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Undo every live preview if the dialog wasn't accepted, restoring the saved appearance.
        if (DialogResult != DialogResult.OK && _overlayPreview is not null)
        {
            var t = _originalTaskbar;
            _overlayPreview.SetStyle(t.Style);
            _overlayPreview.SetBarWidth(t.BarWidth);
            _overlayPreview.SetSize(t.SizePercent);
            _overlayPreview.SetDisplay(t.ShowSessionUsage, t.ShowWeeklyUsage, t.ShowTimeToReset);
            _overlayPreview.SetColors(t.LabelColor, t.NumberColor);
            _overlayPreview.SetAllMonitors(t.AllMonitors);
            _overlayPreview.SetHorizontalOffsets(t.PrimaryHorizontalOffset, t.HorizontalOffset);
            _overlayPreview.SetEnabled(t.Enabled);
        }

        base.OnFormClosing(e);
    }

    // Coerce a persisted value into the control's range, so an out-of-range config never throws.
    private static decimal ClampToRange(NumericUpDown numeric, int value) =>
        Math.Clamp(value, (int)numeric.Minimum, (int)numeric.Maximum);

    // Select the dropdown row whose paired enum value matches, falling back to the first.
    private static void SelectOption<T>(ComboBox combo, (string Text, T Value)[] options, T value)
    {
        var index = Array.FindIndex(options, o => EqualityComparer<T>.Default.Equals(o.Value, value));
        combo.SelectedIndex = index >= 0 ? index : 0;
    }

    // The enum value paired with the currently-selected dropdown row (first option if none).
    private static T SelectedOption<T>(ComboBox combo, (string Text, T Value)[] options)
    {
        var index = combo.SelectedIndex;
        return index >= 0 && index < options.Length ? options[index].Value : options[0].Value;
    }

    private void LoadSettings()
    {
        var settings = _configManager.Settings;

        // Snapshot the saved taskbar appearance so a cancelled dialog can revert the live preview.
        _originalTaskbar = settings.TaskbarDisplay;

        _pollIntervalCombo.SelectedIndex = settings.PollIntervalMinutes switch
        {
            // Anything at or below the floor (a 1 saved by a version that still offered
            // "1 minute", or a hand-edited 0) shows as the 2 minutes it effectively runs at.
            <= 2 => 0,
            3 => 1,
            5 => 2,
            10 => 3,
            _ => 2,
        };

        _notificationsToggle.Checked = settings.Notifications.Enabled;
        _paceAlertsToggle.Checked = settings.AlertThresholds.PaceAlertsEnabled;
        SelectOption(_paceSensitivityCombo, PaceSensitivityOptions, settings.AlertThresholds.PaceSensitivity);
        _nearCapNumeric.Value = ClampToRange(_nearCapNumeric, settings.AlertThresholds.NearCapWarning);
        _sevenDayWarningNumeric.Value = ClampToRange(_sevenDayWarningNumeric, settings.AlertThresholds.SevenDayWarning);
        _notifyOnResetToggle.Checked = settings.Notifications.NotifyOnReset;

        _taskbarToggle.Checked = settings.TaskbarDisplay.Enabled;
        SelectOption(_styleCombo, StyleOptions, settings.TaskbarDisplay.Style);
        SelectOption(_barWidthCombo, BarWidthOptions, settings.TaskbarDisplay.BarWidth);
        _sizeNumeric.Value = ClampToRange(_sizeNumeric, settings.TaskbarDisplay.SizePercent);
        _showSessionToggle.Checked = settings.TaskbarDisplay.ShowSessionUsage;
        _showWeeklyToggle.Checked = settings.TaskbarDisplay.ShowWeeklyUsage;
        _showTimeToResetToggle.Checked = settings.TaskbarDisplay.ShowTimeToReset;
        SelectOption(_labelColorCombo, LabelColorOptions, settings.TaskbarDisplay.LabelColor);
        SelectOption(_numberColorCombo, NumberColorOptions, settings.TaskbarDisplay.NumberColor);
        _primaryOffsetNumeric.Value = ClampToRange(_primaryOffsetNumeric, settings.TaskbarDisplay.PrimaryHorizontalOffset);
        _allMonitorsToggle.Checked = settings.TaskbarDisplay.AllMonitors;
        _secondaryOffsetNumeric.Value = ClampToRange(_secondaryOffsetNumeric, settings.TaskbarDisplay.HorizontalOffset);

        _runAtStartupToggle.Checked = ConfigManager.IsRunAtStartupEnabled();
        _checkForUpdatesToggle.Checked = settings.CheckForUpdates;

        // Controls now hold the saved values, so start honouring relayout + live previews and do
        // the initial layout pass.
        _loading = false;
        Relayout();
    }

    private void OnOkClicked(object? sender, EventArgs e)
    {
        var pollMinutes = _pollIntervalCombo.SelectedIndex switch
        {
            0 => 2,
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
                PaceAlertsEnabled = _paceAlertsToggle.Checked,
                PaceSensitivity = SelectedOption(_paceSensitivityCombo, PaceSensitivityOptions),
                NearCapWarning = (int)_nearCapNumeric.Value,
                SevenDayWarning = (int)_sevenDayWarningNumeric.Value,
            },
            Notifications = new NotificationSettings
            {
                Enabled = _notificationsToggle.Checked,
                NotifyOnReset = _notifyOnResetToggle.Checked,
            },
            TaskbarDisplay = new TaskbarDisplaySettings
            {
                Enabled = _taskbarToggle.Checked,
                Style = SelectedOption(_styleCombo, StyleOptions),
                BarWidth = SelectedOption(_barWidthCombo, BarWidthOptions),
                SizePercent = (int)_sizeNumeric.Value,
                ShowSessionUsage = _showSessionToggle.Checked,
                ShowWeeklyUsage = _showWeeklyToggle.Checked,
                ShowTimeToReset = _showTimeToResetToggle.Checked,
                LabelColor = SelectedOption(_labelColorCombo, LabelColorOptions),
                NumberColor = SelectedOption(_numberColorCombo, NumberColorOptions),
                AllMonitors = _allMonitorsToggle.Checked,
                HorizontalOffset = (int)_secondaryOffsetNumeric.Value,
                PrimaryHorizontalOffset = (int)_primaryOffsetNumeric.Value,
            },
            CheckForUpdates = _checkForUpdatesToggle.Checked,
        };

        _configManager.Update(newSettings);
        ConfigManager.SetRunAtStartup(_runAtStartupToggle.Checked);
    }
}
