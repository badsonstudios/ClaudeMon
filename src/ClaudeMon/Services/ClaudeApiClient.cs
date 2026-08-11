namespace ClaudeMon.Services;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ClaudeMon.Models;

public sealed class ClaudeApiClient : IDisposable
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>
    /// Beta header Claude Code paired with an OAuth bearer token on first-party API calls as of
    /// CLI 2.1.226. The usage endpoint answered with and without it in live testing (#136), so
    /// we send it as forward-insurance against the OAuth contract being enforced later.
    /// </summary>
    private const string OAuthBeta = "oauth-2025-04-20";

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public ClaudeApiClient(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public async Task<ApiResult<UsageResponse>> GetUsageAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            // Set per-request, not on DefaultRequestHeaders: the HttpClient may be the caller's.
            request.Headers.UserAgent.Add(AppUserAgent.Header);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("anthropic-beta", OAuthBeta);

            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ApiResult<UsageResponse>.RateLimited(
                    "API rate limited. Will retry on next poll.");
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return ApiResult<UsageResponse>.AuthError(
                    "OAuth token rejected. Run 'claude' to re-authenticate.");
            }

            if (!response.IsSuccessStatusCode)
            {
                return ApiResult<UsageResponse>.Error(
                    $"API returned HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var usage = JsonSerializer.Deserialize<UsageResponse>(json);

            if (usage is null)
            {
                return ApiResult<UsageResponse>.Error("API returned empty response.");
            }

            return ApiResult<UsageResponse>.Success(usage);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return ApiResult<UsageResponse>.Error("API request timed out.");
        }
        catch (HttpRequestException ex)
        {
            return ApiResult<UsageResponse>.Error($"Network error: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return ApiResult<UsageResponse>.Error($"Failed to parse API response: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}

public record ApiResult<T>
{
    public bool IsSuccess { get; private init; }
    public bool IsRateLimited { get; private init; }
    public bool IsAuthError { get; private init; }
    public T? Data { get; private init; }
    public string? ErrorMessage { get; private init; }

    public static ApiResult<T> Success(T data) =>
        new() { IsSuccess = true, Data = data };

    public static ApiResult<T> RateLimited(string message) =>
        new() { IsRateLimited = true, ErrorMessage = message };

    public static ApiResult<T> AuthError(string message) =>
        new() { IsAuthError = true, ErrorMessage = message };

    public static ApiResult<T> Error(string message) =>
        new() { ErrorMessage = message };
}
