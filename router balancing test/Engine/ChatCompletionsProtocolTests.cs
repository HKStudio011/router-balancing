using System.Text;
using System.Text.Json;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

/// <summary>
/// PrepareUpstreamBody của chat protocol — alias rewrite + force stream:true (non-stream
/// client) + include_usage; mirror <see cref="ResponsesProtocolTests.PrepareUpstreamBody_SwapsModelInjectsNothingElse"/>.
/// </summary>
public class ChatCompletionsProtocolTests
{
    private static IProxyProtocol Protocol => ProxyProtocols.Chat;

    private static byte[] B(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void PrepareUpstreamBody_WhenNonStream_ForcesUpstreamStreamAndInjectsUsage()
    {
        // Non-stream client → upstream phải stream (không thì connection im lặng cả
        // generation bị middle cắt) — client vẫn nhận JSON nhờ aggregate ở handler
        var body = B("""{"model":"combo-fast","messages":[{"role":"user","content":"hi"}]}""");

        var (rewritten, expectsUsage) = Protocol.PrepareUpstreamBody(body, "gpt-4o-real",
            ProviderType.OpenAI);

        Assert.True(expectsUsage); // stream:true + OpenAI → WithIncludeUsage inject
        using var doc = JsonDocument.Parse(rewritten);
        Assert.Equal("gpt-4o-real", doc.RootElement.GetProperty("model").GetString());
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("stream_options").GetProperty("include_usage")
            .GetBoolean());
        // Các trường client gửi giữ nguyên
        Assert.Equal("hi", doc.RootElement.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    [Fact]
    public void PrepareUpstreamBody_WhenStreamAlreadyTrue_InjectsUsageOnly()
    {
        var body = B("""{"model":"m","messages":[],"stream":true}""");

        var (rewritten, expectsUsage) = Protocol.PrepareUpstreamBody(body, "m",
            ProviderType.OpenAI);

        Assert.True(expectsUsage);
        using var doc = JsonDocument.Parse(rewritten);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("stream_options").GetProperty("include_usage")
            .GetBoolean());
    }

    [Fact]
    public void PrepareUpstreamBody_WhenAnthropic_ForcesStreamButSkipsStreamOptions()
    {
        // Anthropic (OpenAI-compat endpoint) hiểu stream:true nhưng KHÔNG có stream_options
        // (spec §6 — WithIncludeUsage chỉ inject cho OpenAI)
        var body = B("""{"model":"claude-x","messages":[]}""");

        var (rewritten, expectsUsage) = Protocol.PrepareUpstreamBody(body, "claude-x",
            ProviderType.Anthropic);

        Assert.False(expectsUsage);
        using var doc = JsonDocument.Parse(rewritten);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("stream_options", out _));
    }
}
