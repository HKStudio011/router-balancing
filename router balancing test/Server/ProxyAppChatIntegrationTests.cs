using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RouterBalancing.Core.Capabilities;
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

    private void SeedProvider(string modelId, ProviderType type = ProviderType.OpenAI,
        string? identifier = null)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = $"p-{modelId}",
            BaseUrl = "https://api.openai.com",
            Type = type,
            Identifier = identifier,
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

    private void SeedCombo(string name, string modelId) => SeedComboWithModels(name, modelId);

    private void SeedComboWithModels(string name, params string[] modelIds)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var combo = new Combo { Name = name, Mode = ComboMode.RoundRobin };
        for (var i = 0; i < modelIds.Length; i++)
        {
            var modelKey = db.Models.Single(m => m.ModelId == modelIds[i]).Id;
            combo.Items.Add(new ComboItem { Position = i, TargetModelId = modelKey });
        }
        db.Combos.Add(combo);
        db.SaveChanges();
    }

    /// <summary>
    /// Đặt ContextWindow (cột trên Models — không thuộc capability model) và seed FACT capability.
    /// Task 4: /v1/models + combo calculator chỉ đọc qua IModelCapabilityReader — tham số
    /// bool ở đây nghĩa là "đã biết" (KnownAbsent=0/1); model không gọi helper này = chưa biết (null).
    /// </summary>
    private void SetModelCaps(string modelId, int? contextWindow,
        bool vision, bool audio, bool video, bool think, bool? tools = null)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var model = db.Models.Single(m => m.ModelId == modelId);
        model.ContextWindow = contextWindow;
        SeedInputModality(db, model.Id, ModalityKind.Image, vision);
        SeedInputModality(db, model.Id, ModalityKind.Audio, audio);
        SeedInputModality(db, model.Id, ModalityKind.Video, video);
        SeedCapability(db, model.Id, CapabilityKind.Reasoning, think);
        if (tools is { } toolsKnown) SeedCapability(db, model.Id, CapabilityKind.Tools, toolsKnown);
        db.SaveChanges();
    }

    private static void SeedInputModality(RouterBalancingDbContext db, long modelId, ModalityKind kind, bool supported) =>
        db.ModelModalities.Add(new ModelModality
        {
            ModelId = modelId, Direction = ModalityDirection.Input, Modality = kind,
            KnownAbsent = !supported, Source = CapabilitySource.Catalog, UpdatedAt = DateTimeOffset.UnixEpoch,
        });

    private static void SeedCapability(RouterBalancingDbContext db, long modelId, CapabilityKind kind, bool supported) =>
        db.ModelCapabilities.Add(new ModelCapability
        {
            ModelId = modelId, Capability = kind,
            KnownAbsent = !supported, Source = CapabilitySource.Catalog, UpdatedAt = DateTimeOffset.UnixEpoch,
        });

    private void AddProviderModel(string providerName, string modelId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        db.Providers.Single(p => p.Name == providerName)
            .Models.Add(new Model { ModelId = modelId, Enabled = true });
        db.SaveChanges();
    }

    private static JsonElement FindEntry(JsonElement root, string id) =>
        root.GetProperty("data").EnumerateArray()
            .Single(e => e.GetProperty("id").GetString() == id);

    private sealed class StubUpstream(Func<HttpResponseMessage> factory) : IUpstreamClient
    {
        public byte[]? LastBody { get; private set; }

        public Task<HttpResponseMessage> PostAsync(
            Provider provider, string apiKey, string path, byte[] body, string idempotencyKey, CancellationToken ct)
        {
            LastBody = body;
            return Task.FromResult(factory());
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

    [Fact]
    public async Task Models_ModelEntry_HasCapabilitiesObject()
    {
        SeedProvider("gpt-4o-mini");
        SetModelCaps("gpt-4o-mini", 128_000, vision: true, audio: true, video: false, think: false, tools: true);
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var caps = FindEntry(await ReadJson(response), "gpt-4o-mini").GetProperty("capabilities");
        Assert.Equal(128_000, caps.GetProperty("context_window").GetInt32());
        // Key "audio" phải tồn tại (ValueKind ≠ Undefined) — trước đây response không có key này
        Assert.True(caps.TryGetProperty("audio", out var audio));
        Assert.NotEqual(JsonValueKind.Undefined, audio.ValueKind);
        Assert.True(audio.GetBoolean());
        Assert.True(caps.GetProperty("vision").GetBoolean());
        Assert.False(caps.GetProperty("video").GetBoolean());
        Assert.False(caps.GetProperty("thinking").GetBoolean());
        // Task 4: payload thêm "tools" đọc từ fact CapabilityKind.Tools
        Assert.True(caps.GetProperty("tools").GetBoolean());
    }

    [Fact]
    public async Task Models_ComboEntry_HasComputedCapabilities()
    {
        SeedProvider("gpt-4o-mini");
        SetModelCaps("gpt-4o-mini", 128_000, vision: true, audio: true, video: true, think: true);
        SeedCombo("combo-fast", "gpt-4o-mini");
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var caps = FindEntry(await ReadJson(response), "combo-fast").GetProperty("capabilities");
        Assert.Equal(128_000, caps.GetProperty("context_window").GetInt32());
        Assert.True(caps.TryGetProperty("audio", out var audio));
        Assert.NotEqual(JsonValueKind.Undefined, audio.ValueKind);
        Assert.True(audio.GetBoolean());
        Assert.True(caps.GetProperty("vision").GetBoolean());
        Assert.True(caps.GetProperty("video").GetBoolean());
        Assert.True(caps.GetProperty("thinking").GetBoolean());
    }

    [Fact]
    public async Task Models_ComboWithUnknownChild_SerializesNullsExplicitly()
    {
        // Member không có fact = chưa biết (null), KHÔNG phải false (Task 4 — view ba trạng thái).
        // AND 3 mức: false của member đã biết thắng null (audio/video ra false), nhưng null
        // lan ở field không có false (vision/thinking từ true ∧ null). ctx min(128000, null) = null.
        // Mọi key phải tồn tại, serialize thành JSON null — client phân biệt "chưa rõ" với
        // "không hỗ trợ", không được omit key.
        SeedProvider("gpt-4o-mini");
        SetModelCaps("gpt-4o-mini", 128_000, vision: true, audio: false, video: false, think: true);
        AddProviderModel("p-gpt-4o-mini", "gpt-4o-default");
        SeedComboWithModels("combo-mixed", "gpt-4o-mini", "gpt-4o-default");
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var caps = FindEntry(await ReadJson(response), "combo-mixed").GetProperty("capabilities");
        Assert.True(caps.TryGetProperty("context_window", out var contextWindow));
        Assert.Equal(JsonValueKind.Null, contextWindow.ValueKind);
        foreach (var name in new[] { "vision", "thinking" })
        {
            Assert.True(caps.TryGetProperty(name, out var flag));
            Assert.Equal(JsonValueKind.Null, flag.ValueKind); // true ∧ unknown = unknown
        }
        foreach (var name in new[] { "audio", "video" })
        {
            Assert.True(caps.TryGetProperty(name, out var flag));
            Assert.Equal(JsonValueKind.False, flag.ValueKind); // false thắng unknown
        }
    }

    [Fact]
    public async Task Chat_WhenModelPinned_ForwardsPlainModelIdToUpstream()
    {
        // Pin `{identifier}/{modelId}` là alias nội bộ — upstream chỉ hiểu model id thật,
        // gửi nguyên pin lên provider sẽ bị từ chối (bug client báo).
        SeedProvider("gpt-4o-mini", identifier: "openai-x");
        var upstream = new StubUpstream(() => Sse());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"openai-x/gpt-4o-mini","messages":[{"role":"user"}],"stream":true}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = JsonDocument.Parse(upstream.LastBody!).RootElement;
        Assert.Equal("gpt-4o-mini", sent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Chat_WhenModelIsCombo_ForwardsResolvedModelIdToUpstream()
    {
        // Tên combo cũng là alias nội bộ — upstream phải nhận model id đã resolve từ combo.
        SeedProvider("gpt-4o-mini");
        SeedCombo("combo-fast", "gpt-4o-mini");
        var upstream = new StubUpstream(() => Sse());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"combo-fast","messages":[{"role":"user"}],"stream":true}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = JsonDocument.Parse(upstream.LastBody!).RootElement;
        Assert.Equal("gpt-4o-mini", sent.GetProperty("model").GetString());
    }
}
