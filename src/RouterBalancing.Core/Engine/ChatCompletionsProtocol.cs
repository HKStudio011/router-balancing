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
}
