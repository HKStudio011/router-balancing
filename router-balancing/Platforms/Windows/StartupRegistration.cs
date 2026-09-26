using Microsoft.Win32;
using RouterBalancing.Core.Platform;

namespace router_balancing.Platforms.Windows;

/// <summary>HKCU Run — không cần quyền admin; giá trị là đường dẫn exe bọc trong nháy kép.</summary>
internal sealed class StartupRegistration : IStartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "router-balancing";

    /// <summary>
    /// "Đã bật" = giá trị Run tồn tại VÀ trỏ đúng exe đang chạy (so không phân biệt hoa/thường,
    /// bỏ nháy kép). Setting là nguồn sự thật: path cũ sau khi exe bị di chuyển phải bị coi là
    /// chưa đăng ký, để sync ghi lại giá trị đúng thay vì tin rằng key đã ổn.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var stored = key?.GetValue(ValueName) as string;
            var current = Environment.ProcessPath;
            if (stored is null || current is null) return false;
            return string.Equals(Unquote(stored.Trim()), current, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Không xác định được đường dẫn exe để đăng ký khởi động.");
            key.SetValue(ValueName, $"\"{exe}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Bỏ nháy kép bọc ngoài — giá trị Run có thể do người dùng ghi tay không theo format của app.</summary>
    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}
