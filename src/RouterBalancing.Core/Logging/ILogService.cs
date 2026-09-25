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

    IReadOnlyList<LogEntry> Query(LogQuery query);

    /// <summary>Tổng dòng khớp bộ lọc — dùng cho pagination.</summary>
    int Count(LogQuery query);
}
