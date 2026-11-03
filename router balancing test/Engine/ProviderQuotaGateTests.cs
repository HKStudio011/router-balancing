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

        gate.Report(7, FreeLimit with { RetryAfter = TimeSpan.FromSeconds(30) });

        time.SetUtcNow(T0.AddSeconds(29));
        Assert.True(gate.TryGetBlock(7, out _), "còn 1s trong window phải còn chặn");
        time.SetUtcNow(T0.AddSeconds(30));
        Assert.False(gate.TryGetBlock(7, out _), "hết window phải mở lại");
    }

    [Fact]
    public void Report_WhenNoRetryAfter_UsesDefaultWindow()
    {
        var time = new ManualTimeProvider(T0);
        var gate = new ProviderQuotaGate(time);

        gate.Report(7, FreeLimit);

        time.SetUtcNow(T0.Add(ProviderQuotaGate.DefaultBlock).AddSeconds(-1));
        Assert.True(gate.TryGetBlock(7, out _));
        time.SetUtcNow(T0.Add(ProviderQuotaGate.DefaultBlock).AddSeconds(1));
        Assert.False(gate.TryGetBlock(7, out _));
    }

    [Fact]
    public void TryGetBlock_WhenOnlyAnotherProviderReported_ReturnsFalse()
    {
        var gate = new ProviderQuotaGate(new ManualTimeProvider(T0));

        gate.Report(7, FreeLimit);

        Assert.False(gate.TryGetBlock(8, out _), "gate chỉ chặn provider vừa báo — provider khác vẫn phải thử");
    }

    [Fact]
    public void TryGetBlock_WhileBlocked_RetryAfterCountsDownRemainingSeconds()
    {
        var time = new ManualTimeProvider(T0);
        var gate = new ProviderQuotaGate(time);
        gate.Report(7, FreeLimit);

        Assert.True(gate.TryGetBlock(7, out var fresh));
        Assert.Equal(429, fresh!.Status);
        var first = fresh.RetryAfter!.Value;
        Assert.True(first > TimeSpan.FromSeconds(118) && first <= ProviderQuotaGate.DefaultBlock,
            $"Retry-After ban đầu phải sát cửa sổ mặc định, nhận {first}");

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
        gate.Report(7, FreeLimit);

        Assert.True(gate.TryGetBlock(7, out var block));
        Assert.Equal("application/json", block!.ContentType);
        Assert.Equal(FreeLimit.Body, block.Body);
    }
}
