namespace RouterBalancing.Core.Providers;

/// <summary>
/// Điền metadata cho model/provider — best-effort với lỗi mạng/DB (log + đi tiếp);
/// chỉ caller cancellation mới được propagate ra ngoài.
/// </summary>
public interface IModelMetadataService
{
    /// <summary>
    /// Chạy chain metadata: ghi <c>ContextWindow</c> khi còn trống và ghi capability fact
    /// qua <see cref="Capabilities.IModelCapabilityWriter"/> với nguồn do bước thắng khai báo
    /// (<see cref="IModelMetadataProvider.Source"/> — Catalog hoặc Endpoint).
    /// Lỗi (mạng, DPAPI, model biến mất) được log warning và nuốt — model đã lưu vẫn OK.
    /// </summary>
    Task TryFillAsync(long modelId, CancellationToken ct = default);

    /// <summary>
    /// Điền metadata cho MỌI model của provider còn thiếu fact (<see cref="Capabilities.ModelCapabilityView.IsEmpty"/>).
    /// Chạy tối đa 2 probe song song, có budget thời gian mềm; lỗi từng model chỉ log rồi đi tiếp.
    /// </summary>
    /// <param name="providerId">PK <c>Providers.Id</c>; provider không tồn tại → trả 0.</param>
    /// <param name="ct">
    /// Token của caller. Bị huỷ giữa đợt → ném <see cref="OperationCanceledException"/>;
    /// timeout nội bộ thì dừng mềm và trả số model đã điền.
    /// </param>
    /// <returns>Số model có view rỗng trước đợt và không còn rỗng sau đợt.</returns>
    Task<int> FillMissingAsync(long providerId, CancellationToken ct = default);

    /// <summary>
    /// Đặt lại capability của model theo catalog: xóa toàn bộ fact (kể cả user) rồi chạy
    /// chain metadata. <c>ContextWindow</c>/<c>MaxOutputTokens</c> là scalar, không bị xóa.
    /// User fact bị mất có chủ đích kể cả khi chain không tìm lại được gì.
    /// </summary>
    /// <param name="modelId">PK <c>Models.Id</c>; model không tồn tại → không làm gì.</param>
    /// <param name="ct">Token hủy của caller.</param>
    Task ResetToCatalogAsync(long modelId, CancellationToken ct = default);
}
