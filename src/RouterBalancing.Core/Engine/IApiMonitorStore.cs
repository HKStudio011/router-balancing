namespace RouterBalancing.Core.Engine;

/// <summary>Trạng thái xử lý của một request trong API Monitor — suy ra từ vòng đời feed (H1–H4).</summary>
public enum ApiCallState
{
    /// <summary>Vừa nhận, chưa dispatch.</summary>
    Queued,

    /// <summary>Đang chọn provider / chờ attempt kết thúc.</summary>
    Running,

    /// <summary>Hoàn tất thành công — feed phát <c>Finished{Success=true}</c>.</summary>
    Done,

    /// <summary>Kết thúc lỗi — feed phát <c>Finished{Success=false}</c>.</summary>
    Error,

    /// <summary>Bị hủy — feed phát <c>Canceled</c>.</summary>
    Cancelled,
}

/// <summary>
/// Một dòng theo dõi API call — immutable; upsert theo RequestId
/// (nhiều attempt vẫn là 1 record, vị trí giữ theo thứ tự thấy lần đầu).
/// </summary>
/// <param name="RequestId">Id duy nhất — key join với trail trace.</param>
/// <param name="Model">Model alias client gửi.</param>
/// <param name="StartedAt">Thời điểm request được nhận (H1).</param>
/// <param name="State">Trạng thái hiện tại — do feed quyết định (<c>RecordError</c> không đụng tới).</param>
/// <param name="FirstTokenAt">Thời điểm byte SSE đầu tiên — null nếu non-stream/chưa có.</param>
/// <param name="CompletedAt">Thời điểm kết thúc (Finished/Canceled) — null khi chưa xong.</param>
/// <param name="Status">HTTP status — null khi lỗi mạng (không có status) hoặc chưa có response. Với request <c>stream=true</c> sau early-headers, wire luôn 200: đây là <b>logical status của outcome</b> (exhaustion 429, resolve 404, fault 500 — spec early-headers §6).</param>
/// <param name="Success">Kết quả thành công — null khi chưa kết luận.</param>
/// <param name="FailureKind">Nhóm lỗi ("http"/"network") — do <c>RecordError</c> ghi.</param>
/// <param name="PromptTokens">Token prompt ghi được từ usage (2xx) — null khi thiếu.</param>
/// <param name="CompletionTokens">Token completion ghi được từ usage (2xx) — null khi thiếu.</param>
/// <param name="Combo">Combo id từ event Attempt cuối cùng.</param>
/// <param name="Provider">Provider id từ event Attempt cuối cùng.</param>
/// <param name="Account">Tài khoản provider từ event Attempt cuối cùng.</param>
/// <param name="Mode">Chế độ dispatch (vd. direct, balancer) từ DispatchStarted.</param>
/// <param name="PromptBody">Prompt UTF-8, cap 64KB (truncate + marker) — giữ từ lúc nhận request.</param>
/// <param name="ResponseBody">Response UTF-8, cap 64KB — chỉ ghi ở 2xx.</param>
/// <param name="ErrorBody">Error body UTF-8, cap 64KB — xóa khi request retry thành công (2xx).</param>
public sealed record ApiCallRecord(
    string RequestId, string Model, DateTimeOffset StartedAt, ApiCallState State,
    DateTimeOffset? FirstTokenAt, DateTimeOffset? CompletedAt,
    int? Status, bool? Success, string? FailureKind,
    int? PromptTokens, int? CompletionTokens,
    string? Combo, string? Provider, string? Account, string? Mode,
    string? PromptBody, string? ResponseBody, string? ErrorBody);

/// <summary>
/// Store in-memory API Monitor: ring 50 record (mới nhất trước) + bộ đếm "Token hôm nay"
/// theo ngày UTC, tiêu thụ <see cref="ITraceFeed.Published"/> để lấy trạng thái vòng đời.
/// </summary>
public interface IApiMonitorStore
{
    /// <summary>
    /// Phát sau mỗi lần store đổi — UI re-render. <b>Contract fail-open:</b> mutation đã ghi
    /// xong mới fire; subscriber nổ chỉ được log (không lan ra caller), và fire luôn nằm
    /// ngoài lock (không giữ lock khi gọi ra ngoài).
    /// </summary>
    event Action Changed;

    /// <summary>Tổng prompt+completion của các response trong ngày UTC hiện tại — tile "Token hôm nay".</summary>
    long TodayTokens { get; }

    /// <summary>Ảnh chụp tối đa 50 record, mới nhất trước; record được cập nhật không đổi vị trí.</summary>
    /// <returns>Danh sách record theo thứ tự mới → cũ.</returns>
    IReadOnlyList<ApiCallRecord> Snapshot();

    /// <summary>Tìm record theo id — <see langword="null"/> nếu chưa có hoặc đã bị evict (dùng join popup).</summary>
    /// <param name="requestId">Id cần tìm.</param>
    ApiCallRecord? Find(string requestId);

    /// <summary>
    /// Ghi (upsert) record tại H1 — record mới vào ring với State=Queued, prompt cap 64KB;
    /// record có sẵn giữ nguyên State (chỉ feed được đổi State sau đó).
    /// </summary>
    /// <param name="requestId">Id duy nhất của request.</param>
    /// <param name="model">Model alias client gửi.</param>
    /// <param name="promptBody">Thân prompt (UTF-8 bytes) — vượt 64KB bị truncate + marker.</param>
    void StartRequest(string requestId, string model, byte[] promptBody);

    /// <summary>
    /// Ghi kết quả 2xx: tokens, FirstTokenAt, ResponseBody; xóa ErrorBody (retry đã thắng)
    /// và cộng vào <see cref="TodayTokens"/> (reset khi đổi ngày UTC).
    /// </summary>
    /// <param name="requestId">Id của request.</param>
    /// <param name="promptTokens">Token prompt — null nếu upstream không trả usage.</param>
    /// <param name="completionTokens">Token completion — null nếu upstream không trả usage.</param>
    /// <param name="firstTokenAt">Byte SSE đầu tiên — null nếu non-stream.</param>
    /// <param name="responseBody">Response UTF-8 — vượt 64KB bị truncate + marker.</param>
    void RecordResponse(string requestId, int? promptTokens, int? completionTokens,
        DateTimeOffset? firstTokenAt, string? responseBody);

    /// <summary>
    /// Ghi lỗi non-2xx/network: ErrorBody + Status (0 → null) + FailureKind ("http"/"network").
    /// <b>Không đụng State</b> — walk còn retry nên chỉ feed H4 mới chốt Done/Error.
    /// </summary>
    /// <param name="requestId">Id của request.</param>
    /// <param name="status">HTTP status — 0 nghĩa là lỗi mạng (không có status).</param>
    /// <param name="errorBody">Error body UTF-8 — vượt 64KB bị truncate + marker; null nếu không có.</param>
    void RecordError(string requestId, int status, string? errorBody);
}
