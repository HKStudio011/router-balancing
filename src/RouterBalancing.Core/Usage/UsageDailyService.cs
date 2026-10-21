using System.Globalization;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Usage;

/// <inheritdoc cref="IUsageDailyService"/>
public sealed class UsageDailyService(
    IDbContextFactory<RouterBalancingDbContext> db,
    TimeProvider time) : IUsageDailyService
{
    public async Task RecordAsync(UsageSubjectKind kind, long subjectId, long requests, long tokens,
        CancellationToken ct = default)
    {
        // Không ghi dòng 0/0 — tránh rác từ các call site chỉ có một loại số liệu.
        if (requests == 0 && tokens == 0) return;

        var kindText = UsageSubjectKinds.ToDb(kind);
        var dateText = TodayUtc().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await using var context = await db.CreateDbContextAsync(ct);

        // INSERT ... SELECT từ bảng chủ thể: chủ thể bị xóa giữa chừng thì SELECT không
        // trả dòng nào → không tạo usage mồ côi. ON CONFLICT cộng dồn atomic, tránh
        // read-modify-write của counter cũ khi nhiều request hoàn tất cùng lúc.
        // WHERE Id = ... cũng tránh ambiguity parser của SQLite cho INSERT...SELECT+UPSERT.
        if (kind == UsageSubjectKind.ClientKey)
        {
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO UsageDaily (SubjectKind, SubjectId, Date, Requests, Tokens)
                SELECT {kindText}, Id, {dateText}, {requests}, {tokens}
                FROM ClientKeys
                WHERE Id = {subjectId}
                ON CONFLICT (SubjectKind, SubjectId, Date)
                DO UPDATE SET
                    Requests = Requests + excluded.Requests,
                    Tokens = Tokens + excluded.Tokens;
                """, ct);
            return;
        }

        if (kind == UsageSubjectKind.ProviderAccount)
        {
            await context.Database.ExecuteSqlAsync($"""
                INSERT INTO UsageDaily (SubjectKind, SubjectId, Date, Requests, Tokens)
                SELECT {kindText}, Id, {dateText}, {requests}, {tokens}
                FROM ProviderAccounts
                WHERE Id = {subjectId}
                ON CONFLICT (SubjectKind, SubjectId, Date)
                DO UPDATE SET
                    Requests = Requests + excluded.Requests,
                    Tokens = Tokens + excluded.Tokens;
                """, ct);
            return;
        }

        throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown usage subject kind.");
    }

    public async Task<IReadOnlyDictionary<long, UsageTotals>> GetTodayTotalsAsync(
        UsageSubjectKind kind, IReadOnlyCollection<long> subjectIds, CancellationToken ct = default)
    {
        if (subjectIds.Count == 0)
            return new Dictionary<long, UsageTotals>();

        var today = TodayUtc();
        await using var context = await db.CreateDbContextAsync(ct);
        var rows = await context.UsageDaily
            .AsNoTracking()
            .Where(u => u.SubjectKind == kind && u.Date == today && subjectIds.Contains(u.SubjectId))
            .Select(u => new { u.SubjectId, u.Requests, u.Tokens })
            .ToListAsync(ct);

        return rows.ToDictionary(
            row => row.SubjectId,
            row => new UsageTotals(row.Requests, row.Tokens));
    }

    private DateOnly TodayUtc() => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
}
