using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>CRUD provider — key luôn mã hóa DPAPI trước khi xuống DB.</summary>
public interface IProviderService
{
    /// <summary>Tất cả provider, sắp theo Id tăng dần, đã Include Models.</summary>
    Task<IReadOnlyList<Provider>> ListAsync(CancellationToken ct = default);

    /// <summary>Provider theo id kèm Models; <see langword="null"/> nếu không tồn tại.</summary>
    Task<Provider?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Tạo provider mới từ bản nháp (key đã Protect).</summary>
    Task<Provider> CreateAsync(ProviderDraft draft, CancellationToken ct = default);

    /// <summary>
    /// Cập nhật provider. <c>draft.ApiKey</c> rỗng = giữ nguyên key cũ
    /// (người dùng sửa form không chủ đích xóa key).
    /// </summary>
    /// <exception cref="KeyNotFoundException">Khi không có provider <paramref name="id"/>.</exception>
    Task UpdateAsync(long id, ProviderDraft draft, CancellationToken ct = default);

    /// <summary>Xóa provider — models con cascade theo cấu hình FK.</summary>
    /// <exception cref="KeyNotFoundException">Khi không có provider <paramref name="id"/>.</exception>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Bật/tắt provider.</summary>
    /// <exception cref="KeyNotFoundException">Khi không có provider <paramref name="id"/>.</exception>
    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Test kết nối: GET {BaseUrl}/v1/models với key override (form) hoặc key đã lưu.
    /// Provider đã lưu (Id != 0) → persist LastTestSuccess/At/Message.
    /// </summary>
    Task<ProviderTestResult> TestConnectionAsync(Provider provider, string? apiKeyOverride, CancellationToken ct = default);
}
