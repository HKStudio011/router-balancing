namespace RouterBalancing.Core.Engine;

/// <summary>
/// Kết quả dispatch 1 request — endpoint ghi response theo outcome
/// (Handled/Aborted không ghi gì; Error/Cancelled endpoint ghi JSON — spec §2.1).
/// Abstract record (không enum) vì <see cref="Error"/> cần mang payload.
/// </summary>
public abstract record DispatchOutcome
{
    /// <summary>Handler đã ghi response (stream/JSON pass-through) — endpoint không làm gì thêm.</summary>
    public sealed record Handled : DispatchOutcome;

    /// <summary>Request bị huỷ qua cancel API — endpoint ghi 400 <c>request_cancelled</c>.</summary>
    public sealed record Cancelled : DispatchOutcome;

    /// <summary>Client đã ngắt — endpoint không ghi gì (response đã đóng).</summary>
    public sealed record Aborted : DispatchOutcome;

    /// <summary>Lỗi cần endpoint ghi JSON lỗi OpenAI-style.</summary>
    /// <param name="Status">HTTP statuscode.</param>
    /// <param name="Message">Nội dung <c>error.message</c> (tiếng Anh, y như 3A).</param>
    /// <param name="Type">Nội dung <c>error.type</c>.</param>
    /// <param name="Param">Nội dung <c>error.param</c> (nullable).</param>
    /// <param name="Code">Nội dung <c>error.code</c> (nullable).</param>
    public sealed record Error(int Status, string Message, string Type, string? Param, string? Code)
        : DispatchOutcome;

    /// <summary>
    /// Lỗi retryable (429/408/5xx/lỗi mạng) — tín hiệu NỘI BỘ, chỉ dispatcher nhìn thấy;
    /// luôn được convert trước khi outcome về endpoint (spec 3C §2.2).
    /// </summary>
    /// <param name="Status"><see langword="null"/> = lỗi mạng (không có HTTP response nào).</param>
    /// <param name="ContentType">Content-Type upstream trả (<see langword="null"/> khi lỗi mạng).</param>
    /// <param name="Body">Body đã buffer — response lỗi nhỏ, chưa commit (rỗng khi lỗi mạng).</param>
    /// <param name="RetryAfter"><c>Retry-After</c> đã parse — forward vào <c>Passthrough.RetryAfterHeader</c> khi exhaustion (§3.3).</param>
    public sealed record Retryable(int? Status, string? ContentType, byte[] Body, TimeSpan? RetryAfter)
        : DispatchOutcome;

    /// <summary>
    /// Lỗi fatal (401/403/404/mạng) — tín hiệu NỘI BỘ: dispatcher advance như
    /// <see cref="Retryable"/>, mang <see cref="Level"/> để failover theo đúng cấp
    /// Account/Provider/Model (spec exhaustive-failover §3.1); chỉ dispatcher nhìn thấy.
    /// </summary>
    /// <param name="Level">Cấp failover — account/provider/model nào bị loại sau lỗi này.</param>
    /// <param name="Status"><see langword="null"/> = lỗi mạng (không có HTTP response nào).</param>
    /// <param name="ContentType">Content-Type upstream trả (<see langword="null"/> khi lỗi mạng).</param>
    /// <param name="Body">Body đã buffer — giữ nguyên cho exhaustion passthrough (§4).</param>
    /// <param name="RetryAfter"><c>Retry-After</c> đã parse (null khi không có).</param>
    public sealed record Fatal(FailoverLevel Level, int? Status, string? ContentType,
        byte[] Body, TimeSpan? RetryAfter) : DispatchOutcome;

    /// <summary>
    /// Response cần endpoint ghi NGUYÊN status + content-type + body (passthrough byte —
    /// exhaustion §3.3 và 4xx non-retryable, quan sát client y hệt 3A).
    /// </summary>
    /// <param name="RetryAfterHeader">Giá trị thô <c>Retry-After</c> copy lại cho client (null khi không có).</param>
    public sealed record Passthrough(int Status, string? ContentType, byte[] Body, string? RetryAfterHeader)
        : DispatchOutcome;
}
