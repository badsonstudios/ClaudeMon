namespace ClaudeMon.Monitoring;

using System.Globalization;
using ClaudeMon.Models;

/// <summary>
/// Composes the flyout's local-cost line, e.g.
/// "Today: ~$4.20 · 1.8M tokens · ~$1.10/hr (est.)". Everything cost-shaped is
/// prefixed "~" and the line ends "(est.)" — these numbers come from the local
/// transcripts and a bundled list-price table, not from billing. Pure and
/// culture-invariant so it is unit-testable and renders identically everywhere.
/// </summary>
public static class LocalCostText
{
    /// <summary>
    /// The full line, or null when there is nothing to show (no snapshot, or no
    /// tokens today) — the flyout omits the line entirely rather than drawing
    /// an empty shell. Cost reads "—" when only unpriced models contributed;
    /// the burn segment is omitted when there was no activity in the window.
    /// </summary>
    public static string? Compose(LocalUsageSnapshot? snapshot)
    {
        if (snapshot is null || snapshot.TotalTokens <= 0)
            return null;

        // A day touched by unpriced models: the known portion is a floor, not
        // an estimate — "≥" says so. Nothing priced at all reads "—".
        var cost = snapshot.HasUnpricedModels
            ? snapshot.CostUsd < 0.005 ? "—" : "≥" + FormatAmount(snapshot.CostUsd)
            : FormatCost(snapshot.CostUsd);

        var line = $"Today: {cost} · {FormatTokens(snapshot.TotalTokens)} tokens";

        if (snapshot.BurnRateUsdPerHour is { } rate)
            line += $" · {FormatCost(rate)}/hr";

        return line + " (est.)";
    }

    /// <summary>"950", "18.5K", "950K", "1.8M" — at most one decimal, no trailing zero.</summary>
    internal static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000)
            return ((tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture)) + "M";
        if (tokens >= 1_000)
            return ((tokens / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture)) + "K";
        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>"&lt;$0.01", "~$4.20", "~$12.40", "~$123" — cents matter less as the total grows.</summary>
    internal static string FormatCost(double usd) =>
        usd < 0.005 ? "<$0.01" : "~" + FormatAmount(usd);

    private static string FormatAmount(double usd) =>
        usd < 100
            ? "$" + usd.ToString("0.00", CultureInfo.InvariantCulture)
            : "$" + usd.ToString("0", CultureInfo.InvariantCulture);
}
