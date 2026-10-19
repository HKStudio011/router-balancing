# Capability Model (quan hệ) + Nguồn fill theo provider — Implementation Plan (Plan 1/2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thay 8 cột capability rời rạc trên `Models` bằng các bảng fact quan hệ có nguồn (`catalog`/`endpoint`/`user`), cho mọi consumer đọc qua một accessor duy nhất, và điền capability hàng loạt từ đúng nguồn chính thức của từng provider.

**Architecture:** Ba bảng fact (`ModelCapability`, `ModelModality`, `ModelReasoningEffort`) với quy tắc "không có dòng = chưa biết". Một `IModelCapabilityReader` (đọc, dẫn xuất vision/audio/video từ modality) và một `IModelCapabilityWriter` (ghi theo nguồn, tôn trọng ý user). Chuỗi cung cấp metadata (`IModelMetadataProvider`) được thay bằng các provider theo nguồn chính thức: OpenRouter list, Ollama tags, NVIDIA API Catalog page, probe `/v1/models/{id}`; bỏ `StaticCatalogMetadataProvider`. Người dùng sửa capability qua editor ba trạng thái ở trang Providers.

**Tech Stack:** .NET 10, EF Core + SQLite, xUnit, .NET MAUI Blazor Hybrid (Razor components), System.Text.Json.

**Spec:** `docs/superpowers/specs/2026-10-19-capability-model-and-tools-round-2-design.md` (mọi quyết định U1–U13, C-A…C-R nằm ở §4–§5 của spec; plan này chỉ thực thi **phase 1**).

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên trong mọi csproj.
- Migration EF: `dotnet ef migrations add <Name> --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations`.
- Gate mỗi task: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj` (0 warning) + `dotnet test "router balancing test/router balancing test.csproj"`. **Không** dùng `dotnet build router-balancing.slnx` (NETSDK1082 có sẵn từ trước). Build app TFM: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` (đóng app nếu bị khóa file).
- 1 task = 1 commit, message conventional commit tiếng Anh (`feat:`/`refactor:`/`test:`/`docs:`/`fix:`); chỉ stage đúng file của task.
- Working tree có file WIP của user (`src/RouterBalancing.Core/Engine/*`, `router balancing test/Engine/*`) — không được stage/sửa/revert.
- Comment tiếng Việt cho "tại sao"; XML doc cho public API; exception message và identifier tiếng Anh. Không TODO vô chủ.
- Test dùng `TestDb` (SQLite file tạm, chạy migration thật) + `DbInitializer.Initialize`; HTTP stub là class cục bộ trong từng file test (house pattern: `JsonHandler`/`StubFactory`/`NullLog`).
- Flake đã biết: `ProxyQueueIntegrationTests.Stream_LargeUpstreamBody_CompletesWithoutDeadlock` timeout khi chạy song song (tiền tồn tại) — chạy lại hoặc `--parallel none` trước khi kết luận đỏ, không sửa test đó.
- Tên enum/cột DB đúng nguyên văn ở Task 1 (các task sau phụ thuộc).

## Review Focus

Năm lớp hành vi mà spec ngụ ý nhưng test của từng task dễ bỏ sót — mỗi dòng đã được gán test cụ thể ở task sở hữu (ghi `[RF-n]` trong step):

1. **Nói "không" chỉ khi nguồn liệt kê đầy đủ** — một endpoint riêng trả `supported_modalities` thiếu `image` không được sinh ra `KnownAbsent`, vì thiếu ở đó chỉ là "nguồn không liệt kê". Kỳ vọng: config/UI không bao giờ phát negative từ probe. → Task 5 (OpenRouter full-list) và Task 7 (NVIDIA full-list) + Task 3 (writer từ chối negative khi `sourceIsCompleteList=false`).
2. **Ý user không bị sync đè** — user tick "audio = có" rồi bấm lại "Tải model free": vẫn còn. Kỳ vọng: dòng `Source=User` đứng yên. → Task 3.
3. **Định dạng model id khác nhau giữa các nguồn** — Ollama có `name:tag` (`deepseek-v4.1-flash:1017`), NVIDIA dùng dấu chấm trong id nhưng slug trong catalog dùng gạch dưới (`nvidia/nemotron-3.5-super-vl…` ↔ `…/nemotron-3_5-super-vl….md`). Kỳ vọng: vẫn khớp đúng model. → Task 6, Task 7.
4. **Dữ liệu cũ hỏng** — cột JSON cũ rỗng/`"null"`/không phải array, hoặc `ContextWindow` âm. Migration không được sập; bỏ dòng hỏng và log. → Task 9.
5. **Model bị sync xóa rồi tạo lại** — fact cũ không được "hồi sinh" cùng model mới. Kỳ vọng: FK `ON DELETE CASCADE` dọn sạch. → Task 2.

---

### Task 1: Bảng fact + enum + EF mapping (chưa bỏ cột cũ)

**Files:**
- Create: `src/RouterBalancing.Core/Domain/Entities/ModelCapability.cs`, `ModelModality.cs`, `ModelReasoningEffort.cs`
- Create: `src/RouterBalancing.Core/Capabilities/CapabilityVocabulary.cs` (enum + map string)
- Modify: `src/RouterBalancing.Core/Storage/RouterBalancingDbContext.cs` (thêm `DbSet`, cấu hình khóa ghép, index, `OnDelete` cascade, conversion enum→TEXT)
- Modify: `src/RouterBalancing.Core/Domain/Entities/Model.cs` (thêm 3 navigation collection `ICollection<…>` cho fact)
- Create (do `dotnet ef` sinh): migration `AddModelCapabilityFacts` + Designer + snapshot
- Test: `router balancing test/Capabilities/CapabilityFactSchemaTests.cs`

**Interfaces:**
- Produces:
  - `enum CapabilitySource { Catalog = 0, Endpoint = 1, User = 2 }`
  - `enum CapabilityKind { Tools = 0, Reasoning = 1 }`
  - `enum ModalityKind { Text = 0, Image = 1, Audio = 2, Video = 3 }`
  - `enum ModalityDirection { Input = 0, Output = 1 }`
  - `static class CapabilityNames` — `ToDb(this CapabilityKind) -> "tools"|"reasoning"`, `ToDb(this ModalityKind) -> "text"|"image"|"audio"|"video"`, `ToDb(this ModalityDirection) -> "input"|"output"` và chiều ngược lại `TryFromDb`.
  - Entities: `ModelCapability { long ModelId; CapabilityKind Capability; bool KnownAbsent; CapabilitySource Source; DateTimeOffset UpdatedAt; }`, `ModelModality { long ModelId; ModalityDirection Direction; ModalityKind Modality; bool KnownAbsent; CapabilitySource Source; DateTimeOffset UpdatedAt; }`, `ModelReasoningEffort { long ModelId; string Level; CapabilitySource Source; DateTimeOffset UpdatedAt; }`.
  - Bảng/điểm ràng buộc DB: `ModelCapabilities(ModelId, Capability)` PK, `ModelModalities(ModelId, Direction, Modality)` PK, `ModelReasoningEfforts(ModelId, Level)` PK; tất cả FK → `Models(Id)` `ON DELETE CASCADE`. `DbSet` trong `RouterBalancingDbContext` đặt tên đúng số nhiều như trên để tên bảng khớp.
  - `static class CapabilityNames` cũng cung cấp `FromDb(string) -> CapabilityKind|ModalityKind|ModalityDirection` (ném `FormatException` cho giá trị lạ) — Task 2/3/11 gọi đúng tên này.
  - Kèm hai ràng buộc rẻ của spec §6 trong **cùng migration này**: unique index trên `Providers.Name`; `ProviderAccounts.ApiKeyEncrypted` cho phép NULL.

- [ ] **Step 1:** Viết test schema trong `CapabilityFactSchemaTests.cs`: dùng `TestDb` + `DbInitializer.Initialize`, chèn một `Model` rồi chèn 3 loại fact, đọc lại bằng `DbContext`, assert: tổ hợp khóa ghép trùng bị chặn (`DbUpdateException`), xóa model thì 3 bảng sạch (cascade), enum lưu ra TEXT chữ thường, `FromDb("tools")` round-trip. Gắn `[RF-5]` cho phần cascade. Thêm hai test ràng buộc kèm: `DuplicateProviderName_Rejected` và `AccountWithNullApiKeyEncrypted_Allowed`.
- [ ] **Step 2:** Chạy `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~CapabilityFactSchemaTests"` → Expected: FAIL (loại entity chưa tồn tại → không compile được; đó là red hợp lệ của feature mới).
- [ ] **Step 3:** Tạo enum + `CapabilityNames` + 3 entity + navigation trên `Model` + cấu hình `RouterBalancingDbContext` (khóa ghép, cascade, `.HasConversion(v => CapabilityNames.ToDb(v), s => CapabilityNames.FromDb(s))`).
- [ ] **Step 4:** Chạy lệnh migration ở Global Constraints với tên `AddModelCapabilityFacts`. Kiểm tra file migration chỉ **tạo 3 bảng mới** (không đụng cột nào cũ).
- [ ] **Step 5:** Chạy lại filter test ở Step 2 → PASS. Chạy full suite → PASS.
- [ ] **Step 6:** Commit: `git add` các file trên + migration → `git commit -m "feat: add capability fact tables with per-source provenance"`.

---

### Task 2: `IModelCapabilityWriter` — luật ghi theo nguồn (C-E, C-N, C-P)

**Files:**
- Create: `src/RouterBalancing.Core/Capabilities/ModelCapabilityWriter.cs`, `ICapabilityContracts.cs` (record `CapabilityPatch`)
- Test: `router balancing test/Capabilities/ModelCapabilityWriterTests.cs`

**Interfaces:**
- Consumes: entities + `CapabilityNames` (Task 1).
- Produces:
  - `sealed record CapabilityPatch(IReadOnlyDictionary<CapabilityKind, bool> Capabilities, IReadOnlyDictionary<(ModalityDirection Direction, ModalityKind Modality), bool> Modalities, IReadOnlySet<string> ReasoningEfforts)` — giá trị `false` trong dictionary = "đã biết là không"; key có trong dictionary = nguồn đó có phát biểu về mục đó.
  - `interface IModelCapabilityWriter` với:
    - `Task ApplyAsync(long modelId, CapabilitySource source, CapabilityPatch patch, bool sourceIsCompleteList, CancellationToken ct = default)`
    - `Task ApplyUserEditAsync(long modelId, CapabilityPatch patch, CancellationToken ct = default)`
    - `Task ClearSourceAsync(long modelId, CapabilitySource source, CancellationToken ct = default)`
    - `Task ClearAllAsync(long modelId, CancellationToken ct = default)`
  - `sealed class ModelCapabilityWriter : IModelCapabilityWriter` (ctor: `IDbContextFactory<RouterBalancingDbContext> db, ILogService log`), đăng ký DI singleton trong `MauiProgram.cs`.
  - Luật (pin, implement đúng): (a) với `source != User`: chỉ ghi khi dòng **chưa tồn tại**, hoặc dòng đang có **cùng Source**; (b) `KnownAbsent=1` chỉ được ghi khi `sourceIsCompleteList = true`; (c) `ApplyUserEditAsync` = với mỗi key trong patch, xóa dòng cũ của model đó bất kể nguồn rồi ghi dòng `Source=User` (key không có trong patch → giữ nguyên); (d) mọi ghi set `UpdatedAt = DateTimeOffset.UtcNow`.
  - Hệ quả của luật (b) — đây là cách spec C-M được bảo đảm mà không cần mang `Source` vào `ModelCapabilityView`: **mọi negative có trong DB đều đến từ nguồn full-list hoặc từ user**, nên generator cứ việc phát negative mà không phải xét nguồn (Task 4 test `Generate_NegativeCapabilityFromKnownAbsent_EmitsFalse`).

- [ ] **Step 1:** Test đỏ, các case sau (mỗi case một `[Fact]` tên mô tả hành vi):
  - `ApplyAsync_CatalogFact_WhenEmpty_WritesRow`
  - `ApplyAsync_Catalog_WhenUserRowExists_DoesNotOverwrite` `[RF-2]`
  - `ApplyAsync_SameSource_RewritesItsOwnRow`
  - `ApplyAsync_IncompleteSource_SkipsNegativeFacts` `[RF-1]`
  - `ApplyAsync_CompleteSource_WritesKnownAbsent`
  - `ApplyUserEditAsync_ReplacesAnySourceWithUserRow`
  - `ApplyUserEditAsync_ClearsFactWhenUserSaysUnknown` — patch có key với giá trị… (xem note dưới), và `ClearSourceAsync`/`ClearAllAsync` xóa đúng phạm vi.
  - `ModelDeleted_FactsGone` (cascade, `[RF-5]`).
- [ ] **Step 2:** Chạy filter `--filter "FullyQualifiedName~ModelCapabilityWriterTests"` → FAIL.
- [ ] **Step 3:** Implement `CapabilityPatch` + `ModelCapabilityWriter`. Với "user nói chưa rõ" (muốn xóa phát biểu), dùng `CapabilityPatch.ClearKeys` — thêm property `IReadOnlySet<(string Kind, string Key)> ClearKeys` vào record và luật (c) xử lý ClearKeys trước khi ghi các key khác; document bằng XML doc.
- [ ] **Step 4:** Chạy filter → PASS. Full suite → PASS.
- [ ] **Step 5:** Commit `feat: add capability writer with source-aware write rules`.

---

### Task 3: `IModelCapabilityReader` + `ModelCapabilityView` (accessor duy nhất, C-H, C-O)

**Files:**
- Create: `src/RouterBalancing.Core/Capabilities/ModelCapabilityView.cs`, `ModelCapabilityReader.cs`
- Test: `router balancing test/Capabilities/ModelCapabilityReaderTests.cs`

**Interfaces:**
- Consumes: 3 bảng fact (Task 1).
- Produces:
  - `sealed record ModelCapabilityView(IReadOnlyDictionary<CapabilityKind, bool> Capabilities, IReadOnlyDictionary<(ModalityDirection, ModalityKind), bool> Modalities, IReadOnlySet<string> ReasoningEfforts)` với:
    - `bool? Tools`, `bool? Reasoning` (null = chưa biết)
    - `bool? Supports(ModalityDirection direction, ModalityKind modality)`
    - `bool? Vision => Supports(Input, Image)`, `bool? AudioInput => Supports(Input, Audio)`, `bool? VideoInput => Supports(Input, Video)`, `bool? AudioOutput => Supports(Output, Audio)`
    - `IReadOnlyList<string> InputModalities` / `OutputModalities` — chỉ các modality có giá trị `true`, theo thứ tự `ModalityKind` tăng dần (generator dùng; không sắp xếp thì test so sánh sẽ không ổn định)
    - `IReadOnlyList<string> ReasoningEfforts` (đã sort theo ordinal)
    - `bool IsEmpty`, `static ModelCapabilityView Empty { get; }`
  - `interface IModelCapabilityReader { Task<ModelCapabilityView> ReadAsync(long modelId, CancellationToken ct = default); Task<IReadOnlyDictionary<long, ModelCapabilityView>> ReadManyAsync(IReadOnlyList<long> modelIds, CancellationToken ct = default); }`
  - `sealed class ModelCapabilityReader : IModelCapabilityReader` (ctor: `IDbContextFactory<RouterBalancingDbContext>`), `ReadManyAsync` dùng **một** query `Where(f => ids.Contains(f.ModelId))` cho cả ba bảng (không N+1), model không có fact → `Empty`.
  - Quy tắc biến đổi fact → view: dòng `KnownAbsent=0` → `true`; dòng `KnownAbsent=1` → `false`; không có dòng → key không có trong dictionary (tức `null` khi tra).
- [ ] **Step 1:** Test đỏ: `ReadManyAsync_ReturnsEmptyForModelsWithoutFacts`, `KnownAbsentRow_MapsToFalse`, `PresentRow_MapsToTrue`, `AbsentModality_IsNullNotFalse` `[RF-1]`, `InputModalities_ListsOnlyTrueValuesInStableOrder`, `ReadManyAsync_UsesOneQueryPerTable` (đếm command qua `DbContext` events hoặc assert không có query theo từng model — nếu khó đo thì đổi thành test hành vi: 20 model, kết quả đúng cho cả 20; ghi rõ trong report).
- [ ] **Step 2:** Chạy filter `--filter "FullyQualifiedName~ModelCapabilityReaderTests"` → FAIL.
- [ ] **Step 3:** Implement view + reader.
- [ ] **Step 4:** Filter → PASS; full suite → PASS.
- [ ] **Step 5:** Commit `feat: add single capability reader used by all consumers`.

---

### Task 4: Chuyển consumer sang reader/writer (chưa đụng nguồn mới, chưa bỏ cột)

**Files:**
- Modify: `src/RouterBalancing.Core/Tools/IOpenCodeConfigService.cs` (`ToolsModelRow` đổi kiểu), `src/RouterBalancing.Core/Tools/OpenCodeConfigService.cs`, `src/RouterBalancing.Core/Tools/OpenCodeConfigGenerator.cs` (`OpenCodeConfigModel` đổi kiểu modality/efforts sang `IReadOnlyList<string>?`), `src/RouterBalancing.Core/Combos/ComboCapabilityCalculator.cs`, `src/RouterBalancing.Core/Server/ProxyApp.cs:460-478`
- Modify: `router-balancing/MauiProgram.cs` (đăng ký reader/writer), `router-balancing/Components/Pages/Tools.razor` (bảng chọn model đọc `ToolsModelRow` kiểu mới — bắt buộc sửa cùng task vì đổi chữ ký làm Razor không compile)
- Test: `router balancing test/Tools/OpenCodeConfigGeneratorTests.cs`, `router balancing test/Tools/OpenCodeConfigServiceTests.cs`, `router balancing test/Combos/ComboCapabilityCalculatorTests.cs` (cập nhật)

**Interfaces:**
- Consumes: `IModelCapabilityReader` (Task 3).
- Produces (chữ ký mới, các task sau dùng):
  - `ToolsModelRow(long Id, string ModelId, string? DisplayName, int? ContextWindow, bool? Tools, bool? Reasoning, bool? Vision, bool? AudioInput, bool? VideoInput, IReadOnlyList<string>? InputModalities, IReadOnlyList<string>? OutputModalities)`
  - `OpenCodeConfigModel(string ModelId, string? DisplayName, int? ContextWindow, int? MaxOutputTokens, bool? SupportsTools, bool? SupportsVision, bool? SupportsThink, IReadOnlyList<string>? ThinkEfforts, IReadOnlyList<string>? InputModalities, IReadOnlyList<string>? OutputModalities)`
  - `OpenCodeConfigService` ctor thêm `IModelCapabilityReader capabilities` (tham số cuối).
  - `ComboCapabilityCalculator.FromModel` giữ nguyên `bool?` AND 3 mức nhưng nhận `ModelCapabilityView` thay vì `Model` → đổi thành `private static ComboCapabilities FromView(ModelCapabilityView view)`; `ComboCapabilities` record **không đổi** (`int? ContextWindow, bool? Vision, bool? Audio, bool? Video, bool? Thinking`).
- [ ] **Step 1:** Sửa test trước cho khớp chữ ký mới (`OpenCodeConfigModel` nhận `IReadOnlyList<string>?` thay vì chuỗi JSON; `ToolsModelRow` có `AudioInput/VideoInput/Reasoning`), và thêm test: `Generate_NegativeCapabilityFromKnownAbsent_EmitsFalse` (fact `KnownAbsent` → `tools:false` trong JSON) + `Generate_UnknownCapability_OmitsField`.
- [ ] **Step 2:** Chạy `--filter "FullyQualifiedName~OpenCodeConfig"` → FAIL (compile error là red hợp lệ).
- [ ] **Step 3:** Implement: generator bỏ hết `TryParseStringArray` (không còn JSON để parse — đây là lúc xóa luôn defect D9 ở backlog); `OpenCodeConfigService` đọc `ReadManyAsync` cho toàn bộ model của một nhóm rồi dựng row; `ComboCapabilityCalculator` dùng view; `ProxyApp` xuất `capabilities` từ view (`vision/audio/video/thinking` + `tools`).
- [ ] **Step 4:** Các filter trên → PASS; full suite → PASS (bao gồm `RazorParameterEntityTests` — nếu nó tham chiếu type của cột đã đổi, cập nhật test theo đúng type mới, không tắt test).
- [ ] **Step 5:** Commit `refactor: read model capabilities through the capability reader`.

---

### Task 5: OpenRouter — lấy capability ngay từ danh sách `/v1/models`

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/FreeModelDetector.cs` (`ParseOpenRouterItem` mới dùng cho kind `PricingZeroOrFreeSuffix`), `src/RouterBalancing.Core/Providers/FreeModelSyncService.cs` (ghi fact qua writer, `Source=Catalog`, `sourceIsCompleteList: true` cho OpenRouter/OpenCode; `false` cho AllFree), `src/RouterBalancing.Core/Providers/FreeProviderCatalog.cs` (thêm cờ `bool CompleteCapabilityList` trên `Entry` — mặc định `false`, `true` cho OpenCode + OpenRouter; Task 6 bật cho Ollama)
- Test: `router balancing test/Providers/FreeModelSyncOpenRouterCapabilityTests.cs`, fixture mới trong `router balancing test/Providers/FreeModelFixtures.cs` (`OpenRouterWithCapabilities`)

**Interfaces:**
- Consumes: `IModelCapabilityWriter` (Task 2).
- Produces: `FreeModelInfo` giữ nguyên (đã có đủ field) nhưng không còn được `FreeModelSyncService` ghi vào cột capability cũ — thay bằng `CapabilityPatch`; `FreeModelSyncService` ctor thêm `IModelCapabilityWriter capabilities` (tham số cuối).
- Mapping OpenRouter (pin): `architecture.input_modalities`/`output_modalities` (mảng string) → `ModelModality`; `supported_parameters` chứa `"tools"` → `Tools=true`, không chứa → `Tools=false` (full-list); `architecture.modality == "text+image"` hoặc `reasoning: true` / `supported_parameters` chứa `"reasoning"` → `Reasoning=true`; `context_length` → `ContextWindow` (vẫn là cột).
- [ ] **Step 1:** Fixture `OpenRouterWithCapabilities` (JSON thật-ish: 2 model, một có `input_modalities:["text","image","audio"]` + `supported_parameters:["tools","reasoning"]`, một có `["text"]` + `supported_parameters:[]`), test: `SyncProviderAsync_OpenRouter_WritesModalityAndToolFacts` assert `ModelModality`/`ModelCapability` sinh ra đúng, kể cả `KnownAbsent=1` cho model thứ hai (`[RF-1]`).
- [ ] **Step 2:** Chạy `--filter "FullyQualifiedName~FreeModelSync"` → FAIL.
- [ ] **Step 3:** Implement parse + đổi merge/insert sang writer (bỏ hết chỗ ghi `model.Supports*` trong `ApplyCapabilities` — phần đó chuyển sang `CapabilityPatch`).
- [ ] **Step 4:** Filter `--filter "FullyQualifiedName~FreeModelSync"` → PASS (các test cũ `FreeModelSyncMergeTests`/`FreeModelSyncCatalogTests` phải vẫn xanh; nếu chúng assert cột cũ thì đổi sang assert qua reader — giữ nguyên hành vi được test).
- [ ] **Step 5:** Full suite → PASS. Commit `feat: take openrouter model capabilities from the model list`.

---

### Task 6: Ollama Cloud — nguồn `https://ollama.com/api/tags`

**Files:**
- Create: `src/RouterBalancing.Core/Providers/OllamaTagsMetadataProvider.cs` (`IModelMetadataProvider`)
- Modify: `router-balancing/MauiProgram.cs` (đăng ký chain), `src/RouterBalancing.Core/Providers/FreeProviderCatalog.cs` (bật `CompleteCapabilityList = true` cho entry Ollama — cờ đã được thêm ở Task 5, mặc định `false`)
- Test: `router balancing test/Providers/OllamaTagsMetadataProviderTests.cs`

**Interfaces:**
- Produces: `OllamaTagsMetadataProvider : IModelMetadataProvider` với `Task<ModelMetadata?> FetchAsync(Provider, Model, CancellationToken)`; chỉ nhận diện provider có `BaseUrl` khớp `https://ollama.com` (`FreeProviderCatalog.FindByBaseUrl`).
- Mapping (pin): gọi `GET https://ollama.com/api/tags` (không auth), tìm phần tử `model` khớp `Model.ModelId` **sau khi bỏ suffix `:tag`** (`deepseek-v4.1-flash:1017` → `deepseek-v4.1-flash`) `[RF-3]`; `capabilities` chứa `vision` → `InputModalities=["image"]`; `tools` → `Tools=true`; `thinking` → `SupportsThink=true`; `completion` bỏ qua. Provider này là **full-list** → được phép sinh negative: model khớp mà không có `tools` trong mảng → `Tools=false`.
- [ ] **Step 1:** Test đỏ: `FetchAsync_MatchesModelIgnoringTagSuffix` `[RF-3]`, `FetchAsync_ToolsAbsentInCompleteList_ReportsFalse`, `FetchAsync_NonOllamaProvider_ReturnsNull`, `FetchAsync_HttpError_ReturnsNull` (best-effort, không ném).
- [ ] **Step 2:** Chạy `--filter "FullyQualifiedName~OllamaTags"` → FAIL.
- [ ] **Step 3:** Implement provider; bật `CompleteCapabilityList` cho entry Ollama; `ModelMetadataService` dùng cờ đó khi gọi `ApplyAsync(..., sourceIsCompleteList: …)`.
- [ ] **Step 4:** Filter → PASS; full suite → PASS. Commit `feat: read ollama cloud model capabilities from its tags endpoint`.

---

### Task 7: NVIDIA — index `build.nvidia.com/models.md` + sidebar trang model

**Files:**
- Create: `src/RouterBalancing.Core/Providers/NvidiaCatalogMetadataProvider.cs`
- Modify: `router-balancing/MauiProgram.cs`, `src/RouterBalancing.Core/Providers/FreeProviderCatalog.cs` (`Entry.CatalogIndexUrl` cho NVIDIA = `https://build.nvidia.com/models.md`)
- Test: `router balancing test/Providers/NvidiaCatalogMetadataProviderTests.cs` + fixture `router balancing test/Providers/NvidiaCatalogFixtures.cs`

**Interfaces:**
- Produces: `NvidiaCatalogMetadataProvider : IModelMetadataProvider`; cache index trong bộ nhớ (static, TTL 24h — `DateTimeOffset` inject để test) map `"<publisher>/<slug>"` → path `.md`.
- Quy tắc khớp (pin): `Model.ModelId` đã là `publisher/slug` (dạng `nvidia/nemotron-3.5-super-vl-120b-a12b`); sinh khóa tra bằng cách thay `.`→`_` ở phần slug như catalog của họ, thử cả hai biến thể `[RF-3]`; không khớp → trả `null` (không request trang).
- Parse sidebar `<aside aria-label="Model information">` → `ModelMetadata`: `Context Length` (`262K`/`1M`/`131K` → nhân 1000; `1M`=1_000_000), `Input Modalities`/`Output Modalities` ("Text, Image, Video" → mảng), `Function Calling`/`Structured Output`/`Reasoning` ("Supported" → true; mục có mặt mà giá trị không phải "Supported" → false; mục **không có** → không phát biểu gì). Sidebar là full-list → `sourceIsCompleteList: true`.
- [ ] **Step 1:** Test đỏ với fixture HTML thật (cắt từ trang `nvidia/nemotron-3.5-super-vl-120b-a12b`): `FetchAsync_ParsesSidebarModalitiesAndLimits`, `FetchAsync_FunctionCallingMissing_DoesNotAssertTools`, `FetchAsync_IdNotInIndex_ReturnsNullWithoutPageRequest` `[RF-3]`, `FetchAsync_ContextLengthSuffix_Multiplies` (262K → 262000).
- [ ] **Step 2:** Chạy `--filter "FullyQualifiedName~NvidiaCatalog"` → FAIL.
- [ ] **Step 3:** Implement provider (fetch index một lần dùng `HttpClient` named client `free-model-sync`, parse markdown link; fetch trang model; parse bằng regex/`IndexOf` trên `aria-label="Model information"`, không kéo thư viện HTML).
- [ ] **Step 4:** Filter → PASS; full suite → PASS. Commit `feat: read NVIDIA model capabilities from the official API catalog pages`.

---

### Task 8: Bỏ `StaticCatalogMetadataProvider`, thêm lộ trình "điền phần thiếu" gắn vào 2 nút đã có + model thủ công (U5)

**Files:**
- Delete: `src/RouterBalancing.Core/Providers/StaticCatalogMetadataProvider.cs`, `router balancing test/Providers/StaticCatalogMetadataProviderTests.cs`
- Modify: `src/RouterBalancing.Core/Providers/ModelMetadata.cs` (**đổi 3 field JSON-string sang kiểu đúng**: `IReadOnlyList<string>? InputModalities`, `IReadOnlyList<string>? OutputModalities`, `IReadOnlyList<string>? ThinkEfforts` — để `ModelMetadataService` dịch thẳng sang `CapabilityPatch`, không còn chỗ nào parse chuỗi), `src/RouterBalancing.Core/Providers/ProviderEndpointMetadataProvider.cs` (+ `FreeModelDetector` nếu nó còn trả chuỗi JSON cho các field này), `src/RouterBalancing.Core/Providers/ModelMetadataService.cs` (thêm `Task<int> FillMissingAsync(long providerId, CancellationToken)`; dịch `ModelMetadata` → `CapabilityPatch` qua helper `internal static class MetadataPatchMapper { static CapabilityPatch From(ModelMetadata m, bool completeList); }`; ghi qua `IModelCapabilityWriter`, `sourceIsCompleteList` lấy từ `FreeProviderCatalog.Entry.CompleteCapabilityList`), `src/RouterBalancing.Core/Providers/ModelService.cs` (`FetchFromProviderAsync` gọi `FillMissingAsync` sau khi thêm model mới; `AddManualAsync`/`AddBulkAsync` gọi `TryFillAsync` nếu model chưa có fact), `src/RouterBalancing.Core/Providers/IModelService.cs` + `IModelMetadataService.cs`, `router-balancing/Components/Pages/Providers.razor` (hiện số model được điền + progress trên nút "Lấy model"), trang chứa nút "Tải model free" (progress + thông báo), `router balancing test/Providers/ModelMetadataServiceTests.cs` + `ProviderEndpointMetadataProviderTests.cs` (chữ ký `ModelMetadata` đổi)
- Test: `router balancing test/Providers/ModelMetadataFillMissingTests.cs`

**Interfaces:**
- Produces: `IModelMetadataService.FillMissingAsync(long providerId, CancellationToken ct = default) -> Task<int>` (số model được bổ sung fact; chỉ probe model **còn thiếu** fact — xác định bằng reader, không đếm N request vô ích).
- Luật chi phí (spec §7): gọi tuần tự, tối đa 2 request song song, tổng thời gian giới hạn `TimeSpan.FromSeconds(120)` cho cả đợt (inject trong ctor để test), lỗi từng model → log + đi tiếp.
- [ ] **Step 1:** Test đỏ: `FillMissingAsync_SkipsModelsThatAlreadyHaveFacts`, `FillMissingAsync_ProbesOnlyMissingOnes`, `FillMissingAsync_OneModelFails_ContinuesOthers`, `AddManualAsync_TriggersFillBestEffort`, `TotalTimeout_Exceeded_StopsAndReturnsFilledCount`.
- [ ] **Step 2:** Chạy `--filter "FullyQualifiedName~ModelMetadataFillMissing"` → FAIL.
- [ ] **Step 3:** Implement (bao gồm xóa `StaticCatalogMetadataProvider` + bỏ đăng ký DI của nó; chain còn lại: `NvidiaCatalogMetadataProvider`, `OllamaTagsMetadataProvider`, `ProviderEndpointMetadataProvider`).
- [ ] **Step 4:** Filter → PASS; full suite → PASS. Commit `feat: fill missing capabilities when models are fetched or added`.

---

### Task 9: Editor capability ba trạng thái + API user-sửa (U13, C-N)

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/IModelService.cs` + `ModelService.cs` (thay `UpdateCapabilitiesAsync` bằng `SetCapabilitiesAsync`), `src/RouterBalancing.Core/Providers/IModelMetadataService.cs` + `ModelMetadataService.cs` (thêm `Task ResetToCatalogAsync(long modelId, CancellationToken ct = default)` = `ClearAllAsync` rồi chạy chain với `Source=Catalog`), `router-balancing/Components/Pages/Providers.razor` (modal + badge), `src/RouterBalancing.Core/Localization/Translations.cs` (key mới), `router balancing test/Localization/TranslationParityTests.cs` (nếu cần)
- Test: `router balancing test/Providers/ModelServiceCapabilityEditTests.cs` (mới), `router balancing test/Providers/ModelServiceTests.cs` (xóa/các test của `UpdateCapabilitiesAsync` cũ — method này bị thay, test nào kiểm cùng hành vi thì viết lại theo `SetCapabilitiesAsync`, không bỏ trống hành vi đã từng được test)

**Interfaces:**
- Produces: `IModelService.SetCapabilitiesAsync(long modelId, int? contextWindow, int? maxOutputTokens, CapabilityPatch patch, CancellationToken ct = default)` — `patch` do UI dựng từ ba trạng thái (có/không/chưa rõ → key không có trong patch = giữ nguyên; ClearKeys = "chưa rõ"), ghi `Source=User` qua `ApplyUserEditAsync`. `UpdateCapabilitiesAsync` cũ bị xóa.
- UI (spec §9.10): modal có bảng: `tools`, `reasoning` (ba lựa chọn), và modality theo **từng hướng** input/output (text/image/audio/video) ba lựa chọn; mỗi ô hiện nguồn hiện tại (`catalog`/`endpoint`/`user`) + nút "Đặt lại theo catalog" gọi `IModelMetadataService.ResetToCatalogAsync` (thêm method này: `ClearAllAsync` rồi chạy chain với `Source=Catalog`).
- [ ] **Step 1:** Test đỏ: `SetCapabilitiesAsync_UserSaysNo_WritesKnownAbsentRegardlessOfSourceList`, `SetCapabilitiesAsync_ClearKey_RemovesFact`, `SetCapabilitiesAsync_OutOfRangeContext_Throws`, `ResetToCatalogAsync_RemovesUserFactsAndRefillsFromCatalog` `[RF-2]`.
- [ ] **Step 2:** Chạy `--filter "FullyQualifiedName~ModelServiceCapabilityEdit"` → FAIL.
- [ ] **Step 3:** Implement API + UI + i18n (thêm key `models.cap.*` cho nhãn ba trạng thái và nguồn, đủ EN/VI).
- [ ] **Step 4:** Filter → PASS; `--filter "FullyQualifiedName~TranslationParity"` → PASS; full suite → PASS; build app TFM → PASS.
- [ ] **Step 5:** Commit `feat: three-state per-direction capability editor in providers page`.

---

### Task 10: Badge tools/audio/video ở Providers + Combos qua reader (U11)

**Files:**
- Modify: `router-balancing/Components/Pages/Providers.razor` (cột capability trong bảng model), `router-balancing/Components/Pages/Combos.razor` + `router-balancing/Components/Shared/ComboPicker.razor` (badge capability combo), `src/RouterBalancing.Core/Localization/Translations.cs` (key `models.badge.tools`)
- Test: `router balancing test/Localization/TranslationParityTests.cs` (tự chạy), không thêm test C# mới

**Interfaces:**
- Consumes: `ModelCapabilityView` (Task 3), `IModelCapabilityReader` (Task 3), `ComboCapabilities` (không đổi).
- Badge chỉ hiện khi giá trị `true`; `false`/`null` không hiện badge (quyết định §11 của spec).
- [ ] **Step 1:** Đổi mọi chỗ `m.Supports*` trong razor sang đọc từ view đã được load sẵn (Providers.razor: load `ReadManyAsync` một lần trong `LoadAsync`, truyền view vào bảng; không gọi reader trong vòng lặp render).
- [ ] **Step 2:** Thêm badge `tools` (U11) và audio/video (giờ đã có dữ liệu từ Task 5–7) vào cột capability; Combos/ComboPicker hiện badge từ `ComboCapabilities` như cũ nhưng thêm `tools` nếu `ComboCapabilities` có field — **không** đổi record `ComboCapabilities`; nếu muốn hiện tools cho combo thì Task 11 của Plan 2 mới cần; ở đây chỉ đảm bảo không còn tham chiếu cột cũ.
- [ ] **Step 3:** Build app TFM + full suite → PASS.
- [ ] **Step 4:** Commit `feat: show tools and media badges from the capability reader`.

---

### Task 11: Migration bỏ 8 cột capability cũ (U2, C-Q)

**Files:**
- Create (do `dotnet ef` sinh): migration `DropLegacyModelCapabilityColumns` + Designer + snapshot
- Modify: `src/RouterBalancing.Core/Domain/Entities/Model.cs` (xóa 8 property), `src/RouterBalancing.Core/Providers/ModelMetadata.cs` (nếu còn field trùng), mọi chỗ còn tham chiếu (`rg SupportsVision|SupportsThink|SupportsAudio|SupportsVideo|SupportsTools|ThinkEfforts|InputModalities|OutputModalities src router-balancing "router balancing test"`)
- Test: `router balancing test/Storage/CapabilityBackfillTests.cs`

**Interfaces:**
- Produces: schema cuối như spec §6; `Model` không còn 8 property.
- [ ] **Step 1:** Test backfill phải dựng DB ở **schema cũ** rồi mới apply migration mới — kỹ thuật pin: `db.Database.Migrate("20261019064905_AddModelToolsOutputLimit")` (migration cuối trước Task 1) để tạo schema cũ, chèn dữ liệu cũ bằng SQL thô (`INSERT INTO Models(... SupportsVision=1, SupportsTools=0, ThinkEfforts='["low","high"]', InputModalities='["text","image"]', SupportsAudio=1 ...)`, kèm một row `InputModalities='garbage'` và một row `ContextWindow=-5`), sau đó `db.Database.Migrate()` để chạy `DropLegacyModelCapabilityColumns`, rồi assert qua reader: vision→`ModelModality(input,image)`, `SupportsTools=0`→`KnownAbsent=1, Source=Catalog` (C-Q), `SupportsAudio=1`→`ModelModality(input,audio)`, `SupportsVision=0`→**không có dòng** (C-K) `[RF-4]`, và row `garbage`/`ContextWindow` âm → không sinh dòng, migration không ném.
- [ ] **Step 2:** Chạy filter `--filter "FullyQualifiedName~CapabilityBackfill"` → FAIL (migration chưa tồn tại).
- [ ] **Step 3:** Tạo migration `DropLegacyModelCapabilityColumns`: **backfill trong `Up()`** bằng `ExecuteSqlRaw` (INSERT…SELECT từ cột cũ, dịch semantics theo C-K/C-Q), rồi `DropColumn` 8 cột. `Down()` tạo lại cột (không cần hồi phục dữ liệu — ghi rõ trong XML doc của migration).
- [ ] **Step 4:** Xóa 8 property khỏi `Model`, sửa mọi tham chiếu còn lại (dùng `rg` ở Files:) sang reader/writer.
- [ ] **Step 5:** Filter backfill → PASS; full suite → PASS; build app TFM → PASS.
- [ ] **Step 6:** Commit `refactor: drop legacy capability columns after backfilling facts`.

---

### Task 12: Nghiệm thu + PR

**Files:** (không sửa file — chỉ chạy gate + PR)

- [ ] **Step 1:** Chạy trên **bản sao DB production của user** (`Copy-Item $env:APPDATA\router-balancing\router-balancing.db <temp>`) với binary mới: migration chạy sạch, rồi đối chiếu số dòng fact với spec §0 (12 vision → 12 `image/input`, 1 audio, 1 video, 0 tools-false, 10 modalities, 10 efforts). Ghi output vào report.
- [ ] **Step 2:** Chạy ba gate: Core build, full suite, app TFM build. Flake `ProxyQueueIntegrationTests` → chạy lại một lần trước khi kết luận.
- [ ] **Step 3:** Smoke checklist (user, app bản mới): OpenCode Free → "Tải model free" → badge audio/video hiện đúng model có trong catalog; OpenRouter Free → "Lấy model" → model có tools/vision; NVIDIA → chỉ model còn trang catalog có badge, model legacy vẫn trống; tick tay "audio output = có" → reload vẫn còn sau khi bấm lại sync; "Đặt lại theo catalog" → mất tick tay, lấy lại của catalog; `GET /v1/models` của proxy trả `capabilities` từ fact.
- [ ] **Step 4:** Push + PR vào `master` (master protected), body theo format PR #29/#30: Changes / Verification.

---

## Ghi chú cho người thực hiện

- Thứ tự Task 1→11 là **bắt buộc**: Task 11 (bỏ cột) chỉ chạy được khi không còn chỗ nào đọc cột cũ (Task 4, 5, 8, 9, 10 đã chuyển hết).
- Task 4, 5, 8, 9, 10 **không** bỏ cột — chúng chỉ ngừng ghi/đọc cột cũ. Cột vẫn còn cho tới Task 11, nên mỗi commit đều build và test xanh.
- Đây là lựa chọn của tôi (không đổi nghiệp vụ theo U2): "một migration dời dữ liệu rồi bỏ cột" được thực hiện ở Task 11 — tạo bảng mới ở Task 1 là bắt buộc về kỹ thuật vì không thể có bảng fact trước khi có chỗ ghi.
