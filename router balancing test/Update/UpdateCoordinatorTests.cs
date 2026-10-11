using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Update;

namespace router_balancing_test.Update;

public class UpdateCoordinatorTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"update-coordinator-{Guid.NewGuid():N}");

    private string SnapshotPath => Path.Combine(_directory, "queue-snapshot.json");

    public UpdateCoordinatorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static ReleaseInfo Release() =>
        new("v9.9.9", "https://github.com/HKStudio011/router-balancing/releases/tag/v9.9.9", null, []);

    private static async Task WaitUntilAsync(Func<bool> condition, string because)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), $"điều kiện không đạt trong 5s: {because}");
    }

    private Fixtures CreateFixtures(Action<string>? record = null)
    {
        var gate = new DrainGate();
        var updates = new FakeAppUpdateService(record);
        var proxy = new FakeProxyHost(SnapshotPath, record);
        var shutdown = new RecordingShutdown(record);
        var coordinator = new UpdateCoordinator(updates, gate, proxy, shutdown, new NullLog(), SnapshotPath);
        return new Fixtures(coordinator, gate, updates, proxy, shutdown);
    }

    [Fact]
    public async Task StartUpdateAsync_WhenBothGatesDone_WritesSnapshotThenInstallsThenQuits()
    {
        // Thứ tự bắt buộc: snapshot → install → quit; IsFinalizing phải bật TRƯỚC khi quit
        var order = new List<string>();
        using var fx = CreateFixtures(order.Add);
        fx.Shutdown.ReadFinalizing = () => fx.Coordinator.IsFinalizing;

        var startTask = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Updates.DownloadStarted.IsCompleted, "download phải bắt đầu");
        fx.Updates.CompleteDownload(Path.Combine(_directory, "app-9.9.9.zip"));
        await WaitUntilAsync(() => fx.Shutdown.QuitCount == 1, "pipeline phải đi tới Quit");

        Assert.True(File.Exists(SnapshotPath));
        Assert.Equal(1, fx.Updates.InstallCalls);
        Assert.Equal(["snapshot", "install", "quit"], order);
        Assert.True(fx.Shutdown.FinalizingAtQuit, "IsFinalizing phải true tại thời điểm Quit");
        await startTask;
    }

    [Fact]
    public async Task StartUpdateAsync_WhenDownloadFails_ResumesDrainAndRaisesFailed()
    {
        using var fx = CreateFixtures();
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += failed.Add;

        var startTask = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Updates.DownloadStarted.IsCompleted, "download phải bắt đầu");
        fx.Updates.CompleteDownload(null);
        await startTask;

        Assert.Equal(UpdateError.DownloadFailed, Assert.Single(failed));
        // Review Focus: dispatcher không được park vĩnh viễn sau khi update fail
        Assert.False(fx.Gate.IsDraining);
        Assert.False(fx.Coordinator.UpdateInProgress);
        Assert.Equal(0, fx.Proxy.WriteCalls);
        Assert.Equal(0, fx.Shutdown.QuitCount);
    }

    [Fact]
    public async Task StartUpdateAsync_WhenSnapshotFails_ResumesDrainAndRaisesFailed()
    {
        using var fx = CreateFixtures();
        fx.Updates.CompleteDownload(Path.Combine(_directory, "app-9.9.9.zip"));
        fx.Proxy.WriteResult = false;
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += failed.Add;

        await fx.Coordinator.StartUpdateAsync(Release());

        Assert.Equal(UpdateError.SnapshotFailed, Assert.Single(failed));
        Assert.False(fx.Gate.IsDraining);
        Assert.False(fx.Coordinator.UpdateInProgress);
        Assert.Equal(1, fx.Proxy.WriteCalls);
        Assert.Equal(0, fx.Updates.InstallCalls);
        Assert.Equal(0, fx.Shutdown.QuitCount);
    }

    [Fact]
    public async Task StartUpdateAsync_WhenInstallFails_DeletesSnapshotAndResumesDrain()
    {
        using var fx = CreateFixtures();
        fx.Updates.CompleteDownload(Path.Combine(_directory, "app-9.9.9.zip"));
        fx.Updates.InstallResult = false;
        var failed = new List<UpdateError>();
        fx.Coordinator.Failed += failed.Add;

        await fx.Coordinator.StartUpdateAsync(Release());

        // Review Focus #5: file snapshot đã ghi phải bị xoá — tránh boot sau restore oan queue
        Assert.False(File.Exists(SnapshotPath));
        Assert.Equal(UpdateError.InstallFailed, Assert.Single(failed));
        Assert.False(fx.Gate.IsDraining);
        Assert.False(fx.Coordinator.UpdateInProgress);
        Assert.Equal(1, fx.Proxy.WriteCalls); // đã ghi file trước khi install fail
        Assert.Equal(0, fx.Shutdown.QuitCount);
    }

    [Fact]
    public async Task StartUpdateAsync_WhenProxyInFlightPositive_WaitsBeforeSnapshot()
    {
        using var fx = CreateFixtures();
        fx.Proxy.InFlight = 1;

        var startTask = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Updates.DownloadStarted.IsCompleted, "download phải bắt đầu");
        fx.Updates.CompleteDownload(Path.Combine(_directory, "app-9.9.9.zip"));

        // Ít nhất 1 chu kỳ poll (500ms) sau khi download xong — in-flight còn 1 thì không được snapshot
        await Task.Delay(700);
        Assert.Equal(0, fx.Proxy.WriteCalls);
        Assert.False(File.Exists(SnapshotPath));
        Assert.True(fx.Coordinator.UpdateInProgress);

        fx.Proxy.InFlight = 0;
        await WaitUntilAsync(() => fx.Proxy.WriteCalls == 1, "in-flight về 0 phải proceed tới snapshot");
        Assert.True(File.Exists(SnapshotPath));
        await WaitUntilAsync(() => fx.Shutdown.QuitCount == 1, "pipeline phải đi tới Quit");
        await startTask;
    }

    [Fact]
    public async Task CancelUpdate_BeforeFinalize_CancelsDownloadAndResumes()
    {
        using var fx = CreateFixtures();
        var cancelled = 0;
        fx.Coordinator.Cancelled += () => cancelled++;

        var startTask = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Updates.DownloadStarted.IsCompleted, "download phải bắt đầu");
        fx.Coordinator.CancelUpdate();
        await startTask;

        Assert.Equal(1, cancelled);
        Assert.False(fx.Gate.IsDraining);
        Assert.False(fx.Coordinator.UpdateInProgress);
        Assert.Equal(0, fx.Proxy.WriteCalls);
        Assert.Equal(0, fx.Shutdown.QuitCount);
        Assert.False(File.Exists(SnapshotPath));

        // Giải phóng task download còn treo để không rò background task sau test
        fx.Updates.CompleteDownload(Path.Combine(_directory, "app-9.9.9.zip"));
    }

    [Fact]
    public async Task CancelUpdate_DuringFinalize_IsNoOp()
    {
        using var fx = CreateFixtures();
        var cancelled = 0;
        fx.Coordinator.Cancelled += () => cancelled++;
        fx.Updates.CompleteDownload(Path.Combine(_directory, "app-9.9.9.zip"));
        // Chặn TryWrite để giữ pipeline đứng trong trạng thái finalise
        var writeBlock = fx.Proxy.BlockWrites();

        var startTask = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Coordinator.IsFinalizing, "pipeline phải vào finalise");
        fx.Coordinator.CancelUpdate();

        Assert.Equal(0, cancelled);
        Assert.True(fx.Gate.IsDraining);
        Assert.True(fx.Coordinator.UpdateInProgress);

        writeBlock.SetResult(true);
        await WaitUntilAsync(() => fx.Shutdown.QuitCount == 1, "huỷ trong finalise là no-op — pipeline vẫn quit");
        await startTask;
    }

    [Fact]
    public async Task StartUpdateAsync_WhenAlreadyInProgress_Throws()
    {
        using var fx = CreateFixtures();
        var first = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Coordinator.UpdateInProgress, "update đầu tiên phải đang chạy");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fx.Coordinator.StartUpdateAsync(Release()));

        fx.Coordinator.CancelUpdate();
        await first;
        fx.Updates.CompleteDownload(null);
    }

    [Fact]
    public async Task Dispose_CancelsPendingUpdate()
    {
        using var fx = CreateFixtures();
        var cancelled = 0;
        fx.Coordinator.Cancelled += () => cancelled++;

        var startTask = fx.Coordinator.StartUpdateAsync(Release());
        await WaitUntilAsync(() => fx.Coordinator.UpdateInProgress, "update phải đang chạy");
        fx.Coordinator.Dispose();
        await WaitUntilAsync(() => startTask.IsCompleted, "pipeline phải thoát sau Dispose");

        Assert.False(fx.Coordinator.UpdateInProgress);
        Assert.False(fx.Gate.IsDraining);
        // Dispose là teardown của app, không phải hành động người dùng — không bắn Cancelled
        Assert.Equal(0, cancelled);
        Assert.Equal(0, fx.Proxy.WriteCalls);
        Assert.Equal(0, fx.Shutdown.QuitCount);

        fx.Updates.CompleteDownload(Path.Combine(_directory, "app-9.9.9.zip"));
    }

    // ===== Test doubles =====

    private sealed record Fixtures(
        UpdateCoordinator Coordinator,
        DrainGate Gate,
        FakeAppUpdateService Updates,
        FakeProxyHost Proxy,
        RecordingShutdown Shutdown) : IDisposable
    {
        public void Dispose() => Coordinator.Dispose();
    }

    /// <summary>Fake IAppUpdateService — download kiểm soát bằng TCS, install trả cờ set được.</summary>
    private sealed class FakeAppUpdateService(Action<string>? record = null) : IAppUpdateService
    {
        private readonly TaskCompletionSource<string?> _download =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource _downloadStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _installCalls;

        public string CurrentVersion => "9.9.8.0";

        public Task DownloadStarted => _downloadStarted.Task;

        public string? DownloadedZipPath { get; set; }

        public bool InstallResult { get; set; } = true;

        public int InstallCalls => Volatile.Read(ref _installCalls);

        public Task<ReleaseInfo?> CheckForUpdatesAsync() => Task.FromResult<ReleaseInfo?>(null);

        public bool IsUpdateAvailable(ReleaseInfo release) => false;

        public Task<string?> DownloadUpdateAsync(ReleaseInfo release, IProgress<int>? progress)
        {
            _downloadStarted.TrySetResult();
            return _download.Task;
        }

        public void CompleteDownload(string? path) => _download.TrySetResult(path);

        public Task<bool> InstallUpdateAsync()
        {
            // Ghi nhận trước khi trả kết quả — thứ tự "snapshot → install → quit" kiểm chứng được
            record?.Invoke("install");
            Interlocked.Increment(ref _installCalls);
            return Task.FromResult(InstallResult);
        }
    }

    /// <summary>Fake IProxyHost — chỉ GetTotalInFlight/TryWriteQueueSnapshotAsync hoạt động, còn lại ném NotSupportedException.</summary>
    private sealed class FakeProxyHost(string snapshotPath, Action<string>? record = null) : IProxyHost
    {
        private int _inFlight;
        private int _writeCalls;
        private TaskCompletionSource<bool>? _writeBlock;

        public int InFlight
        {
            get => Volatile.Read(ref _inFlight);
            set => Volatile.Write(ref _inFlight, value);
        }

        public bool WriteResult { get; set; } = true;

        public int WriteCalls => Volatile.Read(ref _writeCalls);

        /// <summary>Chặn TryWrite cho tới khi test giải phóng — giữ pipeline trong trạng thái finalise.</summary>
        public TaskCompletionSource<bool> BlockWrites()
        {
            _writeBlock = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return _writeBlock;
        }

        public int GetTotalInFlight() => InFlight;

        public async Task<bool> TryWriteQueueSnapshotAsync()
        {
            var block = _writeBlock;
            if (block is not null)
            {
                await block.Task;
            }

            Interlocked.Increment(ref _writeCalls);
            if (!WriteResult)
            {
                return false;
            }

            // Ghi file thật vào đúng đường dẫn coordinator nhận — test install-fail kiểm chứng file bị xoá
            await File.WriteAllTextAsync(snapshotPath, """{"version":1,"requests":[]}""");
            record?.Invoke("snapshot");
            return true;
        }

        public int? Port => throw new NotSupportedException();

        public bool IsRunning => throw new NotSupportedException();

        public event Action? StateChanged
        {
            add => throw new NotSupportedException();
            remove => throw new NotSupportedException();
        }

        public Task StartAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RestartAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IReadOnlyList<QueuedTraceItem> QueuedSnapshot() => throw new NotSupportedException();

        public RequestCancelResult CancelRequest(string id) => throw new NotSupportedException();

        public QueuedRequestInfo? TryGetQueued(string id) => throw new NotSupportedException();

        public RequestPriorityResult SetRequestPriority(string id, RequestPriority priority) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => throw new NotSupportedException();
    }

    /// <summary>IAppShutdown ghi nhận số lần Quit — ReadFinalizing capture state coordinator đúng lúc Quit.</summary>
    private sealed class RecordingShutdown(Action<string>? record = null) : IAppShutdown
    {
        private int _quitCount;

        public int QuitCount => Volatile.Read(ref _quitCount);

        /// <summary>Đọc IsFinalizing của coordinator tại thời điểm Quit chạy.</summary>
        public Func<bool>? ReadFinalizing { get; set; }

        public bool? FinalizingAtQuit { get; private set; }

        public void Quit()
        {
            FinalizingAtQuit = ReadFinalizing?.Invoke();
            record?.Invoke("quit");
            Interlocked.Increment(ref _quitCount);
        }
    }
}
