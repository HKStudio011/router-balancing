using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class FreeModelSyncMergeTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public FreeModelSyncMergeTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db); // 4 preset — dùng chính "OpenRouter Free" (DetectKind PricingZeroOrFreeSuffix)
    }

    public void Dispose() => _testDb.Dispose();

    private FreeModelSyncService ServiceWith(string json) =>
        new(_db, _protector, new StubFactory(new JsonHandler(json)), new NullLog());

    private async Task<long> OpenRouterIdAsync()
    {
        using var db = _db.CreateDbContext();
        return (await db.Providers.SingleAsync(p => p.Name == "OpenRouter Free")).Id;
    }

    private async Task AddModelAsync(long providerId, string modelId, bool isManual, string? displayName = null)
    {
        using var db = _db.CreateDbContext();
        db.Models.Add(new Model
        {
            ProviderId = providerId,
            ModelId = modelId,
            IsManual = isManual,
            DisplayName = displayName,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SyncProviderAsync_FirstSync_InsertsFreeModelsAndSetsLastSyncAt()
    {
        var id = await OpenRouterIdAsync();
        var service = ServiceWith(FreeModelFixtures.OpenRouter);

        var count = await service.SyncProviderAsync(id);

        Assert.Equal(3, count); // fixture: 3 free / 4 total (gpt-4o trả phí bị loại)
        using var db = _db.CreateDbContext();
        var models = await db.Models.AsNoTracking()
            .Where(m => m.ProviderId == id)
            .OrderBy(m => m.ModelId)
            .ToListAsync();
        Assert.Equal(
            new[] { "inclusionai/ling-3.0-flash-sante:free", "stealth/space-bunny-alpha", "vendor/mispriced:free" },
            models.Select(m => m.ModelId));
        Assert.All(models, m =>
        {
            Assert.False(m.IsManual);
            Assert.True(m.Enabled);
        });
        var preset = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.NotNull(preset.LastModelSyncAt);
    }

    [Fact]
    public async Task SyncProviderAsync_ManualModelNotInApi_IsKept()
    {
        var id = await OpenRouterIdAsync();
        await AddModelAsync(id, "my-private-model", isManual: true);
        var service = ServiceWith(FreeModelFixtures.OpenRouter);

        await service.SyncProviderAsync(id);

        using var db = _db.CreateDbContext();
        Assert.Contains(await db.Models.AsNoTracking().ToListAsync(),
            m => m.ModelId == "my-private-model" && m.IsManual); // D5: manual không bao giờ bị xoá
    }

    [Fact]
    public async Task SyncProviderAsync_ManualModelInApi_MetadataNotOverwritten()
    {
        var id = await OpenRouterIdAsync();
        await AddModelAsync(id, "stealth/space-bunny-alpha", isManual: true, displayName: "User Name");
        var service = ServiceWith(FreeModelFixtures.OpenRouter);

        await service.SyncProviderAsync(id);

        using var db = _db.CreateDbContext();
        var model = await db.Models.AsNoTracking()
            .SingleAsync(m => m.ProviderId == id && m.ModelId == "stealth/space-bunny-alpha");
        Assert.True(model.IsManual);
        Assert.Equal("User Name", model.DisplayName); // API nói "Space Bunny Alpha" — không đè (§6.2.6)
    }

    [Fact]
    public async Task SyncProviderAsync_SecondSyncWithChangedFixture_DeletesStaleFetchedAndUpdatesMetadata()
    {
        var id = await OpenRouterIdAsync();
        await AddModelAsync(id, "stale-fetched", isManual: false);
        await AddModelAsync(id, "my-manual", isManual: true);
        var service = ServiceWith(FreeModelFixtures.OpenRouter);
        await service.SyncProviderAsync(id);

        // Fixture đổi: mất "vendor/mispriced:free", "ling" đổi name/context, thêm model mới
        const string changed = """
        {
          "data": [
            { "id": "inclusionai/ling-3.0-flash-sante:free", "name": "Ling v2",
              "pricing": { "prompt": "0", "completion": "0" }, "context_length": 1000000 },
            { "id": "stealth/space-bunny-alpha", "name": "Space Bunny Alpha",
              "pricing": { "prompt": "0.0", "completion": "0.0" } },
            { "id": "new/provider-model", "name": "New Model",
              "pricing": { "prompt": "0", "completion": "0" } }
          ]
        }
        """;
        var count = await ServiceWith(changed).SyncProviderAsync(id);

        Assert.Equal(3, count);
        using var db = _db.CreateDbContext();
        var models = await db.Models.AsNoTracking()
            .Where(m => m.ProviderId == id)
            .OrderBy(m => m.ModelId)
            .ToListAsync();
        Assert.Equal(
            new[]
            {
                "inclusionai/ling-3.0-flash-sante:free",
                "my-manual",
                "new/provider-model",
                "stealth/space-bunny-alpha",
            },
            models.Select(m => m.ModelId)); // "stale-fetched" + "vendor/mispriced:free" đã đi, manual còn
        var ling = models.Single(m => m.ModelId == "inclusionai/ling-3.0-flash-sante:free");
        Assert.Equal("Ling v2", ling.DisplayName);
        Assert.Equal(1000000, ling.ContextWindow); // metadata update khi API có
    }

    [Fact]
    public async Task SyncProviderAsync_EmptyFreeResult_ThrowsAndKeepsExistingModels()
    {
        var id = await OpenRouterIdAsync();
        await AddModelAsync(id, "fetched-old", isManual: false);
        await AddModelAsync(id, "my-manual", isManual: true);
        var service = ServiceWith(FreeModelFixtures.OpenRouterAllPaid); // 0 model free

        var ex = await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(id));

        Assert.Contains("0 free models", ex.Message);
        using var db = _db.CreateDbContext();
        var models = await db.Models.AsNoTracking().Where(m => m.ProviderId == id).ToListAsync();
        Assert.Equal(2, models.Count); // guard §6.2.5: không xoá gì cả
        Assert.Null((await db.Providers.AsNoTracking().SingleAsync(p => p.Id == id)).LastModelSyncAt);
    }

    [Fact]
    public async Task SyncProviderAsync_DisabledPreset_ManualSyncStillWorks()
    {
        // §9: preset chưa Enabled — periodic bỏ qua nhưng manual vẫn cho phép (user chủ động)
        var id = await OpenRouterIdAsync(); // seeded Enabled = false
        var service = ServiceWith(FreeModelFixtures.OpenRouter);

        var count = await service.SyncProviderAsync(id);

        Assert.Equal(3, count);
    }

    // ===== HTTP doubles (pattern ModelServiceTests) =====

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
