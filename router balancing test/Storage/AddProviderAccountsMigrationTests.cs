// router balancing test/Storage/AddProviderAccountsMigrationTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

/// <summary>
/// Round-trip data migration AddProviderAccounts: key cũ trên Providers → account "Default" (Up)
/// và ngược lại (Down). Phải migrate tới đúng migration rồi mới seed — Migrate() mặc định trên DB
/// trống để data SQL chạy trên empty set, không chứng minh gì.
/// </summary>
public class AddProviderAccountsMigrationTests : IDisposable
{
    private const string PreviousId = "20260925172150_InitialCreate";
    private const string TargetId = "20260927122326_AddProviderAccounts";
    private const string Timestamp = "'2026-09-27T00:00:00+00:00'";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void Up_MigratesProviderKey_ToDefaultAccount()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(PreviousId);
        db.Database.ExecuteSqlRaw($"""
            INSERT INTO Providers (Name, Type, BaseUrl, ApiKeyEncrypted, Enabled, MaxConcurrent, CreatedAt, UpdatedAt)
            VALUES ('P', 0, 'https://api.example.com', 'enc-old', 1, 4, {Timestamp}, {Timestamp});
            """);

        db.GetService<IMigrator>().Migrate(TargetId);

        var account = db.ProviderAccounts.Single(a => a.ProviderId == 1);
        Assert.Equal("Default", account.Name);
        Assert.Equal("enc-old", account.ApiKeyEncrypted);
        // Cột cũ đã drop — SQLite không cho EF modelche, phải kiểm tra schema thật
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('Providers') WHERE name = 'ApiKeyEncrypted'")));
    }

    [Fact]
    public void Down_RestoresDefaultAccountKey_ToProviderColumn()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(TargetId);
        // Đúng schema tại TargetId: Providers không còn ApiKeyEncrypted, ProviderAccounts NOT NULL các cột dùng default
        db.Database.ExecuteSqlRaw($"""
            INSERT INTO Providers (Name, Type, BaseUrl, Enabled, MaxConcurrent, CreatedAt, UpdatedAt)
            VALUES ('P', 0, 'https://api.example.com', 1, 4, {Timestamp}, {Timestamp});
            """);
        db.Database.ExecuteSqlRaw($"""
            INSERT INTO ProviderAccounts (ProviderId, Name, ApiKeyEncrypted, Enabled, Weight, Priority,
                                          TokensUsed, RequestsUsed, CreatedAt, UpdatedAt)
            VALUES (1, 'Default', 'enc-back', 1, 100, 0, 0, 0, {Timestamp}, {Timestamp});
            """);

        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal("enc-back", Scalar("SELECT ApiKeyEncrypted FROM Providers WHERE Id = 1"));
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('ProviderAccounts')")));
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
