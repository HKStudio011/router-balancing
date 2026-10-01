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

    /// <summary>Mask cho UI: key sinh ra → <c>sk-rb-…xxxx</c>; key cũ tùy ý → 4 đầu…4 cuối.</summary>
    public static string Mask(string plaintext)
    {
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal))
            return $"{Prefix}…{plaintext[^4..]}";
        if (plaintext.Length <= 8) return $"…{plaintext}";
        return $"{plaintext[..4]}…{plaintext[^4..]}";
    }
}
