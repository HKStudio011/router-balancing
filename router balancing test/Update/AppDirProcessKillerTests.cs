using System.Diagnostics;
using RouterBalancing.Updater;

namespace router_balancing_test.Update;

/// <summary>
/// Diệt process mồ côi còn chạy từ appDir trước khi ghi đè update —
/// bug 2026-10-16: opencode-proxy.exe (Go sidecar không monitor parent) orphan
/// giữ lock file exe → WaitUntilWritable timeout → update abort.
/// </summary>
public class AppDirProcessKillerTests
{
    [Fact]
    public void IsProcessUnderDirectory_FileInsideDirectory_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rb-appdir");
        var file = Path.Combine(dir, "runtimes", "win-x64", "native", "opencode-proxy.exe");

        Assert.True(AppDirProcessKiller.IsProcessUnderDirectory(file, dir));
    }

    [Fact]
    public void IsProcessUnderDirectory_PrefixCollisionDirectory_ReturnsFalse()
    {
        // "rb-appdir-copy" bắt đầu bằng "rb-appdir" nhưng KHÔNG nằm trong đó —
        // so khớp thiếu dấu phân tách cuối sẽ kill nhầm process ngoài appDir.
        var dir = Path.Combine(Path.GetTempPath(), "rb-appdir");
        var file = Path.Combine(Path.GetTempPath(), "rb-appdir-copy", "opencode-proxy.exe");

        Assert.False(AppDirProcessKiller.IsProcessUnderDirectory(file, dir));
    }

    [Fact]
    public void IsProcessUnderDirectory_DirectoryWithTrailingSeparator_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rb-appdir") + Path.DirectorySeparatorChar;
        var file = Path.Combine(Path.GetTempPath(), "rb-appdir", "opencode-proxy.exe");

        Assert.True(AppDirProcessKiller.IsProcessUnderDirectory(file, dir));
    }

    [Fact]
    public void IsProcessUnderDirectory_CaseInsensitiveMatch_ReturnsTrue()
    {
        var dir = Path.Combine(Path.GetTempPath(), "RB-AppDir");
        var file = Path.Combine(Path.GetTempPath(), "rb-appdir", "proxy.exe");

        Assert.True(AppDirProcessKiller.IsProcessUnderDirectory(file, dir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsProcessUnderDirectory_NullOrEmptyFile_ReturnsFalse(string? file)
    {
        Assert.False(AppDirProcessKiller.IsProcessUnderDirectory(file, Path.GetTempPath()));
    }

    [Fact]
    public void KillProcessesUnderDirectory_KillsProcessRunningFromDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), $"rb-killer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var dummy = StartDummyFrom(dir);

            var killed = AppDirProcessKiller.KillProcessesUnderDirectory(dir, Environment.ProcessId);

            Assert.True(killed >= 1, "At least the dummy process should be killed.");
            Assert.True(dummy.WaitForExit(5_000), "Dummy running from appDir should be killed.");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Dummy có thể chưa release file exe kịp — dọn best-effort.
            }
        }
    }

    [Fact]
    public void KillProcessesUnderDirectory_SkipPid_IsNotKilled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), $"rb-killer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var dummy = StartDummyFrom(dir);
            try
            {
                var killed = AppDirProcessKiller.KillProcessesUnderDirectory(dir, dummy.Id);

                Assert.Equal(0, killed);
                Assert.False(dummy.HasExited, "Process matching skipPid must survive.");
            }
            finally
            {
                KillQuietly(dummy);
                dummy.Dispose();
            }
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort dọn temp.
            }
        }
    }

    private static Process StartDummyFrom(string dir)
    {
        // Copy PING.EXE vào dir mô phỏng binary nằm trong appDir — kill test
        // không đụng tới file hệ thống.
        var exe = Path.Combine(dir, "dummy.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "PING.EXE"), exe);

        var process = Process.Start(new ProcessStartInfo(exe, "-t 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        });
        Assert.NotNull(process);
        return process;
    }

    private static void KillQuietly(Process process)
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
            // Đã thoát hoặc không truy cập được — test không phụ thuộc.
        }
    }
}
