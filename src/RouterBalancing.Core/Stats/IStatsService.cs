namespace RouterBalancing.Core.Stats;

/// <summary>Truy vấn thống kê lịch sử từ request journal — spec dashboard-stats §3.4.</summary>
public interface IStatsService
{
    /// <summary>
    /// Trả snapshot cho khoảng <paramref name="range"/> — đọc trực tiếp từ journal
    /// (không cache, không rollup; UI tự refresh qua <c>IStatsInvalidationSource</c>).
    /// </summary>
    /// <param name="range">Khoảng preset; cửa sổ UTC luôn kết thúc tại "now".</param>
    /// <param name="ct">Token hủy — truyền vào truy vấn EF.</param>
    /// <returns>Snapshot tổng hợp: volumes, error rate, latency, tokens, buckets, breakdown.</returns>
    Task<StatsSnapshot> GetRangeAsync(StatsRange range, CancellationToken ct = default);
}
