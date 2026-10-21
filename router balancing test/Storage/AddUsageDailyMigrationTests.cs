using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

/// <summary>
/// Round-trip migration AddUsageDaily (backlog C3): tạo sổ UsageDaily, backfill counter
/// ngày từ ClientKeys/ProviderAccounts rồi bỏ các cột counter. Pattern
/// <see cref="AddProviderHeadersMigrationTests"/> — seed ở migration trước để mô phỏng DB cũ.
/// </summary>
public class AddUsageDailyMigrationTests : IDisposable
{
    private const string PreviousId = "20261021031043_AddProviderHeaders";
    private const string TargetId = "20261021033526_AddUsageDaily";
    private const string Timestamp = "'2026-10-01T00:00:00+00:00'";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void Up_BackfillsCountersAndDropsColumns()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(PreviousId);

        Execute($$"""
            INSERT INTO Providers (Id, Name, Type, BaseUrl, Enabled, MaxConcurrent, IsPreset, CreatedAt, UpdatedAt)
            VALUES (1, 'P1', 0, 'https://p1.test', 1, 4, 0, {{Timestamp}}, {{Timestamp}});

            INSERT INTO ProviderAccounts
                (Id, ProviderId, Name, ApiKeyEncrypted, Enabled, Weight, TokensUsed, RequestsUsed,
                 UsageDate, ProxyOverrideEnabled, CreatedAt, UpdatedAt)
            VALUES
                (10, 1, 'A1', '', 1, 100, 300, 4, '2026-10-20', 0, {{Timestamp}}, {{Timestamp}}),
                (11, 1, 'A2', '', 1, 100, 0, 0, '2026-10-20', 0, {{Timestamp}}, {{Timestamp}}),
                (12, 1, 'A3', '', 1, 100, 50, 1, NULL, 0, {{Timestamp}}, {{Timestamp}});

            INSERT INTO ClientKeys
                (Id, Name, KeyHash, KeyMask, Enabled, RequestsUsed, TokensUsed, UsageDate,
                 CreatedAt, UpdatedAt)
            VALUES
                (20, 'K1', 'hash1', 'mask1', 1, 7, 70, '2026-10-20', {{Timestamp}}, {{Timestamp}}),
                (21, 'K2', 'hash2', 'mask2', 1, 0, 0, '2026-10-20', {{Timestamp}}, {{Timestamp}}),
                (22, 'K3', 'hash3', 'mask3', 1, 1, 1, 'bad', {{Timestamp}}, {{Timestamp}});
            """);

        db.GetService<IMigrator>().Migrate(TargetId);

        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'UsageDaily'")));

        // Client key: chỉ K1 có counter > 0 và ngày hợp lệ; K2 zero, K3 ngày hỏng.
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM UsageDaily WHERE SubjectKind = 'client_key'")));
        Assert.Equal(7L, Convert.ToInt64(Scalar(
            "SELECT Requests FROM UsageDaily WHERE SubjectKind = 'client_key' AND SubjectId = 20")));
        Assert.Equal(70L, Convert.ToInt64(Scalar(
            "SELECT Tokens FROM UsageDaily WHERE SubjectKind = 'client_key' AND SubjectId = 20")));
        Assert.Equal("2026-10-20", Scalar(
            "SELECT Date FROM UsageDaily WHERE SubjectKind = 'client_key' AND SubjectId = 20"));

        // Provider account: chỉ A1; A2 zero, A3 NULL.
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM UsageDaily WHERE SubjectKind = 'provider_account'")));
        Assert.Equal(4L, Convert.ToInt64(Scalar(
            "SELECT Requests FROM UsageDaily WHERE SubjectKind = 'provider_account' AND SubjectId = 10")));
        Assert.Equal(300L, Convert.ToInt64(Scalar(
            "SELECT Tokens FROM UsageDaily WHERE SubjectKind = 'provider_account' AND SubjectId = 10")));

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('ClientKeys') WHERE name IN ('RequestsUsed', 'TokensUsed', 'UsageDate')")));
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('ProviderAccounts') WHERE name IN ('RequestsUsed', 'TokensUsed', 'UsageDate')")));
    }

    [Fact]
    public void Down_DropsTableAndRestoresEmptyColumns()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(TargetId);

        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'UsageDaily'")));

        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'UsageDaily'")));
        Assert.Equal(3L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('ClientKeys') WHERE name IN ('RequestsUsed', 'TokensUsed', 'UsageDate')")));
        Assert.Equal(3L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('ProviderAccounts') WHERE name IN ('RequestsUsed', 'TokensUsed', 'UsageDate')")));
    }

    private void Execute(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_testDb.DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_testDb.DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
