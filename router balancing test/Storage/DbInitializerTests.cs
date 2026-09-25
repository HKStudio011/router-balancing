using Microsoft.EntityFrameworkCore;
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
        var migrations = db.Database
            .SqlQueryRaw<string>("SELECT MigrationId FROM __EFMigrationsHistory")
            .ToList();
        Assert.Single(migrations);
    }
}
