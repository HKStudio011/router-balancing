using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>Gửi request JSON thô tới upstream provider theo path của protocol.</summary>
public interface IUpstreamClient
{
    /// <summary>
    /// POST body JSON thô tới <c>{BaseUrl}{path}</c> với key đã giải mã.
    /// Trả response NGAY KHI có đủ header — body còn stream, không buffer (bắt buộc cho SSE).
    /// </summary>
    /// <param name="provider">Provider đích (OpenAI-compatible).</param>
    /// <param name="apiKey">Plaintext key — người gọi đã resolve từ ProviderAccount.</param>
    /// <param name="path">Path upstream từ <see cref="IProxyProtocol.UpstreamPath"/> (đã leading slash).</param>
    /// <param name="body">Body JSON thô nguyên trạng từ client.</param>
    /// <param name="ct">Token hủy theo RequestAborted.</param>
    Task<HttpResponseMessage> PostAsync(Provider provider, string apiKey, string path, byte[] body, CancellationToken ct);
}
