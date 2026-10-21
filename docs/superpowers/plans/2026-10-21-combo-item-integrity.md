# Combo Item Integrity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Buộc XOR target và unique `(ComboId, Position)` ở tầng DB cho `ComboItems`, migrate dữ liệu bẩn an toàn, và chặn tên combo chứa `/` để không đụng namespace `<Identifier>/<ModelId>`.

**Architecture:** Giữ một bảng `ComboItems`, thêm CHECK constraint + unique index; migration `AddComboItemIntegrity` chuẩn hoá dòng bẩn trước khi tạo ràng buộc. Thêm một mã lỗi validation `NameContainsSlash` vào `ComboService` (create + update), không đổi thứ tự resolve của `ComboResolver`.

**Tech Stack:** .NET 10, EF Core 10.0.12 + SQLite, xUnit, Blazor Hybrid.

**Spec:** `docs/superpowers/specs/2026-10-21-combo-item-integrity-design.md`

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên trong mọi csproj.
- Migration EF: `dotnet ef migrations add <Name> --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations`.
- Gate mỗi task: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj` (0 warning) + `dotnet test "router balancing test/router balancing test.csproj"`. **Không** dùng `dotnet build router-balancing.slnx` (NETSDK1082 có sẵn từ trước). Build app TFM: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`.
- 1 task = 1 commit, conventional commit tiếng Anh; chỉ stage đúng file của task.
- Comment tiếng Việt cho "tại sao"; XML doc cho public API; exception message và identifier tiếng Anh. Không TODO vô chủ.
- Test dùng `TestDb` (SQLite file tạm, chạy migration thật) + `DbInitializer.Initialize`.
- Flake đã biết: `ProxyQueueIntegrationTests.Stream_LargeUpstreamBody_CompletesWithoutDeadlock`, `ProxyHostTests.*(NonLoopback)` — timeout khi chạy song song, tiền tồn tại. Được chạy lại full suite hoặc `--parallel none`; **cấm** `--filter "…!~…"` để loại test khỏi gate.
- Tên constraint/index đúng nguyên văn: `CK_ComboItems_ExactlyOneTarget`, `IX_ComboItems_ComboId_Position`, `ComboValidationError.NameContainsSlash`, i18n key `combos.error.nameContainsSlash`.

## Review Focus

1. **DB user cũ có dòng bẩn** — app tự `Migrate()` lúc khởi động; migration phải chuẩn hoá trước khi tạo CHECK/unique index, không được ném và khoá app. → Task 1 test `Migration_DirtyRows_NormalizedBeforeConstraints`.
2. **Thứ tự Fallback sau khi đánh số lại** — Position mới phải deterministic theo `(Position, Id)`; test assert đúng thứ tự 0..n-1. → Task 1.
3. **Tên combo có `/` sau khi trim** — `"  a/b  "` phải bị chặn như `"a/b"`. → Task 2 test dùng tên có khoảng trắng.
4. **Combo cũ đang có `/` vẫn sửa được để thoát** — đổi sang tên sạch phải thành công, không dead-end. → Task 2 test `Update_ExistingSlashName_ToCleanName_Succeeds`.
5. **CHECK sống sót qua SQLite table rebuild** — SQLite không `ALTER TABLE ADD CONSTRAINT`; test insert dòng sai sau migration phải bị chặn thật (không chỉ nằm trong model). → Task 1 test both-null/both-set.

---

### Task 0: Commit spec + plan

**Files:**
- Create: `docs/superpowers/specs/2026-10-21-combo-item-integrity-design.md` (đã có)
- Create: `docs/superpowers/plans/2026-10-21-combo-item-integrity.md` (file này)

- [ ] **Step 1: Commit tài liệu**

```bash
git add docs/superpowers/specs/2026-10-21-combo-item-integrity-design.md docs/superpowers/plans/2026-10-21-combo-item-integrity.md
git commit -m "docs: add combo item integrity design and plan"
```

---

### Task 1: CHECK XOR + unique `(ComboId, Position)` + migration chuẩn hoá

**Files:**
- Create: `router balancing test/Combos/ComboItemSchemaTests.cs`
- Modify: `src/RouterBalancing.Core/Storage/RouterBalancingDbContext.cs:90-102`
- Create: `src/RouterBalancing.Core/Storage/Migrations/<timestamp>_AddComboItemIntegrity.cs` + `.Designer.cs` (sinh bằng `dotnet ef`)
- Modify: `src/RouterBalancing.Core/Storage/Migrations/RouterBalancingDbContextModelSnapshot.cs` (sinh tự động)

**Interfaces:**
- Consumes: `TestDb`, `DbInitializer.Initialize`, entity `ComboItem` (`ComboId`, `Position`, `TargetModelId?`, `TargetComboId?`).
- Produces: constraint `CK_ComboItems_ExactlyOneTarget`; unique index `IX_ComboItems_ComboId_Position`; migration id dùng làm mốc downgrade trong test.

- [ ] **Step 1: Viết test schema + migration (đỏ)**

Tạo `router balancing test/Combos/ComboItemSchemaTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Combos;

/// <summary>
/// Ràng buộc DB cho ComboItems (backlog C2): XOR target + unique (ComboId, Position),
/// kèm đường upgrade từ DB cũ có dữ liệu bẩn (app tự migrate lúc khởi động).
/// </summary>
public class ComboItemSchemaTests : IDisposable
{
    private const string PreviousMigration = "20261020120545_DropLegacyModelCapabilityColumns";
    private const string Timestamp = "'2026-10-21T00:00:00+00:00'";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void ItemWithBothTargets_Rejected()
    {
        var factory = InitializeDb();
        var (modelId, comboId) = SeedModelAndCombo(factory, "both");

        using var db = factory.CreateDbContext();
        db.ComboItems.Add(new ComboItem
        {
            ComboId = comboId,
            Position = 0,
            TargetModelId = modelId,
            TargetComboId = comboId,
        });

        Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void ItemWithNoTarget_Rejected()
    {
        var factory = InitializeDb();
        var (_, comboId) = SeedModelAndCombo(factory, "none");

        using var db = factory.CreateDbContext();
        db.ComboItems.Add(new ComboItem { ComboId = comboId, Position = 0 });

        Assert.ThrowsAny<DbUpdateException>(() => db.SaveChanges());
    }

    [Fact]
    public void DuplicatePositionWithinCombo_Rejected()
    {
        var factory = InitializeDb();
        var (modelId, comboId) = SeedModelAndCombo(factory, "dup-position");

        using (var db = factory.CreateDbContext())
        {
            db.ComboItems.Add(new ComboItem { ComboId = comboId, Position = 0, TargetModelId = modelId });
            db.SaveChanges();
        }

        using var db2 = factory.CreateDbContext();
        db2.ComboItems.Add(new ComboItem { ComboId = comboId, Position = 0, TargetModelId = modelId });
        Assert.ThrowsAny<DbUpdateException>(() => db2.SaveChanges());
    }

    [Fact]
    public void SamePositionAcrossDifferentCombos_Allowed()
    {
        var factory = InitializeDb();
        var (modelId, comboA) = SeedModelAndCombo(factory, "combo-a");
        var (_, comboB) = SeedModelAndCombo(factory, "combo-b");

        using var db = factory.CreateDbContext();
        db.ComboItems.AddRange(
            new ComboItem { ComboId = comboA, Position = 0, TargetModelId = modelId },
            new ComboItem { ComboId = comboB, Position = 0, TargetModelId = modelId });
        db.SaveChanges();

        Assert.Equal(2, db.ComboItems.Count());
    }

    /// <summary>
    /// DB cũ có thể chứa dòng do import/sửa tay: cả hai NULL (vô nghĩa), cả hai set,
    /// và trùng Position. Migration phải dọn trước khi tạo CHECK/unique index, nếu không
    /// app không khởi động được.
    /// </summary>
    [Fact]
    public void Migration_DirtyRows_NormalizedBeforeConstraints()
    {
        var factory = _testDb.CreateFactory();
        using var db = factory.CreateDbContext();
        db.Database.Migrate(PreviousMigration);

        var provider = new Provider
        {
            Name = "migration-provider",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
            Models = { new Model { ModelId = "m1" } },
        };
        var comboA = new Combo { Name = "combo-a" };
        var comboB = new Combo { Name = "combo-b" };
        var comboC = new Combo { Name = "combo-c" };
        db.Providers.Add(provider);
        db.Combos.AddRange(comboA, comboB, comboC);
        db.SaveChanges();

        var modelId = provider.Models[0].Id;
        db.ComboItems.AddRange(
            new ComboItem { ComboId = comboA.Id, Position = 0, TargetModelId = modelId },
            new ComboItem { ComboId = comboA.Id, Position = 0, TargetModelId = modelId },
            new ComboItem { ComboId = comboB.Id, Position = 0 },
            new ComboItem
            {
                ComboId = comboC.Id,
                Position = 0,
                TargetModelId = modelId,
                TargetComboId = comboA.Id,
            });
        db.SaveChanges();

        db.Database.Migrate();

        var items = db.ComboItems.AsNoTracking().ToList();
        Assert.DoesNotContain(items, i => i.TargetModelId is null && i.TargetComboId is null);
        Assert.DoesNotContain(items, i => i.TargetModelId is not null && i.TargetComboId is not null);
        Assert.Equal(new[] { 0, 1 }, db.ComboItems.AsNoTracking()
            .Where(i => i.ComboId == comboA.Id).OrderBy(i => i.Position)
            .Select(i => i.Position).ToList());
        var kept = db.ComboItems.AsNoTracking().Single(i => i.ComboId == comboC.Id);
        Assert.Equal(modelId, kept.TargetModelId);
        Assert.Null(kept.TargetComboId);
    }

    private IDbContextFactory<RouterBalancingDbContext> InitializeDb()
    {
        var factory = _testDb.CreateFactory();
        DbInitializer.Initialize(factory);
        return factory;
    }

    private static (long ModelId, long ComboId) SeedModelAndCombo(
        IDbContextFactory<RouterBalancingDbContext> factory, string comboName)
    {
        using var db = factory.CreateDbContext();
        var provider = new Provider
        {
            Name = $"P-{comboName}",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
            Models = { new Model { ModelId = "m1" } },
        };
        var combo = new Combo { Name = comboName };
        db.Providers.Add(provider);
        db.Combos.Add(combo);
        db.SaveChanges();
        return (provider.Models[0].Id, combo.Id);
    }
}
```

- [ ] **Step 2: Chạy test để xác nhận đỏ**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ComboItemSchemaTests"
```

Expected: FAIL — 4 test ràng buộc insert được (không ném), test migration ném vì DB chưa có migration mới (hoặc assertion vị trí sai).

- [ ] **Step 3: Cập nhật mapping trong `RouterBalancingDbContext`**

Thay khối `ComboItem` (dòng 90-102) bằng:

```csharp
        modelBuilder.Entity<ComboItem>(e =>
        {
            // WithMany(c => c.Items): map đúng navigation của Combo — nếu dùng WithMany()
            // EF sẽ tạo shadow FK thứ hai (ComboId1) song song với ComboId.
            e.HasOne<Combo>().WithMany(c => c.Items).HasForeignKey(x => x.ComboId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Model>().WithMany().HasForeignKey(x => x.TargetModelId)
                .OnDelete(DeleteBehavior.Cascade);
            // Restrict: app tự kiểm tra combo đang được tham chiếu trước khi xóa
            e.HasOne<Combo>().WithMany().HasForeignKey(x => x.TargetComboId)
                .OnDelete(DeleteBehavior.Restrict);
            // Đúng một target: chặn dòng vô nghĩa/cả hai target ở tầng DB, không chỉ service.
            e.ToTable(t => t.HasCheckConstraint(
                "CK_ComboItems_ExactlyOneTarget",
                "(TargetModelId IS NULL) <> (TargetComboId IS NULL)"));
            // Fallback phụ thuộc thứ tự → Position phải duy nhất trong một combo.
            e.HasIndex(x => new { x.ComboId, x.Position }).IsUnique();
        });
```

- [ ] **Step 4: Sinh migration**

```bash
dotnet ef migrations add AddComboItemIntegrity --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations
```

Mở file migration vừa sinh. Nếu EF sinh `AddCheckConstraint` + `DropIndex`/`CreateIndex` thì giữ nguyên thứ tự; nếu provider SQLite báo operation không hỗ trợ khi chạy test, thay `Up` bằng rebuild thủ công: tạo `ComboItems_new` đầy đủ cột/FK/CHECK, `INSERT … SELECT`, `DROP TABLE ComboItems`, `ALTER TABLE ComboItems_new RENAME TO ComboItems`, tạo lại unique index; `Down` làm ngược (bỏ CHECK, giữ dữ liệu).

- [ ] **Step 5: Chèn SQL chuẩn hoá vào đầu `Up` (trước mọi operation)**

```csharp
            // App tự migrate lúc khởi động: DB cũ có dòng bẩn không được làm sập.
            // 1) Dòng không target: không mang nghĩa (service luôn từ chối) → xoá.
            // 2) Cả hai target: giữ model, bỏ combo (combo lồng là shape thêm sau,
            //    và model không thể tạo chu trình).
            // 3) Trùng Position: đánh số lại 0..n-1 theo (Position, Id) — giữ thứ tự Fallback.
            migrationBuilder.Sql("""
                DELETE FROM ComboItems
                WHERE TargetModelId IS NULL AND TargetComboId IS NULL;

                UPDATE ComboItems
                SET TargetComboId = NULL
                WHERE TargetModelId IS NOT NULL AND TargetComboId IS NOT NULL;

                CREATE TEMP TABLE _combo_item_positions AS
                SELECT Id,
                       ROW_NUMBER() OVER (PARTITION BY ComboId ORDER BY Position, Id) - 1 AS NewPosition
                FROM ComboItems;

                UPDATE ComboItems
                SET Position = (SELECT NewPosition FROM _combo_item_positions WHERE Id = ComboItems.Id);

                DROP TABLE _combo_item_positions;
                """);
```

Thêm XML doc cho class migration: chuẩn hoá dữ liệu là một chiều, `Down` chỉ bỏ ràng buộc (không phục hồi dòng đã xoá/đổi), theo pattern `DropLegacyModelCapabilityColumns`.

- [ ] **Step 6: Chạy lại test + full gate**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ComboItemSchemaTests"
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: PASS, 0 warning, full suite xanh.

- [ ] **Step 7: Commit**

```bash
git add "router balancing test/Combos/ComboItemSchemaTests.cs" src/RouterBalancing.Core/Storage/RouterBalancingDbContext.cs src/RouterBalancing.Core/Storage/Migrations/
git commit -m "feat: enforce one combo item target and unique position in the database"
```

---

### Task 2: Chặn tên combo chứa `/` (D21)

**Files:**
- Modify: `src/RouterBalancing.Core/Combos/ComboValidationException.cs:4-12`
- Modify: `src/RouterBalancing.Core/Combos/ComboService.cs:112-120`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (EN + VI, cạnh các key `combos.error.*`)
- Test: `router balancing test/Combos/ComboServiceTests.cs`

**Interfaces:**
- Consumes: `ComboValidationError` enum, `ComboService.Draft`/`SeedModelAsync`/`SeedComboAsync` helper trong test hiện có.
- Produces: `ComboValidationError.NameContainsSlash`; i18n `combos.error.nameContainsSlash` (EN/VI).

- [ ] **Step 1: Viết test (đỏ)**

Thêm vào `ComboServiceTests` (sau test `Create_WhenNameTooLong_ThrowsNameTooLong`):

```csharp
    [Theory]
    [InlineData("openai/gpt-4o")]
    [InlineData("  openai/gpt-4o  ")]
    public async Task Create_WhenNameContainsSlash_ThrowsNameContainsSlash(string name)
    {
        var modelId = await SeedModelAsync();

        var ex = await Assert.ThrowsAsync<ComboValidationException>(() => _service.CreateAsync(
            Draft(name, new ComboItemDraft { TargetModelId = modelId })));

        Assert.Equal(ComboValidationError.NameContainsSlash, ex.Code);
    }

    [Fact]
    public async Task Update_WhenNameContainsSlash_ThrowsNameContainsSlash()
    {
        var modelId = await SeedModelAsync();
        var comboId = await SeedComboAsync("clean", targetModelId: modelId);

        var ex = await Assert.ThrowsAsync<ComboValidationException>(() => _service.UpdateAsync(
            comboId,
            Draft("bad/name", new ComboItemDraft { TargetModelId = modelId })));

        Assert.Equal(ComboValidationError.NameContainsSlash, ex.Code);
    }

    /// <summary>Combo cũ đang có '/' phải sửa được sang tên sạch — không dead-end.</summary>
    [Fact]
    public async Task Update_ExistingSlashName_ToCleanName_Succeeds()
    {
        var modelId = await SeedModelAsync();
        var comboId = await SeedComboAsync("old/name", targetModelId: modelId);

        var updated = await _service.UpdateAsync(
            comboId,
            Draft("clean-name", new ComboItemDraft { TargetModelId = modelId }));

        Assert.Equal("clean-name", updated.Name);
    }
```

- [ ] **Step 2: Chạy test để xác nhận đỏ**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ComboServiceTests"
```

Expected: FAIL — `NameContainsSlash` chưa tồn tại (compile error là red hợp lệ).

- [ ] **Step 3: Thêm mã lỗi + validation + i18n**

`ComboValidationException.cs` — thêm cuối enum:

```csharp
    NameContainsSlash,
```

`ComboService.ValidateAsync` — ngay sau khối `NameTooLong`:

```csharp
        // D21: tên combo không được đụng namespace model "<Identifier>/<ModelId>" —
        // ComboResolver thử model trước combo nên tên có '/' có thể bị che hoàn toàn.
        if (name.Contains('/'))
        {
            throw new ComboValidationException(ComboValidationError.NameContainsSlash,
                "Combo names cannot contain '/'.");
        }
```

`Translations.cs` — thêm cạnh các key `combos.error.*` hiện có:

```csharp
        ["combos.error.nameContainsSlash"] = "Combo names cannot contain '/'.",
```

```csharp
        ["combos.error.nameContainsSlash"] = "Tên bộ kết hợp không được chứa '/'.",
```

- [ ] **Step 4: Chạy test + full gate**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ComboServiceTests"
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: PASS (bao gồm `TranslationParityTests`), 0 warning, full suite xanh.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Combos/ComboValidationException.cs src/RouterBalancing.Core/Combos/ComboService.cs src/RouterBalancing.Core/Localization/Translations.cs "router balancing test/Combos/ComboServiceTests.cs"
git commit -m "feat: reject combo names that collide with provider-scoped model ids"
```

---

### Task 3: Cập nhật backlog + nghiệm thu

**Files:**
- Modify: `docs/backlog.md`
- Modify: `docs/superpowers/specs/2026-10-21-combo-item-integrity-design.md:3` (trạng thái)
- Modify: `docs/superpowers/specs/2026-10-19-capability-model-and-tools-round-2-design.md` (dòng trạng thái: thêm ghi chú C2/D21 đã xong)

- [ ] **Step 1: Dọn backlog theo thực tế**

- Tick `[x]` cho B1–B5 (đã xong ở Plan 1 — fact tables, metadata provider theo nguồn, unique `Providers.Name`, `ApiKeyEncrypted` nullable). Ghi chú ngắn cạnh B: "B1–B5 xong ở capability model (PR #31); B6 còn lại".
- Tick `[x]` cho C1, C2; ghi ngày 2026-10-21 và PR/branch tương ứng.
- Tick `[x]` cho D21, ghi cách xử lý: chặn `/` ở create/update; combo cũ không tự đổi tên.
- Cập nhật tiêu đề: `# Backlog — cập nhật 2026-10-21`.

- [ ] **Step 2: Cập nhật trạng thái spec**

`2026-10-21-combo-item-integrity-design.md`: `**Trạng thái:** Implemented (2026-10-21)`. Spec capability-model: thêm câu "C2/D21 đã xong 2026-10-21 (migration AddComboItemIntegrity)".

- [ ] **Step 3: Gate cuối**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
git status --short
```

Expected: core build 0 warning; full suite xanh (flakes đã biết được chạy lại); app TFM xanh; chỉ các file docs đang chờ commit.

- [ ] **Step 4: Commit**

```bash
git add docs/backlog.md docs/superpowers/specs/2026-10-21-combo-item-integrity-design.md docs/superpowers/specs/2026-10-19-capability-model-and-tools-round-2-design.md
git commit -m "docs: mark combo item integrity and backlog items complete"
```

---

## Ghi chú cho người thực thi

- Không sửa `src/RouterBalancing.Core/Engine/ComboResolver.cs` — thứ tự resolve giữ nguyên (model trước combo).
- Không thêm i18n key nào khác ngoài `combos.error.nameContainsSlash`.
- Nếu migration SQLite cần rebuild bảng, kiểm tra lại rằng `IX_ComboItems_ComboId_Position` vẫn **unique** và FK `TargetComboId` vẫn `RESTRICT` sau rebuild (test `Migration_DirtyRows...` + `ComboService` tests là lưới an toàn).
