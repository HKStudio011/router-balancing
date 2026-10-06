using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

/// <summary>
/// Integration 3C + exhaustive-account-failover (spec §6.2): walk TK→provider,
/// failover 429→2xx, exhaustion passthrough/502 — đi qua endpoint + dispatcher thật.
/// </summary>
public class ProxyRetryIntegrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly ClientKeyService _clientKeys;
    private readonly DpapiSecretProtector _protector = new();
    private readonly List<string> _messages = [];
    private WebApplication? _app;
    private HttpClient? _client;

    public ProxyRetryIntegrationTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory);
        _log = new LogService(factory);
        _clientKeys = new ClientKeyService(factory);
        // Capture mọi log Write — assert theo nội dung Warn advance (§5)
        _log.LogAdded += entry => _messages.Add(entry.Message);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _log.Dispose();
        _settings.Dispose();
        _db.Dispose();
    }

    private void SeedProvider(string name, int maxConcurrent, params string[] modelIds) =>
        SeedProvider(name, maxConcurrent, accountCount: 1, modelIds);

    /// <summary>
    /// Seed provider với <paramref name="accountCount"/> TK — key distinct theo
    /// (provider, TK) để test nhận diện upstream nhận request trên TK nào (walk §6.2).
    /// </summary>
    private void SeedProvider(string name, int maxConcurrent, int accountCount,
        params string[] modelIds)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            BaseUrl = "https://api.openai.com",
            // ComboResolver Finalize lọc OpenAI — thiếu là resolve trả rỗng
            Type = ProviderType.OpenAI,
            MaxConcurrent = maxConcurrent,
        };
        foreach (var id in modelIds)
            provider.Models.Add(new Model { ModelId = id, Enabled = true });
        for (var i = 1; i <= accountCount; i++)
        {
            provider.Accounts.Add(new ProviderAccount
            {
                Name = $"a{i}",
                Enabled = true,
                ApiKeyEncrypted = _protector.Protect($"sk-{name}-a{i}"),
            });
        }
        db.Providers.Add(provider);
        db.SaveChanges();
    }

    /// <summary>
    /// Seed combo RoundRobin trỏ tới (provider, model) — cần vì cùng model id
    /// có thể tồn tại ở 2 provider (test walk lỗi mạng với mọi model trong combo).
    /// </summary>
    private void SeedCombo(string name, params (string Provider, string ModelId)[] targets)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var combo = new Combo { Name = name, Mode = ComboMode.RoundRobin };
        var position = 0;
        foreach (var (providerName, modelId) in targets)
        {
            var modelKey = db.Models
                .Single(m => m.ModelId == modelId && m.Provider!.Name == providerName).Id;
            combo.Items.Add(new ComboItem { Position = position++, TargetModelId = modelKey });
        }
        db.Combos.Add(combo);
        db.SaveChanges();
    }

    private sealed class ScriptedUpstream(Func<Provider, string, HttpResponseMessage> factory)
        : IUpstreamClient
    {
        private readonly object _gate = new();
        private readonly List<(string Provider, string ApiKey)> _calls = [];

        public int Calls
        {
            get { lock (_gate) return _calls.Count; }
        }

        /// <summary>Lịch sử gọi theo thứ tự (provider, apiKey) — assert walk thử TK/provider nào.</summary>
        public IReadOnlyList<(string Provider, string ApiKey)> Records
        {
            get { lock (_gate) return _calls.ToArray(); }
        }

        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            // Ghi TRƯỚC khi factory throw (mạng giả) — test vẫn đếm được attempt đó
            lock (_gate)
                _calls.Add((provider.Name, apiKey));
            // Factory throw được (mạng giả) — ném đồng bộ, handler bắt trong try có sẵn
            return Task.FromResult(factory(provider, apiKey));
        }
    }

    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"x\":1}\n\ndata: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Resp429(
        string body = """{"error":{"message":"rate limited"}}""", string? retryAfter = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (retryAfter is not null)
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private static HttpResponseMessage Resp401(
        string body = """{"error":{"message":"unauthorized"}}""") =>
        new(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private async Task<HttpClient> StartAsync(IUpstreamClient upstream)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAppSettingsService>(_settings);
        builder.Services.AddSingleton<ILogService>(_log);
        builder.Services.AddSingleton<IDbContextFactory<RouterBalancingDbContext>>(_db.CreateFactory());
        builder.Services.AddSingleton<IClientKeyService>(_clientKeys);

        ProxyApp.ConfigureServices(builder, _protector, new DirectProxyPool());
        // Đăng ký SAU ConfigureServices → wins (last registration), stub thay OpenAiUpstreamClient
        builder.Services.AddSingleton(upstream);

        var app = builder.Build();
        ProxyApp.ConfigurePipeline(app);
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
        return _client;
    }

    private static StringContent Json(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static StringContent ChatBody(string model) =>
        Json($"{{\"model\":\"{model}\",\"messages\":[{{\"role\":\"user\"}}]}}");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private static StringContent StreamChatBody(string model) =>
        Json($"{{\"model\":\"{model}\",\"messages\":[{{\"role\":\"user\"}}],\"stream\":true}}");

    // SendAsync với HttpCompletionOption.ResponseHeadersRead — PostAsync mặc định buffer toàn body sẽ treo
    private static Task<HttpResponseMessage> SendStreamAsync(HttpClient client, string model) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
            { Content = StreamChatBody(model) }, HttpCompletionOption.ResponseHeadersRead);

    /// <summary>
    /// Content SSE giao byte (tùy chọn) rồi nổ IOException khi đọc tiếp — upstream đứt giữa stream.
    /// Đọc qua CreateContentReadStreamAsync (lazy) như BrokenSseContent: byte tới tee rồi mới nổ,
    /// không bị LoadIntoBuffer nuốt (phân loại #7/#8 phụ thuộc số byte thực forward).
    /// </summary>
    private sealed class ThrowingContent : HttpContent
    {
        private readonly bool _writeFirst;

        public ThrowingContent(bool writeFirst)
        {
            _writeFirst = writeFirst;
            // TeeAsync rẽ theo Content-Type: phải là text/event-stream mới đi đường
            // forward-từng-chunk; thiếu là rơi vào nhánh buffer (nuốt byte → contentBytes=0 sai)
            Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ThrowingStream(_writeFirst));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class ThrowingStream : Stream
    {
        private static readonly byte[] FirstChunk = "data: {\"x\":1}\n\n"u8.ToArray();
        private readonly bool _writeFirst;
        private bool _finished;

        public ThrowingStream(bool writeFirst) => _writeFirst = writeFirst;

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
            if (_finished)
                throw new IOException("boom");
            _finished = true;
            if (!_writeFirst)
                throw new IOException("boom");
            FirstChunk.CopyTo(buffer);
            return FirstChunk.Length;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task Chat_WhenFirstProvider429_SecondProviderSucceeds_ClientSeesOnly200()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream((p, _) => p.Name == "p1" ? Resp429() : Sse());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Failover trong 1 request: client chỉ thấy 200 — 429 của p1 không lọt ra ngoài (§3.2)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data:", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, upstream.Calls);
        Assert.Contains(_messages, m =>
            m.Contains("chuyển provider kế") && m.Contains("HTTP 429")); // Warn attempt fail §5
    }

    [Fact]
    public async Task Chat_WhenAllProviders429_PassesLastResponseThroughWithRetryAfterHeader()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream((p, _) => p.Name == "p1"
            ? Resp429("""{"error":{"message":"from-p1"}}""")
            : Resp429("""{"error":{"message":"from-p2"}}""", retryAfter: "30"));
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Exhaustion: attempt cuối (p2, cursor 0→p1 rồi advance cursor 1→p2) quyết định —
        // passthrough nguyên response đó + Retry-After parse rồi format lại cho client (§3.3/§3.6)
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("""{"error":{"message":"from-p2"}}""",
            await response.Content.ReadAsStringAsync());
        Assert.Equal("30", response.Headers.GetValues("Retry-After").Single());
        Assert.Equal(2, upstream.Calls);
    }

    [Fact]
    public async Task Chat_WhenAllProvidersFailWithNetworkError_Returns502()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream((_, _) =>
            throw new HttpRequestException("connection refused"));
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Toàn lỗi mạng → attempt cuối là mạng → 502 y như 3A (§3.3)
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("Upstream provider request failed", error.GetProperty("message").GetString());
        Assert.Equal(2, upstream.Calls);
    }

    [Fact]
    public async Task Chat_When401OnFirstAccount_SucceedsOnSecondAccountOfSameProvider()
    {
        SeedProvider("p1", maxConcurrent: 4, accountCount: 2, modelIds: ["m1"]);
        SeedProvider("p2", maxConcurrent: 4, modelIds: ["m1"]);
        // TK1 (đầu, RR tie theo Id) → 401, TK2 → 200; p2 cũng trả 200 nhưng không được đụng tới
        var firstAccountCalls = 0;
        var upstream = new ScriptedUpstream((p, _) => p.Name != "p1"
            ? Sse()
            : Interlocked.Increment(ref firstAccountCalls) == 1 ? Resp401() : Sse());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data:", await response.Content.ReadAsStringAsync());
        var records = upstream.Records;
        // Walk exhaustive (§6.2): đúng 2 attempt, cả 2 trên p1 với 2 TK khác nhau —
        // còn TK chưa thử thì KHÔNG nhảy sang provider B
        Assert.Equal(2, records.Count);
        Assert.Equal("p1", records[0].Provider);
        Assert.Equal("sk-p1-a1", records[0].ApiKey);
        Assert.Equal("p1", records[1].Provider);
        Assert.Equal("sk-p1-a2", records[1].ApiKey);
    }

    [Fact]
    public async Task Chat_WhenAllAccountsOfProviderUnauthorized_FailsOverToNextProvider()
    {
        SeedProvider("p1", maxConcurrent: 4, accountCount: 2, modelIds: ["m1"]);
        SeedProvider("p2", maxConcurrent: 4, modelIds: ["m1"]);
        var upstream = new ScriptedUpstream((p, _) => p.Name == "p1" ? Resp401() : Sse());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data:", await response.Content.ReadAsStringAsync());
        var records = upstream.Records;
        // Quét hết 2 TK của p1 (401 cả 2, không lặp lại TK nào) rồi mới sang p2 serve (§6.2)
        Assert.Equal(3, records.Count);
        Assert.Equal("p1", records[0].Provider);
        Assert.Equal("p1", records[1].Provider);
        Assert.NotEqual(records[0].ApiKey, records[1].ApiKey);
        Assert.Equal("p2", records[2].Provider);
    }

    [Fact]
    public async Task Chat_WhenProviderNetworkDead_TriesProviderOnceThenSucceedsElsewhere()
    {
        // Combo chứa 2 model của p1 — lỗi mạng fail cả provider: không quét TK, không quét model (§1.3 #3)
        SeedProvider("p1", maxConcurrent: 4, accountCount: 2, modelIds: ["m1", "m2"]);
        SeedProvider("p2", maxConcurrent: 4, modelIds: ["m1"]);
        SeedCombo("c1", ("p1", "m1"), ("p1", "m2"), ("p2", "m1"));
        var upstream = new ScriptedUpstream((p, _) => p.Name == "p1"
            ? throw new HttpRequestException("connection refused")
            : Sse());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("c1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var records = upstream.Records;
        // Đúng 1 lần network tới p1 (mọi model/TK trong combo) rồi request qua p2 serve (§6.2)
        Assert.Equal(2, records.Count);
        Assert.Equal("p1", records[0].Provider);
        Assert.Equal("p2", records[1].Provider);
    }

    [Fact]
    public async Task Chat_WhenAllUpstreamFail_PassesThroughLastResponse()
    {
        SeedProvider("p1", maxConcurrent: 4, accountCount: 2, modelIds: ["m1"]);
        SeedProvider("p2", maxConcurrent: 4, accountCount: 2, modelIds: ["m1"]);
        var upstream = new ScriptedUpstream((p, _) =>
            Resp401(JsonSerializer.Serialize(new { error = new { message = $"from-{p.Name}" } })));
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Exhaustion (§4): attempt cuối (p2/TK2) quyết định — passthrough nguyên response cuối
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("""{"error":{"message":"from-p2"}}""",
            await response.Content.ReadAsStringAsync());
        var records = upstream.Records;
        // Quét hết 4 TK của 2 provider — đúng 4 attempt, không lặp lại TK nào
        Assert.Equal(4, records.Count);
        Assert.Equal("p1", records[0].Provider);
        Assert.Equal("p1", records[1].Provider);
        Assert.NotEqual(records[0].ApiKey, records[1].ApiKey);
        Assert.Equal("p2", records[2].Provider);
        Assert.Equal("p2", records[3].Provider);
        Assert.NotEqual(records[2].ApiKey, records[3].ApiKey);
    }

    [Fact]
    public async Task Chat_WhenAllUpstreamFail_Returns502WhenAllNetworkFailures()
    {
        SeedProvider("p1", maxConcurrent: 4, accountCount: 2, modelIds: ["m1"]);
        SeedProvider("p2", maxConcurrent: 4, accountCount: 2, modelIds: ["m1"]);
        var upstream = new ScriptedUpstream((_, _) =>
            throw new HttpRequestException("connection refused"));
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Toàn mạng → attempt cuối là mạng → 502; mỗi provider chỉ thử 1 lần —
        // lỗi mạng là provider-wide, không quét TK (§1.3 #3/§4)
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("Upstream provider request failed", error.GetProperty("message").GetString());
        var records = upstream.Records;
        Assert.Equal(2, records.Count);
        Assert.Equal("p1", records[0].Provider);
        Assert.Equal("p2", records[1].Provider);
    }

    [Fact]
    public async Task Stream_AllUpstreamFail_PassesThroughLastResponseInBand()
    {
        // Clone nguyên setup của Chat_WhenAllUpstreamFail_PassesThroughLastResponse (Resp401,
        // from-p2, 4 attempt) — chỉ đổi sang stream:true; test gốc không có Retry-After
        // nên assert retry_after (điều kiện "nếu test gốc có") không áp dụng.
        SeedProvider("p1", maxConcurrent: 4, accountCount: 2, modelIds: ["m1"]);
        SeedProvider("p2", maxConcurrent: 4, accountCount: 2, modelIds: ["m1"]);
        var upstream = new ScriptedUpstream((p, _) =>
            Resp401(JsonSerializer.Serialize(new { error = new { message = $"from-{p.Name}" } })));
        var client = await StartAsync(upstream);

        var resp = await SendStreamAsync(client, "m1").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);

        var body = await resp.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(10));
        // Passthrough in-band: error object của attempt cuối (p2) giữ nguyên + type:error, không [DONE]
        Assert.Contains("\"type\":\"error\"", body);
        Assert.Contains("\"message\":\"from-p2\"", body);
        Assert.DoesNotContain("[DONE]", body);

        // Frame merge không có field status → xác thực logical status qua monitor (spec §3.5 row 2)
        var id = resp.Headers.GetValues("X-Request-Id").Single();
        var record = _app!.Services.GetRequiredService<IApiMonitorStore>().Find(id);
        Assert.Equal(401, record!.Status);

        var records = upstream.Records;
        Assert.Equal(4, records.Count);
        Assert.Equal("p1", records[0].Provider);
        Assert.Equal("p1", records[1].Provider);
        Assert.NotEqual(records[0].ApiKey, records[1].ApiKey);
        Assert.Equal("p2", records[2].Provider);
        Assert.Equal("p2", records[3].Provider);
        Assert.NotEqual(records[2].ApiKey, records[3].ApiKey);
    }

    [Fact]
    public async Task Stream_AllNetworkFail_Returns502InBand()
    {
        // Clone setup của Chat_WhenAllUpstreamFail_Returns502WhenAllNetworkFailures, stream:true
        SeedProvider("p1", maxConcurrent: 4, accountCount: 2, modelIds: ["m1"]);
        SeedProvider("p2", maxConcurrent: 4, accountCount: 2, modelIds: ["m1"]);
        var upstream = new ScriptedUpstream((_, _) =>
            throw new HttpRequestException("connection refused"));
        var client = await StartAsync(upstream);

        var resp = await SendStreamAsync(client, "m1").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);

        var body = await resp.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(10));
        // Error(502) map thành event in-band với status 502 trong payload (wire là 200)
        Assert.Contains("\"status\":502", body);
        Assert.Contains("Upstream provider request failed", body);
        Assert.DoesNotContain("[DONE]", body);

        var records = upstream.Records;
        Assert.Equal(2, records.Count);
        Assert.Equal("p1", records[0].Provider);
        Assert.Equal("p2", records[1].Provider);
    }

    [Fact]
    public async Task Stream_WhenUpstreamDiesBeforeContent_ReturnsServerFaultInBandAndRedRow()
    {
        SeedProvider("p-main", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ThrowingContent(writeFirst: false) });
        var client = await StartAsync(upstream);

        var resp = await SendStreamAsync(client, "m1").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);

        var body = await resp.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("\"code\":\"server_error\"", body);
        Assert.Contains("\"type\":\"error\"", body);
        Assert.DoesNotContain("[DONE]", body);

        // Override đỏ #7: Aborted mà 0 byte content (client còn nối) → Finished/Error, status 500
        var id = resp.Headers.GetValues("X-Request-Id").Single();
        var record = _app!.Services.GetRequiredService<IApiMonitorStore>().Find(id);
        Assert.Equal(ApiCallState.Error, record!.State);
        Assert.Equal(500, record.Status);
    }

    [Fact]
    public async Task Stream_WhenUpstreamDiesMidContent_ClosesSilentlyAndYellowRow()
    {
        SeedProvider("p-main", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new ThrowingContent(writeFirst: true) });
        var client = await StartAsync(upstream);

        var resp = await SendStreamAsync(client, "m1").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Đã có content → đóng stream im lặng: KHÔNG append error event, không [DONE]
        var body = await resp.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("data: {\"x\":1}\n\n", body);

        // Giữ chủ đích vàng của b0b3ae7: mid-stream fail → Canceled + FailureKind network
        var id = resp.Headers.GetValues("X-Request-Id").Single();
        var record = _app!.Services.GetRequiredService<IApiMonitorStore>().Find(id);
        Assert.Equal(ApiCallState.Cancelled, record!.State);
        Assert.Equal("network", record.FailureKind);
    }

    [Fact]
    public async Task Stream_WhenExhaustedWithRetryAfter_EmitsInBandRetryAfterWithoutDone()
    {
        // Clone setup của Chat_WhenAllProviders429_PassesLastResponseThroughWithRetryAfterHeader
        // (dòng 210 — attempt cuối p2 có Retry-After "30"), đổi sang stream:true (spec §8.5)
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream((p, _) => p.Name == "p1"
            ? Resp429("""{"error":{"message":"from-p1"}}""")
            : Resp429("""{"error":{"message":"from-p2"}}""", retryAfter: "30"));
        var client = await StartAsync(upstream);

        var resp = await SendStreamAsync(client, "m1").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);

        var body = await resp.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(10));
        // Passthrough in-band: error object cuối giữ nguyên + type:error + retry_after bù
        // header đã mất trên wire (spec §3.5); wire status không mang 429
        Assert.Contains("\"type\":\"error\"", body);
        Assert.Contains("\"message\":\"from-p2\"", body);
        Assert.Contains("\"retry_after\":\"30\"", body);
        Assert.DoesNotContain("[DONE]", body);

        // Status logical 429 qua monitor (frame merge không có field status — spec §3.5 row 3)
        var id = resp.Headers.GetValues("X-Request-Id").Single();
        var record = _app!.Services.GetRequiredService<IApiMonitorStore>().Find(id);
        Assert.Equal(429, record!.Status);

        Assert.Equal(2, upstream.Calls);
    }

    [Fact]
    public async Task Stream_WhenModelNotFound_EmitsInBandResolveErrorWithoutDone()
    {
        // Resolve fail (dispatcher báo Error 404 model_not_found) tới SAU khi head 200 đã
        // flush → lỗi phải là event in-band đúng shape SseErrorEvent.Error (spec §8.6/§3.5 row 1)
        SeedProvider("p-main", maxConcurrent: 4, "m1");
        var client = await StartAsync(new ScriptedUpstream((_, _) => Sse()));

        var resp = await SendStreamAsync(client, "nope").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("text/event-stream", resp.Content.Headers.ContentType!.MediaType);

        var body = await resp.Content.ReadAsStringAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("\"code\":\"model_not_found\"", body);
        Assert.Contains("\"status\":404", body);
        Assert.Contains("The model 'nope' does not exist", body);
        Assert.DoesNotContain("[DONE]", body);
    }
}
