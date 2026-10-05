using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

public class ComboResolverTests : IDisposable
{
    private readonly TestDb _db = new();

    public ComboResolverTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    private long SeedProvider(string name, ProviderType type = ProviderType.OpenAI,
        bool enabled = true, params string[] modelIds)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            Type = type,
            BaseUrl = "https://api.openai.com",
            Enabled = enabled,
        };
        foreach (var id in modelIds)
            provider.Models.Add(new Model { ModelId = id, Enabled = true });
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider.Id;
    }

    private long AddModel(long providerId, string modelId, bool enabled = true)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var model = new Model { ModelId = modelId, Enabled = enabled };
        db.Providers.Find(providerId)!.Models.Add(model);
        db.SaveChanges();
        return model.Id;
    }

    private long SeedCombo(string name, ComboMode mode,
        params (int Position, long? TargetModelId, long? TargetComboId)[] items)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var combo = new Combo { Name = name, Mode = mode };
        foreach (var (position, targetModel, targetCombo) in items)
            combo.Items.Add(new ComboItem
            {
                Position = position,
                TargetModelId = targetModel,
                TargetComboId = targetCombo,
            });
        db.Combos.Add(combo);
        db.SaveChanges();
        return combo.Id;
    }

    private void AddComboItem(long comboId, int position, long? targetModelId, long? targetComboId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        db.ComboItems.Add(new ComboItem
        {
            ComboId = comboId,
            Position = position,
            TargetModelId = targetModelId,
            TargetComboId = targetComboId,
        });
        db.SaveChanges();
    }

    private long ModelKey(string modelId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        return db.Models.First(m => m.ModelId == modelId).Id;
    }

    private void SetIdentifier(long providerId, string identifier)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        db.Providers.Find(providerId)!.Identifier = identifier;
        db.SaveChanges();
    }

    /// <summary>Tạo OutboundProxy trần — port riêng mỗi test (unique index scheme+host+port).</summary>
    private long SeedProxy(int port)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var proxy = new OutboundProxy { Scheme = "http", Host = "127.0.0.1", Port = port };
        db.OutboundProxies.Add(proxy);
        db.SaveChanges();
        return proxy.Id;
    }

    /// <summary>Gán thẳng hàng junction Provider↔Proxy (ProxyService không nằm trong scope test này).</summary>
    private void AssignProviderProxy(long providerId, long proxyId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        db.Set<ProviderProxy>().Add(new ProviderProxy { ProviderId = providerId, ProxyId = proxyId });
        db.SaveChanges();
    }

    /// <summary>Tạo account + hàng junction Account↔Proxy — trả về account Id.</summary>
    private long AddAccountWithProxy(long providerId, long proxyId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var account = new ProviderAccount
        {
            ProviderId = providerId,
            Name = "a1",
            ApiKeyEncrypted = "enc",
            Enabled = true,
        };
        db.ProviderAccounts.Add(account);
        db.SaveChanges();
        db.Set<ProviderAccountProxy>().Add(new ProviderAccountProxy { AccountId = account.Id, ProxyId = proxyId });
        db.SaveChanges();
        return account.Id;
    }

    private ComboResolver CreateSut(out CapturingLog log)
    {
        log = new CapturingLog();
        return new(_db.CreateFactory(), log);
    }

    private sealed class CapturingLog : ILogService
    {
        public List<string> Infos { get; } = [];
        public List<string> Warns { get; } = [];
        public List<string> Errors { get; } = [];

        public event Action<LogEntry>? LogAdded { add { } remove { } }
        public void Write(LogEntry entry) { }
        public void Info(string message, LogCategory category = LogCategory.App) => Infos.Add(message);
        public void Warn(string message, LogCategory category = LogCategory.App) => Warns.Add(message);
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
            Errors.Add(message);
        public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) { }
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }

    [Fact]
    public async Task Resolve_WhenModelIdMatches_ReturnsAllOpenAiCandidatesSortedByProviderId()
    {
        // Thứ tự seed: zeta id < alpha id — sort phải theo Provider.Id, không theo tên (alpha < zeta)
        var zeta = SeedProvider("zeta", modelIds: ["shared"]);
        SeedProvider("alpha", modelIds: ["shared"]);
        SeedProvider("anth", ProviderType.Anthropic, modelIds: ["shared"]);
        using (var db = _db.CreateFactory().CreateDbContext())
        {
            db.Providers.Find(zeta)!.Accounts.Add(new ProviderAccount
            {
                Name = "a1",
                ApiKeyEncrypted = "enc",
                Enabled = true,
            });
            db.SaveChanges();
        }

        var result = await CreateSut(out _).ResolveAsync("shared", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        Assert.Equal(ComboMode.RoundRobin, ok.Mode);
        Assert.Equal(new[] { "zeta", "alpha" }, ok.Candidates.Select(c => c.Provider.Name));
        Assert.Equal("shared", ok.Candidates[0].Model.ModelId);
        // Include(Accounts) phải sống — key resolve cần nav này
        Assert.Contains(ok.Candidates[0].Provider.Accounts, a => a.Name == "a1");
    }

    [Fact]
    public async Task Resolve_WhenModelIdNoMatch_FallsBackToComboByName()
    {
        SeedProvider("p1", modelIds: ["m1"]);
        SeedCombo("fast", ComboMode.RoundRobin, (1, ModelKey("m1"), null));

        var result = await CreateSut(out _).ResolveAsync("fast", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        Assert.Equal(ComboMode.RoundRobin, ok.Mode);
        var candidate = Assert.Single(ok.Candidates);
        Assert.Equal("m1", candidate.Model.ModelId);
    }

    [Fact]
    public async Task ComboResolver_ResolveByComboName_SetsComboNameOnSelection()
    {
        SeedProvider("p1", modelIds: ["m1"]);
        SeedCombo("ai-fast", ComboMode.RoundRobin, (1, ModelKey("m1"), null));

        var result = await CreateSut(out _).ResolveAsync("ai-fast", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        Assert.Equal("ai-fast", ok.ComboName);
    }

    [Fact]
    public async Task ComboResolver_ResolveByModelId_ComboNameIsNull()
    {
        SeedProvider("p1", modelIds: ["m1"]);

        var result = await CreateSut(out _).ResolveAsync("m1", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        Assert.Null(ok.ComboName);
    }

    [Fact]
    public async Task Resolve_WhenNothingMatches_ReturnsNotFoundWithOriginalModelId()
    {
        var result = await CreateSut(out _).ResolveAsync("ghost", default);

        var fail = Assert.IsType<SelectionFailure>(result);
        Assert.Equal("ghost", fail.ModelId);
        Assert.Equal(ResolveFailure.NotFound, fail.Reason);
    }

    [Fact]
    public async Task Resolve_ComboFallbackMode_ReturnsCandidatesInPositionOrder()
    {
        // p1 id < p2 id nhưng m-pos1 thuộc p2 — Fallback phải giữ Position, không sort Provider.Id
        SeedProvider("p1", modelIds: ["m-pos2"]);
        SeedProvider("p2", modelIds: ["m-pos1"]);
        SeedCombo("fb", ComboMode.Fallback,
            (1, ModelKey("m-pos1"), null),
            (2, ModelKey("m-pos2"), null));

        var result = await CreateSut(out _).ResolveAsync("fb", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        Assert.Equal(ComboMode.Fallback, ok.Mode);
        Assert.Equal(new[] { "p2", "p1" }, ok.Candidates.Select(c => c.Provider.Name));
        Assert.Equal(new[] { "m-pos1", "m-pos2" }, ok.Candidates.Select(c => c.Model.ModelId));
    }

    [Fact]
    public async Task Resolve_ComboNestedTwoLevels_CombinesInTraversalOrder()
    {
        SeedProvider("p", modelIds: ["mA", "mB", "mC"]);
        var childId = SeedCombo("child", ComboMode.Fallback,
            (1, ModelKey("mA"), null),
            (2, ModelKey("mB"), null));
        SeedCombo("root", ComboMode.Fallback,
            (1, null, childId),
            (2, ModelKey("mC"), null));

        var result = await CreateSut(out _).ResolveAsync("root", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        Assert.Equal(ComboMode.Fallback, ok.Mode);
        // Combo con inline tại đúng cấp: mA, mB (child) rồi mC (root) — đúng traversal
        Assert.Equal(new[] { "mA", "mB", "mC" }, ok.Candidates.Select(c => c.Model.ModelId));
    }

    [Fact]
    public async Task Resolve_ComboItemTargetDisabled_SkipsItem()
    {
        // Query candidate lọc sẵn model disabled + provider disabled — chỉ model sống quay lại
        var host = SeedProvider("host");
        var dead = AddModel(host, "m-dead", enabled: false);
        SeedProvider("off", enabled: false, modelIds: ["m-offp"]);
        SeedProvider("live", modelIds: ["m-live"]);
        SeedCombo("mixed", ComboMode.RoundRobin,
            (1, dead, null),
            (2, ModelKey("m-offp"), null),
            (3, ModelKey("m-live"), null));

        var result = await CreateSut(out _).ResolveAsync("mixed", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        var candidate = Assert.Single(ok.Candidates);
        Assert.Equal("m-live", candidate.Model.ModelId);
    }

    [Fact]
    public async Task Resolve_WhenComboContainsCycle_SkipsCyclicalItemAndLogsWarn()
    {
        SeedProvider("p", modelIds: ["mX"]);
        var mX = ModelKey("mX");
        var aId = SeedCombo("root", ComboMode.RoundRobin, (1, mX, null));
        var bId = SeedCombo("child", ComboMode.RoundRobin, (1, mX, null));
        AddComboItem(aId, 2, null, bId); // root → child
        AddComboItem(bId, 2, null, aId); // child → root (vòng)

        var sut = CreateSut(out var log);
        var result = await sut.ResolveAsync("root", default);

        // Không treo: cycle-guard bỏ item quay về root; dedup gộp 2 lần thấy mX
        var ok = Assert.IsType<SelectionSuccess>(result);
        Assert.Single(ok.Candidates);
        Assert.Equal("mX", ok.Candidates[0].Model.ModelId);
        Assert.Contains(log.Warns, w => w.Contains("có vòng lặp") && w.Contains("bỏ qua item"));
    }

    [Fact]
    public async Task Resolve_WhenAllCandidatesAnthropic_ReturnsAnthropicNotSupported()
    {
        SeedProvider("claude", ProviderType.Anthropic, modelIds: ["s1"]);

        var result = await CreateSut(out _).ResolveAsync("s1", default);

        var fail = Assert.IsType<SelectionFailure>(result);
        Assert.Equal("s1", fail.ModelId);
        Assert.Equal(ResolveFailure.AnthropicNotSupported, fail.Reason);
    }

    [Fact]
    public async Task Resolve_WhenComboYieldsOnlyAnthropic_ReturnsAnthropicNotSupported()
    {
        SeedProvider("claude", ProviderType.Anthropic, modelIds: ["c1"]);
        SeedCombo("claude-only", ComboMode.Fallback, (1, ModelKey("c1"), null));

        var result = await CreateSut(out _).ResolveAsync("claude-only", default);

        var fail = Assert.IsType<SelectionFailure>(result);
        Assert.Equal("claude-only", fail.ModelId);
        Assert.Equal(ResolveFailure.AnthropicNotSupported, fail.Reason);
    }

    [Fact]
    public async Task Resolve_ComboItemsAllDead_ReturnsNotFound()
    {
        var host = SeedProvider("host");
        var dead = AddModel(host, "m-dead", enabled: false);
        SeedCombo("all-dead", ComboMode.RoundRobin, (1, dead, null));

        var result = await CreateSut(out _).ResolveAsync("all-dead", default);

        var fail = Assert.IsType<SelectionFailure>(result);
        // Message 404 dùng chuỗi client gửi (tên combo), không phải id nội bộ
        Assert.Equal("all-dead", fail.ModelId);
        Assert.Equal(ResolveFailure.NotFound, fail.Reason);
    }

    [Fact]
    public async Task Resolve_WhenModelDisabled_FallsThroughToComboWithSameName()
    {
        var host = SeedProvider("host");
        AddModel(host, "dual", enabled: false); // model id khớp nhưng tắt → không tính
        SeedProvider("live", modelIds: ["mLive"]);
        SeedCombo("dual", ComboMode.RoundRobin, (1, ModelKey("mLive"), null));

        var result = await CreateSut(out _).ResolveAsync("dual", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        var candidate = Assert.Single(ok.Candidates);
        Assert.Equal("mLive", candidate.Model.ModelId);
    }

    [Fact]
    public async Task Resolve_IncludesProviderAccounts_ForKeyResolution()
    {
        var main = SeedProvider("main", modelIds: ["gpt-4o-mini"]);
        using (var db = _db.CreateFactory().CreateDbContext())
        {
            db.Providers.Find(main)!.Accounts.Add(new ProviderAccount
            {
                Name = "a1",
                ApiKeyEncrypted = "enc",
                Enabled = true,
            });
            db.SaveChanges();
        }

        var result = await CreateSut(out _).ResolveAsync("gpt-4o-mini", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        var candidate = Assert.Single(ok.Candidates);
        // AsNoTracking vẫn phải materialize Accounts — ProviderKeyResolver cần (3A parity)
        Assert.Contains(candidate.Provider.Accounts, a => a.Name == "a1");
    }

    [Fact]
    public async Task ResolveAsync_WhenProviderHasAssignment_LoadsJunction()
    {
        var providerId = SeedProvider("proxied", modelIds: ["m-assigned"]);
        var proxyId = SeedProxy(port: 9600);
        AssignProviderProxy(providerId, proxyId);

        var result = await CreateSut(out _).ResolveAsync("m-assigned", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        var candidate = Assert.Single(ok.Candidates);
        // Include junction là regression gate: thiếu thì ProviderProxies rỗng và
        // ProxySelectionResolver trả Direct cho mọi request (spec §6).
        var junction = Assert.Single(candidate.Provider.ProviderProxies);
        Assert.Equal(proxyId, junction.ProxyId);
        Assert.False(new ProxySelectionResolver().Resolve(candidate.Provider, null).IsDirect);
    }

    [Fact]
    public async Task ResolveAsync_WhenAccountHasAssignment_LoadsAccountJunction()
    {
        var providerId = SeedProvider("with-account", modelIds: ["m-account"]);
        var proxyId = SeedProxy(port: 9601);
        AddAccountWithProxy(providerId, proxyId);

        var result = await CreateSut(out _).ResolveAsync("m-account", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        var account = Assert.Single(ok.Candidates[0].Provider.Accounts);
        // AccountProxies đi theo chain Include Accounts → ThenInclude — thiếu thì account
        // override (D1) không bao giờ kích hoạt dù hàng junction có trong DB.
        var junction = Assert.Single(account.AccountProxies);
        Assert.Equal(proxyId, junction.ProxyId);
        Assert.False(new ProxySelectionResolver().Resolve(ok.Candidates[0].Provider, account).IsDirect);
    }

    [Fact]
    public async Task Resolve_WhenProviderDisabled_ReturnsNotFound()
    {
        // Provider tắt → 404 trước 503/selection (3A parity: 404 wins)
        SeedProvider("off", enabled: false, modelIds: ["m1"]);

        var result = await CreateSut(out _).ResolveAsync("m1", default);

        var fail = Assert.IsType<SelectionFailure>(result);
        Assert.Equal("m1", fail.ModelId);
        Assert.Equal(ResolveFailure.NotFound, fail.Reason);
    }

    [Fact]
    public async Task Resolve_WhenIdentifierPrefixProvided_PinsToThatProvider()
    {
        var pinned = SeedProvider("p1", modelIds: ["gpt-4o"]);
        SeedProvider("p2", modelIds: ["gpt-4o"]); // cùng model id, không pin
        SetIdentifier(pinned, "myazure");

        var result = await CreateSut(out _).ResolveAsync("myazure/gpt-4o", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        var candidate = Assert.Single(ok.Candidates);
        Assert.Equal("p1", candidate.Provider.Name);
        Assert.Equal("gpt-4o", candidate.Model.ModelId);
    }

    [Fact]
    public async Task Resolve_WhenPrefixNotAnIdentifier_MatchesWholeModelId()
    {
        // Model id kiểu OpenRouter chứa '/' — không có identifier "openai" thì phải match nguyên chuỗi
        SeedProvider("p1", modelIds: ["openai/gpt-4o"]);

        var result = await CreateSut(out _).ResolveAsync("openai/gpt-4o", default);

        var ok = Assert.IsType<SelectionSuccess>(result);
        Assert.Equal("openai/gpt-4o", Assert.Single(ok.Candidates).Model.ModelId);
    }

    [Fact]
    public async Task Resolve_WhenPrefixMatchesButModelMissingOnProvider_ReturnsNotFound()
    {
        var pinned = SeedProvider("p1", modelIds: ["other"]);
        SetIdentifier(pinned, "myazure");

        var result = await CreateSut(out _).ResolveAsync("myazure/ghost", default);

        var fail = Assert.IsType<SelectionFailure>(result);
        Assert.Equal(ResolveFailure.NotFound, fail.Reason);
    }
}
