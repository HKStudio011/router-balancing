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
/// Integration endpoint <c>/v1/responses</c> qua runner queue-first dùng chung (spec
/// v1-responses §4): hạ tầng mirror ApiMonitorIntegrationTests — TestServer + fake
/// IUpstreamClient BẮT path (assert "/v1/responses", không phải "/v1/chat/completions")
/// + pre-register TraceFeed/ApiMonitorStore TRƯỚC ConfigureServices để assert đúng store
/// mà pipeline ghi vào.
/// </summary>
public class ResponsesEndpointIntegrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly ClientKeyService _clientKeys;
    private readonly DpapiSecretProtector _protector = new();
    private ApiMonitorStore _store = null!; // gán trong StartAsync — mọi test đều qua đó trước khi assert
    private TraceFeed _feed = null!;
    private WebApplication? _app;
    private HttpClient? _client;

    public ResponsesEndpointIntegrationTests()
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

    /// <summary>Upstream theo script + capture path đã gọi — đường nét đặc trưng của suite này:
    /// chứng minh dispatcher resolve protocol theo <c>ProxyRequest.Endpoint</c>.</summary>
    private sealed class ScriptedUpstream(Func<Provider, HttpResponseMessage> factory) : IUpstreamClient
    {
        private readonly List<string> _paths = [];

        public IReadOnlyList<string> Paths
        {
            get { lock (_paths) return [.. _paths]; }
        }

        public Task<HttpResponseMessage> PostAsync(
            Provider provider, string apiKey, string path, byte[] body, CancellationToken ct)
        {
            lock (_paths) _paths.Add(path);
            return Task.FromResult(factory(provider));
        }
    }

    // Body responses non-stream chuẩn: usage ở ROOT (input/output_tokens) — map 11/7 vào
    // PromptTokens/CompletionTokens (ResponsesProtocolTests.Tee_NonStreamJson_*)
    private const string RespJson =
        """{"id":"resp_1","object":"response","status":"completed","usage":{"input_tokens":11,"output_tokens":7}}""";

    private static HttpResponseMessage RespJsonOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(RespJson, Encoding.UTF8, "application/json"),
    };

    // SSE chuẩn Responses API (mirror CompletedSse trong ResponsesProtocolTests): delta text
    // đầu tiên → TTFT, event terminal response.completed → usage 11/7 (spec §4.4)
    private const string CompletedSse =
        "event: response.created\n" +
        "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"status\":\"in_progress\"}}\n\n" +
        "event: response.output_text.delta\n" +
        "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"Hello\"}\n\n" +
        "event: response.completed\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"usage\":{\"input_tokens\":11,\"output_tokens\":7}}}\n\n";

    private static HttpResponseMessage RespSseOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(CompletedSse, Encoding.UTF8, "text/event-stream"),
    };

    // Gateway omit usage ở object terminal — dùng cho test Debug row (spec v1-responses §6)
    private const string RespNoUsageJson = """
        {"id":"resp_1","object":"response","status":"completed"}
        """;

    private static HttpResponseMessage RespNoUsageJsonOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(RespNoUsageJson, Encoding.UTF8, "application/json"),
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

        // Instance TRƯỚC ProxyApp.ConfigureServices → fallback only-if-absent nhường chỗ,
        // test assert đúng feed/store mà endpoint/dispatcher ghi vào
        var feed = new TraceFeed(_log);
        var store = new ApiMonitorStore(feed, _log, TimeProvider.System);
        _feed = feed;
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

    private static StringContent ResponsesBody(string model, bool stream = false) =>
        Json(stream
            ? $$"""{"model":"{{model}}","input":"hi","stream":true}"""
            : $$"""{"model":"{{model}}","input":"hi"}""");

    private static async Task<string> SentIdAsync(HttpResponseMessage response) =>
        response.Headers.GetValues("X-Request-Id").Single();

    /// <summary>Poll kiểu ApiMonitorIntegrationTests (100×50ms) — H4 chạy sau khi client nhận response.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), because);
    }

    [Fact]
    public async Task ResponsesNonStream_TwoXx_PassesThroughBytesAndRecordsMonitor()
    {
        SeedProvider(maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(_ => RespJsonOk());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/responses", ResponsesBody("m1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Byte-forward không đổi (spec §4.3): client nhận đúng payload upstream, không wrap
        Assert.Equal(RespJson, await response.Content.ReadAsStringAsync());
        // Dispatcher phải resolve protocol theo endpoint của request — không đi path chat
        Assert.Single(upstream.Paths);
        Assert.Equal("/v1/responses", upstream.Paths[0]);

        var id = await SentIdAsync(response);
        await WaitUntilAsync(() => _store.Find(id) is { State: ApiCallState.Done },
            "H4 Finished{Success=true} phải chốt record sang Done");

        var record = _store.Find(id)!;
        // input_tokens→PromptTokens, output_tokens→CompletionTokens (Task 3 tee)
        Assert.Equal(11, record.PromptTokens);
        Assert.Equal(7, record.CompletionTokens);
        Assert.Equal(200, record.Status);
        Assert.True(record.Success);
    }

    [Fact]
    public async Task ResponsesStream_TwoXx_TtftAndUsageReachMonitor()
    {
        SeedProvider(maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(_ => RespSseOk());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/responses",
            ResponsesBody("m1", stream: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        // SSE forward nguyên byte qua pipe (early-headers §3.2) — không thêm/bớt event
        Assert.Equal(CompletedSse, await response.Content.ReadAsStringAsync());
        Assert.Single(upstream.Paths);
        Assert.Equal("/v1/responses", upstream.Paths[0]);

        var id = await SentIdAsync(response);
        await WaitUntilAsync(() => _store.Find(id) is { State: ApiCallState.Done },
            "H4 Finished{Success=true} phải chốt record sang Done");

        var record = _store.Find(id)!;
        // TTFT từ event response.output_text.delta ĐẦU TIÊN + usage từ response.completed (spec §4.4)
        Assert.NotNull(record.FirstTokenAt);
        Assert.Equal(11, record.PromptTokens);
        Assert.Equal(7, record.CompletionTokens);
        Assert.Equal(200, record.Status);
    }

    [Fact]
    public async Task ResponsesTwoXxWithoutUsage_LogsDebugRowCorrelatedToRequest()
    {
        SeedProvider(maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(_ => RespNoUsageJsonOk());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/responses", ResponsesBody("m1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Fail-open: upstream omit usage không được đụng tới response của client (spec §4.3)
        Assert.Equal(RespNoUsageJson, await response.Content.ReadAsStringAsync());

        var id = await SentIdAsync(response);
        // Debug row ghi sau tee trên luồng dispatcher — poll như các assert monitor khác.
        // Spec §6: responses 2xx hoàn tất mà không trích được usage → Debug correlate qua RequestId.
        await WaitUntilAsync(
            () => _log.Query(new LogQuery(Search: "Upstream không trả usage"))
                .Any(e => e.Severity == LogSeverity.Debug && e.RequestId == id),
            "responses 2xx không usage phải ghi row Debug với RequestId của request (spec §6)");
    }

    [Fact]
    public async Task ResponsesValidateFail_Writes400AndNoQueueOrMonitorRow()
    {
        SeedProvider(maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(_ => RespJsonOk());
        var client = await StartAsync(upstream);

        // Thiếu input → 400 param 'input' (spec §4.1) — không enqueue, không H1/Received
        var response = await client.PostAsync("/v1/responses", Json("""{"model":"m1"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("Missing required parameter: 'input'.", error.GetProperty("message").GetString());
        Assert.Equal("input", error.GetProperty("param").GetString());

        Assert.Empty(_store.Snapshot());
        Assert.Empty(_app!.Services.GetRequiredService<IRequestQueue>().Snapshot());
        Assert.DoesNotContain(_feed.Snapshot(), e => e.Stage == TraceStage.Received);
        Assert.Empty(upstream.Paths); // validate fail TRƯỚC enqueue — không attempt nào
    }

    [Fact]
    public async Task ResponsesUpstream429ThenOk_RetryWalk_EndsDone()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(p => p.Name == "p1" ? Resp429() : RespJsonOk());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/responses", ResponsesBody("m1"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RespJson, await response.Content.ReadAsStringAsync());
        // Mọi attempt của walk đều phải đi path responses
        Assert.NotEmpty(upstream.Paths);
        Assert.All(upstream.Paths, path => Assert.Equal("/v1/responses", path));

        var id = await SentIdAsync(response);
        await WaitUntilAsync(() => _store.Find(id) is { State: ApiCallState.Done },
            "request 429→ok phải kết thúc Done, không mắc kẹt Error");

        var record = _store.Find(id)!;
        Assert.Equal(200, record.Status);
        Assert.Null(record.ErrorBody);
        // Dấu hiệu duy nhất attempt 429 được RecordError ghi trước khi 2xx xóa ErrorBody
        Assert.Equal("http", record.FailureKind);
    }

    [Fact]
    public async Task ResponsesErrorNon2xx_RecordsErrorOnMonitor()
    {
        SeedProvider(maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(_ => new HttpResponseMessage(
            HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("""{"error":{"message":"server boom"}}""",
                Encoding.UTF8, "application/json"),
        });
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/responses", ResponsesBody("m1"));

        // 500 retryable — walk một provider exhaustion → Passthrough nguyên response cuối
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var id = await SentIdAsync(response);
        await WaitUntilAsync(() => _store.Find(id) is { State: ApiCallState.Error },
            "H4 Finished{Success=false} phải chốt record sang Error");

        var record = _store.Find(id)!;
        // RecordError chạy cho MỌI non-2xx bất kể protocol — status 500 phải vào row
        Assert.Equal(500, record.Status);
        Assert.Contains("server boom", record.ErrorBody);
        Assert.Equal("http", record.FailureKind);
    }
}
