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

    /// <summary>
    /// Phát entry cho từng subscriber, cách ly lỗi và báo lỗi handler vào store.
    /// </summary>
    /// <param name="entry">Entry vừa persist, hoặc dòng lỗi handler đang được phát lại.</param>
    /// <param name="skip">
    /// Handler đã ném khi xử lý entry nguồn — bỏ qua khi phát lại dòng lỗi để không gọi lại
    /// đúng handler vừa hỏng (tránh nhân đôi noise).
    /// </param>
    /// <param name="notifyFailure">
    /// <see langword="false"/> khi đang phát lại dòng lỗi: lỗi phát sinh tiếp vẫn được persist
    /// nhưng không phát lại nữa — chặn đệ quy.
    /// </param>
    private void NotifySubscribers(LogEntry entry, Delegate? skip = null, bool notifyFailure = true)
    {
        var handlers = LogAdded;
        if (handlers is null) return;

        List<(LogEntry Failure, Delegate Handler)>? failures = null;
        // Cách ly từng handler: multicast delegate dừng ở handler ném lỗi đầu tiên,
        // và subscriber (VD Log panel gọi sai thread) có thể ném exception làm hỏng
        // request đang gọi Write. Lỗi handler được ghi vào store (không nuốt) bằng
        // Persist (KHÔNG gọi Write) để tránh đệ quy vô hạn.
        foreach (var handler in handlers.GetInvocationList())
        {
            if (ReferenceEquals(handler, skip)) continue;
            try
            {
                ((Action<LogEntry>)handler)(entry);
            }
            catch (Exception ex)
            {
                var failure = new LogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Severity = LogSeverity.Error,
                    Category = LogCategory.App,
                    Message = $"LogAdded subscriber {handler.Method.Name} threw while handling '{entry.Message}'",
                    // Giữ stack trace để chẩn đoán từ Log panel — không nuốt exception
                    Details = ex.ToString(),
                    ErrorCode = ex.GetType().Name,
                };
                Persist(failure);
                (failures ??= []).Add((failure, handler));
            }
        }

        if (!notifyFailure || failures is null) return;
        // Phát lại dòng lỗi cho các subscriber CÒN LẠI: ErrorAlertAggregator chỉ nghe LogAdded,
        // nên nếu chỉ Persist thì lỗi App/Error của handler mất alert. Bỏ qua chính handler vừa
        // ném và không phát lại lỗi tầng 2 (notifyFailure=false) — đệ quy tối đa 1 tầng.
        foreach (var (failure, handler) in failures)
            NotifySubscribers(failure, skip: handler, notifyFailure: false);
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
