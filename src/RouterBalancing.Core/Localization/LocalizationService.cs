using System.Globalization;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Settings;

namespace RouterBalancing.Core.Localization;

/// <summary>
/// Dịch key i18n sang text theo ngôn ngữ cài đặt. Nguồn mặc định là
/// <see cref="Translations"/> (en/vi hardcode); ngôn ngữ bổ sung lấy từ file
/// JSON trong thư mục <c>translations/</c> cạnh executable (xem
/// <see cref="JsonTranslationLoader"/>). Key thiếu trong bản JSON → dùng bản en.
/// </summary>
public sealed class LocalizationService : IDisposable
{
    private readonly IAppSettingsService _settings;
    private readonly ILogService? _log;
    private readonly string _translationsDir;
    private IReadOnlyDictionary<string, string> _strings;
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _jsonLanguages;
    private bool _disposed;

    /// <summary>Ngôn ngữ hiệu lực: "auto" đã resolve theo UI culture của hệ thống.</summary>
    public string Language { get; private set; }

    /// <summary>Ngôn ngữ thêm từ file JSON — hiển thị trong dropdown Settings bằng tên native.</summary>
    public IReadOnlyList<LanguageInfo> AdditionalLanguages { get; private set; }

    public event Action? LanguageChanged;

    /// <param name="translationsDirectory">Thư mục chứa file bản dịch; mặc định <c>translations/</c> cạnh executable.</param>
    public LocalizationService(
        IAppSettingsService settings,
        ILogService? log = null,
        string? translationsDirectory = null)
    {
        _settings = settings;
        _log = log;
        _translationsDir = translationsDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "translations");
        _jsonLanguages = JsonTranslationLoader.Load(_translationsDir, log);
        AdditionalLanguages = BuildAdditionalLanguages(_jsonLanguages);
        Language = Resolve(settings.Language);
        _strings = BuildStrings(Language);
    }

    /// <summary>Tra cứu text; thiếu key trả chính key — UI hiển thị key thay vì crash.</summary>
    public string this[string key] =>
        _strings.TryGetValue(key, out var text) ? text : key;

    public void SetLanguage(string language)
    {
        _settings.Set(SettingsKeys.Language, language);
        Language = Resolve(language);
        _strings = BuildStrings(Language);
        LanguageChanged?.Invoke();
    }

    /// <summary>Re-resolve khi setting language đổi từ nguồn khác (Settings panel).</summary>
    public void Reload() => SetLanguage(_settings.Language);

    /// <summary>Quét lại translations/ — file thêm mới có hiệu lực không cần restart app.</summary>
    public void ReloadLanguages()
    {
        _jsonLanguages = JsonTranslationLoader.Load(_translationsDir, _log);
        AdditionalLanguages = BuildAdditionalLanguages(_jsonLanguages);
        Reload();
    }

    private static IReadOnlyList<LanguageInfo> BuildAdditionalLanguages(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> jsonLanguages) =>
        jsonLanguages.Keys
            .Where(code => code is not ("en" or "vi"))
            .OrderBy(code => code, StringComparer.Ordinal)
            .Select(code => new LanguageInfo(code, DisplayNameOf(code)))
            .ToList();

    private static string DisplayNameOf(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code).NativeName;
        }
        catch (CultureNotFoundException)
        {
            // Tên file vẫn hợp lệ pattern nhưng .NET không có culture — hiển thị code
            return code;
        }
    }

    private IReadOnlyDictionary<string, string> BuildStrings(string language)
    {
        // Nền = bản hardcode của ngôn ngữ (en/vi) hoặc English cho ngôn ngữ JSON;
        // file JSON overlay lên trên — key thiếu tự rơi về en.
        var baseStrings = language == "vi" ? Translations.Vietnamese : Translations.English;
        if (!_jsonLanguages.TryGetValue(language, out var overrides) || overrides.Count == 0)
            return baseStrings;

        var merged = new Dictionary<string, string>(baseStrings);
        foreach (var (key, text) in overrides)
            merged[key] = text;
        return merged;
    }

    private string Resolve(string setting)
    {
        if (setting is "vi" or "en" || _jsonLanguages.ContainsKey(setting))
            return setting;

        // "auto" và mọi giá trị lạ → theo hệ thống; khớp mã với ngôn ngữ đã nạp, không thì "en"
        var system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        if (system == "vi" || _jsonLanguages.ContainsKey(system))
            return system;
        return "en";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
