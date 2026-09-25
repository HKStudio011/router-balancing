using System.Security.Cryptography;
using System.Text;
using RouterBalancing.Core.Security;

namespace router_balancing_test.Security;

public class DpapiSecretProtectorTests
{
    private readonly DpapiSecretProtector _protector = new();

    [Fact]
    public void Protect_ThenUnprotect_ReturnsOriginal()
    {
        const string secret = "sk-test-123";

        var protectedValue = _protector.Protect(secret);
        var restored = _protector.Unprotect(protectedValue);

        Assert.Equal(secret, restored);
        Assert.NotEqual(secret, protectedValue);
    }

    [Fact]
    public void Unprotect_WhenEntropyDiffers_ThrowsCryptographicException()
    {
        // DPAPI chỉ chạy trên Windows — guard để CA1416 nhận diện, test bỏ qua ở CI phi Windows
        if (!OperatingSystem.IsWindows()) return;

        // Mã hóa với entropy khác (mô phỏng file DB bị đổi purpose) phải fail, không trả bừa
        var wrongEntropy = Encoding.UTF8.GetBytes("other-purpose");
        var foreign = Convert.ToBase64String(ProtectedData.Protect(
            Encoding.UTF8.GetBytes("x"), wrongEntropy, DataProtectionScope.CurrentUser));

        Assert.Throws<CryptographicException>(() => _protector.Unprotect(foreign));
    }

    [Fact]
    public void Protect_OnNonWindows_ThrowsPlatformNotSupported()
    {
        if (OperatingSystem.IsWindows()) return; // chỉ có ý nghĩa trên CI phi Windows

        Assert.Throws<PlatformNotSupportedException>(() => _protector.Protect("x"));
    }
}
