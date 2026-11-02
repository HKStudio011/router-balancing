using RouterBalancing.Core.Notifications;

namespace router_balancing_test.Notifications;

/// <summary>
/// Test <see cref="ErrorAlertMessage"/> — translator là dictionary giả nên
/// message dựng ra kiểm chứng được từng ký tự, không phụ thuộc culture của máy CI.
/// </summary>
public class ErrorAlertMessageTests
{
    private const string ClusterTemplateVi = "{0} lỗi trong {1} giây · gần nhất: {2}";

    private static readonly Dictionary<string, string> VI = new()
    {
        ["notify.error.title"] = "router-balancing: lỗi",
        ["notify.error.cluster"] = ClusterTemplateVi,
    };

    /// <summary>Translator giả theo bảng VI — cùng hợp đồng với call site `key => L[key]`.</summary>
    private static Func<string, string> Translator { get; } = key => VI[key];

    [Fact]
    public void Build_SingleError_UsesErrorTextAsIs()
    {
        var message = ErrorAlertMessage.Build(Translator, 1, TimeSpan.FromSeconds(30), "Proxy start failed");

        Assert.Equal("Proxy start failed", message);
    }

    [Fact]
    public void Build_ManyErrors_UsesClusterTemplate()
    {
        const string last = "Request r1 failed: gpt → openai, HTTP 500, 120ms";

        var message = ErrorAlertMessage.Build(Translator, 12, TimeSpan.FromSeconds(30), last);

        Assert.Equal("12 lỗi trong 30 giây · gần nhất: " + last, message);
    }

    [Fact]
    public void Build_TruncatesMessageOver200Chars()
    {
        // Marker đặt ở cuối message dài — bản cắt 200 ký tự đầu không được chứa nó
        string longMessage = new string('a', 150) + new string('b', 100) + "TAIL-MARKER";

        var message = ErrorAlertMessage.Build(Translator, 5, TimeSpan.FromSeconds(30), longMessage);

        var expectedTail = longMessage[..ErrorAlertMessage.MaxMessageChars] + "…";
        Assert.Equal($"5 lỗi trong 30 giây · gần nhất: {expectedTail}", message);
        Assert.DoesNotContain("TAIL-MARKER", message);
    }

    [Fact]
    public void Truncate_Exactly200Chars_NoEllipsis()
    {
        var exact = new string('a', 200);

        Assert.Equal(exact, ErrorAlertMessage.Truncate(exact));

        var over = ErrorAlertMessage.Truncate(new string('a', 201));
        Assert.Equal(201, over.Length);
        Assert.EndsWith("…", over);
    }
}
