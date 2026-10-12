using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;

namespace router_balancing_test.Proxies;

/// <summary>Cây gán provider-centric (Provider → Proxy → AC) — pure builder, không EF/service.</summary>
public class ProviderAssignmentTreeTests
{
    private static OutboundProxy Proxy(long id, bool enabled = true) =>
        new() { Id = id, Scheme = "http", Host = "10.0.0.1", Port = 8080, Enabled = enabled };

    private static Provider ProviderWith(long id, string name, params long[] accountIds)
    {
        var provider = new Provider { Id = id, Name = name };
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

    [Fact]
    public void Build_ProviderWithoutProxy_ReturnsNodeWithEmptyProxies()
    {
        var proxies = new[] { Proxy(1) };
        var providers = new[] { ProviderWith(10, "OpenAI", 100) };
        var matrix = new ProxyAssignmentMatrix(
            [new ProviderAssignmentRow(10, "OpenAI", null, [])],
            [new AccountAssignmentRow(100, 10, OverrideEnabled: false, [])]);

        var node = Assert.Single(ProviderAssignmentTree.Build(providers, proxies, matrix));

        Assert.Equal(10, node.Provider.Id);
        Assert.Empty(node.Proxies);
        Assert.Equal(ProxyMode.RoundRobin, node.EffectiveMode);
    }

    [Fact]
    public void Build_TickInheritsProviderSet_AndOverrideShowsOwnSet()
    {
        // P1 tập [p1,p2]; A1 kế thừa (flag=false); A2 override [p2] (flag=true).
        var proxies = new[] { Proxy(1), Proxy(2) };
        var providers = new[] { ProviderWith(10, "P1", 100, 101) };
        var matrix = new ProxyAssignmentMatrix(
            [new ProviderAssignmentRow(10, "P1", ProxyMode.Fallback, [1, 2])],
            [
                new AccountAssignmentRow(100, 10, OverrideEnabled: false, []),
                new AccountAssignmentRow(101, 10, OverrideEnabled: true, [2]),
            ]);

        var node = Assert.Single(ProviderAssignmentTree.Build(providers, proxies, matrix));

        Assert.Equal(ProxyMode.Fallback, node.EffectiveMode);
        Assert.Equal(new long[] { 1, 2 }, node.Proxies.Select(n => n.Proxy.Id));

        var underP1 = node.Proxies[0].Accounts;
        Assert.Equal(2, underP1.Count);
        Assert.Equal(100, underP1[0].Account.Id);
        Assert.True(underP1[0].Tick);
        Assert.False(underP1[0].IsOverride);
        Assert.Equal(101, underP1[1].Account.Id);
        Assert.False(underP1[1].Tick);
        Assert.True(underP1[1].IsOverride);

        var underP2 = node.Proxies[1].Accounts;
        Assert.Equal(100, underP2[0].Account.Id);
        Assert.True(underP2[0].Tick);
        Assert.False(underP2[0].IsOverride);
        Assert.Equal(101, underP2[1].Account.Id);
        Assert.True(underP2[1].Tick);
        Assert.True(underP2[1].IsOverride);
    }

    [Fact]
    public void Build_DisabledProxy_StillIncluded()
    {
        var proxies = new[] { Proxy(1, enabled: false) };
        var providers = new[] { ProviderWith(10, "P1", 100) };
        var matrix = new ProxyAssignmentMatrix(
            [new ProviderAssignmentRow(10, "P1", null, [1])],
            [new AccountAssignmentRow(100, 10, OverrideEnabled: false, [])]);

        var node = Assert.Single(ProviderAssignmentTree.Build(providers, proxies, matrix));

        var proxyNode = Assert.Single(node.Proxies);
        Assert.Equal(1, proxyNode.Proxy.Id);
        Assert.False(proxyNode.Proxy.Enabled);
    }

    [Fact]
    public void Build_ProxyIdNotInProxyList_Skipped()
    {
        // Race: proxy 99 đã bị xóa khỏi DB nhưng matrix còn tham chiếu → bỏ qua im lặng.
        var proxies = new[] { Proxy(1) };
        var providers = new[] { ProviderWith(10, "P1", 100) };
        var matrix = new ProxyAssignmentMatrix(
            [new ProviderAssignmentRow(10, "P1", null, [1, 99])],
            [new AccountAssignmentRow(100, 10, OverrideEnabled: true, [99])]);

        var node = Assert.Single(ProviderAssignmentTree.Build(providers, proxies, matrix));

        var proxyNode = Assert.Single(node.Proxies);
        Assert.Equal(1, proxyNode.Proxy.Id);
        var account = Assert.Single(proxyNode.Accounts);
        Assert.False(account.Tick);
        Assert.True(account.IsOverride);
    }

    [Fact]
    public void Build_Orders_Stable()
    {
        var proxies = new[] { Proxy(3), Proxy(1), Proxy(2) };
        var providers = new[]
        {
            ProviderWith(20, "beta", 200),
            ProviderWith(30, "Alpha", 302, 300, 301),
            ProviderWith(10, "alpha", 102, 100, 101),
        };
        var matrix = new ProxyAssignmentMatrix(
            [
                new ProviderAssignmentRow(20, "beta", null, [3, 1]),
                new ProviderAssignmentRow(30, "Alpha", null, [2]),
                new ProviderAssignmentRow(10, "alpha", null, [1, 2, 3]),
            ],
            []);

        var nodes = ProviderAssignmentTree.Build(providers, proxies, matrix);

        // Name OrdinalIgnoreCase, "alpha"/"Alpha" hòa → theo Id: 10, 30, rồi "beta" 20.
        Assert.Equal(new long[] { 10, 30, 20 }, nodes.Select(n => n.Provider.Id));
        // Proxies theo Id bất kể thứ tự input.
        Assert.Equal(new long[] { 1, 2, 3 }, nodes[0].Proxies.Select(n => n.Proxy.Id));
        // Accounts theo Id bất kể thứ tự input.
        var accounts = nodes[0].Proxies[0].Accounts;
        Assert.Equal(new long[] { 100, 101, 102 }, accounts.Select(a => a.Account.Id));
    }

    [Fact]
    public void Build_ProviderMissingFromMatrix_EmitsEmptyNodeWithDefaultMode()
    {
        // Race: matrix nạp trước khi provider được thêm — UI root là mọi provider nên node vẫn phải xuất hiện.
        var proxies = new[] { Proxy(1) };
        var providers = new[] { ProviderWith(10, "P1", 100), ProviderWith(20, "P2", 200) };
        var matrix = new ProxyAssignmentMatrix(
            [new ProviderAssignmentRow(20, "P2", ProxyMode.Fallback, [1])],
            []);

        var nodes = ProviderAssignmentTree.Build(providers, proxies, matrix);

        Assert.Equal(2, nodes.Count);
        var missing = nodes[0];
        Assert.Equal(10, missing.Provider.Id);
        Assert.Empty(missing.Proxies);
        Assert.Equal(ProxyMode.RoundRobin, missing.EffectiveMode);
        Assert.Single(nodes[1].Proxies);
    }
}
