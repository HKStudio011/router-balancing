using System.Text;
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
    public ProxyEndpoint Endpoint => ProxyEndpoint.Chat;

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
    public Task<UsageCapture.TeeResult> TeeAsync(HttpContent source, Stream dest, CancellationToken ct,
        bool captureCompletion = false) =>
        UsageCapture.TeeAsync(source, dest, ct, captureCompletion);

    /// <inheritdoc/>
    public async Task<UsageCapture.TeeResult> AggregateSseAsync(HttpContent source, Stream dest,
        CancellationToken ct)
    {
        // Gộp chunk SSE → object chat.completion JSON một lần (OpenCode gate 403 stream:false
        // buộc upstream stream dù client non-stream — contract client giữ nguyên JSON).
        var stream = await source.ReadAsStreamAsync(ct);
        // Budget null: aggregate phục vụ CLIENT (không phải export) — không giới hạn tích lũy,
        // hành vi y hệt nested class cũ. Completion cho dataset đi đường TeeAsync captureCompletion.
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

        // EOF sạch nhưng thiếu dấu kết thúc hợp lệ = upstream cắt cụt giữa chừng: tool_call
        // arguments có thể còn dở. Chưa ghi byte nào cho client nên đây là PRE-CONTENT →
        // bọc marker (retry/failover) thay vì trả JSON tool call bị lệch tham số.
        if (!state.CompletedCleanly)
            throw new UpstreamDiedBeforeContentException(new UpstreamTruncatedException());

        var json = state.BuildCompletionJson();
        var payload = Encoding.UTF8.GetBytes(json);
        await dest.WriteAsync(payload, ct);
        return new UsageCapture.TeeResult(state.Usage, state.FirstTokenAt, json);
    }
}
