using System.Diagnostics.CodeAnalysis;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// 3 bucket <see cref="SortedList{TKey,TValue}"/> theo mức ưu tiên, key = Sequence tăng dần
/// (FIFO trong cùng mức); dictionary id để tra nhanh. Mọi mutate dưới 1 lock —
/// Enqueue/TryRemove/SetPriority atomic với nhau (race cancel-vs-Take: ai lấy được lock sau thắng).
/// </summary>
public sealed class RequestQueue : IRequestQueue
{
    private readonly object _lock = new();
    private readonly SortedList<long, ProxyRequest> _highest = [];
    private readonly SortedList<long, ProxyRequest> _high = [];
    private readonly SortedList<long, ProxyRequest> _normal = [];
    private readonly Dictionary<string, ProxyRequest> _byId = new();
    private long _sequence;

    /// <inheritdoc/>
    public event Action? Changed;

    /// <inheritdoc/>
    public bool Enqueue(ProxyRequest request)
    {
        lock (_lock)
        {
            if (_byId.ContainsKey(request.Id))
                return false;
            request.Sequence = ++_sequence;
            request.EnqueuedAt = DateTimeOffset.UtcNow;
            Bucket(request.Priority).Add(request.Sequence, request);
            _byId[request.Id] = request;
            if (request.Priority == RequestPriority.Highest)
                DemoteOtherHighest(keep: request);
        }
        Changed?.Invoke();
        return true;
    }

    /// <inheritdoc/>
    public bool Peek([NotNullWhen(true)] out ProxyRequest? request)
    {
        lock (_lock)
        {
            if (TryPeek(_highest, out request) || TryPeek(_high, out request) || TryPeek(_normal, out request))
                return true;
            request = null;
            return false;
        }
    }

    /// <inheritdoc/>
    public bool Take(string id, [NotNullWhen(true)] out ProxyRequest? request) =>
        Remove(id, fireChanged: false, out request);

    /// <inheritdoc/>
    public bool TryRemove(string id, [NotNullWhen(true)] out ProxyRequest? request) =>
        Remove(id, fireChanged: true, out request);

    private bool Remove(string id, bool fireChanged, [NotNullWhen(true)] out ProxyRequest? request)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var removed))
            {
                request = null;
                return false;
            }
            _byId.Remove(id);
            Bucket(removed.Priority).Remove(removed.Sequence);
            request = removed;
        }
        if (fireChanged)
            Changed?.Invoke();
        return true;
    }

    /// <inheritdoc/>
    public bool SetPriority(string id, RequestPriority priority)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var request))
                return false;
            if (request.Priority == priority)
                return true;
            Bucket(request.Priority).Remove(request.Sequence);
            request.Priority = priority;
            Bucket(priority).Add(request.Sequence, request);
            // Thăng cấp lên Highest → hạ cấp các Highest khác (luật 1-Highest có hiệu lực cả ở đây)
            if (priority == RequestPriority.Highest)
                DemoteOtherHighest(keep: request);
        }
        Changed?.Invoke();
        return true;
    }

    /// <inheritdoc/>
    public bool Contains(string id)
    {
        lock (_lock)
            return _byId.ContainsKey(id);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProxyRequest> Snapshot()
    {
        lock (_lock)
        {
            List<ProxyRequest> all = [.. _highest.Values, .. _high.Values, .. _normal.Values];
            return all;
        }
    }

    private void DemoteOtherHighest(ProxyRequest keep)
    {
        // Copy key trước khi sửa — không sửa SortedList khi đang enumerate
        var demote = _highest.Keys.Where(k => k != keep.Sequence).ToList();
        foreach (var key in demote)
        {
            var item = _highest[key];
            _highest.Remove(key);
            item.Priority = RequestPriority.High;
            _high.Add(key, item);
        }
    }

    private SortedList<long, ProxyRequest> Bucket(RequestPriority priority) => priority switch
    {
        RequestPriority.Highest => _highest,
        RequestPriority.High => _high,
        _ => _normal,
    };

    private static bool TryPeek(SortedList<long, ProxyRequest> bucket,
        [NotNullWhen(true)] out ProxyRequest? request)
    {
        if (bucket.Count > 0)
        {
            request = bucket.Values[0];
            return true;
        }
        request = null;
        return false;
    }
}
