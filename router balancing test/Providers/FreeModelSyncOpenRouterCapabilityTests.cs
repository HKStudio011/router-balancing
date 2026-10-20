using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

/// <summary>
/// Task 5: danh sách <c>/v1/models</c> của OpenRouter mang sẵn capability (spec capability-model §7) —
/// sync phải ghi fact ModelCapability/ModelModality qua writer với <c>Source=Catalog</c> và
/// <c>sourceIsCompleteList=true</c>, KHÔNG ghi 8 cột capability cũ nữa. [RF-1]: model thứ hai
/// (supported_parameters rỗng) phải có dòng KnownAbsent=1 thật, không phải "chưa biết".
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

    private FreeModelSyncService ServiceWith(string json) =>
        new(_db, _protector, new StubFactory(new JsonHandler(json)), new NullLog(),
            new ModelCapabilityWriter(_db, new NullLog()));

    [Fact]
    public async Task SyncProviderAsync_OpenRouter_WritesModalityAndToolFacts()
    {
        long providerId;
        using (var db = _db.CreateDbContext())
        {
            providerId = (await db.Providers.SingleAsync(p => p.Name == "OpenRouter Free")).Id;
        }
        var service = ServiceWith(FreeModelFixtures.OpenRouterWithCapabilities);

        var count = await service.SyncProviderAsync(providerId);

        Assert.Equal(2, count);
        var reader = new ModelCapabilityReader(_db);
        long richId, plainId;
        using (var db = _db.CreateDbContext())
        {
            richId = (await db.Models.AsNoTracking()
                .SingleAsync(m => m.ProviderId == providerId && m.ModelId == "vendor/capable-model:free")).Id;
            plainId = (await db.Models.AsNoTracking()
                .SingleAsync(m => m.ProviderId == providerId && m.ModelId == "vendor/plain-model:free")).Id;
        }

        // Model 1: input ["text","image","audio"] + output ["text"] + supported_parameters ["tools","reasoning"]
        var rich = await reader.ReadAsync(richId);
        Assert.True(rich.Tools);
        Assert.True(rich.Reasoning);
        Assert.True(rich.Vision);
        Assert.True(rich.AudioInput);
        // Full-list: video input không có trong mảng ⇒ KnownAbsent (false), không phải chưa biết.
        Assert.False(rich.VideoInput);
        Assert.False(rich.AudioOutput);
        Assert.Equal(new[] { "text", "image", "audio" }, rich.InputModalities);
        Assert.Equal(new[] { "text" }, rich.OutputModalities);

        // Model 2: input/output ["text"] + supported_parameters [] → mọi phát biểu đều "không" trừ text.
        var plain = await reader.ReadAsync(plainId);
        Assert.False(plain.Tools); // [RF-1] KnownAbsent=1 — nguồn full-list khẳng định không có
        Assert.False(plain.Reasoning);
        Assert.Equal(new[] { "text" }, plain.InputModalities);
        Assert.Equal(new[] { "text" }, plain.OutputModalities);
        Assert.Equal(8, plain.Modalities.Count); // 2 chiều × 4 loại — không key nào bỏ sót
        Assert.True(plain.Modalities[(ModalityDirection.Input, ModalityKind.Text)]);
        Assert.False(plain.Modalities[(ModalityDirection.Input, ModalityKind.Image)]);
        Assert.False(plain.Modalities[(ModalityDirection.Input, ModalityKind.Audio)]);
        Assert.False(plain.Modalities[(ModalityDirection.Input, ModalityKind.Video)]);
        Assert.True(plain.Modalities[(ModalityDirection.Output, ModalityKind.Text)]);
        Assert.False(plain.Modalities[(ModalityDirection.Output, ModalityKind.Image)]);
        Assert.False(plain.Modalities[(ModalityDirection.Output, ModalityKind.Audio)]);
        Assert.False(plain.Modalities[(ModalityDirection.Output, ModalityKind.Video)]);

        // Nguồn của mọi dòng fact là Catalog (writer đóng dấu) — model 2 toàn KnownAbsent=1.
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
            Assert.Equal(6, modRows.Count(f => f.KnownAbsent)); // 8 key trừ 2 dòng text present
        }

        // Cột capability cũ KHÔNG còn được ghi (Task 11 mới xoá cột); context_length vẫn là cột.
        using (var db = _db.CreateDbContext())
        {
            var plainRow = await db.Models.AsNoTracking()
                .SingleAsync(m => m.ProviderId == providerId && m.ModelId == "vendor/plain-model:free");
            Assert.Null(plainRow.SupportsTools);
            Assert.Null(plainRow.ThinkEfforts);
            Assert.Null(plainRow.InputModalities);
            Assert.Null(plainRow.OutputModalities);
            Assert.Equal(131072, plainRow.ContextWindow);
        }
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
