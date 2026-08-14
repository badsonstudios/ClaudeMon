namespace ClaudeMon.Services;

using System.Net.Http;
using System.Text;
using ClaudeMon.Models;

/// <summary>
/// Best-effort push notification via ntfy (https://ntfy.sh, or a self-hosted instance) so a
/// rate-limit alert reaches your phone, not just the Windows tray balloon. A no-op unless a
/// topic is configured (see <see cref="NotificationSettings.PushTopic"/>), and every failure —
/// network, DNS, non-2xx — is logged and swallowed: a push must never throw into the alert path
/// that already showed the balloon, and it must never block it either (see <see cref="Notify"/>).
/// A push still in flight when the notifier is disposed is cancelled rather than left to hang.
/// </summary>
public sealed class PushNotifier : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Logger? _logger;
    /// <summary>Cancelled by <see cref="Dispose"/>, so a POST still in flight at shutdown is
    /// abandoned rather than held onto by a slow or unreachable ntfy host. The fire-and-forget
    /// <see cref="Notify"/> path has no caller token to borrow — the alert path it hangs off is
    /// synchronous — so the notifier supplies its own.</summary>
    private readonly CancellationTokenSource _shutdownCts = new();
    // Not volatile: Notify and Dispose are both driven from the UI thread (TrayApplication).
    private bool _disposed;

    public PushNotifier(Logger? logger = null, HttpClient? httpClient = null)
    {
        _logger = logger;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Fires the push and returns immediately without awaiting it — callers sit on the alert
    /// path that already delivered the desktop balloon, so a slow or unreachable ntfy server
    /// must not delay it. The request is tied to the notifier's lifetime and is cancelled by
    /// <see cref="Dispose"/>.
    /// </summary>
    public void Notify(NotificationSettings settings, string title, string text)
    {
        if (_disposed || string.IsNullOrWhiteSpace(settings.PushTopic))
            return;

        _ = NotifyAsync(settings, title, text, _shutdownCts.Token);
    }

    /// <summary>
    /// Awaitable form, for tests and any caller that wants to observe completion. Such a caller
    /// owns cancellation and passes its own token; the fire-and-forget <see cref="Notify"/> path
    /// has no token to pass, so it supplies the notifier's own shutdown one instead.
    /// </summary>
    public async Task NotifyAsync(
        NotificationSettings settings, string title, string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.PushTopic))
            return;

        try
        {
            var baseUrl = string.IsNullOrWhiteSpace(settings.PushServerUrl)
                ? "https://ntfy.sh"
                : settings.PushServerUrl.TrimEnd('/');

            using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/{settings.PushTopic}")
            {
                Content = new StringContent(text, Encoding.UTF8),
            };
            // ntfy reads the notification title from this header rather than the body.
            request.Headers.TryAddWithoutValidation("Title", title);
            // Set per-request, not on DefaultRequestHeaders: the HttpClient may be the caller's.
            request.Headers.UserAgent.Add(AppUserAgent.Header);

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                _logger?.Warn($"Push notification failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Deliberate — shutdown, or a caller giving up. Not a delivery failure, so it isn't
            // worth a log line; unlike the other clients this one can't rethrow, because the
            // fire-and-forget path would turn that into an unobserved task exception.
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A TaskCanceledException that reaches here is the HttpClient timeout, not us.
            _logger?.Warn($"Push notification error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _shutdownCts.Cancel();
        _shutdownCts.Dispose();
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
