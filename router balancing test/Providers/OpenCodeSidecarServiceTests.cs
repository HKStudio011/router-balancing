using Microsoft.Extensions.Logging.Abstractions;
using RouterBalancing.Core.Providers;
using System.Diagnostics;

namespace router_balancing_test.Providers;

/// <summary>
/// Quản lý process Go sidecar: start khi binary có, fail an toàn khi missing,
/// restart khi process chết, kill khi dispose (không orphan).
/// </summary>
public class OpenCodeSidecarServiceTests
{
    [Fact]
    public async Task EnsureStartedAsync_WhenBinaryMissing_ReturnsFalse()
    {
        using var service = new OpenCodeSidecarService("/nonexistent/path/opencode-proxy.exe", NullLogger<OpenCodeSidecarService>.Instance);

        var result = await service.EnsureStartedAsync();

        Assert.False(result);
        Assert.Null(service.BaseUrl);
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenProcessDies_RestartsOnNextCall()
    {
        var binaryPath = FindSidecarBinary();
        if (binaryPath is null)
        {
            // Binary chưa build trong môi trường test — skip, không fail red herring.
            return;
        }

        using var service = new OpenCodeSidecarService(binaryPath, NullLogger<OpenCodeSidecarService>.Instance);
        Assert.True(await service.EnsureStartedAsync());
        var firstPort = service.BaseUrl;

        service.KillForTest();

        Assert.True(await service.EnsureStartedAsync());
        Assert.NotNull(service.BaseUrl);
        // Port ngẫu nhiên mỗi lần bind — chỉ assert service sống lại.
        _ = firstPort;
    }

    [Fact]
    public async Task Dispose_KillsSidecarProcess()
    {
        var binaryPath = FindSidecarBinary();
        if (binaryPath is null)
        {
            return;
        }

        var service = new OpenCodeSidecarService(binaryPath, NullLogger<OpenCodeSidecarService>.Instance);
        Assert.True(await service.EnsureStartedAsync());
        var baseUrl = service.BaseUrl;
        Assert.NotNull(baseUrl);

        service.Dispose();

        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"{baseUrl}/health"));
    }

    [Fact]
    public async Task EnsureStartedAsync_AssignsSidecarToKillOnCloseJob()
    {
        var binaryPath = FindSidecarBinary();
        if (binaryPath is null || !OperatingSystem.IsWindows())
        {
            return;
        }

        // Job được dispose TRƯỚC service — mô phỏng handle job đóng khi app exit:
        // OS phải kill sidecar (KILL_ON_JOB_CLOSE) mà service không Dispose().
        var job = WindowsProcessJob.TryCreate();
        Assert.NotNull(job);

        using var service = new OpenCodeSidecarService(binaryPath, NullLogger<OpenCodeSidecarService>.Instance, job);
        Assert.True(await service.EnsureStartedAsync());
        Assert.NotNull(service.BaseUrl);

        job!.Dispose();

        // DrainAndWatchAsync thấy sidecar chết → clear BaseUrl.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (service.BaseUrl is not null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }
        Assert.Null(service.BaseUrl);
    }

    /// <summary>
    /// Bug 26/10: nhiều request opencode.ai đồng thời gọi <c>EnsureStartedAsync</c> trước khi
    /// process đầu kịp gán <c>_process</c> (TOCTOU check-ngoài-lock) → mỗi call start một
    /// sidecar; process ghi đè sau cùng là của service, các process trước thành rác.
    /// Đếm process theo tên file unique để không lẫn sidecar của app đang chạy.
    /// </summary>
    [Fact]
    public async Task EnsureStartedAsync_WhenCalledConcurrently_StartsSingleSidecarProcess()
    {
        var binaryPath = FindSidecarBinary();
        if (binaryPath is null || !OperatingSystem.IsWindows())
        {
            return;
        }

        var processName = $"opencode-proxy-race-{Guid.NewGuid():N}";
        var tempBinary = Path.Combine(Path.GetTempPath(), processName + ".exe");
        File.Copy(binaryPath, tempBinary, overwrite: true);
        try
        {
            using var service = new OpenCodeSidecarService(tempBinary, NullLogger<OpenCodeSidecarService>.Instance);

            // Gate chung để 12 call ùa vào EnsureStartedAsync cùng thời điểm
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = Enumerable.Range(0, 12)
                .Select(_ => Task.Run(async () =>
                {
                    await start.Task;
                    return await service.EnsureStartedAsync();
                }))
                .ToArray();
            start.SetResult();

            var results = await Task.WhenAll(calls);

            Assert.All(results, Assert.True);
            Assert.NotNull(service.BaseUrl);
            var processes = Process.GetProcessesByName(processName);
            try
            {
                Assert.Single(processes);
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        finally
        {
            // Dispose service đã kill mọi process thuộc job object — đợi OS nhả file rồi xoá binary tạm.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && Process.GetProcessesByName(processName).Length > 0)
            {
                await Task.Delay(100);
            }
            try
            {
                File.Delete(tempBinary);
            }
            catch (IOException)
            {
                // Best-effort: file tạm trong %TEMP% — không làm fail test.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? FindSidecarBinary()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "opencode-proxy.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "runtimes", "win-x64", "native", "opencode-proxy.exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

