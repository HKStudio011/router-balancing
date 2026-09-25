namespace RouterBalancing.Core.Domain;

/// <summary>Chiến lược cân bằng tải của một combo.</summary>
public enum ComboMode
{
    /// <summary>Chia đều request cho các model trong combo; không cross-model failover.</summary>
    RoundRobin = 0,

    /// <summary>Đi theo thứ tự combo; lỗi retryable → chuyển model kế tiếp.</summary>
    Fallback = 1,
}
