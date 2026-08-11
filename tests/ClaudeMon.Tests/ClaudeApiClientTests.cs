namespace ClaudeMon.Tests;

using System.Net;
using System.Text.Json;
using ClaudeMon.Models;
using ClaudeMon.Services;

public class ClaudeApiClientTests : IDisposable
{
    private readonly ClaudeApiClient _client;
    private readonly MockHttpHandler _handler;

    public ClaudeApiClientTests()
    {
        _handler = new MockHttpHandler();
        _client = new ClaudeApiClient(new HttpClient(_handler));
    }

    public void Dispose()
    {
        _client.Dispose();
        _handler.Dispose();
    }

    [Fact]
    public async Task GetUsage_ValidResponse_ReturnsUsageData()
    {
        _handler.SetResponse(HttpStatusCode.OK, """
        {
            "five_hour": {
                "utilization": 23.4,
                "resets_at": "2026-05-22T18:00:00Z"
            },
            "seven_day": {
                "utilization": 45.2,
                "resets_at": "2026-05-25T00:00:00Z"
            }
        }
        """);

        var result = await _client.GetUsageAsync("test-token");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Data);
        Assert.NotNull(result.Data.FiveHour);
        Assert.Equal(23.4, result.Data.FiveHour.UtilizationPct);
        Assert.NotNull(result.Data.SevenDay);
        Assert.Equal(45.2, result.Data.SevenDay.UtilizationPct);
    }

    [Fact]
    public async Task GetUsage_429Response_ReturnsRateLimited()
    {
        _handler.SetResponse(HttpStatusCode.TooManyRequests, "");

        var result = await _client.GetUsageAsync("test-token");

        Assert.False(result.IsSuccess);
        Assert.True(result.IsRateLimited);
        Assert.Contains("rate limited", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUsage_401Response_ReturnsAuthError()
    {
        _handler.SetResponse(HttpStatusCode.Unauthorized, "");

        var result = await _client.GetUsageAsync("test-token");

        Assert.False(result.IsSuccess);
        Assert.True(result.IsAuthError);
        Assert.Contains("re-authenticate", result.ErrorMessage);
    }

    [Fact]
    public async Task GetUsage_500Response_ReturnsError()
    {
        _handler.SetResponse(HttpStatusCode.InternalServerError, "");

        var result = await _client.GetUsageAsync("test-token");

        Assert.False(result.IsSuccess);
        Assert.False(result.IsRateLimited);
        Assert.False(result.IsAuthError);
        Assert.Contains("500", result.ErrorMessage);
    }

    [Fact]
    public async Task GetUsage_InvalidJson_ReturnsError()
    {
        _handler.SetResponse(HttpStatusCode.OK, "not valid json {{{");

        var result = await _client.GetUsageAsync("test-token");

        Assert.False(result.IsSuccess);
        Assert.Contains("parse", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUsage_SendsAuthHeader()
    {
        _handler.SetResponse(HttpStatusCode.OK, """{"five_hour":{"utilization":0,"resets_at":"2026-01-01T00:00:00Z"}}""");

        await _client.GetUsageAsync("my-secret-token");

        Assert.NotNull(_handler.LastRequest);
        Assert.Equal("Bearer", _handler.LastRequest.Headers.Authorization?.Scheme);
        Assert.Equal("my-secret-token", _handler.LastRequest.Headers.Authorization?.Parameter);
    }

    // The usage endpoint is polled every few minutes for the life of the session; identify
    // ourselves honestly so Anthropic can attribute (and if need be, contact) the traffic.
    // Deliberately NOT "claude-code/…" — see the header audit in issue #136.
    [Fact]
    public async Task GetUsage_SendsClaudeMonUserAgent()
    {
        _handler.SetResponse(HttpStatusCode.OK, """{"five_hour":{"utilization":0,"resets_at":"2026-01-01T00:00:00Z"}}""");

        await _client.GetUsageAsync("test-token");

        // The shared app-wide agent, not one this client builds for itself — its shape is
        // asserted in AppUserAgentTests.
        var product = Assert.Single(_handler.LastRequest!.Headers.UserAgent);
        Assert.Equal(AppUserAgent.Header, product);
    }

    // OAuth bearer calls to the first-party API carry this beta header in Claude Code; the
    // usage endpoint tolerates its absence today, so sending it is forward-insurance.
    [Fact]
    public async Task GetUsage_SendsOAuthBetaAndAcceptHeaders()
    {
        _handler.SetResponse(HttpStatusCode.OK, """{"five_hour":{"utilization":0,"resets_at":"2026-01-01T00:00:00Z"}}""");

        await _client.GetUsageAsync("test-token");

        Assert.True(_handler.LastRequest!.Headers.TryGetValues("anthropic-beta", out var beta));
        Assert.Equal("oauth-2025-04-20", Assert.Single(beta));
        Assert.Contains(
            _handler.LastRequest.Headers.Accept,
            h => h.MediaType == "application/json");
    }

    [Fact]
    public void UsageBucket_FormatResetCountdown_Hours()
    {
        var bucket = new UsageBucket(50.0, DateTimeOffset.UtcNow.AddHours(2).AddMinutes(30));
        var text = bucket.FormatResetCountdown();
        Assert.StartsWith("resets 2h", text);
        Assert.Matches(@"resets 2h \d+m", text);
    }

    [Fact]
    public void UsageBucket_FormatResetCountdown_Days()
    {
        var bucket = new UsageBucket(50.0, DateTimeOffset.UtcNow.AddDays(3).AddHours(5));
        var text = bucket.FormatResetCountdown();
        Assert.Contains("3d", text);
    }

    // A past resets_at means the window ended and the user went idle — the API keeps
    // returning the old reset time until new usage opens a window (issue #61). This must
    // read as a distinct idle state, never a perpetual "resetting...".
    [Fact]
    public void UsageBucket_FormatResetCountdown_PastReset_ShowsIdleState()
    {
        var bucket = new UsageBucket(50.0, DateTimeOffset.UtcNow.AddMinutes(-5));
        var text = bucket.FormatResetCountdown();
        Assert.Equal("resets on next use", text);
    }

    [Fact]
    public void UsageBucket_FormatResetCountdown_UnknownReset_ShowsNeutralMarker()
    {
        var bucket = new UsageBucket(50.0, null);
        Assert.Equal("—", bucket.FormatResetCountdown());
    }

    [Fact]
    public void UsageBucket_IsExpired_TracksResetTime()
    {
        Assert.True(new UsageBucket(50.0, DateTimeOffset.UtcNow.AddMinutes(-5)).IsExpired);
        Assert.False(new UsageBucket(50.0, DateTimeOffset.UtcNow.AddMinutes(5)).IsExpired);
        Assert.False(new UsageBucket(50.0, null).IsExpired);
    }

    // An expired idle window must not read as "100% of the window elapsed" — that would skew
    // the pace colouring and pin the time tick at the end of the bar (issue #61).
    [Fact]
    public void UsageBucket_ElapsedFraction_PastReset_IsNull()
    {
        var bucket = new UsageBucket(50.0, DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Null(bucket.ElapsedFraction(TimeSpan.FromHours(5)));
    }

    [Fact]
    public void UsageBucket_ElapsedFraction_LiveWindow_Unchanged()
    {
        var bucket = new UsageBucket(50.0, DateTimeOffset.UtcNow.AddHours(2.5));
        var fraction = bucket.ElapsedFraction(TimeSpan.FromHours(5));
        Assert.NotNull(fraction);
        Assert.InRange(fraction.Value, 0.49, 0.51);
    }

    [Fact]
    public void UsageBucket_WindowStart_IsTheResetMinusTheWindowLength()
    {
        var resetAt = new DateTimeOffset(2026, 6, 27, 17, 0, 0, TimeSpan.Zero);
        var bucket = new UsageBucket(50.0, resetAt);

        Assert.Equal(resetAt.AddHours(-5), bucket.WindowStart(UsageWindows.FiveHour));
    }

    [Fact]
    public void UsageBucket_WindowStart_UnknownReset_IsNull()
    {
        Assert.Null(new UsageBucket(50.0, null).WindowStart(UsageWindows.FiveHour));
    }

    // Unlike ElapsedFraction, an expired window still reports where it began — the burn-rate
    // filter (#160) wants the boundary, and one 5 hours in the past excludes nothing recent.
    [Fact]
    public void UsageBucket_WindowStart_ExpiredWindow_StillKnown()
    {
        var resetAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var bucket = new UsageBucket(50.0, resetAt);

        Assert.Equal(resetAt.AddHours(-5), bucket.WindowStart(UsageWindows.FiveHour));
    }

    // --- Error paths: everything below the happy path must come back as a result record, never
    // as an exception thrown into the poll loop (issue #103).

    [Fact]
    public async Task GetUsage_JsonNullBody_ReturnsEmptyResponseError()
    {
        // Well-formed JSON that deserializes to null — distinct from a parse failure.
        _handler.SetResponse(HttpStatusCode.OK, "null");

        var result = await _client.GetUsageAsync("test-token");

        Assert.False(result.IsSuccess);
        Assert.Contains("empty", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUsage_NetworkFailure_ReturnsErrorWithoutThrowing()
    {
        _handler.SetException(new HttpRequestException("no such host"));

        var result = await _client.GetUsageAsync("test-token");

        Assert.False(result.IsSuccess);
        Assert.False(result.IsAuthError);
        Assert.False(result.IsRateLimited);
        Assert.Contains("Network error", result.ErrorMessage);
    }

    [Fact]
    public async Task GetUsage_Timeout_ReturnsTimedOutError()
    {
        // HttpClient surfaces its own timeout as a TaskCanceledException with no cancellation
        // requested — which must read as "timed out", not as a shutdown.
        _handler.SetException(new TaskCanceledException("The request timed out."));

        var result = await _client.GetUsageAsync("test-token");

        Assert.False(result.IsSuccess);
        Assert.Contains("timed out", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetUsage_Cancelled_PropagatesInsteadOfReportingAnError()
    {
        // Shutdown is not a poll failure: the monitor's own catch handles it, and reporting it
        // as an error would flap the tray icon on the way out.
        _handler.SetResponse(HttpStatusCode.OK, "{}");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _client.GetUsageAsync("test-token", cts.Token));
    }

    [Fact]
    public void Dispose_OwnedHttpClient_IsDisposedIdempotently()
    {
        // Constructed without a caller-supplied client, so this one owns (and must dispose) it.
        var client = new ClaudeApiClient();

        Assert.Null(Record.Exception(client.Dispose));
        Assert.Null(Record.Exception(client.Dispose));
    }

    [Fact]
    public async Task Dispose_CallerSuppliedHttpClient_IsLeftAlone()
    {
        // The monitor shares one HttpClient across the API client and the token refresher, so
        // disposing either must not pull the socket handler out from under the other.
        using var shared = new HttpClient(_handler);
        new ClaudeApiClient(shared).Dispose();

        _handler.SetResponse(HttpStatusCode.OK, """{"five_hour":{"utilization":1,"resets_at":"2026-01-01T00:00:00Z"}}""");
        using var second = new ClaudeApiClient(shared);
        var result = await second.GetUsageAsync("test-token");

        Assert.True(result.IsSuccess);
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _responseBody = "";
        private Exception? _exception;

        public HttpRequestMessage? LastRequest { get; private set; }

        public void SetResponse(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _responseBody = body;
        }

        /// <summary>Makes the next send fail the way the transport would.</summary>
        public void SetException(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            cancellationToken.ThrowIfCancellationRequested();
            if (_exception is not null)
                return Task.FromException<HttpResponseMessage>(_exception);

            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody),
            };
            return Task.FromResult(response);
        }
    }
}
