namespace ClaudeMon.Services;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ClaudeMon.Models;

/// <summary>
/// Exchanges a refresh token for a fresh access token at the Claude Code OAuth
/// token endpoint, so ClaudeMon can stay authenticated without depending on the
/// CLI or VS Code extension having refreshed the on-disk token recently.
/// </summary>
/// <remarks>
/// The refresh token is rotated on every successful refresh — the response
/// carries a new <c>refresh_token</c> that must be persisted, or the next
/// refresh fails. A <c>400</c> / <c>invalid_grant</c> means the refresh token
/// itself is dead (genuine sign-in-expired), not a transient error.
/// </remarks>
public sealed class TokenRefresher : IDisposable
{
    // Public PKCE client id used by Claude Code (no client secret).
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string TokenEndpoint = "https://console.anthropic.com/v1/oauth/token";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public TokenRefresher(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Refreshes <paramref name="current"/> using its refresh token. On success
    /// returns a new <see cref="OAuthCredential"/> carrying the fresh access and
    /// (rotated) refresh tokens plus a recomputed expiry, with the non-token
    /// metadata (scopes, subscription, tier) carried forward unchanged.
    /// </summary>
    public async Task<TokenRefreshResult> RefreshAsync(
        OAuthCredential current, CancellationToken cancellationToken = default)
    {
        if (!current.HasRefreshToken)
        {
            return TokenRefreshResult.SignInExpired("No refresh token available.");
        }

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                grant_type = "refresh_token",
                refresh_token = current.RefreshToken,
                client_id = ClientId,
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            // 400/401 from the token endpoint means the refresh token is no longer
            // valid (invalid_grant) — a genuine sign-in-expired, not retryable.
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
            {
                return TokenRefreshResult.SignInExpired(
                    "Refresh token rejected. Run 'claude' to re-authenticate.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return TokenRefreshResult.Transient(
                    $"Token refresh failed: HTTP {(int)response.StatusCode}.");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsed = JsonSerializer.Deserialize<RefreshTokenResponse>(json);

            if (parsed is null || string.IsNullOrWhiteSpace(parsed.AccessToken))
            {
                return TokenRefreshResult.Transient("Token refresh returned no access token.");
            }

            var expiresAt = DateTimeOffset.UtcNow
                .AddSeconds(parsed.ExpiresIn)
                .ToUnixTimeMilliseconds();

            // Refresh tokens rotate; fall back to the current one only if the
            // response omitted it (defensive — it normally always returns one).
            var refreshToken = string.IsNullOrWhiteSpace(parsed.RefreshToken)
                ? current.RefreshToken
                : parsed.RefreshToken;

            var refreshed = current with
            {
                AccessToken = parsed.AccessToken,
                RefreshToken = refreshToken,
                ExpiresAt = expiresAt,
            };

            return TokenRefreshResult.Success(refreshed);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return TokenRefreshResult.Transient("Token refresh timed out.");
        }
        catch (HttpRequestException ex)
        {
            return TokenRefreshResult.Transient($"Network error during token refresh: {ex.Message}");
        }
        catch (JsonException)
        {
            // Never interpolate the exception message: it can echo a fragment of the
            // response body, which here contains the fresh access/refresh tokens.
            return TokenRefreshResult.Transient("Failed to parse token refresh response.");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

/// <summary>
/// Outcome of a token refresh: a fresh credential, a transient failure (network /
/// server error — keep trying), or sign-in-expired (refresh token is dead).
/// </summary>
public record TokenRefreshResult
{
    public bool IsSuccess { get; private init; }
    public bool IsSignInExpired { get; private init; }
    public OAuthCredential? Credential { get; private init; }
    public string? Error { get; private init; }

    public static TokenRefreshResult Success(OAuthCredential credential) =>
        new() { IsSuccess = true, Credential = credential };

    public static TokenRefreshResult SignInExpired(string error) =>
        new() { IsSignInExpired = true, Error = error };

    public static TokenRefreshResult Transient(string error) =>
        new() { Error = error };
}
