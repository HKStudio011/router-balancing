using System.Security.Cryptography;
using System.Text;

namespace RouterBalancing.Core.Security;

/// <summary>Sinh/hash/mask client key inbound - SHA-256 hex, không bao giờ giữ plaintext.</summary>
public static class ClientKeyHasher
{
    public const string Prefix = "sk-rb-";

    /// <summary>SHA-256(utf8(key)) → hex lower 64 ký tự.</summary>
    public static string Hash(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

    /// <summary><c>sk-rb-</c> + 43 ký tự base64url từ 32 bytes CSPRNG (256-bit).</summary>
    public static string GeneratePlaintext()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var base64 = Convert.ToBase64String(bytes).TrimEnd('=');
        return Prefix + base64.Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// Mask cho UI: key sinh ra → <c>sk-rb-…xxxx</c> (4 ký tự cuối);
    /// key tùy ý khác → <c>…</c> + 4 ký tự cuối; key ≤4 ký tự → <c>…</c> (không lộ ký tự nào).
    /// </summary>
    public static string Mask(string plaintext)
    {
        // Spec §2: DB không bao giờ lưu plaintext — mask chỉ được chứa tối đa 4 ký tự cuối,
        // kể cả key legacy ≤8 ký tự (nhánh cũ lưu trọn vẹn plaintext vào KeyMask khi migrate).
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal))
            return $"{Prefix}…{plaintext[^4..]}";
        if (plaintext.Length <= 4) return "…";
        return $"…{plaintext[^4..]}";
    }
}
