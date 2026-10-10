namespace RouterBalancing.Core.Storage;

/// <summary>
/// Ghi lỗi khởi động ra file thường dưới app data — độc lập DB để dùng cả khi
/// chính DB vừa migrate hỏng (logger/ILogService đều ghi vào DB đó nên không dùng được).
/// </summary>
public static class StartupErrorReporter
{
    /// <summary>Tên file log lỗi khởi động: %AppData%/router-balancing/startup-error.log.</summary>
    public const string LogFileName = "startup-error.log";

    /// <summary>Đường dẫn chuẩn của file log lỗi khởi động.</summary>
    public static string DefaultLogPath =>
        Path.Combine(StoragePathProvider.GetDataDirectory(), LogFileName);

    /// <summary>
    /// Append exception (loại + message + full text) vào <paramref name="logFilePath"/>,
    /// tự tạo thư mục cha nếu chưa có. Best-effort: không ném exception nếu không ghi được —
    /// caller đã ở trong tình huống lỗi và phải luôn thoát được.
    /// </summary>
    /// <returns><paramref name="logFilePath"/> nếu ghi thành công; <see langword="null"/> nếu không ghi được.</returns>
    public static string? Report(Exception exception, string logFilePath)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentException.ThrowIfNullOrEmpty(logFilePath);

        var entry =
            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] UNHANDLED STARTUP EXCEPTION" + Environment.NewLine +
            $"{exception.GetType().FullName}: {exception.Message}" + Environment.NewLine +
            exception.ToString() + Environment.NewLine +
            new string('-', 80) + Environment.NewLine;
        return AppendEntry(entry, logFilePath);
    }

    /// <summary>
    /// Append một entry văn bản thường (sự kiện khởi động đáng chú ý, không phải exception)
    /// vào <paramref name="logFilePath"/> — cùng kênh log file độc lập DB với
    /// <see cref="Report(Exception, string)"/>, dùng khi cần ghi sự kiện trước khi migrate.
    /// Best-effort như <see cref="Report(Exception, string)"/>: không ném nếu không ghi được.
    /// </summary>
    /// <returns><paramref name="logFilePath"/> nếu ghi thành công; <see langword="null"/> nếu không ghi được.</returns>
    public static string? Append(string message, string logFilePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(message);
        ArgumentException.ThrowIfNullOrEmpty(logFilePath);

        var entry =
            $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] {message}" + Environment.NewLine +
            new string('-', 80) + Environment.NewLine;
        return AppendEntry(entry, logFilePath);
    }

    private static string? AppendEntry(string entry, string logFilePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(logFilePath, entry);
            return logFilePath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Ghi file cũng thất bại (thư mục app data không ghi được) — nuốt tại đây vì
            // không còn kênh log nào khác; caller nhận null và hiển thị dialog/exit.
            System.Diagnostics.Debug.WriteLine($"StartupErrorReporter không ghi được log: {ex}");
            return null;
        }
    }
}
