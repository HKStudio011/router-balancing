using System.Text;

namespace RouterBalancing.Updater;

/// <summary>
/// Báo cáo chẩn đoán khi update timeout vì file appDir còn lock: danh sách
/// locked file (cap 20 — tránh log vô hạn) + process giữ lock qua Restart Manager.
/// Ghi ra console updater và append %TEMP%\RouterBalancing_Update\update.log
/// — log trong %TEMP%, không chứa secret, vô hại khi share kèm bug report.
/// </summary>
internal static class LockDiagnostics
{
    /// <summary>Số file tối đa liệt kê trong một báo cáo.</summary>
    internal const int MaxFilesInReport = 20;

    /// <summary>Đường dẫn log chẩn đoán (append, không truncate).</summary>
    internal static string ReportPath =>
        Path.Combine(Path.GetTempPath(), "RouterBalancing_Update", "update.log");

    /// <summary>
    /// Build báo cáo text từ danh sách file locked — mỗi file kèm process giữ
    /// lock (best-effort; không tra được thì chỉ liệt kê path).
    /// </summary>
    internal static string BuildReport(IReadOnlyList<string> lockedFiles)
    {
        if (lockedFiles.Count == 0)
        {
            return "No locked files.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Locked files preventing update ({lockedFiles.Count}):");
        foreach (var file in lockedFiles.Take(MaxFilesInReport))
        {
            sb.AppendLine($"  {file}");
            foreach (var locker in RestartManager.FindLockingProcesses(file))
            {
                sb.AppendLine($"    held by: {locker}");
            }
        }

        if (lockedFiles.Count > MaxFilesInReport)
        {
            sb.AppendLine($"  ... and {lockedFiles.Count - MaxFilesInReport} more");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Append báo cáo vào <see cref="ReportPath"/> — lỗi ghi log được nuốt
    /// (chẩn đoán không được làm hỏng luồng update).
    /// </summary>
    internal static void AppendToFile(string report)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ReportPath)!);
            File.AppendAllText(ReportPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}{report}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Log chỉ là chẩn đoán — không được block update vì không ghi được.
        }
    }
}
