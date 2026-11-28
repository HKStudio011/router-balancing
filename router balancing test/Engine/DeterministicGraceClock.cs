using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

/// <summary>
/// Clock chỉ dùng cho test grace: delay làm time monotonic tiến ngay nhưng vẫn yield để
/// cancellation/read-count assertions có cơ hội chạy. Không phụ thuộc wall-clock timing.
/// </summary>
internal sealed class DeterministicGraceClock : IGraceClock
{
    private readonly List<TimeSpan> _delays = [];
    private long _timestamp;

    public long GetTimestamp() => _timestamp;

    public TimeSpan GetElapsedTime(long timestamp) =>
        TimeSpan.FromTicks(_timestamp - timestamp);

    internal IReadOnlyList<TimeSpan> Delays => _delays;

    public async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));

        _delays.Add(delay);
        _timestamp += delay.Ticks;
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
