using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

/// <summary>
/// Route request opencode.ai/chat/completions qua Go sidecar (TLS fingerprint
/// vượt gate 403); request khác pass-through nguyên trạng.
/// </summary>
public class OpenCodeSidecarHandlerTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return respond(request);
        }
    }

    private sealed class StubSidecar(string? baseUrl) : IOpenCodeSidecarService
    {
        public int EnsureStartedCalls { get; private set; }
        public string? BaseUrl => baseUrl;
        public Task<bool> EnsureStartedAsync(CancellationToken ct = default)
        {
            EnsureStartedCalls++;
            return Task.FromResult(baseUrl is not null);
        }
        public void Dispose() { }
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);

    [Fact]
    public async Task SendAsync_WhenRequestNotToOpencode_PassesThrough()
    {
        var inner = new StubHandler(_ => Task.FromResult(Ok()));
        var sidecar = new StubSidecar("http://127.0.0.1:59999");
        using var handler = new OpenCodeSidecarHandler(sidecar, inner);
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, sidecar.EnsureStartedCalls);
        Assert.Equal("https://api.openai.com/v1/chat/completions", inner.LastRequest!.RequestUri!.ToString());
    }

    [Fact]
    public async Task SendAsync_WhenRequestToOpencode_RoutesThroughSidecarWithProxyHeader()
    {
        var inner = new StubHandler(_ => Task.FromResult(Ok()));
        var sidecar = new StubSidecar("http://127.0.0.1:59999");
        using var handler = new OpenCodeSidecarHandler(sidecar, inner);
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://opencode.ai/zen/v1/chat/completions")
        {
            Content = new StringContent("""{"model":"mimo-v2.6-flash-free"}"""),
        };
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, sidecar.EnsureStartedCalls);
        Assert.Equal("http://127.0.0.1:59999/v1/chat/completions", inner.LastRequest!.RequestUri!.ToString());
        Assert.False(inner.LastRequest.Headers.Contains("X-Proxy-Url"),
            "Không có proxy scope → không gửi X-Proxy-Url (direct).");
    }

    [Fact]
    public async Task SendAsync_WhenSidecarMissing_ThrowsAsProxyConnectFailure()
    {
        var inner = new StubHandler(_ => Task.FromResult(Ok()));
        var sidecar = new StubSidecar(baseUrl: null);
        using var handler = new OpenCodeSidecarHandler(sidecar, inner);
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://opencode.ai/zen/v1/chat/completions");

        // ProxyHealthHandler.IsProxyConnectFailure match HttpRequestException có
        // inner SocketException — sidecar chết phải giả lập cùng shape để retry.
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        Assert.IsType<SocketException>(ex.InnerException);
    }

    [Fact]
    public async Task SendAsync_WhenProxyScopeActive_SetsEncodedXProxyUrl()
    {
        var inner = new StubHandler(_ => Task.FromResult(Ok()));
        var sidecar = new StubSidecar("http://127.0.0.1:59999");
        using var handler = new OpenCodeSidecarHandler(sidecar, inner);
        using var client = new HttpClient(handler);

        var attempt = new RouterBalancing.Core.Proxies.ProxyAttempt(
            1, new Uri("socks5://proxy.example.com:1080"), "user@example", "p@ss:w/rd", "socks5://proxy.example.com:1080");
        RouterBalancing.Core.Proxies.ProxyContext.Current = attempt;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://opencode.ai/zen/v1/chat/completions");
            await client.SendAsync(request);
        }
        finally
        {
            RouterBalancing.Core.Proxies.ProxyContext.Current = null;
        }

        var proxyUrl = inner.LastRequest!.Headers.GetValues("X-Proxy-Url").Single();
        Assert.Equal("socks5://user%40example:p%40ss%3Aw%2Frd@proxy.example.com:1080", proxyUrl);
    }

    [Fact]
    public async Task SendAsync_WhenSidecarReturns502ProxyFailure_ThrowsAsProxyConnectFailure()
    {
        var inner = new StubHandler(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.BadGateway);
            resp.Headers.Add("X-Proxy-Failure", "connect");
            return Task.FromResult(resp);
        });
        var sidecar = new StubSidecar("http://127.0.0.1:59999");
        using var handler = new OpenCodeSidecarHandler(sidecar, inner);
        using var client = new HttpClient(handler);

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://opencode.ai/zen/v1/chat/completions");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        Assert.IsType<SocketException>(ex.InnerException);
    }
}
