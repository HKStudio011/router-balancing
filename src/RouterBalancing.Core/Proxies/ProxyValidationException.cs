namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Lỗi business khi tạo/cập nhật proxy (duplicate endpoint…) — mang dict key i18n cùng format
/// <see cref="ProxyValidator.Validate"/> để UI hiển thị field-level (pattern ProviderValidationException).
/// </summary>
public sealed class ProxyValidationException(IReadOnlyDictionary<string, string> errors)
    : Exception("Proxy validation failed.")
{
    /// <summary>Field name (nameof property) → i18n key lỗi.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}
