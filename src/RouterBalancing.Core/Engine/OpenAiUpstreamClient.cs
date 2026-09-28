using System.Net.Http.Headers;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace RouterBalancing.Core.Engine;

/// <inheritdoc/>
public sealed class OpenAiUpstreamClient(IHttpClientFactory http) : IUpstreamClient
{
    /// <summary>
    /// Tên named HttpClient cho chat streaming — Timeout vô hạn, đăng ký tại
    /// <c>ProxyApp.ConfigureServices</c>. Tách khỏi "provider-probe" (10s) vì
    /// timeout đó sẽ cắt SSE giữa chừng.
    /// </summary>
    public const string HttpClientName = "upstream";

    /// <inheritdoc/>
    public async Task<HttpResponseMessage> PostChatCompletionAsync(
        Provider provider, string apiKey, byte[] body, CancellationToken ct)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = ProviderRequestFactory.Create(
            provider, apiKey, "/v1/chat/completions", HttpMethod.Post, content);

        // ResponseHeadersRead: hoàn tất khi đủ header, body stream tiếp — bắt buộc cho SSE
        return await http.CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}
