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
/// Integration 3C (spec §6.2): failover 429→2xx, exhaustion passthrough/502, gate 503
/// (enqueue), combo skip model ManualRetry — đi qua endpoint + dispatcher + store thật.
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
        // Capture mọi log Write — assert theo nội dung: gate Warn §5 là dấu hiệu phân biệt
        // endpoint-gate với walk-rỗng 503 của dispatcher (status giống nhau)
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

    private void SeedProvider(string name, int maxConcurrent, params string[] modelIds)
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
        provider.Accounts.Add(new ProviderAccount
        {
            Name = "a1",
            Enabled = true,
            ApiKeyEncrypted = _protector.Protect("sk-live"),
        });
        db.Providers.Add(provider);
        db.SaveChanges();
    }

    private long ModelKey(string modelId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        return db.Models.Single(m => m.ModelId == modelId).Id;
    }

    private void SeedCombo(string name, ComboMode mode, params (int Position, long ModelId)[] items)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var combo = new Combo { Name = name, Mode = mode };
        foreach (var (position, modelId) in items)
            combo.Items.Add(new ComboItem { Position = position, TargetModelId = modelId });
        db.Combos.Add(combo);
        db.SaveChanges();
    }

    private sealed class ScriptedUpstream(Func<Provider, HttpResponseMessage> factory) : IUpstreamClient
    {
        public int Calls;

        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            // Factory throw được (mạng giả) — ném đồng bộ, handler bắt trong try có sẵn
            return Task.FromResult(factory(provider));
        }
    }

    // Call#1 giữ tới khi Release (request 2 kịp vào queue); sau Release trả 404 model_not_found
    // → Fatal cấp Model → park — model "tự chết" giữa 2 request
    private sealed class GatedFatalUpstream : IUpstreamClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _entered.TrySetResult();
                await _release.Task;
            }
            return ModelNotFound404();
        }
    }

    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"x\":1}\n\ndata: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage ModelNotFound404() => new(HttpStatusCode.NotFound)
    {
        Content = new StringContent(
            """{"error":{"code":"model_not_found","message":"The model 'm1' does not exist"}}""",
            Encoding.UTF8, "application/json"),
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

    private async Task<string> WaitForQueuedIdAsync(string model)
    {
        var queue = _app!.Services.GetRequiredService<IRequestQueue>();
        for (var i = 0; i < 100; i++)
        {
            var hit = queue.Snapshot().FirstOrDefault(r => r.Model == model);
            if (hit is not null)
                return hit.Id;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Request '{model}' không vào queue trong 5s.");
    }

    [Fact]
    public async Task Chat_WhenFirstProvider429_SecondProviderSucceeds_ClientSeesOnly200()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(p => p.Name == "p1" ? Resp429() : Sse());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Failover trong 1 request: client chỉ thấy 200 — 429 của p1 không lọt ra ngoài (§3.2)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data:", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, upstream.Calls);
        Assert.Contains(_messages, m =>
            m.Contains("Chuyển candidate kế") && m.Contains("HTTP 429")); // Warn §5
    }

    [Fact]
    public async Task Chat_WhenAllProviders429_PassesLastResponseThroughWithRetryAfterHeader()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(p => p.Name == "p1"
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
        var upstream = new ScriptedUpstream(_ => throw new HttpRequestException("connection refused"));
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Toàn lỗi mạng → attempt cuối là mạng → 502 y như 3A (§3.3)
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("Upstream provider request failed", error.GetProperty("message").GetString());
        Assert.Equal(2, upstream.Calls);
    }

    [Fact]
    public async Task Chat_WhenModelParkedByFatal_RejectsNextRequestWith503BeforeQueue()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(_ => ModelNotFound404());
        var client = await StartAsync(upstream);

        var first = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode); // Fatal → exhaustion passthrough attempt cuối
        Assert.Equal(1, upstream.Calls);

        var second = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Gate (§3.3): 503 TRƯỚC khi vào queue — log Warn là assertion phân biệt
        // với walk-rỗng 503 của dispatcher (status/message giống hệt, không log)
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        var error = (await ReadJson(second)).GetProperty("error");
        Assert.Equal("The model 'm1' is temporarily unavailable",
            error.GetProperty("message").GetString());
        Assert.Equal(1, upstream.Calls);
        Assert.Contains(_messages, m =>
            m.Contains("Từ chối request mới") && m.Contains("'m1'"));
        Assert.Empty(_app!.Services.GetRequiredService<IRequestQueue>().Snapshot());

        // Model parked thật trong store share với UI/ping
        Assert.True(_app.Services.GetRequiredService<IManualRetryStore>().IsModelParked("m1"));
    }

    [Fact]
    public async Task Chat_WhenComboHasParkedModel_SkipsDeadModelAndServesHealthyOne()
    {
        SeedProvider("p1", maxConcurrent: 4, "mA");
        SeedProvider("p2", maxConcurrent: 4, "mB");
        SeedCombo("combo-1", ComboMode.Fallback, (0, ModelKey("mA")), (1, ModelKey("mB")));
        var upstream = new ScriptedUpstream(p => p.Name == "p1" ? Resp429() : Sse());
        var client = await StartAsync(upstream);

        // Pre-park mA qua store singleton DI — dispatcher dùng đúng instance (Task 4 if-absent)
        var store = _app!.Services.GetRequiredService<IManualRetryStore>();
        store.Park(ManualRetryLevel.Model, 0, "mA", ManualRetryReason.ModelNotFound);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("combo-1"));

        // Walk filter loại mA từ đầu — chỉ mB@p2 được serve, không advance (§3.4)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, upstream.Calls);
        Assert.DoesNotContain(_messages, m => m.Contains("Chuyển candidate kế"));
    }

    [Fact]
    public async Task Chat_WhenModelParksWhileRequestQueued_QueuedGets503AndNewRejectedAtGate()
    {
        SeedProvider("p1", maxConcurrent: 1, "m1");
        var upstream = new GatedFatalUpstream();
        var client = await StartAsync(upstream);

        var serving = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // call#1 giữ trọn slot
        var queued = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        await WaitForQueuedIdAsync("m1"); // request 2 nằm trong queue, chưa dispatch (slot kín)

        upstream.Release();
        var first = await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode); // Fatal → exhaustion passthrough

        // Park chạy TRƯỚC Exit (Task 4) → model parked trước khi wake dispatch request kế
        var store = _app!.Services.GetRequiredService<IManualRetryStore>();
        Assert.True(store.IsModelParked("m1"));

        // Request 2 đi dispatcher: walk rỗng (chưa thử gì) → 503, không log gate
        var second = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        var walkError = (await ReadJson(second)).GetProperty("error");
        Assert.Equal("The model 'm1' is temporarily unavailable",
            walkError.GetProperty("message").GetString());

        // Request 3 bị chặn tại endpoint gate (log Warn phân biệt với walk-rỗng)
        var third = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);

        Assert.Contains(_messages, m =>
            m.Contains("Từ chối request mới") && m.Contains("'m1'"));
        Assert.Equal(1, upstream.Calls); // không ai gọi upstream sau khi model parked
        Assert.Empty(_app!.Services.GetRequiredService<IRequestQueue>().Snapshot());
    }

    [Fact]
    public async Task Chat_AfterManualUnpark_ServesRequestsAgain()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        var failUpstream = true; // closure — đổi được giữa chừng (upstream "phục hồi")
        var upstream = new ScriptedUpstream(_ => failUpstream ? ModelNotFound404() : Sse());
        var client = await StartAsync(upstream);

        var first = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        Assert.Equal(HttpStatusCode.NotFound, first.StatusCode); // passthrough attempt cuối
        var store = _app!.Services.GetRequiredService<IManualRetryStore>();
        Assert.True(store.IsModelParked("m1"));

        // Model parked → request mới bị gate chặn
        var blocked = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Equal(1, upstream.Calls);

        // Upstream phục hồi + user bấm [Retry now] (Task 8) → Unpark → serve lại
        failUpstream = false;
        store.Unpark(ManualRetryLevel.Model, 0, "m1");

        var second = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(2, upstream.Calls);
    }

    [Fact]
    public async Task Chat_WhenAccountParkedByFatal401_NextRequestFailsOverToOtherProvider()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(p => p.Name == "p1"
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("""{"error":{"message":"Incorrect API key"}}""",
                    Encoding.UTF8, "application/json"),
            }
            : Sse());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // 401 @p1 → Fatal cấp Account → park account + advance p2 — client thấy 200 (§1.3 #4)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, upstream.Calls);
        Assert.Contains(_messages, m =>
            m.Contains("Chuyển candidate kế") && m.Contains("HTTP 401"));
        var store = _app!.Services.GetRequiredService<IManualRetryStore>();
        Assert.Contains(store.GetEntries(), e =>
            e.Level == ManualRetryLevel.Account && e.Reason == ManualRetryReason.Unauthorized);

        // Request kế: p1 không còn TK dùng được → chỉ p2 serve (nếu p1 còn được chọn sẽ là 5 call)
        var second = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(3, upstream.Calls);
    }

    [Fact]
    public async Task Chat_WhenNetworkErrorParksProvider_NextRequestFailsOverToOtherProvider()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(p => p.Name == "p1"
            ? throw new HttpRequestException("connection refused")
            : Sse());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Mạng @p1 → Fatal(Provider) → park provider + advance p2 — client thấy 200 (§3.2)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, upstream.Calls);
        Assert.Contains(_messages, m =>
            m.Contains("Chuyển candidate kế") && m.Contains("lỗi mạng"));
        var store = _app!.Services.GetRequiredService<IManualRetryStore>();
        Assert.Contains(store.GetEntries(), e =>
            e.Level == ManualRetryLevel.Provider && e.Reason == ManualRetryReason.Unreachable);

        // Request kế: p1 parked bị loại từ đầu → chỉ p2 (bug sẽ ra 5 call)
        var second = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(3, upstream.Calls);
    }
}
