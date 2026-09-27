using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;

namespace router_balancing_test.Providers;

public class ProviderKeyResolverTests
{
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    [Fact]
    public void ResolveFirstEnabledKey_OrdersByPriority_SkipsDisabled()
    {
        var provider = new Provider
        {
            Accounts =
            [
                new ProviderAccount { Id = 2, Name = "low", Enabled = true, Priority = 5, ApiKeyEncrypted = _protector.Protect("sk-low") },
                new ProviderAccount { Id = 1, Name = "high", Enabled = true, Priority = 1, ApiKeyEncrypted = _protector.Protect("sk-high") },
                new ProviderAccount { Id = 3, Name = "off", Enabled = false, Priority = 0, ApiKeyEncrypted = _protector.Protect("sk-off") },
            ],
        };

        // off có Priority 0 nhưng tắt → bỏ; high (1) đứng trước low (5)
        Assert.Equal("sk-high", ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector));
    }

    [Fact]
    public void ResolveFirstEnabledKey_WhenNoAccounts_ReturnsNull()
    {
        Assert.Null(ProviderKeyResolver.ResolveFirstEnabledKey(new Provider(), _protector));
    }
}
