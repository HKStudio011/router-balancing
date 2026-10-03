using System.Net;
using RouterBalancing.Core.Server;

namespace router_balancing_test.Server;

public class BindAddressTests
{
    [Fact]
    public void ResolveBindAddress_WhenLanAccessDisabled_ReturnsLoopback() =>
        Assert.Equal(IPAddress.Loopback, ProxyHost.ResolveBindAddress(lanAccess: false));

    [Fact]
    public void ResolveBindAddress_WhenLanAccessEnabled_ReturnsAny() =>
        Assert.Equal(IPAddress.Any, ProxyHost.ResolveBindAddress(lanAccess: true));
}
