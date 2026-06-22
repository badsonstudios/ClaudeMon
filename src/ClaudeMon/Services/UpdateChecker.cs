namespace ClaudeMon.Services;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

/// <summary>
/// Checks GitHub Releases for a newer published ClaudeMon version. Uses the public,
/// <b>unauthenticated</b> API (never embed a token in the shipped app) and is
/// best-effort: any network/parse failure yields a non-fatal "no update" result with
/// an <see cref="UpdateCheckResult.ErrorMessage"/> rather than throwing.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    private const string LatestReleaseEndpoint =
        "https://api.github.com/repos/badsonstudios/ClaudeMon/releases/latest";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public UpdateChecker(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Queries the latest published release and compares it to <paramref name="currentVersion"/>.
    /// Returns whether a newer version exists, plus its version and release-page URL.
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(
        Version currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseEndpoint);
            // GitHub rejects requests without a User-Agent; the Accept header pins the API version.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("ClaudeMon", currentVersion.ToString()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            // 404 == no releases published yet. Treat as "no update", not an error worth surfacing.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return UpdateCheckResult.NoUpdate();

            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Failed($"GitHub returned HTTP {(int)response.StatusCode}.");

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            if (!TryParseVersion(tag, out var latest))
                return UpdateCheckResult.Failed("Could not parse the latest release version.");

            var url = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;

            return Normalize(latest) > Normalize(currentVersion)
                ? UpdateCheckResult.Available(latest, url)
                : UpdateCheckResult.NoUpdate();
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    /// <summary>Parses a release tag such as "v0.6.0" or "0.6.0" into a <see cref="Version"/>.</summary>
    internal static bool TryParseVersion(string? tag, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(tag))
            return false;

        var trimmed = tag.Trim();
        if (trimmed.StartsWith('v') || trimmed.StartsWith('V'))
            trimmed = trimmed[1..];

        return Version.TryParse(trimmed, out version!);
    }

    /// <summary>
    /// Collapses a version to (major, minor, build) so the assembly's 4-part version
    /// (e.g. 0.6.0.0) compares equal to a 3-part release tag (0.6.0).
    /// </summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

public record UpdateCheckResult
{
    public bool UpdateAvailable { get; private init; }
    public Version? LatestVersion { get; private init; }
    public string? ReleaseUrl { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static UpdateCheckResult Available(Version latest, string? releaseUrl) =>
        new() { UpdateAvailable = true, LatestVersion = latest, ReleaseUrl = releaseUrl };

    public static UpdateCheckResult NoUpdate() => new();

    public static UpdateCheckResult Failed(string message) =>
        new() { ErrorMessage = message };
}
