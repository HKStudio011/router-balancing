using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouterBalancing.Core.Dataset;

/// <summary>
/// Externalize các base64 lớn trong source-shaped request/completion thành binary files và references.
/// Transformer chạy trên writer path và thuần in-memory: không tạo thư mục, ghi file hay thay đổi capture.
/// </summary>
public static class DatasetPayloadTransformer
{
    /// <summary>
    /// Thay mọi payload base64 hợp lệ có phần encoded lớn hơn <see cref="DatasetRecordBuilder.InlineMaxFieldBytes"/>
    /// bằng <c>payload://</c> URL. Trả <see langword="null"/> khi không có payload đủ điều kiện để record giữ schema v1.
    /// </summary>
    public static DatasetPayloadTransformResult? Transform(DatasetCapture capture, string exportId)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(exportId);

        JsonNode? requestDocument;
        JsonNode? completionDocument;
        try
        {
            requestDocument = JsonNode.Parse(capture.ClientBody);
            completionDocument = capture.CompletionJson is null
                ? null
                : JsonNode.Parse(capture.CompletionJson);
        }
        catch (JsonException)
        {
            // RecordBuilder vẫn là nơi phân loại JSON lỗi; transformer không được đổi lỗi đó thành exception writer.
            return null;
        }

        if (requestDocument is null || completionDocument is null)
        {
            return null;
        }

        var state = new TransformState(exportId, DateOnly.FromDateTime(capture.FinishedAt.UtcDateTime));
        requestDocument = TransformNode(requestDocument, state, "request", string.Empty, null, null, null);
        completionDocument = TransformNode(completionDocument, state, "completion", string.Empty, null, null, null);

        if (state.Files.Count == 0)
        {
            return null;
        }

        return new DatasetPayloadTransformResult(
            requestDocument!, completionDocument!, state.Files, state.References);
    }

    private static JsonNode? TransformNode(
        JsonNode? node,
        TransformState state,
        string document,
        string pointer,
        string? nodeFieldName,
        string? containingObjectFieldName,
        JsonObject? containingObject)
    {
        switch (node)
        {
            case JsonObject obj:
                // Snapshot cho phép thay value tại property đang duyệt mà không làm invalid enumerator.
                foreach (var property in obj.ToList())
                {
                    var childPointer = pointer + "/" + EscapePointerSegment(property.Key);
                    var transformed = TransformNode(property.Value, state, document, childPointer,
                        property.Key, nodeFieldName, obj);
                    if (!ReferenceEquals(property.Value, transformed))
                    {
                        obj[property.Key] = transformed;
                    }
                }

                return obj;

            case JsonArray array:
                for (var index = 0; index < array.Count; index++)
                {
                    var transformed = TransformNode(array[index], state, document,
                        pointer + "/" + index, null, null, null);
                    if (!ReferenceEquals(array[index], transformed))
                    {
                        array[index] = transformed;
                    }
                }

                return array;

            case JsonValue value when value.TryGetValue<string>(out var text):
                if (!TryDecodeCandidate(text, nodeFieldName, containingObjectFieldName,
                        containingObject, out var mediaType, out var binary))
                {
                    return value;
                }

                var url = state.Externalize(document, pointer, mediaType, binary);
                return JsonValue.Create(url);

            default:
                return node;
        }
    }

    private static bool TryDecodeCandidate(
        string text,
        string? fieldName,
        string? containingObjectFieldName,
        JsonObject? containingObject,
        out string mediaType,
        out byte[] binary)
    {
        mediaType = string.Empty;
        binary = [];
        string encoded;

        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseDataUri(text, out mediaType, out encoded))
            {
                return false;
            }
        }
        else if (string.Equals(fieldName, "b64_json", StringComparison.Ordinal))
        {
            // b64_json không đảm bảo PNG trên mọi provider; sau decode phải nhận diện signature thật.
            mediaType = string.Empty;
            encoded = text;
        }
        else if (string.Equals(fieldName, "data", StringComparison.Ordinal)
                 && string.Equals(containingObjectFieldName, "input_audio", StringComparison.Ordinal))
        {
            mediaType = InputAudioMediaType(containingObject);
            encoded = text;
        }
        else
        {
            return false;
        }

        // Base64 là ASCII nên byte count này cũng chính là encoded length theo ngưỡng v1.
        if (Encoding.UTF8.GetByteCount(encoded) <= DatasetRecordBuilder.InlineMaxFieldBytes)
        {
            return false;
        }

        try
        {
            binary = Convert.FromBase64String(encoded);
            if (mediaType.Length == 0)
            {
                mediaType = DetectImageMediaType(binary);
            }

            return true;
        }
        catch (FormatException)
        {
            // Candidate hỏng vẫn là dữ liệu inline hợp lệ của record v1, không phải lỗi toàn record.
            binary = [];
            return false;
        }
    }

    private static bool TryParseDataUri(string text, out string mediaType, out string encoded)
    {
        const string marker = ";base64,";
        mediaType = string.Empty;
        encoded = string.Empty;

        var markerIndex = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= "data:".Length
            || !MediaTypeHeaderValue.TryParse(text["data:".Length..markerIndex], out var parsed)
            || parsed.MediaType is null)
        {
            return false;
        }

        // Giữ nguyên parameters (ví dụ charset) để consumer dựng lại data URI đúng semantic.
        mediaType = text["data:".Length..markerIndex];
        encoded = text[(markerIndex + marker.Length)..];
        return true;
    }

    private static string DetectImageMediaType(byte[] binary)
    {
        ReadOnlySpan<byte> pngSignature = [0x89, 0x50, 0x4E, 0x47];
        ReadOnlySpan<byte> jpegSignature = [0xFF, 0xD8, 0xFF];
        if (binary.AsSpan().StartsWith(pngSignature))
        {
            return "image/png";
        }

        if (binary.AsSpan().StartsWith(jpegSignature))
        {
            return "image/jpeg";
        }

        if (binary.Length >= 12
            && binary.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && binary.AsSpan(8, 4).SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return "application/octet-stream";
    }

    private static string InputAudioMediaType(JsonObject? inputAudio)
    {
        if (inputAudio?["format"] is JsonValue formatValue
            && formatValue.TryGetValue<string>(out var format))
        {
            switch (format.ToLowerInvariant())
            {
                case "wav":
                    return "audio/wav";
                case "mp3":
                    return "audio/mpeg";
                case "ogg":
                    return "audio/ogg";
                case "flac":
                    return "audio/flac";
            }
        }

        return "application/octet-stream";
    }

    private static string EscapePointerSegment(string segment) =>
        segment.Replace("~", "~0", StringComparison.Ordinal)
            .Replace("/", "~1", StringComparison.Ordinal);

    private static string BaseMediaType(string mediaType)
    {
        var parameterIndex = mediaType.IndexOf(';');
        var baseMediaType = parameterIndex >= 0 ? mediaType[..parameterIndex] : mediaType;
        return baseMediaType.Trim().ToLowerInvariant();
    }

    private static string FilePrefix(string mediaType)
    {
        if (mediaType.StartsWith("image/", StringComparison.Ordinal))
        {
            return "image";
        }

        if (mediaType.StartsWith("audio/", StringComparison.Ordinal))
        {
            return "audio";
        }

        return "file";
    }

    private static string FileExtension(string mediaType) => mediaType switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tiff",
        "image/svg+xml" => ".svg",
        "audio/wav" or "audio/wave" or "audio/x-wav" => ".wav",
        "audio/mpeg" => ".mp3",
        "audio/mp4" => ".m4a",
        "audio/ogg" => ".ogg",
        "audio/flac" => ".flac",
        "application/pdf" => ".pdf",
        "application/zip" => ".zip",
        "text/plain" => ".txt",
        _ => ".bin",
    };

    private sealed class TransformState(string exportId, DateOnly datasetDay)
    {
        public List<DatasetPayloadFile> Files { get; } = [];

        public List<DatasetPayloadReference> References { get; } = [];

        public string Externalize(string document, string pointer, string mediaType, byte[] binary)
        {
            var sha256 = Convert.ToHexString(SHA256.HashData(binary)).ToLowerInvariant();
            var baseMediaType = BaseMediaType(mediaType);
            var fileName = $"{FilePrefix(baseMediaType)}-{Files.Count:D3}-{sha256[..8]}{FileExtension(baseMediaType)}";
            var url = $"payload://{exportId}/{fileName}";
            var relativePath = DatasetPaths.PayloadRelativePath(datasetDay, exportId, fileName);

            Files.Add(new DatasetPayloadFile(relativePath, binary));
            References.Add(new DatasetPayloadReference(
                document, pointer, url, relativePath, "base64", mediaType, binary.LongLength, sha256));
            return url;
        }
    }
}
