using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Toàn bộ ma trận gán proxy (mọi provider + mọi account) — data source cho cây gán UI.
/// </summary>
/// <param name="Providers">Mọi provider kèm tập proxy trực tiếp.</param>
/// <param name="Accounts">Mọi account kèm cờ override + tập proxy trực tiếp.</param>
public sealed record ProxyAssignmentMatrix(
    IReadOnlyList<ProviderAssignmentRow> Providers,
    IReadOnlyList<AccountAssignmentRow> Accounts);

/// <summary>Một hàng provider trong ma trận gán.</summary>
/// <param name="Id">Id provider.</param>
/// <param name="Name">Tên hiển thị.</param>
/// <param name="Mode">Chế độ dùng tập proxy; null = RoundRobin mặc định.</param>
/// <param name="ProxyIds">Tập proxy trực tiếp của provider; rỗng = direct.</param>
public sealed record ProviderAssignmentRow(
    long Id, string Name, ProxyMode? Mode, IReadOnlyList<long> ProxyIds);

/// <summary>Một hàng account trong ma trận gán.</summary>
/// <param name="Id">Id account.</param>
/// <param name="ProviderId">Id provider sở hữu account.</param>
/// <param name="OverrideEnabled">true = AC dùng tập riêng; false = kế thừa provider.</param>
/// <param name="ProxyIds">
/// Tập proxy trực tiếp của AC — luôn rỗng khi <paramref name="OverrideEnabled"/> = false
/// (kế thừa); rỗng + true = direct. Cờ phân biệt 2 trường hợp, cây tự resolve tập hiệu lực.
/// </param>
public sealed record AccountAssignmentRow(
    long Id, long ProviderId, bool OverrideEnabled, IReadOnlyList<long> ProxyIds);
