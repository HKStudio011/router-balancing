using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;

namespace RouterBalancing.Core.Providers;

/// <summary>Chọn key plaintext từ ProviderAccount — fallback cho metadata/test trước khi Phase 3 có selection thật.</summary>
public static class ProviderKeyResolver
{
    /// <summary>
    /// Key của account enabled đầu tiên (Priority tăng dần, tie-break Id tăng dần);
    /// <see langword="null"/> nếu provider không có account khả dụng hoặc nav Accounts chưa load.
    /// </summary>
    public static string? ResolveFirstEnabledKey(Provider provider, ISecretProtector protector)
    {
        var account = provider.Accounts?
            .Where(a => a.Enabled && !string.IsNullOrEmpty(a.ApiKeyEncrypted))
            .OrderBy(a => a.Priority)
            .ThenBy(a => a.Id)
            .FirstOrDefault();
        return account is null ? null : protector.Unprotect(account.ApiKeyEncrypted);
    }
}
