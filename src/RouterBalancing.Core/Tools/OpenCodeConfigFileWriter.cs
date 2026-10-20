using System.Text;
using System.Text.Json.Nodes;

namespace RouterBalancing.Core.Tools;

/// <summary>Kết quả ghi config opencode.</summary>
/// <param name="Path">Đường dẫn tuyệt đối của file đã ghi.</param>
/// <param name="BackupPath">Đường dẫn file <c>.bak</c>; <see langword="null"/> khi file đích chưa tồn tại (tạo mới).</param>
public sealed record OpenCodeConfigWriteResult(string Path, string? BackupPath);

/// <summary>
/// Ghi block <c>router-balancing</c> vào file config opencode: backup <c>.bak</c> rồi ghi tạm
/// và đổi tên (atomic), phần còn lại của file giữ nguyên byte (spec §9.8).
/// </summary>
public interface IOpenCodeConfigFileWriter
{
    /// <summary>
    /// Thay block <c>providers["router-balancing"]</c> trong file tại <paramref name="path"/>.
    /// File chưa tồn tại → tạo thư mục + file mới, không backup.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="path"/> rỗng.</exception>
    /// <exception cref="OpenCodeConfigFormatException">File hiện có không phải JSON object hợp lệ.</exception>
    Task<OpenCodeConfigWriteResult> WriteAsync(
        string path, JsonObject providerBlock, CancellationToken ct = default);
}

/// <inheritdoc cref="IOpenCodeConfigFileWriter"/>
public sealed class OpenCodeConfigFileWriter : IOpenCodeConfigFileWriter
{
    // Strict UTF-8: file lạ encoding phải nổ thành lỗi rõ, không ghi đè bằng text đã hỏng.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);

    /// <inheritdoc/>
    public async Task<OpenCodeConfigWriteResult> WriteAsync(
        string path, JsonObject providerBlock, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(providerBlock);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (!File.Exists(fullPath))
        {
            // Upsert("{}") lo luôn phần chèn providers — cùng một đường splice với file có sẵn.
            var created = OpenCodeConfigSplicer.Upsert("{}", providerBlock);
            await File.WriteAllTextAsync(fullPath, created, Utf8NoBom, ct);
            return new OpenCodeConfigWriteResult(fullPath, BackupPath: null);
        }

        var raw = await File.ReadAllBytesAsync(fullPath, ct);
        var hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        var offset = hasBom ? 3 : 0;
        string original;
        try
        {
            original = Utf8NoBom.GetString(raw, offset, raw.Length - offset);
        }
        catch (DecoderFallbackException ex)
        {
            throw new OpenCodeConfigFormatException("opencode config is not valid UTF-8.", ex);
        }

        // Splice TRƯỚC khi backup/ghi: file hỏng thì không được tạo .bak, cũng không đụng file gốc.
        var updated = OpenCodeConfigSplicer.Upsert(original, providerBlock);

        var backupPath = fullPath + ".bak";
        File.Copy(fullPath, backupPath, overwrite: true);

        var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(tempPath, updated, hasBom ? Utf8WithBom : Utf8NoBom, ct);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }

        return new OpenCodeConfigWriteResult(fullPath, backupPath);
    }
}
