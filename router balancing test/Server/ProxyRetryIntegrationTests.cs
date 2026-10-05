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
/// Integration 3C (spec §6.2): failover 429→2xx, exhaustion passthrough/502 —
/// đi qua endpoint + dispatcher thật.
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
            m.Contains("chuyển provider kế") && m.Contains("HTTP 429")); // Warn attempt fail §5
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
}
