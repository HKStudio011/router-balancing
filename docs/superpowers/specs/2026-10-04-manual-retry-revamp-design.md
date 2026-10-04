# Spec: Manual Retry 3 cấp (bỏ watchdog/MaxRetry) + Dashboard URL `/v1` + Timeout 60s

- **Ngày:** 2026-10-04
- **Trạng thái:** Draft — chờ user review spec
- **Spec liên quan:**
  - [2026-09-29-retry-circuit-design.md](2026-09-29-retry-circuit-design.md) (3C — circuit/watchdog bị thay thế bởi spec này)
  - [2026-09-27-model-capabilities-url-normalization-design.md](2026-09-27-model-capabilities-url-normalization-design.md) (chuẩn hoá `/v1` phía provider — không đổi)
  - [2026-09-28-queue-selection-design.md](2026-09-28-queue-selection-design.md) (queue/selection — không đổi)

## 1. Bối cảnh & mục tiêu

Ba vấn đề người dùng báo:

1. **Dashboard hiển thị sai URL kết nối**: chỉ hiện `http://127.0.0.1:{port}` — client OpenAI-compatible cần `.../v1` (app hiện chỉ serve OpenAI protocol, không có `/v1/messages` Anthropic).
2. **Cơ chế retry hiện tại sai hành vi so với mong muốn** (spec 3C):
   - Lỗi fatal (401/403 sai auth, 404, provider chết) là *non-retryable* → passthrough ngay, **không** vào danh sách chờ retry — user muốn các lỗi này **vào danh sách retry thủ công ngay lần đầu**.
   - Ngược lại lỗi tạm thời (429/5xx) mới tích lũy `MaxRetry` → mở fuse → watchdog tự probe phục hồi — user muốn **bỏ retry tự động**: lỗi khác trả thẳng về client, client tự giải quyết.
   - Phân cấp: lỗi xảy ra ở 3 cấp khác nhau (provider / account / model) nhưng store chỉ có 1 cấp model.
3. **Timeout 10s quá ngắn cho lần kết nối đầu** — `ConnectTimeout=10s` làm request/test lần đầu chậm bị cắt; `provider-probe` timeout 10s hardcode cần chỉnh được.

### 1.1 Mục tiêu

| # | Mục tiêu |
|---|---|
| G1 | Dashboard hiển thị URL kết nối OpenAI-style có `/v1` (loopback + LAN). |
| G2 | Lỗi fatal → đưa entity đúng cấp (provider/account/model) vào **danh sách retry thủ công ngay lần đầu**, đồng thời vẫn failover; hết candidate mới trả lỗi về client. |
| G3 | **Bỏ retry tự động**: không watchdog probe, không tích lũy `MaxRetry`. Lỗi không fatal → trả client như hiện tại (failover giữ nguyên). |
| G4 | Phục hồi **thủ công** qua UI danh sách + nút [Retry now]; **tuỳ chọn** ping tự phục hồi provider qua setting (mặc định tắt). |
| G5 | Ping định kỳ cấp **provider** phát hiện provider chết/sai auth/notfound → park provider. |
| G6 | `ConnectTimeout` 10s → 60s; timeout `provider-probe` là setting (default 60s). |

### 1.2 Ngoài phạm vi (rõ ràng)

| Việc | Lý do |
|---|---|
| Endpoint Anthropic `/v1/responses`, `/v1/messages` | slice 3E — URL Anthropic không-v1 không hiển thị vì app chưa serve |
| FreeModelSync + client timeout 30s của nó | không đụng tới |
| Persist danh sách retry ra DB | in-memory là đủ — restart = request/ping park lại |
| Nav badge / trang riêng cho danh sách retry | YAGNI — card trên Dashboard |
| Thay đổi failover walk, queue, ExecutionList capacity | 2 cơ chế lõi giữ nguyên |

### 1.3 Quyết định đã chốt (user gate khi brainstorm)

1. **URL**: chỉ thêm `/v1` cho URL OpenAI trên Dashboard; bỏ qua Anthropic.
2. **Không thêm toggle "retry thủ công"**; bỏ luôn retry tự động (watchdog + tích lũy MaxRetry) — chỉ lỗi cụ thể vào danh sách.
3. **Giữ failover walk**; bỏ watchdog probe + bỏ ngưỡng `MaxRetry`.
4. **Fatal giữa walk**: park + **vẫn failover tiếp** — client chỉ thấy lỗi khi hết candidate (exhaustion contract giữ nguyên).
5. **Granularity theo cấp**: provider / account / model — theo đúng cấp gây lỗi.
6. **Auth nằm ở account** (đã xác minh `ProviderAccount.ApiKeyEncrypted`, `Provider` không giữ key) → request-time 401/403 park **account**; provider cấp do **ping định kỳ** phát hiện.
7. Nguồn phát hiện: **request-time + ping định kỳ** (nút Test Connection chỉ hiện badge như hiện tại, không park).
8. Phục hồi: **UI danh sách + nút Retry now**; ping provider đã park = **toggle Settings, mặc định tắt**.
9. **Phương án 1**: store mới `ManualRetryStore` 3 cấp (in-memory) thay `ModelHealthStore`/`ModelHealthWatchdog`.
10. **Timeout**: `ConnectTimeout` → 60s; `provider-probe` Timeout → setting default 60s.

## 2. Architecture

### 2.1 Component mới / xoá (`src/RouterBalancing.Core/Engine/`)

| Component | Hành động | Responsibility |
|---|---|---|
| `ManualRetryStore` + `IManualRetryStore` | **mới** (Singleton) | Danh sách retry thủ công 3 cấp, 1 lock cho dict — thread-safe; tự log state-transition qua `SafeLog`; bắn `event Action? Changed`. |
| `ModelHealthStore`, `IModelHealthStore` | **xoá** | Thay bằng store mới — hết counter/probe. |
| `ModelHealthWatchdog` | **xoá** | Thay bằng `ProviderPingService`. |
| `ProviderPingService` (BackgroundService) | **mới** (Singleton + hosted) | Ping định kỳ `GET {base}/v1/models` cho provider enabled; fatal → park provider; setting bật → ping cả provider đã park, 2xx → unpark. Đồng hồ qua `TimeProvider`. |

```csharp
public enum ManualRetryLevel { Provider, Account, Model }
public enum ManualRetryReason { Unauthorized, NotFound, ModelNotFound, Unreachable }

/// <summary>Level + Id (providerId/accountId) + ModelId ("" với Provider/Account) — key định danh entry.</summary>
public sealed record ManualRetryEntry(
    ManualRetryLevel Level, long Id, string ModelId,
    ManualRetryReason Reason, DateTimeOffset ParkedAt);

public interface IManualRetryStore
{
    event Action? Changed;                       // Park/Unpark bắn — UI tự refresh
    void Park(ManualRetryLevel level, long id, string modelId, ManualRetryReason reason);
    void Unpark(ManualRetryLevel level, long id, string modelId);
    bool IsProviderParked(long providerId);
    bool IsAccountParked(long accountId);
    bool IsModelParked(string modelId);
    IReadOnlyList<ManualRetryEntry> GetEntries();
}
```

- `Park` idempotent: đã park → giữ `ParkedAt` cũ, cập nhật `Reason`; log Warn transition đúng 1 lần.
- `Unpark` entry không tồn tại → no-op. Level convention: `Model` → `id = 0`, `ModelId` có giá trị; `Provider`/`Account` → `ModelId = ""`.
- Không persist DB: restart = danh sách sạch, request/ping sẽ park lại khi lỗi thật sự còn.

### 2.2 Thay đổi component có sẵn

**`DispatchOutcome`** — thêm record:

- `Fatal(ManualRetryLevel Level, long Id, string ModelId, int? Status, string? ContentType, byte[] Body, TimeSpan? RetryAfter)` — tín hiệu nội bộ: `Status=null` ⇔ lỗi mạng (mang nghĩa "provider không sống"). **Chỉ dispatcher nhìn thấy** (convert trước khi về endpoint).

**`ChatCompletionsHandler.ForwardAsync`** — phân loại mới (xem §3.2); các nhánh 2xx/no-key/client-abort giữ nguyên.

**`DispatcherLoop`**:

- `ServeAsync`: outcome `Fatal` → `store.Park(...)` (bọc try — store không được phá outcome) → xử lý **giống `Retryable`** (MarkTried → advance → exhaustion khi hết).
- `FilterRemaining`: bỏ candidate nếu `IsProviderParked` **hoặc** `IsModelParked` **hoặc** provider không còn account nào `Enabled && !IsAccountParked` (đọc `candidate.Provider.Accounts` — data có sẵn, không query DB).
- `RecordExhaustion` **xoá** — không còn `RecordFailure`.
- **`RetryState`**: `LastRetryable` (`DispatchOutcome.Retryable?`) đổi thành `LastFailure(int? Status, string? ContentType, byte[] Body, TimeSpan? RetryAfter)` — nhận giá trị từ **cả** `Retryable` và `Fatal` (attempt cuối quyết định exhaustion, không đổi); property `TriedModels` (chỉ dùng cho `RecordExhaustion`) cũng xoá.
- `RecordSuccess` **xoá** — không còn counter cần reset.

**`ExecutionList.TryEnter`** — inject `IManualRetryStore`: loại account đang park khỏi selection; cạnh "tồn tại TK enabled nhưng tất cả đều park" → trả sentinel `AccountId=0` (pattern V1 sẵn có) → forward trả 503, **không park request vô hạn**. Capacity/round-robin/Exited không đổi.

**`ProxyApp`** — gate enqueue đổi sang `IManualRetryStore.IsModelParked(prepared.ModelId)` (message/hành vi 503 giữ nguyên); đăng ký `IManualRetryStore` + `ProviderPingService` (singleton + hosted qua factory — integration test resolve được) thay registrations watchdog; đăng ký thêm client `provider-probe` cho container proxy.

### 2.3 Data flow

```
request → validate (400) → [model parked? → 503] → RequestQueue
  → DispatcherLoop walk (filter: provider/model parked, provider hết TK dùng được):
      candidate: TryEnter (bỏ TK parked) → ForwardAsync
        ├─ Handled(2xx stream)              → trả endpoint
        ├─ Fatal(level, ...)                → store.Park(level) → MarkTried → advance
        ├─ Retryable(429/408/5xx)           → MarkTried → advance (không park)
        ├─ Passthrough(4xx khác)            → trả endpoint ngay (không park)
        └─ Error/Cancelled/Aborted          → trả endpoint (giữ nguyên)
      hết list:
        ├─ chưa thử ai (bị filter hết)      → 503 "temporarily unavailable"
        └─ đã thử ≥1                        → attempt cuối HTTP → Passthrough / mạng → 502

ProviderPingService: tick pingIntervalSec
  → provider enabled CHƯA park: 2xx → ok | 401/403/404/mạng → Park(Provider) | 429/5xx → bỏ qua
  → provider ĐÃ park, pingParkedProviders=true: 2xx → Unpark | fail → giữ nguyên
```

Lưu ý: sau thay đổi, `Retryable` **luôn có `Status`** (429/408/5xx) — mọi lỗi mạng đều đi qua `Fatal(Provider, Status=null)`; exhaustion phân biệt mạng/không mạng qua `LastFailure.Status == null`.

## 3. Behavior

### 3.1 Dashboard URL

- `Dashboard.razor`: dòng loopback đổi `http://127.0.0.1:{port}` → `http://127.0.0.1:{port}/v1`.
- URL LAN: hiển thị `@url/v1` (với mỗi `LanUrlProvider.GetUrls(port)` — URL gốc giữ nguyên, chỉ thêm suffix lúc render).
- Không hiển thị dạng không-`/v1`.

### 3.2 Phân loại lỗi (`ChatCompletionsHandler.ForwardAsync`)

| Điều kiện | Outcome | Park | Walk |
|---|---|---|---|
| 2xx | `Handled` (stream) | — | kết thúc |
| 401/403 | `Fatal(Account, accountId, ...)` | account | advance |
| 404 + body JSON có `error.code == "model_not_found"` | `Fatal(Model, 0, modelId, ...)` | model | advance |
| 404 còn lại | `Fatal(Provider, providerId, ...)` | provider | advance |
| Lỗi mạng/timeout (catch filter sẵn có) | `Fatal(Provider, providerId, Status=null, ...)` | provider | advance |
| 429/408/5xx | `Retryable` (giữ nguyên) | không | advance |
| 4xx khác (400/409/422...) | `Passthrough` (giữ nguyên) | không | dừng — client tự giải quyết |
| No-key (503) / client abort | giữ nguyên | — | — |

- Nhận diện `model_not_found`: deserialize body JSON (best-effort, utf-8) — đọc path `error.code` (chuỗi, so sánh ordinal-ignore-case); body không parse được → coi như 404 thường (park provider).
- 401/403 luôn cấp **account** (auth nằm ở account — §1.3 #6); "provider sai auth" chỉ do ping định kỳ phát hiện (§3.5).
- `Fatal` luôn carry status/content-type/body/`RetryAfter` — không mất response nào khi exhaustion (§4).

### 3.3 Walk & gate

- `Fatal` advance theo đúng cơ chế `Retryable` hiện tại: MarkTried → `FilterRemaining` → còn thì chọn tiếp, hết thì exhaustion. **Không requeue, không backoff** (giữ 3C).
- Filter bổ sung (ngoài `IsModelParked` + `IsTried` như cũ): `IsProviderParked(provider.Id)`; provider mọi TK đều `!Enabled` hoặc `IsAccountParked` → loại candidate.
- Gate enqueue (ProxyApp): exact-id `IsModelParked` → 503 trước khi vào queue — message giữ nguyên `The model '{model}' is temporarily unavailable`.
- Provider/account parked không check ở endpoint (endpoint chỉ biết modelId) — walk filter lo; request bị filter hết → 503 (§4).
- Combo: một model trong combo bị park → walk bỏ qua, model kế vẫn serve (hành vi skip 3C giữ nguyên, khóa đổi sang store mới).

### 3.4 Danh sách retry thủ công — phục hồi

- entry chỉ rời danh sách khi: (a) user bấm **[Retry now]** → `Unpark` + toast + log Info; (b) ping tự phục hồi (§3.5, setting bật).
- Không có đường tự phục hồi khác — request tới model/provider/account đang park **không bao giờ** được dispatch (đã filter) nên không thể "thành công để tự hết".
- Restart app → danh sách trống (in-memory).

### 3.5 Ping định kỳ (`ProviderPingService`)

- Tick mỗi `pingIntervalSec` (setting mới, default 60s, range 10..86400).
- **Đối tượng**: mọi provider `Enabled` chưa park; **bỏ qua `ProviderType.Anthropic`** (app chưa serve — ping chỉ tạo noise).
- Probe: `ProviderRequestFactory.Create(provider, key)` → `GET {base}/v1/models` qua client `provider-probe`; key lấy từ **account enabled đầu tiên chưa park** (`Enabled && !IsAccountParked` — pattern `TestConnectionAsync` nhưng bỏ TK đang park, tránh ping dùng key sai rồi oan park provider); no-key → không header auth; không còn TK hợp lệ → **bỏ qua provider trong lượt ping này**; set `ProxyTarget.Current` quanh call để đi đúng proxy của provider (giống `TestConnectionAsync`).
- **Kết quả**:

  | Response | Hành vi |
  |---|---|
  | 2xx | ok — không thay đổi gì |
  | 401/403 | `Park(Provider, Unauthorized)` |
  | 404 | `Park(Provider, NotFound)` |
  | Lỗi mạng/timeout | `Park(Provider, Unreachable)` |
  | 429/5xx/4xx khác | bỏ qua (lỗi tạm thời — failover/request lo) |

- **Provider đã park**: chỉ ping khi `pingParkedProviders = true` (default **false**); 2xx → `Unpark` (log Info phục hồi qua ping); fail → giữ nguyên (không đổi `ParkedAt`, không log lặp mỗi tick — chỉ log khi state đổi).
- `TimeProvider` inject — unit test điều khiển thời gian.
- Nếu provider đang park mà bị tắt (`Enabled=false`) → không ping, giữ nguyên trạng thái park.

### 3.6 Settings & i18n

**Gỡ** (SettingsKeys, IAppSettingsService, SettingsDraft, SettingsValidator, SettingsPanel, i18n key): `maxRetry`, `watchdogIntervalSec`. Row mồ côi trong bảng `AppSettings` — vô hại, không migration.

**Thêm:**

| Key | Type | Default | Validate | i18n label (EN/VI) |
|---|---|---|---|---|
| `pingIntervalSec` | int | 60 | 10..86400 | `Ping interval (s)` / `Chu kỳ ping (giây)` |
| `providerProbeTimeoutSec` | int | 60 | 1..600 | `Provider probe timeout (s)` / `Timeout probe provider (giây)` |
| `pingParkedProviders` | bool | false | — | `Auto-retry parked providers via ping` / `Tự retry provider đang chờ qua ping` |

**i18n key mới** (EN/VI set bằng nhau — giữ parity):

| Key | EN | VI |
|---|---|---|
| `settings.field.pingInterval` | Ping interval (s) | Chu kỳ ping (giây) |
| `settings.field.probeTimeout` | Provider probe timeout (s) | Timeout probe provider (giây) |
| `settings.field.pingParked` | Auto-retry parked providers via ping | Tự retry provider đang chờ qua ping |
| `settings.error.pingInterval` | Ping interval must be between 10 and 86400 seconds. | Chu kỳ ping phải trong khoảng 10–86400 giây. |
| `settings.error.probeTimeout` | Provider probe timeout must be between 1 and 600 seconds. | Timeout probe provider phải trong khoảng 1–600 giây. |
| `manualRetry.title` | Manual retry list | Danh sách retry thủ công |
| `manualRetry.action.retry` | Retry now | Retry ngay |
| `manualRetry.level.provider` | Provider | Provider |
| `manualRetry.level.account` | Account | Account |
| `manualRetry.level.model` | Model | Model |
| `manualRetry.reason.unauthorized` | Authentication failed | Xác thực thất bại |
| `manualRetry.reason.notFound` | Endpoint not found | Không tìm thấy endpoint |
| `manualRetry.reason.modelNotFound` | Model not found at provider | Model không tồn tại ở provider |
| `manualRetry.reason.unreachable` | Provider unreachable | Không kết nối được provider |
| `manualRetry.msg.unparked` | Removed from manual retry list | Đã gỡ khỏi danh sách retry thủ công |

Bỏ key cũ: `settings.field.maxRetry`, `settings.field.watchdog`, `settings.error.maxRetry`, `settings.error.watchdog` (cả EN lẫn VI).

### 3.7 UI — card danh sách trên Dashboard

- Card mới dưới card Status — **ẩn khi `GetEntries()` rỗng** (không cần empty state).
- Mỗi dòng: badge cấp (`manualRetry.level.*`) + tên entity + lý do (`manualRetry.reason.*`) + `ParkedAt.ToLocalTime()` + nút [Retry now].
  - Tên: Provider → `provider.Name`; Account → `account.Name` (resolve từ `IProviderService.ListAsync()` — đã Include Accounts); Model → `ModelId` nguyên bản.
  - Không resolve được entity (đã xóa khỏi DB) → hiện `#{Id}` / modelId kèm reason, nút Retry vẫn gỡ được entry (Unpark theo key, không cần entity).
- [Retry now] → `Unpark(...)` → toast `manualRetry.msg.unparked` + `Log.Info`.
- Subscribe `store.Changed` trong `OnInitialized`, unsubscribe trong `Dispose` (pattern `IProxyHost.StateChanged`).

### 3.8 Timeout

| Nơi | Hiện tại | Sau |
|---|---|---|
| `ConnectTimeout` handler `provider-probe` (MauiProgram) | 10s | **60s** |
| `ConnectTimeout` handler `free-model-sync` (MauiProgram) | 10s | **60s** |
| `ConnectTimeout` handler upstream chat (ProxyApp) | 10s | **60s** |
| `HttpClient.Timeout` client `provider-probe` | 10s hardcode | **setting `providerProbeTimeoutSec`, default 60s** — áp dụng per-request |
| Client `free-model-sync` total (30s), `ProxyEchoClient` (10s) | — | **không đổi** |

- Setting áp dụng qua `DelegatingHandler` đọc `IAppSettingsService` tại mỗi request (đổi setting có hiệu lực ngay, không rebuild client) — handler bọc call bằng linked CTS `CancelAfter(timeout)`.
- Client `provider-probe` phải đăng ký ở **cả 2 container**: MauiProgram (UI: test/fetch/metadata) và `ProxyApp.ConfigureServices` (proxy: ping service) — hiện chỉ có ở MauiProgram.

## 4. Error contract

| Tình huống | Status | Message (EN) | Type / Param / Code |
|---|---|---|---|
| Exhaustion, attempt cuối có HTTP response (kể cả `Fatal` 401/403/404) | **passthrough** nguyên response cuối | (body provider nguyên si) | — |
| Exhaustion, attempt cuối là mạng | 502 | `Upstream provider request failed` | `server_error` / null / null |
| Model đang parked — request mới (gate enqueue) | 503 | `The model '{model}' is temporarily unavailable` | `server_error` / null / null |
| Walk rỗng ngay (provider/model parked hoặc hết TK dùng được, chưa thử ai) | 503 | như trên | `server_error` / null / null |
| Lỗi không fatal (400/409/422...) | passthrough | (như 3A, không đổi) | — |
| Validate 400 / resolve 404 / no-key 503 | không đổi | (như cũ) | — |

Khác 3C: 401/403/404 từ upstream **không còn dừng walk ngay** — chúng park + advance; client thấy response cuối cùng của cả walk (đúng contract "park + vẫn failover" §1.3 #4).

## 5. Logging (VI — event mới/đổi)

| Event | Level | Nơi | Nội dung |
|---|---|---|---|
| Vào danh sách | Warn | store | `Đưa {level} '{key}' vào danh sách retry thủ công: {reason}` — log đúng 1 lần khi transition |
| Retry thủ công (UI) | Info | Dashboard | `'{key}' được retry thủ công — trở lại vòng xoay` |
| Phục hồi qua ping | Info | ping service | `Provider '{key}' phục hồi qua ping — tự gỡ khỏi danh sách` |
| Ping fail → park | Warn | ping service | `Ping provider '{key}' thất bại ({reason}) — đưa vào danh sách retry thủ công` |
| Advance failover | Warn | dispatcher | giữ nguyên (`Chuyển candidate kế...`) — Fatal cũng ghi như Retryable |
| Exhaustion | Error | dispatcher | giữ nguyên |

Log cũ bị xoá cùng store/watchdog: `Model '{id}' chuyển sang ManualRetry...`, `Probe model...`, `hết lượt probe...`, `Từ chối request mới: model ... đang ManualRetry` (giữ nhưng đổi sang wording không còn "ManualRetry" của store cũ — dùng "retry thủ công"). Không log body/key/messages.

## 6. Testing strategy & gates

### 6.1 Unit

- **`ManualRetryStore`** (thay `ModelHealthStoreTests`): Park/Unpark từng cấp; Park idempotent (giữ `ParkedAt`); `IsXxxParked`; `GetEntries`; event `Changed` bắn đúng; log transition 1 lần; thread-safety cơ bản.
- **Phân loại handler**: matrix §3.2 — 401/403→Account, 404+`model_not_found`→Model, 404→Provider, 401 body hỏng JSON vẫn Account, 404 body không parse được→Provider, mạng→Provider, 429/408/5xx→Retryable, 400/409→Passthrough, 2xx→Handled.
- **`DispatcherLoopTests`** (cập nhật): Fatal → park đúng cấp + advance đúng thứ tự; exhaustion sau Fatal → Passthrough response cuối / mạng → 502; filter provider/model parked + provider hết TK dùng được → 503 khi chưa thử ai; combo skip model parked; **không còn** `RecordFailure`/counter.
- **`ExecutionListTests`** (cập nhật): TryEnter bỏ TK parked (chọn TK còn lại); mọi TK parked → sentinel `0`; capacity/round-robin không đổi.
- **`ProviderPingServiceTests`** (thay `ModelHealthWatchdogTests`, fake `TimeProvider`): park khi 401/404/mạng; không park khi 429/5xx; mặc định bỏ qua provider đã park; `pingParkedProviders=true` → 2xx unpark / fail giữ park; skip Anthropic type; không log lặp khi vẫn fail.
- **Settings**: default + validate 3 key mới; 2 key cũ gỡ khỏi draft/validator; `DelegatingHandler` đổi timeout theo setting.

### 6.2 Integration (TestServer + fake upstream)

- Cập nhật `ProxyRetryIntegrationTests`: bỏ kịch bản tích lũy `MaxRetry`/503 sau N lỗi; thêm:
  - 401 provider A → failover provider B thành công (client 200) + account A vào danh sách.
  - 404 `model_not_found` → model vào danh sách → request exact-id kế bị **503** ngay.
  - Provider A network dead → provider A (mọi model) bị filter → request qua B vẫn serve.
  - Hết candidate (mọi response fatal) → client nhận **đúng response cuối** (passthrough 401/404...); toàn mạng → 502.
  - [Retry now] (gọi thẳng store) → request kế serve lại bình thường.
- Test passthrough 4xx khác / validate / resolve / no-key **giữ xanh**.

### 6.3 Gates cuối

- `dotnet test` toàn suite xanh (0 failed).
- `dotnet build router-balancing.slnx` + app `-f net10.0-windows10.0.19041.0` → 0 Warning / 0 Error.
- i18n parity test (EN set = VI set) xanh sau khi gỡ/thêm key.
- `npm run build` exit 0 (Dashboard card dùng utility class có sẵn — không CSS mới dự kiến).
- `git status` sạch; commit conventional tiếng Anh, mỗi task 1 commit.

## 7. Handoff

- **Slice Anthropic (3E)** sau này: thêm endpoint `/v1/messages` → khi đó cân nhắc hiển thị thêm URL không-`/v1` trên Dashboard.
- **FreeModelSync**: không đổi — nếu sau này sync fail muốn park, gọi `IManualRetryStore.Park(Provider, ...)` từ đó (seam đã sẵn).
- **Stats/error-rate** (nếu có sau): `Park`/`Unpark` là điểm natural để ghi counter.
- `RetryClassifier` giữ nguyên cho 429/408/5xx; `Fatal` phân loại riêng trong handler (không qua `RetryClassifier`).
