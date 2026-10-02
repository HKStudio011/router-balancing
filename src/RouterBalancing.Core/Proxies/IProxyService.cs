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
    Task<ProxyTestResult> TestAsync(long proxyId, CancellationToken ct);
}
