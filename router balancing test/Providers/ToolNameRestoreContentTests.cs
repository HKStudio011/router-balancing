using System.Text;
using System.Text.Json;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

/// <summary>
/// Map ngược tên tool đã bị fingerprint canonicalize (bash→Bash) trong SSE response —
/// client opencode phải thấy đúng tên nó đăng ký, không phải tên upstream dùng.
/// </summary>
public class ToolNameRestoreContentTests
{
    private static readonly IReadOnlyDictionary<string, string> Map =
        new Dictionary<string, string> { ["bash"] = "Bash", ["read"] = "Read" };

    private static async Task<string> RestoreAsync(string sse)
    {
        using var inner = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var wrapper = new ToolNameRestoreContent(inner, Map);
        await using var stream = await wrapper.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task SseDelta_RewritesMappedToolCallName()
    {
        var sse =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\"," +
            "\"function\":{\"name\":\"bash\",\"arguments\":\"{\\\"c\\\"}\"}}]}}]}\n\n";

        var result = await RestoreAsync(sse);

        Assert.Contains("\"name\":\"Bash\"", result);
        // id/arguments/shape giữ nguyên — chỉ đổi name (kiểm giá trị parse được, không so escaping)
        var dataLine = result.Split('\n')[0];
        using var doc = JsonDocument.Parse(dataLine["data: ".Length..]);
        var function = doc.RootElement.GetProperty("choices")[0].GetProperty("delta")
            .GetProperty("tool_calls")[0].GetProperty("function");
        Assert.Equal("c1", doc.RootElement.GetProperty("choices")[0].GetProperty("delta")
            .GetProperty("tool_calls")[0].GetProperty("id").GetString());
        Assert.Equal("{\"c\"}", function.GetProperty("arguments").GetString());
    }

    [Fact]
    public async Task SseDelta_UnmappedNamesAndNonDataLines_PassThroughUnchanged()
    {
        const string sse =
            ": keep-alive comment\n" +
            "\n" +
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\"," +
            "\"function\":{\"name\":\"web_search\",\"arguments\":\"{}\"}}]}}]}\n\n" +
            "data: [DONE]\n\n";

        Assert.Equal(sse, await RestoreAsync(sse));
    }

    [Fact]
    public async Task Sse_MalformedDataLine_PassesThroughByteIdentical()
    {
        // Fail-open: dòng hỏng (TCP cắt giữa JSON) không được làm hỏng cả stream
        const string sse = "data: {bad json\n\ndata: [DONE]\n\n";

        Assert.Equal(sse, await RestoreAsync(sse));
    }

    [Fact]
    public async Task Sse_LineSplitAcrossReadChunks_StillRewrites()
    {
        // Dòng data cắt giữa 2 lần read (TCP split mid-JSON) — buffer đủ dòng mới rewrite
        var sse =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\"," +
            "\"function\":{\"name\":\"read\",\"arguments\":\"{}\"}}]}}]}\n\n" +
            "data: [DONE]\n\n";
        using var inner = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var wrapper = new ToolNameRestoreContent(inner, Map);
        // Đọc từng byte qua wrapper — mô phỏng TCP cắt giữa dòng JSON
        var stream = await wrapper.ReadAsStreamAsync();
        var sink = new MemoryStream();
        var one = new byte[1];
        while (await stream.ReadAsync(one, 0, 1) > 0)
            sink.Write(one, 0, 1);

        var result = Encoding.UTF8.GetString(sink.ToArray());
        Assert.Contains("\"name\":\"Read\"", result);
        Assert.DoesNotContain("\"name\":\"read\"", result);
    }

    [Fact]
    public async Task AggregateMessageJson_RestoresNamesInMessageToolCalls()
    {
        // Provider bỏ qua stream:true → JSON chat.completion với message.tool_calls — cùng contract
        var sse =
            "data: {\"choices\":[{\"message\":{\"tool_calls\":[{\"id\":\"c1\"," +
            "\"function\":{\"name\":\"bash\",\"arguments\":\"{}\"}}]}}]}\n\n";

        var result = await RestoreAsync(sse);

        Assert.Contains("\"name\":\"Bash\"", result);
    }
}
