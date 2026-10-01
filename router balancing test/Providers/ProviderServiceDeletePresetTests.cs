using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ProviderServiceDeletePresetTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ProviderService _service;

    public ProviderServiceDeletePresetTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        _service = new ProviderService(_db, new DpapiSecretProtector(), new NeverHttpFactory(), new NullLog());
    }

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public async Task DeleteAsync_PresetProvider_ThrowsAndKeepsRow()
    {
        var id = await PresetIdAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(id));

        using var db = _db.CreateDbContext();
        Assert.True(await db.Providers.AnyAsync(p => p.Id == id));
        Assert.True(await db.Providers.AnyAsync(p => p.Id == id && p.IsPreset));
    }

    [Fact]
    public async Task DeleteAsync_NormalProvider_RemovesRow()
    {
        long id;
        using (var db = _db.CreateDbContext())
        {
            var provider = db.Providers.Add(new Provider
            {
                Name = "Scratch",
                BaseUrl = "https://api.example.com",
                Type = ProviderType.OpenAI,
            }).Entity;
            await db.SaveChangesAsync();
            id = provider.Id;
        }

        await _service.DeleteAsync(id);

        using var db2 = _db.CreateDbContext();
        Assert.False(await db2.Providers.AnyAsync(p => p.Id == id));
    }

    private async Task<long> PresetIdAsync()
    {
        using var db = _db.CreateDbContext();
        // Seed là 4 preset (DbInitializer.SeedFreeProviders) — FirstAsync vì SingleAsync sẽ ném
        return (await db.Providers.FirstAsync(p => p.IsPreset)).Id;
    }

    /// <summary>CRUD không được gọi network — nếu có thì test fail loud.</summary>
    private sealed class NeverHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("CRUD must not perform HTTP calls.");
    }
}
