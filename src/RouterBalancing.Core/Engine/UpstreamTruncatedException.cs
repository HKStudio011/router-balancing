namespace RouterBalancing.Core.Engine;

/// <summary>
/// Upstream đóng KẾT NỐI SẠCH (EOF, không RST/exception) khi SSE chưa gặp dấu hiệu kết
/// thúc hợp lệ (<c>[DONE]</c> hoặc chunk <c>finish_reason</c> khác null) VÀ chưa có byte
/// nào tới client — marker pre-content để retry/failover (tool_call arguments có thể còn dở).
/// Kế thừa <see cref="IOException"/> để đi cùng nhánh xử lý lỗi mạng của marker.
/// Mid-content (đã có byte) không còn dùng exception này: upstream tự đóng stream được coi
/// là kết thúc bình thường — parity client nối trực tiếp, xem cờ
/// <c>UsageCapture.TeeResult.EndedWithoutTerminal</c>; chỉ lỗi đọc thật (RST) mới propagate
/// để dispatcher map Aborted → in-band <c>upstream_disconnected</c>.
/// </summary>
internal sealed class UpstreamTruncatedException : IOException
{
    /// <summary>Tạo exception phát hiện SSE cụt tại EOF sạch.</summary>
    public UpstreamTruncatedException()
        : base("Upstream SSE stream ended cleanly without [DONE] or finish_reason — "
               + "response truncated mid-stream (partial tool_call arguments possible).")
    {
    }
}
