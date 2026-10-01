namespace RouterBalancing.Core.Providers;

/// <summary>Đồng bộ danh sách model free cho provider preset (spec provider-free §6.1).</summary>
public interface IFreeModelSyncService
{
    /// <summary>
    /// Sync model free cho 1 provider preset — trả về số model free sau khi merge.
    /// </summary>
    /// <param name="providerId">Id provider preset.</param>
    /// <param name="ct">Hủy khi app thoát — DB đã commit mỗi provider là atomic.</param>
    /// <exception cref="FreeModelSyncException">
    /// Provider không tồn tại / không phải preset / không match catalog (đã đổi tên) /
    /// fetch lỗi (HTTP/timeout/JSON) / kết quả rỗng (guard không xoá list cũ).
    /// </exception>
    Task<int> SyncProviderAsync(long providerId, CancellationToken ct = default);

    /// <summary>
    /// Sync mọi provider preset đang Enabled — provider lỗi được log warning và bỏ qua,
    /// không phá vòng sync của các provider còn lại.
    /// </summary>
    Task<IReadOnlyList<(long ProviderId, int ModelCount)>> SyncAllEnabledAsync(CancellationToken ct = default);
}
