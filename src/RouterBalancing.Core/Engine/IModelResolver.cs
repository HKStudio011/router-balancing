using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>Phân loại lỗi resolve model — map sang error contract (spec 3A §4).</summary>
public enum ResolveFailure
{
    /// <summary>Model không tồn tại / đã tắt / provider đã tắt → 404.</summary>
    NotFound = 0,

    /// <summary>Provider là Anthropic — 3A chưa dịch thuật → 503 (spec §1.3 gate 2).</summary>
    AnthropicNotSupported = 1,
}

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
