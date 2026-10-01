# Provider Free (preset catalog 4 provider + sync model free) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Seed 4 nhà cung cấp LLM free vào DB lúc khởi động (preset, không xoá được, `Enabled=false`), hiển thị badge "Free" trong tab Providers, và đồng bộ danh sách model free của từng preset (nền mỗi 6h + nút manual) — spec: `docs/superpowers/specs/2026-10-01-provider-free-design.md`.

**Architecture:** Catalog hardcode trong `FreeProviderCatalog` (4 entry `{DisplayName, BaseUrl, DetectKind}`); `DbInitializer` seed 1 lần khi khởi động (insert-if-no-preset); `FreeModelSyncService` fetch `GET {base}/v1/models` qua `ProviderRequestFactory` + named client 30s, parse/free-detect bằng pure function `FreeModelDetector`, merge vào bảng `Model` dùng lại cột `IsManual` (manual giữ vĩnh viễn, fetched biến mất thì xoá); `FreeModelSyncWorker` chạy nền `PeriodicTimer` (trễ 60s lần đầu, lặp 6h) theo pattern `LogRetentionWorker`; UI badge + nút sync trong `Providers.razor`.

**Tech Stack:** .NET 10 / EF Core 10 (SQLite, auto-migration), xUnit.

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên; TFM app: `net10.0-windows10.0.19041.0`.
- Comment tiếng Việt cho "tại sao", XML doc cho public API; không thêm comment thừa / TODO vô chủ.
- **Exception message tiếng Anh** (consistent với `Provider {id} not found.` hiện có); comment giải thích tiếng Việt.
- i18n: mọi key mới phải có trong **cả 2 dict** `English` và `Vietnamese` của `Translations.cs` (parity test tự bắt).
- 1 task = 1 commit; commit message tiếng Anh conventional (`feat:`/`docs:`).
- Không nuốt exception — chỉ 2 chỗ plan nêu rõ best-effort được catch kèm comment (worker log-gone-bad, y hệt `LogRetentionWorker`).
- Razor: UTF-8 trực tiếp, **không** HTML entity (guard `RazorParameterEntityTests`).
- App `router-balancing` phải **đóng** trước khi build TFM Windows / chạy test (SingleInstanceGuard giữ mutex).
- Gates chuẩn (mỗi task, trước commit):
  - `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj`
  - `dotnet test "router balancing test/router balancing test.csproj"`
  - Task đụng UI/MauiProgram/App.xaml.cs: thêm `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`
  - **Không** dùng `dotnet build router-balancing.slnx` làm gate (6 lỗi pre-existing NETSDK1082 + DLL lock).
- Test flake đã biết: `ProxyControlApiTests` teardown IOException, `ProxyRetryIntegrationTests` fuse-open 503-vs-429, `ProxyQueueIntegrationTests` teardown → chạy lại PASS; test fail do app đang mở → đóng app, chạy lại.
- Migration command chuẩn:
  ```powershell
  dotnet ef migrations add AddFreeProviderPresets --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations
  ```

## File Structure

| Hành động | File | Trách nhiệm |
|---|---|---|
| Modify | `src/RouterBalancing.Core/Domain/Entities/Provider.cs` | + `IsPreset`, `LastModelSyncAt` |
| Create | `src/RouterBalancing.Core/Providers/FreeProviderCatalog.cs` | `FreeDetectKind` + catalog 4 entry + `FindByDisplayName` |
| Create | (dotnet-ef) `Storage/Migrations/*_AddFreeProviderPresets.cs` | 2 cột mới trên `Providers` |
| Modify | `src/RouterBalancing.Core/Storage/DbInitializer.cs` | `SeedFreeProviders` (gọi sau `Migrate`, trước `BackfillIdentifiers`) |
| Create | `src/RouterBalancing.Core/Providers/FreeModelSyncException.cs` | Lỗi sync phân biệt được với lỗi hệ thống |
| Create | `src/RouterBalancing.Core/Providers/FreeModelDetector.cs` | `FreeModelInfo` + parse/filter pure function |
| Create | `src/RouterBalancing.Core/Providers/IFreeModelSyncService.cs` + `FreeModelSyncService.cs` | Fetch + merge + guard |
| Create | `src/RouterBalancing.Core/Providers/FreeModelSyncWorker.cs` | Nền 6h (trễ 60s lần đầu) |
| Modify | `src/RouterBalancing.Core/Providers/ProviderService.cs` + `IProviderService.cs` | Guard `DeleteAsync` + XML doc |
| Modify | `router-balancing/MauiProgram.cs` | DI service + worker + named client 30s |
| Modify | `router-balancing/App.xaml.cs` | Start/Dispose worker |
| Modify | `router-balancing/Components/Pages/Providers.razor` | Badge Free, last-sync, nút sync, ẩn delete |
| Modify | `src/RouterBalancing.Core/Localization/Translations.cs` | + 6 key ×2 dict |
| Create | Tests: `Providers/FreeModelFixtures.cs`, `Providers/FreeModelDetectorTests.cs`, `Providers/FreeModelSyncServiceTests.cs`, `Providers/FreeModelSyncMergeTests.cs`, `Providers/FreeModelSyncWorkerTests.cs`, `Providers/ProviderServiceDeletePresetTests.cs`, `Storage/DbInitializerFreeProviderTests.cs` | Test mới |
| Modify | Tests: `Providers/ProviderServiceTests.cs`, `Providers/ProviderTestConnectionTests.cs`, `Storage/DbInitializerTests.cs` | Sửa assert vỡ do seed 4 preset (xem Task 1) |

## Design decisions locked in this plan (spec gap → plan resolve)

1. **Seed guard = `db.Providers.Any(p => p.IsPreset)` (Option B)** thay vì per-entry Name-check của spec §6.4 — sửa xung đột §6.4 ("match theo Name") vs §13 ("đổi tên 1 cái → không nhân đôi"): match theo Name sẽ re-insert khi user rename. Preset không xoá được (D6) nên `IsPreset` count không bao giờ giảm → seed đúng 1 lần đời DB; hàng đã user-sửa (tên/URL) không bao giờ đụng tới (§6.4 "không bao giờ update/xoá").
2. **Catalog record `{DisplayName, BaseUrl, DetectKind}`** — bỏ `Key` (match theo Name là đủ, chốt S1), bỏ `ModelsEndpoint` (URL trùng bảng §4: `ProviderRequestFactory.Create` + `ProviderUrl.Canonicalize(BaseUrl)` + `/v1/models` → vd `https://openrouter.ai/api/v1/models`), bỏ `NeedsKey` (cả 4 endpoint public, key gắn khi user thêm account).
3. **`FreeDetectKind` nằm trong file catalog** (`FreeProviderCatalog.cs`) vì catalog (Task 1) tham chiếu nó trước khi detector (Task 2) tồn tại; detector dùng lại enum.
4. **Detector = static class** (không `IFreeModelDetector` như sketch §6.3) — pure function, test trực tiếp JSON fixture, không cần DI/mock.
5. **Manual sync gọi thẳng `IFreeModelSyncService.SyncProviderAsync`** từ UI — bỏ `SyncNowAsync`/`SyncAllNowAsync` trên worker (sketch §7): service đã là singleton, worker chỉ lo periodic → ít tầng hơn cùng hành vi.
6. **Named client riêng `"free-model-sync"` timeout 30s** (spec §6.1) — không dùng `provider-probe` (10s, quá ngắn cho list model lớn).
7. **Worker trễ `initialDelay` (60s) TRƯỚC tick đầu** (§7) — khác `LogRetentionWorker` (purge ngay): app mới mở còn nặng, tránh đụng DB/network lúc startup. `initialDelay` inject được (test truyền `TimeSpan.Zero`).
8. **Không gọi `IModelMetadataService` sau sync** — spec §6 chỉ map `DisplayName`/`ContextWindow` từ response models API; capabilities (vision/think) deferred (spec §14 out of scope).
9. **Spec §13 "seed đủ 5" = typo của 4** (catalog có 4 provider, đã chốt trong spec §4) — plan và test đều là 4.
10. **`FreeModelSyncIntegrationTests` (§13) gộp vào `FreeModelSyncMergeTests`** — cùng scenario "sync 2 lần với fixture đổi", fake HTTP qua `HttpMessageHandler` (không cần server thật; pattern `JsonHandler`/`StubFactory` của `ModelServiceTests`).
11. **4 test hiện có phải sửa** do seed thêm 4 preset (đã scan toàn bộ — đây là toàn bộ blast radius):
    - `ProviderServiceTests.ListAsync_WhenProvidersExist_IncludesModels` + `ListAsync_IncludesAccounts`: `Assert.Single(list)` → pick theo Name.
    - `ProviderTestConnectionTests.TestConnection_WhenNotSaved_DoesNotPersist`: `CountAsync()==0` → chỉ đếm hàng không-preset.
    - `DbInitializerTests.Initialize_BackfillsIdentifier_*` (2 test): query `db.Providers` → filter `!p.IsPreset` (test index theo thứ tự Id).

---

### Task 0: Commit the plan

**Files:**
- Create: `docs/superpowers/plans/2026-10-01-provider-free.md` (file này)

**Interfaces:**
- Consumes: —
- Produces: plan file để SDD dispatch theo task number.

- [ ] **Step 1: Verify spec đã commit, tree chỉ có plan file**

Run:
```powershell
git log --oneline -1   # kỳ vọng: 54b43c7 docs: scope provider free spec to four initial providers
git status --short     # chỉ thấy ?? docs/superpowers/plans/2026-10-01-provider-free.md
```
Expected: HEAD = `54b43c7`; chỉ có plan file mới.

- [ ] **Step 2: Commit**

```powershell
git add docs/superpowers/plans/2026-10-01-provider-free.md
git commit -m "docs: add provider free implementation plan"
```

---

### Task 1: Provider columns + FreeProviderCatalog + seed + migration + sửa test vỡ

**Files:**
- Modify: `src/RouterBalancing.Core/Domain/Entities/Provider.cs`
- Create: `src/RouterBalancing.Core/Providers/FreeProviderCatalog.cs`
- Modify: `src/RouterBalancing.Core/Storage/DbInitializer.cs`
- Create (dotnet-ef): `src/RouterBalancing.Core/Storage/Migrations/<ts>_AddFreeProviderPresets.cs` + `.Designer.cs` + snapshot
- Modify (test fix): `router balancing test/Providers/ProviderServiceTests.cs`, `router balancing test/Providers/ProviderTestConnectionTests.cs`, `router balancing test/Storage/DbInitializerTests.cs`
- Create (test): `router balancing test/Storage/DbInitializerFreeProviderTests.cs`

**Interfaces:**
- Consumes: `DbInitializer.Initialize` flow (`Migrate → BackfillIdentifiers → MigrateLegacyApiKey`), `BackfillIdentifiers` (slugify Identifier cho hàng Identifier NULL).
- Produces (Task 2+): cột `Provider.IsPreset`/`Provider.LastModelSyncAt`, `FreeProviderCatalog.Entries`/`FindByDisplayName`, seed 4 preset `Enabled=false`.

- [ ] **Step 1: Thêm 2 cột vào `Provider`**

Sau `public DateTimeOffset? LastTestAt { get; set; }` (hoặc sau `LastTestMessage`) trong `Domain/Entities/Provider.cs`, thêm:

```csharp
    /// <summary>Provider preset từ free catalog — seed lúc khởi động, không cho xóa (spec provider-free §5.1).</summary>
    public bool IsPreset { get; set; }

    /// <summary>Lần sync model free gần nhất — null = chưa sync (tách khỏi LastTestAt: 2 việc khác nhau).</summary>
    public DateTimeOffset? LastModelSyncAt { get; set; }
```

- [ ] **Step 2: Tạo `FreeProviderCatalog.cs`**

Tạo `src/RouterBalancing.Core/Providers/FreeProviderCatalog.cs`:

```csharp
namespace RouterBalancing.Core.Providers;

/// <summary>Cách nhận diện model free trong response list của từng provider (spec provider-free §6.3).</summary>
public enum FreeDetectKind
{
    /// <summary>pricing parse về decimal == 0 ("0", "0.0") HOẶC id endswith ":free" (OpenRouter).</summary>
    PricingZeroOrFreeSuffix,

    /// <summary>id endswith "-free" (OpenCode Zen — response không có pricing).</summary>
    FreeSuffix,

    /// <summary>Mọi model trong response đều free (NVIDIA NIM, Ollama Cloud — không có field pricing).</summary>
    AllFree,
}

/// <summary>
/// Catalog 4 provider free preset — hardcode trong code theo spec §2 (không config ngoài file).
/// BaseUrl canonical KHÔNG kèm /v1: ProviderRequestFactory ghép "/v1/models" +
/// ProviderUrl.Canonicalize chống "/v1/v1" (spec §4 — bảng probe thật 2026-10-01).
/// </summary>
public static class FreeProviderCatalog
{
    /// <summary>1 preset free: tên hiển thị (đồng thời là khóa match), gốc URL, cách detect free.</summary>
    public sealed record Entry(string DisplayName, string BaseUrl, FreeDetectKind DetectKind);

    /// <summary>4 preset seed lúc khởi động — thứ tự = thứ tự insert (spec §4).</summary>
    public static readonly IReadOnlyList<Entry> Entries =
    [
        new("OpenCode Free", "https://opencode.ai/zen", FreeDetectKind.FreeSuffix),
        new("OpenRouter Free", "https://openrouter.ai/api", FreeDetectKind.PricingZeroOrFreeSuffix),
        new("NVIDIA NIM Free", "https://integrate.api.nvidia.com", FreeDetectKind.AllFree),
        new("Ollama Cloud Free", "https://ollama.com", FreeDetectKind.AllFree),
    ];

    /// <summary>
    /// Tra catalog theo Name — user đổi tên preset thì mất link sync (chốt S1, spec §6.2.2);
    /// service ném FreeModelSyncException, periodic log warning bỏ qua.
    /// </summary>
    /// <returns><see langword="null"/> nếu không match — user đã đổi tên preset.</returns>
    public static Entry? FindByDisplayName(string displayName) =>
        Entries.FirstOrDefault(e => string.Equals(e.DisplayName, displayName, StringComparison.Ordinal));
}
```

- [ ] **Step 3: Chạy migration**

```powershell
dotnet ef migrations add AddFreeProviderPresets --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations
```

Expected: file `<ts>_AddFreeProviderPresets.cs` chứa đúng 2 `AddColumn`:
- `Providers.IsPreset` → `BOOLEAN NOT NULL DEFAULT 0` (EF tự thêm default cho cột non-nullable trên SQLite — kiểm tra, nếu thiếu thêm tay `defaultValue: false`),
- `Providers.LastModelSyncAt` → `DATETIMEOFFSET NULL`,
và `Down()` drop cả 2; snapshot cập nhật.

- [ ] **Step 4: Seed trong `DbInitializer`**

Sửa using đầu file — thêm:

```csharp
using RouterBalancing.Core.Providers;
```

Trong `Initialize(IDbContextFactory<RouterBalancingDbContext> factory, ISecretProtector? legacyKeyProtector = null)` — chèn `SeedFreeProviders(db);` giữa `Migrate` và `BackfillIdentifiers` (seed trước để Identifier được slugify ngay lần đầu):

```csharp
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        SeedFreeProviders(db);
        BackfillIdentifiers(db);
        MigrateLegacyApiKey(db, legacyKeyProtector);
```

Thêm method (đặt cạnh `BackfillIdentifiers`):

```csharp
    /// <summary>
    /// Seed 4 provider free preset (spec provider-free §6.4) — chạy đúng 1 lần cho đời sống DB:
    /// guard theo "DB đã có hàng preset nào chưa" thay vì match từng Name, để hàng user đã
    /// đổi tên (§13: không nhân đôi) hay sửa URL không bao giờ bị đụng tới/ghé lại.
    /// Chạy trước BackfillIdentifiers để Identifier được slugify từ Name ngay lần đầu.
    /// </summary>
    private static void SeedFreeProviders(RouterBalancingDbContext db)
    {
        if (db.Providers.Any(p => p.IsPreset)) return;

        foreach (var entry in FreeProviderCatalog.Entries)
        {
            db.Providers.Add(new Provider
            {
                Name = entry.DisplayName,
                BaseUrl = entry.BaseUrl,
                Type = ProviderType.OpenAI, // cả 4 endpoint đều OpenAI-compatible (§4)
                IsPreset = true,
                Enabled = false, // D9: user tự bật — 4 provider lạ không được tự nhận traffic
                MaxConcurrent = 4,
            });
        }
        db.SaveChanges();
    }
```

(`CreatedAt`/`UpdatedAt` có initializer `= DateTimeOffset.UtcNow` trong entity — không set tay.)

- [ ] **Step 5: Chạy test hiện có → xác nhận đúng 4 test vỡ**

```powershell
dotnet test "router balancing test/router balancing test.csproj"
```

Expected (trước khi fix): fail đúng các test sau (đã scan toàn bộ repo — nếu xuất hiện test fail khác STOP, xem lại):
- `ProviderServiceTests.ListAsync_WhenProvidersExist_IncludesModels`
- `ProviderServiceTests.ListAsync_IncludesAccounts`
- `ProviderTestConnectionTests.TestConnection_WhenNotSaved_DoesNotPersist`
- `DbInitializerTests.Initialize_BackfillsIdentifier_SlugifiesDedupesAndFallsBack`
- `DbInitializerTests.Initialize_BackfillsIdentifier_CapsAt50KeepsSlugShapeAndStaysIdempotent`

- [ ] **Step 6: Fix 3 file test hiện có**

**(a)** `ProviderServiceTests.cs` — 2 assert `Assert.Single(list)` giờ list có cả 4 preset. Thay từng chỗ (test line ~119 và ~130):

```csharp
        // List giờ có cả 4 preset seed — pick đúng hàng test tạo thay vì Single(list)
        var loaded = Assert.Single(list, p => p.Name == "OpenAI");
```

(giữ nguyên dòng assert thứ 2 của mỗi test: `Assert.Equal("gpt-4o", Assert.Single(loaded.Models).ModelId);` / `Assert.Equal("Default", Assert.Single(loaded.Accounts).Name);`)

**(b)** `ProviderTestConnectionTests.cs` line ~183 — intent: provider *unsaved* không được persist; giờ DB có 4 preset:

```csharp
        // 4 preset free đã seed — chỉ đếm hàng user-created để giữ intent "unsaved không persist"
        Assert.Equal(0, await db.Providers.CountAsync(p => !p.IsPreset));
```

**(c)** `DbInitializerTests.cs` — 2 test backfill index theo Id trên TẤT CẢ providers; filter hàng preset để index trỏ đúng 3 hàng test tạo (test row có Id lớn hơn preset). Áp dụng 4 chỗ:

Line ~105 và ~145 (`var providers = db.Providers.OrderBy(p => p.Id).ToList();`):

```csharp
            // Seed thêm 4 preset (Id nhỏ hơn hàng test) — loại ra để index [0..2] trỏ đúng hàng test
            var providers = db.Providers.Where(p => !p.IsPreset).OrderBy(p => p.Id).ToList();
```

Line ~116 và ~162 (`db.Providers.OrderBy(p => p.Id).First().Identifier`):

```csharp
                db.Providers.Where(p => !p.IsPreset).OrderBy(p => p.Id).First().Identifier
```

Không đổi `foreach (var p in db.Providers) p.Identifier = null;` (line ~97/~137): nó cũng null Identifier của preset → lần `Initialize` sau backfill lại đúng slug cũ (Name không đổi → slug không đổi) — vô hại, giữ test như là "hàng cũ trước backfill".

- [ ] **Step 7: Test mới `DbInitializerFreeProviderTests`**

Tạo `router balancing test/Storage/DbInitializerFreeProviderTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

public class DbInitializerFreeProviderTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Initialize_FreshDb_SeedsFourPresetsDisabledWithNoModels()
    {
        var factory = _db.CreateFactory();

        DbInitializer.Initialize(factory);

        using var db = factory.CreateDbContext();
        var presets = db.Providers.AsNoTracking().Where(p => p.IsPreset).ToList();
        Assert.Equal(4, presets.Count);
        Assert.Equal(
            FreeProviderCatalog.Entries.Select(e => e.DisplayName).OrderBy(x => x, StringComparer.Ordinal),
            presets.Select(p => p.Name).OrderBy(x => x, StringComparer.Ordinal));
        Assert.All(presets, p =>
        {
            Assert.False(p.Enabled); // D9: user tự bật
            Assert.Equal(ProviderType.OpenAI, p.Type);
            Assert.False(string.IsNullOrEmpty(p.Identifier)); // seed trước Backfill → slugify ngay
            Assert.Empty(p.Models); // chờ sync đầu tiên
            Assert.Null(p.LastModelSyncAt);
        });
    }

    [Fact]
    public void Initialize_ExistingUserProviders_KeepsThemAndSeedsPresets()
    {
        var factory = _db.CreateFactory();
        // Upgrade path: DB cũ đã có provider user, chưa có preset nào
        using (var db = factory.CreateDbContext())
        {
            db.Database.Migrate();
            db.Providers.Add(new Provider
            {
                Name = "My OpenAI",
                BaseUrl = "https://api.openai.com",
                Type = ProviderType.OpenAI,
            });
            db.SaveChanges();
        }

        DbInitializer.Initialize(factory);

        using var db2 = factory.CreateDbContext();
        Assert.Equal(4, db2.Providers.AsNoTracking().Count(p => p.IsPreset));
        var userRow = Assert.Single(db2.Providers.AsNoTracking(), p => p.Name == "My OpenAI");
        Assert.False(userRow.IsPreset); // hàng user không bị đánh dấu preset
    }

    [Fact]
    public void Initialize_RenamedPreset_NotReSeededNoDuplicate()
    {
        var factory = _db.CreateFactory();
        DbInitializer.Initialize(factory);
        using (var db = factory.CreateDbContext())
        {
            var preset = db.Providers.Single(p => p.Name == "OpenRouter Free");
            preset.Name = "My Router";
            db.SaveChanges();
        }

        DbInitializer.Initialize(factory); // lần 2 — guard Any(IsPreset) phải skip

        using var db2 = factory.CreateDbContext();
        Assert.Equal(4, db2.Providers.AsNoTracking().Count(p => p.IsPreset));
        Assert.Contains(db2.Providers.AsNoTracking(), p => p.Name == "My Router");
        Assert.DoesNotContain(db2.Providers.AsNoTracking(), p => p.Name == "OpenRouter Free");
    }
}
```

- [ ] **Step 8: Gates + Commit**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: build 0 lỗi; test **tất cả pass** (419 baseline + 3 mới = 442, 5 test cũ đã fix). Nếu flake đã biết → chạy lại.

```powershell
git add -A
git commit -m "feat: seed free provider presets with IsPreset columns"
```

---

### Task 2: FreeModelSyncException + FreeModelDetector + fixtures

**Files:**
- Create: `src/RouterBalancing.Core/Providers/FreeModelSyncException.cs`
- Create: `src/RouterBalancing.Core/Providers/FreeModelDetector.cs`
- Create (test): `router balancing test/Providers/FreeModelFixtures.cs`
- Create (test): `router balancing test/Providers/FreeModelDetectorTests.cs`

**Interfaces:**
- Consumes: `FreeProviderCatalog.FreeDetectKind` (Task 1).
- Produces (Task 3): `FreeModelSyncException`, `FreeModelDetector.Parse(json, kind)` → `IReadOnlyList<FreeModelInfo>`; fixtures JSON dùng chung cho Task 3 tests.

- [ ] **Step 1: `FreeModelSyncException`**

Tạo `src/RouterBalancing.Core/Providers/FreeModelSyncException.cs`:

```csharp
namespace RouterBalancing.Core.Providers;

/// <summary>
/// Lỗi sync model free — service/UI phân biệt được với lỗi hệ thống:
/// UI hiện message thân thiện, periodic log warning và bỏ qua provider đó (spec provider-free §9).
/// </summary>
public sealed class FreeModelSyncException : Exception
{
    public FreeModelSyncException(string message)
        : base(message)
    {
    }

    public FreeModelSyncException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
```

- [ ] **Step 2: `FreeModelDetector`**

Tạo `src/RouterBalancing.Core/Providers/FreeModelDetector.cs`:

```csharp
using System.Globalization;
using System.Text.Json;

namespace RouterBalancing.Core.Providers;

/// <summary>Model free sau khi parse — DisplayName/ContextWindow lấy từ API nếu response có.</summary>
public sealed record FreeModelInfo(string ModelId, string? DisplayName, int? ContextWindow);

/// <summary>
/// Parse response list model (OpenAI shape <c>{"data":[...]}</c>) và lọc free theo
/// <see cref="FreeDetectKind"/> — pure function, không I/O (spec provider-free §6.3).
/// </summary>
public static class FreeModelDetector
{
    /// <summary>
    /// Lọc danh sách model free theo <paramref name="kind"/>.
    /// </summary>
    /// <param name="json">Body response của models endpoint.</param>
    /// <param name="kind">Cách detect free theo provider.</param>
    /// <returns>Các model free, giữ nguyên thứ tự trong response.</returns>
    /// <exception cref="JsonException">JSON malformed.</exception>
    /// <exception cref="FreeModelSyncException">Response thiếu mảng <c>data</c>.</exception>
    public static IReadOnlyList<FreeModelInfo> Parse(string json, FreeDetectKind kind)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new FreeModelSyncException("Model list response is missing the data array.");
        }

        var result = new List<FreeModelInfo>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement)) continue;
            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (!IsFree(kind, id, item)) continue;
            result.Add(new FreeModelInfo(id!, DisplayNameOf(item), ContextWindowOf(item)));
        }
        return result;
    }

    private static bool IsFree(FreeDetectKind kind, string id, JsonElement item) => kind switch
    {
        FreeDetectKind.AllFree => true,
        FreeDetectKind.FreeSuffix => id.EndsWith("-free", StringComparison.Ordinal),
        // D4: pricing = 0 HOẶC pattern ":free" — OR, không AND (id :free thì free kể cả pricing > 0)
        FreeDetectKind.PricingZeroOrFreeSuffix =>
            id.EndsWith(":free", StringComparison.Ordinal) || IsZeroPricing(item),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown free detect kind."),
    };

    /// <summary>OpenRouter: cả prompt lẫn completion phải tồn tại và parse về 0 — thiếu 1 field = không coi free (chủ động).</summary>
    private static bool IsZeroPricing(JsonElement item)
    {
        if (!item.TryGetProperty("pricing", out var pricing) || pricing.ValueKind != JsonValueKind.Object)
        {
            return false;
        }
        return pricing.TryGetProperty("prompt", out var prompt) && IsZero(prompt)
            && pricing.TryGetProperty("completion", out var completion) && IsZero(completion);
    }

    private static bool IsZero(JsonElement element) => element.ValueKind switch
    {
        // OpenRouter trả string ("0", "0.0", "0.0000025"); number phòng khi API đổi shape
        JsonValueKind.String =>
            decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value == 0m,
        JsonValueKind.Number => element.GetDecimal() == 0m,
        _ => false,
    };

    private static string? DisplayNameOf(JsonElement item) =>
        item.TryGetProperty("name", out var name)
        && name.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(name.GetString())
            ? name.GetString()
            : null;

    private static int? ContextWindowOf(JsonElement item) =>
        item.TryGetProperty("context_length", out var ctx) && ctx.ValueKind == JsonValueKind.Number
            ? ctx.GetInt32()
            : null;
}
```

- [ ] **Step 3: Fixtures JSON (probe thật 2026-10-01, rút gọn)**

Tạo `router balancing test/Providers/FreeModelFixtures.cs`:

```csharp
namespace router_balancing_test.Providers;

/// <summary>
/// JSON fixture rút gọn từ probe thật 2026-10-01 (spec provider-free §4) — shape giữ nguyên,
/// chỉ cắt bớt model. Dùng chung cho detector + sync tests.
/// </summary>
internal static class FreeModelFixtures
{
    /// <summary>OpenRouter: zero-pricing, ":free" (kể cả pricing > 0), và model trả phí.</summary>
    public const string OpenRouter = """
    {
      "data": [
        { "id": "inclusionai/ling-3.0-flash-sante:free", "name": "Ling 3.0 Flash Sante",
          "pricing": { "prompt": "0", "completion": "0" }, "context_length": 262144 },
        { "id": "stealth/space-bunny-alpha", "name": "Space Bunny Alpha",
          "pricing": { "prompt": "0.0", "completion": "0.0" }, "context_length": 131072 },
        { "id": "vendor/mispriced:free", "name": "Mispriced",
          "pricing": { "prompt": "0.1", "completion": "0.1" } },
        { "id": "openai/gpt-4o", "name": "GPT-4o",
          "pricing": { "prompt": "0.0000025", "completion": "0.00001" }, "context_length": 128000 }
      ]
    }
    """;

    /// <summary>OpenRouter toàn model trả phí — detector lọc rỗng → service guard không xoá gì.</summary>
    public const string OpenRouterAllPaid = """
    {
      "data": [
        { "id": "openai/gpt-4o", "name": "GPT-4o",
          "pricing": { "prompt": "0.0000025", "completion": "0.00001" }, "context_length": 128000 },
        { "id": "anthropic/claude-sonnet", "name": "Claude Sonnet",
          "pricing": { "prompt": "0.003", "completion": "0.015" }, "context_length": 200000 }
      ]
    }
    """;

    /// <summary>OpenCode Zen: pricing null, free = id hậu tố "-free".</summary>
    public const string OpenCode = """
    {
      "object": "list",
      "data": [
        { "id": "deepseek-v4-flash-free", "name": "DeepSeek V4 Flash Free", "context_length": 131072 },
        { "id": "mimo-v2.5-free", "name": "MiMo V2.5 Free" },
        { "id": "gpt-5.5", "name": "GPT 5.5", "context_length": 400000 }
      ]
    }
    """;

    /// <summary>NVIDIA NIM: không pricing — AllFree, name/context có thể vắng.</summary>
    public const string Nvidia = """
    {
      "object": "list",
      "data": [
        { "id": "meta/llama-3.1-8b-instruct" },
        { "id": "mistralai/mixtral-8x22b-instruct-v0.1", "name": "Mixtral 8x22B" }
      ]
    }
    """;
}
```

- [ ] **Step 4: Test `FreeModelDetectorTests`**

Tạo `router balancing test/Providers/FreeModelDetectorTests.cs`:

```csharp
using System.Text.Json;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class FreeModelDetectorTests
{
    [Fact]
    public void Parse_OpenRouterFixture_KeepsZeroPricingAndFreeSuffix()
    {
        var models = FreeModelDetector.Parse(
            FreeModelFixtures.OpenRouter, FreeDetectKind.PricingZeroOrFreeSuffix);

        // "vendor/mispriced:free" vào dù pricing > 0 (D4: OR), "openai/gpt-4o" bị loại
        Assert.Equal(
            new[] { "inclusionai/ling-3.0-flash-sante:free", "stealth/space-bunny-alpha", "vendor/mispriced:free" },
            models.Select(m => m.ModelId));
        Assert.Equal(262144, models[0].ContextWindow);
        Assert.Equal("Ling 3.0 Flash Sante", models[0].DisplayName);
    }

    [Fact]
    public void Parse_OpenCodeFixture_KeepsOnlyFreeSuffix()
    {
        var models = FreeModelDetector.Parse(FreeModelFixtures.OpenCode, FreeDetectKind.FreeSuffix);

        Assert.Equal(new[] { "deepseek-v4-flash-free", "mimo-v2.5-free" }, models.Select(m => m.ModelId));
        Assert.Null(models[1].ContextWindow); // response không có context_length → null
    }

    [Fact]
    public void Parse_AllFree_ReturnsEveryModel()
    {
        var models = FreeModelDetector.Parse(FreeModelFixtures.Nvidia, FreeDetectKind.AllFree);

        Assert.Equal(2, models.Count);
        Assert.Null(models[0].DisplayName); // không có name → null
    }

    [Fact]
    public void Parse_MissingDataArray_ThrowsFreeModelSyncException()
        => Assert.Throws<FreeModelSyncException>(() => FreeModelDetector.Parse("""{"object":"list"}""", FreeDetectKind.AllFree));

    [Fact]
    public void Parse_MalformedJson_ThrowsJsonException()
        => Assert.Throws<JsonException>(() => FreeModelDetector.Parse("{not json", FreeDetectKind.AllFree));

    [Fact]
    public void Parse_EmptyDataArray_ReturnsEmpty()
        // Detector trả rỗng là hợp lệ — guard "không xoá gì" nằm ở service (§6.2.5)
        => Assert.Empty(FreeModelDetector.Parse("""{"data":[]}""", FreeDetectKind.AllFree));
}
```

- [ ] **Step 5: Gates + Commit**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: 0 lỗi; toàn bộ pass (442 + 6 = 448).

```powershell
git add -A
git commit -m "feat: add free model detector with per-provider kinds"
```

---

### Task 3: IFreeModelSyncService + FreeModelSyncService + DI + tests

**Files:**
- Create: `src/RouterBalancing.Core/Providers/IFreeModelSyncService.cs`
- Create: `src/RouterBalancing.Core/Providers/FreeModelSyncService.cs`
- Modify: `router-balancing/MauiProgram.cs` (đăng ký service + named client 30s)
- Create (test): `router balancing test/Providers/FreeModelSyncServiceTests.cs`
- Create (test): `router balancing test/Providers/FreeModelSyncMergeTests.cs`

**Interfaces:**
- Consumes: `FreeProviderCatalog.FindByDisplayName`, `FreeModelDetector.Parse`, `ProviderRequestFactory.Create` (default `/v1/models` + `Canonicalize`), `ProviderKeyResolver.ResolveFirstEnabledKey`, `Provider.IsPreset`/`LastModelSyncAt`, `Model.IsManual`, `IFreeModelSyncService` (Task 4 worker + Task 5 UI gọi vào).
- Produces: singleton `IFreeModelSyncService`; named client `"free-model-sync"` (30s).

- [ ] **Step 1: Interface**

Tạo `src/RouterBalancing.Core/Providers/IFreeModelSyncService.cs`:

```csharp
namespace RouterBalancing.Core.Providers;

/// <summary>Đồng bộ danh sách model free cho provider preset (spec provider-free §6.1).</summary>
public interface IFreeModelSyncService
{
    /// <summary>
    /// Sync model free cho 1 provider preset — trả về số model free sau khi merge.
    /// </summary>
    /// <param name="providerId">Id provider preset.</param>
    /// <param name="ct">Hủy khi app thoát — DB đã commit mỗi provider là atomic.</param>
    /// <exception cref="FreeModelSyncException">
    /// Provider không tồn tại / không phải preset / không match catalog (đã đổi tên) /
    /// fetch lỗi (HTTP/timeout/JSON) / kết quả rỗng (guard không xoá list cũ).
    /// </exception>
    Task<int> SyncProviderAsync(long providerId, CancellationToken ct = default);

    /// <summary>
    /// Sync mọi provider preset đang Enabled — provider lỗi được log warning và bỏ qua,
    /// không phá vòng sync của các provider còn lại.
    /// </summary>
    Task<IReadOnlyList<(long ProviderId, int ModelCount)>> SyncAllEnabledAsync(CancellationToken ct = default);
}
```

- [ ] **Step 2: Service**

Tạo `src/RouterBalancing.Core/Providers/FreeModelSyncService.cs`:

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Providers;

/// <inheritdoc cref="IFreeModelSyncService"/>
public sealed class FreeModelSyncService : IFreeModelSyncService
{
    /// <summary>Tên named HttpClient — timeout 30s (list model lớn), đăng ký trong MauiProgram.</summary>
    public const string HttpClientName = "free-model-sync";

    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly ILogService _log;

    /// <inheritdoc/>
    public FreeModelSyncService(
        IDbContextFactory<RouterBalancingDbContext> db,
        ISecretProtector protector,
        IHttpClientFactory http,
        ILogService log)
    {
        _db = db;
        _protector = protector;
        _http = http;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<int> SyncProviderAsync(long providerId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers
            .Include(p => p.Models)
            .Include(p => p.Accounts)
            .FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new FreeModelSyncException($"Provider {providerId} not found.");
        if (!provider.IsPreset)
        {
            throw new FreeModelSyncException($"Provider {providerId} is not a free preset.");
        }

        // Match catalog theo Name (chốt S1, spec §6.2.2): user đổi tên preset → mất link sync
        var entry = FreeProviderCatalog.FindByDisplayName(provider.Name)
            ?? throw new FreeModelSyncException(
                $"Provider '{provider.Name}' is not in the free catalog (renamed?).");

        // Fetch (spec §6.1): key optional (4 endpoint public) — có account thì gắn Authorization
        var key = ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector) ?? string.Empty;
        using var request = ProviderRequestFactory.Create(provider, key);
        using var response = await _http.CreateClient(HttpClientName).SendAsync(request, ct);

        if (!response.IsSuccessStatusCode)
        {
            // Non-200 → ném TRƯỚC khi đọc body; không đụng DB (§6.2.3)
            throw new FreeModelSyncException(
                $"{provider.Name} returned HTTP {(int)response.StatusCode} for model list.");
        }

        var json = await response.Content.ReadAsStringAsync(ct);

        IReadOnlyList<FreeModelInfo> free;
        try
        {
            free = FreeModelDetector.Parse(json, entry.DetectKind);
        }
        catch (JsonException ex)
        {
            throw new FreeModelSyncException(
                $"Model list response from {provider.Name} is not valid JSON.", ex);
        }

        // Guard §6.2.5: API trả rỗng bất thường → KHÔNG bao giờ xoá sạch list cũ
        if (free.Count == 0)
        {
            throw new FreeModelSyncException(
                $"{provider.Name} returned 0 free models — keeping existing list.");
        }

        // ===== Merge (§6.2.6) — 1 SaveChanges duy nhất = atomic per provider =====
        var freeIds = free.Select(f => f.ModelId).ToHashSet(StringComparer.Ordinal);

        // Fetched (!IsManual) biến mất khỏi F → delete; manual giữ vĩnh viễn
        var stale = provider.Models.Where(m => !m.IsManual && !freeIds.Contains(m.ModelId)).ToList();
        if (stale.Count > 0)
        {
            db.Models.RemoveRange(stale);
        }

        var byId = provider.Models.ToDictionary(m => m.ModelId, StringComparer.Ordinal);
        foreach (var info in free)
        {
            if (byId.TryGetValue(info.ModelId, out var model))
            {
                // Manual của user: giữ nguyên kể cả metadata (§6.2.6)
                if (model.IsManual) continue;
                if (info.DisplayName is not null) model.DisplayName = info.DisplayName;
                if (info.ContextWindow is not null) model.ContextWindow = info.ContextWindow;
            }
            else
            {
                db.Models.Add(new Model
                {
                    ProviderId = provider.Id,
                    ModelId = info.ModelId,
                    IsManual = false,
                    Enabled = true,
                    DisplayName = info.DisplayName,
                    ContextWindow = info.ContextWindow,
                });
            }
        }

        provider.LastModelSyncAt = DateTimeOffset.UtcNow;
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _log.Info($"Synced {free.Count} free models for provider {providerId}.");
        return free.Count;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<(long ProviderId, int ModelCount)>> SyncAllEnabledAsync(
        CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var ids = await db.Providers.AsNoTracking()
            .Where(p => p.IsPreset && p.Enabled)
            .Select(p => p.Id)
            .ToListAsync(ct);

        var results = new List<(long ProviderId, int ModelCount)>();
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                results.Add((id, await SyncProviderAsync(id, ct)));
            }
            catch (OperationCanceledException)
            {
                throw; // app thoát giữa chừng — không nuốt
            }
            catch (FreeModelSyncException ex)
            {
                // 1 provider lỗi (§7/§9): log warning + skip, các provider còn lại vẫn sync
                _log.Warn($"Skipping provider {id} in free model sync: {ex.Message}");
            }
            catch (Exception ex)
            {
                _log.Error($"Unexpected error syncing free models for provider {id}.", ex);
            }
        }
        return results;
    }
}
```

- [ ] **Step 3: DI trong `MauiProgram.cs`**

Sau dòng `builder.Services.AddSingleton<IModelService, ModelService>();` thêm:

```csharp
            builder.Services.AddSingleton<IFreeModelSyncService, FreeModelSyncService>();
            // Sync list model free lớn hơn probe 10s — timeout 30s (spec provider-free §6.1)
            builder.Services.AddHttpClient(FreeModelSyncService.HttpClientName,
                client => client.Timeout = TimeSpan.FromSeconds(30));
```

(`using RouterBalancing.Core.Providers;` đã có sẵn — `ProviderRequestFactory` đang dùng.)

- [ ] **Step 4: Test lỗi service `FreeModelSyncServiceTests`**

Tạo `router balancing test/Providers/FreeModelSyncServiceTests.cs`:

```csharp
using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class FreeModelSyncServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public FreeModelSyncServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db); // seed 4 preset — test dùng chính hàng seed
    }

    public void Dispose() => _testDb.Dispose();

    private FreeModelSyncService ServiceWith(HttpMessageHandler handler) =>
        new(_db, _protector, new StubFactory(handler), new NullLog());

    private FreeModelSyncService ServiceWithJson(string json) => ServiceWith(new JsonHandler(json));

    private async Task<long> PresetIdAsync(string name)
    {
        using var db = _db.CreateDbContext();
        return (await db.Providers.SingleAsync(p => p.Name == name)).Id;
    }

    [Fact]
    public async Task SyncProviderAsync_UnknownProvider_ThrowsNotFound()
    {
        var service = ServiceWithJson(FreeModelFixtures.OpenRouter);

        var ex = await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(999_999));

        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task SyncProviderAsync_NonPreset_ThrowsNotPreset()
    {
        long id;
        using (var db = _db.CreateDbContext())
        {
            var provider = db.Providers.Add(new Provider
            {
                Name = "My Provider",
                BaseUrl = "https://api.example.com",
                Type = ProviderType.OpenAI,
                IsPreset = false,
            }).Entity;
            await db.SaveChangesAsync();
            id = provider.Id;
        }
        var service = ServiceWithJson(FreeModelFixtures.OpenRouter);

        var ex = await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(id));

        Assert.Contains("not a free preset", ex.Message);
    }

    [Fact]
    public async Task SyncProviderAsync_RenamedPreset_ThrowsNotInCatalog()
    {
        // Chốt S1: đổi tên preset → mất link sync, lỗi rõ cho user (manual)
        long id;
        using (var db = _db.CreateDbContext())
        {
            var preset = await db.Providers.SingleAsync(p => p.Name == "OpenRouter Free");
            preset.Name = "My Router";
            await db.SaveChangesAsync();
            id = preset.Id;
        }
        var service = ServiceWithJson(FreeModelFixtures.OpenRouter);

        var ex = await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(id));

        Assert.Contains("free catalog", ex.Message);
    }

    [Fact]
    public async Task SyncProviderAsync_HttpError_ThrowsAndKeepsModels()
    {
        var id = await PresetIdAsync("OpenCode Free");
        using (var db = _db.CreateDbContext())
        {
            db.Models.Add(new Model { ProviderId = id, ModelId = "keep-me", IsManual = false });
            await db.SaveChangesAsync();
        }
        var service = ServiceWith(new StatusHandler(HttpStatusCode.InternalServerError));

        var ex = await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(id));

        Assert.Contains("HTTP 500", ex.Message);
        using var db2 = _db.CreateDbContext();
        // Không đụng DB khi fetch lỗi (§6.2.3)
        Assert.Equal("keep-me", Assert.Single(
            await db2.Models.AsNoTracking().Where(m => m.ProviderId == id).ToListAsync()).ModelId);
        var preset = await db2.Providers.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.Null(preset.LastModelSyncAt);
    }

    [Fact]
    public async Task SyncProviderAsync_MalformedJson_Throws()
    {
        var id = await PresetIdAsync("OpenRouter Free");
        var service = ServiceWithJson("{not json");

        var ex = await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(id));

        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public async Task SyncAllEnabledAsync_FailingPreset_SkippedAndOthersSynced()
    {
        // Bật 2 preset: OpenCode (handler sẽ 500) + NVIDIA (200)
        using (var db = _db.CreateDbContext())
        {
            foreach (var preset in db.Providers.Where(p =>
                p.IsPreset && (p.Name == "OpenCode Free" || p.Name == "NVIDIA NIM Free")))
            {
                preset.Enabled = true;
            }
            await db.SaveChangesAsync();
        }
        var nvidiaId = await PresetIdAsync("NVIDIA NIM Free");
        var handler = new RoutingHandler(failUrlPart: "opencode.ai", okJson: FreeModelFixtures.Nvidia);
        var service = ServiceWith(handler);

        var results = await service.SyncAllEnabledAsync();

        var only = Assert.Single(results); // OpenCode bị skip, không throw
        Assert.Equal(nvidiaId, only.ProviderId);
        Assert.Equal(2, only.ModelCount);
        Assert.Equal(2, handler.Calls); // cả 2 preset enabled đều được gọi: 1 fail (skip) + 1 OK
    }

    [Fact]
    public async Task SyncAllEnabledAsync_AllPresetsDisabled_MakesNoHttpCall()
    {
        var handler = new SpyHandler();
        var service = ServiceWith(handler);

        var results = await service.SyncAllEnabledAsync();

        Assert.Empty(results);
        Assert.Equal(0, handler.Calls);
    }

    // ===== HTTP doubles (pattern ModelServiceTests: JsonHandler/StubFactory) =====

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    private sealed class RoutingHandler(string failUrlPart, string okJson) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var fail = request.RequestUri is { } uri
                && uri.ToString().Contains(failUrlPart, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(
                fail ? HttpStatusCode.InternalServerError : HttpStatusCode.OK)
            {
                Content = new StringContent(fail ? "{}" : okJson),
            });
        }
    }

    private sealed class SpyHandler : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[]}"""),
            });
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
```

- [ ] **Step 5: Test merge `FreeModelSyncMergeTests`**

Tạo `router balancing test/Providers/FreeModelSyncMergeTests.cs`:

```csharp
using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class FreeModelSyncMergeTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public FreeModelSyncMergeTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db); // 4 preset — dùng chính "OpenRouter Free" (DetectKind PricingZeroOrFreeSuffix)
    }

    public void Dispose() => _testDb.Dispose();

    private FreeModelSyncService ServiceWith(string json) =>
        new(_db, _protector, new StubFactory(new JsonHandler(json)), new NullLog());

    private async Task<long> OpenRouterIdAsync()
    {
        using var db = _db.CreateDbContext();
        return (await db.Providers.SingleAsync(p => p.Name == "OpenRouter Free")).Id;
    }

    private async Task AddModelAsync(long providerId, string modelId, bool isManual, string? displayName = null)
    {
        using var db = _db.CreateDbContext();
        db.Models.Add(new Model
        {
            ProviderId = providerId,
            ModelId = modelId,
            IsManual = isManual,
            DisplayName = displayName,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task SyncProviderAsync_FirstSync_InsertsFreeModelsAndSetsLastSyncAt()
    {
        var id = await OpenRouterIdAsync();
        var service = ServiceWith(FreeModelFixtures.OpenRouter);

        var count = await service.SyncProviderAsync(id);

        Assert.Equal(3, count); // fixture: 3 free / 4 total (gpt-4o trả phí bị loại)
        using var db = _db.CreateDbContext();
        var models = await db.Models.AsNoTracking()
            .Where(m => m.ProviderId == id)
            .OrderBy(m => m.ModelId)
            .ToListAsync();
        Assert.Equal(
            new[] { "inclusionai/ling-3.0-flash-sante:free", "stealth/space-bunny-alpha", "vendor/mispriced:free" },
            models.Select(m => m.ModelId));
        Assert.All(models, m =>
        {
            Assert.False(m.IsManual);
            Assert.True(m.Enabled);
        });
        var preset = await db.Providers.AsNoTracking().SingleAsync(p => p.Id == id);
        Assert.NotNull(preset.LastModelSyncAt);
    }

    [Fact]
    public async Task SyncProviderAsync_ManualModelNotInApi_IsKept()
    {
        var id = await OpenRouterIdAsync();
        await AddModelAsync(id, "my-private-model", isManual: true);
        var service = ServiceWith(FreeModelFixtures.OpenRouter);

        await service.SyncProviderAsync(id);

        using var db = _db.CreateDbContext();
        Assert.Contains(await db.Models.AsNoTracking().ToListAsync(),
            m => m.ModelId == "my-private-model" && m.IsManual); // D5: manual không bao giờ bị xoá
    }

    [Fact]
    public async Task SyncProviderAsync_ManualModelInApi_MetadataNotOverwritten()
    {
        var id = await OpenRouterIdAsync();
        await AddModelAsync(id, "stealth/space-bunny-alpha", isManual: true, displayName: "User Name");
        var service = ServiceWith(FreeModelFixtures.OpenRouter);

        await service.SyncProviderAsync(id);

        using var db = _db.CreateDbContext();
        var model = await db.Models.AsNoTracking()
            .SingleAsync(m => m.ProviderId == id && m.ModelId == "stealth/space-bunny-alpha");
        Assert.True(model.IsManual);
        Assert.Equal("User Name", model.DisplayName); // API nói "Space Bunny Alpha" — không đè (§6.2.6)
    }

    [Fact]
    public async Task SyncProviderAsync_SecondSyncWithChangedFixture_DeletesStaleFetchedAndUpdatesMetadata()
    {
        var id = await OpenRouterIdAsync();
        await AddModelAsync(id, "stale-fetched", isManual: false);
        await AddModelAsync(id, "my-manual", isManual: true);
        var service = ServiceWith(FreeModelFixtures.OpenRouter);
        await service.SyncProviderAsync(id);

        // Fixture đổi: mất "vendor/mispriced:free", "ling" đổi name/context, thêm model mới
        const string changed = """
        {
          "data": [
            { "id": "inclusionai/ling-3.0-flash-sante:free", "name": "Ling v2",
              "pricing": { "prompt": "0", "completion": "0" }, "context_length": 1000000 },
            { "id": "stealth/space-bunny-alpha", "name": "Space Bunny Alpha",
              "pricing": { "prompt": "0.0", "completion": "0.0" } },
            { "id": "new/provider-model", "name": "New Model",
              "pricing": { "prompt": "0", "completion": "0" } }
          ]
        }
        """;
        var count = await ServiceWith(changed).SyncProviderAsync(id);

        Assert.Equal(3, count);
        using var db = _db.CreateDbContext();
        var models = await db.Models.AsNoTracking()
            .Where(m => m.ProviderId == id)
            .OrderBy(m => m.ModelId)
            .ToListAsync();
        Assert.Equal(
            new[]
            {
                "inclusionai/ling-3.0-flash-sante:free",
                "my-manual",
                "new/provider-model",
                "stealth/space-bunny-alpha",
            },
            models.Select(m => m.ModelId)); // "stale-fetched" + "vendor/mispriced:free" đã đi, manual còn
        var ling = models.Single(m => m.ModelId == "inclusionai/ling-3.0-flash-sante:free");
        Assert.Equal("Ling v2", ling.DisplayName);
        Assert.Equal(1000000, ling.ContextWindow); // metadata update khi API có
    }

    [Fact]
    public async Task SyncProviderAsync_EmptyFreeResult_ThrowsAndKeepsExistingModels()
    {
        var id = await OpenRouterIdAsync();
        await AddModelAsync(id, "fetched-old", isManual: false);
        await AddModelAsync(id, "my-manual", isManual: true);
        var service = ServiceWith(FreeModelFixtures.OpenRouterAllPaid); // 0 model free

        var ex = await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(id));

        Assert.Contains("0 free models", ex.Message);
        using var db = _db.CreateDbContext();
        var models = await db.Models.AsNoTracking().Where(m => m.ProviderId == id).ToListAsync();
        Assert.Equal(2, models.Count); // guard §6.2.5: không xoá gì cả
        Assert.Null((await db.Providers.AsNoTracking().SingleAsync(p => p.Id == id)).LastModelSyncAt);
    }

    [Fact]
    public async Task SyncProviderAsync_DisabledPreset_ManualSyncStillWorks()
    {
        // §9: preset chưa Enabled — periodic bỏ qua nhưng manual vẫn cho phép (user chủ động)
        var id = await OpenRouterIdAsync(); // seeded Enabled = false
        var service = ServiceWith(FreeModelFixtures.OpenRouter);

        var count = await service.SyncProviderAsync(id);

        Assert.Equal(3, count);
    }

    // ===== HTTP doubles (pattern ModelServiceTests) =====

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
```

- [ ] **Step 6: Gates + Commit**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: build 0 lỗi (MauiProgram đăng ký OK); test toàn bộ pass (448 + 13 = 461).

```powershell
git add -A
git commit -m "feat: add free model sync service"
```

---

### Task 4: FreeModelSyncWorker + wiring App startup

**Files:**
- Create: `src/RouterBalancing.Core/Providers/FreeModelSyncWorker.cs`
- Modify: `router-balancing/MauiProgram.cs` (singleton worker)
- Modify: `router-balancing/App.xaml.cs` (ctor + `Start()` + dispose)
- Create (test): `router balancing test/Providers/FreeModelSyncWorkerTests.cs`

**Interfaces:**
- Consumes: `IFreeModelSyncService.SyncAllEnabledAsync` (Task 3), pattern `LogRetentionWorker` (`Start` idempotent, `PeriodicTimer`, `IAsyncDisposable`).
- Produces: worker chạy nền; App start/stop cùng `LogRetentionWorker`.

- [ ] **Step 1: Worker**

Tạo `src/RouterBalancing.Core/Providers/FreeModelSyncWorker.cs`:

```csharp
using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Đồng bộ model free cho các preset đang bật: trễ 60s lần đầu (không đụng DB/network
/// ngay lúc app mở) rồi lặp mỗi 6 giờ — end-user không tự bấm sync nên phải chạy nền
/// (spec provider-free §7). Pattern tái dùng từ <see cref="LogRetentionWorker"/>.
/// </summary>
public sealed class FreeModelSyncWorker : IAsyncDisposable
{
    private readonly IFreeModelSyncService _sync;
    private readonly ILogService _log;
    private readonly TimeSpan _period;
    private readonly TimeSpan _initialDelay;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <param name="sync">Service sync — singleton, chia sẻ với nút manual của UI.</param>
    /// <param name="log">Ghi lỗi mỗi chu kỳ — một lần fail không được giết vòng lặp.</param>
    /// <param name="period">Chu kỳ sync — mặc định 6 giờ (§7).</param>
    /// <param name="initialDelay">Trễ lần đầu — mặc định 60s; test truyền <c>TimeSpan.Zero</c>.</param>
    public FreeModelSyncWorker(
        IFreeModelSyncService sync,
        ILogService log,
        TimeSpan? period = null,
        TimeSpan? initialDelay = null)
    {
        _sync = sync;
        _log = log;
        _period = period ?? TimeSpan.FromHours(6);
        _initialDelay = initialDelay ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>Chạy nền: trễ initialDelay rồi sync + lặp mỗi period — gọi lần 2 chỉ là no-op.</summary>
    public void Start()
    {
        if (_loop is not null) return;
        // Capture CTS local: DisposeAsync có thể đặt _cts = null trước khi Task kịp chạy
        var cts = _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(cts.Token));
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // Trễ lần đầu: app vừa mở còn nặng, tránh đụng DB/network lúc startup (§7)
            await Task.Delay(_initialDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return; // app đóng trước khi tới lần sync đầu
        }

        using var timer = new PeriodicTimer(_period);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // SyncAllEnabledAsync đã catch lỗi từng provider — catch ở đây cho lỗi cấp
                // hệ thống (DB hỏng...) để vòng lặp không chết
                await _sync.SyncAllEnabledAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Lỗi một chu kỳ không được giết vòng lặp — vẫn thử ở chu kỳ sau.
                // Ghi lỗi cũng có thể ném khi log DB hỏng — nuốt là chấp nhận được vì đây là
                // best-effort diagnostics, nếu không vòng lặp sẽ chết vĩnh viễn
                // (y hệt LogRetentionWorker).
                try
                {
                    _log.Error("Đồng bộ model free định kỳ thất bại.", ex);
                }
                catch
                {
                    // Đã nuốt: không còn chỗ nào an toàn để báo lỗi
                }
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(cancellationToken)) break;
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Dừng vòng lặp nền — chờ nó kết thúc để không cắt sync giữa chừng khi app thoát.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Chu kỳ đang chạy bị hủy khi dispose — đã xử lý xong
            }
        }
        _cts.Dispose();
        _cts = null;
        _loop = null;
    }
}
```

- [ ] **Step 2: DI + App wiring**

**(a)** `router-balancing/MauiProgram.cs` — sau dòng `builder.Services.AddSingleton<LogRetentionWorker>();`:

```csharp
            builder.Services.AddSingleton<FreeModelSyncWorker>();
```

**(b)** `router-balancing/App.xaml.cs`:

Thêm using:

```csharp
using RouterBalancing.Core.Providers;
```

Thêm field (sau `private readonly LogRetentionWorker _retention;`):

```csharp
        private readonly FreeModelSyncWorker _freeSync;
```

Ctor — thêm tham số `FreeModelSyncWorker freeSync` sau `LogRetentionWorker retention`, và gán `_freeSync = freeSync;` sau `_retention = retention;`.

Sau `_retention.Start();` (giữ comment cũ phía trên, thêm comment mới):

```csharp
            // Sync model free: trễ 60s rồi lặp 6h — preset free cần list model mới mà user không tự bấm
            _freeSync.Start();
```

Trong `StopProxyOnExit()` — sửa block:

```csharp
                _tray.Dispose();
                // Dừng vòng lặp dọn log + sync model free trước khi process chết
                _retention.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
                _freeSync.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
```

(cập nhật comment cũ `// Dừng vòng lặp dọn log trước khi process chết` thành dòng trên.)

- [ ] **Step 3: Test worker**

Tạo `router balancing test/Providers/FreeModelSyncWorkerTests.cs`:

```csharp
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class FreeModelSyncWorkerTests
{
    [Fact]
    public async Task Start_WithZeroInitialDelay_CallsSyncAllOnce()
    {
        var sync = new RecordingSyncService();
        await using var worker = new FreeModelSyncWorker(
            sync, new NullLog(), period: TimeSpan.FromHours(6), initialDelay: TimeSpan.Zero);

        worker.Start();

        await sync.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, sync.CallCount);
    }

    [Fact]
    public async Task Start_Twice_OnlyOneLoop()
    {
        var sync = new RecordingSyncService();
        await using var worker = new FreeModelSyncWorker(
            sync, new NullLog(), period: TimeSpan.FromHours(6), initialDelay: TimeSpan.Zero);
        worker.Start();
        worker.Start(); // no-op

        await sync.FirstCall.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.DisposeAsync();
        var afterDispose = sync.CallCount;
        await Task.Delay(200);

        Assert.Equal(afterDispose, sync.CallCount); // không còn vòng lặp nào chạy sau dispose
    }

    [Fact]
    public async Task Dispose_BeforeInitialDelay_DoesNotCallSync()
    {
        var sync = new RecordingSyncService();
        var worker = new FreeModelSyncWorker(
            sync, new NullLog(), period: TimeSpan.FromHours(6), initialDelay: TimeSpan.FromHours(1));

        worker.Start();
        await worker.DisposeAsync();
        await Task.Delay(100);

        Assert.Equal(0, sync.CallCount); // app đóng trước delay → không sync lần nào
    }

    private sealed class RecordingSyncService : IFreeModelSyncService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public TaskCompletionSource FirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> SyncProviderAsync(long providerId, CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task<IReadOnlyList<(long ProviderId, int ModelCount)>> SyncAllEnabledAsync(
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _callCount);
            FirstCall.TrySetResult();
            return Task.FromResult<IReadOnlyList<(long ProviderId, int ModelCount)>>([]);
        }
    }
}
```

(`NullLog` nội bộ trong assembly test — `TestDoubles.cs`, dùng trực tiếp.)

- [ ] **Step 4: Gates + Commit**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: build 0 lỗi (App ctor DI khớp); test toàn bộ pass (461 + 3 = 464).

```powershell
git add -A
git commit -m "feat: run periodic free model sync worker"
```

---

### Task 5: UI Providers (badge Free + nút sync + ẩn delete) + i18n + guard service

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/ProviderService.cs` + `IProviderService.cs`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (+ 6 key ×2 dict)
- Modify: `router-balancing/Components/Pages/Providers.razor`
- Create (test): `router balancing test/Providers/ProviderServiceDeletePresetTests.cs`

**Interfaces:**
- Consumes: `Provider.IsPreset`/`LastModelSyncAt`, `IFreeModelSyncService.SyncProviderAsync` (Task 3), `FreeModelSyncException`, `LocalizationService` indexer `L["..."]`, `ReloadKeepExpandAsync`/`LoadAsync`, `Badge`/`BadgeVariant.Info`, `ToastService.Show`.
- Produces: UI spec §8 đầy đủ; `DeleteAsync` guard defense-in-depth.

- [ ] **Step 1: Guard `ProviderService.DeleteAsync`**

Trong `ProviderService.DeleteAsync` (sau khi load `provider`, trước khi `db.Providers.Remove`):

```csharp
        // Preset free không được xoá (D6) — ẩn nút ở UI chưa đủ, service tự chặn (spec §8.3)
        if (provider.IsPreset)
        {
            throw new InvalidOperationException($"Preset provider {id} cannot be deleted.");
        }
```

XML doc trong `IProviderService.DeleteAsync` (line ~26-28) — thêm dòng sau `<exception cref="KeyNotFoundException">...`:

```csharp
    /// <exception cref="InvalidOperationException">Khi provider là preset free — không cho xóa (spec provider-free §6 D6).</exception>
```

- [ ] **Step 2: i18n — 6 key ×2 dict**

**(a)** `Translations.cs` dict `English` — chèn sau dòng `["providers.error.identifierSegmentCollision"] = "Identifier conflicts with an existing model id prefix.",` (trước `["models.section"]`):

```csharp
        ["providers.badge.free"] = "Free",
        ["providers.sync.now"] = "Load free models",
        ["providers.sync.success"] = "Synced {0} free models.",
        ["providers.sync.failed"] = "Free model sync failed: {0}",
        ["providers.sync.lastSync"] = "Synced: {0}",
        ["providers.delete.preset"] = "Preset providers cannot be deleted.",
```

**(b)** dict `Vietnamese` — chèn sau dòng `["providers.error.identifierSegmentCollision"] = "Mã định danh trùng tiền tố model id đang có.",`:

```csharp
        ["providers.badge.free"] = "Free",
        ["providers.sync.now"] = "Tải model free",
        ["providers.sync.success"] = "Đã đồng bộ {0} model free.",
        ["providers.sync.failed"] = "Đồng bộ model free thất bại: {0}",
        ["providers.sync.lastSync"] = "Đã sync: {0}",
        ["providers.delete.preset"] = "Không thể xóa nhà cung cấp preset.",
```

- [ ] **Step 3: `Providers.razor` — inject**

Sau dòng `@inject IModelService ModelSvc` (line ~10):

```razor
@inject IFreeModelSyncService FreeSyncSvc
```

- [ ] **Step 4: `Providers.razor` — badge Free + last-sync trong cột Name**

Thay block (line ~57-62):

```razor
                        <td class="px-2 py-2">
                            <div class="flex items-center gap-2">
                                <span class="font-medium">@p.Name</span>
                                <Badge Text="@TypeLabel(p.Type)" Variant="TypeVariant(p.Type)" />
                                @if (p.IsPreset)
                                {
                                    <Badge Text="@L["providers.badge.free"]" Variant="BadgeVariant.Info" />
                                    @if (p.LastModelSyncAt is { } syncedAt)
                                    {
                                        <span class="text-xs opacity-70">@string.Format(L["providers.sync.lastSync"], syncedAt.ToLocalTime().ToString("g"))</span>
                                    }
                                }
                            </div>
                        </td>
```

- [ ] **Step 5: `Providers.razor` — nút sync + ẩn nút delete trong cột Actions**

Thay block (line ~92-110):

```razor
                        <td class="px-2 py-2 text-right">
                            <div class="flex justify-end gap-1">
                                @if (p.IsPreset)
                                {
                                    <button type="button" class="btn btn-outline-secondary"
                                            disabled="@(_busy || _syncingId == p.Id)"
                                            @onclick="() => SyncFreeModelsAsync(p)">
                                        @L["providers.sync.now"]
                                    </button>
                                }
                                <button type="button" class="btn btn-outline-secondary"
                                        disabled="@(_busy || _testingId == p.Id)"
                                        @onclick="() => TestAsync(p)">
                                    @(_testingId == p.Id ? L["providers.testing"] : L["providers.action.test"])
                                </button>
                                <button type="button" class="btn btn-outline-secondary"
                                        disabled="@_busy"
                                        @onclick="() => OpenEdit(p)">
                                    @L["providers.action.edit"]
                                </button>
                                @* Preset free không xoá được — ẩn nút (D6); service cũng tự chặn (§8.3) *@
                                @if (!p.IsPreset)
                                {
                                    <button type="button" class="btn btn-outline-danger"
                                            disabled="@(_busy || _confirmDeleteProvider is not null)"
                                            @onclick="() => _confirmDeleteProvider = p">
                                        @L["providers.action.delete"]
                                    </button>
                                }
                            </div>
                        </td>
```

- [ ] **Step 6: `Providers.razor` — state + handler**

Sau `private long? _fetchingId;` (line ~612):

```csharp
    private long? _syncingId;
```

Thêm method (đặt ngay sau `TestAsync`, trước `DeleteProviderAsync`):

```csharp
    /// <summary>Sync model free cho preset — single-flight qua _syncId + _busy (spec provider-free §8.4).</summary>
    private async Task SyncFreeModelsAsync(Provider provider)
    {
        if (_busy) return;
        _busy = true;
        _syncingId = provider.Id;
        try
        {
            var count = await FreeSyncSvc.SyncProviderAsync(provider.Id);
            Toast.Show(string.Format(L["providers.sync.success"], count), ToastSeverity.Success);
            // LoadAsync giữ nguyên trạng thái expand/ trang — không ép mở row như ReloadKeepExpandAsync
            await LoadAsync();
        }
        catch (Exception ex)
        {
            // FreeModelSyncException đã có message thân thiện — hiện thẳng, không nuốt
            Log.Error("Đồng bộ model free thất bại.", ex);
            Toast.Show(string.Format(L["providers.sync.failed"], ex.Message), ToastSeverity.Error);
        }
        finally
        {
            _syncingId = null;
            _busy = false;
        }
    }
```

Trong `DeleteProviderAsync` — thêm catch **trước** `catch (Exception ex)` hiện có:

```csharp
        catch (InvalidOperationException ex)
        {
            // Service ném khi provider là preset (defense-in-depth) — hiện message riêng thay vì toast chung
            Log.Error("Không xóa được provider preset.", ex);
            Toast.Show(L["providers.delete.preset"], ToastSeverity.Error);
        }
```

- [ ] **Step 7: Test `ProviderServiceDeletePresetTests`**

Tạo `router balancing test/Providers/ProviderServiceDeletePresetTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ProviderServiceDeletePresetTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ProviderService _service;

    public ProviderServiceDeletePresetTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        _service = new ProviderService(_db, new DpapiSecretProtector(), new NeverHttpFactory(), new NullLog());
    }

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public async Task DeleteAsync_PresetProvider_ThrowsAndKeepsRow()
    {
        var id = await PresetIdAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(id));

        using var db = _db.CreateDbContext();
        Assert.True(await db.Providers.AnyAsync(p => p.Id == id));
        Assert.True(await db.Providers.AnyAsync(p => p.Id == id && p.IsPreset));
    }

    [Fact]
    public async Task DeleteAsync_NormalProvider_RemovesRow()
    {
        long id;
        using (var db = _db.CreateDbContext())
        {
            var provider = db.Providers.Add(new Provider
            {
                Name = "Scratch",
                BaseUrl = "https://api.example.com",
                Type = ProviderType.OpenAI,
            }).Entity;
            await db.SaveChangesAsync();
            id = provider.Id;
        }

        await _service.DeleteAsync(id);

        using var db2 = _db.CreateDbContext();
        Assert.False(await db2.Providers.AnyAsync(p => p.Id == id));
    }

    private async Task<long> PresetIdAsync()
    {
        using var db = _db.CreateDbContext();
        return (await db.Providers.SingleAsync(p => p.IsPreset)).Id;
    }

    /// <summary>CRUD không được gọi network — nếu có thì test fail loud.</summary>
    private sealed class NeverHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("CRUD must not perform HTTP calls.");
    }
}
```

- [ ] **Step 8: Gates + Commit**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: build 0 lỗi; test toàn bộ pass (464 + 2 = 466); `TranslationParityTests` xanh (6 key mới đủ 2 dict).

```powershell
git add -A
git commit -m "feat: add free badge and manual sync to providers page"
```

---

### Task 6: Final verification (gates toàn nhánh)

**Files:** không sửa code (chỉ verify; fix nếu fail → commit riêng `fix:`).

- [ ] **Step 1: Gates đầy đủ**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: 0 lỗi build; test **tất cả pass** (baseline 419 + ~47 mới ≈ 466 — số chính xác theo tổng test đã thêm; flake đã biết → chạy lại). KHÔNG build solution.

- [ ] **Step 2: Kiểm tra diff + history**

```powershell
git status --short          # trống
git log --oneline 54b43c7.. # 7 commits: docs(plan) + 5 feat + (nếu có fix:)
git diff 54b43c7.. --stat   # chỉ các file trong File Structure
```

- [ ] **Step 3: Smoke test thủ công (app mở, cần mạng)**

1. Mở app → tab **Providers**: thấy 4 hàng `OpenCode Free`, `OpenRouter Free`, `NVIDIA NIM Free`, `Ollama Cloud Free` — badge **Free** xanh, checkbox Enabled tắt, không có nút **Delete**.
2. Hàng provider thường (đã có) vẫn có nút Delete bình thường.
3. Bật 1 preset (vd OpenRouter) → bấm **Load free models** → toast `Synced N free models.`, badge `Synced: <giờ>`, cột Models hiện `0/N`.
4. Bấm **Load free models** lần nữa → toast thành công, list không nhảy loạn (merge idempotent).
5. Đổi tên preset thành `My Router` → bấm sync → toast lỗi `Free model sync failed: Provider 'My Router' is not in the free catalog (renamed?).`
6. Đóng app → mở lại trong <60s → không lỗi gì (worker chưa tick); chờ >60s với preset Enabled → log panel có `Synced N free models...` (hoặc warning nếu mạng lỗi — không crash).
7. Thử gọi `DeleteAsync` preset qua UI đã ẩn → không có đường bấm; (optional) log `Preset provider ... cannot be deleted` nếu bắn tay.

- [ ] **Step 4: Báo cáo**

Kết quả 3 gate + smoke checklist → user quyết định next (item 5: proxy pool) / review / merge.

---

## Self-review (plan)

Đã kiểm tra trước khi commit plan:

- [x] **Spec coverage**: §5 (2 cột + migration) → T1; §6.1-6.2 (interface, algorithm 7 bước, guard rỗng) → T3; §6.3 (3 kind, name/context map) → T2; §6.4 (seed, không ghi đè) → T1 (Option B, xem Design decision 1); §7 (worker 6h/trễ 60s/error per provider) → T4; §8 (badge/ẩn delete/guard/nút sync/last-sync/form giữ nguyên) → T5; §9 (bảng error handling) → T3 catches + T4 worker catch; §10 (6 key) → T5; §11 (không log key — service không log key; timeout 30s) → T3/T4; §13 (test list) → T1-T5 (integration gộp, decision 10).
- [x] **Deviations đều ghi trong "Design decisions locked in this plan"** (11 mục, gồm cả4 test fix).
- [x] **Mỗi task để lại repo compile xanh** — thứ tự T1 (entity+seed+test fix) → T2 (pure) → T3 (service+DI) → T4 (worker+App) → T5 (UI); không task nào treo tham chiếu chưa tồn tại (`FreeDetectKind` vào catalog ở T1 vì T1 đã dùng; `FreeModelSyncException` tạo ở T2 trước khi detector/service dùng).
- [x] **Mỗi task có gate + commit riêng**, đúng quy ước AGENTS.md.
- [x] **Blast radius seed đã scan toàn repo**: đúng 3 file/5 test hiện có vỡ (decision 11) — đã có fix step cụ thể; engine/proxy tests dùng provider theo id/Enabled=false của preset → không vỡ (gate T1 sẽ xác nhận).
- [x] **Code mẫu đã soát lại**: không còn ký tự lạ/ý định mơ hồ trong các block code (catalog comment, service merge comment, SyncAll test, smoke checklist đều đã sửa trực tiếp).
- [x] **Flake đã biết** ghi trong Global Constraints.
