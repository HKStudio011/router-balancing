using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

/// <summary>
/// Round-trip schema AddAccountProxyOverrideFlag: Up thêm cột ProxyOverrideEnabled
/// (NOT NULL DEFAULT 0) trên ProviderAccounts, backfill account có junction
/// ProviderAccountProxies → 1 và chèn thiếu hàng ProviderProxies (invariant ⊆);
/// Down xóa cột. Pattern AddStatsJournalColumnsMigrationTests — migrate tới
/// đúng migration rồi seed.
/// </summary>
public class AddAccountProxyOverrideMigrationTests : IDisposable
{
    private const string PreviousId = "20261010150737_AddStatsJournalColumns";
    private const string TargetId = "20261012132740_AddAccountProxyOverrideFlag";
    private const string Timestamp = "'2026-10-01T00:00:00+00:00'";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void Up_AddsFlagColumn_BackfillsOldSemantics()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('ProviderAccounts') "
            + "WHERE name='ProxyOverrideEnabled'")));

        // Seed "cũ" — A1@P1 có junction→proxyX, A2@P1 không junction,
        // A3@P2 có junction→proxyY nhưng KHÔNG có hàng ProviderProxies(P2,proxyY).
        // Seed cha trước junction để FK không bị vi phạm (FK enforce bởi SQLite).
        // $""" : C# interpolate {Timestamp} — không phải EF parameter (pattern AddProxyAssignmentsMigrationTests).
        db.Database.ExecuteSqlRaw($"""
            INSERT INTO Providers (Id, Name, Type, BaseUrl, Enabled, MaxConcurrent, CreatedAt, UpdatedAt)
            VALUES (1, 'P1', 0, 'https://p1.test', 1, 4, {Timestamp}, {Timestamp});
            INSERT INTO Providers (Id, Name, Type, BaseUrl, Enabled, MaxConcurrent, CreatedAt, UpdatedAt)
            VALUES (2, 'P2', 0, 'https://p2.test', 1, 4, {Timestamp}, {Timestamp});
            INSERT INTO OutboundProxies (Id, Scheme, Host, Port, Enabled, CreatedAt, UpdatedAt)
            VALUES (1, 'http', 'proxyx.test', 8080, 1, {Timestamp}, {Timestamp});
            INSERT INTO OutboundProxies (Id, Scheme, Host, Port, Enabled, CreatedAt, UpdatedAt)
            VALUES (2, 'http', 'proxyy.test', 8081, 1, {Timestamp}, {Timestamp});
            INSERT INTO ProviderAccounts (Id, ProviderId, Name, ApiKeyEncrypted, Enabled, Weight,
                                          TokensUsed, RequestsUsed, CreatedAt, UpdatedAt)
            VALUES (1, 1, 'A1', 'enc', 1, 100, 0, 0, {Timestamp}, {Timestamp});
            INSERT INTO ProviderAccounts (Id, ProviderId, Name, ApiKeyEncrypted, Enabled, Weight,
                                          TokensUsed, RequestsUsed, CreatedAt, UpdatedAt)
            VALUES (2, 1, 'A2', 'enc', 1, 100, 0, 0, {Timestamp}, {Timestamp});
            INSERT INTO ProviderAccounts (Id, ProviderId, Name, ApiKeyEncrypted, Enabled, Weight,
                                          TokensUsed, RequestsUsed, CreatedAt, UpdatedAt)
            VALUES (3, 2, 'A3', 'enc', 1, 100, 0, 0, {Timestamp}, {Timestamp});
            """);
        db.Database.ExecuteSqlRaw("""
            INSERT INTO ProviderProxies (ProviderId, ProxyId) VALUES (1, 1);
            INSERT INTO ProviderAccountProxies (AccountId, ProxyId) VALUES (1, 1);
            INSERT INTO ProviderAccountProxies (AccountId, ProxyId) VALUES (3, 2);
            """);

        db.GetService<IMigrator>().Migrate(TargetId);

        // Cột tồn tại, NOT NULL (AddColumn sinh INTEGER NOT NULL DEFAULT 0)
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('ProviderAccounts') "
            + "WHERE name='ProxyOverrideEnabled' AND \"notnull\"=1")));

        // Backfill: account có junction → 1, không junction → 0
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT ProxyOverrideEnabled FROM ProviderAccounts WHERE Id = 1")));
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT ProxyOverrideEnabled FROM ProviderAccounts WHERE Id = 2")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT ProxyOverrideEnabled FROM ProviderAccounts WHERE Id = 3")));

        // Invariant ⊆: (P2,proxyY) được chèn; (P1,proxyX) đã có không nhân đôi
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM ProviderProxies WHERE ProviderId = 2 AND ProxyId = 2")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM ProviderProxies WHERE ProviderId = 1 AND ProxyId = 1")));
    }

    [Fact]
    public void Down_DropsFlagColumn()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(TargetId);

        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('ProviderAccounts') "
            + "WHERE name='ProxyOverrideEnabled'")));

        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('ProviderAccounts') "
            + "WHERE name='ProxyOverrideEnabled'")));
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
