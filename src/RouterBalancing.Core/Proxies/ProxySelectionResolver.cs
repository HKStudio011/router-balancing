using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// most-specific-wins (D1): account gán proxy → dùng tập + mode của account;
/// không thì provider; không thì Direct. Mode inherit:
/// account.Mode ?? provider.Mode ?? RoundRobin (D6).
/// </summary>
public sealed class ProxySelectionResolver : IProxySelectionResolver
{
    /// <inheritdoc />
    public ProxySelection Resolve(Provider provider, ProviderAccount? account)
    {
        var accountProxies = account?.AccountProxies;
        if (accountProxies is not null && accountProxies.Count > 0)
        {
            // accountProxies != null ⟹ account != null (?. trả null khi account null).
            return new ProxySelection(
                account!.ProxyMode ?? provider.ProxyMode ?? ProxyMode.RoundRobin,
                accountProxies.Select(x => x.ProxyId).ToList());
        }

        var providerProxies = provider.ProviderProxies;
        if (providerProxies is not null && providerProxies.Count > 0)
        {
            return new ProxySelection(
                provider.ProxyMode ?? ProxyMode.RoundRobin,
                providerProxies.Select(x => x.ProxyId).ToList());
        }

        return ProxySelection.Direct;
    }
}
