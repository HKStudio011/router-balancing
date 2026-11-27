using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Server;

namespace router_balancing_test.Server;

/// <summary>
/// Unit test trực tiếp <see cref="ProxyApp.RunStreamLoopAsync"/> — event buffering,
/// keep-alive, drain và hai byte counters.
/// </summary>
public class StreamLoopTests
{
    [Fact]
    public async Task RunStreamLoop_PartialEventBeforeBoundary_BuffersAndKeepsAlive()
    {
        var ctx = new DefaultHttpContext { RequestAborted = CancellationToken.None };
        var body = new MemoryStream();
        ctx.Response.Body = body;
        var pipe = new Pipe();
        var completion = new TaskCompletionSource<DispatchOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var partial = "data: {\"created\":179212631"u8.ToArray();
        var keepAlive = ": keep-alive\n\n"u8.ToArray();
        var fullEvent = "data: {\"created\":179212631}\n\n"u8.ToArray();
        await pipe.Writer.WriteAsync(partial);
        await pipe.Writer.FlushAsync();

        var loop = ProxyApp.RunStreamLoopAsync(ctx, pipe, completion.Task);

        // Fragment chưa được forward, nên wire vẫn ở boundary và keep-alive an toàn.
        await Task.Delay(TimeSpan.FromMilliseconds(5500));
        Assert.Equal(keepAlive, body.ToArray());

        await pipe.Writer.WriteAsync("}\n\n"u8.ToArray());
        await pipe.Writer.FlushAsync();
        pipe.Writer.Complete();
        completion.SetResult(new DispatchOutcome.Handled());

        var (outcome, contentBytes, upstreamBytes) = await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(fullEvent.Length, contentBytes);
        Assert.Equal(fullEvent.Length, upstreamBytes);
        Assert.Equal(keepAlive.Concat(fullEvent).ToArray(), body.ToArray());
    }

    [Fact]
    public async Task RunStreamLoop_PartialEventThenAborted_DropsFragmentButCountsUpstreamBytes()
    {
        var ctx = new DefaultHttpContext { RequestAborted = CancellationToken.None };
        var body = new MemoryStream();
        ctx.Response.Body = body;
        var pipe = new Pipe();
        var completion = new TaskCompletionSource<DispatchOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var partial = "data: {\"choices\":["u8.ToArray();
        await pipe.Writer.WriteAsync(partial);
        await pipe.Writer.FlushAsync();
        completion.SetResult(new DispatchOutcome.Aborted());

        var (outcome, contentBytes, upstreamBytes) = await ProxyApp.RunStreamLoopAsync(
            ctx, pipe, completion.Task).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsType<DispatchOutcome.Aborted>(outcome);
        Assert.Equal(0, contentBytes);
        Assert.Equal(partial.Length, upstreamBytes);
        Assert.Empty(body.ToArray());
    }

    [Fact]
    public async Task RunStreamLoop_CompleteEventSplitAcrossReads_ForwardsOnlyAtBoundary()
    {
        var ctx = new DefaultHttpContext { RequestAborted = CancellationToken.None };
        var body = new MemoryStream();
        ctx.Response.Body = body;
        var pipe = new Pipe();
        var completion = new TaskCompletionSource<DispatchOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await pipe.Writer.WriteAsync("data: one\n"u8.ToArray());
        await pipe.Writer.FlushAsync();

        var loop = ProxyApp.RunStreamLoopAsync(ctx, pipe, completion.Task);
        await Task.Delay(100);
        Assert.Empty(body.ToArray());

        var tail = "data: two\n\n"u8.ToArray();
        await pipe.Writer.WriteAsync(tail);
        await pipe.Writer.FlushAsync();
        completion.SetResult(new DispatchOutcome.Handled());
        var fullEvent = "data: one\ndata: two\n\n"u8.ToArray();

        var (outcome, contentBytes, upstreamBytes) = await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(fullEvent.Length, contentBytes);
        Assert.Equal(fullEvent.Length, upstreamBytes);
        Assert.Equal(fullEvent, body.ToArray());
    }

    [Fact]
    public async Task RunStreamLoop_HandledWithoutBlankLine_FlushesTailAtEof()
    {
        var ctx = new DefaultHttpContext { RequestAborted = CancellationToken.None };
        var body = new MemoryStream();
        ctx.Response.Body = body;
        var pipe = new Pipe();
        var completion = new TaskCompletionSource<DispatchOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tail = "data: [DONE]"u8.ToArray();
        await pipe.Writer.WriteAsync(tail);
        await pipe.Writer.FlushAsync();
        completion.SetResult(new DispatchOutcome.Handled());

        var (outcome, contentBytes, upstreamBytes) = await ProxyApp.RunStreamLoopAsync(
            ctx, pipe, completion.Task).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(tail.Length, contentBytes);
        Assert.Equal(tail.Length, upstreamBytes);
        Assert.Equal(tail, body.ToArray());
    }

    [Fact]
    public async Task RunStreamLoop_PendingEventOverOneMiB_ThrowsUpstreamStreamDied()
    {
        var ctx = new DefaultHttpContext { RequestAborted = CancellationToken.None };
        ctx.Response.Body = new MemoryStream();
        var pipe = new Pipe();
        var completion = new TaskCompletionSource<DispatchOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var loop = ProxyApp.RunStreamLoopAsync(ctx, pipe, completion.Task);
        var writeTask = pipe.Writer.WriteAsync(new byte[1024 * 1024 + 1]);

        await Assert.ThrowsAsync<UpstreamStreamDiedException>(() =>
            loop.WaitAsync(TimeSpan.FromMilliseconds(200)));
        pipe.Writer.Complete();
        _ = writeTask.AsTask().ContinueWith(static task => { _ = task.Exception; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Client chết ở tầng write (keep-alive ném OCE) khi read còn pending → loop phải rời qua
    /// nhánh catch-abort (await completion, không deadlock không cần timeout) và trả đúng outcome.
    /// </summary>
    [Fact]
    public async Task RunStreamLoop_KeepAliveWriteThrowsOce_ReturnsOutcomeWhenCompletionArrives()
    {
        var ctx = new DefaultHttpContext { RequestAborted = CancellationToken.None };
        ctx.Response.Body = new CanceledWriteStream();
        var pipe = new Pipe();
        var completion = new TaskCompletionSource<DispatchOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var loop = ProxyApp.RunStreamLoopAsync(ctx, pipe, completion.Task);
        await Task.Delay(TimeSpan.FromMilliseconds(5600));
        Assert.False(loop.IsCompleted);

        completion.SetResult(new DispatchOutcome.Aborted());

        var (outcome, contentBytes, upstreamBytes) = await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<DispatchOutcome.Aborted>(outcome);
        Assert.Equal(0, contentBytes);
        Assert.Equal(0, upstreamBytes);

        pipe.Writer.Complete();
    }

    /// <summary>Stream đích luôn ném OCE khi write — mô phỏng client đã ngắt ở tầng socket.</summary>
    private sealed class CanceledWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get; set; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override Task WriteAsync(byte[] buffer, int offset, int count,
            CancellationToken ct) =>
            Task.FromCanceled(new CancellationToken(canceled: true));
    }
}
