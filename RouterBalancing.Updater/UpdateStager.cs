using System.IO.Compression;

namespace RouterBalancing.Updater;

/// <summary>
/// Các bước update an toàn: trích zip sang staging, chờ file đích hết lock,
/// chỉ sau đó mới ghi vào thư mục app — extract trực tiếp vào appDir dở dang
/// sẽ để lại folder lẫn lộn runtime self-contained + framework-dependent (gây
/// lỗi host ".NET missing" vì hostfxr self-contained cạnh exe làm apphost
/// chọn sai chỗ phân giải framework).
/// </summary>
internal static class UpdateStager
{
    /// <summary>Thư mục staging chung dưới %TEMP% — dọn sạch bản cũ trước khi trích.</summary>
    internal static string ExtractToStaging(string zipPath)
    {
        var staging = Path.Combine(Path.GetTempPath(), "RouterBalancing_Update", "staging");
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);

        ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
        return staging;
    }

    /// <summary>
    /// Chờ mọi file trong staging đã tồn tại ở <paramref name="appDir"/> mở ghi
    /// exclusive được (hết lock) trong <paramref name="timeout"/>.
    /// </summary>
    /// <returns><see langword="true"/> khi tất cả file đích ghi được; false nếu hết timeout còn lock.</returns>
    /// <exception cref="UnauthorizedAccessException">File bị từ chối quyền (không phải lock) — không retry được.</exception>
    internal static bool WaitUntilWritable(string stagingDir, string appDir, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            var allWritable = true;
            foreach (var src in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
            {
                var dst = Path.Combine(appDir, Path.GetRelativePath(stagingDir, src));
                if (!File.Exists(dst)) continue;

                try
                {
                    // FileShare.None: file đang được process khác load/mở sẽ ném IOException
                    using var _ = File.Open(dst, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException)
                {
                    allWritable = false;
                    break;
                }
            }

            if (allWritable) return true;
            if (DateTime.UtcNow >= deadline) return false;

            Thread.Sleep(500);
        }
    }

    /// <summary>Copy toàn bộ file staging (kể cả thư mục con) vào appDir, ghi đè file trùng.</summary>
    internal static void CopyDirectory(string stagingDir, string appDir)
    {
        foreach (var dir in Directory.EnumerateDirectories(stagingDir, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(appDir, Path.GetRelativePath(stagingDir, dir)));
        }

        foreach (var src in Directory.EnumerateFiles(stagingDir, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(appDir, Path.GetRelativePath(stagingDir, src));
            File.Copy(src, dst, overwrite: true);
        }
    }
}
