using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

/// <summary>
/// Round-trip schema AddStatsJournalColumns: Up thêm 4 cột nullable (Outcome, ProviderKey,
/// ModelKey, HttpStatus) trên LogEntries — dòng cũ giữ nguyên, Outcome = NULL,
/// Down xóa. Pattern AddProxyAssignmentsMigrationTests — migrate tới đúng migration rồi seed.
/// </summary>
public class AddStatsJournalColumnsMigrationTests : IDisposable
{
    private const string PreviousId = "20261004172008_RemoveAccountPriority";
    private const string TargetId = "20261010150737_AddStatsJournalColumns";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void Up_AddsFourNullableStatsColumns_OldRowsStayNull()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('LogEntries') "
            + "WHERE name IN ('Outcome','ProviderKey','ModelKey','HttpStatus')")));

        // Dòng "cũ" chỉ có các cột trước migration — mô phỏng journal đã ghi trước khi upgrade
        db.Database.ExecuteSqlRaw(
            "INSERT INTO LogEntries (Timestamp, Severity, Category, Message) VALUES (0, 0, 1, 'old row')");

        db.GetService<IMigrator>().Migrate(TargetId);

        // 4 cột xuất hiện, tất cả nullable (notnull=0) — SQLite không có default
        // notnull (reserved word SQLite ≥3.37) phải quote — pattern "unique" của AddOutboundProxyMigrationTests
        Assert.Equal(4L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('LogEntries') "
            + "WHERE name IN ('Outcome','ProviderKey','ModelKey','HttpStatus') AND \"notnull\"=0")));

        // CLR type nullable — EF model chứ không chỉ schema SQLite
        var logEntry = db.Model.FindEntityType(typeof(LogEntry))!;
        Assert.True(logEntry.FindProperty(nameof(LogEntry.Outcome))!.IsNullable);
        Assert.True(logEntry.FindProperty(nameof(LogEntry.ProviderKey))!.IsNullable);
        Assert.True(logEntry.FindProperty(nameof(LogEntry.ModelKey))!.IsNullable);
        Assert.True(logEntry.FindProperty(nameof(LogEntry.HttpStatus))!.IsNullable);

        // Dòng cũ sau upgrade: Outcome = NULL (= không phải terminal)
        // ExecuteScalar trả DBNull.Value chứ không phải null khi cột NULL
        Assert.Equal(DBNull.Value, Scalar("SELECT Outcome FROM LogEntries WHERE Id = 1"));
    }

    [Fact]
    public void Down_DropsFourStatsColumns()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(TargetId);
        db.Database.ExecuteSqlRaw(
            "INSERT INTO LogEntries (Timestamp, Severity, Category, Message, Outcome, ProviderKey, ModelKey, HttpStatus) "
            + "VALUES (0, 0, 1, 'row', 1, 'openai', 'gpt-5', 200)");

        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('LogEntries') "
            + "WHERE name IN ('Outcome','ProviderKey','ModelKey','HttpStatus')")));
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
