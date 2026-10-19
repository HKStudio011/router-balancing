using System.Net;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

/// <summary>
/// Sync preset OpenCode Free qua catalog v2 (models.opencode.ai/api.json): fetch đúng catalog URL,
/// parse nhánh <see cref="FreeDetectKind.OpenCodeCatalog"/> và merge capabilities vào Model
/// (spec opencode-tools-page §6).
/// </summary>
public class FreeModelSyncCatalogTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public FreeModelSyncCatalogTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db); // 4 preset — test dùng "OpenCode Free" (DetectKind OpenCodeCatalog)
    }

    public void Dispose() => _testDb.Dispose();

    private FreeModelSyncService ServiceWith(string json) =>
        new(_db, _protector, new StubFactory(new JsonHandler(json)), new NullLog());

    private async Task<long> OpenCodeIdAsync()
    {
        using var db = _db.CreateDbContext();
        return (await db.Providers.SingleAsync(p => p.Name == "OpenCode Free")).Id;
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
    public async Task SyncProviderAsync_OpenCodeCatalog_FetchesCatalogUrlAndWritesCapabilities()
    {
        var id = await OpenCodeIdAsync();
        var handler = new JsonHandler(FreeModelFixtures.OpenCodeCatalog);
        var service = new FreeModelSyncService(_db, _protector, new StubFactory(handler), new NullLog());

        var count = await service.SyncProviderAsync(id);

        Assert.Equal(3, count);
        Assert.Equal("https://models.opencode.ai/api.json", handler.LastRequestUri!.ToString());
        using var db = _db.CreateDbContext();
        var big = await db.Models.AsNoTracking().SingleAsync(m => m.ProviderId == id && m.ModelId == "big-pickle");
        Assert.True(big.Enabled);
        Assert.False(big.IsManual);
        Assert.Equal(200000, big.ContextWindow);
        Assert.Equal(32000, big.MaxOutputTokens);
        Assert.True(big.SupportsTools);
        Assert.True(big.SupportsThink);
        Assert.False(big.SupportsVision);
    }

    [Fact]
    public async Task SyncProviderAsync_ModelLeavesCatalog_DeletesNonManualKeepsManual()
    {
        var id = await OpenCodeIdAsync();
        await AddModelAsync(id, "jev-1.13-free", isManual: false);   // model legacy, không còn trong catalog (U5)
        await AddModelAsync(id, "my-manual-model", isManual: true);
        var service = ServiceWith(FreeModelFixtures.OpenCodeCatalog);

        await service.SyncProviderAsync(id);

        using var db = _db.CreateDbContext();
        var ids = await db.Models.AsNoTracking().Where(m => m.ProviderId == id).Select(m => m.ModelId).ToListAsync();
        Assert.DoesNotContain("jev-1.13-free", ids);
        Assert.Contains("my-manual-model", ids);
        Assert.Contains("big-pickle", ids);
    }

    [Fact]
    public async Task SyncProviderAsync_CatalogFreeSetEmpty_ThrowsAndKeepsExisting()
    {
        var id = await OpenCodeIdAsync();
        await AddModelAsync(id, "old-free", isManual: false,
            displayName: "Old");                                     // có sẵn 1 model cũ
        var service = ServiceWith(FreeModelFixtures.OpenCodeCatalogAllPaid);  // fixture mới: chỉ model paid
        await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(id));

        using var db = _db.CreateDbContext();
        Assert.Contains(await db.Models.AsNoTracking().ToListAsync(),
            m => m.ProviderId == id && m.ModelId == "old-free");     // không bị xoá
    }

    [Fact]
    public async Task SyncProviderAsync_CatalogWithUndecryptableProviderKey_SkipsKeyResolutionAndSyncs()
    {
        // M1: ciphertext copy từ máy/user khác → Unprotect ném CryptographicException. Nhánh catalog
        // là raw GET không auth nên không được resolve key, nếu không sync OpenCode Free gãy mỗi
        // chu kỳ dù catalog không cần key.
        var id = await OpenCodeIdAsync();
        using (var db = _db.CreateDbContext())
        {
            db.ProviderAccounts.Add(new ProviderAccount
            {
                ProviderId = id,
                Name = "Default",
                ApiKeyEncrypted = "foreign-ciphertext-not-decryptable",
                Enabled = true,
            });
            await db.SaveChangesAsync();
        }
        var handler = new JsonHandler(FreeModelFixtures.OpenCodeCatalog);
        var service = new FreeModelSyncService(
            _db, new UndecryptableKeyProtector(), new StubFactory(handler), new NullLog());

        var count = await service.SyncProviderAsync(id);

        Assert.Equal(3, count);
        Assert.Equal("https://models.opencode.ai/api.json", handler.LastRequestUri!.ToString());
        using var db2 = _db.CreateDbContext();
        Assert.Contains(await db2.Models.AsNoTracking().ToListAsync(),
            m => m.ProviderId == id && m.ModelId == "big-pickle");
    }

    // ===== HTTP doubles (pattern FreeModelSyncMergeTests: JsonHandler/StubFactory) =====

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }

    /// <summary>
    /// Giả lập ciphertext DPAPI copy từ máy/user khác — Unprotect ném CryptographicException
    /// đúng như <see cref="DpapiSecretProtector"/> khi blob không thuộc CurrentUser.
    /// </summary>
    private sealed class UndecryptableKeyProtector : ISecretProtector
    {
        public string Protect(string plaintext) => throw new NotSupportedException();

        public string Unprotect(string protectedValue) =>
            throw new CryptographicException("Key was protected for a different user or machine.");
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
