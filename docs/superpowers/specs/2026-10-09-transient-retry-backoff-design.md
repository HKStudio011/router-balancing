# Spec: Transient Retry Backoff cùng Account (dispatcher)

- **Ngày:** 2026-10-09
- **Trạng thái:** Approved (user "ok" tại `54459d5`)
- **Nguồn gốc:** Nghiên cứu cách Hermes/OpenCode xử lý 504 NVIDIA — retry transient với backoff + jitter trước khi failover; chốt các quyết định khi brainstorm (mục 1.3).
- **Điều kiện đầu:** master tại `11a7f5d` — working tree đang có thay đổi khác (live-trace markers, v1-responses) **không** thuộc slice này; plan phải tách task, không trộn.

## 1. Mục tiêu & phạm vi

### 1.1 Vấn đề

Dispatcher hiện failover **ngay lập tức** sau mỗi lỗi retryable: 504/503/502 (NVIDIA NIM hay gặp — gateway timeout phía provider, tự phục hồi trong vài giây) khiến request rotate TK/model ngay, dù thử lại đúng endpoint đó 1–2 lần có thể đã thành công. Mục tiêu: thêm **retry backoff cùng (provider, model, account)** cho lỗi transient **trước khi** rotate/advance, theo mô hình Hermes ("transport error → retry vài lần rồi mới fallback").

### 1.2 Trong phạm vi

- **Nhánh retry trong `DispatcherLoop.ServeAsync`**: outcome `Retryable` transient + ngân sách còn → `Task.Delay` (jitter, hủy được) giữ nguyên slot/candidate/account → gọi `ForwardAsync` lại.
- **`TransientRetries`** trên `RetryState` — ngân sách **per-request** (sống qua park, không reset).
- **`BackoffPolicy`** (static): `wait(n) = min(baseMs × 2^(n-1), 4000ms) + jitter [0..25%]`.
- **2 settings key mới** + Settings UI (2 ô number) + 4 i18n key × 2 ngôn ngữ.
- **Không đổi**: error contract, streaming, validate, queue/park, selection, journal/trace semantics (retry là attempt thật, ghi bình thường).

### 1.3 Quyết định đã chốt (user gate)

1. **Phạm vi lỗi**: transient = HTTP `null` (lỗi mạng/timeout), `408`, `500–599` (gồm 504). **429 KHÔNG retry backoff** — rotate TK ngay như hiện tại.
2. **Backoff**: giữ slot trong lúc chờ; **5 lần retry** mặc định, **base 1000ms**, ×2 mỗi lần, **cap 4000ms/lần**, jitter 25% → worst-case tích lũy **~15s (~18.75s với jitter)**/request — trần hiếm gặp, chấp nhận.
3. **Settings làm UI chỉnh được** (không hardcode): `transientMaxRetries` (0..10, default 5; 0 = tắt), `transientBackoffBaseMs` (250..4000, default 1000).
4. **Phương án A — dispatcher** (không phải retry trong handler): ngân sách per-request chặt về latency, retry hiện trong Live Trace; từ chối Phương án B (handler-internal — ngân sách per-account, ~11s+ khi outage, retry mù trong trace).
5. Park giữ nguyên (không "re-park chờ backoff" — đã loại từ đầu).

### 1.4 Ngoài phạm vi

| Việc | Ghi chú |
|---|---|
| Retry-After cho 429 trong request | 429 không thuộc transient; đã chốt không chờ |
| Hiển thị "đang chờ retry" trên Live Trace UI | gap timestamp giữa 2 Attempt event đã thể hiện; marker riêng = slice UI sau |
| Đổi error contract / response trả client | không đổi — client chỉ thấy latency |
| Thêm delay seam `IDelay` để mock | YAGNI — test dùng base 250ms (min range) |

## 2. Architecture

### 2.1 Component mới

| Component | DI | Responsibility |
|---|---|---|
| `BackoffPolicy` (static, `Engine/`) | — | `Delay(int retryNumber, int baseMs, Random rng)` → `min(baseMs × 2^(n-1), 4000) + uniform[0, 25%]` ms. RNG inject qua tham số để unit test deterministic; overload mặc định dùng `Random.Shared`. n ≤ 1 → base (không nhân). |

### 2.2 Component đổi

**`RetryState`** — thêm:

```csharp
/// <summary>Số lần retry transient đã dùng trong request (per-request, sống qua park).</summary>
public int TransientRetries { get; set; }
```

**`SettingsKeys` / `IAppSettingsService` / `AppSettingsService`** — 2 key + 2 property:

| Key | Property | Default | Validator |
|---|---|---|---|
| `transientMaxRetries` | `TransientMaxRetries` | 5 | int 0..10 |
| `transientBackoffBaseMs` | `TransientBackoffBaseMs` | 1000 | int 250..4000 |

**`SettingsValidator`** — range như trên; **`SettingsDraft`** + trang Settings: 2 ô number + label/mô tả i18n.

**`DispatcherLoop`** — inject thêm `IAppSettingsService` (đọc **tại mỗi quyết định retry** — chỉnh settings có hiệu lực ngay, theo pattern `ProviderProbeTimeoutHandler`); 1 nhánh mới trong `ServeAsync` (§3.2).

**`Translations`** — 4 key mới vào đủ `English` + `Vietnamese`: `Settings_TransientMaxRetries`, `Settings_TransientMaxRetriesDesc`, `Settings_TransientBackoffBaseMs`, `Settings_TransientBackoffBaseMsDesc`.

Không thêm file settings mới, không entity DB, không endpoint.

### 2.3 Data flow

```
attempt i: ForwardAsync → journal (RecordAttempt + Trace done) + LastFailure
  → [MỚI] Retryable && IsTransient && Retry.TransientRetries < setting:
      TransientRetries++
      log Info "retry cùng TK sau {wait}ms"
      await Task.Delay(wait, RequestAborted)   ← slot vẫn giữ
        ├─ OCE (client abort) → Exit slot → complete Aborted → return
        └─ xong → continue vòng serve → attempt i+1, CÙNG provider/model/account
  → không phải transient / hết ngân sách → routing cũ (không đổi):
      Account → MarkAccountTried → Exit → TryEnter TK kế
      Model/Provider (Fatal) → MarkTried → candidate-advance
      hết list → CompleteExhaustion
```

## 3. Behavior

### 3.1 `IsTransient(int? status)`

- `true`: `status is null` (lỗi mạng/timeout — catch filter 3A), `408`, `500..599`.
- `false`: `429` (và mọi 4xx khác — nhưng 4xx không retryable đã thành `Fatal`/`Passthrough` từ handler, nên về đây chỉ có 429).
- Đặt trong **`RetryClassifier`** (static `IsTransient(int? status)`) — giữ nguyên cam kết "1 điểm duy nhất phân loại lỗi retryable", unit test cùng chỗ `RetryClassifierTests`.

### 3.2 Nhánh retry (đặt SAU journal, TRƯỚC nhánh `level` routing)

```csharp
if (outcome is DispatchOutcome.Retryable r
    && IsTransient(r.Status)
    && request.Retry.TransientRetries < settings.TransientMaxRetries)
{
    request.Retry.TransientRetries++;
    var wait = BackoffPolicy.Delay(request.Retry.TransientRetries, settings.TransientBackoffBaseMs);
    LogAttemptFail(request, candidate, accountId, attemptBudget, $"chờ {wait.TotalMilliseconds:0}ms retry");
    try { await Task.Delay(wait, request.Context.RequestAborted); }
    catch (OperationCanceledException)
    {
        executions.Exit(request.Id);
        request.Completion.TrySetResult(new DispatchOutcome.Aborted());
        return;
    }
    continue; // vòng serve — publish Attempt start mới (attemptNo++), cùng route
}
```

- Slot giữ nguyên trong lúc chờ (đã chốt) — request khác thấy `Full` → park bình thường (hành vi 3B không đổi).
- Abort trong delay: mirror nhánh abort hiện có — `Exit` + `Aborted`, không nuốt `RequestAborted`.
- Journal/trace: lần retry là attempt thật — `attemptNo = Attempts + 1`, route lặp (cùng TK) → Live Trace dot lặp trên cùng nhánh; `ApiMonitorStore.RecordError` upsert theo RequestId → attempt cuối thắng.

### 3.3 Backoff math

- `wait(n)` với n = `TransientRetries` **sau khi ++** (1-based): `min(baseMs × 2^(n-1), 4000)` rồi + jitter `uniform[0, 0.25 × raw]`.
- Mặc định (base 1000): 1s → 2s → 4s → 4s → 4s = **15s** raw, ≤ ~18.75s với jitter.
- Settings `transientMaxRetries = 0` → nhánh `if` không bao giờ chạy → **hành vi y hệt code hiện tại** (back-compat).

### 3.4 Không đổi (cam kết)

- Error contract (`§4` spec 3A/exhaustive-failover): client không phân biệt retry.
- 429 → rotate TK ngay; `Fatal` (401/403/404) → cấp failover cũ; `Handled`/`Passthrough`/`Error`/`Cancelled`/`Aborted` không qua nhánh mới.
- Stream 2xx lỗi giữa chừng → không retry (xử lý cũ, outcome `Aborted`/`Error`).
- Exhaustion: attempt cuối quyết định passthrough/502 — retry làm tăng `Attempts` nên log "k/N" phản ánh đúng số call thật.

## 4. Error contract

**Không có thay đổi.** Mọi tình huống giữ nguyên response hiện hành (retry chỉ trì hoãn thời điểm failover/passthrough).

## 5. Logging (VI — event mới)

| Event | Level | Nơi | Nội dung |
|---|---|---|---|
| Retry chờ | Info | dispatcher | reuse `LogAttemptFail` — `Request {id} — attempt {k}/{n} fail: ... — chờ {ms}ms retry ({t}/{max})` |

Không log body/key/messages (nguyên tắc 3A).

## 6. Testing strategy & gates

### 6.1 Unit

- **`BackoffPolicyTests` (mới)**: progression 1000→2000→4000→4000 (cap từ n=3), jitter ∈ [0; 25%], RNG seed deterministic, n ≤ 1.
- **`RetryStateTests`**: `TransientRetries` tăng/reset mặc định 0.
- **`DispatcherLoopTests`** (mở rộng; test set base=250ms để nhanh):
  1. 504 → retry cùng (provider, model, account) → 200 ⇒ client 200, `Attempts=2`;
  2. 504 liên tục quá ngân sách → rotate TK/model như hiện tại, exhaustion passthrough cuối;
  3. **429 → rotate TK ngay, không retry cùng TK**;
  4. `transientMaxRetries=0` → y hệt hành vi hiện tại;
  5. abort trong delay → `Aborted` + slot đã trả (`ExecutionList.Contains=false`);
  6. slot vẫn giữ trong delay (request khác `Full` → park);
  7. lỗi mạng (status null) → retry; `Fatal` 401 → không retry.
- **`SettingsValidatorTests`**: range 0..10 / 250..4000, default 5/1000.
- **`TranslationParityTests`**: tự enforce 4 key × 2 ngôn ngữ.

### 6.2 Integration (TestServer + fake upstream)

- Upstream 504 ×2 rồi 200 → client 200; monitor row cuối = 200 (upsert).
- 504 vĩnh viễn → passthrough response cuối nguyên vẹn (hành vi exhaustion giữ nguyên).
- Trace: attempt events lặp **cùng route** cho 1 request retry.

### 6.3 e2e

- Mở rộng `mock-upstream.mjs`: mode "504 N lần đầu rồi OK" (đã có pattern fail-N-rồi-OK) → failover-backoff thành công; gate ALL PASS.

### 6.4 Gates cuối slice

- `dotnet test` toàn suite xanh (baseline + test mới, 0 failed).
- `dotnet build -f net10.0-windows10.0.19041.0` → 0 Warning / 0 Error.
- i18n parity (test tự enforce).
- `git status` sạch (chỉ các file thuộc slice); commit conventional tiếng Anh, mỗi task 1 commit.

## 7. Handoff / rủi ro

- **Working tree dang dở** (live-trace markers, v1-responses): plan phải branch/task tách khỏi những thay đổi đó — không commit lẫn.
- **Live Trace UI sau này** có thể thêm marker "đang chờ retry" (đọc gap giữa 2 Attempt event) — ngoài slice này.
- `Attempts` tăng thêm do retry → mọi reader log "k/N" (exhaustion Details) đã được thiết kế theo số call thật, không cần đổi.
