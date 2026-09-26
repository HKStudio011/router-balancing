using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Logging;

public class LogRetentionWorkerTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;

    public LogRetentionWorkerTests()
    {
        // TestDb chỉ tạo file trống — phải migrate schema trước khi ghi AppSettings/LogEntry
        DbInitializer.Initialize(_db.CreateFactory());
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory, new DpapiSecretProtector());
        _log = new LogService(factory);
    }

    public void Dispose()
    {
        _log.Dispose();
        _settings.Dispose();
        _db.Dispose();
    }

    private LogRetentionWorker CreateWorker() => new(_db.CreateFactory(), _settings, _log);

    private void WriteEntryAt(int daysAgo) =>
        _log.Write(new LogEntry
        {
            Message = $"entry {daysAgo}d",
            Timestamp = DateTimeOffset.UtcNow.AddDays(-daysAgo),
        });

    [Fact]
    public async Task PurgeAsync_WhenEntryPastRetention_DeletesOnlyExpired()
    {
        WriteEntryAt(100);
        WriteEntryAt(1);
        var worker = CreateWorker();

        var removed = await worker.PurgeAsync();

        Assert.Equal(1, removed);
        var remaining = Assert.Single(_log.Query(new LogQuery()));
        Assert.Equal("entry 1d", remaining.Message);
    }

    [Fact]
    public async Task PurgeAsync_WhenAllEntriesWithinRetention_ReturnsZero()
    {
        WriteEntryAt(1);
        var worker = CreateWorker();

        var removed = await worker.PurgeAsync();

        Assert.Equal(0, removed);
        Assert.Single(_log.Query(new LogQuery()));
    }

    [Fact]
    public async Task PurgeAsync_WhenRetentionDaysChanged_UsesNewValue()
    {
        _settings.Set(SettingsKeys.LogRetentionDays, 7);
        WriteEntryAt(10);
        WriteEntryAt(2);
        var worker = CreateWorker();

        var removed = await worker.PurgeAsync();

        Assert.Equal(1, removed);
        Assert.Equal("entry 2d", Assert.Single(_log.Query(new LogQuery())).Message);
    }

    [Fact]
    public async Task Start_ThenDispose_PurgesOnStartAndStopsCleanly()
    {
        WriteEntryAt(100);
        var worker = CreateWorker();

        worker.Start();
        // Purge đầu chạy ngay khi Start — poll tối đa 5s thay vì sleep cố định
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_log.Count(new LogQuery()) > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
        await worker.DisposeAsync();

        Assert.Equal(0, _log.Count(new LogQuery()));
    }
}
