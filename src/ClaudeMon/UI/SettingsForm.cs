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
/// them (so the dialog height follows the current tab), within whatever height the monitor's
/// working area allows — see <see cref="SettingsFormLayout"/>. The app-wide dark mode
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
    private readonly ToggleSwitch _notifyOnServiceIncidentToggle;
    private readonly TextBox _pushTopicText;
    private readonly ToggleSwitch _dailyBudgetToggle;
    private readonly NumericUpDown _dailyCapNumeric;
    private readonly ToggleSwitch _weeklyBudgetToggle;
    private readonly NumericUpDown _weeklyCapNumeric;
    private readonly ToggleSwitch _taskbarToggle;
    private readonly ComboBox _styleCombo;
    private readonly ComboBox _barWidthCombo;
    private readonly NumericUpDown _sizeNumeric;
    private readonly ToggleSwitch _showSessionToggle;
    private readonly ToggleSwitch _showWeeklyToggle;
    private readonly ToggleSwitch _showTimeToLimitToggle;
    private readonly ToggleSwitch _showTimeToResetToggle;
    private readonly ToggleSwitch _percentSignToggle;
    private readonly ComboBox _labelColorCombo;
    private readonly ComboBox _numberColorCombo;
    private readonly NumericUpDown _primaryOffsetNumeric;
    private readonly ToggleSwitch _allMonitorsToggle;
    private readonly NumericUpDown _secondaryOffsetNumeric;
    private readonly ToggleSwitch _runAtStartupToggle;
    private readonly ToggleSwitch _checkForUpdatesToggle;
    private readonly ToggleSwitch _autoInstallUpdatesToggle;
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
    private const int TabContentGap = 12; // between the tab strip's baseline and the first row
    // Floor for the height clamp when the monitor's working area is too small to hold even the
    // window chrome; see SettingsFormLayout.ClampClientHeight.
    private const int MinClientHeight = 200;

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
        ("Auto (match taskbar)", TaskbarTextColor.MatchTaskbar),
        ("White", TaskbarTextColor.White),
        ("Black", TaskbarTextColor.Black),
        ("Light gray", TaskbarTextColor.LightGray),
        ("Dark gray", TaskbarTextColor.DarkGray),
    ];

    private static readonly (string Text, TaskbarTextColor Value)[] NumberColorOptions =
    [
        ("Auto (usage level)", TaskbarTextColor.Auto),
        ("Auto (match taskbar)", TaskbarTextColor.MatchTaskbar),
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
        // Manual + CenterOnPrimary in OnLoad — all app dialogs open on the primary monitor (#88).
        StartPosition = FormStartPosition.Manual;
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
        _hspec.Add((_tabStrip, Pad, ContentRight - Pad, TabStrip.LogicalHeight));

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
        // Off by default: the flyout already shows an incident passively (issue #132).
        _notifyOnServiceIncidentToggle = AddToggleRow("Notify on Anthropic service incidents",
            indent: true, visible: AlertsOn);
        // Push notifications (ntfy.sh), in addition to the desktop balloon — see PushNotifier.
        // Blank disables it; there's no default topic, since a topic is a de facto shared
        // secret (anyone who knows an unauthenticated ntfy topic name can read it) and so has
        // to be something the user picks, not something ClaudeMon invents on their behalf.
        _pushTopicText = AddTextRow("Push notification topic (ntfy.sh)", indent: true, visible: AlertsOn,
            placeholder: "blank = disabled");
        // Estimated-cost budgets (issue #74), computed from the local Claude Code
        // transcripts. Caps are dollars, two decimals; sub-rows collapse with
        // their toggle like the pace sensitivity row above.
        _dailyBudgetToggle = AddToggleRow("Daily budget alert (est. cost)", indent: true, visible: AlertsOn);
        _dailyCapNumeric = AddNumericRow("Daily cap", 1, 10_000,
            indent: true, visible: () => AlertsOn() && _dailyBudgetToggle.Checked, suffix: "USD");
        _dailyCapNumeric.DecimalPlaces = 2;
        _weeklyBudgetToggle = AddToggleRow("Weekly budget alert (Mon–Sun)", indent: true, visible: AlertsOn);
        _weeklyCapNumeric = AddNumericRow("Weekly cap", 1, 10_000,
            indent: true, visible: () => AlertsOn() && _weeklyBudgetToggle.Checked, suffix: "USD");
        _weeklyCapNumeric.DecimalPlaces = 2;

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
        // The two time elements are Numbers-style; the bar draws no text (and has its own time
        // tick), so these rows hide in Bar mode rather than offering toggles that do nothing there.
        _showTimeToLimitToggle = AddToggleRow("Show time to limit (estimated)", indent: true,
            visible: () => TaskbarOn() && !IsBar());
        _showTimeToResetToggle = AddToggleRow("Show time left to reset", indent: true,
            visible: () => TaskbarOn() && !IsBar());
        // Percentages are a Numbers-style element (the bar draws no numbers), so the row
        // hides in Bar mode like the countdown toggle above.
        _percentSignToggle = AddToggleRow("Show % sign after percentages", indent: true,
            visible: () => TaskbarOn() && !IsBar());
        _labelColorCombo = AddComboRow("\"Claude\" label color", LabelColorOptions.Select(o => o.Text),
            indent: true, visible: () => TaskbarOn() && !IsBar());
        _numberColorCombo = AddComboRow("Percentage color", NumberColorOptions.Select(o => o.Text),
            indent: true, visible: () => TaskbarOn() && !IsBar());
        _allMonitorsToggle = AddToggleRow("Show on secondary monitors", indent: true, visible: TaskbarOn);
        // Named like the primary row — this one only shows indented under "Show on secondary
        // monitors", so that context carries the "secondary" meaning (#105).
        _secondaryOffsetNumeric = AddNumericRow("Position (− left / + right)", -300, 300,
            indent: true, visible: () => TaskbarOn() && _allMonitorsToggle.Checked, suffix: null);
        _secondaryOffsetNumeric.Increment = 2;

        // --- Updates tab ---
        _currentTab = 3;
        _checkForUpdatesToggle = AddToggleRow("Check for updates automatically");
        // Silent download + install on the automatic check. Hidden while checks are off — with
        // no check there is nothing to auto-install.
        _autoInstallUpdatesToggle = AddToggleRow("Install updates automatically", indent: true,
            visible: () => _checkForUpdatesToggle.Checked);

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
        ScrollDialogInsteadOfEditing(combo);
        return combo;
    }

    private TextBox AddTextRow(
        string label, bool indent = false, Func<bool>? visible = null, string? placeholder = null)
    {
        var left = indent ? Pad + 16 : Pad;
        var lbl = new Label
        {
            Text = label,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var textBox = new TextBox { PlaceholderText = placeholder ?? string.Empty };
        Controls.Add(lbl);
        Controls.Add(textBox);
        _hspec.Add((lbl, left, ControlLeft - left - 8, 24));
        _hspec.Add((textBox, ControlLeft, ComboWidth, 0));

        _rows.Add(new RowDef { Items = [(lbl, 2), (textBox, 2)], Height = 34, Tab = _currentTab, Visible = visible });
        return textBox;
    }

    private NumericUpDown AddNumericRow(
        string label, int min, int max, bool indent = false, Func<bool>? visible = null, string? suffix = "%")
    {
        // Fixed width capped short of the numeric's left edge, with AutoEllipsis — a label the
        // font renders wider than the column truncates with "…" instead of running under the
        // spinner (#105). MiddleLeft in a fixed-height label keeps the text on the numeric's
        // vertical centre, where the AutoSize baseline used to sit.
        var left = indent ? Pad + 16 : Pad;
        var lbl = new Label
        {
            Text = label,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var numeric = new ThemedNumericUpDown { Minimum = min, Maximum = max };
        Controls.Add(lbl);
        Controls.Add(numeric);
        // 24 high (not the ~18 the font needs) so text scaled up without a DPI change — the same
        // accessibility setting that causes the overflow — has vertical headroom too.
        _hspec.Add((lbl, left, ControlLeft - left - 8, 24));
        _hspec.Add((numeric, ControlLeft, NumericWidth, 0));

        var items = new List<(Control, int)> { (lbl, 2), (numeric, 3) };
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
        ScrollDialogInsteadOfEditing(numeric);
        return numeric;
    }

    // Windows delivers the wheel to the focused control, so once the dialog scrolls (#139) a wheel
    // gesture aimed at the window would instead silently change whichever dropdown or spinner was
    // last clicked — and OK would save it. While the dialog scrolls, take the wheel away from the
    // control and scroll the window with it; a dialog that fits the monitor never sees this at all,
    // so the wheel keeps adjusting the focused control exactly as it always has.
    private void ScrollDialogInsteadOfEditing(Control control) =>
        control.MouseWheel += (_, e) =>
        {
            if (!AutoScroll || e is not HandledMouseEventArgs handled)
                return;

            handled.Handled = true;
            OnMouseWheel(e);
        };

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
    private void Relayout(bool preserveScroll = true)
    {
        // Every top below is an unscrolled coordinate, so scroll back to the origin before writing
        // them — otherwise WinForms offsets each control we position by the current scroll amount
        // and the rows drift up the window (#139). Where the row the user is looking at survives
        // the relayout (a sub-option expanding, not a tab switch) the offset is restored at the
        // end, so ticking a toggle at the bottom of a scrolled tab doesn't jump them to the top.
        var scrolledTo = AutoScroll && preserveScroll ? -AutoScrollPosition.Y : 0;
        if (AutoScroll)
            AutoScrollPosition = Point.Empty;

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

        // The height the content wants — which the monitor may not have room for. Clamping it, and
        // then sliding the window back up under the bottom of the working area, is what keeps the
        // OK/Cancel row on screen on a short display or at a large scale factor; the overflow
        // becomes a scrollbar instead of falling off the bottom (#139). Both halves matter: the
        // window grows downwards from the top it was centered at for the tab it opened on, so a
        // taller tab can run off the bottom long before it is too tall for the monitor at all.
        var area = CurrentWorkingArea();
        var contentHeight = y + _okButton.Height + Sc(Pad);
        var (clientHeight, scroll) = SettingsFormLayout.ClampClientHeight(
            contentHeight, area.Height, Height - ClientSize.Height, Sc(MinClientHeight));

        // Setting a non-empty AutoScrollMinSize turns AutoScroll on by itself; the assignment
        // after it is what turns it back off once a shorter tab fits again. AdjustWindowRectEx
        // ignores WS_VSCROLL, so the scrollbar comes out of the client width rather than widening
        // the window, and it fits in the right margin — but only just: the rows end at 456 logical
        // and carry a 3px unscaled Margin, against a client width of 480 less a ~17px scrollbar.
        // That is ~4px of slack, so don't grow ContentRight without re-checking it, or a spurious
        // horizontal scrollbar appears (and inflates the chrome measured above by ~17px).
        AutoScrollMinSize = scroll ? new Size(0, contentHeight) : Size.Empty;
        AutoScroll = scroll;
        ClientSize = new Size(Sc(480), clientHeight);

        // Only once the window exists — before that, Top is meaningless and OnLoad's
        // CenterOnPrimary does the opening placement anyway.
        if (IsHandleCreated)
            Top = SettingsFormLayout.ClampTop(Top, Height, area.Top, area.Bottom);

        // Last word on the scroll offset: hiding the focused control (which is what switching tabs
        // does) makes WinForms scroll whatever gains focus into view, so the reset at the top of
        // this method is not enough on its own. WinForms clamps the value to the new range.
        if (scroll)
            AutoScrollPosition = new Point(0, scrolledTo);
    }

    /// <summary>
    /// The working area both clamps measure against: the monitor the dialog is on once it has a
    /// window, the primary monitor's before that. Those agree for the first layout — the
    /// dialog is centered on the primary in <see cref="OnLoad"/> (#88) and an as-yet-unplaced form
    /// sits at the origin, which is on the primary by definition — so the pass that runs before
    /// the window is shown already clamps against the monitor the user will see it on.
    /// </summary>
    private Rectangle CurrentWorkingArea()
    {
        try
        {
            if (IsHandleCreated)
                return Screen.FromControl(this).WorkingArea;
        }
        catch
        {
            // Monitor enumeration can fail in odd session states; fall through to the primary.
        }

        return DialogPlacement.PrimaryWorkingArea();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // DeviceDpi is only reliable once the handle exists; the constructor's Relayout ran at the
        // default DPI, so redo it here (before first paint) at the real monitor DPI.
        Relayout();
        DialogPlacement.CenterOnPrimary(this);
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
        // Switching tabs swaps which rows are visible (and re-fits the window height). A new tab
        // starts at its top, unlike the sub-option toggles below, which keep their scroll offset.
        _tabStrip.SelectedIndexChanged += (_, _) => RelayoutLive(preserveScroll: false);

        // Collapse/expand on the gating toggles; some also live-preview the taskbar appearance.
        _notificationsToggle.CheckedChanged += (_, _) => RelayoutLive();
        _checkForUpdatesToggle.CheckedChanged += (_, _) => RelayoutLive();
        _paceAlertsToggle.CheckedChanged += (_, _) => RelayoutLive();
        _dailyBudgetToggle.CheckedChanged += (_, _) => RelayoutLive();
        _weeklyBudgetToggle.CheckedChanged += (_, _) => RelayoutLive();
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
        _showTimeToLimitToggle.CheckedChanged += (_, _) => PreviewDisplay();
        _showTimeToResetToggle.CheckedChanged += (_, _) => PreviewDisplay();
        _percentSignToggle.CheckedChanged += (_, _) => PreviewDisplay();
        _labelColorCombo.SelectedIndexChanged += (_, _) => PreviewColors();
        _numberColorCombo.SelectedIndexChanged += (_, _) => PreviewColors();
        _primaryOffsetNumeric.ValueChanged += (_, _) => PreviewOffsets();
        _secondaryOffsetNumeric.ValueChanged += (_, _) => PreviewOffsets();
    }

    private void RelayoutLive(bool preserveScroll = true)
    {
        if (!_loading)
            Relayout(preserveScroll);
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
        SelectedMetrics(), _percentSignToggle.Checked));

    /// <summary>The display toggles as the selection the overlay and the cycle logic speak in.</summary>
    private TaskbarMetricSelection SelectedMetrics() => new(
        _showSessionToggle.Checked, _showWeeklyToggle.Checked,
        _showTimeToLimitToggle.Checked, _showTimeToResetToggle.Checked);

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
            _overlayPreview.SetDisplay(t.Metrics, t.ShowPercentSign);
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

    // Double overload for the dollar caps. NaN/infinity (a hand-edited config)
    // coerces to the minimum rather than throwing in the decimal conversion.
    private static decimal ClampToRange(NumericUpDown numeric, double value) =>
        double.IsFinite(value)
            ? Math.Clamp((decimal)Math.Clamp(value, -1e15, 1e15), numeric.Minimum, numeric.Maximum)
            : numeric.Minimum;

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
        _notifyOnServiceIncidentToggle.Checked = settings.Notifications.NotifyOnServiceIncident;
        _pushTopicText.Text = settings.Notifications.PushTopic ?? string.Empty;
        _dailyBudgetToggle.Checked = settings.Budgets.DailyEnabled;
        _dailyCapNumeric.Value = ClampToRange(_dailyCapNumeric, settings.Budgets.DailyCapUsd);
        _weeklyBudgetToggle.Checked = settings.Budgets.WeeklyEnabled;
        _weeklyCapNumeric.Value = ClampToRange(_weeklyCapNumeric, settings.Budgets.WeeklyCapUsd);

        _taskbarToggle.Checked = settings.TaskbarDisplay.Enabled;
        SelectOption(_styleCombo, StyleOptions, settings.TaskbarDisplay.Style);
        SelectOption(_barWidthCombo, BarWidthOptions, settings.TaskbarDisplay.BarWidth);
        _sizeNumeric.Value = ClampToRange(_sizeNumeric, settings.TaskbarDisplay.SizePercent);
        _showSessionToggle.Checked = settings.TaskbarDisplay.ShowSessionUsage;
        _showWeeklyToggle.Checked = settings.TaskbarDisplay.ShowWeeklyUsage;
        _showTimeToLimitToggle.Checked = settings.TaskbarDisplay.ShowTimeToLimit;
        _showTimeToResetToggle.Checked = settings.TaskbarDisplay.ShowTimeToReset;
        _percentSignToggle.Checked = settings.TaskbarDisplay.ShowPercentSign;
        SelectOption(_labelColorCombo, LabelColorOptions, settings.TaskbarDisplay.LabelColor);
        SelectOption(_numberColorCombo, NumberColorOptions, settings.TaskbarDisplay.NumberColor);
        _primaryOffsetNumeric.Value = ClampToRange(_primaryOffsetNumeric, settings.TaskbarDisplay.PrimaryHorizontalOffset);
        _allMonitorsToggle.Checked = settings.TaskbarDisplay.AllMonitors;
        _secondaryOffsetNumeric.Value = ClampToRange(_secondaryOffsetNumeric, settings.TaskbarDisplay.HorizontalOffset);

        _runAtStartupToggle.Checked = ConfigManager.IsRunAtStartupEnabled();
        _checkForUpdatesToggle.Checked = settings.CheckForUpdates;
        _autoInstallUpdatesToggle.Checked = settings.AutoInstallUpdates;

        // Controls now hold the saved values, so start honouring relayout + live previews and do
        // the initial layout pass.
        _loading = false;
        Relayout();
    }

    private void OnOkClicked(object? sender, EventArgs e)
    {
        _configManager.Update(BuildSettings());
        ConfigManager.SetRunAtStartup(_runAtStartupToggle.Checked);
    }

    /// <summary>
    /// The settings the dialog's controls describe, layered onto the saved settings with
    /// <c>with</c> so everything the dialog doesn't edit survives the save. Separate from
    /// <see cref="OnOkClicked"/> so that layering is testable without OK's registry write.
    /// </summary>
    internal AppSettings BuildSettings()
    {
        var pollMinutes = _pollIntervalCombo.SelectedIndex switch
        {
            0 => 2,
            1 => 3,
            2 => 5,
            3 => 10,
            _ => 5,
        };

        // Pulled out of the initializer below because the click-to-cycle home is derived from
        // these and the saved toggles (see CycleHome), and a `with` initializer can't read its
        // own members.
        var savedTaskbar = _configManager.Settings.TaskbarDisplay;
        var taskbarStyle = SelectedOption(_styleCombo, StyleOptions);
        var taskbarMetrics = new TaskbarMetricSelection(
            Session: _showSessionToggle.Checked,
            Weekly: _showWeeklyToggle.Checked,
            TimeToLimit: _showTimeToLimitToggle.Checked,
            TimeToReset: _showTimeToResetToggle.Checked);

        return _configManager.Settings with
        {
            PollIntervalMinutes = pollMinutes,
            // `with` on the existing record, not `new` — for the same reason as TaskbarDisplay
            // and Notifications below: every AlertThresholds field has a control today, so a
            // reconstruction loses nothing yet, but the first field added without one would be
            // reset on every settings save.
            AlertThresholds = _configManager.Settings.AlertThresholds with
            {
                PaceAlertsEnabled = _paceAlertsToggle.Checked,
                PaceSensitivity = SelectedOption(_paceSensitivityCombo, PaceSensitivityOptions),
                NearCapWarning = (int)_nearCapNumeric.Value,
                SevenDayWarning = (int)_sevenDayWarningNumeric.Value,
            },
            // `with` on the existing record, not `new` — a reconstruction would
            // silently drop SnoozeUntil (an active snooze) on every settings save.
            Notifications = _configManager.Settings.Notifications with
            {
                Enabled = _notificationsToggle.Checked,
                NotifyOnReset = _notifyOnResetToggle.Checked,
                NotifyOnServiceIncident = _notifyOnServiceIncidentToggle.Checked,
                PushTopic = string.IsNullOrWhiteSpace(_pushTopicText.Text) ? null : _pushTopicText.Text.Trim(),
            },
            // `with`, not `new` — same hazard as AlertThresholds above: the dialog edits all four
            // budget fields today, and a fifth one without a control would silently snap back to
            // its default on every save.
            Budgets = _configManager.Settings.Budgets with
            {
                DailyEnabled = _dailyBudgetToggle.Checked,
                DailyCapUsd = (double)_dailyCapNumeric.Value,
                WeeklyEnabled = _weeklyBudgetToggle.Checked,
                WeeklyCapUsd = (double)_weeklyCapNumeric.Value,
            },
            // `with` on the existing record, not `new` — for the same reason as Notifications
            // above: a reconstruction silently resets every field the dialog doesn't edit
            // (today the migration-only LegacyShowSevenDay) on every settings save.
            TaskbarDisplay = savedTaskbar with
            {
                Enabled = _taskbarToggle.Checked,
                Style = taskbarStyle,
                BarWidth = SelectedOption(_barWidthCombo, BarWidthOptions),
                SizePercent = (int)_sizeNumeric.Value,
                ShowSessionUsage = taskbarMetrics.Session,
                ShowWeeklyUsage = taskbarMetrics.Weekly,
                ShowTimeToLimit = taskbarMetrics.TimeToLimit,
                ShowTimeToReset = taskbarMetrics.TimeToReset,
                ShowPercentSign = _percentSignToggle.Checked,
                // Editing these toggles also (re)sets the composition click-to-cycle wraps back
                // to (#156): they are the source of truth for the readout, so a home left over
                // from a composition you have since edited away must not reappear on a wrap.
                // Leaving them alone says nothing about it, though — recomputing unconditionally
                // would mean that opening Settings mid-cycle to change the poll interval quietly
                // destroyed the layout the wrap was about to restore, which is the very loss
                // this exists to prevent. Compared against the saved toggles rather than the
                // ones the dialog loaded because the two are the same thing here: the gesture
                // stands down while Settings is open, so only the user can make them differ.
                CycleHome = taskbarMetrics == savedTaskbar.Metrics
                    ? savedTaskbar.CycleHome
                    : TaskbarMetricCycle.HomeFor(taskbarMetrics, taskbarStyle),
                LabelColor = SelectedOption(_labelColorCombo, LabelColorOptions),
                NumberColor = SelectedOption(_numberColorCombo, NumberColorOptions),
                AllMonitors = _allMonitorsToggle.Checked,
                HorizontalOffset = (int)_secondaryOffsetNumeric.Value,
                PrimaryHorizontalOffset = (int)_primaryOffsetNumeric.Value,
            },
            CheckForUpdates = _checkForUpdatesToggle.Checked,
            AutoInstallUpdates = _autoInstallUpdatesToggle.Checked,
        };
    }
}
