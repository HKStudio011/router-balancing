using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Storage;
using router_balancing_test.Engine;

namespace router_balancing_test.Providers;

/// <summary>
/// Task 6: Ollama Cloud lấy capability từ <c>GET https://ollama.com/api/tags</c>.
/// Nguồn là full-list cho đúng ba dimension tools/reasoning/vision (R-T6-1: patch không được
/// chứa audio/video/text); mảng <c>capabilities</c> vắng = nguồn không phát biểu (R-T5-7),
/// còn mảng có mặt mà thiếu token = "đã biết là không". Tags cache in-process 24h (R-T6-3).
/// </summary>
public class OllamaTagsMetadataProviderTests : IDisposable
{
    private const string TagsUrl = "https://ollama.com/api/tags";

    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;

    public OllamaTagsMetadataProviderTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
    }

    public void Dispose() => _testDb.Dispose();

    private static OllamaTagsMetadataProvider ProviderWith(
        HttpMessageHandler handler, ILogService? log = null, TimeProvider? timeProvider = null) =>
        new(new StubFactory(handler), log ?? new NullLog(), timeProvider);

    /// <summary>Provider preset Ollama từ DB thật + model cần lấy metadata.</summary>
    private async Task<(Provider Provider, Model Model)> OllamaModelAsync(string modelId)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.SingleAsync(p => p.Name == "Ollama Cloud Free");
        var model = new Model { ProviderId = provider.Id, ModelId = modelId };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return (provider, model);
    }

    private async Task<(Provider Provider, Model Model)> NonOllamaModelAsync()
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider
        {
            Name = "Local",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
        };
        var model = new Model { ModelId = "llama3.1:8b" };
        provider.Models.Add(model);
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return (provider, model);
    }

    [Fact]
    public async Task FetchAsync_MatchesExactNameFirst()
    {
        const string json = """
            {"models":[
              {"name":"alpha:7b","model":"alpha-family","capabilities":["completion","tools"]},
              {"name":"other-model","model":"alpha:7b","capabilities":["completion","vision"]}
            ]}
            """;
        var (provider, model) = await OllamaModelAsync("alpha:7b");

        var meta = await ProviderWith(new JsonHandler(json)).FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.True(meta.SupportsTools);
        // Entry có name khớp tuyệt đối phải thắng entry chỉ khớp ở field model.
        Assert.False(meta.SupportsVision);
    }

    [Fact]
    public async Task FetchAsync_MatchesByModelField_WhenNameMisses()
    {
        const string json = """
            {"models":[{"name":"llama3.1:8b","model":"llama3.1","capabilities":["completion","tools"]}]}
            """;
        var (provider, model) = await OllamaModelAsync("llama3.1");

        var meta = await ProviderWith(new JsonHandler(json)).FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.True(meta.SupportsTools);
    }

    [Fact]
    public async Task FetchAsync_MatchesAfterStrippingTagSuffix()
    {
        const string json = """
            {"models":[{"name":"deepseek-v4.1-flash","model":"deepseek-v4.1-flash","capabilities":["completion","thinking"]}]}
            """;
        var (provider, model) = await OllamaModelAsync("deepseek-v4.1-flash:1017");

        var meta = await ProviderWith(new JsonHandler(json)).FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.True(meta.SupportsThink);
        Assert.False(meta.SupportsTools);
    }

    [Fact]
    public async Task FetchAsync_NoFuzzyOrPrefixMatch_ReturnsNull()
    {
        const string json = """
            {"models":[{"name":"deepseek-v4.1-flash","model":"deepseek-v4.1-flash","capabilities":["tools"]}]}
            """;
        var (provider, model) = await OllamaModelAsync("deepseek-v4.1");

        Assert.Null(await ProviderWith(new JsonHandler(json)).FetchAsync(provider, model));
    }

    [Fact]
    public async Task FetchAsync_ToolsAbsentInCompleteList_ReportsFalse()
    {
        const string json = """
            {"models":[{"name":"m1","model":"m1","capabilities":["completion","thinking","vision"]}]}
            """;
        var (provider, model) = await OllamaModelAsync("m1");
        var metadataProvider = ProviderWith(new JsonHandler(json));

        var meta = await metadataProvider.FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.False(meta.SupportsTools); // full-list: không có tools = đã biết là không
        Assert.True(meta.SupportsThink);
        Assert.True(metadataProvider.ReturnsCompleteFacts);
    }

    [Fact]
    public async Task FetchAsync_VisionAbsent_ReportsImageAbsent()
    {
        const string json = """
            {"models":[{"name":"m1","model":"m1","capabilities":["completion","tools"]}]}
            """;
        var (provider, model) = await OllamaModelAsync("m1");

        var meta = await ProviderWith(new JsonHandler(json)).FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.False(meta.SupportsVision);
        var patch = MetadataPatchMapper.From(meta);
        Assert.False(patch.Modalities[(ModalityDirection.Input, ModalityKind.Image)]);
    }

    [Fact]
    public async Task FetchAsync_PatchHasNoAudioOrVideoKeys()
    {
        const string json = """
            {"models":[{"name":"m1","model":"m1","capabilities":["completion","tools","thinking","vision"]}]}
            """;
        var (provider, model) = await OllamaModelAsync("m1");

        var meta = await ProviderWith(new JsonHandler(json)).FetchAsync(provider, model);

        Assert.NotNull(meta);
        var patch = MetadataPatchMapper.From(meta);

        // R-T6-1: nguồn chỉ nói tools/reasoning/vision — im lặng ở audio/video/text
        // là "nguồn không nói", không được biến thành KnownAbsent.
        Assert.Equal([CapabilityKind.Tools, CapabilityKind.Reasoning], patch.Capabilities.Keys.Order().ToArray());
        Assert.Equal(
            [(ModalityDirection.Input, ModalityKind.Image)],
            patch.Modalities.Keys.ToArray());
        Assert.Empty(patch.ReasoningEfforts);
    }

    [Fact]
    public async Task FetchAsync_UnknownToken_IgnoredAndLoggedOnce()
    {
        const string json = """
            {"models":[{"name":"m1","model":"m1","capabilities":["completion","tools","decision","mystery"]}]}
            """;
        var (provider, model) = await OllamaModelAsync("m1");
        var log = new RecordingLog();

        var meta = await ProviderWith(new JsonHandler(json), log).FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.True(meta.SupportsTools);
        Assert.False(meta.SupportsThink);
        Assert.False(meta.SupportsVision);
        // Một cảnh báo/một model/một lần gọi — không phải một dòng cho mỗi token lạ.
        var warning = Assert.Single(log.Warnings);
        Assert.Contains("decision", warning);
        Assert.Contains("mystery", warning);
    }

    [Fact]
    public async Task FetchAsync_MissingCapabilities_ReturnsNullTriState()
    {
        const string json = """
            {"models":[{"name":"m1","model":"m1"}]}
            """;
        var (provider, model) = await OllamaModelAsync("m1");

        var meta = await ProviderWith(new JsonHandler(json)).FetchAsync(provider, model);

        // R-T5-7: vắng mảng capabilities = không phát biểu, KHÔNG phải toàn false.
        Assert.NotNull(meta);
        Assert.Null(meta.SupportsTools);
        Assert.Null(meta.SupportsThink);
        Assert.Null(meta.SupportsVision);
        var patch = MetadataPatchMapper.From(meta);
        Assert.Empty(patch.Capabilities);
        Assert.Empty(patch.Modalities);
        Assert.Empty(patch.ReasoningEfforts);
    }

    [Fact]
    public async Task FetchAsync_SecondCall_UsesCache()
    {
        const string json = """
            {"models":[{"name":"m1","model":"m1","capabilities":["tools"]}]}
            """;
        var (provider, model) = await OllamaModelAsync("m1");
        var handler = new CountingJsonHandler(json);
        var factory = new StubFactory(handler);
        var metadataProvider = new OllamaTagsMetadataProvider(factory, new NullLog());

        for (var i = 0; i < 22; i++)
        {
            Assert.NotNull(await metadataProvider.FetchAsync(provider, model));
        }

        // R-T6-3: 22 lần FetchAsync chỉ tốn đúng một request tags.
        Assert.Equal(1, handler.Calls);
        Assert.Equal(TagsUrl, handler.LastUri!.ToString());
        Assert.Null(handler.LastAuthorization); // nguồn công khai, không gắn auth
        Assert.Equal(ProviderRequestFactory.HttpClientName, factory.LastClientName);
    }

    [Fact]
    public async Task FetchAsync_ConcurrentCalls_UseSingleHttpRequest()
    {
        const string json = """
            {"models":[{"name":"m1","model":"m1","capabilities":["tools"]}]}
            """;
        var (provider, model) = await OllamaModelAsync("m1");
        var handler = new CountingJsonHandler(json, delayMs: 20);
        var metadataProvider = ProviderWith(handler);

        var results = await Task.WhenAll(
            Enumerable.Range(0, 22).Select(_ => metadataProvider.FetchAsync(provider, model)));

        Assert.All(results, m => Assert.NotNull(m));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FetchAsync_TtlExpired_Refetches()
    {
        const string json = """
            {"models":[{"name":"m1","model":"m1","capabilities":["tools"]}]}
            """;
        var (provider, model) = await OllamaModelAsync("m1");
        var handler = new CountingJsonHandler(json);
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero));
        var metadataProvider = ProviderWith(handler, timeProvider: time);

        Assert.NotNull(await metadataProvider.FetchAsync(provider, model));
        time.AdvanceDays(1); // đúng 24h → hết TTL
        Assert.NotNull(await metadataProvider.FetchAsync(provider, model));

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task FetchAsync_HttpError_ReturnsNull()
    {
        var (provider, model) = await OllamaModelAsync("m1");

        Assert.Null(await ProviderWith(new StatusHandler(HttpStatusCode.InternalServerError))
            .FetchAsync(provider, model));
    }

    [Fact]
    public async Task FetchAsync_MalformedJson_ReturnsNull()
    {
        var (provider, model) = await OllamaModelAsync("m1");

        Assert.Null(await ProviderWith(new JsonHandler("{not-json")).FetchAsync(provider, model));
    }

    [Fact]
    public async Task FetchAsync_HttpErrorThenSuccess_RetriesAndCachesOnlySuccess()
    {
        const string json = """
            {"models":[{"name":"m1","model":"m1","capabilities":["tools"]}]}
            """;
        var (provider, model) = await OllamaModelAsync("m1");
        var handler = new SequenceStatusHandler([HttpStatusCode.InternalServerError, HttpStatusCode.OK], json);
        var metadataProvider = ProviderWith(handler);

        Assert.Null(await metadataProvider.FetchAsync(provider, model));
        var meta = await metadataProvider.FetchAsync(provider, model);
        Assert.NotNull(meta);
        Assert.Equal(2, handler.Calls); // lỗi không được cache
    }

    [Fact]
    public async Task FetchAsync_NonOllamaProvider_ReturnsNull()
    {
        var (provider, model) = await NonOllamaModelAsync();
        var handler = new CountingJsonHandler("""{"models":[]}""");

        Assert.Null(await ProviderWith(handler).FetchAsync(provider, model));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task TryFillAsync_OllamaModel_WritesOnlyToolsReasoningAndImage()
    {
        const string json = """
            {"models":[{"name":"llama3.1:8b","model":"llama3.1:8b","capabilities":["completion","tools","thinking","vision"]}]}
            """;
        var (_, model) = await OllamaModelAsync("llama3.1:8b");
        var metadataProvider = ProviderWith(new JsonHandler(json));
        var service = new ModelMetadataService(
            _db, new NullLog(), [metadataProvider], new ModelCapabilityWriter(_db, new NullLog()),
            new ModelCapabilityReader(_db));

        await service.TryFillAsync(model.Id);

        var view = await new ModelCapabilityReader(_db).ReadAsync(model.Id);
        Assert.True(view.Tools);
        Assert.True(view.Reasoning);
        Assert.True(view.Vision);
        Assert.Single(view.Modalities); // chỉ (input, image) — R-T6-1
        Assert.Empty(view.ReasoningEfforts);

        using var db = _db.CreateDbContext();
        var stored = await db.Models.SingleAsync(m => m.Id == model.Id);
        // Các cột capability cũ không còn được ghi (Task 11 mới bỏ hẳn cột).
        Assert.Null(stored.SupportsTools);
        Assert.False(stored.SupportsVision);
        Assert.False(stored.SupportsThink);
        Assert.False(stored.SupportsAudio);
        Assert.False(stored.SupportsVideo);
        Assert.Null(stored.ThinkEfforts);
        Assert.Null(stored.InputModalities);
        Assert.Null(stored.OutputModalities);
    }

    private sealed class RecordingLog : ILogService
    {
        public List<string> Warnings { get; } = [];
        public event Action<LogEntry>? LogAdded { add { } remove { } }
        public void Write(LogEntry entry) { }
        public void Info(string message, LogCategory category = LogCategory.App) { }
        public void Warn(string message, LogCategory category = LogCategory.App) => Warnings.Add(message);
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) { }
        public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) { }
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }

    // ===== HTTP doubles (house pattern: JsonHandler/StubFactory) =====

    private class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    private sealed class SequenceStatusHandler(HttpStatusCode[] statuses, string json) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var status = statuses[Math.Min(Calls, statuses.Length - 1)];
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK ? json : "{}"),
            });
        }
    }

    private sealed class CountingJsonHandler(string json, int delayMs = 0) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public Uri? LastUri { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (delayMs > 0) await Task.Delay(delayMs, ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public string? LastClientName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            LastClientName = name;
            return new HttpClient(handler, disposeHandler: false);
        }
    }
}
