using System.Text;
using System.Text.Json;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

/// <summary>
/// AggregateSseAsync của chat protocol — OpenCode gate 403 stream:false buộc fingerprint
/// force stream:true; client non-stream nhận JSON chat.completion đã gộp chunk.
/// </summary>
public class ChatCompletionsProtocolAggregateTests
{
    private static IProxyProtocol Protocol => ProxyProtocols.Chat;

    // SSE chat chuẩn: chunk delta content + chunk usage terminal (include_usage)
    private const string ChatSse =
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"m1\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"m1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"m1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\" world\"},\"finish_reason\":null}]}\n\n" +
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"m1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
        "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1700000000,\"model\":\"m1\",\"choices\":[],\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2}}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Aggregate_Deltas_ConcatenatesContentAndBuildsCompletionJson()
    {
        using var content = new StringContent(ChatSse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await Protocol.AggregateSseAsync(content, dest, CancellationToken.None);

        var written = Encoding.UTF8.GetString(dest.ToArray());
        Assert.Equal(result.ResponseBody, written);
        using var doc = JsonDocument.Parse(written);
        Assert.Equal("chatcmpl-1", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
        Assert.Equal(1700000000, doc.RootElement.GetProperty("created").GetInt64());
        Assert.Equal("m1", doc.RootElement.GetProperty("model").GetString());

        var choice = doc.RootElement.GetProperty("choices")[0];
        Assert.Equal("assistant", choice.GetProperty("message").GetProperty("role").GetString());
        Assert.Equal("Hello world", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());

        Assert.Equal(5, doc.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
        Assert.NotNull(result.Usage);
        Assert.Equal(5, result.Usage!.PromptTokens);
        Assert.Equal(2, result.Usage.CompletionTokens);
        Assert.NotNull(result.FirstTokenAt);
    }

    [Fact]
    public async Task Aggregate_EmptyStream_WritesMinimalCompletionFailsOpen()
    {
        using var content = new StringContent("", Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await Protocol.AggregateSseAsync(content, dest, CancellationToken.None);

        var written = Encoding.UTF8.GetString(dest.ToArray());
        using var doc = JsonDocument.Parse(written);
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
        Assert.Equal("assistant", doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("role").GetString());
        Assert.False(doc.RootElement.TryGetProperty("usage", out _));
        Assert.Null(result.Usage);
        Assert.Null(result.FirstTokenAt);
    }

    [Fact]
    public async Task Aggregate_MalformedChunk_SkipsChunkFailsOpen()
    {
        const string sse =
            "data: {\"id\":\"chatcmpl-1\",\"model\":\"m1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hi\"},\"finish_reason\":null}]}\n\n" +
            "data: {hỏng\n\n" +
            "data: {\"id\":\"chatcmpl-1\",\"model\":\"m1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"!\"},\"finish_reason\":\"stop\"}]}\n\n";
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await Protocol.AggregateSseAsync(content, dest, CancellationToken.None);

        var written = Encoding.UTF8.GetString(dest.ToArray());
        using var doc = JsonDocument.Parse(written);
        Assert.Equal("Hi!", doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString());
        Assert.Null(result.Usage);
    }
}
