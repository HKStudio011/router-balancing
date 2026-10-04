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
/// Integration in-proc qua TestServer (spec §7.2): cùng pipeline với ProxyHost thật,
/// khác mỗi IUpstreamClient được stub để không phụ thuộc mạng.
/// </summary>
public class ProxyAppChatIntegrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly ClientKeyService _clientKeys;
    private readonly DpapiSecretProtector _protector = new();
    private WebApplication? _app;
    private HttpClient? _client;

    public ProxyAppChatIntegrationTests()
    {
        // TestDb chỉ tạo file trống — migrate trước khi AppSettingsService đọc
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

    private void SeedProvider(string modelId, ProviderType type = ProviderType.OpenAI)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = $"p-{modelId}",
            BaseUrl = "https://api.openai.com",
            Type = type,
        };
        provider.Models.Add(new Model { ModelId = modelId, Enabled = true });
        provider.Accounts.Add(new ProviderAccount
        {
            Name = "a1",
            Enabled = true,
            ApiKeyEncrypted = _protector.Protect("sk-live"),
        });
        db.Providers.Add(provider);
        db.SaveChanges();
    }

    private void SeedCombo(string name, string modelId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var modelKey = db.Models.Single(m => m.ModelId == modelId).Id;
        var combo = new Combo { Name = name, Mode = ComboMode.RoundRobin };
        combo.Items.Add(new ComboItem { Position = 0, TargetModelId = modelKey });
        db.Combos.Add(combo);
        db.SaveChanges();
    }

    private sealed class StubUpstream(Func<HttpResponseMessage> factory) : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct) =>
            Task.FromResult(factory());
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

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Chat_WhenApiKeyEnabledAndMissing_Returns401OpenAiShape()
    {
        await _clientKeys.CreateAsync(new ClientKeyDraft("chat", null, null));
        SeedProvider("gpt-4o-mini");
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"gpt-4o-mini","messages":[{"role":"user"}]}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("invalid_api_key", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Chat_WhenValidRequest_PassesSseThroughUnchanged()
    {
        SeedProvider("gpt-4o-mini");
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}],"stream":true}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("data: {\"x\":1}\n\ndata: [DONE]\n\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Chat_WhenInvalidJson_Returns400OpenAiShape()
    {
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.PostAsync("/v1/chat/completions",
            new StringContent("{broken", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // Spec §3.6: MỌI response — kể cả validate 400 — phải kèm X-Request-Id để client đối chiếu
        Assert.False(string.IsNullOrWhiteSpace(response.Headers.GetValues("X-Request-Id").Single()));
        var json = await ReadJson(response);
        var error = json.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("Invalid JSON body", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Chat_WhenUnknownModel_Returns404ModelNotFound()
    {
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"no-such-model","messages":[{"role":"user"}]}"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await ReadJson(response);
        var error = json.GetProperty("error");
        Assert.Equal("model_not_found", error.GetProperty("code").GetString());
        Assert.Equal("model", error.GetProperty("param").GetString());
    }

    [Fact]
    public async Task Chat_WhenProviderAnthropic_Returns503ServerError()
    {
        SeedProvider("sonnet-4", ProviderType.Anthropic);
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"sonnet-4","messages":[{"role":"user"}]}"""));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("server_error", json.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Chat_WhenUpstream429_PassesStatusAndBodyThrough()
    {
        SeedProvider("gpt-4o-mini");
        var upstreamBody = """{"error":{"message":"rate limited","type":"rate_limit_error"}}""";
        var client = await StartAsync(new StubUpstream(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(upstreamBody, Encoding.UTF8, "application/json"),
        }));

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"gpt-4o-mini","messages":[{"role":"user"}]}"""));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(upstreamBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_WhenApiKeyEnabled_StillOpen()
    {
        await _clientKeys.CreateAsync(new ClientKeyDraft("health", null, null));
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Models_ReturnsEnabledModelsListShape()
    {
        SeedProvider("gpt-4o-mini");
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("list", json.GetProperty("object").GetString());
        var data = json.GetProperty("data").EnumerateArray().ToList();
        Assert.Single(data);
        Assert.Equal("gpt-4o-mini", data[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task Models_ReturnsCombosAlongsideEnabledModels()
    {
        // Quyết định #10: client chọn combo qua trường model — combo phải xuất hiện
        // trong /v1/models để client (opencode...) phát hiện được trước khi gọi chat.
        SeedProvider("gpt-4o-mini");
        SeedCombo("combo-fast", "gpt-4o-mini");
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await ReadJson(response)).GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(2, data.Count);
        Assert.Contains(data, e => e.GetProperty("id").GetString() == "gpt-4o-mini");
        Assert.Contains(data, e => e.GetProperty("id").GetString() == "combo-fast");
    }
}
