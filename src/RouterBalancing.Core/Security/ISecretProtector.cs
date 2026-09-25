namespace RouterBalancing.Core.Security;

/// <summary>Mã hóa secret (API key) trước khi lưu DB — chống đọc trực tiếp file SQLite.</summary>
public interface ISecretProtector
{
    /// <summary>Mã hóa plaintext thành chuỗi an toàn lưu DB.</summary>
    string Protect(string plaintext);

    /// <summary>Giải mã chuỗi đã Protect.</summary>
    /// <exception cref="CryptographicException">Khi chuỗi bị đổi hoặc purpose sai.</exception>
    string Unprotect(string protectedValue);
}
