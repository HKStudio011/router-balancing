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

        using var dummy = new DummyAppDirProcess();

        // Process mới start có thể chưa lộ path cho scan MainModule dưới tải — poll tới khi
        // killer thấy (kill idempotent nên gọi lại vô hại).
        var killed = 0;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (killed < 1 && DateTime.UtcNow < deadline)
        {
            killed += AppDirProcessKiller.KillProcessesUnderDirectory(dummy.Dir, Environment.ProcessId);
            if (killed < 1)
            {
                Thread.Sleep(100);
            }
        }

        Assert.True(killed >= 1, "At least the dummy process should be killed.");
        Assert.True(dummy.WaitForExit(5_000), "Dummy running from appDir should be killed.");
    }

    [Fact]
    public void KillProcessesUnderDirectory_SkipPid_IsNotKilled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var dummy = new DummyAppDirProcess();

        var killed = AppDirProcessKiller.KillProcessesUnderDirectory(dummy.Dir, dummy.Id);

        Assert.Equal(0, killed);
        Assert.False(dummy.HasExited, "Process matching skipPid must survive.");
    }
}
