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
    public async Task Aggregate_EmptyStream_ThrowsPreContentMarker()
    {
        // Stream rỗng = upstream đóng sạch mà không có [DONE]/finish_reason → cụt;
        // chưa ghi byte nào nên phải là marker (retry/failover), không phải JSON rỗng.
        using var content = new StringContent("", Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var ex = await Assert.ThrowsAsync<UpstreamDiedBeforeContentException>(() =>
            Protocol.AggregateSseAsync(content, dest, CancellationToken.None));

        Assert.IsType<UpstreamTruncatedException>(ex.InnerException);
        Assert.Empty(dest.ToArray());
    }

    [Fact]
    public async Task Aggregate_StreamEndsMidToolCall_ThrowsPreContentMarker()
    {
        // tool_call arguments còn dở rồi EOF sạch — gộp lại sẽ ra JSON tool call thiếu tham số
        var sse = SseChunk(new
        {
            tool_calls = new[]
            {
                new
                {
                    index = 0,
                    id = "call_1",
                    type = "function",
                    @function = new { name = "task", arguments = "{\"descrip" },
                },
            },
        });
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var ex = await Assert.ThrowsAsync<UpstreamDiedBeforeContentException>(() =>
            Protocol.AggregateSseAsync(content, dest, CancellationToken.None));

        Assert.IsType<UpstreamTruncatedException>(ex.InnerException);
        Assert.Empty(dest.ToArray());
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

    // Chunk SSE chat — serialize qua JsonSerializer để escape arguments fragment đúng chuẩn
    private static string SseChunk(object delta, string? finishReason = null) =>
        $"data: {JsonSerializer.Serialize(new
        {
            id = "chatcmpl-1",
            model = "m1",
            choices = new[] { new { index = 0, delta, finish_reason = finishReason } },
        })}\n\n";

    [Fact]
    public async Task Aggregate_ToolCallDeltas_ConcatenatesArgumentsAndBuildsToolCalls()
    {
        // arguments cắt giữa 2 chunk (partial JSON string) — contract streaming tool_calls
        var sse = SseChunk(new { role = "assistant", content = (string?)null })
            + SseChunk(new
            {
                tool_calls = new[]
                {
                    new
                    {
                        index = 0,
                        id = "call_1",
                        type = "function",
                        @function = new { name = "task", arguments = "" },
                    },
                },
            })
            + SseChunk(new
            {
                tool_calls = new[]
                {
                    new { index = 0, @function = new { arguments = "{\"description\"" } },
                },
            })
            + SseChunk(new
            {
                tool_calls = new[]
                {
                    new { index = 0, @function = new { arguments = ":\"x\"}" } },
                },
            })
            + SseChunk(new { }, "tool_calls");
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        await Protocol.AggregateSseAsync(content, dest, CancellationToken.None);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(dest.ToArray()));
        var choice = doc.RootElement.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());

        var toolCall = message.GetProperty("tool_calls")[0];
        Assert.Equal("call_1", toolCall.GetProperty("id").GetString());
        Assert.Equal("function", toolCall.GetProperty("type").GetString());
        Assert.Equal("task", toolCall.GetProperty("function").GetProperty("name").GetString());
        var arguments = toolCall.GetProperty("function").GetProperty("arguments").GetString();
        using var argsDoc = JsonDocument.Parse(arguments!);
        Assert.Equal("x", argsDoc.RootElement.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Aggregate_MultipleInterleavedToolCalls_OrdersByIndex()
    {
        var sse = SseChunk(new
        {
            tool_calls = new object[]
            {
                new
                {
                    index = 0,
                    id = "call_a",
                    type = "function",
                    @function = new { name = "alpha", arguments = "{\"a\"" },
                },
                new
                {
                    index = 1,
                    id = "call_b",
                    type = "function",
                    @function = new { name = "beta", arguments = "{}" },
                },
            },
        })
            + SseChunk(new
            {
                tool_calls = new[]
                {
                    new { index = 0, @function = new { arguments = ":1}" } },
                },
            })
            + SseChunk(new { }, "tool_calls");
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        await Protocol.AggregateSseAsync(content, dest, CancellationToken.None);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(dest.ToArray()));
        var toolCalls = doc.RootElement.GetProperty("choices")[0].GetProperty("message")
            .GetProperty("tool_calls");
        Assert.Equal(2, toolCalls.GetArrayLength());

        Assert.Equal("call_a", toolCalls[0].GetProperty("id").GetString());
        Assert.Equal("alpha", toolCalls[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"a\":1}", toolCalls[0].GetProperty("function")
            .GetProperty("arguments").GetString());

        Assert.Equal("call_b", toolCalls[1].GetProperty("id").GetString());
        Assert.Equal("beta", toolCalls[1].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{}", toolCalls[1].GetProperty("function").GetProperty("arguments").GetString());
    }
}
