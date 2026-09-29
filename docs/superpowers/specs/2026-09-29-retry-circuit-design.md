# Spec: Retry, Circuit & Watchdog (Phase 3, Slice 3C)

- **Ngày:** 2026-09-29
- **Trạng thái:** Chờ user review
- **Slice:** 3C trong roadmap 5 slice Phase 3 (3A → 3B → 3C → 3D → 3E)
- **Điều kiện đầu:** 3B merge tại `44c84d5` (master, 273/0, e2e ALL PASS 12 checks)
- **Quyết định user khi brainstorm:** (1) một spec 3C đầy đủ 3 mảng, data layer; (2) merge 3B → master trước khi làm 3C; (3) kiến trúc dispatcher single-walk failover + circuit state machine (Phương án 1); (4) counter circuit +1 theo **exhaustion end-to-end**; (5) exhaustion contract = passthrough response cuối / 502; (6) probe = chat 1 token; (7) verify = unit + integration + e2e script.

## 1. Mục tiêu & phạm vi

Thêm **retry/failover khi lỗi**, **circuit breaker per-model** (lỗi liên tiếp → `ManualRetry` + watchdog auto-retry) và **xử lý 429 chủ động** (`Retry-After`) vào pipeline đã có của 3A+3B. Slice này **không đổi** streaming, validate, error contract 3A (trừ các dòng ghi rõ bên dưới), priority queue, selection.

### 1.1 Trong phạm vi (3C)

- `RetryClassifier` — 1 điểm phân loại lỗi retryable (429/408/5xx/network) vs non-retryable (4xx còn lại).
- **Failover walk** trong `DispatcherLoop`: lỗi retryable → advance sang candidate kế trong list thứ tự sẵn có (combo `Position` × thứ tự provider), **không requeue, không chờ backoff trong request**.
- **Exhaustion contract**: hết candidates → passthrough response cuối đã buffer (giữ "lỗi provider passthrough nguyên" của 3A); toàn lỗi mạng → 502 như 3A.
- **`ModelHealthStore`** (circuit per-model): +1 khi request **exhaustion** retryable, reset khi 2xx, đạt `MaxRetry` → `ManualRetry`.
- **Gate `ManualRetry`**: reject request MỚI 503 (message mới, xem §4); walk skip candidate thuộc model `ManualRetry`; request đang chờ trong queue vẫn dispatch (master spec: "ngừng nhận request mới").
- **`ModelHealthWatchdog`** (hosted service): tick `WatchdogIntervalSec`, probe model `ManualRetry` bằng chat tối thiểu 1 token; backoff `60s×n` (n ≤ `MaxRetry` theo Quyết định #11); hết lượt probe → ở lại `ManualRetry` (nút [Retry now] = UI slice sau).
- **429 chủ động**: parse `Retry-After` (delta-seconds + HTTP-date, clamp `0..3600s`) → floor cho `nextProbeAt`. **Không** chờ `Retry-After` trong request.
- **Không thêm settings key mới** — dùng `MaxRetry` + `WatchdogIntervalSec` sẵn có → parity i18n giữ **208/208**.

### 1.2 Ngoài phạm vi (rõ ràng — slice khác)

| Việc | Slice |
|---|---|
| UI: nút [Retry now], countdown probe, panel health model | slices UI sau (data layer 3C đủ) |
| Account weighting/quota, cập nhật `TokensUsed`/`RequestsUsed` | 3D |
| Dịch Anthropic, `/v1/responses` | 3E |
| Chờ backoff/retry cùng provider ngay trong request | ngoài phạm vi — đã chốt failover ngay (Phương án 1) |
| Expose `SetPriority` endpoint | slice sau |
| Hủy request đang chờ khi model chuyển `ManualRetry` | ngoài phạm vi — request cũ vẫn thử (§3.4) |

### 1.3 Các quyết định đã chốt (user gate)

1. **Phạm vi 3C đầy đủ 3 mảng** (retry/failover + 429 + circuit/watchdog), data layer — brainstorm answer "theo A".
2. **Merge 3B → master fast-forward trước** khi làm 3C (đã thực hiện `44c84d5`).
3. **Kiến trúc Phương án 1**: dispatcher single-walk failover (mỗi candidate thử đúng 1 lần, không chặn slot/vị trí queue chờ backoff) + circuit state machine riêng. Từ chối Phương án 2 (bọc retry `IUpstreamClient` — giữ slot quá lâu) và Phương án 3 (requeue — latency tệ cho chat interactive).
4. **Counter circuit +1 theo exhaustion end-to-end** (không từng attempt riêng lẻ) — diễn giải Quyết định #11 khớp user-visible behavior: request fail toàn bộ candidates mới tính, 2xx reset.
5. **Exhaustion contract**: attempt cuối là HTTP response → passthrough nguyên; attempt cuối là lỗi mạng → 502 `Upstream provider request failed`.
6. **503 message mới**: `The model '{model}' is temporarily unavailable`.
7. **Probe = chat tối thiểu** (`max_tokens:1`, không stream) thay vì `GET /models` — lỗi 429 quota chỉ lộ ra khi generation.
8. **Verify**: Unit + Integration (TestServer + fake upstream) + e2e script — như 3A/3B.

### 1.4 Giới hạn đã chấp nhận (documented, không sửa trong 3C)

- **Provider chết nhưng backup sống**: request luôn thành công qua failover → counter không tăng → circuit không mở (chỉ tốn 1 attempt fail mỗi request). Circuit dành cho **tổng outage**; mở/thủ công bằng tinct UI ở slice sau.
- **401/403 upstream (key sai)**: non-retryable → passthrough ngay, **không** cộng counter (Quyết định #11 chỉ đếm lỗi retryable). Xử lý key xoay vòng thuộc 3D.
- **Watchdog probe dừng sau `MaxRetry` lần** — không probe vô hạn (theo Quyết định #11 `60s×1 … 60s×MaxRetry`).

## 2. Architecture

### 2.1 Component mới (`src/RouterBalancing.Core/Engine/`)

| Component | DI | Responsibility |
|---|---|---|
| `RetryClassifier` (static) | — | `IsRetryable(HttpStatusCode)` → 429/408/5xx = true; còn lại false. Lỗi network/timeout classify bởi catch filter sẵn có của handler (3A) → signal retryable. |
| `IModelHealthStore` / `ModelHealthStore` (singleton) | Singleton | State per `modelId`: `Healthy` \| `ManualRetry(consecutiveFailures, attemptsMade, nextProbeAt)`. API cố định: `RecordSuccess(modelId)`, `RecordFailure(modelId)` (exhaustion), `IsManualRetry(modelId)` (model chưa từng lỗi = `false`), `GetManualRetryModels()`, `ScheduleProbe(modelId, nextAt)`; nội bộ 1 lock cho dict, thread-safe. |
| `ModelHealthWatchdog` (BackgroundService) | Hosted service | Tick `WatchdogIntervalSec`; quét models `ManualRetry` có `nextProbeAt ≤ now` → probe chat 1 token; 2xx → `RecordSuccess`; fail → `attemptsMade+1`, `nextProbeAt = now + 60s×attemptsMade`; hết `MaxRetry` → dừng probe (log Warn). Đồng hồ inject qua `TimeProvider` (test không chờ thật). |

Không thêm file settings mới; không thêm key i18n.

### 2.2 Thay đổi ở component có sẵn

**`DispatchOutcome`** — mở thêm 2 record:

- `Retryable(int? Status, string? ContentType, byte[] Body)` — tín hiệu nội bộ: `Status=null` ⇔ lỗi mạng (không có HTTP response). **Chỉ dispatcher nhìn thấy** — luôn được convert trước khi về endpoint.
- `Passthrough(int Status, string? ContentType, byte[] Body)` — endpoint ghi nguyên status + content-type + body (pass-through byte nguyên, không JSON wrap).

**`ChatCompletionsHandler.ForwardAsync`** — đổi một nhánh, giữ nguyên phần còn lại:

| Đầu ra | Trước (3A/3B) | Sau (3C) |
|---|---|---|
| 2xx (stream) | ghi stream → `Handled` | **giữ nguyên** → `Handled` |
| 4xx không retryable | ghi trực tiếp → `Handled` | trả `Passthrough` — endpoint ghi (quan sát được y hệt) |
| 429/408/5xx | ghi trực tiếp → `Handled` | **không ghi** — buffer body (response nhỏ, chưa commit) → `Retryable(status, ct, body)` |
| Network/timeout (đã catch sẵn) | `Error(502)` ngay | → `Retryable(null, null, [])` (502 chỉ sinh ở exhaustion) |
| No enabled key | `Error(503)` | **giữ nguyên** (fatal, không retry) |
| Client abort | propagate | **giữ nguyên** |

**`DispatcherLoop.ServeAsync`** — walk mở rộng (park/wake, `TryEnter`→`Take`, `Exit` trong `finally` **không đổi**):

1. Build candidate list từ 3B + **bỏ candidate thuộc model `ManualRetry`**.
2. Với mỗi candidate: `ForwardAsync` → nếu `Retryable` → `Exit`, log Warn, **advance candidate kế** (không requeue, không ngủ).
3. Kết thúc list: (a) list rỗng vì tất cả `ManualRetry` → outcome 503 §4; (b) attempt cuối `Retryable` có response → **`RecordFailure` từng model đã thử** rồi trả `Passthrough`; (c) attempt cuối là mạng → `RecordFailure` từng model đã thử rồi trả `Error(502)` (y như 3A).
4. `Handled` (2xx) → `RecordSuccess(model)` cho model vừa thành công (các model khác trong combo không đổi — chưa chứng minh gì).
5. `Error` fatal / `Cancelled` / `Aborted` → trả ngay như hiện tại (không `RecordFailure` — không phải lỗi retryable).

**`ProxyApp`**:

- **Gate enqueue**: sau validate, trước khi enqueue — `model` exact-id đang `ManualRetry` → ghi 503 §4 ngay (không vào queue). (Model combo chưa resolve lúc này — gate combo nằm ở bước 2 walk phía trên.)
- **Endpoint xử lý outcome mới**: `Passthrough` → ghi nguyên (giống hành vi passthrough 3A); `Retryable` **không bao giờ tới endpoint** (dispatcher convert — endpoint Debug-assert/default trả 500 như phòng thủ).
- Đăng ký `IModelHealthStore` Singleton + `AddHostedService<ModelHealthWatchdog>` (cùng chỗ với `DispatcherLoop`, `ProxyApp.ConfigureServices`).

### 2.3 Data flow

```
request → validate (400) → [exact-id ManualRetry? → 503] → RequestQueue (3B)
  → DispatcherLoop walk:
      skip candidate ManualRetry
      candidate i: TryEnter → ForwardAsync
        ├─ Handled(2xx stream)   → RecordSuccess(model) → kết thúc
        ├─ Passthrough(4xx fatal)→ trả endpoint ghi → (không đếm counter)
        ├─ Error/Cancelled/Aborted → trả endpoint (hành vi hiện tại)
        └─ Retryable             → Exit → log Warn → candidate i+1
      hết list:
        ├─ rỗng vì ManualRetry   → 503 §4
        ├─ attempt cuối có HTTP  → RecordFailure(các model đã thử) → endpoint ghi Passthrough
        └─ attempt cuối là mạng  → RecordFailure(các model đã thử) → endpoint ghi 502

ModelHealthStore: RecordFailure × N → đủ MaxRetry → ManualRetry
ModelHealthWatchdog: tick → probe chat 1 token → 2xx → RecordSuccess → Healthy
                                            └ fail → attemptsMade+1 → backoff 60s×n
429 có Retry-After → nextProbeAt = max(backoff, now + clamp(Retry-After))
```

## 3. Behavior

### 3.1 `RetryClassifier`

- `IsRetryable(status)`: **true** với 429, 408, 500–599; **false** với 2xx/3xx (không cần — chỉ gọi khi lỗi) và 4xx còn lại (400/401/403/404/409/410/422...).
- Lỗi mạng/timeout: classify bằng catch filter có sẵn trong `ForwardAsync` (`HttpRequestException`/`TaskCanceledException && !RequestAborted`) → `Retryable(null,...)`. Client-abort → **không** retry (propagate `Aborted`/`Cancelled` như hiện tại — tuyệt đối không nuốt `RequestAborted`).
- Không retry 401/403 (key sai) — xem §1.4.

### 3.2 Failover walk

- Thứ tự: danh sách candidates như 3B (combo `Position` × thứ tự provider từ selector) — **đã được filter bỏ model `ManualRetry`**.
- Mỗi candidate **thử đúng 1 lần**; retryable → advance ngay (không backoff, không giữ `ExecutionList` slot — `Exit` trước khi advance).
- **Không advance** khi: `Passthrough` (4xx không retryable — trả endpoint ngay, hành vi 3A), fatal `Error` (no-key, validate), `Cancelled`, `Aborted`, hay request bị park/full (hành vi park 3B giữ nguyên).
- Combo: failover đi hết các model theo thứ tự combo (candidate kế có thể là model khác).

### 3.3 Exhaustion contract

- **Attempt cuối có HTTP response** (429/408/5xx) → endpoint ghi **`Passthrough`: nguyên status + content-type + body response cuối** — client thấy đúng thứ sẽ thấy ở 3A. (Response 429/5xx đều nhỏ — buffer an toàn, chưa commit.)
- **Attempt cuối là lỗi mạng** → `Error(502, "Upstream provider request failed", "server_error", null, null)` — y như 3A.
- Quy tắc: **attempt cuối cùng quyết định** (không "response hay nhất trong walk").
- List rỗng ngay từ đầu vì tất cả model đều `ManualRetry` → 503 §4 (khác 404 — model tồn tại nhưng đang mở fuse).

### 3.4 Circuit per-model (`ModelHealthStore`)

- **State:** `Healthy` (default) | `ManualRetry(consecutiveFailures, attemptsMade, nextProbeAt)`. Không thêm trạng thái "degraded".
- **Counter:** `RecordFailure(modelId)` gọi khi một request **exhaustion retryable** — tăng `consecutiveFailures` của **từng model đã bị thử** trong walk đó (combo: nhiều model cùng +1). `RecordSuccess(modelId)` (2xx) → reset `consecutiveFailures = 0`, về `Healthy`, hủy `nextProbeAt`.
- **Mở fuse:** `consecutiveFailures ≥ MaxRetry` (setting sẵn, default 3) → `ManualRetry`.
- **Gate enqueue** (request mới): exact-id model `ManualRetry` → 503 trước khi vào queue.
- **Gate walk**: skip candidate model `ManualRetry` (request combo/request cũ vẫn chạy trên candidate khỏe mạnh còn lại).
- **Không hủy** request đang chờ trong queue khi fuse mở — chúng vẫn dispatch (đúng "ngừng nhận request mới").

### 3.5 Watchdog (`ModelHealthWatchdog`)

- Tick mỗi `WatchdogIntervalSec` (setting sẵn có).
- Mỗi model `ManualRetry` có `nextProbeAt ≤ now` → probe: `PostChatCompletionAsync(provider, key, body)` với body `{"model":...,"messages":[{"role":"user","content":"ping"}],"max_tokens":1,"stream":false}` — provider enabled đầu tiên của model (dùng key resolution hiện tại), qua `IUpstreamClient`.
- **2xx** → `RecordSuccess` (về `Healthy`), log Info.
- **Thất bại** (kể cả 4xx probe, network) → `attemptsMade+1`; `nextProbeAt = now + 60s × attemptsMade`; khi `attemptsMade ≥ MaxRetry` → **dừng probe** (ở lại `ManualRetry` vô hạn, log Warn "hết lượt probe tự động").
- **Backoff floor bởi `Retry-After`** (§3.6): `nextProbeAt = max(lịch 60s×n, now + retryAfterClamped)`.
- `TimeProvider` inject được — unit test điều khiển thời gian, không `Thread.Sleep` thật.

### 3.6 429 chủ động (`Retry-After`)

- Parse khi nhận 429 từ upstream (request thật hoặc probe): hỗ trợ **delta-seconds** và **HTTP-date**; clamp `0..3600s`; không có header/thông lệ → backoff mặc định.
- Dùng **duy nhất** để floor `nextProbeAt` (§3.5). **Không** chờ trong request (đã chốt Phương án 1) — request failover ngay sang candidate kế.
- Không có chỗ nào khác đọc `Retry-After` trong 3C (client vẫn nhận nguyên header qua passthrough exhaustion — 3A giữ nguyên).

## 4. Error contract

| Tình huống | Status | Message (EN, client-facing) | Type / Param / Code |
|---|---|---|---|
| Exhaustion, attempt cuối có HTTP response | **passthrough** nguyên response cuối | (body provider nguyên si) | — |
| Exhaustion, attempt cuối là mạng | 502 | `Upstream provider request failed` | `server_error` / null / null |
| Model `ManualRetry` — request mới (gate enqueue) | 503 | **mới:** `The model '{model}' is temporarily unavailable` | `server_error` / null / null |
| Walk rỗng vì tất cả model `ManualRetry` | 503 | như trên | `server_error` / null / null |
| Lỗi không retryable (4xx khác từ provider) | passthrough | (như 3A, không đổi) | — |
| Giữa stream 2xx lỗi | không retry, không ghi thêm (đã fix `44c84d5`, giữ nguyên) | — | — |
| Validate 400 / resolve 404 / no-key 503 | **không đổi** so với 3A | (như cũ) | — |

Mọi JSON lỗi sinh qua `WriteErrorAsync` (encoder relax giữ apostrophe — 3A). Message 503 mới là literal EN, **không qua i18n** (giống mọi message lỗi proxy).

## 5. Logging (VI — chỉ event mới)

| Event | Level | Nội dung (giả định) |
|---|---|---|
| Advance failover | Warn | `Chuyển candidate kế: '{provider}'/'{model}' lỗi retryable (HTTP {code})` cho requestId |
| Exhaustion | Error | `Request {requestId} thất bại sau {n} candidate — chuyển phản hồi cuối về client` |
| Mở fuse | Warn | `Model '{id}' chuyển sang ManualRetry sau {n} lỗi liên tiếp` |
| Reject enqueue | Warn | `Từ chối request mới: model '{id}' đang ManualRetry` |
| Probe fail | Warn | `Probe model '{id}' thất bại (lần {k}/{max}) — thử lại sau {sec}s` |
| Hết lượt probe | Warn | `Model '{id}' hết lượt probe tự động — chờ Retry now (slice UI)` |
| Recover | Info | `Model '{id}' phục hồi — trở lại Healthy` |

Không log body/key/messages (nguyên tắc 3A).

## 6. Testing strategy & gates

### 6.1 Unit

- **`RetryClassifier`**: matrix 429/408/500/502/503/504 = retryable; 400/401/403/404/409/422 = không; network/timeout = retryable-null; client-abort ≠ retryable.
- **`ModelHealthStore`**: +1 theo exhaustion, reset khi success, mở fuse tại đúng `MaxRetry`, thread-safety cơ bản (nhiều model/lock), `ScheduleProbe`/backoff math `60s×n`, dừng ở `MaxRetry` probe.
- **Dispatcher walk** (mở rộng `DispatcherLoopTests`): retryable → advance đúng thứ tự combo/provider; fatal → không advance; exhaustion → `Passthrough` với response cuối; exhaustion toàn mạng → 502; list rỗng vì ManualRetry → 503; 2xx giữa chừng → `RecordSuccess` đúng model.
- **`Retry-After` parser**: delta-seconds, HTTP-date, clamp 0..3600, header thiếu/thông lệ.
- **Watchdog** với fake `TimeProvider`: probe theo lịch, backoff floor Retry-After, dừng sau MaxRetry probe, recover 2xx.
- **Unit test passthrough 3A** (429/5xx): cập nhật assertion theo outcome mới (`Retryable`/`Passthrough` thay vì ghi trực tiếp trong handler) — **quan sát client không đổi** (integration/e2e giữ xanh, §6.2).

### 6.2 Integration (TestServer + fake upstream)

- 429 provider A → **thành công provider B** trong cùng 1 request (failover, client chỉ thấy 200).
- Cả 2 provider retryable fail → passthrough 429/response cuối nguyên vẹn; cả 2 network fail → 502.
- Tích lũy `MaxRetry` exhaustion → request mới exact-id bị **503** ngay (gate enqueue) trong khi request cũ trong queue vẫn dispatch.
- Combo có 1 model `ManualRetry` → walk skip, chạy model kế.
- Probe recovery: fuse mở → probe 2xx (fake time) → request mới đi lại bình thường.
- Test 3A passthrough (429/5xx, single candidate) **giữ xanh** — exhaustion → passthrough → client thấy y hệt.

### 6.3 e2e

- `scripts/e2e-3c.sh` + `mock-upstream.mjs` mở rộng failure mode stateful (fail N lần đầu → OK): failover thành công, exhaustion passthrough, 503 gate. Gate: ALL PASS.

### 6.4 Gates cuối slice

- `dotnet test` toàn suite xanh (baseline **273** + test mới, 0 failed).
- `dotnet build -f net10.0-windows10.0.19041.0` → **0 Warning / 0 Error**.
- Parity i18n **208/208** (không thêm key).
- `git status` sạch; commit conventional tiếng Anh, mỗi task 1 commit (SDD như 3A/3B).

## 7. Handoff sang slice sau (điều 3D/3E cần biết)

- **3D (account):** `IUpstreamClient` vẫn là seam đổi key resolution (3A/3C đều wrap quanh seam này — 3C thêm watchdog cũng gọi qua nó); `IModelHealthStore.IsManualRetry(modelId)` là signal availability cho selection; `ProxyRequest` đã mang request id để trace usage.
- **3D/3E (stats):** `RecordSuccess`/`RecordFailure` là điểm natural để cộng `RequestsUsed`/error-rate stats sau này.
- **UI Runtime panel:** đọc state từ `IModelHealthStore` (cần bổ sung read-only snapshot API nếu chưa đủ — thêm khi làm UI); nút [Retry now] gọi `RecordSuccess`-style reset hoặc method `ForceHealthy` riêng (chưa có trong 3C — YAGNI đến khi UI).
- **Queue in-memory:** `ManualRetry` không ảnh hưởng persistence (không có persistence — 3B).
