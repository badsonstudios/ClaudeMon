namespace ClaudeMon.Monitoring;

using ClaudeMon.Models;
using ClaudeMon.Services;

public sealed class UsageMonitor : IDisposable
{
    // Refresh a little ahead of the hard expiry so a poll never races the cutoff.
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    private readonly CredentialReader _credentialReader;
    private readonly ClaudeApiClient _apiClient;
    private readonly TokenRefresher? _tokenRefresher;
    private readonly Logger? _logger;
    private readonly UsageHistoryStore? _history;
    private readonly System.Timers.Timer _timer;
    private readonly object _lock = new();
    private bool _polling;
    private MonitorStatus _loggedStatus = MonitorStatus.Initializing;
    private CancellationTokenSource? _cts;

    public UsageResponse? LastUsage { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastUpdated { get; private set; }
    public MonitorStatus Status { get; private set; } = MonitorStatus.Initializing;

    public event EventHandler<UsageUpdatedEventArgs>? UsageUpdated;

    public UsageMonitor(
        CredentialReader credentialReader,
        ClaudeApiClient apiClient,
        TimeSpan pollInterval,
        TokenRefresher? tokenRefresher = null,
        Logger? logger = null,
        UsageHistoryStore? history = null)
    {
        _credentialReader = credentialReader;
        _apiClient = apiClient;
        _tokenRefresher = tokenRefresher;
        _logger = logger;
        _history = history;
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
            var credential = credResult.Credential!;
            var canRefresh = _tokenRefresher is not null && credential.HasRefreshToken;
            var refreshedThisPoll = false;

            // Proactive refresh: if the on-disk token is expired (or about to be),
            // try to renew it ourselves before spending the poll on a doomed call.
            if (credential.WillExpireWithin(RefreshSkew))
            {
                if (canRefresh)
                {
                    var (refreshed, outcome) = await TryRefreshAsync(credential, token);
                    switch (outcome)
                    {
                        case RefreshOutcome.Refreshed:
                            credential = refreshed!;
                            refreshedThisPoll = true;
                            break;
                        case RefreshOutcome.SignInExpired:
                            SetError("Sign-in expired. Run 'claude' to re-authenticate.", MonitorStatus.AuthError);
                            return;
                        default: // Transient — couldn't reach the token endpoint.
                            // Treat as offline and keep the last known usage rather
                            // than flapping to auth-error.
                            SetOffline("Could not refresh sign-in (offline?). Will retry.");
                            return;
                    }
                }
                else if (credential.IsExpired)
                {
                    // Genuinely expired and nothing to refresh with — report it.
                    SetError("OAuth token has expired. Run 'claude' to re-authenticate.", MonitorStatus.AuthError);
                    return;
                }
                // Otherwise: still valid for under the skew window and not
                // refreshable — fall through and use it while it lasts.
            }

            var apiResult = await _apiClient.GetUsageAsync(credential.AccessToken, token);

            // Reactive refresh: the token looked valid by its timestamp but the
            // server rejected it. Refresh once and retry before giving up.
            if (apiResult.IsAuthError && !refreshedThisPoll && canRefresh)
            {
                var (refreshed, outcome) = await TryRefreshAsync(credential, token);
                switch (outcome)
                {
                    case RefreshOutcome.Refreshed:
                        credential = refreshed!;
                        apiResult = await _apiClient.GetUsageAsync(credential.AccessToken, token);
                        break;
                    case RefreshOutcome.SignInExpired:
                        SetError("Sign-in expired. Run 'claude' to re-authenticate.", MonitorStatus.AuthError);
                        return;
                    default: // Transient
                        SetOffline("Could not refresh sign-in (offline?). Will retry.");
                        return;
                }
            }

            if (apiResult.IsSuccess)
            {
                LastUsage = apiResult.Data;
                LastError = null;
                LastUpdated = DateTimeOffset.UtcNow;
                Status = MonitorStatus.Connected;
                LogTransition(MonitorStatus.Connected, "usage poll succeeded");
                RecordHistory(apiResult.Data!);

                UsageUpdated?.Invoke(this, new UsageUpdatedEventArgs(
                    apiResult.Data!, null, MonitorStatus.Connected));
            }
            else if (apiResult.IsRateLimited)
            {
                // Keep last known data, just update status
                Status = MonitorStatus.RateLimited;
                LastError = apiResult.ErrorMessage;
                LogTransition(MonitorStatus.RateLimited, apiResult.ErrorMessage ?? "rate limited");

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
                LogTransition(MonitorStatus.Offline, apiResult.ErrorMessage ?? "offline");

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

    /// <summary>
    /// Refreshes <paramref name="credential"/> (callers ensure a refresher and a
    /// refresh token are present), writing a successful result back to the
    /// credentials file so the CLI/extension benefit too. Returns the (possibly
    /// new) credential and a classified outcome.
    /// </summary>
    private async Task<(OAuthCredential? Credential, RefreshOutcome Outcome)> TryRefreshAsync(
        OAuthCredential credential, CancellationToken token)
    {
        _logger?.Info("Access token expired or near expiry — attempting refresh.");
        var result = await _tokenRefresher!.RefreshAsync(credential, token);

        if (result.IsSuccess)
        {
            _credentialReader.WriteBack(result.Credential!);
            _logger?.Info("Access token refreshed.");
            return (result.Credential, RefreshOutcome.Refreshed);
        }

        if (result.IsSignInExpired)
            _logger?.Warn("Token refresh rejected — sign-in expired.");
        else
            _logger?.Warn($"Token refresh failed (transient): {result.Error}");

        return (null, result.IsSignInExpired ? RefreshOutcome.SignInExpired : RefreshOutcome.Transient);
    }

    private enum RefreshOutcome
    {
        Refreshed,
        SignInExpired,
        Transient,
    }

    private void SetError(string error, MonitorStatus status)
    {
        LastError = error;
        Status = status;
        LogTransition(status, error);
        UsageUpdated?.Invoke(this, new UsageUpdatedEventArgs(LastUsage, error, status));
    }

    private void SetOffline(string message)
    {
        Status = MonitorStatus.Offline;
        LastError = message;
        LogTransition(MonitorStatus.Offline, message);
        UsageUpdated?.Invoke(this, new UsageUpdatedEventArgs(LastUsage, message, MonitorStatus.Offline));
    }

    // Records a usage sample for the trend sparkline. Only fresh, successful polls
    // with a 5-hour value contribute (the 5-hour series is what the sparkline draws).
    private void RecordHistory(UsageResponse usage)
    {
        if (_history is null || usage.FiveHour is null)
            return;

        _history.Record(new UsageSample(
            DateTimeOffset.UtcNow,
            usage.FiveHour.UtilizationPct,
            usage.SevenDay?.UtilizationPct));
    }

    // Logs only when the status actually changes, so a steady state (e.g. polling
    // along happily Connected) doesn't fill the log with identical lines.
    private void LogTransition(MonitorStatus status, string detail)
    {
        if (_logger is null || status == _loggedStatus)
            return;

        _loggedStatus = status;
        var entry = $"Status -> {status}: {detail}";
        switch (status)
        {
            case MonitorStatus.AuthError:
                _logger.Error(entry);
                break;
            case MonitorStatus.Offline:
            case MonitorStatus.RateLimited:
                _logger.Warn(entry);
                break;
            default:
                _logger.Info(entry);
                break;
        }
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
