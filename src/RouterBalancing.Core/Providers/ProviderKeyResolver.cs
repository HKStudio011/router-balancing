using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;

namespace RouterBalancing.Core.Providers;

/// <summary>Chọn key plaintext từ ProviderAccount — fallback cho metadata/test trước khi Phase 3 có selection thật.</summary>
public static class ProviderKeyResolver
{
    /// <summary>
    /// Key plaintext của account enabled đầu tiên (Id tăng dần):
    /// <see langword="null"/> nếu không có account khả dụng hoặc nav Accounts chưa load;
    /// <c>""</c> nếu account đó là no-key (spec free-account D2 — khác null để caller
    /// phân biệt "không probe/forward" vs "probe không auth").
    /// </summary>
    public static string? ResolveFirstEnabledKey(Provider provider, ISecretProtector protector)
    {
        var account = ResolveFirstEnabledAccount(provider);
        if (account is null)
        {
            return null;
        }
        // Không Unprotect("") — DPAPI ném CryptographicException; no-key lưu cột rỗng
        return string.IsNullOrEmpty(account.ApiKeyEncrypted)
            ? string.Empty
            : protector.Unprotect(account.ApiKeyEncrypted);
    }

    /// <summary>
    /// Account enabled đầu tiên (Id tăng dần) — null nếu không có.
    /// Chỉ filter Enabled: account no-key (key rỗng) VẪN được chọn (spec free-account D2).
    /// </summary>
    public static ProviderAccount? ResolveFirstEnabledAccount(Provider provider) =>
        provider.Accounts?.Where(a => a.Enabled)
            .OrderBy(a => a.Id).FirstOrDefault();
}
