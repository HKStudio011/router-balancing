using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;

namespace router_balancing_test.Proxies;

/// <summary>Resolver most-specific-wins (D1) + mode inherit (D6) — không cần DB.</summary>
public class ProxySelectionResolverTests
{
    private static readonly IProxySelectionResolver Resolver = new ProxySelectionResolver();

    private static Provider ProviderWithProxies(params long[] ids) => new()
    {
        Id = 1,
        Name = "Test",
        Type = ProviderType.OpenAI,
        BaseUrl = "https://api.example.com",
        ProxyMode = ProxyMode.RoundRobin,
        ProviderProxies = ids.Select(id => new ProviderProxy { ProviderId = 1, ProxyId = id }).ToList(),
    };

    private static ProviderAccount AccountWithProxies(params long[] ids) => new()
    {
        Id = 10,
        ProviderId = 1,
        Name = "Acc",
        AccountProxies = ids.Select(id => new ProviderAccountProxy { AccountId = 10, ProxyId = id }).ToList(),
    };

    [Fact]
    public void Resolve_NoProxies_ReturnsDirect()
    {
        var sel = Resolver.Resolve(ProviderWithProxies(), null);
        Assert.Same(ProxySelection.Direct, sel);
        Assert.True(sel.IsDirect);
        Assert.Null(sel.ProxyIds);
    }

    [Fact]
    public void Resolve_ProviderOnly_ReturnsProviderSetAndMode()
    {
        var provider = ProviderWithProxies(100, 200);
        provider.ProxyMode = ProxyMode.Fallback;
        var sel = Resolver.Resolve(provider, null);
        Assert.False(sel.IsDirect);
        Assert.Equal(ProxyMode.Fallback, sel.Mode);
        Assert.Equal(new[] { 100L, 200L }, sel.ProxyIds!.ToList());
    }

    [Fact]
    public void Resolve_AccountOnly_ReturnsAccountSet()
    {
        var provider = new Provider { Id = 1, Name = "Test", Type = ProviderType.OpenAI, BaseUrl = "https://a.com" };
        var account = AccountWithProxies(300, 400);
        account.ProxyMode = ProxyMode.Fallback;
        var sel = Resolver.Resolve(provider, account);
        Assert.Equal(ProxyMode.Fallback, sel.Mode);
        Assert.Equal(new[] { 300L, 400L }, sel.ProxyIds!.ToList());
    }

    [Fact]
    public void Resolve_AccountOverridesProvider_UsesAccountSet()
    {
        var provider = ProviderWithProxies(100, 200);
        var account = AccountWithProxies(300, 400);
        account.ProxyMode = ProxyMode.Fallback;
        var sel = Resolver.Resolve(provider, account);
        Assert.Equal(ProxyMode.Fallback, sel.Mode);
        Assert.Equal(new[] { 300L, 400L }, sel.ProxyIds!.ToList()); // không phải [100,200]
    }

    [Fact]
    public void Resolve_ModeInherit_AccountNullProviderMode()
    {
        var provider = ProviderWithProxies(100);
        provider.ProxyMode = ProxyMode.Fallback;
        var account = new ProviderAccount { Id = 10, ProviderId = 1, Name = "Acc" }; // ProxyMode null
        var sel = Resolver.Resolve(provider, account);
        Assert.Equal(ProxyMode.Fallback, sel.Mode);
    }

    [Fact]
    public void Resolve_ModeInherit_BothNull_ReturnsRoundRobin()
    {
        var provider = ProviderWithProxies(100);
        provider.ProxyMode = null;
        var account = new ProviderAccount { Id = 10, ProviderId = 1, Name = "Acc" };
        account.ProxyMode = null;
        var sel = Resolver.Resolve(provider, account);
        Assert.Equal(ProxyMode.RoundRobin, sel.Mode);
    }

    [Fact]
    public void Resolve_EmptyAccountProxies_FallsThroughToProvider()
    {
        var provider = ProviderWithProxies(100);
        var account = new ProviderAccount { Id = 10, ProviderId = 1, Name = "Acc", AccountProxies = [] };
        var sel = Resolver.Resolve(provider, account);
        Assert.Equal(new[] { 100L }, sel.ProxyIds!.ToList());
    }
}
