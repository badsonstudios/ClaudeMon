namespace ClaudeMon.Tests;

using System.Net;
using ClaudeMon.Models;
using ClaudeMon.Services;

public class PushNotifierTests : IDisposable
{
    private readonly PushNotifier _notifier;
    private readonly MockHttpHandler _handler;

    public PushNotifierTests()
    {
        _handler = new MockHttpHandler();
        _notifier = new PushNotifier(logger: null, httpClient: new HttpClient(_handler));
    }

    public void Dispose()
    {
        _notifier.Dispose();
        _handler.Dispose();
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

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private HttpStatusCode _statusCode = HttpStatusCode.OK;
        private string _responseBody = "";

        public HttpRequestMessage? LastRequest { get; private set; }

        public void SetResponse(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _responseBody = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseBody),
            };
            return Task.FromResult(response);
        }
    }
}
