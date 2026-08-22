namespace ClaudeMon.Monitoring;

using System.Globalization;
using ClaudeMon.Models;

/// <summary>
/// Pure text composition for the Limit history tab (issue #186), the
/// <see cref="LocalCostText"/>/<see cref="CapacityReadoutText"/> pattern: kind labels shared
/// by the chart legend and the table, per-window cell text, and the drift alert's wording.
/// Culture-invariant so the same data always reads the same way.
/// </summary>
internal static class LimitHistoryText
{
    /// <summary>"Session (5-hour)", "Weekly", "Weekly (Opus 4)", or a humanized unknown kind.</summary>
    internal static string KindLabel(string? kind, string? scopeModel)
    {
        var normalized = kind?.Trim().ToLowerInvariant() ?? "";
        return normalized switch
        {
            "session" => "Session (5-hour)",
            "weekly_all" => "Weekly",
            "weekly_scoped" => string.IsNullOrWhiteSpace(scopeModel)
                ? "Weekly (model)"
                : $"Weekly ({scopeModel.Trim()})",
            "" => "Limit",
            _ => Humanize(normalized),
        };
    }

    /// <summary>"≈61M" for a derived capacity, "—" when the window can't support one.</summary>
    internal static string CapacityText(LimitHistoryRow row) =>
        row.ImpliedCapacity is { } capacity
            ? "≈" + LocalCostText.FormatTokens((long)Math.Round(capacity))
            : "—";

    /// <summary>"Max 20x", "Pro (changed)", or "—" when the plan was never set.</summary>
    internal static string PlanText(LimitWindowRecord record)
    {
        var plan = record.Plan switch
        {
            ClaudePlan.Pro => "Pro",
            ClaudePlan.Max5x => "Max 5x",
            ClaudePlan.Max20x => "Max 20x",
            _ => "—",
        };
        return record.PlanChanged ? plan + " (changed)" : plan;
    }

    /// <summary>"Aug 22, 14:05" — compact local time for the table's start/end columns.</summary>
    internal static string TimeText(DateTimeOffset instant) =>
        instant.ToLocalTime().ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The drift alert's title and body, e.g. 27% below a ≈52M norm.</summary>
    internal static (string Title, string Text) DriftAlert(
        string? kind, string? scopeModel, double current, double baseline)
    {
        var label = KindLabel(kind, scopeModel);
        var below = (int)Math.Round((1 - current / baseline) * 100);
        return (
            $"Possible throttling: {label} capacity down",
            $"Implied {label} capacity is ≈{LocalCostText.FormatTokens((long)Math.Round(current))} tokens, " +
            $"{below}% below its 30-day norm of ≈{LocalCostText.FormatTokens((long)Math.Round(baseline))}. " +
            "See Usage & costs → Limit history for the evidence. (est.)");
    }

    private static string Humanize(string value)
    {
        var text = value.Replace('_', ' ');
        return char.ToUpperInvariant(text[0]) + text[1..];
    }
}
