# Spec: Đa tài khoản Provider (Provider Accounts)

- **Status:** Approved
- **Date:** 2026-09-27
- **Phase:** 2 (còn lại của Phase 2 — làm trước Phase 3 Engine)
- **Approach:** Phương án A — entity `ProviderAccount` mới, 1-n `Provider`

## 1. Mục tiêu

Mỗi Provider sở hữu nhiều tài khoản (API key) để:

1. **Chia tải/quota:** nhiều key cùng match 1 model → phân bổ theo `Weight`.
2. **Failover:** key hỏng/rate-limit → chuyển key có `Priority` kế tiếp.
3. **Gắn model:** mỗi key kèm `ModelPatterns` (optional) — chỉ phục vụ các model khớp pattern.

**Non-goals (Phase 3 — Engine):** mọi logic chọn key (weighted random theo Priority/Weight, quota enforcement, đếm `TokensUsed/RequestsUsed`, reset `UsageDate`, circuit-break theo key). Phase này chỉ dựng **data + service + UI** để Phase 3 đọc.

## 2. Data model

### 2.1 Entity `ProviderAccount`

```csharp
namespace RouterBalancing.Core.Domain;

/// <summary>Tài khoản (API key) thuộc một nhà cung cấp.</summary>
public class ProviderAccount
{
    public long Id { get; set; }
    public long ProviderId { get; set; }
    public Provider Provider { get; set; } = null!;

    /// <summary>Tên hiển thị — unique trong cùng provider.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>API key mã hoá DPAPI — không bao giờ lưu/plaintext/log.</summary>
    public string ApiKeyEncrypted { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>JSON array of string pattern; <see langword="null"/>/rỗng = match mọi model.</summary>
    public string? ModelPatterns { get; set; }

    /// <summary>Trọng số chia tải khi nhiều key cùng match (Phase 3). 0–10000, mặc định 100.</summary>
    public int Weight { get; set; } = 100;

    /// <summary>Thứ tự failover — nhỏ hơn = dùng trước; −1000–1000, mặc định 0.</summary>
    public int Priority { get; set; }

    public int? DailyTokenLimit { get; set; }
    public long TokensUsed { get; set; }
    public int? DailyRequestLimit { get; set; }
    public long RequestsUsed { get; set; }

    /// <summary>Ngày UTC của 2 counter trên — reset khi sang ngày (Phase 3 ghi).</summary>
    public DateOnly? UsageDate { get; set; }

    /// <summary>Kết quả test gần nhất; null = chưa test.</summary>
    public bool? LastTestSuccess { get; set; }
    public DateTimeOffset? LastTestAt { get; set; }
    public string? LastTestMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

### 2.2 DbContext

- Nav `Provider.Accounts` — `HasMany().WithOne().HasForeignKey().OnDelete(DeleteBehavior.Cascade)`.
- Unique index `(ProviderId, Name)` — `IX_ProviderAccounts_ProviderId_Name`. Không có index `ProviderId` riêng: FK SQLite không tự index, unique composite phục vụ lookup theo ProviderId qua prefix.
- **`Provider.ApiKeyEncrypted` bị xoá** (column drop) — key sống hoàn toàn ở account.
- `Provider.MaxConcurrent`, `LastTestSuccess/At/Message` (level provider) **giữ nguyên**.

### 2.3 Entity `Provider` (thay đổi)

- Bỏ `ApiKeyEncrypted`.
- Thêm `List<ProviderAccount> Accounts`.

## 3. Migration

1 file migration `AddProviderAccounts`:

1. **`Up()`**:
   1. `CreateTable ProviderAccounts` (toàn bộ column, FK cascade → Providers, 2 index).
   2. **Data migration** (SQL thô, mỗi provider còn key):
      ```sql
      INSERT INTO ProviderAccounts
        (ProviderId, Name, ApiKeyEncrypted, Enabled, Weight, Priority, ModelPatterns,
         DailyTokenLimit, TokensUsed, DailyRequestLimit, RequestsUsed, UsageDate,
         LastTestSuccess, LastTestAt, LastTestMessage, CreatedAt, UpdatedAt)
      SELECT Id, 'Default', ApiKeyEncrypted, 1, 100, 0, NULL,
             NULL, 0, NULL, 0, NULL,
             NULL, NULL, NULL, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now')
      FROM Provider WHERE ApiKeyEncrypted <> '';
      ```
      Tên `Default` là **data** (không qua i18n); counter/test fields bắt đầu ở giá trị rỗng/chưa test.
   3. `DropColumn Provider.ApiKeyEncrypted`.
2. **`Down()`**: tạo lại column `ApiKeyEncrypted` (EF sqlite table-rebuild), migrate ngược: mỗi provider lấy `ApiKeyEncrypted` của account `Default` đầu tiên (còn nếu provider chưa có account → để rỗng).

### 3.1 Ripple — consumer đang đọc `Provider.ApiKeyEncrypted`

3 chỗ (Phase 2A/1) cần key để gọi metadata/test:

| Nơi | Thay đổi |
|---|---|
| `ModelService` (list models) | Dùng key của account **enabled đầu tiên** của provider |
| `ProviderEndpointMetadataProvider` | Tương tự |
| `ProviderRequestFactory` (gửi request thật) | Tương tự — Phase 3 sẽ thay bằng selection |

Để không trùng logic: thêm helper nội bộ `ProviderKeyResolver` (static hoặc service):

```csharp
/// <summary>Lấy key plaintext của account enabled đầu tiên; null nếu provider không có account khả dụng.</summary>
static string? ResolveFirstEnabledKey(Provider p, IDataProtector protector);
```

`ProviderService.TestAsync` (cũ, test level provider) **bị xoá** — thay bằng `TestAllAsync` (§4). UI không còn gọi test level provider; `Provider.LastTest*` do `TestAllAsync` ghi (AND các account enabled).

## 4. Contract — `IProviderAccountService`

```csharp
namespace RouterBalancing.Core.Providers;

/// <summary>CRUD + test connection cho các tài khoản (API key) của một provider.</summary>
public interface IProviderAccountService
{
    /// <summary>Liệt kê accounts theo provider, theo Priority rồi Name — không trả plaintext key.</summary>
    Task<IReadOnlyList<ProviderAccount>> ListAsync(long providerId, CancellationToken ct = default);

    /// <summary>Tạo account mới. draft.ApiKey bắt buộc.</summary>
    /// <exception cref="KeyNotFoundException">Provider không tồn tại.</exception>
    /// <exception cref="InvalidOperationException">Trùng Name trong cùng provider.</exception>
    /// <exception cref="ArgumentException">Draft không hợp lệ (xem §4.1).</exception>
    Task<ProviderAccount> CreateAsync(ProviderAccountDraft draft, CancellationToken ct = default);

    /// <summary>Cập nhật. draft.ApiKey rỗng = giữ nguyên key cũ.</summary>
    /// <exception cref="KeyNotFoundException">Account không tồn tại.</exception>
    /// <exception cref="InvalidOperationException">Trùng Name trong cùng provider (trừ chính nó).</exception>
    /// <exception cref="ArgumentException">Draft không hợp lệ.</exception>
    Task<ProviderAccount> UpdateAsync(long id, ProviderAccountDraft draft, CancellationToken ct = default);

    /// <summary>Xoá account. Cấm xoá account cuối cùng của provider.</summary>
    /// <exception cref="KeyNotFoundException">Account không tồn tại.</exception>
    /// <exception cref="InvalidOperationException">Account cuối cùng.</exception>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Test mọi account enabled của provider — kết quả từng dòng, ghi LastTest* mỗi account
    /// và cập nhật Provider.LastTest* = AND các account enabled (null nếu không có account enabled).</summary>
    Task<IReadOnlyList<ProviderAccountTestResult>> TestAllAsync(long providerId, CancellationToken ct = default);
}
```

### 4.1 `ProviderAccountDraft` + validation

```csharp
/// <summary>Record mutable các field form account — ApiKey chỉ tồn tại lúc edit.</summary>
public sealed class ProviderAccountDraft
{
    public long ProviderId { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>Plaintext lúc nhập; rỗng khi update = giữ key đã lưu.</summary>
    public string ApiKey { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    /// <summary>Pattern do UI gửi; service serialize JSON vào ModelPatterns.</summary>
    public string[] ModelPatterns { get; set; } = [];
    public int Weight { get; set; } = 100;
    public int Priority { get; set; }
    public int? DailyTokenLimit { get; set; }
    public int? DailyRequestLimit { get; set; }
}
```

`ProviderAccountValidator` (pattern `ProviderValidator`), throw `ArgumentException`:

| Rule | |
|---|---|
| `Name` | bắt buộc, ≤ 100 ký tự, trim |
| `ApiKey` | bắt buộc khi **Create**; bỏ qua khi Update rỗng |
| `Weight` | 0–10000 |
| `Priority` | −1000–1000 |
| `DailyTokenLimit` / `DailyRequestLimit` | null hoặc > 0 |
| `ModelPatterns` | ≤ 50 phần tử; mỗi phần tử không rỗng sau trim |

- Key mã hoá qua `IDataProtector` (DPAPI) — **tái dùng helper Protect/Unprotect đang có** trong `ProviderService`; service **không bao giờ** trả plaintext.
- UI clamp bằng input `min`/`max` chặn trước; server validator là backstop → `ArgumentException` rơi catch chung → toast `dashboard.msg.failed` (không thêm key lỗi cho range — như pattern Combos).

### 4.2 Thay đổi `IProviderService`

- `CreateAsync(ProviderDraft)`: sau khi tạo provider, **nếu `draft.ApiKey` không rỗng** → tạo kèm account `Name="Default"`, key đó, `Enabled=true, Weight=100, Priority=0`. (Behavior tạo provider không key giữ nguyên như hiện tại — không có account nào.)
- `UpdateAsync`: **bỏ hẳn nhánh cập nhật `draft.ApiKey`** (key quản lý ở account). `ProviderDraft.ApiKey` chỉ còn nghĩa khi Create — XML doc ghi rõ.
- `TestAsync` bị **xoá** (thay bằng `ProviderAccountService.TestAllAsync`).

### 4.3 `ProviderAccountTestResult`

```csharp
/// <summary>Kết quả test của một account trong lần TestAll.</summary>
public sealed record ProviderAccountTestResult(long AccountId, string AccountName, bool Success, string? Message);
```

## 5. UI — `Providers.razor`

### 5.1 Form Create provider (modal mới)

- **Giữ nguyên ô API key hiện tại** (`providers.field.apiKey`) — giá trị = key của account `Default` tạo kèm (§4.2). Không đổi UX tạo mới.

### 5.2 Form Edit provider (modal sửa)

- **Bỏ ô API key.**
- Thêm section **"Tài khoản / API keys"** (`accounts.section.title`) ngay sau các field provider:

**Bảng dòng** (mỗi account 1 dòng): `Name` · badge `Enabled/Disabled` (tái dùng pattern badge hiện có) · pattern summary (`accounts.patterns.all` khi rỗng, `accounts.patterns.count` format `{0}` = số pattern khi có) · `W{weight} P{priority}` · usage (`accounts.usage.format` / `accounts.usage.noLimit` — số format `N0`) · kết quả test (✓/✗ + `LastTestMessage` là **data hiển thị thẳng**; chưa test → `accounts.test.none`) · thao tác `✏️` `🗑`.

- Header section: `+ Thêm tài khoản` (`accounts.action.add`), nút `Test tất cả` (`accounts.action.testAll`).
- **Empty state** (provider tạo không key): `accounts.empty` + nút add.

**Sub-form inline add/edit** (ẩn/hiện trong section, pattern model rows):

| Field | Control | Ghi chú |
|---|---|---|
| Tên | text, required | |
| API key | `type="password"` | Create: placeholder `accounts.placeholder.key`; Edit: placeholder `accounts.placeholder.keySaved`, value rỗng = giữ |
| Bật | checkbox | default true |
| Gắn model | textarea, 1 pattern/dòng | placeholder `accounts.placeholder.patterns`; trống = mọi model |
| Trọng số | number `min=0 max=10000` | default 100 |
| Ưu tiên | number `min=-1000 max=1000` | nhỏ = dùng trước |
| Giới hạn token/ngày | number `min=1`, optional | |
| Giới hạn request/ngày | number `min=1`, optional | |

- Nút trong sub-form: `Lưu` (tái dùng `settings.action.save`) + `Hủy` (tái dùng `confirm.cancel`).
- **`CanSave`**: `Name` không rỗng (trim) — và Create cần `ApiKey` không rỗng.
- **`CanDelete` (🗑)**: số account > 1 → nếu = 1, nút xoá disabled (pre-check; service `InvalidOperationException` là backstop → toast `accounts.error.lastAccount`).
- **Xoá**: ConfirmDialog — Title và ConfirmText tái dùng `providers.action.delete`; Message = `accounts.confirm.delete` format `{0}` = Name; CancelText `confirm.cancel`; toast `accounts.msg.deleted` format `{0}`.

### 5.3 Test connection

- Nút `Test tất cả` → `TestAllAsync` → toast `accounts.msg.testDone` format `{0}` = số OK, `{1}` = số lỗi; badge từng dòng cập nhật lại (không reload trang — service đã ghi DB, UI re-fetch `ListAsync`).
- Test với key của từng account; request test: **gọi models endpoint của provider** (reuse đúng endpoint/timeout với test cũ), BaseUrl/Type kế thừa từ provider.
- `Provider.LastTest*` là **dual-writer**: `ProviderAccountService.TestAllAsync` ghi AND các account enabled (Test tất cả / test từ card), `ProviderService.TestConnectionAsync` ghi theo kết quả 1 key (nút Test trong modal, provider đã lưu — spec phase2a §4.2). Hai ngữ nghĩa chưa chuẩn hoá — thống nhất ở Phase 3 Engine.

### 5.4 Submit form provider

- Form edit **không gửi API key** — `UpdateAsync` chỉ chứa field provider (Name/Type/BaseUrl/MaxConcurrent). Accounts độc lập (lưu ngay khi bấm Lưu ở sub-form, không part của provider draft).

### 5.5 Error mapping (toast)

| Exception / điều kiện | Key |
|---|---|
| `InvalidOperationException` trùng tên | `accounts.error.duplicateName` |
| `InvalidOperationException` account cuối | `accounts.error.lastAccount` |
| `KeyNotFoundException` | `dashboard.msg.failed` (tái dùng) |
| `ArgumentException` (range…) | `dashboard.msg.failed` (tái dùng) |
| Save account OK | `accounts.msg.saved` format `{0}` = Name |
| Delete OK | `accounts.msg.deleted` format `{0}` = Name |

Tất cả text hiển thị qua `L["key"]`.

## 6. i18n — 27 keys mới, bỏ 1 key chết (`combos.*` giữ nguyên)

- `providers.key.saved` bị **xoá** (chỉ dùng ở ô key mode edit — form edit bỏ ô key theo §5.2).
- Tổng: 183 − 1 + 27 = **209**.

Chèn sau nhóm `combos.*` trong `Translations.cs` (EN + VI), guard parity:

| Key | VI | EN |
|---|---|---|
| `accounts.section.title` | Tài khoản / API keys | Accounts / API keys |
| `accounts.empty` | Chưa có tài khoản nào. | No accounts yet. |
| `accounts.action.add` | + Thêm tài khoản | + Add account |
| `accounts.action.testAll` | Test tất cả | Test all |
| `accounts.field.name` | Tên tài khoản | Account name |
| `accounts.field.apiKey` | API key | API key |
| `accounts.field.enabled` | Bật | Enabled |
| `accounts.badge.disabled` | Đã tắt | Disabled |
| `accounts.field.patterns` | Gắn model | Model patterns |
| `accounts.field.weight` | Trọng số | Weight |
| `accounts.field.priority` | Ưu tiên | Priority |
| `accounts.field.tokenLimit` | Giới hạn token/ngày | Daily token limit |
| `accounts.field.requestLimit` | Giới hạn request/ngày | Daily request limit |
| `accounts.placeholder.key` | Nhập API key | Enter API key |
| `accounts.placeholder.keySaved` | Đã lưu — để trống để giữ nguyên | Saved — leave blank to keep |
| `accounts.placeholder.patterns` | Mỗi dòng một pattern; để trống = mọi model | One pattern per line; blank = all models |
| `accounts.patterns.all` | Tất cả model | All models |
| `accounts.patterns.count` | {0} model | {0} models |
| `accounts.usage.format` | Token {0}/{1} · Request {2}/{3} | Tokens {0}/{1} · Requests {2}/{3} |
| `accounts.usage.noLimit` | Token {0} · Request {1} | Tokens {0} · Requests {1} |
| `accounts.test.none` | Chưa test | Not tested |
| `accounts.msg.saved` | Đã lưu tài khoản "{0}". | Saved account "{0}". |
| `accounts.msg.deleted` | Đã xóa tài khoản "{0}". | Deleted account "{0}". |
| `accounts.msg.testDone` | Test xong: {0} OK, {1} lỗi | Test done: {0} ok, {1} failed |
| `accounts.error.duplicateName` | Tên tài khoản đã tồn tại trong nhà cung cấp này. | Account name already exists for this provider. |
| `accounts.error.lastAccount` | Phải giữ lại ít nhất một tài khoản. | At least one account must remain. |
| `accounts.confirm.delete` | Xóa tài khoản "{0}"? | Delete account "{0}"? |

Không đổi key `providers.*` (trừ bỏ dùng `apiKey` ở edit — key giữ nguyên cho form create).

## 7. Testing & Gates

### Unit tests — `router balancing test/Providers/ProviderAccountServiceTests.cs`

Mirror `ProviderServiceTests` (`TestDb` + `DbInitializer` + `NeverHttpFactory`), ~19 test:

1. `CreateAsync_WithKey_SavesDpapiEncrypted` — không plaintext trong DB.
2. `CreateAsync_DuplicateNameInSameProvider_Throws`.
3. `CreateAsync_SameNameDifferentProviders_Ok`.
4. `CreateAsync_WhenProviderMissing_ThrowsKeyNotFound`.
5. `CreateAsync_InvalidWeightOrPriority_ThrowsArgument` (Theory các case range).
6. `CreateAsync_WithPatterns_SerializesJsonArray`.
7. `UpdateAsync_EmptyApiKey_KeepsExistingKey`.
8. `UpdateAsync_NewKey_ReplacesEncrypted`.
9. `UpdateAsync_DuplicateNameExcludingSelf_Ok`.
10. `DeleteAsync_LastAccount_ThrowsInvalidOperation`.
11. `DeleteAsync_NotLast_Succeeds`.
12. `ListAsync_OrdersByPriorityThenName`.
13. `DeleteAsync_CascadesToAccounts` (xoá provider → accounts theo).
14. `TestAllAsync_MixedResults_WritesEachAndProviderAnd` — mock HTTP: 1 key OK, 1 key 401 → từng dòng đúng; `Provider.LastTestSuccess == false`.
15. `TestAllAsync_NoEnabledAccounts_SetsProviderTestNull`.
16. `CreateProviderAsync_WithApiKey_CreatesDefaultAccount` (§4.2).
17. `UpdateProviderAsync_IgnoresApiKey`.
18. `ResolveFirstEnabledKey_...` (helper).

**Dự kiến tổng: 147 → ~178.**

### CDP self-test (phase C, script `%TEMP%\opencode\cdp-test\drive40.js`, pattern drive32–34)

S1 Edit modal → section title + bảng (hoặc empty state) visible · S2 Add account: fill đủ field → toast lưu → dòng mới · S3 Duplicate name → toast `accounts.error.duplicateName` + modal mở · S4 Edit để trống key → giữ key (không lỗi) · S5 Delete account (≥2) → confirm → toast; account cuối → nút 🗑 disabled · S6 Test tất cả → toast summary + badge ✓/✗ · S7 EN/VI switch → section/sub-form/placeholder EN · S8 Provider create với key → account `Default` xuất hiện.

### Gates

```
dotnet test  →  ~178/0
dotnet build (net10.0-windows…) → 0 warning / 0 error
npm run build → exit 0
i18n parity EN = VI = 209
git working tree clean
CDP S1–S8 PASS
```

## 8. Phase 3 handoff (không code ở phase này)

Interface điểm, Phase 3 Engine đọc:

- Chọn key: filter `Enabled` → quota còn (`UsageDate` reset ngày UTC, `TokensUsed < DailyTokenLimit?`...) → `ModelPatterns` match → sort `Priority` → weighted random `Weight` trong cùng Priority.
- Ghi usage: `TokensUsed/RequestsUsed/UsageDate` sau mỗi request.
- Failover runtime: khi 401/rate-limit → chuyển key Priority kế (không cần sửa data — field đã đủ).
- `ProviderKeyResolver` chỉ là fallback tạm cho metadata call; Phase 3 thay bằng selection thật trong `ProviderRequestFactory`.
