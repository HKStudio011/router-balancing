namespace RouterBalancing.Core.Domain;

/// <summary>Chiến lược cân bằng tải của một combo.</summary>
public enum ComboMode
{
    /// <summary>
    /// Chia đều request cho các model trong combo (lọc eligible, sort min-in-flight, cursor);
    /// lỗi retryable → rotate sang candidate kế (RR quay vòng, khác Fallback theo thứ tự
    /// Position — dispatcher re-select RR sau MarkTried).
    /// </summary>
    RoundRobin = 0,

    /// <summary>Đi theo thứ tự combo; lỗi retryable → chuyển model kế tiếp.</summary>
    Fallback = 1,
}
