using System.Net;
using System.Text;
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
/// Integration API Monitor: hook H1 (endpoint <c>StartRequest</c>) + hook 2xx/error
/// (<c>RecordResponse</c>/<c>RecordError</c> trong ForwardAsync) + feed H4 chốt State/Status.
/// Khác harness khác đúng 1 chỗ: pre-register cả <see cref="TraceFeed"/> lẫn
/// <see cref="ApiMonitorStore"/> TRƯỚC <see cref="ProxyApp.ConfigureServices"/> → fallback
/// only-if-absent nhường chỗ, test assert đúng store mà pipeline ghi vào.
/// </summary>
public class ApiMonitorIntegrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly ClientKeyService _clientKeys;
    private readonly DpapiSecretProtector _protector = new();
    private ApiMonitorStore _store = null!; // gán trong StartAsync — mọi test đều qua đó trước khi assert
    private WebApplication? _app;
    private HttpClient? _client;

    public ApiMonitorIntegrationTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory);
        _log = new LogService(factory);
        _clientKeys = new ClientKeyService(factory);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _log.Dispose();
        _settings.Dispose();
        _db.Dispose();
    }

    private void SeedProvider(int maxConcurrent, params string[] modelIds) =>
        SeedProvider("p-main", maxConcurrent, modelIds);

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
            ApiKeyEncrypted = _protector.Protect($"sk-{name}-a1"),
        });
        db.Providers.Add(provider);
        db.SaveChanges();
    }

    private sealed class StubUpstream : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct) =>
            Task.FromResult(SseWithUsage());
    }

    private sealed class GatedUpstream : IUpstreamClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            _entered.TrySetResult();
            await _release.Task;
            return Sse();
        }
    }

    /// <summary>Upstream theo script (mirror TraceIntegrationTests) — factory throw được (mạng giả).</summary>
    private sealed class ScriptedUpstream(Func<Provider, HttpResponseMessage> factory) : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct) =>
            Task.FromResult(factory(provider));
    }

    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"x\":1}\n\ndata: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

    // SSE có usage (spec api-monitor test 1): chunk thường + chunk usage 7/5 — tee parse được
    // prompt_tokens/completion_tokens, monitor phải ghi đủ 2 token
    private static HttpResponseMessage SseWithUsage() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
            "data: {\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":5}}\n\n" +
            "data: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Resp429(
        string body = """{"error":{"message":"rate limited"}}""") =>
        new(HttpStatusCode.TooManyRequests)
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

        // Instance TRƯỚC ProxyApp.ConfigureServices → fallback only-if-absent (Task 3) nhường
        // chỗ, test assert đúng feed/store mà endpoint/dispatcher ghi vào
        var feed = new TraceFeed(_log);
        var store = new ApiMonitorStore(feed, _log, TimeProvider.System);
        _store = store;
        builder.Services.AddSingleton<ITraceFeed>(feed);
        builder.Services.AddSingleton<IApiMonitorStore>(store);

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

    /// <summary>Poll kiểu WaitUntilAsync có sẵn (100×50ms) — H4 chạy sau khi client nhận response.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), because);
    }

    [Fact]
    public async Task Chat_Success_MonitorRecordHasTokensStateRouteAndResponse()
    {
        SeedProvider(maxConcurrent: 4, "m1");
        var client = await StartAsync(new StubUpstream());

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = response.Headers.GetValues("X-Request-Id").Single();
        await WaitUntilAsync(() => _store.Find(id) is { State: ApiCallState.Done },
            "H4 Finished{Success=true} phải chốt record sang Done");

        var record = _store.Find(id)!;
        Assert.Equal(7, record.PromptTokens);
        Assert.Equal(5, record.CompletionTokens);
        Assert.Equal(200, record.Status);
        Assert.True(record.Success);
        Assert.Equal("p-main", record.Provider);
        Assert.Equal("a1", record.Account);
        // Tee tách prefix "data:" khỏi payload (UsageCaptureTests) — assert payload SSE thật đã được buffer
        Assert.Contains("\"choices\"", record.ResponseBody);
        Assert.Contains("\"prompt_tokens\":7", record.ResponseBody);
        Assert.Contains("\"messages\"", record.PromptBody);
    }

    [Fact]
    public async Task Provider429ThenOk_RecordErrorClearedByFinalSuccess()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(p => p.Name == "p1" ? Resp429() : Sse());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = response.Headers.GetValues("X-Request-Id").Single();
        await WaitUntilAsync(() => _store.Find(id) is { State: ApiCallState.Done },
            "request 429→ok phải kết thúc Done, không mắc kẹt Error");

        var record = _store.Find(id)!;
        Assert.Equal(200, record.Status);
        Assert.Null(record.ErrorBody);
        // FailureKind chỉ do RecordError ghi (feed không đụng) — client chỉ thấy response cuối
        // nên đây là dấu hiệu duy nhất attempt 429 đã được hook ghi trước khi 2xx xóa ErrorBody
        Assert.Equal("http", record.FailureKind);
    }

    [Fact]
    public async Task Upstream400_MonitorRecordErrorWithBodyAndErrorState()
    {
        SeedProvider(maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"bad request"}}""",
                Encoding.UTF8, "application/json"),
        });
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var id = response.Headers.GetValues("X-Request-Id").Single();
        await WaitUntilAsync(() => _store.Find(id) is { State: ApiCallState.Error },
            "H4 Finished{Success=false} phải chốt record sang Error");

        var record = _store.Find(id)!;
        Assert.Equal(400, record.Status);
        Assert.Contains("\"error\"", record.ErrorBody);
    }

    [Fact]
    public async Task QueuedRequest_Cancel_MonitorRecordCancelled()
    {
        SeedProvider(maxConcurrent: 1, "m1");
        var upstream = new GatedUpstream();
        var client = await StartAsync(upstream);

        var serving = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // request 1 giữ trọn slot
        var queued = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        var queuedId = await WaitForQueuedIdAsync("m1");

        var cancel = await CancelAsync(client, queuedId);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);

        var origin = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.BadRequest, origin.StatusCode);
        await WaitUntilAsync(() => _store.Find(queuedId) is { State: ApiCallState.Cancelled },
            "H4 Canceled phải chốt record sang Cancelled");

        upstream.Release();
        var ok = await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task NetworkFailure_MonitorRecordNetworkError()
    {
        SeedProvider(maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(_ => throw new HttpRequestException("connection refused"));
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var id = response.Headers.GetValues("X-Request-Id").Single();
        await WaitUntilAsync(() => _store.Find(id) is { State: ApiCallState.Error },
            "H4 Finished{Success=false} phải chốt record sang Error");

        var record = _store.Find(id)!;
        // RecordError(0) ghi "network" (status 0 → không có HTTP status); feed không đụng FailureKind
        Assert.Equal("network", record.FailureKind);
    }
}
