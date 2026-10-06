using System.Text.Json;

namespace RouterBalancing.Core.Engine;

/// <summary>Lý do validate chat request thất bại — map sang error contract (spec 3A §4).</summary>
public enum ValidationFailure
{
    /// <summary>Request hợp lệ.</summary>
    None = 0,

    /// <summary>Body không parse được JSON (kể cả body rỗng).</summary>
    InvalidJson = 1,

    /// <summary>Thiếu / sai kiểu / rỗng trường <c>model</c>.</summary>
    MissingModel = 2,

    /// <summary>Thiếu / sai kiểu / rỗng trường <c>messages</c>.</summary>
    MissingMessages = 3,
}

/// <summary>Kết quả validate: thành công kèm model id cần resolve, hoặc lý do thất bại.</summary>
/// <param name="Failure">Lý do thất bại (<see cref="ValidationFailure.None"/> khi hợp lệ).</param>
/// <param name="ModelId">Model id trích được từ body; <see langword="null"/> khi failure.</param>
/// <param name="IsStream">
/// Body có <c>"stream": true</c> (literal) hay không — luôn <see langword="false"/> khi failure;
/// dùng để flush header sớm cho stream (spec early-headers).
/// </param>
public readonly record struct ValidationResult(ValidationFailure Failure, string? ModelId, bool IsStream)
{
    /// <summary>True khi request hợp lệ (<see cref="Failure"/> == <see cref="ValidationFailure.None"/>).</summary>
    public bool IsValid => Failure == ValidationFailure.None;
}

/// <summary>
/// Validate body chat request theo rule tối thiểu (spec 3A §3) — chỉ check presence/type,
/// không interpret nội dung: body được forward nguyên từng byte ở tầng handler.
/// </summary>
public static class ChatRequestValidator
{
    /// <summary>
    /// Parse <paramref name="body"/> và chạy rule V1–V3.
    /// </summary>
    /// <param name="body">Body thô nhận từ client (kể cả rỗng).</param>
    /// <returns>Thành công kèm id model; hoặc failure reason cho error contract.</returns>
    public static ValidationResult Validate(byte[] body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Bắt cả body rỗng (Parse ném cho 0 byte) — coi như JSON hỏng (V1)
            return new ValidationResult(ValidationFailure.InvalidJson, null, false);
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Root không phải object (array/scalar/null) → không có trường model (V2).
            // TryGetProperty ném InvalidOperationException trên non-object nên phải chặn ở đây.
            if (root.ValueKind != JsonValueKind.Object)
                return new ValidationResult(ValidationFailure.MissingModel, null, false);

            if (!root.TryGetProperty("model", out var model)
                || model.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(model.GetString()))
                return new ValidationResult(ValidationFailure.MissingModel, null, false);

            if (!root.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array
                || messages.GetArrayLength() == 0)
                return new ValidationResult(ValidationFailure.MissingMessages, null, false);

            // Chỉ literal true mới bật stream — thiếu/sai kiểu → non-stream (mặc định
            // an toàn: consumer chỉ flush header sớm khi chắc chắn request là SSE)
            var isStream = root.TryGetProperty("stream", out var stream)
                && stream.ValueKind == JsonValueKind.True;
            // ValueKind == String đã kiểm ở trên → GetString() không null (không cần !)
            return new ValidationResult(ValidationFailure.None, model.GetString(), isStream);
        }
    }
}
