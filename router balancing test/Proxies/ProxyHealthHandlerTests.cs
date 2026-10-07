using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;
using router_balancing_test.Server;

namespace router_balancing_test.Proxies;

public class ProxyHealthHandlerTests : IDisposable
{
    private static ProxyAttempt Attempt(long id, int port) =>
        new(id, new Uri($"http://127.0.0.1:{port}"), null, null, $"http://127.0.0.1:{port}");

    private static HttpRequestException ConnectFailure() =>
        new("Connection refused", new SocketException((int)SocketError.ConnectionRefused));

    /// <summary>SocksException là internal type của System.Net.Http — inner thật của lỗi
    /// tunnel/handshake SOCKS5, tạo qua reflection để pin classifier.</summary>
    private static Exception SocksFailure()
    {
        var type = Type.GetType("System.Net.Http.SocksException, System.Net.Http")
            ?? throw new InvalidOperationException("SocksException type not found.");
        var ctor = type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
            null, [typeof(string)], null)
            ?? throw new InvalidOperationException("SocksException(string) ctor not found.");
        return (Exception)ctor.Invoke(
            ["SOCKS server failed to connect to the destination. Received error code 0x03."]);
    }

    [Fact]
    public async Task SendAsync_ConnectFailure_ReportsAndFailsOverToNextProxy()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(call => call == 1 ? throw ConnectFailure() : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Calls);
        Assert.Equal([1L], pool.Failures);
        Assert.Equal([2L], pool.Successes);
        // Attempt 1 qua p1, attempt 2 qua p2 — context ghi đúng pick của từng call
        Assert.Equal(1L, stub.ContextAtCall[0]!.Id);
        Assert.Equal(2L, stub.ContextAtCall[1]!.Id);
        Assert.Null(ProxyContext.Current); // reset sau khi handler xong
    }

    [Fact]
    public async Task SendAsync_Upstream500_ReportsSuccessNotFailure()
    {
        var pool = new FakePool().Add(Attempt(1, 8001));
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(pool.Failures); // 5xx từ upstream ≠ proxy fail (§4.4)
        Assert.Equal([1L], pool.Successes);
    }

    [Fact]
    public async Task SendAsync_407Response_TreatedAsProxyFailure()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired)
            : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([1L], pool.Failures); // 407 = proxy fail, failover sang p2
        Assert.Equal([2L], pool.Successes);
    }

    [Fact]
    // Pin classifier §4.4: HttpRequestException inner TimeoutException → proxy fail, failover
    // (đủ chỉ inner timeout, không cần message "407").
    public async Task SendAsync_TimeoutInnerHttpRequestException_Failover()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(call => call == 1
            ? throw new HttpRequestException("timeout", new TimeoutException())
            : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Calls);
        Assert.Equal([1L], pool.Failures); // p1 timeout inner → đánh down, p2 thành công
        Assert.Equal([2L], pool.Successes);
    }

    [Fact]
    // Pin classifier §4.4: message chứa "407" (không inner) → proxy fail, failover
    // (đủ chỉ message "407", không cần inner socket/timeout).
    public async Task SendAsync_MessageContains407_Failover()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(call => call == 1
            ? throw new HttpRequestException("HTTP 407 Proxy Authentication Required")
            : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Calls);
        Assert.Equal([1L], pool.Failures); // p1 message "407" → đánh down, p2 thành công
        Assert.Equal([2L], pool.Successes);
    }

    [Fact]
    // Regression 2026-10-06: inner SocksException (lỗi tunnel SOCKS5) là connect-phase
    // failure — phải đánh down + failover, không bị coi là "lỗi khác" ném nguyên.
    public async Task SendAsync_SocksInnerException_ReportsAndFailsOverToNextProxy()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(call => call == 1
            ? throw new HttpRequestException(
                "An error occurred while establishing a connection to the proxy tunnel.", SocksFailure())
            : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Calls);
        Assert.Equal([1L], pool.Failures); // p1 socks fail → đánh down, p2 thành công
        Assert.Equal([2L], pool.Successes);
    }

    [Fact]
    // Pin classifier §4.4: HttpRequestException không inner socket/timeout, không "407"
    // → KHÔNG phải proxy fail: ném nguyên ra caller, không đánh down (Failures rỗng).
    public async Task SendAsync_NonProxyHttpRequestException_NoReportFailure()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(call => throw new HttpRequestException("boom"));
        using var client = ClientFor(pool, stub);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://upstream.example/v1/chat"));

        Assert.Equal(1, stub.Calls); // không failover — p2 không được gọi
        Assert.Empty(pool.Failures); // không phải proxy fail → không report
        Assert.Empty(pool.Successes);
    }

    [Fact]
    public async Task SendAsync_AllProxiesFail_FallsBackToDirect()
    {
        var pool = new FakePool().Add(Attempt(1, 8001));
        var stub = new StubHandler(call => call == 1 ? throw ConnectFailure() : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Calls);
        Assert.Equal([1L], pool.Failures);
        Assert.Null(stub.ContextAtCall[1]); // direct attempt — không dính proxy nào
    }

    [Fact]
    public async Task SendAsync_BudgetExhausted_ThrowsLastFailureWithoutRetry()
    {
        // FakePool không gỡ proxy khỏi list → GetNext luôn trả → pin nhánh hết budget
        var pool = new FakePool(removeOnFailure: false).Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(_ => throw ConnectFailure());
        using var client = ClientFor(pool, stub);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://upstream.example/v1/chat"));

        Assert.Equal(2, stub.Calls); // 2 attempt theo budget, không có attempt thứ 3
        Assert.Null(ProxyContext.Current);
    }

    [Fact]
    public async Task SendAsync_NonReplayableContent_DoesNotRetry()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(_ => throw ConnectFailure());
        using var client = ClientFor(pool, stub);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://upstream.example/v1/chat")
        {
            Content = new OpaqueContent(),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));

        Assert.Equal(1, stub.Calls); // không retry — ném ngay (§4.4 replay guard)
        Assert.Equal([1L], pool.Failures);
    }

    [Fact]
    public async Task SendAsync_NonReplayableContent_Throws407WithoutRetry()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired));
        using var client = ClientFor(pool, stub);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://upstream.example/v1/chat")
        {
            Content = new OpaqueContent(),
        };

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));

        Assert.Contains("407", ex.Message); // ném 407 ngay, không failover
        Assert.Equal(1, stub.Calls); // không gửi lần 2 — replay guard mirror nhánh exception
        Assert.Equal([1L], pool.Failures); // ReportFailure đúng 1 lần cho proxy 1
    }

    [Fact]
    public async Task SendAsync_EmptyPool_AttemptsDirectImmediately()
    {
        var pool = new FakePool();
        var stub = new StubHandler(_ => Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(stub.ContextAtCall[0]); // không scope → IsBypassed → direct
        Assert.Empty(pool.Failures);
    }

    private static HttpClient ClientFor(FakePool pool, StubHandler stub) =>
        new(new ProxyHealthHandler(pool, new NullLog(), new ProxySelectionResolver()) { InnerHandler = stub });

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);

    /// <summary>
    /// Pool scriptable: ghi lại report thành công/thất bại; <c>removeOnFailure=false</c> giữ proxy trong list
    /// để test nhánh hết budget (GetNext vẫn trả proxy sau khi đã fail).
    /// </summary>
    private sealed class FakePool(bool removeOnFailure = true) : IProxyPool
    {
        private readonly List<ProxyAttempt> _alive = [];
        private int _cursor;

        public List<long> Failures { get; } = [];

        public List<long> Successes { get; } = [];

        public FakePool Add(params ProxyAttempt[] attempts)
        {
            _alive.AddRange(attempts);
            return this;
        }

        public ProxyAttempt? GetNext()
        {
            if (_alive.Count == 0)
            {
                return null;
            }

            var pick = _alive[_cursor % _alive.Count];
            _cursor++;
            return pick;
        }

        public ProxyAttempt? GetNext(IReadOnlyList<long>? allowedIds)
        {
            var candidates = allowedIds is not null
                ? _alive.Where(a => allowedIds.Contains(a.Id)).ToList()
                : _alive;
            if (candidates.Count == 0)
            {
                return null;
            }

            var pick = candidates[_cursor % candidates.Count];
            _cursor++;
            return pick;
        }

        public IReadOnlyList<ProxyAttempt> GetLivingInOrder(IReadOnlyList<long> ids) =>
            _alive.Where(a => ids.Contains(a.Id)).OrderBy(a => a.Id).ToList();

        public bool ReportFailure(long proxyId)
        {
            Failures.Add(proxyId);
            return removeOnFailure ? _alive.RemoveAll(a => a.Id == proxyId) > 0 : true;
        }

        public void ReportSuccess(long proxyId) => Successes.Add(proxyId);

        public void Invalidate()
        {
        }

        public IReadOnlyList<ProxyRuntimeStatus> Snapshot() =>
            _alive.Select(a => new ProxyRuntimeStatus(a.Id, a.Endpoint, false, null)).ToList();
    }

    /// <summary>Inner handler ghi lại call count + ProxyContext tại thời điểm gọi — script có thể ném.</summary>
    private sealed class StubHandler(Func<int, HttpResponseMessage> script) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<ProxyAttempt?> ContextAtCall { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            ContextAtCall.Add(ProxyContext.Current);
            return Task.FromResult(script(Calls));
        }
    }

    /// <summary>Content không thuộc nhóm replayable (không kế thừa ByteArrayContent).</summary>
    private sealed class OpaqueContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync("x"u8.ToArray()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }

    // ===== Helper cho assignment tests (dùng TestDb + real stubs, pattern ProxyOutboundHttpStubTests) =====
    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly LocalHttpServer _destination = new();
    private readonly List<IDisposable> _stubs = [];

    public ProxyHealthHandlerTests()
    {
        _factory = _db.CreateFactory();
        DbInitializer.Initialize(_factory);
    }

    public void Dispose()
    {
        foreach (var s in _stubs)
        {
            s.Dispose();
        }
        _destination.Dispose();
        _db.Dispose();
    }

    private async Task<long> AddRowAsync(int port, string? username = null, string? password = null)
    {
        using var db = _factory.CreateDbContext();
        var row = new OutboundProxy
        {
            Scheme = "http",
            Host = "127.0.0.1",
            Port = port,
            Enabled = true,
            Username = username,
            PasswordEncrypted = password is null ? null : _protector.Protect(password),
        };
        db.OutboundProxies.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private (HttpClient client, ProxyPool pool) CreateClient()
    {
        var pool = new ProxyPool(_factory, _protector, TimeProvider.System, new NullLog());
        var handler = new ProxyHealthHandler(pool, new NullLog(), new ProxySelectionResolver())
        {
            InnerHandler = new SocketsHttpHandler
            {
                Proxy = new RoundRobinWebProxy(),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(3),
            },
        };
        return (new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) }, pool);
    }

    private Uri DestinationUrl() => new($"http://127.0.0.1:{_destination.Port}/v1/chat/completions");

    private static int ReserveClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port; // port vừa đóng — connect failover nhanh (connection refused)
    }

    private static Provider ProviderWithProxies(params long[] proxyIds) => new()
    {
        Id = 1, Name = "Test", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com",
        ProxyMode = ProxyMode.RoundRobin,
        ProviderProxies = proxyIds.Select(id => new ProviderProxy { ProviderId = 1, ProxyId = id }).ToList(),
    };

    [Fact]
    public async Task SendAsync_DirectTarget_SendsDirectNoProxy()
    {
        var provider = new Provider { Id = 1, Name = "Test", Type = ProviderType.OpenAI, BaseUrl = "https://a.com" };
        await AddRowAsync(ReserveClosedPort()); // 1 proxy trong pool nhưng không assign
        var (client, pool) = CreateClient();
        ProxyTarget.Current.Value = new ProxyTarget(provider, null); // Direct (không ProxyProxies)
        try
        {
            var response = await client.GetAsync(DestinationUrl());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, _destination.RequestsHandled);
        }
        finally
        {
            ProxyTarget.Current.Value = null;
        }
    }

    [Fact]
    public async Task SendAsync_RoundRobinTarget_OnlyAssignedProxiesReceiveTraffic()
    {
        var liveA = new LocalHttpProxyStub();
        _stubs.Add(liveA);
        var liveB = new LocalHttpProxyStub();
        _stubs.Add(liveB);
        var unusedA = new LocalHttpProxyStub();
        _stubs.Add(unusedA);
        var unusedB = new LocalHttpProxyStub();
        _stubs.Add(unusedB);
        var idA = await AddRowAsync(liveA.Port);
        var idB = await AddRowAsync(liveB.Port);
        await AddRowAsync(unusedA.Port);
        await AddRowAsync(unusedB.Port);
        var provider = ProviderWithProxies(idA, idB);
        var (client, _) = CreateClient();
        ProxyTarget.Current.Value = new ProxyTarget(provider, null);
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var response = await client.GetAsync(DestinationUrl());
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            Assert.Equal(3, liveA.RequestsHandled);
            Assert.Equal(3, liveB.RequestsHandled);
            Assert.Equal(0, unusedA.RequestsHandled); // không trong set → không dùng
            Assert.Equal(0, unusedB.RequestsHandled);
        }
        finally
        {
            ProxyTarget.Current.Value = null;
        }
    }

    [Fact]
    public async Task SendAsync_FallbackTarget_FailsOverInOrder()
    {
        var deadPort = ReserveClosedPort();
        var live = new LocalHttpProxyStub();
        _stubs.Add(live);
        var deadId = await AddRowAsync(deadPort);
        var liveId = await AddRowAsync(live.Port);
        var provider = ProviderWithProxies(deadId, liveId);
        provider.ProxyMode = ProxyMode.Fallback;
        var (client, pool) = CreateClient();
        ProxyTarget.Current.Value = new ProxyTarget(provider, null);
        try
        {
            var response = await client.GetAsync(DestinationUrl());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, live.RequestsHandled);
            Assert.Equal(1, _destination.RequestsHandled);
            Assert.True(pool.Snapshot().Single(s => s.Id == deadId).IsDown);
        }
        finally
        {
            ProxyTarget.Current.Value = null;
        }
    }

    [Fact]
    public async Task SendAsync_FallbackTarget_AllDown_GoesDirect()
    {
        await AddRowAsync(ReserveClosedPort());
        await AddRowAsync(ReserveClosedPort());
        var provider = ProviderWithProxies(1, 2);
        provider.ProxyMode = ProxyMode.Fallback;
        var (client, pool) = CreateClient();
        ProxyTarget.Current.Value = new ProxyTarget(provider, null);
        try
        {
            var response = await client.GetAsync(DestinationUrl());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, _destination.RequestsHandled);
        }
        finally
        {
            ProxyTarget.Current.Value = null;
        }
    }
}
