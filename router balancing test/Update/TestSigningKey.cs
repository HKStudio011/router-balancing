using System.Security.Cryptography;

namespace router_balancing_test.Update;

/// <summary>Keypair ECDSA P-256 test-only — sinh runtime, không đụng key production.</summary>
internal sealed class TestSigningKey : IDisposable
{
    private readonly ECDsa _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicKeyBase64 { get; }

    public TestSigningKey()
    {
        PublicKeyBase64 = Convert.ToBase64String(_ecdsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Ký SHA-256 của <paramref name="data"/>, trả Base64 (định dạng .sig trên release).</summary>
    public string SignToBase64(byte[] data)
    {
        var hash = SHA256.HashData(data);
        // ECDsa mặc định xuất IEEE P1363 (r||s 64 bytes với P-256) — trùng format CI ký
        return Convert.ToBase64String(_ecdsa.SignHash(hash));
    }

    public void Dispose() => _ecdsa.Dispose();
}
