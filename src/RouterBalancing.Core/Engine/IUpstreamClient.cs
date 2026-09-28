using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>Gửi request chat completion tới upstream provider.</summary>
public interface IUpstreamClient
{
    /// <summary>
    /// POST body JSON thô tới <c>{BaseUrl}/v1/chat/completions</c> với key đã giải mã.
    /// Trả response NGAY KHI có đủ header — body còn stream, không buffer (bắt buộc cho SSE).
    /// </summary>
    /// <param name="provider">Provider đích (OpenAI-compatible).</param>
    /// <param name="apiKey">Plaintext key — người gọi đã resolve từ ProviderAccount.</param>
    /// <param name="body">Body JSON thô nguyên trạng từ client.</param>
    /// <param name="ct">Token hủy theo RequestAborted.</param>
    Task<HttpResponseMessage> PostChatCompletionAsync(Provider provider, string apiKey, byte[] body, CancellationToken ct);
}
