using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Triển khai <see cref="ITraceFeed"/>: ring buffer cap 200 (drop oldest) + map
/// request chưa terminal — mutation dưới lock, invoke <see cref="Published"/> ngoài lock.
/// </summary>
public sealed class TraceFeed(ILogService log) : ITraceFeed
{
    /// <summary>Số event tối đa ring giữ lại — theo spec Live Request Trace.</summary>
    private const int RingCap = 200;

    private readonly object _gate = new();
    private readonly Queue<TraceEvent> _ring = new();
    private readonly Dictionary<string, TraceEvent> _active = new();

    /// <inheritdoc />
    public event Action<TraceEvent>? Published;

    /// <inheritdoc />
    public void Publish(TraceEvent e)
    {
        // Contract: method này không bao giờ ném exception ra caller — mọi lỗi được
        // nuốt sau khi đã log, vì publisher (dispatcher) không được phép fail vì UI feed.
        try
        {
            lock (_gate)
            {
                _ring.Enqueue(e);
                // Drop oldest: giữ đúng 200 — UI chỉ cần trace gần đây.
                while (_ring.Count > RingCap)
                    _ring.Dequeue();

                // Terminal → remove; còn lại → upsert latest event của request.
                if (e.Stage is TraceStage.Finished or TraceStage.Canceled)
                    _active.Remove(e.RequestId);
                else
                    _active[e.RequestId] = e;
            }

            // Invoke ngoài lock: subscriber (UI render) không được giữ lock — tránh deadlock
            // và không chặn publisher khác.
            Published?.Invoke(e);
        }
        catch (Exception ex)
        {
            // Nếu chính log cũng ném thì vẫn nuốt — không còn chỗ nào report nữa
            // mà không phá contract "Publish không ném ra caller".
            try
            {
                log.Error("TraceFeed: Publish failed", ex);
            }
            catch
            {
                // intentional swallow — see comment above
            }
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<TraceEvent> Snapshot()
    {
        lock (_gate)
            return new List<TraceEvent>(_ring);
    }

    /// <inheritdoc />
    public void PurgeAll()
    {
        lock (_gate)
        {
            _ring.Clear();
            _active.Clear();
        }
    }

    /// <summary>
    /// Ảnh chụp các request chưa terminal (latest event theo RequestId) — internal
    /// để unit test đọc qua InternalsVisibleTo.
    /// </summary>
    internal IReadOnlyList<TraceEvent> ActiveSnapshot()
    {
        lock (_gate)
            return new List<TraceEvent>(_active.Values);
    }
}
