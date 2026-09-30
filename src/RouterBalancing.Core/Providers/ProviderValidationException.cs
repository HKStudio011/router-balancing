namespace RouterBalancing.Core.Providers;

/// <summary>
/// Lỗi business cần DB (unique, segment-trùng) khi tạo/cập nhật provider —
/// mang dict key i18n cùng format <see cref="ProviderValidator.Validate"/> để UI hiển thị field-level.
/// </summary>
public sealed class ProviderValidationException(IReadOnlyDictionary<string, string> errors)
    : Exception("Provider validation failed.")
{
    /// <summary>Field name (nameof property) → i18n key lỗi.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}
