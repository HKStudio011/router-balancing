namespace RouterBalancing.Core.Dataset;

/// <summary>
/// Hợp đồng fail-open của ống export dataset JSONL (spec §7.6): <see cref="Capture"/> chạy trên
/// thread request nên KHÔNG bao giờ ném exception và KHÔNG làm IO — chỉ đọc gate, đo kích thước
/// thô và đẩy tham chiếu vào queue bounded. Mọi việc nặng (parse body, chuẩn hoá responses,
/// serialize, ghi file) chạy trên writer task nền để không nghẽn đường forward (anti-goal spec §4).
/// </summary>
public interface IDatasetSink
{
    /// <summary>Phát khi stats đổi (ghi/bỏ/drop record) — UI Settings re-read <see cref="Stats"/>.</summary>
    event Action Changed;

    /// <summary>
    /// Thu một record sau 2xx thành công — không ném, không IO (contract fail-open).
    /// Export tắt → return ngay; queue đầy hoặc vượt guard byte → drop + đếm
    /// <see cref="DatasetStats.SkippedQueueFull"/>.
    /// </summary>
    /// <param name="capture">Body gốc của client + completion + route/usage (chỉ giữ tham chiếu).</param>
    void Capture(DatasetCapture capture);

    /// <summary>Snapshot đếm hiện tại — counter Interlocked, không lock.</summary>
    DatasetStats Stats { get; }
}
