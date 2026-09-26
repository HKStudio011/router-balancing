using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;

namespace router_balancing_test;

/// <summary>ILogService rỗng — test không cần ghi log thật.</summary>
internal sealed class NullLog : ILogService
{
    public event Action<LogEntry>? LogAdded { add { } remove { } }
    public void Write(LogEntry entry) { }
    public void Info(string message, LogCategory category = LogCategory.App) { }
    public void Warn(string message, LogCategory category = LogCategory.App) { }
    public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) { }
    public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
    public int Count(LogQuery query) => 0;
}
