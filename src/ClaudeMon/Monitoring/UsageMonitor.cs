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
    private readonly LimitLogRecorder? _limitLog;
    private readonly ServiceStatusClient? _serviceStatus;
    private readonly System.Timers.Timer _timer;
    private readonly object _lock = new();
    private bool _polling;
    private MonitorStatus _loggedStatus = MonitorStatus.Initializing;
    private readonly HashSet<string> _loggedUnknownLimitKinds = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;

    // A refreshed credential whose write-back failed (issue #192). Refresh tokens rotate, so
    // that refresh consumed the on-disk token: until the write lands, the file holds a dead
    // token and this pair is the only live lineage. _pendingDiskToken is the consumed token
    // the file still holds — the marker that disk is stale, and the write-back guard's
    // reference point. Process-lifetime only (tokens are never persisted anywhere but the
    // credentials file), so a restart inside this window still loses the lineage — the
    // accepted residual risk. Touched only inside PollAsync, which is serialized by _polling.
    private OAuthCredential? _pendingWriteBack;
    private string? _pendingDiskToken;
    // The stuck-episode breadcrumb has been logged: one line per episode, not per poll (the
    // app's transition-logging convention) — a lock can persist for days at the poll cadence.
    private bool _pendingEpisodeLogged;

    public UsageResponse? LastUsage { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastUpdated { get; private set; }
    public MonitorStatus Status { get; private set; } = MonitorStatus.Initializing;

    /// <summary>
    /// The last known Anthropic service status, or null while none has been fetched. A failed
    /// fetch leaves the previous value in place (like <see cref="LastUsage"/> on a failed poll)
    /// rather than flapping the flyout line off and on.
    /// </summary>
    public ServiceStatus? LastServiceStatus { get; private set; }

    public event EventHandler<UsageUpdatedEventArgs>? UsageUpdated;

    /// <summary>Raised only when the service status actually changes, never on an unchanged poll.</summary>
    public event EventHandler<ServiceStatusUpdatedEventArgs>? ServiceStatusUpdated;

    public UsageMonitor(
        CredentialReader credentialReader,
        ClaudeApiClient apiClient,
        TimeSpan pollInterval,
        TokenRefresher? tokenRefresher = null,
        Logger? logger = null,
        UsageHistoryStore? history = null,
        ServiceStatusClient? serviceStatus = null,
        LimitLogRecorder? limitLog = null)
    {
        _credentialReader = credentialReader;
        _apiClient = apiClient;
        _tokenRefresher = tokenRefresher;
        _logger = logger;
        _history = history;
        _limitLog = limitLog;
        _serviceStatus = serviceStatus;
        _timer = new System.Timers.Timer(pollInterval.TotalMilliseconds);
        // The poll runs unawaited on a timer thread-pool thread: any exception PollAsync
        // doesn't swallow would otherwise escape unobserved and tear the whole app down.
        // SafePollAsync guards it (mirrors TrayApplication.CheckForUpdatesAsync).
        _timer.Elapsed += (_, _) => _ = SafePollAsync();
        _timer.AutoReset = true;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _timer.Start();
        _ = SafePollAsync();
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

    /// <summary>
    /// Stops the poll timer while the workstation is locked. Deliberately does
    /// not cancel an in-flight poll: a poll that started while unlocked is
    /// still valid data, and letting it drain is the simplest non-faulting
    /// behavior. Counterpart of <see cref="Resume"/>; distinct from
    /// <see cref="Stop"/>, which is shutdown and cancels outstanding work.
    /// </summary>
    public void Pause()
    {
        _timer.Stop();
    }

    /// <summary>
    /// Restarts the poll timer and fires an immediate poll so the readout is
    /// fresh within seconds of unlock rather than a full interval later. If a
    /// paused-era poll is somehow still draining, the re-entrancy guard in
    /// PollAsync makes the immediate poll a no-op and the timer catches up.
    /// </summary>
    public void Resume()
    {
        _timer.Start();
        _ = SafePollAsync();
    }

    // Fire-and-forget wrapper for polls started outside the timer: PollAsync runs
    // unawaited, so any exception it doesn't swallow would escape unobserved.
    private async Task SafePollAsync()
    {
        try
        {
            await PollAsync();
        }
        catch (Exception ex)
        {
            _logger?.Error($"Usage poll failed: {ex.Message}");
        }
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

        // The service-status fetch, once started (see below). Declared out here so the finally
        // can join it whichever way the usage path exits.
        Task? statusTask = null;

        try
        {
            var token = _cts?.Token ?? CancellationToken.None;

            // Started alongside the usage work rather than in front of it: a slow status page
            // must never be what makes a poll (or "Refresh Now", or the unlock refresh) feel
            // slow — least of all during an outage, when it's most likely to be slow. Because
            // it's kicked off before the credential read, it still runs on polls that never
            // reach the usage API at all — which is exactly when the user asks whether it's
            // them or Anthropic. Joined in the finally below, so every early return observes it.
            statusTask = PollServiceStatusAsync(token);

            var credResult = _credentialReader.Read();
            OAuthCredential credential;
            if (credResult.IsSuccess)
            {
                credential = AdoptPendingWriteBack(credResult.Credential!);
            }
            else if (_pendingWriteBack is not null)
            {
                // The same lock that failed the write-back can fail this read (issue #192):
                // while a pending lineage exists, the in-memory credential is the live one —
                // serve the poll from it rather than flapping to auth-error. The retry write
                // needs the file readable again anyway, so it just waits for a later poll.
                if (!_pendingEpisodeLogged)
                {
                    _logger?.Warn("Credentials file unreadable; continuing on the refreshed sign-in held in memory.");
                    _pendingEpisodeLogged = true;
                }

                credential = _pendingWriteBack;
            }
            else
            {
                SetError(credResult.Error!, MonitorStatus.AuthError);
                return;
            }
            // The refresh token the file itself holds this poll — what WriteBack must compare
            // against. Distinct from credential.RefreshToken while a write-back is pending:
            // refreshes then run off the in-memory lineage, but the file still holds the
            // older token it will be updated from.
            var diskRefreshToken = _pendingDiskToken ?? credential.RefreshToken;
            var canRefresh = _tokenRefresher is not null && credential.HasRefreshToken;
            var refreshedThisPoll = false;

            // Proactive refresh: if the on-disk token is expired (or about to be),
            // try to renew it ourselves before spending the poll on a doomed call.
            if (credential.WillExpireWithin(RefreshSkew))
            {
                if (canRefresh)
                {
                    var (refreshed, outcome) = await TryRefreshAsync(credential, diskRefreshToken, token);
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
                var (refreshed, outcome) = await TryRefreshAsync(credential, diskRefreshToken, token);
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
                LogUnknownLimitKinds(apiResult.Data!);
                RecordHistory(apiResult.Data!);
                // The correlated limit log (issue #184): one sample per successful poll —
                // success-path only, so it adds zero API traffic. Record never throws.
                _limitLog?.Record(apiResult.Data!);

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
            // PollServiceStatusAsync never throws, so this can't mask the usage path's outcome
            // — it only makes sure the fetch is observed and finished before the poll is
            // considered done (which also stops two status fetches ever overlapping).
            if (statusTask is not null)
                await statusTask;

            lock (_lock) { _polling = false; }
        }
    }

    /// <summary>
    /// Fetches the Anthropic service status, piggybacking the usage poll: no second timer, and
    /// it pauses with the poll timer while the workstation is locked. Failures are silent and
    /// leave the last known status alone — the status page being unreachable is not an alert.
    /// </summary>
    /// <remarks>
    /// Never throws. A secondary signal must not be able to break the primary one: this runs
    /// concurrently with the usage poll and is awaited in its finally, so anything escaping here
    /// — including the shutdown race where the client is disposed mid-fetch — would surface as a
    /// lost usage update (or, on the RefreshNowAsync path, an unhandled UI-thread exception).
    /// </remarks>
    private async Task PollServiceStatusAsync(CancellationToken token)
    {
        if (_serviceStatus is null)
            return;

        try
        {
            var status = await _serviceStatus.GetStatusAsync(token);
            if (status is null)
                return;

            var previous = LastServiceStatus;
            LastServiceStatus = status;

            // Raise only on an actual change, so the log and the subscribers see one event per
            // transition rather than one per poll. This is a chattiness filter, not the notify
            // decision — that is the latch's job, and it is deliberately not told what the last
            // reading was.
            if (previous == status)
                return;

            LogServiceStatus(status);
            ServiceStatusUpdated?.Invoke(this, new ServiceStatusUpdatedEventArgs(status));
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested — nothing to report.
        }
        catch (Exception ex)
        {
            _logger?.Warn($"Service status check failed: {ex.Message}");
        }
    }

    // One line per actual change, so a multi-hour incident doesn't fill the log.
    private void LogServiceStatus(ServiceStatus status)
    {
        if (_logger is null)
            return;

        var entry = $"Anthropic service status -> {status.Level}: {status.Description}";
        if (status.IsOperational)
            _logger.Info(entry);
        else
            _logger.Warn(entry);
    }

    /// <summary>
    /// Refreshes <paramref name="credential"/> (callers ensure a refresher and a
    /// refresh token are present), writing a successful result back to the
    /// credentials file so the CLI/extension benefit too. Returns the (possibly
    /// new) credential and a classified outcome.
    /// <para>
    /// <paramref name="diskRefreshToken"/> is the refresh token the file held when this
    /// poll read it — not necessarily the one being refreshed from, since a pending
    /// failed write-back means refreshing from the in-memory lineage while the file
    /// still holds its consumed ancestor. WriteBack compares against the file's actual
    /// content, so this is the token that keeps the no-clobber guard honest.
    /// </para>
    /// </summary>
    private async Task<(OAuthCredential? Credential, RefreshOutcome Outcome)> TryRefreshAsync(
        OAuthCredential credential, string? diskRefreshToken, CancellationToken token)
    {
        _logger?.Info("Access token expired or near expiry — attempting refresh.");
        var result = await _tokenRefresher!.RefreshAsync(credential, token);

        if (result.IsSuccess)
        {
            // The refreshed token still serves this poll from memory regardless of whether it
            // persisted — but persisting is not optional: the refresh consumed the previous
            // token, so until the write lands the file holds a dead one (issue #192).
            var outcome = _credentialReader.WriteBack(result.Credential!, diskRefreshToken);
            switch (outcome)
            {
                case WriteBackOutcome.Written:
                    _logger?.Info("Access token refreshed.");
                    ClearPendingWriteBack();
                    break;
                case WriteBackOutcome.SupersededByAnotherClient:
                    _logger?.Info("Access token refreshed; another client already rotated the on-disk token, so left it in place.");
                    ClearPendingWriteBack();
                    break;
                case WriteBackOutcome.Failed:
                    _logger?.Warn("Access token refreshed but could not be written back to the credentials file — keeping it in memory and retrying next poll.");
                    _pendingWriteBack = result.Credential;
                    _pendingDiskToken = diskRefreshToken;
                    break;
            }

            return (result.Credential, RefreshOutcome.Refreshed);
        }

        if (result.IsSignInExpired)
            _logger?.Warn("Token refresh rejected — sign-in expired.");
        else
            _logger?.Warn($"Token refresh failed (transient): {result.Error}");

        return (null, result.IsSignInExpired ? RefreshOutcome.SignInExpired : RefreshOutcome.Transient);
    }

    /// <summary>
    /// Reconciles the freshly-read on-disk credential with a pending failed write-back
    /// (issue #192). While the file still holds exactly the token our refresh consumed, the
    /// on-disk lineage is dead — re-adopting it would sign every client out — so the
    /// in-memory credential serves the poll and the write is retried until it lands. The
    /// moment the file holds anything else (the retry landed, or the user signed in afresh),
    /// the file wins again.
    /// </summary>
    private OAuthCredential AdoptPendingWriteBack(OAuthCredential disk)
    {
        if (_pendingWriteBack is null)
            return disk;

        if (disk.RefreshToken != _pendingDiskToken)
        {
            ClearPendingWriteBack();
            return disk;
        }

        var pending = _pendingWriteBack;
        switch (_credentialReader.WriteBack(pending, _pendingDiskToken))
        {
            case WriteBackOutcome.Written:
                _logger?.Info("Refreshed sign-in written back to the credentials file after an earlier failure.");
                ClearPendingWriteBack();
                break;
            case WriteBackOutcome.SupersededByAnotherClient:
                // A new lineage landed between the read and this write: the file wins from
                // the next poll on; the in-memory token still validly serves this one.
                _logger?.Info("Pending refreshed sign-in superseded on disk by another client.");
                ClearPendingWriteBack();
                break;
            case WriteBackOutcome.Failed:
                if (!_pendingEpisodeLogged)
                {
                    _logger?.Warn("Credentials file still holds the superseded refresh token and could not be updated; using the refreshed sign-in from memory and retrying every poll.");
                    _pendingEpisodeLogged = true;
                }

                break;
        }

        return pending;
    }

    private void ClearPendingWriteBack()
    {
        _pendingWriteBack = null;
        _pendingDiskToken = null;
        _pendingEpisodeLogged = false;
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

    // Logs each unrecognized limits[] kind once per app run — a breadcrumb that the API grew a
    // new bucket type (rendered generically until the code learns it), without spamming every poll.
    private void LogUnknownLimitKinds(UsageResponse usage)
    {
        if (_logger is null)
            return;

        foreach (var kind in LimitDisplay.UnknownKinds(usage))
        {
            if (_loggedUnknownLimitKinds.Add(kind))
                _logger.Info($"Usage API returned unrecognized limit kind '{kind}'; rendering it generically.");
        }
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

/// <summary>
/// A change in the Anthropic service status. Only the new status is carried: whether to notify
/// is decided against the <em>persisted</em> latch (<see cref="AppSettings.ServiceIncidentLevel"/>,
/// see <see cref="ServiceStatusAlerts"/>), not against the previous reading, so a "previous"
/// here would be a second, restart-amnesiac answer to a question already answered elsewhere —
/// exactly the bug #138 fixed. The raiser still compares against the last reading to decide
/// whether anything changed at all, but that is its own business (#150).
/// </summary>
public record ServiceStatusUpdatedEventArgs(
    ServiceStatus Current
);
