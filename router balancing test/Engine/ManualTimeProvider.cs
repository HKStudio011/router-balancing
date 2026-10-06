namespace router_balancing_test.Engine;

/// <summary>TimeProvider do test điều khiển — nhảy ngày UTC để kiểm chứng reset TodayTokens.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _utcNow = start;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    /// <summary>Đưa thời gian tới một mốc cụ thể (vd. sang ngày hôm sau).</summary>
    public void SetUtcNow(DateTimeOffset value) => _utcNow = value;

    /// <summary>Cộng thêm số ngày vào thời điểm hiện tại.</summary>
    public void AdvanceDays(int days) => _utcNow = _utcNow.AddDays(days);
}
