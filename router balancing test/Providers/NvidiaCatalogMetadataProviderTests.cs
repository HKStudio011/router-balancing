using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Storage;
using router_balancing_test.Engine;

namespace router_balancing_test.Providers;

/// <summary>
/// Task 7: NVIDIA API Catalog lấy modality + context từ trang model chính thức
/// <c>https://build.nvidia.com/{publisher}/{slug}</c>. Sidebar chỉ có
/// <c>Context Length</c>/<c>Input Modalities</c>/<c>Output Modalities</c> nên provider
/// KHÔNG phát biểu tools/reasoning (R-T7-2); 66/81 trang 200 nhưng không có sidebar → null.
/// Cache theo model id TTL 24h, cache cả kết quả 200-không-sidebar (negative cache) nhưng
/// không cache lỗi HTTP/mạng.
/// </summary>
public class NvidiaCatalogMetadataProviderTests : IDisposable
{
    private const string NemotronId = "nvidia/nemotron-3.5-super-vl-120b-a12b";
    private const string NemotronUrl = "https://build.nvidia.com/" + NemotronId;
    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "nvidia-nemotron-sidebar.html");

    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;

    public NvidiaCatalogMetadataProviderTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
    }

    public void Dispose() => _testDb.Dispose();

    private static NvidiaCatalogMetadataProvider ProviderWith(
        HttpMessageHandler handler, ILogService? log = null, TimeProvider? timeProvider = null) =>
        new(new StubFactory(handler), log ?? new NullLog(), timeProvider);

    private static Task<string> FixtureAsync() => File.ReadAllTextAsync(FixturePath);

    /// <summary>Provider preset NVIDIA từ DB thật + model cần lấy metadata.</summary>
    private async Task<(Provider Provider, Model Model)> NvidiaModelAsync(string modelId = NemotronId)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.SingleAsync(p => p.Name == "NVIDIA NIM Free");
        var model = new Model { ProviderId = provider.Id, ModelId = modelId };
        db.Models.Add(model);
        await db.SaveChangesAsync();
        return (provider, model);
    }

    private async Task<(Provider Provider, Model Model)> NonNvidiaModelAsync()
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider
        {
            Name = "Local",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
        };
        var model = new Model { ModelId = NemotronId };
        provider.Models.Add(model);
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return (provider, model);
    }

    [Fact]
    public async Task FetchAsync_ParsesSidebarModalitiesAndContext()
    {
        var (provider, model) = await NvidiaModelAsync();
        var handler = new HtmlHandler(await FixtureAsync());
        var factory = new StubFactory(handler);
        var metadataProvider = new NvidiaCatalogMetadataProvider(factory, new NullLog());

        var meta = await metadataProvider.FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.Equal(262_000, meta.ContextWindow);
        Assert.Equal(new[] { "Text", "Image", "Video" }, meta.InputModalities);
        Assert.Equal(new[] { "Text" }, meta.OutputModalities);
        // Sidebar NVIDIA không nói tools/reasoning/vision-field → không bịa phát biểu.
        Assert.Null(meta.SupportsTools);
        Assert.Null(meta.SupportsThink);
        Assert.Null(meta.SupportsVision);
        Assert.Null(meta.ThinkEfforts);
        Assert.True(metadataProvider.ReturnsCompleteFacts);

        Assert.Equal(NemotronUrl, handler.LastUri!.ToString());
        Assert.Null(handler.LastAuthorization); // nguồn công khai, không auth
        Assert.Equal(ProviderRequestFactory.HttpClientName, factory.LastClientName);
    }

    [Fact]
    public async Task FetchAsync_PageWithoutSidebar_ReturnsNull()
    {
        var (provider, model) = await NvidiaModelAsync();
        const string html = """
            <!DOCTYPE html>
            <html><body><main><h1>Legacy model page</h1><div>No model card</div></main></body></html>
            """;

        var meta = await ProviderWith(new HtmlHandler(html)).FetchAsync(provider, model);

        Assert.Null(meta);
    }

    [Fact]
    public async Task FetchAsync_NonNvidiaProvider_ReturnsNull()
    {
        var (provider, model) = await NonNvidiaModelAsync();
        var handler = new CountingHtmlHandler(await FixtureAsync());

        Assert.Null(await ProviderWith(handler).FetchAsync(provider, model));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("262K", 262_000)]
    [InlineData("1M", 1_000_000)]
    [InlineData("8192", 8192)]
    [InlineData("1.5K", 1_500)]
    [InlineData("262k", 262_000)]
    public async Task FetchAsync_ContextLengthSuffix_Converts(string raw, int expected)
    {
        var (provider, model) = await NvidiaModelAsync();

        var meta = await ProviderWith(new HtmlHandler(PageWith(context: raw)))
            .FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.Equal(expected, meta.ContextWindow);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("1.2345K")]
    [InlineData("9999999999")]
    public async Task FetchAsync_MalformedContextLength_ReturnsNullContext(string raw)
    {
        var (provider, model) = await NvidiaModelAsync();

        var meta = await ProviderWith(new HtmlHandler(PageWith(context: raw)))
            .FetchAsync(provider, model);

        Assert.NotNull(meta); // sidebar có mặt → vẫn là bước biết, chỉ context không đọc được
        Assert.Null(meta.ContextWindow);
    }

    [Fact]
    public async Task FetchAsync_MissingContextLength_ReturnsNullContext()
    {
        var (provider, model) = await NvidiaModelAsync();

        var meta = await ProviderWith(new HtmlHandler(PageWith(context: null)))
            .FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.Null(meta.ContextWindow);
        Assert.Equal(new[] { "Text", "Image", "Video" }, meta.InputModalities);
    }

    [Fact]
    public async Task FetchAsync_HtmlEntitiesDecodedAndWhitespaceNormalized()
    {
        var (provider, model) = await NvidiaModelAsync();
        // Entity &nbsp;/&#32; và khoảng trắng thừa xuất hiện trong HTML thật khi render.
        var html = PageWith(context: "262&nbsp;K", input: "Text,&#32;Image,  Video ");

        var meta = await ProviderWith(new HtmlHandler(html)).FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.Equal(262_000, meta.ContextWindow);
        Assert.Equal(new[] { "Text", "Image", "Video" }, meta.InputModalities);
    }

    [Fact]
    public async Task FetchAsync_UnknownModalityToken_DroppedAndLoggedOnce()
    {
        var (provider, model) = await NvidiaModelAsync();
        var log = new RecordingLog();
        var metadataProvider = ProviderWith(
            new HtmlHandler(PageWith(input: "Text, Hologram")), log);

        var meta = await metadataProvider.FetchAsync(provider, model);

        Assert.NotNull(meta);
        // Provider giữ token gốc; mapping sang ModalityKind (và drop token lạ) ở ModelMetadataService.
        Assert.Equal(new[] { "Text", "Hologram" }, meta.InputModalities);
        var patch = MetadataPatchMapper.From(meta);
        Assert.True(patch.Modalities[(ModalityDirection.Input, ModalityKind.Text)]);
        Assert.False(patch.Modalities[(ModalityDirection.Input, ModalityKind.Image)]);
        Assert.False(patch.Modalities[(ModalityDirection.Input, ModalityKind.Audio)]);
        Assert.False(patch.Modalities[(ModalityDirection.Input, ModalityKind.Video)]);
        Assert.False(patch.Modalities[(ModalityDirection.Input, ModalityKind.File)]);

        var warning = Assert.Single(log.Warnings);
        Assert.Contains("Hologram", warning);

        // Cache: lần hai không parse lại → không log lặp.
        Assert.NotNull(await metadataProvider.FetchAsync(provider, model));
        Assert.Single(log.Warnings);
    }

    [Fact]
    public async Task FetchAsync_PatchHasNoToolsOrReasoningKeys()
    {
        var (provider, model) = await NvidiaModelAsync();

        var meta = await ProviderWith(new HtmlHandler(await FixtureAsync()))
            .FetchAsync(provider, model);

        Assert.NotNull(meta);
        var patch = MetadataPatchMapper.From(meta);

        // R-T7-2: nguồn chỉ nói modality; tools/reasoning để user tick tay.
        Assert.Empty(patch.Capabilities);
        Assert.Empty(patch.ReasoningEfforts);
        // Input + output list có mặt → full direction; không có key thừa ngoài 2 hướng.
        Assert.Equal(10, patch.Modalities.Count); // 5 ModalityKind × 2 direction
        Assert.Equal(
            new[] { ModalityDirection.Input, ModalityDirection.Output },
            patch.Modalities.Keys.Select(k => k.Direction).Distinct().Order().ToArray());
    }

    [Fact]
    public async Task FetchAsync_SecondCall_UsesCache()
    {
        var (provider, model) = await NvidiaModelAsync();
        var handler = new CountingHtmlHandler(await FixtureAsync());
        var metadataProvider = ProviderWith(handler);

        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(await metadataProvider.FetchAsync(provider, model));
        }

        Assert.Equal(1, handler.Calls);
        Assert.Equal(NemotronUrl, handler.LastUri!.ToString());
        Assert.Null(handler.LastAuthorization);
    }

    [Fact]
    public async Task FetchAsync_PageWithoutSidebar_IsNegativeCached()
    {
        var (provider, model) = await NvidiaModelAsync();
        var handler = new CountingHtmlHandler("<html><body>no model card</body></html>");
        var metadataProvider = ProviderWith(handler);

        Assert.Null(await metadataProvider.FetchAsync(provider, model));
        Assert.Null(await metadataProvider.FetchAsync(provider, model));

        // 66/81 trang không có sidebar: cache cả kết quả null để lần fill sau không tải lại.
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task FetchAsync_TtlExpired_Refetches()
    {
        var (provider, model) = await NvidiaModelAsync();
        var handler = new CountingHtmlHandler(await FixtureAsync());
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
        var (provider, model) = await NvidiaModelAsync();

        Assert.Null(await ProviderWith(new StatusHandler(HttpStatusCode.InternalServerError))
            .FetchAsync(provider, model));
    }

    [Fact]
    public async Task FetchAsync_HttpError_NotCached()
    {
        var (provider, model) = await NvidiaModelAsync();
        var handler = new SequenceStatusHandler(
            [HttpStatusCode.InternalServerError, HttpStatusCode.OK], await FixtureAsync());
        var metadataProvider = ProviderWith(handler);

        Assert.Null(await metadataProvider.FetchAsync(provider, model));
        Assert.NotNull(await metadataProvider.FetchAsync(provider, model));

        Assert.Equal(2, handler.Calls); // lỗi HTTP không được cache
    }

    [Fact]
    public async Task FetchAsync_DifferentModels_FetchConcurrently()
    {
        var (provider, firstModel) = await NvidiaModelAsync("nvidia/model-a");
        var (_, secondModel) = await NvidiaModelAsync("nvidia/model-b");
        var handler = new ConcurrentHtmlHandler(await FixtureAsync());
        var metadataProvider = ProviderWith(handler);

        var first = metadataProvider.FetchAsync(provider, firstModel);
        var second = metadataProvider.FetchAsync(provider, secondModel);
        try
        {
            // Không có global lock: cả hai request phải cùng bay trước khi được thả.
            await handler.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            handler.Release.TrySetResult();
        }

        var results = await Task.WhenAll(first, second);
        Assert.All(results, meta => Assert.NotNull(meta));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task TryFillAsync_WritesOnlyModalityFacts_NoToolsOrReasoningRows()
    {
        var (_, model) = await NvidiaModelAsync();
        var service = new ModelMetadataService(
            _db, new NullLog(), [ProviderWith(new HtmlHandler(await FixtureAsync()))],
            new ModelCapabilityWriter(_db, new NullLog()));

        await service.TryFillAsync(model.Id);

        var view = await new ModelCapabilityReader(_db).ReadAsync(model.Id);
        Assert.Null(view.Tools);
        Assert.Null(view.Reasoning);
        Assert.Empty(view.Efforts);
        Assert.True(view.Supports(ModalityDirection.Input, ModalityKind.Text));
        Assert.True(view.Supports(ModalityDirection.Input, ModalityKind.Image));
        Assert.True(view.Supports(ModalityDirection.Input, ModalityKind.Video));
        Assert.False(view.Supports(ModalityDirection.Input, ModalityKind.Audio));
        Assert.False(view.Supports(ModalityDirection.Input, ModalityKind.File));
        Assert.True(view.Supports(ModalityDirection.Output, ModalityKind.Text));
        Assert.False(view.Supports(ModalityDirection.Output, ModalityKind.Image));
        Assert.False(view.Supports(ModalityDirection.Output, ModalityKind.Audio));
        Assert.False(view.Supports(ModalityDirection.Output, ModalityKind.Video));
        Assert.False(view.Supports(ModalityDirection.Output, ModalityKind.File));
        Assert.Equal(new[] { "text", "image", "video" }, view.InputModalities);
        Assert.Equal(new[] { "text" }, view.OutputModalities);

        using var db = _db.CreateDbContext();
        Assert.Empty(await db.ModelCapabilities.Where(c => c.ModelId == model.Id).ToListAsync());
        Assert.Equal(10, await db.ModelModalities.CountAsync(m => m.ModelId == model.Id));
        var stored = await db.Models.SingleAsync(m => m.Id == model.Id);
        Assert.Equal(262_000, stored.ContextWindow);
        // 8 cột legacy không bị provider mới ghi (Task 11 mới bỏ).
        Assert.Null(stored.SupportsTools);
        Assert.Null(stored.ThinkEfforts);
        Assert.Null(stored.InputModalities);
        Assert.Null(stored.OutputModalities);
    }

    [Fact]
    public async Task TryFillAsync_ContextWindowWrittenOnlyWhenNull()
    {
        var (_, model) = await NvidiaModelAsync();
        using (var seedDb = _db.CreateDbContext())
        {
            var seeded = await seedDb.Models.SingleAsync(m => m.Id == model.Id);
            seeded.ContextWindow = 4096;
            await seedDb.SaveChangesAsync();
        }
        var service = new ModelMetadataService(
            _db, new NullLog(), [ProviderWith(new HtmlHandler(await FixtureAsync()))],
            new ModelCapabilityWriter(_db, new NullLog()));

        await service.TryFillAsync(model.Id);

        using var db = _db.CreateDbContext();
        Assert.Equal(4096, (await db.Models.SingleAsync(m => m.Id == model.Id)).ContextWindow);
        // Context đã có không chặn fact modality.
        Assert.True((await new ModelCapabilityReader(_db).ReadAsync(model.Id)).Vision);
    }

    [Fact]
    public async Task TryFillAsync_MissingOutputList_DoesNotInventOutputFacts()
    {
        var (_, model) = await NvidiaModelAsync();
        var service = new ModelMetadataService(
            _db, new NullLog(), [ProviderWith(new HtmlHandler(PageWith(output: null)))],
            new ModelCapabilityWriter(_db, new NullLog()));

        await service.TryFillAsync(model.Id);

        var view = await new ModelCapabilityReader(_db).ReadAsync(model.Id);
        Assert.True(view.Supports(ModalityDirection.Input, ModalityKind.Text));
        Assert.Empty(view.OutputModalities);
        Assert.Null(view.Supports(ModalityDirection.Output, ModalityKind.Text));
        Assert.Null(view.Supports(ModalityDirection.Output, ModalityKind.Image));
        Assert.Null(view.Supports(ModalityDirection.Output, ModalityKind.Audio));
        Assert.Null(view.Supports(ModalityDirection.Output, ModalityKind.Video));
        Assert.Null(view.Supports(ModalityDirection.Output, ModalityKind.File));

        using var db = _db.CreateDbContext();
        var rows = await db.ModelModalities.Where(m => m.ModelId == model.Id).ToListAsync();
        Assert.Equal(5, rows.Count);
        Assert.All(rows, row => Assert.Equal(ModalityDirection.Input, row.Direction));
    }

    // ===== Test doubles (house pattern: handler/factory/recording log cục bộ) =====

    /// <summary>Trang tối giản cùng shape sidebar thật — dùng cho các biến thể context/modality.</summary>
    private static string PageWith(
        string? context = "262K",
        string? input = "Text, Image, Video",
        string? output = "Text")
    {
        var rows = new StringBuilder();
        AppendRow(rows, "Provider", "NVIDIA");
        AppendRow(rows, "Last Modified", "<time dateTime=\"2026-10-15T14:13:52.977Z\">4 days ago</time>");
        if (context is not null) AppendRow(rows, "Context Length", context);
        AppendRow(rows, "Parameters", "124B");
        if (input is not null) AppendRow(rows, "Input Modalities", input);
        if (output is not null) AppendRow(rows, "Output Modalities", output);

        var page = new StringBuilder();
        page.AppendLine("<!DOCTYPE html>");
        page.AppendLine("<html lang=\"en\">");
        page.AppendLine("  <body>");
        page.AppendLine("    <main>");
        page.AppendLine("      <aside aria-label=\"Model information\" class=\"nv-flex rounded-2 border border-tw120 p-6\" data-testid=\"nv-flex\">");
        page.AppendLine("        <section aria-labelledby=\"sidebar-specifications-heading\">");
        page.AppendLine("          <dl class=\"grid grid-cols-2 gap-2 [&amp;&gt;*]:min-w-0\">");
        page.Append(rows);
        page.AppendLine("          </dl>");
        page.AppendLine("        </section>");
        page.AppendLine("      </aside>");
        page.AppendLine("    </main>");
        page.AppendLine("  </body>");
        page.AppendLine("</html>");
        return page.ToString();
    }

    private static void AppendRow(StringBuilder sb, string label, string value)
    {
        sb.AppendLine("            <dt class=\"flex min-h-10 items-center gap-2 text-placeholder\" id=\"_R_x_\">");
        sb.AppendLine("              <svg data-icon-name=\"business\" height=\"12\" width=\"12\" aria-hidden=\"true\"><use href=\"/assets/icons/business.svg#business\"></use></svg>");
        sb.AppendLine($"              <span class=\"nv-text nv-text--label-regular-md truncate leading-loose\" data-testid=\"nv-text\">{label}</span>");
        sb.AppendLine("            </dt>");
        sb.AppendLine($"            <dd aria-labelledby=\"_R_x_\" class=\"flex min-h-10 items-center\"><span class=\"nv-text nv-text--label-regular-md\" data-testid=\"nv-text\">{value}</span></dd>");
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

    private class HtmlHandler(string html) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            });
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    private sealed class SequenceStatusHandler(HttpStatusCode[] statuses, string html) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var status = statuses[Math.Min(Calls, statuses.Length - 1)];
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(status == HttpStatusCode.OK ? html : "{}"),
            });
        }
    }

    private sealed class CountingHtmlHandler(string html) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public Uri? LastUri { get; private set; }
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            LastUri = request.RequestUri;
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            });
        }
    }

    /// <summary>Giữ request mở cho tới khi cả hai model cùng bay — chứng minh không có global lock.</summary>
    private sealed class ConcurrentHtmlHandler(string html) : HttpMessageHandler
    {
        private int _calls;
        private int _active;

        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource BothStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (Interlocked.Increment(ref _active) >= 2)
            {
                BothStarted.TrySetResult();
            }
            await Release.Task.ConfigureAwait(false);
            Interlocked.Decrement(ref _active);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            };
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
