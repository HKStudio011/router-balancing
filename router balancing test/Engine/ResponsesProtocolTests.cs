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
}
