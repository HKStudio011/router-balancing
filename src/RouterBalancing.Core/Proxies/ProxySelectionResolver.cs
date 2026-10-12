using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Chọn proxy theo cờ <c>ProviderAccount.ProxyOverrideEnabled</c> — nguồn sự thật (spec §A1):
/// <c>false</c> = kế thừa toàn bộ tập + mode provider (junction của account, kể cả rác, bị bỏ qua);
/// <c>true</c> + junction = đúng tập account; <c>true</c> + junction rỗng = Direct.
/// Mode inherit (D6): account.Mode ?? provider.Mode ?? RoundRobin.
/// </summary>
public sealed class ProxySelectionResolver : IProxySelectionResolver
{
    /// <inheritdoc />
    public ProxySelection Resolve(Provider provider, ProviderAccount? account)
    {
        if (account is { ProxyOverrideEnabled: true })
        {
            var ids = account.AccountProxies.Select(x => x.ProxyId).ToList();
            if (ids.Count == 0) return ProxySelection.Direct;
            return new ProxySelection(
                account.ProxyMode ?? provider.ProxyMode ?? ProxyMode.RoundRobin, ids);
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
