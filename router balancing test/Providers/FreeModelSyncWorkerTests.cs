using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class FreeModelSyncWorkerTests
{
    [Fact]
    public async Task Start_WithZeroInitialDelay_CallsSyncAllOnce()
    {
        var sync = new RecordingSyncService();
        await using var worker = new FreeModelSyncWorker(
            sync, new NullLog(), period: TimeSpan.FromHours(6), initialDelay: TimeSpan.Zero);

        worker.Start();

        await sync.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, sync.CallCount);
    }

    [Fact]
    public async Task Start_Twice_OnlyOneLoop()
    {
        var sync = new RecordingSyncService();
        await using var worker = new FreeModelSyncWorker(
            sync, new NullLog(), period: TimeSpan.FromHours(6), initialDelay: TimeSpan.Zero);
        worker.Start();
        worker.Start(); // no-op

        await sync.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.DisposeAsync();
        var afterDispose = sync.CallCount;
        await Task.Delay(200);

        Assert.Equal(afterDispose, sync.CallCount); // không còn vòng lặp nào chạy sau dispose
    }

    [Fact]
    public async Task Dispose_BeforeInitialDelay_DoesNotCallSync()
    {
        var sync = new RecordingSyncService();
        var worker = new FreeModelSyncWorker(
            sync, new NullLog(), period: TimeSpan.FromHours(6), initialDelay: TimeSpan.FromHours(1));

        worker.Start();
        await worker.DisposeAsync();
        await Task.Delay(100);

        Assert.Equal(0, sync.CallCount); // app đóng trước delay → không sync lần nào
    }

    private sealed class RecordingSyncService : IFreeModelSyncService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public TaskCompletionSource FirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> SyncProviderAsync(long providerId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<(long ProviderId, int ModelCount)>> SyncAllEnabledAsync(
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            FirstCall.TrySetResult();
            return Task.FromResult<IReadOnlyList<(long ProviderId, int ModelCount)>>([]);
        }
    }
}
