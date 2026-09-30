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

    private static Model ModelOf(Provider provider) => provider.Models[0];

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

    private sealed class StubUpstream(Func<HttpResponseMessage> factory) : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct) =>
            Task.FromResult(factory());
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

    private ChatCompletionsHandler Create(IUpstreamClient upstream, CapturingLog? log = null) =>
        new(upstream, _protector, log ?? new CapturingLog());

    [Fact]
    public async Task PrepareAsync_WhenJsonInvalid_Returns400OpenAiShapeAndSingleWarn()
    {
        var log = new CapturingLog();
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx("{broken");

        var prepared = await sut.PrepareAsync(ctx);

        Assert.Null(prepared);
        var (status, contentType, body) = await ReadAsync(ctx);
        Assert.Equal(400, status);
        Assert.StartsWith("application/json", contentType);
        Assert.Contains("Invalid JSON body", body);
        Assert.Contains("\"invalid_request_error\"", body);
        Assert.Single(log.Warns);
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task PrepareAsync_WhenModelMissing_Returns400ParamModel()
    {
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")));
        var ctx = Ctx("""{"messages":[{"role":"user"}]}""");

        var prepared = await sut.PrepareAsync(ctx);

        Assert.Null(prepared);
        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(400, status);
        Assert.Contains("Missing required parameter: 'model'.", body);
        Assert.Contains("\"param\":\"model\"", body);
    }

    [Fact]
    public async Task PrepareAsync_WhenMessagesMissing_Returns400ParamMessages()
    {
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")));
        var ctx = Ctx("""{"model":"gpt-4o-mini"}""");

        var prepared = await sut.PrepareAsync(ctx);

        Assert.Null(prepared);
        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(400, status);
        Assert.Contains("Missing required parameter: 'messages'.", body);
        Assert.Contains("\"param\":\"messages\"", body);
    }

    [Fact]
    public async Task ForwardAsync_WhenNoEnabledKey_ReturnsError503WithoutWritingResponse()
    {
        var log = new CapturingLog();
        var provider = SeedProvider(withKey: false);
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(503, error.Status);
        Assert.Contains("No enabled API key for provider 'openai-main'", error.Message);
        Assert.Single(log.Warns);
        // Handler không tự ghi response lỗi — endpoint ghi theo outcome (spec §2.1)
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstreamThrows_ReturnsNetworkRetryableAndLogsError()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var sut = Create(new ThrowingUpstream(new HttpRequestException("connection refused")), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        // Lỗi mạng = retryable Status null — 502 chỉ sinh ở exhaustion (T5 convert, spec §2.2)
        var retryable = Assert.IsType<DispatchOutcome.Retryable>(outcome);
        Assert.Null(retryable.Status);
        Assert.Null(retryable.ContentType);
        Assert.Empty(retryable.Body);
        Assert.Null(retryable.RetryAfter);
        Assert.Single(log.Errors); // giữ log Error 3A
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream429_ReturnsRetryableWithoutWritingResponse()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var upstreamBody = """{"error":{"message":"rate limited"}}""";
        var sut = Create(new StubUpstream(() => Upstream(429, upstreamBody)), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        // Handler KHÔNG ghi response 429 — dispatcher walk quyết định advance/passthrough (spec §2.2)
        var retryable = Assert.IsType<DispatchOutcome.Retryable>(outcome);
        Assert.Equal(429, retryable.Status);
        Assert.Equal(upstreamBody, Encoding.UTF8.GetString(retryable.Body));
        Assert.StartsWith("application/json", retryable.ContentType);
        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(200, status);
        Assert.Equal(string.Empty, body);
        // Không Info ở nhánh retryable — Warn/Err do dispatcher ghi (§5)
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream500_ReturnsRetryableWithBufferedBody()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var upstreamBody = """{"error":{"message":"internal"}}""";
        var sut = Create(new StubUpstream(() => Upstream(500, upstreamBody)), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        var retryable = Assert.IsType<DispatchOutcome.Retryable>(outcome);
        Assert.Equal(500, retryable.Status);
        Assert.Equal(upstreamBody, Encoding.UTF8.GetString(retryable.Body));
        // Response chưa commit — ctx untouched để dispatcher advance candidate kế
        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(200, status);
        Assert.Equal(string.Empty, body);
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream400_ReturnsPassthroughWithoutWritingResponse()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var upstreamBody = """{"error":{"message":"bad request"}}""";
        var sut = Create(new StubUpstream(() => Upstream(400, upstreamBody)), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        // Non-retryable — endpoint ghi (quan sát client y hệt 3A), không advance
        var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome);
        Assert.Equal(400, passthrough.Status);
        Assert.Equal(upstreamBody, Encoding.UTF8.GetString(passthrough.Body));
        Assert.Null(passthrough.RetryAfterHeader);
        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(200, status);
        Assert.Equal(string.Empty, body);
        Assert.Single(log.Infos); // Info giữ nguyên cho passthrough (parity quen sát 3A)
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream429WithRetryAfter_ParsesRetryAfterIntoRetryable()
    {
        var provider = SeedProvider();
        var response = Upstream(429, "{}");
        response.Headers.TryAddWithoutValidation("Retry-After", "30");
        var sut = Create(new StubUpstream(() => response));
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        var retryable = Assert.IsType<DispatchOutcome.Retryable>(outcome);
        // Delta-seconds parse được → floor nextProbeAt khi exhaustion (§3.6)
        Assert.Equal(TimeSpan.FromSeconds(30), retryable.RetryAfter);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstreamSse_PassesStreamBytesUnchanged()
    {
        var provider = SeedProvider();
        var sut = Create(new StubUpstream(() => Upstream(200, "data: {\"x\":1}\n\ndata: [DONE]\n\n",
            "text/event-stream")));
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        var (status, contentType, body) = await ReadAsync(ctx);
        Assert.Equal(200, status);
        Assert.StartsWith("text/event-stream", contentType);
        Assert.Equal("data: {\"x\":1}\n\ndata: [DONE]\n\n", body);
    }

    [Fact]
    public async Task ForwardAsync_WhenSuccess_LogsExactlyOneInfoWithModelAndProvider()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Single(log.Infos);
        Assert.Contains("gpt-4o-mini", log.Infos[0]);
        Assert.Contains("openai-main", log.Infos[0]);
        Assert.Contains("HTTP 200", log.Infos[0]);
        Assert.Empty(log.Warns);
        Assert.Empty(log.Errors);
    }
}
