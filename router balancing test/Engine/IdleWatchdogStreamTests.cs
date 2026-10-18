using System.Net;
using System.Text;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class IdleWatchdogStreamTests
{
    private static readonly byte[] Chunk = "data: hi\n\n"u8.ToArray();

    [Fact]
    public async Task ReadAsync_WhenInnerDeliversBytes_PassesThroughAndTracksLastByteAt()
    {
        using var inner = new DeliverThenBlockStream(Chunk);
        var watchdog = new IdleWatchdogStream(inner, TimeSpan.FromSeconds(30));
        var start = DateTimeOffset.UtcNow;
        var buffer = new byte[64];

        var read = await watchdog.ReadAsync(buffer.AsMemory(), CancellationToken.None);

        Assert.Equal(Chunk.Length, read);
        Assert.Equal(Chunk, buffer.AsSpan(0, read).ToArray());
        Assert.True(watchdog.LastByteAt >= start);
    }

    [Fact]
    public async Task ReadAsync_WhenUpstreamSilentBeyondIdleTimeout_ThrowsUpstreamIdleTimeoutException()
    {
        using var inner = new BlockForeverStream();
        var watchdog = new IdleWatchdogStream(inner, TimeSpan.FromMilliseconds(50));
        var buffer = new byte[64];

        // IOException để vòng đọc (UsageCapture/protocol) bọc marker pre-content → retry/failover
        var ex = await Assert.ThrowsAsync<UpstreamIdleTimeoutException>(
            async () => await watchdog.ReadAsync(buffer.AsMemory(), CancellationToken.None));

        Assert.IsAssignableFrom<IOException>(ex);
        Assert.Equal(TimeSpan.FromMilliseconds(50), ex.IdleTimeout);
    }

    [Fact]
    public async Task ReadAsync_WhenCallerCancels_PropagatesOperationCanceledExceptionNotIdle()
    {
        using var inner = new BlockForeverStream();
        var watchdog = new IdleWatchdogStream(inner, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var buffer = new byte[64];

        // Client abort phải giữ nguyên OCE (dispatcher map Aborted) — không được convert thành idle
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await watchdog.ReadAsync(buffer.AsMemory(), cts.Token));
    }

    [Fact]
    public async Task ReadAsync_WhenInnerDiesAfterBytes_WrapsWithSilenceGap()
    {
        using var inner = new DeliverThenThrowStream(Chunk, new IOException("reset"));
        var watchdog = new IdleWatchdogStream(inner, TimeSpan.FromSeconds(30));
        var buffer = new byte[64];

        await watchdog.ReadAsync(buffer.AsMemory(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<UpstreamStreamDiedException>(
            async () => await watchdog.ReadAsync(buffer.AsMemory(), CancellationToken.None));

        // Giữ lỗi gốc để journal ErrorCode/stack đúng — gap ≈0 = chết giữa token (Class B)
        Assert.IsType<IOException>(ex.InnerException);
        Assert.True(ex.SilenceGap < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReadAsync_WhenInnerDiesBeforeAnyByte_WrapsWithSmallGap()
    {
        using var inner = new DeliverThenThrowStream([], new IOException("reset"));
        var watchdog = new IdleWatchdogStream(inner, TimeSpan.FromSeconds(30));
        var buffer = new byte[64];

        var ex = await Assert.ThrowsAsync<UpstreamStreamDiedException>(
            async () => await watchdog.ReadAsync(buffer.AsMemory(), CancellationToken.None));

        Assert.IsType<IOException>(ex.InnerException);
        Assert.True(ex.SilenceGap < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WatchdogContent_WhenInnerSilent_ThrowsIdleTimeoutAndKeepsContentType()
    {
        using var inner = new BlockingSseContent();
        using var content = new UpstreamWatchdogContent(inner, TimeSpan.FromMilliseconds(50));

        // Protocol đọc Content-Type để phân biệt SSE/JSON — header phải copy sang wrapper
        Assert.Equal("text/event-stream", content.Headers.ContentType?.MediaType);
        var stream = await content.ReadAsStreamAsync();
        var buffer = new byte[64];

        await Assert.ThrowsAsync<UpstreamIdleTimeoutException>(
            async () => await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None));
    }

    [Fact]
    public async Task WatchdogContent_WhenInnerDelivers_PassesBytesThrough()
    {
        using var inner = new StringContent("""{"ok":1}""", Encoding.UTF8, "application/json");
        using var content = new UpstreamWatchdogContent(inner, TimeSpan.FromSeconds(30));

        var stream = await content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);

        Assert.Equal("application/json", content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"ok":1}""", await reader.ReadToEndAsync());
    }

    /// <summary>Trả byte đầu rồi chặn vô hạn — mô phỏng upstream im lặng giữa chừng.</summary>
    private sealed class DeliverThenBlockStream(byte[] first) : Stream
    {
        private bool _delivered;

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

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken ct = default)
        {
            if (_delivered)
            {
                await Task.Delay(Timeout.Infinite, ct);
                return 0;
            }
            _delivered = true;
            first.CopyTo(buffer.Span);
            return first.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>Mỗi lần đọc chặn vô hạn — chỉ watchdog timer cắt được.</summary>
    private sealed class BlockForeverStream : Stream
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

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>Trả byte (có thể rỗng) rồi nổ IOException — mô phỏng RST giữa chừng.</summary>
    private sealed class DeliverThenThrowStream(byte[] first, Exception error) : Stream
    {
        private bool _delivered;

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

        public override ValueTask<int> ReadAsync(Memory<byte> buffer,
            CancellationToken ct = default)
        {
            // first rỗng = chết ngay lần đọc đầu (pre-content); không thì trả byte rồi nổ ở lần sau
            if (first.Length == 0 || _delivered) throw error;
            _delivered = true;
            first.CopyTo(buffer.Span);
            return ValueTask.FromResult(first.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>Content SSE không bao giờ trả byte — mô phỏng upstream treo (blackhole).</summary>
    private sealed class BlockingSseContent : HttpContent
    {
        public BlockingSseContent() =>
            Headers.TryAddWithoutValidation("Content-Type", "text/event-stream");

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new BlockForeverStream());

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.CompletedTask;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
