using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

public class DbInitializerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Initialize_OnEmptyDatabase_CreatesAllTables()
    {
        var factory = _db.CreateFactory();

        DbInitializer.Initialize(factory);

        using var db = factory.CreateDbContext();
        var tables = db.Database
            .SqlQueryRaw<string>("SELECT name FROM sqlite_master WHERE type='table'")
            .ToList();
        Assert.Contains("Providers", tables);
        Assert.Contains("ProviderAccounts", tables);
        Assert.Contains("Models", tables);
        Assert.Contains("Combos", tables);
        Assert.Contains("ComboItems", tables);
        Assert.Contains("LogEntries", tables);
        Assert.Contains("AppSettings", tables);
    }

    [Fact]
    public void Initialize_RunTwice_IsIdempotent()
    {
        var factory = _db.CreateFactory();

        DbInitializer.Initialize(factory);
        DbInitializer.Initialize(factory);

        using var db = factory.CreateDbContext();
        var applied = db.Database
            .SqlQueryRaw<string>("SELECT MigrationId FROM __EFMigrationsHistory")
            .ToList();
        // Idempotent = mỗi migration định nghĩa apply đúng 1 lần sau 2 lần Initialize —
        // không hardcode số migration (thêm migration mới không được phá test này).
        var defined = db.Database.GetMigrations().ToList();
        Assert.Equal(defined.Count, applied.Count);
        Assert.Equal(
            defined.OrderBy(m => m, StringComparer.Ordinal),
            applied.OrderBy(m => m, StringComparer.Ordinal));
    }

    [Fact]
    public void Initialize_WhenComboItemsWritten_BothNavigationAndForeignKeyLoad()
    {
        var factory = _db.CreateFactory();
        DbInitializer.Initialize(factory);

        long comboId;
        using (var db = factory.CreateDbContext())
        {
            var combo = new Combo
            {
                Name = "combo-roundtrip",
                Items = [new ComboItem { Position = 0 }],
            };
            db.Combos.Add(combo);
            db.SaveChanges();
            comboId = combo.Id;
        }

        using (var db = factory.CreateDbContext())
        {
            var reloaded = db.Combos.Include(c => c.Items).Single(c => c.Id == comboId);
            Assert.Single(reloaded.Items);

            var byForeignKey = db.ComboItems.Single(i => i.ComboId == comboId);
            Assert.Equal(reloaded.Items[0].Id, byForeignKey.Id);
        }
    }
}
