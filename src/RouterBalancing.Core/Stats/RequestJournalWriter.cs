using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Stats;

/// <summary>
/// Journal writer: subscribe <see cref="ITraceFeed.Published"/> trong ctor (singleton,
/// mirror <c>ApiMonitorStore</c> — không unsubscribe), ghi 1 dòng terminal/request vào
/// LogEntries rồi fire <see cref="IStatsInvalidationSource.Invalidate"/> — spec dashboard-stats §4.
/// Fail-open: mọi lỗi chỉ được log, không bao giờ nổ ra feed — multicast delegate dừng ở
/// subscriber nổ, handler này nổ sẽ chặn subscriber phía sau (bài học RequestTrace).
/// </summary>
public sealed class RequestJournalWriter : IRequestJournalWriter
{
    /// <summary>Entry không tới terminal trong 30 phút bị sweep — chống leak khi request không bao giờ publish terminal (vd. enqueue fail).</summary>
    private static readonly TimeSpan StaleEntryAge = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Pending> _active = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ILogService _log;
    private readonly IStatsInvalidationSource _invalidation;
    private readonly TimeProvider _clock;

    /// <summary>Tạo writer và đăng ký ngay vào feed — singleton sống suốt app (ProxyHost eager-create).</summary>
    /// <param name="feed">Feed trace — nguồn event theo vòng đời request.</param>
    /// <param name="db">Factory DB — ghi dòng terminal trực tiếp vào LogEntries.</param>
    /// <param name="log">Log service — mọi lỗi (kể cả subscriber nổ) chỉ về đây, không ra feed.</param>
    /// <param name="invalidation">Nguồn invalidation — fire sau khi ghi dòng thành công.</param>
    /// <param name="clock">Đồng hồ cho sweep 30 phút (UTC).</param>
    /// <exception cref="ArgumentNullException">Khi một trong các tham số là <see langword="null"/>.</exception>
    public RequestJournalWriter(ITraceFeed feed, IDbContextFactory<RouterBalancingDbContext> db,
        ILogService log, IStatsInvalidationSource invalidation, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(invalidation);
        ArgumentNullException.ThrowIfNull(clock);

        _db = db;
        _log = log;
        _invalidation = invalidation;
        _clock = clock;

        // Why: Publish chạy trên thread endpoint/dispatcher — handler phải tự cách ly lỗi
        // (try/catch toàn bộ trong OnPublished) để không làm nổ caller hay chặn subscriber sau.
        feed.Published += OnPublished;
    }

    /// <summary>Dữ liệu carry từ Received tới terminal — theo RequestId.</summary>
    /// <param name="StartedAt">Thời điểm Received đầu — không đổi khi Received bị publish lại (G2/priority).</param>
    /// <param name="Model">Model tại Received.</param>
    /// <param name="ClientKeyId">Client key đã auth — chỉ Received đầu mang giá trị.</param>
    /// <param name="Combo">Combo id từ attempt gần nhất.</param>
    /// <param name="Provider">Provider id từ attempt gần nhất — attempt mới nhất đè.</param>
    /// <param name="Account">Account từ attempt gần nhất.</param>
    private sealed record Pending(DateTimeOffset StartedAt, string Model, long? ClientKeyId,
        string? Combo, string? Provider, string? Account);

    /// <summary>Nhận event từ feed: sweep entry cũ, cập nhật map hoặc ghi dòng terminal.</summary>
    /// <param name="e">Event trace vừa được publish.</param>
    private void OnPublished(TraceEvent e)
    {
        try
        {
            Sweep();

            switch (e.Stage)
            {
                case TraceStage.Received:
                    // TryAdd KHÔNG đè StartedAt: Received bị publish lại (G2 HeadersSent,
                    // priority-update G3) — đè StartedAt sẽ làm DurationMs sai (âm hoặc lệch mốc).
                    // TryAdd fail → thử merge ClientKeyId (priority-update có thể đến trước
                    // Received thật — xem MergeClientKeyId).
                    if (!_active.TryAdd(e.RequestId,
                            new Pending(e.At, e.Model, e.ClientKeyId, null, null, null)))
                        MergeClientKeyId(e);
                    break;
                case TraceStage.DispatchStarted:
                case TraceStage.Attempt:
                    UpdateRoute(e);
                    break;
                case TraceStage.Finished:
                    WriteTerminal(e, e.Success == true ? RequestOutcome.Ok : RequestOutcome.Error);
                    break;
                case TraceStage.Canceled:
                    // Stream chết giữa chừng khi client còn nối (ProxyApp ghi FailureKind
                    // "network") là LỖI — ghi Error để stats ErrorRate không nuốt; client
                    // cancel thật (FailureKind null) giữ nguyên Cancelled như cũ
                    WriteTerminal(e, e.FailureKind == "network"
                        ? RequestOutcome.Error
                        : RequestOutcome.Cancelled);
                    break;
                // Parked: marker Live Trace, không phải terminal — không journal
            }
        }
        catch (Exception ex)
        {
            SafeLog(nameof(OnPublished), ex);
        }
    }

    /// <summary>
    /// Merge ClientKeyId từ Received thật vào entry đã tạo bởi priority-update (G3) —
    /// priority-update (<c>RequestQueue.PublishPriorityUpdate</c>, ClientKeyId=null) có thể
    /// publish TRƯỚC Received đầu tiên của ProxyApp trong cửa sổ microgiây Enqueue→Received,
    /// khiến TryAdd đầu tiên lưu ClientKeyId=null. Chỉ update ClientKeyId khi entry chưa có
    /// giá trị; StartedAt LUÔN giữ nguyên (event sớm nhất quyết định DurationMs).
    /// </summary>
    /// <param name="e">Received event vừa fail TryAdd.</param>
    private void MergeClientKeyId(TraceEvent e)
    {
        if (e.ClientKeyId is null)
            return; // incoming không mang giá trị (G2/priority-update) — không đụng entry

        if (!_active.TryGetValue(e.RequestId, out var current) || current.ClientKeyId is not null)
            return; // entry đã bị xóa (terminal/sweep) hoặc đã có ClientKeyId — không đè

        _active.TryUpdate(e.RequestId, current with { ClientKeyId = e.ClientKeyId }, current);
    }

    /// <summary>
    /// Cập nhật Combo/Provider/Account từ DispatchStarted/Attempt — attempt mới nhất đè
    /// (event terminal lấy route tại thời điểm request, spec §4).
    /// </summary>
    /// <param name="e">Event trace — <see cref="TraceEvent.Route"/> null (DispatchStarted
    /// chưa chọn xong) bị bỏ qua, không đè route đã biết.</param>
    private void UpdateRoute(TraceEvent e)
    {
        // DispatchStarted publish Route=null (DispatcherLoop:162-165) — null KHÔNG được đè
        // route đã biết từ Attempt (quy ước ApiMonitorStore/DispatcherLoop:687:
        // null never overwrites known route).
        if (e.Route is null)
            return;

        if (!_active.TryGetValue(e.RequestId, out var current))
            return; // chưa Received hoặc đã sweep — không có entry để cập nhật

        var updated = current with
        {
            Combo = e.Route.Combo,
            Provider = e.Route.Provider,
            Account = e.Route.Account,
        };
        _active.TryUpdate(e.RequestId, updated, current);
    }

    /// <summary>
    /// Ghi 1 dòng terminal rồi xóa entry. Fail-open: lỗi DB chỉ được log — không invalidate,
    /// không nổ feed.
    /// </summary>
    /// <param name="e">Event terminal (Finished/Canceled).</param>
    /// <param name="outcome">Kết quả terminal của request.</param>
    private void WriteTerminal(TraceEvent e, RequestOutcome outcome)
    {
        if (!_active.TryGetValue(e.RequestId, out var pending))
            return; // entry đã sweep hoặc không thấy Received — thiếu StartedAt không tính được DurationMs

        var written = false;
        try
        {
            using var db = _db.CreateDbContext();
            db.LogEntries.Add(BuildEntry(e, pending, outcome));
            db.SaveChanges();
            written = true;
        }
        catch (Exception ex)
        {
            // Ghi thất bại → KHÔNG invalidate: số liệu sẽ làm mới ở lần ghi thành công kế tiếp
            SafeLog(nameof(WriteTerminal), ex);
        }
        finally
        {
            _active.TryRemove(e.RequestId, out _);
        }

        if (written)
            _invalidation.Invalidate();
    }

    /// <summary>Xây dòng journal theo bảng spec §4 — Timestamp chuẩn hóa UTC (publish sites dùng offset local).</summary>
    /// <param name="e">Event terminal.</param>
    /// <param name="pending">Entry đang active của request.</param>
    /// <param name="outcome">Kết quả terminal.</param>
    /// <returns>Dòng LogEntry(Category=Request) sắp ghi.</returns>
    private static LogEntry BuildEntry(TraceEvent e, Pending pending, RequestOutcome outcome)
    {
        var durationMs = (int)(e.At - pending.StartedAt).TotalMilliseconds;
        return new LogEntry
        {
            Timestamp = e.At.ToUniversalTime(),
            Severity = outcome == RequestOutcome.Error ? LogSeverity.Error : LogSeverity.Info,
            Category = LogCategory.Request,
            Message = BuildMessage(e, pending, outcome, durationMs),
            RequestId = e.RequestId,
            DurationMs = durationMs,
            ClientKeyId = pending.ClientKeyId,
            // FailureKind từ publisher (vd. "network" cho upstream cắt mid-stream) override
            // suy ra từ Status — request stream chết có status 200/500 nhưng gốc là lỗi mạng
            ErrorCode = outcome switch
            {
                RequestOutcome.Error => e.FailureKind ?? (e.Status is > 0 ? "http" : "network"),
                _ => null,
            },
            Outcome = outcome,
            ProviderKey = pending.Provider,
            ModelKey = pending.Model,
            // Cancelled không có HTTP status trả về client (spec §4)
            HttpStatus = outcome == RequestOutcome.Cancelled ? null : e.Status,
        };
    }

    /// <summary>Message theo spec §4: hoàn tất / thất bại / bị huỷ theo outcome.</summary>
    /// <param name="e">Event terminal.</param>
    /// <param name="pending">Entry đang active của request.</param>
    /// <param name="outcome">Kết quả terminal.</param>
    /// <param name="durationMs">Thời gian xử lý tính từ StartedAt.</param>
    /// <returns>Chuỗi message hiển thị trong Log panel.</returns>
    private static string BuildMessage(TraceEvent e, Pending pending, RequestOutcome outcome, int durationMs)
    {
        var verb = outcome switch
        {
            RequestOutcome.Ok => "hoàn tất",
            RequestOutcome.Error => "thất bại",
            _ => "bị huỷ",
        };
        // Không có status (lỗi mạng / huỷ trước response) → bỏ segment "HTTP …" thay vì hiển thị rỗng
        var statusPart = outcome != RequestOutcome.Cancelled && e.Status is { } status
            ? $", HTTP {status}"
            : string.Empty;
        return $"Request {e.RequestId} {verb}: {pending.Model} → {pending.Provider ?? "—"}{statusPart}, {durationMs}ms";
    }

    /// <summary>
    /// Xóa entry có StartedAt cũ hơn 30 phút — chạy trước mỗi event để giới hạn leak
    /// khi request không bao giờ tới terminal.
    /// </summary>
    private void Sweep()
    {
        var cutoff = _clock.GetUtcNow() - StaleEntryAge;
        foreach (var (id, pending) in _active)
        {
            // Conditional remove: chỉ xóa đúng giá trị đã thấy khi enumerate —
            // không đụng entry mới (re-add cùng id) xảy ra giữa enumerate và remove
            if (pending.StartedAt < cutoff)
                _active.TryRemove(new KeyValuePair<string, Pending>(id, pending));
        }
    }

    /// <summary>Ghi lỗi ra log — chính log nổ cũng không được phá contract fail-open (mirror ApiMonitorStore.SafeLog).</summary>
    /// <param name="operation">Tên thao tác bị lỗi.</param>
    /// <param name="ex">Exception cần ghi.</param>
    private void SafeLog(string operation, Exception ex)
    {
        try
        {
            _log.Error($"RequestJournalWriter: {operation} failed", ex);
        }
        catch
        {
            // intentional swallow — không còn chỗ report nào an toàn hơn (mirror TraceFeed.Publish)
        }
    }
}
