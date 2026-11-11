namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Pool proxy outbound singleton — round-robin qua proxy sống, health in-memory
/// (spec proxy-pool §4.1). Trả <see langword="null"/> từ <see cref="GetNext"/> = direct.
/// </summary>
public interface IProxyPool
{
    /// <summary>Chọn proxy kế theo round-robin trong số proxy sống (Enabled và không trong cooldown).</summary>
    ProxyAttempt? GetNext();

    /// <summary>
    /// Chọn proxy kế theo round-robin trong số proxy sống, chỉ trong tập
    /// <paramref name="allowedIds"/> khi có (null = toàn bộ, như <see cref="GetNext"/>).
    /// Trả <see langword="null"/> = direct (không còn proxy sống trong tập).
    /// </summary>
    ProxyAttempt? GetNext(IReadOnlyList<long>? allowedIds);

    /// <summary>
    /// Proxy sống trong <paramref name="ids"/> (không cooldown), sort theo ProxyId tăng —
    /// cho Fallback (thử theo thứ tự, không RR).
    /// </summary>
    IReadOnlyList<ProxyAttempt> GetLivingInOrder(IReadOnlyList<long> ids);

    /// <summary>
    /// Ghi nhận lỗi kết nối tới proxy — đánh down + cooldown 60s.
    /// Trả <see langword="true"/> khi vừa chuyển sống→down (log Warn duy nhất lần đầu);
    /// proxy đã down = no-op, trả <see langword="false"/>.
    /// </summary>
    bool ReportFailure(long proxyId);

    /// <summary>
    /// Chặn proxy tới hết <paramref name="ttl"/> vì egress của nó bị upstream từ chối
    /// (IP-abuse 403 — OpenCode free tier, spec §7.3). Cooldown RIÊNG, tách
    /// <see cref="ReportFailure"/> (lỗi kết nối): <see cref="ReportSuccess"/> KHÔNG xóa block —
    /// exit chỉ tự trở lại rotation khi hết TTL. Id không tồn tại = no-op.
    /// </summary>
    /// <param name="proxyId">Id proxy vừa trả IP-abuse 403.</param>
    /// <param name="ttl">Thời gian chặn tính từ lúc gọi.</param>
    void ReportBlocked(long proxyId, TimeSpan ttl);

    /// <summary>Ghi nhận thành công — reset trạng thái failure của proxy.</summary>
    void ReportSuccess(long proxyId);

    /// <summary>Bắn lại snapshot từ DB — service CRUD gọi sau mọi thao tác.</summary>
    void Invalidate();

    /// <summary>Trạng thái runtime hiện tại cho UI (danh sách proxy Enabled).</summary>
    IReadOnlyList<ProxyRuntimeStatus> Snapshot();
}
