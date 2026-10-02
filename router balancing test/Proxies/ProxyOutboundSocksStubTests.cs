using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;
using router_balancing_test.Server;

namespace router_balancing_test.Proxies;

public class ProxyOutboundSocksStubTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly LocalHttpServer _destination = new();
    private readonly List<IDisposable> _stubs = [];

    public ProxyOutboundSocksStubTests()
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

    private async Task<long> AddRowAsync(int port, string scheme,
        string? username = null, string? password = null)
    {
        using var db = _factory.CreateDbContext();
        var row = new OutboundProxy
        {
            Scheme = scheme,
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

    [Fact]
    public async Task SendAsync_Socks5NoAuth_TunnelsToDestination()
    {
        var stub = new LocalSocks5Stub();
        _stubs.Add(stub);
        await AddRowAsync(stub.Port, scheme: "socks5");
        var (client, _) = CreateClient();

        var response = await client.GetAsync(DestinationUrl());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, stub.TunnelsEstablished); // native socks5: greeting → CONNECT → tunnel
        Assert.Equal(1, _destination.RequestsHandled);
    }

    [Fact]
    public async Task SendAsync_Socks5WithCredentials_AuthenticatesViaAsyncLocal()
    {
        var stub = new LocalSocks5Stub { RequireUser = "alice", RequirePassword = "s3cret" };
        _stubs.Add(stub);
        await AddRowAsync(stub.Port, scheme: "socks5", username: "alice", password: "s3cret");
        var (client, _) = CreateClient();

        var response = await client.GetAsync(DestinationUrl());

        // ===== RED GATE (spec §11.4) =====
        // Nếu HttpRequestException (handshake fail vì .NET không gọi GetCredential cho
        // SOCKS5) → STOP, báo user, KHÔNG sửa assertion/skip.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, stub.AuthFailures);
        Assert.Equal("alice", Assert.Single(stub.Usernames)); // credentials đi qua AsyncLocal
        Assert.Equal(1, _destination.RequestsHandled);
    }
}
