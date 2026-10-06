using System.Text;
using System.Text.Json;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class ResponsesProtocolTests
{
    private static IProxyProtocol Protocol => ProxyProtocols.Responses;

    private static byte[] B(string json) => Encoding.UTF8.GetBytes(json);

    [Theory]
    [InlineData("""{"input":"hi"}""")]
    [InlineData("""{"model":123,"input":"hi"}""")]
    [InlineData("""{"model":"","input":"hi"}""")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    public void Validate_WhenModelMissing_Returns400Message(string json)
    {
        // Root non-object cũng rơi vào nhánh này — TryGetProperty ném nếu không chặn trước
        var result = Protocol.Validate(B(json));

        Assert.False(result.IsValid);
        Assert.Null(result.ModelId);
        Assert.Equal("Missing required parameter: 'model'.", result.ErrorMessage);
        Assert.Equal("model", result.ErrorParam);
    }

    [Theory]
    [InlineData("""{"model":"gpt-4o"}""")]
    [InlineData("""{"model":"gpt-4o","input":null}""")]
    [InlineData("""{"model":"gpt-4o","input":""}""")]
    [InlineData("""{"model":"gpt-4o","input":[]}""")]
    public void Validate_WhenInputMissing_Returns400ForInput(string json)
    {
        var result = Protocol.Validate(B(json));

        Assert.False(result.IsValid);
        Assert.Equal("Missing required parameter: 'input'.", result.ErrorMessage);
        Assert.Equal("input", result.ErrorParam);
    }

    [Theory]
    [InlineData("""{"model":"gpt-4o","input":42}""")]
    [InlineData("""{"model":"gpt-4o","input":true}""")]
    [InlineData("""{"model":"gpt-4o","input":{"text":"hi"}}""")]
    public void Validate_WhenInputNumber_Returns400ForInput(string json)
    {
        var result = Protocol.Validate(B(json));

        Assert.False(result.IsValid);
        Assert.Equal("Missing required parameter: 'input'.", result.ErrorMessage);
        Assert.Equal("input", result.ErrorParam);
    }

    [Fact]
    public void Validate_WhenInputString_AcceptsAndReadsStreamLiteralTrueOnly()
    {
        var streamOn = Protocol.Validate(B("""{"model":"gpt-4o","input":"hi","stream":true}"""));

        Assert.True(streamOn.IsValid);
        Assert.Equal("gpt-4o", streamOn.ModelId);
        Assert.True(streamOn.IsStream);

        foreach (var json in new[]
        {
            """{"model":"gpt-4o","input":"hi","stream":"1"}""",
            """{"model":"gpt-4o","input":"hi","stream":"TRUE"}""",
            """{"model":"gpt-4o","input":"hi","stream":1}""",
        })
        {
            var off = Protocol.Validate(B(json));

            Assert.True(off.IsValid);
            Assert.False(off.IsStream);
        }
    }

    [Fact]
    public void Validate_WhenInputArrayNonEmpty_Accepts()
    {
        // input dạng array of items + field phụ trợ (tools/reasoning) — opaque, không kiểm nội dung
        var result = Protocol.Validate(B(
            """{"model":"gpt-4o","input":[{"role":"user","content":"hi"}],"tools":[{"type":"web_search"}],"reasoning":{"effort":"low"},"max_output_tokens":100}"""));

        Assert.True(result.IsValid);
        Assert.Equal("gpt-4o", result.ModelId);
        Assert.False(result.IsStream);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    public void Validate_WhenInvalidJson_ReturnsInvalidJson(string json)
    {
        var result = Protocol.Validate(B(json));

        Assert.False(result.IsValid);
        Assert.Equal("Invalid JSON body", result.ErrorMessage);
        Assert.Null(result.ErrorParam);
    }

    [Fact]
    public void PrepareUpstreamBody_SwapsModelInjectsNothingElse()
    {
        var body = B("""{"model":"my-combo","input":"hi","stream":true}""");

        var (rewritten, expectsUsage) = Protocol.PrepareUpstreamBody(body, "gpt-4o-real", ProviderType.OpenAI);

        Assert.False(expectsUsage);
        using var doc = JsonDocument.Parse(rewritten);
        Assert.Equal("gpt-4o-real", doc.RootElement.GetProperty("model").GetString());
        // Responses API luôn kèm usage trong event terminal — cấm inject stream_options (spec §4.2)
        Assert.False(doc.RootElement.TryGetProperty("stream_options", out _));
        Assert.Equal("hi", doc.RootElement.GetProperty("input").GetString());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
    }

    // ─── TeeAsync: parse usage/TTFT non-stream + SSE (spec v1-responses §4.3/§4.4) ───

    [Fact]
    public async Task Tee_NonStreamJson_ExtractsUsageAndResponseBody()
    {
        const string json = """{"id":"resp_1","status":"completed","usage":{"input_tokens":11,"output_tokens":7}}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var dest = new MemoryStream();

        var result = await Protocol.TeeAsync(content, dest, CancellationToken.None);

        Assert.Equal(json, Encoding.UTF8.GetString(dest.ToArray()));   // byte-forward giữ nguyên
        Assert.NotNull(result.Usage);
        Assert.Equal(11, result.Usage.PromptTokens);                   // input_tokens → PromptTokens
        Assert.Equal(7, result.Usage.CompletionTokens);                // output_tokens → CompletionTokens
        Assert.Equal(json, result.ResponseBody);
        Assert.Null(result.FirstTokenAt);                              // non-stream: không có token đầu
    }

    [Fact]
    public async Task Tee_NonStreamJson_NoUsage_FailsOpen()
    {
        const string json = """{"id":"resp_1","status":"completed"}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var dest = new MemoryStream();

        var result = await Protocol.TeeAsync(content, dest, CancellationToken.None);

        Assert.Null(result.Usage);
        Assert.Equal(json, Encoding.UTF8.GetString(dest.ToArray()));
        Assert.Equal(json, result.ResponseBody);
    }

    // SSE chuẩn Responses API: cặp `event:`/`data:` phân cách bởi dòng trống, kèm comment keep-alive.
    private const string CompletedSse =
        "event: response.created\n" +
        "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"status\":\"in_progress\"}}\n\n" +
        ": keep-alive\n\n" +
        "event: response.output_text.delta\n" +
        "data: {\"type\":\"response.output_text.delta\",\"output_index\":0,\"content_index\":0,\"delta\":\"Hello\"}\n\n" +
        "event: response.output_text.done\n" +
        "data: {\"type\":\"response.output_text.done\",\"text\":\"Hello world\"}\n\n" +
        "event: response.completed\n" +
        "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"status\":\"completed\",\"usage\":{\"input_tokens\":11,\"output_tokens\":7}}}\n\n";

    [Fact]
    public async Task Tee_Stream_UsageFromCompletedEvent()
    {
        using var content = new StringContent(CompletedSse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await Protocol.TeeAsync(content, dest, CancellationToken.None);

        Assert.Equal(CompletedSse, Encoding.UTF8.GetString(dest.ToArray()));
        Assert.NotNull(result.Usage);
        Assert.Equal(11, result.Usage.PromptTokens);
        Assert.Equal(7, result.Usage.CompletionTokens);
        Assert.NotNull(result.FirstTokenAt);   // có output_text.delta trước completed → TTFT phải có
    }

    [Fact]
    public async Task Tee_Stream_FirstOutputTextDeltaSetsFirstToken()
    {
        // Chỉ có delta, không có .done — bản thân event delta phải thiết lập TTFT (spec §4.4)
        const string sse =
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"Hi\"}\n\n";
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var before = DateTimeOffset.UtcNow;
        var result = await Protocol.TeeAsync(content, dest, CancellationToken.None);
        var after = DateTimeOffset.UtcNow;

        Assert.NotNull(result.FirstTokenAt);
        Assert.InRange(result.FirstTokenAt!.Value, before, after);
    }

    [Fact]
    public async Task Tee_Stream_MalformedEventDataJson_FailsOpen()
    {
        const string sse =
            "event: response.output_text.delta\n" +
            "data: {sai\n\n" +
            "event: response.completed\n" +
            "data: {cũng sai\n\n";
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await Protocol.TeeAsync(content, dest, CancellationToken.None);

        Assert.Null(result.Usage);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));   // dest vẫn nhận đủ byte
    }

    [Fact]
    public async Task Tee_Stream_ReasoningOnly_NoTtftButUsage()
    {
        // reasoning_summary delta KHÔNG phải output_text delta → TTFT null, usage vẫn đọc (spec §4.4/§8)
        const string sse =
            "event: response.created\n" +
            "data: {\"type\":\"response.created\"}\n\n" +
            "event: response.reasoning_summary_text.delta\n" +
            "data: {\"type\":\"response.reasoning_summary_text.delta\",\"delta\":\"thinking...\"}\n\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":4,\"output_tokens\":9}}}\n\n";
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await Protocol.TeeAsync(content, dest, CancellationToken.None);

        Assert.Null(result.FirstTokenAt);
        Assert.NotNull(result.Usage);
        Assert.Equal(4, result.Usage.PromptTokens);
        Assert.Equal(9, result.Usage.CompletionTokens);
    }

    [Fact]
    public async Task Tee_Stream_IncompleteEvent_UsageRead()
    {
        // response.incomplete (cutoff vì max_output_tokens) vẫn best-effort đọc usage (spec §4.4)
        const string sse =
            "event: response.output_text.delta\n" +
            "data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n" +
            "event: response.incomplete\n" +
            "data: {\"type\":\"response.incomplete\",\"response\":{\"status\":\"incomplete\",\"incomplete_details\":{\"reason\":\"max_output_tokens\"},\"usage\":{\"input_tokens\":5,\"output_tokens\":3}}}\n\n";
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await Protocol.TeeAsync(content, dest, CancellationToken.None);

        Assert.NotNull(result.Usage);
        Assert.Equal(5, result.Usage.PromptTokens);
        Assert.Equal(3, result.Usage.CompletionTokens);
    }

    [Fact]
    public async Task Tee_Stream_BodyCappedAt64KWithMarker()
    {
        var bigDelta = new string('x', 70 * 1024);
        var sse = "event: response.output_text.delta\n" +
                  "data: {\"type\":\"response.output_text.delta\",\"delta\":\"" + bigDelta + "\"}\n\n";
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await Protocol.TeeAsync(content, dest, CancellationToken.None);

        Assert.NotNull(result.ResponseBody);
        Assert.True(result.ResponseBody.Length <= 64 * 1024 + "[truncated]".Length,
            $"ResponseBody.Length = {result.ResponseBody.Length} vượt cap 64KB + marker");
        Assert.EndsWith("[truncated]", result.ResponseBody);
        Assert.Equal(result.ResponseBody.IndexOf("[truncated]", StringComparison.Ordinal),
                     result.ResponseBody.LastIndexOf("[truncated]", StringComparison.Ordinal)); // marker đúng 1 lần
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));   // client vẫn nhận đủ byte
    }
}
