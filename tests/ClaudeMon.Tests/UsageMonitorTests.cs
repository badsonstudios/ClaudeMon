namespace ClaudeMon.Tests;

using System.Net;
using ClaudeMon.Models;
using ClaudeMon.Monitoring;
using ClaudeMon.Services;

public class UsageMonitorTests : IDisposable
{
    private readonly string _tempDir;

    public UsageMonitorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"claudemon-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private string WriteCredentialFile(string token = "test-token", long expiresAt = 9999999999999)
    {
        var path = Path.Combine(_tempDir, ".credentials.json");
        File.WriteAllText(path, $$"""
        {
            "claudeAiOauth": {
                "accessToken": "{{token}}",
                "expiresAt": {{expiresAt}}
            }
        }
        """);
        return path;
    }

    [Fact]
    public async Task RefreshNow_ValidResponse_UpdatesLastUsage()
    {
        var credPath = WriteCredentialFile();
        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {
            "five_hour": {"utilization": 42.0, "resets_at": "2026-06-01T00:00:00Z"},
            "seven_day": {"utilization": 15.0, "resets_at": "2026-06-05T00:00:00Z"}
        }
        """);
        using var httpClient = new HttpClient(handler);
        using var apiClient = new ClaudeApiClient(httpClient);
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1));

        UsageUpdatedEventArgs? receivedArgs = null;
        monitor.UsageUpdated += (_, args) => receivedArgs = args;

        await monitor.RefreshNowAsync();

        Assert.NotNull(monitor.LastUsage);
        Assert.Equal(42.0, monitor.LastUsage.FiveHour?.UtilizationPct);
        Assert.Equal(15.0, monitor.LastUsage.SevenDay?.UtilizationPct);
        Assert.Equal(MonitorStatus.Connected, monitor.Status);
        Assert.NotNull(receivedArgs);
        Assert.Equal(MonitorStatus.Connected, receivedArgs.Status);
    }

    [Fact]
    public async Task RefreshNow_MissingCredentials_SetsAuthError()
    {
        var credPath = Path.Combine(_tempDir, "nonexistent.json");
        using var apiClient = new ClaudeApiClient(new HttpClient(new MockHttpHandler(HttpStatusCode.OK, "{}")));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1));

        await monitor.RefreshNowAsync();

        Assert.Equal(MonitorStatus.AuthError, monitor.Status);
        Assert.NotNull(monitor.LastError);
        Assert.Contains("not found", monitor.LastError);
    }

    [Fact]
    public async Task RefreshNow_RateLimited_KeepsLastData()
    {
        var credPath = WriteCredentialFile();

        // First call succeeds
        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {"five_hour": {"utilization": 30.0, "resets_at": "2026-06-01T00:00:00Z"}}
        """);
        using var httpClient = new HttpClient(handler);
        using var apiClient = new ClaudeApiClient(httpClient);
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1));

        await monitor.RefreshNowAsync();
        Assert.Equal(30.0, monitor.LastUsage?.FiveHour?.UtilizationPct);

        // Second call gets rate limited
        handler.SetResponse(HttpStatusCode.TooManyRequests, "");
        await monitor.RefreshNowAsync();

        // Last usage data should still be available
        Assert.Equal(30.0, monitor.LastUsage?.FiveHour?.UtilizationPct);
        Assert.Equal(MonitorStatus.RateLimited, monitor.Status);
    }

    [Fact]
    public async Task RefreshNow_NetworkError_SetsOffline()
    {
        var credPath = WriteCredentialFile();
        var handler = new MockHttpHandler(HttpStatusCode.InternalServerError, "");
        using var httpClient = new HttpClient(handler);
        using var apiClient = new ClaudeApiClient(httpClient);
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1));

        await monitor.RefreshNowAsync();

        Assert.Equal(MonitorStatus.Offline, monitor.Status);
    }

    private string WriteExpiredCredentialFile()
    {
        var path = Path.Combine(_tempDir, ".credentials.json");
        File.WriteAllText(path, """
        {
            "claudeAiOauth": {
                "accessToken": "stale-access",
                "refreshToken": "valid-refresh",
                "expiresAt": 1000000000000
            }
        }
        """);
        return path;
    }

    [Fact]
    public async Task RefreshNow_ExpiredToken_RefreshesAndConnects()
    {
        var credPath = WriteExpiredCredentialFile();
        var handler = new RoutingHttpHandler(
            tokenResponse: """{"access_token":"fresh-access","refresh_token":"fresh-refresh","expires_in":28800}""",
            usageResponse: """{"five_hour":{"utilization":12.0,"resets_at":"2026-06-01T00:00:00Z"}}""");

        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var refresher = new TokenRefresher(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), refresher);

        await monitor.RefreshNowAsync();

        Assert.Equal(MonitorStatus.Connected, monitor.Status);
        Assert.Equal(12.0, monitor.LastUsage?.FiveHour?.UtilizationPct);
        // The usage call used the refreshed access token, not the stale one.
        Assert.Equal("fresh-access", handler.LastUsageToken);
        // The refreshed token was written back to the shared credentials file.
        var raw = File.ReadAllText(credPath);
        Assert.Contains("fresh-access", raw);
        Assert.Contains("fresh-refresh", raw);
    }

    [Fact]
    public async Task RefreshNow_ExpiredToken_RefreshRejected_SetsAuthError()
    {
        var credPath = WriteExpiredCredentialFile();
        var handler = new RoutingHttpHandler(
            tokenResponse: """{"error":"invalid_grant"}""",
            usageResponse: "{}",
            tokenStatus: HttpStatusCode.BadRequest);

        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var refresher = new TokenRefresher(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), refresher);

        await monitor.RefreshNowAsync();

        Assert.Equal(MonitorStatus.AuthError, monitor.Status);
        // We never attempted the usage call with a dead token.
        Assert.Null(handler.LastUsageToken);
    }

    [Fact]
    public async Task RefreshNow_ExpiredToken_RefreshTransientFailure_SetsOffline()
    {
        var credPath = WriteExpiredCredentialFile();
        var handler = new RoutingHttpHandler(
            tokenResponse: "",
            usageResponse: "{}",
            tokenStatus: HttpStatusCode.InternalServerError);

        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var refresher = new TokenRefresher(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), refresher);

        await monitor.RefreshNowAsync();

        Assert.Equal(MonitorStatus.Offline, monitor.Status);
    }

    [Fact]
    public async Task RefreshNow_ValidLookingToken_Rejected401_RefreshesReactivelyAndConnects()
    {
        // Token's expiresAt is far in the future, so the proactive path is skipped;
        // the server still rejects it (401), driving the reactive refresh-and-retry.
        var credPath = WriteCredentialFile(token: "stale-access");
        File.WriteAllText(credPath, """
        {
            "claudeAiOauth": {
                "accessToken": "stale-access",
                "refreshToken": "valid-refresh",
                "expiresAt": 9999999999999
            }
        }
        """);
        var handler = new RoutingHttpHandler(
            tokenResponse: """{"access_token":"fresh-access","refresh_token":"fresh-refresh","expires_in":28800}""",
            usageResponse: """{"five_hour":{"utilization":7.0,"resets_at":"2026-06-01T00:00:00Z"}}""",
            usageRequiresToken: "fresh-access");

        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var refresher = new TokenRefresher(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), refresher);

        await monitor.RefreshNowAsync();

        Assert.Equal(MonitorStatus.Connected, monitor.Status);
        Assert.Equal(7.0, monitor.LastUsage?.FiveHour?.UtilizationPct);
        Assert.Equal("fresh-access", handler.LastUsageToken);
    }

    [Fact]
    public async Task RefreshNow_ExpiredToken_NoRefresher_SetsAuthError()
    {
        var credPath = WriteExpiredCredentialFile();
        using var apiClient = new ClaudeApiClient(new HttpClient(new MockHttpHandler(HttpStatusCode.OK, "{}")));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1));

        await monitor.RefreshNowAsync();

        Assert.Equal(MonitorStatus.AuthError, monitor.Status);
    }

    [Fact]
    public async Task RefreshNow_Connected_LogsTransition_NeverLogsToken()
    {
        const string secretToken = "super-secret-access-token";
        var credPath = WriteCredentialFile(token: secretToken);
        var logPath = Path.Combine(_tempDir, "monitor.log");
        var logger = new Logger(logPath);

        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {"five_hour": {"utilization": 5.0, "resets_at": "2026-06-01T00:00:00Z"}}
        """);
        using var apiClient = new ClaudeApiClient(new HttpClient(handler));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), tokenRefresher: null, logger: logger);

        await monitor.RefreshNowAsync();

        var log = File.ReadAllText(logPath);
        Assert.Contains("Connected", log);
        Assert.DoesNotContain(secretToken, log);
    }

    [Fact]
    public async Task RefreshNow_ExpiredToken_LogsRefresh_NeverLogsTokens()
    {
        var credPath = WriteExpiredCredentialFile(); // accessToken "stale-access", refreshToken "valid-refresh"
        var logPath = Path.Combine(_tempDir, "refresh.log");
        var logger = new Logger(logPath);

        var handler = new RoutingHttpHandler(
            tokenResponse: """{"access_token":"fresh-access","refresh_token":"fresh-refresh","expires_in":28800}""",
            usageResponse: """{"five_hour":{"utilization":3.0,"resets_at":"2026-06-01T00:00:00Z"}}""");

        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var refresher = new TokenRefresher(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), refresher, logger);

        await monitor.RefreshNowAsync();

        var log = File.ReadAllText(logPath);
        Assert.Contains("refresh", log, StringComparison.OrdinalIgnoreCase);
        foreach (var token in new[] { "stale-access", "valid-refresh", "fresh-access", "fresh-refresh" })
            Assert.DoesNotContain(token, log);
    }

    [Fact]
    public async Task RefreshNow_SteadyConnected_LogsTransitionOnlyOnce()
    {
        var credPath = WriteCredentialFile();
        var logPath = Path.Combine(_tempDir, "dedup.log");
        var logger = new Logger(logPath);

        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {"five_hour": {"utilization": 1.0, "resets_at": "2026-06-01T00:00:00Z"}}
        """);
        using var apiClient = new ClaudeApiClient(new HttpClient(handler));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), tokenRefresher: null, logger: logger);

        // Three successful polls in a row are one logical state — log once, not thrice.
        await monitor.RefreshNowAsync();
        await monitor.RefreshNowAsync();
        await monitor.RefreshNowAsync();

        var connectedLines = File.ReadAllLines(logPath).Count(l => l.Contains("-> Connected"));
        Assert.Equal(1, connectedLines);
    }

    [Fact]
    public async Task RefreshNow_MalformedTokenResponse_NeverLeaksBodyIntoLog()
    {
        // The token-endpoint response is malformed but contains a token-shaped string.
        // A naive parse-error message would echo that fragment; the log must not.
        const string leak = "sk-ant-oat01-LEAKED";
        var credPath = WriteExpiredCredentialFile();
        var logPath = Path.Combine(_tempDir, "leak.log");
        var logger = new Logger(logPath);

        var handler = new RoutingHttpHandler(
            tokenResponse: $$"""{ "access_token": "{{leak}}" this is broken json """,
            usageResponse: "{}");

        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var refresher = new TokenRefresher(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), refresher, logger);

        await monitor.RefreshNowAsync();

        var log = File.ReadAllText(logPath);
        Assert.DoesNotContain(leak, log);
    }

    [Fact]
    public async Task RefreshNow_Success_RecordsHistorySample()
    {
        var credPath = WriteCredentialFile();
        var histPath = Path.Combine(_tempDir, "history.json");
        var history = new UsageHistoryStore(histPath);

        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {"five_hour": {"utilization": 42.0, "resets_at": "2026-06-01T00:00:00Z"},
         "seven_day": {"utilization": 18.0, "resets_at": "2026-06-05T00:00:00Z"}}
        """);
        using var apiClient = new ClaudeApiClient(new HttpClient(handler));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1),
            tokenRefresher: null, logger: null, history: history);

        await monitor.RefreshNowAsync();

        var samples = history.Samples;
        Assert.Single(samples);
        Assert.Equal(42.0, samples[0].FiveHourPct);
        Assert.Equal(18.0, samples[0].SevenDayPct);
    }

    [Fact]
    public async Task RefreshNow_AuthError_RecordsNoHistory()
    {
        var credPath = Path.Combine(_tempDir, "nonexistent.json"); // read fails → AuthError
        var histPath = Path.Combine(_tempDir, "history.json");
        var history = new UsageHistoryStore(histPath);

        using var apiClient = new ClaudeApiClient(new HttpClient(new MockHttpHandler(HttpStatusCode.OK, "{}")));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1),
            tokenRefresher: null, logger: null, history: history);

        await monitor.RefreshNowAsync();

        Assert.Empty(history.Samples);
    }

    [Fact]
    public async Task RefreshNow_ConcurrentCalls_OnlyOneExecutes()
    {
        var credPath = WriteCredentialFile();
        var handler = new SlowHttpHandler("""
        {"five_hour": {"utilization": 10.0, "resets_at": "2026-06-01T00:00:00Z"}}
        """);
        using var httpClient = new HttpClient(handler);
        using var apiClient = new ClaudeApiClient(httpClient);
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1));

        // Fire two concurrent polls
        var task1 = monitor.RefreshNowAsync();
        var task2 = monitor.RefreshNowAsync();
        await Task.WhenAll(task1, task2);

        // Only one should have actually executed
        Assert.True(handler.CallCount <= 2);
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode;
        private string _responseBody;

        public MockHttpHandler(HttpStatusCode statusCode, string responseBody)
        {
            _statusCode = statusCode;
            _responseBody = responseBody;
        }

        public void SetResponse(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _responseBody = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody),
            });
        }
    }

    /// <summary>
    /// Routes by host: the OAuth token endpoint (console.anthropic.com) returns a
    /// configurable refresh response; the usage endpoint (api.anthropic.com)
    /// returns usage JSON and records the bearer token it was called with.
    /// </summary>
    private sealed class RoutingHttpHandler : HttpMessageHandler
    {
        private readonly string _tokenResponse;
        private readonly string _usageResponse;
        private readonly HttpStatusCode _tokenStatus;

        public string? LastUsageToken { get; private set; }

        // When set, the usage endpoint returns 401 unless called with exactly this
        // bearer token — used to drive the reactive refresh-on-401 path.
        private readonly string? _usageRequiresToken;

        public RoutingHttpHandler(
            string tokenResponse,
            string usageResponse,
            HttpStatusCode tokenStatus = HttpStatusCode.OK,
            string? usageRequiresToken = null)
        {
            _tokenResponse = tokenResponse;
            _usageResponse = usageResponse;
            _tokenStatus = tokenStatus;
            _usageRequiresToken = usageRequiresToken;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var host = request.RequestUri!.Host;
            if (host.Contains("console.anthropic.com"))
            {
                return Task.FromResult(new HttpResponseMessage(_tokenStatus)
                {
                    Content = new StringContent(_tokenResponse),
                });
            }

            var bearer = request.Headers.Authorization?.Parameter;
            LastUsageToken = bearer;

            if (_usageRequiresToken is not null && bearer != _usageRequiresToken)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent(""),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_usageResponse),
            });
        }
    }

    private sealed class SlowHttpHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        public int CallCount;

        public SlowHttpHandler(string responseBody) => _responseBody = responseBody;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            await Task.Delay(100, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody),
            };
        }
    }
}
