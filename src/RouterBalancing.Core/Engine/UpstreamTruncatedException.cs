namespace RouterBalancing.Core.Engine;

/// <summary>
/// Upstream đóng KẾT NỐI SẠCH (EOF, không RST/exception) khi SSE chưa gặp dấu hiệu kết
/// thúc hợp lệ (<c>[DONE]</c> hoặc chunk <c>finish_reason</c> khác null); tool_call arguments
/// có thể còn dở. Pre-content được bọc marker để retry/failover. Mid-content, handler ném
/// exception này khi tee báo <c>UsageCapture.TeeResult.EndedWithoutTerminal</c> để dispatcher
/// map Aborted → in-band <c>upstream_disconnected</c>.
/// Kế thừa <see cref="IOException"/> để đi cùng nhánh xử lý lỗi mạng hiện có.
/// </summary>
internal sealed class UpstreamTruncatedException : IOException
{
    /// <summary>Khoảng lặng từ byte cuối tới EOF, nếu stream đã có content.</summary>
    public TimeSpan? LastByteGap { get; }

    /// <summary>Tạo exception phát hiện SSE cụt tại EOF sạch.</summary>
    /// <param name="lastByteGap">Khoảng lặng từ byte cuối tới EOF; chỉ telemetry, không chứa content.</param>
    public UpstreamTruncatedException(TimeSpan? lastByteGap = null)
        : base("Upstream SSE stream ended cleanly without [DONE] or finish_reason — "
               + "response truncated mid-stream (partial tool_call arguments possible)."
               + (lastByteGap is { } gap ? $" silence_gap={gap.TotalSeconds:0.###}s" : string.Empty))
    {
        LastByteGap = lastByteGap;
    }
}
