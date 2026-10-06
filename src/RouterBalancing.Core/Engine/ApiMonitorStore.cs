using System.Text;
using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Triển khai <see cref="IApiMonitorStore"/>: ring 50 record theo thứ tự thấy lần đầu
/// (mutation dưới lock, fire <see cref="IApiMonitorStore.Changed"/> ngoài lock) + tiêu thụ
/// <see cref="ITraceFeed.Published"/> để lấy State/Status/Success/CompletedAt (H4 authoritative).
/// Mọi entry point bọc try/catch + log — không bao giờ ném exception ra caller (contract fail-open).
/// </summary>
public sealed class ApiMonitorStore : IApiMonitorStore
{
    /// <summary>Số record tối đa giữ lại — ring độc lập (Live Trace giữ ring 60 riêng).</summary>
    private const int RingCap = 50;

    /// <summary>Cap body hiển thị — prompt/response/error dài hơn bị cắt + marker (chống row multi-MB).</summary>
    private const int BodyCap = 64 * 1024;

    private const string TruncateMarker = "[truncated]";

    private readonly object _gate = new();
    private readonly List<ApiCallRecord> _calls = [];
    private readonly ILogService _log;
    private readonly TimeProvider _time;

    private long _todayTokens;
    private DateTime _today;

    /// <summary>Tạo store và đăng ký ngay vào feed để nhận vòng đời request.</summary>
    /// <param name="feed">Feed trace — nguồn H1–H4 cho State/Status/Success/CompletedAt.</param>
    /// <param name="log">Log service — mọi lỗi (kể cả subscriber nổ) chỉ về đây, không ra caller.</param>
    /// <param name="time">Chỉ quyết định "hôm nay" của <see cref="TodayTokens"/> (ngày UTC — không dùng DateTime.Now).</param>
    public ApiMonitorStore(ITraceFeed feed, ILogService log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);

        _log = log;
        _time = time;
        _today = time.GetUtcNow().Date;

        // Why: Publish chạy trên thread endpoint/dispatcher — multicast delegate dừng ở
        // subscriber nổ, nên nếu handler này nổ thì publish sẽ nổ caller (H4 xung quanh).
        // Toàn bộ handler bọc try/catch + log → monitor không bao giờ làm request nổ.
        feed.Published += OnPublished;
    }

    /// <inheritdoc />
    public event Action Changed = delegate { };

    /// <inheritdoc />
    public long TodayTokens
    {
        get
        {
            lock (_gate)
            {
                // Reset Ở CẢ GETTER: tile "Token hôm nay" phải đọc đúng ngày UTC hiện tại
                // ngay sau nửa đêm, kể cả khi chưa có RecordResponse nào của ngày mới.
                try
                {
                    RollToday();
                }
                catch (Exception ex)
                {
                    // Fail-open: TimeProvider nổ thì trả counter hiện có, không nổ UI
                    SafeLog(nameof(TodayTokens), ex);
                }
                return _todayTokens;
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ApiCallRecord> Snapshot()
    {
        lock (_gate)
        {
            var snapshot = _calls.ToArray();
            // List giữ thứ tự first-seen → đảo lại để UI hiển thị mới nhất trước
            Array.Reverse(snapshot);
            return snapshot;
        }
    }

    /// <inheritdoc />
    public ApiCallRecord? Find(string requestId)
    {
        lock (_gate)
        {
            var index = _calls.FindIndex(c => c.RequestId == requestId);
            return index >= 0 ? _calls[index] : null;
        }
    }

    /// <inheritdoc />
    public void StartRequest(string requestId, string model, byte[] promptBody)
    {
        try
        {
            var prompt = DecodeCapped(promptBody);
            lock (_gate)
            {
                var index = _calls.FindIndex(c => c.RequestId == requestId);
                if (index < 0)
                {
                    Add(Skeleton(requestId, model, _time.GetUtcNow(), prompt));
                }
                else
                {
                    // Upsert tại chỗ — không reorder ring. State KHÔNG đụng: enqueue chạy trước
                    // hook H1 nên DispatchStarted có thể về trước StartRequest — không được
                    // downgrade Running → Queued; sau khi record tồn tại chỉ feed mới đổi State.
                    _calls[index] = _calls[index] with
                    {
                        Model = model,
                        StartedAt = _time.GetUtcNow(),
                        PromptBody = prompt,
                    };
                }
            }
        }
        catch (Exception ex)
        {
            SafeLog(nameof(StartRequest), ex);
        }

        InvokeChanged();
    }

    /// <inheritdoc />
    public void RecordResponse(string requestId, int? promptTokens, int? completionTokens,
        DateTimeOffset? firstTokenAt, string? responseBody)
    {
        try
        {
            var body = Truncate(responseBody);
            lock (_gate)
            {
                var index = GetOrCreate(requestId, string.Empty, _time.GetUtcNow());
                _calls[index] = _calls[index] with
                {
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    FirstTokenAt = firstTokenAt,
                    ResponseBody = body,
                    // 2xx sau khi đã retry qua lỗi → error body cũ hết nghĩa
                    ErrorBody = null,
                };

                // "Token hôm nay" theo ngày UTC của TimeProvider — reset khi đổi ngày.
                // Đếm riêng ring: record có thể bị evict nhưng token vẫn thuộc hôm nay.
                RollToday();
                _todayTokens += (promptTokens ?? 0) + (completionTokens ?? 0);
            }
        }
        catch (Exception ex)
        {
            SafeLog(nameof(RecordResponse), ex);
        }

        InvokeChanged();
    }

    /// <inheritdoc />
    public void RecordError(string requestId, int status, string? errorBody)
    {
        try
        {
            var body = Truncate(errorBody);
            lock (_gate)
            {
                var index = GetOrCreate(requestId, string.Empty, _time.GetUtcNow());
                _calls[index] = _calls[index] with
                {
                    ErrorBody = body,
                    // status 0 = lỗi mạng (không có HTTP status) → Status null, kind "network"
                    Status = status > 0 ? status : null,
                    FailureKind = status > 0 ? "http" : "network",
                    // Why KHÔNG đụng State: walk 429 → attempt sau → 2xx phải kết thúc Done,
                    // nên chỉ feed H4 (Finished/Canceled) mới chốt State (H4 authoritative)
                };
            }
        }
        catch (Exception ex)
        {
            SafeLog(nameof(RecordError), ex);
        }

        InvokeChanged();
    }

    /// <summary>
    /// Nhận event từ feed: tạo skeleton nếu chưa có record, rồi chuyển trạng thái theo stage.
    /// </summary>
    /// <param name="e">Event trace vừa được publish.</param>
    private void OnPublished(TraceEvent e)
    {
        try
        {
            lock (_gate)
            {
                var index = GetOrCreate(e.RequestId, e.Model, e.At);
                var current = _calls[index];
                var updated = e.Stage switch
                {
                    // Received: skeleton vừa tạo ở trên đã đúng (Queued); record có sẵn giữ nguyên
                    TraceStage.Received => current,
                    TraceStage.DispatchStarted => current with
                    {
                        State = ApiCallState.Running,
                        Mode = e.Mode,
                    },
                    // Attempt cuối thắng: attempt mới nhất đè route; prompt/mode giữ nguyên
                    TraceStage.Attempt => current with
                    {
                        State = ApiCallState.Running,
                        Combo = e.Route?.Combo,
                        Provider = e.Route?.Provider,
                        Account = e.Route?.Account,
                    },
                    TraceStage.Finished => current with
                    {
                        State = e.Success == true ? ApiCallState.Done : ApiCallState.Error,
                        // Status null (lỗi mạng) giữ status tạm do RecordError ghi, không đè
                        Status = e.Status ?? current.Status,
                        Success = e.Success,
                        CompletedAt = e.At,
                    },
                    TraceStage.Canceled => current with
                    {
                        State = ApiCallState.Cancelled,
                        CompletedAt = e.At,
                    },
                    _ => current,
                };
                _calls[index] = updated;
            }
        }
        catch (Exception ex)
        {
            SafeLog("Published", ex);
        }

        InvokeChanged();
    }

    /// <summary>
    /// Tìm index record; thiếu thì tạo skeleton (Queued) + evict nếu vượt ring.
    /// Gọi khi đang giữ <see cref="_gate"/> — trả về index để upsert tại chỗ (giữ first-seen).
    /// </summary>
    private int GetOrCreate(string requestId, string model, DateTimeOffset startedAt)
    {
        var index = _calls.FindIndex(c => c.RequestId == requestId);
        if (index >= 0)
            return index;

        Add(Skeleton(requestId, model, startedAt, promptBody: null));
        return _calls.Count - 1;
    }

    /// <summary>Thêm record vào cuối ring (thứ tự first-seen) và drop bớt nếu vượt cap.</summary>
    private void Add(ApiCallRecord record)
    {
        _calls.Add(record);
        // Cũ nhất luôn đứng đầu list → drop từ đầu cho tới khi về cap
        while (_calls.Count > RingCap)
            _calls.RemoveAt(0);
    }

    /// <summary>
    /// Reset bộ đếm nếu đã sang ngày UTC mới — nguồn sự thật duy nhất cho quy tắc
    /// "Token hôm nay"; gọi từ cả <see cref="TodayTokens"/> và <see cref="RecordResponse"/>
    /// (getter phải tự reset để tile không hiển thị tổng của hôm qua). Gọi khi đang giữ <see cref="_gate"/>.
    /// </summary>
    private void RollToday()
    {
        var today = _time.GetUtcNow().Date;
        if (today != _today)
        {
            _today = today;
            _todayTokens = 0;
        }
    }

    /// <summary>Record mới ở trạng thái Queued — khung dùng chung cho StartRequest/feed/upsert.</summary>
    private static ApiCallRecord Skeleton(string requestId, string model, DateTimeOffset startedAt,
        string? promptBody) =>
        new(requestId, model, startedAt, ApiCallState.Queued,
            FirstTokenAt: null, CompletedAt: null, Status: null, Success: null, FailureKind: null,
            PromptTokens: null, CompletionTokens: null, Combo: null, Provider: null, Account: null,
            Mode: null, PromptBody: promptBody, ResponseBody: null, ErrorBody: null);

    /// <summary>Cắt body vượt cap + marker — một helper chung cho prompt/response/error.</summary>
    private static string? Truncate(string? value)
    {
        if (value is null || value.Length <= BodyCap)
            return value;
        // Đã qua DecodeCapped/tee (marker nằm cuối, tổng không vượt cap + marker) → giữ nguyên:
        // cắt lần nữa sẽ cắt vào giữa marker sinh "[t[truncated]" (marker 2 phần) —
        // điều phối với handler để marker xuất hiện đúng 1 lần
        if (value.Length <= BodyCap + TruncateMarker.Length
            && value.EndsWith(TruncateMarker, StringComparison.Ordinal))
            return value;
        return value[..BodyCap] + TruncateMarker;
    }

    /// <summary>
    /// Decode UTF-8 TRƯỚC khi cap: chỉ đọc tối đa <see cref="BodyCap"/> byte đầu rồi mới
    /// chuyển string — body nhiều MB không sinh string tạm ~2× kích thước (trước đây
    /// GetString toàn bộ rồi mới cắt). Byte cắt rơi giữa chuỗi multi-byte → ký tự thay thế
    /// U+FFFD (chấp nhận). Output ≤ <see cref="BodyCap"/> ký tự + marker; marker
    /// <c>[truncated]</c> xuất hiện đúng 1 lần khi body bị cắt.
    /// </summary>
    /// <param name="body">Body UTF-8 thô (prompt/error) — có thể nhiều MB.</param>
    internal static string DecodeCapped(byte[] body)
    {
        var cut = Math.Min(body.Length, BodyCap);
        var text = Encoding.UTF8.GetString(body, 0, cut);
        return body.Length > cut ? text + TruncateMarker : text;
    }

    /// <summary>
    /// Fire <see cref="Changed"/> NGOÀI lock: subscriber (UI render) không được giữ lock —
    /// tránh deadlock; bọc try/catch để subscriber nổ không lan ra caller
    /// (multicast delegate dừng ở subscriber nổ nên bọc cả chuỗi).
    /// </summary>
    private void InvokeChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            SafeLog(nameof(Changed), ex);
        }
    }

    /// <summary>Ghi lỗi ra log — chính log nổ cũng không được phá contract fail-open.</summary>
    private void SafeLog(string operation, Exception ex)
    {
        try
        {
            _log.Error($"ApiMonitorStore: {operation} failed", ex);
        }
        catch
        {
            // intentional swallow — không còn chỗ report nào an toàn hơn (mirror TraceFeed.Publish)
        }
    }
}
