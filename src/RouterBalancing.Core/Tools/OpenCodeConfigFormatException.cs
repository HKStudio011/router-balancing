namespace RouterBalancing.Core.Tools;

/// <summary>
/// File config opencode hiện có không dùng được (JSON hỏng, root hoặc <c>providers</c> sai kiểu,
/// block sau khi splice không hợp lệ) — tầng ghi phải báo lỗi rõ và KHÔNG đụng file gốc (spec §9.8).
/// </summary>
public sealed class OpenCodeConfigFormatException : Exception
{
    /// <inheritdoc/>
    public OpenCodeConfigFormatException(string message) : base(message)
    {
    }

    /// <inheritdoc/>
    public OpenCodeConfigFormatException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
