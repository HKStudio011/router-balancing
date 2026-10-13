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

    [Fact]
    public void SetLanguage_WhenLanguageFromJson_FileWinsAndMissingKeysFallBackToEnglish()
    {
        using var temp = new TempTranslationsDir();
        temp.Write("ja.json", """{ "nav.dashboard": "ダッシュボード" }""");
        using var service = new LocalizationService(_settings, translationsDirectory: temp.Dir);

        service.SetLanguage("ja");

        Assert.Equal("ja", service.Language);
        Assert.Equal("ダッシュボード", service["nav.dashboard"]);
        // Key không có trong file ja → nền English, không hiện key
        Assert.Equal(Translations.English["dashboard.action.stop"], service["dashboard.action.stop"]);
        Assert.Equal("some.unknown.key", service["some.unknown.key"]);
    }

    [Fact]
    public void AdditionalLanguages_WhenJsonLoaded_ContainsCodeWithNativeName()
    {
        using var temp = new TempTranslationsDir();
        temp.Write("ja.json", """{ "a": "b" }""");
        using var service = new LocalizationService(_settings, translationsDirectory: temp.Dir);

        var ja = Assert.Single(service.AdditionalLanguages, l => l.Code == "ja");
        Assert.Equal("日本語", ja.DisplayName);
        // en/vi là built-in — dropdown render bằng key riêng, không nằm trong AdditionalLanguages
        Assert.DoesNotContain(service.AdditionalLanguages, l => l.Code is "en" or "vi");
    }

    [Fact]
    public void Language_WhenAutoAndSystemCultureMatchesLoadedLanguage_UsesLoadedLanguage()
    {
        using var temp = new TempTranslationsDir();
        temp.Write("ja.json", """{ "a": "b" }""");
        _settings.Set(SettingsKeys.Language, "auto");
        var original = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("ja-JP");
            using var service = new LocalizationService(_settings, translationsDirectory: temp.Dir);

            Assert.Equal("ja", service.Language);
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    [Fact]
    public void English_WhenOverrideFilePresent_FileWinsOverBuiltIn()
    {
        using var temp = new TempTranslationsDir();
        temp.Write("en.json", """{ "app.title": "RB Override" }""");
        _settings.Set(SettingsKeys.Language, "en");
        using var service = new LocalizationService(_settings, translationsDirectory: temp.Dir);

        Assert.Equal("RB Override", service["app.title"]);
    }

    [Fact]
    public void ReloadLanguages_WhenFileAddedAfterStartup_PicksUpNewLanguage()
    {
        using var temp = new TempTranslationsDir();
        using var service = new LocalizationService(_settings, translationsDirectory: temp.Dir);
        Assert.DoesNotContain(service.AdditionalLanguages, l => l.Code == "ja");

        temp.Write("ja.json", """{ "nav.dashboard": "ダッシュボード" }""");
        service.ReloadLanguages();

        Assert.Contains(service.AdditionalLanguages, l => l.Code == "ja");
    }

    private sealed class TempTranslationsDir : IDisposable
    {
        public string Dir { get; } =
            Path.Combine(Path.GetTempPath(), "rb-svc-translations-" + Guid.NewGuid().ToString("N"));

        public void Write(string name, string content)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, name), content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Dir))
                Directory.Delete(Dir, recursive: true);
        }
    }
}
