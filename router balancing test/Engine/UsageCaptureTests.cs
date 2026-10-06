using System.Text;
using System.Text.Json;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class UsageCaptureTests
{
    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void WithIncludeUsage_OpenAiStream_InjectsIncludeUsageWithoutMutatingOriginal()
    {
        const string json = """{"model":"m","stream":true,"messages":[]}""";
        var body = Bytes(json);

        var (result, expects) = UsageCapture.WithIncludeUsage(body, ProviderType.OpenAI);

        Assert.True(expects);
        Assert.Equal(json, Encoding.UTF8.GetString(body));   // body gốc không đổi
        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public void WithIncludeUsage_ExistingStreamOptions_MergesAndKeepsProps()
    {
        var body = Bytes("""{"stream":true,"stream_options":{"include_usage":false,"x":1}}""");

        var (result, expects) = UsageCapture.WithIncludeUsage(body, ProviderType.OpenAI);

        Assert.True(expects);
        using var doc = JsonDocument.Parse(result);
        var options = doc.RootElement.GetProperty("stream_options");
        Assert.True(options.GetProperty("include_usage").GetBoolean());
        Assert.Equal(1, options.GetProperty("x").GetInt32());
    }

    [Fact]
    public void WithIncludeUsage_NotStream_ReturnsOriginalAndNotExpects()
    {
        var body = Bytes("""{"stream":false,"messages":[]}""");

        var (result, expects) = UsageCapture.WithIncludeUsage(body, ProviderType.OpenAI);

        Assert.False(expects);
        Assert.Same(body, result);
    }

    [Fact]
    public void WithIncludeUsage_AnhropicStream_DoesNotInject()
    {
        var body = Bytes("""{"stream":true}""");

        var (result, expects) = UsageCapture.WithIncludeUsage(body, ProviderType.Anthropic);

        Assert.False(expects);
        Assert.Same(body, result);
    }

    [Fact]
    public void WithIncludeUsage_InvalidJson_ReturnsOriginalAndNotExpects()
    {
        var body = Bytes("{broken");

        var (result, expects) = UsageCapture.WithIncludeUsage(body, ProviderType.OpenAI);

        Assert.False(expects);
        Assert.Same(body, result);
    }

    private static async Task<(byte[] Dest, UsageCapture.TeeResult Result)> TeeJsonAsync(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var dest = new MemoryStream();
        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None);
        return (dest.ToArray(), result);
    }

    [Fact]
    public async Task Tee_JsonWithUsage_ForwardsBytesAndParses()
    {
        const string json = """{"id":"x","usage":{"prompt_tokens":11,"completion_tokens":7}}""";

        var (dest, result) = await TeeJsonAsync(json);

        Assert.Equal(json, Encoding.UTF8.GetString(dest));
        Assert.NotNull(result.Usage);
        Assert.Equal(11, result.Usage.PromptTokens);
        Assert.Equal(7, result.Usage.CompletionTokens);
    }

    [Fact]
    public async Task Tee_JsonWithoutUsage_ReturnsNullForwardsBytes()
    {
        const string json = """{"id":"x"}""";

        var (dest, result) = await TeeJsonAsync(json);

        Assert.Null(result.Usage);
        Assert.Equal(json, Encoding.UTF8.GetString(dest));
    }

    [Fact]
    public async Task Tee_InvalidJson_ReturnsNullStillForwards()
    {
        const string json = "{broken";

        var (dest, result) = await TeeJsonAsync(json);

        Assert.Null(result.Usage);
        Assert.Equal(json, Encoding.UTF8.GetString(dest));
    }

    private const string SseFixture =
        "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
        "data: {\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}\n\n" +
        "data: {\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":2}}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Tee_SseWithUsage_ForwardsBytesUnchanged_LastUsageWins()
    {
        using var content = new StringContent(SseFixture, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None);

        Assert.Equal(SseFixture, Encoding.UTF8.GetString(dest.ToArray()));  // byte-forward nguyên vẹn
        Assert.NotNull(result.Usage);
        Assert.Equal(9, result.Usage.PromptTokens);                         // last-wins (spec §6.1)
        Assert.Equal(2, result.Usage.CompletionTokens);
    }

    [Fact]
    public async Task Tee_SseWithoutUsage_ForwardsAndReturnsNull()
    {
        const string sse = "data: {\"x\":1}\n\ndata: [DONE]\n\n";
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None);

        Assert.Null(result.Usage);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
    }

    /// <summary>Stream trả data theo chunk tùy ý — dựng lại case dòng SSE cắt giữa 2 lần read.</summary>
    private sealed class ChunkedStream(params byte[][] chunks) : Stream
    {
        private int _chunk;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunk >= chunks.Length) return 0;
            var current = chunks[_chunk];
            var take = Math.Min(count, current.Length - _offset);
            Buffer.BlockCopy(current, _offset, buffer, offset, take);
            _offset += take;
            if (_offset >= current.Length) { _chunk++; _offset = 0; }
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task TeeSse_DataLineSplitAcross7ByteChunks_StillCapturesUsage()
    {
        var all = Encoding.UTF8.GetBytes(SseFixture);
        var chunks = new List<byte[]>();
        for (var i = 0; i < all.Length; i += 7)
            chunks.Add(all[i..Math.Min(i + 7, all.Length)]);
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new ChunkedStream([.. chunks]), dest, CancellationToken.None);

        Assert.Equal(SseFixture, Encoding.UTF8.GetString(dest.ToArray()));
        Assert.NotNull(result.Usage);
        Assert.Equal(9, result.Usage.PromptTokens);
    }

    [Fact]
    public async Task TeeSse_MalformedDataLine_SkippedLastGoodUsageKept()
    {
        var sse = "data: {bad json\n\n" +
                  "data: {\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":1}}\n\n" +
                  "data: [DONE]\n";
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)),
            dest, CancellationToken.None);

        Assert.NotNull(result.Usage);
        Assert.Equal(5, result.Usage.PromptTokens);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
    }

    [Fact]
    public async Task TeeAsync_Sse_ReportsFirstTokenAtAndAccumulatesDataWithoutDone()
    {
        const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
                           "data: {\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}\n\n" +
                           "data: [DONE]\n\n";
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None);

        Assert.NotNull(result.FirstTokenAt);
        Assert.Contains("{\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}", result.ResponseBody);
        Assert.Contains("{\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}", result.ResponseBody);
        Assert.DoesNotContain("[DONE]", result.ResponseBody);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));  // byte-forward vẫn nguyên vẹn
    }

    [Fact]
    public async Task TeeAsync_NotStream_FirstTokenAtNull_ReturnsBodyAndUsage()
    {
        const string json = """{"id":"x","usage":{"prompt_tokens":11,"completion_tokens":7}}""";
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None);

        Assert.Null(result.FirstTokenAt);
        Assert.Equal(json, result.ResponseBody);
        Assert.NotNull(result.Usage);
        Assert.Equal(11, result.Usage.PromptTokens);
        Assert.Equal(7, result.Usage.CompletionTokens);
        Assert.Equal(json, Encoding.UTF8.GetString(dest.ToArray()));
    }

    [Fact]
    public async Task TeeAsync_BodyOverCap_TruncatesWithMarker()
    {
        var payload = new string('a', 70 * 1024);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None);

        Assert.NotNull(result.ResponseBody);
        Assert.EndsWith("[truncated]", result.ResponseBody);
        Assert.True(result.ResponseBody.Length <= 64 * 1024 + "[truncated]".Length,
            $"ResponseBody.Length = {result.ResponseBody.Length} vượt cap 64KB + marker");
        Assert.Equal(payload, Encoding.UTF8.GetString(dest.ToArray()));  // client vẫn nhận đủ 70KB
    }
}
