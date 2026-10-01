using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

public class DbInitializerFreeProviderTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Initialize_FreshDb_SeedsFourPresetsDisabledWithNoModels()
    {
        var factory = _db.CreateFactory();

        DbInitializer.Initialize(factory);

        using var db = factory.CreateDbContext();
        var presets = db.Providers.AsNoTracking().Where(p => p.IsPreset).ToList();
        Assert.Equal(4, presets.Count);
        Assert.Equal(
            FreeProviderCatalog.Entries.Select(e => e.DisplayName).OrderBy(x => x, StringComparer.Ordinal),
            presets.Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(presets, p =>
        {
            Assert.False(p.Enabled); // D9: user tự bật
            Assert.Equal(ProviderType.OpenAI, p.Type);
            Assert.False(string.IsNullOrEmpty(p.Identifier)); // seed trước Backfill → slugify ngay
            Assert.Empty(p.Models); // chờ sync đầu tiên
            Assert.Null(p.LastModelSyncAt);
        });
    }

    [Fact]
    public void Initialize_ExistingUserProviders_KeepsThemAndSeedsPresets()
    {
        var factory = _db.CreateFactory();
        // Upgrade path: DB cũ đã có provider user, chưa có preset nào
        using (var db = factory.CreateDbContext())
        {
            db.Database.Migrate();
            db.Providers.Add(new Provider
            {
                Name = "My OpenAI",
                BaseUrl = "https://api.openai.com",
                Type = ProviderType.OpenAI,
            });
            db.SaveChanges();
        }

        DbInitializer.Initialize(factory);

        using var db2 = factory.CreateDbContext();
        Assert.Equal(4, db2.Providers.AsNoTracking().Count(p => p.IsPreset));
        var userRow = Assert.Single(db2.Providers.AsNoTracking(), p => p.Name == "My OpenAI");
        Assert.False(userRow.IsPreset); // hàng user không bị đánh dấu preset
    }

    [Fact]
    public void Initialize_RenamedPreset_NotReSeededNoDuplicate()
    {
        var factory = _db.CreateFactory();
        DbInitializer.Initialize(factory);
        using (var db = factory.CreateDbContext())
        {
            var preset = db.Providers.Single(p => p.Name == "OpenRouter Free");
            preset.Name = "My Router";
            db.SaveChanges();
        }

        DbInitializer.Initialize(factory); // lần 2 — guard Any(IsPreset) phải skip

        using var db2 = factory.CreateDbContext();
        Assert.Equal(4, db2.Providers.AsNoTracking().Count(p => p.IsPreset));
        Assert.Contains(db2.Providers.AsNoTracking(), p => p.Name == "My Router");
        Assert.DoesNotContain(db2.Providers.AsNoTracking(), p => p.Name == "OpenRouter Free");
    }
}
