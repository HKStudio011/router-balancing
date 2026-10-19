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
    public async Task<HttpResponseMessage> PostAsync(
        Provider provider, string apiKey, string path, byte[] body, string idempotencyKey,
        CancellationToken ct)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = ProviderRequestFactory.Create(
            provider, apiKey, path, HttpMethod.Post, content, idempotencyKey);

        // ResponseHeadersRead: hoàn tất khi đủ header, body stream tiếp — bắt buộc cho SSE
        var response = await http.CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // OpenCode lane: request đã bị canonicalize tên quartet (Bash→bash) — response phải
        // map ngược về đúng tên client đăng ký, nếu không model trả "bash" trong khi client
        // chỉ biết "Bash" → "invalid tool"/lệch schema parameter (drift mỗi bản opencode).
        if (OpenCodeFreeFingerprint.RestoreMapFor(provider, body) is { } restore)
            response.Content = new ToolNameRestoreContent(response.Content, restore);

        return response;
    }
}
