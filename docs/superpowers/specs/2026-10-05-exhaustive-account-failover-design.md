# Spec: Exhaustive account failover (bỏ ManualRetryStore, bỏ ping, log walk đầy đủ)

- **Ngày:** 2026-10-05
- **Trạng thái:** Draft — chờ user review spec
- **Spec liên quan:**
  - [2026-10-04-manual-retry-revamp-design.md](2026-10-04-manual-retry-revamp-design.md) — **bị thay thế** ở các phần manual-retry/ping/watchdog; các phần Dashboard URL `/v1` + timeout (`providerProbeTimeoutSec`) đã giao trước đó, không đụng lại.
  - [2026-09-29-retry-circuit-design.md](2026-09-29-retry-circuit-design.md) (3C — circuit/watchdog đã bị xoá từ trước; spec này tiếp tục gỡ nốt ManualRetryStore thay thế nó).
  - [2026-09-28-queue-selection-design.md](2026-09-28-queue-selection-design.md) (queue/selection — **giữ nguyên**, spec này chỉ mở rộng tầng account).

## 1. Bối cảnh & mục tiêu

Người dùng đổi hướng sau khi spec 2026-10-04 được implement:

1. **Không còn khái niệm "park"/danh sách retry thủ công** — xoá `ManualRetryStore`, card Dashboard, gate 503 theo model parked, nút [Retry now]. Không còn trạng thái "chờ retry" giữa các request.
2. **Không còn ping định kỳ** — xoá `ProviderPingService` (cả 2 nhịp normal/parked vừa tách) + toàn bộ setting ping. App chỉ còn failover theo request.
3. **Retry tự động theo request, exhaustive**: mỗi request tự thử **mọi TK enabled của mọi provider trong combo** trước khi trả lỗi về client; hết mới dừng.
4. **Log walk hiện tại không đủ lỗi** — lỗi retryable im lặng, không ghi account, exhaustion thiếu chi tiết (xem §5).

### 1.1 Mục tiêu

| # | Mục tiêu |
|---|---|
| G1 | Walk thử hết TK (cùng provider) → hết TK thì sang provider kế; không còn filter/park theo store. |
| G2 | Phân cấp advance rõ ràng: 401/403/429/408/5xx → TK kế; lỗi mạng + 404 khác → bỏ nguyên provider; 404 `model_not_found` → candidate kế. |
| G3 | Xoá hoàn toàn subsystem manual-retry + ping (core, DI, UI, settings, i18n, test). |
| G4 | Log mỗi attempt fail (Warn) + exhaustion (Error) đủ provider/TK/status/hành động/lịch sử. |
| G5 | Giữ nguyên: queue priority, capacity/round-robin, combo mode RoundRobin/Fallback, exhaustion contract, mọi lỗi contract khác. |

### 1.2 Ngoài phạm vi

| Việc | Lý do |
|---|---|
| Endpoint Anthropic, `/v1/responses` | slice 3E — không đổi |
| FreeModelSync / provider-probe / `providerProbeTimeoutSec` | không đụng tới |
| Persist trạng thái walk ra DB | `RetryState` sống trên request — đủ |
| Đổi thứ tự chọn provider (RR) hay queue priority | G5 — giữ nguyên |
| Thêm trần attempt (`maxRetry`) | user đã chọn gỡ (§1.3 #4) |
| Stats/error-rate dashboard | YAGNI |

### 1.3 Quyết định đã chốt (user gate khi brainstorm)

1. **Bỏ hết park** — không còn khái niệm park; "Xoá luôn ping" — xoá cả `ProviderPingService`.
2. **Bảng advance** (mục Q1, chọn A): 401/403 **và** 429/408/5xx → **TK kế** cùng provider; 404 `model_not_found` → candidate kế; 404 khác → provider kế; 4xx client (400/409/422) → passthrough ngay (giữ 3A).
3. **Lỗi mạng (timeout/unreachable) → bỏ qua cả provider ngay** (mục Q3, chọn A) — không quét TK: lỗi mạng là provider-wide, quét N TK = N lần timeout vô ích. 401/403/429/5xx server vẫn trả lời nhanh → quét TK OK.
4. **Gỡ luôn `maxRetry` + `watchdogIntervalSec`** (mục Q2, chọn A) — walk tự exhaustive, không còn consumer. Sau khi rà tác động queue/ExecutionList/selection → xác nhận A.
5. **Exhaustion contract giữ nguyên**: attempt cuối có HTTP → passthrough nguyên response đó; mạng → 502.
6. **RoundRobin/Fallback giữ nguyên** — mode là thuộc tính tầng chọn provider; walk mới chỉ thêm tầng lặp TK bên trong. Fallback được hưởng lợi: thử hết TK provider theo Position 1 **rồi mới** rơi xuống Position 2.
7. **Log**: mỗi attempt fail = Warn + exhaustion Error (mục Q1 chọn A), hiển thị theo **`provider.Name`/`account.Name`** (id chỉ nội bộ).
8. **`Details` log chứa snippet lỗi upstream** ~500 ký tự (JSON `error.message`/`type`) hoặc exception (mạng) — **không** ghi body request, không ghi API key (mục Q2 chọn A).
9. **Approach 1**: `exclude` trong `TryEnter` + mở rộng `RetryState` — capacity/RR giữ một nguồn trong `ExecutionList`.

## 2. Architecture

### 2.1 Component xoá / sửa / thêm

**Xoá:**

| Component | Vị trí |
|---|---|
| `ManualRetryStore`, `IManualRetryStore` (kèm `ManualRetryLevel`, `ManualRetryReason`, `ManualRetryEntry`), `ManualRetryI18n` | `src/RouterBalancing.Core/Engine/` |
| `ProviderPingService` | `src/RouterBalancing.Core/Providers/` |
| DI: `MauiProgram.cs:81`; `ProxyApp` factory if-absent store (40-46), đăng ký ping (102-103), tham số `store` (130), **gate `IsModelParked → 503` (148)**; `ProxyHost` field/ctor/passthrough `_manualRetryStore` (27/40/79) | `MauiProgram.cs`, `Server/` |
| Card retry + `RetryNowAsync` + related code/CSS | `Dashboard.razor` |
| 5 field settings: `maxRetry`, `watchdogIntervalSec`, `pingIntervalSec`, `pingParkedIntervalSec`, `pingParkedProviders` (key + draft prop + validator rule + service property + UI + i18n `settings.field/error.*`) | `Settings/`, `SettingsPanel.razor`, `Translations.cs` |
| i18n block `manualRetry.*` | `Translations.cs` |
| Test: `ManualRetryStoreTests`, `ManualRetryI18nTests`, `ProviderPingServiceTests` + mọi block park-based | `router balancing test/` |

**Sửa:**

| Component | Thay đổi |
|---|---|
| `DispatchOutcome.Fatal` | `Fatal(FailoverLevel Level, int? Status, string? ContentType, byte[] Body, TimeSpan? RetryAfter)` — **bỏ `Id`/`ModelId`/`Reason`** (dispatcher có sẵn candidate + accountId, log tự tra tên). |
| `ChatCompletionsHandler.ClassifyFatal` | Map sang `FailoverLevel` (bảng §3.1). `RetryClassifier` (429/408/5xx → `Retryable`) **giữ nguyên**. |
| `DispatcherLoop` | Bỏ `store` khỏi ctor; `FilterRemaining` (§3.2); `ServeAsync` rẽ nhánh theo cấp fail + log attempt (§5); bỏ `LogAdvance` (đã gộp vào attempt log); bỏ `store.Park`. |
| `ExecutionList` | Bỏ `store` (không còn filter parked trong `LoadCapacityAsync`); `TryEnterAsync` trả **tri-state** (§2.2). |
| `RetryState` | Thêm account đã thử, provider đã fail, bộ đếm attempt, attempt trail (§2.3). |
| `Settings` 5 tầng + `SettingsPanel` + `Translations` | Gỡ 5 key như trên (row mồ côi trong bảng `AppSettings` — vô hại, không migration). |

**Thêm:**

| Component | Responsibility |
|---|---|
| `FailoverLevel` enum (`Account`, `Provider`, `Model`) — file `Engine/FailoverLevel.cs` hoặc trong `DispatchOutcome.cs` | Cấp advance thay `ManualRetryLevel`. |
| `TryEnterResult` record (§2.2) | Kết quả `TryEnterAsync` 3 nghĩa: vào được / đầy / hết TK. |

**Không đổi:** `ModelSelector`, `ComboResolver`, `RequestQueue`, `ProxyRequest`, `RetryClassifier`, sentinel `AccountId=0`, `CanEnterAsync`.

### 2.2 `ExecutionList.TryEnterAsync` — tri-state

```csharp
public abstract record TryEnterResult
{
    public sealed record Entered(long AccountId) : TryEnterResult; // gồm sentinel 0
    public sealed record Full : TryEnterResult;                    // còn TK nhưng hết capacity → park
    public sealed record NoAccountLeft : TryEnterResult;           // không còn TK enabled nào ngoài exclude → advance
}

Task<TryEnterResult> TryEnterAsync(long providerId, string requestId, string providerName,
    string modelId, RequestPriority priority, DateTimeOffset enqueuedAt,
    IReadOnlySet<long>? excludedAccounts, CancellationToken ct);
```

Logic (trong 1 lock, sau khi load capacity mới nhất từ DB):

1. `capacity` null (provider xoá) → **`Full`** (park — giữ hành vi D-B7 hiện tại).
2. `Accounts.Count == 0` (0 TK enabled) → sentinel `Entered(0)` — giữ nguyên lưới an toàn V1.
3. `Accounts − excludedAccounts` rỗng (các TK enabled đều đã thử trong request này) → **`NoAccountLeft`**.
4. Còn TK chưa thử nhưng tất cả đầy → **`Full`**.
5. Còn TK chưa thử còn capacity → chọn least-in-flight + RR tie (giữ nguyên) → `Entered(id)`.

`excludedAccounts = null` khi dispatch đầu / advance sang provider mới chưa từng thử; = tập đã thử **của provider đó** khi advance giữa chừng (account kế). Tập tried theo provider nên TK của provider khác không bị loại.

**Lý do tri-state thay vì `long?`:** với snapshot `candidate.Provider.Accounts` có thể stale (user tắt TK giữa walk), dispatcher không tự phân biệt được "hết TK chưa thử" (= advance) vs "TK đầy" (= park) — đoán sai theo kiểu nào cũng có bug: park sai → request treo đến khi client huỷ; advance sai → bỏ sót TK. Để ExecutionList (nơi đọc DB mới nhất) quyết định.

`CanEnterAsync` (selector) **không đổi** — không cần exclude; filter ở `FilterRemaining` đã loại candidate hết TK chưa thử trước khi selector nhìn thấy.

### 2.3 `RetryState` — mở rộng

```csharp
private readonly HashSet<(long ProviderId, string ModelId)> _tried = [];        // pair đã thử (giữ)
private readonly HashSet<(long ProviderId, long AccountId)> _triedAccounts = []; // TK đã thử
private readonly HashSet<long> _failedProviders = [];                           // provider fail cấp Provider

public int Attempts { get; private set; }                                       // bộ đếm attempt (log k/N)
public List<AttemptRecord> Trail { get; }                                       // lịch sử attempt (exhaustion Details)

public sealed record AttemptRecord(string Provider, string Model, string Account, int? Status);
```

- `MarkAccountTried(providerId, accountId)` / `IsAccountTried` — account-level fail.
- `MarkProviderFailed(providerId)` / `IsProviderFailed` — fail cấp Provider (mạng/404 khác) → filter loại **mọi candidate của provider** kể cả model chưa thử (quan trọng khi capacity park re-enqueue — dispatch lại không tốn timeout thử model khác cùng provider chết).
- `MarkTried(provider, model)` (pair) giữ nguyên — cấp Model fail + cấp Provider nhưng đã qua account-advance (`NoAccountLeft`).
- `HasTried`/`TriedCount` (pair) giữ nguyên — phân biệt 503 walk-rỗng vs exhaustion.
- `LastFailure` giữ nguyên (attempt cuối quyết định exhaustion).
- Không lock — 1 logical owner (đã ghi trong file cũ, giữ nguyên).

### 2.4 Data flow

```
request → validate (400) → RequestQueue (không còn gate 503 theo parked model)
  → DispatcherLoop: resolve → FilterRemaining → selector → TryEnter(null) → ServeAsync:
      attempt k: ForwardAsync(provider, model, accountId)
        ├─ Handled (2xx stream)        → Exit, trả endpoint
        ├─ Passthrough (4xx client)    → Exit, trả endpoint ngay (không attempt-fail log)
        ├─ Error/Cancelled/Aborted     → Exit, trả endpoint (giữ nguyên)
        ├─ Retryable (429/408/5xx)     → account-advance:  MarkAccountTried → Exit
        │                                  → TryEnter(exclude) → Entered: tiếp / Full: park
        │                                  → NoAccountLeft: MarkTried(pair) → candidate-advance
        ├─ Fatal(Account)              → như Retryable
        ├─ Fatal(Model)   (404 model)  → MarkTried(pair)   → Exit → candidate-advance
        └─ Fatal(Provider)(mạng/404khác)→ MarkProviderFailed → Exit → candidate-advance
      candidate-advance: FilterRemaining → rỗng? exhaustion (Passthrough/502)
                                        : selector → TryEnter(exclude theo provider mới)
                                          → Entered: tiếp / Full: park / NoAccountLeft: MarkTried(pair) → lặp (hữu hạn)
```

`ReenqueueForPark` (capacity `Full`) giữ nguyên — item về queue chờ `Exited`/`Changed`; `RetryState` sống qua re-enqueue nên TK/pair đã thử không bị thử lại.

## 3. Behavior

### 3.1 Phân loại lỗi & cấp advance (`ClassifyFatal` + dispatcher)

| Điều kiện | Outcome | Cấp advance | Hành động walk |
|---|---|---|---|
| 2xx | `Handled` | — | kết thúc |
| 401/403 | `Fatal(Account)` | Account | đánh dấu TK → TK kế cùng provider |
| 429/408/5xx | `Retryable` (giữ) | Account (do dispatcher gán) | như trên |
| Lỗi mạng/timeout | `Fatal(Provider)` | Provider | `MarkProviderFailed` → provider kế (không quét TK) |
| 404 + `error.code=model_not_found` | `Fatal(Model)` | Model | `MarkTried(pair)` → candidate kế |
| 404 còn lại | `Fatal(Provider)` | Provider | như lỗi mạng |
| 4xx khác (400/409/422...) | `Passthrough` | — | dừng, client tự giải quyết |
| No-key 503 / client abort | giữ nguyên | — | — |

- Nhận diện `model_not_found` best-effort như hiện tại (JSON `error.code`, ordinal-ignore-case; body hỏng → coi 404 thường = cấp Provider).
- `Fatal` không còn `Id`/`ModelId`/`Reason`: dispatcher lấy context từ `candidate` + `accountId` đang cầm.

### 3.2 Walk (`ServeAsync`) & `FilterRemaining`

**`FilterRemaining`** (dùng cho dispatch đầu + mỗi candidate-advance):

```csharp
candidates.Where(c => !retry.IsTried(c.Provider.Id, c.Model.ModelId)
                   && !retry.IsProviderFailed(c.Provider.Id)
                   && HasEnabledUntriedAccount(c))
```

- `HasEnabledUntriedAccount(c)` = `c.Provider.Accounts?.Any(a => a.Enabled && !retry.IsAccountTried(a.Id)) != false` — thay `HasEnabledUnparkedAccount` (bỏ mọi check store; `null` Accounts → không loại, ExecutionList tự lo).
- Kết quả rỗng + `!HasTried` → 503 `temporarily unavailable` (giữ); `HasTried` → exhaustion (§4).

**Vòng `ServeAsync`** — sau mỗi attempt fail, rẽ theo cấp (xem data flow §2.4):

- **Account-advance:** `MarkAccountTried` → `Exit` → `TryEnter(same provider, exclude)` → `Entered` = tiếp tục với TK mới; `Full` = park; `NoAccountLeft` = `MarkTried(pair)` → candidate-advance.
- **Candidate-advance:** `Exit` → filter → rỗng → exhaustion; còn → selector → `TryEnter(new, exclude theo provider mới)` → `Entered` tiếp tục / `Full` park / `NoAccountLeft` → `MarkTried(pair)` → filter lại (mỗi vòng `NoAccountLeft` đánh dấu 1 pair → hữu hạn, không loop vô hạn).
- **Park:** `ReenqueueForPark` giữ nguyên mọi check abort/idempotent hiện tại.
- **Lỗi exception giữa select/enter:** slot đã trả trước đó → outcome `Error 500` (giữ nguyên cấu trúc try/catch, log bọc nuốt I2).
- Sentinel `Entered(0)` → `ForwardAsync` không có account → `Error 503` → walk dừng, client nhận 503 (lưới an toàn hiếm gặp — snapshot stale).

RR/Fallback: không đổi — `FilterRemaining` giữ thứ tự list (Fallback = Position), selector RR quyết định lựa chọn đầu; account-advance không qua selector (cùng provider, chọn least-loaded).

### 3.3 Settings & i18n

**Gỡ** khỏi `SettingsKeys`, `IAppSettingsService`, `SettingsDraft`, `SettingsValidator`, `SettingsPanel`, `Translations`:

| Key | Ghi chú |
|---|---|
| `maxRetry`, `watchdogIntervalSec` | vừa khôi phục trong session trước — gỡ lại (Q2-A) |
| `pingIntervalSec`, `pingParkedIntervalSec`, `pingParkedProviders` | `ProviderPingService` chết → hết consumer |

- i18n key `settings.field.maxRetry/watchdog/pingInterval/pingParkedInterval/pingParked` + `settings.error.*` tương ứng + **toàn bộ `manualRetry.*`** (EN/VI) xoá khỏi `Translations.cs`.
- `SettingsValidatorTests`, `AppSettingsServiceTests`, `SettingsTranslationTests` gỡ assert 5 field.
- Row mồ côi trong bảng `AppSettings` — vô hại, không migration.

### 3.4 UI

- **Dashboard**: xoá card retry thủ công (render entries, badge level/reason, nút Retry now, `GetEntryDisplayName`, subscribe `store.Changed`) + CSS class chỉ card này dùng.
- **Settings**: xoá 5 field + error block tương ứng; phần engine còn lại (probe timeout…) giữ nguyên.
- Không có UI thay thế — "trạng thái failover" quan sát qua **Logs** (§5).

## 4. Error contract

| Tình huống | Status | Message (EN) | Type / Param / Code |
|---|---|---|---|
| Exhaustion, attempt cuối có HTTP (kể cả 401/404/429/5xx) | **passthrough** nguyên response cuối | (body provider nguyên si) | — |
| Exhaustion, attempt cuối là mạng | 502 | `Upstream provider request failed` | `server_error` / null / null |
| Walk rỗng ngay (mọi candidate không còn TK enabled/untried, **chưa thử ai**) | 503 | `The model '{model}' is temporarily unavailable` | `server_error` / null / null |
| Validate 400 / resolve 404 / no-key 503 / cancel 400 | không đổi | (như cũ) | — |
| 4xx client (400/409/422) | passthrough | (như 3A) | — |

**Xoá**: dòng "model parked — gate enqueue 503" (gate bị gỡ theo §2.1). Không còn 503 do store.

## 5. Logging

Log walk tập trung ở `DispatcherLoop` (helper bọc try/catch nuốt — I2), `LogCategory.Request`, hiển thị **`provider.Name` / `account.Name`** (tra từ `candidate.Provider.Accounts`; sentinel ghi `-`).

**1. Mỗi attempt fail — 1 dòng `Warn`:**

```
Request ab12cd34 — attempt 3/9 fail: provider 'OpenRouter'/'llama-3.3-70b' account 'key-studio' HTTP 429 → chuyển TK kế
```

- Trạng thái: `HTTP {code}` hoặc `lỗi mạng`; `action` ∈ `chuyển TK kế` / `chuyển provider kế` / `chuyển candidate kế` / `chờ slot` (park) / `exhausted`.
- `k` = `RetryState.Attempts` (tăng mỗi lần gọi `ForwardAsync`); `N` = `k` + tổng TK enabled của các candidate còn lại khi bắt đầu vòng serve (ước lượng upper bound từ snapshot — có thể lệch nhẹ, chỉ phục vụ log).
- `Details` = snippet lỗi upstream từ `Fatal`/`Retryable.Body` (JSON `error.message`/`error.type`, cắt ~500 ký tự, best-effort); lỗi mạng → short reason (exception đã có ở dòng handler Error riêng).
- Không ghi body request, không ghi API key, không ghi nội dung prompt.
- **Xoá `LogAdvance`** ("Chuyển candidate kế..." cũ) — trùng info, đã gộp vào `action`.

**2. Exhaustion — 1 dòng `Error` (thay dòng "thất bại sau N candidate"):**

```
Request ab12cd34 thất bại sau 9 attempt — chuyển phản hồi cuối về client
```

`Details` = JSON mảng lịch sử attempt từ `RetryState.Trail`:
`[{"p":"OpenRouter","m":"llama-3.3-70b","a":"key-studio","s":429}, ...{"p":"Groq",...,"s":null}]` (attempt cuối đứng cuối; `s:null` = lỗi mạng).

**3. Giữ nguyên:** lỗi mạng transport Error + exception (handler — nguyên nhân gốc; chồng lên attempt Warn là chủ đích); `LogForwarded` Info 2xx/passthrough; resolve-failure Warn; `LogRequestUsage`.

## 6. Testing strategy & gates

### 6.1 Unit (TDD — RED trước khi viết implementation)

- **`ExecutionList`**: `TryEnterAsync_WhenAccountExcluded_DoesNotPickItAgain`; `TryEnterAsync_WhenAllUntriedAccountsFull_ReturnsFull`; `TryEnterAsync_WhenAllEnabledAccountsExcluded_ReturnsNoAccountLeft`; `TryEnterAsync_WhenNoEnabledAccounts_ReturnsSentinelZero`; `TryEnterAsync_WithoutExclusion_RoundRobinsAsBefore`; provider xoá → `Full`.
- **`RetryState`**: mark/read account theo provider (cùng provider khác provider); `MarkProviderFailed` + `IsProviderFailed`; pair `HasTried` sau `NoAccountLeft`; `Attempts`/`Trail`.
- **`DispatcherLoop`** (hành vi walk — cốt lõi):
  - `ServeAsync_When401_TriesNextAccountOfSameProvider_BeforeChangingProvider`
  - `ServeAsync_When429_TriesNextAccountOfSameProvider`
  - `ServeAsync_WhenAllAccountsTried_MovesToNextProvider`
  - `ServeAsync_WhenNetworkError_SkipsProvider_WithoutTryingOtherAccounts` (kể cả model khác cùng provider trong combo)
  - `ServeAsync_WhenModelNotFound_SkipsCandidate` / `ServeAsync_When404Other_SkipsProvider`
  - `ServeAsync_WhenExhausted_PassesThroughLastHttpResponse` / `ServeAsync_WhenExhaustedOnNetworkFailure_Returns502`
  - `ServeAsync_WhenUntriedAccountsFull_Reenqueues_AndDoesNotRetryTriedAccounts` (park + wake lại)
  - `ServeAsync_WhenReEnqueuedAfterPark_DoesNotRetryFailedProviderOrAccounts`
  - `ServeAsync_LogsWarnPerFailedAttempt_WithProviderAndAccountNames` / `ServeAsync_WhenExhausted_LogsErrorWithAttemptTrailInDetails`
  - `TryDispatch_WhenComboFallback_KeepsPositionalOrderAcrossProviders`
  - Walk rỗng chưa thử ai → 503 `temporarily unavailable`
- **`ChatCompletionsHandlerTests`**: đổi assert `ManualRetryLevel/Reason` → `FailoverLevel` (401/403→`Account`, 404 model→`Model`, 404 khác+mạng→`Provider`, 429/5xx→`Retryable`, 400→`Passthrough`).
- **Settings/i18n**: gỡ assert 5 field; i18n parity test xanh sau khi xoá key.

### 6.2 Integration (`ProxyRetryIntegrationTests`)

Thay test store-gate bằng:

- 401 TK1 của provider A → **TK2 của cùng A** thành công (client 200) — không nhảy sang B.
- 401 cả 2 TK của A → failover provider B thành công.
- Provider A network dead (mọi model trong combo) → timeout đúng **1 lần** (không quét TK), request qua B serve.
- Hết combo (mọi response fail) → client nhận **đúng response cuối** (passthrough 401/429...); toàn mạng → 502.
- Test passthrough 4xx / validate / resolve / no-key / cancel **giữ xanh**.

### 6.3 Gates cuối

- `dotnet test` toàn suite xanh (0 failed).
- `dotnet build router-balancing.slnx` + app `-f net10.0-windows10.0.19041.0` → 0 Warning / 0 Error.
- i18n parity test xanh sau khi gỡ key.
- `git status` sạch; commit conventional tiếng Anh, mỗi task 1 commit.

## 7. Handoff

- **Step 0 khi implement:** revert/tháo toàn bộ work chưa commit của session trước (khôi phục `MaxRetry`/`Watchdog`/`PingParked*` settings + tách 2 nhịp ping) — spec này gỡ hết. **Không đụng** 2 file dirty không phải của session: `src/RouterBalancing.Core/Providers/ProviderRequestFactory.cs` (modified), `NvidiaRequestProfile.cs` + `NvidiaRequestProfileTests.cs` (untracked).
- Spec 2026-10-04: các phần URL `/v1` + timeout đã giao — chỉ phần manual-retry/ping/watchdog bị thay thế bởi spec này.
- `RetryClassifier` giữ nguyên; phân loại cấp mới nằm trọn trong `ClassifyFatal` + dispatcher.
- Nếu sau này muốn "ceiling an toàn" cho walk → thêm lại `maxRetry` với nghĩa mới (đếm `RetryState.Attempts`) — seam nằm trong `ServeAsync`.
- Stats/error-rate: điểm natural để ghi counter là attempt-fail log (§5) — mỗi dòng Warn là 1 sự kiện fail.
