using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Proxies;

public class ProxyPoolTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public ProxyPoolTests()
    {
        _factory = _db.CreateFactory();
        DbInitializer.Initialize(_factory);
    }

    public void Dispose() => _db.Dispose();

    private async Task<long> AddProxyAsync(
        string host = "127.0.0.1", int port = 8080, string scheme = "http",
        bool enabled = true, string? username = null, string? password = null)
    {
        using var db = _factory.CreateDbContext();
        var row = new OutboundProxy
        {
            Scheme = scheme,
            Host = host,
            Port = port,
            Enabled = enabled,
            Username = username,
            PasswordEncrypted = password is null ? null : _protector.Protect(password),
        };
        db.OutboundProxies.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private ProxyPool CreatePool(TimeProvider? time = null) =>
        new(_factory, _protector, time ?? TimeProvider.System, new NullLog());

    [Fact]
    public async Task GetNext_TwoProxies_RoundRobinsInIdOrder()
    {
        await AddProxyAsync(port: 8001);
        await AddProxyAsync(port: 8002);
        var pool = CreatePool();

        var first = pool.GetNext();
        var second = pool.GetNext();
        var third = pool.GetNext();

        Assert.Equal("http://127.0.0.1:8001", first!.Endpoint);
        Assert.Equal("http://127.0.0.1:8002", second!.Endpoint);
        Assert.Equal("http://127.0.0.1:8001", third!.Endpoint);
    }

    [Fact]
    public async Task GetNext_SkipsDisabledProxy()
    {
        await AddProxyAsync(port: 8001, enabled: false);
        await AddProxyAsync(port: 8002);
        var pool = CreatePool();

        Assert.Equal("http://127.0.0.1:8002", pool.GetNext()!.Endpoint);
        Assert.Equal("http://127.0.0.1:8002", pool.GetNext()!.Endpoint); // chỉ 1 proxy sống
    }

    [Fact]
    public void GetNext_EmptyPool_ReturnsNull() => Assert.Null(CreatePool().GetNext());

    [Fact]
    public async Task GetNext_AllProxiesDown_ReturnsNull()
    {
        var id = await AddProxyAsync();
        var pool = CreatePool();

        Assert.True(pool.ReportFailure(id));
        Assert.Null(pool.GetNext());
    }

    [Fact]
    public async Task GetNext_DownProxy_RecoversAfterCooldown()
    {
        var id = await AddProxyAsync();
        var time = new FakeTime(Start);
        var pool = CreatePool(time);
        pool.ReportFailure(id);

        Assert.Null(pool.GetNext());
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(pool.GetNext()); // còn 1s cooldown

        time.Advance(TimeSpan.FromSeconds(2)); // tổng 61s — passive recover, không cần ReportSuccess
        Assert.NotNull(pool.GetNext());
    }

    [Fact]
    public async Task ReportFailure_FirstTime_ReturnsTrueAndShowsInSnapshot()
    {
        var id = await AddProxyAsync();
        var pool = CreatePool();

        Assert.True(pool.ReportFailure(id));
        var status = Assert.Single(pool.Snapshot());
        Assert.True(status.IsDown);
        Assert.Equal(id, status.Id);
        Assert.NotNull(status.DownUntil);
    }

    [Fact]
    public async Task ReportFailure_AlreadyDown_ReturnsFalseAndKeepsOriginalCooldown()
    {
        var id = await AddProxyAsync();
        var time = new FakeTime(Start);
        var pool = CreatePool(time);
        pool.ReportFailure(id); // down tới Start+60s

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(pool.ReportFailure(id)); // no-op — không gia hạn cooldown

        time.Advance(TimeSpan.FromSeconds(31)); // tổng 61s > cooldown gốc 60s
        Assert.NotNull(pool.GetNext()); // nếu bị gia hạn thì giờ này vẫn down
    }

    [Fact]
    public async Task ReportSuccess_ClearsDownState()
    {
        var id = await AddProxyAsync();
        var pool = CreatePool();
        pool.ReportFailure(id);

        pool.ReportSuccess(id);

        Assert.NotNull(pool.GetNext());
        Assert.False(pool.Snapshot().Single().IsDown);
    }

    [Fact]
    public async Task Invalidate_PicksUpNewProxy()
    {
        var pool = CreatePool();
        Assert.Null(pool.GetNext()); // pool rỗng

        await AddProxyAsync(port: 8010);
        pool.Invalidate();

        Assert.Equal("http://127.0.0.1:8010", pool.GetNext()!.Endpoint);
    }

    [Fact]
    public async Task Invalidate_PreservesDownStateOfExistingProxies()
    {
        var downId = await AddProxyAsync(port: 8021);
        await AddProxyAsync(port: 8022);
        var pool = CreatePool();
        pool.ReportFailure(downId);

        await AddProxyAsync(port: 8023);
        pool.Invalidate(); // reload — down-state của proxy cũ phải giữ (Design decision 4)

        var picks = new HashSet<string>();
        for (var i = 0; i < 2; i++)
        {
            picks.Add(pool.GetNext()!.Endpoint);
        }
        Assert.DoesNotContain("http://127.0.0.1:8021", picks);
        Assert.Contains("http://127.0.0.1:8023", picks); // proxy mới vào pool
    }

    [Fact]
    public async Task GetNext_DecryptsPassword_AndUriHasNoUserinfo()
    {
        await AddProxyAsync(username: "user", password: "secret");
        var pool = CreatePool();

        var attempt = pool.GetNext()!;

        Assert.Equal("user", attempt.Username);
        Assert.Equal("secret", attempt.Password); // đã decrypt trong memory
        Assert.Equal(string.Empty, attempt.Uri.UserInfo); // không userinfo trong Uri
        Assert.Equal("http://127.0.0.1:8080", attempt.Endpoint); // display không credentials
    }

    [Fact]
    public async Task GetNext_ParallelCalls_AreThreadSafe()
    {
        await AddProxyAsync(port: 8031);
        await AddProxyAsync(port: 8032);
        var pool = CreatePool();
        var results = new ConcurrentBag<ProxyAttempt?>();

        Parallel.For(0, 200, _ => results.Add(pool.GetNext()));

        Assert.Equal(200, results.Count);
        Assert.All(results, r => Assert.NotNull(r));
    }

    [Fact]
    public async Task Reload_InitialLoadFails_KeepsRetryingWithoutInvalidate()
    {
        await AddProxyAsync(port: 8041);
        var wrapper = new FlakyFactory(_factory, failCount: 1);
        var pool = new ProxyPool(wrapper, _protector, TimeProvider.System, new NullLog());

        Assert.Null(pool.GetNext()); // load lần đầu fail → chưa có list → direct

        // Không Invalidate() — pool phải tự heal ở lần truy cập kế
        Assert.Equal("http://127.0.0.1:8041", pool.GetNext()!.Endpoint);
    }

    [Fact]
    public async Task Reload_ReloadFails_KeepsExistingList()
    {
        await AddProxyAsync(port: 8051);
        var wrapper = new FlakyFactory(_factory, failCount: 0);
        var pool = new ProxyPool(wrapper, _protector, TimeProvider.System, new NullLog());
        Assert.Equal("http://127.0.0.1:8051", pool.GetNext()!.Endpoint); // load thành công trước

        wrapper.FailNext = true;
        pool.Invalidate(); // reload kế sẽ ném

        Assert.Equal("http://127.0.0.1:8051", pool.GetNext()!.Endpoint); // list cũ vẫn chạy
    }

    /// <summary>Clock fake theo pattern ClientKeyRateLimiterTests.FakeTime.</summary>
    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    /// <summary>
    /// Factory ném <paramref name="failCount"/> lần tạo context đầu (rồi ủy quyền factory thật);
    /// <see cref="FailNext"/> ép ném đúng 1 lần kế — giả lập DB fail có kiểm soát.
    /// </summary>
    private sealed class FlakyFactory(IDbContextFactory<RouterBalancingDbContext> inner, int failCount)
        : IDbContextFactory<RouterBalancingDbContext>
    {
        private int _failuresLeft = failCount;

        public bool FailNext { get; set; }

        public RouterBalancingDbContext CreateDbContext()
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("Simulated DB failure.");
            }
            if (_failuresLeft > 0)
            {
                _failuresLeft--;
                throw new InvalidOperationException("Simulated DB failure.");
            }
            return inner.CreateDbContext();
        }
    }

    [Fact]
    public async Task GetNext_NullAllowedId_MatchesGlobal()
    {
        var pool = CreatePool();
        // Chỉ cần 2 hàng proxy tồn tại trong DB — id trả về không dùng ở test này
        await AddProxyAsync(port: 9000);
        await AddProxyAsync(port: 9001);
        pool.Invalidate();
        var first = pool.GetNext();
        Assert.NotNull(first);
        var second = pool.GetNext();
        Assert.NotNull(second);
        Assert.NotEqual(first!.Id, second!.Id); // RR, giống GetNext() gốc
    }

    [Fact]
    public async Task GetNext_AllowedIds_ReturnsOnlyInSet()
    {
        var pool = CreatePool();
        var inSet = await AddProxyAsync(port: 9100);
        var inSet2 = await AddProxyAsync(port: 9101);
        var outSet = await AddProxyAsync(port: 9102);
        pool.Invalidate();
        var ids = new[] { inSet, inSet2 };
        for (var i = 0; i < 6; i++)
        {
            var pick = pool.GetNext(ids);
            Assert.NotNull(pick);
            Assert.Contains(pick!.Id, ids);
        }
        // outSet không bao giờ được chọn
        var picks = new List<long>();
        for (var i = 0; i < 6; i++) picks.Add(pool.GetNext(ids)!.Id);
        Assert.DoesNotContain(outSet, picks);
    }

    [Fact]
    public async Task GetNext_AllowedIds_SkipsDown()
    {
        var pool = CreatePool();
        var a = await AddProxyAsync(port: 9200);
        var b = await AddProxyAsync(port: 9201);
        pool.Invalidate();
        pool.ReportFailure(a); // a down
        var ids = new[] { a, b };
        var pick = pool.GetNext(ids);
        Assert.NotNull(pick);
        Assert.Equal(b, pick!.Id); // bỏ qua a down
    }

    [Fact]
    public async Task GetNext_AllowedIds_AllDown_ReturnsNull()
    {
        var pool = CreatePool();
        var a = await AddProxyAsync(port: 9300);
        var b = await AddProxyAsync(port: 9301);
        pool.Invalidate();
        pool.ReportFailure(a);
        pool.ReportFailure(b);
        Assert.Null(pool.GetNext(new[] { a, b }));
    }

    [Fact]
    public async Task GetLivingInOrder_SortsByIdAndSkipsDown()
    {
        var pool = CreatePool();
        var a = await AddProxyAsync(port: 9400);
        var b = await AddProxyAsync(port: 9401);
        var c = await AddProxyAsync(port: 9402);
        pool.Invalidate();
        pool.ReportFailure(b); // b down
        var ordered = pool.GetLivingInOrder(new[] { c, a, b });
        Assert.Equal(new[] { a, c }, ordered.Select(x => x.Id).ToList());
        Assert.Empty(pool.GetLivingInOrder(new[] { b }));
    }
}
