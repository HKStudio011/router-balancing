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

    /// <summary>Cập nhật capabilities tay (Context window, Vision, Think, Audio, Video) — user sửa inline ở UI.</summary>
    /// <param name="modelId">Id model cần sửa.</param>
    /// <param name="contextWindow"><see langword="null"/> = xóa giá trị; hợp lệ 1..10_000_000.</param>
    /// <param name="supportsVision">Model có nhận input ảnh không.</param>
    /// <param name="supportsThink">Model có chế độ suy luận không.</param>
    /// <param name="supportsAudio">Model có nhận input audio không.</param>
    /// <param name="supportsVideo">Model có nhận input video không.</param>
    /// <param name="ct">Token hủy.</param>
    /// <exception cref="ArgumentOutOfRangeException">Khi <paramref name="contextWindow"/> ngoài 1..10_000_000.</exception>
    /// <exception cref="KeyNotFoundException">Khi model không tồn tại.</exception>
    Task UpdateCapabilitiesAsync(long modelId, int? contextWindow, bool supportsVision, bool supportsThink,
        bool supportsAudio, bool supportsVideo, CancellationToken ct = default);
}
