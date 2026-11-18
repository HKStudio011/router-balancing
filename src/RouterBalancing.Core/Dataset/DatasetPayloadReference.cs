namespace RouterBalancing.Core.Dataset;

/// <summary>
/// Liên kết một giá trị base64 đã externalize với vị trí RFC 6901 trong source document.
/// </summary>
/// <param name="Document">Source document chứa giá trị: <c>request</c> hoặc <c>completion</c>.</param>
/// <param name="JsonPointer">JSON Pointer tới giá trị đã được thay bằng <paramref name="Url"/>.</param>
/// <param name="Url">URL payload ổn định trong source document.</param>
/// <param name="Path">Đường dẫn binary tương đối từ dataset root.</param>
/// <param name="Kind">Loại payload externalized; hiện luôn là <c>base64</c>.</param>
/// <param name="MediaType">Media type dùng khi khôi phục dữ liệu.</param>
/// <param name="Bytes">Số byte binary sau khi decode.</param>
/// <param name="Sha256">SHA-256 lowercase hex của binary.</param>
public sealed record DatasetPayloadReference(
    string Document, string JsonPointer, string Url, string Path, string Kind,
    string MediaType, long Bytes, string Sha256);
