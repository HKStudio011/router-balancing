using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Protocol <c>/v1/chat/completions</c> — giữ nguyên 100% hành vi 3A/3B/3C:
/// validate qua <see cref="ChatRequestValidator"/>, rewrite alias + inject
/// <c>stream_options.include_usage</c>, tee qua <see cref="UsageCapture"/> (spec v1-responses §3.1).
/// </summary>
internal sealed class ChatCompletionsProtocol : IProxyProtocol
{
    /// <inheritdoc/>
    public string UpstreamPath => "/v1/chat/completions";

    /// <summary>
    /// Validate rule 3A rồi map <see cref="ValidationFailure"/> → message/param 400 —
    /// đúng chuỗi error contract cũ (move switch từ handler, không đổi text).
    /// </summary>
    public ProxyValidationResult Validate(byte[] body)
    {
        var result = ChatRequestValidator.Validate(body);
        if (result.IsValid)
            return new ProxyValidationResult(true, result.ModelId, result.IsStream, null, null);

        var (message, param) = result.Failure switch
        {
            ValidationFailure.MissingModel =>
                ("Missing required parameter: 'model'.", "model"),
            ValidationFailure.MissingMessages =>
                ("Missing required parameter: 'messages'.", "messages"),
            _ => ("Invalid JSON body", null),
        };
        return new ProxyValidationResult(false, null, false, message, param);
    }

    /// <inheritdoc/>
    public (byte[] Body, bool ExpectsUsage) PrepareUpstreamBody(byte[] body, string realModelId,
        ProviderType providerType)
    {
        // Alias nội bộ (pin/combo) phải đổi về model id thật trước khi forward —
        // upstream không biết pin/ten combo (bug client: gửi nguyên alias bị ModelError).
        var upstreamBody = ChatBody.WithModel(body, realModelId);
        // Ép stream:true cho non-stream client (xem ChatBody.WithForcedStream): hết cửa sổ
        // im lặng tuyệt đối → middle không cắt vì idle giữa generation; client vẫn nhận JSON
        // nhờ aggregate (ForwardAsync: upstreamSse && !hasPipe). Provider bỏ qua stream:true
        // → trả JSON → tee giữ nguyên như cũ.
        upstreamBody = ChatBody.WithForcedStream(upstreamBody);
        // Yêu cầu upstream trả usage cho stream OpenAI (spec §6) — body gốc giữ nguyên ở queue/prepare
        return UsageCapture.WithIncludeUsage(upstreamBody, providerType);
    }

    /// <inheritdoc/>
    public Task<UsageCapture.TeeResult> TeeAsync(HttpContent source, Stream dest, CancellationToken ct) =>
        UsageCapture.TeeAsync(source, dest, ct);

    /// <inheritdoc/>
    public async Task<UsageCapture.TeeResult> AggregateSseAsync(HttpContent source, Stream dest,
        CancellationToken ct)
    {
        // Gộp chunk SSE → object chat.completion JSON một lần (OpenCode gate 403 stream:false
        // buộc upstream stream dù client non-stream — contract client giữ nguyên JSON).
        var stream = await source.ReadAsStreamAsync(ct);
        var state = new ChatSseAggregateState();
        var buffer = new byte[16 * 1024];
        byte[] carry = [];
        int read;
        while (true)
        {
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Dest chỉ ghi MỘT lần ở cuối — mọi lỗi đọc đều pre-content (marker)
                throw new UpstreamDiedBeforeContentException(ex);
            }
            if (read <= 0) break;

            state.FirstTokenAt ??= DateTimeOffset.UtcNow;
            var combined = new byte[carry.Length + read];
            Buffer.BlockCopy(carry, 0, combined, 0, carry.Length);
            Buffer.BlockCopy(buffer, 0, combined, carry.Length, read);

            var lineStart = 0;
            for (var i = 0; i < combined.Length; i++)
            {
                if (combined[i] != (byte)'\n') continue;
                state.HandleLine(combined.AsSpan(lineStart, i - lineStart));
                lineStart = i + 1;
            }
            carry = combined[lineStart..];
        }
        if (carry.Length > 0) state.HandleLine(carry);

        var json = state.BuildCompletionJson();
        var payload = Encoding.UTF8.GetBytes(json);
        await dest.WriteAsync(payload, ct);
        return new UsageCapture.TeeResult(state.Usage, state.FirstTokenAt, json);
    }

    /// <summary>
    /// Máy trạng thái gộp SSE chat → JSON non-stream: tích nội dung <c>delta.content</c>,
    /// role, finish_reason, id/model/created từ chunk đầu + usage last-wins. JSON hỏng
    /// → bỏ chunk, không throw (fail-open như <see cref="UsageCapture"/>).
    /// </summary>
    private sealed class ChatSseAggregateState
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

        public UsageCapture.Usage? Usage { get; private set; }
        public DateTimeOffset? FirstTokenAt { get; set; }

        public void HandleLine(ReadOnlySpan<byte> line)
        {
            if (line.Length > 0 && line[^1] == (byte)'\r') line = line[..^1];
            if (!line.StartsWith("data:"u8)) return;
            var payload = line["data:"u8.Length..];
            if (!payload.IsEmpty && payload[0] == (byte)' ') payload = payload[1..];
            if (payload.StartsWith("[DONE]"u8)) return;
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
                if (text is not null) _content.Append(text);
                AccumulateToolCalls(choice);
                _finishReason = ReadString(choice, "finish_reason") ?? _finishReason;
            }
            catch (JsonException)
            {
                // Chunk hỏng — bỏ qua, không làm hỏng cả stream (fail-open)
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
                    if (function.TryGetProperty("arguments", out var args)
                        && args.ValueKind == JsonValueKind.String
                        && args.GetString() is { } argText)
                        state.Arguments.Append(argText);
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
}
