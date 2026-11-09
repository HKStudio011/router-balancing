# Backlog - cập nhật 2026-11-07

Nguồn: phiên brainstorm sau khi ship PR #30 (trang Tools + free sync catalog).
Ký hiệu: `[ ]` chưa làm · **OPEN** = chưa chốt quyết định · *đo được* = có số liệu/chứng minh trong code, không phải cảm giác.

Trạng thái nền: `master` = `0e04681` (PR #33 đã merge). C2 + D21 + C3 + C5 (provider header) xong trên nhánh `feat/combo-item-integrity` ngày 2026-10-21.

---

## A. Trang Tools (OpenCode config) — vòng kế tiếp

**Đã xong 2026-10-21** trên nhánh `feat/tools-round-2` (Plan 2, spec capability-model §9; PR #33). Các mục dưới giữ lại làm hồ sơ.

1. `[x]` **Nhớ lựa chọn đã tick + nút "Reset về mặc định"** — `AppSettings(Key, ValueJson)` với `ToolsSelection`, resolver lọc id đã xoá; "Về mặc định" = tập C7 (model `Enabled` của preset OpenCode Free khi provider bật).
2. `[x]` **Đưa combo vào phần chọn model** — combo là mục riêng trong bảng chọn; tick combo = **một entry config key = tên combo** (U12), không kéo/nhân bản model thành viên; capability là giao an toàn của member (C-L).
3. `[x]` **Badge audio/video trên Tools và Providers** — đọc từ fact tables qua `ModelCapabilityView`; badge chỉ hiện khi đã biết là có (spec §11).
4. `[x]` **Expand/collapse** nhóm model bằng cách bấm tên provider — trạng thái lưu theo `ProviderId` trong `AppSettings`.
5. `[x]` **Tìm kiếm model/combo** (lọc trên danh sách đã nạp; khớp DisplayName/ModelId/tên combo, không phân biệt hoa thường; số đếm x/y).
6. `[x]` **Chọn hết / bỏ chọn hết cho từng provider** — chỉ áp dụng model đang hiển thị sau lọc.
7. `[x]` **Nút "Ghi vào config opencode"** đặt cạnh nút Sao chép.
   - Đường dẫn mặc định `~/.config/opencode/opencode.json`, đổi được trong Settings (field + nút "mặc định").
   - Đã chốt (C-A, 2026-10-20): model key luôn `<Identifier>/<ModelId>`, chỉ bare khi provider chưa có `Identifier` — **hết OPEN** (nhánh trước: prefix như block cũ).
   - Splice text bằng token `Utf8JsonReader` (an toàn JSONC/comment, map offset byte→char cho non-ASCII), xoá block cũ trong `provider`+`providers`, chèn block mới vào `providers`; backup `.bak` + ghi tạm rồi đổi tên; file hỏng JSON → báo lỗi, không ghi.
   - Block v2: `settings.timeout:false` (C-D), không `modelID`/`family` (C-B/C-C), entry combo (U12), negative chỉ từ full-list/user (writer bảo đảm C-M).
8. `[x]` **Badge tools trong danh sách capability của model ở trang Providers/Combos** — Providers đã có từ phase 1; Combos thêm badge `🛠` từ `ComboCapabilities.Tools`.

---

## B. Thiếu hụt đã đo được (lỗi thiết kế/nghiệp vụ, không phải tính năng mới)

**Đã xong 2026-10-21:** B1–B5 giải quyết ở capability model Plan 1 (PR #31) — `ModelMetadata`/metadata provider theo nguồn có audio/video/tools, `FreeModelSyncService` ghi fact qua `CapabilityPatch`, `KnownAbsent` biểu diễn "biết là không có", unique `Providers.Name`, `ApiKeyEncrypted` nullable. **B6 còn lại.**

1. `[x]` **Chain fill metadata không có audio/video/tools** *đo được*: `ModelMetadata` (src/RouterBalancing.Core/Providers/ModelMetadata.cs) chỉ có `ContextWindow/SupportsVision/SupportsThink/ThinkEfforts/InputModalities/OutputModalities`. Số liệu DB thật (137 model): `SupportsAudio=true` **1**, `SupportsVideo=true` **1** (chỉ do tick tay), `SupportsTools IS NULL` **127**, `ContextWindow IS NULL` **102**, có `InputModalities` **10**.
2. `[x]` **Đường sync preset free (OpenRouter/NVIDIA/Ollama) không gọi `TryFillAsync`** — `FreeModelSyncService` merge chỉ ghi DisplayName/ContextWindow; trong khi `ProviderEndpointMetadataProvider` đã biết đọc `architecture.input_modalities`/`supported_modalities` (OpenRouter trả đủ).
3. `[x]` **Không biểu diễn được "biết là không có"**: `ModelMetadataService` chỉ ghi khi field đang trống và bool chỉ ghi `true` → không sửa được dữ liệu cũ, không phân biệt catalog/endpoint/user.
4. `[x]` **`Providers.Name` không có unique index** nhưng code tra preset theo tên (`FreeModelSyncService` → `FreeProviderCatalog.FindByDisplayName`) và seed theo tên (`DbInitializer`) → hai provider trùng tên cho hành vi khó đoán.
5. `[x]` **`ProviderAccounts.ApiKeyEncrypted` NOT NULL** trong khi tồn tại luồng "tài khoản không key" → đang dùng chuỗi rỗng làm sentinel; nên là NULL.
6. `[ ]` **`LogEntries` (28.629 dòng, chiếm phần lớn 6.6MB DB) không có retention**, và một bảng gánh hai vai (log + số liệu thống kê `Outcome/HttpStatus/DurationMs/Tokens`). Đồng thời chứa cả `ModelId/ProviderId` lẫn snapshot chuỗi `ModelKey/ProviderKey` → hai bản ghi của cùng một sự thật; phải chốt snapshot chuỗi là nguồn thống kê, id chỉ là liên kết best-effort.

Ghi chú vật lý: DB **không** mảnh vụn (`integrity_check ok`, `freelist_count 0`, `journal_mode=wal`) — nên mọi mục ở đây là chuyện thiết kế, không phải vacuum.

---

## C. Chuẩn hoá schema theo mô hình quan hệ (hướng đã user chốt 2026-10-19)

1. `[x]` **Capabilities của `Models`** — ưu tiên 1, vì chặn trực tiếp A3, A8, B1–B3 **(xong 2026-10-20, PR #31)**:
   - `ModelCapability(ModelId, Capability, KnownAbsent)`: **không có dòng = chưa biết**, có dòng = đã biết (true/false). Một quy tắc cho mọi capability, thay cho `SupportsTools` nullable + 4 bool NOT NULL semantics "false = không hoặc chưa rõ".
   - `ModelModality(ModelId, Direction, Modality)` (Direction ∈ input/output) thay JSON `InputModalities/OutputModalities`; `ModelReasoningEffort(ModelId, Level)` thay JSON `ThinkEfforts`.
   - **Bỏ các cột bool dẫn xuất** (`SupportsVision/Audio/Video/Think`) — suy ra từ bảng trị tại đúng một chỗ (DTO/repository), hết cảnh "một sự thật chứa hai nơi".
   - Thêm `Source` (catalog/endpoint/user) + `UpdatedAt` cho mỗi fact để sync không đè ý người dùng và để sửa được giá trị cũ.
   - `ContextWindow`/`MaxOutputTokens` giữ là cột (scalar nguyên tử, có dùng để hiển thị/sắp xếp).
   - **OPEN**: một migration duy nhất (dời dữ liệu cũ sang bảng trị + bỏ cột) hay bắc cầu (thêm bảng mới, code đọc bảng mới, giữ cột cũ một release rồi bỏ)? → đã chọn migration dời + bỏ cột (`DropLegacyModelCapabilityColumns`).
2. `[x]` **Bỏ liên kết đa hình ở `ComboItems`** **(xong 2026-10-21, `feat/combo-item-integrity`)** — chọn **giữ một bảng** + CHECK `(TargetModelId IS NULL) <> (TargetComboId IS NULL)` thay vì tách hai bảng: `Position` là thứ tự chung, tách bảng sẽ không enforce được unique xuyên bảng. Đã thêm unique index `(ComboId, Position)`; chặn chu trình đã có ở `ComboService.ValidateNoCycleAsync` từ trước.
3. `[x]` **`UsageDaily(SubjectKind, SubjectId, Date, Requests, Tokens)`** thay bộ đếm nằm trong bảng cấu hình — **xong 2026-10-21**, migration `AddUsageDaily`:
   - PK ghép `(SubjectKind, SubjectId, Date)`, `SubjectKind` lưu `client_key`/`provider_account`, `Date` lưu `yyyy-MM-dd` UTC; **không FK** để xóa key/account vẫn giữ lịch sử.
   - Backfill counter cũ (bỏ dòng 0/0, ngày NULL/hỏng) rồi drop `RequestsUsed/TokensUsed/UsageDate` khỏi cả `ClientKeys` và `ProviderAccounts`.
   - Ghi atomic upsert; request client key đếm ở middleware, token ở `RequestUsageSink`; provider account đếm request + token sau mọi 2xx.
   - UI vẫn hiển thị "hôm nay" qua `[NotMapped]` totals do `ListAsync` nạp từ ledger.
   - **Follow-up:** enforce `ProviderAccount.DailyTokenLimit`/`DailyRequestLimit` (hiện limit chỉ để hiển thị).
4. `[x]` **`ProbeResult`** — đã kiểm tra 2026-10-21: cả 3 bảng (`Providers`, `ProviderAccounts`, `OutboundProxies`) đều dùng `bool? LastTestSuccess` / `DateTimeOffset? LastTestAt` / `string? LastTestMessage`, DB cũng nullable; không cần migration. Khác biệt còn lại chỉ là `LastTestMessage` maxLength 500 ở proxy (không đáng đổi schema).
5. `[x]` **`ProviderHeader(ProviderId, Name, Value)`** thay JSON `Providers.ExtraHeaders` — xong 2026-10-21, migration `AddProviderHeaders` (PK ghép + `NOCASE`, backfill JSON, bỏ cột). `[ ]` **`AccountModelPattern(AccountId, Pattern)`** thay `ModelPatterns` — độ lợi thấp, làm sau.
6. `[x]` **Không chuẩn hoá `AppSettings(Key, ValueJson)`** — key/value là mô hình đúng cho config app; mục A1 sẽ nằm đây.
7. `[ ]` Chặn input bằng CHECK (port, giới hạn token, độ dài tên) — hiện toàn schema **không có CHECK nào**, mọi ràng buộc数值 đều nằm trong code.

Thứ tự gợi ý: C1 → (A4,A5,A6,A8 là UI rẻ, không phụ thuộc schema) → A3 → C2 + B4/B5 (rẻ, thêm ràng buộc) → A7 → C3 → C4/C5, B6. **Còn lại sau 2026-10-21: B6 → C7; C5 còn `AccountModelPattern` (độ lợi thấp); C3 còn enforce `DailyTokenLimit`/`DailyRequestLimit` (follow-up).**

---

## E. OpenCode v2 — fingerprint + phân loại lỗi free-tier (ghi 2026-11-07)

Bối cảnh: đối chiếu với nguồn mở `anomalyco/opencode` khi thiết kế dataset export
(`docs/superpowers/specs/2026-11-06-dataset-jsonl-export-design.md` §11). Chưa probe live nên
mới chỉ là bằng chứng phía upstream, chưa phải kết luận cho code này.

- `[ ]` **E1 Fingerprint OpenCode có còn qua gate với client v2 không.** `OpenCodeFreeFingerprint.Quartet`
  = `bash/glob/grep/read` (dòng 38) và `sidecar/opencode-proxy/anonymous-tools.json`
  = `shell/edit/glob/grep/read` là vocabulary v1. OpenCode 2.0.32/2.1.0 dùng tool namespace
  `opencode.*` + tool code-mode `execute` (upstream issue #57346, mở, cập nhật 2026-11-03, than
  `Read/Grep/Glob/Bash` vắng mặt khỏi catalog). Cần **spike probe live** opencode.ai:
  (a) body có quartet + UA `opencode/1.18.31`, (b) body tool kiểu v2 + UA `opencode/2.x`,
  (c) bỏ quartet — ghi lại status + `error.type` từng nhánh rồi mới quyết định cập nhật
  `Quartet`/`anonymous-tools.json`. *do được*: comment probe 2026-10-04/2026-10-13 trong
  `OpenCodeFreeFingerprint.cs` (gate 403 `FreeTierError` theo UA regex + header `x-opencode-*`
  + quartet) — các probe đó cũ hơn các phát hành 2.x được ghi nhận.
- `[ ]` **E2 Tách `FreeTierError` (refusal) khỏi quota thật.** `DispatcherLoop.IsGlobalQuota`
  (dòng 669-675) chỉ nhận `429 + body chứa "FreeUsageLimitError"`; 401/403 → Fatal cấp Account
  (`RetryClassifier.cs:17`). Upstream v2 khi hết quota báo
  `AI_APICallError: Upstream request failed: Invalid credential` **không** kèm `Retry-After`
  (upstream issue #54338, 2026-10-10), còn refusal vẫn là 403 `FreeTierError`
  "OpenCode's free tier can only be used from within OpenCode" → hai điều kiện đang lẫn vào
  nhau: refusal có thể bị tính là "key sai", quota thật có thể không mở window. User ghi nhận
  hiện tượng gộp chung này. Cần đối chiếu log thật (bảng `LogEntries`, filter theo
  `error.type`) trước khi đổi phân loại. Liên quan tới `ProviderQuotaGate` + `QuotaCooldownBadge`.

---

## D. Deferred minors từ review PR #30 (ledger SDD bị git-ignore, nên ghi lại đây)

`[ ]` D1 `FreeModelDetector.LimitOf` dùng `GetInt32()` → nên `TryGetInt32` (skip entry hỏng, không sập cả lần sync). `[ ]` D2 `ThinkEffortsOf` match `reasoning_options.type` bằng `OrdinalIgnoreCase` (spec literal lowercase → `Ordinal` chặt hơn). `[ ]` D3 thiếu test nhánh `["toggle", ...values]` và modalities thiếu hẳn. `[ ]` D4 doc enum `FreeSuffix` còn nhắc "OpenCode Zen". `[ ]` D5 làm ổn định `ProxyQueueIntegrationTests` (flake timeout dưới song song, tiền tồn tại). `[ ]` D6 test guard "0 free models" không phân biệt được với parse failure (assert message). `[ ]` D7 test catalog không assert `Method == GET` / không có `Authorization`. `[ ]` D8 nhánh update capabilities trên model non-manual (catalog) chưa test trực tiếp. `[x]` D10 thiếu test `SupportsTools=false` → emit `"tools": false` (đã có `Generate_NegativeCapabilityFromKnownAbsent_EmitsFalse`, Task 4). `[ ]` D11 service: thiếu test "preset free tắt + model Enabled → DefaultSelectedIds rỗng". `[ ]` D12 test dedupe không phân biệt "free thắng" với "Id nhỏ thắng"; thiếu test Ordinal case-sensitive. `[ ]` D13 `SkippedDuplicateModelIds` lặp entry khi ≥3 model cùng ModelId. `[x]` D14 `Tools.razor`: `RegenerateAsync` catch không xóa `_json`/`_skipped` → preview cũ không khớp tick hiện tại (đã fix trong Plan 2 — catch xoá preview trước khi toast). `[ ]` D15 `Tools.razor`: `DownloadAsync` không bắt `JSException` khi `rb-tools.js` chưa load. `[ ]` D16 `Tools.razor`: `StateHasChanged` sau `await` khi component đã dispose. `[ ]` D17 `Tools.razor`: `CreateKeyAsync` không busy-guard → double-click tạo 2 client key thật. `[ ]` D18 parser: root JSON không phải object → `InvalidOperationException` thay vì `FreeModelSyncException` (latent, giống `Parse` cũ — sửa thì làm đối xứng cả hai). `[ ]` D19 các call site `ResolveFirstEnabledKey` khác (`ProviderEndpointMetadataProvider`, `ModelService`) vẫn resolve vô điều kiện → cùng lỗi DPAPI khi DB sang máy khác. `[ ]` D20 doc `FreeSuffix`/`ProviderId` đã sửa một phần — rà còn mô tả cũ nào nữa không. `[x]` D21 (Plan 2 review) tên combo chứa `/` sẽ trùng namespace `<Identifier>/<ModelId>` khi Engine resolve (`ComboResolver` ưu tiên provider-scoped trước khi fallback combo-name) — đã chặn ở `ComboService.ValidateAsync` (create + update) với `ComboValidationError.NameContainsSlash`; không đổi thứ tự resolve, combo cũ không tự đổi tên (2026-10-21).
---

## F. Dataset JSONL export — deferred minors (final whole-branch review, 2026-11-09)

Nhánh `feat/dataset-jsonl-export` (spec `docs/superpowers/specs/2026-11-06-dataset-jsonl-export-design.md`, plan `docs/superpowers/plans/2026-11-07-dataset-jsonl-export.md`). Ledger SDD bị git-ignore → ghi lại đây. Final review verdict: BRANCH READY; 3 item FIX-BEFORE-MERGE đã fix ở commit cuối (`ebc91d3`), phần còn lại triage defer/no-action như dưới.

1. `[ ]` **F1 (spec — cần user chốt khi duyệt spec)** `thread_hint` hash trên `messages[0]` → suy biến thành hằng số khi mọi record cùng chung system prompt/instructions đầu (đúng trường hợp chính của client OpenCode) → mất tác dụng nhóm/dedup theo phiên (§7.4/§8.5). Đề xuất final review: hash **message không-phải-system đầu tiên**, bỏ `thread_hint` nếu toàn system. Code hiện đúng spec text — đây là sửa spec + ~5 dòng code + test.
2. `[ ]` **F2 (spec cleanup, gộp với F1)** Spec tự mâu thuẫn ở dòng trạng thái UI: §2 liệt kê "thư mục hiện tại" nhưng format cụ thể §7.7 không có; ví dụ §7.7 "Đã ghi N dòng **hôm nay**" không satisfiable bởi `DatasetStats` (chỉ có counter từ lúc sink start, không có per-day). UI đã theo §7.7 và không nói "hôm nay". Follow-up rẻ: append `Path.GetDirectoryName(Stats.CurrentFile)` vào dòng trạng thái khi non-null.
3. `[ ]` **F3** `DatasetRecordBuilder.Build` ném `InvalidOperationException` với body root JSON hợp lệ nhưng không phải object (`[1]`, `"x"`, `42`) — đã xác minh trên .NET 10; thực tế unreachable qua pipeline (validator 400 trước) và được chứa bởi catch của writer sink (bỏ record + 1 dòng log, không bao giờ ghi byte sai). Sửa: 1 dòng guard `ValueKind` để đúng contract never-throw đã document.
4. `[ ]` **F4** Thiếu test các nhánh mapping chưa chạm: 3 giá trị `lane`, `extra.items` type lạ, input-side reasoning → `extra.reasoning` — đúng chỗ một regression tương lai có thể làm hỏng record mà không báo.
5. `[ ]` **F5** Không có counter trong `DatasetStats` cho record bị bỏ do lỗi ghi đĩa (chỉ có log) → UI mù điểm này; spec tự liệt kê các counter nên implementation đang conform — thêm field = đổi shape record + i18n template, làm một gói riêng.
6. `[ ]` **F6** `SafeDatasetLog` (`ProxyRequestHandler.cs`) không kèm `requestId` (có sẵn trong scope tại call site) → khó tương quan với monitor ring khi export lỗi. 1 dòng.
7. `[ ]` **F7** Boundary validator chưa assert hợp lệ: path đúng 260 ký tự và retention đúng 3650 ngày (rule `> 260` / `is < 0 or > 3650` đã đúng bằng kiểm tra mắt; thêm 2 assert khi chạm file test này).
8. `[ ]` **F8** Record vượt budget SSE (`MaxFieldBytes`) bị đếm `SkippedTruncated` ("cụt") thay vì `SkippedTooLarge` ("quá cỡ") theo §7.6 — chỉ sai nhãn counter (overflow null hoá `CompletionJson` nên size check của builder không bao giờ chạy); sửa đúng cần thêm flag trên `TeeResult`/`DatasetCapture`.
9. `[ ]` **F9** Nhãn counter góc cạnh: record vừa `status != "completed"` (non-stream responses) vừa chứa refusal phrase bị đếm `Refusal` thay vì `Truncated` (check status ở `DatasetRecordBuilder.cs:134` chạy sau check Refusal toàn cục :72, ngược thứ tự ưu tiên §7.6) — cả hai đều skip, không mất data.
10. `[ ]` **F10** Flake mới quan sát (chỉ khi chạy full suite, pass isolated): `UpdateCoordinatorTests.StartUpdateAsync_WhenProxyInFlightPositive_WaitsBeforeSnapshot` — timing dưới song song, không liên quan dataset; gộp với D5 nếu làm chung đợt ổn định test.
