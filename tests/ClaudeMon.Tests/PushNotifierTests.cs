namespace ClaudeMon.Tests;

using System.Net;
using ClaudeMon.Models;
using ClaudeMon.Services;

public class PushNotifierTests : IDisposable
{
    private readonly PushNotifier _notifier;
    private readonly MockHttpHandler _handler;
    private readonly string _tempDir;

    public PushNotifierTests()
    {
        _handler = new MockHttpHandler();
        _notifier = new PushNotifier(logger: null, httpClient: new HttpClient(_handler));
        _tempDir = Path.Combine(Path.GetTempPath(), $"claudemon-push-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        _notifier.Dispose();
        _handler.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static NotificationSettings Settings(string? topic, string? serverUrl = null) => new()
    {
        PushTopic = topic,
        PushServerUrl = serverUrl ?? "https://ntfy.sh",
    };

    [Fact]
    public async Task NotifyAsync_NoTopic_DoesNotSendRequest()
    {
        _handler.SetResponse(HttpStatusCode.OK, "");

        await _notifier.NotifyAsync(Settings(topic: null), "title", "text");

        Assert.Null(_handler.LastRequest);
    }

    [Fact]
    public async Task NotifyAsync_BlankTopic_DoesNotSendRequest()
    {
        _handler.SetResponse(HttpStatusCode.OK, "");

        await _notifier.NotifyAsync(Settings(topic: "   "), "title", "text");

        Assert.Null(_handler.LastRequest);
    }

    [Fact]
    public async Task NotifyAsync_TopicConfigured_PostsToDefaultServer()
    {
        _handler.SetResponse(HttpStatusCode.OK, "");

        await _notifier.NotifyAsync(Settings(topic: "my-topic"), "Almost Out", "5-hour usage at 92%");

        Assert.NotNull(_handler.LastRequest);
        Assert.Equal(HttpMethod.Post, _handler.LastRequest.Method);
        Assert.Equal("https://ntfy.sh/my-topic", _handler.LastRequest.RequestUri?.ToString());
        Assert.Equal("Almost Out", _handler.LastRequest.Headers.GetValues("Title").Single());
    }

    [Fact]
    public async Task NotifyAsync_CustomServerUrl_PostsToConfiguredServer()
    {
        _handler.SetResponse(HttpStatusCode.OK, "");

        await _notifier.NotifyAsync(
            Settings(topic: "my-topic", serverUrl: "https://ntfy.example.com/"), "title", "text");

        Assert.Equal("https://ntfy.example.com/my-topic", _handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task NotifyAsync_ServerError_DoesNotThrow()
    {
        _handler.SetResponse(HttpStatusCode.InternalServerError, "");

        await _notifier.NotifyAsync(Settings(topic: "my-topic"), "title", "text");

        // No assertion beyond "didn't throw" — failures are logged and swallowed by design.
    }

    [Fact]
    public async Task NotifyAsync_TransportFails_SwallowsAndLogsTheError()
    {
        // The caller is on the alert path that already showed the desktop balloon; a dead ntfy
        // host must leave a breadcrumb, not an exception.
        var logger = new Logger(Path.Combine(_tempDir, "push-logs"));
        using var handler = new ThrowingHttpHandler(new HttpRequestException("no such host"));
        using var notifier = new PushNotifier(logger, new HttpClient(handler));

        await notifier.NotifyAsync(Settings(topic: "my-topic"), "title", "text");

        var log = File.ReadAllText(logger.FilePath);
        Assert.Contains("[WARN]", log);
        Assert.Contains("Push notification error", log);
    }

    [Fact]
    public void Notify_NoTopic_SendsNothing()
    {
        // The synchronous guard: no topic configured means the feature is off, and the
        // fire-and-forget task must never even be started.
        _handler.SetResponse(HttpStatusCode.OK, "");

        _notifier.Notify(Settings(topic: null), "title", "text");

        Assert.Null(_handler.LastRequest);
    }

    [Fact]
    public async Task Notify_TopicConfigured_PostsToTheConfiguredTopic()
    {
        // Notify returns before the POST completes on purpose — the alert path must not wait
        // on a slow ntfy server — so the request is observed through the handler instead.
        _handler.SetResponse(HttpStatusCode.OK, "");

        _notifier.Notify(Settings(topic: "my-topic"), "Almost Out", "5-hour usage at 92%");

        var request = await _handler.Requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("https://ntfy.sh/my-topic", request.RequestUri?.ToString());
        Assert.Equal("Almost Out", request.Headers.GetValues("Title").Single());
    }

    [Fact]
    public void Dispose_OwnedHttpClient_IsIdempotent()
    {
        // The parameterless form creates — and therefore owns — its own HttpClient.
        var owning = new PushNotifier();

        owning.Dispose();
        owning.Dispose();
    }

    [Fact]
    public async Task Dispose_LeavesACallerSuppliedHttpClientUsable()
    {
        using var handler = new MockHttpHandler();
        using var client = new HttpClient(handler);
        handler.SetResponse(HttpStatusCode.OK, "");

        new PushNotifier(logger: null, httpClient: client).Dispose();

        // A client the notifier didn't create isn't its to dispose: it's shared with the rest
        // of the app, so tearing the notifier down must leave it working.
        using var response = await client.GetAsync("https://ntfy.sh/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _responseBody = "";

        public HttpRequestMessage? LastRequest { get; private set; }

        /// <summary>Completes with the first request seen — the only way to observe the
        /// fire-and-forget <see cref="PushNotifier.Notify"/> path.</summary>
        public TaskCompletionSource<HttpRequestMessage> Requested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetResponse(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _responseBody = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            Requested.TrySetResult(request);
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody),
            };
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHttpHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}
