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

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _responseBody = "";

        public HttpRequestMessage? LastRequest { get; private set; }

        public void SetResponse(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _responseBody = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody),
            };
            return Task.FromResult(response);
        }
    }
}
