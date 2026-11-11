using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Nhận diện provider thuộc lane OpenCode (host <c>opencode.ai</c>) — chỉ còn LANE SELECTION:
/// phân loại 403/429 của gate (<see cref="Engine.ProxyRequestHandler"/>) và lane provenance của
/// dataset (<see cref="Dataset.DatasetLane"/>).
/// Từ v2, mọi shaping (lane split, prompt injection, đổi tên tool + official tool set, restore
/// tên tool trong SSE) do Go sidecar <c>sidecar/opencode-proxy</c> đảm nhiệm; fingerprint dựng
/// ở phía host (UA/x-opencode-*/quartet decoy) đã bị xóa vì gate v2 không còn nhận shape v1.
/// </summary>
public static class OpenCodeFreeFingerprint
{
    /// <summary>
    /// Host <c>opencode.ai</c>: gate kiểm theo client fingerprint chứ không phân biệt auth — áp
    /// cho cả lane key rỗng (free) lẫn lane key thật (xem <c>OpenCodeSidecarHandler</c>). BaseUrl
    /// không parse được thành URI tuyệt đối → không thuộc lane (request đi đường thường).
    /// </summary>
    public static bool IsApplicable(Provider provider) =>
        provider.Type == ProviderType.OpenAI
        && Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri)
        && string.Equals(uri.Host, "opencode.ai", StringComparison.OrdinalIgnoreCase);
}
