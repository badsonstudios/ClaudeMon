namespace ClaudeMon.Models;

/// <summary>
/// Severity of the Anthropic status page's overall indicator (statuspage.io's
/// <c>status.indicator</c>), ordered least → most severe so "did this get worse?" is a plain
/// comparison. <see cref="Maintenance"/> sits just above <see cref="Operational"/>: it is
/// scheduled work rather than a fault, but still worth telling the user about.
/// </summary>
public enum ServiceStatusLevel
{
    Operational,
    Maintenance,
    Minor,
    Major,
    Critical,
}

/// <summary>
/// The Anthropic status page's overall state. <see cref="Description"/> is the page's own
/// wording ("All Systems Operational", "Partial System Outage"), so ClaudeMon never invents a
/// characterization of an incident it can't see the detail of.
/// </summary>
public record ServiceStatus(ServiceStatusLevel Level, string Description)
{
    /// <summary>True when the status page reports everything healthy — nothing to show.</summary>
    public bool IsOperational => Level == ServiceStatusLevel.Operational;
}
