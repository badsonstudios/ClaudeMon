namespace ClaudeMon.UI;

using System.Drawing;
using System.Globalization;
using System.Text;
using ClaudeMon.Models;
using ClaudeMon.Monitoring;
using ClaudeMon.Services;

/// <summary>
/// The "Usage &amp; costs" window (issue #74): per-model and per-project
/// cost/token tables over a selectable timeframe (Today / 7 / 30 days),
/// computed locally from the Claude Code transcripts, with CSV export.
/// Follows the <see cref="AboutDialog"/> conventions — <c>AutoScaleMode.None</c>
/// with hand-scaled metrics, <see cref="Theme"/> accents, deliberate monitor
/// placement, re-layout on load and DPI change — except that it opens on the
/// monitor holding the foreground window rather than on the primary (#116),
/// the same <see cref="ForegroundMonitor"/> path the update dialogs use (#108).
/// Data is pulled through <see cref="LocalUsageMonitor"/>'s thread-safe queries
/// on open and whenever the timeframe changes; the window shows a static picture
/// (no live refresh — reopen for fresh numbers, matching how the flyout
/// snapshots on open).
///
/// Either table can be re-sorted by clicking a column header (#111); the ordering itself lives in
/// the pure <see cref="BreakdownSort"/>, which sorts the <see cref="BreakdownRow"/> numbers rather
/// than the formatted cell text and keeps the totals row pinned to the bottom.
///
/// Selecting a row drills into it (#112): the two tables are the two axes of the same cells, so a
/// selected model turns the project table into "the projects that model ran in" (and a selected
/// project turns the model table into "the models that project used") — the pure
/// <see cref="BreakdownDrill"/> slicing <see cref="LocalUsageBreakdown.Pairs"/> of the breakdown
/// already on screen, with the axes swapped. Slicing the same snapshot rather than re-querying the
/// store is what keeps a drill-down from totalling more than the row it drills into on a window
/// that has been open across a scan. The selection lives in one table at a time; the other table's
/// heading says what it is showing and grows a "Show all" button to get back. Deliberately no
/// third table: the window is already two tables tall. Exporting while drilled writes the drilled
/// tables rather than the breakdown behind them, scope named in the file and in the suggested file
/// name (#168) — the window's rule is that the file is what you are looking at.
///
/// A <see cref="TabStrip"/> (#113) puts a cost-per-day <see cref="CostChart"/> behind a second tab
/// rather than stacking it under the tables, which would push them off a compact window. Tables is
/// the default tab and the window still opens at the size it always did — the strip's row comes out
/// of the tables' share, not out of the window's height. The tabs switch <i>views</i>, not filters:
/// both describe the same timeframe, and the tables keep their sort and drill-down while the chart
/// is up. The chart is deliberately whole-timeframe — a drilled-into project does not narrow it,
/// since per-project series are out of scope for #113.
///
/// Unlike the app's other windows this one is <b>resizable</b> (#110): a month of
/// usage across several projects doesn't fit a fixed 700×150 viewport. The
/// hand-scaled convention is kept — no <c>AutoScaleMode</c>, no anchors — but
/// <see cref="Relayout"/> is driven by the current <see cref="Form.ClientSize"/>
/// instead of dictating it, and the size/column math lives in the pure,
/// unit-tested <see cref="UsageBreakdownLayout"/>. Size and position are not
/// remembered between sessions (deliberately out of scope for #110).
/// </summary>
internal sealed class UsageBreakdownForm : Form
{
    // Layout metrics, logical (96-DPI) units.
    private const int Pad = 20;
    private const int DefaultClientWidth = 700;
    private const int HeaderTop = 16;
    private const int SectionGap = 14;
    private const int LabelGap = 6;
    private const int DefaultTableHeight = 150;
    /// <summary>Floor for each table: its header plus about two rows stay visible.</summary>
    private const int MinTableHeight = 66;
    private const int ButtonHeight = 30;
    private const int ButtonWidth = 100;
    private const int ButtonGap = 8;
    private const int CloseButtonWidth = 82;
    private const int ComboWidth = 140;
    /// <summary>The "Show all" button that ends a drill-down, right-aligned in a section heading.</summary>
    private const int ShowAllWidth = 84;
    private const int ShowAllHeight = 22;
    private const int NumericColumn = 62;
    private const int CostColumn = 78;
    private const int MinFirstColumn = 120;

    /// <summary>
    /// Both edges of a <see cref="BorderStyle.FixedSingle"/> border. Not DPI-scaled — it is a
    /// one-pixel window frame at every scale — and measured as a constant rather than from
    /// <c>Width - ClientSize.Width</c>, which would also swallow a visible scrollbar.
    /// </summary>
    private const int TableBorder = 2;

    private const int WM_DPICHANGED = 0x02E0;

    private readonly Theme _theme = Theme.Current;
    private readonly LocalUsageMonitor _localUsage;
    private readonly Logger? _logger;
    private readonly LimitLogStore? _limitLog;
    private readonly Action? _onLimitHistoryViewed;

    private readonly Font _baseFont = new("Segoe UI", 9.75f);
    private readonly Font _headingFont = new("Segoe UI Semibold", 11.25f);

    private readonly Label _heading;
    private readonly Label _timeframeLabel;
    private readonly ComboBox _timeframeCombo;
    private readonly Label _selectHint;
    private readonly TabStrip _tabStrip;
    private readonly Label _chartLabel;
    private readonly CostChart _chart;
    private readonly Label _modelLabel;
    private readonly ListView _modelList;
    private readonly Button _modelShowAll;
    private readonly Label _projectLabel;
    private readonly ListView _projectList;
    private readonly Button _projectShowAll;
    private readonly Label _hint;
    private readonly Button _exportButton;
    private readonly Button _closeButton;

    // The Limit history tab (#186): per-window capacity chart + table over the forever log,
    // loaded lazily one month-page at a time so memory never scales with log size.
    private readonly Label _limitLabel;
    private readonly ComboBox _limitViewCombo;
    private readonly ComboBox _limitKindCombo;
    private readonly LimitHistoryChart _limitChart;
    private readonly ListView _limitList;
    private readonly Button _limitLoadOlder;
    private List<LimitHistoryRow> _limitRows = [];
    private LimitWindowSortState _limitSort = LimitWindowSortState.Default;
    private DateTime? _limitOldestLoaded;
    private bool _limitLoadedOnce;

    private LocalUsageBreakdown? _current;
    private bool _updatingMinimum;
    private bool _wasMinimized;

    // Placement (#116, following #108). The monitor is resolved once in OnLoad and reused if a
    // DPI change arrives before the window is visible; _shown then freezes it, so dragging the
    // window to another monitor afterwards never re-centers it.
    private Rectangle? _placementArea;
    private bool _shown;
    private bool _placing;

    // The row selected in one table, resolved against the other axis — null when nothing is
    // drilled into and both tables show everything.
    private LocalUsageDrillDown? _drill;
    // Filling a table changes its selection, which would re-enter the selection handler.
    private bool _suppressSelection;
    // One click that moves a selection raises SelectedIndexChanged twice (the row losing it, then
    // the row gaining it), so the response is posted once and reads the settled selection.
    private bool _selectionPending;
    private (ListView List, BreakdownAxis Axis)? _lastSelectionSource;

    // One per table, so the two sort independently. Kept across a timeframe change on purpose:
    // switching Today → 30 days should answer the same question, not reset it.
    private BreakdownSortState _modelSort = BreakdownSortState.Default;
    private BreakdownSortState _projectSort = BreakdownSortState.Default;

    /// <summary>
    /// The six numeric columns, in the order <see cref="BreakdownSortColumn"/> lists them after
    /// <see cref="BreakdownSortColumn.Name"/>. A clicked column index is cast straight to that
    /// enum, so a column added, removed or moved here has to move there too.
    /// </summary>
    private static readonly string[] NumericColumns =
        ["Input", "Output", "Cache W", "Cache R", "Tokens", "Cost (est.)"];

    private static readonly (string Text, BreakdownTimeframe Value)[] TimeframeOptions =
    [
        ("Today", BreakdownTimeframe.Today),
        ("Last 7 days", BreakdownTimeframe.SevenDays),
        ("Last 30 days", BreakdownTimeframe.ThirtyDays),
    ];

    /// <summary>The limit table's eight columns — indexes cast straight to <see cref="LimitWindowColumn"/>.</summary>
    private static readonly (string Text, int Width, bool Right)[] LimitColumns =
    [
        ("Start", 104, false),
        ("End", 104, false),
        ("Kind", 108, false),
        ("Peak %", 56, true),
        ("Tokens", 64, true),
        ("Top model", 128, false),
        ("Capacity (est.)", 88, true),
        ("Plan", 88, false),
    ];

    private static readonly (string Text, string? Kind)[] LimitKindOptions =
    [
        ("All kinds", null),
        ("Session (5-hour)", "session"),
        ("Weekly", "weekly_all"),
        ("Per-model weekly", "weekly_scoped"),
    ];

    public UsageBreakdownForm(
        LocalUsageMonitor localUsage, Logger? logger = null,
        LimitLogStore? limitLog = null, Action? onLimitHistoryViewed = null)
    {
        _localUsage = localUsage;
        _logger = logger;
        _limitLog = limitLog;
        _onLimitHistoryViewed = onLimitHistoryViewed;

        Text = "ClaudeMon — Usage & costs";
        // The one resizable window in the app (#110) — the tables are the content, and there is
        // never a "right" fixed height for them. MinimizeBox stays off: this is an ownerless
        // modal guarded by TrayApplication._breakdownOpen, so a minimized copy would swallow
        // further menu clicks with nothing on screen to explain why.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        // Keeps the title bar icon-less (the existing look, and the convention these dialogs
        // follow). It also keeps WM_GETICON answering 0, which is how Windows is left to
        // synthesize the taskbar/Alt-Tab icon from the executable's own resource — see the
        // <ApplicationIcon> note in ClaudeMon.csproj (#108). FixedDialog used to imply this.
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Font = _baseFont;

        _heading = new Label
        {
            Text = "Usage & costs",
            // Labels treat '&' as a mnemonic marker and swallow it — the
            // heading rendered "Usage costs" without this.
            UseMnemonic = false,
            AutoSize = true,
            Font = _headingFont,
            ForeColor = _theme.HeaderAccent,
        };
        Controls.Add(_heading);

        _timeframeLabel = new Label { Text = "Timeframe:", AutoSize = true };
        Controls.Add(_timeframeLabel);

        _timeframeCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        _timeframeCombo.Items.AddRange(TimeframeOptions.Select(o => (object)o.Text).ToArray());
        _timeframeCombo.SelectedIndex = 0;
        _timeframeCombo.SelectedIndexChanged += (_, _) => Reload();
        Controls.Add(_timeframeCombo);

        _selectHint = new Label
        {
            Text = "Select a row to break it down.",
            AutoSize = true,
            ForeColor = _theme.HintText,
        };
        Controls.Add(_selectHint);

        // Tables first and selected by default: the numbers are what this window is for, and the
        // chart is one click away when the question is "how is it trending?" instead (#113).
        _tabStrip = new TabStrip("Tables", "Chart", "Limit history") { AccessibleName = "Usage views" };
        _tabStrip.SelectedIndexChanged += (_, _) =>
        {
            OnLimitTabMaybeSelected();
            ApplyTab();
            // A mouse click on a tab header doesn't move the focus (TabStrip only sets its own
            // SelectedIndex), so hiding the table that had it would let WinForms hand the focus to
            // the next control in the tab order — the Export button, several rows away. Taking it
            // onto the strip is both where the user just clicked and where the Left/Right arrow
            // keys work. The same reasoning as ClearDrill's source.Focus().
            _tabStrip.Focus();
        };
        Controls.Add(_tabStrip);

        // The chart says what it is, and — because it is deliberately whole-timeframe — says so
        // when a drill-down is running underneath it. Without this the Chart tab is the one place
        // where the window shows unlabelled numbers with a narrower question still on screen.
        _chartLabel = MakeSectionLabel(BreakdownDrillText.ChartSection(null));
        _chartLabel.Visible = false;
        Controls.Add(_chartLabel);

        // Laid out in the same region as the tables and simply hidden behind them, so switching
        // tabs is a visibility flip rather than a second layout pass.
        _chart = new CostChart { Visible = false };
        Controls.Add(_chart);

        _modelLabel = MakeSectionLabel(BreakdownDrillText.ModelSection(null));
        Controls.Add(_modelLabel);
        _modelList = MakeTable("Model");
        _modelList.ColumnClick += (_, e) =>
        {
            _modelSort = _modelSort.Toggle(e.Column);
            FillModels();
        };
        _modelList.SelectedIndexChanged += (_, _) => OnRowSelected(_modelList, BreakdownAxis.Model);
        Controls.Add(_modelList);
        _modelShowAll = MakeShowAllButton();
        Controls.Add(_modelShowAll);

        _projectLabel = MakeSectionLabel(BreakdownDrillText.ProjectSection(null));
        Controls.Add(_projectLabel);
        _projectList = MakeTable("Project");
        _projectList.ColumnClick += (_, e) =>
        {
            _projectSort = _projectSort.Toggle(e.Column);
            FillProjects();
        };
        _projectList.SelectedIndexChanged += (_, _) => OnRowSelected(_projectList, BreakdownAxis.Project);
        Controls.Add(_projectList);
        _projectShowAll = MakeShowAllButton();
        Controls.Add(_projectShowAll);

        // --- Limit history tab (#186), laid out in the same region and hidden behind the
        // tables until its tab comes forward — the Chart tab's convention. ---
        _limitLabel = MakeSectionLabel("Recorded limit windows");
        _limitLabel.Visible = false;
        Controls.Add(_limitLabel);

        // The chart before the combo whose handler drives its mode, so the capture is
        // provably initialized.
        _limitChart = new LimitHistoryChart { Visible = false, BackColor = _theme.FieldBack };
        Controls.Add(_limitChart);

        _limitViewCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
        _limitViewCombo.Items.AddRange(["Capacity over time", "Tokens & peak per window"]);
        _limitViewCombo.SelectedIndex = 0;
        _limitViewCombo.SelectedIndexChanged += (_, _) =>
        {
            _limitChart.Mode = _limitViewCombo.SelectedIndex == 1
                ? LimitHistoryChartMode.Utilization
                : LimitHistoryChartMode.Capacity;
        };
        Controls.Add(_limitViewCombo);

        _limitKindCombo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Visible = false };
        _limitKindCombo.Items.AddRange(LimitKindOptions.Select(o => (object)o.Text).ToArray());
        _limitKindCombo.SelectedIndex = 0;
        _limitKindCombo.SelectedIndexChanged += (_, _) => RefreshLimitViews();
        Controls.Add(_limitKindCombo);

        _limitList = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Clickable,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = _theme.FieldBack,
            ForeColor = _theme.FieldText,
            Visible = false,
            ShowItemToolTips = true,
        };
        foreach (var (text, _, right) in LimitColumns)
            _limitList.Columns.Add(text, -2, right ? HorizontalAlignment.Right : HorizontalAlignment.Left);
        _limitList.ColumnClick += (_, e) =>
        {
            _limitSort = _limitSort.Toggle(e.Column);
            FillLimitList();
        };
        Controls.Add(_limitList);

        _limitLoadOlder = MakeButton("Load older");
        _limitLoadOlder.Visible = false;
        _limitLoadOlder.Click += (_, _) => LoadOlderLimitPage();
        Controls.Add(_limitLoadOlder);

        _hint = new Label
        {
            Text = CostHintText,
            AutoSize = true,
            ForeColor = _theme.HintText,
        };
        Controls.Add(_hint);

        _exportButton = MakeButton("Export CSV...");
        _exportButton.Click += (_, _) => ExportCsv();
        Controls.Add(_exportButton);

        _closeButton = MakeButton("Close");
        _closeButton.DialogResult = DialogResult.OK;
        Controls.Add(_closeButton);

        AcceptButton = _closeButton;
        CancelButton = _closeButton;

        // The tables were added before this bottom band, which in WinForms puts them in *front* of
        // it (index 0 is the front). That never mattered while MinimumSize guaranteed room for
        // everything, but since #172 the floor is capped at the monitor, so on a screen too small
        // to hold the window the tables keep their floor height and run under the hint and the
        // buttons. Put the band back in front, where a ListView can no longer paint over Export and
        // Close — or, worse, swallow their clicks. No effect at any size that fits.
        _hint.BringToFront();
        _exportButton.BringToFront();
        _closeButton.BringToFront();

        Reload();
        ApplyTab();
        ClientSize = DefaultClientSize();
        Relayout();
    }

    /// <summary>What the window shows right now: the selected tab crossed with the drill state.</summary>
    private BreakdownTabVisibility Visibility => BreakdownTabView.For(
        _tabStrip.SelectedIndex,
        DrillInto(BreakdownAxis.Model) is not null,
        DrillInto(BreakdownAxis.Project) is not null);

    // The tabs share one layout, so switching them only changes what is visible. The rule itself
    // lives in the pure BreakdownTabView -- a drill-down survives a trip to the Chart tab, so a
    // "Show all" button's visibility depends on the tab as well as on the drill.
    private const string CostHintText =
        "Estimates at API list prices, computed locally from Claude Code transcripts — not billing.";
    private const string LimitsHintText =
        "Every finalized rate-limit window from the local log; capacities are estimates from this machine's tokens.";

    private void ApplyTab()
    {
        var visible = Visibility;

        _selectHint.Visible = visible.SelectHint;
        _timeframeLabel.Visible = visible.Timeframe;
        _timeframeCombo.Visible = visible.Timeframe;
        _modelLabel.Visible = visible.Tables;
        _modelList.Visible = visible.Tables;
        _projectLabel.Visible = visible.Tables;
        _projectList.Visible = visible.Tables;
        _chartLabel.Visible = visible.Chart;
        _chart.Visible = visible.Chart;
        _modelShowAll.Visible = visible.ModelShowAll;
        _projectShowAll.Visible = visible.ProjectShowAll;
        _limitLabel.Visible = visible.Limits;
        _limitViewCombo.Visible = visible.Limits;
        _limitKindCombo.Visible = visible.Limits;
        _limitChart.Visible = visible.Limits;
        _limitList.Visible = visible.Limits;
        _limitLoadOlder.Visible = visible.Limits;
        _exportButton.Visible = visible.Export;
        // One hint label serves all three tabs; the limits tab tells its own truth.
        _hint.Text = visible.Limits ? LimitsHintText : CostHintText;
    }

    // First selection loads the newest page (lazy: users who never open the tab never read a
    // file), and every selection acknowledges an active drift episode — the tab is where the
    // evidence lives, so viewing it is the natural "I've seen this".
    private void OnLimitTabMaybeSelected()
    {
        if (_tabStrip.SelectedIndex != BreakdownTabView.LimitsTab)
            return;

        if (!_limitLoadedOnce)
        {
            _limitLoadedOnce = true;
            LoadInitialLimitPage();
        }

        _onLimitHistoryViewed?.Invoke();
    }

    private void LoadInitialLimitPage()
    {
        if (_limitLog is null)
            return;

        // Newest month plus the one before it, so a fresh month's first days still show a
        // meaningful page; "Load older" walks further back one month at a time.
        var now = DateTimeOffset.UtcNow;
        var from = new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, 1).AddMonths(-1);
        LoadLimitWindows(from, now);
    }

    private void LoadOlderLimitPage()
    {
        if (_limitLog is null || _limitOldestLoaded is not { } oldest)
            return;

        var from = oldest.AddMonths(-1);
        LoadLimitWindows(from, new DateTimeOffset(oldest, TimeSpan.Zero) - TimeSpan.FromTicks(1));
    }

    // Streams one page of window records into the loaded set. Records are deduped on
    // (kind, model, end) per the log's at-least-once delivery, kept chronological, and both
    // views rebuilt from the same rows so the chart and table can never disagree. A failed
    // read leaves the paging state where it was — the initial page retries on the next tab
    // selection, and "Load older" retries the same month instead of silently skipping it.
    private void LoadLimitWindows(DateTime fromMonth, DateTimeOffset until)
    {
        try
        {
            var loaded = _limitLog!
                .ReadWindows(new DateTimeOffset(fromMonth, TimeSpan.Zero), until)
                .ToList();
            var merged = LimitWindowCapacity.Dedupe(
                loaded.Concat(_limitRows.Select(r => r.Record)));

            _limitRows = merged
                .OrderBy(r => r.End)
                .Select(LimitWindowCapacity.RowFor)
                .ToList();
            _limitOldestLoaded = fromMonth;
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Limit history load failed: {ex.Message}");
            if (_limitOldestLoaded is null)
                _limitLoadedOnce = false;
        }

        var oldestOnDisk = _limitLog!.OldestWindowMonth();
        _limitLoadOlder.Enabled = _limitOldestLoaded is { } loadedFrom
            && oldestOnDisk is { } disk && disk < loadedFrom;

        RefreshLimitViews();
    }

    private string? SelectedLimitKind =>
        _limitKindCombo.SelectedIndex is var i && i >= 0 && i < LimitKindOptions.Length
            ? LimitKindOptions[i].Kind
            : null;

    // The kind filter narrows both views identically.
    private List<LimitHistoryRow> FilteredLimitRows()
    {
        var kind = SelectedLimitKind;
        return kind is null
            ? _limitRows
            : _limitRows
                .Where(r => string.Equals(r.Record.Kind?.Trim(), kind, StringComparison.OrdinalIgnoreCase))
                .ToList();
    }

    private void RefreshLimitViews()
    {
        FillLimitList();
        UpdateLimitChart();
    }

    private void FillLimitList()
    {
        _limitList.BeginUpdate();
        _limitList.Items.Clear();

        var rows = FilteredLimitRows();
        if (rows.Count == 0)
        {
            _limitList.Items.Add(new ListViewItem("(no recorded windows yet)") { ForeColor = _theme.HintText });
        }
        else
        {
            foreach (var row in LimitWindowSort.Order(rows, _limitSort))
                _limitList.Items.Add(MakeLimitItem(row));
        }

        _limitList.EndUpdate();
        ListViewSortIndicator.Apply(_limitList, (int)_limitSort.Column, _limitSort.Ascending);
    }

    private ListViewItem MakeLimitItem(LimitHistoryRow row)
    {
        var record = row.Record;
        // The same helpers the sorter uses, so a cell can never disagree with its own ordering.
        var total = LimitWindowCapacity.RawTotal(record);
        var topModel = LimitWindowCapacity.TopModel(record) ?? "—";

        var item = new ListViewItem(LimitHistoryText.TimeText(record.Start));
        item.SubItems.Add(LimitHistoryText.TimeText(record.End));
        item.SubItems.Add(LimitHistoryText.KindLabel(record.Kind, record.ScopeModel));
        item.SubItems.Add(record.PeakPercent.ToString("0", CultureInfo.InvariantCulture) + "%");
        item.SubItems.Add(total > 0 ? LocalCostText.FormatTokens(total) : "—");
        item.SubItems.Add(topModel);
        item.SubItems.Add(LimitHistoryText.CapacityText(row));
        item.SubItems.Add(LimitHistoryText.PlanText(record));

        if (record.Incomplete)
        {
            // Best-effort windows read dimmed, with the reason a hover away.
            item.ForeColor = _theme.HintText;
            item.ToolTipText = $"Incomplete ({record.IncompleteReason ?? "partial observation"}) — " +
                "the app wasn't watching for part of this window.";
        }

        return item;
    }

    private void UpdateLimitChart()
    {
        var rows = FilteredLimitRows();
        var records = rows.Select(r => r.Record).ToList();

        // Series index per kind label, in order of first appearance — the legend's order.
        var seriesLabels = new List<string>();
        var seriesByLabel = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var slots = new List<LimitHistorySlot>(rows.Count);
        foreach (var row in rows)
        {
            var label = LimitHistoryText.KindLabel(row.Record.Kind, row.Record.ScopeModel);
            if (!seriesByLabel.TryGetValue(label, out var series))
            {
                series = seriesLabels.Count;
                seriesByLabel[label] = series;
                seriesLabels.Add(label);
            }

            slots.Add(new LimitHistorySlot(
                row.Record.End, series, row.ImpliedCapacity,
                row.Quality == WindowCapacityQuality.Low,
                row.WeightedTokens, Math.Max(row.Record.PeakPercent, row.Record.LastPercent)));
        }

        var markers = LimitWindowCapacity.PlanTransitions(records)
            .Select(t => (t.Index, t.Plan switch
            {
                ClaudePlan.Pro => "Pro",
                ClaudePlan.Max5x => "Max 5x",
                ClaudePlan.Max20x => "Max 20x",
                _ => "plan?",
            }))
            .ToList();

        _limitChart.SetData(slots, seriesLabels, markers);
    }

    // A table's heading. Sized in LayoutSectionRow rather than by AutoSize: a drilled heading
    // carries a project path, and the row it shares with the "Show all" button is one line high —
    // so it is given the width that is actually free and ellipsized into it, instead of growing
    // under the button (or wrapping, which would change the row's height).
    private Label MakeSectionLabel(string text) => new()
    {
        Text = text,
        AutoSize = false,
        AutoEllipsis = true,
        ForeColor = _theme.HeaderAccent,
    };

    private Button MakeButton(string text)
    {
        var button = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ButtonBack,
            ForeColor = _theme.ButtonText,
        };
        button.FlatAppearance.BorderColor = _theme.ButtonBorder;
        return button;
    }

    // The way out of a drill-down: shown in the heading of whichever table is currently filtered.
    private Button MakeShowAllButton()
    {
        var button = MakeButton("Show all");
        button.Visible = false;
        button.Click += (_, _) => ClearDrill();
        return button;
    }

    private ListView MakeTable(string firstColumn)
    {
        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            // The selection is the drill-down, so it has to keep reading as selected while the
            // focus is on the other table, the "Show all" button, or the timeframe box.
            HideSelection = false,
            // Clickable both to receive ColumnClick and to give the headers the hot-tracking that
            // tells people they can be clicked (#111).
            HeaderStyle = ColumnHeaderStyle.Clickable,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = _theme.FieldBack,
            ForeColor = _theme.FieldText,
        };
        // Columns get their scaled widths in Relayout; -2 here is a placeholder.
        list.Columns.Add(firstColumn);
        foreach (var column in NumericColumns)
            list.Columns.Add(column, -2, HorizontalAlignment.Right);
        return list;
    }

    private BreakdownTimeframe SelectedTimeframe =>
        _timeframeCombo.SelectedIndex is var i && i >= 0 && i < TimeframeOptions.Length
            ? TimeframeOptions[i].Value
            : BreakdownTimeframe.Today;

    // Pulls fresh data for the selected timeframe and rebuilds both tables and the chart.
    private void Reload()
    {
        _current = _localUsage.Breakdown(SelectedTimeframe);
        // Both queries re-aggregate the same cached cells, so the chart is refreshed even when it
        // is behind the Tables tab: it costs a walk over at most 30 days, and it means the two
        // tabs are always snapshots of the same moment rather than of whenever each was last
        // looked at. (Like the tables, that moment is the last open or timeframe change.)
        _chart.Series = _localUsage.CostSeries(SelectedTimeframe);

        // A drill-down survives a timeframe change — switching Today → 30 days asks the same
        // question over a wider window — but is dropped when the selected model or project has no
        // usage left in range, since there would be nothing on screen to point at.
        _drill = _drill is null ? null : BreakdownDrill.For(_current, _drill.Axis, _drill.Key);
        ApplyDrill();

        // No SizeColumns here on purpose: the widths are computed from the list's scrollbar-free
        // width, so a timeframe with more rows (and therefore a scrollbar) doesn't change them.
        _exportButton.Enabled = _current is not null && _current.Totals.TotalTokens > 0;
    }

    // Each table shows every row of its own axis, unless the OTHER table's selection has narrowed
    // it to that selection's counterparts. The totals row follows the rows either way, so a
    // drilled table still sums to what it says it is showing — the selected row's own totals.
    private void FillModels()
    {
        var drill = DrillInto(BreakdownAxis.Model);
        Fill(_modelList, drill?.Rows ?? _current?.ByModel, drill?.Totals ?? _current?.Totals, _modelSort);
    }

    private void FillProjects()
    {
        var drill = DrillInto(BreakdownAxis.Project);
        Fill(_projectList, drill?.Rows ?? _current?.ByProject, drill?.Totals ?? _current?.Totals, _projectSort);
    }

    /// <summary>The drill-down currently filtering the <paramref name="axis"/> table, if any.</summary>
    private LocalUsageDrillDown? DrillInto(BreakdownAxis axis) => BreakdownDrill.Filtering(_drill, axis);

    private void Fill(ListView list, IReadOnlyList<BreakdownRow>? rows, BreakdownRow? totals, BreakdownSortState sort)
    {
        // Rebuilding the items clears the selection, and the selection IS the drill-down — so the
        // handler is muted throughout and the drilled row is put back at the end. Without this a
        // header click would silently undo the drill it was meant to re-sort.
        var wasSuppressed = _suppressSelection;
        _suppressSelection = true;
        try
        {
            list.BeginUpdate();
            list.Items.Clear();

            if (rows is null || rows.Count == 0)
            {
                var empty = new ListViewItem("(no local usage data)") { ForeColor = _theme.HintText };
                list.Items.Add(empty);
            }
            else
            {
                // ReferenceEquals rather than the row's position or value: BreakdownRow is a record,
                // and a project whose totals happen to match the grand total would compare equal.
                var ordered = BreakdownSort.Order(rows, totals, sort);
                foreach (var row in ordered)
                    list.Items.Add(MakeItem(row, accent: ReferenceEquals(row, totals)));
            }

            list.EndUpdate();
            RestoreDrilledRow(list);
        }
        finally
        {
            _suppressSelection = wasSuppressed;
        }

        ListViewSortIndicator.Apply(list, (int)sort.Column, sort.Ascending);
    }

    private ListViewItem MakeItem(BreakdownRow row, bool accent)
    {
        var item = new ListViewItem(row.DisplayName);
        // Only body rows carry their row, and only rows with one can be drilled into. The totals
        // row deliberately doesn't: selecting it means "everything", which is the undrilled view.
        item.Tag = accent ? null : row;
        item.SubItems.Add(LocalCostText.FormatTokens(row.InputTokens));
        item.SubItems.Add(LocalCostText.FormatTokens(row.OutputTokens));
        item.SubItems.Add(LocalCostText.FormatTokens(row.CacheWriteTokens));
        item.SubItems.Add(LocalCostText.FormatTokens(row.CacheReadTokens));
        item.SubItems.Add(LocalCostText.FormatTokens(row.TotalTokens));
        item.SubItems.Add(CostText(row));
        if (accent)
            item.ForeColor = _theme.HeaderAccent;
        return item;
    }

    // Mirrors the flyout's cost conventions: "—" when nothing priced, "≥$x"
    // when the figure is a floor because an unpriced model contributed.
    private static string CostText(BreakdownRow row) =>
        row.HasUnpricedModels
            ? row.CostUsd < 0.005 ? "—" : "≥" + LocalCostText.FormatCost(row.CostUsd).TrimStart('~')
            : LocalCostText.FormatCost(row.CostUsd);

    /// <summary>The table a drill-down was started from — the one holding the selection.</summary>
    private ListView SourceList(BreakdownAxis axis) =>
        axis == BreakdownAxis.Model ? _modelList : _projectList;

    private static bool SameKey(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // Re-selects the drilled-into row after its own table was rebuilt (a re-sort, a timeframe
    // change). A no-op for the other table, whose rows are the drill-down's results.
    private void RestoreDrilledRow(ListView list)
    {
        if (_drill is null || SourceList(_drill.Axis) != list)
            return;

        foreach (ListViewItem item in list.Items)
        {
            if (item.Tag is BreakdownRow row && SameKey(row.Key, _drill.Key))
            {
                item.Selected = true;
                // Focused too, or the arrow keys would carry on from the top of the rebuilt list
                // instead of from the row that is actually selected.
                item.Focused = true;
                item.EnsureVisible();
                return;
            }
        }
    }

    private void OnRowSelected(ListView list, BreakdownAxis axis)
    {
        if (_suppressSelection || !IsHandleCreated)
            return;

        // Moving the selection raises this twice — once for the row losing it (when the selection
        // is momentarily empty) and once for the row gaining it. Responding to both would rebuild
        // the other table twice, flashing the undrilled view in between, so the response is posted
        // once and reads whatever the selection has settled on by the time it runs.
        _lastSelectionSource = (list, axis);
        if (_selectionPending)
            return;

        _selectionPending = true;
        BeginInvoke(ApplyPendingSelection);
    }

    private void ApplyPendingSelection()
    {
        _selectionPending = false;
        if (IsDisposed || _lastSelectionSource is not { } source)
            return;

        var (list, axis) = source;

        // Exactly one table holds a selection: the one the user just acted on. Anything left over
        // in the other one would read as a drill-down that is no longer on — and, since clicking an
        // already-selected row raises no event, could not even be clicked to bring it back.
        ClearSelection(list == _modelList ? _projectList : _modelList);

        // No row (an empty selection, the totals row, or the empty-state placeholder) means
        // "everything" — the undrilled view — rather than an empty drill-down panel.
        var row = list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as BreakdownRow : null;
        SetDrill(row is null ? null : BreakdownDrill.For(_current, axis, row.Key));
    }

    // "Show all": back to both full tables, selection and all.
    private void ClearDrill()
    {
        if (_drill is not null)
        {
            var source = SourceList(_drill.Axis);
            ClearSelection(source);

            // The button is about to hide itself, and WinForms would hand the focus to whatever
            // comes next in the tab order (the Export button) — put it back on the table the
            // selection came from instead.
            source.Focus();
        }

        SetDrill(null);
    }

    // Drops a table's selection without letting it look like the user did it.
    private void ClearSelection(ListView list)
    {
        if (list.SelectedIndices.Count == 0)
            return;

        var wasSuppressed = _suppressSelection;
        _suppressSelection = true;
        try
        {
            list.SelectedIndices.Clear();
        }
        finally
        {
            _suppressSelection = wasSuppressed;
        }
    }

    private void SetDrill(LocalUsageDrillDown? drill)
    {
        // Nothing to redraw when the same row (or nothing at all) is picked again — and skipping
        // it keeps a click on the already-selected row from throwing away its own scroll position.
        if (BreakdownDrill.Same(_drill, drill))
            return;

        var previous = _drill;
        _drill = drill;

        // Only the table whose rows actually changed is rebuilt. The one holding the selection
        // keeps its items — and with them the row the user just clicked, including the totals row,
        // whose "everything" selection would otherwise vanish the moment it was made.
        var (model, project) = BreakdownDrill.Rebuild(previous, drill);
        if (model)
            FillModels();
        if (project)
            FillProjects();

        UpdateSectionHeadings();
    }

    // Rebuilds both tables and the headings for the current drill state.
    private void ApplyDrill()
    {
        FillModels();
        FillProjects();
        UpdateSectionHeadings();
    }

    private void UpdateSectionHeadings()
    {
        // At most one table is filtered at a time, so the drilled-into name belongs to whichever
        // heading is not the plain one.
        var name = DrilledDisplayName();
        var model = DrillInto(BreakdownAxis.Model) is not null;
        var project = DrillInto(BreakdownAxis.Project) is not null;

        _modelLabel.Text = BreakdownDrillText.ModelSection(model ? name : null);
        _projectLabel.Text = BreakdownDrillText.ProjectSection(project ? name : null);
        // The chart doesn't narrow with the drill-down, so its heading owns up to that.
        _chartLabel.Text = BreakdownDrillText.ChartSection(name);

        // Starting or ending a drill-down changes which "Show all" buttons belong on screen, and
        // that is the same tab-and-drill rule the tab switch applies.
        ApplyTab();
    }

    // The drilled-into row's own display name for the heading — a project's real path rather than
    // its directory key. The CSV export names the scope with the same helper.
    private string? DrilledDisplayName() =>
        _drill is null ? null : BreakdownDrill.DisplayName(_current, _drill);

    private void ExportCsv()
    {
        if (_current is null)
            return;

        var stamp = _current.ToDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var range = SelectedTimeframe switch
        {
            BreakdownTimeframe.Today => "today",
            BreakdownTimeframe.SevenDays => "7d",
            _ => "30d",
        };

        using var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            // The drill scope rides in the name as well as in the file, so a drilled export doesn't
            // sit in a folder looking exactly like the full one saved a minute earlier (#168).
            FileName = $"claudemon-usage-{range}-{stamp}{BreakdownCsv.FileNameScope(_drill)}.csv",
            DefaultExt = "csv",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            // Each table's own sort and the drill-down go with it, so the file is the two tables as
            // they are on screen — same rows, same order (#119, #168). UTF-8 with BOM so Excel
            // detects the encoding.
            File.WriteAllText(
                dialog.FileName,
                BreakdownCsv.Compose(_current, _drill, _modelSort, _projectSort),
                new UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger?.Warn($"CSV export failed: {ex.Message}");
            MessageBox.Show(this, $"Could not write the CSV file:\n{ex.Message}",
                "Export failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private int Sc(int value) => DpiScale.Scale(value, DeviceDpi / 96f);

    // Everything above the first table is fixed-height and width-independent, so it is laid out
    // the same way whether we're measuring a candidate size or laying out the real one. The
    // hint's wrap width is set here too, because its resulting height feeds ChromeHeight.
    private void LayoutHeader(int contentWidth)
    {
        _heading.Location = new Point(Sc(Pad), Sc(HeaderTop));

        _timeframeLabel.Location = new Point(Sc(Pad), _heading.Bottom + Sc(SectionGap) + Sc(4));
        _timeframeCombo.SetBounds(
            _timeframeLabel.Right + Sc(8), _heading.Bottom + Sc(SectionGap), Sc(ComboWidth), 0,
            BoundsSpecified.Location | BoundsSpecified.Width);
        _selectHint.Location = new Point(
            _timeframeCombo.Right + Sc(12),
            _timeframeCombo.Top + ((_timeframeCombo.Height - _selectHint.Height) / 2));

        _tabStrip.SetBounds(Sc(Pad), TabStripTop, contentWidth, Sc(TabStrip.LogicalHeight));

        LayoutSectionRow(_modelLabel, _modelShowAll, SectionTop, contentWidth);
        _hint.MaximumSize = new Size(contentWidth, 0);
    }

    /// <summary>Top of the tab header row. Requires <see cref="LayoutHeader"/> to have run.</summary>
    private int TabStripTop => _timeframeCombo.Bottom + Sc(SectionGap);

    /// <summary>
    /// The vertical space the tab strip adds above the content. Its own baseline hairline does the
    /// separating, so it only needs the small gap under it.
    /// </summary>
    private int TabStripRow => Sc(TabStrip.LogicalHeight) + Sc(LabelGap);

    /// <summary>
    /// Top of the tab content — the first section heading on the Tables tab, the chart on the
    /// Chart tab. Requires <see cref="LayoutHeader"/> to have run.
    /// </summary>
    private int SectionTop => TabStripTop + TabStripRow;

    /// <summary>
    /// The height of a section heading row. The "Show all" button is taller than the label, and
    /// the row keeps its height whether or not the button is showing — otherwise starting a
    /// drill-down would shift both tables down a few pixels.
    /// </summary>
    private int SectionRowHeight => Math.Max(_modelLabel.PreferredHeight, Sc(ShowAllHeight));

    // A section heading: the label on the left with everything the button doesn't need, its
    // "Show all" button right-aligned with the table below, both centred in a row of
    // SectionRowHeight.
    private void LayoutSectionRow(Label label, Button showAll, int top, int contentWidth)
    {
        var row = SectionRowHeight;
        var labelHeight = label.PreferredHeight;
        label.SetBounds(
            Sc(Pad), top + ((row - labelHeight) / 2),
            Math.Max(0, contentWidth - Sc(ShowAllWidth) - Sc(ButtonGap)), labelHeight);
        showAll.SetBounds(
            Sc(Pad) + contentWidth - Sc(ShowAllWidth), top + ((row - Sc(ShowAllHeight)) / 2),
            Sc(ShowAllWidth), Sc(ShowAllHeight));
    }

    /// <summary>
    /// The client height taken by everything that isn't a table, for a hint line of
    /// <paramref name="hintHeight"/>. Requires <see cref="LayoutHeader"/> to have run.
    /// </summary>
    private int ChromeHeight(int hintHeight) =>
        SectionTop + SectionRowHeight + Sc(LabelGap)               // down to the first table
        + Sc(SectionGap) + SectionRowHeight + Sc(LabelGap)         // "By project" between them
        + Sc(SectionGap) + hintHeight + Sc(SectionGap)             // the hint line
        + Sc(ButtonHeight) + Sc(Pad);                              // the button row

    /// <summary>
    /// The smallest client height the window still renders sensibly at: the chrome plus two
    /// floor-height tables, for a hint line of <paramref name="hintHeight"/>.
    /// </summary>
    private int MinContentHeight(int hintHeight) =>
        ChromeHeight(hintHeight) + (2 * Sc(MinTableHeight));

    /// <summary>
    /// The size the window opens at: the chrome plus two default-height tables, capped at what the
    /// monitor has room for. The tab strip's row is subtracted back off, so adding the Chart tab
    /// left the default window exactly the size it was before it (#113) — the strip comes out of
    /// the tables' share instead. There is plenty of slack for that: the default tables are more
    /// than twice their floor height.
    ///
    /// The cap is #153's clamp: two 150-logical tables plus the chrome is ~560 logical, which wants
    /// more than a 1080p working area at 200% scaling. Unlike the fixed dialogs this window is
    /// resizable and its layout is elastic, so the overflow is absorbed by the tables taking a
    /// smaller share — no scrollbar, hence the discarded flag. The floor is the same
    /// two-floor-height-tables minimum <see cref="MinClientSize"/> uses, so the clamp lands on the
    /// window's own minimum rather than somewhere below it. (Not exactly: the two measure the hint
    /// line at different widths, so the floor here can come out a line shorter than the one
    /// <c>OnLoad</c> sets. It no longer nudges the window taller, though — since #172 that floor is
    /// itself capped at the working area, which on any monitor small enough for this floor to bite
    /// is shorter than the window it produced.)
    /// </summary>
    private Size DefaultClientSize()
    {
        var width = Sc(DefaultClientWidth);
        LayoutHeader(width - (2 * Sc(Pad)));

        var (height, _) = DialogPlacement.ClampClientHeight(
            UsageBreakdownLayout.DefaultHeight(
                ChromeHeight(_hint.Height), Sc(DefaultTableHeight), TabStripRow),
            DialogPlacement.WorkingAreaFor(this).Height,
            Height - ClientSize.Height,
            MinContentHeight(_hint.Height));

        return new Size(width, height);
    }

    /// <summary>
    /// The smallest client area the window still renders sensibly in: wide enough for both table
    /// headers at their natural column widths (and for the button row), tall enough for two
    /// floor-height tables plus the chrome.
    /// </summary>
    private Size MinClientSize()
    {
        var tableWidth = UsageBreakdownLayout.MinTableWidth(
            SystemInformation.VerticalScrollBarWidth,
            Sc(NumericColumn), Sc(CostColumn), Sc(MinFirstColumn)) + TableBorder;
        var buttonRow = Sc(ButtonWidth) + Sc(ButtonGap) + Sc(CloseButtonWidth);
        // The timeframe row is normally the shortest of the three, but its labels are system-font
        // width rather than scaled metrics, so it is measured rather than assumed to fit.
        var timeframeRow =
            _timeframeLabel.Width + Sc(8) + Sc(ComboWidth) + Sc(12) + _selectHint.Width;
        var width = (2 * Sc(Pad)) + Math.Max(Math.Max(tableWidth, buttonRow), timeframeRow);

        // Measure the hint at the minimum width rather than reusing its current height: it wraps
        // to more lines when narrow, and the chrome has to leave room for that. Asking the label
        // itself (rather than TextRenderer) guarantees the same answer Relayout will read back
        // out of _hint.Height — a few pixels of disagreement at the wrap point costs a whole line.
        var hint = _hint.GetPreferredSize(new Size(width - (2 * Sc(Pad)), 0)).Height;

        return new Size(width, MinContentHeight(hint));
    }

    /// <summary>
    /// Recomputes the window's resize floor. <c>MinimumSize</c> is the outer window size, so the
    /// frame has to be added back on, and the result is capped at the monitor's working area
    /// (#172): on a small panel at a large scale factor the content-derived floor can be bigger
    /// than the screen, and then the user cannot shrink the window to fit — the one case #153's cap
    /// on the *opening* size cannot save, since it only bounds where the window starts. Shrunk that
    /// far the window is below its own content minimum, so the tables stop at their floor height
    /// (see <see cref="UsageBreakdownLayout.SplitTableHeights"/>) and are clipped by the hint and
    /// the button row, which the constructor puts in front of them for exactly this case: cramped,
    /// but on a screen that small a cramped window beats one that can't be shrunk onto it at all.
    ///
    /// Deliberately not called from <see cref="OnResize"/> on every pass — the value is
    /// resize-invariant, and assigning it can itself resize the window, which would bounce straight
    /// back in here. That also settles what happens when the window is dragged to a smaller
    /// monitor: the cap is re-evaluated on the triggers that already exist (open, DPI change,
    /// restore from minimized), and a same-DPI move to a smaller screen keeps the old floor until
    /// one of those fires or the window is reopened. That is the app's existing rule for monitor
    /// moves — placement never re-centers a dialog the user dragged either — and the alternative,
    /// chasing <c>Screen.FromControl</c> on every move, would resize a window out from under the
    /// hand that is dragging it.
    /// </summary>
    /// <param name="area">
    /// The working area to cap the floor at, or null to measure the monitor the window is
    /// currently on. <c>OnLoad</c> passes the area the window is about to be centered on (#116):
    /// until that move happens the window is still parked on the primary, so measuring it there
    /// would size the floor for the wrong monitor — and a floor bigger than the screen the window
    /// opens on is exactly what #172 exists to prevent.
    /// </param>
    private void UpdateMinimumSize(Rectangle? area = null)
    {
        // Minimizing reports a degenerate frame, so there is nothing sensible to compute from;
        // OnResize restores the floor when the window comes back.
        if (_updatingMinimum || WindowState == FormWindowState.Minimized)
            return;

        _updatingMinimum = true;
        try
        {
            var min = MinClientSize();
            MinimumSize = DialogPlacement.ClampMinimumSize(
                new Size(
                    min.Width + (Width - ClientSize.Width),
                    min.Height + (Height - ClientSize.Height)),
                (area ?? DialogPlacement.WorkingAreaFor(this)).Size);
        }
        finally
        {
            _updatingMinimum = false;
        }
    }

    /// <summary>
    /// Centers on <paramref name="area"/>, ignoring re-entrant calls — the same wrapper
    /// <see cref="UpdateAvailableDialog"/> uses (#108). Moving the window can make Windows deliver
    /// <c>WM_DPICHANGED</c> synchronously, which lands back in <see cref="OnDpiChanged"/> and would
    /// call straight back in here; <c>PlaceStable</c> already re-measures after its own move, so
    /// the nested call has nothing to add and would only risk a loop.
    /// </summary>
    private void PlaceOn(Rectangle area)
    {
        if (_placing)
            return;

        _placing = true;
        try
        {
            DialogPlacement.CenterOn(this, area);
        }
        finally
        {
            _placing = false;
        }
    }

    private void Relayout()
    {
        var contentWidth = Math.Max(Sc(MinFirstColumn), ClientSize.Width - (2 * Sc(Pad)));
        LayoutHeader(contentWidth);

        // The hint and buttons hang off the bottom edge; the tables take what's left in between.
        var buttonsTop = ClientSize.Height - Sc(Pad) - Sc(ButtonHeight);
        var hintTop = buttonsTop - Sc(SectionGap) - _hint.Height;

        var tablesTop = SectionTop + SectionRowHeight + Sc(LabelGap);

        // Both tabs are laid out every pass, visible or not: one layout path, and the chart is
        // already the right size when its tab comes forward rather than a frame later. The chart's
        // heading sits in the same row as the first table's and the chart fills everything below,
        // so nothing above or below moves when the tab changes.
        var contentBottom = hintTop - Sc(SectionGap);
        _chartLabel.SetBounds(
            Sc(Pad), SectionTop + ((SectionRowHeight - _chartLabel.PreferredHeight) / 2),
            contentWidth, _chartLabel.PreferredHeight);
        _chart.SetBounds(Sc(Pad), tablesTop, contentWidth, Math.Max(0, contentBottom - tablesTop));

        // The Limit history tab shares the same region (#186): its filter row sits in the
        // section row — label left, the two combos and "Load older" right — with the chart on
        // top and the window table below, split like the two breakdown tables.
        var comboW = Sc(ComboWidth);
        var loadOlderW = Sc(ShowAllWidth);
        var right = Sc(Pad) + contentWidth;
        _limitLoadOlder.SetBounds(
            right - loadOlderW, SectionTop + ((SectionRowHeight - Sc(ShowAllHeight)) / 2),
            loadOlderW, Sc(ShowAllHeight));
        _limitKindCombo.SetBounds(
            right - loadOlderW - Sc(ButtonGap) - comboW, SectionTop, comboW, 0,
            BoundsSpecified.Location | BoundsSpecified.Width);
        _limitViewCombo.SetBounds(
            right - loadOlderW - (2 * Sc(ButtonGap)) - (2 * comboW), SectionTop, comboW, 0,
            BoundsSpecified.Location | BoundsSpecified.Width);
        _limitLabel.SetBounds(
            Sc(Pad), SectionTop + ((SectionRowHeight - _limitLabel.PreferredHeight) / 2),
            Math.Max(0, contentWidth - loadOlderW - (2 * comboW) - (3 * Sc(ButtonGap))),
            _limitLabel.PreferredHeight);

        var limitAvailable = Math.Max(2 * Sc(MinTableHeight), contentBottom - tablesTop - Sc(SectionGap));
        var limitChartHeight = Math.Max(Sc(MinTableHeight), limitAvailable * 45 / 100);
        var limitListHeight = Math.Max(Sc(MinTableHeight), limitAvailable - limitChartHeight);
        _limitChart.SetBounds(Sc(Pad), tablesTop, contentWidth, limitChartHeight);
        _limitList.SetBounds(
            Sc(Pad), tablesTop + limitChartHeight + Sc(SectionGap), contentWidth, limitListHeight);
        for (var i = 0; i < LimitColumns.Length; i++)
        {
            var width = Sc(LimitColumns[i].Width);
            if (_limitList.Columns[i].Width != width)
                _limitList.Columns[i].Width = width;
        }

        var betweenTables = Sc(SectionGap) + SectionRowHeight + Sc(LabelGap);
        var (modelHeight, projectHeight) = UsageBreakdownLayout.SplitTableHeights(
            hintTop - Sc(SectionGap) - tablesTop - betweenTables, Sc(MinTableHeight));

        _modelList.SetBounds(Sc(Pad), tablesTop, contentWidth, modelHeight);
        SizeColumns(_modelList);

        var projectSectionTop = _modelList.Bottom + Sc(SectionGap);
        LayoutSectionRow(_projectLabel, _projectShowAll, projectSectionTop, contentWidth);
        _projectList.SetBounds(
            Sc(Pad), projectSectionTop + SectionRowHeight + Sc(LabelGap), contentWidth, projectHeight);
        SizeColumns(_projectList);

        _hint.Location = new Point(Sc(Pad), hintTop);
        _exportButton.SetBounds(Sc(Pad), buttonsTop, Sc(ButtonWidth), Sc(ButtonHeight));
        _closeButton.SetBounds(
            ClientSize.Width - Sc(Pad) - Sc(CloseButtonWidth), buttonsTop,
            Sc(CloseButtonWidth), Sc(ButtonHeight));
    }

    // First column takes what the six fixed-width numeric columns leave over; see
    // UsageBreakdownLayout.ColumnWidths for the scrollbar reservation.
    private void SizeColumns(ListView list)
    {
        // Width minus the border, not ClientSize.Width: a visible vertical scrollbar is already
        // outside the client area, so measuring from there would reserve its width a second time
        // and leave a dead strip. This way the widths depend only on the list's own size, which
        // makes the relayout idempotent — it runs on every mouse move during a drag-resize.
        var widths = UsageBreakdownLayout.ColumnWidths(
            list.Width - TableBorder, SystemInformation.VerticalScrollBarWidth,
            Sc(NumericColumn), Sc(CostColumn), Sc(MinFirstColumn));

        for (var i = 0; i < widths.Length; i++)
        {
            // Dragging an edge re-lays out on every mouse move; skip the columns that didn't
            // change, since assigning a width repaints the whole table.
            if (list.Columns[i].Width != widths[i])
                list.Columns[i].Width = widths[i];
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Relayout first: DeviceDpi is only reliable once the handle exists (see
        // UpdateAvailableDialog.OnLoad), and MinClientSize measures off the laid-out header.
        Relayout();

        // Open where the user is working rather than always on the primary monitor (#116): the
        // same ForegroundMonitor path the update dialogs took in #108, with the same fall back to
        // the primary when there is no usable foreground window. Resolved once, before the floor
        // is computed, so both the floor and the move describe the same monitor.
        var area = DialogPlacement.ForegroundWorkingArea();
        _placementArea = area;
        UpdateMinimumSize(area);

        // The tables were filled in the constructor, before there was a header control to put the
        // sort arrow on.
        ListViewSortIndicator.Apply(_modelList, (int)_modelSort.Column, _modelSort.Ascending);
        ListViewSortIndicator.Apply(_projectList, (int)_projectSort.Column, _projectSort.Ascending);

        // Last, so PlaceStable centers the final size — assigning MinimumSize above can itself
        // grow the window. Moving onto a differently-scaled monitor resizes it again, which is
        // exactly what PlaceStable re-measures for.
        PlaceOn(area);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (!IsHandleCreated)
            return;

        // Minimizing reports a degenerate client size; laying out against it would stack every
        // control in the corner and leave it there when the window is restored. (MinimizeBox is
        // off, but "Show desktop" and a taskbar-thumbnail minimise still get here.)
        if (WindowState == FormWindowState.Minimized)
        {
            _wasMinimized = true;
            return;
        }

        Relayout();

        // A DPI change that arrived while minimized cleared the floor in WndProc and
        // UpdateMinimumSize refused to recompute it; put it back now there's a real frame again.
        if (_wasMinimized)
        {
            _wasMinimized = false;
            UpdateMinimumSize();
        }
    }

    protected override void WndProc(ref Message m)
    {
        // MinimumSize is physical pixels, so the one computed for the old monitor is a stale
        // floor that would clamp the shrink Windows performs while handling WM_DPICHANGED (which
        // happens inside base.WndProc). Drop it here; OnDpiChanged puts the rescaled one back.
        // The WM_SIZE that base.WndProc raises on the way through still sees the old DeviceDpi,
        // so its Relayout is off by one scale factor for a single frame — OnDpiChanged, which
        // runs immediately after, redoes it at the new one.
        if (m.Msg == WM_DPICHANGED)
            MinimumSize = Size.Empty;

        base.WndProc(ref m);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Relayout();
        UpdateMinimumSize();

        // Still part of the initial placement: if Windows deferred the DPI transition from the
        // OnLoad move until the window was shown, the relayout above has just resized the window
        // around a position computed for the old size — re-center rather than leave it off-center
        // or straddling the monitor edge (#108's placement, #104's failure mode). Once shown,
        // never re-center: that would yank a window the user is dragging.
        if (!_shown && _placementArea is { } area)
            PlaceOn(area);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _shown = true;
        Activate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SystemTheme.ApplyTitleBar(Handle, _theme.IsDark);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _baseFont.Dispose();
            _headingFont.Dispose();
        }
    }
}
