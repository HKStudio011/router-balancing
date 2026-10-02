using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;
using router_balancing_test.Server;

namespace router_balancing_test.Proxies;

/// <summary>Fallback thực: 2 proxy thật qua LocalHttpProxyStub (1 chết, 1 sống) +
/// ProxyTarget set provider Fallback [dead, live] → failover tới live.</summary>
public class ProxyAssignmentIntegrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly LocalHttpServer _destination = new();
    private readonly List<IDisposable> _stubs = [];

    public ProxyAssignmentIntegrationTests()
    {
        _factory = _db.CreateFactory();
        DbInitializer.Initialize(_factory);
    }

    public void Dispose()
    {
        foreach (var s in _stubs) s.Dispose();
        _destination.Dispose();
        _db.Dispose();
    }

    private async Task<long> AddRowAsync(int port)
    {
        using var db = _factory.CreateDbContext();
        var row = new OutboundProxy { Scheme = "http", Host = "127.0.0.1", Port = port };
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

    private static int ReserveClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task SendAsync_FallbackAssignment_FailsOverToLive()
    {
        var deadPort = ReserveClosedPort();
        var deadId = await AddRowAsync(deadPort);
        var live = new LocalHttpProxyStub();
        _stubs.Add(live);
        var liveId = await AddRowAsync(live.Port);

        var provider = new Provider
        {
            Id = 1, Name = "Test", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com",
            ProxyMode = ProxyMode.Fallback,
            ProviderProxies = [
                new ProviderProxy { ProviderId = 1, ProxyId = deadId },
                new ProviderProxy { ProviderId = 1, ProxyId = liveId },
            ],
        };

        var (client, pool) = CreateClient();
        ProxyTarget.Current.Value = new ProxyTarget(provider, null);
        try
        {
            var response = await client.GetAsync(new Uri($"http://127.0.0.1:{_destination.Port}/v1/chat/completions"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, live.RequestsHandled);
            Assert.Equal(1, _destination.RequestsHandled);
            // dead không forward (connection refused) — proxy down passive
            Assert.True(pool.Snapshot().Single(s => s.Id == deadId).IsDown);
        }
        finally
        {
            ProxyTarget.Current.Value = null;
        }
    }
}
