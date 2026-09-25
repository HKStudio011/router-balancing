namespace RouterBalancing.Core.Domain;

/// <summary>Key-value setting dạng JSON — dễ thêm key khi upgrade app.</summary>
public class AppSetting
{
    /// <summary>Khóa chính là key, ví dụ <c>port</c>.</summary>
    public string Key { get; set; } = string.Empty;

    public string ValueJson { get; set; } = string.Empty;
}
