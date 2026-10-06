using System.Text;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class SseErrorEventTests
{
    private const string DataPrefix = "data: ";

    private static string Frame(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    private static JsonObject ParseEvent(byte[] bytes)
    {
        var text = Frame(bytes);
        Assert.StartsWith(DataPrefix, text, StringComparison.Ordinal);
        Assert.EndsWith("\n\n", text, StringComparison.Ordinal);
        return JsonNode.Parse(text[DataPrefix.Length..^2])!.AsObject();
    }

    [Fact]
    public void Error_WhenGiven_ProducesTypeErrorEnvelope()
    {
        var bytes = SseErrorEvent.Error(
            404, "The model 'x' does not exist", "invalid_request_error", "model", "model_not_found");

        // So string chính xác từng byte (kể cả đuôi \n\n) — apostrophe phải giữ nguyên,
        // escape thành \u0027 là sai contract OpenAI (spec §4).
        const string expected =
            "data: {\"type\":\"error\",\"error\":{\"message\":\"The model 'x' does not exist\"," +
            "\"type\":\"invalid_request_error\",\"param\":\"model\",\"code\":\"model_not_found\"," +
            "\"status\":404}}\n\n";
        Assert.Equal(expected, Frame(bytes));
    }

    [Fact]
    public void Error_WhenParamNull_SerializesNull()
    {
        var bytes = SseErrorEvent.Error(502, "Upstream provider request failed", "server_error", null, null);

        const string expected =
            "data: {\"type\":\"error\",\"error\":{\"message\":\"Upstream provider request failed\"," +
            "\"type\":\"server_error\",\"param\":null,\"code\":null,\"status\":502}}\n\n";
        Assert.Equal(expected, Frame(bytes));
    }

    [Fact]
    public void Cancelled_ProducesRequestCancelledEvent()
    {
        var error = ParseEvent(SseErrorEvent.Cancelled())["error"]!.AsObject();

        // Shape tối giản đúng spec §3.5 — không status, không param
        Assert.Equal(new[] { "message", "type", "code" }, error.Select(p => p.Key).ToArray());
        Assert.Equal("Request cancelled.", error["message"]!.GetValue<string>());
        Assert.Equal("invalid_request_error", error["type"]!.GetValue<string>());
        Assert.Equal("request_cancelled", error["code"]!.GetValue<string>());
    }

    [Fact]
    public void ServerFault_ProducesServerErrorEvent()
    {
        var error = ParseEvent(SseErrorEvent.ServerFault())["error"]!.AsObject();

        Assert.Equal(new[] { "message", "type", "code" }, error.Select(p => p.Key).ToArray());
        Assert.Equal("Internal server error", error["message"]!.GetValue<string>());
        Assert.Equal("server_error", error["type"]!.GetValue<string>());
        Assert.Equal("server_error", error["code"]!.GetValue<string>());
    }

    [Fact]
    public void Passthrough_WhenBodyHasErrorKey_MergesTypeAndRetryAfter()
    {
        var body = Encoding.UTF8.GetBytes("""{"error":{"message":"rate limited"}}""");

        var evt = ParseEvent(SseErrorEvent.Passthrough(429, body, "30"));

        Assert.Equal("error", evt["type"]!.GetValue<string>());
        var error = evt["error"]!.AsObject();
        Assert.Equal("rate limited", error["message"]!.GetValue<string>());
        Assert.Equal("30", error["retry_after"]!.GetValue<string>());
    }

    [Fact]
    public void Passthrough_WhenBodyNotJson_FallsBackToUpstreamError()
    {
        var body = Encoding.UTF8.GetBytes("<html>oops</html>");

        var evt = ParseEvent(SseErrorEvent.Passthrough(400, body, null));

        Assert.Equal("error", evt["type"]!.GetValue<string>());
        var error = evt["error"]!.AsObject();
        Assert.Equal("upstream_error", error["type"]!.GetValue<string>());
        Assert.Equal(400, error["status"]!.GetValue<int>());
        Assert.StartsWith("<html>oops", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        // retryAfterHeader null → không thêm field retry_after
        Assert.False(error.ContainsKey("retry_after"));
    }

    [Fact]
    public void Passthrough_WhenBodyNotJsonWithRetryAfter_IncludesRetryAfter()
    {
        var body = Encoding.UTF8.GetBytes("<html>oops</html>");

        var error = ParseEvent(SseErrorEvent.Passthrough(503, body, "17"))["error"]!.AsObject();

        Assert.Equal("17", error["retry_after"]!.GetValue<string>());
    }

    [Fact]
    public void Passthrough_WhenBodyJsonButNoErrorKey_FallsBack()
    {
        var body = Encoding.UTF8.GetBytes("""{"message":"weird"}""");

        var evt = ParseEvent(SseErrorEvent.Passthrough(500, body, null));

        Assert.Equal("error", evt["type"]!.GetValue<string>());
        var error = evt["error"]!.AsObject();
        Assert.Equal("upstream_error", error["type"]!.GetValue<string>());
        Assert.Contains("weird", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public void Passthrough_WhenBodyHuge_MessageCappedAt64K()
    {
        // Message phải qua DecodeCapped (64KB, cắt byte trước decode) —
        // body nhiều MB không được bung thành string nguyên con trong JSON lỗi.
        var body = Encoding.UTF8.GetBytes(new string('a', 70 * 1024));

        var error = ParseEvent(SseErrorEvent.Passthrough(500, body, null))["error"]!.AsObject();
        var message = error["message"]!.GetValue<string>();

        Assert.EndsWith("[truncated]", message, StringComparison.Ordinal);
        Assert.True(message.Length <= 64 * 1024 + "[truncated]".Length,
            $"message.Length = {message.Length} vượt cap 64KB + marker");
    }
}
