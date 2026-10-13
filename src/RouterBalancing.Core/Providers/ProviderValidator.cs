using System.Text.RegularExpressions;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Validate bản nháp form provider. Trả key lỗi i18n (không text literal) —
/// UI dịch theo ngôn ngữ hiện tại, giống <c>SettingsValidator</c>.
/// </summary>
public static partial class ProviderValidator
{
    /// <summary>Slug: một_segment gồm chữ thường/số, các segment nối bằng đúng một dấu gạch.</summary>
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdentifierPattern();

    /// <summary>Kiểm tra toàn bộ rule; dictionary rỗng = hợp lệ.</summary>
    public static IReadOnlyDictionary<string, string> Validate(ProviderDraft draft)
    {
        var errors = new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            errors[nameof(ProviderDraft.Name)] = "providers.error.name";
        }

        // Chỉ chấp nhận http(s) — SSRF/parse URL ở tầng service tin được BaseUrl này
        if (draft.BaseUrl is not string s
            || (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            errors[nameof(ProviderDraft.BaseUrl)] = "providers.error.baseUrl";
        }

        if (draft.MaxConcurrent is < 0 or > 64)
        {
            errors[nameof(ProviderDraft.MaxConcurrent)] = "providers.error.maxConcurrent";
        }

        if (string.IsNullOrWhiteSpace(draft.Identifier))
        {
            errors[nameof(ProviderDraft.Identifier)] = "providers.error.identifierRequired";
        }
        else if (draft.Identifier.Length > 50 || !IdentifierPattern().IsMatch(draft.Identifier))
        {
            errors[nameof(ProviderDraft.Identifier)] = "providers.error.identifierFormat";
        }

        ValidateExtraHeaders(draft, errors);

        return errors;
    }

    /// <summary>
    /// Header tùy chỉnh: key bắt buộc (trim), không trùng (HTTP header case-insensitive),
    /// value không null (rỗng = hợp lệ). Key lỗi mang index dòng để UI trỏ đúng ô nhập.
    /// </summary>
    private static void ValidateExtraHeaders(ProviderDraft draft, Dictionary<string, string> errors)
    {
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < draft.ExtraHeaders.Count; i++)
        {
            var entry = draft.ExtraHeaders[i];
            var keyField = $"{nameof(ProviderDraft.ExtraHeaders)}.{i}.Key";
            var valueField = $"{nameof(ProviderDraft.ExtraHeaders)}.{i}.Value";

            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                errors[keyField] = "providers.error.headerKey";
            }
            else if (!seenKeys.Add(entry.Key.Trim()))
            {
                errors[keyField] = "providers.error.headerDuplicate";
            }

            if (entry.Value is null)
            {
                errors[valueField] = "providers.error.headerValue";
            }
        }
    }
}
