using System.Net;
using RouterBalancing.Core.Proxies;

namespace router_balancing_test.Proxies;

public class RoundRobinWebProxyTests
{
    private static readonly Uri Destination = new("https://api.example.com/v1/chat/completions");

    private static ProxyAttempt Attempt() =>
        new(1, new Uri("http://127.0.0.1:8080"), "alice", "s3cret", "http://127.0.0.1:8080");

    [Fact]
    public void GetProxy_WithScopedAttempt_ReturnsAttemptUriWithoutUserinfo()
    {
        var proxy = new RoundRobinWebProxy();
        ProxyContext.Current = Attempt();
        try
        {
            Assert.Equal(new Uri("http://127.0.0.1:8080"), proxy.GetProxy(Destination));
            Assert.False(proxy.IsBypassed(Destination));
        }
        finally
        {
            ProxyContext.Current = null;
        }
    }

    [Fact]
    public void GetProxy_NoScope_BypassesToDirect()
    {
        var proxy = new RoundRobinWebProxy();

        Assert.True(proxy.IsBypassed(Destination));
        Assert.Null(proxy.GetProxy(Destination));
    }

    [Fact]
    public void Credentials_IsStableDynamicInstance()
    {
        var proxy = new RoundRobinWebProxy();

        Assert.Same(DynamicProxyCredentials.Instance, proxy.Credentials);
        Assert.Same(proxy.Credentials, proxy.Credentials); // handler đọc 1 lần lúc construct
    }

    [Fact]
    public void DynamicProxyCredentials_GetCredential_ReadsAsyncLocal()
    {
        var proxy = new RoundRobinWebProxy();
        ProxyContext.Current = Attempt();
        try
        {
            var credential = proxy.Credentials!.GetCredential(Destination, "Basic");

            Assert.NotNull(credential);
            Assert.Equal("alice", credential!.UserName);
            Assert.Equal("s3cret", credential.Password);
        }
        finally
        {
            ProxyContext.Current = null;
        }

        // Không scope → không auth (direct / proxy không auth)
        Assert.Null(DynamicProxyCredentials.Instance.GetCredential(Destination, "Basic"));
    }
}
