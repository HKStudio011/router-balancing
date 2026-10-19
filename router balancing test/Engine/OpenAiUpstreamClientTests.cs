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
        string? AuthParam, string? ContentType, byte[] Body, string? IdempotencyKey);

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
                request.Content?.Headers.ContentType?.ToString(), body,
                request.Headers.TryGetValues("Idempotency-Key", out var keys)
                    ? keys.Single()
                    : null));
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

        await sut.PostAsync(P(), "sk-live", "/v1/chat/completions", body, "req-1",
            CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        // Base kèm /v1 được canonicalize — không ra /v1/v1 (ProviderUrl)
        Assert.Equal("https://api.openai.com/v1/chat/completions", captured.Uri!.ToString());
        Assert.Equal("Bearer", captured.AuthScheme);
        Assert.Equal("sk-live", captured.AuthParam);
        Assert.StartsWith("application/json", captured.ContentType);
        Assert.Equal(body, captured.Body);
        // Idempotency-Key = request id gốc — provider dedupe retry/failover replay (lọt request)
        Assert.Equal("req-1", captured.IdempotencyKey);
    }

    [Fact]
    public async Task PostAsync_ReturnsUpstreamResponse_WithSseHeaders()
    {
        var client = new HttpClient(new FixedHandler(SseResponse(), _ => { }));
        var sut = new OpenAiUpstreamClient(new FixedFactory(client));

        var response = await sut.PostAsync(P(), "k", "/v1/chat/completions", [], "req-2",
            CancellationToken.None);

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

        await sut.PostAsync(P(), "k", "/v1/chat/completions", [], "req-3", CancellationToken.None);

        // ResponseContentRead sẽ load body vào bộ nhớ trước khi trả về (SerializeToStreamAsync chạy)
        Assert.False(tracking.WasRead);
    }

    private const string RenamedBody =
        """{"model":"m","tools":[{"type":"function","function":{"name":"Bash","parameters":{"type":"object"}}}]}""";

    private const string BashCallSse =
        "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\"," +
        "\"function\":{\"name\":\"bash\",\"arguments\":\"{}\"}}]}}]}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task PostAsync_OpenCodeRenamedQuartet_RestoresClientToolNamesInStream()
    {
        // Client khai "Bash" → request bị fingerprint đổi thành "bash"; response trả "bash"
        // PHẢI map ngược về "Bash" trước khi tới client, nếu không opencode báo invalid tool.
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BashCallSse, Encoding.UTF8, "text/event-stream"),
        };
        var sut = new OpenAiUpstreamClient(
            new FixedFactory(new HttpClient(new FixedHandler(response, _ => { }))));

        var result = await sut.PostAsync(P("https://opencode.ai/zen"), "k",
            "/v1/chat/completions", Encoding.UTF8.GetBytes(RenamedBody), "req-4",
            CancellationToken.None);

        var text = await result.Content.ReadAsStringAsync();
        Assert.Contains("\"name\":\"Bash\"", text);
        Assert.DoesNotContain("\"name\":\"bash\"", text);
    }

    [Fact]
    public async Task PostAsync_NonOpenCodeProvider_KeepsStreamUntouched()
    {
        // Không phải opencode.ai → không bọc response (byte-forward nguyên vẹn)
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BashCallSse, Encoding.UTF8, "text/event-stream"),
        };
        var sut = new OpenAiUpstreamClient(
            new FixedFactory(new HttpClient(new FixedHandler(response, _ => { }))));

        var result = await sut.PostAsync(P(), "k", "/v1/chat/completions",
            Encoding.UTF8.GetBytes(RenamedBody), "req-5", CancellationToken.None);

        Assert.IsType<StringContent>(result.Content);
        Assert.Equal(BashCallSse, await result.Content.ReadAsStringAsync());
    }
}
