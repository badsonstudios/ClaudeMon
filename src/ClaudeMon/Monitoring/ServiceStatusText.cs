namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;

/// <summary>
/// Composes the flyout's Anthropic-service line, e.g. "⚠ Anthropic: Partial System Outage".
/// Pure, so the "healthy shows nothing" rule is unit-testable rather than buried in paint code.
/// </summary>
public static class ServiceStatusText
{
    /// <summary>
    /// The status page's own description is echoed verbatim, but a page that ever writes an
    /// essay there must not push the flyout line into the gear button — it trims at this length.
    /// </summary>
    internal const int MaxDescriptionLength = 52;

    /// <summary>
    /// The line to draw, or null when there is nothing to say — no status known, or everything
    /// operational. A healthy service adds no visual noise at all: the flyout omits the line.
    /// </summary>
    public static string? Compose(ServiceStatus? status)
    {
        if (status is null || status.IsOperational)
            return null;

        return $"⚠ Anthropic: {Trim(status.Description)}";
    }

    /// <summary>The description, trimmed with an ellipsis if it's longer than the line allows.</summary>
    internal static string Trim(string description) =>
        description.Length <= MaxDescriptionLength
            ? description
            : description[..(MaxDescriptionLength - 1)].TrimEnd() + "…";
}
