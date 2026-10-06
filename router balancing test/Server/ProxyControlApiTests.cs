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
/// Control API 3B (spec §3.4–3.5): GET /v1/requests + POST /v1/requests/{id}/cancel.
/// </summary>
public class ProxyControlApiTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly ClientKeyService _clientKeys;
    private readonly DpapiSecretProtector _protector = new();
    private WebApplication? _app;
    private HttpClient? _client;

    public ProxyControlApiTests()
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

    private static StringContent ChatBody(string model) =>
        Json($"{{\"model\":\"{model}\",\"messages\":[{{\"role\":\"user\"}}]}}");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private Task<HttpResponseMessage> CancelAsync(string id) =>
        _client!.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/v1/requests/{id}/cancel"));

    /// <summary>Chờ request thực sự vào queue (dispatcher có thể đang park) — poll tối đa 5s.</summary>
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

    private async Task<string> FindRequestIdAsync(string state)
    {
        var response = await _client!.GetAsync("/v1/requests");
        var requests = (await ReadJson(response)).GetProperty("requests").EnumerateArray();
        return requests.Single(r => r.GetProperty("state").GetString() == state)
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Snapshot_WhenEmpty_ReturnsEmptyArray()
    {
        var client = await StartAsync(new StubUpstream());

        var response = await client.GetAsync("/v1/requests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Empty(json.GetProperty("requests").EnumerateArray().ToList());
    }

    [Fact]
    public async Task Snapshot_WhenQueuedAndServing_ShowsBothStatesAndCancelable()
    {
        // 1 provider, MaxConcurrent=1: giữ slot bằng request đang serve → request thứ 2 park trong queue
        SeedProvider(maxConcurrent: 1, "m-served", "m-queued");
        var upstream = new GatedUpstream();
        var client = await StartAsync(upstream);

        var servingTask = client.PostAsync("/v1/chat/completions", ChatBody("m-served"));
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var parkedTask = client.PostAsync("/v1/chat/completions", ChatBody("m-queued"));
        var queuedId = await WaitForQueuedIdAsync("m-queued");

        var response = await client.GetAsync("/v1/requests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requests = (await ReadJson(response)).GetProperty("requests")
            .EnumerateArray().ToList();
        Assert.Equal(2, requests.Count);
        var serving = requests.Single(r => r.GetProperty("state").GetString() == "serving");
        var queued = requests.Single(r => r.GetProperty("state").GetString() == "queued");

        Assert.False(serving.GetProperty("cancelable").GetBoolean());
        Assert.Equal("p-main", serving.GetProperty("provider").GetString());
        Assert.NotEqual(JsonValueKind.Null, serving.GetProperty("startedAt").ValueKind);

        Assert.True(queued.GetProperty("cancelable").GetBoolean());
        Assert.Equal(queuedId, queued.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, queued.GetProperty("provider").ValueKind);
        Assert.Equal(JsonValueKind.Null, queued.GetProperty("startedAt").ValueKind);

        // Dọn để test không treo Dispose: huỷ request đang chờ, mở gate cho request đang serve
        var cancel = await CancelAsync(queuedId);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await parkedTask).StatusCode);
        upstream.Release();
        Assert.Equal(HttpStatusCode.OK, (await servingTask).StatusCode);
    }

    [Fact]
    public async Task Cancel_WhenIdUnknown_Returns404RequestNotFound()
    {
        var client = await StartAsync(new StubUpstream());

        var response = await CancelAsync("zzzzzzzz");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("request_not_found", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cancel_WhenRequestIsServing_Returns409NotCancellable()
    {
        SeedProvider(maxConcurrent: 1, "m1");
        var upstream = new GatedUpstream();
        var client = await StartAsync(upstream);

        var pending = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var servingId = await FindRequestIdAsync("serving");

        var response = await CancelAsync(servingId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("not_cancellable", json.GetProperty("error").GetProperty("code").GetString());

        upstream.Release();
        Assert.Equal(HttpStatusCode.OK, (await pending).StatusCode);
    }

    [Fact]
    public async Task Cancel_WhenRequestIsQueued_Returns200AndOriginGets400()
    {
        // max=0 giờ nghĩa là unlimited (D-B1) — park bằng cách bão hòa 1 tài khoản với N=1
        SeedProvider(maxConcurrent: 1, "m1");
        var upstream = new GatedUpstream();
        var client = await StartAsync(upstream);

        var holding = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // holder chiếm trọn slot
        var pending = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        var queuedId = await WaitForQueuedIdAsync("m1");

        var response = await CancelAsync(queuedId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJson(response);
        Assert.True(json.GetProperty("cancelled").GetBoolean());

        // Endpoint gốc đang await TCS → nhận Cancelled → ghi 400 request_cancelled (spec §3.4)
        var origin = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.BadRequest, origin.StatusCode);
        var error = (await ReadJson(origin)).GetProperty("error");
        Assert.Equal("request_cancelled", error.GetProperty("code").GetString());
        Assert.Equal(queuedId, origin.Headers.GetValues("X-Request-Id").Single());

        // Dọn có kiểm chứng: mở gate cho holder — không treo Dispose
        upstream.Release();
        Assert.Equal(HttpStatusCode.OK, (await holding).StatusCode);
    }
}
