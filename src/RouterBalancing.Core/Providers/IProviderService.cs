using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>CRUD provider — key luôn mã hóa DPAPI trước khi xuống DB.</summary>
public interface IProviderService
{
    /// <summary>Tất cả provider, sắp theo Id tăng dần, đã Include Models + Accounts.</summary>
    Task<IReadOnlyList<Provider>> ListAsync(CancellationToken ct = default);

    /// <summary>Provider theo id kèm Models + Accounts; <see langword="null"/> nếu không tồn tại.</summary>
    Task<Provider?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Tạo provider mới từ bản nháp. Luôn tạo kèm account "Default":
    /// <c>draft.NoKey</c> = true → cột key rỗng (no-key); ngược lại <c>draft.ApiKey</c> bắt buộc
    /// (rỗng mà không NoKey → <see cref="ArgumentException"/>; spec free-account D4).</summary>
    /// <exception cref="ProviderValidationException">Identifier trùng hoặc xung đột segment model id.</exception>
    Task<Provider> CreateAsync(ProviderDraft draft, CancellationToken ct = default);

    /// <summary>
    /// Cập nhật provider. <c>draft.ApiKey</c> bị bỏ qua —
    /// key quản lý ở <c>IProviderAccountService</c> (spec provider-accounts §4.2).
    /// </summary>
    /// <exception cref="KeyNotFoundException">Khi không có provider <paramref name="id"/>.</exception>
    /// <exception cref="ProviderValidationException">Identifier trùng hoặc xung đột segment model id.</exception>
    Task UpdateAsync(long id, ProviderDraft draft, CancellationToken ct = default);

    /// <summary>Xóa provider — models con và accounts cascade theo cấu hình FK.</summary>
    /// <exception cref="KeyNotFoundException">Khi không có provider <paramref name="id"/>.</exception>
    /// <exception cref="InvalidOperationException">Khi provider là preset free — không cho xóa (spec provider-free §6 D6).</exception>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Bật/tắt provider.</summary>
    /// <exception cref="KeyNotFoundException">Khi không có provider <paramref name="id"/>.</exception>
    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Test kết nối: GET {BaseUrl}/v1/models.
    /// <paramref name="apiKeyOverride"/> <see langword="null"/> = dùng key account enabled đầu tiên;
    /// <c>""</c> = ép probe không key (no-key, không gửi header auth) — spec free-account D9.
    /// </summary>
    Task<ProviderTestResult> TestConnectionAsync(Provider provider, string? apiKeyOverride, CancellationToken ct = default);
}
