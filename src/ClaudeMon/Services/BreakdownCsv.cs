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
/// </summary>
public static class BreakdownCsv
{
    public const string Header =
        "Section,Name,InputTokens,OutputTokens,CacheWriteTokens,CacheReadTokens,TotalTokens,EstCostUsd,CostIncomplete";

    public static string Compose(LocalUsageBreakdown breakdown)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Header);

        foreach (var row in breakdown.ByModel)
            AppendRow(sb, "model", row);
        foreach (var row in breakdown.ByProject)
            AppendRow(sb, "project", row);
        AppendRow(sb, "total", breakdown.Totals with { DisplayName = "" });

        return sb.ToString();
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
