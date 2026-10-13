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
        while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
        {
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
    }
}
