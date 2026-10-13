using RouterBalancing.Core.Update;

namespace router_balancing_test.Update;

public class PackageSignatureVerifierTests
{
    [Fact]
    public void Verify_ReturnsTrue_WhenSignatureValid()
    {
        using var pair = new TestSigningKey();
        var package = WriteTempFile("PK-valid-package-bytes");
        var signature = SignToFile(pair, package);

        try
        {
            Assert.True(PackageSignatureVerifier.Verify(package, signature, pair.PublicKeyBase64));
        }
        finally
        {
            File.Delete(package);
            File.Delete(signature);
        }
    }

    [Fact]
    public void Verify_ReturnsFalse_WhenPackageTampered()
    {
        // Threat model #3: attacker sửa nội dung zip sau khi ký — hash lệch → verify fail
        using var pair = new TestSigningKey();
        var package = WriteTempFile("PK-original-package");
        var signature = SignToFile(pair, package);
        try
        {
            File.WriteAllText(package, "PK-tampered-package");

            Assert.False(PackageSignatureVerifier.Verify(package, signature, pair.PublicKeyBase64));
        }
        finally
        {
            File.Delete(package);
            File.Delete(signature);
        }
    }

    [Fact]
    public void Verify_ReturnsFalse_WhenSignedByDifferentKey()
    {
        using var signer = new TestSigningKey();
        using var other = new TestSigningKey();
        var package = WriteTempFile("PK-package");
        var signature = SignToFile(signer, package);

        try
        {
            Assert.False(PackageSignatureVerifier.Verify(package, signature, other.PublicKeyBase64));
        }
        finally
        {
            File.Delete(package);
            File.Delete(signature);
        }
    }

    [Fact]
    public void Verify_ReturnsFalse_WhenSignatureNotBase64()
    {
        using var pair = new TestSigningKey();
        var package = WriteTempFile("PK-package");
        var signature = WriteTempFile("!!! not base64 !!!");

        try
        {
            Assert.False(PackageSignatureVerifier.Verify(package, signature, pair.PublicKeyBase64));
        }
        finally
        {
            File.Delete(package);
            File.Delete(signature);
        }
    }

    [Fact]
    public void Verify_ReturnsFalse_WhenFileMissing()
    {
        using var pair = new TestSigningKey();
        var missing = Path.Combine(Path.GetTempPath(), $"rb-missing-{Guid.NewGuid():N}");

        Assert.False(PackageSignatureVerifier.Verify(missing, missing, pair.PublicKeyBase64));
    }

    [Fact]
    public void Verify_ReturnsFalse_WhenPublicKeyMalformed()
    {
        var package = WriteTempFile("PK-package");
        var signature = WriteTempFile("AAAA");

        try
        {
            Assert.False(PackageSignatureVerifier.Verify(package, signature, "not-a-public-key"));
        }
        finally
        {
            File.Delete(package);
            File.Delete(signature);
        }
    }

    private static string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"rb-sigtest-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, content);
        return path;
    }

    private static string SignToFile(TestSigningKey pair, string packagePath)
    {
        var path = packagePath + ".sig";
        File.WriteAllText(path, pair.SignToBase64(File.ReadAllBytes(packagePath)));
        return path;
    }
}
