# Spec: Provider Identifier (routing + hiển thị) & Account Quick Toggle

**Ngày:** 2026-09-30
**Trạng thái:** Approved (design), chờ review spec
**Approach đã chọn:** A — prefix `identifier/model`, fallback về match toàn bộ

## 1. Bối cảnh & vấn đề

- `ComboResolver.ResolveAsync(model)` match `ModelId == model` trên **toàn bộ**
  provider — model trùng tên (rất phổ biến: `gpt-4o`, `llama-3.3-70b`) không
  chọn được provider cụ thể.
- `Provider` chỉ có `Name` (tên hiển thị, không unique, chứa space/Unicode) —
  không có mã machine-readable.
- Dropdown Combos (`Combos.razor:122-125`) group theo `provider.Name` nhưng
  client không thể pin provider khi gửi request.
- **Bổ sung (2b):** account chỉ bật/tắt được qua form edit — bảng chỉ có badge,
  thiếu toggle nhanh (provider/model đã có).

## 2. Quyết định thiết kế

| Quyết định | Giá trị |
|---|---|
| Syntax pin | `identifier/model` (segment đầu = identifier) |
| Định dạng identifier | slug: `^[a-z0-9]+(-[a-z0-9]+)*$`, ≤ 50 ký tự |
| Unique | Bắt buộc, unique toàn cục |
| Bắt buộc nhập | Có (UI + validator); DB nullable tạm thời (xem §3) |
| Fallback | Không prefix / prefix lạ → match toàn bộ model id như hiện tại |
| Ưu tiên parse | Prefix match identifier → pin; nếu không match → coi cả chuỗi là model id |

## 3. Data model & migration

- `Provider.Identifier`: `string?`, `MaxLength(50)`, **unique index**.
  - Nullable chỉ để thứ tự migration an toàn (SQLite unique cho phép nhiều
    `NULL`); sau `DbInitializer.Initialize()` mọi hàng đều có giá trị.
- Migration `AddProviderIdentifier`: `AddColumn<string>` nullable + `CreateIndex` unique.
- **Backfill bằng C#** trong `DbInitializer.Initialize(IDbContextFactory)`
  (sau `db.Database.Migrate()`), idempotent — bỏ qua hàng `Identifier` đã khác rỗng:
  1. Với mỗi provider (tăng dần `Id`): slugify `Name` —
     Unicode normalize (FormD, bỏ NonSpacingMark) → lower → ký tự ngoài
     `[a-z0-9]` thành `-` → collapse `-` → trim `-` ở hai đầu.
  2. Slug rỗng → `provider-{Id}`.
  3. Đã tồn tại (so sánh ordinal) → append `-2`, `-3`, … cho đến khi không trùng.
  4. Ghi `Identifier`, `UpdatedAt` giữ nguyên (backfill không phải user edit).

## 4. Validation

### 4.1 Format/required — `ProviderValidator.Validate` (sync, field-level)

- `Identifier` rỗng → lỗi `providers.error.identifierRequired`.
- Không khớp regex → `providers.error.identifierFormat`.
- Trả về dict key i18n như các rule hiện có.

### 4.2 Unique + chống segment-trùng — `ProviderService` (cần DB)

- `CreateAsync`/`UpdateAsync` kiểm tra trước khi lưu, sai → ném
  `ProviderValidationException` (mới) mang theo `IReadOnlyDictionary<string, string> Errors`
  (cùng format với `ProviderValidator`).
  - **Unique:** hàng khác `Id` (khi update) có `Identifier` cùng giá trị
    (ordinal) → `providers.error.identifierDuplicate`.
  - **Chống segment-trùng:** tồn tại model có `ModelId` bắt đầu bằng
    `{identifier}/` (so sánh ordinal trong C#) →
    `providers.error.identifierSegmentCollision`.
    *Mục đích:* bảo vệ client đang gửi model id kiểu OpenRouter
    (`openai/gpt-4o`) khỏi bị pin nhầm khi user tạo identifier `openai`.
- UI (`Providers.razor`): bắt `ProviderValidationException`, merge `Errors`
  vào `_errors` → hiển thị field-level như lỗi validator thường.
- Unique index = phòng thủ cuối (race), không cần bắt đẹp ở tầng UI.

**Biên đã biết (chấp nhận, document):** model được fetch **sau** khi identifier
đã tồn tại mà có id dạng `{identifier}/...` → parse ưu tiên pin. Không validate
ở chiều thêm model (out of scope).

## 5. Routing — `ComboResolver.ResolveAsync`

```
model = "X" | "prefix/rest"
1. model chứa '/'?
   a. segment đầu (trước '/' lần đầu) match Identifier của BẤT KỲ provider nào?
      → query: m.Provider.Identifier == prefix && m.ModelId == rest
        (thêm filter Enabled provider/model + Include Accounts — tái dùng QueryCandidatesAsync)
   b. không match → query toàn bộ: m.ModelId == model (hành vi hiện tại)
2. model không chứa '/' → query toàn bộ (hành vi hiện tại)
3. Candidates rỗng → fallback tìm Combo.Name == model (chuỗi đầy đủ) — giữ nguyên
4. Không có gì → NotFound (giữ nguyên semantics)
```

- Tắt/bật provider/model/account: filter `Enabled` đã có sẵn trong
  `QueryCandidatesAsync` — pin tới provider bị tắt → rỗng → fallback → NotFound.
- Combo items: không đổi (target theo `TargetModelId` PK).
- RoundRobin dedup `(ProviderId, ModelId)` — không đổi.

## 6. UI

### 6.1 Providers page

- Form thêm/sửa: input **Identifier** cạnh `Name`, `maxlength="50"`,
  placeholder `vd: openai-prod`, bind `_draft.Identifier`, required.
- `ProviderDraft`: thêm `Identifier` (`string.Empty` default).
- `ProviderService.CreateAsync/UpdateAsync`: gán `draft.Identifier.Trim()`.
- Bảng provider: thêm cột **Identifier** (font-mono, text-xs).

### 6.2 Combos page

- Optgroup: `label="{Name} ({Identifier})"` — chỉ thêm phần `(identifier)`
  khi `Identifier` khác rỗng.

### 6.3 Account quick toggle (2b)

- `IProviderAccountService` + `ProviderAccountService`: thêm
  `SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)` —
  flip `Enabled` + `UpdatedAt`, pattern giống `ProviderService.SetEnabledAsync`.
- Bảng account, cột Enabled (`Providers.razor:399-402`): **thay Badge bằng checkbox**
  ```razor
  <input type="checkbox" checked="@account.Enabled"
         disabled="@(_busy || _accountForm is not null)"
         @onchange="() => ToggleAccountEnabledAsync(account)" />
  ```
- Không guard "account cuối" — cho phép tắt hết (consistent với provider/model).

## 7. i18n — `Translations.cs` (cả `English` + `Vietnamese`)

| Key | Nội dung (EN/VI ý nghĩa) |
|---|---|
| `providers.col.identifier` | Identifier / Mã định danh (cột bảng) |
| `providers.field.identifier` | Identifier / Mã định danh |
| `providers.hint.identifier` | Hint: used in `identifier/model` routing |
| `providers.error.identifierRequired` | Identifier is required |
| `providers.error.identifierFormat` | Lowercase letters, digits, dashes only |
| `providers.error.identifierDuplicate` | Identifier already exists |
| `providers.error.identifierSegmentCollision` | Conflicts with existing model id prefix |

## 8. Testing (xUnit)

1. **`ProviderValidatorTests`**: required / format / max-length → key i18n đúng.
2. **`ProviderServiceTests`**:
   - create identifier trùng → `ProviderValidationException` + key duplicate.
   - create identifier collide segment (`openai` khi có model `openai/gpt-4o`) → key segmentCollision.
   - update đổi identifier hợp lệ → lưu; giữ nguyên khi không đổi.
3. **`ComboResolverTests`**:
   - `prov/gpt-4o` → chỉ candidate của provider `prov`.
   - prefix không tồn tại (`foo/gpt-4o`) → match toàn bộ (fallback).
   - prefix đúng nhưng provider không có model đó → rỗng → combo fallback / NotFound.
   - `gpt-4o` không prefix → không đổi hành vi (regression).
4. **Backfill** (pattern `AddProviderAccountsMigrationTests`): slugify có dấu,
   dedupe `-2`, rỗng → `provider-{Id}`, idempotent chạy 2 lần.
5. **`ProviderAccountServiceTests`**: `SetEnabledAsync` flip + persist;
   account tắt bị `ProviderKeyResolver` loại (nếu chưa có).

## 9. Ngoài phạm vi

- Hiện identifier ở Dashboard/Log (sau làm riêng nếu cần).
- Validate model-id collision ở chiều `ModelService.FetchFromProviderAsync`.
- Sửa identifier khi client đã cache prefix cũ (user tự quản lý).
- Tách identifier thành bảng riêng (denormalize chấp nhận — 1 cột).

## 10. Commit plan (nhỏ, một việc một commit — mỗi commit phải build/test xanh)

1. `docs: add provider identifier design spec`
2. `feat: add provider identifier column with slug backfill` — entity, migration, backfill, test
3. `feat: pin provider by identifier prefix in combo resolver` — resolver + test
4. `feat: add identifier validation, form field and display` — draft/validator/exception/service + UI + i18n + test
5. `feat: add quick enabled toggle for provider accounts` — service + UI + test

*Validation (commit 4) phải đi cùng UI form field — tách ra sớm hơn sẽ khiến form
không nhập được Identifier, app không lưu được provider.*
