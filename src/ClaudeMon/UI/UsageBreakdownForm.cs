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
/// with hand-scaled metrics, <see cref="Theme"/> accents, primary-monitor
/// placement, re-layout on load and DPI change. Data is pulled through
/// <see cref="LocalUsageMonitor"/>'s thread-safe queries on open and whenever
/// the timeframe changes; the window shows a static picture (no live refresh —
/// reopen for fresh numbers, matching how the flyout snapshots on open).
///
/// Either table can be re-sorted by clicking a column header (#111); the ordering itself lives in
/// the pure <see cref="BreakdownSort"/>, which sorts the <see cref="BreakdownRow"/> numbers rather
/// than the formatted cell text and keeps the totals row pinned to the bottom.
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

    private readonly Font _baseFont = new("Segoe UI", 9.75f);
    private readonly Font _headingFont = new("Segoe UI Semibold", 11.25f);

    private readonly Label _heading;
    private readonly Label _timeframeLabel;
    private readonly ComboBox _timeframeCombo;
    private readonly Label _modelLabel;
    private readonly ListView _modelList;
    private readonly Label _projectLabel;
    private readonly ListView _projectList;
    private readonly Label _hint;
    private readonly Button _exportButton;
    private readonly Button _closeButton;

    private LocalUsageBreakdown? _current;
    private bool _updatingMinimum;
    private bool _wasMinimized;

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

    public UsageBreakdownForm(LocalUsageMonitor localUsage, Logger? logger = null)
    {
        _localUsage = localUsage;
        _logger = logger;

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

        _modelLabel = new Label { Text = "By model", AutoSize = true, ForeColor = _theme.HeaderAccent };
        Controls.Add(_modelLabel);
        _modelList = MakeTable("Model");
        _modelList.ColumnClick += (_, e) =>
        {
            _modelSort = _modelSort.Toggle(e.Column);
            FillModels();
        };
        Controls.Add(_modelList);

        _projectLabel = new Label { Text = "By project", AutoSize = true, ForeColor = _theme.HeaderAccent };
        Controls.Add(_projectLabel);
        _projectList = MakeTable("Project");
        _projectList.ColumnClick += (_, e) =>
        {
            _projectSort = _projectSort.Toggle(e.Column);
            FillProjects();
        };
        Controls.Add(_projectList);

        _hint = new Label
        {
            Text = "Estimates at API list prices, computed locally from Claude Code transcripts — not billing.",
            AutoSize = true,
            ForeColor = _theme.HintText,
        };
        Controls.Add(_hint);

        _exportButton = new Button
        {
            Text = "Export CSV...",
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ButtonBack,
            ForeColor = _theme.ButtonText,
        };
        _exportButton.FlatAppearance.BorderColor = _theme.ButtonBorder;
        _exportButton.Click += (_, _) => ExportCsv();
        Controls.Add(_exportButton);

        _closeButton = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.ButtonBack,
            ForeColor = _theme.ButtonText,
        };
        _closeButton.FlatAppearance.BorderColor = _theme.ButtonBorder;
        Controls.Add(_closeButton);

        AcceptButton = _closeButton;
        CancelButton = _closeButton;

        Reload();
        ClientSize = DefaultClientSize();
        Relayout();
    }

    private ListView MakeTable(string firstColumn)
    {
        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
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

    // Pulls fresh data for the selected timeframe and rebuilds both tables.
    private void Reload()
    {
        _current = _localUsage.Breakdown(SelectedTimeframe);

        FillModels();
        FillProjects();

        // No SizeColumns here on purpose: the widths are computed from the list's scrollbar-free
        // width, so a timeframe with more rows (and therefore a scrollbar) doesn't change them.
        _exportButton.Enabled = _current is not null && _current.Totals.TotalTokens > 0;
    }

    private void FillModels() => Fill(_modelList, _current?.ByModel, _current?.Totals, _modelSort);

    private void FillProjects() => Fill(_projectList, _current?.ByProject, _current?.Totals, _projectSort);

    private void Fill(ListView list, IReadOnlyList<BreakdownRow>? rows, BreakdownRow? totals, BreakdownSortState sort)
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
        ListViewSortIndicator.Apply(list, (int)sort.Column, sort.Ascending);
    }

    private ListViewItem MakeItem(BreakdownRow row, bool accent)
    {
        var item = new ListViewItem(row.DisplayName);
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
            FileName = $"claudemon-usage-{range}-{stamp}.csv",
            DefaultExt = "csv",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        try
        {
            // UTF-8 with BOM so Excel detects the encoding.
            File.WriteAllText(dialog.FileName, BreakdownCsv.Compose(_current), new UTF8Encoding(true));
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

        _modelLabel.Location = new Point(Sc(Pad), _timeframeCombo.Bottom + Sc(SectionGap));
        _hint.MaximumSize = new Size(contentWidth, 0);
    }

    /// <summary>
    /// The client height taken by everything that isn't a table, for a hint line of
    /// <paramref name="hintHeight"/>. Requires <see cref="LayoutHeader"/> to have run.
    /// </summary>
    private int ChromeHeight(int hintHeight) =>
        _modelLabel.Bottom + Sc(LabelGap)                          // down to the first table
        + Sc(SectionGap) + _projectLabel.Height + Sc(LabelGap)     // "By project" between them
        + Sc(SectionGap) + hintHeight + Sc(SectionGap)             // the hint line
        + Sc(ButtonHeight) + Sc(Pad);                              // the button row

    /// <summary>The size the window opens at: the chrome plus two default-height tables.</summary>
    private Size DefaultClientSize()
    {
        var width = Sc(DefaultClientWidth);
        LayoutHeader(width - (2 * Sc(Pad)));
        return new Size(width, ChromeHeight(_hint.Height) + (2 * Sc(DefaultTableHeight)));
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
        var width = (2 * Sc(Pad)) + Math.Max(tableWidth, buttonRow);

        // Measure the hint at the minimum width rather than reusing its current height: it wraps
        // to more lines when narrow, and the chrome has to leave room for that. Asking the label
        // itself (rather than TextRenderer) guarantees the same answer Relayout will read back
        // out of _hint.Height — a few pixels of disagreement at the wrap point costs a whole line.
        var hint = _hint.GetPreferredSize(new Size(width - (2 * Sc(Pad)), 0)).Height;

        return new Size(width, ChromeHeight(hint) + (2 * Sc(MinTableHeight)));
    }

    // MinimumSize is the outer window size, so the frame has to be added back on. Deliberately
    // not called from OnResize on every pass — the value is resize-invariant, and assigning it
    // can itself resize the window, which would bounce straight back in here.
    private void UpdateMinimumSize()
    {
        // Minimizing reports a degenerate frame, so there is nothing sensible to compute from;
        // OnResize restores the floor when the window comes back.
        if (_updatingMinimum || WindowState == FormWindowState.Minimized)
            return;

        _updatingMinimum = true;
        try
        {
            var min = MinClientSize();
            MinimumSize = new Size(
                min.Width + (Width - ClientSize.Width),
                min.Height + (Height - ClientSize.Height));
        }
        finally
        {
            _updatingMinimum = false;
        }
    }

    private void Relayout()
    {
        var contentWidth = Math.Max(Sc(MinFirstColumn), ClientSize.Width - (2 * Sc(Pad)));
        LayoutHeader(contentWidth);

        // The hint and buttons hang off the bottom edge; the tables take what's left in between.
        var buttonsTop = ClientSize.Height - Sc(Pad) - Sc(ButtonHeight);
        var hintTop = buttonsTop - Sc(SectionGap) - _hint.Height;

        var tablesTop = _modelLabel.Bottom + Sc(LabelGap);
        var betweenTables = Sc(SectionGap) + _projectLabel.Height + Sc(LabelGap);
        var (modelHeight, projectHeight) = UsageBreakdownLayout.SplitTableHeights(
            hintTop - Sc(SectionGap) - tablesTop - betweenTables, Sc(MinTableHeight));

        _modelList.SetBounds(Sc(Pad), tablesTop, contentWidth, modelHeight);
        SizeColumns(_modelList);

        _projectLabel.Location = new Point(Sc(Pad), _modelList.Bottom + Sc(SectionGap));
        _projectList.SetBounds(Sc(Pad), _projectLabel.Bottom + Sc(LabelGap), contentWidth, projectHeight);
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
        UpdateMinimumSize();

        // The tables were filled in the constructor, before there was a header control to put the
        // sort arrow on.
        ListViewSortIndicator.Apply(_modelList, (int)_modelSort.Column, _modelSort.Ascending);
        ListViewSortIndicator.Apply(_projectList, (int)_projectSort.Column, _projectSort.Ascending);

        DialogPlacement.CenterOnPrimary(this);
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
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
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
