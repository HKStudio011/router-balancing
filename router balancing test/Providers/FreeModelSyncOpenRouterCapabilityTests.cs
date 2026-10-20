using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

/// <summary>
/// Task 5: danh sách <c>/v1/models</c> của OpenRouter mang sẵn capability (spec capability-model §7,
/// mapping pin theo probe API thật 2026-10-20) — sync phải ghi fact ModelCapability/ModelModality/
/// ModelReasoningEffort qua writer với <c>Source=Catalog</c> và <c>sourceIsCompleteList=true</c>,
/// KHÔNG ghi 8 cột capability cũ nữa. [RF-1]: model (2) (supported_parameters rỗng, không có key
/// reasoning) phải có dòng KnownAbsent=1 thật, không phải "chưa biết".
/// R-T5-3: Reasoning = object <c>reasoning</c> có mặt HOẶC token "reasoning" trong supported_parameters.
/// R-T5-4: <c>reasoning.supported_efforts</c> → dòng ModelReasoningEffort; object không có efforts → không ghi effort.
/// R-T5-5: "file" là ModalityKind hợp lệ; token ngoài từ vựng → bỏ + log một lần mỗi model, không ném.
/// </summary>
public class FreeModelSyncOpenRouterCapabilityTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public FreeModelSyncOpenRouterCapabilityTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db); // preset "OpenRouter Free" — DetectKind PricingZeroOrFreeSuffix
    }

    public void Dispose() => _testDb.Dispose();

    private FreeModelSyncService ServiceWith(string json, ILogService? log = null) =>
        new(_db, _protector, new StubFactory(new JsonHandler(json)), log ?? new NullLog(),
            new ModelCapabilityWriter(_db, new NullLog()));

    private async Task<long> OpenRouterIdAsync()
    {
        using var db = _db.CreateDbContext();
        return (await db.Providers.SingleAsync(p => p.Name == "OpenRouter Free")).Id;
    }

    /// <summary>Sync fixture chuẩn rồi trả PK provider + PK Models.Id của 3 model theo fixture.</summary>
    private async Task<(long ProviderId, long RichId, long PlainId, long SilentId)> SyncFixtureAsync()
    {
        var providerId = await OpenRouterIdAsync();
        var count = await ServiceWith(FreeModelFixtures.OpenRouterWithCapabilities).SyncProviderAsync(providerId);
        Assert.Equal(3, count);
        using var db = _db.CreateDbContext();
        var rows = await db.Models.AsNoTracking()
            .Where(m => m.ProviderId == providerId)
            .ToListAsync();
        return (
            providerId,
            rows.Single(m => m.ModelId == "vendor/capable-model:free").Id,
            rows.Single(m => m.ModelId == "vendor/plain-model:free").Id,
            rows.Single(m => m.ModelId == "vendor/silent-reasoner:free").Id);
    }

    [Fact]
    public async Task SyncProviderAsync_OpenRouter_WritesModalityAndToolFacts()
    {
        var (providerId, richId, plainId, _) = await SyncFixtureAsync();
        var reader = new ModelCapabilityReader(_db);

        // Model (1): input ["text","image","audio","file"] + output ["text"] + params ["tools","reasoning"]
        var rich = await reader.ReadAsync(richId);
        Assert.True(rich.Tools);
        Assert.True(rich.Reasoning);
        Assert.True(rich.Vision);
        Assert.True(rich.AudioInput);
        // Full-list: video input không có trong mảng ⇒ KnownAbsent (false), không phải chưa biết.
        Assert.False(rich.VideoInput);
        Assert.False(rich.AudioOutput);
        // View.InputModalities sort theo ModalityKind tăng dần (Text,Image,Audio,Video,File).
        Assert.Equal(new[] { "text", "image", "audio", "file" }, rich.InputModalities);
        Assert.Equal(new[] { "text" }, rich.OutputModalities);

        // Model (2): input/output ["text"] + supported_parameters [] + không có key reasoning
        // → mọi phát biểu đều "không" trừ text.
        var plain = await reader.ReadAsync(plainId);
        Assert.False(plain.Tools); // [RF-1] KnownAbsent=1 — nguồn full-list khẳng định không có
        Assert.False(plain.Reasoning);
        Assert.Equal(new[] { "text" }, plain.InputModalities);
        Assert.Equal(new[] { "text" }, plain.OutputModalities);
        Assert.Equal(10, plain.Modalities.Count); // 2 chiều × 5 loại (File vào từ vựng, R-T5-5)
        Assert.True(plain.Modalities[(ModalityDirection.Input, ModalityKind.Text)]);
        Assert.False(plain.Modalities[(ModalityDirection.Input, ModalityKind.Image)]);
        Assert.False(plain.Modalities[(ModalityDirection.Input, ModalityKind.Audio)]);
        Assert.False(plain.Modalities[(ModalityDirection.Input, ModalityKind.Video)]);
        Assert.False(plain.Modalities[(ModalityDirection.Input, ModalityKind.File)]);
        Assert.True(plain.Modalities[(ModalityDirection.Output, ModalityKind.Text)]);
        Assert.False(plain.Modalities[(ModalityDirection.Output, ModalityKind.Image)]);
        Assert.False(plain.Modalities[(ModalityDirection.Output, ModalityKind.Audio)]);
        Assert.False(plain.Modalities[(ModalityDirection.Output, ModalityKind.Video)]);
        Assert.False(plain.Modalities[(ModalityDirection.Output, ModalityKind.File)]);

        // Nguồn của mọi dòng fact là Catalog (writer đóng dấu) — model (2) toàn KnownAbsent=1.
        using (var db = _db.CreateDbContext())
        {
            var capRows = await db.ModelCapabilities.AsNoTracking()
                .Where(f => f.ModelId == plainId).ToListAsync();
            Assert.Equal(
                new[] { CapabilityKind.Tools, CapabilityKind.Reasoning },
                capRows.Select(f => f.Capability).OrderBy(c => c));
            Assert.All(capRows, f =>
            {
                Assert.Equal(CapabilitySource.Catalog, f.Source);
                Assert.True(f.KnownAbsent);
            });
            var modRows = await db.ModelModalities.AsNoTracking()
                .Where(f => f.ModelId == plainId).ToListAsync();
            Assert.All(modRows, f => Assert.Equal(CapabilitySource.Catalog, f.Source));
            Assert.Equal(8, modRows.Count(f => f.KnownAbsent)); // 10 key trừ 2 dòng text present
        }

        // Context window vẫn là cột scalar của Models — fact capability nằm ở bảng riêng.
        using (var db = _db.CreateDbContext())
        {
            var plainRow = await db.Models.AsNoTracking()
                .SingleAsync(m => m.ProviderId == providerId && m.ModelId == "vendor/plain-model:free");
            Assert.Equal(131072, plainRow.ContextWindow);
        }
    }

    [Fact]
    public async Task SyncProviderAsync_OpenRouter_ReasoningObject_WritesEffortFacts()
    {
        var (_, richId, _, silentId) = await SyncFixtureAsync();
        var reader = new ModelCapabilityReader(_db);

        // Model (1): reasoning object + supported_efforts ["low","high"] → Reasoning=true + 2 dòng effort.
        var rich = await reader.ReadAsync(richId);
        Assert.True(rich.Reasoning); // R-T5-3: object có mặt là đủ, dù token "reasoning" cũng có trong params
        Assert.Equal(new[] { "high", "low" }, rich.Efforts); // Efforts sort ordinal (R-T3-1)

        // Model (3): reasoning object NHƯNG supported_parameters không chứa "reasoning" và
        // supported_efforts rỗng → vẫn Reasoning=true (R-T5-3), và KHÔNG dòng effort nào (R-T5-4: không bịa).
        var silent = await reader.ReadAsync(silentId);
        Assert.True(silent.Reasoning);
        Assert.Empty(silent.ReasoningEfforts);
        Assert.True(silent.Tools); // tín hiệu tools đọc từ supported_parameters, độc lập reasoning

        using (var db = _db.CreateDbContext())
        {
            var richEfforts = await db.ModelReasoningEfforts.AsNoTracking()
                .Where(f => f.ModelId == richId).ToListAsync();
            Assert.Equal(new[] { "high", "low" }, richEfforts.Select(f => f.Level).OrderBy(l => l, StringComparer.Ordinal));
            Assert.All(richEfforts, f => Assert.Equal(CapabilitySource.Catalog, f.Source));
            Assert.Empty(await db.ModelReasoningEfforts.AsNoTracking()
                .Where(f => f.ModelId == silentId).ToListAsync());
        }
    }

    [Fact]
    public async Task SyncProviderAsync_OpenRouter_FileModality_WrittenAsFile()
    {
        var (_, richId, _, _) = await SyncFixtureAsync();

        // R-T5-5: token "file" trong input_modalities → dòng (input, file) KnownAbsent=0, không phải bị bỏ.
        var view = await new ModelCapabilityReader(_db).ReadAsync(richId);
        Assert.True(view.Supports(ModalityDirection.Input, ModalityKind.File));
        // Output không có file → KnownAbsent=1 (full-list).
        Assert.False(view.Supports(ModalityDirection.Output, ModalityKind.File));
        Assert.Contains("file", view.InputModalities);

        using var db = _db.CreateDbContext();
        var row = Assert.Single(await db.ModelModalities.AsNoTracking()
            .Where(f => f.ModelId == richId && f.Direction == ModalityDirection.Input && f.Modality == ModalityKind.File)
            .ToListAsync());
        Assert.False(row.KnownAbsent);
        Assert.Equal(CapabilitySource.Catalog, row.Source);
    }

    [Fact]
    public async Task SyncProviderAsync_OpenRouter_UnknownModalityToken_DroppedAndLogged()
    {
        // "pdf" (token của catalog khác, KHÔNG thuộc từ vựng kể cả sau R-T5-5 — "file" ≠ "pdf"):
        // bỏ + log ĐÚNG MỘT lần mỗi model, không ném (R-T5-5).
        const string json = """
        {
          "data": [
            { "id": "vendor/odd-model:free", "name": "Odd Model",
              "architecture": { "modality": "text+image->text",
                "input_modalities": ["text", "image", "pdf"], "output_modalities": ["text"] },
              "supported_parameters": ["tools"],
              "pricing": { "prompt": "0", "completion": "0" },
              "context_length": 65536 }
          ]
        }
        """;
        var log = new RecordingLog();
        var providerId = await OpenRouterIdAsync();

        var count = await ServiceWith(json, log).SyncProviderAsync(providerId);

        Assert.Equal(1, count); // không ném — sync hoàn tất bình thường
        long oddId;
        using (var db = _db.CreateDbContext())
        {
            oddId = (await db.Models.AsNoTracking()
                .SingleAsync(m => m.ProviderId == providerId && m.ModelId == "vendor/odd-model:free")).Id;
        }
        var view = await new ModelCapabilityReader(_db).ReadAsync(oddId);
        Assert.Equal(new[] { "text", "image" }, view.InputModalities);
        // "pdf" ≠ "file": không có dòng THUẬN cho file/audio — chúng chỉ là negative KnownAbsent
        // như mọi loại không được liệt kê (full-list). Nếu "pdf" bị map nhầm thành "file", Supports sẽ true.
        Assert.False(view.Supports(ModalityDirection.Input, ModalityKind.File));
        Assert.False(view.Supports(ModalityDirection.Input, ModalityKind.Audio));

        Assert.Single(log.Warnings); // đúng MỘT dòng log cho model
        Assert.Contains("pdf", log.Warnings[0]);
        Assert.Contains("vendor/odd-model:free", log.Warnings[0]);
    }

    /// <summary>ILogService chỉ bắt Warn — test "log một lần mỗi model" cần đếm số dòng cảnh báo.</summary>
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

    // ===== HTTP doubles (pattern FreeModelSyncMergeTests: JsonHandler/StubFactory) =====

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
