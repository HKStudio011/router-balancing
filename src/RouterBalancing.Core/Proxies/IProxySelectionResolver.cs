using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Chọn proxy theo Provider + Account (most-specific-wins, D1): account có proxy →
/// override provider; không thì provider; không thì Direct.
/// </summary>
public interface IProxySelectionResolver
{
    /// <summary>
    /// Entity provider/account phải đã load junction (Include ProviderProxies /
    /// AccountProxies) — resolver chỉ đọc, không query DB.
    /// </summary>
    ProxySelection Resolve(Provider provider, ProviderAccount? account);
}
