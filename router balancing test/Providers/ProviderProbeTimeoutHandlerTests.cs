using System.Diagnostics;
using System.Net;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ProviderProbeTimeoutHandlerTests : IDisposable
{
    private readonly TestDb _db = new();

    public ProviderProbeTimeoutHandlerTests()
    {
        // TestDb trống — service đọc AppSettings nên phải migrate trước
        DbInitializer.Initialize(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    /// <summary>Delay 30s — nếu timeout setting không cắt thì test fail (đợi hết delay).</summary>
    private sealed class StubInnerHandler : HttpMessageHandler
    {
        public int DelayMs { get; set; } = 30_000;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(DelayMs, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private (ProviderProbeTimeoutHandler Handler, AppSettingsService Settings, StubInnerHandler Inner)
        CreateHandler(int? probeTimeoutSec = null)
    {
        var settings = new AppSettingsService(_db.CreateFactory());
        if (probeTimeoutSec is { } sec)
            settings.Set(SettingsKeys.ProviderProbeTimeoutSec, sec);
        var inner = new StubInnerHandler();
        var handler = new ProviderProbeTimeoutHandler(settings) { InnerHandler = inner };
        return (handler, settings, inner);
    }

    // HttpMessageInvoker không wrap exception như HttpClient — message timeout của handler về nguyên vẹn
    private static async Task<HttpResponseMessage> SendAsync(
        HttpMessageHandler handler, CancellationToken ct = default)
    {
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        return await invoker.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://localhost/"), ct);
    }

    [Fact]
    public async Task SendAsync_WhenProbeTimeoutElapsed_ThrowsTaskCanceledWithTimeoutMessage()
    {
        var (handler, _, _) = CreateHandler(probeTimeoutSec: 1);

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => SendAsync(handler));
        sw.Stop();

        // Setting 1s nhưng inner delay 30s — phải cắt ở ~1s, không đợi hết delay
        Assert.Contains("exceeded 1s timeout", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"elapsed={sw.Elapsed}");
    }

    [Fact]
    public async Task SendAsync_WhenSettingChangedAfterHandlerCreated_UsesNewValueOnNextRequest()
    {
        var (handler, settings, _) = CreateHandler();           // default 60s lúc tạo handler
        settings.Set(SettingsKeys.ProviderProbeTimeoutSec, 1);  // đổi SAU — request kế phải đọc lại

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => SendAsync(handler));
        sw.Stop();

        // Chứng minh per-request read (không cache trong ctor) — spec §3.8
        Assert.Contains("exceeded 1s timeout", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"elapsed={sw.Elapsed}");
    }

    [Fact]
    public async Task SendAsync_WhenCallerCancels_PropagatesWithoutTimeoutWrap()
    {
        var (handler, _, _) = CreateHandler(); // default 60s — KHÔNG được tự timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SendAsync(handler, cts.Token));
        sw.Stop();

        // Cancel của caller phải đi qua nguyên vẹn — không đổi thành timeout message
        Assert.DoesNotContain("exceeded", ex.Message);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"elapsed={sw.Elapsed}");
    }

    [Fact]
    public async Task SendAsync_WhenCallCompletesInTime_ReturnsResponse()
    {
        var (handler, _, inner) = CreateHandler();
        inner.DelayMs = 0;

        var response = await SendAsync(handler);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
