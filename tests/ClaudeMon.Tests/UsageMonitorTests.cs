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
