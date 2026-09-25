using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Settings;

public class AppSettingsServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public AppSettingsServiceTests()
    {
        // TestDb chỉ tạo file trống — phải migrate schema trước khi service đọc AppSettings
        DbInitializer.Initialize(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    private AppSettingsService Create() => new(_db.CreateFactory(), _protector);

    [Fact]
    public void Get_MissingKey_ReturnsDefault()
    {
        using var service = Create();

        Assert.Equal(8317, service.Get(SettingsKeys.Port, 8317));
        Assert.Equal("auto", service.Language);
        Assert.Equal(3, service.MaxRetry);
        Assert.Equal(90, service.LogRetentionDays);
        Assert.True(service.CloseToTray);
    }

    [Fact]
    public void Set_ThenGet_ReturnsValue()
    {
        using var service = Create();

        service.Set(SettingsKeys.MaxRetry, 5);

        Assert.Equal(5, service.MaxRetry);
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
    public void SetApiKey_StoresProtectedValue_Roundtrips()
    {
        using var service = Create();
        service.SetApiKey("sk-plaintext");

        // Plaintext không bao giờ nằm trong DB
        using var db = _db.CreateFactory().CreateDbContext();
        var stored = db.AppSettings.First(x => x.Key == "apiKey").ValueJson;
        Assert.DoesNotContain("sk-plaintext", stored);
        Assert.Equal("sk-plaintext", service.GetApiKey());
    }

    [Fact]
    public void Get_WhenKeyIsApiKey_ThrowsInvalidOperation()
    {
        using var service = Create();

        // Chặn đường generic truy cập plaintext key — phải đi qua GetApiKey/SetApiKey
        Assert.Throws<InvalidOperationException>(() => service.Get<string>(SettingsKeys.ApiKey, ""));
        Assert.Throws<InvalidOperationException>(() => service.Set(SettingsKeys.ApiKey, "x"));
    }
}
