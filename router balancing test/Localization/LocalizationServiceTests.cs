using System.Globalization;
using RouterBalancing.Core.Localization;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Localization;

public class LocalizationServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;

    public LocalizationServiceTests()
    {
        // TestDb chỉ tạo file trống — phải migrate schema trước khi service đọc AppSettings
        DbInitializer.Initialize(_db.CreateFactory());
        _settings = new AppSettingsService(_db.CreateFactory());
    }

    public void Dispose()
    {
        _settings.Dispose();
        _db.Dispose();
    }

    [Fact]
    public void Language_WhenSettingVi_ReturnsVi()
    {
        _settings.Set(SettingsKeys.Language, "vi");
        using var service = new LocalizationService(_settings);

        Assert.Equal("vi", service.Language);
    }

    [Fact]
    public void Language_WhenSettingAuto_ResolvesSystemCulture()
    {
        _settings.Set(SettingsKeys.Language, "auto");
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            using var service = new LocalizationService(_settings);
            Assert.Equal("en", service.Language);

            CultureInfo.CurrentUICulture = new CultureInfo("vi-VN");
            using var serviceVi = new LocalizationService(_settings);
            Assert.Equal("vi", serviceVi.Language);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void Indexer_WhenKeyExistsInLanguage_ReturnsTranslation()
    {
        _settings.Set(SettingsKeys.Language, "vi");
        using var service = new LocalizationService(_settings);

        Assert.Equal("Bảng điều khiển", service["nav.dashboard"]);
    }

    [Fact]
    public void Indexer_WhenKeyMissing_ReturnsKeyItself()
    {
        using var service = new LocalizationService(_settings);

        Assert.Equal("some.unknown.key", service["some.unknown.key"]);
    }

    [Fact]
    public void SetLanguage_FiresLanguageChanged_AndPersists()
    {
        using var service = new LocalizationService(_settings);
        var raised = 0;
        service.LanguageChanged += () => raised++;

        service.SetLanguage("vi");

        Assert.Equal(1, raised);
        Assert.Equal("vi", service.Language);
        Assert.Equal("vi", _settings.Language);
    }
}
