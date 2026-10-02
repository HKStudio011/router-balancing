using RouterBalancing.Core.Proxies;

namespace router_balancing_test.Proxies;

public class ProxyAttemptTests
{
    [Fact]
    public void ToString_NeverIncludesDecryptedPassword()
    {
        var attempt = new ProxyAttempt(
            7, new Uri("http://proxy.example:8080"), "alice", "secret", "http://proxy.example:8080");

        var text = attempt.ToString();

        Assert.Contains("[REDACTED]", text);
        Assert.Contains("http://proxy.example:8080", text);
        Assert.DoesNotContain("secret", text);
    }
}
