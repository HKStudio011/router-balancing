using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Dictionary id → entry dưới 1 lock (check-then-add atomic với Exit) —
/// local app, contention thấp nên lock đơn giản hơn ConcurrentDictionary + Interlocked.
/// </summary>
public sealed class ExecutionList(IDbContextFactory<RouterBalancingDbContext> db,
    IManualRetryStore store) : IExecutionList
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ExecutionEntry> _entries = new();

    // Cursor round-robin per-provider cho nhóm TK cùng in-flight — tăng dưới _lock
    // (TryEnter giữ lock trọn vòng chọn) nên không cần volatile/Interlocked.
    private readonly Dictionary<long, long> _accountCursor = new();

    /// <inheritdoc/>
    public event Action? Exited;

    /// <inheritdoc/>
    public async Task<bool> CanEnterAsync(long providerId, CancellationToken ct)
    {
        var capacity = await LoadCapacityAsync(providerId, ct);
        if (capacity is null)
            return false; // provider không tồn tại — không bao giờ enter (D-B7)
        if (capacity.Value.Accounts.Count == 0)
        {
            // Sentinel (V1): 0 TK enabled hoặc mọi TK enabled đã parked → TryEnter vẫn tạo
            // entry AccountId=0 để forward 503; park ở đây sẽ treo vĩnh viễn vì không có
            // wake signal nào khi user bật lại TK / unpark (Unpark chỉ fire Changed)
            return true;
        }
        lock (_lock)
            return capacity.Value.Accounts.Any(a =>
                HasCapacity(capacity.Value.Max, providerId, a.Id));
    }

    /// <inheritdoc/>
    public async Task<long?> TryEnterAsync(long providerId, string requestId, string providerName,
        string modelId, RequestPriority priority, DateTimeOffset enqueuedAt, CancellationToken ct)
    {
        // Query tại mỗi lần Enter — chỉnh MaxConcurrent trong UI có hiệu lực ngay (spec §2.1)
        var capacity = await LoadCapacityAsync(providerId, ct);
        if (capacity is null)
            return null; // provider không tồn tại — không serve (D-B7)

        lock (_lock)
        {
            AccountSlot chosen;
            if (capacity.Value.Accounts.Count == 0)
            {
                // Sentinel (V1) — xem chú thích trong CanEnterAsync: 0 TK enabled hoặc
                // mọi TK enabled đã parked → entry AccountId=0 forward 503, không treo
                chosen = new AccountSlot(0, string.Empty);
            }
            else
            {
                // Chọn TK ít in-flight nhất; nhóm tie (cùng in-flight) xoay theo cursor
                // per-provider — request tuần tự không chồng chéo (luôn thấy 0-0) vẫn
                // dàn trải đều thay vì dồn vào 1 TK (bug tie-break Priority→Id cũ).
                var candidates = capacity.Value.Accounts
                    .Where(a => HasCapacity(capacity.Value.Max, providerId, a.Id))
                    .ToList();
                if (candidates.Count == 0)
                    return null; // mọi TK enabled đầy → park, chờ Exited (D-B4.4)
                var minInFlight = candidates.Min(a => CountInFlight(providerId, a.Id));
                var tied = candidates
                    .Where(a => CountInFlight(providerId, a.Id) == minInFlight)
                    .OrderBy(a => a.Id)
                    .ToList();
                _accountCursor.TryGetValue(providerId, out var cursor);
                chosen = tied[(int)(cursor % tied.Count)];
                _accountCursor[providerId] = cursor + 1;
            }

            _entries[requestId] = new ExecutionEntry(
                requestId, providerId, providerName, modelId, priority, enqueuedAt,
                DateTimeOffset.UtcNow, chosen.Id, chosen.Name);
            return chosen.Id;
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

    private int CountInFlight(long providerId, long accountId) =>
        _entries.Values.Count(e => e.ProviderId == providerId && e.AccountId == accountId);

    /// <summary>Max ≤ 0 = không giới hạn (D-B1) — validator UI chặn 0..64 nên âm không tới được từ UI.</summary>
    private bool HasCapacity(int max, long providerId, long accountId) =>
        max <= 0 || CountInFlight(providerId, accountId) < max;

    /// <summary>
    /// Query MaxConcurrent + TK enabled (Id, Name) mới nhất từ DB rồi loại TK
    /// đang parked (store in-memory — không dịch được sang SQL nên filter sau materialize);
    /// provider không tồn tại → null (D-B7 — không dùng FirstOrDefault = 0 vì 0 giờ là "không giới hạn").
    /// </summary>
    private async Task<(int Max, List<AccountSlot> Accounts)?> LoadCapacityAsync(
        long providerId, CancellationToken ct)
    {
        using var context = await db.CreateDbContextAsync(ct);
        var provider = await context.Providers.AsNoTracking()
            .Where(p => p.Id == providerId)
            .Select(p => new
            {
                p.MaxConcurrent,
                Accounts = p.Accounts
                    .Where(a => a.Enabled)
                    .Select(a => new { a.Id, a.Name })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);
        return provider is null
            ? null
            : (provider.MaxConcurrent, provider.Accounts
                .Where(a => !store.IsAccountParked(a.Id))
                .Select(a => new AccountSlot(a.Id, a.Name)).ToList());
    }

    /// <summary>TK enabled trong capacity query — Id/Name cho entry (D-B4).</summary>
    private sealed record AccountSlot(long Id, string Name);
}
