namespace RouterBalancing.Core.Tools;

/// <summary>
/// Đường dẫn file config opencode — mặc định và cách expand <c>~</c> (spec §9.8).
/// Tách khỏi UI/settings service để writer và Settings dùng đúng một định nghĩa.
/// </summary>
public static class OpenCodeConfigPaths
{
    /// <summary>Đường dẫn mặc định <c>~/.config/opencode/opencode.json</c> (Windows: dưới %USERPROFILE%).</summary>
    public static string DefaultPath => Path.Combine(UserProfile, ".config", "opencode", "opencode.json");

    /// <summary>
    /// Expand đường dẫn người dùng nhập: rỗng/whitespace → <see cref="DefaultPath"/>;
    /// <c>~</c> / <c>~/</c> / <c>~\</c> → home; đường dẫn khác giữ nguyên.
    /// </summary>
    public static string Expand(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return DefaultPath;

        var trimmed = path.Trim();
        if (trimmed == "~") return UserProfile;
        if (trimmed.StartsWith("~/", StringComparison.Ordinal)
            || trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            // Chuẩn hoá separator để path hiển thị/lưu nhất quán trên Windows.
            var relative = trimmed[2..]
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);
            return Path.Combine(UserProfile, relative);
        }
        return trimmed;
    }

    private static string UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
