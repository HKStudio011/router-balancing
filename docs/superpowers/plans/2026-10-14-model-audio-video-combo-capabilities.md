# Model Audio/Video + Combo Capabilities Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thêm capability audio/video cho model (auto-detect + chỉnh tay), expose nested `capabilities` qua `/v1/models`, và combo có capabilities tính tự động (AND bool / min-ctx / null propagation / đệ quy).

**Architecture:** Entity `Model` thêm 2 cột bool (migration); metadata chain derive từ `InputModalities` JSON (không đổi interface); `UpdateCapabilitiesAsync` mở rộng + UI Providers 2 checkbox/badge mới; service mới `ComboCapabilityCalculator` tính on-the-fly (DI qua `ProxyApp.ConfigureServices` + `MauiProgram`); `/v1/models` đính kèm `capabilities` cho model lẫn combo; Combos.razor thêm cột read-only.

**Tech Stack:** .NET 10, EF Core 10 (SQLite, `Database.Migrate()`), xUnit v3 (MTP runner), Blazor (MAUI Hybrid), Tailwind.

**Spec:** `docs/superpowers/specs/2026-10-14-model-audio-video-combo-capabilities-design.md`

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên (AGENTS.md).
- Branch mới `feat/model-audio-video-capabilities` từ `master` (master bảo vệ — chỉ PR; PR xunit-v3 #17 xử lý riêng).
- Build gate: `dotnet build "router-balancing/router-balancing.csproj" -f net10.0-windows10.0.19041.0 --nologo -v q` (không build full slnx).
- Test: `dotnet test "router balancing test/router balancing test.csproj"` (thư mục có space; MTP — KHÔNG dùng `--nologo`, chỉ `--no-banner` nếu cần tắt banner). `SingleInstanceGuardTests` fail khi app đang chạy — môi trường; `Cancel_QueuedRequest_*`/`Stream_ClientAbortQueued_*` flaky — chạy lại.
- Migration tool: `dotnet ef migrations add <Name> --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design`.
- Comment: chỉ "why", không "what"; XML doc cho public API; tên test mô tả hành vi (`X_WhenY_Z`).
- Capabilities **informational only** — không thêm bất kỳ block/route-check nào ở dispatcher (spec Non-goals).
- Localization: thêm key vào `src/RouterBalancing.Core/Localization/Translations.cs` — file có 2 dictionary (VI + EN), thêm cùng key vào cả hai theo pattern hiện có.
- Commit theo conventional commit, message tiếng Anh; mỗi task 1 commit.

## Review Focus

1. **Combo cycle dirty data** — DB có thể chứa item trỏ quay ancestor (validate UI chặn nhưng data cũ/manual) → calculator phải skip edge quay lại, không treo/overflow. → Task 4 test `ComputeAll_WhenCycleInItems_SkipsBackEdge`.
2. **Phân biệt `false` vs `null` ở combo** — một member `false` → cả combo `false` (khác với member `null` → combo `null`); serialize phải giữ được cả hai. → Task 4 test `Vision_WhenAnyMemberFalse_ReturnsFalse` + `Vision_WhenAnyMemberNull_ReturnsNull`; Task 5 test serialize null tường minh.
3. **Min-ctx null propagation** — 1/3 member thiếu ctx → combo ctx `null` kể cả 2 member còn lại có ctx (không phải min của subset). → Task 4 test `ContextWindow_WhenAnyMemberUnset_ReturnsNull`.
4. **Derive không đè manual** — user đã tick audio=true tay, probe lại có `InputModalities` không chứa audio → giữ `true` (fill chỉ `false→true`). JSON modalities hỏng không được throw (fill best-effort). → Task 3 tests `TryFillAsync_ManualAudioTrue_NotClearedByModalities` + `TryFillAsync_BrokenModalitiesJson_ThrowsNo`.
5. **Migration Down/Default** — cột mới NOT NULL DEFAULT 0; Down drop sạch (test pattern `AddStatsJournalColumnsMigrationTests`). → Task 1 tests `Up_AddsColumnsWithDefault` + `Down_DropsColumns`.

---

### Task 1: Entity + Migration `AddModelAudioVideoCapabilities`

**Files:**
- Modify: `src/RouterBalancing.Core/Domain/Entities/Model.cs:25-27` (thêm 2 property sau `SupportsThink`)
- Create: `src/RouterBalancing.Core/Storage/Migrations/<timestamp>_AddModelAudioVideoCapabilities.cs` (+ Designer, snapshot — sinh bằng tool)
- Test: `router balancing test/Storage/AddModelAudioVideoCapabilitiesMigrationTests.cs`

**Interfaces:**
- Produces: `Model.SupportsAudio`, `Model.SupportsVideo` (bool, default `false`) — Task 2/3/4/5 đọc; false = chưa rõ (cùng convention `SupportsVision`, ModelMetadataService.cs:63-65).

- [ ] **Step 1: Viết test migration (fail — chưa có cột)**

Pattern `AddStatsJournalColumnsMigrationTests` (`PreviousId = "20261013125137_AddProviderExtraHeaders"`; `TargetId` tạm placeholder, sửa sau khi sinh migration):

```
Up_AddsColumnsWithDefault:
  Migrate(PreviousId)
  seed 1 provider + 1 model (raw SQL hoặc entity)
  Migrate(TargetId)
  assert pragma_table_info "Models": SupportsAudio + SupportsVideo tồn tại, "notnull"=1, "dflt_value"="0"
  assert model row 2 cột = 0

Down_DropsColumns:
  Migrate(TargetId) → Down về PreviousId → 2 cột biến mất
```

- [ ] **Step 2: Chạy test — fail**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~AddModelAudioVideoCapabilitiesMigrationTests"`
Expected: FAIL (chưa có migration/cột).

- [ ] **Step 3: Thêm property + sinh migration**

1. `Model.cs` thêm sau `SupportsThink`:
```csharp
/// <summary>Model nhận input audio; false = không hỗ trợ/chưa rõ (fill chỉ viết true).</summary>
public bool SupportsAudio { get; set; }

/// <summary>Model nhận input video; false = không hỗ trợ/chưa rõ (fill chỉ viết true).</summary>
public bool SupportsVideo { get; set; }
```
2. Sinh: `dotnet ef migrations add AddModelAudioVideoCapabilities --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design`
3. Kiểm tra Up() EF sinh `AddColumn<bool>(..., nullable: false, defaultValue: false)` — SQLite ra `INTEGER NOT NULL DEFAULT 0`, giữ nguyên; sửa `TargetId` test khớp tên migration thật.

- [ ] **Step 4: Chạy test — pass**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~AddModelAudioVideoCapabilitiesMigrationTests"`
Expected: PASS.

- [ ] **Step 5: Build gate**

Run: `dotnet build "router-balancing/router-balancing.csproj" -f net10.0-windows10.0.19041.0 --nologo -v q`
Expected: 0 Error.

- [ ] **Step 6: Commit**

```bash
git add "src/RouterBalancing.Core/Domain/Entities/Model.cs" "src/RouterBalancing.Core/Storage/Migrations/" "router balancing test/Storage/AddModelAudioVideoCapabilitiesMigrationTests.cs"
git commit -m "feat: add audio/video capability columns to model"
```

---

### Task 2: `UpdateCapabilitiesAsync` audio/video + UI Providers

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/IModelService.cs:40`
- Modify: `src/RouterBalancing.Core/Providers/ModelService.cs:217-240`
- Modify: `router-balancing/Components/Pages/Providers.razor` — form edit (250-276), badge (289-300), state fields (732-734), handlers (1087-1141)
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (2 dictionary: thêm `models.capabilities.audio`, `models.capabilities.video`, `models.badge.audio`, `models.badge.video`)
- Test: `router balancing test/Providers/ModelServiceTests.cs` (mở rộng `UpdateCapabilities_*`)

**Interfaces:**
- Consumes: `Model.SupportsAudio/SupportsVideo` (Task 1).
- Produces: `Task UpdateCapabilitiesAsync(long modelId, int? contextWindow, bool supportsVision, bool supportsThink, bool supportsAudio, bool supportsVideo, CancellationToken ct = default)` — Providers.razor gọi (Task này); test khác sửa theo.

- [ ] **Step 1: Sửa test hiện có + thêm test mới (fail — service chưa nhận param)**

`ModelServiceTests.cs` — cập nhật các call `UpdateCapabilitiesAsync` hiện có (lines ~298-365) thêm `supportsAudio: false, supportsVideo: false` cho overload cũ; thêm:

```
UpdateCapabilities_WhenAudioVideoSet_PersistsBoth:
  seed model → UpdateCapabilitiesAsync(..., supportsAudio: true, supportsVideo: true)
  assert model.SupportsAudio && model.SupportsVideo

UpdateCapabilities_WhenAudioVideoCleared_ClearsBoth:
  seed model (đã true) → UpdateCapabilitiesAsync(..., supportsAudio: false, supportsVideo: false)
  assert !model.SupportsAudio && !model.SupportsVideo
```

- [ ] **Step 2: Chạy test — fail (compile error CS7036 — signature chưa có)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ModelServiceTests.UpdateCapabilities"`
Expected: FAIL (compile error).

- [ ] **Step 3: Mở rộng service**

`IModelService` + `ModelService.UpdateCapabilitiesAsync`: thêm 2 param `bool supportsAudio, bool supportsVideo` sau `supportsThink`; gán cạnh 230-232 (`model.SupportsAudio = supportsAudio; model.SupportsVideo = supportsVideo;`), XML doc cập nhật.

- [ ] **Step 4: Chạy test — pass**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ModelServiceTests.UpdateCapabilities"`
Expected: PASS.

- [ ] **Step 5: UI Providers.razor + translations**

1. State: thêm `private bool _editAudio;` `private bool _editVideo;` sau `_editThink` (734).
2. Form (sau label think, 262-265): 2 label checkbox tương tự `@bind="_editAudio"` / `@bind="_editVideo"`, text `@L["models.capabilities.audio"]` / `@L["models.capabilities.video"]`.
3. Badge (sau badge think, 293-296):
```razor
@if (m.SupportsAudio) { <Badge Text="@L["models.badge.audio"]" Variant="BadgeVariant.Info" /> }
@if (m.SupportsVideo) { <Badge Text="@L["models.badge.video"]" Variant="BadgeVariant.Neutral" /> }
```
4. Placeholder condition (297): thêm `&& !m.SupportsAudio && !m.SupportsVideo`.
5. `BeginEditCapabilities` (1092-1094): load `_editAudio = model.SupportsAudio; _editVideo = model.SupportsVideo;`.
6. `SaveCapabilitiesAsync` (1122): gửi thêm `_editAudio, _editVideo`.
7. `Translations.cs`: thêm 2 key vào dictionary VI + EN — badge: `models.badge.audio` = "🔊 audio"/"Audio", `models.badge.video` = "🎬 video"/"Video"; form label: `models.capabilities.audio` = "Audio", `models.capabilities.video` = "Video" (theo pattern key hiện có, 2 chỗ ~line 299 và ~line 767).

- [ ] **Step 6: Build + test toàn cục**

Run: `dotnet build "router-balancing/router-balancing.csproj" -f net10.0-windows10.0.19041.0 --nologo -v q && dotnet test "router balancing test/router balancing test.csproj"`
Expected: build 0 Error; test PASS (trừ môi trường/flaky đã biết).

- [ ] **Step 7: Commit**

```bash
git add "src/RouterBalancing.Core/Providers/IModelService.cs" "src/RouterBalancing.Core/Providers/ModelService.cs" "router-balancing/Components/Pages/Providers.razor" "src/RouterBalancing.Core/Localization/Translations.cs" "router balancing test/Providers/ModelServiceTests.cs"
git commit -m "feat: extend model capability editing with audio and video"
```

---

### Task 3: Derive audio/video từ `InputModalities` trong metadata fill

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/ModelMetadataService.cs:66-96` (thêm nhánh derive trong block fill)
- Test: `router balancing test/Providers/ModelMetadataServiceTests.cs` (thêm test)

**Interfaces:**
- Consumes: `Model.SupportsAudio/SupportsVideo` (Task 1); `ModelMetadata.InputModalities` (JSON array string, đã có ở mọi provider bước chain).
- Produces: behavior — `TryFillAsync` set `SupportsAudio/SupportsVideo = true` khi `meta.InputModalities` chứa "audio"/"video" (OrdinalIgnoreCase) và cờ đang false.

- [ ] **Step 1: Viết test fail**

Dùng pattern `FixedProvider` + `SeedModelAsync` sẵn có:

```
TryFillAsync_ModalitiesContainAudio_SetsSupportsAudio:
  meta = new(..., InputModalities: """["text","audio"]""", ...)
  → model.SupportsAudio true; SupportsVideo false

TryFillAsync_ModalitiesContainVideo_SetsSupportsVideo:
  meta = new(..., InputModalities: """["text","video"]""", ...)
  → model.SupportsVideo true

TryFillAsync_ManualAudioTrue_NotClearedByModalities:
  seed model có sẵn SupportsAudio=true
  meta = new(..., InputModalities: """["text"]""", ...)
  → SupportsAudio vẫn true; SupportsVideo false

TryFillAsync_BrokenModalitiesJson_ThrowsNo:
  meta = new(..., InputModalities: "not-a-json", ...)
  → TryFillAsync không ném; model giữ nguyên (flags false, InputModalities vẫn được ghi field khác — assert ctx fill nếu có)
```

- [ ] **Step 2: Chạy fail**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ModelMetadataServiceTests"`
Expected: FAIL.

- [ ] **Step 3: Implement derive**

Trong block `changed` của `TryFillAsync` (sau nhánh OutputModalities, ~96): parse `meta.InputModalities` 1 lần — `JsonDocument.TryParse`; nếu parse được, scan token strings chứa "audio"/"video" (OrdinalIgnoreCase); set flag tương ứng khi `!model.SupportsAudio`/`!model.SupportsVideo`. JSON null/hỏng → bỏ qua (best-effort, không log error — follow spec B1).

- [ ] **Step 4: Chạy pass**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ModelMetadataServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add "src/RouterBalancing.Core/Providers/ModelMetadataService.cs" "router balancing test/Providers/ModelMetadataServiceTests.cs"
git commit -m "feat: derive audio/video capabilities from input modalities"
```

---

### Task 4: `ComboCapabilityCalculator` + DI

**Files:**
- Create: `src/RouterBalancing.Core/Combos/ComboCapabilities.cs` (record + interface)
- Create: `src/RouterBalancing.Core/Combos/ComboCapabilityCalculator.cs`
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs` (`ConfigureServices`, sau `IComboResolver` line 101)
- Modify: `router-balancing/MauiProgram.cs:136` (đăng ký cạnh `IComboService`)
- Test: `router balancing test/Combos/ComboCapabilityCalculatorTests.cs` (mới)

**Interfaces:**
- Consumes: `Combo`, `ComboItem` (TargetModelId/TargetComboId), `Model` (ContextWindow/SupportsVision/SupportsThink/SupportsAudio/SupportsVideo), `IDbContextFactory<RouterBalancingDbContext>`.
- Produces (Task 5/6 dùng):

```csharp
/// <summary>Capabilities của combo — toàn bộ nullable; null = chưa rõ (spec D2).</summary>
public sealed record ComboCapabilities(
    int? ContextWindow, bool? Vision, bool? Audio, bool? Video, bool? Thinking);

public interface IComboCapabilityCalculator
{
    /// <summary>Tính capabilities cho mọi combo trong DB — gọi 1 lần, memoize theo combo Id.</summary>
    Task<IReadOnlyDictionary<long, ComboCapabilities>> ComputeAllAsync(CancellationToken ct = default);
}
```

- [ ] **Step 1: Viết test fail**
`ComboCapabilityCalculatorTests` theo pattern `ModelMetadataServiceTests` (TestDb + DbInitializer), seed trực tiếp provider/model/combo.

**Semantics chốt tại đây (spec D2):** bool member trên entity `false` = "không hỗ trợ/chưa rõ" (2 nghĩa gộp 1 theo convention hiện tại) → AND cho combo `false`. `null` chỉ phát sinh từ **combo con** chưa rõ (all-null record) hoặc combo rỗng:

```
ComputeAll_SingleModelMember_CopiesEntityValues:
  model (ctx 128K, vision true, audio true, video false, think false); combo 1 item
  → { ctx 128K, vision true, audio true, video false, thinking false }

ComputeAll_MixedMembers_AndsBools_MinsCtx:
  model A (ctx 128K, vision true, audio true, video false, think false)
  + model B (ctx 64K, vision true, audio false, video true, think true); combo 2 item
  → { ctx 64K, vision true, audio false, video true, thinking false }

ComputeAll_EmptyCombo_AllNull:
  combo 0 item → { null, null, null, null, null }

ComputeAll_NestedCombo_AndsRecursively:
  combo-inner 1 model (all true, ctx 100K)
  combo-outer: item combo-inner + model (audio false, ctx 50K)
  → inner {100K,true,true,true,true}; outer {50K,true,false,true,true}

ComputeAll_NestedComboChildWithFalse_PropagatesFalse:
  combo-inner 1 model (audio false)
  combo-outer 2 item = combo-inner + model (all true)
  → outer audio = false (false của combo con "thắng" true)

ComputeAll_WhenCycleInItems_SkipsBackEdge:
  seed cycle thô (A item→B, B item→A) — bypass UI validate
  → không treo; 2 combo trả được (edge quay lại bị skip)

ComputeAll_DisabledModelMember_StillCounted:
  model Enabled=false vẫn tính khi là member combo (capabilities informational, không lọc enabled)
```

- [ ] **Step 2: Chạy fail**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ComboCapabilityCalculatorTests"`
Expected: FAIL (type chưa tồn tại).

- [ ] **Step 3: Implement**

`ComboCapabilityCalculator(IDbContextFactory<RouterBalancingDbContext> db)`:
1. `ComputeAllAsync`: load toàn bộ `db.Combos.Include(c => c.Items)` + toàn bộ `db.Models` (dict theo Id) 1 lần.
2. Recursive local func `(long comboId, HashSet<long> visiting) → ComboCapabilities` với memo dict:
   - member model → convert entity → partial caps (bools nguyên vẹn, ctx nullable).
   - member combo con: nếu id ∈ `visiting` → skip (cycle guard); nếu đã memo → lấy; else đệ quy (thêm/rem visited).
   - combine: bools AND (`false` thắng `null`: `a & b` ba-valued — `false` nếu cả hai không null và có false... chính xác: `false` nếu ANY member `false`; else `null` nếu ANY `null`; else `true`).Ctx: min giá trị khác null; ANY null → null.
3. Combo rỗng → all null. XML doc public; comment "why" cho cycle guard + 3-valued AND.
4. Đăng ký: `ProxyApp.ConfigureServices` — `builder.Services.AddSingleton<IComboCapabilityCalculator, ComboCapabilityCalculator>();` sau dòng 101; `MauiProgram.cs` — cùng dòng sau 136.

- [ ] **Step 4: Chạy pass**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ComboCapabilityCalculatorTests"`
Expected: PASS.

- [ ] **Step 5: Build + commit**

```bash
dotnet build "router-balancing/router-balancing.csproj" -f net10.0-windows10.0.19041.0 --nologo -v q
git add "src/RouterBalancing.Core/Combos/" "src/RouterBalancing.Core/Server/ProxyApp.cs" "router-balancing/MauiProgram.cs" "router balancing test/Combos/ComboCapabilityCalculatorTests.cs"
git commit -m "feat: add combo capability calculator with recursive AND semantics"
```

---

### Task 5: Expose `capabilities` trong `/v1/models`

**Files:**
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs:408-441` (`/v1/models` handler)
- Test: `router balancing test/Server/ProxyAppChatIntegrationTests.cs` (mở rộng `Models_*`)

**Interfaces:**
- Consumes: `IComboCapabilityCalculator`, `ComboCapabilities` (Task 4); `Model` entity (Task 1).
- Produces: contract JSON — mỗi entry `data[]` có key `capabilities`:

```json
"capabilities": { "context_window": 128000, "vision": true, "audio": false, "video": false, "thinking": true }
```

Model direct: bools luôn có (entity false = chưa rõ), `context_window` nullable. Combo: cả 5 field nullable (`bool?`/`int?`), serialize **tường minh cả null** (không omit — client phân biệt "false" vs "không rõ").

- [ ] **Step 1: Viết test fail**

Thêm vào `ProxyAppChatIntegrationTests` (dùng `SeedProvider`/`SeedCombo` hiện có; extend `SeedProvider` với optional capability params hoặc seed model caps trực tiếp trong test):

```
Models_ModelEntry_HasCapabilitiesObject:
  SeedProvider("gpt-4o-mini") với model caps: ctx 128K, vision true, audio true, video false, think false
  GET /v1/models → entry gpt-4o-mini:
    capabilities.context_window == 128000
    capabilities.vision/audio == true; video/thinking == false
    (property "audio" tồn tại — phân biệt với trước đây không có key)

Models_ComboEntry_HasComputedCapabilities:
  SeedProvider caps đầy đủ (ctx 128K, all true) + SeedCombo("combo-fast", ...)
  → combo entry: capabilities.context_window == 128000; 4 bool == true

Models_ComboWithUnknownChild_SerializesNullsExplicitly:
  SeedProvider("gpt-4o-mini") caps bình thường + seed thêm model caps default (ctx null, all false) trong cùng provider; combo gồm 2 model
  → combo capabilities: context_window == ValueKind.Null; vision/audio/video/thinking == false (member false → false)
```

Ghi chú: đọc response qua `JsonElement` (pattern `ReadJson` + `GetProperty` sẵn có); assert `GetProperty("capabilities").GetProperty("audio").ValueKind` khác `Undefined` để bắt case key bị omit.

- [ ] **Step 2: Chạy fail**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProxyAppChatIntegrationTests.Models"`
Expected: FAIL (chưa có key capabilities).

- [ ] **Step 3: Implement handler**

Trong handler `/v1/models`:
1. Projection models thêm:
```csharp
capabilities = new
{
    context_window = m.ContextWindow,
    vision = m.SupportsVision,
    audio = m.SupportsAudio,
    video = m.SupportsVideo,
    thinking = m.SupportsThink,
},
```
2. Query combos giữ nguyên + `var caps = await http.RequestServices.GetRequiredService<IComboCapabilityCalculator>().ComputeAllAsync(http.RequestAborted);` — mỗi combo entry thêm `capabilities = new { context_window = caps[c.Id].ContextWindow, vision = ..., audio = ..., video = ..., thinking = ... }` (đọc từ record nullable). Combo không có trong dict (phòng thủ) → object toàn null.
3. JSON null: `Results.Json` default serialize null — verify qua test; nếu omit (mất property) thì set `JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.Never }`.

- [ ] **Step 4: Chạy pass**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProxyAppChatIntegrationTests"`
Expected: PASS (toàn class — không regression `Models_ReturnsEnabledModelsListShape` cũ).

- [ ] **Step 5: Commit**

```bash
git add "src/RouterBalancing.Core/Server/ProxyApp.cs" "router balancing test/Server/ProxyAppChatIntegrationTests.cs"
git commit -m "feat: expose model and combo capabilities in /v1/models"
```

---

### Task 6: Combos.razor — cột "Khả năng" read-only

**Files:**
- Modify: `router-balancing/Components/Pages/Combos.razor` (table 27-57 + @code load + inject)
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (key `combos.col.capabilities`, `combos.caps.tooltip`, `combos.caps.ctxUnknown`)

**Interfaces:**
- Consumes: `IComboCapabilityCalculator.ComputeAllAsync`, `ComboCapabilities` (Task 4); `Badge` component (variants Neutral/Success/Warning/Info).
- Produces: UI only — không API mới.

- [ ] **Step 1: Inject + load**

`@inject IComboCapabilityCalculator ComboCapsSvc`; field `private IReadOnlyDictionary<long, ComboCapabilities> _caps = new Dictionary<long, ComboCapabilities>();` — nạp cùng chỗ load `_combos` (method nào fetch `_combos` hiện tại, await `ComboCapsSvc.ComputeAllAsync()` sau đó; wrap try/catch toast theo pattern load hiện có — caps fail không được block trang: log + `_caps` rỗng → cột hiện "—").

- [ ] **Step 2: Cột table**

Header sau `@L["combos.col.items"]` (33): `<th>@L["combos.col.capabilities"]</th>`. Cell sau cell Items (46): nhóm badge bọc `<span title="@L["combos.caps.tooltip"]">`:

- ctx: `_caps.TryGetValue(combo.Id, out var caps)` → nếu `caps.ContextWindow is { } c` → `<Badge Text="@($"ctx: {FormatCtx(c)}")" Variant="Info" />` (copy helper `FormatCtx` từ Providers.razor thành private static local — 3 dòng, không share); null → `<Badge Text="@L["combos.caps.ctxUnknown"]" Variant="Neutral" />`.
- 4 bool: `true` → Badge (vision Success / thinking Warning / audio Info / video Neutral — cùng mapping Task 2); `false` → span text-opposite suffix "✕" (vd `<span class="opacity-70">👁 ✕</span>` — language-neutral, theo pattern placeholder Providers:299-300); `null` → không render (không rõ ≠ không hỗ trợ).
- Thiếu key trong dict → render `—` (opacity-70).

- [ ] **Step 3: Translations**

Thêm VI + EN: `combos.col.capabilities` = "Khả năng"/"Capabilities"; `combos.caps.tooltip` = "AND mọi thành viên · ctx = min · thành viên chưa rõ → combo chưa rõ"/"AND of members · ctx = min · unknown member → unknown"; `combos.caps.ctxUnknown` = "ctx: ?"/"ctx: ?".

- [ ] **Step 4: Build gate + verify thủ công**

Run: `dotnet build "router-balancing/router-balancing.csproj" -f net10.0-windows10.0.19041.0 --nologo -v q` → 0 Error.
Checklist thủ công (launch app): trang /combos hiện cột mới; combo 1 model full caps hiện đủ badge; combo có member chưa ctx hiện "ctx: ?"; hover hiện tooltip.

- [ ] **Step 5: Commit**

```bash
git add "router-balancing/Components/Pages/Combos.razor" "src/RouterBalancing.Core/Localization/Translations.cs"
git commit -m "feat: show computed capabilities on combos page"
```
