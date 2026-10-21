using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

/// <summary>
/// Round-trip migration AddProviderHeaders (backlog C5): tạo bảng, backfill từ JSON
/// <c>Providers.ExtraHeaders</c> rồi bỏ cột. Seed ở migration trước để mô phỏng DB cũ;
/// pattern <see cref="AddModelAudioVideoCapabilitiesMigrationTests"/>.
/// </summary>
public class AddProviderHeadersMigrationTests : IDisposable
{
    private const string PreviousId = "20261021023602_AddComboItemIntegrity";
    private const string TargetId = "20261021031043_AddProviderHeaders";
    private const string Timestamp = "'2026-10-01T00:00:00+00:00'";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void Up_BackfillsJsonAndDropsColumn()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(PreviousId);

        // 1) JSON hợp lệ, có key trùng hoa thường; 2) JSON hỏng; 3) NULL;
        // 4) JSON array (không phải object); 5) key rỗng + value không phải string.
        // Dùng ADO thuần: ExecuteSqlRaw gọi string.Format nên brace của JSON bị hiểu là placeholder.
        Execute($$"""
            INSERT INTO Providers (Id, Name, Type, BaseUrl, Enabled, MaxConcurrent, IsPreset, CreatedAt, UpdatedAt, ExtraHeaders)
            VALUES
                (1, 'P1', 0, 'https://p1.test', 1, 4, 0, {{Timestamp}}, {{Timestamp}},
                 '{"X-Custom":"a","x-custom":"dup","X-Other":"b"}'),
                (2, 'P2', 0, 'https://p2.test', 1, 4, 0, {{Timestamp}}, {{Timestamp}}, '{not json'),
                (3, 'P3', 0, 'https://p3.test', 1, 4, 0, {{Timestamp}}, {{Timestamp}}, NULL),
                (4, 'P4', 0, 'https://p4.test', 1, 4, 0, {{Timestamp}}, {{Timestamp}}, '["array"]'),
                (5, 'P5', 0, 'https://p5.test', 1, 4, 0, {{Timestamp}}, {{Timestamp}},
                 '{"  ":"blank","X-Num":1,"X-Ok":"ok"}');
            """);

        db.GetService<IMigrator>().Migrate(TargetId);

        // Cột JSON biến mất, bảng mới tồn tại.
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('Providers') WHERE name = 'ExtraHeaders'")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ProviderHeaders'")));

        // Provider 1: 2 dòng; key trùng case bị gộp (dòng đầu thắng).
        Assert.Equal(2L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM ProviderHeaders WHERE ProviderId = 1")));
        Assert.Equal("a", Scalar(
            "SELECT Value FROM ProviderHeaders WHERE ProviderId = 1 AND Name = 'x-custom'"));

        // JSON hỏng / NULL / array → không có dòng.
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM ProviderHeaders WHERE ProviderId IN (2, 3, 4)")));

        // Key rỗng và value number bị bỏ; chỉ còn X-Ok.
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM ProviderHeaders WHERE ProviderId = 5")));
        Assert.Equal("X-Ok", Scalar(
            "SELECT Name FROM ProviderHeaders WHERE ProviderId = 5"));
        Assert.Equal("ok", Scalar(
            "SELECT Value FROM ProviderHeaders WHERE ProviderId = 5"));
    }

    [Fact]
    public void Down_RecreatesColumnAndDropsTable()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(TargetId);

        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ProviderHeaders'")));

        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ProviderHeaders'")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('Providers') WHERE name = 'ExtraHeaders'")));
    }

    [Fact]
    public void DuplicateNameDifferentCase_IsRejected()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(TargetId);
        SeedProvider(10);

        Execute("INSERT INTO ProviderHeaders (ProviderId, Name, Value) VALUES (10, 'X-Custom', 'a');");

        // PK NOCASE: "x-custom" là cùng key với "X-Custom".
        Assert.Throws<SqliteException>(() =>
            Execute("INSERT INTO ProviderHeaders (ProviderId, Name, Value) VALUES (10, 'x-custom', 'b');"));
    }

    [Fact]
    public void DeleteProvider_CascadesHeaders()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(TargetId);
        SeedProvider(20);
        Execute("INSERT INTO ProviderHeaders (ProviderId, Name, Value) VALUES (20, 'X-Custom', 'a');");

        // Foreign Keys mặc định OFF với ADO thuần — bật để kiểm tra đúng FK ON DELETE CASCADE.
        using var conn = new SqliteConnection(
            $"Data Source={_testDb.DbPath};Pooling=False;Foreign Keys=True");
        conn.Open();

        using (var delete = conn.CreateCommand())
        {
            delete.CommandText = "DELETE FROM Providers WHERE Id = 20;";
            delete.ExecuteNonQuery();
        }

        using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM ProviderHeaders WHERE ProviderId = 20;";
        Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar()));
    }

    private void SeedProvider(long id) =>
        Execute($"""
            INSERT INTO Providers (Id, Name, Type, BaseUrl, Enabled, MaxConcurrent, IsPreset, CreatedAt, UpdatedAt)
            VALUES ({id}, 'P{id}', 0, 'https://p{id}.test', 1, 4, 0, {Timestamp}, {Timestamp});
            """);

    /// <summary>Chạy một câu SQL qua ADO thuần — tránh alias "Value" của EF SqlQuery.</summary>
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
