using System.Net;
using System.Text;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class OpenAiUpstreamClientTests
{
    private static Provider P(string baseUrl = "https://api.openai.com/v1") => new()
    {
        Name = "openai-main",
        Type = ProviderType.OpenAI,
        BaseUrl = baseUrl,
    };

    private static HttpResponseMessage SseResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
    };

    private sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? AuthScheme,
        string? AuthParam, string? ContentType, byte[] Body);

    private sealed class FixedHandler(HttpResponseMessage response, Action<CapturedRequest> capture)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Copy ngay tại đây — request bị Dispose khi SendAsync xong, đọc sau là ObjectDisposed
            var body = request.Content is null
                ? []
                : request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
            capture(new CapturedRequest(request.Method, request.RequestUri,
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter,
                request.Content?.Headers.ContentType?.ToString(), body));
            return Task.FromResult(response);
        }
    }

    private sealed class FixedFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TrackingContent : HttpContent
    {
        public bool WasRead { get; private set; }

        // Chỉ được gọi khi HttpClient bơm body vào bộ nhớ — ResponseHeadersRead không đụng tới
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            WasRead = true;
            return Task.CompletedTask;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task PostAsync_SendsPostToChatPath_WithBearerAndJsonBody()
    {
        CapturedRequest? captured = null;
        var client = new HttpClient(new FixedHandler(SseResponse(), c => captured = c));
        var sut = new OpenAiUpstreamClient(new FixedFactory(client));
        var body = Encoding.UTF8.GetBytes("""{"model":"m","messages":[]}""");

        await sut.PostAsync(P(), "sk-live", "/v1/chat/completions", body, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        // Base kèm /v1 được canonicalize — không ra /v1/v1 (ProviderUrl)
        Assert.Equal("https://api.openai.com/v1/chat/completions", captured.Uri!.ToString());
        Assert.Equal("Bearer", captured.AuthScheme);
        Assert.Equal("sk-live", captured.AuthParam);
        Assert.StartsWith("application/json", captured.ContentType);
        Assert.Equal(body, captured.Body);
    }

    [Fact]
    public async Task PostAsync_ReturnsUpstreamResponse_WithSseHeaders()
    {
        var client = new HttpClient(new FixedHandler(SseResponse(), _ => { }));
        var sut = new OpenAiUpstreamClient(new FixedFactory(client));

        var response = await sut.PostAsync(P(), "k", "/v1/chat/completions", [], CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("data: [DONE]\n\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PostAsync_WithDefaultCompletionOption_DoesNotBufferUpstreamBody()
    {
        var tracking = new TrackingContent();
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = tracking };
        var client = new HttpClient(new FixedHandler(response, _ => { }));
        var sut = new OpenAiUpstreamClient(new FixedFactory(client));

        await sut.PostAsync(P(), "k", "/v1/chat/completions", [], CancellationToken.None);

        // ResponseContentRead sẽ load body vào bộ nhớ trước khi trả về (SerializeToStreamAsync chạy)
        Assert.False(tracking.WasRead);
    }
}
