namespace RouterBalancing.Core.Engine;

/// <summary>
/// Anchor ngữ nghĩa của một node trên sơ đồ trace — UI map sang pixel
/// (Attempt cần layout chip nên resolve ở component, xem <c>RequestTrace.AnchorOf</c>).
/// </summary>
public enum TraceAnchor
{
    /// <summary>Hàng đợi — request đang chờ hoặc bị đẩy về chờ slot (Received, Parked).</summary>
    Queue,

    /// <summary>Nút Thực thi — vừa bắt đầu chọn provider/combo (DispatchStarted).</summary>
    Dispatch,

    /// <summary>Cụm chip Account của route — đang thử gửi upstream (Attempt).</summary>
    Attempt,

    /// <summary>Nút Client — kết thúc (Finished, Canceled).</summary>
    Client,
}

/// <summary>Trạng thái hiển thị của một request trên sơ đồ — do <see cref="TraceNodeAggregator"/> quản lý.</summary>
public sealed class TraceNodeState
{
    /// <summary>Id request — khóa trong <see cref="TraceNodeAggregator.Nodes"/>.</summary>
    public required string Id { get; init; }

    /// <summary>Model đang xử lý.</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Giai đoạn hiện tại — merge rule spec §2.5 xem <see cref="TraceNodeAggregator.Apply"/>.</summary>
    public TraceStage Stage { get; set; } = TraceStage.Received;

    /// <summary>Route đã chọn — giữ giá trị đã biết khi event mang <see langword="null"/>.</summary>
    public TraceRoute? Route { get; set; }

    /// <summary>Kết quả có thành công không — <see langword="null"/> khi chưa kết luận.</summary>
    public bool? Success { get; set; }

    /// <summary>HTTP status trả về — <see langword="null"/> khi chưa có response.</summary>
    public int? Status { get; set; }

    /// <summary>Chế độ dispatch (vd. direct, balancer) — tùy chọn.</summary>
    public string? Mode { get; set; }

    /// <summary>Priority hiện tại — merge từ G3; UI map sang tag màu (spec E).</summary>
    public RequestPriority? Priority { get; set; }

    /// <summary>Endpoint gốc ("chat" | "responses") — chỉ ở event đầu (spec G5).</summary>
    public string? Endpoint { get; set; }

    /// <summary>Header 200 đã commit (early-headers, stream only) — spec G2.</summary>
    public bool? HeadersSent { get; set; }

    /// <summary>Thời điểm node được dựng từ event đầu tiên — thứ tự vẽ dot.</summary>
    public DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Hạn xử lý — node terminal sống thêm <c>terminalTtl</c> rồi fade; <see langword="null"/> = sống mãi.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
}
