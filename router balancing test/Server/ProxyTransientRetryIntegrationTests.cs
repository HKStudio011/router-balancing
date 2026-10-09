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
/// Integration transient-retry: retry 5xx tạm thời trong DispatcherLoop với backoff —
/// mirror harness của <see cref="ApiMonitorIntegrationTests"/> (feed/store pre-register
/// trước <see cref="ProxyApp.ConfigureServices"/>) để assert cả wire status lẫn record
/// monitor cuối request. Test tự Set settings transient (trước StartAsync) thay baseline 0.
/// </summary>
public class ProxyTransientRetryIntegrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly ClientKeyService _clientKeys;
    private readonly DpapiSecretProtector _protector = new();
    private ApiMonitorStore _store = null!; // gán trong StartAsync — mọi test đều qua đó trước khi assert
    private WebApplication? _app;
    private HttpClient? _client;

    public ProxyTransientRetryIntegrationTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory);
        _settings.Set(SettingsKeys.TransientMaxRetries, 0); // baseline opt-out — mỗi test opt-in riêng trước StartAsync
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

    /// <summary>Upstream theo script (mirror ApiMonitorIntegrationTests) — factory throw được (mạng giả).</summary>
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

    private static HttpResponseMessage Resp504(
        string body = """{"error":{"message":"gateway timeout"}}""") =>
        new(HttpStatusCode.GatewayTimeout)
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

        // Instance TRƯỚC ProxyApp.ConfigureServices → fallback only-if-absent nhường
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

    /// <summary>Poll kiểu WaitUntilAsync có sẵn (100×50ms) — H4 chạy sau khi client nhận response.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), because);
    }

    [Fact]
    public async Task TransientRetry_504TwiceThenOk_Returns200AndMonitorDoneWith200()
    {
        _settings.Set(SettingsKeys.TransientMaxRetries, 5);
        _settings.Set(SettingsKeys.TransientBackoffBaseMs, 250);
        SeedProvider(maxConcurrent: 4, "m1");
        var calls = 0;
        var upstream = new ScriptedUpstream(_ =>
            Interlocked.Increment(ref calls) <= 2 ? Resp504() : Sse());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, calls);                                    // 504×2 retry + attempt 3 OK
        var id = response.Headers.GetValues("X-Request-Id").Single();
        await WaitUntilAsync(() => _store.Find(id) is { State: ApiCallState.Done },
            "request 504→504→200 phải chốt Done");
        var record = _store.Find(id)!;
        Assert.Equal(200, record.Status);                          // upsert — attempt cuối thắng
        Assert.True(record.Success);
    }

    [Fact]
    public async Task TransientRetry_504Forever_PassesThroughFinal504Intact()
    {
        _settings.Set(SettingsKeys.TransientMaxRetries, 2);        // ngắn để test nhanh
        _settings.Set(SettingsKeys.TransientBackoffBaseMs, 250);
        SeedProvider(maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(_ => Resp504("""{"error":{"message":"gateway timeout"}}"""));
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Hết ngân sách → exhaustion passthrough nguyên response cuối (hành vi cũ giữ nguyên §3.4)
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Contains("gateway timeout", await response.Content.ReadAsStringAsync());
    }
}
