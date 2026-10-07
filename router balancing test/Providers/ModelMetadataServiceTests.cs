using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ModelMetadataServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;

    public ModelMetadataServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
    }

    public void Dispose() => _testDb.Dispose();

    private sealed class FixedProvider(ModelMetadata? result) : IModelMetadataProvider
    {
        public Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    /// <summary>Bước chain ghi lại ProxyTarget tại thời điểm gọi — bắt lỗi thiếu scope probe.</summary>
    private sealed class TargetRecordingProvider : IModelMetadataProvider
    {
        public ProxyTarget? CapturedTarget { get; private set; }

        public Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default)
        {
            CapturedTarget = ProxyTarget.Current.Value;
            return Task.FromResult<ModelMetadata?>(null);
        }
    }

    // NullLog: dùng lại từ `router balancing test/TestDoubles.cs` (Task 3) — KHÔNG khai
    // báo inner class (bản inline thiếu Write/Count → CS0535 với ILogService thật).

    private async Task<long> SeedModelAsync()
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com" };
        provider.Models.Add(new Model { ModelId = "gpt-4o" });
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Models[0].Id;
    }

    [Fact]
    public async Task TryFillAsync_WhenFirstProviderKnows_AppliesAndStops()
    {
        var modelId = await SeedModelAsync();
        var known = new ModelMetadata(128_000, true, false, null, """["text"]""", """["text"]""");
        var service = new ModelMetadataService(_db, new NullLog(),
            [new FixedProvider(known), new FixedProvider(new ModelMetadata(1, null, null, null, null, null))]);

        await service.TryFillAsync(modelId);

        using var db = _db.CreateDbContext();
        var model = await db.Models.SingleAsync(m => m.Id == modelId);
        Assert.Equal(128_000, model.ContextWindow);
        Assert.True(model.SupportsVision);
        // Bước 1 trả non-null → service dừng, không chạy bước 2 (field 1 không bị ghi đè)
        Assert.NotNull(model.InputModalities);
    }

    [Fact]
    public async Task TryFillAsync_WhenFirstReturnsNull_FallsBackToSecond()
    {
        var modelId = await SeedModelAsync();
        var service = new ModelMetadataService(_db, new NullLog(),
            [new FixedProvider(null), new FixedProvider(new ModelMetadata(99_999, null, null, null, null, null))]);

        await service.TryFillAsync(modelId);

        using var db = _db.CreateDbContext();
        var model = await db.Models.SingleAsync(m => m.Id == modelId);
        Assert.Equal(99_999, model.ContextWindow);
    }

    [Fact]
    public async Task TryFillAsync_WhenAllNull_LeavesModelUnchanged()
    {
        var modelId = await SeedModelAsync();
        var service = new ModelMetadataService(_db, new NullLog(), [new FixedProvider(null)]);

        await service.TryFillAsync(modelId);

        using var db = _db.CreateDbContext();
        var model = await db.Models.SingleAsync(m => m.Id == modelId);
        Assert.Null(model.ContextWindow);
        Assert.False(model.SupportsVision);
    }

    [Fact]
    public async Task TryFillAsync_WhenFieldAlreadySet_KeepsExistingValue()
    {
        var modelId = await SeedModelAsync();
        using (var db = _db.CreateDbContext())
        {
            // 'seeded' thay vì 'model' — brief typo gây CS0136 (trùng local với khai
            // báo ở scope method phía dưới); controller approved.
            var seeded = await db.Models.SingleAsync(m => m.Id == modelId);
            seeded.ContextWindow = 42;
            await db.SaveChangesAsync();
        }
        var service = new ModelMetadataService(_db, new NullLog(),
            [new FixedProvider(new ModelMetadata(128_000, true, null, null, null, null))]);

        await service.TryFillAsync(modelId);

        using var db2 = _db.CreateDbContext();
        var model = await db2.Models.SingleAsync(m => m.Id == modelId);
        Assert.Equal(42, model.ContextWindow); // không ghi đè giá trị đã có (spec §3.3)
        Assert.True(model.SupportsVision);     // false (default) → true vẫn điền được
    }

    [Fact]
    public async Task TryFillAsync_WhenModelUnknown_DoesNothing()
    {
        var service = new ModelMetadataService(_db, new NullLog(), [new FixedProvider(null)]);

        // Không ném — fill là best-effort
        await service.TryFillAsync(999);
    }

    [Fact]
    public async Task TryFillAsync_ScopesProxyTargetWithJunctionsForProbeChain()
    {
        long proxyId;
        using (var db = _db.CreateDbContext())
        {
            var proxy = new OutboundProxy { Scheme = "socks5", Host = "127.0.0.1", Port = 1080 };
            db.OutboundProxies.Add(proxy);
            await db.SaveChangesAsync();
            proxyId = proxy.Id;
        }
        long providerId, modelId;
        using (var db = _db.CreateDbContext())
        {
            var provider = new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com" };
            provider.Models.Add(new Model { ModelId = "gpt-4o" });
            db.Providers.Add(provider);
            await db.SaveChangesAsync();
            providerId = provider.Id;
            modelId = provider.Models[0].Id;
        }
        using (var db = _db.CreateDbContext())
        {
            db.Set<ProviderProxy>().Add(new ProviderProxy { ProviderId = providerId, ProxyId = proxyId });
            await db.SaveChangesAsync();
        }

        var probe = new TargetRecordingProvider();
        var service = new ModelMetadataService(_db, new NullLog(), [probe]);

        await service.TryFillAsync(modelId);

        // Regression 2026-10-06: thiếu ProxyTarget → request metadata rơi về global pool
        // (D7), provider không gán proxy vẫn đi qua SOCKS; junction phải được load để
        // provider CÓ proxy resolve đúng.
        Assert.NotNull(probe.CapturedTarget);
        Assert.Equal(providerId, probe.CapturedTarget!.Provider.Id);
        Assert.Single(probe.CapturedTarget.Provider.ProviderProxies);
        Assert.Equal(proxyId, probe.CapturedTarget.Provider.ProviderProxies[0].ProxyId);
        Assert.Null(ProxyTarget.Current.Value); // finally phải reset scope
    }
}
