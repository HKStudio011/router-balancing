using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Proxies;

/// <summary>Một AC dưới node provider trong cây reverse view (Proxy → Provider → AC).</summary>
/// <param name="Account">AC sở hữu node.</param>
/// <param name="Override">Hàng override của AC cho proxy này — <see langword="null"/> = không override.</param>
/// <param name="Assigned">
/// AC có dùng proxy này: override trực tiếp hoặc kế thừa từ hàng provider.
/// </param>
public sealed record ProxyTreeAccountNode(ProviderAccount Account, ProxyUsage? Override, bool Assigned);

/// <summary>Node provider trong cây — chỉ xuất hiện khi provider hoặc ít nhất 1 AC của nó dùng proxy.</summary>
/// <param name="Provider">Provider sở hữu node.</param>
/// <param name="ProviderUsage">Hàng provider cho proxy này — <see langword="null"/> = chỉ AC gán (chưa có hàng provider).</param>
/// <param name="Accounts">Toàn bộ AC của provider, kèm trạng thái kế thừa/override.</param>
public sealed record ProxyTreeProviderNode(
    Provider Provider,
    ProxyUsage? ProviderUsage,
    IReadOnlyList<ProxyTreeAccountNode> Accounts)
{
    /// <summary>Provider chưa gán trực tiếp mà chỉ có AC override dùng proxy này.</summary>
    public bool IsAccountOnly => ProviderUsage is null;

    /// <summary>Số AC dùng được proxy này (override + kế thừa) — meta "N AC" của node.</summary>
    public int AssignedAccountCount => Accounts.Count(a => a.Assigned);
}

/// <summary>1 proxy trong cây reverse view — data source cho UI expander lồng nhau.</summary>
/// <param name="Proxy">Proxy gốc.</param>
/// <param name="Usages">Tất cả scope đang dùng trực tiếp proxy này (provider trước, account sau).</param>
/// <param name="Providers">Node provider hiển thị dưới proxy — provider không liên quan bị loại.</param>
public sealed record ProxyTreeNode(
    OutboundProxy Proxy,
    IReadOnlyList<ProxyUsage> Usages,
    IReadOnlyList<ProxyTreeProviderNode> Providers);

/// <summary>Dựng cây hiển thị Proxy → Provider → AC từ reverse assignments (D-A6).</summary>
/// <remarks>
/// Quy tắc (spec proxy-per-provider, UI expander):
/// provider hiện node khi có hàng provider HOẶC có ≥1 AC override cho proxy này;
/// mọi AC của provider đều được liệt kê — AC không override nhưng provider có gán
/// thì kế thừa (Assigned = true).
/// </remarks>
public static class ProxyAssignmentTree
{
    /// <summary>Dựng node cho từng proxy theo thứ tự <paramref name="proxies"/> giữ nguyên.</summary>
    /// <param name="proxies">Danh sách proxy — thứ tự đầu ra theo đây.</param>
    /// <param name="providers">Danh sách provider kèm Accounts (nguồn cho cấp AC).</param>
    /// <param name="reverse">Proxy-id → scope đang dùng; proxy không có key = chưa gán ai.</param>
    /// <param name="accountOverrides">
    /// Mọi hàng override của AC (kể cả proxy ngoài tập của provider) — needed để phân biệt
    /// "chưa override (kế thừa)" với "đã override nhưng proxy này không nằm trong tập" (D1).
    /// </param>
    public static IReadOnlyList<ProxyTreeNode> Build(
        IReadOnlyList<OutboundProxy> proxies,
        IReadOnlyList<Provider> providers,
        IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>> reverse,
        IReadOnlyList<ProxyAssignment>? accountOverrides = null)
    {
        ArgumentNullException.ThrowIfNull(proxies);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(reverse);

        var overriddenAccountIds = accountOverrides is null
            ? null
            : accountOverrides.Select(a => a.Id).ToHashSet();

        var nodes = new List<ProxyTreeNode>(proxies.Count);
        foreach (var proxy in proxies)
        {
            var usages = reverse.TryGetValue(proxy.Id, out var found) ? found : [];
            nodes.Add(new ProxyTreeNode(proxy, usages,
                BuildProviders(providers, usages, overriddenAccountIds)));
        }
        return nodes;
    }

    /// <summary>Lọc provider có liên quan + gắn trạng thái kế thừa/override cho từng AC.</summary>
    private static IReadOnlyList<ProxyTreeProviderNode> BuildProviders(
        IReadOnlyList<Provider> providers,
        IReadOnlyList<ProxyUsage> usages,
        HashSet<long>? overriddenAccountIds)
    {
        if (usages.Count == 0) return [];

        // ScopeId của usage là provider-id (IsProvider) hoặc account-id — tách 2 lookup
        // để O(n) thay vì quét lại list cho từng provider/AC.
        var providerUsages = new Dictionary<long, ProxyUsage>();
        var accountUsages = new Dictionary<long, ProxyUsage>();
        foreach (var usage in usages)
        {
            var target = usage.IsProvider ? providerUsages : accountUsages;
            target[usage.ScopeId] = usage;
        }

        var nodes = new List<ProxyTreeProviderNode>();
        foreach (var provider in providers)
        {
            providerUsages.TryGetValue(provider.Id, out var providerUsage);

            var accountNodes = new List<ProxyTreeAccountNode>(provider.Accounts.Count);
            var hasAnyOverride = false;
            foreach (var account in provider.Accounts)
            {
                accountUsages.TryGetValue(account.Id, out var overrideUsage);
                hasAnyOverride |= overrideUsage is not null;
                // D1 most-specific-wins: hàng override THAY THẾ toàn bộ tập kế thừa —
                // AC có override row nhưng proxy này không nằm trong tập → KHÔNG dùng proxy này.
                var hasOverrideRow = overriddenAccountIds?.Contains(account.Id) == true;
                accountNodes.Add(new ProxyTreeAccountNode(
                    account,
                    overrideUsage,
                    Assigned: overrideUsage is not null
                        || (!hasOverrideRow && providerUsage is not null)));
            }

            if (providerUsage is null && !hasAnyOverride) continue;
            nodes.Add(new ProxyTreeProviderNode(provider, providerUsage, accountNodes));
        }
        return nodes;
    }
}
