namespace RouterBalancing.Core.Proxies;

/// <summary>Trạng thái runtime 1 proxy cho UI — chụp tại1 thời điểm, không tự refresh (spec §4.1).</summary>
/// <param name="Id">Id hàng <c>OutboundProxy</c>.</param>
/// <param name="Endpoint"><c>scheme://host:port</c>.</param>
/// <param name="IsDown">Đang trong cooldown do lỗi kết nối.</param>
/// <param name="DownUntil">Hết hạn cooldown này proxy quay lại round-robin (passive recover).</param>
public sealed record ProxyRuntimeStatus(long Id, string Endpoint, bool IsDown, DateTimeOffset? DownUntil);
