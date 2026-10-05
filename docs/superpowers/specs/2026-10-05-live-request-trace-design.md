# Spec: Live Request Trace (sơ đồ động trace request trên Dashboard)

- **Ngày:** 2026-10-05
- **Trạng thái:** Đã duyệt (commit `3a52615`); bổ sung amendment khi viết implementation plan (cùng ngày): H3 tách start/result, field `Mode`/`AttemptDone`, `IProxyHost.QueuedSnapshot()` — xem §2.1/§3/§4/§5.3.
- **Spec liên quan:**
  - Roadmap #4 Dashboard (ledger `.superpowers/sdd/progress.md`) — spec này cover nửa **#4b (trace live)**; nửa #4a (sơ đồ tổng quan, UI hàng đợi/thực thi, thống kê) sẽ có spec riêng sau.
  - [2026-09-28-queue-selection-design.md](2026-09-28-queue-selection-design.md) — queue/`ExecutionList` là nguồn trạng thái; **giữ nguyên**, chỉ observe.
  - [2026-10-05-exhaustive-account-failover-design.md](2026-10-05-exhaustive-account-failover-design.md) — `RetryState.Trail`/attempt journal là nguồn dữ liệu drill-down; **giữ nguyên**.

## 1. Bối cảnh & mục tiêu

Dashboard hiện tại chỉ có card trạng thái + Start/Stop/Restart + URL (`Dashboard.razor`, 105 dòng). Không có cách nào nhìn request đang đi tới đâu trong pipeline (queue → dispatch → combo/provider/account → kết quả) mà phải đọc Log panel.

### 1.1 Mục tiêu

| # | Mục tiêu |
|---|---|
| G1 | Trace live từng request: từ lúc app nhận → hàng đợi → thực thi → trả client, hiển thị dạng **sơ đồ động** (node biểu tượng + circle di chuyển trên đường nối). |
| G2 | Circle = request, 5 màu: **trắng** = mới nhận/queue, **xanh dương** = đang thực thi, terminal: **xanh lá** thành công / **đỏ** lỗi / **vàng** cancel; terminal fade sau ~10s. |
| G3 | Sơ đồ **dọc**: spine Input → Hàng đợi → Danh sách thực thi → Client; nhánh tỏa phải theo đúng route **Combo → Provider → Account** của request đó. |
| G4 | **Drill-down**: click circle → modal timeline attempt (nhận → dispatch → từng attempt → kết quả). |
| G5 | An toàn với mật độ cao: nhiều account/request cùng lúc → cap hiển thị + chip "+N", không tràn UI. |

### 1.2 Ngoài phạm vi

| Việc | Lý do |
|---|---|
| Lưu history > 10s / persist trace ra DB | view live, fade 10s — YAGNI |
| Nút pause/clear, filter theo provider/model | chưa cần; làm sau nếu user dùng nhiều |
| UI hàng đợi, thống kê, sơ đồ tổng thể Dashboard (#4a) | spec riêng |
| Endpoint HTTP mới cho trace | in-process Blazor Hybrid — không cần |
| Đổi bất kỳ behavior dispatch/failover/queue | chỉ observe (publish không throw, không chậm pipeline) |

### 1.3 Quyết định đã chốt (user gate khi brainstorm)

1. **Mục đích:** monitor tổng quan **+ drill-down** (chọn B) — trail attempt có sẵn trong `RetryState`, chỉ cần expose.
2. **Vị trí:** section trong Dashboard (dưới status card).
3. **Terminal:** fade out sau ~10s (chọn A).
4. **Layout sơ đồ:** phương án **B — sơ đồ dọc** (mockup A ngang / B dọc / C trunk-lanes → chọn B).
5. **Data feed:** **#1 `ITraceFeed` in-process** (3 cách: in-process event / derive từ log / DB+poll → chọn #1): Blazor Hybrid chạy cùng process với Core, DI sẵn, data structured.
6. **Design v2 chấp nhận** (mockup cuối): animation circle di chuyển thật trên đường nối (SVG `animateMotion`/CSS transition), node có icon, route active marching-ants, **cap "+N"** cho nhiều account/queue (max 4 chip account + "+9 idle", max 6 dot queue), route tree chỉ vẽ combo/provider đang active.
7. **Drill-down** dùng `Modal` có sẵn, nội dung timeline attempt như mockup.

## 2. Architecture

### 2.1 Component thêm / sửa

**Thêm:**

| Component | Vị trí | Vai trò |
|---|---|---|
| `ITraceFeed`, `TraceFeed`, `TraceEvent`, `TraceStage`, `TraceRoute` | `src/RouterBalancing.Core/Engine/` | Singleton in-process: nhận publish từ pipeline, trả snapshot, phát event cho UI (§3) |
| DI đăng ký `ITraceFeed → TraceFeed` | `MauiProgram.cs` | singleton MAUI — UI (`RequestTrace`) inject từ container này |
| DI trong proxy container | `ProxyApp.ConfigureServices` + `ProxyHost.StartAsync` | ProxyHost **re-register cùng instance** vào proxy container (cùng pattern `_settings`/`_log`) để endpoint/dispatcher publish vào đúng feed UI đang đọc; `ConfigureServices` chỉ đăng ký fallback nếu vắng (test harness chưa đăng ký) |
| `IProxyHost.QueuedSnapshot()` | `IProxyHost.cs` / `ProxyHost.cs` | Bù `Received` cho request đang chờ nhưng feed chưa có (§5.3) — UI không truy cập được `IRequestQueue` nằm trong proxy container |
| `RequestTrace.razor` | `router-balancing/Components/Shared/` | Component section sơ đồ động (§5) |
| `<RequestTrace />` | `Components/Pages/Dashboard.razor` | Chèn dưới status card |

**Sửa (chỉ thêm dòng publish — không đổi logic):**

| Component | Thay đổi |
|---|---|
| `ProxyApp` (endpoint pipeline) | Hook **Received** (sau khi `ProxyRequest` được `Enqueue`) + **Finished/Canceled** (sau khi outcome xử lý xong, response đã ghi/xác định) — §4 |
| `DispatcherLoop` | Hook **DispatchStarted** (sau `queue.Take` thành công) + **Attempt** (sau `Retry.RecordAttempt`) — §4 |

### 2.2 Luồng dữ liệu

```
ProxyApp (Received) ─┐
DispatcherLoop (DispatchStarted, Attempt) ─┤→ TraceFeed.Publish
ProxyApp (Finished | Canceled) ─┘                    │
                                          ring buffer 200 + active map
                                                     │ event Published
                                          RequestTrace.razor (throttle ~100ms)
                                                     │
                                          circle màu/độ chờ fade + drill-down modal
```

## 3. TraceFeed — model & API

```csharp
public enum TraceStage { Received, DispatchStarted, Attempt, Finished, Canceled }

/// <summary>Route của request tại thời điểm event — tên hiển thị (không phải id).</summary>
public sealed record TraceRoute(string? Combo, string? Provider, string? Account);

public sealed record TraceEvent(
    string RequestId,          // ProxyRequest.Id
    TraceStage Stage,
    string Model,              // ProxyRequest.Model
    TraceRoute? Route,         // null ở Received/DispatchStarted cho tới Attempt đầu tiên
    int? Attempt,              // số attempt (1-based) — chỉ ở Stage Attempt
    int? Status,               // Finished (nếu có); Attempt-done: HTTP status (null = lỗi mạng)
    bool? Success,             // chỉ ở Finished — outcome cuối là thành công hay không
    string? FailureKind,       // Attempt-done: "http" | "network" — phục vụ hiện icon trong trail
    DateTimeOffset At,
    string? Mode = null,       // DispatchStarted: ComboMode.ToString() — hiển thị ở drill-down (§5.5)
    bool? AttemptDone = null); // Attempt: false = attempt BẮT ĐẦU (chưa có kết quả), true = đã RecordAttempt

// Amendment: Attempt sinh tối đa 2 event — start (H3a, route đầy đủ để vẽ circle vào
// nhánh Provider/Account ngay khi attempt chạy) + done (H3b, sau RecordAttempt — có
// Status/FailureKind). Attempt thành công cuối chỉ có event start, status lấy từ Finished.
```

**API `ITraceFeed`:**

| Thành viên | Hành vi |
|---|---|
| `event Action<TraceEvent>? Published` | Phát sau mỗi `Publish` — UI subscribe |
| `void Publish(TraceEvent e)` | **Thread-safe**, cập nhật active map + ring buffer (cap **200**, drop cũ nhất), rồi phát `Published`. **Contract: không bao giờ ném exception ra caller** — observer không được làm hỏng luồng request (bọc try/catch, lỗi ghi `ILogService.Error`; đây là ràng buộc cố ý, không phải nuốt im lặng). |
| `IReadOnlyList<TraceEvent> Snapshot()` | 200 event gần nhất — subscriber trễ rebuild trail (§5.3) |
| `void PurgeAll()` | Xóa ring + active map — gọi từ `ProxyHost.StopAsync` (`src/RouterBalancing.Core/Server/ProxyHost.cs` — cùng process, cùng assembly Core) |

**Quy tắc lifecycle trong feed:** event `Finished`/`Canceled` → gỡ entry khỏi active map (request không còn "sống"); ring buffer vẫn giữ để truy vết. Active map entry tạo ở `Received`, cập nhật ở `DispatchStarted`/`Attempt`.

## 4. Hook — 4 điểm publish

| # | Site | Lúc nào | Publish | Data lấy từ đâu |
|---|---|---|---|---|
| H1 | `ProxyApp` — endpoint tạo request | Ngay sau khi `queue.Enqueue(request)` thành công (khu vực tạo `ProxyRequest`, ~dòng 133) | `Received` | `request.Id`, `request.Model` |
| H2 | `DispatcherLoop` | Sau khi `queue.Take` thành công, trước/đầu `ServeAsync` (~dòng 152–159) | `DispatchStarted` | `request.Id`, `request.Model`; `Route` = null (chưa chọn xong attempt) |
| H3a | `DispatcherLoop` | **Trước** `handler.ForwardAsync` trong `ServeAsync` (~dòng 184) — attempt vừa bắt đầu | `Attempt{AttemptDone=false}` | `Attempt` = `Retry.Attempts + 1`; `Route` = combo (`SelectionSuccess.ComboName`, amendment: field mới) + provider + account — **đủ dữ kiện vẽ circle vào nhánh ngay khi attempt đang chạy** |
| H3b | `DispatcherLoop` | Sau `request.Retry.RecordAttempt(provider, model, account, status)` (~dòng 225) — attempt fail đã journal | `Attempt{AttemptDone=true}` | Args của `RecordAttempt` + `Status`; `FailureKind` = `status is null ? "network" : "http"` (parity `RetryState` — status null = lỗi mạng) |
| H4 | `ProxyApp` | Sau khi outcome đã xử lý xong — client đã nhận response / error đã ghi / cancel xác định (khu vực await `request.Completion`, ~dòng 164–215) | `Finished` (outcome != Cancelled/Aborted, `Success` từ outcome) **hoặc** `Canceled` (outcome `Cancelled` hoặc `Aborted` — client ngắt) | `outcome` + thời điểm hiện tại |

- **Resolve failure** (combo không resolve được) → `Finished{Success=false}` ở H4 — không cần hook riêng.
- **Cancel request còn trong queue** (`ProxyApp` dòng ~164 đã set `Completion.TrySetResult(Cancelled)`) → đi qua H4 → `Canceled`. Không hook riêng.
- **Proxy stop** → `PurgeAll()` — không để active map rỗng mồ côi.
- **Không đổi behavior:** publish chỉ đọc dữ liệu đã có; failure của feed không lan ra pipeline (§3 contract).

## 5. UI — `RequestTrace.razor`

### 5.1 Cấu trúc section

- Header: tiêu đề + counter: `n chờ · n đang thực thi · n OK · n lỗi · n cancel` (đếm từ node hiển thị).
- Skeleton node **luôn vẽ** (kể cả idle): spine dọc `📥 Input → ⏳ Hàng đợi → ⚙️ Danh sách thực thi → 🖥 Client` + legend 5 màu.
- Nhánh `🧩 Combo → ☁️ Provider → 👤 Account` **chỉ render khi có request active đi qua** (theo mockup "route tree chỉ vẽ route đang active").

### 5.2 Circle & di chuyển

- Mỗi request sống = 1 circle, **absolutely positioned tại anchor của stage hiện tại**; đổi stage → đổi tọa độ → **CSS `transition` (transform/left/top ~0.35s ease)** tạo hiệu ứng circle chạy dọc đường nối (hình thật cần, không phải animation trang trí).
- Ornament (chỉ trang trí, không gắn data): 1–2 circle lặp vô hạn trên path bằng SVG `animateMotion`; route active dùng stroke `stroke-dasharray` + `stroke-dashoffset` animation (marching-ants) như mockup v2.
- Trong node: xếp circle theo stack, **max 6 dot + chip `+N`**; account: **max 4 chip + chip `+N idle ▾`**.
- Màu circle theo trạng thái (G2); dot tại account/provider node mang màu của request đang dùng node đó.

### 5.3 Snapshot cho subscriber trễ

Khi component init (hoặc user mở lại Dashboard): dựng state từ `TraceFeed.Snapshot()`:

- Group event theo `RequestId`; request **chưa có** `Finished`/`Canceled` = còn sống → render tại stage cuối cùng biết được.
- Bù `Received` cho request đang chờ nhưng feed chưa có (mới mở app / ring eviction): `IProxyHost.QueuedSnapshot()` (amendment — UI không inject được `IRequestQueue` thuộc proxy container) — id chưa có trong feed → tạo node trắng tại Hàng đợi.
- Trail drill-down = toàn bộ event của `RequestId` (từ ring buffer), sắp theo `At`.

### 5.4 Fade, cap, throttle

| Quy tắc | Giá trị |
|---|---|
| Terminal fade | Circle `Finished`/`Canceled` giữ **10s** (`ExpiresAt = At + 10s`) rồi xóa; tick dọn mỗi 500ms |
| Cap node hiển thị | **60** — vượt: drop terminal cũ nhất trước; nếu vẫn vượt (active > 60) → giữ 60 + badge `+N` |
| Render throttle | Event đến → set dirty; timer **100ms** → `InvokeAsync(StateHasChanged)` — request dày không thrash render |
| Subscribe/Unsubscribe | `OnInitializedAsync` subscribe, `Dispose` unsubscribe — theo pattern `LogPanel`/`Providers` |

### 5.5 Drill-down

- Click circle → dùng `Modal` component có sẵn, hiện: `RequestId`, model, thời gian bắt đầu, tổng duration, **timeline trail**: nhận → dispatch (hiện combo mode) → từng `Attempt` (provider/account/status) → kết quả (OK kèm status / lỗi / cancel).
- Chip `+N idle ▾` → expand inline danh sách account (tên + dot active) — dữ liệu lấy lazy từ `IProviderService.ListAsync()` khi expand lần đầu, dot active suy ra từ route các request đang sống.

### 5.6 i18n

Thêm key ×2 dict (`Translations.cs`): `trace.title`, `trace.legend.queued/running/ok/error/cancelled`, `trace.count.queued/running/ok/error/cancelled`, `trace.more`, `trace.idleAccounts`, `trace.detail.title/trail.*`, `trace.fadeHint` (≈ 14–16 key).

## 6. Edge cases

| Case | Xử lý |
|---|---|
| Mở Dashboard khi request đang chạy | Snapshot rebuild (§5.3) — circle xanh xuất hiện đúng chỗ |
| Request stream sống nhiều phút (SSE) | Circle xanh đến khi `Finished` — hành vi đúng |
| Proxy stop/start giữa chừng | `PurgeAll()` khi stop → skeleton về idle |
| Event đồng thời từ nhiều thread | `Publish` thread-safe (§3) + render throttle (§5.4) |
| Giữa `DispatchStarted` và `Attempt` start đầu tiên (cửa sổ rất ngắn) | Circle xanh ở Danh sách thực thi, route placeholder "—" cho tới khi H3a |
| Feed lỗi (exception bên trong) | `Publish` không ném ra caller, ghi log — pipeline không bị ảnh hưởng (§3) |
| Memory | Ring cap 200 + active map bị giới hạn bởi concurrency thực tế + cap UI 60 |

## 7. Testing & gates

### 7.1 Unit test (`router balancing test/`)

| Test | Hành vi |
|---|---|
| `TraceFeedTests.Publish_RaisesPublished_WithEvent` | Publish → event nhận đúng event |
| `TraceFeedTests.Snapshot_LateSubscriber_GetsRecentEvents_ReturnsLast200` | Buffer cap 200, drop cũ nhất |
| `TraceFeedTests.ActiveSnapshot_RequestWithoutTerminal_IncludesId_AfterTerminalRemovesIt` | Lifecycle active map: chưa có `Finished`/`Canceled` = còn sống (pin ngữ nghĩa §5.3; test qua `ActiveSnapshot()` internal — `InternalsVisibleTo`) |
| `TraceFeedTests.Publish_DoesNotThrow_ToCaller` | Contract không ném ra caller |
| `TraceFeedTests.PurgeAll_ClearsState` | Proxy stop |
| `TraceFeedTests.Publish_FromManyThreads_PreservesAllEvents` | Publish đồng thời nhiều thread → không mất/duplicated event (§6) |
| `ProxyApp.ClassifyOutcome` [Theory] (test trong test project) | H4 mapping: `Cancelled`/`Aborted` → Canceled; `Handled` → Finished{true}; `Passthrough`/`Error` → Finished theo status |

### 7.2 Integration test

Dispatch full flow qua test harness có sẵn → sequence `Received → DispatchStarted → Attempt(start, có route) → Finished`; case 429 failover → thêm `Attempt{done, Status=429, FailureKind="http"}`; lỗi mạng → `FailureKind="network"`; cancel request đang chờ → `Canceled`. (Dùng pattern `ProxyQueueIntegrationTests`/`ProxyRetryIntegrationTests`.)

### 7.3 Manual checklist (chạy MAUI app thật)

1. Gửi request → circle trắng vào Hàng đợi → xanh ở Thực thi → xanh lá ở Client, fade sau ~10s.
2. Gửi request lỗi (provider lỗi) → circle đỏ.
3. Cancel → circle vàng.
4. Click circle → modal trail đúng các bước.
5. Queue dài / nhiều account → chip `+N`, không tràn node.
6. Dừng proxy → section về idle, không node mồ côi.

### 7.4 Gates

- `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental` → 0W/0E
- `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → 0W/0E
- `dotnet test "router balancing test/router balancing test.csproj"` → xanh (2 fail known `SingleInstanceGuardTests` khi app đang chạy — bỏ qua, đã có tiền lệ trong ledger)
