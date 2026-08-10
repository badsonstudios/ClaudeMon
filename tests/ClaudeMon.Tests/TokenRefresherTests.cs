namespace ClaudeMon.Tests;

using System.Net;
using System.Text.Json;
using ClaudeMon.Models;
using ClaudeMon.Services;

public class TokenRefresherTests
{
    private static OAuthCredential ExpiredCredential(string? refreshToken = "sk-ant-ort01-old") =>
        new(
            AccessToken: "sk-ant-oat01-old",
            RefreshToken: refreshToken,
            ExpiresAt: 1000000000000, // long past
            Scopes: new[] { "user:inference" },
            SubscriptionType: "max",
            RateLimitTier: "default_claude_max_5x");

    [Fact]
    public async Task Refresh_ValidResponse_ReturnsRotatedTokensAndFutureExpiry()
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {
            "access_token": "sk-ant-oat01-new",
            "refresh_token": "sk-ant-ort01-new",
            "expires_in": 28800
        }
        """);
        using var refresher = new TokenRefresher(new HttpClient(handler));

        var result = await refresher.RefreshAsync(ExpiredCredential());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Credential);
        Assert.Equal("sk-ant-oat01-new", result.Credential.AccessToken);
        Assert.Equal("sk-ant-ort01-new", result.Credential.RefreshToken);
        Assert.False(result.Credential.IsExpired);
        // Non-token metadata is carried forward unchanged.
        Assert.Equal("max", result.Credential.SubscriptionType);
        Assert.Equal("default_claude_max_5x", result.Credential.RateLimitTier);
    }

    [Fact]
    public async Task Refresh_PostsCorrectEndpointBodyAndClientId()
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {"access_token":"a","refresh_token":"b","expires_in":100}
        """);
        using var refresher = new TokenRefresher(new HttpClient(handler));

        await refresher.RefreshAsync(ExpiredCredential(refreshToken: "the-refresh-token"));

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        Assert.Equal("https://console.anthropic.com/v1/oauth/token", handler.LastRequest.RequestUri!.ToString());

        using var doc = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("refresh_token", doc.RootElement.GetProperty("grant_type").GetString());
        Assert.Equal("the-refresh-token", doc.RootElement.GetProperty("refresh_token").GetString());
        Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", doc.RootElement.GetProperty("client_id").GetString());
    }

    // The token endpoint used to be the one call that identified itself as nobody at all (#141).
    [Fact]
    public async Task Refresh_SendsSharedUserAgentHeader()
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {"access_token":"a","refresh_token":"b","expires_in":100}
        """);
        using var refresher = new TokenRefresher(new HttpClient(handler));

        await refresher.RefreshAsync(ExpiredCredential());

        Assert.NotNull(handler.LastRequest);
        Assert.Equal(AppUserAgent.Header, Assert.Single(handler.LastRequest.Headers.UserAgent));
    }

    [Fact]
    public async Task Refresh_MissingRefreshTokenInResponse_KeepsExistingOne()
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {"access_token":"sk-ant-oat01-new","expires_in":100}
        """);
        using var refresher = new TokenRefresher(new HttpClient(handler));

        var result = await refresher.RefreshAsync(ExpiredCredential(refreshToken: "keep-me"));

        Assert.True(result.IsSuccess);
        Assert.Equal("keep-me", result.Credential!.RefreshToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Refresh_RejectedToken_ReturnsSignInExpired(HttpStatusCode status)
    {
        var handler = new MockHttpHandler(status, """{"error":"invalid_grant"}""");
        using var refresher = new TokenRefresher(new HttpClient(handler));

        var result = await refresher.RefreshAsync(ExpiredCredential());

        Assert.False(result.IsSuccess);
        Assert.True(result.IsSignInExpired);
    }

    [Fact]
    public async Task Refresh_ServerError_ReturnsTransientFailure()
    {
        var handler = new MockHttpHandler(HttpStatusCode.InternalServerError, "");
        using var refresher = new TokenRefresher(new HttpClient(handler));

        var result = await refresher.RefreshAsync(ExpiredCredential());

        Assert.False(result.IsSuccess);
        Assert.False(result.IsSignInExpired);
    }

    [Fact]
    public async Task Refresh_NoRefreshToken_ReturnsSignInExpiredWithoutCallingNetwork()
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK, "{}");
        using var refresher = new TokenRefresher(new HttpClient(handler));

        var result = await refresher.RefreshAsync(ExpiredCredential(refreshToken: null));

        Assert.True(result.IsSignInExpired);
        Assert.Null(handler.LastRequest);
    }

    // --- Failure branches (issue #103). A refresh that can't complete must classify itself:
    // transient (keep the last known state and retry) versus sign-in-expired (tell the user).

    [Theory]
    [InlineData("{}")]                                             // no access_token at all
    [InlineData("""{"access_token": "", "expires_in": 100}""")]    // present but empty
    [InlineData("""{"access_token": "   ", "expires_in": 100}""")] // whitespace only
    [InlineData("null")]                                           // deserializes to null
    public async Task Refresh_ResponseWithoutUsableAccessToken_IsTransient(string body)
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK, body);
        using var refresher = new TokenRefresher(new HttpClient(handler));

        var result = await refresher.RefreshAsync(ExpiredCredential());

        Assert.False(result.IsSuccess);
        Assert.False(result.IsSignInExpired); // a broken response is not a dead refresh token
        Assert.Contains("no access token", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_MalformedJson_IsTransientAndNeverEchoesTheBody()
    {
        // The body of a token response carries the fresh access and refresh tokens, so the
        // parse error's message must never be interpolated into the reported error.
        var handler = new MockHttpHandler(HttpStatusCode.OK, """{"access_token": "sk-ant-oat01-leak""");
        using var refresher = new TokenRefresher(new HttpClient(handler));

        var result = await refresher.RefreshAsync(ExpiredCredential());

        Assert.False(result.IsSuccess);
        Assert.False(result.IsSignInExpired);
        Assert.NotNull(result.Error);
        // Pins the constant message: interpolating the JsonException's own text here would echo
        // the fragment of the body it choked on, which is where the tokens live.
        Assert.DoesNotContain("sk-ant-", result.Error);
    }

    [Fact]
    public async Task Refresh_NetworkFailure_IsTransient()
    {
        var handler = new MockHttpHandler(new HttpRequestException("no such host"));
        using var refresher = new TokenRefresher(new HttpClient(handler));

        var result = await refresher.RefreshAsync(ExpiredCredential());

        Assert.False(result.IsSuccess);
        Assert.False(result.IsSignInExpired);
        Assert.Contains("Network error", result.Error);
    }

    [Fact]
    public async Task Refresh_Timeout_IsTransient()
    {
        // HttpClient reports its own timeout as a TaskCanceledException with nothing cancelled.
        var handler = new MockHttpHandler(new TaskCanceledException("The request timed out."));
        using var refresher = new TokenRefresher(new HttpClient(handler));

        var result = await refresher.RefreshAsync(ExpiredCredential());

        Assert.False(result.IsSuccess);
        Assert.False(result.IsSignInExpired);
        Assert.Contains("timed out", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_Cancelled_PropagatesInsteadOfBeingClassified()
    {
        var handler = new MockHttpHandler(HttpStatusCode.OK, "{}");
        using var refresher = new TokenRefresher(new HttpClient(handler));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => refresher.RefreshAsync(ExpiredCredential(), cts.Token));
    }

    [Fact]
    public void Dispose_OwnedHttpClient_IsDisposedIdempotently()
    {
        var refresher = new TokenRefresher();

        Assert.Null(Record.Exception(refresher.Dispose));
        Assert.Null(Record.Exception(refresher.Dispose));
    }

    [Fact]
    public async Task Dispose_CallerSuppliedHttpClient_IsLeftAlone()
    {
        // UsageMonitor hands the same HttpClient to the API client and the refresher; disposing
        // one must not break the other.
        var handler = new MockHttpHandler(HttpStatusCode.OK,
            """{"access_token":"sk-ant-oat01-new","refresh_token":"sk-ant-ort01-new","expires_in":28800}""");
        using var shared = new HttpClient(handler);
        new TokenRefresher(shared).Dispose();

        using var second = new TokenRefresher(shared);
        var result = await second.RefreshAsync(ExpiredCredential());

        Assert.True(result.IsSuccess);
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;
        private readonly Exception? _exception;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public MockHttpHandler(HttpStatusCode statusCode, string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        /// <summary>Makes the send fail the way the transport would.</summary>
        public MockHttpHandler(Exception exception)
            : this(HttpStatusCode.OK, "")
        {
            _exception = exception;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            cancellationToken.ThrowIfCancellationRequested();

            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            if (_exception is not null)
                throw _exception;

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody),
            };
        }
    }
}
