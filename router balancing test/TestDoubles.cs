using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Server;

namespace router_balancing_test;

/// <summary>ILogService rỗng — test không cần ghi log thật.</summary>
internal sealed class NullLog : ILogService
{
    public event Action<LogEntry>? LogAdded { add { } remove { } }
    public void Write(LogEntry entry) { }
    public void Info(string message, LogCategory category = LogCategory.App) { }
    public void Warn(string message, LogCategory category = LogCategory.App) { }
    public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) { }
    public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) { }
    public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
    public int Count(LogQuery query) => 0;
}

/// <summary>IClientKeyUsageSink rỗng — unit test không cần DB/thật.</summary>
internal sealed class NullUsageSink : IClientKeyUsageSink
{
    public Task RecordAsync(long? clientKeyId, int promptTokens, int completionTokens,
        CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Pool luôn trả direct — các test cũ không phụ thuộc behavior proxy.</summary>
public sealed class DirectProxyPool : IProxyPool
{
    public ProxyAttempt? GetNext() => null;

    public bool ReportFailure(long proxyId) => false;

    public void ReportSuccess(long proxyId)
    {
    }

    public void Invalidate()
    {
    }

    public IReadOnlyList<ProxyRuntimeStatus> Snapshot() => [];
}
