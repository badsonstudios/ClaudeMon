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
/// </summary>
public sealed class PushNotifier : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly Logger? _logger;

    public PushNotifier(Logger? logger = null, HttpClient? httpClient = null)
    {
        _logger = logger;
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// Fires the push and returns immediately without awaiting it — callers sit on the alert
    /// path that already delivered the desktop balloon, so a slow or unreachable ntfy server
    /// must not delay it.
    /// </summary>
    public void Notify(NotificationSettings settings, string title, string text)
    {
        if (string.IsNullOrWhiteSpace(settings.PushTopic))
            return;

        _ = NotifyAsync(settings, title, text);
    }

    /// <summary>Awaitable form, for tests and any caller that wants to observe completion.</summary>
    public async Task NotifyAsync(NotificationSettings settings, string title, string text)
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

            using var response = await _httpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                _logger?.Warn($"Push notification failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger?.Warn($"Push notification error: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
