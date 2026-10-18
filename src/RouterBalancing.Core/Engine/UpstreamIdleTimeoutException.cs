namespace RouterBalancing.Core.Engine;

/// <summary>
/// Upstream không gửi byte nào trong suốt <see cref="IdleTimeout"/> — watchdog idle-read ném khi
/// timer của <see cref="IdleWatchdogStream"/> cắt một read đang treo. Kế thừa <see cref="IOException"/>
/// để mọi vòng đọc (UsageCapture/protocol) map đúng nhóm lỗi mạng: pre-content → bọc
/// <see cref="UpstreamDiedBeforeContentException"/> (retry/failover), mid-content → propagate
/// (dispatcher map Aborted, client nhận in-band <c>upstream_disconnected</c>).
/// Khác <see cref="UpstreamStreamDiedException"/>: ở đây upstream vẫn sống nhưng IM LẶNG quá ngưỡng
/// (đo được bằng <see cref="LastByteAt"/>), không phải chết đột ngột.
/// </summary>
internal sealed class UpstreamIdleTimeoutException : IOException
{
    /// <summary>Ngưỡng idle đã áp dụng (đã scale từ setting giây).</summary>
    public TimeSpan IdleTimeout { get; }

    /// <summary>Thời điểm byte cuối cùng đọc được — nền tảng cho telemetry khoảng lặng.</summary>
    public DateTimeOffset LastByteAt { get; }

    /// <summary>Tạo exception watchdog idle.</summary>
    /// <param name="idleTimeout">Ngưỡng idle đã áp dụng cho lần đọc này.</param>
    /// <param name="lastByteAt">Thời điểm byte đọc được gần nhất.</param>
    public UpstreamIdleTimeoutException(TimeSpan idleTimeout, DateTimeOffset lastByteAt)
        : base($"Upstream sent no data for {idleTimeout.TotalSeconds:0.#}s "
               + $"(last byte at {lastByteAt:O}) — idle watchdog cut the read.")
    {
        IdleTimeout = idleTimeout;
        LastByteAt = lastByteAt;
    }
}
