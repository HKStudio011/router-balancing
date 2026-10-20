namespace RouterBalancing.Core.Providers;

/// <summary>Điền metadata cho model sau khi thêm — best-effort, không bao giờ ném ra caller.</summary>
public interface IModelMetadataService
{
    /// <summary>
    /// Chạy chain metadata: ghi <c>ContextWindow</c> khi còn trống và ghi capability fact
    /// qua <see cref="Capabilities.IModelCapabilityWriter"/> (nguồn <c>Catalog</c>).
    /// Lỗi (mạng, DPAPI, model biến mất) được log warning và nuốt — model đã lưu vẫn OK.
    /// </summary>
    Task TryFillAsync(long modelId, CancellationToken ct = default);
}
