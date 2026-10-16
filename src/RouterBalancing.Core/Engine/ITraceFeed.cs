using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>Giai đoạn xử lý của một request trong pipeline — điểm phát trace event.</summary>
public enum TraceStage
{
    /// <summary>Request vừa được nhận vào pipeline.</summary>
    Received,

    /// <summary>Bắt đầu chọn provider/combo để dispatch.</summary>
    DispatchStarted,

    /// <summary>Một lần thử gửi upstream (retry là Attempt mới).</summary>
    Attempt,

    /// <summary>Request kết thúc thành công — terminal.</summary>
    Finished,

    /// <summary>Request bị hủy — terminal.</summary>
    Canceled,

    /// <summary>Request đã từng dispatch nhưng bị đẩy về queue chờ slot (ReenqueueForPark) — spec G1.</summary>
    Parked,
}

/// <summary>1 hop trong đường đi combo lồng nhau — tên + mode CỦA CẤP ĐÓ (breadcrumb, mockup B).</summary>
/// <param name="Name">Tên combo cấp này.</param>
/// <param name="Mode">Mode của combo cấp này — không phải mode root.</param>
public sealed record TraceHop(string Name, ComboMode Mode);

/// <summary>Route đã chọn cho một request — null từng field khi chưa xác định.</summary>
/// <param name="Combo">Combo id đã chọn.</param>
/// <param name="Provider">Provider id đã chọn.</param>
/// <param name="Account">Tài khoản provider đã dùng.</param>
/// <param name="ComboPath">Đường đi combo root→leaf (không gồm leaf) khi resolve qua combo
/// lồng nhau — <see langword="null"/> khi flat path hoặc combo 1 cấp (UI giữ breadcrumb cũ).</param>
public sealed record TraceRoute(string? Combo, string? Provider, string? Account,
    IReadOnlyList<TraceHop>? ComboPath = null);

/// <summary>Một điểm sự kiện trong trace của request — immutable, không chứa secret/prompt.</summary>
/// <param name="RequestId">Id duy nhất của request để ghép các event thành một trace.</param>
/// <param name="Stage">Giai đoạn của event.</param>
/// <param name="Model">Model đang xử lý.</param>
/// <param name="Route">Route đã chọn — <see langword="null"/> khi chưa xác định.</param>
/// <param name="Attempt">Số lần thử (bắt đầu từ 1) — <see langword="null"/> khi chưa có attempt.</param>
/// <param name="Status">HTTP status trả về — <see langword="null"/> khi chưa có response.</param>
/// <param name="Success">Kết quả có thành công không — <see langword="null"/> khi chưa kết luận.</param>
/// <param name="FailureKind">Nhóm lỗi (vd. timeout, rate_limit) khi thất bại.</param>
/// <param name="At">Thời điểm event xảy ra.</param>
/// <param name="Mode">Chế độ dispatch (vd. direct, balancer) — tùy chọn.</param>
/// <param name="AttemptDone">Attempt này đã kết thúc chưa — dùng cho UI hiển thị tiến trình.</param>
/// <param name="Priority">Priority hiện tại của request — gửi ở Received + update khi bị demote (spec G3).</param>
/// <param name="Endpoint">Endpoint gốc ("chat" | "responses") — chỉ ở event đầu (spec G5).</param>
/// <param name="HeadersSent">Header 200 đã commit (early-headers, stream only) — spec G2.</param>
/// <param name="ClientKeyId">Client key id đã auth — chỉ điền ở event Received đầu; journal writer
/// carry qua active map để ghi thống kê theo key (spec dashboard-stats §3.1).</param>
public sealed record TraceEvent(
    string RequestId, TraceStage Stage, string Model, TraceRoute? Route,
    int? Attempt, int? Status, bool? Success, string? FailureKind,
    DateTimeOffset At, string? Mode = null, bool? AttemptDone = null,
    RequestPriority? Priority = null, string? Endpoint = null, bool? HeadersSent = null,
    long? ClientKeyId = null);

/// <summary>Feed sự kiện trace in-process: publisher (Task 3/4) đẩy, UI (Task 5) subscribe.</summary>
public interface ITraceFeed
{
    /// <summary>Phát sau mỗi lần <see cref="Publish"/> thành công — UI re-render.</summary>
    event Action<TraceEvent>? Published;

    /// <summary>Ghi một event vào ring + active map. <b>Không bao giờ ném exception ra caller</b> (contract).</summary>
    /// <param name="e">Event cần ghi.</param>
    void Publish(TraceEvent e);

    /// <summary>Ảnh chụp ring hiện tại — tối đa 200 event, cũ nhất bị drop trước.</summary>
    /// <returns>Danh sách event theo thứ tự cũ → mới.</returns>
    IReadOnlyList<TraceEvent> Snapshot();

    /// <summary>Xóa toàn bộ state (ring + active map) — dùng khi reset UI/test.</summary>
    void PurgeAll();
}
