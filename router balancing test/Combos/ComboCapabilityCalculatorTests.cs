using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Combos;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Combos;

public class ComboCapabilityCalculatorTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ComboCapabilityCalculator _calculator;

    public ComboCapabilityCalculatorTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        _calculator = new ComboCapabilityCalculator(_db);
    }

    public void Dispose() => _testDb.Dispose();

    private async Task<long> SeedModelAsync(int? ctx = null, bool vision = false, bool audio = false,
        bool video = false, bool think = false, bool enabled = true)
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com" };
        provider.Models.Add(new Model
        {
            ModelId = "m",
            ContextWindow = ctx,
            SupportsVision = vision,
            SupportsAudio = audio,
            SupportsVideo = video,
            SupportsThink = think,
            Enabled = enabled,
        });
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Models[0].Id;
    }

    private async Task<long> SeedComboAsync(string name, params ComboItem[] items)
    {
        using var db = _db.CreateDbContext();
        var combo = new Combo { Name = name };
        for (var i = 0; i < items.Length; i++)
        {
            items[i].Position = i;
            combo.Items.Add(items[i]);
        }

        db.Combos.Add(combo);
        await db.SaveChangesAsync();
        return combo.Id;
    }

    private static ComboItem ItemToModel(long modelId) => new() { TargetModelId = modelId };

    private static ComboItem ItemToCombo(long comboId) => new() { TargetComboId = comboId };

    [Fact]
    public async Task ComputeAll_SingleModelMember_CopiesEntityValues()
    {
        var modelId = await SeedModelAsync(ctx: 128_000, vision: true, audio: true, video: false, think: false);
        var comboId = await SeedComboAsync("combo", ItemToModel(modelId));

        var caps = await _calculator.ComputeAllAsync();

        var combo = caps[comboId];
        Assert.Equal(128_000, combo.ContextWindow);
        Assert.True(combo.Vision);
        Assert.True(combo.Audio);
        Assert.False(combo.Video);
        Assert.False(combo.Thinking);
    }

    [Fact]
    public async Task ComputeAll_MixedMembers_AndsBools_MinsCtx()
    {
        var modelA = await SeedModelAsync(ctx: 128_000, vision: true, audio: true, video: false, think: false);
        var modelB = await SeedModelAsync(ctx: 64_000, vision: true, audio: false, video: true, think: true);
        var comboId = await SeedComboAsync("combo", ItemToModel(modelA), ItemToModel(modelB));

        var caps = await _calculator.ComputeAllAsync();

        var combo = caps[comboId];
        Assert.Equal(64_000, combo.ContextWindow);
        Assert.True(combo.Vision);
        Assert.False(combo.Audio);    // false của model B thắng true của model A
        Assert.False(combo.Video);    // false của model A thắng true của model B
        Assert.False(combo.Thinking); // false của model A thắng true của model B
    }

    [Fact]
    public async Task ComputeAll_EmptyCombo_AllNull()
    {
        var comboId = await SeedComboAsync("empty");

        var caps = await _calculator.ComputeAllAsync();

        var combo = caps[comboId];
        Assert.Null(combo.ContextWindow);
        Assert.Null(combo.Vision);
        Assert.Null(combo.Audio);
        Assert.Null(combo.Video);
        Assert.Null(combo.Thinking);
    }

    [Fact]
    public async Task ComputeAll_NestedCombo_AndsRecursively()
    {
        var innerModel = await SeedModelAsync(ctx: 100_000, vision: true, audio: true, video: true, think: true);
        var innerId = await SeedComboAsync("inner", ItemToModel(innerModel));
        var outerModel = await SeedModelAsync(ctx: 50_000, vision: true, audio: false, video: true, think: true);
        var outerId = await SeedComboAsync("outer", ItemToCombo(innerId), ItemToModel(outerModel));

        var caps = await _calculator.ComputeAllAsync();

        var inner = caps[innerId];
        Assert.Equal(100_000, inner.ContextWindow);
        Assert.True(inner.Vision);
        Assert.True(inner.Audio);
        Assert.True(inner.Video);
        Assert.True(inner.Thinking);

        var outer = caps[outerId];
        Assert.Equal(50_000, outer.ContextWindow); // min(100K, 50K)
        Assert.True(outer.Vision);
        Assert.False(outer.Audio);
        Assert.True(outer.Video);
        Assert.True(outer.Thinking);
    }

    [Fact]
    public async Task ComputeAll_NestedComboChildWithFalse_PropagatesFalse()
    {
        var innerModel = await SeedModelAsync(ctx: 64_000, vision: true, audio: false, video: true, think: true);
        var innerId = await SeedComboAsync("inner", ItemToModel(innerModel));
        var otherModel = await SeedModelAsync(ctx: 32_000, vision: true, audio: true, video: true, think: true);
        var outerId = await SeedComboAsync("outer", ItemToCombo(innerId), ItemToModel(otherModel));

        var caps = await _calculator.ComputeAllAsync();

        Assert.False(caps[innerId].Audio);
        Assert.False(caps[outerId].Audio); // false của combo con thắng true của model còn lại
    }

    [Fact]
    public async Task ComputeAll_EmptyChildWithFalseSibling_FalseBeatsNullButUnknownPersists()
    {
        var emptyChildId = await SeedComboAsync("empty-child");
        var modelId = await SeedModelAsync(ctx: 128_000, vision: true, audio: true, video: false, think: true);
        var parentId = await SeedComboAsync("parent", ItemToCombo(emptyChildId), ItemToModel(modelId));

        var caps = await _calculator.ComputeAllAsync();

        var parent = caps[parentId];
        // Guard 3-valued AND: false thắng null, nhưng null phải sống sót ở field
        // không có false — nếu regression (a ?? false) & (b ?? false) thì Vision
        // thành false thay vì null ("không hỗ trợ" oan cho member chưa rõ)
        Assert.False(parent.Video);
        Assert.Null(parent.Vision);
        Assert.Null(parent.Audio);
        Assert.Null(parent.Thinking);
        Assert.Null(parent.ContextWindow); // ctx null từ child chưa rõ — không phải min(128K)
    }

    [Fact]
    public async Task ComputeAll_NullContextWindowMember_ComboCtxIsNull()
    {
        var modelWithCtx = await SeedModelAsync(ctx: 128_000, vision: true);
        var modelWithoutCtx = await SeedModelAsync(vision: true); // ContextWindow null
        var comboId = await SeedComboAsync("combo", ItemToModel(modelWithCtx), ItemToModel(modelWithoutCtx));

        var caps = await _calculator.ComputeAllAsync();

        var combo = caps[comboId];
        // ANY member ctx null → combo null, không phải min-of-non-null (128K)
        Assert.Null(combo.ContextWindow);
        Assert.True(combo.Vision); // bool AND vẫn tính độc lập ctx
    }

    [Fact]
    public async Task ComputeAll_WhenCycleInItems_SkipsBackEdge()
    {
        // Seed cycle thô, bypass validation của ComboService (data bẩn phòng thủ)
        var aId = await SeedComboAsync("a");
        var bId = await SeedComboAsync("b", ItemToCombo(aId));
        using (var db = _db.CreateDbContext())
        {
            var a = await db.Combos.SingleAsync(c => c.Id == aId);
            a.Items.Add(ItemToCombo(bId));
            await db.SaveChangesAsync();
        }

        var caps = await _calculator.ComputeAllAsync();

        Assert.True(caps.ContainsKey(aId));
        Assert.True(caps.ContainsKey(bId));
    }

    [Fact]
    public async Task ComputeAll_DisabledModelMember_StillCounted()
    {
        var modelId = await SeedModelAsync(ctx: 128_000, vision: true, enabled: false);
        var comboId = await SeedComboAsync("combo", ItemToModel(modelId));

        var caps = await _calculator.ComputeAllAsync();

        var combo = caps[comboId];
        Assert.Equal(128_000, combo.ContextWindow);
        Assert.True(combo.Vision);
    }
}
