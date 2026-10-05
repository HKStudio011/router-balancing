using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Request profile cho NVIDIA NIM (host <c>integrate.api.nvidia.com</c>) — bỏ
/// <c>name</c>/<c>tool_name</c> khỏi message <c>role=tool</c>: NIM chấp nhận ToolMessage
/// schema hẹp hơn OpenAI-compatible chuẩn, giữ nguyên → 400 (hermes
/// <c>NvidiaProviderProfile.prepare_messages</c>). Attribution headers
/// (NVIDIA + OpenRouter) do <see cref="ProviderAttribution"/> gắn.
/// </summary>
public static class NvidiaRequestProfile
{
    private const string NvidiaHost = "integrate.api.nvidia.com";

    /// <summary>
    /// Chỉ áp cho provider OpenAI-type trỏ đúng host NIM cloud — local NIM
    /// (localhost/vùng khác) và provider khác không bị transform body oan.
    /// </summary>
    /// <param name="provider">Provider đích.</param>
    /// <returns><see langword="true"/> khi thuộc profile NVIDIA NIM.</returns>
    public static bool IsApplicable(Provider provider) =>
        provider.Type == ProviderType.OpenAI
        && Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, NvidiaHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Transform body (bỏ name/tool_name khỏi role=tool) — attribution headers do
    /// <see cref="ProviderAttribution"/> đảm nhiệm. Body không phải JSON hợp lệ → giữ nguyên.
    /// </summary>
    /// <param name="request">Request đã có URI — thay content nếu transform đổi bytes.</param>
    /// <param name="content">Body chat (không có với GET probe) — thay nếu transform đổi bytes.</param>
    public static void Apply(HttpRequestMessage request, HttpContent? content)
    {
        if (content is not null)
        {
            StripToolMessageFields(request, content);
        }
    }

    /// <summary>
    /// Copy-on-write như hermes: chỉ copy message bị mất field (không deep-copy tool output lớn);
    /// không có gì đổi → giữ nguyên content instance.
    /// </summary>
    private static void StripToolMessageFields(HttpRequestMessage request, HttpContent content)
    {
        // Content luôn là ByteArray/StringContent (in-memory) — task hoàn tất ngay,
        // không có rủi ro block chờ I/O mạng (cùng luận điểm với OpenCodeFreeFingerprint)
        var raw = content.ReadAsByteArrayAsync().GetAwaiter().GetResult();

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(Encoding.UTF8.GetString(raw));
        }
        catch (JsonException)
        {
            // Body không phải JSON hợp lệ — validator/upstream xử lý, không đụng vào
            return;
        }

        if (node is not JsonObject obj || obj["messages"] is not JsonArray messages) return;

        var changed = false;
        foreach (var item in messages)
        {
            if (item is not JsonObject message) continue;
            if (message["role"] is not JsonValue role
                || !role.TryGetValue<string>(out var roleName)
                || !string.Equals(roleName, "tool", StringComparison.Ordinal))
                continue;

            // name/tool_name hợp lệ với user/system nhưng NIM chối trên tool message
            if (message.Remove("name")) changed = true;
            if (message.Remove("tool_name")) changed = true;
        }

        if (!changed) return;

        ContentReplacer.Replace(request, content, obj.ToJsonString());
    }
}
