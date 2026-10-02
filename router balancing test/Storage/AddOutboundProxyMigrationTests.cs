// router balancing test/Storage/AddOutboundProxyMigrationTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

/// <summary>
/// Round-trip schema AddOutboundProxies: Up tạo bảng OutboundProxies (spec proxy-pool §3.1),
/// Down thả bảng. Pattern AddProviderAccountsMigrationTests — migrate tới đúng migration
/// rồi mới seed, không Migrate() mặc định trên DB trống.
/// </summary>
public class AddOutboundProxyMigrationTests : IDisposable
{
    private const string PreviousId = "20261001121706_AddFreeProviderPresets";
    // Điền sau khi chạy dotnet ef (Task 1 Step 3) — dạng <ts>_AddOutboundProxies
    private const string TargetId = "20261002005636_AddOutboundProxies";
    private const string Timestamp = "'2026-10-01T00:00:00+00:00'";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void Up_CreatesOutboundProxiesTable_AcceptingFullRow()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(PreviousId);
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='OutboundProxies'")));

        db.GetService<IMigrator>().Migrate(TargetId);

        // Đủ 13 cột theo §3.1 — insert raw SQL với mọi cột NOT NULL
        db.Database.ExecuteSqlRaw($"""
            INSERT INTO OutboundProxies (Scheme, Host, Port, Username, PasswordEncrypted, Enabled,
                                         LastTestSuccess, LastTestAt, LastTestMessage, LastTestIp,
                                         CreatedAt, UpdatedAt)
            VALUES ('http', '127.0.0.1', 8080, 'u', 'enc', 1, NULL, NULL, NULL, NULL, {Timestamp}, {Timestamp});
            """);
        var row = db.OutboundProxies.Single();
        Assert.Equal("http", row.Scheme);
        Assert.Equal("127.0.0.1", row.Host);
        Assert.Equal(8080, row.Port);
        Assert.True(row.Enabled);
        // Unique index 3 cột tồn tại (backstop Design decision 6) —
        // HasIndex().IsUnique() sinh CREATE UNIQUE INDEX → origin='c'
        // ('u' chỉ dành cho UNIQUE table constraint), "unique"=1 xác nhận unique
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_index_list('OutboundProxies') "
            + "WHERE name='IX_OutboundProxies_Scheme_Host_Port' AND origin='c' AND \"unique\"=1")));
    }

    [Fact]
    public void Down_DropsOutboundProxiesTable()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(TargetId);
        db.Database.ExecuteSqlRaw($"""
            INSERT INTO OutboundProxies (Scheme, Host, Port, Username, PasswordEncrypted, Enabled,
                                         LastTestSuccess, LastTestAt, LastTestMessage, LastTestIp,
                                         CreatedAt, UpdatedAt)
            VALUES ('http', 'h', 1, NULL, NULL, 1, NULL, NULL, NULL, NULL, {Timestamp}, {Timestamp});
            """);

        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='OutboundProxies'")));
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
