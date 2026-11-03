namespace router_balancing_test;

/// <summary>
/// Budget chờ rendezvous giữa test và pipeline (queue/upstream/dispatcher). Full suite chạy song
/// song có thể làm thread pool/IO đói vài giây — budget 5s cũ flake (TimeoutException tại
/// <c>WaitAsync</c> dù pipeline không deadlock). 30s vẫn fail nếu có deadlock thật.
/// </summary>
internal static class TestTimeouts
{
    /// <summary>Thời gian tối đa chờ một sự kiện pipeline/rendezvous trong test.</summary>
    public static readonly TimeSpan Rendezvous = TimeSpan.FromSeconds(30);
}
