namespace RouterBalancing.Core.Engine;

/// <summary>Phân loại lỗi resolve model (spec 3A §2.3).</summary>
public enum ResolveFailure
{
    /// <summary>Model không tồn tại / đã tắt / provider đã tắt.</summary>
    NotFound,

    /// <summary>Model chỉ có ở provider Anthropic — chưa hỗ trợ (3E).</summary>
    AnthropicNotSupported,
}
