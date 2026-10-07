using RouterBalancing.Core.Proxies;

namespace router_balancing_test.Proxies;

/// <summary>CooldownTracker — G4: proxy down → map accountName → max(DownUntil) cho badge Live Trace.</summary>
public class CooldownTrackerTests
{
    // Khởi tạo trong ctor (không dùng field initializer — CS0236) để At/T sẵn sàng
    // TRƯỚC khi ResolveAsync bắt `now`; nếu lazy-init trong lambda thì At > now → sai filter.
    private readonly DateTimeOffset At;
    private readonly DateTimeOffset T;

    public CooldownTrackerTests()
    {
        At = DateTimeOffset.UtcNow;
        T = At + TimeSpan.FromSeconds(45);
    }

    [Fact]
    public async Task Resolve_UsesAccountProxies_WhenAssigned()
    {
        var tracker = new CooldownTracker(
            () => [new ProxyRuntimeStatus(1, "http://p1", true, T)],
            _ => Task.FromResult<IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>>(
                new Dictionary<long, IReadOnlyList<ProxyUsage>>
                {
                    [1] = [new ProxyUsage(10, "acc1", IsProvider: false, null)],
                }),
            (_, _) => throw new InvalidOperationException("không được gọi"));

        var map = await tracker.ResolveAsync(CancellationToken.None);

        Assert.NotNull(map);
        Assert.Equal(T, map!["acc1"]);
    }

    [Fact]
    public async Task Resolve_FallsBackToProviderProxies()
    {
        var tracker = new CooldownTracker(
            () => [new ProxyRuntimeStatus(1, "http://p1", true, T)],
            _ => Task.FromResult<IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>>(
                new Dictionary<long, IReadOnlyList<ProxyUsage>>
                {
                    [1] = [new ProxyUsage(100, "prov", IsProvider: true, null)],
                }),
            (pid, _) => Task.FromResult<IReadOnlyList<ProxyAssignment>>(
            [
                new() { Id = 100, Name = "prov", IsProvider = true },
                new() { Id = 11, Name = "inherited", ProxyIds = [] },                    // kế thừa
                new() { Id = 12, Name = "explicit", ProxyIds = [99] },                   // proxy 99 không down
            ]));

        var map = await tracker.ResolveAsync(CancellationToken.None);

        Assert.NotNull(map);
        Assert.Equal(T, map!["inherited"]);
        Assert.False(map.ContainsKey("explicit"));
    }

    [Fact]
    public async Task Resolve_ReturnsNone_WhenDirect()
    {
        // Proxy sống / không proxy down → map rỗng (authoritative) — caller clear badge
        var tracker = new CooldownTracker(
            () => [new ProxyRuntimeStatus(1, "http://p1", false, null)],
            _ => throw new InvalidOperationException("không được gọi"),
            (_, _) => throw new InvalidOperationException("không được gọi"));

        var map = await tracker.ResolveAsync(CancellationToken.None);

        Assert.NotNull(map);
        Assert.Empty(map!);
    }

    [Fact]
    public async Task Resolve_FiltersExpiredCooldowns_ReturnsEmpty()
    {
        var tracker = new CooldownTracker(
            () => [new ProxyRuntimeStatus(1, "http://p1", true, At)], // DownUntil <= now
            _ => throw new InvalidOperationException("không được gọi"),
            (_, _) => throw new InvalidOperationException("không được gọi"));

        var map = await tracker.ResolveAsync(CancellationToken.None);

        Assert.NotNull(map);
        Assert.Empty(map!); // countdown hết → badge tự tắt dù pool chưa flip IsDown
    }

    [Fact]
    public async Task SnapshotFailure_ReturnsNone_WithoutThrowing()
    {
        Exception? seen = null;
        var tracker = new CooldownTracker(
            () => throw new IOException("db locked"),
            _ => Task.FromResult<IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>>(new Dictionary<long, IReadOnlyList<ProxyUsage>>()),
            (_, _) => Task.FromResult<IReadOnlyList<ProxyAssignment>>([]),
            ex => seen = ex);

        var map = await tracker.ResolveAsync(CancellationToken.None);

        Assert.Null(map);              // null = giữ map cũ ở caller (spec §3.5)
        Assert.IsType<IOException>(seen);
    }
}
