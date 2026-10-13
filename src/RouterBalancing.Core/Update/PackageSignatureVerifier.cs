using System.Security.Cryptography;

namespace RouterBalancing.Core.Update;

/// <summary>
/// Xác thực chữ ký ECDSA P-256 của zip update tải từ GitHub Release.
/// </summary>
/// <remarks>
/// Public key production được nhúng tại <see cref="ReleasePublicKeyBase64"/>; private key
/// chỉ tồn tại trong GitHub Actions secret UPDATE_SIGNING_KEY (ký trên CI) — không commit vào repo.
/// File chữ ký (.sig) chứa Base64 của signature IEEE P1363 (r||s, 64 bytes với P-256),
/// ký trên SHA-256 hash của zip — CI dùng cùng định dạng (xem release.yml).
/// Mọi trường hợp không xác thực được đều trả về <see langword="false"/> (fail-closed).
/// </remarks>
public static class PackageSignatureVerifier
{
    /// <summary>
    /// ECDSA P-256 public key (SPKI Base64) của release signing keypair.
    /// </summary>
    public const string ReleasePublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEcI9ZZi0rhTQuTeJGbueLkI7wRmp5GO0WZYNxZy/43krgXMWB92VH+aNyixgq4lP6TTmiqU+03saoK2jygeBMnA==";

    /// <summary>
    /// Kiểm tra chữ ký ECDSA của file <paramref name="packagePath"/> trong file <paramref name="signaturePath"/>.
    /// </summary>
    /// <param name="packagePath">Đường dẫn zip update.</param>
    /// <param name="signaturePath">Đường dẫn file chữ ký (.sig, Base64).</param>
    /// <param name="publicKeyBase64">SPKI Base64 của public key cần verify.</param>
    /// <returns><see langword="true"/> khi chữ ký hợp lệ; ngược lại <see langword="false"/>.</returns>
    public static bool Verify(string packagePath, string signaturePath, string publicKeyBase64)
    {
        try
        {
            if (!File.Exists(packagePath) || !File.Exists(signaturePath))
            {
                return false;
            }

            var signature = Convert.FromBase64String(ReadTrimmedBase64(signaturePath));
            var hash = SHA256.HashData(File.ReadAllBytes(packagePath));

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out var bytesRead);
            if (bytesRead != Convert.FromBase64String(publicKeyBase64).Length)
            {
                // Public key có extra bytes phía sau — nghi ngờ tamper, từ chối
                return false;
            }

            return ecdsa.VerifyHash(hash, signature);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or IOException)
        {
            // Fail-closed: mọi lỗi parse/đọc file/giải mã đều coi là không hợp lệ.
            // Caller (GitHubAppUpdateService) log chi tiết ở tầng cao hơn.
            return false;
        }
    }

    private static string ReadTrimmedBase64(string path) =>
        File.ReadAllText(path).Trim();
}
