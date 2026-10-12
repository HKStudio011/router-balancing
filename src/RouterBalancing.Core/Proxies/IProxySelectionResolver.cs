using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Chọn proxy theo cờ <c>ProviderAccount.ProxyOverrideEnabled</c> — nguồn sự thật (3 trạng thái):
/// flag tắt = kế thừa tập provider (junction của account bị bỏ qua); flag bật + junction = tập
/// override của account; flag bật + junction rỗng = Direct.
/// </summary>
public interface IProxySelectionResolver
{
    /// <summary>
    /// Entity provider/account phải đã load junction (Include ProviderProxies /
    /// AccountProxies) — resolver chỉ đọc, không query DB.
    /// </summary>
    ProxySelection Resolve(Provider provider, ProviderAccount? account);
}
