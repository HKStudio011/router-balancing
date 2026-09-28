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
}
