using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;
using router_balancing_test.Server;

namespace router_balancing_test.Proxies;

public class ProxyEchoClientTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly LocalHttpServer _destination = new();
    private readonly List<IDisposable> _stubs = [];

    public ProxyEchoClientTests()
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

    [Fact]
    public async Task EchoAsync_ThroughStubProxy_ReturnsSuccess()
    {
        var stub = new LocalHttpProxyStub();
        _stubs.Add(stub);
        var echo = new ProxyEchoClient($"http://127.0.0.1:{_destination.Port}/ip");
        var attempt = new ProxyAttempt(1, new Uri($"http://127.0.0.1:{stub.Port}"),
            null, null, $"http://127.0.0.1:{stub.Port}");

        var result = await echo.EchoAsync(attempt, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(200, result.HttpStatus);
        Assert.Null(result.Error);
        Assert.Equal(1, stub.RequestsHandled);
        Assert.Equal(1, _destination.RequestsHandled);
    }

    [Fact]
    public async Task EchoAsync_DeadProxy_ThrowsHttpRequestException()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var echo = new ProxyEchoClient($"http://127.0.0.1:{_destination.Port}/ip");
        var attempt = new ProxyAttempt(1, new Uri($"http://127.0.0.1:{deadPort}"),
            null, null, $"http://127.0.0.1:{deadPort}");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => echo.EchoAsync(attempt, CancellationToken.None));
        Assert.Equal(0, _destination.RequestsHandled);
    }
}
