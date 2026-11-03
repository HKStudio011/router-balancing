using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Stats;

/// <inheritdoc cref="IStatsService"/>
/// <remarks>
/// Đọc journal trực tiếp mỗi lần query — 2 query (dòng terminal + dòng usage) rồi aggregate
/// LINQ-to-objects: volumes desktop ≤90 ngày, master spec §3 không rollup table.
/// </remarks>
public sealed class StatsService : IStatsService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly TimeProvider _clock;
    private readonly TimeZoneInfo _zone;

    /// <summary>Tạo service.</summary>
    /// <param name="db">Factory DB — nguồn journal (LogEntries).</param>
    /// <param name="clock">Đồng hồ lấy "now" — test truyền FixedTimeProvider.</param>
    /// <param name="zone">
    /// Múi giờ neo cửa sổ thống kê (UI dashboard hiển thị giờ máy). Mặc định
    /// <see cref="TimeZoneInfo.Utc"/> để call site cũ giữ nguyên nghĩa.
    /// </param>
    /// <exception cref="ArgumentNullException">Khi <paramref name="db"/> hoặc <paramref name="clock"/> là <see langword="null"/>.</exception>
    public StatsService(IDbContextFactory<RouterBalancingDbContext> db, TimeProvider clock,
        TimeZoneInfo? zone = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(clock);
        _db = db;
        _clock = clock;
        _zone = zone ?? TimeZoneInfo.Utc;
    }

    /// <inheritdoc cref="IStatsService.GetRangeAsync"/>
    public async Task<StatsSnapshot> GetRangeAsync(StatsRange range, CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var (from, bucketCount, bucketStep) = ResolveWindow(range, now);

        await using var db = await _db.CreateDbContextAsync(ct);
        var terminals = await db.LogEntries.AsNoTracking()
            .Where(e => e.Category == LogCategory.Request
                && e.Outcome != null
                && e.Timestamp >= from
                && e.Timestamp <= now)
            .Select(e => new TerminalRow(e.Outcome!.Value, e.DurationMs, e.ProviderKey,
                e.ModelKey, e.ClientKeyId, e.RequestId, e.Timestamp))
            .ToListAsync(ct);

        var usages = await db.LogEntries.AsNoTracking()
            .Where(e => e.Category == LogCategory.Request
                && e.PromptTokens != null
                && e.Timestamp >= from
                && e.Timestamp <= now)
            .Select(e => new UsageRow(e.RequestId, e.PromptTokens, e.CompletionTokens))
            .ToListAsync(ct);

        // Id → Name cho breakdown client key — journal chỉ lưu ClientKeyId, UI hiển thị tên
        var clientKeyNames = await db.ClientKeys.AsNoTracking()
            .Select(k => new { k.Id, k.Name })
            .ToDictionaryAsync(k => k.Id, k => k.Name, ct);

        var ok = terminals.Count(t => t.Outcome == RequestOutcome.Ok);
        var errors = terminals.Count(t => t.Outcome == RequestOutcome.Error);
        var cancelled = terminals.Count(t => t.Outcome == RequestOutcome.Cancelled);

        // Cancelled không vào denominator — hủy là chủ đích client, tính vào sẽ lệch
        // tiêu chí sức khỏe hệ thống (spec §5, Review Focus #3)
        var errorRate = ok + errors == 0 ? 0.0 : errors * 100.0 / (ok + errors);

        var durations = terminals
            .Where(t => t.Outcome == RequestOutcome.Ok && t.DurationMs is not null)
            .Select(t => t.DurationMs!.Value)
            .ToList();
        int? avgLatency = durations.Count == 0 ? null : (int)Math.Round(durations.Average());
        int? p95Latency = durations.Count == 0 ? null : NearestRankP95(durations);

        var tokensByRequest = IndexTokensByRequestId(usages);
        var buckets = BuildBuckets(terminals, tokensByRequest, from, bucketCount, bucketStep);
        var byProvider = BuildBreakdown(terminals, t => t.ProviderKey ?? "—", tokensByRequest);
        var byModel = BuildBreakdown(terminals, t => t.ModelKey ?? "—", tokensByRequest);
        var byClientKey = BuildBreakdown(terminals,
            t => t.ClientKeyId is not { } id ? "—" : clientKeyNames.GetValueOrDefault(id, id.ToString()),
            tokensByRequest);

        return new StatsSnapshot(
            from, now,
            terminals.Count, ok, errors, cancelled,
            errorRate, avgLatency, p95Latency,
            usages.Sum(u => (long)(u.PromptTokens ?? 0)),
            usages.Sum(u => (long)(u.CompletionTokens ?? 0)),
            buckets, byProvider, byModel, byClientKey);
    }

    /// <summary>
    /// Cửa sổ theo múi giờ inject (spec §5 + bug "biểu đồ lệch 7 giờ trên máy UTC+7"):
    /// Hôm nay = [nửa đêm của múi, now]; 7/30 ngày = [nửa đêm múi −6/−29 ngày, now].
    /// Bucket Hôm nay theo giờ, còn lại theo ngày — đầu bucket đều mang offset của múi.
    /// Mặc định <see cref="TimeZoneInfo.Utc"/> ⇒ cùng nghĩa với hành vi cũ.
    /// </summary>
    /// <param name="range">Khoảng preset.</param>
    /// <param name="now">Thời điểm hiện tại — đầu cửa sổ là nửa đêm của ngày THUỘC MÚI <c>_zone</c>.</param>
    /// <returns>(Đầu cửa sổ, số bucket, kích thước bucket).</returns>
    /// <exception cref="ArgumentOutOfRangeException">Khi <paramref name="range"/> không nằm trong enum.</exception>
    private (DateTimeOffset From, int BucketCount, TimeSpan BucketStep) ResolveWindow(
        StatsRange range, DateTimeOffset now)
    {
        // Why không đọc now.Offset: TimeProvider.System trả DateTimeOffset offset 0 dù máy ở
        // UTC+7 — phải quy đổi sang múi rồi mới lấy ngày, nếu không fix là no-op trên production.
        // .Date trên wall-clock đã quy đổi = nửa đêm 00:00 của ngày địa phương.
        var localDay = TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, _zone).Date;
        return range switch
        {
            StatsRange.Today => (StartOfDay(localDay), 24, TimeSpan.FromHours(1)),
            StatsRange.Last7Days => (StartOfDay(localDay.AddDays(-6)), 7, TimeSpan.FromDays(1)),
            StatsRange.Last30Days => (StartOfDay(localDay.AddDays(-29)), 30, TimeSpan.FromDays(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(range), range, "Unknown stats range"),
        };
    }

    /// <summary>
    /// Nửa đêm của ngày địa phương <paramref name="localMidnight"/> (wall-clock 00:00 của múi
    /// <c>_zone</c>) — offset do chính múi quyết định qua <c>GetUtcOffset</c> (Kind Unspecified =
    /// giờ địa phương của múi; DST do framework xử lý, không tự chế quy tắc riêng).
    /// </summary>
    private DateTimeOffset StartOfDay(DateTime localMidnight)
    {
        // ConvertTimeFromUtc trả Kind=Utc dù giá trị là wall-clock địa phương → phải SpecifyKind
        // để GetUtcOffset hiểu đây là giờ địa phương của múi, không phải một mốc UTC.
        var local = DateTime.SpecifyKind(localMidnight, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, _zone.GetUtcOffset(local));
    }

    /// <summary>
    /// p95 nearest-rank: sort TĂNG rồi lấy index <c>ceil(0.95n) − 1</c> — rank ⌈0.95n⌉
    /// tính từ giá trị nhỏ nhất (đúng định nghĩa nearest-rank; sort DESC lấy index từ
    /// đầu danh sách sẽ ra rank ngược, p05 thay vì p95).
    /// </summary>
    /// <param name="durations">DurationMs của dòng Ok (bị sửa đổi — sort tại chỗ).</param>
    /// <returns>Giá trị p95; caller đảm bảo danh sách không rỗng.</returns>
    private static int NearestRankP95(List<int> durations)
    {
        durations.Sort();
        return durations[(int)Math.Ceiling(0.95 * durations.Count) - 1];
    }

    /// <summary>
    /// Sinh đủ bucket rỗng rồi điền Requests/Errors theo Timestamp và token join theo
    /// RequestId của dòng terminal — usage không correlate được terminal chỉ vào tổng snapshot.
    /// </summary>
    /// <param name="terminals">Dòng terminal trong cửa sổ.</param>
    /// <param name="tokensByRequest">Index token theo RequestId (dùng chung với breakdown).</param>
    /// <param name="from">Đầu cửa sổ — cũng là BucketStart[0].</param>
    /// <param name="bucketCount">24 (giờ) hoặc 7/30 (ngày).</param>
    /// <param name="bucketStep">Kích thước bucket (1 giờ hoặc 1 ngày — số nguyên tuyệt đối, không đổi theo múi).</param>
    /// <returns>Danh sách bucket đầy đủ, thứ tự tăng dần theo thời gian.</returns>
    private static IReadOnlyList<StatsBucket> BuildBuckets(IReadOnlyList<TerminalRow> terminals,
        IReadOnlyDictionary<string, (long Prompt, long Completion)> tokensByRequest,
        DateTimeOffset from, int bucketCount, TimeSpan bucketStep)
    {
        var requests = new int[bucketCount];
        var errors = new int[bucketCount];
        var promptTokens = new long[bucketCount];
        var completionTokens = new long[bucketCount];
        foreach (var t in terminals)
        {
            // Chia theo khoảng từ đầu cửa sổ — Timestamp đã bị WHERE kẹp trong window nên
            // index luôn nằm trong [0, bucketCount); guard phòng trường hợp định nghĩa đổi
            var index = (int)((t.Timestamp - from).Ticks / bucketStep.Ticks);
            if (index < 0 || index >= bucketCount) continue;
            requests[index]++;
            if (t.Outcome == RequestOutcome.Error) errors[index]++;
            if (t.RequestId is not null && tokensByRequest.TryGetValue(t.RequestId, out var tks))
            {
                promptTokens[index] += tks.Prompt;
                completionTokens[index] += tks.Completion;
            }
        }

        var buckets = new StatsBucket[bucketCount];
        for (var i = 0; i < bucketCount; i++)
            buckets[i] = new StatsBucket(from + bucketStep * i, requests[i], errors[i],
                promptTokens[i], completionTokens[i]);
        return buckets;
    }

    /// <summary>
    /// Index token theo RequestId để join dòng usage ↔ dòng terminal — dòng usage nằm ngoài
    /// window hoặc không có terminal tương ứng vẫn vào tổng snapshot nhưng không vào breakdown.
    /// </summary>
    /// <param name="usages">Dòng usage trong cửa sổ.</param>
    /// <returns>RequestId → tổng (prompt, completion); nhiều usage cùng request được cộng dồn.</returns>
    private static IReadOnlyDictionary<string, (long Prompt, long Completion)> IndexTokensByRequestId(
        IReadOnlyList<UsageRow> usages)
    {
        var index = new Dictionary<string, (long Prompt, long Completion)>(StringComparer.Ordinal);
        foreach (var u in usages)
        {
            if (u.RequestId is null) continue; // usage không correlate được — chỉ tính tổng snapshot
            index.TryGetValue(u.RequestId, out var current);
            index[u.RequestId] = (current.Prompt + (u.PromptTokens ?? 0),
                current.Completion + (u.CompletionTokens ?? 0));
        }
        return index;
    }

    /// <summary>Group dòng terminal theo key rồi join token từ index theo RequestId (spec §3.4).</summary>
    /// <param name="terminals">Dòng terminal trong cửa sổ.</param>
    /// <param name="keyOf">Hàm chọn key nhóm (NULL → "—").</param>
    /// <param name="tokensByRequest">Index token theo RequestId.</param>
    /// <returns>Dòng breakdown sort Requests desc (tie-break theo key để thứ tự ổn định).</returns>
    private static IReadOnlyList<StatsBreakdownRow> BuildBreakdown(
        IReadOnlyList<TerminalRow> terminals, Func<TerminalRow, string> keyOf,
        IReadOnlyDictionary<string, (long Prompt, long Completion)> tokensByRequest)
    {
        return terminals
            .GroupBy(keyOf, StringComparer.Ordinal)
            .Select(g =>
            {
                var tokens = g.Sum(t =>
                    t.RequestId is not null && tokensByRequest.TryGetValue(t.RequestId, out var tks)
                        ? tks.Prompt + tks.Completion
                        : 0L);
                return new StatsBreakdownRow(g.Key, g.Count(),
                    g.Count(t => t.Outcome == RequestOutcome.Error), tokens);
            })
            .OrderByDescending(r => r.Requests)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Dòng terminal projection — chỉ cột cần cho aggregate (không materialize toàn entity).</summary>
    private sealed record TerminalRow(RequestOutcome Outcome, int? DurationMs, string? ProviderKey,
        string? ModelKey, long? ClientKeyId, string? RequestId, DateTimeOffset Timestamp);

    /// <summary>Dòng usage projection — cột token + RequestId để join.</summary>
    private sealed record UsageRow(string? RequestId, int? PromptTokens, int? CompletionTokens);
}
