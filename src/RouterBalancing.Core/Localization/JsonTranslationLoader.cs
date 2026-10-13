using System.Text.Json;
using System.Text.RegularExpressions;
using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Localization;

/// <summary>
/// Nạp bản dịch bổ sung từ thư mục <c>translations/</c> cạnh executable: mỗi file
/// JSON phẳng <c>{ "key": "text" }</c> với key giống <see cref="Translations"/>,
/// tên file = mã ISO ngôn ngữ (vd <c>ja.json</c>). File sai tên hoặc JSON hỏng
/// được bỏ qua + ghi log — một file lỗi không được làm hỏng các file còn lại.
/// </summary>
public static class JsonTranslationLoader
{
    /// <summary>Tên file hợp lệ: mã 2 chữ thường, tùy chọn vùng (vd "ja", "zh-Hans").</summary>
    private static readonly Regex FileNamePattern =
        new("^[a-z]{2}(-[A-Za-z]{2,4})?$", RegexOptions.Compiled);

    /// <summary>Quét thư mục; trả mã ngôn ngữ → bảng key/text. Thư mục không tồn tại → rỗng.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Load(
        string directory, ILogService? log = null)
    {
        var languages = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

        if (!Directory.Exists(directory))
            return languages;

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var fileName = Path.GetFileName(path);
            var code = Path.GetFileNameWithoutExtension(path);
            if (!FileNamePattern.IsMatch(code))
            {
                log?.Warn($"translations: bỏ qua file '{fileName}' — tên phải là mã ngôn ngữ (vd ja.json, zh-Hans.json).");
                continue;
            }

            try
            {
                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(path));
                if (entries is null)
                {
                    log?.Warn($"translations: file '{fileName}' không có nội dung — bỏ qua.");
                    continue;
                }

                languages[code] = entries;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                log?.Error($"translations: không đọc được file '{fileName}' — bỏ qua.", ex);
            }
        }

        return languages;
    }
}
