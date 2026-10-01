using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Logging;

/// <summary>Ghi/truy vấn nhật ký app + request; phát sự kiện khi có dòng mới.</summary>
public interface ILogService
{
    /// <summary>Phát sau mỗi lần ghi thành công — Log panel re-render.</summary>
    event Action<LogEntry>? LogAdded;

    /// <summary>Ghi một dòng (Timestamp được set nếu đang là default).</summary>
    void Write(LogEntry entry);

    void Info(string message, LogCategory category = LogCategory.App);

    void Warn(string message, LogCategory category = LogCategory.App);

    /// <summary>Ghi lỗi — stack trace của <paramref name="exception"/> đưa vào Details.</summary>
    void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App);

    /// <summary>Ghi dòng Request ghi lại usage token từ upstream (spec client-keys §6.2).</summary>
    /// <param name="requestId">Request id 8 ký tự — null nếu không có (request ngoài pipeline).</param>
    /// <param name="clientKeyId">Client key đã dùng — null khi auth đang mở.</param>
    void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens);

    IReadOnlyList<LogEntry> Query(LogQuery query);

    /// <summary>Tổng dòng khớp bộ lọc — dùng cho pagination.</summary>
    int Count(LogQuery query);
}
