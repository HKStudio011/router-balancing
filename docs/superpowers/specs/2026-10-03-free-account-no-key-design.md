# Spec: Free Account No-Key

**Ngày:** 2026-10-03 — **Trạng thái:** Approved (thảo luận với user, Q1–Q6 + 4 section design)
**Roadmap:** item #2 trong "5 việc cần làm"

## 1. Bối cảnh

Provider free (opencode `https://opencode.ai/zen/v1` và các endpoint public khác) **không yêu cầu API key**, nhưng app hiện bắt key ở mọi lớp:

| Chỗ | Hành vi hiện tại (chặ no-key) |
|---|---|
| `ProviderAccountValidator` | Create bắt buộc key (`requireApiKey`) |
| UI `CanSaveAccount` | Tạo mới phải gõ key; update bỏ trống = giữ key cũ → không cách xóa key |
| `ProviderKeyResolver` | Filter `ApiKeyEncrypted != rỗng` → account trống không bao giờ được chọn |
| `ChatCompletionsHandler.ForwardAsync` | Không resolve được account → **503 "No enabled API key"** |
| `ProviderRequestFactory` | Luôn gắn `Authorization: Bearer <key>` kể cả rỗng |
| `ProviderAccountService.TestAllAsync` | `Unprotect("")` → CryptographicException → test fail |
| `ModelHealthWatchdog` | `ResolveFirstEnabledKey → null` → `RecordProbeFailure` — provider no-key-only probe fail vĩnh viễn |
| `ProviderService.CreateAsync` | Key rỗng → tạo provider **không có account nào** → 503 âm thầm |

Mục tiêu: account **không cần API key** hoạt động bình thường ở mọi đường request (chat, probe, test, metadata, sync), UI cho phép khai báo tường minh, và gate kiểm tra kết nối trước khi lưu.

## 2. Quyết định (đã confirm với user)

| # | Quyết định |
|---|---|
| D1 | **No-key = `ApiKeyEncrypted == ""`** — không migration, không cột mới; lưu thẳng `""` (không `Protect` chuỗi rỗng). |
| D2 | **Resolver semantics:** `null` = không có account khả dụng; `""` = có account nhưng account đó không key. `ResolveFirstEnabledAccount` filter **chỉ `Enabled`** — no-key tham gia selection theo Priority/Id bình thường. `ResolveFirstEnabledKey` trả `""` cho account no-key (không còn null). |
| D3 | **Phạm vi mọi provider** — không hardcode opencode; "no-key" là tính chất của account (toggle trong form), không phải cờ ở provider. |
| D4 | **Tạo provider mới bắt buộc có account:** `NoKey = true` HOẶC key không rỗng — bỏ hẳn nhánh "tạo provider không account". Luồng create **gộp 1 lần lưu** + test-before-save đã có sẵn (`CanSave` signature), giữ nguyên. |
| D5 | **Edit account:** `NoKey=true` → xóa key (`= ""`); `NoKey=false` + ô trống = giữ key cũ (semantics hiện tại được pin bằng test). Toggle thể hiện tường minh, không đổi semantics "rỗng = giữ cũ". |
| D6 | **No-key bắt buộc test pass mới lưu:** create đã có gate; edit account thêm nút **Test** (chỉ hiện khi toggle bật) + signature gate (`Type\|BaseUrl\|NoKey`) — sửa draft sau test → phải test lại; guard chặn cứng trong `SaveAccountAsync` (mirror Combos/Providers). |
| D7 | **Header:** key `""` → **không gắn** `Authorization: Bearer` (OpenAI) / `x-api-key` (Anthropic); giữ `anthropic-version`. Không gửi header auth rỗng. |
| D8 | **Watchdog:** `key is null` → `RecordProbeFailure` (giữ); `key == ""` → **probe tiếp không key**. |
| D9 | **`TestConnectionAsync` override:** `null` = fallback account enabled đầu tiên (hành vi cũ); `""` = **force test không key** (mới — phục vụ test draft no-key). |
| D10 | **401 upstream** (no-key gọi provider bắt key) → Passthrough, không retry/failover (hành vi 3A giữ nguyên). Anthropic no-key: test 401 → gate tự chặn, không cần nhánh code riêng. |

## 3. Mô hình dữ liệu & contract

Không migration. Thêm flag trên draft (không phải entity):

```csharp
// ProviderDraft + ProviderAccountDraft
public bool NoKey { get; set; }   // true = không dùng API key (tạo mới: tạo account rỗng; update: xóa key)
```

```csharp
public static class ProviderKeyResolver
{
    // Filter Chỉ Enabled — account no-key được chọn theo Priority tăng, tie-break Id.
    public static ProviderAccount? ResolveFirstEnabledAccount(Provider provider);

    // null  = không có account khả dụng (ý nghĩa cũ, giữ nguyên)
    // ""    = có account, account đó không key (MỚI)
    public static string? ResolveFirstEnabledKey(Provider provider, ISecretProtector protector);
}
```

**Bất biến:** `null` và `""` không bao giờ lẫn — mọi caller phân nhánh được "thiếu account" vs "account không key".

## 4. Backend — thay đổi theo đường

### 4.1 Chat path — `ChatCompletionsHandler.ForwardAsync`

- Vẫn `ResolveFirstEnabledAccount` → `null` → 503 (giữ).
- `key = ApiKeyEncrypted == "" ? "" : protector.Unprotect(...)` → `PostChatCompletionAsync(provider, key, ...)`.
- `ProxyTarget(provider, account)` đặt như cũ (account no-key vẫn có junction proxy).

### 4.2 `ProviderRequestFactory.Create`

```csharp
case ProviderType.OpenAI:
    if (key.Length > 0)   // "" → bỏ hẳn Authorization, không gửi "Bearer "
        request.Headers.Authorization = new("Bearer", key);
    break;
case ProviderType.Anthropic:
    if (key.Length > 0)   // "" → bỏ x-api-key
        request.Headers.TryAddWithoutValidation("x-api-key", key);
    request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01"); // luôn giữ
    break;
```

### 4.3 `ModelHealthWatchdog.ProbeOneAsync`

Giữ nguyên structure — `ResolveFirstEnabledKey` giờ trả `""` cho no-key nên dòng `if (key is null) → RecordProbeFailure` chỉ còn nổ khi **thật sự không có account**; `""` rơi xuống `PostChatCompletionAsync(provider, "", ...)` → probe không key. **Đây là fix bug âm thầm:** không sửa thì provider no-key-only probe fail vĩnh viễn → model luôn unhealthy.

### 4.4 `ProviderService.TestConnectionAsync` (D9)

```csharp
var key = apiKeyOverride switch
{
    null => account is not null ? UnprotectSafe(account) : string.Empty, // fallback (cũ)
    _    => apiKeyOverride,                                              // "" = force no-key (mới)
};
```

### 4.5 Services & validator

- `ProviderAccountValidator.ValidateAndThrow`: đổi điều kiện thành `!draft.NoKey && string.IsNullOrWhiteSpace(draft.ApiKey)` → `throw ArgumentException("API key is required.")`.
- `ProviderAccountService.CreateAsync`: `NoKey=true` → `ApiKeyEncrypted = ""`; ngược lại bắt buộc key (qua validator).
- `ProviderAccountService.UpdateAsync`: `NoKey=true` → `ApiKeyEncrypted = ""` (xóa key); `NoKey=false` → giữ nguyên (rỗng = giữ key cũ).
- `ProviderService.CreateAsync`: **throw nếu `!NoKey && key rỗng`** — luôn tạo ≥1 account ("Default").
- `ProviderAccountService.TestAllAsync`: `key = ApiKeyEncrypted == "" ? "" : protector.Unprotect(...)` — không bao giờ `Unprotect("")`.
- `FreeModelSyncService` / `ModelService` / `ProviderEndpointMetadataProvider`: pattern `?? string.Empty` hiện có — resolve trả `""` thay `null`, **không cần đổi code**.

## 5. UI — `Providers.razor`

### 5.1 Form tạo provider (create mode)

- Checkbox `accounts.field.noKey` ngay trên ô ApiKey; bật → ô ApiKey `disabled` + placeholder `accounts.placeholder.noKey`.
- Gate `CanSave` giữ nguyên (test pass + `DraftSignature` = `Type|BaseUrl|ApiKey`; no-key → ApiKey `""` trong signature — đổi draft là test lại).
- Chặn cứng: `!NoKey && key trống` → hiện `accounts.error.keyOrNoKey` dưới ô key, Save disabled.
- **Edit mode của provider** (cập nhật provider đã lưu): giữ nguyên — test tùy chọn, `draft.ApiKey` bị bỏ qua (spec provider-accounts §4.2) — không đổi hành vi này.

### 5.2 Form account (edit mode)

- Checkbox cùng key i18n; mở form từ account no-key → toggle bật sẵn + ô key đóng băng; account có key → toggle tắt, ô trống = giữ key cũ.
- **Nút Test** (tái dùng `providers.action.test`) chỉ render khi toggle bật → `TestConnectionAsync(provider, override: "")` — test `GET /v1/models` không key.
- **Gate Save:** toggle bật → Save disabled tới khi `_accountTestResult.Success` **và** `_accountTestedSignature == Type|BaseUrl|NoKey` khớp draft hiện tại; guard chặn cứng trong `SaveAccountAsync` (pattern CanSave của Providers/Combos).
- **Signature & target lấy từ draft hiện tại trong modal** (`_draft.Type`/`_draft.BaseUrl`) — cùng nguồn với `TestDraftAsync`, để user sửa URL/type rồi test lại đúng thứ sẽ lưu; BaseUrl đổi sau test → signature lệch → bắt test lại.

### 5.3 Danh sách account

- Nhãn nhỏ `no key` trong cột info (sau `W… P… · usage`) để phân biệt account không key — khỏi mở form.

## 6. i18n (bắt buộc cả 2 dict EN + VI trong `Translations.cs`)

| Key | VI | EN |
|---|---|---|
| `accounts.field.noKey` | Không dùng API key | No API key |
| `accounts.error.keyOrNoKey` | Nhập API key hoặc bật "Không dùng API key" | Enter an API key or enable "No API key" |
| `accounts.placeholder.noKey` | (không dùng API key) | (no API key) |

Nút Test tái dùng `providers.action.test`; toast lỗi chung `dashboard.msg.failed`.

## 7. Tests

| Nhóm | Test |
|---|---|
| Resolver | `ResolveFirstEnabledKey_NoKeyAccount_ReturnsEmptyString`; `ResolveFirstEnabledKey_NoAccounts_ReturnsNull`; `ResolveFirstEnabledAccount_NoKeyAccount_SelectedByPriority`; `ResolveFirstEnabledKey_MixedKeyAndNoKey_FollowsPriority` |
| RequestFactory | `Create_KeyEmpty_OmitsAuthorizationHeader`; `Create_KeyEmpty_Anthropic_KeepsVersionHeaderDropsApiKey`; `Create_KeyPresent_SetsHeader` |
| Forward (integration qua stub) | `ForwardAsync_NoKeyAccount_SendsWithoutAuthorizationHeader`; `ForwardAsync_NoAccounts_Returns503` |
| Watchdog | `ProbeAsync_NoKeyAccount_ProbesWithEmptyKey`; `ProbeAsync_NoAccounts_RecordProbeFailure` |
| Validator/Services | `CreateAccount_NoKeyTrue_EmptyKey_Ok`; `CreateAccount_NoKeyFalse_EmptyKey_Throws`; `UpdateAccount_NoKeyTrue_ClearsKey`; `UpdateAccount_NoKeyFalse_EmptyKey_KeepsOldKey`; `ProviderCreate_NoKeyTrue_CreatesDefaultAccountEmptyKey`; `ProviderCreate_NoKeyFalse_NoKey_Throws` |
| Test gate | `TestConnectionAsync_OverrideEmpty_ForcesNoKeyProbe`; `TestConnectionAsync_OverrideNull_FallsBackToAccount`; `TestAllAsync_NoKeyAccount_SkipsUnprotect` |

**Gates:** Core build 0W/0E · test suite xanh (baseline 533 + ~20 mới) · TFM build 0W/0E. Flake policy: 2 flake cũ đã biết (`ProxyQueueIntegrationTests`, `ProxyControlApiTests`) — rerun + isolation.

## 8. Non-goals

- Không auto-detect provider free / không hardcode catalog free ở đây (đã có item provider-free).
- Không thêm cột DB / migration.
- Không đụng inbound `ApiKeyMiddleware` (API key của client).
- Không đổi selection 3D (first-by-Priority giữ nguyên, weighting/quota thuộc slice 3D).
- Không sửa nhánh Anthropic no-key riêng — gate test tự chặn bằng 401.
- UI không unit test (build-gated theo convention repo).
