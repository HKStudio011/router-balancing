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
    public void Source_IsEndpointAndNotCompleteList()
    {
        var provider = new ProviderEndpointMetadataProvider(
            new StubFactory(new JsonHandler("{}")), new DpapiSecretProtector());

        Assert.Equal(RouterBalancing.Core.Capabilities.CapabilitySource.Endpoint, provider.Source);
        Assert.False(provider.ReturnsCompleteFacts);
    }

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
        Assert.Equal(new[] { "text", "image" }, meta.InputModalities);
        Assert.False(provider.ReturnsCompleteFacts); // probe positive-only (R-T7-2)
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
        Assert.Equal(new[] { "text", "image" }, meta.InputModalities);
        Assert.Equal(new[] { "text" }, meta.OutputModalities);
    }

    [Fact]
    public async Task FetchAsync_OpenRouter_UsesSingularModelEndpoint()
    {
        var handler = new JsonHandler("""{"id":"baidu/ernie-5.5-flash-preview:free","context_length":1024000}""");
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());
        var p = new Provider
        {
            Name = "Renamed OpenRouter",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://openrouter.ai/api",
        };
        var m = new Model { ProviderId = 1, ModelId = "baidu/ernie-5.5-flash-preview:free" };

        await provider.FetchAsync(p, m);

        Assert.Equal(
            "https://openrouter.ai/api/v1/model/baidu/ernie-5.5-flash-preview:free",
            handler.LastUri!.ToString());
    }

    [Fact]
    public async Task FetchAsync_OpenRouterDataEnvelope_ParsesMetadata()
    {
        // Shape API thật: object model nằm dưới data, giữ nguyên variant :free và modalities nested.
        var handler = new JsonHandler("""
            {
              "data": {
                "id": "baidu/ernie-5.5-flash-preview:free",
                "canonical_slug": "baidu/ernie-5.5-flash-preview-20261110",
                "name": "Baidu: ERNIE-5.5-Flash-Preview (free)",
                "created": 1794333533,
                "context_length": 1024000,
                "architecture": {
                  "modality": "text->text",
                  "input_modalities": ["text"],
                  "output_modalities": ["text"],
                  "tokenizer": "Other",
                  "instruct_type": null
                },
                "pricing": { "prompt": "0", "completion": "0" },
                "top_provider": {
                  "context_length": 1024000,
                  "max_completion_tokens": 921600,
                  "is_moderated": false
                },
                "per_request_limits": null,
                "supported_parameters": ["max_tokens", "reasoning", "tools"],
                "default_parameters": {},
                "supported_voices": null,
                "knowledge_cutoff": null,
                "expiration_date": null,
                "links": { "details": "/api/v1/models/baidu/ernie-5.5-flash-preview-20261110/endpoints" },
                "endpoints": ["/api/v1/chat/completions"],
                "reasoning": { "mandatory": true, "default_enabled": true, "supported_efforts": ["high"] }
              }
            }
            """);
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());
        var p = new Provider
        {
            Name = "OpenRouter Free",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://openrouter.ai/api",
        };
        var m = new Model { ProviderId = 1, ModelId = "baidu/ernie-5.5-flash-preview:free" };

        var meta = await provider.FetchAsync(p, m);

        Assert.NotNull(meta);
        Assert.Equal(1_024_000, meta.ContextWindow);
        Assert.False(meta.SupportsVision);
        Assert.Equal(new[] { "text" }, meta.InputModalities);
        Assert.Equal(new[] { "text" }, meta.OutputModalities);
    }

    [Fact]
    public async Task FetchAsync_NvidiaToolsFlag_ParsesTools()
    {
        var handler = new JsonHandler("""{"id":"m","supports_tools":true}""");
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());
        var (p, m) = Pair();

        var meta = await provider.FetchAsync(p, m);

        Assert.NotNull(meta);
        Assert.True(meta.SupportsTools);
    }

    [Fact]
    public async Task FetchAsync_NvidiaThinkingFlag_ParsesReasoning()
    {
        var handler = new JsonHandler("""{"id":"m","supports_thinking":true}""");
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());
        var (p, m) = Pair();

        var meta = await provider.FetchAsync(p, m);

        Assert.NotNull(meta);
        Assert.True(meta.SupportsThink);
    }

    [Theory]
    [InlineData("vlm")]
    [InlineData("nv-vlm")]
    public async Task FetchAsync_NvidiaVisionModelType_ParsesVision(string modelType)
    {
        var handler = new JsonHandler($$"""{"id":"m","model_type":"{{modelType}}"}""");
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());
        var (p, m) = Pair();

        var meta = await provider.FetchAsync(p, m);

        Assert.NotNull(meta);
        Assert.True(meta.SupportsVision);
    }

    [Fact]
    public async Task FetchAsync_OpenRouterSupportedParameters_ParsesTools()
    {
        var handler = new JsonHandler("""
            {"data":{"id":"m","supported_parameters":["max_tokens","tools"]}}
            """);
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());
        var p = new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://openrouter.ai/api" };
        var m = new Model { ProviderId = 1, ModelId = "m" };

        var meta = await provider.FetchAsync(p, m);

        Assert.NotNull(meta);
        Assert.True(meta.SupportsTools);
    }

    [Fact]
    public async Task FetchAsync_OpenRouterReasoningObject_ParsesReasoning()
    {
        var handler = new JsonHandler("""
            {"data":{"id":"m","reasoning":{"mandatory":false}}}
            """);
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());
        var p = new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://openrouter.ai/api" };
        var m = new Model { ProviderId = 1, ModelId = "m" };

        var meta = await provider.FetchAsync(p, m);

        Assert.NotNull(meta);
        Assert.True(meta.SupportsThink);
    }

    [Fact]
    public async Task FetchAsync_OpenRouterReasoningObject_ParsesEffortLevels()
    {
        var handler = new JsonHandler("""
            {"data":{"id":"m","reasoning":{"mandatory":false,"supported_efforts":["high","low"]}}}
            """);
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());
        var p = new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://openrouter.ai/api" };
        var m = new Model { ProviderId = 1, ModelId = "m" };

        var meta = await provider.FetchAsync(p, m);

        Assert.NotNull(meta);
        Assert.Equal(new[] { "high", "low" }, meta.ThinkEfforts);
    }
}
