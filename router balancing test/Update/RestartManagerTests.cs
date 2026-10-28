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
        using var dummy = new DummyAppDirProcess();

        // PING.EXE mới start: OS có độ trễ cập nhật trạng thái "đang chạy từ path" —
        // poll có điều kiện thay vì assert ngay (flake dưới tải, run local 28/10).
        var deadline = DateTime.UtcNow.AddSeconds(15);
        IReadOnlyList<string> lockers = [];
        while (DateTime.UtcNow < deadline)
        {
            lockers = RestartManager.FindLockingProcesses(dummy.ExePath);
            if (lockers.Any(l => l.Contains(dummy.Id.ToString())))
            {
                break;
            }
            Thread.Sleep(100);
        }

        Assert.NotEmpty(lockers);
        Assert.Contains(lockers, l => l.Contains(dummy.Id.ToString()));
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
