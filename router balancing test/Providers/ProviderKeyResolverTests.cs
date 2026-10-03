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

    [Fact]
    public void ResolveFirstEnabledKey_WhenEnabledAccountHasNoKey_ReturnsEmptyString()
    {
        var provider = new Provider
        {
            Accounts =
            [
                new ProviderAccount { Id = 1, Name = "free", Enabled = true, ApiKeyEncrypted = string.Empty },
            ],
        };

        // "" ≠ null: "" = account no-key (probe/forward KHÔNG auth), null = không có account (chặn)
        Assert.Equal(string.Empty, ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector));
    }

    [Fact]
    public void ResolveFirstEnabledKey_WhenFirstEnabledIsNoKey_DoesNotSkipToNextAccount()
    {
        var provider = new Provider
        {
            Accounts =
            [
                new ProviderAccount { Id = 1, Name = "free", Enabled = true, Priority = 0, ApiKeyEncrypted = string.Empty },
                new ProviderAccount { Id = 2, Name = "paid", Enabled = true, Priority = 5, ApiKeyEncrypted = _protector.Protect("sk-paid") },
            ],
        };

        // Chọn theo Priority trên tập Enabled — account no-key đứng trước thì trả "" chứ
        // không nhảy sang account có key (spec D2: không hidden failover ở tầng resolver)
        Assert.Equal(string.Empty, ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector));
    }

    [Fact]
    public void ResolveFirstEnabledAccount_WhenFirstEnabledHasNoKey_ReturnsThatAccount()
    {
        var provider = new Provider
        {
            Accounts =
            [
                new ProviderAccount { Id = 1, Name = "free", Enabled = true, ApiKeyEncrypted = string.Empty },
            ],
        };

        var account = ProviderKeyResolver.ResolveFirstEnabledAccount(provider);

        Assert.NotNull(account);
        Assert.Equal("free", account.Name);
    }
}
