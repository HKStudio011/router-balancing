using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class FreeModelSyncServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public FreeModelSyncServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db); // seed 4 preset — test dùng chính hàng seed
    }

    public void Dispose() => _testDb.Dispose();

    private FreeModelSyncService ServiceWith(HttpMessageHandler handler) =>
        new(_db, _protector, new StubFactory(handler), new NullLog());

    private FreeModelSyncService ServiceWithJson(string json) => ServiceWith(new JsonHandler(json));

    private async Task<long> PresetIdAsync(string name)
    {
        using var db = _db.CreateDbContext();
        return (await db.Providers.SingleAsync(p => p.Name == name)).Id;
    }

    [Fact]
    public async Task SyncProviderAsync_UnknownProvider_ThrowsNotFound()
    {
        var service = ServiceWithJson(FreeModelFixtures.OpenRouter);

        var ex = await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(999_999));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task SyncProviderAsync_NonPreset_ThrowsNotPreset()
    {
        long id;
        using (var db = _db.CreateDbContext())
        {
            var provider = db.Providers.Add(new Provider
            {
                Name = "My Provider",
                BaseUrl = "https://api.example.com",
                Type = ProviderType.OpenAI,
                IsPreset = false,
            }).Entity;
            await db.SaveChangesAsync();
            id = provider.Id;
        }
        var service = ServiceWithJson(FreeModelFixtures.OpenRouter);

        var ex = await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(id));

        Assert.Contains("not a free preset", ex.Message);
    }

    [Fact]
    public async Task SyncProviderAsync_RenamedPreset_ThrowsNotInCatalog()
    {
        // Chốt S1: đổi tên preset → mất link sync, lỗi rõ cho user (manual)
        long id;
        using (var db = _db.CreateDbContext())
        {
            var preset = await db.Providers.SingleAsync(p => p.Name == "OpenRouter Free");
            preset.Name = "My Router";
            await db.SaveChangesAsync();
            id = preset.Id;
        }
        var service = ServiceWithJson(FreeModelFixtures.OpenRouter);

        var ex = await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(id));

        Assert.Contains("free catalog", ex.Message);
    }

    [Fact]
    public async Task SyncProviderAsync_HttpError_ThrowsAndKeepsModels()
    {
        var id = await PresetIdAsync("OpenCode Free");
        using (var db = _db.CreateDbContext())
        {
            db.Models.Add(new Model { ProviderId = id, ModelId = "keep-me", IsManual = false });
            await db.SaveChangesAsync();
        }
        var service = ServiceWith(new StatusHandler(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(id));

        Assert.Contains("HTTP 500", ex.Message);
        using var db2 = _db.CreateDbContext();
        // Không đụng DB khi fetch lỗi (§6.2.3)
        Assert.Equal("keep-me", Assert.Single(
            await db2.Models.AsNoTracking().Where(m => m.ProviderId == id).ToListAsync()).ModelId);
        var preset = await db2.Providers.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Null(preset.LastModelSyncAt);
    }

    [Fact]
    public async Task SyncProviderAsync_MalformedJson_Throws()
    {
        var id = await PresetIdAsync("OpenRouter Free");
        var service = ServiceWithJson("{not json");

        var ex = await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(id));

        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public async Task SyncAllEnabledAsync_FailingPreset_SkippedAndOthersSynced()
    {
        // Bật 2 preset: OpenCode (handler sẽ 500) + NVIDIA (200)
        using (var db = _db.CreateDbContext())
        {
            foreach (var preset in db.Providers.Where(p =>
                p.IsPreset && (p.Name == "OpenCode Free" || p.Name == "NVIDIA NIM Free")))
            {
                preset.Enabled = true;
            }
            await db.SaveChangesAsync();
        }
        var nvidiaId = await PresetIdAsync("NVIDIA NIM Free");
        var handler = new RoutingHandler(failUrlPart: "opencode.ai", okJson: FreeModelFixtures.Nvidia);
        var service = ServiceWith(handler);

        var results = await service.SyncAllEnabledAsync();

        var only = Assert.Single(results); // OpenCode bị skip, không throw
        Assert.Equal(nvidiaId, only.ProviderId);
        Assert.Equal(2, only.ModelCount);
        Assert.Equal(2, handler.Calls); // cả 2 preset enabled đều được gọi: 1 fail (skip) + 1 OK
    }

    [Fact]
    public async Task SyncAllEnabledAsync_AllPresetsDisabled_MakesNoHttpCall()
    {
        var handler = new SpyHandler();
        var service = ServiceWith(handler);

        var results = await service.SyncAllEnabledAsync();

        Assert.Empty(results);
        Assert.Equal(0, handler.Calls);
    }

    // ===== HTTP doubles (pattern ModelServiceTests: JsonHandler/StubFactory) =====

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    private sealed class RoutingHandler(string failUrlPart, string okJson) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var fail = request.RequestUri is { } uri
                && uri.ToString().Contains(failUrlPart, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(
                fail ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
            {
                Content = new StringContent(fail ? "{}" : okJson),
            });
        }
    }

    private sealed class SpyHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}"""),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
