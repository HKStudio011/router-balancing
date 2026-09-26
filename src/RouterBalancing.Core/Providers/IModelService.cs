using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>Quản lý models trong một provider — fetch tự động, thêm tay, toggle, xóa.</summary>
public interface IModelService
{
    /// <summary>GET /v1/models rồi thêm ids thiếu; trả (số thêm, số bỏ qua).</summary>
    /// <exception cref="KeyNotFoundException">Khi provider không tồn tại.</exception>
    /// <exception cref="HttpRequestException">Khi HTTP không 2xx (UI hiện toast).</exception>
    Task<(int Added, int Skipped)> FetchFromProviderAsync(long providerId, CancellationToken ct = default);

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
}
