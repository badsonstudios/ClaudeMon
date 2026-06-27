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

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _responseBody;

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        public MockHttpHandler(HttpStatusCode statusCode, string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody),
            };
        }
    }
}
