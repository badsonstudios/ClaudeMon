namespace ClaudeMon.Tests;

using System.Net;
using ClaudeMon.Services;

public class UpdateCheckerTests : IDisposable
{
    private readonly UpdateChecker _checker;
    private readonly MockHttpHandler _handler;

    public UpdateCheckerTests()
    {
        _handler = new MockHttpHandler();
        _checker = new UpdateChecker(new HttpClient(_handler));
    }

    public void Dispose()
    {
        _checker.Dispose();
        _handler.Dispose();
    }

    private static string Release(string tag, string url = "https://github.com/badsonstudios/ClaudeMon/releases/tag/x") =>
        $$"""{ "tag_name": "{{tag}}", "html_url": "{{url}}" }""";

    [Fact]
    public async Task Check_NewerRelease_ReportsAvailable()
    {
        _handler.SetResponse(HttpStatusCode.OK, Release("v0.6.0", "https://example.com/rel"));

        var result = await _checker.CheckAsync(new Version(0, 5, 0));

        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(0, 6, 0), result.LatestVersion);
        Assert.Equal("https://example.com/rel", result.ReleaseUrl);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Check_SameVersion_ReportsNoUpdate()
    {
        _handler.SetResponse(HttpStatusCode.OK, Release("v0.5.0"));

        var result = await _checker.CheckAsync(new Version(0, 5, 0));

        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task Check_OlderRemote_ReportsNoUpdate()
    {
        _handler.SetResponse(HttpStatusCode.OK, Release("v0.4.0"));

        var result = await _checker.CheckAsync(new Version(0, 5, 0));

        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task Check_AssemblyFourPartVersion_MatchesThreePartTag()
    {
        // The running assembly version is 4-part (0.5.0.0); the tag is 3-part (0.5.0).
        _handler.SetResponse(HttpStatusCode.OK, Release("v0.5.0"));

        var result = await _checker.CheckAsync(new Version(0, 5, 0, 0));

        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task Check_TagWithoutVPrefix_IsParsed()
    {
        _handler.SetResponse(HttpStatusCode.OK, Release("0.7.0"));

        var result = await _checker.CheckAsync(new Version(0, 5, 0));

        Assert.True(result.UpdateAvailable);
        Assert.Equal(new Version(0, 7, 0), result.LatestVersion);
    }

    [Fact]
    public async Task Check_NoReleasesYet_404_IsSilentNoUpdate()
    {
        _handler.SetResponse(HttpStatusCode.NotFound, "");

        var result = await _checker.CheckAsync(new Version(0, 5, 0));

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task Check_InvalidJson_ReportsFailureWithoutThrowing()
    {
        _handler.SetResponse(HttpStatusCode.OK, "not json {{{");

        var result = await _checker.CheckAsync(new Version(0, 5, 0));

        Assert.False(result.UpdateAvailable);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task Check_ServerError_ReportsFailure()
    {
        _handler.SetResponse(HttpStatusCode.InternalServerError, "");

        var result = await _checker.CheckAsync(new Version(0, 5, 0));

        Assert.False(result.UpdateAvailable);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task Check_SendsUserAgentHeader()
    {
        _handler.SetResponse(HttpStatusCode.OK, Release("v0.5.0"));

        await _checker.CheckAsync(new Version(0, 5, 0));

        Assert.NotNull(_handler.LastRequest);
        Assert.NotEmpty(_handler.LastRequest.Headers.UserAgent);
    }

    [Theory]
    [InlineData("v0.6.0", true, 0, 6, 0)]
    [InlineData("0.6.0", true, 0, 6, 0)]
    [InlineData("V1.2.3", true, 1, 2, 3)]
    [InlineData("", false, 0, 0, 0)]
    [InlineData("not-a-version", false, 0, 0, 0)]
    [InlineData(null, false, 0, 0, 0)]
    public void TryParseVersion_HandlesTagFormats(
        string? tag, bool expected, int major, int minor, int build)
    {
        var ok = UpdateChecker.TryParseVersion(tag, out var version);

        Assert.Equal(expected, ok);
        if (expected)
            Assert.Equal(new Version(major, minor, build), version);
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
