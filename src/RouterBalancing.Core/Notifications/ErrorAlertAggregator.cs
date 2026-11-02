using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Stats;

namespace RouterBalancing.Core.Notifications;

/// <summary>
/// Gom mọi dòng lỗi đáng thông báo thành **một** <see cref="ErrorAlert"/> cho mỗi cửa sổ
/// <see cref="AggregationWindowSec"/> giây (spec error-notify-client-name §2.2), rồi bắn qua
/// <see cref="AlertRaised"/>.
/// <para>
/// Vì sao subscribe HAI nguồn: (1) <see cref="ILogService.LogAdded"/> chỉ nổ cho dòng App —
/// dòng journal của request được ghi thẳng bằng <c>db.LogEntries.Add</c> trong
/// <c>RequestJournalWriter.WriteTerminal</c>, không đi qua <c>ILogService.Write</c>; (2)
/// <see cref="IRequestJournalWriter.EntryWritten"/> là nguồn duy nhất cho request fail. Chỉ
/// subscribe <c>LogAdded</c> sẽ bỏ lỡ đúng kịch bản motivating: provider chết hàng loạt khi
/// app đang ở khay system.
/// </para>
/// <para>
/// Vì sao aggregator chỉ gom, không chọn kênh: quyết định kênh (toast trong app / notification
/// Windows / tắt) thuộc <see cref="NotificationChannelPolicy"/> do app layer gọi (Task 9). Một
/// nguồn sự kiện duy nhất đảm bảo việc chọn kênh xảy ra đúng một lần, không race giữa hai subscriber.
/// </para>
/// </summary>
public sealed class ErrorAlertAggregator : IDisposable
{
    /// <summary>Độ dài cửa sổ gom cụm lỗi (giây) — user chốt 30s (spec §2.2).</summary>
    internal const int AggregationWindowSec = 30;

    // Chu kỳ đánh thức Flush(): 1s — rẻ (khi cụm rỗng chỉ là một phép so sánh) và giữ cho
    // cụm đóng sát ranh giới 30s mà không cần busy-wait hay Thread.Sleep.
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan Window = TimeSpan.FromSeconds(AggregationWindowSec);

    // Trạng thái cụm: ghi từ thread request (EntryWritten), thread worker (LogAdded) và thread
    // loop flush → đọc/ghi luôn trong _gate. AlertRaised luôn bắn NGOÀI lock để handler UI hay
    // subscriber re-entrant không bao giờ giữ lock của luồng request.
    private readonly object _gate = new();

    private readonly ILogService _log;
    private readonly IRequestJournalWriter _journal;
    private readonly TimeProvider _clock;
    private readonly Func<string, string> _translate;
    private readonly List<LogEntry> _entries = [];

    private bool _windowOpen;
    private DateTimeOffset _windowStart;

    // Guard chống re-entrancy (review Task 4): EntryWritten chạy đồng bộ trên thread request,
    // nên subscriber của AlertRaised có thể gọi ngược Flush()/Observe(). Guard này ngăn cùng
    // một cửa sổ bị flush lần thứ hai (và ngăn đệ quy vô hạn Flush → AlertRaised → Flush).
    private bool _flushing;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    /// <summary>
    /// Nối aggregator vào hai nguồn lỗi và translator i18n.
    /// </summary>
    /// <param name="log">Nguồn dòng App (proxy/sidecar/update/DB/middleware).</param>
    /// <param name="journal">Nguồn dòng request terminal — xem <see cref="IRequestJournalWriter.EntryWritten"/>.</param>
    /// <param name="clock">Đồng hồ đo cửa sổ 30s — test truyền đồng hồ điều khiển được.</param>
    /// <param name="translate">Hàm dịch i18n; call site truyền <c>key =&gt; L[key]</c>.</param>
    /// <remarks>
    /// Không inject settings hay <c>IDesktopState</c> ở đây: bật/tắt thông báo và chọn kênh thuộc
    /// <see cref="NotificationChannelPolicy"/> (Task 6/9) — aggregator chỉ gom.
    /// </remarks>
    public ErrorAlertAggregator(
        ILogService log,
        IRequestJournalWriter journal,
        TimeProvider clock,
        Func<string, string> translate)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(translate);

        _log = log;
        _journal = journal;
        _clock = clock;
        _translate = translate;

        log.LogAdded += OnLogAdded;
        journal.EntryWritten += OnEntryWritten;
    }

    /// <summary>
    /// Bắn khi một cụm lỗi đóng cửa sổ — app layer (Task 9 <c>ErrorNotifier</c>) là nơi chọn
    /// kênh và hiển thị.
    /// </summary>
    /// <remarks>
    /// Hợp đồng: bắn NGOÀI lock, mỗi subscriber chạy trong try/catch riêng nên một subscriber
    /// ném lỗi không làm hỏng luồng request, không làm nuốt các subscriber còn lại.
    /// </remarks>
    public event Action<ErrorAlert>? AlertRaised;

    /// <summary>
    /// Đưa một dòng log vào cụm nếu nó vượt qua bộ lọc nhiễu (spec §2.2.1).
    /// Public để test bơm entry; hai subscription trong ctor chỉ gọi nó.
    /// </summary>
    /// <param name="entry">Dòng log từ <c>LogAdded</c> hoặc <c>EntryWritten</c> — chỉ đọc, không sửa.</param>
    /// <remarks>
    /// Hợp đồng fail-open vì handler chạy đồng bộ trên thread request: không ném, không block,
    /// không await, không gọi <see cref="ILogService"/>. Payload của <c>EntryWritten</c> là bản
    /// detached của journal writer nên aggregator không mutate nó.
    /// </remarks>
    public void Observe(LogEntry entry)
    {
        // Null-check im lặng thay vì ThrowIfNull: Observe không được phép ném (hợp đồng trên).
        if (entry is null || !IsAlertable(entry))
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!_windowOpen)
            {
                _windowOpen = true;
                // Cửa sổ mở theo đồng hồ TimeProvider (không theo entry.Timestamp) để test
                // điều khiển được ranh giới 30s mà không phụ thuộc giờ thật của entry.
                _windowStart = _clock.GetUtcNow();
            }

            _entries.Add(entry);
        }
    }

    /// <summary>
    /// Đóng cửa sổ hiện tại nếu đã đủ <see cref="AggregationWindowSec"/> giây và cụm không rỗng,
    /// rồi bắn đúng một <see cref="ErrorAlert"/>.
    /// </summary>
    /// <returns>Số alert đã bắn (0 hoặc 1).</returns>
    public int Flush()
    {
        ClusterSnapshot? snapshot = null;
        lock (_gate)
        {
            // Re-entrancy guard: subscriber gọi Flush() ngay trong handler sẽ vào đây khi flag
            // còn set và trả 0 — cụm đang bắn không bị bắn lại, dù entry mới đã vào buffer.
            if (_flushing)
            {
                return 0;
            }

            // Không entry nào → không có cửa sổ để mở, Flush() im lặng hoàn toàn.
            if (_entries.Count == 0)
            {
                return 0;
            }

            if (_clock.GetUtcNow() - _windowStart < Window)
            {
                return 0;
            }

            _flushing = true;
            snapshot = TakeSnapshotLocked();

            // Xóa buffer TRƯỚC khi dispatch: entry đến trong lúc subscriber chạy sẽ mở cửa sổ
            // mới, không bị tính trùng vào cụm vừa bắn. Buffer cũng đã xóa nên nếu translator
            // i18n ném (thiếu key) thì cụm đó rơi chứ không bị flush lại mỗi giây.
            _entries.Clear();
            _windowOpen = false;
        }

        try
        {
            // Dựng message + i18n NGOÀI lock: translator do app cung cấp, không được giữ lock
            // của luồng request trong lúc chạy code app.
            RaiseAlert(BuildAlert(snapshot!.Value));
        }
        finally
        {
            lock (_gate)
            {
                _flushing = false;
            }
        }

        return 1;
    }

    /// <summary>
    /// Khởi động vòng lặp nền gọi <see cref="Flush"/> mỗi giây. Gọi lại lần sau là no-op;
    /// không gọi thì vẫn flush thủ công được (flush_when_flush).
    /// </summary>
    /// <exception cref="ObjectDisposedException">Sau khi <see cref="Dispose"/> — không khởi động lại.</exception>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loop is not null)
            {
                return;
            }

            var cts = _cts = new CancellationTokenSource();
            _loop = Task.Run(() => RunLoopAsync(cts));
        }
    }

    /// <summary>
    /// Dừng vòng lặp nền và cắt hai subscription. Idempotent — gọi lặp lại không ném.
    /// </summary>
    public void Dispose()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        // Hai nguồn là singleton sống lâu hơn aggregator → phải unsubscribe, nếu không cụm
        // vẫn âm thầm lớn tiếp sau khi app shutdown.
        _log.LogAdded -= OnLogAdded;
        _journal.EntryWritten -= OnEntryWritten;

        if (cts is not null)
        {
            cts.Cancel();
            // Loop chưa từng chạy thì không ai dispose CTS giúp → dispose ngay. Loop đã chạy
            // thì để loop tự dispose ở finally: dispose CTS khi loop đang chờ token sẽ làm
            // loop ném ObjectDisposedException.
            if (loop is null)
            {
                cts.Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Snapshot của một cụm, lấy dưới lock rồi đem dựng message ngoài lock.</summary>
    private readonly record struct ClusterSnapshot(int Count, DateTimeOffset FirstAt, DateTimeOffset LastAt, string LastMessage);

    /// <summary>
    /// Bộ lọc nhiễu spec §2.2.1 — chạy TRƯỚC khi entry vào cụm.
    /// Request fail có <see cref="LogEntry.ProviderKey"/> null nghĩa là request chết trước khi
    /// tới upstream (body parse lỗi, model không có trong combo, auth inbound fail): báo hết thì
    /// một client đang gửi request hỏng liên tục sẽ tạo notification vô hạn mỗi 30 giây.
    /// Warning/Info/Debug luôn bị loại vì dày đặc (retry, park, chờ slot) và nhấn chìm lỗi thật.
    /// </summary>
    private static bool IsAlertable(LogEntry entry) =>
        entry.Severity == LogSeverity.Error
        && (entry.Category == LogCategory.App || entry.ProviderKey is not null);

    /// <summary>
    /// Chụp trạng thái cụm hiện tại. Gọi trong <see cref="_gate"/>.
    /// </summary>
    private ClusterSnapshot TakeSnapshotLocked()
    {
        // Entry đến từ nhiều thread nên Timestamp không theo thứ tự arrival: lấy min/max để
        // FirstAt/LastAt đúng ngữ nghĩa "lỗi đầu tiên"/"lỗi gần nhất"; khi hòa timestamp thì
        // lấy theo arrival (message vừa tới là cái user thấy gần nhất).
        var firstAt = _entries[0].Timestamp;
        var lastAt = firstAt;
        var lastMessage = _entries[0].Message;
        for (var i = 1; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.Timestamp < firstAt)
            {
                firstAt = entry.Timestamp;
            }

            if (entry.Timestamp >= lastAt)
            {
                lastAt = entry.Timestamp;
                lastMessage = entry.Message;
            }
        }

        return new ClusterSnapshot(_entries.Count, firstAt, lastAt, lastMessage);
    }

    /// <summary>
    /// Dựng <see cref="ErrorAlert"/> từ snapshot — chạy ngoài lock.
    /// LogEntry.Details (stack trace) không bao giờ đi vào thông báo, chỉ Message;
    /// <see cref="ErrorAlertMessage.Build"/> cắt cứng 200 ký tự.
    /// </summary>
    private ErrorAlert BuildAlert(ClusterSnapshot snapshot)
    {
        var title = _translate("notify.error.title");
        var message = ErrorAlertMessage.Build(_translate, snapshot.Count, Window, snapshot.LastMessage);
        return new ErrorAlert(snapshot.Count, title, message, snapshot.FirstAt, snapshot.LastAt);
    }

    /// <summary>Gói <see cref="Observe"/> cho nguồn App log, có cách ly lỗi.</summary>
    private void OnLogAdded(LogEntry entry)
    {
        try
        {
            Observe(entry);
        }
        catch
        {
            // intentional swallow: ghi log từ đây sẽ bắn LogAdded → chính Observe nhận được
            // → App Error mới → alert mới mỗi 30 giây, tức vòng tự nuôi (review Task 4).
        }
    }

    /// <summary>Gói <see cref="Observe"/> cho nguồn request journal, có cách ly lỗi.</summary>
    private void OnEntryWritten(LogEntry entry)
    {
        try
        {
            Observe(entry);
        }
        catch
        {
            // intentional swallow: handler chạy đồng bộ trên thread request — nếu ném,
            // RequestJournalWriter.SafeLog sẽ ghi App Error → LogAdded → Observe → vòng tự nuôi.
        }
    }

    /// <summary>
    /// Gọi từng subscriber trong một try/catch riêng — multicast delegate dừng ở handler ném
    /// lỗi đầu tiên nếu không cách ly.
    /// </summary>
    private void RaiseAlert(ErrorAlert alert)
    {
        var handlers = AlertRaised;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<ErrorAlert>)handler)(alert);
            }
            catch (Exception ex)
            {
                // SafeWarn là loop-safe: dòng Warn không vượt qua IsAlertable (đòi Severity=Error),
                // nên việc báo lỗi subscriber hỏng không tự nuôi thêm alert mỗi 30 giây. Không dùng
                // Error: notifier hỏng vĩnh viễn sẽ thành alert mới vô hạn và nuốt hết trace.
                SafeWarn("subscriber", ex);
            }
        }
    }

    /// <summary>
    /// Vòng lặp nền: flush mỗi giây cho tới khi <see cref="Dispose"/>. Một chu kỳ lỗi không
    /// giết vòng lặp và không bao giờ ném ra ngoài (pattern <c>UpdateCheckService.RunAsync</c>).
    /// </summary>
    private async Task RunLoopAsync(CancellationTokenSource cts)
    {
        var token = cts.Token;
        try
        {
            using var timer = new PeriodicTimer(FlushInterval);
            while (true)
            {
                if (!await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                {
                    break; // timer đã bị dispose
                }

                try
                {
                    Flush();
                }
                catch (Exception ex)
                {
                    SafeWarn("flush", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // app đã Dispose — thoát êm
        }
        catch (Exception ex)
        {
            SafeWarn("loop", ex);
        }
        finally
        {
            // CTS do loop sở hữu sau Start: Dispose() chỉ Cancel, còn giải phóng ở đây để
            // không dispose token đang được WaitForNextTickAsync chờ.
            cts.Dispose();
        }
    }

    /// <summary>
    /// Ghi lỗi nền ở mức Warning chứ không Error: dòng Error Category=App sẽ qua bộ lọc
    /// §2.2.1 và tự nuôi một alert mỗi 30 giây cho tới khi app thoát.
    /// </summary>
    private void SafeWarn(string where, Exception ex)
    {
        try
        {
            _log.Warn($"ErrorAlertAggregator {where} failed: {ex.Message}");
        }
        catch
        {
            // intentional swallow — chính log nổ thì không còn chỗ báo an toàn hơn
            // (mirror UpdateCheckService.RunAsync).
        }
    }
}
