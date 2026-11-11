using System.Text;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Phân loại 3 chiều lỗi gate OpenCode free (spec opencode-free-gate-v2 §5.9) từ body upstream:
/// shape-403 ("within OpenCode") = gate từ chối HÌNH THỂ request → deterministic, đổi
/// account/egress không cứu được; IP-abuse-403 (marker egress) = mạng hiện tại bị chặn →
/// phải đổi exit proxy. Sidecar pass-through nguyên status + body nên chỉ C# phân loại
/// (spec §5.9 "phân loại nằm ở C#"); tách class để test thuần theo body.
/// </summary>
public static class OpencodeGateClassifier
{
    // 3 câu marker egress bị chặn (probe/live issue). So khớp trên text đã lowercase nên
    // marker ở đây phải viết thường.
    private const string AbusiveTrafficMarker = "abusive traffic";
    private const string AbuseTrafficMarker = "abuse traffic";
    private const string AbuseOfTrafficMarker = "abuse of traffic";

    // Combo 2 phần: "free models aren't available" một mình còn có thể là model hết chỗ —
    // chỉ khi kèm "from your current network" mới khẳng định egress bị chặn (spec §5.9 #2).
    private const string FreeModelsUnavailableMarker = "free models aren't available";
    private const string CurrentNetworkMarker = "from your current network";

    /// <summary>Marker shape-403 thật của gate: "…free tier can only be used from within OpenCode".</summary>
    private const string WithinOpencodeMarker = "within opencode";

    /// <summary>
    /// Body 403 có phải IP-abuse không (egress bị chặn, không phải lỗi request): khớp một
    /// trong 3 marker hành vi, HOẶC khớp CẢ HAI phần của combo "free models aren't
    /// available" + "from your current network". Không phân biệt hoa/thường.
    /// </summary>
    /// <param name="body">Body lỗi upstream đã buffer (có thể rỗng).</param>
    public static bool IsIpAbuse(byte[] body)
    {
        var text = LowercaseText(body);
        return text.Contains(AbusiveTrafficMarker, StringComparison.Ordinal)
            || text.Contains(AbuseTrafficMarker, StringComparison.Ordinal)
            || text.Contains(AbuseOfTrafficMarker, StringComparison.Ordinal)
            || (text.Contains(FreeModelsUnavailableMarker, StringComparison.Ordinal)
                && text.Contains(CurrentNetworkMarker, StringComparison.Ordinal));
    }

    /// <summary>
    /// Body 403 có phải gate từ chối theo hình thể request không ("within OpenCode") —
    /// deterministic: retry cùng shape ở account/egress khác là vô nghĩa (spec §5.9 #1).
    /// Không phân biệt hoa/thường.
    /// </summary>
    /// <param name="body">Body lỗi upstream đã buffer (có thể rỗng).</param>
    public static bool IsShapeRefusal(byte[] body) =>
        LowercaseText(body).Contains(WithinOpencodeMarker, StringComparison.Ordinal);

    /// <summary>
    /// Decode UTF-8 + lowercase một lần cho cả body: marker là ASCII nên lowercase toàn bộ là
    /// đủ và rẻ (body lỗi nhỏ — vài trăm byte tới vài chục KB). Invalid UTF-8 → U+FFFD, không ném.
    /// </summary>
    private static string LowercaseText(byte[] body) =>
        Encoding.UTF8.GetString(body).ToLowerInvariant();
}
