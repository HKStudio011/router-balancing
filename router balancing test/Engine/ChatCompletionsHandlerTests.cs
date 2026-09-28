using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;

namespace router_balancing_test.Engine;

public class ChatCompletionsHandlerTests
{
    private readonly DpapiSecretProtector _protector = new();

    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);
    private const string ValidJson = """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}]}""";

    private Provider SeedProvider(bool withKey = true)
    {
        var provider = new Provider
        {
            Name = "openai-main",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.openai.com",
        };
        provider.Models.Add(new Model { ModelId = "gpt-4o-mini", Enabled = true });
        if (withKey)
            provider.Accounts.Add(new ProviderAccount
            {
                Name = "a1",
                Enabled = true,
                ApiKeyEncrypted = _protector.Protect("sk-live"),
            });
        return provider;
    }

    private static ModelResolveSuccess Success(Provider p) =>
        new(p, p.Models[0]);

    private static DefaultHttpContext Ctx(string? json = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(json is null ? [] : Body(json));
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<(int Status, string? ContentType, string Body)> ReadAsync(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        return (ctx.Response.StatusCode, ctx.Response.ContentType, await reader.ReadToEndAsync());
    }

    private static HttpResponseMessage Upstream(int status, string body, string mediaType = "application/json") =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class StubResolver(ModelResolveResult result) : IModelResolver
    {
        public Task<ModelResolveResult> ResolveAsync(string modelId, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class StubUpstream(Func<HttpResponseMessage> factory) : IUpstreamClient
    {
        public byte[]? LastBody { get; private set; }

        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            LastBody = body;
            return Task.FromResult(factory());
        }
    }

    private sealed class ThrowingUpstream(Exception ex) : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(ex);
    }

    private sealed class CapturingLog : ILogService
    {
        public List<string> Infos { get; } = [];
        public List<string> Warns { get; } = [];
        public List<string> Errors { get; } = [];

        public event Action<LogEntry>? LogAdded { add { } remove { } }
        public void Write(LogEntry entry) { }
        public void Info(string message, LogCategory category = LogCategory.App) => Infos.Add(message);
        public void Warn(string message, LogCategory category = LogCategory.App) => Warns.Add(message);
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
            Errors.Add(message);
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }

    private ChatCompletionsHandler Create(
        ModelResolveResult resolve, IUpstreamClient upstream, CapturingLog? log = null) =>
        new(new StubResolver(resolve), upstream, _protector, log ?? new CapturingLog());

    [Fact]
    public async Task HandleAsync_WhenJsonInvalid_Returns400OpenAiShapeAndSingleWarn()
    {
        var log = new CapturingLog();
        var sut = Create(
            new ModelResolveFailure("x", ResolveFailure.NotFound),
            new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx("{broken");

        await sut.HandleAsync(ctx);

        var (status, contentType, body) = await ReadAsync(ctx);
        Assert.Equal(400, status);
        Assert.StartsWith("application/json", contentType);
        Assert.Contains("Invalid JSON body", body);
        Assert.Contains("\"invalid_request_error\"", body);
        Assert.Single(log.Warns);
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task HandleAsync_WhenModelMissing_Returns400ParamModel()
    {
        var sut = Create(
            new ModelResolveFailure("x", ResolveFailure.NotFound),
            new StubUpstream(() => Upstream(200, "{}")));
        var ctx = Ctx("""{"messages":[{"role":"user"}]}""");

        await sut.HandleAsync(ctx);

        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(400, status);
        Assert.Contains("Missing required parameter: 'model'.", body);
        Assert.Contains("\"param\":\"model\"", body);
    }

    [Fact]
    public async Task HandleAsync_WhenMessagesMissing_Returns400ParamMessages()
    {
        var sut = Create(
            new ModelResolveFailure("x", ResolveFailure.NotFound),
            new StubUpstream(() => Upstream(200, "{}")));
        var ctx = Ctx("""{"model":"gpt-4o-mini"}""");

        await sut.HandleAsync(ctx);

        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(400, status);
        Assert.Contains("\"param\":\"messages\"", body);
    }

    [Fact]
    public async Task HandleAsync_WhenModelUnknown_Returns404ModelNotFound()
    {
        var log = new CapturingLog();
        var sut = Create(
            new ModelResolveFailure("nope", ResolveFailure.NotFound),
            new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(404, status);
        Assert.Contains("The model 'nope' does not exist", body);
        Assert.Contains("\"model_not_found\"", body);
        Assert.Single(log.Warns);
    }

    [Fact]
    public async Task HandleAsync_WhenProviderAnthropic_Returns503ServerError()
    {
        var log = new CapturingLog();
        var sut = Create(
            new ModelResolveFailure("sonnet-4", ResolveFailure.AnthropicNotSupported),
            new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(503, status);
        Assert.Contains("not supported yet", body);
        Assert.Contains("\"server_error\"", body);
        Assert.Single(log.Warns);
    }

    [Fact]
    public async Task HandleAsync_WhenNoEnabledKey_Returns503WithProviderName()
    {
        var provider = SeedProvider(withKey: false);
        var sut = Create(
            Success(provider),
            new StubUpstream(() => Upstream(200, "{}")));
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(503, status);
        Assert.Contains("No enabled API key for provider 'openai-main'", body);
    }

    [Fact]
    public async Task HandleAsync_WhenUpstreamThrows_Returns502AndLogsError()
    {
        var log = new CapturingLog();
        var sut = Create(
            Success(SeedProvider()),
            new ThrowingUpstream(new HttpRequestException("connection refused")), log);
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(502, status);
        Assert.Contains("Upstream provider request failed", body);
        Assert.Single(log.Errors);
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task HandleAsync_WhenUpstream429_PassesStatusAndBodyThroughWithInfoLog()
    {
        var log = new CapturingLog();
        var upstreamBody = """{"error":{"message":"rate limited"}}""";
        var sut = Create(
            Success(SeedProvider()),
            new StubUpstream(() => Upstream(429, upstreamBody)), log);
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        var (status, contentType, body) = await ReadAsync(ctx);
        Assert.Equal(429, status);
        Assert.Equal(upstreamBody, body);
        Assert.StartsWith("application/json", contentType);
        // Upstream đã trả response → Info là dòng log duy nhất (spec §6)
        Assert.Single(log.Infos);
        Assert.Empty(log.Warns);
    }

    [Fact]
    public async Task HandleAsync_WhenUpstreamSse_PassesStreamBytesUnchanged()
    {
        var sut = Create(
            Success(SeedProvider()),
            new StubUpstream(() => Upstream(200, "data: {\"x\":1}\n\ndata: [DONE]\n\n", "text/event-stream")));
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        var (status, contentType, body) = await ReadAsync(ctx);
        Assert.Equal(200, status);
        Assert.StartsWith("text/event-stream", contentType);
        Assert.Equal("data: {\"x\":1}\n\ndata: [DONE]\n\n", body);
    }

    [Fact]
    public async Task HandleAsync_WhenSuccess_LogsExactlyOneInfoWithModelAndProvider()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var stub = new StubUpstream(() => Upstream(200, "{}"));
        var sut = Create(Success(provider), stub, log);
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        Assert.Single(log.Infos);
        Assert.Contains("gpt-4o-mini", log.Infos[0]);
        Assert.Contains("openai-main", log.Infos[0]);
        Assert.Contains("HTTP 200", log.Infos[0]);
        Assert.Empty(log.Warns);
        Assert.Empty(log.Errors);
    }
}
