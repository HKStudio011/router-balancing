using System.Security.Cryptography;
using System.Text;

namespace RouterBalancing.Core.Security;

/// <summary>
/// DPAPI CurrentUser — secret chỉ giải được bởi cùng user trên cùng máy,
/// không cần cơ chế key management riêng của app.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Entropy purpose: gắn mã hóa vào app này — copy file DB sang app khác không tự giải được
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("router-balancing/keys");

    public string Protect(string plaintext)
    {
        // Inline guard: CA1416 chỉ nhận diện guard cùng method với lời gọi ProtectedData
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI chỉ hỗ trợ Windows.");

        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(plaintext), Purpose, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public string Unprotect(string protectedValue)
    {
        // Inline guard: CA1416 chỉ nhận diện guard cùng method với lời gọi ProtectedData
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI chỉ hỗ trợ Windows.");

        var bytes = ProtectedData.Unprotect(
            Convert.FromBase64String(protectedValue), Purpose, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }
}
