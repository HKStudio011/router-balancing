using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Proxies;

/// <summary>Node provider trong cây gán provider-centric — data source cho UI tree.</summary>
/// <param name="Provider">Provider sở hữu node.</param>
/// <param name="EffectiveMode">
/// Chế độ dùng tập proxy từ hàng matrix; hàng thiếu Mode (hoặc thiếu hàng) → <see cref="ProxyMode.RoundRobin"/>.
/// </param>
/// <param name="Proxies">Tập proxy hiệu lực của provider, sắp theo Id; rỗng = direct.</param>
public sealed record ProviderTreeNode(
    Provider Provider, ProxyMode EffectiveMode, IReadOnlyList<ProxyNode> Proxies);

/// <summary>1 proxy dưới node provider — kèm toàn bộ AC của provider để gắn tick.</summary>
/// <param name="Proxy">Proxy gốc (endpoint + Enabled — proxy tắt vẫn là node, UI badge "(tắt)").</param>
/// <param name="Accounts">Mọi AC của provider, sắp theo Id, kèm tick hiệu lực cho proxy này.</param>
public sealed record ProxyNode(
    OutboundProxy Proxy, IReadOnlyList<AccountTickNode> Accounts);

/// <summary>1 AC dưới node proxy — tick = AC có đi qua proxy này không.</summary>
/// <param name="Account">AC sở hữu node.</param>
/// <param name="Tick">
/// true = tập hiệu lực của AC chứa proxy này
/// (override dùng tập của AC, không override dùng tập của provider).
/// </param>
/// <param name="IsOverride">true = AC đang dùng tập riêng (badge "override" trong UI).</param>
public sealed record AccountTickNode(
    ProviderAccount Account, bool Tick, bool IsOverride);

/// <summary>Dựng cây hiển thị Provider → Proxy → AC từ ma trận gán (B5) — builder thuần, không EF/service/async.</summary>
/// <remarks>
/// Quy tắc: mọi provider đều có node (kể cả chưa gán proxy);
/// tập proxy của node = tập của hàng provider — hàng thiếu (race nạp) → rỗng;
/// proxy id không có trong <paramref name="proxies"/> bị bỏ qua im lặng (race xóa).
/// </remarks>
public static class ProviderAssignmentTree
{
    /// <summary>Dựng node cho từng provider, thứ tự ổn định (Name OrdinalIgnoreCase → Id).</summary>
    /// <param name="providers">Mọi provider, cần <see cref="Provider.Accounts"/> (Id, Name) cho cấp AC.</param>
    /// <param name="proxies">Toàn bộ proxy — nguồn endpoint + Enabled; không lọc theo Enabled (proxy tắt vẫn có node).</param>
    /// <param name="matrix">Ma trận gán (Task 3) — nguồn tập proxy + cờ override, builder tự tính tick.</param>
    /// <returns>Danh sách node provider cho UI tree.</returns>
    /// <exception cref="ArgumentNullException">Khi một trong 3 tham số là <see langword="null"/>.</exception>
    public static IReadOnlyList<ProviderTreeNode> Build(
        IReadOnlyList<Provider> providers,
        IReadOnlyList<OutboundProxy> proxies,
        ProxyAssignmentMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(proxies);
        ArgumentNullException.ThrowIfNull(matrix);

        var proxyById = proxies.ToDictionary(p => p.Id);
        var providerRows = matrix.Providers.ToDictionary(r => r.Id);
        var accountRows = matrix.Accounts.ToDictionary(r => r.Id);

        var nodes = new List<ProviderTreeNode>(providers.Count);
        foreach (var provider in providers
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id))
        {
            providerRows.TryGetValue(provider.Id, out var providerRow);
            var providerSet = (providerRow?.ProxyIds ?? []).ToHashSet();
            var accounts = provider.Accounts.OrderBy(a => a.Id).ToList();

            var proxyNodes = new List<ProxyNode>(providerSet.Count);
            foreach (var proxyId in providerSet.OrderBy(id => id))
            {
                // Id ngoài `proxies` = proxy vừa bị xóa trong lúc nạp matrix → bỏ qua, không vỡ UI.
                if (!proxyById.TryGetValue(proxyId, out var proxy)) continue;
                proxyNodes.Add(new ProxyNode(proxy,
                    BuildAccountNodes(accounts, proxyId, providerSet, accountRows)));
            }

            nodes.Add(new ProviderTreeNode(provider,
                providerRow?.Mode ?? ProxyMode.RoundRobin, proxyNodes));
        }
        return nodes;
    }

    /// <summary>Gắn tick cho mọi AC (sắp Id) theo tập hiệu lực: override dùng tập AC, còn lại kế thừa provider.</summary>
    private static IReadOnlyList<AccountTickNode> BuildAccountNodes(
        IReadOnlyList<ProviderAccount> accountsSortedById,
        long proxyId,
        HashSet<long> providerSet,
        IReadOnlyDictionary<long, AccountAssignmentRow> accountRows)
    {
        var nodes = new List<AccountTickNode>(accountsSortedById.Count);
        foreach (var account in accountsSortedById)
        {
            accountRows.TryGetValue(account.Id, out var row);

            bool tick;
            bool isOverride;
            if (row is { OverrideEnabled: true })
            {
                isOverride = true;
                tick = row.ProxyIds.Contains(proxyId);
            }
            else
            {
                // Hàng AC thiếu (race nạp matrix) → coi như chưa override → kế thừa tập provider.
                isOverride = false;
                tick = providerSet.Contains(proxyId);
            }
            nodes.Add(new AccountTickNode(account, tick, isOverride));
        }
        return nodes;
    }
}
