using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Proxies;

/// <summary>CRUD proxy outbound — mọi thao tác gọi <c>IProxyPool.Invalidate</c> (spec proxy-pool §5).</summary>
public interface IProxyService
{
    /// <summary>Tất cả proxy theo Id — UI tự lọc Enabled; không trả password plaintext.</summary>
    Task<IReadOnlyList<OutboundProxy>> ListAsync(CancellationToken ct = default);

    /// <summary>Tạo proxy mới — encrypt password, invalidate pool.</summary>
    /// <exception cref="ProxyValidationException">Scheme/host/port không hợp lệ hoặc endpoint trùng.</exception>
    Task<OutboundProxy> CreateAsync(ProxyDraft draft, CancellationToken ct = default);

    /// <summary>
    /// Cập nhật proxy — <c>Password = null</c> giữ password cũ; username trống xóa toàn bộ auth.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Không có proxy với <paramref name="id"/>.</exception>
    /// <exception cref="ProxyValidationException">Rule validate/duplicate fail.</exception>
    Task<OutboundProxy> UpdateAsync(long id, ProxyDraft draft, CancellationToken ct = default);

    /// <exception cref="KeyNotFoundException">Không có proxy với <paramref name="id"/>.</exception>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <exception cref="KeyNotFoundException">Không có proxy với <paramref name="id"/>.</exception>
    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);

    /// <summary>Test kết nối thủ công qua proxy — persist LastTest* (spec §5.4).</summary>
    /// <remarks>
    /// Lỗi decrypt password (CryptographicException) được implementation bắt và persist
    /// thành LastTestSuccess = false — không ném ra caller.
    /// </remarks>
    /// <exception cref="KeyNotFoundException">Không có proxy với <paramref name="proxyId"/>.</exception>
    Task<ProxyTestResult> TestAsync(long proxyId, CancellationToken ct);

    /// <summary>Gán proxy của provider (M2M) + mode. proxyIds rỗng = gỡ toàn bộ.</summary>
    /// <param name="mode">Mode dùng tập proxy; null = kế thừa (account hoặc Robin).</param>
    /// <exception cref="KeyNotFoundException">Không có provider với <paramref name="providerId"/> hoặc proxy không tồn tại.</exception>
    Task AssignProviderProxiesAsync(long providerId, IReadOnlyList<long> proxyIds,
        ProxyMode? mode, CancellationToken ct = default);

    /// <summary>Gán proxy của account (override provider) + mode. proxyIds rỗng = gỡ toàn bộ.</summary>
    /// <param name="mode">Mode dùng tập proxy; null = kế thừa provider.</param>
    /// <exception cref="KeyNotFoundException">Không có account với <paramref name="accountId"/> hoặc proxy không tồn tại.</exception>
    Task AssignAccountProxiesAsync(long accountId, IReadOnlyList<long> proxyIds,
        ProxyMode? mode, CancellationToken ct = default);

    /// <summary>Gán proxy hiện tại (provider + account) cho UI.</summary>
    /// <exception cref="KeyNotFoundException">Không có provider với <paramref name="providerId"/>.</exception>
    Task<IReadOnlyList<ProxyAssignment>> GetAssignmentsAsync(long providerId, CancellationToken ct = default);

    /// <summary>
    /// Tất cả provider/account đang gán cho mỗi proxy — đúng 2 query tổng (provider trước,
    /// account sau), không N+1 (D-A6). Proxy chưa gán cho ai không xuất hiện trong map.
    /// </summary>
    Task<IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>> GetReverseAssignmentsAsync(
        CancellationToken ct = default);
}
