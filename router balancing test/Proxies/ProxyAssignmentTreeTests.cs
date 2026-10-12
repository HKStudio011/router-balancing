using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;

namespace router_balancing_test.Proxies;

/// <summary>Quy tắc dựng cây Proxy → Provider → AC cho UI expander (D-A6).</summary>
public class ProxyAssignmentTreeTests
{
    private static OutboundProxy Proxy(long id) =>
        new() { Id = id, Scheme = "http", Host = "10.0.0.1", Port = 8080 };

    private static Provider ProviderWith(long id, params long[] accountIds)
    {
        var provider = new Provider { Id = id, Name = $"provider-{id}" };
        foreach (var accountId in accountIds)
        {
            provider.Accounts.Add(new ProviderAccount
            {
                Id = accountId,
                ProviderId = id,
                Name = $"acc-{accountId}",
            });
        }
        return provider;
    }

    private static Dictionary<long, IReadOnlyList<ProxyUsage>> Reverse(
        (long ProxyId, ProxyUsage Usage)[] rows)
    {
        var map = new Dictionary<long, List<ProxyUsage>>();
        foreach (var (proxyId, usage) in rows)
        {
            if (!map.TryGetValue(proxyId, out var list))
                map[proxyId] = list = [];
            list.Add(usage);
        }
        return map.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ProxyUsage>)kv.Value);
    }

    [Fact]
    public void Build_WhenProviderAssigned_MarksAllAccountsInherited()
    {
        var proxies = new[] { Proxy(1) };
        var providers = new[] { ProviderWith(10, 100, 101) };
        var reverse = Reverse(
            [(1, new ProxyUsage(10, "OpenAI", IsProvider: true, ProxyMode.RoundRobin))]);

        var treeNode = Assert.Single(ProxyAssignmentTree.Build(proxies, providers, reverse));
        var providerNode = Assert.Single(treeNode.Providers);

        Assert.False(providerNode.IsAccountOnly);
        Assert.Equal(ProxyMode.RoundRobin, providerNode.ProviderUsage?.Mode);
        Assert.Equal(2, providerNode.AssignedAccountCount);
        Assert.All(providerNode.Accounts, acc =>
        {
            Assert.True(acc.Assigned);
            Assert.Null(acc.Override);
        });
    }

    [Fact]
    public void Build_WhenAccountOverrides_MarksOnlyThatAccount()
    {
        var proxies = new[] { Proxy(1) };
        var providers = new[] { ProviderWith(10, 100, 101) };
        var reverse = Reverse(
        [
            (1, new ProxyUsage(10, "OpenAI", IsProvider: true, ProxyMode.RoundRobin)),
            (1, new ProxyUsage(101, "acc-101", IsProvider: false, ProxyMode.Fallback)),
        ]);

        var providerNode = Assert.Single(ProxyAssignmentTree.Build(proxies, providers, reverse)[0].Providers);

        var inherited = Assert.Single(providerNode.Accounts, a => a.Account.Id == 100);
        Assert.True(inherited.Assigned);
        Assert.Null(inherited.Override);

        var overridden = Assert.Single(providerNode.Accounts, a => a.Account.Id == 101);
        Assert.True(overridden.Assigned);
        Assert.Equal(ProxyMode.Fallback, overridden.Override?.Mode);
    }

    [Fact]
    public void Build_WhenOnlyAccountOverride_ShowsAccountOnlyAndUnassignedSiblings()
    {
        var proxies = new[] { Proxy(1) };
        var providers = new[] { ProviderWith(10, 100, 101) };
        var reverse = Reverse(
            [(1, new ProxyUsage(100, "acc-100", IsProvider: false, ProxyMode.RoundRobin))]);

        var providerNode = Assert.Single(ProxyAssignmentTree.Build(proxies, providers, reverse)[0].Providers);

        Assert.True(providerNode.IsAccountOnly);
        Assert.Null(providerNode.ProviderUsage);
        Assert.Equal(1, providerNode.AssignedAccountCount);

        var assigned = Assert.Single(providerNode.Accounts, a => a.Account.Id == 100);
        Assert.True(assigned.Assigned);

        var sibling = Assert.Single(providerNode.Accounts, a => a.Account.Id == 101);
        Assert.False(sibling.Assigned);
    }

    [Fact]
    public void Build_WhenProviderUnrelatedToProxy_SkipsNode()
    {
        var proxies = new[] { Proxy(1) };
        var providers = new[] { ProviderWith(10, 100), ProviderWith(20, 200) };
        var reverse = Reverse(
            [(1, new ProxyUsage(10, "OpenAI", IsProvider: true, ProxyMode.RoundRobin))]);

        var providerNode = Assert.Single(ProxyAssignmentTree.Build(proxies, providers, reverse)[0].Providers);

        Assert.Equal(10, providerNode.Provider.Id);
    }

    [Fact]
    public void Build_WhenProxyHasNoUsages_ReturnsEmptyProviders()
    {
        var proxies = new[] { Proxy(1) };
        var providers = new[] { ProviderWith(10, 100) };

        var node = Assert.Single(ProxyAssignmentTree.Build(proxies, providers, new Dictionary<long, IReadOnlyList<ProxyUsage>>()));

        Assert.Empty(node.Usages);
        Assert.Empty(node.Providers);
    }

    [Fact]
    public void Build_WhenAccountHasOverrideElsewhere_DoesNotInheritProviderProxy()
    {
        // D1 most-specific-wins: acc-100 override tập {proxy 2} → KHÔNG dùng proxy 1
        // dù provider 10 vẫn gán proxy 1 (kế thừa chỉ áp dụng khi CHƯA có override row).
        var proxies = new[] { Proxy(1) };
        var providers = new[] { ProviderWith(10, 100, 101) };
        var reverse = Reverse(
            [(1, new ProxyUsage(10, "OpenAI", IsProvider: true, ProxyMode.RoundRobin))]);
        var overrides = new List<ProxyAssignment>
        {
            new()
            {
                Id = 100,
                Name = "acc-100",
                IsProvider = false,
                Mode = null,
                ProxyIds = [2],
            },
        };

        var providerNode = Assert.Single(
            ProxyAssignmentTree.Build(proxies, providers, reverse, overrides)[0].Providers);

        var overriddenElsewhere = Assert.Single(providerNode.Accounts, a => a.Account.Id == 100);
        Assert.False(overriddenElsewhere.Assigned);
        Assert.Null(overriddenElsewhere.Override);

        var inherited = Assert.Single(providerNode.Accounts, a => a.Account.Id == 101);
        Assert.True(inherited.Assigned);
    }

    [Fact]
    public void Build_PreservesProxyOrder()
    {
        var proxies = new[] { Proxy(3), Proxy(1), Proxy(2) };

        var nodes = ProxyAssignmentTree.Build(proxies, [], new Dictionary<long, IReadOnlyList<ProxyUsage>>());

        Assert.Equal(new[] { 3L, 1L, 2L }, nodes.Select(n => n.Proxy.Id));
    }
}
