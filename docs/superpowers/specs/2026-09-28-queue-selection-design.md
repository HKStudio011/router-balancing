# Spec: Queue & Selection (Phase 3, Slice 3B)

- **Ngày:** 2026-09-28
- **Trạng thái:** Chờ user review
- **Slice:** 3B trong roadmap 5 slice Phase 3 (3A → 3B → 3C → 3D → 3E)
- **Điều kiện đầu:** 3A merge tại `04071ab` (master, 226/0, e2e ALL PASS)

## 1. Mục tiêu & phạm vi

Dựng **engine cốt lõi** của bộ cân bằng tải: priority queue toàn cục + dispatcher + chọn model (RR/Fallback, combo nested) + giới hạn đồng thời theo provider + API huỷ/snapshot request. Pipeline streaming/error/logging của 3A giữ nguyên — slice này chèn hàng đợi và selection **vào giữa** validate và upstream.

### 1.1 Trong phạm vi (3B)

- `RequestQueue` priority 3 mức + luật **1-Highest** + header `X-Priority`.
- `DispatcherLoop` 1 task async: peek → resolve (combo/model) → chọn model → acquire slot → chạy handler; **park vô hạn** khi thiếu capacity (item **ở lại queue**, không dequeue), wake bằng event (không polling).
- `ExecutionList`: đếm in-flight theo provider, enforce `MaxConcurrent`, track request id — data source cho snapshot.
- Chọn model: **RR** (least-loaded, cursor tie-break, skip đầy) cho model thường; **Fallback** theo thứ tự combo (`Position`); combo **nested recurse** + cycle-guard.
- API: `GET /v1/requests` (snapshot) + `POST /v1/requests/{id}/cancel` (chỉ huỷ request còn trong queue).
- `X-Request-Id` (id ngắn **sinh lúc request vào endpoint, trước validate**) trong response header + log + snapshot.

### 1.2 Ngoài phạm vi (rõ ràng — slice sau)

| Việc | Slice |
|---|---|
| Retry / failover khi lỗi / circuit / 429 | 3C |
| Chọn account theo Weight/Priority/quota, `TokensUsed`/`RequestsUsed` | 3D |
| Dịch Anthropic Messages ↔ OpenAI, `/v1/responses` | 3E |
| UI: Runtime panel, nút huỷ / đổi priority trên màn hình | slices UI sau (data layer 3B đã đủ) |
| Endpoint đổi priority runtime (`SetPriority` có sẵn ở service, chưa expose) | khi UI cần |

### 1.3 Các quyết định đã chốt (user gate)

1. Đi **đầy đủ master spec** (queue + dispatcher + park/wake + Execution List + `X-Priority`) — đây là phần cốt lõi của app.
2. Thiếu capacity → **park vô hạn** (client tự ngắt bằng disconnect); không timeout queue, không `MaxQueueLength`.
3. **Huỷ request** là tính năng của 3B nhưng **chỉ engine + API**; nút bấm UI ở slice Runtime panel sau.
4. Contract huỷ: request còn queue → client gốc nhận **400** + `code:"request_cancelled"`; **request đang phục vụ KHÔNG cho huỷ** (đơn giản hoá — bỏ phương án hard-close giữa stream).
5. Kèm **snapshot endpoint** (`GET /v1/requests`) để bên huỷ biết id — đồng thời là data source cho Runtime panel sau.
6. Slot `MaxConcurrent` tính **theo Provider** (mọi model của provider dùng chung pool).
7. Kiến trúc: **Approach 1 — Queue-first dispatcher** (literal master spec).

## 2. Architecture

### 2.1 Component mới (`src/RouterBalancing.Core/Engine/`)

| Type | Responsibility | Phụ thuộc |
|---|---|---|
| `IRequestQueue` / `RequestQueue` | 3 mức `Normal(0)<High(1)<Highest(2)`; key `(Priority, Sequence)` — Sequence tăng dần toàn cục, FIFO trong cùng priority; `Enqueue` / `Peek` / `Take(id)` / `TryRemove(id)` / `SetPriority` / `Snapshot`; event `Changed`; enforce **1-Highest** tại đây | — |
| `IExecutionList` / `ExecutionList` | `ConcurrentDictionary<providerId, state>`: đếm in-flight, enforce `provider.MaxConcurrent` bằng `TryEnter(provider, request)`/`Exit`; **`CanEnter(provider)` check không mutate (cho selector)**; track `{id, model, provider, startedAt}`; event `Exited` (= wake cho dispatcher); `Snapshot()` | `Provider.MaxConcurrent` (query tại Enter — giá trị mới nhất từ DB) |
| `IDispatcher` / `DispatcherLoop` (hosted service) | Vòng lặp: chờ `Queue.Changed` (queue rỗng) hoặc `Queue.Changed \| ExecutionList.Exited` (đầu queue kẹt) → **Peek** đầu queue → resolve + chọn → capacity đủ: `TryEnter` → **`Take` atomic** (Take fail = vừa bị huỷ/abort → `Exit` ngay + bỏ qua); capacity thiếu: **không Take, item ở lại queue** + chờ event → chạy handler task con → finally `Exit`. Exception từng vòng → `Log.Error` + tiếp (service không chết) | `IRequestQueue`, `IExecutionList`, `IModelSelector`, `IComboResolver`, `ChatCompletionsHandler` |
| `IComboResolver` / `ComboResolver` | `model` string → kết quả resolve phân loại (§3.2): model id thật **ưu tiên**, không có thì tra `Combo.Name` (items recurse, cycle-guard `visited` set) | `IDbContextFactory` |
| `IModelSelector` / `ModelSelector` | Danh sách candidate đã resolve → chọn theo §3.3 (RR / Fallback) với ràng buộc capacity từ `IExecutionList` | `IExecutionList` |
| `ProxyRequest` | Đại diện 1 request trong queue: `Id` (8 ký tự base36, **do endpoint sinh trước validate**, retry nếu trùng), `Priority`, `EnqueuedAt`, `Model` (chuỗi gốc), `HttpContext`, `TaskCompletionSource<DispatchOutcome>`; đăng ký `RequestAborted` callback | — |
| `DispatchOutcome` (enum) | `Handled` (handler đã ghi response) / `Error(status, payload)` (endpoint ghi JSON) / `Cancelled` (endpoint ghi 400 `request_cancelled`) / `Aborted` (không ghi gì) | — |

### 2.2 Thay đổi ở component có sẵn

- **`ChatCompletionsHandler`** — tách responsibility:
  - **Validate chuyển ra endpoint** (nguyên văn rule + payload + log như 3A — lỗi validate trả lời **ngay, không qua queue**).
  - Handler giữ phần **cần slot**: giải mã key → upstream → copy response → log. Entry mới (tên chi tiết plan chốt): nhận `HttpContext` + kết quả selection đã có target.
  - Viết lỗi JSON (`WriteErrorAsync`, `ErrorJsonOptions`) tái dùng cho endpoint (resolve-failure + cancel) — accessiblity do plan chốt (static helper hoặc service nhỏ).
- **`ModelResolver` (3A)** — trả 1 provider (Id nhỏ nhất) bị thay bằng `ComboResolver` trả **tất cả candidate**; query giống 3A nhưng `Include(Accounts)` + giữ đúng filter (provider `Enabled`, model `Enabled`). Cũ xóa/thay trong plan; 226 test cũ điều chỉnh tương ứng (hành vi client **không đổi**).
- **Endpoint chat** (`ProxyApp`): sinh id + set `X-Request-Id` header → validate (fail → 400 ngay như 3A, header đã có) → sinh `ProxyRequest` → enqueue → await TCS → switch `DispatchOutcome` (Handled = đã xong, Error = ghi payload, Cancelled = ghi 400 cancel, Aborted = return).
- **`ProxyApp.ConfigureServices`**: đăng ký singletons queue/execution list/selector/resolver + `AddHostedService<DispatcherLoop>`.
- **`ProxyApp.ConfigurePipeline`**: thêm `GET /v1/requests`, `POST /v1/requests/{id}/cancel` (cùng auth policy với route chat — middleware đã chạy trước map).

### 2.3 Data flow

```
client POST chat
  → sinh id + X-Request-Id header
  → validate (endpoint, fail → 400 ngay như 3A)
  → enqueue ProxyRequest (priority từ X-Priority)
  → await TCS ─────────────────────────────────────────┐
dispatcher:                                             │
  Peek → ComboResolver → ModelSelector (capacity)      │
  → ExecutionList.TryEnter → Take atomic               │
  → ChatCompletionsHandler (key → upstream → stream)    │
  → finally ExecutionList.Exit → TCS.Complete ──────────┘
  → endpoint ghi (nếu Outcome cần) → về client
```

## 3. Behavior

### 3.1 Priority & 1-Highest

- Header `X-Priority: high|max` (case-insensitive, trim); giá trị lạ/không có → `Normal` (**lenient**, không 400).
- Luật **1-Highest**: `Enqueue` mức `Highest` → mọi item đang ở `Highest` downgrade xuống `High` (chỉ trong `RequestQueue`, atomic với enqueue).
- `SetPriority(id, priority)` có sẵn mặt service-level + unit test cho UI sau; **3B không expose endpoint**.
- FIFO trong cùng priority theo `Sequence`.

### 3.2 Resolve model/combo

Thứ tự tra (trả về cái khớp đầu tiên):

1. **Model id thật** (query model `Enabled` + provider `Enabled` như 3A) → candidate list. Giữ nguyên semantics 3A: tất cả candidate là provider `Anthropic` → **503** `server_error` + log Warn; không model nào khớp → sang bước 2.
2. **`Combo.Name`** → items theo `Position`, mỗi item: `TargetModelId` → candidate list (bỏ qua model disabled/không tồn tại), `TargetComboId` → recurse với `visited` set (**cycle: bỏ qua item gây cycle**, log Warn — validate lúc lưu đã chặn, đây là defense); item không ra được gì → bỏ; **cả combo không có candidate nào → 404** `model_not_found`.
3. Không model lẫn combo → **404** `model_not_found` (payload y như 3A).

Không candidate `OpenAI`-type nào (sau khi filter) mà có candidate `Anthropic` → 503 (giữ 3A). Candidate list cuối: filter provider `Enabled` + type OpenAI, sort `(ProviderId, ModelId)` — thứ tự ổn định cho RR.

### 3.3 Chọn model (khi dispatch)

Input: candidate list (đã resolve), mode:

- **RR** (model thường; combo mode `RoundRobin`):
  1. Filter candidate còn slot (`CanEnter` — check không mutate). Rỗng → **park** (item không Take, chờ `Queue.Changed | ExecutionList.Exited`).
  2. Ưu tiên `in-flight == 0`; kế tiếp `in-flight` thấp nhất; tie-break: **cursor RR** (toàn cục, tăng sau mỗi lượt dispatch thành công, wrap quanh) rồi `ProviderId` asc (định danh xác định để test).
  3. Chọn xong → `TryEnter` thật → **`Take` atomic**; `Take` fail (item vừa bị huỷ/abort) → `Exit` ngay + bỏ qua vòng này.
- **Fallback** (combo mode `Fallback`): lấy candidate **đầu tiên** theo thứ tự `Position` (nested inline theo thứ tự từng cấp; eligible = có trong kết quả resolve, **không** filter theo capacity); candidate đó đang đầy → **park chờ đúng nó** (không nhảy sang kế — failover chỉ khi *lỗi*, phần 3C).

- **Park:** item **không được Take** — nằm nguyên tại vị trí trong queue (sequence giữ nguyên, không starvation); dispatcher chờ `Queue.Changed | ExecutionList.Exited`. Huỷ/abort trong lúc park vẫn `TryRemove` bình thường (không có race "item không nằm ở đâu").
- **Dispatch:** `Take` thành công = **đang phục vụ** — không còn huỷ được.

### 3.4 Huỷ request

- `POST /v1/requests/{id}/cancel`:
  - id trong queue → `TryRemove` atomic → TCS.Complete(`Cancelled`) → **200** `{"cancelled":true}`.
  - id đang serving (không có trong queue nhưng có trong `ExecutionList`) → **409** + lỗi OpenAI JSON `code:"not_cancellable"`.
  - id không tồn tại ở cả 2 → **404** + `code:"request_not_found"`.
  - Race `Take`-vs-cancel: `TryRemove` false (đã Take) → rơi vào nhánh serving/404 — không bao giờ hai bên cùng lúc "thắng"; `Take` false (đã bị remove) → dispatcher bỏ qua.
- Endpoint gốc (đang await) nhận `Cancelled` → ghi **400** `{"error":{"message":"Request cancelled.","type":"invalid_request_error","code":"request_cancelled"}}` (chưa gửi header nên ghi được) + log `Info` kèm id.
- `RequestAborted` khi còn trong queue → `TryRemove` + log `Info` Cancelled, **không ghi gì** (client đã ngắt). Khi đang serving → hành vi 3A giữ nguyên (propagate abort, không 502).
- Huỷ một id đã bị abort gỡ khỏi queue → 404 (không còn ở đâu).

### 3.5 Snapshot

`GET /v1/requests` → 200:

```json
{"requests":[{"id":"k3j9x2a1","state":"queued","priority":"normal",
  "model":"gpt-4o-mini","provider":null,"enqueuedAt":"2026-09-28T12:00:00Z",
  "startedAt":null,"elapsedMs":1234,"cancelable":true}]}
```

- `state`: `queued` (từ queue) | `serving` (từ `ExecutionList`); `provider` null khi queued; `elapsedMs` = từ `enqueuedAt` (queued) hoặc `startedAt` (serving); `cancelable` = `state=="queued"` (kiểm tra lại qua `TryRemove`-side khi huỷ).
- Trình tự: lấy snapshot của 2 collection trong 1 lần gọi, không đồng bộ hoá chặt giữa 2 nguồn — client tự hiểu id có thể vừa chuyển state (huỷ cạnh tranh đã xử lý bằng 404/409).

### 3.6 `X-Request-Id`

- Sinh lúc request vào endpoint (**trước validate** — để cả validate-error cũng có id): 8 ký tự base36 (a–z, 0–9), retry nếu va chạm trong queue + execution list.
- Set `Response.Headers["X-Request-Id"]` ngay tại endpoint **trước mọi write** (mọi response chat — kể cả validate-error 400 — đều kèm header).
- Xuất hiện trong log message các event mới.

## 4. Logging (VI — chỉ event mới)

| Event | Level | Message (ý nghĩa) |
|---|---|---|
| Huỷ request đang chờ | `Info` | `Đã huỷ request {id} (đang chờ), model {model}.` |
| `RequestAborted` khi còn trong queue | `Info` | `Request {id} bị client ngắt khi đang chờ.` |
| Cycle phát hiện lúc recurse | `Warn` | `Combo {name} có vòng lặp - bỏ qua item {itemId}.` |
| Dispatcher exception (mỗi vòng) | `Error` | `Lỗi dispatcher: {ex.Message}` — vòng lặp tiếp tục |

Không log per-request cho enqueue/dequeue (đã có log 3A); không log body. Log resolve-failure (404/503) + warn Anthropic: **giữ nguyên message 3A**, chỉ đổi nơi gọi (handler → dispatcher).

## 5. Testing strategy

Unit (tên mô tả hành vi, gate số test plan chốt):

- `RequestQueue`: FIFO trong mức; thứ tự 3 mức; **1-Highest downgrade**; `TryRemove` true/false; `SetPriority` (kể cả hiện tại Highest); item ở lại queue nguyên vẹn khi park (sequence giữ).
- `ExecutionList`: `TryEnter` chặn vượt `MaxConcurrent`; `Exit` bắn event `Exited`; snapshot entries đủ field.
- `ComboResolver`: model id precedence > combo; combo nested 2 cấp đúng thứ tự; cycle → bỏ item + không treo; skip model/provider disabled; all-anthropic → 503; 404 khi không khớp gì.
- `ModelSelector`: RR ưu tiên in-flight=0 rồi load thấp rồi cursor; skip đầy; Fallback lấy đúng vị trí `Position`, đầy vị trí đầu → park (không nhảy).
- `Dispatcher`: wake đúng event (dùng stub event, không polling — assert không có vòng lặp đọc capacity lặp lại); race huỷ vs `Take` (một bên thắng, không có item "lơ lửng"); `RequestAborted` queued; exception trong vòng không giết service.
- Endpoint: snapshot shape; cancel 200/404/409; `X-Priority` parse (high/max/lạ); `X-Request-Id` có trên mọi response chat.
- `ChatCompletionsHandler` (điều chỉnh test 3A): entry mới chỉ phần upstream.

Integration (TestServer + mock upstream, seam như 3A):

- `MaxConcurrent=1` → request 2 **chờ** (không 503) → request 1 xong → request 2 được serve (nhận đủ SSE).
- Huỷ queued qua `POST .../cancel` → 200; client gốc nhận 400 `request_cancelled` + `X-Request-Id`.
- Snapshot phản ánh đúng queued/serving.
- **226 test cũ còn lại phải pass** (test nào đụng refactor handler/resolver được sửa assertion nhưng hành vi client không đổi).

## 6. Handoff sang slice sau (điều 3C/3D cần biết)

- 3C (retry/failover): seam = sau khi handler trả lỗi retryable — dispatcher/selection đã có `Position`-order sẵn cho failover-on-error; `ExecutionList` đã có list request để requeue rule dùng lại.
- 3D (account): `ProviderKeyResolver.ResolveFirstEnabledKey` vẫn là fallback trong handler — 3D thay bằng weighted/quota selection; `ProxyRequest` đã mang context đủ để trace usage về request id.
- UI Runtime panel: đọc `Snapshot()` từ 2 collection + `SetPriority` service — không cần đổi engine.
- Queue in-memory: restart app → mất request đang chờ (chấp nhận, không persist).
