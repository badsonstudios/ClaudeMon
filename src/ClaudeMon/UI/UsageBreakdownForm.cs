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
/// </summary>
internal sealed class UsageBreakdownForm : Form
{
    // Layout metrics, logical (96-DPI) units.
    private const int Pad = 20;
    private const int ClientWidth = 700;
    private const int ContentRight = ClientWidth - Pad;
    private const int HeaderTop = 16;
    private const int SectionGap = 14;
    private const int LabelGap = 6;
    private const int TableHeight = 150;
    private const int ButtonHeight = 30;
    private const int ButtonWidth = 100;
    private const int CloseButtonWidth = 82;

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
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Font = _baseFont;

        _heading = new Label
        {
            Text = "Usage & costs",
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
        Controls.Add(_modelList);

        _projectLabel = new Label { Text = "By project", AutoSize = true, ForeColor = _theme.HeaderAccent };
        Controls.Add(_projectLabel);
        _projectList = MakeTable("Project");
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
        Relayout();
    }

    private ListView MakeTable(string firstColumn)
    {
        var list = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = _theme.FieldBack,
            ForeColor = _theme.FieldText,
        };
        // Columns get their scaled widths in Relayout; -2 here is a placeholder.
        list.Columns.Add(firstColumn);
        list.Columns.Add("Input", -2, HorizontalAlignment.Right);
        list.Columns.Add("Output", -2, HorizontalAlignment.Right);
        list.Columns.Add("Cache W", -2, HorizontalAlignment.Right);
        list.Columns.Add("Cache R", -2, HorizontalAlignment.Right);
        list.Columns.Add("Tokens", -2, HorizontalAlignment.Right);
        list.Columns.Add("Cost (est.)", -2, HorizontalAlignment.Right);
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

        Fill(_modelList, _current?.ByModel, _current?.Totals);
        Fill(_projectList, _current?.ByProject, _current?.Totals);

        _exportButton.Enabled = _current is not null && _current.Totals.TotalTokens > 0;
    }

    private void Fill(ListView list, IReadOnlyList<BreakdownRow>? rows, BreakdownRow? totals)
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
            foreach (var row in rows)
                list.Items.Add(MakeItem(row, accent: false));
            if (totals is not null)
                list.Items.Add(MakeItem(totals, accent: true));
        }

        list.EndUpdate();
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

    private void Relayout()
    {
        var contentWidth = ContentRight - Pad;

        _heading.Location = new Point(Sc(Pad), Sc(HeaderTop));

        _timeframeLabel.Location = new Point(Sc(Pad), _heading.Bottom + Sc(SectionGap) + Sc(4));
        _timeframeCombo.SetBounds(
            _timeframeLabel.Right + Sc(8), _heading.Bottom + Sc(SectionGap), Sc(140), 0,
            BoundsSpecified.Location | BoundsSpecified.Width);

        _modelLabel.Location = new Point(Sc(Pad), _timeframeCombo.Bottom + Sc(SectionGap));
        _modelList.SetBounds(Sc(Pad), _modelLabel.Bottom + Sc(LabelGap), Sc(contentWidth), Sc(TableHeight));
        SizeColumns(_modelList);

        _projectLabel.Location = new Point(Sc(Pad), _modelList.Bottom + Sc(SectionGap));
        _projectList.SetBounds(Sc(Pad), _projectLabel.Bottom + Sc(LabelGap), Sc(contentWidth), Sc(TableHeight));
        SizeColumns(_projectList);

        _hint.MaximumSize = new Size(Sc(contentWidth), 0);
        _hint.Location = new Point(Sc(Pad), _projectList.Bottom + Sc(SectionGap));

        var buttonsTop = _hint.Bottom + Sc(SectionGap);
        _exportButton.SetBounds(Sc(Pad), buttonsTop, Sc(ButtonWidth), Sc(ButtonHeight));
        _closeButton.SetBounds(
            Sc(ContentRight - CloseButtonWidth), buttonsTop, Sc(CloseButtonWidth), Sc(ButtonHeight));

        ClientSize = new Size(Sc(ClientWidth), buttonsTop + Sc(ButtonHeight) + Sc(Pad));
    }

    // First column takes what the six fixed-width numeric columns leave over.
    // The vertical scrollbar's width is reserved up front so a long 30-day
    // list doesn't force a horizontal scrollbar the moment the vertical appears.
    private void SizeColumns(ListView list)
    {
        var numeric = Sc(62);
        var cost = Sc(78);
        var total = list.ClientSize.Width - SystemInformation.VerticalScrollBarWidth;
        var first = Math.Max(Sc(120), total - (numeric * 5 + cost));

        list.Columns[0].Width = first;
        for (var i = 1; i <= 5; i++)
            list.Columns[i].Width = numeric;
        list.Columns[6].Width = cost;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Relayout();
        DialogPlacement.CenterOnPrimary(this);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Relayout();
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
