using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

/// <summary>
/// Round-trip schema AddModelAudioVideoCapabilities: Up thêm 2 cột NOT NULL DEFAULT 0
/// (SupportsAudio, SupportsVideo) trên Models — dòng cũ nhận default 0 (= chưa rõ,
/// cùng convention SupportsVision), Down xóa. Pattern AddAccountProxyOverrideMigrationTests
/// — migrate tới đúng migration rồi seed.
/// </summary>
public class AddModelAudioVideoCapabilitiesMigrationTests : IDisposable
{
    private const string PreviousId = "20261013125137_AddProviderExtraHeaders";
    private const string TargetId = "20261014061037_AddModelAudioVideoCapabilities";
    private const string Timestamp = "'2026-10-01T00:00:00+00:00'";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void Up_AddsColumnsWithDefault()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('Models') "
            + "WHERE name IN ('SupportsAudio','SupportsVideo')")));

        // Seed "cũ" — 1 provider + 1 model chưa có 2 cột; cha trước con để FK không vi phạm.
        // $""" : C# interpolate {Timestamp} — không phải EF parameter (pattern AddAccountProxyOverrideMigrationTests).
        db.Database.ExecuteSqlRaw($"""
            INSERT INTO Providers (Id, Name, Type, BaseUrl, Enabled, MaxConcurrent, CreatedAt, UpdatedAt)
            VALUES (1, 'P1', 0, 'https://p1.test', 1, 4, {Timestamp}, {Timestamp});
            INSERT INTO Models (Id, ProviderId, ModelId, Enabled, IsManual, SupportsVision, SupportsThink, CreatedAt)
            VALUES (1, 1, 'm1', 1, 0, 0, 0, {Timestamp});
            """);

        db.GetService<IMigrator>().Migrate(TargetId);

        // 2 cột INTEGER NOT NULL DEFAULT 0 (AddColumn<bool>(nullable: false, defaultValue: false)
        // trên SQLite) — "notnull" (reserved word SQLite ≥3.37) phải quote
        Assert.Equal(2L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('Models') "
            + "WHERE name IN ('SupportsAudio','SupportsVideo') "
            + "AND type='INTEGER' AND \"notnull\"=1 AND \"dflt_value\"='0'")));

        // Dòng cũ sau upgrade: cả 2 cột nhận default 0
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM Models WHERE Id = 1 AND SupportsAudio = 0 AND SupportsVideo = 0")));
    }

    [Fact]
    public void Down_DropsColumns()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(TargetId);

        Assert.Equal(2L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('Models') "
            + "WHERE name IN ('SupportsAudio','SupportsVideo')")));

        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('Models') "
            + "WHERE name IN ('SupportsAudio','SupportsVideo')")));
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
