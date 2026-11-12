using System.Text;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

/// <summary>
/// Phân loại 3 chiều lỗi gate OpenCode free (spec opencode-free-gate-v2 §5.9) thuần theo body:
/// shape-403 ("within OpenCode" — gate từ chối hình thể request) vs IP-abuse-403 (marker egress
/// bị chặn). Sidecar pass-through nguyên status + body nên C# là nơi duy nhất phân loại.
/// </summary>
public class OpencodeGateClassifierTests
{
    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void TestIsIpAbuse_MarkersCaseInsensitive()
    {
        // 3 marker hành vi + combo 2 phần, hoa/thường lộn xộn — scan phải không phân biệt case
        Assert.True(OpencodeGateClassifier.IsIpAbuse(Body(
            """{"error":{"message":"Detected ABUSIVE TRAFFIC from your network"}}""")));
        Assert.True(OpencodeGateClassifier.IsIpAbuse(Body(
            """{"error":{"message":"We saw Abuse Of Traffic on this connection"}}""")));
        Assert.True(OpencodeGateClassifier.IsIpAbuse(Body(
            """{"error":{"message":"ABUSE traffic detected"}}""")));
        // Combo "free models aren't available" + "from your current network" cũng case-insensitive
        Assert.True(OpencodeGateClassifier.IsIpAbuse(Body(
            """{"error":{"message":"FREE MODELS AREN'T AVAILABLE from Your Current Network"}}""")));
    }

    [Fact]
    public void TestIsIpAbuse_BothPartComboRequired()
    {
        // Nửa đầu combo một mình KHÔNG đủ (model hết chỗ cũng có thể nói câu này, không phải egress)
        Assert.False(OpencodeGateClassifier.IsIpAbuse(Body(
            """{"error":{"message":"free models aren't available right now"}}""")));
        // Nửa sau một mình cũng không đủ (câu mô tả network limit chung)
        Assert.False(OpencodeGateClassifier.IsIpAbuse(Body(
            """{"error":{"message":"requests from your current network are limited"}}""")));
        // Đủ cả hai phần → egress bị chặn
        Assert.True(OpencodeGateClassifier.IsIpAbuse(Body(
            """{"error":{"message":"free models aren't available from your current network"}}""")));
    }

    [Fact]
    public void TestIsShapeRefusal_WithinOpenCode()
    {
        // Shape-403 thật của gate (probe 2026-10-13) — deterministic, retry account/egress vô ích
        Assert.True(OpencodeGateClassifier.IsShapeRefusal(Body(
            """{"error":{"message":"OpenCode's free tier can only be used from within OpenCode","type":"FreeTierError"}}""")));
        // Body không mang marker shape → không nhận nhầm
        Assert.False(OpencodeGateClassifier.IsShapeRefusal(Body(
            """{"error":{"message":"forbidden"}}""")));
    }

    [Fact]
    public void TestIsIpAbuse_Plain403_False()
    {
        // 403 thường (không marker) — refinement giữ nguyên Fatal(Account) như trước
        Assert.False(OpencodeGateClassifier.IsIpAbuse(Body("""{"error":{"message":"forbidden"}}""")));
        // Shape-403 cũng KHÔNG phải IP-abuse: hai nhánh loại trừ nhau
        Assert.False(OpencodeGateClassifier.IsIpAbuse(Body(
            """{"error":{"message":"OpenCode's free tier can only be used from within OpenCode"}}""")));
        // 403 không body — không được ném
        Assert.False(OpencodeGateClassifier.IsIpAbuse([]));
    }
}
