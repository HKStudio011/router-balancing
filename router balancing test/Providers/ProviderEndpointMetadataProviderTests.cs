using System.Net;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;

namespace router_balancing_test.Providers;

public class ProviderEndpointMetadataProviderTests
{
    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("boom");
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (Provider Provider, Model Model) Pair() =>
        (new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com" },
         new Model { ProviderId = 1, ModelId = "gpt-4o" });

    [Fact]
    public async Task FetchAsync_WhenEndpointReturnsContextWindow_ParsesMetadata()
    {
        var handler = new JsonHandler("""
            {"id":"gpt-4o","context_window":128000,
             "supported_modalities":{"input":["text","image"],"output":["text"]}}
            """);
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());
        var (p, m) = Pair();

        var meta = await provider.FetchAsync(p, m);

        Assert.NotNull(meta);
        Assert.Equal(128_000, meta.ContextWindow);
        Assert.True(meta.SupportsVision);
        Assert.Equal("""["text","image"]""", meta.InputModalities);
        // Request đúng path {base}/v1/models/{id}
        Assert.Equal("https://api.example.com/v1/models/gpt-4o", handler.LastUri!.ToString());
    }

    [Fact]
    public async Task FetchAsync_WhenEndpointThrows_ReturnsNull()
    {
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(new ThrowingHandler()), new DpapiSecretProtector());
        var (p, m) = Pair();

        Assert.Null(await provider.FetchAsync(p, m));
    }

    [Fact]
    public async Task FetchAsync_WhenShapeUnrecognized_ReturnsNull()
    {
        var provider = new ProviderEndpointMetadataProvider(
            new StubFactory(new JsonHandler("""{"foo":"bar"}""")), new DpapiSecretProtector());
        var (p, m) = Pair();

        Assert.Null(await provider.FetchAsync(p, m));
    }

    [Fact]
    public async Task FetchAsync_WhenContextLengthOnly_ParsesContextWindow()
    {
        // OpenRouter đặt ctx ở context_length, không phải context_window
        var handler = new JsonHandler("""{"id":"m","context_length":65536}""");
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());

        var (p, m) = Pair();
        var meta = await provider.FetchAsync(p, m);

        Assert.NotNull(meta);
        Assert.Equal(65_536, meta.ContextWindow);
    }

    [Fact]
    public async Task FetchAsync_WhenMaxModelLenOnly_ParsesContextWindow()
    {
        // vLLM đặt ctx ở max_model_len
        var handler = new JsonHandler("""{"id":"m","max_model_len":32768}""");
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());

        var (p, m) = Pair();
        var meta = await provider.FetchAsync(p, m);

        Assert.NotNull(meta);
        Assert.Equal(32_768, meta.ContextWindow);
    }

    [Fact]
    public async Task FetchAsync_WhenMultipleContextFields_PrefersContextWindow()
    {
        var handler = new JsonHandler("""
            {"id":"m","context_window":128000,"context_length":65536,"max_model_len":32768}
            """);
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());

        var (p, m) = Pair();
        var meta = await provider.FetchAsync(p, m);

        Assert.NotNull(meta);
        Assert.Equal(128_000, meta.ContextWindow);
    }

    [Fact]
    public async Task FetchAsync_WhenArchitectureModalities_ParsesModalitiesAndVision()
    {
        // OpenRouter shape: modalities nằm trong architecture, không có supported_modalities
        var handler = new JsonHandler("""
            {"id":"m","architecture":{"input_modalities":["text","image"],"output_modalities":["text"]}}
            """);
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());

        var (p, m) = Pair();
        var meta = await provider.FetchAsync(p, m);

        Assert.NotNull(meta);
        Assert.True(meta.SupportsVision);
        Assert.Equal("""["text","image"]""", meta.InputModalities);
        Assert.Equal("""["text"]""", meta.OutputModalities);
    }
}
