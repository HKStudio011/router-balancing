using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Settings;

public class AppSettingsServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public AppSettingsServiceTests()
    {
        // TestDb chỉ tạo file trống — phải migrate schema trước khi service đọc AppSettings
        DbInitializer.Initialize(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    private AppSettingsService Create() => new(_db.CreateFactory());

    [Fact]
    public void Get_MissingKey_ReturnsDefault()
    {
        using var service = Create();

        Assert.Equal(8317, service.Get(SettingsKeys.Port, 8317));
        Assert.Equal("auto", service.Language);
        Assert.Equal(60, service.ProviderProbeTimeoutSec);
        Assert.Equal(90, service.LogRetentionDays);
        Assert.True(service.CloseToTray);
    }

    [Fact]
    public void Set_PersistsToDatabase_NewServiceInstanceReadsSameValue()
    {
        using (var service = Create())
        {
            service.Set(SettingsKeys.Port, 9000);
        }

        using var reopened = Create();

        Assert.Equal(9000, reopened.Port);
    }

    [Fact]
    public void Set_FiresSettingsChanged()
    {
        using var service = Create();
        var raised = 0;
        service.SettingsChanged += () => raised++;

        service.Set(SettingsKeys.Theme, "dark");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Set_WhenCalledConcurrently_KeepsCacheConsistent()
    {
        using var service = Create();
        var raised = 0;
        service.SettingsChanged += () => Interlocked.Increment(ref raised);

        const int taskCount = 6;
        const int iterationsPerTask = 20;

        // Nhiều task ghi cùng một cặp key để race đọc/ghi trên đúng key xảy ra
        var tasks = Enumerable.Range(0, taskCount).Select(i => Task.Run(() =>
        {
            for (var n = 0; n < iterationsPerTask; n++)
            {
                service.Set(SettingsKeys.Theme, $"v{i}-{n}");
                service.Set(SettingsKeys.Port, i * 1000 + n);
                _ = service.Theme;
                _ = service.Port;
            }
        })).ToArray();

        Assert.Null(Record.Exception(() => Task.WaitAll(tasks)));

        // Mỗi Set thành công phát event đúng một lần
        Assert.Equal(taskCount * iterationsPerTask * 2, raised);

        // Đọc được sau race và instance nạp lại từ DB cho cùng giá trị → cache không lệch DB
        using var reopened = Create();
        Assert.Matches(@"^v\d+-\d+$", service.Theme);
        Assert.InRange(service.Port, 0, (taskCount - 1) * 1000 + iterationsPerTask);
        Assert.Equal(service.Theme, reopened.Theme);
        Assert.Equal(service.Port, reopened.Port);
    }
}
