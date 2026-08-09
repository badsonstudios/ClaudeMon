namespace ClaudeMon.Services;

using System.Text.Json;
using ClaudeMon.Models;

/// <summary>
/// Reads Anthropic's public status page (statuspage.io's <c>/api/v2/status.json</c>) so the
/// flyout can answer "is it me, or is it down?".
///
/// Best-effort by design: no credentials are sent, and any network, HTTP, or parse failure
/// yields <c>null</c> rather than an error — the status of the status page is not itself
/// something worth alerting about, and the caller simply keeps showing what it last knew.
/// </summary>
public sealed class ServiceStatusClient : IDisposable
{
    /// <summary>
    /// The public status page, opened when the flyout's status line is clicked.
    /// <c>status.anthropic.com</c> 302-redirects here, so the canonical host is used directly
    /// rather than paying for a redirect hop on every poll.
    /// </summary>
    public const string StatusPageUrl = "https://status.claude.com";

    private const string StatusEndpoint = "https://status.claude.com/api/v2/status.json";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public ServiceStatusClient(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        // Shorter than the usage client's timeout: this is a secondary signal riding along on
        // the usage poll, so it should never be what makes a poll feel slow.
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Fetches the current overall status, or null when it can't be determined (offline, HTTP
    /// error, malformed body, or an indicator string this version doesn't recognize).
    /// </summary>
    public async Task<ServiceStatus?> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, StatusEndpoint);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return Parse(json);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // (A malformed body can't reach here — Parse swallows JsonException itself.)
            return null;
        }
    }

    /// <summary>
    /// Pure parse of a statuspage <c>status.json</c> body. Null means "no usable status" —
    /// malformed JSON, no <c>status</c> object, or an indicator this version doesn't know.
    /// Treating an unknown indicator as "nothing to show" is deliberate: inventing an incident
    /// out of a string we can't interpret would be worse than staying quiet.
    /// </summary>
    internal static ServiceStatus? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("status", out var status)
                || status.ValueKind != JsonValueKind.Object)
                return null;

            if (!TryParseLevel(ReadString(status, "indicator"), out var level))
                return null;

            var description = ReadString(status, "description");
            return new ServiceStatus(
                level,
                string.IsNullOrWhiteSpace(description) ? DefaultDescription(level) : description.Trim());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Maps statuspage's <c>indicator</c> vocabulary onto <see cref="ServiceStatusLevel"/>.</summary>
    internal static bool TryParseLevel(string? indicator, out ServiceStatusLevel level)
    {
        switch (indicator?.Trim().ToLowerInvariant())
        {
            case "none":
                level = ServiceStatusLevel.Operational;
                return true;
            case "maintenance":
                level = ServiceStatusLevel.Maintenance;
                return true;
            case "minor":
                level = ServiceStatusLevel.Minor;
                return true;
            case "major":
                level = ServiceStatusLevel.Major;
                return true;
            case "critical":
                level = ServiceStatusLevel.Critical;
                return true;
            default:
                level = ServiceStatusLevel.Operational;
                return false;
        }
    }

    // Only a JSON string counts; a number/object/null in that slot is treated as absent.
    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Fallback wording for the rare payload that carries an indicator but no description.
    private static string DefaultDescription(ServiceStatusLevel level) => level switch
    {
        ServiceStatusLevel.Operational => "All Systems Operational",
        ServiceStatusLevel.Maintenance => "Maintenance in progress",
        ServiceStatusLevel.Minor => "Minor service issue",
        ServiceStatusLevel.Major => "Major service outage",
        _ => "Critical service outage",
    };

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
