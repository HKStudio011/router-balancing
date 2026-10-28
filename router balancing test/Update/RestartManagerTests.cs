using System.Diagnostics;
using RouterBalancing.Updater;

namespace router_balancing_test.Update;

/// <summary>
/// Restart Manager: xác định process giữ lock file — chẩn đoán timeout update
/// (bug 2026-10-16 cần biết "ai" giữ lock, không chỉ "file nào").
/// </summary>
public class RestartManagerTests
{
    [Fact]
    public void FindLockingProcesses_ProcessExecutingFromPath_ReturnsThatProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // RM bị hạn chế trong testhost (job object) — fallback scan MainModule
        // phải bắt được process thực thi từ chính file (case sidecar thật).
        var dir = Path.Combine(Path.GetTempPath(), $"rb-rm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "dummy.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE"), exe);

        var process = Process.Start(new ProcessStartInfo(exe, "-t 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);
        try
        {
            // PING.EXE mới start: OS có độ trễ cập nhật trạng thái "đang chạy từ path" —
            // hỏi ngay lập tức flake dưới tải (run local 28/10). Poll có điều kiện thay vì assert ngay.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            IReadOnlyList<string> lockers = [];
            while (DateTime.UtcNow < deadline)
            {
                lockers = RestartManager.FindLockingProcesses(exe);
                if (lockers.Any(l => l.Contains(process.Id.ToString())))
                {
                    break;
                }
                Thread.Sleep(100);
            }

            Assert.NotEmpty(lockers);
            Assert.Contains(lockers, l => l.Contains(process.Id.ToString()));
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    // Đợi OS thu hồi handle exe trước khi xoá thư mục — Kill trả về trước
                    // khi handle được nhả (CI 40711439161: Delete ném UnauthorizedAccessException).
                    process.WaitForExit(5_000);
                }
            }
            catch
            {
                // Process đã thoát — không cần kill.
            }

            process.Dispose();
            // Dọn best-effort: exe có thể còn bị giữ vài trăm ms sau khi process chết —
            // Windows ném cả IOException lẫn UnauthorizedAccessException; retry ngắn rồi bỏ qua.
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
            }
        }
    }

    [Fact]
    public void FindLockingProcesses_FileUnlocked_ReturnsEmpty()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(Path.GetTempPath(), $"rb-rm-{Guid.NewGuid():N}.lock");
        File.WriteAllText(path, "x");
        try
        {
            var lockers = RestartManager.FindLockingProcesses(path);

            Assert.Empty(lockers);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
