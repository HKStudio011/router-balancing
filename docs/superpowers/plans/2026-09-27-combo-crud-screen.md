# Plan: Combo CRUD Screen (Phase 2C)

- **Spec:** [docs/superpowers/specs/2026-09-27-combo-crud-screen-design.md](../specs/2026-09-27-combo-crud-screen-design.md) (Approved)
- **Goal:** CRUD screen cho Combo — service có validation + cycle check (unit test), page `/combos` list + modal form + delete warning, nav link, 27 key i18n.
- **Branch:** `feat/combo-crud` từ master (`0ae6be3`).
- **Stack:** .NET 10 MAUI Blazor Hybrid, EF Core (SQLite, bảng `Combos`/`ComboItems` đã có từ Phase 1), Tailwind (vite), xUnit.
- **Non-goals:** dispatcher/nested resolution runtime, `/v1/models` trả combo, combo test-before-save, status active/total.
- **Baseline:** `dotnet test` = 127 tests xanh (chạy trước khi bắt đầu — nếu app đang mở, đóng trước để tránh mutex fail 2 test `SingleInstanceGuardTests`).

## Tasks

### Task 1 — Core: `IComboService` + `ComboService` + validation + tests (TDD)

**Files:**
- Tạo mới: `src/RouterBalancing.Core/Combos/ComboDraft.cs`
- Tạo mới: `src/RouterBalancing.Core/Combos/ComboValidationException.cs`
- Tạo mới: `src/RouterBalancing.Core/Combos/IComboService.cs`
- Tạo mới: `src/RouterBalancing.Core/Combos/ComboService.cs`
- Sửa: `router-balancing/MauiProgram.cs` — đăng ký DI
- Tạo mới: `router balancing test/Combos/ComboServiceTests.cs`

**RED:** tạo file test với 20 test dưới đây → chạy → fail (chưa có type).

**Code — `ComboDraft.cs`:**

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Combos;

/// <summary>Bản nháp form combo — property mutable để Blazor @bind ghi được (giống ProviderDraft).</summary>
public sealed record ComboDraft
{
    public string Name { get; set; } = string.Empty;

    public ComboMode Mode { get; set; } = ComboMode.RoundRobin;

    public List<ComboItemDraft> Items { get; set; } = [];
}

/// <summary>Dòng item trong draft — đúng 1 trong 2 target, validate ở service (spec §3).</summary>
public sealed record ComboItemDraft
{
    public long? TargetModelId { get; set; }

    public long? TargetComboId { get; set; }
}
```

**Code — `ComboValidationException.cs`:**

```csharp
namespace RouterBalancing.Core.Combos;

/// <summary>Lý do draft combo không hợp lệ — UI map sang key i18n combos.error.{camelCase}.</summary>
public enum ComboValidationError
{
    DuplicateName,
    EmptyItems,
    CycleDetected,
    InvalidItemTarget,
    TargetNotFound,
    NameTooLong,
}

/// <summary>Exception validation combo — giữ Code để UI chọn đúng thông điệp i18n.</summary>
public sealed class ComboValidationException : Exception
{
    /// <summary>Lý do cụ thể.</summary>
    public ComboValidationError Code { get; }

    public ComboValidationException(ComboValidationError code, string message)
        : base(message)
    {
        Code = code;
    }
}
```

**Code — `IComboService.cs`:**

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Combos;

/// <summary>CRUD combo — cycle check chạy lúc save, không để FK Restrict nổ (spec §3).</summary>
public interface IComboService
{
    /// <summary>Tất cả combo, sắp theo Name, đã Include Items.</summary>
    Task<IReadOnlyList<Combo>> ListAsync(CancellationToken ct = default);

    /// <summary>Tạo combo mới từ draft — Position gán 0..n-1 theo thứ tự Items.</summary>
    /// <exception cref="ComboValidationException">Khi draft không hợp lệ.</exception>
    /// <exception cref="ArgumentException">Khi Name rỗng sau trim.</exception>
    Task<Combo> CreateAsync(ComboDraft draft, CancellationToken ct = default);

    /// <summary>Cập nhật — Items thay thế toàn bộ theo draft.</summary>
    /// <exception cref="KeyNotFoundException">Khi không có combo <paramref name="id"/>.</exception>
    /// <exception cref="ComboValidationException">Khi draft không hợp lệ, gồm cycle.</exception>
    /// <exception cref="ArgumentException">Khi Name rỗng sau trim.</exception>
    Task UpdateAsync(long id, ComboDraft draft, CancellationToken ct = default);

    /// <summary>Xóa combo — Items cascade; chặn khi còn combo khác trỏ tới (FK Restrict backstop).</summary>
    /// <exception cref="KeyNotFoundException">Khi không có combo <paramref name="id"/>.</exception>
    /// <exception cref="InvalidOperationException">Khi còn combo tham chiếu.</exception>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Tên combo đang trỏ tới <paramref name="id"/>, sắp theo Name — cho warning của UI trước khi xóa.</summary>
    Task<IReadOnlyList<string>> GetReferencingComboNamesAsync(long id, CancellationToken ct = default);
}
```

**Code — `ComboService.cs`:**

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Combos;

/// <inheritdoc/>
public sealed class ComboService : IComboService
{
    private const int MaxNameLength = 200;

    private readonly IDbContextFactory<RouterBalancingDbContext> _db;

    /// <inheritdoc/>
    public ComboService(IDbContextFactory<RouterBalancingDbContext> db)
    {
        _db = db;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Combo>> ListAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.Combos.Include(c => c.Items).OrderBy(c => c.Name).ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<Combo> CreateAsync(ComboDraft draft, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var name = NormalizeName(draft.Name);
        await ValidateAsync(db, name, draft.Items, selfId: null, ct);

        var combo = new Combo { Name = name, Mode = draft.Mode };
        for (var i = 0; i < draft.Items.Count; i++)
        {
            combo.Items.Add(ToItem(draft.Items[i], i));
        }

        db.Combos.Add(combo);
        await db.SaveChangesAsync(ct);
        return combo;
    }

    /// <inheritdoc/>
    public async Task UpdateAsync(long id, ComboDraft draft, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var combo = await db.Combos.Include(c => c.Items)
                .FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new KeyNotFoundException($"Combo {id} not found.");
        var name = NormalizeName(draft.Name);
        await ValidateAsync(db, name, draft.Items, selfId: id, ct);

        combo.Name = name;
        combo.Mode = draft.Mode;
        // Thay thế toàn bộ items: Position tính lại theo thứ tự draft (spec §3)
        db.ComboItems.RemoveRange(combo.Items);
        combo.Items.Clear();
        for (var i = 0; i < draft.Items.Count; i++)
        {
            combo.Items.Add(ToItem(draft.Items[i], i));
        }

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var combo = await db.Combos.FirstOrDefaultAsync(c => c.Id == id, ct)
            ?? throw new KeyNotFoundException($"Combo {id} not found.");
        var referencing = await db.ComboItems.CountAsync(ci => ci.TargetComboId == id, ct);
        if (referencing > 0)
        {
            // FK Restrict sẽ nổ nếu bỏ qua — chặn sớm với thông điệp rõ ràng
            throw new InvalidOperationException($"Combo {id} is referenced by {referencing} item(s).");
        }

        db.Combos.Remove(combo);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<string>> GetReferencingComboNamesAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.Combos
            .Where(c => c.Items.Any(ci => ci.TargetComboId == id))
            .OrderBy(c => c.Name)
            .Select(c => c.Name)
            .ToListAsync(ct);
    }

    private static string NormalizeName(string name)
    {
        var trimmed = name.Trim();
        // Rỗng → ArgumentException (nhất quán ThrowIfNullOrWhiteSpace của repo); UI CanSave chặn trước
        return trimmed.Length == 0
            ? throw new ArgumentException("Combo name is required.", nameof(name))
            : trimmed;
    }

    private static ComboItem ToItem(ComboItemDraft draft, int position) => new()
    {
        TargetModelId = draft.TargetModelId,
        TargetComboId = draft.TargetComboId,
        Position = position,
    };

    private static async Task ValidateAsync(RouterBalancingDbContext db, string name,
        IReadOnlyList<ComboItemDraft> items, long? selfId, CancellationToken ct)
    {
        if (name.Length > MaxNameLength)
        {
            throw new ComboValidationException(ComboValidationError.NameTooLong,
                $"Name must be {MaxNameLength} characters or fewer.");
        }

        if (items.Count == 0)
        {
            throw new ComboValidationException(ComboValidationError.EmptyItems,
                "Combo needs at least one item.");
        }

        if (await db.Combos.AnyAsync(c => c.Name == name && c.Id != selfId, ct))
        {
            throw new ComboValidationException(ComboValidationError.DuplicateName,
                $"A combo named '{name}' already exists.");
        }

        foreach (var item in items)
        {
            var hasModel = item.TargetModelId is not null;
            var hasCombo = item.TargetComboId is not null;
            if (hasModel == hasCombo) // cả 2 null hoặc cả 2 có → sai XOR (spec §3)
            {
                throw new ComboValidationException(ComboValidationError.InvalidItemTarget,
                    "Each item must target exactly one model or combo.");
            }

            if (hasModel && !await db.Models.AnyAsync(m => m.Id == item.TargetModelId, ct))
            {
                throw new ComboValidationException(ComboValidationError.TargetNotFound,
                    $"Model {item.TargetModelId} not found.");
            }

            if (hasCombo && !await db.Combos.AnyAsync(c => c.Id == item.TargetComboId, ct))
            {
                throw new ComboValidationException(ComboValidationError.TargetNotFound,
                    $"Combo {item.TargetComboId} not found.");
            }
        }

        if (selfId is not null)
        {
            await ValidateNoCycleAsync(db, selfId.Value, items, ct);
        }
    }

    private static async Task ValidateNoCycleAsync(RouterBalancingDbContext db, long selfId,
        IReadOnlyList<ComboItemDraft> items, CancellationToken ct)
    {
        // Đồ thị: X → Y = combo X chứa combo Y (từ TargetComboId). Thêm self → C tạo
        // cycle đúng khi đi từ C theo cạnh "chứa" chạm self — tức C là self hoặc tổ tiên của self.
        // CreateAsync truyền selfId null vì id mới chưa combo nào trỏ tới (spec §3).
        var edges = await db.ComboItems
            .Where(ci => ci.TargetComboId != null)
            .Select(ci => new { ci.ComboId, Target = ci.TargetComboId!.Value })
            .ToListAsync(ct);
        var children = edges
            .GroupBy(e => e.ComboId)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Target).ToList());

        foreach (var item in items)
        {
            if (item.TargetComboId is not { } start)
            {
                continue;
            }

            var visited = new HashSet<long>();
            var stack = new Stack<long>();
            stack.Push(start);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == selfId)
                {
                    throw new ComboValidationException(ComboValidationError.CycleDetected,
                        $"Combo {selfId} would contain itself through combo {start}.");
                }

                if (!visited.Add(current))
                {
                    continue;
                }

                if (children.TryGetValue(current, out var next))
                {
                    foreach (var child in next)
                    {
                        stack.Push(child);
                    }
                }
            }
        }
    }
}
```

**Sửa `MauiProgram.cs`:** chèn sau dòng đăng ký `IModelService`:

```csharp
builder.Services.AddSingleton<IComboService, ComboService>();
```

(cùng `using RouterBalancing.Core.Combos;` nếu chưa có).

**Code test — `router balancing test/Combos/ComboServiceTests.cs`** (pattern `TestDb`/`DbInitializer`, mirror `ProviderServiceTests`; namespace `router_balancing_test.Combos`):

```csharp
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
        Assert.Equal(new[] { model2, model1 }, ordered.Select(i => i.TargetModelId));
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
```

**Kiểm chứng Task 1:**

```bash
dotnet test "router balancing test/router balancing test.csproj" --nologo   # 127 + 20 = 147 xanh
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo  # 0W/0E
```

**Commit:** `feat: add combo service with validation and cycle detection`

**Deliverable:** Report `{"status":"complete","summary":"...","commits":["<sha>"],"testFiles":["router balancing test/Combos/ComboServiceTests.cs"],"notes":"..."}`
**Test count chính xác: 147.**

---

### Task 2 — UI: i18n 27 keys + NavMenu + `Combos.razor` + gates

**Files:**
- Sửa: `src/RouterBalancing.Core/Localization/Translations.cs` — 27 keys EN + 27 VI
- Sửa: `router-balancing/Components/Layout/NavMenu.razor` — NavLink `combos`
- Tạo mới: `router-balancing/Components/Pages/Combos.razor`

**Step 1 — i18n.** Chèn khối sau dòng `["models.error.duplicate"] = ...` trong **mỗi** dictionary (EN ~dòng 177, VI ~dòng 347). EN:

```csharp
        // Combos page (Phase 2C)
        ["nav.combos"] = "Combos",
        ["combos.title"] = "Router combos",
        ["combos.add"] = "Add",
        ["combos.col.name"] = "Name",
        ["combos.col.mode"] = "Mode",
        ["combos.col.items"] = "Items",
        ["combos.col.actions"] = "Actions",
        ["combos.mode.roundRobin"] = "Round robin",
        ["combos.mode.fallback"] = "Fallback",
        ["combos.empty"] = "No combos yet. Create one!",
        ["combos.form.title.new"] = "New combo",
        ["combos.form.title.edit"] = "Edit combo",
        ["combos.form.name"] = "Name",
        ["combos.form.mode"] = "Mode",
        ["combos.form.items"] = "Items",
        ["combos.form.addModelItem"] = "Add model",
        ["combos.form.addComboItem"] = "Add combo",
        ["combos.delete.confirm"] = "Delete this combo?",
        ["combos.delete.referenced"] = "Used by:",
        ["combos.error.duplicateName"] = "A combo with this name already exists.",
        ["combos.error.emptyItems"] = "Add at least one item.",
        ["combos.error.cycleDetected"] = "A combo cannot include itself, directly or indirectly.",
        ["combos.error.invalidItemTarget"] = "Each item must target exactly one model or combo.",
        ["combos.error.targetNotFound"] = "Target model or combo no longer exists.",
        ["combos.error.nameTooLong"] = "Name must be 200 characters or fewer.",
        ["combos.msg.saved"] = "Combo saved.",
        ["combos.msg.deleted"] = "Combo deleted.",
```

VI (chú ý: file UTF-8, giữ nguyên ký tự tiếng Việt có dấu):

```csharp
        // Combos page (Phase 2C)
        ["nav.combos"] = "Bộ kết hợp",
        ["combos.title"] = "Bộ kết hợp",
        ["combos.add"] = "Thêm",
        ["combos.col.name"] = "Tên",
        ["combos.col.mode"] = "Chế độ",
        ["combos.col.items"] = "Thành phần",
        ["combos.col.actions"] = "Thao tác",
        ["combos.mode.roundRobin"] = "Chia đều",
        ["combos.mode.fallback"] = "Thử lần lượt",
        ["combos.empty"] = "Chưa có bộ kết hợp nào. Tạo ngay!",
        ["combos.form.title.new"] = "Bộ kết hợp mới",
        ["combos.form.title.edit"] = "Sửa bộ kết hợp",
        ["combos.form.name"] = "Tên",
        ["combos.form.mode"] = "Chế độ",
        ["combos.form.items"] = "Thành phần",
        ["combos.form.addModelItem"] = "Thêm model",
        ["combos.form.addComboItem"] = "Thêm combo con",
        ["combos.delete.confirm"] = "Xóa bộ kết hợp này?",
        ["combos.delete.referenced"] = "Đang được dùng bởi:",
        ["combos.error.duplicateName"] = "Tên bộ kết hợp đã tồn tại.",
        ["combos.error.emptyItems"] = "Cần ít nhất một thành phần.",
        ["combos.error.cycleDetected"] = "Không thể lồng bộ kết hợp vào chính nó, trực tiếp hay gián tiếp.",
        ["combos.error.invalidItemTarget"] = "Mỗi thành phần phải trỏ đúng một model hoặc combo.",
        ["combos.error.targetNotFound"] = "Model hoặc combo được chọn không còn tồn tại.",
        ["combos.error.nameTooLong"] = "Tên tối đa 200 ký tự.",
        ["combos.msg.saved"] = "Đã lưu bộ kết hợp.",
        ["combos.msg.deleted"] = "Đã xóa bộ kết hợp.",
```

Key tái dùng (không thêm mới): `confirm.cancel`, `settings.action.save`, `providers.action.delete`, `dashboard.msg.failed` (precedent: Settings Save đã tái dùng ở Providers.razor).

**Step 2 — `NavMenu.razor`:** chèn NavLink sau NavLink `providers` (giữa Providers và Logs), cùng pattern:

```razor
    <NavLink class="rounded px-3 py-2 text-sm no-underline hover:bg-background"
             href="combos">
        @L["nav.combos"]
    </NavLink>
```

**Step 3 — `Combos.razor`** (full file):

```razor
@page "/combos"
@using RouterBalancing.Core.Combos
@using RouterBalancing.Core.Domain
@using RouterBalancing.Core.Providers
@implements IDisposable
@inject IComboService ComboSvc
@inject IProviderService ProviderSvc
@inject LocalizationService L
@inject ToastService Toast
@inject ILogService Log

<div class="mb-4 flex items-center justify-between gap-3">
    <h1 class="text-xl font-semibold">@L["combos.title"]</h1>
    <button type="button" class="btn btn-primary" @onclick="OpenAdd">+ @L["combos.add"]</button>
</div>

@if (_loading)
{
    <p class="text-sm opacity-70">...</p>
}
else if (_combos.Count == 0)
{
    <EmptyState Icon="&#128218;" Message="@L["combos.empty"]" />
}
else
{
    <div class="overflow-x-auto rounded border border-border bg-surface">
        <table class="w-full text-sm">
            <thead>
                <tr class="border-b border-border text-left text-xs uppercase opacity-70">
                    <th class="px-2 py-2">@L["combos.col.name"]</th>
                    <th class="px-2 py-2">@L["combos.col.mode"]</th>
                    <th class="px-2 py-2">@L["combos.col.items"]</th>
                    <th class="px-2 py-2 text-right">@L["combos.col.actions"]</th>
                </tr>
            </thead>
            <tbody>
                @foreach (var combo in _combos)
                {
                    <tr @key="@($"{combo.Id}-row")" class="border-b border-border">
                        <td class="px-2 py-2 font-medium">@combo.Name</td>
                        <td class="px-2 py-2">
                            <Badge Text="@(combo.Mode == ComboMode.RoundRobin ? L["combos.mode.roundRobin"] : L["combos.mode.fallback"])"
                                   Variant="@(combo.Mode == ComboMode.RoundRobin ? BadgeVariant.Info : BadgeVariant.Warning)" />
                        </td>
                        <td class="px-2 py-2 opacity-80">@combo.Items.Count</td>
                        <td class="px-2 py-2 text-right">
                            <button type="button" class="btn btn-outline-secondary mr-1" disabled="@_busy"
                                    @onclick="() => OpenEdit(combo)">✏️</button>
                            <button type="button" class="btn btn-outline-danger" disabled="@_busy"
                                    @onclick="() => AskDeleteAsync(combo)">🗑</button>
                        </td>
                    </tr>
                }
            </tbody>
        </table>
    </div>
}

<Modal Visible="_modalVisible"
       Title="@(_editingId is null ? L["combos.form.title.new"] : L["combos.form.title.edit"])"
       OnClose="CloseModal">
    <div class="grid gap-3">
        <label class="flex flex-col gap-1 text-sm">
            @L["combos.form.name"]
            <input class="rounded border border-border bg-surface px-2 py-1.5"
                   maxlength="200" autofocus @bind="_draft.Name" @bind:event="oninput" />
        </label>

        <fieldset class="flex flex-col gap-1 text-sm">
            <legend class="mb-1">@L["combos.form.mode"]</legend>
            <div class="flex gap-4">
                <label class="flex items-center gap-1.5">
                    <input type="radio" name="combo-mode" checked="@(_draft.Mode == ComboMode.RoundRobin)"
                           @onchange="() => _draft.Mode = ComboMode.RoundRobin" />
                    @L["combos.mode.roundRobin"]
                </label>
                <label class="flex items-center gap-1.5">
                    <input type="radio" name="combo-mode" checked="@(_draft.Mode == ComboMode.Fallback)"
                           @onchange="() => _draft.Mode = ComboMode.Fallback" />
                    @L["combos.mode.fallback"]
                </label>
            </div>
        </fieldset>

        <div class="flex flex-col gap-2 text-sm">
            <div class="flex items-center justify-between">
                <span>@L["combos.form.items"]</span>
                <div class="flex gap-2">
                    <button type="button" class="btn btn-outline-secondary"
                            @onclick="() => AddItemRow(ItemKind.Model)">
                        + @L["combos.form.addModelItem"]
                    </button>
                    <button type="button" class="btn btn-outline-secondary"
                            @onclick="() => AddItemRow(ItemKind.Combo)">
                        + @L["combos.form.addComboItem"]
                    </button>
                </div>
            </div>

            @for (var i = 0; i < _rows.Count; i++)
            {
                var index = i;
                var row = _rows[index];
                <div @key="@($"{index}-item")"
                     class="flex items-center gap-2 rounded border border-border bg-background px-2 py-1.5">
                    @if (row.Kind == ItemKind.Model)
                    {
                        @* Đúng spec §4: 1 select, optgroup theo Provider, chỉ hiện model Enabled
                           (giữ model đang chọn dù đã tắt — edit không âm thầm bỏ target) *@
                        <select class="min-w-64 flex-1 rounded border border-border bg-surface px-1.5 py-1"
                                @bind="row.ModelId">
                            <option value="">—</option>
                            @foreach (var provider in _providers)
                            {
                                var models = provider.Models.Where(m => m.Enabled || m.Id == row.ModelId).ToList();
                                if (models.Count == 0)
                                {
                                    continue;
                                }

                                <optgroup label="@provider.Name">
                                    @foreach (var model in models)
                                    {
                                        <option value="@model.Id">@model.ModelId</option>
                                    }
                                </optgroup>
                            }
                        </select>
                    }
                    else
                    {
                        <select class="min-w-64 flex-1 rounded border border-border bg-surface px-1.5 py-1"
                                @bind="row.ComboId">
                            <option value="">—</option>
                            @foreach (var eligible in EligibleCombos)
                            {
                                <option value="@eligible.Id">@eligible.Name</option>
                            }
                        </select>
                    }

                    <div class="ml-auto flex gap-1">
                        <button type="button" class="btn btn-outline-secondary px-1.5" disabled="@(index == 0)"
                                @onclick="() => MoveRow(index, -1)">↑</button>
                        <button type="button" class="btn btn-outline-secondary px-1.5"
                                disabled="@(index == _rows.Count - 1)"
                                @onclick="() => MoveRow(index, 1)">↓</button>
                        <button type="button" class="btn btn-outline-danger px-1.5"
                                @onclick="() => _rows.Remove(row)">✕</button>
                    </div>
                </div>
            }
        </div>
    </div>

    <div class="mt-4 flex justify-end gap-2">
        <button type="button" class="btn btn-outline-secondary" disabled="@(_busy || _saving)"
                @onclick="CloseModal">
            @L["confirm.cancel"]
        </button>
        <button type="button" class="btn btn-primary" disabled="@(!CanSave || _busy || _saving)"
                @onclick="SaveAsync">
            @(_saving ? "..." : L["settings.action.save"])
        </button>
    </div>
</Modal>

@if (_confirmDelete is { } comboToDelete)
{
    <ConfirmDialog Visible="true"
                   Title="@L["providers.action.delete"]"
                   Message="@_deleteMessage"
                   Danger="true"
                   ConfirmText="@L["providers.action.delete"]"
                   CancelText="@L["confirm.cancel"]"
                   OnConfirm="DeleteAsync"
                   OnCancel="CancelDelete" />
}

@code {
    /// <summary>Loại target của dòng item — quyết định select nào hiển thị.</summary>
    public enum ItemKind
    {
        Model,
        Combo,
    }

    /// <summary>Dòng item trong form — state UI.</summary>
    private sealed class ItemRow
    {
        public ItemKind Kind { get; init; }

        public long? ModelId { get; set; }

        public long? ComboId { get; set; }

        /// <summary>Dòng có target hợp lệ — dòng trống bị bỏ qua lúc save (spec §4).</summary>
        public bool HasTarget => Kind == ItemKind.Model ? ModelId is not null : ComboId is not null;
    }

    private List<Combo> _combos = [];
    private List<Provider> _providers = [];
    private bool _loading = true;
    private bool _busy;
    private bool _saving;
    private bool _modalVisible;
    private long? _editingId;
    private ComboDraft _draft = new();
    private List<ItemRow> _rows = [];
    private Combo? _confirmDelete;
    private string? _deleteMessage;

    /// <summary>Chỉ chặn self + tổ tiên; service vẫn là authoritative (spec §4).</summary>
    private IEnumerable<Combo> EligibleCombos =>
        _editingId is { } self ? _combos.Where(c => !IsAncestorOrSelf(c.Id, self)) : _combos;

    protected override async Task OnInitializedAsync()
    {
        L.LanguageChanged += OnLanguageChanged;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _combos = (await ComboSvc.ListAsync()).ToList();
            _providers = (await ProviderSvc.ListAsync()).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Không tải được danh sách combo.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private void OpenAdd()
    {
        _editingId = null;
        _draft = new ComboDraft();
        _rows = [];
        _modalVisible = true;
    }

    private void OpenEdit(Combo combo)
    {
        _editingId = combo.Id;
        _draft = new ComboDraft { Name = combo.Name, Mode = combo.Mode };
        _rows = [];
        foreach (var item in combo.Items.OrderBy(i => i.Position))
        {
            if (item.TargetModelId is { } modelId)
            {
                _rows.Add(new ItemRow { Kind = ItemKind.Model, ModelId = modelId });
            }
            else if (item.TargetComboId is { } childComboId)
            {
                // Target null/null không xảy ra: model bị xóa thì cascade xóa luôn item
                _rows.Add(new ItemRow { Kind = ItemKind.Combo, ComboId = childComboId });
            }
        }

        _modalVisible = true;
    }

    private void CloseModal() => _modalVisible = false;

    private void AddItemRow(ItemKind kind) =>
        _rows.Add(new ItemRow { Kind = kind });

    private void MoveRow(int index, int delta)
    {
        var target = index + delta;
        if (target < 0 || target >= _rows.Count)
        {
            return;
        }

        (_rows[index], _rows[target]) = (_rows[target], _rows[index]);
    }

    private bool IsAncestorOrSelf(long comboId, long self)
    {
        if (comboId == self)
        {
            return true;
        }

        // BFS theo cạnh ngược (chứa → bị chứa): tìm mọi combo đi tới được self
        var ancestors = new HashSet<long>();
        var stack = new Stack<long>();
        stack.Push(self);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var candidate in _combos)
            {
                if (candidate.Items.Any(i => i.TargetComboId == current) && ancestors.Add(candidate.Id))
                {
                    stack.Push(candidate.Id);
                }
            }
        }

        return ancestors.Contains(comboId);
    }

    private bool CanSave =>
        _draft.Name.Trim().Length > 0 && _draft.Name.Length <= 200 && _rows.Any(r => r.HasTarget);

    private async Task SaveAsync()
    {
        if (_busy) return; // Single-flight — double-click vẫn tới được đây khi nút chưa re-render
        if (!CanSave) return; // chặn cứng — nút đã disabled nhưng test chưa pass thì không được lưu

        var items = _rows
            .Where(r => r.HasTarget)
            .Select(r => new ComboItemDraft
            {
                TargetModelId = r.Kind == ItemKind.Model ? r.ModelId : null,
                TargetComboId = r.Kind == ItemKind.Combo ? r.ComboId : null,
            })
            .ToList();
        var draft = new ComboDraft { Name = _draft.Name, Mode = _draft.Mode, Items = items };

        _busy = true;
        _saving = true;
        try
        {
            if (_editingId is { } id)
            {
                await ComboSvc.UpdateAsync(id, draft);
            }
            else
            {
                await ComboSvc.CreateAsync(draft);
            }

            Toast.Show(L["combos.msg.saved"], ToastSeverity.Success);
            CloseModal();
            await LoadAsync();
        }
        catch (ComboValidationException ex)
        {
            // DuplicateName → duplicateName (PascalCase → lowerCamel), spec §4
            var code = ex.Code.ToString();
            Toast.Show(L[$"combos.error.{char.ToLowerInvariant(code[0])}{code[1..]}"], ToastSeverity.Error);
        }
        catch (Exception ex)
        {
            Log.Error("Không lưu được combo.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _saving = false;
            _busy = false;
        }
    }

    private async Task AskDeleteAsync(Combo combo)
    {
        if (_busy) return;
        try
        {
            // Confirm trước với warning nếu có combo khác trỏ tới (spec §4)
            var names = await ComboSvc.GetReferencingComboNamesAsync(combo.Id);
            _deleteMessage = names.Count > 0
                ? $"{L["combos.delete.referenced"]} {string.Join(", ", names)}"
                : L["combos.delete.confirm"];
            _confirmDelete = combo;
        }
        catch (Exception ex)
        {
            Log.Error("Không kiểm tra được tham chiếu combo.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
    }

    private void CancelDelete()
    {
        _confirmDelete = null;
        _deleteMessage = null;
    }

    private async Task DeleteAsync()
    {
        if (_busy || _confirmDelete is not { } combo) return;

        _busy = true;
        try
        {
            await ComboSvc.DeleteAsync(combo.Id);
            Toast.Show(L["combos.msg.deleted"], ToastSeverity.Success);
            CancelDelete();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Không xóa được combo.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void OnLanguageChanged() => _ = InvokeAsync(StateHasChanged);

    public void Dispose() => L.LanguageChanged -= OnLanguageChanged;
}
```

**Kiểm chứng Task 2 (gates):**

```bash
dotnet test "router balancing test/router balancing test.csproj" --nologo   # 147 xanh
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo  # 0W/0E
```

```bash
npm run build   # workdir router-balancing/vite-project — Tailwind sinh class mới (min-w-64, grid gap...)
git add router-balancing/wwwroot/build/
```

- Parity check (controller chạy): đếm key EN == VI == 183.
- Controller self-test CDP (xem Verification).

**Commit:** `feat: add combo CRUD page with i18n and nav link`

**Deliverable:** Report `{"status":"complete","summary":"...","commits":["<sha>"],"testFiles":[],"notes":"..."}`
**Test count giữ nguyên: 147.**

---

## Verification (controller, trước khi merge)

1. `dotnet test` → 147 xanh; `dotnet build` → 0W/0E; parity EN/VI = 183/183.
2. CDP self-test (env `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`, app đã build):
   - **S1:** nav có "Combos"/"Bộ kết hợp" giữa Providers và Logs; switch VI/EN đổi nhãn.
   - **S2:** danh sách rỗng → EmptyState → ＋ Add mở modal "New combo".
   - **S3:** Save disabled khi name rỗng / chưa có dòng có target; ＋ Add model → chọn provider+model → Save → toast "Combo saved." + dòng trong bảng.
   - **S4:** tạo trùng tên → toast `combos.error.duplicateName`.
   - **S5:** tạo combo A (item model); tạo combo B với combo child trỏ A — OK; sửa A thêm dòng trỏ B → toast `combos.error.cycleDetected` (A chứa B chứa A).
   - **S6:** 🗑 combo bị tham chiếu → dialog "Used by: ..."; 🗑 combo trống → confirm thường → toast "Combo deleted."
   - **S7:** ↑↓ đổi thứ tự → lưu → mở lại → thứ tự giữ nguyên.
   - **S8:** switch VI → toàn bộ nhãn modal/table/dialog tiếng Việt.
3. Final whole-branch review + finishing-a-development-branch (SDD).
