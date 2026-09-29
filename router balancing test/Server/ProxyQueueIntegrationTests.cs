using System.Net;
using System.Net.Http.Json;
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
/// Integration 3B (spec §5): saturation → park, cancel queued → 400 gốc,
/// snapshot priority/timing — đi qua endpoint thật + dispatcher thật.
/// </summary>
public class ProxyQueueIntegrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly DpapiSecretProtector _protector = new();
    private WebApplication? _app;
    private HttpClient? _client;

    public ProxyQueueIntegrationTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory, new DpapiSecretProtector());
        _log = new LogService(factory);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _log.Dispose();
        _settings.Dispose();
        _db.Dispose();
    }

    private void SeedProvider(int maxConcurrent, params string[] modelIds)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = "p-main",
            BaseUrl = "https://api.openai.com",
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

    private sealed class StubUpstream : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct) =>
            Task.FromResult(Sse());
    }

    private sealed class GatedUpstream : IUpstreamClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls;
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            _entered.TrySetResult();
            await _release.Task;
            return Sse();
        }
    }

    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"x\":1}\n\ndata: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

    private async Task<HttpClient> StartAsync(IUpstreamClient upstream)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAppSettingsService>(_settings);
        builder.Services.AddSingleton<ILogService>(_log);
        builder.Services.AddSingleton<IDbContextFactory<RouterBalancingDbContext>>(_db.CreateFactory());

        ProxyApp.ConfigureServices(builder, _protector);
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

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, string id) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/v1/requests/{id}/cancel"));

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

    private async Task<string> FindRequestIdByStateAsync(HttpClient client, string state)
    {
        var response = await client.GetAsync("/v1/requests");
        var requests = (await ReadJson(response)).GetProperty("requests").EnumerateArray();
        return requests.Single(r => r.GetProperty("state").GetString() == state)
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Chat_WhenProviderSaturated_ParksSecondRequestUntilSlotFree()
    {
        SeedProvider(maxConcurrent: 1, "m1");
        var upstream = new GatedUpstream();
        var client = await StartAsync(upstream);

        var first = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // request 1 giữ trọn slot
        var second = client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Park vô hạn (spec §1.1): KHÔNG trả 503 ngay — request 2 chờ event, không polling
        await Task.Delay(300);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, upstream.Calls); // bound: request 2 phải nằm trong queue, chưa tới upstream

        // Request 1 xong → Exit → bắn Exited → wake → request 2 được serve
        upstream.Release();
        var r1 = await first.WaitAsync(TimeSpan.FromSeconds(5));
        var r2 = await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(2, upstream.Calls);
        var id1 = r1.Headers.GetValues("X-Request-Id").Single();
        var id2 = r2.Headers.GetValues("X-Request-Id").Single();
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public async Task Cancel_QueuedRequest_OriginReceives400WithMatchedRequestId()
    {
        SeedProvider(maxConcurrent: 1, "m1");
        var upstream = new GatedUpstream();
        var client = await StartAsync(upstream);

        var serving = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Client chưa nhận header (endpoint còn chờ) — tự discover id qua snapshot (spec §3.5)
        var queuedId = await FindRequestIdByStateAsync(client, "queued");
        var cancel = await CancelAsync(client, queuedId);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.True((await ReadJson(cancel)).GetProperty("cancelled").GetBoolean());

        // Endpoint gốc await TCS → Cancelled → 400 request_cancelled, header khớp id đã huỷ
        var origin = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.BadRequest, origin.StatusCode);
        var error = (await ReadJson(origin)).GetProperty("error");
        Assert.Equal("request_cancelled", error.GetProperty("code").GetString());
        Assert.Equal(queuedId, origin.Headers.GetValues("X-Request-Id").Single());

        // Request đang phục vụ không bị ảnh hưởng — vẫn trả 200 bình thường
        upstream.Release();
        var ok = await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Snapshot_ReflectsPriorityHeaderAndTiming()
    {
        // MaxConcurrent=0 để request kẹt trong queue đủ lâu đọc snapshot
        SeedProvider(maxConcurrent: 0, "m1");
        var client = await StartAsync(new StubUpstream());

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = ChatBody("m1"),
        };
        request.Headers.Add("X-Priority", "max");
        using var abort = new CancellationTokenSource();
        var pending = client.SendAsync(request, abort.Token);
        var queuedId = await WaitForQueuedIdAsync("m1");

        var response = await client.GetAsync("/v1/requests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = (await ReadJson(response)).GetProperty("requests").EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == queuedId);
        Assert.Equal("queued", entry.GetProperty("state").GetString());
        Assert.Equal("m1", entry.GetProperty("model").GetString());
        // "max" chứ không phải "highest" — round-trip với vocab input X-Priority (chốt §10)
        Assert.Equal("max", entry.GetProperty("priority").GetString());
        Assert.True(entry.GetProperty("elapsedMs").GetInt64() >= 0);
        Assert.True(DateTimeOffset.TryParse(entry.GetProperty("enqueuedAt").GetString(), out _));
        Assert.True(entry.GetProperty("cancelable").GetBoolean());

        // Dọn có kiểm chứng (spec §3.4): client ngắt khi đang chờ → đúng dòng
        // ctx.RequestAborted.Register trong endpoint (T7) phải gỡ item + log Info — Dispose không treo
        var logged = new List<LogEntry>();
        void OnLog(LogEntry entry) => logged.Add(entry);
        _log.LogAdded += OnLog;
        try
        {
            abort.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            // Poll cho tới khi log xuất hiện — log chạy ngay sau TryRemove trong callback
            // (cùng thread), nên thấy log là chắc item đã rời queue
            var sawLog = () => logged.Exists(e => e.Message.Contains(queuedId) && e.Message.Contains("ngắt"));
            for (var i = 0; i < 100 && !sawLog(); i++)
                await Task.Delay(50);
            Assert.False(_app!.Services.GetRequiredService<IRequestQueue>().Contains(queuedId),
                "Register của endpoint phải gỡ item khỏi queue");
            Assert.Contains(logged, e => e.Message.Contains(queuedId) && e.Message.Contains("ngắt"));
        }
        finally
        {
            _log.LogAdded -= OnLog;
        }
    }
}
