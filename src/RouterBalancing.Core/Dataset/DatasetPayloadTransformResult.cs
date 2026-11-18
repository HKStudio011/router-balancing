using System.Text.Json.Nodes;

namespace RouterBalancing.Core.Dataset;

/// <summary>Một binary payload đã decode, chờ writer lưu theo đường dẫn tương đối.</summary>
/// <param name="RelativePath">Đường dẫn từ dataset root; transformer không tự ghi file.</param>
/// <param name="Content">Toàn bộ binary đã decode từ base64.</param>
public sealed record DatasetPayloadFile(string RelativePath, byte[] Content);

/// <summary>Kết quả externalize base64 gồm source documents, binary files và references tương ứng.</summary>
/// <param name="RequestDocument">JSON nguyên hình của request, chỉ thay các payload đã externalize.</param>
/// <param name="CompletionDocument">JSON nguyên hình của completion, chỉ thay các payload đã externalize.</param>
/// <param name="Files">Binary payloads chờ writer lưu.</param>
/// <param name="References">Metadata resolve từng payload trong source documents.</param>
public sealed record DatasetPayloadTransformResult(
    JsonNode RequestDocument,
    JsonNode CompletionDocument,
    IReadOnlyList<DatasetPayloadFile> Files,
    IReadOnlyList<DatasetPayloadReference> References);
