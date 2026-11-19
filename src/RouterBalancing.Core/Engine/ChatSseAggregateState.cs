using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Máy trạng thái gộp SSE chat → JSON non-stream: tích nội dung <c>delta.content</c>,
/// role, finish_reason, id/model/created từ chunk đầu + usage last-wins. JSON hỏng
/// → bỏ chunk, không throw (fail-open như <see cref="UsageCapture"/>). Dùng chung cho
/// aggregate (client non-stream, upstream forced-stream) và tee capture completion
/// cho dataset export — budget <paramref name="contentBudgetChars"/> giới hạn phần
/// content + arguments tool_calls tích lũy; mặc định đếm ký tự cho client aggregation,
/// còn dataset bật <paramref name="budgetInUtf8Bytes"/> để đếm theo JSON UTF-8 đã encode.
/// Chạm budget thì ngừng tích lũy và đánh dấu <see cref="Overflowed"/> thay vì phình memory.
/// </summary>
internal sealed class ChatSseAggregateState(
    int? contentBudgetChars = null,
    bool budgetInUtf8Bytes = false)
{
    private readonly StringBuilder _content = new();
    // tool_calls stream theo index (OpenAI spec): id/name first-wins, arguments concat
    // từng delta — thiếu mảng này thì client non-stream nhận message rỗng (mất tool call)
    private readonly SortedDictionary<int, ToolCallState> _toolCalls = new();
    private string? _id;
    private string? _model;
    private string? _role;
    private string? _finishReason;
    private long _created;
    // [DONE] tường minh: finish_reason có thể vắng khi provider cắt cụt — cần cả hai
    // dấu hiệu để phân biệt "stream kết thúc" với "upstream đóng sạch giữa chừng".
    private bool _doneSeen;

    // Tổng ký tự hoặc JSON UTF-8 byte đã tích lũy (content + arguments), tùy mode;
    // budget null = không giới hạn (hành vi aggregate cũ y hệt, không tốn check).
    private readonly int? _budget = contentBudgetChars;
    private int _accumulated;

    public UsageCapture.Usage? Usage { get; private set; }
    public DateTimeOffset? FirstTokenAt { get; set; }

    /// <summary>Stream có kết thúc hợp lệ (<c>[DONE]</c> hoặc <c>finish_reason</c> khác null).</summary>
    public bool CompletedCleanly => _doneSeen || _finishReason is not null;

    /// <summary>
    /// True khi chạm <paramref name="contentBudgetChars"/> và đã ngừng tích lũy —
    /// completion xây từ trạng thái này là CẮT GIỮA, caller phải coi như không có
    /// completion tin cậy (dataset: bỏ record thay vì train trên dữ liệu dở dang).
    /// </summary>
    public bool Overflowed { get; private set; }

    public void HandleLine(ReadOnlySpan<byte> line)
    {
        if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
        if (!line.StartsWith("data:"u8)) return;
        var payload = line["data:"u8.Length..];
        if (!payload.IsEmpty && payload[0] == (byte)' ') payload = payload[1..];
        if (payload.StartsWith("[DONE]"u8))
        {
            _doneSeen = true;
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(payload.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            _id ??= ReadString(doc.RootElement, "id");
            _model ??= ReadString(doc.RootElement, "model");
            if (doc.RootElement.TryGetProperty("created", out var created)
                && created.ValueKind == JsonValueKind.Number && _created == 0)
                _created = created.GetInt64();

            if (doc.RootElement.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("prompt_tokens", out var prompt)
                && prompt.ValueKind == JsonValueKind.Number
                && usage.TryGetProperty("completion_tokens", out var completion)
                && completion.ValueKind == JsonValueKind.Number)
                Usage = new UsageCapture.Usage(prompt.GetInt32(), completion.GetInt32());

            if (!doc.RootElement.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0
                || choices[0] is not { ValueKind: JsonValueKind.Object } choice)
                return;
            _role = ReadDeltaString(choice, "role") ?? _role;
            var text = ReadDeltaString(choice, "content");
            if (text is not null)
            {
                var deltaSize = DeltaSize(text);
                if (TryAccumulate(deltaSize))
                {
                    _content.Append(text);
                    _accumulated += deltaSize;
                }
            }
            AccumulateToolCalls(choice);
            _finishReason = ReadString(choice, "finish_reason") ?? _finishReason;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // Chunk hỏng — bỏ qua, không làm hỏng cả stream (fail-open). GetInt64/GetInt32
            // trên JSON Number vượt phạm vi (vd created: 1e30) ném FormatException, số sai
            // kiểu ném InvalidOperationException — ĐỀU không phải JsonException; bắt thiếu
            // là fail-open hole vì byte đã forward cho client trước khi HandleLine chạy.
        }
    }

    public string BuildCompletionJson()
    {
        var message = new JsonObject { ["role"] = _role ?? "assistant" };
        if (_content.Length > 0) message["content"] = _content.ToString();
        if (_toolCalls.Count > 0)
        {
            var toolCalls = new JsonArray();
            foreach (var (_, state) in _toolCalls)
            {
                toolCalls.Add(new JsonObject
                {
                    ["id"] = state.Id ?? string.Empty,
                    ["type"] = state.Type ?? "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = state.Name ?? string.Empty,
                        ["arguments"] = state.Arguments.ToString(),
                    },
                });
            }
            message["tool_calls"] = toolCalls;
            // Shape OpenAI: tool_calls message luôn kèm content null tường minh
            if (_content.Length == 0) message["content"] = null;
        }
        var choice = new JsonObject
        {
            ["index"] = 0,
            ["message"] = message,
            ["finish_reason"] = _finishReason ?? "stop",
        };
        var completion = new JsonObject
        {
            ["id"] = _id ?? "chatcmpl-aggregated",
            ["object"] = "chat.completion",
            ["created"] = _created == 0
                ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() : _created,
            ["model"] = _model ?? string.Empty,
            ["choices"] = new JsonArray(choice),
        };
        if (Usage is { } usage)
        {
            completion["usage"] = new JsonObject
            {
                ["prompt_tokens"] = usage.PromptTokens,
                ["completion_tokens"] = usage.CompletionTokens,
            };
        }
        return completion.ToJsonString();
    }

    // Dataset phải trả giá theo chính default encoder của BuildCompletionJson. Đếm từng delta
    // độc lập có thể overflow sớm khi tách surrogate pair, nhưng không bao giờ vượt budget muộn.
    private int DeltaSize(string text) => budgetInUtf8Bytes
        ? Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(text)) - 2
        : text.Length;

    // Chặn append khi đã chạm budget — sau Overflowed KHÔNG tích lũy thêm dù mảnh
    // nhỏ (ngừng hẳn, không "lấp đầy phần còn lại") vì completion đã mất tính nguyên vẹn.
    private bool TryAccumulate(int addedSize)
    {
        if (Overflowed) return false;
        if (_budget is null || _accumulated + addedSize <= _budget) return true;
        Overflowed = true;
        return false;
    }

    private static string? ReadString(JsonElement holder, string property) =>
        holder.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Chunk SSE chat lồng delta: choices[0].delta.{content,role}
    private static string? ReadDeltaString(JsonElement choice, string property)
    {
        if (!choice.TryGetProperty("delta", out var delta)
            || delta.ValueKind != JsonValueKind.Object)
            return null;
        return ReadString(delta, property);
    }

    // Gộp delta.tool_calls theo index: id/type/name chỉ nhận giá trị đầu (first-wins),
    // arguments nối tiếp từng mảnh — đúng contract streaming tool_calls của OpenAI.
    private void AccumulateToolCalls(JsonElement choice)
    {
        if (!choice.TryGetProperty("delta", out var delta)
            || delta.ValueKind != JsonValueKind.Object
            || !delta.TryGetProperty("tool_calls", out var toolCalls)
            || toolCalls.ValueKind != JsonValueKind.Array)
            return;

        // index thiếu (provider không chuẩn) → fallback theo thứ tự xuất hiện trong delta
        var position = 0;
        foreach (var element in toolCalls.EnumerateArray())
        {
            var fallbackIndex = position++;
            if (element.ValueKind != JsonValueKind.Object) continue;

            var index = element.TryGetProperty("index", out var indexValue)
                && indexValue.ValueKind == JsonValueKind.Number
                ? indexValue.GetInt32()
                : fallbackIndex;
            if (!_toolCalls.TryGetValue(index, out var state))
            {
                state = new ToolCallState();
                _toolCalls[index] = state;
            }

            if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                state.Id ??= id.GetString();
            if (element.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String)
                state.Type ??= type.GetString();
            if (element.TryGetProperty("function", out var function)
                && function.ValueKind == JsonValueKind.Object)
            {
                if (function.TryGetProperty("name", out var name)
                    && name.ValueKind == JsonValueKind.String)
                    state.Name ??= name.GetString();
                // Arguments cũng nằm trong budget chung với content — stream tool call dài
                // vô hạn không được phình memory cho dataset export.
                if (function.TryGetProperty("arguments", out var args)
                    && args.ValueKind == JsonValueKind.String
                    && args.GetString() is { } argText)
                {
                    var deltaSize = DeltaSize(argText);
                    if (TryAccumulate(deltaSize))
                    {
                        state.Arguments.Append(argText);
                        _accumulated += deltaSize;
                    }
                }
            }
        }
    }

    /// <summary>Tool call đã tích lũy — arguments là các mảnh delta đã nối.</summary>
    private sealed class ToolCallState
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Name { get; set; }
        public StringBuilder Arguments { get; } = new();
    }
}
