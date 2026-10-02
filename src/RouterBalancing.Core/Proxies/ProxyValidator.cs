namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Validate bản nháp form proxy (không cần DB — duplicate ở service).
/// Trả key lỗi i18n (không text literal) — pattern <c>ProviderValidator</c>.
/// </summary>
public static class ProxyValidator
{
    /// <summary>Kiểm tra toàn bộ rule; dictionary rỗng = hợp lệ.</summary>
    public static IReadOnlyDictionary<string, string> Validate(ProxyDraft draft)
    {
        var errors = new Dictionary<string, string>();

        if (draft.Scheme?.Trim().ToLowerInvariant() is not ("http" or "socks5"))
        {
            errors[nameof(ProxyDraft.Scheme)] = "proxies.error.scheme";
        }

        if (string.IsNullOrWhiteSpace(draft.Host))
        {
            errors[nameof(ProxyDraft.Host)] = "proxies.error.host";
        }

        if (draft.Port is < 1 or > 65535)
        {
            errors[nameof(ProxyDraft.Port)] = "proxies.error.port";
        }

        return errors;
    }
}
