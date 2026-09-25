using System.Globalization;
using RouterBalancing.Core.Settings;

namespace RouterBalancing.Core.Localization;

/// <summary>Dịch key i18n sang text theo ngôn ngữ cài đặt (xem <see cref="Translations"/>).</summary>
public sealed class LocalizationService : IDisposable
{
    private readonly IAppSettingsService _settings;
    private IReadOnlyDictionary<string, string> _strings;
    private bool _disposed;

    /// <summary>Ngôn ngữ hiệu lực: "auto" đã resolve theo UI culture của hệ thống.</summary>
    public string Language { get; private set; }

    public event Action? LanguageChanged;

    public LocalizationService(IAppSettingsService settings)
    {
        _settings = settings;
        Language = Resolve(settings.Language);
        _strings = Translations.For(Language);
    }

    /// <summary>Tra cứu text; thiếu key trả chính key — UI hiển thị key thay vì crash.</summary>
    public string this[string key] =>
        _strings.TryGetValue(key, out var text) ? text : key;

    public void SetLanguage(string language)
    {
        _settings.Set(SettingsKeys.Language, language);
        Language = Resolve(language);
        _strings = Translations.For(Language);
        LanguageChanged?.Invoke();
    }

    /// <summary>Re-resolve khi setting language đổi từ nguồn khác (Settings panel).</summary>
    public void Reload() => SetLanguage(_settings.Language);

    private static string Resolve(string setting) => setting switch
    {
        "vi" => "vi",
        "en" => "en",
        // "auto" và mọi giá trị lạ → theo hệ thống; không phải "vi" thì dùng English
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "vi" ? "vi" : "en",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
