using RouterBalancing.Core.Capabilities;
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>Quản lý models trong một provider — fetch tự động, thêm tay, toggle, xóa.</summary>
public interface IModelService
{
    /// <summary>
    /// GET /v1/models rồi thêm ids thiếu; sau đó điền metadata cho mọi model của provider
    /// còn thiếu fact (kể cả model cũ — nút Fetch là đường retry có chủ đích).
    /// </summary>
    /// <returns>(số thêm, số bỏ qua, số model được điền metadata trong đợt này).</returns>
    /// <exception cref="KeyNotFoundException">Khi provider không tồn tại.</exception>
    /// <exception cref="HttpRequestException">Khi HTTP không 2xx (UI hiện toast).</exception>
    Task<(int Added, int Skipped, int Filled)> FetchFromProviderAsync(long providerId, CancellationToken ct = default);

    /// <summary>Thêm 1 model thủ công (IsManual = true).</summary>
    /// <exception cref="InvalidOperationException">Khi modelId đã tồn tại trong provider.</exception>
    Task<Model> AddManualAsync(long providerId, string modelId, CancellationToken ct = default);

    /// <summary>Thêm nhiều model (mỗi phần tử 1 id) — trim, bỏ rỗng, dedupe; trả (số thêm, số bỏ qua).</summary>
    Task<(int Added, int Skipped)> AddBulkAsync(long providerId, IReadOnlyList<string> modelIds, CancellationToken ct = default);

    /// <summary>Xóa 1 model.</summary>
    Task RemoveAsync(long modelId, CancellationToken ct = default);

    /// <summary>Xóa hết models của provider; trả số dòng đã xóa.</summary>
    Task<int> RemoveAllAsync(long providerId, CancellationToken ct = default);

    /// <summary>Bật/tắt 1 model.</summary>
    Task SetEnabledAsync(long modelId, bool enabled, CancellationToken ct = default);

    /// <summary>Bật/tắt toàn bộ models của provider.</summary>
    Task SetAllEnabledAsync(long providerId, bool enabled, CancellationToken ct = default);

    /// <summary>
    /// Ghi lựa chọn tay của user: scalar limits + capability/modality qua writer với <c>Source=User</c>.
    /// Chỉ key có trong <paramref name="patch"/> mới bị đụng — UI dựng patch theo diff so với
    /// baseline để không biến các fact catalog/endpoint không đổi thành User.
    /// </summary>
    /// <param name="modelId">Id model cần sửa.</param>
    /// <param name="contextWindow"><see langword="null"/> = xóa giá trị; hợp lệ 1..10_000_000.</param>
    /// <param name="maxOutputTokens"><see langword="null"/> = xóa giá trị; hợp lệ 1..10_000_000.</param>
    /// <param name="patch">Phát biểu user thêm/sửa; <see cref="CapabilityPatch.ClearKeys"/> = trả về "chưa rõ".</param>
    /// <param name="ct">Token hủy.</param>
    /// <exception cref="ArgumentNullException">Khi <paramref name="patch"/> null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Khi context/max output ngoài 1..10_000_000.</exception>
    /// <exception cref="KeyNotFoundException">Khi model không tồn tại.</exception>
    /// <remarks>
    /// Fact được ghi trước qua transaction của writer, sau đó scalar được lưu ở context riêng —
    /// hai bước không nguyên tử với nhau. Lỗi DB ở bước scalar có thể để lại fact đã ghi.
    /// </remarks>
    Task SetCapabilitiesAsync(long modelId, int? contextWindow, int? maxOutputTokens,
        CapabilityPatch patch, CancellationToken ct = default);
}
