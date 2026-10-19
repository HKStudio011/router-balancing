using System.IO.Pipelines;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;

namespace router_balancing_test.Engine;

public class ProxyRequestHandlerTests
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

    private Provider SeedOpenCodeProvider()
    {
        // Host opencode.ai → OpenCodeFreeFingerprint áp dụng; dùng để kiểm tín hiệu chẩn đoán
        // decoy-call (response vẫn đi qua StubUpstream nên request KHÔNG bị rewrite — chỉ nhánh
        // tính decoy/tool list từ body gốc được test)
        var provider = new Provider
        {
            Name = "opencode-free",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://opencode.ai/zen",
        };
        provider.Models.Add(new Model { ModelId = "gpt-4o-mini", Enabled = true });
        provider.Accounts.Add(new ProviderAccount
        {
            Name = "a1",
            Enabled = true,
            ApiKeyEncrypted = _protector.Protect("sk-live"),
        });
        return provider;
    }

    private static Model ModelOf(Provider provider) => provider.Models[0];

    // Entity in-memory (chưa SaveChanges) → Id = 0 khớp account duy nhất của test;
    // withKey: false → không có account → -1 → handler không match → 503 như cũ.
    private static long AccountIdOf(Provider provider) => provider.Accounts.FirstOrDefault()?.Id ?? -1;

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
        public string? LastApiKey { get; private set; }

        // Token mà handler truyền cho PostAsync — test xác minh cancel-before-error
        // (upstream bị hủy TRƯỚC khi outcome/exception tới endpoint ghi lỗi cho client)
        public CancellationToken LastCt { get; private set; }

        public Task<HttpResponseMessage> PostAsync(
            Provider provider, string apiKey, string path, byte[] body, string idempotencyKey, CancellationToken ct)
        {
            LastApiKey = apiKey;
            LastCt = ct;
            return Task.FromResult(factory());
        }
    }

    // Stub settings cho handler — default 300 như production để test hiện có chạy với watchdog bật
    private sealed class StubSettings : IAppSettingsService
    {
        public event Action? SettingsChanged { add { } remove { } }
        public string Language => "auto";
        public string Theme => "system";
        public int Port => 8317;
        public bool LanAccess => false;
        public bool CloseToTray => true;
        public bool StartWithWindows => false;
        public int ProviderProbeTimeoutSec => 60;
        public int LogRetentionDays => 90;
        public int StatsErrorRateThreshold => 10;
        public int TransientMaxRetries => 5;
        public int TransientBackoffBaseMs => 1000;
        public int UpstreamIdleTimeoutSec { get; set; } = 300;
        public T Get<T>(string key, T defaultValue) => defaultValue;
        public void Set<T>(string key, T value) { }
    }

    private sealed class ThrowingUpstream(Exception ex) : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostAsync(
            Provider provider, string apiKey, string path, byte[] body, string idempotencyKey, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(ex);
    }

    private sealed class CapturingLog : ILogService
    {
        public List<string> Infos { get; } = [];
        public List<string> Warns { get; } = [];
        public List<string> Errors { get; } = [];
        public List<string> Debugs { get; } = [];
        public List<LogEntry> Entries { get; } = [];
        public List<(string? RequestId, long? ClientKeyId, int Prompt, int Completion)> Usages { get; } = [];

        public event Action<LogEntry>? LogAdded { add { } remove { } }

        // Handler ghi journal qua Write (kèm RequestId/ClientKeyId, spec §7) — route theo Severity
        // để assert Infos/Warns/Errors cũ vẫn đúng; wrapper Info/Warn/Error gọi Write như LogService thật
        public void Write(LogEntry entry)
        {
            Entries.Add(entry);
            switch (entry.Severity)
            {
                case LogSeverity.Debug: Debugs.Add(entry.Message); break;
                case LogSeverity.Warning: Warns.Add(entry.Message); break;
                case LogSeverity.Error: Errors.Add(entry.Message); break;
                default: Infos.Add(entry.Message); break;
            }
        }
        public void Info(string message, LogCategory category = LogCategory.App) =>
            Write(new LogEntry { Severity = LogSeverity.Info, Category = category, Message = message });
        public void Warn(string message, LogCategory category = LogCategory.App) =>
            Write(new LogEntry { Severity = LogSeverity.Warning, Category = category, Message = message });
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
            Write(new LogEntry { Severity = LogSeverity.Error, Category = category, Message = message });
        public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) =>
            Usages.Add((requestId, clientKeyId, promptTokens, completionTokens));
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }

    private ProxyRequestHandler Create(IUpstreamClient upstream, CapturingLog? log = null,
        IClientKeyUsageSink? sink = null, ApiMonitorStore? monitor = null,
        StubSettings? settings = null)
    {
        var effectiveLog = log ?? new CapturingLog();
        // Store/feed thật tham gia DI — hook monitor không được đổi hành vi outcome/log đang test
        return new(upstream, _protector, effectiveLog, sink ?? new NullUsageSink(),
            monitor ?? new ApiMonitorStore(new TraceFeed(effectiveLog), effectiveLog,
                TimeProvider.System),
            settings ?? new StubSettings());
    }

    [Fact]
    public async Task PrepareAsync_WhenJsonInvalid_Returns400OpenAiShapeAndSingleWarn()
    {
        var log = new CapturingLog();
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx("{broken");
        ctx.Items[ClientKeyItems.RequestId] = "req-42";
        ctx.Items[ClientKeyItems.Id] = 7L;

        var prepared = await sut.PrepareAsync(ctx, ProxyProtocols.Chat);

        Assert.Null(prepared);
        var (status, contentType, body) = await ReadAsync(ctx);
        Assert.Equal(400, status);
        Assert.StartsWith("application/json", contentType);
        Assert.Contains("Invalid JSON body", body);
        Assert.Contains("\"invalid_request_error\"", body);
        Assert.Single(log.Warns);
        Assert.Empty(log.Infos);
        // Row validate-fail vẫn correlate được request qua RequestId/ClientKeyId (spec §7)
        var warn = Assert.Single(log.Entries, e => e.Severity == LogSeverity.Warning);
        Assert.Equal("req-42", warn.RequestId);
        Assert.Equal(7L, warn.ClientKeyId);
    }

    [Fact]
    public async Task PrepareAsync_WhenModelMissing_Returns400ParamModel()
    {
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")));
        var ctx = Ctx("""{"messages":[{"role":"user"}]}""");

        var prepared = await sut.PrepareAsync(ctx, ProxyProtocols.Chat);

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

        var prepared = await sut.PrepareAsync(ctx, ProxyProtocols.Chat);

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

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(503, error.Status);
        Assert.Contains("No enabled API key for provider 'openai-main'", error.Message);
        Assert.Single(log.Warns);
        // Handler không tự ghi response lỗi — endpoint ghi theo outcome (spec §2.1)
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ForwardAsync_WhenNoKeyAccount_SendsEmptyKeyAndHandles()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        provider.Accounts[0].ApiKeyEncrypted = string.Empty; // account no-key đã lưu
        var upstream = new StubUpstream(() => Upstream(200, "{}"));
        var sut = Create(upstream, log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(string.Empty, upstream.LastApiKey); // upstream không auth, không throw
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstreamThrows_ReturnsFatalProviderAndLogsError()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var sut = Create(new ThrowingUpstream(new HttpRequestException("connection refused")), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        // Mạng = Fatal(Provider, Status null) — dispatcher advance candidate kế (spec §3.2);
        // 502 chỉ sinh ở exhaustion khi attempt cuối là mạng (§4)
        var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
        Assert.Equal(FailoverLevel.Provider, fatal.Level);
        Assert.Null(fatal.Status);
        Assert.Null(fatal.ContentType);
        Assert.Empty(fatal.Body);
        Assert.Null(fatal.RetryAfter);
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

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

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

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

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

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

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
    public async Task ForwardAsync_WhenUpstream401_ReturnsFatalAccountCarryingPayload()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var upstreamBody = """{"error":{"message":"invalid api key"}}""";
        var sut = Create(new StubUpstream(() => Upstream(401, upstreamBody)), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        // 401 = auth sai ở account (spec §1.3 #6/§3.2) — payload giữ cho exhaustion passthrough (§4)
        var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
        Assert.Equal(FailoverLevel.Account, fatal.Level);
        Assert.Equal(401, fatal.Status);
        Assert.StartsWith("application/json", fatal.ContentType);
        Assert.Equal(upstreamBody, Encoding.UTF8.GetString(fatal.Body));
        // Handler không ghi response — dispatcher/endpoint quyết định (parity 3A)
        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(200, status);
        Assert.Equal(string.Empty, body);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream403_ReturnsFatalAccount()
    {
        var provider = SeedProvider();
        var sut = Create(new StubUpstream(() => Upstream(403, """{"error":{"message":"forbidden"}}""")));
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        // 403 cùng nhóm lỗi auth với 401 → account cấp (spec §3.2)
        var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
        Assert.Equal(FailoverLevel.Account, fatal.Level);
        Assert.Equal(403, fatal.Status);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream404ModelNotFound_ReturnsFatalModel()
    {
        var provider = SeedProvider();
        var upstreamBody =
            """{"error":{"message":"The model does not exist","code":"model_not_found"}}""";
        var sut = Create(new StubUpstream(() => Upstream(404, upstreamBody)));
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        // 404 + error.code=model_not_found = model sai — Fatal cấp Model (§3.2)
        var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
        Assert.Equal(FailoverLevel.Model, fatal.Level);
        Assert.Equal(404, fatal.Status);
        Assert.Equal(upstreamBody, Encoding.UTF8.GetString(fatal.Body));
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream404CodeUppercase_MatchesCaseInsensitive()
    {
        var provider = SeedProvider();
        var sut = Create(new StubUpstream(() => Upstream(404,
            """{"error":{"message":"nope","code":"MODEL_NOT_FOUND"}}""")));
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        // ordinal-ignore-case — provider code khác casing vẫn nhận diện (V2)
        var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
        Assert.Equal(FailoverLevel.Model, fatal.Level);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream404PlainBody_ReturnsFatalProvider()
    {
        var provider = SeedProvider();
        var sut = Create(new StubUpstream(() => Upstream(404,
            """{"error":{"message":"not found"}}""")));
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        // 404 thường = sai endpoint/provider chết — Fatal cấp Provider (§3.2)
        var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
        Assert.Equal(FailoverLevel.Provider, fatal.Level);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream404BodyIsNotJson_ReturnsFatalProvider()
    {
        var provider = SeedProvider();
        var sut = Create(new StubUpstream(() => Upstream(404, "<html>404</html>", "text/html")));
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        // Body không parse được → coi 404 thường, không crash phân loại (V2)
        var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
        Assert.Equal(FailoverLevel.Provider, fatal.Level);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream401BodyIsNotJson_StillReturnsFatalAccount()
    {
        var provider = SeedProvider();
        var sut = Create(new StubUpstream(() => Upstream(401, "oops", "text/plain")));
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        // 401 check theo TRẠNG THÁI trước, không parse body — body hỏng vẫn Fatal(Account)
        var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
        Assert.Equal(FailoverLevel.Account, fatal.Level);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream429WithRetryAfter_ParsesRetryAfterIntoRetryable()
    {
        var provider = SeedProvider();
        var response = Upstream(429, "{}");
        response.Headers.TryAddWithoutValidation("Retry-After", "30");
        var sut = Create(new StubUpstream(() => response));
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        var retryable = Assert.IsType<DispatchOutcome.Retryable>(outcome);
        // Delta-seconds parse được → giữ nguyên cho exhaustion passthrough (§3.6)
        Assert.Equal(TimeSpan.FromSeconds(30), retryable.RetryAfter);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstreamSse_StreamClient_PassesBytesUnchanged()
    {
        // Stream client (có pipe) + upstream SSE → byte-forward nguyên vẹn vào pipe
        var provider = SeedProvider();
        var sut = Create(new StubUpstream(() => Upstream(200, "data: {\"x\":1}\n\ndata: [DONE]\n\n",
            "text/event-stream")));
        var ctx = Ctx();
        var pipe = new System.IO.Pipelines.Pipe();
        ctx.Items[SsePipeItems.Key] = pipe;

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);
        await pipe.Writer.CompleteAsync();

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(0, ((MemoryStream)ctx.Response.Body).Length); // handler không viết Response.Body khi có pipe
        using var reader = new StreamReader(pipe.Reader.AsStream(), Encoding.UTF8);
        Assert.Equal("data: {\"x\":1}\n\ndata: [DONE]\n\n", await reader.ReadToEndAsync());
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstreamSse_NonStreamClient_AggregatesToChatCompletionJson()
    {
        // Non-stream client + upstream SSE (fingerprint force stream) → aggregate
        // về JSON chat.completion, content-type application/json (OpenCode gate contract)
        var provider = SeedProvider();
        var sut = Create(new StubUpstream(() => Upstream(200,
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: [DONE]\n\n",
            "text/event-stream")));
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        var (status, contentType, body) = await ReadAsync(ctx);
        Assert.Equal(200, status);
        Assert.Equal("application/json", contentType);
        Assert.Contains("\"object\":\"chat.completion\"", body);
        Assert.Contains("\"content\":\"hi\"", body);
    }

    [Fact]
    public async Task ForwardAsync_WhenSuccess_LogsExactlyOneInfoWithModelAndProvider()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Single(log.Infos);
        Assert.Contains("gpt-4o-mini", log.Infos[0]);
        Assert.Contains("openai-main", log.Infos[0]);
        Assert.Contains("HTTP 200", log.Infos[0]);
        Assert.Empty(log.Warns);
        Assert.Empty(log.Errors);
    }

    private const string StreamJson =
        """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}],"stream":true}""";

    private sealed class BodyCapturingUpstream(Func<HttpResponseMessage> factory) : IUpstreamClient
    {
        public byte[]? LastBody { get; private set; }
        public Task<HttpResponseMessage> PostAsync(
            Provider provider, string apiKey, string path, byte[] body, string idempotencyKey, CancellationToken ct)
        {
            LastBody = body;
            return Task.FromResult(factory());
        }
    }

    private sealed class CapturingUsageSink : IClientKeyUsageSink
    {
        public List<(long? KeyId, int Prompt, int Completion)> Records { get; } = [];
        public Task RecordAsync(long? clientKeyId, int promptTokens, int completionTokens,
            CancellationToken ct = default)
        {
            Records.Add((clientKeyId, promptTokens, completionTokens));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ForwardAsync_WhenStreamOpenAi_GetsIncludeUsageInjected()
    {
        var upstream = new BodyCapturingUpstream(
            () => Upstream(200, "data: [DONE]\n\n", "text/event-stream"));
        var sut = Create(upstream);
        var ctx = Ctx();
        var provider = SeedProvider();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(StreamJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.NotNull(upstream.LastBody);
        using var doc = System.Text.Json.JsonDocument.Parse(upstream.LastBody);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task ForwardAsync_WhenSseHasUsage_WritesUsageRowAndCountsSink()
    {
        const string sse =
            "data: {\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":7}}\n\ndata: [DONE]\n\n";
        var log = new CapturingLog();
        var sink = new CapturingUsageSink();
        var sut = Create(new StubUpstream(() => Upstream(200, sse, "text/event-stream")), log, sink);
        var ctx = Ctx();
        ctx.Items[ClientKeyItems.RequestId] = "abc12345";
        ctx.Items[ClientKeyItems.Id] = 42L;
        var provider = SeedProvider();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        var (_, contentType, body) = await ReadAsync(ctx);
        // Non-stream client + SSE upstream → aggregate JSON, usage vẫn parse được
        Assert.Equal("application/json", contentType);
        Assert.Contains("\"prompt_tokens\":11", body);
        Assert.Contains("\"completion_tokens\":7", body);
        Assert.Equal(("abc12345", 42L, 11, 7), Assert.Single(log.Usages));
        Assert.Equal((42L, 11, 7), Assert.Single(sink.Records));
        Assert.Single(log.Infos);                                  // LogForwarded vẫn đúng 1 Info
    }

    [Fact]
    public async Task ForwardAsync_WhenIncludeUsageButNoUsage_LogsDebugAndStillHandled()
    {
        var log = new CapturingLog();
        var sut = Create(new StubUpstream(() => Upstream(200, "data: [DONE]\n\n", "text/event-stream")), log);
        var ctx = Ctx();
        var provider = SeedProvider();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(StreamJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Single(log.Debugs);
        Assert.Empty(log.Usages);
        Assert.Single(log.Infos);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstreamIgnoresForcedStream_LogsDebugMissingUsage()
    {
        // Client non-stream → protocol force stream:true + include_usage (expectsUsage=true);
        // upstream bỏ qua, trả JSON không usage → Debug "thiếu usage" là telemetry đúng
        var log = new CapturingLog();
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx();
        var provider = SeedProvider();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), ProxyProtocols.Chat, default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Single(log.Debugs);
        Assert.Empty(log.Usages);
        Assert.Single(log.Infos);
    }

    /// <summary>
    /// Content SSE có stream nổ sau chunk đầu — mô phỏng upstream/client đứt giữa chừng
    /// (spec api-monitor §7 "SSE lỗi giữa chừng").
    /// </summary>
    private sealed class BrokenSseContent : HttpContent
    {
        public BrokenSseContent() =>
            Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");

        // Đọc qua CreateContentReadStreamAsync (ReadAsStreamAsync) — trả thẳng stream hỏng,
        // không buffer qua SerializeToStreamAsync (tránh nuốt lỗi vào LoadIntoBuffer)
        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new BrokenSseStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class BrokenSseStream : Stream
    {
        private const string FirstChunk = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n";
        private bool _delivered;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            Deliver(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            ValueTask.FromResult(Deliver(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken ct) =>
            Task.FromResult(Deliver(buffer.AsSpan(offset, count)));

        private int Deliver(Span<byte> buffer)
        {
            // Chunk đầu forward được cho client (HasStarted = true), chunk kế nổ →
            // dispatcher map Aborted (row vàng) đúng như runtime thật
            if (_delivered)
                throw new IOException("upstream stream broke mid-chunk");
            _delivered = true;
            var bytes = Encoding.UTF8.GetBytes(FirstChunk);
            bytes.CopyTo(buffer);
            return bytes.Length;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// Content SSE nổ ngay lần đọc đầu — upstream chết TRƯỚC khi gửi byte content nào
    /// (mô phỏng RST/ResponseEnded trước event đầu, spec exhaustive-failover §3.1).
    /// </summary>
    private sealed class DeadOnFirstReadSseContent : HttpContent
    {
        public DeadOnFirstReadSseContent() =>
            Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new DeadStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class DeadStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new IOException("upstream reset before content");

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
                throw new IOException("upstream reset before content");

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken ct) =>
                throw new IOException("upstream reset before content");

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
        }
    }

    /// <summary>
    /// Content SSE im lặng vô hạn — tôn trọng ct để watchdog timer cắt được ngay (không thì
    /// test treo). deliverFirstChunk=false: chết ngay từ lần đọc đầu (pre-content idle);
    /// =true: trôi được 1 chunk rồi im lặng (mid-content idle).
    /// </summary>
    private sealed class BlockingSseContent : HttpContent
    {
        private readonly bool _deliverFirstChunk;

        public BlockingSseContent(bool deliverFirstChunk)
        {
            _deliverFirstChunk = deliverFirstChunk;
            Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new BlockingSseStream(_deliverFirstChunk));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class BlockingSseStream(bool deliverFirstChunk) : Stream
        {
            private static readonly byte[] FirstChunk =
                Encoding.UTF8.GetBytes("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n");
            private bool _delivered;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
                CancellationToken ct = default)
            {
                if (deliverFirstChunk && !_delivered)
                {
                    _delivered = true;
                    FirstChunk.CopyTo(buffer.Span);
                    return FirstChunk.Length;
                }

                // Im lặng vô hạn cho tới khi bị hủy — tôn trọng ct để CancelAfter cắt được ngay
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken ct) =>
                ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException("Chỉ hỗ trợ async read.");

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task ForwardAsync_WhenTeeFailsMidStream_RethrowsAndRecordsNetworkKind()
    {
        var log = new CapturingLog();
        var monitor = new ApiMonitorStore(new TraceFeed(log), log, TimeProvider.System);
        var sut = Create(new StubUpstream(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new BrokenSseContent(),
        }), log, monitor: monitor);
        var ctx = Ctx();
        ctx.Items[ClientKeyItems.RequestId] = "req-mid";
        // Stream client (đã qua flush point) → dest là pipe writer: chunk đầu ĐÃ tới client
        // trước khi nổ → không retry được (đã có byte trên wire), giữ IOException rethrow như cũ
        ctx.Items[SsePipeItems.Key] = new Pipe();
        var provider = SeedProvider();

        // Rethrow — không đổi hành vi pipeline: dispatcher vẫn map HasStarted → Aborted như cũ.
        // ThrowsAny (không phải Throws): watchdog bọc IOException gốc thành UpstreamStreamDiedException
        // (vẫn kế thừa IOException) — nhóm lỗi mạng giữ nguyên, chỉ type cụ thể đổi có chủ đích
        await Assert.ThrowsAnyAsync<IOException>(() =>
            sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson),
                AccountIdOf(provider), ProxyProtocols.Chat, default));

        // RecordResponse chạy SAU tee nên TTFT/ResponseBody mất; RecordError(0) bọc quanh tee
        // để popup có FailureKind=network thay vì trống (spec api-monitor §7)
        var record = monitor.Find("req-mid");
        Assert.NotNull(record);
        Assert.Equal("network", record.FailureKind);
        Assert.Null(record.Status);
        Assert.Null(record.ResponseBody);
        Assert.Null(record.FirstTokenAt);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstreamDiesBeforeContent_ReturnsProviderNetworkFatal()
    {
        var log = new CapturingLog();
        var monitor = new ApiMonitorStore(new TraceFeed(log), log, TimeProvider.System);
        var sut = Create(new StubUpstream(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new DeadOnFirstReadSseContent(),
        }), log, monitor: monitor);
        var ctx = Ctx();
        ctx.Items[ClientKeyItems.RequestId] = "req-pre";
        var provider = SeedProvider();

        // Chết TRƯỚC byte content nào → Fatal(Provider, Status null) — dispatcher retry
        // transient rồi failover (contract như lỗi kết nối lúc gửi), không abort request
        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson),
            AccountIdOf(provider), ProxyProtocols.Chat, default);

        var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
        Assert.Equal(FailoverLevel.Provider, fatal.Level);
        Assert.Null(fatal.Status);
        Assert.Empty(fatal.Body);

        var record = monitor.Find("req-pre");
        Assert.NotNull(record);
        Assert.Equal("network", record.FailureKind);
        Assert.Null(record.Status);

        Assert.Contains(log.Errors, m => m.Contains("cắt kết nối trước khi gửi content"));
    }

    // Timeout 8s: GREEN cần ~1s (watchdog) + biên; RED (chưa bọc watchdog) sẽ treo tới timeout
    [Fact(Timeout = 8000)]
    public async Task ForwardAsync_WhenUpstreamIdleBeforeContent_CancelsUpstreamAndReturnsFatal()
    {
        // Upstream IM LẶNG vô hạn trước byte content đầu (không RST) — watchdog idle-read cắt,
        // map thành nhóm lỗi mạng → Fatal(Provider) để dispatcher retry/failover
        var log = new CapturingLog();
        var monitor = new ApiMonitorStore(new TraceFeed(log), log, TimeProvider.System);
        var settings = new StubSettings { UpstreamIdleTimeoutSec = 1 };
        var upstream = new StubUpstream(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new BlockingSseContent(deliverFirstChunk: false),
        });
        var sut = Create(upstream, log, monitor: monitor, settings: settings);
        var ctx = Ctx();
        ctx.Items[ClientKeyItems.RequestId] = "req-idle-pre";
        var provider = SeedProvider();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson),
            AccountIdOf(provider), ProxyProtocols.Chat, default);

        var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
        Assert.Equal(FailoverLevel.Provider, fatal.Level);
        // Cancel-before-error: token upstream bị hủy TRƯỚC khi outcome rời handler
        Assert.True(upstream.LastCt.IsCancellationRequested,
            "upstream token phải bị hủy trước khi trả outcome cho endpoint.");
        var record = monitor.Find("req-idle-pre");
        Assert.NotNull(record);
        Assert.Equal("network", record.FailureKind);
        Assert.Contains(log.Errors, m => m.Contains("cắt kết nối trước khi gửi content"));
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstreamDiesMidStream_CancelsUpstreamBeforeRethrow()
    {
        // Chết giữa chừng sau khi đã trôi chunk đầu → rethrow như cũ, nhưng upstream PHẢI bị
        // hủy trước khi exception rời handler (endpoint ghi lỗi in-band sau đó)
        var log = new CapturingLog();
        var monitor = new ApiMonitorStore(new TraceFeed(log), log, TimeProvider.System);
        var upstream = new StubUpstream(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new BrokenSseContent(),
        });
        var sut = Create(upstream, log, monitor: monitor);
        var ctx = Ctx();
        ctx.Items[ClientKeyItems.RequestId] = "req-cancel-mid";
        ctx.Items[SsePipeItems.Key] = new Pipe();
        var provider = SeedProvider();

        await Assert.ThrowsAnyAsync<IOException>(() =>
            sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson),
                AccountIdOf(provider), ProxyProtocols.Chat, default));

        Assert.True(upstream.LastCt.IsCancellationRequested,
            "upstream token phải bị hủy trước khi exception rời handler.");
        var record = monitor.Find("req-cancel-mid");
        Assert.NotNull(record);
        Assert.Equal("network", record.FailureKind);
    }

    // Timeout 8s: GREEN cần ~1s (watchdog) + biên; RED (chưa bọc watchdog) sẽ treo tới timeout
    [Fact(Timeout = 8000)]
    public async Task ForwardAsync_WhenUpstreamIdleMidStream_CancelsUpstreamAndRethrows()
    {
        // Trôi chunk đầu rồi IM LẶNG vô hạn — watchdog cắt giữa chừng → IOException (không
        // retry được vì client đã nhận byte) + cancel upstream trước khi rethrow
        var log = new CapturingLog();
        var monitor = new ApiMonitorStore(new TraceFeed(log), log, TimeProvider.System);
        var settings = new StubSettings { UpstreamIdleTimeoutSec = 1 };
        var upstream = new StubUpstream(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new BlockingSseContent(deliverFirstChunk: true),
        });
        var sut = Create(upstream, log, monitor: monitor, settings: settings);
        var ctx = Ctx();
        ctx.Items[ClientKeyItems.RequestId] = "req-idle-mid";
        ctx.Items[SsePipeItems.Key] = new Pipe();
        var provider = SeedProvider();

        var ex = await Assert.ThrowsAnyAsync<IOException>(() =>
            sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson),
                AccountIdOf(provider), ProxyProtocols.Chat, default));
        Assert.IsType<UpstreamIdleTimeoutException>(ex);

        Assert.True(upstream.LastCt.IsCancellationRequested,
            "upstream token phải bị hủy trước khi exception rời handler.");
        var record = monitor.Find("req-idle-mid");
        Assert.NotNull(record);
        Assert.Equal("network", record.FailureKind);
    }

    [Fact]
    public async Task ForwardAsync_WhenErrorBodyLongMultiByte_TruncatesBytesBeforeDecode()
    {
        var log = new CapturingLog();
        var monitor = new ApiMonitorStore(new TraceFeed(log), log, TimeProvider.System);
        // ~80KB bytes nhưng chỉ ~40k chars — cap phải tính theo BYTE trước khi decode
        var longError = "{\"error\":{\"message\":\"" + new string('é', 40000) + "\"}}";
        var sut = Create(new StubUpstream(() => Upstream(400, longError)), log, monitor: monitor);
        var ctx = Ctx();
        ctx.Items[ClientKeyItems.RequestId] = "req-err";
        var provider = SeedProvider();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson),
            AccountIdOf(provider), ProxyProtocols.Chat, default);

        Assert.IsType<DispatchOutcome.Passthrough>(outcome);
        var record = monitor.Find("req-err")!;
        Assert.NotNull(record.ErrorBody);
        Assert.EndsWith("[truncated]", record.ErrorBody);
        Assert.Equal(1, record.ErrorBody.Split("[truncated]").Length - 1);
        Assert.True(record.ErrorBody.Length <= 64 * 1024 + "[truncated]".Length,
            $"ErrorBody.Length = {record.ErrorBody.Length} vượt cap 64KB + marker");
    }

    [Fact]
    public async Task ForwardAsync_WhenOpenCodeModelCallsDecoyTool_LogsCorrelatedWarning()
    {
        // Client không khai tools → fingerprint chèn 4 decoy qua gate; model gọi "read" là
        // TOOL MA client không đăng ký (opencode sẽ báo "invalid tool" khó hiểu). Response
        // giữ nguyên (hướng B) — chỉ thêm Warning chẩn đoán gắn RequestId.
        const string sse =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\"," +
            "\"function\":{\"name\":\"read\",\"arguments\":\"{}\"}}]}," +
            "\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var log = new CapturingLog();
        var sut = Create(new StubUpstream(() => Upstream(200, sse, "text/event-stream")), log);
        var ctx = Ctx();
        ctx.Items[ClientKeyItems.RequestId] = "req-decoy";
        var provider = SeedOpenCodeProvider();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson),
            AccountIdOf(provider), ProxyProtocols.Chat, default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Contains(log.Entries, e =>
            e.Severity == LogSeverity.Warning && e.RequestId == "req-decoy"
            && e.Message.Contains("read"));
    }

    [Fact]
    public async Task ForwardAsync_WhenOpenCodeModelCallsClientTool_LogsNoWarning()
    {
        // Client tự khai "bash" (không phải decoy) → model gọi bình thường, không cảnh báo
        const string body =
            """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}],"tools":[{"type":"function","function":{"name":"bash","description":"d","parameters":{"type":"object","properties":{}}}}]}""";
        const string sse =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\"," +
            "\"function\":{\"name\":\"bash\",\"arguments\":\"{}\"}}]}," +
            "\"finish_reason\":\"tool_calls\"}]}\n\n" +
            "data: [DONE]\n\n";
        var log = new CapturingLog();
        var sut = Create(new StubUpstream(() => Upstream(200, sse, "text/event-stream")), log);
        var ctx = Ctx();
        ctx.Items[ClientKeyItems.RequestId] = "req-notdecoy";
        var provider = SeedOpenCodeProvider();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(body),
            AccountIdOf(provider), ProxyProtocols.Chat, default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Empty(log.Warns);
    }
}
