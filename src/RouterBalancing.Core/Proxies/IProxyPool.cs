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

    /// <summary>Ghi nhận thành công — reset trạng thái failure của proxy.</summary>
    void ReportSuccess(long proxyId);

    /// <summary>Bắn lại snapshot từ DB — service CRUD gọi sau mọi thao tác.</summary>
    void Invalidate();

    /// <summary>Trạng thái runtime hiện tại cho UI (danh sách proxy Enabled).</summary>
    IReadOnlyList<ProxyRuntimeStatus> Snapshot();
}
