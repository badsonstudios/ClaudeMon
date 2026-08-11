namespace ClaudeMon.Services;

using System.Globalization;
using System.Text;
using ClaudeMon.Models;

/// <summary>
/// Composes the breakdown window's CSV export: one RFC-4180 file holding both
/// tables, distinguished by a leading Section column so it pivots cleanly in a
/// spreadsheet. Numbers are invariant-culture and undecorated (no '~'/'$' —
/// this is data; the CostIncomplete column carries the unpriced flag). The
/// caller writes the text as UTF-8 with BOM so Excel detects the encoding.
///
/// Each section is written in its own table's on-screen order (#119), and — when a row is
/// selected — with that table's drill-down applied (#168): the caller hands over the two
/// <see cref="BreakdownSortState"/>s and the <see cref="LocalUsageDrillDown"/> the window is
/// showing, and the rows go through the same pure <see cref="BreakdownSort"/> and
/// <see cref="BreakdownDrill"/> the tables are filled from. Exporting therefore produces the file
/// you were just looking at rather than the store's default order or the breakdown you drilled
/// from. Sorting the rows (not the formatted text) is what keeps "1.2M" from landing under "900K".
///
/// A drilled export leads with a <c>drill-model</c>/<c>drill-project</c> row naming the selected
/// row and carrying its totals, so a file holding one model's projects can't be mistaken for the
/// full picture. That row is also the drilled table's total — the rows below it sum to it — while
/// the trailing <c>total</c> row stays the whole timeframe, which is what the undrilled table
/// still shows. An undrilled export has no <c>drill-*</c> row and is byte-for-byte what it was.
/// </summary>
internal static class BreakdownCsv
{
    public const string Header =
        "Section,Name,InputTokens,OutputTokens,CacheWriteTokens,CacheReadTokens,TotalTokens,EstCostUsd,CostIncomplete";

    /// <summary>How much of a drilled-into key the suggested file name keeps.</summary>
    private const int MaxScopeLength = 40;

    public static string Compose(
        LocalUsageBreakdown breakdown, LocalUsageDrillDown? drill,
        BreakdownSortState modelSort, BreakdownSortState projectSort)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);

        if (drill is not null)
        {
            AppendRow(sb, "drill-" + AxisName(drill.Axis),
                drill.Totals with { DisplayName = BreakdownDrill.DisplayName(breakdown, drill) });
        }

        // A drill-down filters the table on the *other* axis, which is exactly the rule the window
        // fills its tables by — so the file follows the selection without a second notion of scope.
        // totals: null — the grand total is written once at the end of the file rather than after
        // each section, so it stays the last row however the two tables are ordered.
        foreach (var row in BreakdownSort.Order(
            BreakdownDrill.Filtering(drill, BreakdownAxis.Model)?.Rows ?? breakdown.ByModel,
            totals: null, modelSort))
            AppendRow(sb, "model", row);
        foreach (var row in BreakdownSort.Order(
            BreakdownDrill.Filtering(drill, BreakdownAxis.Project)?.Rows ?? breakdown.ByProject,
            totals: null, projectSort))
            AppendRow(sb, "project", row);
        AppendRow(sb, "total", breakdown.Totals with { DisplayName = "" });

        return sb.ToString();
    }

    /// <summary>
    /// The file-name fragment naming the drill scope — empty when nothing is drilled into, else
    /// "-model-<c>key</c>" / "-project-<c>key</c>" (#168). The drilled scope is in the file too,
    /// but the name is what a folder full of exports shows, and two files a minute apart otherwise
    /// differ only in their contents. The key is reduced to a filename-safe slug rather than
    /// escaped: a project key is a directory name and lands in whatever the save dialog offers.
    /// </summary>
    public static string FileNameScope(LocalUsageDrillDown? drill)
    {
        if (drill is null)
            return "";

        var slug = Slug(drill.Key);
        return slug.Length == 0 ? "-" + AxisName(drill.Axis) : $"-{AxisName(drill.Axis)}-{slug}";
    }

    private static string AxisName(BreakdownAxis axis) => axis == BreakdownAxis.Model ? "model" : "project";

    // Lowercase ASCII alphanumerics, every other run collapsed to a single '-', clipped to length.
    private static string Slug(string key)
    {
        var sb = new StringBuilder(Math.Min(key.Length, MaxScopeLength));
        foreach (var c in key)
        {
            if (char.IsAsciiLetterOrDigit(c))
                sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');

            if (sb.Length == MaxScopeLength)
                break;
        }

        return sb.ToString().TrimEnd('-');
    }

    private static void AppendRow(StringBuilder sb, string section, BreakdownRow row)
    {
        sb.Append(section).Append(',')
          .Append(EscapeField(row.DisplayName)).Append(',')
          .Append(row.InputTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(row.OutputTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(row.CacheWriteTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(row.CacheReadTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(row.TotalTokens.ToString(CultureInfo.InvariantCulture)).Append(',')
          .Append(row.CostUsd.ToString("0.0###", CultureInfo.InvariantCulture)).Append(',')
          .Append(row.HasUnpricedModels ? "true" : "false")
          .AppendLine();
    }

    // RFC-4180: quote when the field contains a comma, quote, or line break;
    // double any embedded quotes. Project paths can contain commas. Fields
    // starting with a formula trigger (= + - @) are prefixed with a quote-safe
    // apostrophe — directory names are attacker-influenceable-ish input and a
    // name like "=cmd|..." must not open as a live formula in Excel.
    internal static string EscapeField(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
            value = "'" + value;

        return value.IndexOfAny([',', '"', '\r', '\n']) < 0
            ? value
            : "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
