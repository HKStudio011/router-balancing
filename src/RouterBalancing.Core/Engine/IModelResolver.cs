using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>Kết quả resolve model — Success (provider + model) hoặc Failure có lý do.</summary>
public abstract record ModelResolveResult;

/// <summary>Resolve thành công — provider đã Enabled, type OpenAI, Accounts đã Include.</summary>
public sealed record ModelResolveSuccess(Provider Provider, Model Model) : ModelResolveResult;

/// <summary>Resolve thất bại — kèm model id gốc để dựng error message.</summary>
public sealed record ModelResolveFailure(string ModelId, ResolveFailure Reason) : ModelResolveResult;

/// <summary>Map model id (chính xác) sang provider sẽ phục vụ request.</summary>
public interface IModelResolver
{
    /// <summary>
    /// Tìm model enabled có provider enabled; trả về lý do fail phân loại khi không thấy.
    /// </summary>
    /// <param name="modelId">Model id client gửi — so khớp chính xác, không pattern.</param>
    /// <param name="ct">Token hủy theo request.</param>
    Task<ModelResolveResult> ResolveAsync(string modelId, CancellationToken ct);
}
