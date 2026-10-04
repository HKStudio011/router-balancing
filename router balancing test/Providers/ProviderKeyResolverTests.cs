using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;

namespace router_balancing_test.Providers;

public class ProviderKeyResolverTests
{
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    [Fact]
    public void ResolveFirstEnabledKey_OrdersById_SkipsDisabled()
    {
        var provider = new Provider
        {
            Accounts =
            [
                new ProviderAccount { Id = 2, Name = "second", Enabled = true, ApiKeyEncrypted = _protector.Protect("sk-second") },
                new ProviderAccount { Id = 1, Name = "first", Enabled = true, ApiKeyEncrypted = _protector.Protect("sk-first") },
                new ProviderAccount { Id = 3, Name = "off", Enabled = false, ApiKeyEncrypted = _protector.Protect("sk-off") },
            ],
        };

        // off tắt → bỏ; Id nhỏ nhất trong tập Enabled → "first"
        Assert.Equal("sk-first", ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector));
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
                new ProviderAccount { Id = 1, Name = "free", Enabled = true, ApiKeyEncrypted = string.Empty },
                new ProviderAccount { Id = 2, Name = "paid", Enabled = true, ApiKeyEncrypted = _protector.Protect("sk-paid") },
            ],
        };

        // Chọn theo Id tăng dần trên tập Enabled — account no-key đứng trước thì trả "" chứ
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
