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
            var lockers = RestartManager.FindLockingProcesses(exe);

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
                }
            }
            catch
            {
                // Process đã thoát — không cần kill.
            }

            process.Dispose();
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Dummy chưa release file exe kịp — dọn best-effort.
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
