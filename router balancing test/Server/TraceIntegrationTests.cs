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
/// Integration Live Request Trace: sequence H1 (Received) → H2 (DispatchStarted) →
/// H3a/H3b (Attempt) → H4 (Finished/Canceled) qua endpoint + dispatcher thật.
/// Khác harness ProxyQueueIntegrationTests đúng 1 chỗ: pre-register instance <see cref="TraceFeed"/>
/// TRƯỚC <see cref="ProxyApp.ConfigureServices"/> → fallback only-if-absent nhường chỗ.
/// </summary>
public class TraceIntegrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly ClientKeyService _clientKeys;
    private readonly DpapiSecretProtector _protector = new();
    private TraceFeed _feed = null!; // gán trong StartAsync — mọi test đều qua đó trước khi assert
    private WebApplication? _app;
    private HttpClient? _client;

    public TraceIntegrationTests()
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
        public Task<HttpResponseMessage> PostAsync(
            Provider provider, string apiKey, string path, byte[] body, CancellationToken ct) =>
            Task.FromResult(Sse());
    }

    private sealed class GatedUpstream : IUpstreamClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<HttpResponseMessage> PostAsync(
            Provider provider, string apiKey, string path, byte[] body, CancellationToken ct)
        {
            _entered.TrySetResult();
            await _release.Task;
            return Sse();
        }
    }

    /// <summary>Upstream theo script (mirror ProxyRetryIntegrationTests) — factory throw được (mạng giả).</summary>
    private sealed class ScriptedUpstream(Func<Provider, HttpResponseMessage> factory) : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostAsync(
            Provider provider, string apiKey, string path, byte[] body, CancellationToken ct) =>
            Task.FromResult(factory(provider));
    }

    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"x\":1}\n\ndata: [DONE]\n\n",
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
        // chỗ, test assert đúng feed mà endpoint/dispatcher publish vào
        var feed = new TraceFeed(_log);
        _feed = feed;
        builder.Services.AddSingleton<ITraceFeed>(feed);

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

    /// <summary>Event của 1 request theo THỨ TỰ PUBLISH (Snapshot = ring cũ → mới), không sort lại.</summary>
    private IReadOnlyList<TraceEvent> EventsOf(string requestId) =>
        _feed.Snapshot().Where(e => e.RequestId == requestId).ToList();

    /// <summary>Poll kiểu WaitUntilAsync có sẵn (100×50ms) — H4 có thể chạy sau khi client đã nhận response.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), because);
    }

    [Fact]
    public async Task Chat_Success_ReceivesReceivedDispatchAttemptFinishedSequence()
    {
        SeedProvider(maxConcurrent: 4, "m1");
        var upstream = new StubUpstream();
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = response.Headers.GetValues("X-Request-Id").Single();

        // Handled stream response trong ForwardAsync TRƯỚC TrySetResult — H4 (Finished) chạy sau
        // nên client có thể nhận xong response trước khi publish → poll thay vì assert ngay
        await WaitUntilAsync(() => EventsOf(id).Any(e => e.Stage == TraceStage.Finished),
            "terminal Finished phải có trong feed");

        var events = EventsOf(id);
        // Thứ tự publish guarantee: Received → DispatchStarted → Attempt → Finished
        Assert.Equal(
        [
            TraceStage.Received,
            TraceStage.DispatchStarted,
            TraceStage.Attempt,
            TraceStage.Finished,
        ], events.Select(e => e.Stage));

        Assert.Null(events[0].Route); // Received — chưa dispatch
        Assert.Null(events[1].Route); // DispatchStarted — Route null theo spec §4 H2

        var attempt = events[2];
        Assert.Equal(1, attempt.Attempt);
        Assert.False(attempt.AttemptDone);
        Assert.NotNull(attempt.Route);
        Assert.Equal("m1", attempt.Model);
        Assert.Null(attempt.Route!.Combo); // resolve theo model id → không có combo
        Assert.Equal("p-main", attempt.Route.Provider);
        Assert.Equal("a1", attempt.Route.Account);

        Assert.True(events[3].Success);
    }

    [Fact]
    public async Task QueuedRequest_Cancel_PublishesCanceled()
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

        // H4 (Canceled) publish TRƯỚC khi endpoint ghi 400 → response về = event đã có
        var origin = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.BadRequest, origin.StatusCode);
        Assert.Contains(EventsOf(queuedId), e => e.Stage == TraceStage.Canceled);

        upstream.Release();
        var ok = await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var id1 = ok.Headers.GetValues("X-Request-Id").Single();
        await WaitUntilAsync(() => EventsOf(id1).Any(e => e.Stage == TraceStage.Finished),
            "request giữ slot phải Finished");
        Assert.True(EventsOf(id1).Single(e => e.Stage == TraceStage.Finished).Success);
    }

    [Fact]
    public async Task Provider429_Failover_PublishesAttemptDoneWith429ThenNextProvider()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(p => p.Name == "p1" ? Resp429() : Sse());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = response.Headers.GetValues("X-Request-Id").Single();
        await WaitUntilAsync(() => EventsOf(id).Any(e => e.Stage == TraceStage.Finished),
            "terminal Finished phải có trong feed");

        var events = EventsOf(id).ToList();
        var fail = events.Single(e => e.Stage == TraceStage.Attempt && e.AttemptDone == true);
        Assert.Equal(429, fail.Status);
        Assert.Equal("http", fail.FailureKind);
        Assert.Equal(1, fail.Attempt);
        Assert.Equal("p1", fail.Route!.Provider);

        var secondStart = events.Single(e => e.Stage == TraceStage.Attempt
            && e.AttemptDone == false && e.Route!.Provider == "p2");
        Assert.Equal(2, secondStart.Attempt);
        // Attempt done (p1/429) phải đứng TRƯỚC attempt start của provider kế — đúng sequence
        Assert.True(events.IndexOf(fail) < events.IndexOf(secondStart),
            "Attempt{done=true} của p1 phải được publish trước Attempt{done=false} của p2");

        Assert.True(events.Last(e => e.Stage == TraceStage.Finished).Success);
    }

    [Fact]
    public async Task NetworkError_RecordsFailureKindNetwork()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(_ => throw new HttpRequestException("connection refused"));
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var id = response.Headers.GetValues("X-Request-Id").Single();
        await WaitUntilAsync(() => EventsOf(id).Any(e => e.Stage == TraceStage.Finished),
            "terminal Finished phải có trong feed");

        // parity RetryState: status null = lỗi mạng → FailureKind "network" (không phải "http");
        // 2 provider cùng ném HttpRequestException → có ít nhất 1 attempt done kiểu này
        Assert.Contains(EventsOf(id), e => e.Stage == TraceStage.Attempt
            && e.AttemptDone == true && e.FailureKind == "network" && e.Status is null);
    }
}
