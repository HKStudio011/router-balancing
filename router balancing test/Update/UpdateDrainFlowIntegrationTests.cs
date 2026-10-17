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
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;
using RouterBalancing.Core.Update;

namespace router_balancing_test.Update;

/// <summary>
/// Integration e2e (spec §12): drain flow end-to-end trên TestServer thật —
/// enqueue 2 request → <see cref="DrainGate.BeginDrain"/> → dispatcher ngưng nhận
/// request mới → in-flight đang serve chạy hết về 0 → <see cref="QueueSnapshotStore"/>
/// ghi đúng 1 item còn lại trong queue → <see cref="QueueSnapshotRestorer"/> round-trip
/// trên container mới trả item về queue. Đây là mắt xích còn thiếu: các test đơn lẻ
/// chỉ phủ từng nửa (coordinator với FakeProxyHost; dispatcher/restorer có test riêng).
/// </summary>
public class UpdateDrainFlowIntegrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly ClientKeyService _clientKeys;
    private readonly DpapiSecretProtector _protector = new();
    private readonly DrainGate _drain = new();
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"update-drain-e2e-{Guid.NewGuid():N}");
    private WebApplication? _app;
    private HttpClient? _client;

    private string SnapshotPath => Path.Combine(_directory, "queue-snapshot.json");

    public UpdateDrainFlowIntegrationTests()
    {
        Directory.CreateDirectory(_directory);
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
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private void SeedSaturatedProvider()
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = "p-drain",
            BaseUrl = "https://api.openai.com",
            Type = ProviderType.OpenAI,
            MaxConcurrent = 1, // đúng 1 slot — request 2 bắt buộc nằm trong queue
        };
        provider.Models.Add(new Model { ModelId = "m1", Enabled = true });
        provider.Accounts.Add(new ProviderAccount
        {
            Name = "a1",
            Enabled = true,
            ApiKeyEncrypted = _protector.Protect("sk-live"),
        });
        db.Providers.Add(provider);
        db.SaveChanges();
    }

    /// <summary>Gated upstream — giữ request đầu cho tới khi test Release (in-flight thật).</summary>
    private sealed class GatedUpstream : IUpstreamClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls;
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<HttpResponseMessage> PostAsync(
            Provider provider, string apiKey, string path, byte[] body, string idempotencyKey, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            _entered.TrySetResult();
            await _release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: {\"x\":1}\n\ndata: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream"),
            };
        }
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
        // Đăng ký TRƯỚC ConfigureServices: instance DrainGate dùng chung với test —
        // BeginDrain của "update" phải thấy đúng DispatcherLoop đang chạy trong container này
        // (ConfigureServices có fallback singleton chỉ khi chưa đăng ký)
        builder.Services.AddSingleton(_drain);

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

    private static StringContent ChatBody(string model) =>
        new($"{{\"model\":\"{model}\",\"messages\":[{{\"role\":\"user\"}}]}}",
            Encoding.UTF8, "application/json");

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

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), $"điều kiện không đạt trong 5s: {because}");
    }

    [Fact]
    public async Task Drain_SnapshotRestore_RoundTripsRemainingQueuedRequest()
    {
        SeedSaturatedProvider();
        var upstream = new GatedUpstream();
        var client = await StartAsync(upstream);

        // 2 request: A giữ trọn slot MaxConcurrent=1, B nằm trong queue
        var requestA = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var requestB = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        var queuedId = await WaitForQueuedIdAsync("m1");

        // Update bắt đầu — đóng gate dispatch
        _drain.BeginDrain();
        Assert.True(_drain.IsDraining);

        var queue = _app!.Services.GetRequiredService<IRequestQueue>();
        var executions = _app.Services.GetRequiredService<IExecutionList>();

        // In-flight đang serve chạy hết về 0; dispatcher KHÔNG được Take B khi đang drain
        upstream.Release();
        var okA = await requestA.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, okA.StatusCode);
        await WaitUntilAsync(() => executions.TotalInFlight == 0, "in-flight phải drain về 0");
        Assert.True(queue.Contains(queuedId), "B phải còn trong queue khi drain xong — dispatcher không nhận request mới");
        Assert.Equal(1, upstream.Calls); // B chưa bao giờ tới upstream

        // Gate 2 xong (download giả lập đã xong) → ghi snapshot — đúng seam
        // ProxyHost.TryWriteQueueSnapshotAsync (resolve IRequestQueue + SaveAsync)
        Assert.True(_drain.IsDraining);
        await QueueSnapshotStore.SaveAsync(queue.Snapshot(), SnapshotPath);

        var snapshot = await QueueSnapshotStore.LoadAsync(SnapshotPath);
        Assert.NotNull(snapshot);
        var remaining = Assert.Single(snapshot!.Requests);
        Assert.Equal(queuedId, remaining.Id);
        Assert.Equal("m1", remaining.Model);

        // "App mới" — container queue trống hoàn toàn, restorer thật đọc file + enqueue
        var restoredQueue = new RequestQueue();
        var restorer = new QueueSnapshotRestorer(restoredQueue, new StubMonitor(), new StubTrace(), new NullLog());
        await restorer.RestoreAsync(SnapshotPath);

        var restored = Assert.Single(restoredQueue.Snapshot());
        Assert.Equal(queuedId, restored.Id);
        Assert.Equal("m1", restored.Model);
        Assert.False(File.Exists(SnapshotPath)); // restorer luôn xoá file đã thấy

        // Dọn: mở lại gate để B được serve bình thường —Dispose không treo vì endpoint còn sống
        _drain.CancelDrain();
        var okB = await requestB.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, okB.StatusCode);
        Assert.Equal(2, upstream.Calls);
    }

    /// <summary>IApiMonitorStore tối thiểu cho restorer — chỉ ghi nhận StartRequest.</summary>
    private sealed class StubMonitor : IApiMonitorStore
    {
        public event Action Changed { add { } remove { } }

        public long TodayTokens => 0;

        public IReadOnlyList<ApiCallRecord> Snapshot() => [];

        public ApiCallRecord? Find(string requestId) => null;

        public void StartRequest(string requestId, string model, byte[] promptBody)
        {
        }

        public void RecordResponse(string requestId, int? promptTokens, int? completionTokens,
            DateTimeOffset? firstTokenAt, string? responseBody)
        {
        }

        public void RecordError(string requestId, int status, string? errorBody)
        {
        }
    }

    /// <summary>ITraceFeed tối thiểu cho restorer — nuốt mọi Publish.</summary>
    private sealed class StubTrace : ITraceFeed
    {
        public event Action<TraceEvent>? Published { add { } remove { } }

        public void Publish(TraceEvent e)
        {
        }

        public IReadOnlyList<TraceEvent> Snapshot() => [];

        public void PurgeAll()
        {
        }
    }
}
