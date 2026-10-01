namespace RouterBalancing.Core.Domain;

/// <summary>Mức log — cho filter Log panel và ngưỡng cảnh báo stats.</summary>
public enum LogSeverity
{
    /// <summary>Dưới Info — filter mặc định ẩn; dùng cho telemetry (usage degrade, spec client-keys §6).</summary>
    Debug = -1,
    Info = 0,
    Warning = 1,
    Error = 2,
}
