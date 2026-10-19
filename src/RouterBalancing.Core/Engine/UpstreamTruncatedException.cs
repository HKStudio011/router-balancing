namespace RouterBalancing.Core.Engine;

/// <summary>
/// Upstream đóng KẾT NỐI SẠCH (EOF, không RST/exception) khi SSE chưa gặp dấu hiệu kết
/// thúc hợp lệ (<c>[DONE]</c> hoặc chunk <c>finish_reason</c> khác null) — stream CỤT giữa
/// chừng, tool_call arguments có thể còn dở. Kế thừa <see cref="IOException"/> để đi cùng
/// nhánh xử lý lỗi mạng: pre-content → bọc marker (retry/failover), mid-content →
/// dispatcher map Aborted → endpoint ghi in-band <c>upstream_disconnected</c> để client
/// discard tool-call partial thay vì parse JSON dở dang.
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
