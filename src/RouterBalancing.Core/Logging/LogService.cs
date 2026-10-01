using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Logging;

/// <inheritdoc cref="ILogService"/>
public sealed class LogService : ILogService, IDisposable
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private volatile bool _disposed;

    public event Action<LogEntry>? LogAdded;

    public LogService(IDbContextFactory<RouterBalancingDbContext> db) => _db = db;

    public void Write(LogEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (entry.Timestamp == default) entry.Timestamp = DateTimeOffset.UtcNow;
        Persist(entry);
        NotifySubscribers(entry);
    }

    private void Persist(LogEntry entry)
    {
        using var db = _db.CreateDbContext();
        db.LogEntries.Add(entry);
        db.SaveChanges();
    }

    private void NotifySubscribers(LogEntry entry)
    {
        var handlers = LogAdded;
        if (handlers is null) return;
        // Cách ly từng handler: multicast delegate dừng ở handler ném lỗi đầu tiên,
        // và subscriber (VD Log panel gọi sai thread) có thể ném exception làm hỏng
        // request đang gọi Write. Lỗi handler được ghi vào store (không nuốt) —
        // gọi Persist (KHÔNG gọi Write) để tránh đệ quy: Write sẽ phát lại LogAdded.
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action<LogEntry>)handler)(entry);
            }
            catch (Exception ex)
            {
                Persist(new LogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Severity = LogSeverity.Error,
                    Category = LogCategory.App,
                    Message = $"LogAdded subscriber {handler.Method.Name} threw while handling '{entry.Message}'",
                    // Giữ stack trace để chẩn đoán từ Log panel — không nuốt exception
                    Details = ex.ToString(),
                    ErrorCode = ex.GetType().Name,
                });
            }
        }
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

    public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) =>
        Write(new LogEntry
        {
            Severity = LogSeverity.Info,
            Category = LogCategory.Request,
            Message = $"Usage từ upstream: {promptTokens} prompt token, {completionTokens} completion token.",
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            RequestId = requestId,
            ClientKeyId = clientKeyId,
        });

    public IReadOnlyList<LogEntry> Query(LogQuery query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var db = _db.CreateDbContext();
        return ApplyFilter(db.LogEntries.AsNoTracking(), query)
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
        return ApplyFilter(db.LogEntries.AsNoTracking(), query).Count();
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
        // Timestamp được converter sang UTC ticks (xem RouterBalancingDbContext)
        // nên WHERE/ORDER BY chạy trọn vẹn trong SQL.
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
