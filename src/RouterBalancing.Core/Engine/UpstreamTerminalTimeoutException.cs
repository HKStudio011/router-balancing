namespace RouterBalancing.Core.Engine;

/// <summary>
/// Upstream đã EOF sau content nhưng không có byte mới hoặc terminal trong suốt grace hữu hạn.
/// Đây là lỗi giữa-stream, không phải marker pre-content và không được retry/replay request.
/// </summary>
internal sealed class UpstreamTerminalTimeoutException : IOException
{
    /// <summary>Khoảng grace đã cấu hình trước khi báo timeout.</summary>
    public TimeSpan GracePeriod { get; }

    /// <summary>Tạo lỗi timeout chờ terminal sau grace.</summary>
    public UpstreamTerminalTimeoutException(TimeSpan gracePeriod)
        : base($"Upstream did not resume or send a terminal event within "
               + $"the configured {gracePeriod.TotalSeconds:0.###}s grace period.")
    {
        GracePeriod = gracePeriod;
    }
}
