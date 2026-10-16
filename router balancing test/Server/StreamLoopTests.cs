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
}
