using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ModelServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public ModelServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
    }

    public void Dispose() => _testDb.Dispose();

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Handler ghi lại ProxyTarget tại thời điểm gửi — bắt lỗi thiếu scope probe.</summary>
    private sealed class ProbeRecordingHandler(string json) : HttpMessageHandler
    {
        public ProxyTarget? CapturedTarget { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CapturedTarget = ProxyTarget.Current.Value;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }

    private ModelService ServiceWith(string json, IModelMetadataService? metadata = null)
    {
        metadata ??= new ModelMetadataService(_db, new NullLog(), [new NoopMetadata()],
            new ModelCapabilityWriter(_db, new NullLog()), new ModelCapabilityReader(_db));
        return new ModelService(_db, _protector, new StubFactory(new JsonHandler(json)), new NullLog(),
            metadata, new ModelCapabilityWriter(_db, new NullLog()));
    }

    /// <summary>Ghi lại lời gọi fill để assert button semantics mà không cần HTTP/DB fact.</summary>
    private sealed class TrackingMetadataService : IModelMetadataService
    {
        public List<long> TryFillCalls { get; } = [];
        public List<long> FillMissingCalls { get; } = [];

        public Task TryFillAsync(long modelId, CancellationToken ct = default)
        {
            TryFillCalls.Add(modelId);
            return Task.CompletedTask;
        }

        public Task<int> FillMissingAsync(long providerId, CancellationToken ct = default)
        {
            FillMissingCalls.Add(providerId);
            return Task.FromResult(0);
        }

        public Task ResetToCatalogAsync(long modelId, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Catalog stub không biết gì — tests CRUD không quan tâm metadata.</summary>
    private sealed class NoopMetadata : IModelMetadataProvider
    {
        public bool ReturnsCompleteFacts => false;

        public CapabilitySource Source => CapabilitySource.Catalog;

        public Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default) =>
            Task.FromResult<ModelMetadata?>(null);
    }

    private async Task<long> SeedProviderAsync(params string[] existingModels)
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider
        {
            Name = "P",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
            Accounts =
            [
                new ProviderAccount
                {
                    Name = "Default",
                    ApiKeyEncrypted = _protector.Protect("sk-saved"),
                    Enabled = true,
                },
            ],
        };
        foreach (var id in existingModels)
        {
            provider.Models.Add(new Model { ModelId = id });
        }
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Id;
    }

    [Fact]
    public async Task FetchFromProvider_WhenOpenAiShape_AddsNewModels_SkipsExisting()
    {
        var providerId = await SeedProviderAsync("gpt-4o");
        var service = ServiceWith("""
            {"data":[{"id":"gpt-4o"},{"id":"gpt-4o-mini"},{"id":"gpt-4o"}]}
            """);

        var (added, skipped, _) = await service.FetchFromProviderAsync(providerId);

        // Payload cố ý chứa bản sao gpt-4o: unique index (ProviderId, ModelId) chặn
        // duplicate → service phải skip cả bản sao trong payload (spec §4: "skip thay vì
        // throw raw"), không được thêm lần 2.
        Assert.Equal(1, added);   // chỉ gpt-4o-mini là mới
        Assert.Equal(2, skipped); // gpt-4o đã có + bản sao gpt-4o trong payload (dedupe)

        using var db = _db.CreateDbContext();
        var models = await db.Models.Where(m => m.ProviderId == providerId).ToListAsync();
        Assert.Equal(2, models.Count);
        Assert.All(models, m => Assert.False(m.IsManual)); // auto-fetch
    }

    [Fact]
    public async Task FetchFromProvider_WhenAnthropicShape_ParsesIds()
    {
        var providerId = await SeedProviderAsync();
        var service = ServiceWith("""
            {"data":[{"type":"model","id":"claude-3-5-sonnet-20241022","display_name":"Claude 3.5 Sonnet","created_at":"2024-10-22"}]}
            """);

        var (added, skipped, _) = await service.FetchFromProviderAsync(providerId);

        Assert.Equal(1, added);
        Assert.Equal(0, skipped);

        using var db = _db.CreateDbContext();
        var model = await db.Models.SingleAsync(m => m.ProviderId == providerId);
        Assert.Equal("claude-3-5-sonnet-20241022", model.ModelId);
    }

    [Fact]
    public async Task FetchFromProvider_WhenProviderUnknown_ThrowsKeyNotFound()
    {
        var service = ServiceWith("""{"data":[]}""");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.FetchFromProviderAsync(999));
    }

    [Fact]
    public async Task FetchFromProvider_WhenProviderHasNoProxy_ScopesProxyTargetForDirectDispatch()
    {
        var providerId = await SeedProviderAsync();
        var probe = new ProbeRecordingHandler("""{"data":[]}""");
        var metadata = new ModelMetadataService(_db, new NullLog(), [new NoopMetadata()],
            new ModelCapabilityWriter(_db, new NullLog()), new ModelCapabilityReader(_db));
        var service = new ModelService(_db, _protector, new StubFactory(probe), new NullLog(), metadata,
            new ModelCapabilityWriter(_db, new NullLog()));

        await service.FetchFromProviderAsync(providerId);

        // Regression (2026-10-06): thiếu ProxyTarget → ProxyHealthHandler rơi về global
        // pool (D7) → provider không gán proxy vẫn bị gửi qua SOCKS proxy của pool.
        Assert.NotNull(probe.CapturedTarget);
        Assert.Equal(providerId, probe.CapturedTarget!.Provider.Id);
        Assert.Empty(probe.CapturedTarget.Provider.ProviderProxies);
        Assert.Null(ProxyTarget.Current.Value); // finally phải reset scope
    }

    [Fact]
    public async Task FetchFromProvider_WhenProviderHasProxy_JunctionsLoadedForResolver()
    {
        long proxyId;
        using (var db = _db.CreateDbContext())
        {
            var proxy = new OutboundProxy { Scheme = "socks5", Host = "127.0.0.1", Port = 1080 };
            db.OutboundProxies.Add(proxy);
            await db.SaveChangesAsync();
            proxyId = proxy.Id;
        }
        var providerId = await SeedProviderAsync();
        using (var db = _db.CreateDbContext())
        {
            db.Set<ProviderProxy>().Add(new ProviderProxy { ProviderId = providerId, ProxyId = proxyId });
            await db.SaveChangesAsync();
        }

        var probe = new ProbeRecordingHandler("""{"data":[]}""");
        var metadata = new ModelMetadataService(_db, new NullLog(), [new NoopMetadata()],
            new ModelCapabilityWriter(_db, new NullLog()), new ModelCapabilityReader(_db));
        var service = new ModelService(_db, _protector, new StubFactory(probe), new NullLog(), metadata,
            new ModelCapabilityWriter(_db, new NullLog()));

        await service.FetchFromProviderAsync(providerId);

        // Resolver đọc junction từ entity đã load — thiếu Include thì provider CÓ proxy
        // cũng bị resolve thành Direct (sai theo chiều ngược).
        Assert.NotNull(probe.CapturedTarget);
        Assert.Single(probe.CapturedTarget!.Provider.ProviderProxies);
        Assert.Equal(proxyId, probe.CapturedTarget.Provider.ProviderProxies[0].ProxyId);
    }

    [Fact]
    public async Task AddManual_WhenNew_PersistsWithIsManualFlag()
    {
        var providerId = await SeedProviderAsync();
        var service = ServiceWith("""{"data":[]}""");

        var model = await service.AddManualAsync(providerId, "my-model");

        Assert.True(model.IsManual);
        using var db = _db.CreateDbContext();
        var saved = await db.Models.SingleAsync(m => m.Id == model.Id);
        Assert.Equal("my-model", saved.ModelId);
    }

    [Fact]
    public async Task AddManualAsync_TriggersFillBestEffort()
    {
        var providerId = await SeedProviderAsync();
        var metadata = new TrackingMetadataService();
        var service = ServiceWith("""{"data":[]}""", metadata);

        var model = await service.AddManualAsync(providerId, "my-model");

        // R-T8-5: row mới luôn IsEmpty theo construction nên AddManual đi thẳng TryFillAsync;
        // không gọi FillMissingAsync (method đó thuộc semantics của nút Fetch/sync).
        Assert.Equal([model.Id], metadata.TryFillCalls);
        Assert.Empty(metadata.FillMissingCalls);
    }

    [Fact]
    public async Task FetchFromProvider_WhenNoNewModels_StillFillsMissingExistingEmptyModels()
    {
        var providerId = await SeedProviderAsync("already-there");
        var metadata = new TrackingMetadataService();
        var service = ServiceWith("""{"data":[{"id":"already-there"}]}""", metadata);

        var (added, skipped, filled) = await service.FetchFromProviderAsync(providerId);

        Assert.Equal(0, added);
        Assert.Equal(1, skipped);
        // R-T8-1: nút Fetch vẫn retry các model cũ đang rỗng dù lần này không thêm model nào.
        Assert.Equal([providerId], metadata.FillMissingCalls);
        Assert.Equal(0, filled); // fake trả 0 — giá trị thật đến từ FillMissingAsync
    }

    [Fact]
    public async Task AddManual_WhenDuplicate_ThrowsInvalidOperation()
    {
        var providerId = await SeedProviderAsync("gpt-4o");
        var service = ServiceWith("""{"data":[]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddManualAsync(providerId, "gpt-4o"));
    }

    [Fact]
    public async Task AddBulk_WhenMixedPayload_TrimsDedupsAndSkipsExisting()
    {
        var providerId = await SeedProviderAsync("gpt-4o");
        var service = ServiceWith("""{"data":[]}""");

        var (added, skipped) = await service.AddBulkAsync(providerId,
        [
            "  deepseek-chat  ",   // mới (sau trim)
            "gpt-4o",              // đã có
            "deepseek-chat",       // duplicate trong payload
            "",                    // dòng rỗng — bỏ
            "   ",                 // whitespace — bỏ
        ]);

        Assert.Equal(1, added);
        Assert.Equal(2, skipped);

        using var db = _db.CreateDbContext();
        var model = await db.Models.SingleAsync(m => m.ModelId == "deepseek-chat");
        Assert.True(model.IsManual);
    }

    [Fact]
    public async Task Remove_WhenCalled_DeletesOnlyThatModel()
    {
        var providerId = await SeedProviderAsync("a", "b");
        var service = ServiceWith("""{"data":[]}""");
        using (var db = _db.CreateDbContext())
        {
            var target = await db.Models.SingleAsync(m => m.ModelId == "a");
            await service.RemoveAsync(target.Id);
        }

        using var db2 = _db.CreateDbContext();
        Assert.False(await db2.Models.AnyAsync(m => m.ModelId == "a"));
        Assert.True(await db2.Models.AnyAsync(m => m.ModelId == "b"));
    }

    [Fact]
    public async Task RemoveAll_WhenCalled_ReturnsRemovedCount()
    {
        var providerId = await SeedProviderAsync("a", "b", "c");
        var service = ServiceWith("""{"data":[]}""");

        var removed = await service.RemoveAllAsync(providerId);

        Assert.Equal(3, removed);
        using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.Models.CountAsync(m => m.ProviderId == providerId));
    }

    [Fact]
    public async Task SetAllEnabled_WhenToggled_UpdatesEveryModel()
    {
        var providerId = await SeedProviderAsync("a", "b");
        var service = ServiceWith("""{"data":[]}""");

        await service.SetAllEnabledAsync(providerId, false);

        using var db = _db.CreateDbContext();
        var models = await db.Models.Where(m => m.ProviderId == providerId).ToListAsync();
        Assert.Equal(2, models.Count);
        Assert.All(models, m => Assert.False(m.Enabled));
    }

    [Fact]
    public async Task SetEnabled_WhenToggled_PersistsSingleModel()
    {
        var providerId = await SeedProviderAsync("a");
        var service = ServiceWith("""{"data":[]}""");

        using (var db = _db.CreateDbContext())
        {
            var model = await db.Models.SingleAsync(m => m.ModelId == "a");
            await service.SetEnabledAsync(model.Id, false);
        }

        using var db2 = _db.CreateDbContext();
        Assert.False((await db2.Models.SingleAsync(m => m.ModelId == "a")).Enabled);
    }
}
