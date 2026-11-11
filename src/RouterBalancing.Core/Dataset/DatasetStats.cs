namespace RouterBalancing.Core.Dataset;

/// <summary>
/// Đếm trạng thái export — hiển thị ở Settings và dùng cho test/monitoring (spec §7.6–7.7).
/// </summary>
/// <param name="RecordsWritten">Số record đã ghi file thành công.</param>
/// <param name="SkippedTooLarge">Bỏ vì body/completion vượt cap 1MB.</param>
/// <param name="SkippedTruncated">Bỏ vì completion cụt giữa stream (không terminal).</param>
/// <param name="SkippedEmpty">Bỏ vì output/completion rỗng hoặc thiếu.</param>
/// <param name="SkippedRefusal">Bỏ vì 2xx nhưng completion là lời từ chối của upstream.</param>
/// <param name="SkippedQueueFull">Bỏ vì channel writer đã đầy (bảo vệ RAM, spec §7.6).</param>
/// <param name="SkippedSynthetic">
/// Bỏ vì completion do sidecar tự tổng hợp (title synthesized) — không đi qua Capture,
/// handler đếm trực tiếp (spec opencode-free-gate-v2 §7.2).
/// </param>
/// <param name="BytesWritten">Tổng byte đã ghi kể từ khi sink khởi động.</param>
/// <param name="CurrentFile">Đường dẫn file đang mở; <see langword="null"/> khi chưa có.</param>
public sealed record DatasetStats(
    int RecordsWritten,
    int SkippedTooLarge,
    int SkippedTruncated,
    int SkippedEmpty,
    int SkippedRefusal,
    int SkippedQueueFull,
    int SkippedSynthetic,
    long BytesWritten,
    string? CurrentFile);
