namespace RouterBalancing.Core.Domain;

/// <summary>Mức log — cho filter Log panel và ngưỡng cảnh báo stats.</summary>
public enum LogSeverity
{
    /// <summary>
    /// Dưới Info — dùng cho telemetry (usage degrade, spec client-keys §6).
    /// Ẩn khi filter là Info/Warning/Error (threshold ≥ Info); hiển thị khi filter
    /// All — vốn là filter mặc định của Log panel nên Debug hiện bình thường khi mở Logs.
    /// </summary>
    Debug = -1,
    Info = 0,
    Warning = 1,
    Error = 2,
}
