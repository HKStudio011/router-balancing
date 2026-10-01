namespace RouterBalancing.Core.Domain;

/// <summary>
/// Một dòng nhật ký — dùng chung cho app log (Category=App) và
/// request journal/stats (Category=Request) để chỉ cần một bảng.
/// </summary>
public class LogEntry
{
    public long Id { get; set; }

    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

    public LogSeverity Severity { get; set; }

    public LogCategory Category { get; set; }

    public string Message { get; set; } = string.Empty;

    public long? ProviderId { get; set; }

    public long? ModelId { get; set; }

    /// <summary>Id 8 ký tự của request proxy (RequestId.New) - correlate các dòng cùng request.</summary>
    public string? RequestId { get; set; }

    /// <summary>Thời gian xử lý (ms) — chỉ Category=Request.</summary>
    public int? DurationMs { get; set; }

    public int? PromptTokens { get; set; }

    public int? CompletionTokens { get; set; }

    /// <summary>Client key đã dùng request này - null khi request đi qua khi auth đang mở.</summary>
    public long? ClientKeyId { get; set; }

    public string? ErrorCode { get; set; }

    /// <summary>JSON chi tiết (error body, stack…) — không chứa secret.</summary>
    public string? Details { get; set; }
}
