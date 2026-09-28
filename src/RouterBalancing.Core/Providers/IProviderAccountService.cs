using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>CRUD + test connection cho các tài khoản (API key) của một provider — key luôn mã hoá DPAPI.</summary>
public interface IProviderAccountService
{
    /// <summary>Accounts theo provider, sắp Priority tăng dần rồi Name — không trả plaintext key.</summary>
    Task<IReadOnlyList<ProviderAccount>> ListAsync(long providerId, CancellationToken ct = default);

    /// <summary>Tạo account mới; <c>draft.ApiKey</c> bắt buộc.</summary>
    /// <exception cref="KeyNotFoundException">Provider không tồn tại.</exception>
    /// <exception cref="InvalidOperationException">Trùng Name trong cùng provider.</exception>
    /// <exception cref="ArgumentException">Draft không hợp lệ (ProviderAccountValidator).</exception>
    Task<ProviderAccount> CreateAsync(ProviderAccountDraft draft, CancellationToken ct = default);

    /// <summary>Cập nhật; <c>draft.ApiKey</c> rỗng hoặc toàn khoảng trắng = giữ nguyên key cũ.</summary>
    /// <exception cref="KeyNotFoundException">Account không tồn tại.</exception>
    /// <exception cref="InvalidOperationException">Trùng Name trong cùng provider (trừ chính nó).</exception>
    /// <exception cref="ArgumentException">Draft không hợp lệ.</exception>
    Task<ProviderAccount> UpdateAsync(long id, ProviderAccountDraft draft, CancellationToken ct = default);

    /// <summary>Xoá account; cấm xoá account cuối cùng của provider.</summary>
    /// <exception cref="KeyNotFoundException">Account không tồn tại.</exception>
    /// <exception cref="InvalidOperationException">Đây là account cuối cùng của provider.</exception>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Test mọi account enabled của provider: ghi LastTest* từng account và
    /// Provider.LastTest* = AND các account enabled (null nếu không có account enabled).
    /// </summary>
    /// <exception cref="KeyNotFoundException">Provider không tồn tại.</exception>
    Task<IReadOnlyList<ProviderAccountTestResult>> TestAllAsync(long providerId, CancellationToken ct = default);
}
