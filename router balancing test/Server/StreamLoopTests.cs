using System.IO.Pipelines;
using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Server;

namespace router_balancing_test.Server;

/// <summary>
/// Unit test trực tiếp <see cref="ProxyApp.RunStreamLoopAsync"/> — nhánh keep-alive SSE
/// (bug 2026-10-16: keep-alive dính giữa dòng data chưa đóng phá framing — client parse
/// JSON lỗi: "Expected ',' or '}' ... position 96" đúng sau chuỗi cắt + ": keep-alive").
/// </summary>
public class StreamLoopTests
{
    /// <summary>
    /// Dòng <c>data:</c> chưa đóng (TCP split mid-JSON) thì keep-alive KHÔNG được viết —
    /// comment dính vào dòng cụt sẽ bị client gộp vào cùng dòng → JSON hỏng.
    /// </summary>
    [Fact]
    public async Task RunStreamLoop_DataLineNotTerminated_SkipsKeepAliveUntilBoundary()
    {
        var ctx = new DefaultHttpContext { RequestAborted = CancellationToken.None };
        var body = new MemoryStream();
        ctx.Response.Body = body;
        var pipe = new Pipe();
        var completion = new TaskCompletionSource<DispatchOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Nửa dòng JSON không có \n — mô phỏng upstream chunk bị cắt giữa chừng
        var partial = "data: {\"created\":179212631"u8.ToArray();
        await pipe.Writer.WriteAsync(partial);
        await pipe.Writer.FlushAsync();

        var loop = ProxyApp.RunStreamLoopAsync(ctx, pipe, completion.Task);

        // Quá KeepAliveIntervalMs (5s) mà dòng chưa đóng — im lặng, không giữ kết nối bằng cách phá framing
        await Task.Delay(TimeSpan.FromMilliseconds(5500));
        Assert.Equal(partial, body.ToArray());

        // Đóng dòng + outcome về → drain nốt phần còn lại, loop thoát
        await pipe.Writer.WriteAsync("}\n\n"u8.ToArray());
        await pipe.Writer.FlushAsync();
        pipe.Writer.Complete();
        completion.SetResult(new DispatchOutcome.Handled());

        var (outcome, contentBytes) = await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(partial.Length + 3, contentBytes);
        Assert.Equal("data: {\"created\":179212631}\n\n", Encoding.UTF8.GetString(body.ToArray()));
    }

    /// <summary>
    /// Client chết ở tầng write (keep-alive ném OCE) khi read còn pending → loop phải rời qua
    /// nhánh catch-abort (await completion, không deadlock không cần timeout) và trả đúng
    /// outcome; task read bị bỏ rơi ở chính nhánh này được observe bởi finally — trước đây
    /// nó faulted/OCE không ai đọc → finalizer ném lại làm sập tiến trình (lỗi 2026-10-19).
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

        // Chưa có data (read pending) + chưa outcome: sau KeepAliveIntervalMs (5s) loop chạm
        // keep-alive đầu tiên → write ném OCE → rơi vào catch abort, chờ completion
        await Task.Delay(TimeSpan.FromMilliseconds(5600));
        Assert.False(loop.IsCompleted);

        completion.SetResult(new DispatchOutcome.Aborted());

        var (outcome, contentBytes) = await loop.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<DispatchOutcome.Aborted>(outcome);
        Assert.Equal(0, contentBytes);

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
