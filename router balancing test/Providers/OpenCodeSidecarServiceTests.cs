using Microsoft.Extensions.Logging.Abstractions;
using RouterBalancing.Core.Providers;

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

