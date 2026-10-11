using RouterBalancing.Core.Update;

namespace router_balancing_test.Update;

public class UpdateCheckServiceTests
{
    private static ReleaseInfo Release(string tag = "v9.9.9") =>
        new(tag, $"https://github.com/HKStudio011/router-balancing/releases/tag/{tag}", null, []);

    [Fact]
    public async Task CheckOnceAsync_UpdateAvailable_SetsStateAndRaisesUpdateFound()
    {
        var release = Release();
        var updates = new FakeAppUpdateService { NextRelease = release };
        await using var service = new UpdateCheckService(updates, new NullLog());
        var found = new List<ReleaseInfo>();
        var stateChanged = 0;
        service.UpdateFound += found.Add;
        service.StateChanged += () => stateChanged++;

        var result = await service.CheckOnceAsync();

        Assert.Equal(UpdateCheckResult.UpdateAvailable, result);
        Assert.True(service.IsUpdateAvailable);
        Assert.Same(release, service.AvailableRelease);
        Assert.Same(release, Assert.Single(found));
        Assert.Equal(1, stateChanged);
    }

    [Fact]
    public async Task CheckOnceAsync_SecondTimeWithSameRelease_DoesNotRaiseUpdateFoundAgain()
    {
        var updates = new FakeAppUpdateService { NextRelease = Release() };
        await using var service = new UpdateCheckService(updates, new NullLog());
        var found = 0;
        var stateChanged = 0;
        service.UpdateFound += _ => found++;
        service.StateChanged += () => stateChanged++;

        await service.CheckOnceAsync();
        var second = await service.CheckOnceAsync();

        // Transition chỉ 1 lần — badge đã thấy; state không đổi nên StateChanged cũng không bắn thêm
        Assert.Equal(UpdateCheckResult.UpdateAvailable, second);
        Assert.Equal(1, found);
        Assert.Equal(1, stateChanged);
        Assert.True(service.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckOnceAsync_AlreadyLatest_ClearsPreviousState()
    {
        var updates = new FakeAppUpdateService { NextRelease = Release() };
        await using var service = new UpdateCheckService(updates, new NullLog());
        await service.CheckOnceAsync();
        Assert.True(service.IsUpdateAvailable);

        updates.NewerAvailable = false; // check thứ 2 báo "bằng nhau"
        var stateChanged = 0;
        var found = 0;
        service.StateChanged += () => stateChanged++;
        service.UpdateFound += _ => found++;

        var result = await service.CheckOnceAsync();

        Assert.Equal(UpdateCheckResult.AlreadyLatest, result);
        Assert.False(service.IsUpdateAvailable);
        Assert.Null(service.AvailableRelease);
        Assert.Equal(1, stateChanged); // clear badge → re-render đúng 1 lần
        Assert.Equal(0, found);
    }

    [Fact]
    public async Task CheckOnceAsync_WhenCheckReturnsNull_ReturnsFailed_StateUnchanged()
    {
        var updates = new FakeAppUpdateService { NextRelease = Release() };
        await using var service = new UpdateCheckService(updates, new NullLog());
        await service.CheckOnceAsync();
        var discovered = service.AvailableRelease;

        updates.NextRelease = null; // check fail (mất mạng, GitHub lỗi...)
        var events = 0;
        service.StateChanged += () => events++;
        service.UpdateFound += _ => events++;

        var result = await service.CheckOnceAsync();

        // Một lần check lỗi không được xoá badge của bản update đã phát hiện trước đó
        Assert.Equal(UpdateCheckResult.Failed, result);
        Assert.True(service.IsUpdateAvailable);
        Assert.Same(discovered, service.AvailableRelease);
        Assert.Equal(0, events);
    }

    [Fact]
    public async Task Start_OnNonWindows_IsNoOp()
    {
        var updates = new FakeAppUpdateService { NextRelease = Release() };
        // isSupported=false mô phỏng non-Windows; initialDelay=Zero để nếu guard hỏng,
        // loop chạy check ngay trong lúc chờ — assert mới có sức kiểm chứng
        var service = new UpdateCheckService(
            updates, new NullLog(), isSupported: false, initialDelay: TimeSpan.Zero);

        service.Start();
        await Task.Delay(200);

        Assert.Equal(0, updates.CheckCalls);
        await service.DisposeAsync(); // dispose ngay — không ném
    }

    // ===== Test doubles =====

    /// <summary>Fake IAppUpdateService — check trả release set được, cờ IsUpdateAvailable điều khiển được.</summary>
    private sealed class FakeAppUpdateService : IAppUpdateService
    {
        private int _checkCalls;

        public ReleaseInfo? NextRelease { get; set; }

        /// <summary>Kết quả của IsUpdateAvailable — false mô phỏng "bằng nhau" (AlreadyLatest).</summary>
        public bool NewerAvailable { get; set; } = true;

        public int CheckCalls => Volatile.Read(ref _checkCalls);

        public string CurrentVersion => "9.9.8.0";

        public string? DownloadedZipPath { get; set; }

        public Task<ReleaseInfo?> CheckForUpdatesAsync()
        {
            Interlocked.Increment(ref _checkCalls);
            return Task.FromResult(NextRelease);
        }

        public bool IsUpdateAvailable(ReleaseInfo release) => NewerAvailable;

        public Task<string?> DownloadUpdateAsync(ReleaseInfo release, IProgress<int>? progress) =>
            throw new NotSupportedException();

        public Task<bool> InstallUpdateAsync() => throw new NotSupportedException();
    }
}
