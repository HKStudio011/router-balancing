# Spec: API Monitor (bên cạnh Live Trace)

- **Ngày:** 2026-10-06
- **Trạng thái:** Draft — chờ user review
- **Tiền đề:** Roadmap #4b "Live Request Trace" (spec `2026-10-05-live-request-trace-design.md`) đã implement gần xong (Task 8 chờ checklist manual). Tính năng này **kế thừa** trace feed + popup chi tiết.

## 1. Bối cảnh & mục tiêu

Dashboard hiện có Live Trace (sơ đồ vòng đời request) nhưng không có số liệu API per-request. Mục tiêu: **API Monitor** — panel "kê bên" Live Trace, mô phỏng theo API Monitor của Unsloth Studio (summary tiles + bảng per-request + chi tiết), kết hợp dữ liệu với Live Trace qua **một popup chi tiết dùng chung**.

Nguồn cảm hứng (Unsloth Studio): side panel tóm tắt (model, live requests, errors, avg latency) + trang đầy đủ (prompts, responses, token counts, TTFT, throughput, errors).

### Quyết định của user (binding)

1. **Scope B**: summary + bảng per-request (model, tokens, latency/TTFT, throughput, error) — **không** persist DB; prompt/response giữ **in-memory**.
2. **Cap 50 request** mới nhất cho bảng (Live Trace giữ cap 60 riêng).
3. **Popup dùng chung**: click circle (Live Trace) hoặc row (API Monitor) → cùng một popup chi tiết (trail + metrics + prompt/response).
4. **Màu row/circle theo bảng màu Live Trace** (trắng chờ / xanh dương chạy / xanh lá ok /đỏ lỗi /vàng hủy).
5. **Layout A**: 2 cột — Live Trace trái, API Monitor phải; màn hẹp (< ~1180px) xếp dọc.
6. **Popup cấu trúc stack**: metrics dải trên → trail → prompt/response collapse dưới cùng.
7. **Panel**: 4 tiles (Live · Lỗi · Avg latency · Token hôm nay) + bảng per-request.

Mockup đã duyệt: `.superpowers/brainstorm/w49485-1791256812/content/` (`layout.html`, `popup.html`, `dashboard-monitor.html`).

### Non-goals

- Persist prompt/response/thông số ra DB hoặc file.
- Filter/sort/search bảng, export, dashboard charts theo thời gian.
- Đổi hành vi dispatch/failover/queue hoặc giao thức stream cho client.
- Đổi layout Live Trace hiện tại (chỉ bọc grid bên ngoài).

## 2. Kiến trúc tổng quan

```
ProxyApp H1 ──StartRequest──┐
ForwardAsync 2xx ──RecordResponse──┤   ┌── IApiMonitorStore (ring 50, singleton)
ForwardAsync lỗi ──RecordError──────┤──►│    + TodayTokens counter
ITraceFeed.Published ──(store subscribe nội bộ)──┘    + event Changed
                                                          │
                            ┌─────────────────────────────┤
                            ▼                             ▼
                   ApiMonitorPanel.razor        RequestDetailModal.razor
                   (tiles + bảng, cột phải)     (popup dùng chung, join theo RequestId)
```

- **Approach (đã duyệt):** store riêng `IApiMonitorStore` thay vì mở rộng `TraceEvent` — tách bạch trace (vòng đời routing, nhẹ) vs monitor (chi tiết HTTP, nặng — body); cap/retention độc lập; theo đúng precedent `ITraceFeed`.
- Store **không tự thêm hook ở DispatcherLoop** — lifecycle/state lấy từ việc subscribe `ITraceFeed.Published` (các event H1–H4 đã có đủ: Received, DispatchStarted, Attempt, Finished, Canceled).
- Popup join 2 nguồn theo `RequestId`: trail từ `ITraceFeed`, metrics/body từ `IApiMonitorStore`.

## 3. Data contracts

### 3.1 `ApiCallRecord` (record, Core.Engine)

| Field | Loại | Nguồn |
|---|---|---|
| `RequestId` | `string` | key join |
| `Model` | `string` | H1 (alias client gửi) |
| `StartedAt` | `DateTimeOffset` | H1 Received (end-to-end) |
| `FirstTokenAt` | `DateTimeOffset?` | byte SSE đầu khi tee — null nếu non-stream |
| `CompletedAt` | `DateTimeOffset?` | H4 |
| `Status` | `int?` | H4 (`FailureKind=="network"` → null) |
| `Success` | `bool?` | H4 |
| `FailureKind` | `string?` | H4 ("http"/"network") |
| `PromptTokens` / `CompletionTokens` | `int?` | `UsageCapture` (2xx) |
| `Combo` / `Provider` / `Account` | `string?` | event `Attempt` cuối (từ feed) |
| `Mode` | `string?` | event `DispatchStarted` |
| `PromptBody` / `ResponseBody` | `string?` | H1 / tee 2xx — UTF-8, **cap 64KB** (truncate + marker) |
| `ErrorBody` | `string?` | nhánh non-2xx / catch network — cap 64KB |
| `State` | `Queued/Running/Done/Error/Cancelled` | suy ra từ feed events |

### 3.2 `IApiMonitorStore`

```csharp
public interface IApiMonitorStore
{
    event Action Changed;                          // UI subscribe — handler phải không nổ
    long TodayTokens { get; }                      // counter UTC-day — tile "Token hôm nay"
    IReadOnlyList<ApiCallRecord> Snapshot();       // ≤50, newest-first
    ApiCallRecord? Find(string requestId);         // popup join
    void StartRequest(string requestId, string model, byte[] promptBody);   // H1
    void RecordResponse(string requestId, int? promptTokens, int? completionTokens,
        DateTimeOffset? firstTokenAt, string? responseBody);                 // 2xx sau tee
    void RecordError(string requestId, int status, string? errorBody);       // non-2xx / network
}
```

- **Upsert theo `RequestId`**: nhiều attempt → 1 record; prompt giữ lần đầu; tokens/response chỉ ghi khi 2xx.
- **Ring 50**: vượt → drop cũ nhất (khác cap 60 của trace — 2 ring độc lập).
- **`TodayTokens` + `TodayDate`**: cộng `prompt+completion` khi `RecordResponse`; reset khi đổi ngày UTC — **không** tính từ ring (đúng nghĩa "Token hôm nay", độc lập eviction).
- Thread-safety: lock nội bộ (kết hợp `Published` đến từ nhiều thread — bài học `Publish_FromManyThreads_PreservesAllEvents`).

### 3.3 `UsageCapture` — đổi API nội bộ

`TeeAsync` trả `TeeResult` thay vì `Usage?`:

```csharp
public sealed record TeeResult(Usage? Usage, DateTimeOffset? FirstTokenAt, string? ResponseBody);
```

- `FirstTokenAt`: timestamp chunk đầu tiên đọc được từ upstream, **chỉ khi** content-type là `text/event-stream`; non-stream → `null`.
- `ResponseBody`: tích lũy UTF-8 trong lúc tee (SSE → ghép payload `data:`; JSON → toàn thân), cap 64KB.
- **Hành vi cho client giữ nguyên tuyệt đối** — byte vẫn về `ctx.Response.Body` từng phần như cũ.
- Cập nhật mọi test đang dùng `TeeAsync`.

## 4. Hook sites (3 call site mới + 1 subscription)

| # | Site | Gọi | Ghi chú |
|---|---|---|---|
| 1 | `ProxyApp` — ngay tại H1 (sau enqueue, đã có `PreparedChatRequest.Body` + `id`) | `StartRequest(id, request.Model, body)` | request không qua enqueue (validate 400) → **không** hiện trong monitor — đồng bộ với trace |
| 2 | `ChatCompletionsHandler.ForwardAsync` — nhánh 2xx, **sau `TeeAsync` và kể cả khi `usage == null`** (không đặt trong nhánh `usage is not null`) | `RecordResponse(id, usage?.PromptTokens, usage?.CompletionTokens, tee.FirstTokenAt, tee.ResponseBody)` | response ghi được thì ghi — thiếu usage chỉ làm token null |
| 3 | `ForwardAsync` — nhánh non-2xx: **sau khi buffer `errorBody`** (`ReadAsByteArrayAsync`), trước khi `ClassifyFatal`; + nhánh `catch` network (`status = 0` → record lỗi network, map về `FailureKind="network"` ở feed đã có) | `RecordError(status, Encoding.UTF8.GetString(cap(errorBody)))` | body cap 64KB |
| 4 | `ApiMonitorStore` ctor nhận `ITraceFeed`, subscribe `Published` | cập nhật State/Combo/Provider/Account/Mode/CompletedAt/Status/Success/FailureKind | handler **try/catch + log** — không được nổ (multicast delegate dừng ở subscriber nổ) |

**Nguồn sự thật cuối cùng:** `State/Status/Success/FailureKind/CompletedAt` do **feed H4** (`Finished`/`Canceled`) quyết định. `RecordError` chỉ bổ sung `ErrorBody` (+`Status` tạm nếu H4 chưa về) — **không** set `State=Error` khi walk còn đang retry (429→attempt sau→ok phải kết thúc `Done`, không mắc kẹt `Error`).

**An toàn (contract):** mọi entry point của store bọc try/catch + `ILogService` — monitor không được làm request nổ, giống `TraceFeed.Publish`.

## 5. Định nghĩa measurement (pin)

| Số | Công thức | Khi thiếu |
|---|---|---|
| **Latency** | `CompletedAt − StartedAt` (end-to-end, gồm queue — queue wait thấy ở trail) | chưa xong → `—` |
| **TTFT** | `FirstTokenAt − StartedAt` | non-stream / chưa xong → `—` |
| **Throughput** | `CompletionTokens / (CompletedAt − FirstTokenAt)` (tok/s) | thiếu 1 điều kiện → `—` |
| **Lỗi** | H4 `Finished{Success:false}` hoặc network (`FailureKind=="network"` → hiện chữ "network") | — |
| **Màu row** | cùng mapping circle: Queued→trắng, Running→xanh dương, Done-ok→xanh lá, Error→đỏ, Cancelled→vàng | — |

**Retry/walk:** 1 request = 1 row — attempt cuối thắng (khớp màu circle hiện tại).

## 6. UI

### 6.1 `ApiMonitorPanel.razor` (mới, `Components/Shared/`)

- Inject `IApiMonitorStore`; `OnInitialized` snapshot + subscribe `Changed`; handler: không nổ + `InvokeAsync(StateHasChanged)` + unsubscribe khi `IAsyncDisposable` (why-comment — bài học Task 5).
- 4 tiles: **Live** (số row `State==Running`) · **Lỗi** (số row `State==Error` — **không** tính `Cancelled`) · **Avg latency** (trung bình `CompletedAt − StartedAt` các row có `CompletedAt`, hiển thị ms/s) · **Token hôm nay** (`store.TodayTokens`).
- Bảng ≤50 row, newest-first, cột: chấm màu · `model · combo/provider/account` · latency · TTFT · tokens (`↑p↓c`). Row `Running` có CSS pulse.
- Click row → mở popup chung (xem §6.3).
- Text qua `Translations.cs`; `<style>` inline (không `.razor.css` — theo precedent).

### 6.2 Layout Dashboard (A)

- Bọc `<RequestTrace />` + `<ApiMonitorPanel />` trong grid: `grid-template-columns: auto minmax(360px, 1fr)`; stack dọc khi `< 1180px`.
- Không đổi nội dung Live Trace.

### 6.3 `RequestDetailModal.razor` (tách từ Task 7)

- Tách modal hiện ở `RequestTrace.razor` thành component riêng; **cả 2 panel** embed, mỗi bên giữ `_selectedId` riêng.
- Tham số: `RequestId`, `OnClose`. Inject `ITraceFeed` + `IApiMonitorStore`.
- Cấu trúc stack (đã duyệt):
  1. **Metrics dải trên**: latency · TTFT · ↑prompt ↓completion tok · throughput · status (+tag `network`).
  2. **Trail**: `ITraceFeed.Snapshot().Where(RequestId==...)` theo publish order (giữ nguyên logic Task 7 kể cả merge-rule attempt/Finished).
  3. **Prompt/Response** (`<details>` collapse, mặc định đóng).
- Fallback join theo từng nguồn, **rule phân biệt**:
  - Monitor record có + trail rỗng → trail hiện **"Đã vượt lịch sử trace"** (ring 60 evicted); metrics/body bình thường.
  - Monitor record không có (đã out-of-ring 50) + trail có → metrics/body hiện **"Không còn trong bộ nhớ"**; trail bình thường.
  - Cả hai rỗng → `trace.detail.idle` (như Task 7 — không phân biệt được với evicted, chấp nhận).
- Màu tag: dùng chung mapping — extract `ColorClass` khỏi `RequestTrace.razor` thành helper static dùng cho cả 3 chỗ (panel, modal, trace) — một nguồn sự thật.

### 6.4 i18n

~18 key mới (`monitor.*` + một vài `detail.*` cho metrics mới) × **cả 2 dict**; danh sách key chính xác chốt trong implementation plan. `TranslationParityTests` gate. Giữ 100% text hiển thị qua `Translations.cs` (rule dòng 19 của plan #4b — không lặp lỗi caption hardcode).

## 7. Edge cases

| Tình huống | Hành vi |
|---|---|
| Proxy dừng giữa chừng | feed phát `Canceled` → row chuyển vàng tự nhiên; row đã xong giữ nguyên (lịch sử) |
| Request chỉ đến H1 rồi client hủy trước dispatch | row trắng → vàng; **prompt body vẫn có** (bắt ở H1), response metrics thiếu (null → `—`) |
| Upstream không trả usage (dù `include_usage`) | tokens null → cột `—`; log Debug hiện có giữ nguyên |
| SSE lỗi giữa chừng | row lỗi (H4), `FirstTokenAt` giữ nếu đã có |
| 2 ring lệch nhau (trace 60 vs monitor 50) | popup join theo từng nguồn, phần thiếu hiện fallback (§6.3) |
| Body > 64KB | truncate + marker `[truncated]` |
| App shutdown | singleton chết theo app — không cần persist |

## 8. Testing

- **Unit — `ApiMonitorStoreTests`**: upsert 1 request qua nhiều attempt (attempt cuối thắng, prompt giữ đầu); cap 50 eviction; consume feed state machine (Received→…→Finished/Canceled set đúng field); `Changed` fire; reset `TodayTokens` khi đổi ngày; thread-safety (nhiều writer song song); entry point không nổ khi subscriber/callback lỗi.
- **Unit — `UsageCaptureTests` mở rộng**: `TeeResult` first-byte (SSE vs non-SSE → null); tích lũy response + cap 64KB; usage parse giữ nguyên.
- **Integration — `ApiMonitorIntegrationTests`** (theo mẫu `TraceIntegrationTests`): request ok → record có tokens/status/response body + prompt body; upstream 429→walk→ok (attempt cuối thắng); request lỗi hết → row đỏ + error body; cancel → row vàng.
- **UI**: app build `net10.0-windows10.0.19041.0` 0W/0E + parity test (không bUnit — theo precedent).
- **Manual checklist (task cuối, chạy cùng user)**: row màu khớp circle; tiles đúng; TTFT/throughput hiển thị khi stream; popup mở được từ **cả 2 nguồn** và nội dung trùng nhau; prompt/response collapse; out-of-window fallback; responsive stack.

## 9. Files dự kiến

| Loại | File |
|---|---|
| Tạo | `src/RouterBalancing.Core/Engine/IApiMonitorStore.cs`, `ApiMonitorStore.cs` |
| Sửa | `src/RouterBalancing.Core/Engine/UsageCapture.cs` (`TeeResult`) |
| Sửa | `src/RouterBalancing.Core/Server/ProxyApp.cs` (hook H1) |
| Sửa | `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs` (hook 2xx/error) |
| Sửa | `src/RouterBalancing.Core/Engine/ProxyApp` DI / `MauiProgram.cs` (đăng ký store — 2 container pattern như `ITraceFeed`) |
| Tạo | `router-balancing/Components/Shared/ApiMonitorPanel.razor`, `RequestDetailModal.razor`, helper màu shared |
| Sửa | `router-balancing/Components/Shared/RequestTrace.razor` (dùng modal + helper màu mới), `Components/Pages/Dashboard.razor` (grid) |
| Sửa | `src/RouterBalancing.Core/Localization/Translations.cs` (~18 key ×2 dict) |
| Tạo/Sửa | test: `ApiMonitorStoreTests.cs`, `ApiMonitorIntegrationTests.cs`, `UsageCaptureTests.cs` (sửa), các test dùng `TeeAsync` |
