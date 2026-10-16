using System.Diagnostics;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

/// <summary>
/// Job object bảo đảm sidecar chết cùng app: KILL_ON_JOB_CLOSE — khi mọi handle
/// tới job đóng (app process thoát bất kỳ cách nào), OS kill process đã gán.
/// </summary>
public class WindowsProcessJobTests
{
    [Fact]
    public void Dispose_KillsAssignedProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var job = WindowsProcessJob.TryCreate();
        Assert.NotNull(job);

        var dummy = StartDummyProcess();
        try
        {
            Assert.False(dummy.HasExited);

            Assert.True(job!.Assign(dummy));

            // Dispose đóng handle job → KILL_ON_JOB_CLOSE giết process đã gán.
            job.Dispose();

            Assert.True(dummy.WaitForExit(5_000), "Assigned process should be killed when job handle closes.");
        }
        finally
        {
            KillQuietly(dummy);
            dummy.Dispose();
        }
    }

    [Fact]
    public void Dispose_WhenNoProcessAssigned_DoesNotThrow()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var job = WindowsProcessJob.TryCreate();
        Assert.NotNull(job);

        job!.Dispose();
        job.Dispose(); // dispose lần 2 là no-op, không throw
    }

    [Fact]
    public void Assign_AfterDispose_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var job = WindowsProcessJob.TryCreate();
        Assert.NotNull(job);
        job!.Dispose();

        var dummy = StartDummyProcess();
        try
        {
            Assert.False(job.Assign(dummy));
        }
        finally
        {
            KillQuietly(dummy);
            dummy.Dispose();
        }
    }

    private static Process StartDummyProcess()
    {
        // ping -t chạy vô hạn — dummy process chờ bị kill bởi job.
        var process = Process.Start(new ProcessStartInfo("ping", "-t 127.0.0.1")
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
            // Dummy đã thoát hoặc không truy cập được — test không phụ thuộc.
        }
    }
}
