using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Logging;

/// <inheritdoc cref="ILogService"/>
public sealed class LogService : ILogService, IDisposable
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private bool _disposed;

    public event Action<LogEntry>? LogAdded;

    public LogService(IDbContextFactory<RouterBalancingDbContext> db) => _db = db;

    public void Write(LogEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (entry.Timestamp == default) entry.Timestamp = DateTimeOffset.UtcNow;
        using var db = _db.CreateDbContext();
        db.LogEntries.Add(entry);
        db.SaveChanges();
        LogAdded?.Invoke(entry);
    }

    public void Info(string message, LogCategory category = LogCategory.App) =>
        Write(new LogEntry { Severity = LogSeverity.Info, Category = category, Message = message });

    public void Warn(string message, LogCategory category = LogCategory.App) =>
        Write(new LogEntry { Severity = LogSeverity.Warning, Category = category, Message = message });

    public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
        Write(new LogEntry
        {
            Severity = LogSeverity.Error,
            Category = category,
            Message = message,
            // Giữ stack trace để chẩn đoán từ Log panel — không nuốt exception
            Details = exception?.ToString(),
            ErrorCode = exception?.GetType().Name,
        });

    public IReadOnlyList<LogEntry> Query(LogQuery query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var db = _db.CreateDbContext();
        return ApplyTimeFilter(ApplyFilter(db.LogEntries.AsNoTracking(), query).AsEnumerable(), query)
            .OrderByDescending(e => e.Timestamp)
            .ThenByDescending(e => e.Id)
            .Skip((Math.Max(query.Page, 1) - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToList();
    }

    public int Count(LogQuery query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var db = _db.CreateDbContext();
        return ApplyTimeFilter(ApplyFilter(db.LogEntries.AsNoTracking(), query).AsEnumerable(), query).Count();
    }

    private static IQueryable<LogEntry> ApplyFilter(IQueryable<LogEntry> source, LogQuery query)
    {
        if (query.MinSeverity is { } min)
            source = source.Where(e => e.Severity >= min);
        if (query.Category is { } category)
            source = source.Where(e => e.Category == category);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            source = source.Where(e => e.Message.Contains(term));
        }
        return source;
    }

    private static IEnumerable<LogEntry> ApplyTimeFilter(IEnumerable<LogEntry> source, LogQuery query)
    {
        // Phải chạy client-side: EF Core SQLite không dịch được predicate/ORDER BY
        // trên DateTimeOffset ("SQLite does not support expressions of type
        // 'DateTimeOffset'") — để trong IQueryable sẽ ném lúc enumerate.
        if (query.From is { } from)
            source = source.Where(e => e.Timestamp >= from);
        if (query.To is { } to)
            source = source.Where(e => e.Timestamp <= to);
        return source;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
