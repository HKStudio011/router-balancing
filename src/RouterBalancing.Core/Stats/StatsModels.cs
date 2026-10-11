namespace RouterBalancing.Core.Stats;

/// <summary>Một bucket thời gian trên biểu đồ — đếm dòng terminal theo Timestamp (spec §5).</summary>
/// <param name="BucketStart">Đầu bucket, luôn UTC (giờ 00h…23h cho Hôm nay; nửa đêm cho bucket ngày).</param>
/// <param name="Requests">Số dòng terminal trong bucket.</param>
/// <param name="Errors">Số dòng terminal có <c>Outcome=Error</c> trong bucket.</param>
public sealed record StatsBucket(DateTimeOffset BucketStart, int Requests, int Errors);

/// <summary>Một dòng breakdown theo key Provider/Model/Client key — sort Requests desc (spec §3.4).</summary>
/// <param name="Key">Nhóm key; "—" cho giá trị NULL (request hủy trước khi chọn route…).</param>
/// <param name="Requests">Số dòng terminal trong nhóm.</param>
/// <param name="Errors">Số dòng terminal lỗi trong nhóm — Cancelled tính Requests nhưng không tính Errors.</param>
/// <param name="Tokens">Tổng prompt+completion join từ dòng usage theo RequestId.</param>
public sealed record StatsBreakdownRow(string Key, int Requests, int Errors, long Tokens);

/// <summary>Snapshot thống kê một cửa sổ thời gian — spec dashboard-stats §3.4.</summary>
/// <param name="From">Đầu cửa sổ (nửa đêm UTC hoặc UTC −6/−29 ngày).</param>
/// <param name="To">Cuối cửa sổ = "now" tại thời điểm query (không đóng cửa sổ cuối ngày).</param>
/// <param name="Requests">Tổng dòng terminal trong cửa sổ.</param>
/// <param name="Ok">Số dòng terminal có <c>Outcome=Ok</c>.</param>
/// <param name="Errors">Số dòng terminal có <c>Outcome=Error</c>.</param>
/// <param name="Cancelled">Số dòng terminal có <c>Outcome=Cancelled</c> — không tham gia ErrorRate.</param>
/// <param name="ErrorRatePercent">
/// <c>Errors / (Ok + Errors) × 100</c> — Cancelled không vào denominator (hủy là chủ đích
/// client, đồng bộ ApiMonitorPanel); 0 khi mẫu rỗng.
/// </param>
/// <param name="AvgLatencyMs">Trung bình DurationMs của dòng Ok (làm tròn nearest); null khi không có mẫu.</param>
/// <param name="P95LatencyMs">p95 nearest-rank của dòng Ok; null khi không có mẫu.</param>
/// <param name="PromptTokens">Tổng PromptTokens của dòng usage trong cửa sổ.</param>
/// <param name="CompletionTokens">Tổng CompletionTokens của dòng usage trong cửa sổ.</param>
/// <param name="Buckets">24 bucket giờ UTC (Hôm nay) hoặc 7/30 bucket ngày UTC; bucket rỗng trả 0.</param>
/// <param name="ByProvider">Breakdown theo <c>ProviderKey ?? "—"</c>, sort Requests desc.</param>
/// <param name="ByModel">Breakdown theo <c>ModelKey ?? "—"</c>, sort Requests desc.</param>
/// <param name="ByClientKey">Breakdown theo <c>ClientKeyId?.ToString() ?? "—"</c>, sort Requests desc.</param>
public sealed record StatsSnapshot(
    DateTimeOffset From, DateTimeOffset To,
    int Requests, int Ok, int Errors, int Cancelled,
    double ErrorRatePercent, int? AvgLatencyMs, int? P95LatencyMs,
    long PromptTokens, long CompletionTokens,
    IReadOnlyList<StatsBucket> Buckets,
    IReadOnlyList<StatsBreakdownRow> ByProvider,
    IReadOnlyList<StatsBreakdownRow> ByModel,
    IReadOnlyList<StatsBreakdownRow> ByClientKey);
