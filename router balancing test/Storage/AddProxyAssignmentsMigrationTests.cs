// router balancing test/Storage/AddProxyAssignmentsMigrationTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

/// <summary>
/// Round-trip schema AddProxyAssignments: Up thêm 2 junction table (ProviderProxies,
/// ProviderAccountProxies) + 2 cột ProxyMode trên Providers/ProviderAccounts; Down xóa.
/// Pattern AddOutboundProxyMigrationTests — migrate tới đúng migration rồi mới seed.
/// </summary>
public class AddProxyAssignmentsMigrationTests : IDisposable
{
    private const string PreviousId = "20261002005636_AddOutboundProxies";
    // Migration thực tế sinh ra bởi dotnet ef (Task 1 Step 6).
    private const string TargetId = "20261002110334_AddProxyAssignments";
    private const string Timestamp = "'2026-10-02T00:00:00+00:00'";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void Up_CreatesJunctionTablesAndModeColumns()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProviderProxies'")));
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProviderAccountProxies'")));

        db.GetService<IMigrator>().Migrate(TargetId);

        // 2 junction table xuất hiện
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProviderProxies'")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProviderAccountProxies'")));

        // Cột ProxyMode xuất hiện trên Providers + ProviderAccounts
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('Providers') WHERE name='ProxyMode'")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('ProviderAccounts') WHERE name='ProxyMode'")));
    }

    [Fact]
    public void Down_DropsJunctionTablesAndModeColumns()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(TargetId);

        // Seed cha để FK không bị vi phạm khi insert junction (FK enforce bởi SQLite).
        // $""" : C# interpolate {Timestamp} — không phải EF parameter (pattern AddOutboundProxyMigrationTests).
        db.Database.ExecuteSqlRaw($"""
            INSERT INTO Providers (Id, Name, Type, BaseUrl, Enabled, MaxConcurrent, CreatedAt, UpdatedAt)
            VALUES (1, 'P1', 0, 'https://a.com', 1, 4, {Timestamp}, {Timestamp});
            INSERT INTO ProviderAccounts (Id, ProviderId, Name, ApiKeyEncrypted, Enabled, Weight, Priority,
                                          TokensUsed, RequestsUsed, CreatedAt, UpdatedAt)
            VALUES (3, 1, 'A1', 'enc', 1, 100, 0, 0, 0, {Timestamp}, {Timestamp});
            INSERT INTO OutboundProxies (Id, Scheme, Host, Port, Enabled, CreatedAt, UpdatedAt)
            VALUES (2, 'http', '127.0.0.1', 8080, 1, {Timestamp}, {Timestamp});
            """);

        db.Database.ExecuteSqlRaw($"""
            INSERT INTO ProviderProxies (ProviderId, ProxyId) VALUES (1, 2);
            INSERT INTO ProviderAccountProxies (AccountId, ProxyId) VALUES (3, 2);
            """);

        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProviderProxies'")));
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProviderAccountProxies'")));
    }

    /// <summary>Scalar qua ADO thuần — tránh alias "Value" của EF SqlQuery và trần thuật ngữ EF.</summary>
    private object? Scalar(string sql)
    {
        // Pooling=False như TestDb: pool giữ handle file → Dispose không xóa được (IOException trên Windows)
        using var conn = new SqliteConnection($"Data Source={_testDb.DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
