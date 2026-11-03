using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

/// <summary>
/// Badge cooldown trong Live Trace cho cửa sổ quota cấp provider: window của gate keyed by
/// providerId, còn chip trong trace là ACCOUNT — helper này bắc cầu hai phía để UI dùng lại
/// đúng map + badge của cooldown proxy (CooldownTracker), không thêm markup/CSS mới.
/// </summary>
public class QuotaCooldownBadgeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static Provider Provider(long id, string name, params string[] accounts) => new()
    {
        Id = id,
        Name = name,
        Accounts = accounts.Select(a => new ProviderAccount { ProviderId = id, Name = a }).ToList(),
    };

    [Fact]
    public void Merge_WhenProviderBlocked_LabelsEveryAccountOfThatProvider()
    {
        var providers = new[]
        {
            Provider(7, "OpenCode Free", "key-a", "key-b"),
            Provider(8, "OpenAI", "sk-live"),
        };

        var merged = QuotaCooldownBadge.MergeByAccount(
            new Dictionary<long, DateTimeOffset> { [7] = T0.AddSeconds(120) }, providers,
            new Dictionary<string, DateTimeOffset>());

        Assert.Equal(2, merged.Count);
        Assert.Equal(T0.AddSeconds(120), merged["key-a"]);
        Assert.Equal(T0.AddSeconds(120), merged["key-b"]);
        Assert.False(merged.ContainsKey("sk-live"), "provider không bị gate không được gắn badge");
    }

    [Fact]
    public void Merge_WhenProxyCooldownLonger_KeepsProxyWindow()
    {
        // Không được rút ngắn countdown đang chạy — cùng logic "window đang chạy không bị
        // thu ngắn" của ProviderQuotaGate.
        IReadOnlyList<Provider> providers = [Provider(7, "OpenCode Free", "key-a")];
        var proxy = new Dictionary<string, DateTimeOffset> { ["key-a"] = T0.AddMinutes(10) };

        var merged = QuotaCooldownBadge.MergeByAccount(
            new Dictionary<long, DateTimeOffset> { [7] = T0.AddSeconds(120) }, providers, proxy);

        Assert.Equal(T0.AddMinutes(10), merged["key-a"]);
    }

    [Fact]
    public void Merge_WhenQuotaWindowLonger_QuotaWins()
    {
        IReadOnlyList<Provider> providers = [Provider(7, "OpenCode Free", "key-a")];
        var proxy = new Dictionary<string, DateTimeOffset> { ["key-a"] = T0.AddSeconds(20) };

        var merged = QuotaCooldownBadge.MergeByAccount(
            new Dictionary<long, DateTimeOffset> { [7] = T0.AddMinutes(5) }, providers, proxy);

        Assert.Equal(T0.AddMinutes(5), merged["key-a"]);
    }

    [Fact]
    public void Merge_WhenWindowForUnknownProvider_KeepsExistingMapOnly()
    {
        // Provider đã bị xóa, hoặc cache provider chưa nạp xong ở refresh đầu — window vẫn
        // còn trong gate nhưng không có chip nào để gắn; map proxy phải còn nguyên.
        IReadOnlyList<Provider> providers = [Provider(8, "OpenAI", "sk-live")];
        var proxy = new Dictionary<string, DateTimeOffset> { ["sk-live"] = T0.AddSeconds(30) };

        var merged = QuotaCooldownBadge.MergeByAccount(
            new Dictionary<long, DateTimeOffset> { [7] = T0.AddSeconds(120) }, providers, proxy);

        Assert.Equal(new[] { "sk-live" }, merged.Keys.ToArray());
    }

    [Fact]
    public void Merge_WhenNoWindows_ReturnsProxyMapUnchangedAndDoesNotMutateInputs()
    {
        IReadOnlyList<Provider> providers = [Provider(7, "OpenCode Free", "key-a")];
        var proxy = new Dictionary<string, DateTimeOffset> { ["key-a"] = T0.AddSeconds(30) };
        var windows = new Dictionary<long, DateTimeOffset>();

        var merged = QuotaCooldownBadge.MergeByAccount(windows, providers, proxy);

        Assert.Equal(T0.AddSeconds(30), merged["key-a"]);
        proxy.Remove("key-a");
        Assert.True(merged.ContainsKey("key-a"), "không được trả về cùng reference với map gốc");
    }
}
