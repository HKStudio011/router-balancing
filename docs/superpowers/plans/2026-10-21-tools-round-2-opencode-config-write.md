# Trang Tools vòng 2 + ghi config opencode — Implementation Plan (Plan 2/2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hoàn thiện trang Tools theo spec §9: nhớ lựa chọn, reset mặc định, tìm kiếm, thu/giãn và chọn theo provider, đưa combo thành một entry config, và ghi thẳng block `providers["router-balancing"]` (shape v2) vào file opencode mà không phá phần còn lại của file.

**Architecture:** Mở rộng `ComboCapabilities` (tools + modalities giao nhau) để combo phát được entry an toàn; generator phát key `<Identifier>/<ModelId>` (C-A), `settings.timeout:false` (C-D), entry combo key = tên combo (U12) và negative reasoning khi đã biết là không (C-M — writer đã bảo đảm negative chỉ đến từ full-list/user). Ghi file bằng `OpenCodeConfigSplicer` thuần (định vị block bằng `Utf8JsonReader` rồi splice text, giữ nguyên byte phần khác) + `OpenCodeConfigFileWriter` (backup `.bak`, ghi tạm rồi đổi tên). Trang Tools lưu lựa chọn/thu giãn vào `AppSettings`, logic chọn/lọc tách thành helper thuần trong Core để unit test được.

**Tech Stack:** .NET 10, System.Text.Json (`Utf8JsonReader`, `JsonNode`), EF Core + SQLite, xUnit v3 (MTP), .NET MAUI Blazor Hybrid (Razor), Tailwind/Vite (`router-balancing/vite-project`).

**Spec:** `docs/superpowers/specs/2026-10-19-capability-model-and-tools-round-2-design.md` — plan này thực thi **phase 2** (§9 + checklist §10 phần trang Tools); phase 1 đã ship ở PR #31 (`v1.6.0`). Các quyết định U1–U13, C-A…C-Q ở §4–§5 của spec không bàn lại.

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên trong mọi csproj.
- Nhánh làm việc: `feat/tools-round-2` tạo từ `master` tại `c54468d` (release `v1.6.0` đã phát hành — **không bump version** trong plan này).
- Gate mỗi task: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj` (0 warning) + `dotnet test "router balancing test/router balancing test.csproj"`. **Không** dùng `dotnet build router-balancing.slnx` (NETSDK1082 có sẵn từ trước).
- Test tập trung: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~<TênTestClass>"`.
- Build app TFM (Razor compile): `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` (đóng app nếu bị khóa file).
- Nếu Razor thêm class Tailwind **chưa từng xuất hiện** trong repo: chạy `npm run build` tại `router-balancing/vite-project` rồi commit `router-balancing/wwwroot/build/assets/*` nếu output đổi.
- 1 task = 1 commit, message conventional commit tiếng Anh (`feat:`/`refactor:`/`test:`/`docs:`/`fix:`); chỉ stage đúng file của task.
- Không đụng `src/RouterBalancing.Core/Engine/*` và `router balancing test/Engine/*` (WIP của user).
- Comment tiếng Việt cho "tại sao"; XML doc cho public API; exception message và identifier tiếng Anh. Không TODO vô chủ.
- Test dùng `TestDb` (SQLite file tạm, migrate thật) + `DbInitializer.Initialize`; HTTP stub là class cục bộ trong từng file test (house pattern).
- Baseline đỏ đã biết của môi trường (không tính là lỗi mới): `SingleInstanceGuardTests`, `Cancel_QueuedRequest`, `Stream_ClientAbortQueued` và teardown flake (`RestartManagerTests`, `OpenCodeSidecarE2ETests`/`TestDb.Dispose`, `AppDirProcessKillerTests`); CI đã loại/rerun các test này. Gate = full suite không phát sinh lỗi mới ngoài baseline.
- Key model config luôn `<Identifier>/<ModelId>` (C-A); chỉ bare khi provider chưa có `Identifier`. Không phát `modelID`/`family` (C-B/C-C). Negative chỉ phát khi fact đến từ full-list/user — `ModelCapabilityWriter` đã bảo đảm điều này ở tầng ghi (Plan 1 Task 2), nên generator không phải truy vấn thêm source.
- File config người dùng: giữ nguyên byte mọi phần ngoài block `router-balancing` (kể cả comment); backup `<path>.bak`; ghi tạm rồi đổi tên.

## Review Focus

Năm lớp hành vi spec ngụ ý nhưng test từng task dễ bỏ sót — mỗi dòng được gán test cụ thể ở task sở hữu (`[RF-n]`):

1. **JSONC thật (comment/trailing comma)** — parser phải skip comment để định vị block, phần ngoài block giữ nguyên từng byte, kết quả vẫn parse được. → Task 4.
2. **Dấu phẩy/khoảng trắng khi block cũ là member đầu / cuối / duy nhất, hoặc `providers` chưa tồn tại** — không được sinh `,,`/mất dấu phẩy/JSON hỏng. → Task 4.
3. **Ghi lỗi giữa đường (file read-only, copy/move fail)** — file gốc và `.bak` cũ không bị hỏng, temp được dọn, lỗi nổi lên cho UI toast. → Task 5.
4. **Trùng khóa** — hand-edit để lại 2 block `router-balancing`, hoặc 2 provider chưa có `Identifier` cùng `ModelId` → kết quả chỉ còn một key, phần bị bỏ được báo (`SkippedKeys`). → Task 2/3/4.
5. **Lựa chọn nhớ toàn id đã bị xóa** — về rỗng, **không** fallback về mặc định (spec §9.1), và mặc định vẫn được lọc theo id còn tồn tại. → Task 8.

---

### Task 0: Commit plan

**Files:**
- Create: `docs/superpowers/plans/2026-10-21-tools-round-2-opencode-config-write.md` (file này)

- [ ] **Step 1:** Tạo nhánh: `git switch -c feat/tools-round-2 master`.
- [ ] **Step 2:** Commit plan: `git add docs/superpowers/plans/2026-10-21-tools-round-2-opencode-config-write.md && git commit -m "docs: add tools round 2 implementation plan"`.

---

### Task 1: `ComboCapabilities` thêm tools + modalities; badge tools ở Combos (U11, C-L)

**Files:**
- Modify: `src/RouterBalancing.Core/Combos/ComboCapabilities.cs`
- Modify: `src/RouterBalancing.Core/Combos/ComboCapabilityCalculator.cs`
- Modify: `router-balancing/Components/Pages/Combos.razor` (cột capability: thêm badge tools)
- Test: `router balancing test/Combos/ComboCapabilityCalculatorTests.cs`

**Interfaces:**
- Consumes: `ModelCapabilityView` (đã có).
- Produces:
  - `ComboCapabilities` giữ nguyên positional ctor `(int? ContextWindow, bool? Vision, bool? Audio, bool? Video, bool? Thinking)` và thêm init-only:
    - `bool? Tools { get; init; }`
    - `IReadOnlyList<string>? InputModalities { get; init; }`
    - `IReadOnlyList<string>? OutputModalities { get; init; }`
  - Luật tính: `Tools` = AND 3 mức như các bool khác; `InputModalities`/`OutputModalities` = giao các danh sách khẳng định thuận, **null nếu bất kỳ member nào null** (member chưa rõ làm combo chưa rõ — không nói quá); combo rỗng → cả ba null.
  - `ComboCapabilityCalculator.FromView` map `view.Tools`; `view.InputModalities`/`OutputModalities` rỗng → `null` (chưa biết), ngược lại giữ nguyên list.

- [ ] **Step 1: Viết test đỏ** trong `ComboCapabilityCalculatorTests.cs`. Mở rộng `SeedModelAsync` bằng tham số optional cuối `bool? tools = null, IReadOnlyList<ModalityKind>? inputModalities = null, IReadOnlyList<ModalityKind>? outputModalities = null` (giữ các tham số cũ; ghi fact qua `SeedCapability`/`SeedModality`, modality output thêm `Direction = Output`). Thêm các `[Fact]`:
  - `ComputeAll_SingleModelMember_CopiesToolsAndModalityLists` — model có tools true, input `[Text, Image]`, output `[Text, Audio]` → combo copy đúng ba field.
  - `ComputeAll_MixedMembers_IntersectsToolsAndModalityLists` — A: tools true, input `[Text, Image]`, output `[Text, Audio]`; B: tools true, input `[Text]`, output `[Text]` → combo tools true, input `["text"]`, output `["text"]`.
  - `ComputeAll_UnknownModalitiesOrTools_MakeFieldNull` — A có facts, B không có gì → `InputModalities`/`OutputModalities`/`Tools` của combo đều null, các bool cũ vẫn theo AND 3 mức.
  - `ComputeAll_EmptyCombo_ToolsAndModalityListsNull`.
  - `ComputeAll_NestedCombo_IntersectsToolsAndModalityLists`.
  - `ComputeAll_DisabledModelMember_StillCountsForToolsAndModalities` (mở rộng test cũ nếu tiện).
- [ ] **Step 2:** Chạy `--filter "FullyQualifiedName~ComboCapabilityCalculatorTests"` → Expected: FAIL (compile: property chưa tồn tại).
- [ ] **Step 3:** Implement record + calculator. Trong `Combine`, giữ `acc`/`m` bằng object initializer; thêm helper `private static IReadOnlyList<string>? Intersect(IReadOnlyList<string>? a, IReadOnlyList<string>? b) => a is null || b is null ? null : a.Intersect(b, StringComparer.Ordinal).ToArray();`.
- [ ] **Step 4:** `Combos.razor`: trong khối `_caps` của cột capabilities, thêm trước badge vision:
  ```razor
  @if (caps.Tools is true) { <Badge Text="🛠" Variant="BadgeVariant.Success" /> }
  else if (caps.Tools is false) { <Badge Text="🛠 ✕" Variant="BadgeVariant.Neutral" /> }
  ```
- [ ] **Step 5:** Chạy filter → PASS; full suite → không lỗi mới. Nếu CSS class mới xuất hiện: chạy vite build và commit output.
- [ ] **Step 6:** Commit: `feat: add tools and modalities to combo capabilities`.

---

### Task 2: Generator v2 — `settings.timeout`, khóa có prefix, entry combo, negative reasoning (C-A/C-D/U12/C-M)

**Files:**
- Modify: `src/RouterBalancing.Core/Tools/OpenCodeConfigGenerator.cs`
- Test: `router balancing test/Tools/OpenCodeConfigGeneratorTests.cs`

**Interfaces:**
- Produces:
  - `OpenCodeConfigModel` thêm tham số optional **cuối**: `string? Key = null` — key trong object `models`; `null` → dùng `ModelId`. Tên hiển thị vẫn fallback `DisplayName ?? ModelId` (không dùng `Key`).
  - Mới: `sealed record OpenCodeConfigCombo(string Name, int? ContextWindow, bool? SupportsTools, bool? SupportsVision, bool? SupportsThink, IReadOnlyList<string>? InputModalities, IReadOnlyList<string>? OutputModalities)`.
  - Mới: `public static JsonObject BuildProviderObject(string baseUrl, string? apiKey, IReadOnlyList<OpenCodeConfigModel> models, IReadOnlyList<OpenCodeConfigCombo>? combos = null)` — Task 3/6 dùng lại để ghi file.
  - `Generate(string baseUrl, string? apiKey, IReadOnlyList<OpenCodeConfigModel> models, IReadOnlyList<OpenCodeConfigCombo>? combos = null)` (tham số `combos` optional → call site cũ vẫn compile).
  - `settings` luôn có `timeout = false` (C-D), sau `baseURL`/`apiKey`.
  - Dedupe theo **key**: models duyệt trước (first-wins), combos duyệt sau, combo trùng key bị bỏ (deterministic; Task 3 báo `SkippedKeys`).
  - `SupportsThink == false` → `capabilities.reasoning = { "supported": false }` (C-M; thay hành vi omit cũ). `null` → omit.

- [ ] **Step 1: Viết/sửa test đỏ:**
  - Sửa `Generate_EmitsProviderShapeAndMinimalModel`: assert thêm `settings.timeout` là `false`.
  - `Generate_ModelWithKey_UsesKeyForObjectKey_KeepsNameFallback` — `Minimal("big-pickle") with { Key = "opencode-free/big-pickle" }` → property `opencode-free/big-pickle`, `name == "big-pickle"`.
  - `Generate_Combo_EmitsSingleEntryKeyedByName` — combo `unsloth-studio`, ctx 64_000, tools/vision/think true, input `[text,image]`, output `[text]` → property `unsloth-studio`, `name == "unsloth-studio"`, `limit.context == 64000`, `capabilities` đúng.
  - `Generate_ComboKeyCollidesWithModelKey_ModelWins` — model `Minimal("combo-a")` + combo `combo-a` → đúng một property `combo-a`, `name` là của model.
  - `Generate_ThinkingKnownAbsent_EmitsSupportedFalse` — thay test `Generate_ThinkingOff_OmitsReasoning`: `SupportsThink = false` → `reasoning.supported == false`; `SupportsThink = null` → không có key `reasoning`.
  - `Generate_ComboUnknownModalities_FallsBackToDefaults` (input theo `SupportsVision`, output `[text]`).
- [ ] **Step 2:** Chạy `--filter "FullyQualifiedName~OpenCodeConfigGeneratorTests"` → FAIL.
- [ ] **Step 3:** Implement: `BuildProviderObject` dựng `settings`/`models` node (BuildModel nhận key + BuildCombo dùng chung `BuildEntry(key, displayName, ...)`), `Generate` bọc `{"providers":{"router-balancing": BuildProviderObject(...)}}`.
- [ ] **Step 4:** Filter → PASS; full suite → không lỗi mới.
- [ ] **Step 5:** Commit: `feat: emit v2 config with prefixed keys, combos and provider timeout`.

---

### Task 3: Service — combo rows, provider id, khóa prefix, combo trong generate (U12, §9.6)

**Files:**
- Modify: `src/RouterBalancing.Core/Tools/IOpenCodeConfigService.cs`
- Modify: `src/RouterBalancing.Core/Tools/OpenCodeConfigService.cs`
- Modify: `router-balancing/Components/Pages/Tools.razor` (chỉ call site `GenerateAsync` thêm tham số combo `[]`)
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (`tools.duplicates` đổi nghĩa sang "key trùng")
- Test: `router balancing test/Tools/OpenCodeConfigServiceTests.cs`

**Interfaces:**
- Consumes: `IComboCapabilityCalculator` (đã có), `OpenCodeConfigCombo`/`Key` (Task 2).
- Produces:
  - `ToolsModelGroup(long ProviderId, string ProviderName, bool ProviderEnabled, bool IsFreePreset, IReadOnlyList<ToolsModelRow> Models)`.
  - Mới: `sealed record ToolsComboRow(long Id, string Name, ComboMode Mode, int MemberCount, bool? Tools, bool? Reasoning, bool? Vision, bool? Audio, bool? Video, int? ContextWindow, IReadOnlyList<string>? InputModalities, IReadOnlyList<string>? OutputModalities)`.
  - `OpenCodeConfigPageData(IReadOnlyList<ToolsModelGroup> Groups, IReadOnlyList<ToolsComboRow> Combos, IReadOnlyList<long> DefaultSelectedIds)`.
  - `OpenCodeConfigResult(string Json, IReadOnlyList<string> SkippedKeys)` (đổi tên từ `SkippedDuplicateModelIds`; giá trị là key cuối cùng, ví dụ `opencode-free/shared-id`).
  - `GenerateAsync(IReadOnlyList<long> selectedModelIds, IReadOnlyList<long> selectedComboIds, string baseUrl, string? apiKey, CancellationToken ct = default)`.
  - `OpenCodeConfigService` ctor nhận thêm `IComboCapabilityCalculator comboCapabilities` (tham số cuối, sau `IModelCapabilityReader`).
  - Khóa model: `provider.Identifier` khác null/rỗng → `$"{identifier}/{ModelId}"`, ngược lại bare `ModelId`. Dedupe theo khóa cuối, thứ tự models: free preset trước, rồi `Id`; combos theo `Name`; combo trùng khóa với model bị bỏ và ghi vào `SkippedKeys`.
  - Combo entry lấy capability từ `ComboCapabilities` giao-an-toàn: `Tools`, `Thinking`→`SupportsThink`, `Vision`, `Audio`/`Video` (input), `ContextWindow`, `InputModalities`, `OutputModalities`; không phát member model kèm theo (chỉ entry combo khi user tick combo).

- [ ] **Step 1: Cập nhật test:**
  - `Service()` → `new(_db, new ModelCapabilityReader(_db), new ComboCapabilityCalculator(_db, new ModelCapabilityReader(_db)))`.
  - Đổi `GenerateAsync_DuplicateIds_...`: hai preset khác provider cùng `ModelId` giờ ra **hai** key (`opencode-free/shared-id`, `openrouter-free/shared-id`) — assert cả hai, không còn skipped.
  - Thêm `GenerateAsync_KeyConflictWithoutIdentifier_ReportsSkippedKey` `[RF-4]`: tạo 2 provider mới `Identifier = null` (tên khác nhau), cùng `ModelId "dup"`, select cả hai → key `dup` chỉ một entry (first-wins theo free/Id), `SkippedKeys` chứa `dup` đúng một lần.
  - Thêm `GenerateAsync_ModelUsesProviderIdentifierPrefix`.
  - Thêm `GetPageData_GroupCarriesProviderId`.
  - Thêm `GetPageData_ListsCombosWithCapabilitiesAndMemberCount`: seed 2 model có facts (tools/vision/audio, ctx 128k/64k) vào 1 provider + combo 2 item → row có `MemberCount == 2`, `ContextWindow == 64_000`, `Tools == true`, input/output lists giao nhau, `Mode`.
  - Thêm `GenerateAsync_ComboOnly_EmitsSingleEntryAndNoMemberModels`: combo có member model, chỉ tick combo → object `models` có đúng key tên combo; không có key của member; capabilities lấy giao.
  - Thêm `GenerateAsync_ComboCapabilities_UseIntersectionWeakestMember` (một member audio false → combo entry input không có `audio`).
  - Cập nhật mọi call `GenerateAsync`/`SkippedDuplicateModelIds` còn lại theo chữ ký mới (`SkippedKeys`, thêm `[]` combos).
- [ ] **Step 2:** Chạy `--filter "FullyQualifiedName~OpenCodeConfigServiceTests"` → FAIL.
- [ ] **Step 3:** Implement: `GetPageDataAsync` thêm `ProviderId` + query `db.Combos.AsNoTracking().Include(c => c.Items).OrderBy(c => c.Name)` + `ComputeAllAsync`; `GenerateAsync` query model kèm provider, dựng khóa, dựng entry combo; `Tools.razor` gọi `GenerateAsync(_selected.ToArray(), [], _baseUrl, _apiKey)`; sửa i18n `tools.duplicates` EN/VI ("Some duplicate keys were skipped." / "Một số key trùng đã bị bỏ qua.").
- [ ] **Step 4:** Filter → PASS; full suite → không lỗi mới.
- [ ] **Step 5:** Commit: `feat: build tools page combos and prefixed model keys`.

---

### Task 4: `OpenCodeConfigSplicer` — định vị block bằng token và splice text (§9.8)

**Files:**
- Create: `src/RouterBalancing.Core/Tools/OpenCodeConfigFormatException.cs`
- Create: `src/RouterBalancing.Core/Tools/OpenCodeConfigSplicer.cs`
- Test: `router balancing test/Tools/OpenCodeConfigSplicerTests.cs`

**Interfaces:**
- Produces:
  - `public sealed class OpenCodeConfigFormatException : Exception` (hai ctor message + message/inner).
  - `public static class OpenCodeConfigSplicer` với `public static string Upsert(string text, JsonObject providerBlock)` — trả về text mới; ném `OpenCodeConfigFormatException` khi root/`providers` sai kiểu hoặc JSON hỏng.
- Thuật toán (implementer viết body theo đúng các bước này):
  1. Parse text UTF-8 bằng `Utf8JsonReader` với `CommentHandling = Skip, AllowTrailingCommas = true`; token đầu phải `StartObject`, sau root không còn token nào. Dựng cây `JsonObjectNode(Start, End, List<JsonMember>(Name, NameStart, ValueStart, ValueEnd, Child))` cho các object (array thì skip bằng depth).
  2. `newline` = `"\r\n"` nếu text chứa CRLF, ngược lại `"\n"`; `indentUnit` = phần indent dòng của member đầu root, mặc định 4 space.
  3. Thu thập `TextEdit` xoá **mọi** member tên `router-balancing` trong object con của `provider` và `providers` (dùng danh sách member gốc: nếu có member kế tiếp → xoá `[target.NameStart, next.NameStart)`; nếu là cuối → xoá `[prev.ValueEnd, target.ValueEnd)`; nếu là duy nhất → xoá `[parent.Start + 1, target.ValueEnd)`). Gộp các range **chồng nhau** trước khi áp (`[RF-4]`; range liền kề không cần gộp).
  4. Chèn block mới:
     - Có `providers` (phải là object, nếu không → throw): indent của member = indent dòng của member đầu object (fallback `parentIndent + indentUnit` nếu member nằm cùng dòng với `{`); chèn ngay sau `{` với `newline + member + ","` nếu còn member khác, ngược lại `newline + member` (thêm `newline + parentIndent` khi object rỗng một dòng).
     - Không có `providers`: dựng member `"providers": { <providerBlock> }` rồi chèn vào root theo đúng luật dấu phẩy/indent như trên.
     - Member sinh bằng `JsonNode.ToJsonString(WriteIndented)` của wrapper 1 key rồi re-indent từng dòng: `indent + indentUnit * (leadingSpaces / 2) + phần còn lại`.
  5. Áp edit theo thứ tự offset giảm dần (cùng offset: xoá trước, chèn sau) bằng `StringBuilder`.
  6. Parse lại kết quả (`JsonDocumentOptions` skip comment/trailing comma) và xác nhận `providers["router-balancing"]` là object; sai → throw (guard chống off-by-one).
- `providerBlock` được `DeepClone()` trước khi gắn vào wrapper để tránh lỗi "node đã có parent".

- [ ] **Step 1: Viết test đỏ** `[RF-1]` `[RF-2]` `[RF-4]`:
  - `Upsert_ExistingOldBlocks_RemovesThemAndInsertsIntoProviders_PreservingOtherText` — file mẫu có `$schema`, `plugin`, `mcp`, `agent`, `provider.other`, `provider.router-balancing`, `providers.other`; assert text của `plugin`/`mcp`/`agent`/`provider.other`/`providers.other` còn nguyên **nguyên văn** (so substring), parse lại không còn `provider.router-balancing`, có `providers.router-balancing` mới.
  - `Upsert_MissingProviders_CreatesProvidersObject` + `Upsert_EmptyRootObject_CreatesProviders`.
  - `Upsert_ProvidersWithoutOtherMembers_NoDanglingComma`; `Upsert_ProvidersWithOtherMember_KeepsOtherMember`.
  - `Upsert_OldBlockFirstMember_LeavesValidComma`; `Upsert_OldBlockLastMember_LeavesValidComma`; `Upsert_OldBlockOnlyMember_LeavesValidBraces`.
  - `Upsert_JSONCWithComments_PreservesCommentsOutsideBlock` — comment trước/sau block; parse lại được.
  - `Upsert_DuplicateRouterBalancingBlocks_LeavesExactlyOne` `[RF-4]`.
  - `Upsert_InvalidJson_ThrowsFormat`; `Upsert_RootNotObject_ThrowsFormat`; `Upsert_ProvidersNotObject_ThrowsFormat`; `Upsert_TrailingContent_ThrowsFormat`.
  - `Upsert_UsesFileIndentAndCrlf` — file 4-space + CRLF → block chèn dùng đúng 8 space cho member con và CRLF.
- [ ] **Step 2:** Chạy `--filter "FullyQualifiedName~OpenCodeConfigSplicerTests"` → FAIL (type chưa tồn tại).
- [ ] **Step 3:** Implement exception + splicer theo thuật toán trên.
- [ ] **Step 4:** Filter → PASS; full suite → không lỗi mới.
- [ ] **Step 5:** Commit: `feat: splice router-balancing block into opencode config preserving bytes`.

---

### Task 5: `OpenCodeConfigFileWriter` — backup + ghi atomic (§9.8)

**Files:**
- Create: `src/RouterBalancing.Core/Tools/OpenCodeConfigFileWriter.cs`
- Modify: `router-balancing/MauiProgram.cs` (đăng ký `IOpenCodeConfigFileWriter` → `OpenCodeConfigFileWriter`, singleton, cạnh `IOpenCodeConfigService`)
- Test: `router balancing test/Tools/OpenCodeConfigFileWriterTests.cs`

**Interfaces:**
- Consumes: `OpenCodeConfigSplicer` (Task 4), `OpenCodeConfigFormatException` (Task 4).
- Produces:
  - `sealed record OpenCodeConfigWriteResult(string Path, string? BackupPath)` (`BackupPath` null khi file đích chưa tồn tại).
  - `interface IOpenCodeConfigFileWriter { Task<OpenCodeConfigWriteResult> WriteAsync(string path, JsonObject providerBlock, CancellationToken ct = default); }`.
  - `sealed class OpenCodeConfigFileWriter : IOpenCodeConfigFileWriter`.
  - Luật ghi: file tồn tại → đọc byte, giữ BOM (UTF-8 strict, BOM sai/không hợp lệ → `OpenCodeConfigFormatException`), `Upsert`, copy `.bak` (overwrite), ghi file tạm cùng thư mục rồi `File.Move(temp, path, overwrite: true)`, dọn temp trong `finally`; file chưa tồn tại → tạo thư mục + ghi file mới (không backup), nội dung = `Upsert("{}", providerBlock)`; lỗi I/O nổi lên nguyên trạng (UI toast), file gốc không bị đụng.

- [ ] **Step 1: Viết test đỏ** (dùng thư mục tạm tự tạo/dọn trong test class):
  - `WriteAsync_NewFile_CreatesDirectoryAndFile_NoBackup`.
  - `WriteAsync_ExistingFile_CreatesBackupWithOriginalBytes`.
  - `WriteAsync_ExistingFile_ReplacesBlock_PreservesBomAndCrlf`.
  - `WriteAsync_InvalidJson_ThrowsAndLeavesFileUntouched_NoBackup`.
  - `WriteAsync_ReadOnlyTarget_ThrowsAndLeavesOriginalUntouched` `[RF-3]` (chỉ chạy Windows; set attribute read-only rồi trả lại trong `finally`).
  - `WriteAsync_UnrelatedTextPreserved` (file có `plugin`/`mcp`; so nguyên văn sau ghi).
- [ ] **Step 2:** Chạy `--filter "FullyQualifiedName~OpenCodeConfigFileWriterTests"` → FAIL.
- [ ] **Step 3:** Implement writer + đăng ký DI.
- [ ] **Step 4:** Filter → PASS; full suite → không lỗi mới.
- [ ] **Step 5:** Commit: `feat: write opencode config with backup and atomic replace`.

---

### Task 6: Service `WriteAsync` — ghép generator + writer

**Files:**
- Modify: `src/RouterBalancing.Core/Tools/IOpenCodeConfigService.cs`
- Modify: `src/RouterBalancing.Core/Tools/OpenCodeConfigService.cs`
- Test: `router balancing test/Tools/OpenCodeConfigServiceTests.cs`

**Interfaces:**
- Consumes: `IOpenCodeConfigFileWriter` (Task 5), `BuildProviderObject` (Task 2).
- Produces:
  - `Task<OpenCodeConfigWriteResult> WriteAsync(string path, IReadOnlyList<long> selectedModelIds, IReadOnlyList<long> selectedComboIds, string baseUrl, string? apiKey, CancellationToken ct = default)` trên `IOpenCodeConfigService`.
  - `OpenCodeConfigService` ctor nhận thêm `IOpenCodeConfigFileWriter writer` (tham số cuối).
  - `GenerateAsync` và `WriteAsync` dùng chung helper private `Task<(JsonObject Provider, IReadOnlyList<string> Skipped)> BuildProviderAsync(IReadOnlyList<long> modelIds, IReadOnlyList<long> comboIds, string baseUrl, string? apiKey, CancellationToken ct)` (không lặp logic): `GenerateAsync` bọc provider vào root `providers` rồi serialize preview; `WriteAsync` chuyển thẳng `JsonObject` provider cho writer.

- [ ] **Step 1: Viết test đỏ:**
  - `WriteAsync_SelectedModelAndCombo_WritesBlockAndReturnsPaths` — file đích có sẵn nội dung khác; gọi; parse file: có `providers.router-balancing.models` chứa key model prefix + key combo; `BackupPath == path + ".bak"`; `.bak` tồn tại đúng nội dung gốc.
  - `WriteAsync_InvalidConfigFile_Throws_LeavesFileUntouched`.
  - `WriteAsync_NoSelection_WritesProviderBlockWithEmptyModels` (vẫn hợp lệ khi UI cho phép? UI chặn khi `_json` null — test này ghim hành vi service: không ném, models rỗng).
- [ ] **Step 2:** Chạy `--filter "FullyQualifiedName~OpenCodeConfigServiceTests"` → FAIL.
- [ ] **Step 3:** Implement tách helper dựng entry + `WriteAsync`.
- [ ] **Step 4:** Filter → PASS; full suite → không lỗi mới.
- [ ] **Step 5:** Commit: `feat: write generated config into the opencode config file`.

---

### Task 7: Settings — đường dẫn file config opencode + nút "mặc định" (§9.8)

**Files:**
- Create: `src/RouterBalancing.Core/Tools/OpenCodeConfigPaths.cs`
- Modify: `src/RouterBalancing.Core/Settings/SettingsKeys.cs`, `IAppSettingsService.cs`, `AppSettingsService.cs`, `SettingsDraft.cs`, `SettingsValidator.cs`
- Modify: `router-balancing/Components/Pages/SettingsPanel.razor`, `src/RouterBalancing.Core/Localization/Translations.cs`
- Test: `router balancing test/Tools/OpenCodeConfigPathsTests.cs`, `router balancing test/Settings/AppSettingsServiceTests.cs`, `router balancing test/Settings/SettingsValidatorTests.cs`, `router balancing test/Engine/ProxyRequestHandlerTests.cs` (StubSettings)

**Interfaces:**
- Produces:
  - `public static class OpenCodeConfigPaths`:
    - `public static string DefaultPath` = `<user profile>/.config/opencode/opencode.json`.
    - `public static string Expand(string? path)` — rỗng/whitespace → `DefaultPath`; `"~"` hoặc `"~/"`/`"~\"` → ghép `user profile`; còn lại giữ nguyên.
  - `SettingsKeys.OpenCodeConfigPath = "openCodeConfigPath"`.
  - `IAppSettingsService.OpenCodeConfigPath { get; }` — `OpenCodeConfigPaths.Expand(Get(SettingsKeys.OpenCodeConfigPath, ""))`.
  - `SettingsDraft.OpenCodeConfigPath` (string, mặc định `""`).
  - Validator: rỗng/whitespace hoặc chứa `Path.GetInvalidPathChars()` → `settings.error.openCodeConfigPath`.
  - UI General: input text + nút `settings.action.default` (set `_draft.OpenCodeConfigPath = OpenCodeConfigPaths.DefaultPath`); `GeneralFields` thêm field; `SaveGeneralAsync` ghi `Settings.Set(SettingsKeys.OpenCodeConfigPath, _draft.OpenCodeConfigPath)`; `LoadDraft` đọc `Settings.OpenCodeConfigPath`.
  - i18n EN/VI: `settings.field.openCodeConfigPath`, `settings.action.default`, `settings.error.openCodeConfigPath`.

- [ ] **Step 1: Viết test đỏ:**
  - `OpenCodeConfigPathsTests`: `DefaultPath_EndsWithConfigOpencodeJson`; `Expand_Tilde_ReturnsUserProfile`; `Expand_TildeRelative_Combines`; `Expand_Empty_ReturnsDefault`; `Expand_AbsolutePath_Unchanged`.
  - `AppSettingsServiceTests`: `OpenCodeConfigPath_WhenUnset_ReturnsDefault`; `OpenCodeConfigPath_TildeStored_ExpandsToProfile` (Set raw `"~"` → property trả home).
  - `SettingsValidatorTests`: `Validate_WhenOpenCodeConfigPathEmpty_ReturnsError`; `Validate_WhenOpenCodeConfigPathValid_ReturnsNoError`.
- [ ] **Step 2:** Chạy các filter tương ứng → FAIL.
- [ ] **Step 3:** Implement Core + UI + i18n; cập nhật `StubSettings` thêm `public string OpenCodeConfigPath => OpenCodeConfigPaths.DefaultPath;` (thêm `using RouterBalancing.Core.Tools;`).
- [ ] **Step 4:** Filter `OpenCodeConfigPaths|SettingsValidator|AppSettingsService` → PASS; `--filter "FullyQualifiedName~TranslationParity"` → PASS; full suite → không lỗi mới; build app TFM (Razor compile).
- [ ] **Step 5:** Commit: `feat: make the opencode config path editable in settings`.

---

### Task 8: Trang Tools — nhớ lựa chọn, reset, tìm kiếm, thu/giãn, chọn theo provider, badge audio/video (§9.1–§9.5, §9.7)

**Files:**
- Create: `src/RouterBalancing.Core/Tools/ToolsSelection.cs`, `ToolsSelectionResolver.cs`, `ToolsSearch.cs`
- Modify: `src/RouterBalancing.Core/Settings/SettingsKeys.cs` (thêm `ToolsSelection = "toolsSelection"`, `ToolsCollapsedProviders = "toolsCollapsedProviders"`)
- Modify: `router-balancing/Components/Pages/Tools.razor`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs`
- Test: `router balancing test/Tools/ToolsSelectionResolverTests.cs`, `router balancing test/Tools/ToolsSearchTests.cs`

**Interfaces:**
- Produces:
  - `sealed record ToolsSelection(long[] ModelIds, long[] ComboIds)`.
  - `public static class ToolsSelectionResolver`:
    `public static ToolsSelection Resolve(ToolsSelection? stored, IReadOnlyCollection<long> availableModelIds, IReadOnlyCollection<long> availableComboIds, IReadOnlyList<long> defaultModelIds)` — `stored == null` → mặc định đã lọc theo id còn tồn tại + combo rỗng; `stored != null` → chỉ lọc id còn tồn tại cho cả hai (rỗng vẫn là rỗng — không fallback mặc định).
  - `public static class ToolsSearch`: `bool MatchesModel(ToolsModelRow model, string? query)` (khớp `DisplayName`, `ModelId`, `OrdinalIgnoreCase`, query rỗng → true); `bool MatchesCombo(ToolsComboRow combo, string? query)` (khớp `Name`).
  - `Tools.razor`:
    - `_selectedModels`/`_selectedCombos` nạp qua resolver; mọi thay đổi tick (model, selectFree, clearAll, chọn theo provider, reset, tick combo ở Task 9) ghi ngay `Settings.Set(SettingsKeys.ToolsSelection, new ToolsSelection(...))` — id sort tăng dần trước khi lưu.
    - Nút "Về mặc định": `_selectedModels = DefaultSelectedIds`, `_selectedCombos.Clear()`, persist, regenerate (đúng quy tắc C7 đã tính ở `GetPageDataAsync`).
    - Ô search (`@bind` + `@bind:after`) lọc không phân biệt hoa thường; `_visibleGroups` = nhóm + model khớp (khi search rỗng vẫn giữ nhóm rỗng như cũ để hiện CTA free), ẩn nhóm không kết quả khi đang lọc; header hiện `khớp/tổng`.
    - Header provider là nút thu/giãn theo `ProviderId`; trạng thái lưu `Settings.Set(SettingsKeys.ToolsCollapsedProviders, long[])`.
    - Hai nút nhỏ trong header: chọn hết / bỏ hết **chỉ model đang hiển thị sau lọc**.
    - Badge hàng model thêm audio/video (chỉ khi `== true`): `🔊 @L["models.badge.audio"]` variant Purple, `🎬 @L["models.badge.video"]` variant Pink.
    - `RegenerateAsync` khi lỗi phải xoá `_json`/`_skipped` (fix backlog D14 — preview cũ không được khớp tick mới).
  - i18n EN/VI mới: `tools.resetDefault`, `tools.search.placeholder`, `tools.group.selectAll`, `tools.group.selectNone`.

- [ ] **Step 1: Viết test đỏ:**
  - `ToolsSelectionResolverTests` `[RF-5]`:
    - `Resolve_NoStoredSelection_UsesDefaultsFilteredByAvailableIds`
    - `Resolve_StoredSelection_DropsStaleModelAndComboIds`
    - `Resolve_StoredEmptySelection_StaysEmptyNotDefaults`
    - `Resolve_StoredAllStale_ReturnsEmpty`
  - `ToolsSearchTests`:
    - `MatchesModel_EmptyQuery_True`; `MatchesModel_DisplayNameCaseInsensitive`; `MatchesModel_ModelIdMatchesWhenDisplayNameSet`; `MatchesModel_NoMatch_False`; `MatchesCombo_CaseInsensitiveName`; `MatchesCombo_EmptyQuery_True`.
- [ ] **Step 2:** Chạy `--filter "FullyQualifiedName~ToolsSelectionResolverTests|FullyQualifiedName~ToolsSearchTests"` → FAIL.
- [ ] **Step 3:** Implement helper + UI + i18n. Giữ `_selectedCombos` được persist/regenerate dù Task 9 mới có UI tick combo (để không mất dữ liệu về sau).
- [ ] **Step 4:** Filter trên → PASS; full suite → không lỗi mới; build app TFM; nếu có class Tailwind mới → vite build + commit output.
- [ ] **Step 5:** Commit: `feat: remember tools selection and add search, collapse and group select`.

---

### Task 9: Trang Tools — hàng combo + nút ghi config (§9.6, §9.8)

**Files:**
- Modify: `router-balancing/Components/Pages/Tools.razor`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs`
- Không thêm test C# mới (hành vi service đã ghim ở Task 3/6; UI không có bUnit — xác nhận bằng build + smoke tay ở Task 10).

**Interfaces:**
- Consumes: `ToolsComboRow` (Task 3), `IOpenCodeConfigService.WriteAsync` (Task 6), `Settings.OpenCodeConfigPath`/`OpenCodeConfigPaths.Expand` (Task 7), helper chọn/lọc (Task 8).
- Produces (UI):
  - Mục "Combo" riêng trong khối chọn: mỗi hàng = checkbox + tên + badge mode (`combos.mode.*`) + số member + badge capability dương (ctx, 🛠/🔊/👁/⚡/🎬 — chỉ hiện khi `== true`, false/null ẩn theo spec §11); tick ghi selection + regenerate; khi search chỉ hiện combo khớp.
  - Nút "Ghi vào config opencode" cạnh Copy/Download (disabled khi `_json is null` hoặc `_writing`): hiện đường dẫn hiện tại; gọi `ConfigSvc.WriteAsync(path, _selectedModels.ToArray(), _selectedCombos.ToArray(), _baseUrl, _apiKey)`; thành công → toast `tools.write.done` kèm path; lỗi → `Log.Error` + toast `ex.Message`, **không** đổi `_json`/`_skipped`.
  - i18n EN/VI mới: `tools.combos.title`, `tools.combo.members` (format `{0}`), `tools.write`, `tools.write.path`, `tools.write.done` (format `{0}`).

- [ ] **Step 1:** Render mục Combo (dùng `_visibleCombos` từ Task 8) + toggle `ToggleComboAsync(id, on)` (persist + regenerate, `@key` theo combo id).
- [ ] **Step 2:** Thêm nút ghi + handler `WriteConfigAsync` với busy-guard `_writing`; `path = OpenCodeConfigPaths.Expand(Settings.OpenCodeConfigPath)`; hiển thị path dưới nút bằng `code`.
- [ ] **Step 3:** Thêm i18n EN/VI.
- [ ] **Step 4:** Chạy `--filter "FullyQualifiedName~TranslationParity"` → PASS; full suite → không lỗi mới; build app TFM; vite build nếu có class mới.
- [ ] **Step 5:** Commit: `feat: pick combos and write the config from the tools page`.

---

### Task 10: Nghiệm thu + cập nhật tài liệu + PR

**Files:**
- Modify: `docs/backlog.md` (đánh dấu A1–A8 đã xong, bỏ ghi chú OPEN về key prefix — C-A đã chốt; ghi chú D14 đã fix)
- Modify: `docs/superpowers/specs/2026-10-19-capability-model-and-tools-round-2-design.md` (dòng Trạng thái: phase 1 PR #31, phase 2 PR này)
- Không sửa code.

- [ ] **Step 1:** Chạy ba gate: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj` (0 warning), full suite (đối chiếu baseline đỏ đã biết), `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`.
- [ ] **Step 2:** Smoke tay trên **bản sao** file config thật trước (`Copy-Item $env:USERPROFILE\.config\opencode\opencode.json <temp>`), trỏ Settings path vào bản sao:
  - Tick vài model + một combo → "Ghi vào config opencode" → `.bak` tồn tại; `plugin`/`mcp`/`agent` còn nguyên; block cũ trong `provider` biến mất; block mới trong `providers` có key `<Identifier>/<id>` và `settings.timeout:false`; opencode đọc được file.
  - Tắt app, mở lại → lựa chọn còn nguyên; "Về mặc định" về đúng nhóm C7; search "beam" ra model/combo; thu một provider rồi mở lại → trạng thái đúng.
  - File JSON hỏng (sửa tay một bản sao) → nút ghi báo lỗi, file không đổi.
  - Giữ app chạy với path thật (`~/.config/opencode/opencode.json`) chỉ sau khi bản sao đã đúng.
- [ ] **Step 3:** Cập nhật backlog + spec status; commit `docs: mark tools round 2 complete in backlog`.
- [ ] **Step 4:** Push nhánh + mở PR vào `master` (master protected), body theo format Changes / Verification (như PR #31/#32), liệt kê test đã chạy và kết quả smoke.

---

## Ghi chú cho người thực hiện

- Thứ tự Task 1→9 là bắt buộc: Task 3 cần record/key của Task 2; Task 6 cần writer Task 5; Task 9 cần service/writer/settings/helper của Task 3/6/7/8.
- Không được nới bất kỳ luật nào của phase 1: mọi consumer vẫn đọc capability qua `ModelCapabilityView`; `ComboCapabilities` chỉ là dẫn xuất giao-an-toàn, không query fact trực tiếp.
- Quyết định negative reasoning (`SupportsThink == false` → `supported:false`) là chủ đích theo C-M: tầng writer (Plan 1) chỉ cho `KnownAbsent` đến từ nguồn full-list hoặc user, nên generator không cần kiểm tra `Source`. Nếu vô tình phát hiện DB có `KnownAbsent=1, Source=Endpoint` (dữ liệu phá luật), giữ hành vi hiện tại + log điều tra, **không** hack riêng trong generator.
- Khóa `<Identifier>/<ModelId>` làm thay đổi key so với block cũ: đây là chủ đích C-A. Client gửi `router-balancing/<Identifier>/<ModelId>`; proxy đã resolve format này từ trước (xem `ProxyRequestHandlerTests` — `opencode-free/mimo-...`), không cần sửa engine.
- File config user có thể bị app khác đang chạy giữ khóa: writer ném lỗi và không đụng file — tuyệt đối không fallback sang ghi trực tiếp.
- Không bump version ở plan này; việc phát hành bản kế tiếp do user quyết sau khi smoke.
