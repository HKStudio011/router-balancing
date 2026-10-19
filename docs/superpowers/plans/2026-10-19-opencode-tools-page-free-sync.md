# OpenCode Tools Page + Free Sync v2 (catalog) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Chuyển sync model free của preset "OpenCode Free" sang catalog v2 (`models.opencode.ai/api.json`, free = `cost.input==0 && cost.output==0 && status != deprecated`) kèm capabilities, và thêm trang **Tools** sinh config opencode v2 trỏ về proxy của app.

**Architecture:** `FreeProviderCatalog.Entry` thêm `CatalogUrl` + kind `OpenCodeCatalog`; parser mới `FreeModelDetector.ParseOpenCodeCatalog` (pure) trả `FreeModelInfo` mở rộng (tools/vision/think/efforts/modalities/limit); `FreeModelSyncService` fetch catalog URL tuyệt đối (không auth — vẫn qua `ProxyTarget` provider) rồi merge y như cũ + ghi capabilities; 2 cột mới `Model.SupportsTools` (`bool?`) / `Model.MaxOutputTokens` (`int?`). Trang Tools = `OpenCodeConfigGenerator` (static, thuần) + `IOpenCodeConfigService` (đọc DB, default tick, dedupe) + `Tools.razor` (copy qua `IClipboardService`, download qua JS blob) + 23 key i18n.

**Tech Stack:** .NET 10 / EF Core 10 (SQLite, auto-migration), xUnit, Blazor Hybrid (Razor Components).

**Spec:** `docs/superpowers/specs/2026-10-19-opencode-tools-page-free-sync-design.md`

## Global Constraints

- **Trước Task 1:** tạo branch từ master hiện tại — `git switch -c feat/opencode-tools-page` (master protected, PR cuối; 3 commit docs spec hiện có đi kèm branch này).
- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên; TFM app: `net10.0-windows10.0.19041.0`.
- Comment tiếng Việt cho "tại sao", XML doc cho public API; **exception message tiếng Anh** (consistent code cũ); không TODO vô chủ.
- i18n: mọi key mới phải có trong **cả 2 dict** `English` và `Vietnamese` của `Translations.cs` (parity test tự bắt).
- 1 task = 1 commit; commit message tiếng Anh conventional (`feat:`).
- Không nuốt exception; không thêm `catch {}` rỗng.
- Razor: UTF-8 trực tiếp, **không** HTML entity (guard `Components/RazorParameterEntityTests.cs`).
- App `router-balancing` phải **đóng** trước khi build TFM Windows / chạy test (SingleInstanceGuard + DLL lock).
- Gates chuẩn (mỗi task, trước commit):
  - `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj`
  - `dotnet test "router balancing test/router balancing test.csproj"`
  - Task đụng UI/MauiProgram: thêm `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`
  - **Không** dùng `dotnet build router-balancing.slnx` làm gate (NETSDK1082 pre-existing).
- Test flake đã biết: `ProxyControlApiTests`/`ProxyRetryIntegrationTests`/`ProxyQueueIntegrationTests` teardown → chạy lại PASS; test fail do app đang mở → đóng app, chạy lại.
- Migration command chuẩn:
  ```powershell
  dotnet ef migrations add AddModelToolsOutputLimit --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations
  ```
- **Chỉ stage đúng file của task** — working tree đang có file user tự sửa ngoài scope (IdleWatchdogStream, ClientKeyUsageSink…); không `git add -A`.

## Review Focus

Các kịch bản spec ngụ ý nhưng dễ bị bỏ sót — mỗi dòng đã gắn test ở task sở hữu:

1. Catalog thật (5.8MB, nhiều field lạ: `limit.input`, `interleaved`, `canonical_model_id`…) — parser phải bỏ qua field lạ và map đúng entry thật, không crash → Task 1 `ParseOpenCodeCatalog_RealCatalogSlice_ParsesLiveShape`.
2. Upstream đổi shape / trả 0 free → **không** được xoá sạch list cũ (guard giữ nguyên hành vi) → Task 3 `SyncProviderAsync_CatalogFreeSetEmpty_ThrowsAndKeepsExisting`.
3. Proxy chưa chạy lúc sinh config → baseURL dùng port settings, không crash/không rỗng → Task 5 `BuildBaseUrl_..._FallsBackToSettings`.
4. `reasoning_options` effort giá trị lạ (`minimal`, `xhigh`…) → pass-through nguyên trạng (opencode tự validate), không nuốt/bỏ giá trị → Task 4 `Generate_ExoticEffortValues_PassThroughExactly`.
5. Hai provider cùng `ModelId` (model free trùng tên ở provider khác) → JSON chỉ 1 key, ưu tiên bản preset free, có ghi chú UI → Task 5 `GenerateAsync_DuplicateIds_KeepsFreePresetThenIdOrder_AndReportsSkipped`.

## File Structure

| Hành động | File | Trách nhiệm |
|---|---|---|
| Modify | `src/RouterBalancing.Core/Providers/FreeProviderCatalog.cs` | + kind `OpenCodeCatalog`, + `CatalogUrl`, + `FindByBaseUrl` |
| Modify | `src/RouterBalancing.Core/Providers/FreeModelDetector.cs` | + params `FreeModelInfo`, + `ParseOpenCodeCatalog` |
| Modify | `src/RouterBalancing.Core/Providers/FreeModelSyncService.cs` | fetch catalog URL + parse switch + merge capabilities |
| Modify | `src/RouterBalancing.Core/Domain/Entities/Model.cs` | + `SupportsTools`, `MaxOutputTokens` |
| Create | (dotnet-ef) `src/RouterBalancing.Core/Storage/Migrations/*_AddModelToolsOutputLimit.cs` | 2 cột mới trên `Models` |
| Create | `src/RouterBalancing.Core/Tools/OpenCodeConfigGenerator.cs` | generator JSON thuần (static) |
| Create | `src/RouterBalancing.Core/Tools/IOpenCodeConfigService.cs` | interface + records trang Tools |
| Create | `src/RouterBalancing.Core/Tools/OpenCodeConfigService.cs` | đọc DB, default tick, dedupe, `BuildBaseUrl` |
| Modify | `router-balancing/MauiProgram.cs` | DI `IOpenCodeConfigService` |
| Create | `router-balancing/Components/Pages/Tools.razor` | trang Tools |
| Modify | `router-balancing/Components/Layout/NavMenu.razor` | link Tools |
| Create | `router-balancing/wwwroot/rb-tools.js` | `rbTools.download` (blob) |
| Modify | `router-balancing/wwwroot/index.html` | `<script src="rb-tools.js">` |
| Modify | `src/RouterBalancing.Core/Localization/Translations.cs` | 23 key ×2 dict |
| Create | Tests: `Providers/FreeProviderCatalogTests.cs`, `Providers/OpenCodeCatalogParserTests.cs`, `Providers/FreeModelSyncCatalogTests.cs`, `Tools/OpenCodeConfigGeneratorTests.cs`, `Tools/OpenCodeConfigServiceTests.cs` | Test mới |
| Modify | Tests: `Providers/FreeModelFixtures.cs` | + 2 fixture catalog |

## Design decisions locked in this plan (spec gap → plan resolve)

1. **Parser tách riêng `ParseOpenCodeCatalog`** — root catalog `{"opencode":{"models":{...}}}` khác hẳn shape `{"data":[...]}`; `Parse(json, kind)` cũ giữ nguyên, kind `OpenCodeCatalog` không đi vào nó.
2. **`FreeModelInfo` mở rộng bằng optional params** — 3 kind cũ truyền 3 tham số đầu, test cũ không đổi.
3. **`Entry.CatalogUrl` là tham số thứ 4 (default null)**; fetch catalog: GET trực tiếp URL tuyệt đối, **không** auth header/không fingerprint, vẫn set `ProxyTarget.Current` theo provider (như request cũ).
4. **Merge dùng helper `ApplyCapabilities(model, info)`** cho cả insert/update; chỉ ghi field non-null; model `IsManual` không đụng (semantics §6.2.6 giữ nguyên).
5. **Generator = static class thuần `OpenCodeConfigGenerator` + service `IOpenCodeConfigService`** (spec §7.2 ghi lỏng `IOpenCodeConfigGenerator` — plan chốt tách 2 vai: pure + DB).
6. **Dedupe 2 lớp:** generator first-wins (defense); service sắp thứ tự (free preset trước, rồi `Id`) và trả `SkippedDuplicateModelIds` để UI ghi chú.
7. **Không có nút "Generate"** — preview tự cập nhật mỗi lần tick/bỏ tick; chưa chọn model → empty-state + Copy/Download disable (thay câu chữ spec §8).
8. **`OpenCodeConfigService.BuildBaseUrl(int? proxyPort, int settingsPort)`** static — test được nhánh proxy chưa chạy: `http://127.0.0.1:{proxyPort ?? settingsPort}/v1`.
9. **Nhận diện preset free ở trang Tools bằng `FindByBaseUrl`** (canonical, `OrdinalIgnoreCase`) — rename-safe, khớp spec C4/C7.
10. **Quy ước `ThinkEfforts`:** literal `"toggle"` + các effort value; catalog `reasoning_options: []` → null; option `effort` đầu tiên thắng; thứ tự serialize: `"toggle"` trước.
11. **Thứ tự key JSON sinh ra:** `name` → `capabilities` (`tools?`, `input`, `output`, `reasoning?`) → `limit` (`context?`, `output?`); **không bao giờ** emit `cost`/`disabled`.
12. **Không map audio/video** từ `modalities` vào `SupportsAudio/Video` (ngoài spec §7.2 — deferred).
13. i18n chốt 23 key; thay 2 key nháp spec §9: `tools.proxy.status` → tách `tools.proxy.running`/`tools.proxy.stopped`, bỏ `tools.models.freeGroup`.

---

### Task 1: Catalog kind + parser v2 + fixtures

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/FreeProviderCatalog.cs`
- Modify: `src/RouterBalancing.Core/Providers/FreeModelDetector.cs`
- Modify: `router balancing test/Providers/FreeModelFixtures.cs`
- Create: `router balancing test/Providers/OpenCodeCatalogParserTests.cs`
- Create: `router balancing test/Providers/FreeProviderCatalogTests.cs`

**Interfaces:**
- Consumes: (không)
- Produces:
  - `FreeDetectKind.OpenCodeCatalog` (giá trị enum mới).
  - `FreeProviderCatalog.Entry(string DisplayName, string BaseUrl, FreeDetectKind DetectKind, string? CatalogUrl = null)`; entry OpenCode Free: `new("OpenCode Free", "https://opencode.ai/zen", FreeDetectKind.OpenCodeCatalog, "https://models.opencode.ai/api.json")`.
  - `static Entry? FreeProviderCatalog.FindByBaseUrl(string baseUrl)`.
  - `sealed record FreeModelInfo(string ModelId, string? DisplayName, int? ContextWindow, bool? SupportsTools = null, bool? SupportsVision = null, bool? SupportsThink = null, string? ThinkEfforts = null, string? InputModalities = null, string? OutputModalities = null, int? MaxOutputTokens = null)`.
  - `static IReadOnlyList<FreeModelInfo> FreeModelDetector.ParseOpenCodeCatalog(string json)` — throws `JsonException`/`FreeModelSyncException`.

- [ ] **Step 1: Thêm 2 fixture catalog vào `FreeModelFixtures.cs`**

```csharp
/// <summary>Catalog models.opencode.ai — shape thật 2026-10-19 (rút gọn): free / half-free / paid / deprecated / toggle.</summary>
public const string OpenCodeCatalog = """
{
  "opencode": {
    "models": {
      "big-pickle": { "name": "Big Pickle", "attachment": false, "tool_call": true, "reasoning": true,
        "reasoning_options": [], "modalities": { "input": ["text"], "output": ["text"] },
        "cost": { "input": 0, "output": 0, "cache_read": 0 }, "limit": { "context": 200000, "output": 32000 } },
      "deepseek-v4.1-flash-free": { "name": "DeepSeek V4.1 Flash Free", "attachment": true, "tool_call": true, "reasoning": true,
        "reasoning_options": [ { "type": "effort", "values": ["low", "high", "max"] } ],
        "modalities": { "input": ["text", "image"], "output": ["text"] },
        "cost": { "input": 0, "output": 0 }, "limit": { "context": 200000, "output": 32000 } },
      "toggle-model": { "tool_call": true, "reasoning": true,
        "reasoning_options": [ { "type": "toggle" } ],
        "cost": { "input": 0, "output": 0 } },
      "dead-model-free": { "cost": { "input": 0, "output": 0 }, "status": "deprecated" },
      "paid-model": { "cost": { "input": 3, "output": 15 } },
      "half-free": { "cost": { "input": 0, "output": 2 } }
    }
  }
}
""";

/// <summary>2 entry THẬT từ snapshot (giữ field lạ: description/family/interleaved/provider/canonical_model_id/limit.input…)
/// + 1 model thật deprecated — chứng minh parser bỏ qua field lạ và map đúng shape sống.</summary>
public const string OpenCodeCatalogRealSlice = """
{
  "opencode": {
    "models": {
      "big-pickle": { "id": "big-pickle", "name": "Big Pickle", "description": "Reasoning model for deliberate analysis",
        "family": "big-pickle", "attachment": false, "reasoning": true, "reasoning_options": [], "tool_call": true,
        "interleaved": { "field": "reasoning_content" }, "structured_output": true, "temperature": true,
        "knowledge": "2025-01", "release_date": "2025-10-17", "last_updated": "2025-10-17",
        "modalities": { "input": ["text"], "output": ["text"] }, "open_weights": false,
        "limit": { "context": 200000, "input": 160000, "output": 32000 },
        "provider": { "api": "https://opencode.ai/inference/openai/v1" },
        "cost": { "input": 0, "output": 0, "cache_read": 0, "cache_write": 0 } },
      "nemotron-3-ultra-free": { "id": "nemotron-3-ultra-free", "name": "Nemotron 3 Ultra Free", "family": "nemotron-free",
        "attachment": false, "reasoning": true, "reasoning_options": [], "tool_call": true, "temperature": true,
        "knowledge": "2026-02", "modalities": { "input": ["text"], "output": ["text"] }, "open_weights": true,
        "limit": { "context": 1000000, "output": 128000 },
        "provider": { "api": "https://opencode.ai/inference/openai/v1" },
        "cost": { "input": 0, "output": 0, "cache_read": 0 },
        "canonical_model_id": "nvidia/nemotron-3-ultra-550b-a55b" },
      "muse-spark-1.3-contributor-free": { "id": "muse-spark-1.3-contributor-free", "name": "Muse Spark 1.3 Free",
        "attachment": true, "reasoning": true, "reasoning_options": [ { "type": "effort", "values": ["minimal", "low", "medium", "high", "xhigh"] } ],
        "tool_call": true, "modalities": { "input": ["text", "image", "video", "pdf", "audio"], "output": ["text"] },
        "limit": { "context": 1048576, "output": 131072 }, "status": "deprecated",
        "provider": { "npm": "@ai-sdk/openai", "api": "https://opencode.ai/inference/openai/v1" },
        "cost": { "input": 0, "output": 0, "cache_read": 0 } }
    }
  }
}
""";
```

- [ ] **Step 2: Viết test fail (khai báo vỏ trước cho compile)**

Thêm vào `FreeProviderCatalog.cs`: enum value `OpenCodeCatalog` (kèm XML doc tham chiếu spec), `CatalogUrl` param, `FindByBaseUrl`; sửa entry OpenCode Free. Thêm vào `FreeModelDetector.cs`: record `FreeModelInfo` mở rộng + `ParseOpenCodeCatalog` **throw `NotImplementedException`**.

`FreeProviderCatalogTests.cs`:

```csharp
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class FreeProviderCatalogTests
{
    [Fact]
    public void Entries_OpenCodeFree_HasCatalogUrlAndCatalogKind()
    {
        var entry = FreeProviderCatalog.Entries.Single(e => e.DisplayName == "OpenCode Free");
        Assert.Equal(FreeDetectKind.OpenCodeCatalog, entry.DetectKind);
        Assert.Equal("https://models.opencode.ai/api.json", entry.CatalogUrl);
    }

    [Fact]
    public void FindByBaseUrl_CanonicalAndCaseInsensitive_Matches()
    {
        Assert.NotNull(FreeProviderCatalog.FindByBaseUrl("HTTPS://OpenCode.ai/zen"));
        Assert.Null(FreeProviderCatalog.FindByBaseUrl("https://example.com"));
    }
}
```

`OpenCodeCatalogParserTests.cs`:

```csharp
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class OpenCodeCatalogParserTests
{
    [Fact]
    public void ParseOpenCodeCatalog_FiltersFreeActiveAndMapsCapabilities()
    {
        var models = FreeModelDetector.ParseOpenCodeCatalog(FreeModelFixtures.OpenCodeCatalog);

        Assert.Equal(new[] { "big-pickle", "deepseek-v4.1-flash-free", "toggle-model" },
            models.Select(m => m.ModelId));

        var big = models.Single(m => m.ModelId == "big-pickle");
        Assert.Equal("Big Pickle", big.DisplayName);
        Assert.False(big.SupportsVision);            // attachment:false = biết chắc false (khác null = chưa rõ)
        Assert.True(big.SupportsTools);
        Assert.True(big.SupportsThink);
        Assert.Null(big.ThinkEfforts);               // reasoning_options rỗng
        Assert.Equal("[\"text\"]", big.InputModalities);
        Assert.Equal("[\"text\"]", big.OutputModalities);
        Assert.Equal(200000, big.ContextWindow);
        Assert.Equal(32000, big.MaxOutputTokens);

        var deepseek = models.Single(m => m.ModelId == "deepseek-v4.1-flash-free");
        Assert.True(deepseek.SupportsVision);
        Assert.Equal("[\"low\",\"high\",\"max\"]", deepseek.ThinkEfforts);
        Assert.Equal("[\"text\",\"image\"]", deepseek.InputModalities);
    }

    [Fact]
    public void ParseOpenCodeCatalog_ToggleOption_StoresToggleLiteral()
    {
        var toggle = FreeModelDetector.ParseOpenCodeCatalog(FreeModelFixtures.OpenCodeCatalog)
            .Single(m => m.ModelId == "toggle-model");
        Assert.Equal("[\"toggle\"]", toggle.ThinkEfforts);
        Assert.Null(toggle.DisplayName);             // thiếu name → null
        Assert.Null(toggle.MaxOutputTokens);         // thiếu limit → null
    }

    [Fact]
    public void ParseOpenCodeCatalog_DeprecatedCostZero_Excluded()
    {
        var models = FreeModelDetector.ParseOpenCodeCatalog(FreeModelFixtures.OpenCodeCatalog);
        Assert.DoesNotContain(models, m => m.ModelId == "dead-model-free");
    }

    [Fact]
    public void ParseOpenCodeCatalog_HalfZeroOrPaidCost_Excluded()
    {
        var models = FreeModelDetector.ParseOpenCodeCatalog(FreeModelFixtures.OpenCodeCatalog);
        Assert.DoesNotContain(models, m => m.ModelId == "half-free");   // output 2 ≠ 0
        Assert.DoesNotContain(models, m => m.ModelId == "paid-model");
    }

    [Fact]
    public void ParseOpenCodeCatalog_MissingOpencodeModels_Throws()
    {
        Assert.Throws<FreeModelSyncException>(() =>
            FreeModelDetector.ParseOpenCodeCatalog("""{"data":[]}"""));
    }

    [Fact]
    public void ParseOpenCodeCatalog_RealCatalogSlice_ParsesLiveShape()
    {
        var models = FreeModelDetector.ParseOpenCodeCatalog(FreeModelFixtures.OpenCodeCatalogRealSlice);

        Assert.Equal(new[] { "big-pickle", "nemotron-3-ultra-free" }, models.Select(m => m.ModelId));
        var nemotron = models.Single(m => m.ModelId == "nemotron-3-ultra-free");
        Assert.Equal(1000000, nemotron.ContextWindow);
        Assert.Equal(128000, nemotron.MaxOutputTokens);
        Assert.Equal("[\"text\"]", nemotron.InputModalities);
    }
}
```

- [ ] **Step 3: Chạy test xác nhận fail**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~OpenCodeCatalogParserTests|FullyQualifiedName~FreeProviderCatalogTests"`
Expected: FAIL — `NotImplementedException` (parser) và/hoặc `FindByBaseUrl` chưa có.

- [ ] **Step 4: Implement catalog (`FreeProviderCatalog.cs`)**

`FindByBaseUrl` (XML doc "rename-safe cho trang Tools"):

```csharp
public static Entry? FindByBaseUrl(string baseUrl)
{
    var canonical = ProviderUrl.Canonicalize(baseUrl);
    return Entries.FirstOrDefault(e =>
        string.Equals(e.BaseUrl, canonical, StringComparison.OrdinalIgnoreCase));
}
```

- [ ] **Step 5: Implement `ParseOpenCodeCatalog` (`FreeModelDetector.cs`)**

Bảng map (từng entry = property của `opencode.models`, dùng **tên property** làm `ModelId`):

| Catalog path | `FreeModelInfo` | Ghi chú |
|---|---|---|
| `cost.input`, `cost.output` | (điều kiện free) | cả hai `== 0`; thiếu `cost` → không free; dùng lại `IsZero` (số/chuỗi) |
| `status == "deprecated"` | (loại) | so sánh `OrdinalIgnoreCase`; thiếu status = active |
| `name` | `DisplayName` | string không rỗng, ngược lại null |
| `tool_call` | `SupportsTools` | bool?; thiếu field → null |
| `attachment` | `SupportsVision` | bool? |
| `reasoning` | `SupportsThink` | bool? |
| `reasoning_options[]` | `ThinkEfforts` | option `"toggle"` → literal `"toggle"`; option `"effort"` **đầu tiên** → values; serialize `["toggle", ...values]`; rỗng → null |
| `modalities.input/.output` | `Input/OutputModalities` | array string → JSON serialize; thiếu/rỗng → null |
| `limit.context` | `ContextWindow` | number → int? |
| `limit.output` | `MaxOutputTokens` | number → int? |

Cấu trúc: parse root → thiếu `opencode.models` (object) → `throw new FreeModelSyncException("Catalog response is missing opencode.models.")`; loop `EnumerateObject()`, skip value không phải object; giữ thứ tự trong JSON.

- [ ] **Step 6: Chạy test xác nhận pass + gate**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~OpenCodeCatalogParserTests|FullyQualifiedName~FreeProviderCatalogTests"`
Expected: PASS. Sau đó full gate: build Core + full `dotnet test`.

- [ ] **Step 7: Commit**

```bash
git add src/RouterBalancing.Core/Providers/FreeProviderCatalog.cs src/RouterBalancing.Core/Providers/FreeModelDetector.cs "router balancing test/Providers/FreeModelFixtures.cs" "router balancing test/Providers/OpenCodeCatalogParserTests.cs" "router balancing test/Providers/FreeProviderCatalogTests.cs"
git commit -m "feat: parse free models from opencode v2 catalog"
```

---

### Task 2: Entity `Model` + migration

**Files:**
- Modify: `src/RouterBalancing.Core/Domain/Entities/Model.cs`
- Create: (dotnet-ef) `src/RouterBalancing.Core/Storage/Migrations/*_AddModelToolsOutputLimit.cs`

**Interfaces:**
- Consumes: (không)
- Produces: `Model.SupportsTools` (`bool?`), `Model.MaxOutputTokens` (`int?`) — Task 3 ghi, Task 5 đọc.

- [ ] **Step 1: Thêm 2 property vào `Model.cs`**

Sau `SupportsThink` (dòng ~27) và sau `ContextWindow` (dòng ~23) tương ứng:

```csharp
/// <summary>Model hỗ trợ tool/function calling; null = chưa rõ (khác các bool cũ: false = không/chưa rõ).</summary>
public bool? SupportsTools { get; set; }

/// <summary>Giới hạn token output; null = chưa rõ.</summary>
public int? MaxOutputTokens { get; set; }
```

- [ ] **Step 2: Tạo migration**

Run (app đang mở → đóng trước):
```powershell
dotnet ef migrations add AddModelToolsOutputLimit --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations
```
Kiểm tra file sinh ra: đúng 2 `AddColumn` trên `Models` — `SupportsTools` INTEGER nullable + `MaxOutputTokens` INTEGER nullable; **không** có thay đổi nào khác.

- [ ] **Step 3: Gate**

Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj` → PASS.
Run: `dotnet test "router balancing test/router balancing test.csproj"` → PASS (TestDb migrate schema mới; suite xanh).

- [ ] **Step 4: Commit**

```bash
git add src/RouterBalancing.Core/Domain/Entities/Model.cs src/RouterBalancing.Core/Storage/Migrations/
git commit -m "feat: add model supports-tools and max-output-tokens columns"
```

---

### Task 3: Sync service — nhánh catalog + merge capabilities

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/FreeModelSyncService.cs`
- Create: `router balancing test/Providers/FreeModelSyncCatalogTests.cs`

**Interfaces:**
- Consumes: `FreeDetectKind.OpenCodeCatalog`, `Entry.CatalogUrl`, `FreeModelDetector.ParseOpenCodeCatalog`, `FreeModelInfo` (Task 1); `Model.SupportsTools`/`MaxOutputTokens` (Task 2).
- Produces: hành vi sync mới (không đổi public API service).

- [ ] **Step 1: Viết test fail (`Providers/FreeModelSyncCatalogTests.cs`)**

Theo pattern `FreeModelSyncMergeTests` (TestDb + `DbInitializer.Initialize` + `StubFactory`/`JsonHandler`/`NullLog` — copy cục bộ như các file test khác); `JsonHandler` thêm property `public Uri? LastRequestUri { get; private set; }` (gán trong `SendAsync`).

```csharp
[Fact]
public async Task SyncProviderAsync_OpenCodeCatalog_FetchesCatalogUrlAndWritesCapabilities()
{
    var id = await OpenCodeIdAsync();          // Providers.Single(p => p.Name == "OpenCode Free").Id
    var handler = new JsonHandler(FreeModelFixtures.OpenCodeCatalog);
    var service = new FreeModelSyncService(_db, _protector, new StubFactory(handler), new NullLog());

    var count = await service.SyncProviderAsync(id);

    Assert.Equal(3, count);
    Assert.Equal("https://models.opencode.ai/api.json", handler.LastRequestUri!.ToString());
    using var db = _db.CreateDbContext();
    var big = await db.Models.AsNoTracking().SingleAsync(m => m.ProviderId == id && m.ModelId == "big-pickle");
    Assert.True(big.Enabled);
    Assert.False(big.IsManual);
    Assert.Equal(200000, big.ContextWindow);
    Assert.Equal(32000, big.MaxOutputTokens);
    Assert.True(big.SupportsTools);
    Assert.True(big.SupportsThink);
    Assert.False(big.SupportsVision);
}

[Fact]
public async Task SyncProviderAsync_ModelLeavesCatalog_DeletesNonManualKeepsManual()
{
    var id = await OpenCodeIdAsync();
    await AddModelAsync(id, "jev-1.13-free", isManual: false);   // model legacy, không còn trong catalog (U5)
    await AddModelAsync(id, "my-manual-model", isManual: true);
    var service = ServiceWith(FreeModelFixtures.OpenCodeCatalog);

    await service.SyncProviderAsync(id);

    using var db = _db.CreateDbContext();
    var ids = await db.Models.AsNoTracking().Where(m => m.ProviderId == id).Select(m => m.ModelId).ToListAsync();
    Assert.DoesNotContain("jev-1.13-free", ids);
    Assert.Contains("my-manual-model", ids);
    Assert.Contains("big-pickle", ids);
}

[Fact]
public async Task SyncProviderAsync_CatalogFreeSetEmpty_ThrowsAndKeepsExisting()
{
    var id = await OpenCodeIdAsync();
    await AddModelAsync(id, "old-free", isManual: false,
        displayName: "Old");                                     // có sẵn 1 model cũ
    var service = ServiceWith(FreeModelFixtures.OpenCodeCatalogAllPaid);  // fixture mới: chỉ model paid
    await Assert.ThrowsAsync<FreeModelSyncException>(() => service.SyncProviderAsync(id));

    using var db = _db.CreateDbContext();
    Assert.Contains(await db.Models.AsNoTracking().ToListAsync(),
        m => m.ProviderId == id && m.ModelId == "old-free");     // không bị xoá
}
```

Thêm fixture `OpenCodeCatalogAllPaid` vào `FreeModelFixtures.cs` (2 model cost > 0, cùng shape root catalog, models rỗng cũng được):

```csharp
/// <summary>Catalog toàn model trả phí — detector lọc rỗng → guard không xoá list.</summary>
public const string OpenCodeCatalogAllPaid = """
{ "opencode": { "models": { "paid-a": { "cost": { "input": 3, "output": 15 } } } } }
""";
```

- [ ] **Step 2: Chạy test xác nhận fail**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~FreeModelSyncCatalogTests"`
Expected: FAIL — request đi `https://opencode.ai/zen/v1/models` (không phải catalog URL) / assertion capabilities fail.

- [ ] **Step 3: Implement (`FreeModelSyncService.cs`)**

Ba sửa đổi:

1. Call site: `using var response = await SendModelListAsync(provider, entry, key, ct);`; đổi chữ ký `SendModelListAsync(Provider provider, FreeProviderCatalog.Entry entry, string key, CancellationToken ct)` — request:
```csharp
using var request = entry.CatalogUrl is { Length: > 0 } catalogUrl
    ? new HttpRequestMessage(HttpMethod.Get, catalogUrl)     // catalog public: không auth, không fingerprint
    : ProviderRequestFactory.Create(provider, key);
```
(giữ nguyên `ProxyTarget.Current` + catch timeout như cũ).

2. Parse:
```csharp
free = entry.DetectKind == FreeDetectKind.OpenCodeCatalog
    ? FreeModelDetector.ParseOpenCodeCatalog(json)
    : FreeModelDetector.Parse(json, entry.DetectKind);
```

3. Merge — thay 3 dòng ghi DisplayName/ContextWindow (cả 2 nhánh) bằng helper gọi chung:
```csharp
private static void ApplyCapabilities(Model model, FreeModelInfo info)
{
    if (info.SupportsTools is not null) model.SupportsTools = info.SupportsTools;
    if (info.SupportsVision is not null) model.SupportsVision = info.SupportsVision.Value;
    if (info.SupportsThink is not null) model.SupportsThink = info.SupportsThink.Value;
    if (info.ThinkEfforts is not null) model.ThinkEfforts = info.ThinkEfforts;
    if (info.InputModalities is not null) model.InputModalities = info.InputModalities;
    if (info.OutputModalities is not null) model.OutputModalities = info.OutputModalities;
    if (info.MaxOutputTokens is not null) model.MaxOutputTokens = info.MaxOutputTokens;
}
```
- Nhánh update (sau `if (model.IsManual) continue;`): `DisplayName`/`ContextWindow` như cũ + `ApplyCapabilities(model, info);`
- Nhánh insert: thêm `SupportsTools = info.SupportsTools, SupportsVision = info.SupportsVision ?? false, SupportsThink = info.SupportsThink ?? false, ThinkEfforts = info.ThinkEfforts, InputModalities = info.InputModalities, OutputModalities = info.OutputModalities, MaxOutputTokens = info.MaxOutputTokens`.

- [ ] **Step 4: Chạy test xác nhận pass + gate**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~FreeModelSync"`
Expected: PASS (cả test cũ — semantics merge không đổi). Sau đó full gate: build Core + full `dotnet test`.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Providers/FreeModelSyncService.cs "router balancing test/Providers/FreeModelSyncCatalogTests.cs" "router balancing test/Providers/FreeModelFixtures.cs"
git commit -m "feat: sync opencode free models from v2 catalog"
```

---

### Task 4: Generator JSON thuần

**Files:**
- Create: `src/RouterBalancing.Core/Tools/OpenCodeConfigGenerator.cs`
- Create: `router balancing test/Tools/OpenCodeConfigGeneratorTests.cs`

**Interfaces:**
- Consumes: (không — chỉ string/int/bool input).
- Produces:
  - `sealed record OpenCodeConfigModel(string ModelId, string? DisplayName, int? ContextWindow, int? MaxOutputTokens, bool? SupportsTools, bool SupportsVision, bool SupportsThink, string? ThinkEfforts, string? InputModalities, string? OutputModalities)`.
  - `static class OpenCodeConfigGenerator` với `const string ProviderId = "router-balancing"`, `ProviderName = "Router Balancing"`, `ProviderPackage = "@opencode/ai/providers/openai-compatible"`; `static string Generate(string baseUrl, string? apiKey, IReadOnlyList<OpenCodeConfigModel> models)`.

- [ ] **Step 1: Viết test fail**

`Tools/OpenCodeConfigGeneratorTests.cs` (namespace `router_balancing_test.Tools`; assert bằng `JsonDocument` parse, không so chuỗi). Helper:

```csharp
private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;
private static JsonElement Provider(string json) =>
    Root(json).GetProperty("providers").GetProperty(OpenCodeConfigGenerator.ProviderId);
private static JsonElement Model(string json, string id) => Provider(json).GetProperty("models").GetProperty(id);

private static OpenCodeConfigModel Minimal(string id) =>
    new(id, null, null, null, null, false, false, null, null, null);
```

Các test (đủ assertion):

```csharp
[Fact]
public void Generate_EmitsProviderShapeAndMinimalModel()
{
    var json = OpenCodeConfigGenerator.Generate("http://127.0.0.1:8317/v1", null, [Minimal("m1")]);
    var p = Provider(json);
    Assert.Equal("Router Balancing", p.GetProperty("name").GetString());
    Assert.Equal("@opencode/ai/providers/openai-compatible", p.GetProperty("package").GetString());
    Assert.Equal("http://127.0.0.1:8317/v1", p.GetProperty("settings").GetProperty("baseURL").GetString());
    Assert.False(p.GetProperty("settings").TryGetProperty("apiKey", out _));
    Assert.False(Root(json).TryGetProperty("$schema", out _));             // C6: không emit $schema
    var m = Model(json, "m1");
    Assert.Equal("m1", m.GetProperty("name").GetString());                 // fallback ModelId
    Assert.Equal(new[] { "text" },                                  // semantic: emit JSON indent — đừng so raw text
        m.GetProperty("capabilities").GetProperty("input").EnumerateArray().Select(e => e.GetString()));
    Assert.False(m.GetProperty("capabilities").TryGetProperty("tools", out _));
    Assert.False(m.GetProperty("capabilities").TryGetProperty("reasoning", out _));
    Assert.False(m.TryGetProperty("limit", out _));                        // cả 2 null → không emit
    Assert.False(m.TryGetProperty("cost", out _));                         // U1
    Assert.False(m.TryGetProperty("disabled", out _));                     // U2
}

[Fact]
public void Generate_VisionWithoutModalities_InputsTextImage()
{
    var m = new OpenCodeConfigModel("m1", "Một", 100000, 8000, true, true, false, null, null, null);
    var json = OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]);
    var caps = Model(json, "m1").GetProperty("capabilities");
    Assert.Equal(new[] { "text", "image" },
        caps.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
    Assert.True(caps.GetProperty("tools").GetBoolean());
    Assert.Equal(100000, Model(json, "m1").GetProperty("limit").GetProperty("context").GetInt32());
}

[Fact]
public void Generate_ReasoningToggle_EmitsToggleAndEffort_AndExoticValuesPassThrough()
{
    // ThinkEfforts = ["toggle","minimal","xhigh"] — giá trị lạ pass-through nguyên trạng (Review Focus)
    var m = new OpenCodeConfigModel("m1", null, null, null, null, false, true, "[\"toggle\",\"minimal\",\"xhigh\"]", null, null);
    var reasoning = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
        .GetProperty("capabilities").GetProperty("reasoning");
    Assert.True(reasoning.GetProperty("supported").GetBoolean());
    Assert.True(reasoning.GetProperty("toggle").GetBoolean());
    Assert.Equal(new[] { "minimal", "xhigh" },
        reasoning.GetProperty("effort").EnumerateArray().Select(e => e.GetString()));
}

[Fact]
public void Generate_ThinkingSupportedOnly_NoToggleNoEffort()
{
    var m = new OpenCodeConfigModel("m1", null, null, null, null, false, true, null, null, null);
    var reasoning = Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
        .GetProperty("capabilities").GetProperty("reasoning");
    Assert.True(reasoning.GetProperty("supported").GetBoolean());
    Assert.False(reasoning.TryGetProperty("toggle", out _));
    Assert.False(reasoning.TryGetProperty("effort", out _));
}

[Fact]
public void Generate_ThinkingOff_OmitsReasoning()
{
    var m = new OpenCodeConfigModel("m1", null, null, null, null, false, false, "[\"low\"]", null, null);
    Assert.False(Model(OpenCodeConfigGenerator.Generate("http://x/v1", null, [m]), "m1")
        .GetProperty("capabilities").TryGetProperty("reasoning", out _));
}

[Fact]
public void Generate_ApiKeyIncludedOnlyWhenSet()
{
    var withKey = OpenCodeConfigGenerator.Generate("http://x/v1", "sk-rb-abc", [Minimal("m1")]);
    Assert.Equal("sk-rb-abc", Provider(withKey).GetProperty("settings").GetProperty("apiKey").GetString());
    var emptyKey = OpenCodeConfigGenerator.Generate("http://x/v1", "", [Minimal("m1")]);
    Assert.False(Provider(emptyKey).GetProperty("settings").TryGetProperty("apiKey", out _));
}

[Fact]
public void Generate_DuplicateModelIds_FirstWins()
{
    var json = OpenCodeConfigGenerator.Generate("http://x/v1", null,
        [Minimal("dup") with { DisplayName = "First" }, Minimal("dup") with { DisplayName = "Second" }]);
    Assert.Equal("First", Model(json, "dup").GetProperty("name").GetString());
    Assert.Single(Provider(json).GetProperty("models").EnumerateObject());
}

[Fact]
public void Generate_EmptyModelList_EmitsEmptyModelsRecord()
{
    var json = OpenCodeConfigGenerator.Generate("http://x/v1", null, []);
    Assert.Empty(Provider(json).GetProperty("models").EnumerateObject());
}
```

Test `Generate_NullLimitFields_Omitted`: model `ContextWindow=null, MaxOutputTokens=8000` → `limit` có `output` nhưng **không** có `context`.

- [ ] **Step 2: Chạy test xác nhận fail**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~OpenCodeConfigGeneratorTests"`
Expected: FAIL — `OpenCodeConfigGenerator` chưa tồn tại (compile) → thêm vỏ `Generate` throw `NotImplementedException` trước nếu cần red sạch.

- [ ] **Step 3: Implement (`OpenCodeConfigGenerator.cs`)**

Dùng `System.Text.Json.Nodes` (`JsonObject`/`JsonArray`), `ToJsonString(new JsonSerializerOptions { WriteIndented = true })`. Quy tắc:

- Root: `{"providers": {ProviderId: {name, package, settings, models}}}`; `settings.baseURL = baseUrl`; `settings.apiKey` chỉ khi `!string.IsNullOrEmpty(apiKey)`.
- Mỗi model: key order `name` → `capabilities` → `limit`; `name = DisplayName ?? ModelId`.
- `capabilities`: luôn có `input` + `output`; `tools` chỉ khi `SupportsTools is not null`; `reasoning` chỉ khi `SupportsThink`:
  - `input` = parse `InputModalities` (JSON array string) ?? (`SupportsVision ? ["text","image"] : ["text"]`); parse fail → coi như null.
  - `output` = parse `OutputModalities` ?? `["text"]`.
  - `reasoning` = `{"supported": true}` + `toggle: true` nếu efforts chứa literal `"toggle"` + `effort: [phần còn lại]` nếu còn.
- `limit`: chỉ emit khi `ContextWindow`/`MaxOutputTokens` khác null; từng field emit riêng khi non-null.
- Dedupe: `HashSet<string>` Ordinal, **first-wins**, bỏ qua bản sau.

- [ ] **Step 4: Chạy test xác nhận pass + gate**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~OpenCodeConfigGeneratorTests"` → PASS.
Full gate: build Core + full `dotnet test`.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Tools/OpenCodeConfigGenerator.cs "router balancing test/Tools/OpenCodeConfigGeneratorTests.cs"
git commit -m "feat: add opencode v2 config generator"
```

---

### Task 5: Service trang Tools (DB + default tick + dedupe)

**Files:**
- Create: `src/RouterBalancing.Core/Tools/IOpenCodeConfigService.cs`
- Create: `src/RouterBalancing.Core/Tools/OpenCodeConfigService.cs`
- Create: `router balancing test/Tools/OpenCodeConfigServiceTests.cs`

**Interfaces:**
- Consumes: `OpenCodeConfigGenerator` + `OpenCodeConfigModel` (Task 4); `FreeProviderCatalog.FindByBaseUrl`/`FreeDetectKind.OpenCodeCatalog` (Task 1).
- Produces (Task 6 UI dùng đúng các tên này):
  - `sealed record ToolsModelRow(long Id, string ModelId, string? DisplayName, int? ContextWindow, bool? SupportsTools, bool SupportsVision, bool SupportsThink)`
  - `sealed record ToolsModelGroup(string ProviderName, bool ProviderEnabled, bool IsFreePreset, IReadOnlyList<ToolsModelRow> Models)`
  - `sealed record OpenCodeConfigPageData(IReadOnlyList<ToolsModelGroup> Groups, IReadOnlyList<long> DefaultSelectedIds)`
  - `sealed record OpenCodeConfigResult(string Json, IReadOnlyList<string> SkippedDuplicateModelIds)`
  - `interface IOpenCodeConfigService { Task<OpenCodeConfigPageData> GetPageDataAsync(CancellationToken ct = default); Task<OpenCodeConfigResult> GenerateAsync(IReadOnlyList<long> selectedModelIds, string baseUrl, string? apiKey, CancellationToken ct = default); }`
  - `static string OpenCodeConfigService.BuildBaseUrl(int? proxyPort, int settingsPort)`

- [ ] **Step 1: Viết test fail (`Tools/OpenCodeConfigServiceTests.cs`)**

Pattern TestDb + `DbInitializer.Initialize(_db)` (seed 4 preset, `Enabled=false`).

```csharp
[Fact]
public async Task GetPageData_ProviderDisabled_FlagsGroupAndSkipsDefaultTick()
{
    // seed mặc định: preset Enabled=false
    var data = await Service().GetPageDataAsync();

    var free = data.Groups.Single(g => g.IsFreePreset);
    Assert.False(free.ProviderEnabled);
    Assert.Empty(data.DefaultSelectedIds);               // C7: provider tắt → không tick mặc định
    Assert.All(free.Models, m => Assert.True(m.Id > 0));
}

[Fact]
public async Task GetPageData_DefaultsToFreePresetModels_WhenProviderEnabled()
{
    await SetProviderEnabledAsync("OpenCode Free", true);
    await AddModelsAsync("OpenCode Free", ("big-pickle", true), ("hidden-model", false));

    var data = await Service().GetPageDataAsync();

    var free = data.Groups.Single(g => g.IsFreePreset);
    Assert.True(free.ProviderEnabled);
    Assert.Single(free.Models);                          // chỉ model Enabled được list
    Assert.Equal(data.DefaultSelectedIds, free.Models.Select(m => m.Id));
}

[Fact]
public async Task GenerateAsync_DuplicateIds_KeepsFreePresetThenIdOrder_AndReportsSkipped()
{
    await SetProviderEnabledAsync("OpenCode Free", true);
    var freeRow = await AddModelsAsync("OpenCode Free", ("shared-id", true, "Free copy"));
    var otherRow = await AddModelsAsync("OpenRouter Free", ("shared-id", true, "Other copy")); // provider khác, cùng ModelId

    var result = await Service().GenerateAsync([otherRow, freeRow], "http://127.0.0.1:8317/v1", null);

    Assert.Equal(new[] { "shared-id" }, result.SkippedDuplicateModelIds);
    var name = JsonDocument.Parse(result.Json).RootElement.GetProperty("providers")
        .GetProperty("router-balancing").GetProperty("models").GetProperty("shared-id").GetProperty("name");
    Assert.Equal("Free copy", name.GetString());   // preset OpenCode Free thắng (IsFreePreset sort trước)
}

[Fact]
public void BuildBaseUrl_UsesProxyPort_WhenRunning_FallsBackToSettings()
{
    Assert.Equal("http://127.0.0.1:9000/v1", OpenCodeConfigService.BuildBaseUrl(9000, 8317));
    Assert.Equal("http://127.0.0.1:8317/v1", OpenCodeConfigService.BuildBaseUrl(null, 8317));
}

[Fact]
public async Task GenerateAsync_EmptySelection_EmitsEmptyModels()
{
    var result = await Service().GenerateAsync([], "http://127.0.0.1:8317/v1", null);
    Assert.Empty(JsonDocument.Parse(result.Json).RootElement.GetProperty("providers")
        .GetProperty("router-balancing").GetProperty("models").EnumerateObject());
}
```

Test `GetPageData_ListsOnlyEnabledModels_SkipsEmptyGroups`: provider bật + 0 model enabled → group không xuất hiện.

`GenerateAsync` order quy tắc (chốt): sort `IsFreePreset desc, Id asc`; model đầu mỗi `ModelId` vào JSON, bản sau vào `SkippedDuplicateModelIds`.

- [ ] **Step 2: Chạy test xác nhận fail**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~OpenCodeConfigServiceTests"`
Expected: FAIL — type chưa tồn tại (compile) → thêm vỏ interface/records + `NotImplementedException` nếu cần red sạch.

- [ ] **Step 3: Implement (`IOpenCodeConfigService.cs` + `OpenCodeConfigService.cs`)**

`GetPageDataAsync`:
1. `db.Providers.AsNoTracking().Include(p => p.Models).OrderBy(p => p.Id)`.
2. `isFreePreset = p.IsPreset && FreeProviderCatalog.FindByBaseUrl(p.BaseUrl)?.DetectKind == FreeDetectKind.OpenCodeCatalog`.
3. Models `Enabled`, order `Id`; skip provider không có model enabled nào.
4. `DefaultSelectedIds` = model của group `IsFreePreset && ProviderEnabled` (C7).

`GenerateAsync`:
1. `db.Models.AsNoTracking().Include(m => m.Provider).Where(m => selectedModelIds.Contains(m.Id))`.
2. Order `IsFreePreset(provider) desc, Id asc`; dedupe `HashSet<string>` Ordinal, bản sau → `skipped`.
3. Map sang `OpenCodeConfigModel` → `OpenCodeConfigGenerator.Generate(baseUrl, apiKey, picked)`.

`BuildBaseUrl`:
```csharp
/// <summary>URL proxy cho opencode: port thật khi đang chạy, không thì port settings (§7.1.1 spec).</summary>
public static string BuildBaseUrl(int? proxyPort, int settingsPort) =>
    $"http://127.0.0.1:{proxyPort ?? settingsPort}/v1";
```

- [ ] **Step 4: Chạy test xác nhận pass + gate**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~OpenCodeConfigServiceTests"` → PASS.
Full gate: build Core + full `dotnet test`.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Tools/IOpenCodeConfigService.cs src/RouterBalancing.Core/Tools/OpenCodeConfigService.cs "router balancing test/Tools/OpenCodeConfigServiceTests.cs"
git commit -m "feat: add opencode config service with default selection"
```

---

### Task 6: UI — trang Tools + nav + i18n + JS + DI

**Files:**
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs`
- Modify: `router-balancing/Components/Layout/NavMenu.razor`
- Modify: `router-balancing/wwwroot/index.html`
- Modify: `router-balancing/MauiProgram.cs`
- Create: `router-balancing/wwwroot/rb-tools.js`
- Create: `router-balancing/Components/Pages/Tools.razor`

**Interfaces:**
- Consumes: `IOpenCodeConfigService` + records (Task 5); `IProxyHost.Port`/`IsRunning`; `IAppSettingsService.Port`; `IClientKeyService.ListAsync/CreateAsync` + `ClientKeyDraft`; `IClipboardService.CopyAsync`; `ToastService.Show`; `LocalizationService`; `IJSRuntime`.
- Produces: trang `/tools`; JS `window.rbTools.download(filename, text)`.

- [ ] **Step 1: i18n — 23 key vào cả 2 dict `Translations.cs`**

EN (đặt cạnh các key `nav.*`/`tools.*` cuối dict English) / VI (cuối dict Vietnamese):

| Key | EN | VI |
|---|---|---|
| `nav.tools` | Tools | Công cụ |
| `tools.title` | OpenCode config | Cấu hình OpenCode |
| `tools.proxy.running` | Proxy is running | Proxy đang chạy |
| `tools.proxy.stopped` | Proxy is not running — start it in Settings; config can still be generated with the settings port. | Proxy chưa chạy — bật ở trang Cài đặt; vẫn sinh được config với port trong settings. |
| `tools.baseUrl` | Base URL | Base URL |
| `tools.apiKey.label` | Client key (optional) | Client key (tùy chọn) |
| `tools.apiKey.hint` | Only needed when the proxy has client keys enabled. Paste a key to include it in the config. | Chỉ cần khi proxy đã bật client key. Dán key để ghi kèm vào config. |
| `tools.apiKey.notNeeded` | No client keys enabled — a key is not required. | Proxy chưa bật client key — không cần key. |
| `tools.apiKey.create` | Create key for OpenCode | Tạo key cho OpenCode |
| `tools.apiKey.created` | Key created — copy it now, shown only once: | Đã tạo key — copy ngay, chỉ hiện một lần: |
| `tools.apiKey.warning` | This config contains the key — keep the file secret. | Config chứa key — giữ kín file này. |
| `tools.models.title` | Models to include | Model đưa vào config |
| `tools.selectFree` | Select free group | Chọn nhóm free |
| `tools.clearAll` | Clear all | Bỏ chọn hết |
| `tools.preview` | Preview | Xem trước |
| `tools.copy` | Copy | Sao chép |
| `tools.copied` | Copied | Đã sao chép |
| `tools.download` | Download | Tải file |
| `tools.empty` | No models selected | Chưa chọn model nào |
| `tools.hint.merge` | Merge this into your opencode.json(c), or use it as the config file. | Merge file này vào opencode.json(c), hoặc dùng làm file config. |
| `tools.provider.disabled` | Provider is disabled — its models will not be routed. | Provider đang tắt — model của nhóm này không được định tuyến. |
| `tools.free.cta` | Enable the OpenCode Free provider to preselect the free group. | Bật provider OpenCode Free để chọn sẵn nhóm free. |
| `tools.duplicates` | Some duplicate model ids were skipped. | Một số model trùng id đã bị bỏ qua. |

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~TranslationParityTests"` → PASS.

- [ ] **Step 2: NavMenu + JS + index.html + DI**

`NavMenu.razor` — thêm sau link Combos:
```razor
<NavLink class="rounded px-3 py-2 text-sm no-underline hover:bg-background"
         href="tools">
    @L["nav.tools"]
</NavLink>
```

`wwwroot/rb-tools.js` (style giống `rb-combo.js`):
```js
//#region rbTools — tiện ích trang Tools
// Tải text thành file: Blazor trong WebView2 không có dialog save native — dùng Blob URL
// + thẻ <a download>. UTF-8 giữ nguyên tiếng Việt trong file config.
window.rbTools = {
	download: function (filename, text) {
		var blob = new Blob([text], { type: "application/json;charset=utf-8" });
		var url = URL.createObjectURL(blob);
		var a = document.createElement("a");
		a.href = url;
		a.download = filename;
		document.body.appendChild(a);
		a.click();
		a.remove();
		URL.revokeObjectURL(url);
	}
};
//#endregion
```

`index.html`: thêm `<script src="rb-tools.js"></script>` ngay sau dòng `rb-combo.js`.

`MauiProgram.cs`: thêm `using RouterBalancing.Core.Tools;` + cạnh dòng 131:
```csharp
builder.Services.AddSingleton<IOpenCodeConfigService, OpenCodeConfigService>();
```

- [ ] **Step 3: Viết `Tools.razor`**

Khung (markup theo style `Providers.razor`: `div.mb-4` header, card `rounded border border-border bg-surface`, table `w-full text-sm`, `EmptyState`):

```razor
@page "/tools"
@using RouterBalancing.Core.Tools
@using RouterBalancing.Core.Settings
@implements IDisposable
@inject IOpenCodeConfigService ConfigSvc
@inject IProxyHost Proxy
@inject IAppSettingsService Settings
@inject IClientKeyService ClientKeys
@inject IClipboardService Clipboard
@inject IJSRuntime JS
@inject LocalizationService L
@inject ToastService Toast
@inject ILogService Log
```

State: `_loading`, `_groups` (`List<ToolsModelGroup>`), `_selected` (`HashSet<long>`), `_apiKey` (`string?`), `_keysEnabled` (bool), `_json` (`string?`), `_skipped` (`IReadOnlyList<string>`), `_newKeyPlaintext` (`string?`), `_baseUrl` (`string`).

Methods (pin tên):
- `OnInitializedAsync` → subscribe `Proxy.StateChanged` (refresh `StateHasChanged` qua `InvokeAsync` như `ApiMonitorPanel`) → `LoadAsync()`.
- `LoadAsync()` → `_baseUrl = OpenCodeConfigService.BuildBaseUrl(Proxy.Port, Settings.Port)`; `var data = await ConfigSvc.GetPageDataAsync(); _groups = data.Groups.ToList(); _selected = data.DefaultSelectedIds.ToHashSet();` `_keysEnabled = (await ClientKeys.ListAsync()).Any(k => k.Enabled);` → `_loading = false` → `RegenerateAsync()`.
- `RegenerateAsync()` → `_selected.Count == 0` ⇒ `_json = null; _skipped = [];` ngược lại `var r = await ConfigSvc.GenerateAsync(_selected.ToArray(), _baseUrl, _apiKey); _json = r.Json; _skipped = r.SkippedDuplicateModelIds;`
- `ToggleAsync(long id, bool on)` → cập nhật `_selected` → `RegenerateAsync()`.
- `SelectFreeAsync()` → add toàn bộ model của groups `IsFreePreset` → `RegenerateAsync()`.
- `ClearAllAsync()` → `_selected.Clear()` → `RegenerateAsync()`.
- `CopyAsync()` → `if (_json is null) return;` `await Clipboard.CopyAsync(_json)` → toast `tools.copied` (Success) / lỗi (Error).
- `DownloadAsync()` → `await JS.InvokeVoidAsync("rbTools.download", "opencode-router-balancing.jsonc", _json);`
- `CreateKeyAsync()` → `try { var (_, plaintext) = await ClientKeys.CreateAsync(new ClientKeyDraft("OpenCode", null, null)); _apiKey = plaintext; _newKeyPlaintext = plaintext; _keysEnabled = true; Toast.Show(L["tools.apiKey.created"] + " " + plaintext, Success); await RegenerateAsync(); } catch (Exception ex) { Log.Error("Create client key for OpenCode failed.", ex); Toast.Show(ex.Message, Error); }`
- `Dispose()` → unsubscribe `StateChanged`.

Markup (4 khối theo spec §7.1):
1. **Trạng thái proxy**: `_baseUrl` + text `tools.proxy.running`/`tools.proxy.stopped` (nền vàng khi stopped).
2. **Client key**: nếu `!_keysEnabled` → info `tools.apiKey.notNeeded`; luôn có input `@bind="_apiKey" @bind:after="RegenerateAsync"` + `tools.apiKey.hint`; button `tools.apiKey.create` (chỉ khi `_keysEnabled`); khi `_apiKey` non-empty → badge cảnh báo `tools.apiKey.warning`; hiện `_newKeyPlaintext` một lần.
3. **Chọn model**: header `tools.models.title` + buttons `tools.selectFree` / `tools.clearAll`; group header = `ProviderName` + badge đỏ `tools.provider.disabled` khi `!ProviderEnabled` + badge free khi `IsFreePreset`; group free nhưng provider tắt → note `tools.free.cta`; mỗi model 1 dòng: checkbox `checked="@_selected.Contains(m.Id)"` + `@onchange="e => ToggleAsync(m.Id, (bool)e.Value!)"`, tên (`DisplayName ?? ModelId`), context, badge caps (tools/vision/think — theo pattern badge của `Providers.razor`).
4. **Preview**: `_json is null` → `EmptyState` với `tools.empty`; ngược lại `<pre>` có scroll + `_skipped.Count > 0` → note `tools.duplicates`; buttons Copy/Download `disabled="@(_json is null)"`; hint `tools.hint.merge`.

- [ ] **Step 4: Gate**

Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj` → PASS.
Run: `dotnet test "router balancing test/router balancing test.csproj"` → PASS (parity + razor entity guard).
Run (app đã đóng): `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Localization/Translations.cs router-balancing/Components/Layout/NavMenu.razor router-balancing/Components/Pages/Tools.razor router-balancing/wwwroot/rb-tools.js router-balancing/wwwroot/index.html router-balancing/MauiProgram.cs
git commit -m "feat: add opencode config tools page"
```

---

### Task 7: Verification toàn bộ + smoke + PR

**Files:** (không sửa file — chỉ chạy gate + PR)

- [ ] **Step 1: Full gates**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # app đã đóng
```
Expected: cả 3 xanh (flake đã biết → chạy lại 1 lần trước khi kết luận đỏ).

- [ ] **Step 2: Smoke tay (user hoặc executer có app chạy)**

1. Providers → bật OpenCode Free → Sync → **10 model free**, có `big-pickle` + `nemotron-3-ultra-free`; `jev-1.13-free` biến mất; context hiện đúng (nemotron 1.000.000).
2. Trang Tools: proxy status đúng; nhóm free tick sẵn (khi provider bật); preview JSON có `providers.router-balancing`.
3. Copy + Tải file `opencode-router-balancing.jsonc`.
4. Merge vào config opencode của user → mở opencode → danh sách model có các model `router-balancing`.
5. Gọi thử 1 model free qua opencode → dashboard app thấy request.
6. (Nếu bật client key) tạo key qua nút trên trang Tools → config có `apiKey` → gọi lại thành công; tắt key sai → 401 (chỉ cảnh báo, không chặn generate).
7. Dừng proxy → cảnh báo hiện, generate vẫn chạy với port settings.

- [ ] **Step 3: PR**

```bash
git push -u origin feat/opencode-tools-page
gh pr create --title "feat: opencode v2 tools page + free sync catalog" --body "..."
```
(master protected — chờ CI xanh; merge theo flow thường của repo.)
