using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RouterBalancing.Core.Dataset;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

/// <summary>
/// Integration đầu-cuối dataset JSONL export (spec dataset-export §7/§7.9): request thật qua
/// TestServer → upstream giả → pipeline proxy (<see cref="ProxyRequestHandler"/>) gọi
/// <see cref="IDatasetSink.Capture"/> sau 2xx thắng → writer thread nền → dòng trên đĩa.
/// Harness mirror <see cref="ApiMonitorIntegrationTests"/> (feed/store pre-register TRƯỚC
/// <see cref="ProxyApp.ConfigureServices"/> để assert đúng store pipeline ghi vào) + đăng ký
/// sẵn <see cref="DatasetSink"/> thật chỉ vào thư mục temp của test — fallback only-if-absent
/// trong ConfigureServices nhường chỗ, không có đường nào ghi ra AppData thật.
/// Sink là ASYNC (queue bounded + writer thread): mọi assert file đi qua
/// <see cref="WaitDatasetLinesAsync"/> poll <see cref="DatasetStats.RecordsWritten"/> —
/// counter chỉ tăng SAU khi StreamWriter (AutoFlush) đã flush byte xuống FileStream, nên
/// "đếm đạt + đọc thấy đủ dòng" là invariant byte-đã-trên-đĩa, không cần sleep cố định.
/// </summary>
public class DatasetExportIntegrationTests : IDisposable
{
    /// <summary>
    /// Budget rendezvous với pipeline/feed — full suite chạy song song có thể làm thread pool/IO
    /// đói vài giây; 30s vẫn fail nếu có deadlock thật (pattern ApiMonitorIntegrationTests).
    /// </summary>
    private static readonly TimeSpan RendezvousTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Deadline cho writer nền của sink — ghi file local là ms, 10s để dành cho suite song song.</summary>
    private static readonly TimeSpan SinkWriteTimeout = TimeSpan.FromSeconds(10);

    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly ClientKeyService _clientKeys;
    private readonly DpapiSecretProtector _protector = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rb-ds-it-" + Guid.NewGuid().ToString("N"));
    private readonly DatasetSink _sink;
    private ApiMonitorStore _store = null!; // gán trong StartAsync — test 400 rendezvous qua đó trước khi assert
    private WebApplication? _app;
    private HttpClient? _client;

    public DatasetExportIntegrationTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory);
        _settings.Set(SettingsKeys.TransientMaxRetries, 0); // baseline opt-out — test retry tự opt-in trước StartAsync
        // Bật export TRƯỚC khi pipeline start (brief Task 8 Step 1): sink đọc settings lazily ở mỗi
        // Capture nhưng set sớm để mọi đường nhìn cùng trạng thái, không phụ thuộc thứ tự request.
        _settings.Set(SettingsKeys.DatasetExportPath, _dir);
        _settings.Set(SettingsKeys.DatasetExportEnabled, true);
        _log = new LogService(factory);
        _clientKeys = new ClientKeyService(factory);
        // Sink THẬT (không stub): ý nghĩa của suite này là chứng minh writer thread ghi dòng thật.
        _sink = new DatasetSink(_settings, _log, TimeProvider.System);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        // Flush dòng cuối + nhả handle file TRƯỚC khi xoá thư mục (brief: dispose sink ở teardown).
        _sink.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _log.Dispose();
        _settings.Dispose();
        _db.Dispose();
        Cleanup(_dir);
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
            Provider provider, string apiKey, string path, byte[] body, string idempotencyKey, CancellationToken ct) =>
            Task.FromResult(factory(provider));
    }

    // SSE chat hoàn chỉnh: delta content + finish_reason/usage ở chunk cuối + [DONE] — đủ cho cả
    // ScanSseLine (usage 7/5) lẫn ChatSseAggregateState.BuildCompletionJson
    // (completion.content == "hello world").
    private const string SseChat =
        "data: {\"id\":\"chatcmpl-1\",\"model\":\"m1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"hello world\"}}]}\n\n" +
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":7,\"completion_tokens\":5}}\n\n" +
        "data: [DONE]\n\n";

    private static HttpResponseMessage SseChatOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(SseChat, Encoding.UTF8, "text/event-stream"),
    };

    // SSE /v1/responses: delta text đầu (TTFT) + event terminal response.completed chứa nguyên
    // object `response` — output message map thành completion, usage 11/7 (spec v1-responses §4.4).
    private const string RespSse =
        "event: response.created\n" +
        "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"status\":\"in_progress\"}}\n\n" +
        "event: response.output_text.delta\n" +
        "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"hi\"}\n\n" +
        "event: response.completed\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"status\":\"completed\"," +
        "\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"hi there\"}]}]," +
        "\"usage\":{\"input_tokens\":11,\"output_tokens\":7}}}\n\n";

    private static HttpResponseMessage RespSseOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(RespSse, Encoding.UTF8, "text/event-stream"),
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

        // Instance TRƯỚC ProxyApp.ConfigureServices → fallback only-if-absent nhường chỗ: pipeline
        // dùng ĐÚNG feed/store/sink của test — sink ghi vào _dir, không phải AppData thật.
        var feed = new TraceFeed(_log);
        var store = new ApiMonitorStore(feed, _log, TimeProvider.System);
        _store = store;
        builder.Services.AddSingleton<ITraceFeed>(feed);
        builder.Services.AddSingleton<IApiMonitorStore>(store);
        builder.Services.AddSingleton<IDatasetSink>(_sink);

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

    /// <summary>Poll tới <see cref="RendezvousTimeout"/> — H4 chạy sau khi client nhận response.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + RendezvousTimeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(condition(), because);
    }

    // ===== Helpers assert file dataset (deterministic với sink async) =====

    /// <summary>
    /// Chờ writer nền của sink ghi ĐÚNG <paramref name="expectedLines"/> dòng rồi trả về chúng.
    /// <see cref="DatasetStats.RecordsWritten"/> chỉ tăng SAU khi StreamWriter (AutoFlush=true) đã
    /// flush byte xuống FileStream — đếm đạt + đọc file thấy đủ dòng là invariant "byte đã trên
    /// đĩa", không dùng sleep cố định làm đồng bộ duy nhất. Sau đó chờ ngắn một cửa sổ ổn định
    /// rồi assert counter ĐÚNG bằng: bug double-capture enqueue cả 2 record gần như đồng thời,
    /// đọc file giữa hai lần write có thể thấy đúng 1 dòng và lọt qua assert — cửa sổ này chặn.
    /// </summary>
    private async Task<JsonObject[]> WaitDatasetLinesAsync(int expectedLines)
    {
        var deadline = DateTime.UtcNow + SinkWriteTimeout;
        while (DateTime.UtcNow < deadline && _sink.Stats.RecordsWritten < expectedLines)
            await Task.Delay(25);

        Assert.True(_sink.Stats.RecordsWritten >= expectedLines,
            $"Dataset sink không ghi đủ {expectedLines} dòng trong {SinkWriteTimeout.TotalSeconds:0}s. " +
            $"Stats: {DescribeStats()}; dir {_dir}: {DescribeDir()}");

        // Cửa sổ ổn định 300ms (ghi local <ms): chờ counter dừng tăng rồi mới assert chính xác.
        var stableUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(300);
        while (DateTime.UtcNow < stableUntil && _sink.Stats.RecordsWritten <= expectedLines)
            await Task.Delay(25);

        // xUnit v3: Assert.Equal không nhận failure message — dùng Assert.True kèm ngữ cảnh.
        Assert.True(_sink.Stats.RecordsWritten == expectedLines,
            $"mỗi request thành công đúng MỘT dòng dataset — không trùng bản ghi " +
            $"(thấy {_sink.Stats.RecordsWritten}). Stats: {DescribeStats()}");

        var lines = ReadAllDatasetLines();
        Assert.True(lines.Count >= expectedLines,
            $"RecordsWritten={expectedLines} nhưng đọc file chỉ thấy {lines.Count} dòng. dir {_dir}: {DescribeDir()}");
        return [.. lines.Select(l => (JsonObject)JsonNode.Parse(l)!)];
    }

    /// <summary>
    /// Đọc mọi dòng trong file dataset của ngày chạy — nhận CẢ HAI ngày liên tiếp (hôm nay + hôm
    /// qua UTC): capture xảy ra vài ms trước assertion nên nếu test chạy sát biên 00:00 UTC, dòng
    /// nằm ở file ngày hôm qua dù tên file tính tại assertion time là hôm nay (brief ruling: accept
    /// either of two adjacent dates — chọn phương án này vì không cần đoán request kết thúc ngày nào).
    /// File chưa tồn tại → đóng góp 0 dòng.
    /// </summary>
    private List<string> ReadAllDatasetLines()
    {
        var lines = new List<string>();
        foreach (var name in CandidateFileNames())
        {
            var path = Path.Combine(_dir, name);
            if (!File.Exists(path)) continue;
            // Writer của sink vẫn giữ handle append (FileShare.Read) — reader phải mở với share
            // ReadWrite, File.ReadAllLines (Share.None) bị ACCESS_DENIED trên Windows.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            lines.AddRange(reader.ReadToEnd().Split('\n',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return lines;
    }

    // Tên file qua DatasetPaths.FileName — không tự format "requests-..." (brief ruling).
    private static string[] CandidateFileNames()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        return [DatasetPaths.FileName(today), DatasetPaths.FileName(today.AddDays(-1))];
    }

    private string DescribeStats()
    {
        var s = _sink.Stats;
        return $"RecordsWritten={s.RecordsWritten}, SkippedTooLarge={s.SkippedTooLarge}, " +
               $"SkippedTruncated={s.SkippedTruncated}, SkippedEmpty={s.SkippedEmpty}, " +
               $"SkippedRefusal={s.SkippedRefusal}, SkippedQueueFull={s.SkippedQueueFull}, " +
               $"BytesWritten={s.BytesWritten}, CurrentFile={s.CurrentFile ?? "<none>"}";
    }

    private string DescribeDir() =>
        Directory.Exists(_dir) ? string.Join(", ", Directory.GetFiles(_dir)) : "<chưa tạo>";

    private static void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Writer chưa kịp nhả handle — để lại rác temp thay vì làm test cleanup nổ.
        }
    }

    [Fact]
    public async Task ChatSuccess_WritesExactlyOneDatasetLine()
    {
        SeedProvider(maxConcurrent: 4, "m1");
        var client = await StartAsync(new ScriptedUpstream(_ => SseChatOk()));

        // Client xin stream → đường pipe + tee với captureCompletion (seam Task 5): CompletionJson
        // được ChatSseAggregateState gộp từ delta SSE.
        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"m1","stream":true,"messages":[{"role":"user","content":"hi"}]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var id = response.Headers.GetValues("X-Request-Id").Single();

        var lines = await WaitDatasetLinesAsync(1);
        Assert.Single(lines); // đúng 1 dòng — không vacuous (0) và không trùng (2)
        var line = lines[0];
        Assert.Equal(1, line["v"]!.GetValue<int>());
        Assert.Equal(id, line["request_id"]!.GetValue<string>());
        Assert.Equal("chat", line["endpoint"]!.GetValue<string>());
        Assert.Equal("hello world", line["completion"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task TransientRetryThenSuccess_WritesOneLineNotTwo()
    {
        // Brief: bật ngân sách transient. Lưu ý RetryClassifier: 429 KHÔNG transient (xoay tài
        // khoản) nên cơ chế thật ở đây là failover walk p1 → p2 — invariant không đổi: NHIỀU
        // attempt nhưng đúng MỘT dòng, thuộc về attempt thắng.
        _settings.Set(SettingsKeys.TransientMaxRetries, 1);
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var calls = 0;
        var upstream = new ScriptedUpstream(p =>
        {
            Interlocked.Increment(ref calls);
            return p.Name == "p1" ? Resp429() : SseChatOk();
        });
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"m1","stream":true,"messages":[{"role":"user","content":"hi"}]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, calls); // attempt 1 (p1) ăn 429 → attempt 2 (p2) thắng — có retry thật
        var id = response.Headers.GetValues("X-Request-Id").Single();

        var lines = await WaitDatasetLinesAsync(1);
        Assert.Single(lines);
        var line = lines[0];
        Assert.Equal(id, line["request_id"]!.GetValue<string>());
        // provider/account là attempt THẮNG — không phải attempt 429 đã thất bại.
        Assert.Equal("p2", line["provider"]!.GetValue<string>());
        Assert.Equal("a1", line["account"]!.GetValue<string>());
    }

    [Fact]
    public async Task ClientNonStreamUpstreamForcedToSse_AggregatedCompletionStillWritesOneLine()
    {
        SeedProvider(maxConcurrent: 4, "m1");
        var client = await StartAsync(new ScriptedUpstream(_ => SseChatOk()));

        // Body KHÔNG có "stream" → không có pipe; upstream vẫn trả SSE (đường fingerprint ép
        // stream) → nhánh aggregate của handler: completion lấy từ ResponseBody của
        // AggregateSseAsync, KHÔNG phải CompletionJson (null ở đường này).
        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"m1","messages":[{"role":"user","content":"hi"}]}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Client nhận JSON đã aggregate chứ không phải SSE — bằng chứng test đang chạy đúng nhánh.
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("\"choices\"", await response.Content.ReadAsStringAsync());
        var id = response.Headers.GetValues("X-Request-Id").Single();

        var lines = await WaitDatasetLinesAsync(1);
        Assert.Single(lines);
        var line = lines[0];
        Assert.Equal(id, line["request_id"]!.GetValue<string>());
        Assert.Equal("chat", line["endpoint"]!.GetValue<string>());
        // CompletionJson phải được lấy từ ResponseBody của AggregateSseAsync — nếu wiring sai
        // (lấy tee.CompletionJson = null) thì builder skip Empty và không có dòng nào.
        Assert.Equal("hello world", line["completion"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task ResponsesSuccess_WritesNormalizedMessages()
    {
        SeedProvider(maxConcurrent: 4, "m1");
        var client = await StartAsync(new ScriptedUpstream(_ => RespSseOk()));

        // input là string → đúng một message user (spec §7.5); upstream trả SSE với terminal
        // response.completed → completion map từ `output` của object terminal.
        var response = await client.PostAsync("/v1/responses", Json("""{"model":"m1","input":"hi"}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var id = response.Headers.GetValues("X-Request-Id").Single();

        var lines = await WaitDatasetLinesAsync(1);
        Assert.Single(lines);
        var line = lines[0];
        Assert.Equal(1, line["v"]!.GetValue<int>());
        Assert.Equal(id, line["request_id"]!.GetValue<string>());
        Assert.Equal("responses", line["endpoint"]!.GetValue<string>());
        // messages phải là MẢNG đúng một message user (input string → 1 message, spec §7.5).
        var messages = Assert.IsType<JsonArray>(line["messages"]);
        Assert.Equal(1, messages.Count);
        Assert.Equal("user", messages[0]!["role"]!.GetValue<string>());
        Assert.Equal("hi", messages[0]!["content"]!.GetValue<string>());
        Assert.Equal("hi there", line["completion"]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Upstream400_WritesNoDatasetLine()
    {
        SeedProvider(maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"bad request"}}""",
                Encoding.UTF8, "application/json"),
        });
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"m1","messages":[{"role":"user","content":"hi"}]}"""));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var id = response.Headers.GetValues("X-Request-Id").Single();
        // Rendezvous: record Error = ForwardAsync đã chạy hết nhánh non-2xx (Capture chỉ gọi ở
        // nhánh 2xx) — nếu có bug capture sai chỗ, record đã được enqueue TRƯỚC thời điểm này.
        await WaitUntilAsync(() => _store.Find(id) is { State: ApiCallState.Error },
            "H4 Finished{Success=false} phải chốt record sang Error");

        // Cửa sổ quan sát writer nền: dòng giả (nếu wiring sai) đã enqueue ở rendezvous trên và
        // writer thật ghi 1 dòng nhỏ trong <ms — chờ tới 2s cho bất kỳ dòng muộn nào xuất hiện.
        // RecordsWritten > 0 trong cửa sổ = test fail đúng, không phải vacuous pass do đọc trước
        // khi writer kịp viết (đây là buffer SAU rendezvous thật, không phải đồng bộ duy nhất).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (DateTime.UtcNow < deadline && _sink.Stats.RecordsWritten == 0)
            await Task.Delay(25);

        Assert.Equal(0, _sink.Stats.RecordsWritten);
        // File có thể chưa tồn tại (writer start lười ở Capture đầu tiên) — cả hai trường hợp
        // "không tồn tại" lẫn "rỗng" đều phải cho 0 dòng.
        Assert.Empty(ReadAllDatasetLines());
    }
}
