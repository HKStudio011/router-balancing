using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

/// <summary>
/// Test cơ chế observe task bị bỏ rơi của <see cref="TaskObservation"/> — chống
/// UnobservedTaskException bị finalizer ném lại (lỗi thật 2026-10-19: OCE từ
/// <c>pipe.Reader.ReadAsync(ct).AsTask()</c> bị bỏ rơi ở nhánh client-abort stream loop).
/// </summary>
public class TaskObservationTests
{
    [Fact]
    public async Task ObserveFailure_CanceledPipeRead_SuppressesUnobservedEscalation()
    {
        var escalated = false;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            // Chỉ tính task dạng ValueTaskSourceAsTask (pipe read bị hủy) — task lạ từ test
            // class chạy song song không được làm fail oan.
            escalated |= e.Exception.InnerExceptions.Any(ex =>
                ex is OperationCanceledException
                && ex.StackTrace?.Contains("ValueTaskSourceAsTask") == true);
            e.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            CreateCanceledReadObservedByHelper();
            await FinalizeAllAsync();
            Assert.False(escalated);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    /// <summary>
    /// ObserveFailure không được nuốt/mất exception gốc: awaiter khác vẫn phải thấy đúng
    /// exception đó (observe chỉ đánh dấu, không đổi outcome của task).
    /// </summary>
    [Fact]
    public void ObserveFailure_FaultedTask_KeepsExceptionForOtherAwaiters()
    {
        var fault = new InvalidOperationException("faulted");
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetException(fault);

        TaskObservation.ObserveFailure(tcs.Task);

        var thrown = Assert.Throws<InvalidOperationException>(
            () => tcs.Task.GetAwaiter().GetResult());
        Assert.Same(fault, thrown);
    }

    /// <summary>Task canceled: cùng contract — không ném ra ngoài, awaiter khác vẫn thấy TCE.</summary>
    [Fact]
    public void ObserveFailure_CanceledTask_KeepsCancellationForOtherAwaiters()
    {
        var task = Task.FromCanceled(new CancellationToken(canceled: true));

        TaskObservation.ObserveFailure(task);

        var thrown = Assert.ThrowsAny<OperationCanceledException>(
            () => task.GetAwaiter().GetResult());
        Assert.IsType<TaskCanceledException>(thrown);
    }

    // NoInlining: tham chiếu task/pipe không được sống sót trong frame test — phải thành
    // unreachable thì task mới hết finalizable và cơ chế unobserved mới có cơ hội chạy.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateCanceledReadObservedByHelper()
    {
        var pipe = new Pipe();
        using var cts = new CancellationTokenSource();
        var readTask = pipe.Reader.ReadAsync(cts.Token).AsTask();

        cts.Cancel();

        TaskObservation.ObserveFailure(readTask);
    }

    // GC nhiều vòng: TaskExceptionHolder cần finalizer chạy xong mới báo unobserved —
    // đủ vòng để task chết ở gen bất kỳ đều được thu và xử lý.
    private static async Task FinalizeAllAsync()
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            await Task.Yield();
        }
    }
}
