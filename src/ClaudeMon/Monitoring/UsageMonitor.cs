namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;
using ClaudeMon.Services;

public sealed class UsageMonitor : IDisposable
{
    private readonly CredentialReader _credentialReader;
    private readonly ClaudeApiClient _apiClient;
    private readonly System.Timers.Timer _timer;
    private readonly object _lock = new();
    private bool _polling;
    private CancellationTokenSource? _cts;

    public UsageResponse? LastUsage { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastUpdated { get; private set; }
    public MonitorStatus Status { get; private set; } = MonitorStatus.Initializing;

    public event EventHandler<UsageUpdatedEventArgs>? UsageUpdated;

    public UsageMonitor(
        CredentialReader credentialReader,
        ClaudeApiClient apiClient,
        TimeSpan pollInterval)
    {
        _credentialReader = credentialReader;
        _apiClient = apiClient;
        _timer = new System.Timers.Timer(pollInterval.TotalMilliseconds);
        _timer.Elapsed += async (_, _) => await PollAsync();
        _timer.AutoReset = true;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _timer.Start();
        _ = PollAsync();
    }

    public void Stop()
    {
        _timer.Stop();
        _cts?.Cancel();
    }

    public async Task RefreshNowAsync()
    {
        await PollAsync();
    }

    public void UpdateInterval(TimeSpan newInterval)
    {
        _timer.Interval = newInterval.TotalMilliseconds;
    }

    private async Task PollAsync()
    {
        lock (_lock)
        {
            if (_polling) return;
            _polling = true;
        }

        try
        {
            var credResult = _credentialReader.Read();
            if (!credResult.IsSuccess)
            {
                SetError(credResult.Error!, MonitorStatus.AuthError);
                return;
            }

            var token = _cts?.Token ?? CancellationToken.None;
            var apiResult = await _apiClient.GetUsageAsync(credResult.Credential!.AccessToken, token);

            if (apiResult.IsSuccess)
            {
                LastUsage = apiResult.Data;
                LastError = null;
                LastUpdated = DateTimeOffset.UtcNow;
                Status = MonitorStatus.Connected;

                UsageUpdated?.Invoke(this, new UsageUpdatedEventArgs(
                    apiResult.Data!, null, MonitorStatus.Connected));
            }
            else if (apiResult.IsRateLimited)
            {
                // Keep last known data, just update status
                Status = MonitorStatus.RateLimited;
                LastError = apiResult.ErrorMessage;

                UsageUpdated?.Invoke(this, new UsageUpdatedEventArgs(
                    LastUsage, apiResult.ErrorMessage, MonitorStatus.RateLimited));
            }
            else if (apiResult.IsAuthError)
            {
                SetError(apiResult.ErrorMessage!, MonitorStatus.AuthError);
            }
            else
            {
                // Network or other error — keep last known data
                Status = MonitorStatus.Offline;
                LastError = apiResult.ErrorMessage;

                UsageUpdated?.Invoke(this, new UsageUpdatedEventArgs(
                    LastUsage, apiResult.ErrorMessage, MonitorStatus.Offline));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested
        }
        finally
        {
            lock (_lock) { _polling = false; }
        }
    }

    private void SetError(string error, MonitorStatus status)
    {
        LastError = error;
        Status = status;
        UsageUpdated?.Invoke(this, new UsageUpdatedEventArgs(LastUsage, error, status));
    }

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
        _cts?.Dispose();
    }
}

public enum MonitorStatus
{
    Initializing,
    Connected,
    RateLimited,
    AuthError,
    Offline,
}

public record UsageUpdatedEventArgs(
    UsageResponse? Usage,
    string? Error,
    MonitorStatus Status
);
