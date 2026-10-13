namespace RouterBalancing.Core.Localization;

/// <summary>Ngôn ngữ do file JSON trong translations/ cung cấp — hiển thị trong dropdown Settings.</summary>
/// <param name="Code">Mã ISO ngôn ngữ lấy từ tên file (vd "ja", "zh-Hans").</param>
/// <param name="DisplayName">Tên native theo culture (vd "日本語"); fallback về code nếu code không hợp lệ culture.</param>
public sealed record LanguageInfo(string Code, string DisplayName);
