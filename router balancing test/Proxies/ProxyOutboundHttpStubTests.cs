using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;
using router_balancing_test.Server;

namespace router_balancing_test.Proxies;

public class ProxyOutboundHttpStubTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly LocalHttpServer _destination = new();
    private readonly List<IDisposable> _stubs = [];

    public ProxyOutboundHttpStubTests()
    {
        _factory = _db.CreateFactory();
        DbInitializer.Initialize(_factory);
    }

    public void Dispose()
    {
        foreach (var stub in _stubs)
        {
            stub.Dispose();
        }

        _destination.Dispose();
        _db.Dispose();
    }

    private LocalHttpProxyStub AddStub(string? user = null, string? password = null)
    {
        var stub = new LocalHttpProxyStub { RequireUser = user, RequirePassword = password };
        _stubs.Add(stub);
        return stub;
    }

    private static int ReserveClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port; // port vừa đóng — connect failover nhanh (connection refused)
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

    /// <summary>Pipeline 1 chiều đúng như MauiProgram sẽ wire (Task 8): handler → SocketsHttpHandler.</summary>
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

    [Fact]
    public async Task SendAsync_TwoProxies_DistributesEvenlyAcrossStubs()
    {
        var stubA = AddStub();
        var stubB = AddStub();
        await AddRowAsync(stubA.Port);
        await AddRowAsync(stubB.Port);
        var (client, _) = CreateClient();

        for (var i = 0; i < 6; i++)
        {
            var response = await client.GetAsync(DestinationUrl());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(3, stubA.RequestsHandled); // RR đều 3/3 — pin per-request (§4.1)
        Assert.Equal(3, stubB.RequestsHandled);
        Assert.Equal(6, _destination.RequestsHandled); // mọi request đều tới destination
        Assert.All(stubA.RequestLines.Concat(stubB.RequestLines),
            line => Assert.StartsWith("GET http://", line)); // absolute-URI, không CONNECT
    }

    [Fact]
    public async Task SendAsync_WithProxyCredentials_SendsBasicAuthorization()
    {
        var stub = AddStub(user: "alice", password: "s3cret");
        await AddRowAsync(stub.Port, username: "alice", password: "s3cret");
        var (client, _) = CreateClient();

        var response = await client.GetAsync(DestinationUrl());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = "Basic " + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("alice:s3cret"));
        // Lần 1: 407 challenge (header thiếu/sai — nếu .NET tự retry nội bộ), lần cuối: đúng.
        // Nếu .NET KHÔNG tự retry thách thức 407 với proxy credentials, test này fail →
        // xem lại cách SocketsHttpHandler xử lý Proxy-Authorization trước khi đổi assertion.
        Assert.Equal(expected, stub.ProxyAuthorizationHeaders[^1]);
        Assert.Equal(1, stub.RequestsHandled);
        Assert.Equal(1, _destination.RequestsHandled);
    }

    [Fact]
    public async Task SendAsync_DeadProxy_FailsOverAndMarksDown()
    {
        var deadPort = ReserveClosedPort();
        var deadId = await AddRowAsync(deadPort);
        var live = AddStub();
        await AddRowAsync(live.Port);
        var (client, pool) = CreateClient();

        var response = await client.GetAsync(DestinationUrl());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, live.RequestsHandled);
        Assert.Equal(1, _destination.RequestsHandled);
        Assert.True(pool.Snapshot().Single(s => s.Id == deadId).IsDown); // passive health
    }

    [Fact]
    public async Task SendAsync_AllProxiesDead_AttemptsDirect()
    {
        await AddRowAsync(ReserveClosedPort());
        await AddRowAsync(ReserveClosedPort());
        var (client, pool) = CreateClient();

        var response = await client.GetAsync(DestinationUrl());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _destination.RequestsHandled); // direct tới destination
        Assert.All(pool.Snapshot(), s => Assert.True(s.IsDown)); // cả 2 down → budget hết → direct
    }
}
