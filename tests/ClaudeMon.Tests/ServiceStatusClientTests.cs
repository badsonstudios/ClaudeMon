namespace ClaudeMon.Tests;

using System.Net;
using ClaudeMon.Models;
using ClaudeMon.Services;

public class ServiceStatusClientTests : IDisposable
{
    private readonly ServiceStatusClient _client;
    private readonly MockHttpHandler _handler;
    private readonly HttpClient _httpClient;

    public ServiceStatusClientTests()
    {
        _handler = new MockHttpHandler();
        // The client doesn't own an injected HttpClient, so the test disposes it (and with it
        // the handler) rather than pulling the handler out from under a live client.
        _httpClient = new HttpClient(_handler);
        _client = new ServiceStatusClient(_httpClient);
    }

    public void Dispose()
    {
        _client.Dispose();
        _httpClient.Dispose();
    }

    // --- Captured payloads (statuspage.io /api/v2/status.json) ---

    /// <summary>
    /// The live response from https://status.anthropic.com/api/v2/status.json (which 302s to
    /// status.claude.com), captured verbatim on 2026-08-09 while everything was healthy.
    /// </summary>
    private const string OperationalPayload = """
        {"page":{"id":"tymt9n04zgry","name":"Claude","url":"https://status.claude.com","time_zone":"Etc/UTC","updated_at":"2026-08-09T13:08:44.230Z"},"status":{"indicator":"none","description":"All Systems Operational"}}
        """;

    /// <summary>
    /// The same shape with a degraded indicator. statuspage emits exactly this document for an
    /// active incident — only the status object changes — so these are the real payloads for
    /// states we can't wait around for.
    /// </summary>
    private static string IncidentPayload(string indicator, string description) => $$$"""
        {"page":{"id":"tymt9n04zgry","name":"Claude","url":"https://status.claude.com","time_zone":"Etc/UTC","updated_at":"2026-08-09T13:08:44.230Z"},"status":{"indicator":"{{{indicator}}}","description":"{{{description}}}"}}
        """;

    [Fact]
    public void Parse_CapturedOperationalPayload_IsOperational()
    {
        var status = ServiceStatusClient.Parse(OperationalPayload);

        Assert.NotNull(status);
        Assert.Equal(ServiceStatusLevel.Operational, status.Level);
        Assert.Equal("All Systems Operational", status.Description);
        Assert.True(status.IsOperational);
    }

    [Theory]
    [InlineData("maintenance", ServiceStatusLevel.Maintenance)]
    [InlineData("minor", ServiceStatusLevel.Minor)]
    [InlineData("major", ServiceStatusLevel.Major)]
    [InlineData("critical", ServiceStatusLevel.Critical)]
    public void Parse_IncidentPayloads_MapToLevels(string indicator, ServiceStatusLevel expected)
    {
        var status = ServiceStatusClient.Parse(IncidentPayload(indicator, "Partial System Outage"));

        Assert.NotNull(status);
        Assert.Equal(expected, status.Level);
        Assert.Equal("Partial System Outage", status.Description);
        Assert.False(status.IsOperational);
    }

    [Fact]
    public void Parse_IndicatorCasingAndPadding_IsTolerated()
    {
        var status = ServiceStatusClient.Parse(IncidentPayload(" MAJOR ", "Major Service Outage"));

        Assert.Equal(ServiceStatusLevel.Major, status?.Level);
    }

    [Fact]
    public void Parse_UnknownIndicator_IsIgnoredRatherThanInventingAnIncident()
    {
        Assert.Null(ServiceStatusClient.Parse(IncidentPayload("catastrophic", "Something new")));
    }

    [Fact]
    public void Parse_MissingDescription_FallsBackToLevelWording()
    {
        var status = ServiceStatusClient.Parse("""{"status":{"indicator":"minor"}}""");

        Assert.Equal(ServiceStatusLevel.Minor, status?.Level);
        Assert.False(string.IsNullOrWhiteSpace(status?.Description));
    }

    [Theory]
    [InlineData("""{"page":{"id":"x"}}""")]                     // no status object
    [InlineData("""{"status":"none"}""")]                        // status isn't an object
    [InlineData("""{"status":{"indicator":7}}""")]               // indicator isn't a string
    [InlineData("""{"status":{}}""")]                            // no indicator at all
    [InlineData("not json {{{")]
    [InlineData("")]
    public void Parse_UnusableBodies_ReturnNull(string json)
    {
        Assert.Null(ServiceStatusClient.Parse(json));
    }

    [Fact]
    public async Task GetStatus_Success_ReturnsParsedStatus()
    {
        _handler.SetResponse(HttpStatusCode.OK, IncidentPayload("minor", "Partially Degraded Service"));

        var status = await _client.GetStatusAsync();

        Assert.Equal(ServiceStatusLevel.Minor, status?.Level);
        Assert.Equal("Partially Degraded Service", status?.Description);
    }

    [Fact]
    public async Task GetStatus_CallsTheStatusEndpoint_WithNoCredentials()
    {
        _handler.SetResponse(HttpStatusCode.OK, OperationalPayload);

        await _client.GetStatusAsync();

        Assert.Equal(
            "https://status.claude.com/api/v2/status.json",
            _handler.LastRequest?.RequestUri?.ToString());
        Assert.Null(_handler.LastRequest?.Headers.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task GetStatus_HttpFailure_IsSilentNull(HttpStatusCode code)
    {
        _handler.SetResponse(code, "");

        Assert.Null(await _client.GetStatusAsync());
    }

    [Fact]
    public async Task GetStatus_NetworkError_IsSilentNull()
    {
        _handler.Throw(new HttpRequestException("no network"));

        Assert.Null(await _client.GetStatusAsync());
    }

    [Fact]
    public async Task GetStatus_MalformedBody_IsSilentNull()
    {
        _handler.SetResponse(HttpStatusCode.OK, "<html>not the api</html>");

        Assert.Null(await _client.GetStatusAsync());
    }

    [Fact]
    public async Task GetStatus_Cancelled_Throws()
    {
        // Shutdown must propagate rather than be swallowed as "no status" — the caller's poll
        // loop already treats OperationCanceledException as "we're closing".
        _handler.SetResponse(HttpStatusCode.OK, OperationalPayload);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _client.GetStatusAsync(cts.Token));
    }

    [Fact]
    public void StatusPageUrl_IsHttps()
    {
        // The flyout hands this straight to BrowserLauncher, which only opens http(s).
        Assert.StartsWith("https://", ServiceStatusClient.StatusPageUrl);
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
            _exception = null;
        }

        public void Throw(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastRequest = request;

            if (_exception is not null)
                return Task.FromException<HttpResponseMessage>(_exception);

            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody),
            });
        }
    }
}
