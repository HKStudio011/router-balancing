using System.Diagnostics;

namespace RouterBalancing.Updater;

/// <summary>
/// Diệt process mồ côi còn chạy từ appDir sau khi app chính đã thoát.
/// Bug 2026-10-16: opencode-proxy.exe (Go sidecar, không monitor parent) sống
/// tiếp sau khi app Quit — orphan giữ lock file exe trong appDir →
/// <see cref="UpdateStager.WaitUntilWritable"/> không mở được file → timeout →
/// update abort. Job object (WindowsProcessJob) chặn sidecar do app mình sinh ra;
/// killer này là lưới an toàn cuối cho sidecar của phiên bản cũ (chưa có job)
/// hoặc process lạ khác chạy từ appDir.
/// </summary>
internal static class AppDirProcessKiller
{
    /// <summary>
    /// So khớp filePath có nằm TRONG directory không — thêm dấu phân tách cuối
    /// trước khi so để không nhầm prefix sỗ ("C:\App" không bắt "C:\AppCopy\x.exe").
    /// </summary>
    /// <param name="filePath">Đường dẫn executable cần kiểm (null/empty → false).</param>
    /// <param name="directory">Thư mục app — không bắt buộc có trailing separator.</param>
    /// <returns><see langword="true"/> khi file nằm trong directory (không phân biệt hoa thường).</returns>
    internal static bool IsProcessUnderDirectory(string? filePath, string directory)
    {
        if (string.IsNullOrEmpty(filePath))
        {
            return false;
        }

        try
        {
            var dir = Path.GetFullPath(directory);
            if (!dir.EndsWith(Path.DirectorySeparatorChar))
            {
                dir += Path.DirectorySeparatorChar;
            }

            return Path.GetFullPath(filePath).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Đường dẫn không hợp lệ từ MainModule — coi như không thuộc appDir.
            return false;
        }
    }

    /// <summary>
    /// Kill mọi process có executable nằm trong <paramref name="directory"/>,
    /// bỏ qua <paramref name="skipPid"/> (chính updater). Lỗi từng process
    /// (AccessDenied, vừa thoát) được nuốt — <see cref="UpdateStager.WaitUntilWritable"/>
    /// sẽ báo chính xác file còn lock nếu vẫn chưa ghi được.
    /// </summary>
    /// <param name="directory">Thư mục app cần dọn process mồ côi.</param>
    /// <param name="skipPid">PID được miễn kill (updater — kill chính mình sẽ hỏng update).</param>
    /// <param name="log">Nhận thông tin từng process bị kill (null = im lặng).</param>
    /// <returns>Số process đã kill.</returns>
    internal static int KillProcessesUnderDirectory(string directory, int skipPid, Action<string>? log = null)
    {
        var killed = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == skipPid)
                {
                    continue;
                }

                string? fileName;
                try
                {
                    // System process/quyền thấp không đọc được MainModule — bỏ qua.
                    fileName = process.MainModule?.FileName;
                }
                catch
                {
                    continue;
                }

                if (!IsProcessUnderDirectory(fileName, directory))
                {
                    continue;
                }

                try
                {
                    process.Kill(entireProcessTree: true);
                    killed++;
                    log?.Invoke($"Killed leftover process {process.Id} ({fileName})");
                }
                catch
                {
                    // AccessDenied hoặc vừa thoát — WaitUntilWritable xử lý nốt.
                }
            }
        }

        return killed;
    }
}
