using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Dataset;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class UsageCaptureTests
{
    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private const string MultilineWriteSse =
        "data: {\"id\":\"chatcmpl-write\",\"model\":\"m1\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_write\",\"type\":\"function\",\"function\":{\"name\":\"write\",\n" +
        "data: \"arguments\":\"{\\\"path\\\":\\\"/tmp/a.txt\\\",\\\"content\\\":\\\"hello\\\"}\"}}]},\"finish_reason\":\"tool_calls\"}]}\n\n";

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
    public async Task TeeSse_MultilineToolCallWithFinishReasonWithoutDone_CompletesAndForwardsExactBytes()
    {
        var chunks = Bytes(MultilineWriteSse).Select(value => new[] { value }).ToArray();
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new ChunkedStream(chunks), dest, CancellationToken.None);

        Assert.False(result.EndedWithoutTerminal);
        Assert.Equal(MultilineWriteSse, Encoding.UTF8.GetString(dest.ToArray()));
        Assert.Null(result.LastByteGap);
    }

    [Fact]
    public async Task TeeSse_MultilineToolCallWithDone_CaptureCompletionPreservesDatasetToolCall()
    {
        const string sse = MultilineWriteSse + "data: [DONE]\n\n";
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new MemoryStream(Bytes(sse)), dest, CancellationToken.None, captureCompletion: true);

        Assert.False(result.EndedWithoutTerminal);
        Assert.True(result.CompletionComplete);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
        Assert.NotNull(result.CompletionJson);
        using var doc = JsonDocument.Parse(result.CompletionJson!);
        Assert.Equal("chatcmpl-write", doc.RootElement.GetProperty("id").GetString());
        Assert.Equal("m1", doc.RootElement.GetProperty("model").GetString());
        var choice = doc.RootElement.GetProperty("choices")[0];
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        var toolCall = choice.GetProperty("message").GetProperty("tool_calls")[0];
        Assert.Equal("call_write", toolCall.GetProperty("id").GetString());
        Assert.Equal("write", toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"path\":\"/tmp/a.txt\",\"content\":\"hello\"}",
            toolCall.GetProperty("function").GetProperty("arguments").GetString());
    }

    [Fact]
    public async Task TeeSse_MultilineToolCallWithDoneWithoutTrailingBlankLine_Completes()
    {
        const string sse = MultilineWriteSse + "data: [DONE]\n";
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new MemoryStream(Bytes(sse)), dest, CancellationToken.None);

        Assert.False(result.EndedWithoutTerminal);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
        Assert.Null(result.LastByteGap);
    }

    [Theory]
    [InlineData("[DONE]garbage")]
    [InlineData("[DONE]x")]
    public async Task TeeSse_DonePrefixGarbage_RemainsTruncatedAndVisibleInCapture(string doneCandidate)
    {
        var sse = $"data: {doneCandidate}\n\n";
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new MemoryStream(Bytes(sse)), dest, CancellationToken.None, captureCompletion: true);

        Assert.True(result.EndedWithoutTerminal);
        Assert.False(result.CompletionComplete);
        Assert.Equal(doneCandidate, result.ResponseBody);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
    }

    [Fact]
    public async Task TeeSse_ExactDoneWithoutFinishReason_CapturePreservesNullFinishReason()
    {
        const string sse =
            "data: {\"id\":\"chatcmpl-null\",\"model\":\"m1\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n" +
            "data: [DONE]\n\n";
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new MemoryStream(Bytes(sse)), dest, CancellationToken.None, captureCompletion: true);

        Assert.False(result.EndedWithoutTerminal);
        Assert.True(result.CompletionComplete);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
        Assert.NotNull(result.CompletionJson);
        using var doc = JsonDocument.Parse(result.CompletionJson!);
        Assert.Equal(JsonValueKind.Null,
            doc.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").ValueKind);
    }

    [Fact]
    public async Task TeeSse_DoneSplitAcrossDataFields_RemainsTruncatedAndCaptureIncomplete()
    {
        const string sse = "data: [DO\ndata: NE]\n\n";
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new MemoryStream(Bytes(sse)), dest, CancellationToken.None, captureCompletion: true);

        Assert.True(result.EndedWithoutTerminal);
        Assert.False(result.CompletionComplete);
        Assert.Equal("[DO\nNE]", result.ResponseBody);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
    }

    [Fact]
    public async Task TeeSse_DoneLineSplitAcrossNetworkChunks_Completes()
    {
        const string sse = "data: [DONE]\n\n";
        byte[][] chunks = [Bytes("data: [DO"), Bytes("NE]\n\n")];
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new ChunkedStream(chunks), dest, CancellationToken.None);

        Assert.False(result.EndedWithoutTerminal);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
    }

    [Theory]
    [InlineData("data:  [DONE]\n\n")]
    [InlineData("data:\t[DONE]\n\n")]
    public async Task TeeSse_NonCanonicalDoneAndFinishOnlyOnSecondChoice_RemainsTruncated(string done)
    {
        const string finishOnSecondChoice =
            "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":null}," +
            "{\"index\":1,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
        var sse = finishOnSecondChoice + done;
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new MemoryStream(Bytes(sse)), dest, CancellationToken.None);

        Assert.True(result.EndedWithoutTerminal);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
    }

    [Fact]
    public async Task TeeSse_CleanEofWithoutTerminal_ReportsNonNegativeLastByteGap()
    {
        const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"fixture-content-must-not-be-logged\"}}]}\n\n";
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new MemoryStream(Bytes(sse)), dest, CancellationToken.None);

        Assert.True(result.EndedWithoutTerminal);
        var lastByteGap = Assert.IsType<TimeSpan>(result.LastByteGap);
        Assert.True(lastByteGap >= TimeSpan.Zero);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
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

    /// <summary>
    /// Content SSE nổ khi đọc — <paramref name="deliverFirst"/> = chunk đầu đi qua rồi nổ
    /// (mid-content); ngược lại nổ ngay lần đọc đầu (pre-content).
    /// </summary>
    private sealed class FlakySseContent : HttpContent
    {
        private readonly bool _deliverFirst;
        private bool _delivered;

        // TeeAsync rẽ theo Content-Type — thiếu header này sẽ rơi vào nhánh buffer (non-SSE)
        public FlakySseContent(bool deliverFirst)
        {
            _deliverFirst = deliverFirst;
            Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new FlakyStream(this));

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        private sealed class FlakyStream(FlakySseContent owner) : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count) =>
                Deliver(buffer.AsSpan(offset, count));

            public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
                ValueTask.FromResult(Deliver(buffer.Span));

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
                CancellationToken ct) =>
                Task.FromResult(Deliver(buffer.AsSpan(offset, count)));

            private int Deliver(Span<byte> buffer)
            {
                if (owner._delivered)
                    throw new IOException("upstream broke mid-chunk");
                owner._delivered = true;
                if (!owner._deliverFirst)
                    throw new IOException("upstream reset before content");
                var bytes = Encoding.UTF8.GetBytes("data: {\"x\":1}\n\n");
                bytes.CopyTo(buffer);
                return bytes.Length;
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException();
        }
    }

    [Fact]
    public async Task Tee_SseDiesBeforeFirstByte_ThrowsMarkerAndForwardsNothing()
    {
        using var content = new FlakySseContent(deliverFirst: false);
        using var dest = new MemoryStream();

        var ex = await Assert.ThrowsAsync<UpstreamDiedBeforeContentException>(() =>
            UsageCapture.TeeAsync(content, dest, CancellationToken.None));

        Assert.IsType<IOException>(ex.InnerException);
        Assert.Empty(dest.ToArray());  // client chưa nhận gì — retry/failover an toàn
    }

    [Fact]
    public async Task Tee_SseDiesAfterFirstByte_RethrowsOriginalIOException()
    {
        using var content = new FlakySseContent(deliverFirst: true);
        using var dest = new MemoryStream();

        // Đã có byte trên wire → giữ nguyên IOException gốc (không marker → không retry)
        await Assert.ThrowsAsync<IOException>(() =>
            UsageCapture.TeeAsync(content, dest, CancellationToken.None));

        Assert.NotEmpty(dest.ToArray());
    }

    [Fact]
    public async Task TeeSse_CleanEofMidStream_CompletesWithEndedWithoutTerminalFlag()
    {
        // Không [DONE], không finish_reason: upstream tự đóng SẠCH (EOF) giữa stream
        // (vd Unsloth cancel/swap khi stream dài). Tee chỉ forward bytes và báo cờ;
        // ProxyRequestHandler chuyển cờ thành lỗi mid-content để phát in-band
        // upstream_disconnected — error policy không nằm trong observer này.
        const string sse =
            "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"c1\"," +
            "\"function\":{\"name\":\"bash\",\"arguments\":\"{\\\"cmd\\\"\"}}]}}]}\n\n";
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)), dest, CancellationToken.None);

        Assert.True(result.EndedWithoutTerminal);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));   // byte-forward nguyên vẹn
    }

    [Fact]
    public async Task TeeSse_NullGraceOption_CleanEofKeepsImmediateLegacyResult()
    {
        const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n";
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)), dest, CancellationToken.None,
            gracePeriod: null);

        Assert.True(result.EndedWithoutTerminal);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
    }

    [Fact]
    public async Task TeeSse_FiniteGraceWithoutBytes_TimesOutAtDeadlineAfterFinalRead()
    {
        const string content = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n";
        using var source = new ResumableSseStream(
            ResumableSseStream.Content(content),
            ResumableSseStream.Eof(),
            ResumableSseStream.Eof(),
            ResumableSseStream.Eof(),
            ResumableSseStream.Eof());
        using var dest = new MemoryStream();
        var clock = new DeterministicGraceClock();

        var result = await UsageCapture.TeeSseAsync(source, dest, CancellationToken.None,
            gracePeriod: TimeSpan.FromSeconds(3), graceClock: clock);

        Assert.True(result.EndedWithoutTerminal);
        Assert.True(result.TerminalWaitTimedOut);
        Assert.False(result.CompletionComplete);
        Assert.Equal(content, Encoding.UTF8.GetString(dest.ToArray()));
        Assert.Equal(5, source.ReadCount); // content + initial EOF + 3 polling reads
        Assert.Equal(1, source.MaxConcurrentReads);
        Assert.Equal([
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1),
        ], clock.Delays);
    }

    [Fact]
    public async Task TeeSse_GraceShorterThanPollInterval_ReadsAtDeadlineThenTimesOut()
    {
        using var source = new ResumableSseStream(
            ResumableSseStream.Content("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n"),
            ResumableSseStream.Eof(),
            ResumableSseStream.Eof());
        using var dest = new MemoryStream();
        var clock = new DeterministicGraceClock();

        var result = await UsageCapture.TeeSseAsync(source, dest, CancellationToken.None,
            gracePeriod: TimeSpan.FromMilliseconds(500), graceClock: clock);

        Assert.True(result.TerminalWaitTimedOut);
        Assert.Equal([TimeSpan.FromMilliseconds(500)], clock.Delays);
        Assert.Equal(3, source.ReadCount);
    }

    [Fact]
    public async Task TeeSse_ZeroGrace_WaitsUntilClientCancellationWithoutTimeout()
    {
        using var source = new ResumableSseStream(
            ResumableSseStream.Content("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n"),
            ResumableSseStream.Eof());
        using var dest = new MemoryStream();
        var clock = new DeterministicGraceClock();
        using var cts = new CancellationTokenSource();

        var teeTask = UsageCapture.TeeSseAsync(source, dest, cts.Token,
            gracePeriod: Timeout.InfiniteTimeSpan, graceClock: clock);
        while (clock.Delays.Count < 2)
        {
            Assert.False(teeTask.IsCompleted);
            await Task.Yield();
        }
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            teeTask.WaitAsync(TimeSpan.FromMilliseconds(250)));
        Assert.Equal(1, source.MaxConcurrentReads);
        Assert.True(source.ReadCount >= 3);
        Assert.All(clock.Delays, delay => Assert.Equal(TimeSpan.FromSeconds(1), delay));
    }

    [Fact(Explicit = true)]
    public async Task TeeSse_ZeroGrace_TenMinutes_RemainsActiveUntilClientCancellation()
    {
        const string content = "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n";
        using var source = new ResumableSseStream(
            ResumableSseStream.Content(content), ResumableSseStream.Eof());
        using var cts = new CancellationTokenSource();
        var ctx = new DefaultHttpContext { RequestAborted = cts.Token };
        var responseBody = new MemoryStream();
        ctx.Response.Body = responseBody;
        var pipe = new Pipe();
        var completion = new TaskCompletionSource<DispatchOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var teeTask = UsageCapture.TeeSseAsync(source, pipe.Writer.AsStream(), cts.Token,
            gracePeriod: Timeout.InfiniteTimeSpan);
        var loopTask = ProxyApp.RunStreamLoopAsync(ctx, pipe, completion.Task);

        await Task.Delay(TimeSpan.FromMinutes(10));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            teeTask.WaitAsync(TimeSpan.FromMilliseconds(250)));
        completion.SetResult(new DispatchOutcome.Aborted());
        var (outcome, contentBytes, upstreamBytes) = await loopTask.WaitAsync(
            TimeSpan.FromMilliseconds(250));

        Assert.IsType<DispatchOutcome.Aborted>(outcome);
        Assert.Equal(content.Length, contentBytes);
        Assert.Equal(content.Length, upstreamBytes);
        var responseText = Encoding.UTF8.GetString(responseBody.ToArray());
        var keepAliveCount = responseText.Split(": keep-alive\n\n").Length - 1;
        Assert.InRange(keepAliveCount, 100, 130);
        Assert.DoesNotContain("timeout_waiting_for_terminal", responseText);
        Assert.Equal(1, source.MaxConcurrentReads);
    }

    [Fact]
    public async Task TeeSse_PartialEventResumedAfterEof_ForwardsAndCapturesEventExactlyOnce()
    {
        const string first = "data: {\"choices\":[{\"delta\":{\"content\":\"He";
        const string resumed = "llo\"}}]}\n\n";
        const string terminal = "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
        const string done = "data: [DONE]\n\n";
        using var source = new ResumableSseStream(
            ResumableSseStream.Content(first),
            ResumableSseStream.Eof(),
            ResumableSseStream.Content(resumed),
            ResumableSseStream.Content(terminal),
            ResumableSseStream.Content(done),
            ResumableSseStream.Eof());
        using var dest = new MemoryStream();
        var clock = new DeterministicGraceClock();

        var result = await UsageCapture.TeeSseAsync(source, dest, CancellationToken.None,
            captureCompletion: true, gracePeriod: TimeSpan.FromSeconds(3), graceClock: clock);

        Assert.False(result.EndedWithoutTerminal);
        Assert.False(result.TerminalWaitTimedOut);
        Assert.True(result.CompletionComplete);
        Assert.Equal(first + resumed + terminal + done, Encoding.UTF8.GetString(dest.ToArray()));
        Assert.Equal([TimeSpan.FromSeconds(1)], clock.Delays);
        Assert.NotNull(result.CompletionJson);
        using var doc = JsonDocument.Parse(result.CompletionJson!);
        Assert.Equal("Hello", doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString());
    }

    [Fact]
    public async Task TeeSse_EofAfterResume_StartsFreshFullGrace()
    {
        using var source = new ResumableSseStream(
            ResumableSseStream.Content("data: one\n\n"),
            ResumableSseStream.Eof(),
            ResumableSseStream.Content("data: two\n\n"),
            ResumableSseStream.Eof(),
            ResumableSseStream.Eof(),
            ResumableSseStream.Eof(),
            ResumableSseStream.Eof());
        using var dest = new MemoryStream();
        var clock = new DeterministicGraceClock();

        var result = await UsageCapture.TeeSseAsync(source, dest, CancellationToken.None,
            gracePeriod: TimeSpan.FromSeconds(3), graceClock: clock);

        Assert.True(result.TerminalWaitTimedOut);
        Assert.Equal(TimeSpan.FromSeconds(4), clock.GetElapsedTime(0));
        Assert.Equal(4, clock.Delays.Count);
        Assert.All(clock.Delays, delay => Assert.Equal(TimeSpan.FromSeconds(1), delay));
        Assert.Equal(1, source.MaxConcurrentReads);
    }

    [Fact]
    public async Task TeeSse_BlockingReadPastFiniteDeadline_TimesOutAndCancelsRead()
    {
        using var source = new ResumableSseStream(
            ResumableSseStream.Content("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n"),
            ResumableSseStream.Eof(),
            ResumableSseStream.Eof(TimeSpan.FromSeconds(30)));
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(source, dest, CancellationToken.None,
                gracePeriod: TimeSpan.FromSeconds(3), graceClock: SystemGraceClock.Instance)
            .WaitAsync(TimeSpan.FromSeconds(4));

        Assert.True(result.TerminalWaitTimedOut);
        Assert.Equal(1, source.MaxConcurrentReads);
    }

    [Fact]
    public async Task TeeSse_DeadlineCancellationBeforeClockCatchesUp_ReturnsTimeoutInsteadOfThrowing()
    {
        using var source = new ResumableSseStream(
            ResumableSseStream.Content("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n"),
            ResumableSseStream.Eof(),
            ResumableSseStream.Eof(TimeSpan.FromMilliseconds(50)));
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(source, dest, CancellationToken.None,
                gracePeriod: TimeSpan.FromMilliseconds(1), graceClock: new StalledGraceClock())
            .WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(result.TerminalWaitTimedOut);
        Assert.Equal(1, source.MaxConcurrentReads);
    }

    [Fact]
    public async Task TeeSse_PreContentEof_WithInfiniteGrace_KeepsImmediateFailoverMarker()
    {
        using var source = new ResumableSseStream(ResumableSseStream.Eof());
        using var dest = new MemoryStream();
        var clock = new DeterministicGraceClock();

        await Assert.ThrowsAsync<UpstreamDiedBeforeContentException>(() =>
            UsageCapture.TeeSseAsync(source, dest, CancellationToken.None,
                gracePeriod: Timeout.InfiniteTimeSpan, graceClock: clock));

        Assert.Empty(clock.Delays);
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task TeeSse_TransportExceptionDuringGrace_RethrowsOriginalIOException()
    {
        var failure = new IOException("upstream reset during grace");
        using var source = new ResumableSseStream(
            ResumableSseStream.Content("data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n"),
            ResumableSseStream.Eof(),
            ResumableSseStream.Failure(failure));
        using var dest = new MemoryStream();
        var clock = new DeterministicGraceClock();

        var actual = await Assert.ThrowsAsync<IOException>(() =>
            UsageCapture.TeeSseAsync(source, dest, CancellationToken.None,
                gracePeriod: TimeSpan.FromSeconds(3), graceClock: clock));

        Assert.Same(failure, actual);
        Assert.Equal([TimeSpan.FromSeconds(1)], clock.Delays);
    }

    [Fact]
    public async Task TeeSse_CompleteTerminalTailAtEof_DoesNotEnterGrace()
    {
        using var source = new ResumableSseStream(
            ResumableSseStream.Content("data: [DONE]"),
            ResumableSseStream.Eof());
        using var dest = new MemoryStream();
        var clock = new DeterministicGraceClock();

        var result = await UsageCapture.TeeSseAsync(source, dest, CancellationToken.None,
            gracePeriod: TimeSpan.FromSeconds(3), graceClock: clock);

        Assert.False(result.EndedWithoutTerminal);
        Assert.Empty(clock.Delays);
    }

    [Fact]
    public async Task TeeSse_CleanEofBeforeAnyByte_ThrowsPreContentMarker()
    {
        using var dest = new MemoryStream();

        var ex = await Assert.ThrowsAsync<UpstreamDiedBeforeContentException>(() =>
            UsageCapture.TeeSseAsync(new MemoryStream([]), dest, CancellationToken.None));

        Assert.IsType<UpstreamTruncatedException>(ex.InnerException);
        Assert.Empty(dest.ToArray());   // chưa có byte nào → retry/failover an toàn
    }

    [Fact]
    public async Task TeeSse_FinishReasonWithoutDone_CompletesWithoutThrow()
    {
        // finish_reason khác null cũng là dấu hiệu kết thúc hợp lệ (một số provider bỏ [DONE])
        const string sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"},\"finish_reason\":null}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n";
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeSseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)), dest, CancellationToken.None);

        Assert.Null(result.Usage);
        Assert.False(result.EndedWithoutTerminal);   // finish_reason vẫn là dấu kết thúc hợp lệ
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
    }

    // ─── captureCompletion (dataset export): gộp SSE → object chat.completion đầy đủ ───

    [Fact]
    public async Task Tee_Sse_CaptureCompletion_True_BuildsFullMessageFromDeltas()
    {
        // 3 chunk delta content + finish_reason ở chunk cuối + [DONE] — completion phải là
        // NGUYÊN object chat.completion (không phải message) với content đã nối đủ.
        const string sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"He\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{\"content\":\"llo\"}}]}\n\n" +
            "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None,
            captureCompletion: true);

        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));   // byte-forward không đổi
        Assert.NotNull(result.CompletionJson);
        using var doc = JsonDocument.Parse(result.CompletionJson!);
        Assert.Equal("Hello", doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString());
        Assert.True(result.CompletionComplete);
    }

    [Fact]
    public async Task Tee_Sse_CaptureCompletion_Default_NullCompletionJson()
    {
        // Không bật captureCompletion → CompletionJson null, mọi thứ khác giữ nguyên hành vi cũ
        using var content = new StringContent(SseFixture, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None);

        Assert.Null(result.CompletionJson);
        Assert.Equal(SseFixture, Encoding.UTF8.GetString(dest.ToArray()));
    }

    [Fact]
    public async Task Tee_Sse_CaptureCompletion_StreamCuts_SetsCompletionCompleteFalse()
    {
        // EOF sạch không [DONE]/finish_reason = stream cụt → CompletionComplete false để
        // builder bỏ record (không train trên completion dở dang).
        const string sse = "data: {\"choices\":[{\"delta\":{\"content\":\"He\"}}]}\n\n";
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None,
            captureCompletion: true);

        Assert.True(result.EndedWithoutTerminal);
        Assert.False(result.CompletionComplete);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
    }

    [Fact]
    public async Task Tee_Sse_CaptureCompletion_ContentBetweenOneAndSixteenMiB_KeepsCompletion()
    {
        var big = new string('a', DatasetRecordBuilder.InlineMaxFieldBytes + 1);
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"" + big + "\"}}]}\n\n" +
                  "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                  "data: [DONE]\n\n";
        var originalClientBytes = Bytes(sse);
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None,
            captureCompletion: true);

        var forwardedBytes = dest.ToArray();
        Assert.Equal(originalClientBytes, forwardedBytes);
        Assert.NotNull(result.CompletionJson);
        Assert.True(result.CompletionComplete);
        using var doc = JsonDocument.Parse(result.CompletionJson!);
        Assert.Equal(big.Length, doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString()!.Length);
    }

    [Fact]
    public async Task Tee_Sse_CaptureCompletion_OverSixteenMiB_PreservesClientBytes()
    {
        // Cap export chỉ chặn TÍCH LŨY cho dataset, KHÔNG được cắt/trễ byte của client.
        var big = new string('a', DatasetRecordBuilder.MaxExportFieldBytes + 1);
        var sse = "data: {\"choices\":[{\"delta\":{\"content\":\"" + big + "\"}}]}\n\n" +
                  "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
                  "data: [DONE]\n\n";
        var originalClientBytes = Bytes(sse);
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None,
            captureCompletion: true);

        var forwardedBytes = dest.ToArray();
        Assert.Equal(originalClientBytes, forwardedBytes);
        Assert.Null(result.CompletionJson);
        Assert.False(result.CompletionComplete);
    }

    [Fact]
    public async Task Tee_Sse_CaptureCompletion_ExactBudgetAndPlusOneByte()
    {
        await AssertBudgetBoundaryAsync(16_777_216, expectCompletion: true);
        await AssertBudgetBoundaryAsync(16_777_217, expectCompletion: false);

        static async Task AssertBudgetBoundaryAsync(int contentBytes, bool expectCompletion)
        {
            var text = new string('a', contentBytes);
            var sse = DatasetDeltaLine(text) + "data: [DONE]\n\n";
            var originalClientSseBytes = Bytes(sse);
            using var content = new ByteArrayContent(originalClientSseBytes);
            content.Headers.ContentType = new("text/event-stream");
            using var dest = new MemoryStream();

            var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None,
                captureCompletion: true);

            Assert.Equal(originalClientSseBytes, dest.ToArray());
            Assert.Equal(expectCompletion, result.CompletionJson is not null);
            Assert.Equal(expectCompletion, result.CompletionComplete);
            if (!expectCompletion) return;

            using var doc = JsonDocument.Parse(result.CompletionJson!);
            Assert.Equal(16_777_216, doc.RootElement.GetProperty("choices")[0]
                .GetProperty("message").GetProperty("content").GetString()!.Length);
        }
    }

    [Fact]
    public async Task Tee_Sse_CaptureCompletion_NonAsciiEscapedContent_UsesDefaultJsonUtf8Budget()
    {
        const string text = "Aé<&👋";
        Assert.Equal(ExpectedDefaultEncodedByteCount(text), ActualDatasetAccumulatorSize(text));

        // Mỗi 'é' chiếm một char nhưng default encoder của BuildCompletionJson ghi "\\u00E9"
        // (6 byte). Chuỗi này vẫn dưới 16 MiB theo char nhưng vượt budget theo JSON UTF-8.
        var largeText = new string('é', DatasetRecordBuilder.MaxExportFieldBytes / 6 + 1);
        var sse = DatasetDeltaLine(largeText) + "data: [DONE]\n\n";
        var originalClientBytes = Bytes(sse);
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None,
            captureCompletion: true);

        Assert.Null(result.CompletionJson);
        Assert.False(result.CompletionComplete);
        Assert.Equal(originalClientBytes, dest.ToArray());
    }

    [Fact]
    public void Tee_Sse_CaptureCompletion_SurrogateSplit_NeverOverrunsUtf8Budget()
    {
        const string highSurrogate = "\uD83D";
        const string lowSurrogate = "\uDC4B";
        var encodedPairBytes = ExpectedDefaultEncodedByteCount("👋");

        var result = CaptureDatasetDeltas(encodedPairBytes - 1, highSurrogate, lowSurrogate);

        Assert.True(result.Overflowed || result.CompletionJson is null);
    }

    private static int ExpectedDefaultEncodedByteCount(string text) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(text)) - 2;

    private static int ActualDatasetAccumulatorSize(string text)
    {
        // Ngưỡng nhỏ nhất còn chấp nhận delta chính là kích thước accumulator quan sát được;
        // không đọc private counter và không suy ra expected từ implementation.
        for (var budget = 0; budget <= 128; budget++)
        {
            var result = CaptureDatasetDeltas(budget, text);
            if (!result.Overflowed)
            {
                return budget;
            }
        }

        throw new InvalidOperationException("Dataset accumulator vượt 128 byte cho fixture ngắn.");
    }

    private static (bool Overflowed, string? CompletionJson) CaptureDatasetDeltas(
        int budget, params string[] deltas)
    {
        var state = new ChatSseAggregateState(budget, budgetInUtf8Bytes: true);
        foreach (var delta in deltas)
        {
            state.HandleLine(Bytes(DatasetDeltaLine(delta)));
        }

        return (state.Overflowed, state.Overflowed ? null : state.BuildCompletionJson());
    }

    private static string DatasetDeltaLine(string text) =>
        "data: {\"choices\":[{\"delta\":{\"content\":" + JsonSerializer.Serialize(text) + "}}]}\n\n";

    [Fact]
    public async Task Tee_Sse_CaptureCompletion_OutOfRangeNumber_ContainedAndForwardsClientBytes()
    {
        // "created": 1e30 là JSON Number hợp lệ nhưng vượt phạm vi Int64 — GetInt64 ném lỗi
        // chuyển đổi (KHÔNG phải JsonException). Fail-open: lỗi không được thoát ra ngoài
        // TeeAsync vì byte đã forward cho client TRƯỚC khi HandleLine chạy. Hành vi chứa lại
        // đã assert bên dưới: chunk hỏng bị bỏ nguyên khối, các chunk sau vẫn tích lũy.
        const string sse =
            "data: {\"id\":\"chatcmpl-1\",\"model\":\"m\",\"created\":1e30,\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"He\"}}]}\n\n" +
            "data: {\"id\":\"chatcmpl-1\",\"model\":\"m\",\"created\":1700000000,\"choices\":[{\"index\":0,\"delta\":{\"content\":\"llo\"}}]}\n\n" +
            "data: {\"id\":\"chatcmpl-1\",\"model\":\"m\",\"created\":1700000000,\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n" +
            "data: [DONE]\n\n";
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None,
            captureCompletion: true);

        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));   // client nhận đủ byte nguyên vẹn
        Assert.NotNull(result.CompletionJson);
        using var doc = JsonDocument.Parse(result.CompletionJson!);
        // Degrade đã chứa lại: chunk 1 (created vượt phạm vi) bị bỏ → content thiếu mảnh "He";
        // created lấy từ chunk hợp lệ đầu tiên sau đó; stream vẫn kết thúc sạch.
        Assert.Equal("llo", doc.RootElement.GetProperty("choices")[0]
            .GetProperty("message").GetProperty("content").GetString());
        Assert.Equal(1700000000L, doc.RootElement.GetProperty("created").GetInt64());
        Assert.True(result.CompletionComplete);
    }

    [Fact]
    public async Task Tee_Sse_UsageTokensOutOfRange_NoThrowAndKeepsClientBytes()
    {
        // prompt_tokens 1e15 là JSON Number hợp lệ nhưng vượt phạm vi Int32 → GetInt32 trong
        // ScanSseLine ném lỗi chuyển đổi (KHÔNG phải JsonException). Fail-open: không throw,
        // dòng usage bị bỏ (Usage null), byte client vẫn forward nguyên vẹn.
        const string sse =
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
            "data: {\"usage\":{\"prompt_tokens\":1e15,\"completion_tokens\":3}}\n\n" +
            "data: [DONE]\n\n";
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var result = await UsageCapture.TeeAsync(content, dest, CancellationToken.None);

        Assert.Null(result.Usage);   // dòng usage hỏng bị bỏ — không last-wins từ nó
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
    }

    [Fact]
    public async Task Tee_NonStream_UsageTokensOutOfRange_NoThrowAndKeepsClientBytes()
    {
        // prompt_tokens 1e15 là JSON Number hợp lệ nhưng vượt phạm vi Int32 → GetInt32 trong
        // ParseUsage ném lỗi chuyển đổi (KHÔNG phải JsonException). Fail-open: branch non-stream
        // ghi body vào dest TRƯỚC khi ParseUsage chạy nên lỗi không được thoát ra ngoài TeeAsync.
        const string json = """{"id":"x","usage":{"prompt_tokens":1e15,"completion_tokens":3}}""";

        var (dest, result) = await TeeJsonAsync(json);

        Assert.Null(result.Usage);   // usage hỏng bị bỏ — degrade về null như body parse lỗi
        Assert.Equal(json, Encoding.UTF8.GetString(dest));
    }

    /// <summary>Clock mô phỏng timer fire trước khi elapsed-time check kịp bắt kịp.</summary>
    private sealed class StalledGraceClock : IGraceClock
    {
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long timestamp) => TimeSpan.Zero;
        public Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
