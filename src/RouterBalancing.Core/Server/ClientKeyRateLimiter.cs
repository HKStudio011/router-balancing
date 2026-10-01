namespace RouterBalancing.Core.Server;

/// <inheritdoc cref="IClientKeyRateLimiter"/>
/// <remarks>
/// Window cố định 60s, state sống trong RAM của tiến trình proxy — đủ vì proxy là 1 instance
/// loopback duy nhất; không cần share đa process. Dùng <see cref="TimeProvider"/> để test rollover.
/// </remarks>
public sealed class ClientKeyRateLimiter(TimeProvider time) : IClientKeyRateLimiter
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromSeconds(60);

    private readonly Dictionary<long, Window> _windows = new();
    private readonly object _gate = new();

    private sealed class Window
    {
        public long Requests;
        public long Tokens;
        public DateTimeOffset StartedAt;
    }

    public (bool Allowed, int RetryAfterSec) TryEnter(long keyId, int? ratePerMinute, int? tokensPerMinute)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var window = GetOrCreate(keyId, now);

            if (tokensPerMinute is int tpm && window.Tokens >= tpm)
                return (false, RetryAfterSec(now, window));

            if (ratePerMinute is int rpm && window.Requests + 1 > rpm)
                return (false, RetryAfterSec(now, window));

            window.Requests++;
            return (true, 0);
        }
    }

    public void AddTokens(long keyId, int tokens)
    {
        lock (_gate)
        {
            GetOrCreate(keyId, time.GetUtcNow()).Tokens += tokens;
        }
    }

    private Window GetOrCreate(long keyId, DateTimeOffset now)
    {
        if (!_windows.TryGetValue(keyId, out var window) || now - window.StartedAt >= WindowLength)
        {
            window = new Window { StartedAt = now };
            _windows[keyId] = window;
        }
        return window;
    }

    // ceil lên tối thiểu 1: Retry-After: 0 khuyến khích client bắn lại ngay — ngược tác dụng chống hammer
    private static int RetryAfterSec(DateTimeOffset now, Window window)
        => Math.Max(1, (int)Math.Ceiling((window.StartedAt + WindowLength - now).TotalSeconds));
}
