using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Logging;

/// <summary>Bộ lọc truy vấn Log panel — Page bắt đầu từ 1.</summary>
public sealed record LogQuery(
    LogSeverity? MinSeverity = null,
    LogCategory? Category = null,
    string? Search = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 100);
