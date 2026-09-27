using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Combos;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Combos;

public class ComboServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ComboService _service;

    public ComboServiceTests()
    {
        // Initialize trước mỗi test - TestDb là file trống, schema chưa có (pattern Phase 1)
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        _service = new ComboService(_db);
    }

    public void Dispose() => _testDb.Dispose();

    private static ComboDraft Draft(string name = "combo-a", params ComboItemDraft[] items) => new()
    {
        Name = name,
        Items = [.. items],
    };

    private async Task<long> SeedModelAsync(string modelId = "m1")
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider
        {
            Name = "P",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
            Models = { new Model { ModelId = modelId } },
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Models[0].Id;
    }

    private async Task<long> SeedComboAsync(string name, long? targetModelId = null, long? targetComboId = null)
    {
        using var db = _db.CreateDbContext();
        var combo = new Combo { Name = name };
        if (targetModelId is not null || targetComboId is not null)
        {
            combo.Items.Add(new ComboItem { TargetModelId = targetModelId, TargetComboId = targetComboId, Position = 0 });
        }

        db.Combos.Add(combo);
        await db.SaveChangesAsync();
        return combo.Id;
    }

    [Fact]
    public async Task ListAsync_ReturnsWithItemsSortedByName()
    {
        var modelId = await SeedModelAsync();
        await SeedComboAsync("zeta", targetModelId: modelId);
        await SeedComboAsync("alpha", targetModelId: modelId);

        var combos = await _service.ListAsync();

        Assert.Equal(new[] { "alpha", "zeta" }, combos.Select(c => c.Name));
        Assert.All(combos, c => Assert.NotEmpty(c.Items)); // Include Items - không phải query trì hoãn
    }

    [Fact]
    public async Task Create_WhenValid_TrimsNameAndAssignsPositions()
    {
        var model1 = await SeedModelAsync("m1");
        var model2 = await SeedModelAsync("m2");

        var combo = await _service.CreateAsync(Draft("  combo-a  ",
            new ComboItemDraft { TargetModelId = model2 },
            new ComboItemDraft { TargetModelId = model1 }));

        Assert.Equal("combo-a", combo.Name); // trim
        using var db = _db.CreateDbContext();
        var saved = await db.Combos.Include(c => c.Items).SingleAsync(c => c.Id == combo.Id);
        // Position tính theo thứ tự draft, không theo id
        var ordered = saved.Items.OrderBy(i => i.Position).ToList();
        Assert.Equal(new[] { 0, 1 }, ordered.Select(i => i.Position));
        Assert.Equal(new long?[] { model2, model1 }, ordered.Select(i => i.TargetModelId));
    }

    [Fact]
    public async Task Create_WhenNameEmpty_ThrowsArgumentException()
    {
        var modelId = await SeedModelAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.CreateAsync(Draft("   ", new ComboItemDraft { TargetModelId = modelId })));

        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public async Task Create_WhenDuplicateName_ThrowsDuplicateName()
    {
        var modelId = await SeedModelAsync();
        await SeedComboAsync("combo-a", targetModelId: modelId);

        var ex = await Assert.ThrowsAsync<ComboValidationException>(() =>
            _service.CreateAsync(Draft("combo-a", new ComboItemDraft { TargetModelId = modelId })));

        Assert.Equal(ComboValidationError.DuplicateName, ex.Code);
    }

    [Fact]
    public async Task Create_WhenNoItems_ThrowsEmptyItems()
    {
        var ex = await Assert.ThrowsAsync<ComboValidationException>(() =>
            _service.CreateAsync(Draft("combo-a")));

        Assert.Equal(ComboValidationError.EmptyItems, ex.Code);
    }

    [Fact]
    public async Task Create_WhenNameTooLong_ThrowsNameTooLong()
    {
        var modelId = await SeedModelAsync();

        var ex = await Assert.ThrowsAsync<ComboValidationException>(() =>
            _service.CreateAsync(Draft(new string('a', 201), new ComboItemDraft { TargetModelId = modelId })));

        Assert.Equal(ComboValidationError.NameTooLong, ex.Code);
    }

    [Fact]
    public async Task Create_WhenItemTargetsNothing_ThrowsInvalidItemTarget()
    {
        var ex = await Assert.ThrowsAsync<ComboValidationException>(() =>
            _service.CreateAsync(Draft("combo-a", new ComboItemDraft())));

        Assert.Equal(ComboValidationError.InvalidItemTarget, ex.Code);
    }

    [Fact]
    public async Task Create_WhenItemTargetsBoth_ThrowsInvalidItemTarget()
    {
        var modelId = await SeedModelAsync();
        var comboId = await SeedComboAsync("other");

        var ex = await Assert.ThrowsAsync<ComboValidationException>(() =>
            _service.CreateAsync(Draft("combo-a",
                new ComboItemDraft { TargetModelId = modelId, TargetComboId = comboId })));

        Assert.Equal(ComboValidationError.InvalidItemTarget, ex.Code);
    }

    [Fact]
    public async Task Create_WhenModelMissing_ThrowsTargetNotFound()
    {
        var ex = await Assert.ThrowsAsync<ComboValidationException>(() =>
            _service.CreateAsync(Draft("combo-a", new ComboItemDraft { TargetModelId = 999 })));

        Assert.Equal(ComboValidationError.TargetNotFound, ex.Code);
    }

    [Fact]
    public async Task Create_WhenComboMissing_ThrowsTargetNotFound()
    {
        var ex = await Assert.ThrowsAsync<ComboValidationException>(() =>
            _service.CreateAsync(Draft("combo-a", new ComboItemDraft { TargetComboId = 999 })));

        Assert.Equal(ComboValidationError.TargetNotFound, ex.Code);
    }

    [Fact]
    public async Task Create_WhenTargetingExistingCombo_Succeeds()
    {
        var otherId = await SeedComboAsync("other");

        var combo = await _service.CreateAsync(Draft("combo-a", new ComboItemDraft { TargetComboId = otherId }));

        Assert.True(combo.Id > 0); // lồng combo hợp lệ - không thể cycle khi tạo id mới
    }

    [Fact]
    public async Task Update_WhenSelfReference_ThrowsCycleDetected()
    {
        var comboId = await SeedComboAsync("combo-a");

        var ex = await Assert.ThrowsAsync<ComboValidationException>(() =>
            _service.UpdateAsync(comboId, Draft("combo-a", new ComboItemDraft { TargetComboId = comboId })));

        Assert.Equal(ComboValidationError.CycleDetected, ex.Code);
    }

    [Fact]
    public async Task Update_WhenTargetingAncestor_ThrowsCycleDetected()
    {
        // x chứa y (x → y); sửa y thêm item trỏ x → y sẽ chứa x chứa y = cycle gián tiếp
        var yId = await SeedComboAsync("y");
        var xId = await SeedComboAsync("x", targetComboId: yId);

        var ex = await Assert.ThrowsAsync<ComboValidationException>(() =>
            _service.UpdateAsync(yId, Draft("y", new ComboItemDraft { TargetComboId = xId })));

        Assert.Equal(ComboValidationError.CycleDetected, ex.Code);
    }

    [Fact]
    public async Task Update_WhenValid_ReplacesItemsAndUpdatesMode()
    {
        var model1 = await SeedModelAsync("m1");
        var model2 = await SeedModelAsync("m2");
        var comboId = await SeedComboAsync("old-name", targetModelId: model1);

        await _service.UpdateAsync(comboId, new ComboDraft
        {
            Name = "new-name",
            Mode = ComboMode.Fallback,
            Items = [new ComboItemDraft { TargetModelId = model2 }],
        });

        using var db = _db.CreateDbContext();
        var saved = await db.Combos.Include(c => c.Items).SingleAsync(c => c.Id == comboId);
        Assert.Equal("new-name", saved.Name);
        Assert.Equal(ComboMode.Fallback, saved.Mode);
        var item = Assert.Single(saved.Items); // item cũ bị thay thế toàn bộ
        Assert.Equal(0, item.Position);
        Assert.Equal(model2, item.TargetModelId);
    }

    [Fact]
    public async Task Update_WhenUnknownId_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            _service.UpdateAsync(999, Draft("combo-a")));
    }

    [Fact]
    public async Task Delete_WhenUnreferenced_RemovesComboAndItems()
    {
        var modelId = await SeedModelAsync();
        var comboId = await SeedComboAsync("combo-a", targetModelId: modelId);

        await _service.DeleteAsync(comboId);

        using var db = _db.CreateDbContext();
        Assert.False(await db.Combos.AnyAsync(c => c.Id == comboId));
        Assert.Equal(0, await db.ComboItems.CountAsync()); // cascade items
    }

    [Fact]
    public async Task Delete_WhenReferenced_ThrowsInvalidOperation()
    {
        var parentId = await SeedComboAsync("parent");
        await SeedComboAsync("child", targetComboId: parentId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(parentId));
    }

    [Fact]
    public async Task Delete_WhenUnknownId_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.DeleteAsync(999));
    }

    [Fact]
    public async Task GetReferencingComboNames_ReturnsNamesSorted()
    {
        var parentId = await SeedComboAsync("parent");
        await SeedComboAsync("zeta-child", targetComboId: parentId);
        await SeedComboAsync("alpha-child", targetComboId: parentId);
        await SeedComboAsync("unrelated");

        var names = await _service.GetReferencingComboNamesAsync(parentId);

        Assert.Equal(new[] { "alpha-child", "zeta-child" }, names);
    }

    [Fact]
    public async Task Cascade_WhenTargetModelRemoved_ComboItemsRemoved()
    {
        var modelId = await SeedModelAsync();
        var comboId = await SeedComboAsync("combo-a", targetModelId: modelId);

        using (var db = _db.CreateDbContext())
        {
            var model = await db.Models.SingleAsync(m => m.Id == modelId);
            db.Models.Remove(model);
            await db.SaveChangesAsync();
        }

        using var db2 = _db.CreateDbContext();
        var combo = await db2.Combos.Include(c => c.Items).SingleAsync(c => c.Id == comboId);
        Assert.Empty(combo.Items); // FK cascade Phase 1 còn hiệu lực
    }
}
