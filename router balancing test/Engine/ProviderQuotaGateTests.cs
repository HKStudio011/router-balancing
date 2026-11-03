using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

/// <summary>
/// Gate quota cấp provider: upstream trả 429 mà cap KHÔNG theo key/session/IP
/// (OpenCode Zen free tier — probe 2026-11-03: đổi key, IP, session, model đều vẫn 429)
/// thì window ngắn được mở cho CẢ provider để request sau không gọi upstream vô ích.
/// </summary>
public class ProviderQuotaGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Fallback mặc định của setting <c>providerQuotaBlockSec</c> — test dùng lại để không lệch số.</summary>
    private static readonly TimeSpan Configured = TimeSpan.FromSeconds(120);

    /// <summary>Shape lỗi thật của OpenCode Zen free tier (status 429, không Retry-After).</summary>
    private static readonly RetryState.Failure FreeLimit = new(
        429, "application/json",
        """{"type":"error","error":{"type":"FreeUsageLimitError","message":"Free usage limit reached."}}"""u8.ToArray(),
        null);

    [Fact]
    public void Report_WhenUpstreamSendsRetryAfter_BlocksForExactlyThatDelay()
    {
        var time = new ManualTimeProvider(T0);
        var gate = new ProviderQuotaGate(time);

        gate.Report(7, FreeLimit with { RetryAfter = TimeSpan.FromSeconds(30) }, Configured);

        time.SetUtcNow(T0.AddSeconds(29));
        Assert.True(gate.TryGetBlock(7, out _), "còn 1s trong window phải còn chặn");
        time.SetUtcNow(T0.AddSeconds(30));
        Assert.False(gate.TryGetBlock(7, out _), "hết window phải mở lại");
    }

    [Fact]
    public void Report_WhenNoRetryAfter_UsesFallbackFromSettings()
    {
        var time = new ManualTimeProvider(T0);
        var gate = new ProviderQuotaGate(time);

        gate.Report(7, FreeLimit, TimeSpan.FromSeconds(90));

        time.SetUtcNow(T0.AddSeconds(89));
        Assert.True(gate.TryGetBlock(7, out _));
        time.SetUtcNow(T0.AddSeconds(91));
        Assert.False(gate.TryGetBlock(7, out _), "window phải theo TTL caller đưa, không phải hằng số cứng");
    }

    [Fact]
    public void Report_WhenFallbackZero_DoesNotOpenWindow()
    {
        // providerQuotaBlockSec = 0 là tắt tính năng (user muốn mọi request đi upstream thật)
        var gate = new ProviderQuotaGate(new ManualTimeProvider(T0));

        gate.Report(7, FreeLimit, TimeSpan.Zero);

        Assert.False(gate.TryGetBlock(7, out _));
    }

    [Fact]
    public void Report_WhenWindowActive_DoesNotShortenItWithSmallerTtl()
    {
        var time = new ManualTimeProvider(T0);
        var gate = new ProviderQuotaGate(time);
        gate.Report(7, FreeLimit, TimeSpan.FromSeconds(120));

        // Attempt thứ hai (Retry-After ngắn hơn) không được mở lại window ngắn hơn
        gate.Report(7, FreeLimit with { RetryAfter = TimeSpan.FromSeconds(10) }, TimeSpan.FromSeconds(10));

        time.SetUtcNow(T0.AddSeconds(60));
        Assert.True(gate.TryGetBlock(7, out _), "window 120s đang chạy phải còn nguyên sau attempt TTL 10s");
    }

    [Fact]
    public void TryGetBlock_WhenOnlyAnotherProviderReported_ReturnsFalse()
    {
        var gate = new ProviderQuotaGate(new ManualTimeProvider(T0));

        gate.Report(7, FreeLimit, Configured);

        Assert.False(gate.TryGetBlock(8, out _), "gate chỉ chặn provider vừa báo — provider khác vẫn phải thử");
    }

    [Fact]
    public void TryGetBlock_WhileBlocked_RetryAfterCountsDownRemainingSeconds()
    {
        var time = new ManualTimeProvider(T0);
        var gate = new ProviderQuotaGate(time);
        gate.Report(7, FreeLimit, Configured);

        Assert.True(gate.TryGetBlock(7, out var fresh));
        Assert.Equal(429, fresh!.Status);
        var first = fresh.RetryAfter!.Value;
        Assert.True(first > TimeSpan.FromSeconds(118) && first <= Configured,
            $"Retry-After ban đầu phải sát cửa sổ đã cấu hình, nhận {first}");

        time.SetUtcNow(T0.AddSeconds(100));
        Assert.True(gate.TryGetBlock(7, out var later));
        var countdown = later!.RetryAfter!.Value;
        Assert.True(countdown > TimeSpan.FromSeconds(18) && countdown <= TimeSpan.FromSeconds(20),
            $"Retry-After phải đếm ngược còn ~20s, nhận {countdown}");
    }

    [Fact]
    public void TryGetBlock_ReturnsUpstreamBodySoLaterRequestCanReplayIt()
    {
        var gate = new ProviderQuotaGate(new ManualTimeProvider(T0));
        gate.Report(7, FreeLimit, Configured);

        Assert.True(gate.TryGetBlock(7, out var block));
        Assert.Equal("application/json", block!.ContentType);
        Assert.Equal(FreeLimit.Body, block.Body);
    }

    [Fact]
    public void Clear_WhenProviderConfigChanged_OpensWindowImmediately()
    {
        // Thêm/sửa tài khoản hoặc proxy cho provider = bằng chứng user muốn thử lại
        // (key mới, IP mới) → không bắt họ chờ hết window.
        var gate = new ProviderQuotaGate(new ManualTimeProvider(T0));
        gate.Report(7, FreeLimit, Configured);

        gate.Clear(7);

        Assert.False(gate.TryGetBlock(7, out _));
    }

    [Fact]
    public void Clear_LeavesOtherProvidersAlone()
    {
        var gate = new ProviderQuotaGate(new ManualTimeProvider(T0));
        gate.Report(7, FreeLimit, Configured);
        gate.Report(8, FreeLimit, Configured);

        gate.Clear(7);

        Assert.True(gate.TryGetBlock(8, out _), "sửa provider 7 không được xóa window của provider 8");
    }

    [Fact]
    public void BlockedCount_CountsOnlyWindowsStillOpen()
    {
        // Live Trace cần một phép thử rẻ để phát hiện window mở/đóng ngay (badge) thay vì
        // dựng snapshot Dictionary mỗi tick 100ms của vòng render.
        var time = new ManualTimeProvider(T0);
        var gate = new ProviderQuotaGate(time);
        Assert.Equal(0, gate.BlockedCount());

        gate.Report(7, FreeLimit, TimeSpan.FromSeconds(30));
        gate.Report(8, FreeLimit, TimeSpan.FromSeconds(300));
        Assert.Equal(2, gate.BlockedCount());

        time.SetUtcNow(T0.AddSeconds(31));
        // Hết window phải được tính là đã đóng để UI tắt badge
        Assert.Equal(1, gate.BlockedCount());

        gate.Clear(8);
        Assert.Equal(0, gate.BlockedCount());
    }

    [Fact]
    public void SnapshotBlocked_ReturnsOnlyProvidersStillInsideWindow()
    {
        var time = new ManualTimeProvider(T0);
        var gate = new ProviderQuotaGate(time);
        gate.Report(7, FreeLimit, TimeSpan.FromSeconds(30));
        gate.Report(8, FreeLimit, TimeSpan.FromSeconds(300));

        var snapshot = gate.SnapshotBlocked();
        Assert.Equal(2, snapshot.Count);
        Assert.True(snapshot.ContainsKey(7) && snapshot.ContainsKey(8));

        time.SetUtcNow(T0.AddSeconds(31));
        snapshot = gate.SnapshotBlocked();
        // Entry hết hạn phải biến mất khỏi snapshot để UI tắt badge mà không cần đường riêng
        Assert.Equal(new long[] { 8 }, snapshot.Keys.ToArray());
    }
}
