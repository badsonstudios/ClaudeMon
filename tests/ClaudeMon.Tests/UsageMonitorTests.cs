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
        var logger = new Logger(Path.Combine(_tempDir, "monitor-logs"));

        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {"five_hour": {"utilization": 5.0, "resets_at": "2026-06-01T00:00:00Z"}}
        """);
        using var apiClient = new ClaudeApiClient(new HttpClient(handler));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), tokenRefresher: null, logger: logger);

        await monitor.RefreshNowAsync();

        var log = File.ReadAllText(logger.FilePath);
        Assert.Contains("Connected", log);
        Assert.DoesNotContain(secretToken, log);
    }

    [Fact]
    public async Task RefreshNow_ExpiredToken_LogsRefresh_NeverLogsTokens()
    {
        var credPath = WriteExpiredCredentialFile(); // accessToken "stale-access", refreshToken "valid-refresh"
        var logger = new Logger(Path.Combine(_tempDir, "refresh-logs"));

        var handler = new RoutingHttpHandler(
            tokenResponse: """{"access_token":"fresh-access","refresh_token":"fresh-refresh","expires_in":28800}""",
            usageResponse: """{"five_hour":{"utilization":3.0,"resets_at":"2026-06-01T00:00:00Z"}}""");

        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var refresher = new TokenRefresher(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), refresher, logger);

        await monitor.RefreshNowAsync();

        var log = File.ReadAllText(logger.FilePath);
        Assert.Contains("refresh", log, StringComparison.OrdinalIgnoreCase);
        foreach (var token in new[] { "stale-access", "valid-refresh", "fresh-access", "fresh-refresh" })
            Assert.DoesNotContain(token, log);
    }

    [Fact]
    public async Task RefreshNow_SteadyConnected_LogsTransitionOnlyOnce()
    {
        var credPath = WriteCredentialFile();
        var logger = new Logger(Path.Combine(_tempDir, "dedup-logs"));

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

        var connectedLines = File.ReadAllLines(logger.FilePath).Count(l => l.Contains("-> Connected"));
        Assert.Equal(1, connectedLines);
    }

    [Fact]
    public async Task RefreshNow_MalformedTokenResponse_NeverLeaksBodyIntoLog()
    {
        // The token-endpoint response is malformed but contains a token-shaped string.
        // A naive parse-error message would echo that fragment; the log must not.
        const string leak = "sk-ant-oat01-LEAKED";
        var credPath = WriteExpiredCredentialFile();
        var logger = new Logger(Path.Combine(_tempDir, "leak-logs"));

        var handler = new RoutingHttpHandler(
            tokenResponse: $$"""{ "access_token": "{{leak}}" this is broken json """,
            usageResponse: "{}");

        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var refresher = new TokenRefresher(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), refresher, logger);

        await monitor.RefreshNowAsync();

        var log = File.ReadAllText(logger.FilePath);
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

    [Fact]
    public async Task RefreshNow_UnknownLimitKind_LoggedOncePerRun()
    {
        var credPath = WriteCredentialFile();
        var logDir = Path.Combine(_tempDir, "logs");
        var logger = new Logger(logDir);
        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {
            "five_hour": {"utilization": 10.0, "resets_at": "2026-06-01T00:00:00Z"},
            "limits": [
                {"kind": "seven_day_cowork", "group": "seven_day", "percent": 5.0, "severity": "normal", "resets_at": "2026-06-05T00:00:00Z"}
            ]
        }
        """);
        using var httpClient = new HttpClient(handler);
        using var apiClient = new ClaudeApiClient(httpClient);
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), logger: logger);

        // Two successful polls with the same unknown kind → exactly one log line for it.
        await monitor.RefreshNowAsync();
        await monitor.RefreshNowAsync();

        var log = File.ReadAllText(logger.FilePath);
        var occurrences = log.Split("seven_day_cowork").Length - 1;
        Assert.Equal(1, occurrences);
    }

    [Fact]
    public async Task Resume_TriggersImmediatePoll()
    {
        var credPath = WriteCredentialFile();
        var handler = new MockHttpHandler(HttpStatusCode.OK, """
        {"five_hour": {"utilization": 55.0, "resets_at": "2026-06-01T00:00:00Z"}}
        """);
        using var apiClient = new ClaudeApiClient(new HttpClient(handler));
        // Hour-long interval: any update that arrives must come from Resume's
        // immediate poll, not a timer tick.
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1));

        var updated = new TaskCompletionSource<UsageUpdatedEventArgs>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.UsageUpdated += (_, args) => updated.TrySetResult(args);

        monitor.Resume();

        var args = await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MonitorStatus.Connected, args.Status);
        Assert.Equal(55.0, monitor.LastUsage?.FiveHour?.UtilizationPct);
    }

    [Fact]
    public async Task Pause_StopsTimerPolls()
    {
        var credPath = WriteCredentialFile();
        var handler = new CountingHttpHandler("""
        {"five_hour": {"utilization": 5.0, "resets_at": "2026-06-01T00:00:00Z"}}
        """);
        using var apiClient = new ClaudeApiClient(new HttpClient(handler));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromMilliseconds(30));

        monitor.Start();

        // Wait until the timer has demonstrably ticked at least once past the
        // initial Start() poll, then pause.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (handler.CallCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(handler.CallCount >= 2, "timer never ticked");

        monitor.Pause();
        // Ticks queued before Pause can still land arbitrarily late on a starved
        // runner (Timer.Stop doesn't wait for dispatched Elapsed callbacks), so
        // wait for the count to hold still across consecutive samples before
        // asserting silence over several would-be intervals.
        var countAfterPause = handler.CallCount;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            var current = handler.CallCount;
            if (current == countAfterPause) break;
            countAfterPause = current;
        }
        await Task.Delay(300);

        Assert.Equal(countAfterPause, handler.CallCount);
    }

    [Fact]
    public async Task Pause_DuringInFlightPoll_PollCompletesWithoutFault()
    {
        var credPath = WriteCredentialFile();
        var handler = new GatedHttpHandler("""
        {"five_hour": {"utilization": 21.0, "resets_at": "2026-06-01T00:00:00Z"}}
        """);
        using var apiClient = new ClaudeApiClient(new HttpClient(handler));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1));

        // Start a poll and hold it at the API call, then pause mid-flight
        // (the lock-while-polling case).
        var poll = monitor.RefreshNowAsync();
        await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.Pause();
        handler.Release.TrySetResult();
        await poll.WaitAsync(TimeSpan.FromSeconds(5));

        // The in-flight poll drained normally and its result still landed.
        Assert.Equal(MonitorStatus.Connected, monitor.Status);
        Assert.Equal(21.0, monitor.LastUsage?.FiveHour?.UtilizationPct);
    }

    // --- Anthropic service status, piggybacked on the usage poll (issue #132) ---

    private const string UsageBody =
        """{"five_hour": {"utilization": 9.0, "resets_at": "2026-06-01T00:00:00Z"}}""";

    private static string StatusBody(string indicator, string description) =>
        $$$"""{"page":{"id":"tymt9n04zgry"},"status":{"indicator":"{{{indicator}}}","description":"{{{description}}}"}}""";

    [Fact]
    public async Task RefreshNow_FetchesServiceStatus_AndRaisesEventOnChangeOnly()
    {
        var credPath = WriteCredentialFile();
        var handler = new StatusRoutingHttpHandler(UsageBody, StatusBody("major", "Partial System Outage"));
        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var statusClient = new ServiceStatusClient(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1),
            tokenRefresher: null, logger: null, history: null, serviceStatus: statusClient);

        var events = new List<ServiceStatusUpdatedEventArgs>();
        monitor.ServiceStatusUpdated += (_, args) => events.Add(args);

        await monitor.RefreshNowAsync();
        await monitor.RefreshNowAsync(); // same status — must not re-announce

        Assert.Equal(ServiceStatusLevel.Major, monitor.LastServiceStatus?.Level);
        Assert.Equal("Partial System Outage", monitor.LastServiceStatus?.Description);
        var evt = Assert.Single(events);
        Assert.Equal(ServiceStatusLevel.Major, evt.Current.Level);
        // No second timer: the status came along on the usage poll's own cadence.
        Assert.Equal(2, handler.StatusCallCount);
    }

    [Fact]
    public async Task RefreshNow_ServiceStatusChanges_RaisesTheEventWithTheNewStatus()
    {
        var credPath = WriteCredentialFile();
        var handler = new StatusRoutingHttpHandler(UsageBody, StatusBody("none", "All Systems Operational"));
        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var statusClient = new ServiceStatusClient(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1),
            tokenRefresher: null, logger: null, history: null, serviceStatus: statusClient);

        ServiceStatusUpdatedEventArgs? last = null;
        monitor.ServiceStatusUpdated += (_, args) => last = args;

        await monitor.RefreshNowAsync();
        handler.SetStatusBody(StatusBody("critical", "Major Service Outage"));
        await monitor.RefreshNowAsync();

        // The event carries only the new status (#150) — whether to notify is decided against
        // the persisted latch, not against the reading before this one.
        Assert.Equal(ServiceStatusLevel.Critical, last?.Current.Level);
    }

    [Fact]
    public async Task RefreshNow_ServiceStatusFetchFails_KeepsLastKnownAndStaysSilent()
    {
        var credPath = WriteCredentialFile();
        var handler = new StatusRoutingHttpHandler(UsageBody, StatusBody("minor", "Partially Degraded Service"));
        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var statusClient = new ServiceStatusClient(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1),
            tokenRefresher: null, logger: null, history: null, serviceStatus: statusClient);

        var eventCount = 0;
        monitor.ServiceStatusUpdated += (_, _) => eventCount++;

        await monitor.RefreshNowAsync();
        handler.StatusStatusCode = HttpStatusCode.InternalServerError;
        await monitor.RefreshNowAsync();

        // The status page being unreachable is not itself an alert, and it must not flap the
        // flyout line off — the usage poll still succeeded.
        Assert.Equal(ServiceStatusLevel.Minor, monitor.LastServiceStatus?.Level);
        Assert.Equal(1, eventCount);
        Assert.Equal(MonitorStatus.Connected, monitor.Status);
        Assert.Null(monitor.LastError);
    }

    [Fact]
    public async Task RefreshNow_MissingCredentials_StillFetchesServiceStatus()
    {
        // "Is it me or is it down?" matters most on a poll that can't reach the usage API.
        var credPath = Path.Combine(_tempDir, "nonexistent.json");
        var handler = new StatusRoutingHttpHandler(UsageBody, StatusBody("major", "Partial System Outage"));
        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var statusClient = new ServiceStatusClient(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1),
            tokenRefresher: null, logger: null, history: null, serviceStatus: statusClient);

        await monitor.RefreshNowAsync();

        Assert.Equal(MonitorStatus.AuthError, monitor.Status);
        Assert.Equal(ServiceStatusLevel.Major, monitor.LastServiceStatus?.Level);
    }

    [Fact]
    public async Task RefreshNow_NoServiceStatusClient_LeavesStatusUnknown()
    {
        var credPath = WriteCredentialFile();
        using var apiClient = new ClaudeApiClient(
            new HttpClient(new MockHttpHandler(HttpStatusCode.OK, UsageBody)));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1));

        await monitor.RefreshNowAsync();

        Assert.Null(monitor.LastServiceStatus);
        Assert.Equal(MonitorStatus.Connected, monitor.Status);
    }

    [Fact]
    public async Task RefreshNow_ServiceStatusChange_IsLoggedOncePerChange()
    {
        var credPath = WriteCredentialFile();
        var logger = new Logger(Path.Combine(_tempDir, "status-logs"));
        var handler = new StatusRoutingHttpHandler(UsageBody, StatusBody("major", "Partial System Outage"));
        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var statusClient = new ServiceStatusClient(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1),
            tokenRefresher: null, logger: logger, history: null, serviceStatus: statusClient);

        await monitor.RefreshNowAsync();
        await monitor.RefreshNowAsync();

        var lines = File.ReadAllLines(logger.FilePath)
            .Count(l => l.Contains("Anthropic service status"));
        Assert.Equal(1, lines);
    }

    /// <summary>
    /// Routes by host: the status page (status.claude.com) returns a configurable statuspage
    /// body, everything else returns usage JSON.
    /// </summary>
    private sealed class StatusRoutingHttpHandler : HttpMessageHandler
    {
        private readonly string _usageBody;
        private string _statusBody;
        private int _statusCallCount;

        public HttpStatusCode StatusStatusCode { get; set; } = HttpStatusCode.OK;
        public int StatusCallCount => Volatile.Read(ref _statusCallCount);

        public StatusRoutingHttpHandler(string usageBody, string statusBody)
        {
            _usageBody = usageBody;
            _statusBody = statusBody;
        }

        public void SetStatusBody(string statusBody) => _statusBody = statusBody;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host.Contains("status."))
            {
                Interlocked.Increment(ref _statusCallCount);
                return Task.FromResult(new HttpResponseMessage(StatusStatusCode)
                {
                    Content = new StringContent(_statusBody),
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_usageBody),
            });
        }
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

    // --- Fire-and-forget and notification error paths (issue #103) ---

    [Fact]
    public async Task Start_PollThrowsUnexpectedly_IsSwallowedAndLogged()
    {
        // PollAsync runs unawaited from the timer and from Start/Resume. Anything it doesn't
        // swallow itself would escape as an unobserved task exception and tear the app down,
        // so SafePollAsync is the last line of defence — and it must leave a breadcrumb.
        var credPath = WriteCredentialFile();
        var logger = new Logger(Path.Combine(_tempDir, "safepoll-logs"));
        var handler = new ThrowingHttpHandler(new InvalidOperationException("handler exploded"));

        using var apiClient = new ClaudeApiClient(new HttpClient(handler));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), tokenRefresher: null, logger: logger);

        monitor.Start();

        var log = await WaitForLogAsync(logger, "Usage poll failed");
        Assert.Contains("Usage poll failed", log);
        Assert.Contains("handler exploded", log);
    }

    [Fact]
    public async Task UpdateInterval_TakesEffectOnTheRunningTimer()
    {
        var credPath = WriteCredentialFile();
        var handler = new CountingHttpHandler(
            """{"five_hour":{"utilization":1.0,"resets_at":"2026-06-01T00:00:00Z"}}""");
        using var apiClient = new ClaudeApiClient(new HttpClient(handler));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1));

        monitor.Start(); // one immediate poll, then nothing for an hour
        monitor.UpdateInterval(TimeSpan.FromMilliseconds(50));

        var deadline = Environment.TickCount64 + 10_000;
        while (handler.CallCount < 3 && Environment.TickCount64 < deadline)
            await Task.Delay(25);

        // Stop before asserting: a 50 ms poll loop still draining into teardown would hold the
        // credentials file open while the fixture deletes the temp tree.
        monitor.Stop();
        await Task.Delay(100); // let the last in-flight poll drain

        Assert.True(handler.CallCount >= 3, $"only {handler.CallCount} polls at the new interval");
    }

    [Fact]
    public async Task RefreshNow_CredentialsUnreadable_NotifiesSubscribersAndLogsAnError()
    {
        // SetError has to reach the UI as well as the log: the tray icon can't be left showing
        // stale-but-green while the app is actually signed out.
        var logger = new Logger(Path.Combine(_tempDir, "autherror-logs"));
        var handler = new MockHttpHandler(HttpStatusCode.OK, "{}");
        using var apiClient = new ClaudeApiClient(new HttpClient(handler));
        using var monitor = new UsageMonitor(
            new CredentialReader(Path.Combine(_tempDir, "no-such-credentials.json")),
            apiClient, TimeSpan.FromHours(1), tokenRefresher: null, logger: logger);

        UsageUpdatedEventArgs? received = null;
        monitor.UsageUpdated += (_, args) => received = args;

        await monitor.RefreshNowAsync();

        Assert.Equal(MonitorStatus.AuthError, monitor.Status);
        Assert.NotNull(received);
        Assert.Equal(MonitorStatus.AuthError, received.Status);
        Assert.NotNull(received.Error);
        // Auth problems are logged at ERROR, not WARN — they need the user to do something.
        Assert.Contains("[ERROR]", File.ReadAllText(logger.FilePath));
    }

    [Fact]
    public async Task RefreshNow_TokenEndpointErrors_NotifiesSubscribersWithOffline()
    {
        // A refresh the server can't complete is "offline", not "signed out": only a 400/401
        // means the refresh token is dead. Anything else must keep the last known state rather
        // than telling the user to re-authenticate over a flaky connection.
        var credPath = WriteExpiredCredentialFile();
        var handler = new MockHttpHandler(HttpStatusCode.InternalServerError, "{}");
        using var apiClient = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));
        using var refresher = new TokenRefresher(new HttpClient(handler, disposeHandler: false));
        using var monitor = new UsageMonitor(
            new CredentialReader(credPath), apiClient, TimeSpan.FromHours(1), refresher);

        UsageUpdatedEventArgs? received = null;
        monitor.UsageUpdated += (_, args) => received = args;

        await monitor.RefreshNowAsync();

        Assert.Equal(MonitorStatus.Offline, monitor.Status);
        Assert.NotNull(received);
        Assert.Equal(MonitorStatus.Offline, received.Status);
        Assert.Contains("retry", received.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> WaitForLogAsync(Logger logger, string expected)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (Environment.TickCount64 < deadline)
        {
            var text = ReadSharing(logger.FilePath);
            if (text.Contains(expected, StringComparison.Ordinal))
                return text;

            await Task.Delay(25);
        }

        return ReadSharing(logger.FilePath);
    }

    /// <summary>
    /// Reads with FileShare.ReadWrite. File.ReadAllText would deny the writer, and Logger drops
    /// any line it can't append — so a polling reader could silently destroy the very entry it
    /// is waiting for.
    /// </summary>
    private static string ReadSharing(string path)
    {
        if (!File.Exists(path))
            return "";

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Fails the way a transport fault the client doesn't classify would.</summary>
    private sealed class ThrowingHttpHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }

    private sealed class CountingHttpHandler : HttpMessageHandler
    {
        private readonly string _responseBody;
        private int _callCount;

        public CountingHttpHandler(string responseBody) => _responseBody = responseBody;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody),
            });
        }
    }

    /// <summary>
    /// Signals <see cref="Entered"/> when the usage call arrives, then holds the
    /// response until the test sets <see cref="Release"/> — lets a test act (e.g.
    /// Pause) while a poll is verifiably in flight.
    /// </summary>
    private sealed class GatedHttpHandler : HttpMessageHandler
    {
        private readonly string _responseBody;

        public GatedHttpHandler(string responseBody) => _responseBody = responseBody;

        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseBody),
            };
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
