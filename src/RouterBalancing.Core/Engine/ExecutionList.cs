using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Dictionary id → entry dưới 1 lock (check-then-add atomic với Exit) —
/// local app, contention thấp nên lock đơn giản hơn ConcurrentDictionary + Interlocked.
/// </summary>
public sealed class ExecutionList(IDbContextFactory<RouterBalancingDbContext> db) : IExecutionList
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ExecutionEntry> _entries = new();

    /// <inheritdoc/>
    public event Action? Exited;

    /// <inheritdoc/>
    public async Task<bool> CanEnterAsync(long providerId, CancellationToken ct)
    {
        var max = await MaxConcurrentAsync(providerId, ct);
        lock (_lock)
            return CountInFlight(providerId) < max;
    }

    /// <inheritdoc/>
    public async Task<bool> TryEnterAsync(long providerId, string requestId, string providerName,
        string modelId, RequestPriority priority, DateTimeOffset enqueuedAt, CancellationToken ct)
    {
        // Query tại mỗi lần Enter — chỉnh MaxConcurrent trong UI có hiệu lực ngay (spec §2.1)
        var max = await MaxConcurrentAsync(providerId, ct);
        lock (_lock)
        {
            if (CountInFlight(providerId) >= max)
                return false;
            _entries[requestId] = new ExecutionEntry(
                requestId, providerId, providerName, modelId, priority, enqueuedAt, DateTimeOffset.UtcNow);
            return true;
        }
    }

    /// <inheritdoc/>
    public void Exit(string requestId)
    {
        lock (_lock)
        {
            if (!_entries.Remove(requestId))
                return;
        }
        Exited?.Invoke();
    }

    /// <inheritdoc/>
    public bool Contains(string requestId)
    {
        lock (_lock)
            return _entries.ContainsKey(requestId);
    }

    /// <inheritdoc/>
    public int GetInFlight(long providerId)
    {
        lock (_lock)
            return CountInFlight(providerId);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ExecutionEntry> Snapshot()
    {
        lock (_lock)
            return _entries.Values.ToList();
    }

    private int CountInFlight(long providerId) =>
        _entries.Values.Count(e => e.ProviderId == providerId);

    /// <summary>Provider không tồn tại → FirstOrDefault = 0 → không ai enter được (tránh serve vào provider đã xoá).</summary>
    private async Task<int> MaxConcurrentAsync(long providerId, CancellationToken ct)
    {
        using var context = await db.CreateDbContextAsync(ct);
        return await context.Providers.AsNoTracking()
            .Where(p => p.Id == providerId)
            .Select(p => p.MaxConcurrent)
            .FirstOrDefaultAsync(ct);
    }
}
