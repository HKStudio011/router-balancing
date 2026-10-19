namespace RouterBalancing.Core.Engine;

/// <summary>
/// Upstream stream chết (RST/ResponseEnded...) trong lúc đang đọc — watchdog bọc lỗi đọc gốc kèm
/// <see cref="SilenceGap"/> (khoảng cách từ byte cuối tới thời điểm chết) để journal phân biệt
/// "chết giữa token" (gap ≈ 0 — peer RST chủ động) với "im lặng kéo dài rồi chết" (gap lớn —
/// origin kill/idle). Kế thừa <see cref="IOException"/> để mọi vòng đọc map đúng nhóm lỗi mạng
/// như lỗi gốc; lỗi gốc giữ ở <see cref="Exception.InnerException"/> cho ErrorCode/stack.
/// </summary>
internal sealed class UpstreamStreamDiedException : IOException
{
    /// <summary>Khoảng lặng giữa byte cuối cùng và thời điểm stream chết.</summary>
    public TimeSpan SilenceGap { get; }

    /// <summary>Tạo exception bọc lỗi đọc gốc từ upstream stream.</summary>
    /// <param name="innerException">Lỗi đọc thật (HttpIOException/SocketException...).</param>
    /// <param name="silenceGap">Khoảng lặng tính từ byte đọc được gần nhất.</param>
    public UpstreamStreamDiedException(Exception innerException, TimeSpan silenceGap)
        : base($"Upstream stream died after {silenceGap.TotalSeconds:0.###}s of silence "
               + "(gap since last byte — 0 ≈ mid-token RST, large ≈ idle kill).", innerException)
    {
        SilenceGap = silenceGap;
    }
}
