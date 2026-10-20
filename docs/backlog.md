# Backlog — cập nhật 2026-10-19

Nguồn: phiên brainstorm sau khi ship PR #30 (trang Tools + free sync catalog).
Ký hiệu: `[ ]` chưa làm · **OPEN** = chưa chốt quyết định · *đo được* = có số liệu/chứng minh trong code, không phải cảm giác.

Trạng thái nền: `feat/opencode-tools-page` = `2326d98`, PR #30 mở, `master` local đã merge fast-forward tới cùng commit.

---

## A. Trang Tools (OpenCode config) — vòng kế tiếp

**Đã xong 2026-10-21** trên nhánh `feat/tools-round-2` (Plan 2, spec capability-model §9; PR kế tiếp). Các mục dưới giữ lại làm hồ sơ.

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

1. `[ ]` **Chain fill metadata không có audio/video/tools** *đo được*: `ModelMetadata` (src/RouterBalancing.Core/Providers/ModelMetadata.cs) chỉ có `ContextWindow/SupportsVision/SupportsThink/ThinkEfforts/InputModalities/OutputModalities`. Số liệu DB thật (137 model): `SupportsAudio=true` **1**, `SupportsVideo=true` **1** (chỉ do tick tay), `SupportsTools IS NULL` **127**, `ContextWindow IS NULL` **102**, có `InputModalities` **10**.
2. `[ ]` **Đường sync preset free (OpenRouter/NVIDIA/Ollama) không gọi `TryFillAsync`** — `FreeModelSyncService` merge chỉ ghi DisplayName/ContextWindow; trong khi `ProviderEndpointMetadataProvider` đã biết đọc `architecture.input_modalities`/`supported_modalities` (OpenRouter trả đủ).
3. `[ ]` **Không biểu diễn được "biết là không có"**: `ModelMetadataService` chỉ ghi khi field đang trống và bool chỉ ghi `true` → không sửa được dữ liệu cũ, không phân biệt catalog/endpoint/user.
4. `[ ]` **`Providers.Name` không có unique index** nhưng code tra preset theo tên (`FreeModelSyncService` → `FreeProviderCatalog.FindByDisplayName`) và seed theo tên (`DbInitializer`) → hai provider trùng tên cho hành vi khó đoán.
5. `[ ]` **`ProviderAccounts.ApiKeyEncrypted` NOT NULL** trong khi tồn tại luồng "tài khoản không key" → đang dùng chuỗi rỗng làm sentinel; nên là NULL.
6. `[ ]` **`LogEntries` (28.629 dòng, chiếm phần lớn 6.6MB DB) không có retention**, và một bảng gánh hai vai (log + số liệu thống kê `Outcome/HttpStatus/DurationMs/Tokens`). Đồng thời chứa cả `ModelId/ProviderId` lẫn snapshot chuỗi `ModelKey/ProviderKey` → hai bản ghi của cùng một sự thật; phải chốt snapshot chuỗi là nguồn thống kê, id chỉ là liên kết best-effort.

Ghi chú vật lý: DB **không** mảnh vụn (`integrity_check ok`, `freelist_count 0`, `journal_mode=wal`) — nên mọi mục ở đây là chuyện thiết kế, không phải vacuum.

---

## C. Chuẩn hoá schema theo mô hình quan hệ (hướng đã user chốt 2026-10-19)

1. `[ ]` **Capabilities của `Models`** — ưu tiên 1, vì chặn trực tiếp A3, A8, B1–B3:
   - `ModelCapability(ModelId, Capability, KnownAbsent)`: **không có dòng = chưa biết**, có dòng = đã biết (true/false). Một quy tắc cho mọi capability, thay cho `SupportsTools` nullable + 4 bool NOT NULL semantics "false = không hoặc chưa rõ".
   - `ModelModality(ModelId, Direction, Modality)` (Direction ∈ input/output) thay JSON `InputModalities/OutputModalities`; `ModelReasoningEffort(ModelId, Level)` thay JSON `ThinkEfforts`.
   - **Bỏ các cột bool dẫn xuất** (`SupportsVision/Audio/Video/Think`) — suy ra từ bảng trị tại đúng một chỗ (DTO/repository), hết cảnh "một sự thật chứa hai nơi".
   - Thêm `Source` (catalog/endpoint/user) + `UpdatedAt` cho mỗi fact để sync không đè ý người dùng và để sửa được giá trị cũ.
   - `ContextWindow`/`MaxOutputTokens` giữ là cột (scalar nguyên tử, có dùng để hiển thị/sắp xếp).
   - **OPEN**: một migration duy nhất (dời dữ liệu cũ sang bảng trị + bỏ cột) hay bắc cầu (thêm bảng mới, code đọc bảng mới, giữ cột cũ một release rồi bỏ)?
2. `[ ]` **Bỏ liên kết đa hình ở `ComboItems`**: tách `ComboModelItem(ComboId, ModelId, Position)` và `ComboComboItem(ComboId, MemberComboId, Position)`; nếu tạm giữ một bảng thì thêm CHECK `(TargetModelId IS NULL) <> (TargetComboId IS NULL)`; index `(ComboId, Position)` phải **unique** (hiện non-unique); thêm chặn chu trình cho combo lồng combo.
3. `[ ]` **`UsageDaily(SubjectKind, SubjectId, Date, Requests, Tokens)`** thay bộ đếm nằm trong bảng cấu hình: `ClientKeys.RequestsUsed/TokensUsed/UsageDate`, `ProviderAccounts` y hệt (mất lịch sử khi sang ngày, reset bằng cách so `UsageDate`).
4. `[ ]` **`ProbeResult(OwnerKind, OwnerId, Success, At, Message, Detail)`** thống nhất bộ ba test-kết-nối đang lặp ở 3 bảng (`Providers`, `ProviderAccounts`, `OutboundProxies`) với kiểu không nhất quán (`LastTestSuccess` có nơi nullable, có nơi NOT NULL). Phương án nhẹ: chỉ đồng nhất kiểu + semantics, không tách bảng.
5. `[ ]` **`ProviderHeader(ProviderId, Name, Value)`** thay JSON `Providers.ExtraHeaders` (unique (ProviderId,Name)). `AccountModelPattern(AccountId, Pattern)` thay `ModelPatterns` — độ lợi thấp, làm sau.
6. `[x]` **Không chuẩn hoá `AppSettings(Key, ValueJson)`** — key/value là mô hình đúng cho config app; mục A1 sẽ nằm đây.
7. `[ ]` Chặn input bằng CHECK (port, giới hạn token, độ dài tên) — hiện toàn schema **không có CHECK nào**, mọi ràng buộc数值 đều nằm trong code.

Thứ tự gợi ý: C1 → (A4,A5,A6,A8 là UI rẻ, không phụ thuộc schema) → A3 → C2 + B4/B5 (rẻ, thêm ràng buộc) → A7 → C3 → C4/C5, B6.

---

## D. Deferred minors từ review PR #30 (ledger SDD bị git-ignore, nên ghi lại đây)

`[ ]` D1 `FreeModelDetector.LimitOf` dùng `GetInt32()` → nên `TryGetInt32` (skip entry hỏng, không sập cả lần sync). `[ ]` D2 `ThinkEffortsOf` match `reasoning_options.type` bằng `OrdinalIgnoreCase` (spec literal lowercase → `Ordinal` chặt hơn). `[ ]` D3 thiếu test nhánh `["toggle", ...values]` và modalities thiếu hẳn. `[ ]` D4 doc enum `FreeSuffix` còn nhắc "OpenCode Zen". `[ ]` D5 làm ổn định `ProxyQueueIntegrationTests` (flake timeout dưới song song, tiền tồn tại). `[ ]` D6 test guard "0 free models" không phân biệt được với parse failure (assert message). `[ ]` D7 test catalog không assert `Method == GET` / không có `Authorization`. `[ ]` D8 nhánh update capabilities trên model non-manual (catalog) chưa test trực tiếp. `[x]` D10 thiếu test `SupportsTools=false` → emit `"tools": false` (đã có `Generate_NegativeCapabilityFromKnownAbsent_EmitsFalse`, Task 4). `[ ]` D11 service: thiếu test "preset free tắt + model Enabled → DefaultSelectedIds rỗng". `[ ]` D12 test dedupe không phân biệt "free thắng" với "Id nhỏ thắng"; thiếu test Ordinal case-sensitive. `[ ]` D13 `SkippedDuplicateModelIds` lặp entry khi ≥3 model cùng ModelId. `[x]` D14 `Tools.razor`: `RegenerateAsync` catch không xóa `_json`/`_skipped` → preview cũ không khớp tick hiện tại (đã fix trong Plan 2 — catch xoá preview trước khi toast). `[ ]` D15 `Tools.razor`: `DownloadAsync` không bắt `JSException` khi `rb-tools.js` chưa load. `[ ]` D16 `Tools.razor`: `StateHasChanged` sau `await` khi component đã dispose. `[ ]` D17 `Tools.razor`: `CreateKeyAsync` không busy-guard → double-click tạo 2 client key thật. `[ ]` D18 parser: root JSON không phải object → `InvalidOperationException` thay vì `FreeModelSyncException` (latent, giống `Parse` cũ — sửa thì làm đối xứng cả hai). `[ ]` D19 các call site `ResolveFirstEnabledKey` khác (`ProviderEndpointMetadataProvider`, `ModelService`) vẫn resolve vô điều kiện → cùng lỗi DPAPI khi DB sang máy khác. `[ ]` D20 doc `FreeSuffix`/`ProviderId` đã sửa một phần — rà còn mô tả cũ nào nữa không. `[ ]` D21 (Plan 2 review) tên combo chứa `/` sẽ trùng namespace `<Identifier>/<ModelId>` khi Engine resolve (`ComboResolver` ưu tiên provider-scoped trước khi fallback combo-name) — cân nhắc validate tên combo hoặc ghi ADR hai-shape convention.
