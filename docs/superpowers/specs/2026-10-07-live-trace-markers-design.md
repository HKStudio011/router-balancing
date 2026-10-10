# Spec: Live Trace markers & luồng (bổ sung G1–G5 + 3 luồng wire)

- **Ngày:** 2026-10-07
- **Trạng thái:** Đã duyệt 4 section khi brainstorm (user gates §1.3); chờ implementation plan.
- **Spec liên quan:**
  - [2026-10-05-live-request-trace-design.md](2026-10-05-live-request-trace-design.md) — spec nền (`ITraceFeed`, `RequestTrace`, fade/cap/throttle). Spec này **bổ sung** marker + wire, không thay đổi quyết định §1.3/§5 cũ.
  - [2026-10-06-early-headers-keepalive-design.md](2026-10-06-early-headers-keepalive-design.md) — backend G2 đã làm (§4.5: trace/monitor chưa marker → spec này thêm).
  - [2026-10-02-proxy-per-provider-account-design.md](2026-10-02-proxy-per-provider-account-design.md) — proxy gắn per account (D1) — nguồn data G4.
  - [2026-09-28-queue-selection-design.md](2026-09-28-queue-selection-design.md) — luật 1-Highest/demote — nguồn data G3.
  - Mockup: catalog `docs/superpowers/mockups/2026-10-07-trace-cases.html`; screen cuối `.superpowers/brainstorm/1175-1791338434/content/trace-cases-branch-v2.html`.

## 1. Bối cảnh & mục tiêu

Catalog 12 case + 6 gap (G1–G6) và 3 luồng route chỉ có trong mockup; backend nhiều dữ liệu đã có nhưng trace chưa hiện.

| # | Gap | Mục tiêu |
|---|---|---|
| G1 | Park (capacity) mù | Hiện request bị đẩy về queue chờ slot: ring vàng + badge "chờ slot" |
| G2 | early-headers mù | Badge "headers đã gửi" khi stream đã commit 200 |
| G3 | Priority/demote mù | Badge priority trên dot; tự đổi khi luật 1-Highest demote |
| G4 | Proxy cooldown mù | Badge `COOLDOWN · {s}s` trên chip account |
| G5 | Endpoint mù | Highlight dòng `POST /v1/...` tương ứng trên node Input |
| G6 | Enqueue-fail mù | **BỎ** — xem §1.4 |

Thêm **3 luồng wire** (route sáng theo state thật): ① Input→Queue ② Cancel→Client ③ Lỗi thực thi→Client.

### 1.2 Ngoài phạm vi

| Việc | Lý do |
|---|---|
| Publish trace/monitor cho G6 | §1.4 — rủi ro ghi nhầm node cũ > giá trị |
| Circuit per-model (`ModelHealthStore`) | chưa implement (spec 2026-09-29), spec riêng |
| Backoff/Retry-After cho park | G1 chỉ visualize `ReenqueueForPark` — không đổi behavior dispatch |
| Marker trong Api Monitor / drill-down | user gate: **chỉ Live Trace** |
| Filter G6 row 500 trong monitor | vô nghĩa khi G6 bị loại |

### 1.3 Quyết định đã chốt (user gate khi brainstorm)

1. Phạm vi round: **G1–G6 + 3 luồng** (sau đó G6 bị loại — §1.4).
2. G1 = **park thật capacity** (`ReenqueueForPark`), không thêm backoff.
3. G4 = **proxy/account cooldown** (data sẵn `ProxyRuntimeStatus`), không làm circuit per-model.
4. G3 = **badge + demote full** (không chỉ badge tĩnh).
5. UI phạm vi: **chỉ Live Trace** (không marker ở Api Monitor/drill-down).
6. Luồng dữ liệu: **Approach A — mở rộng `ITraceFeed`** (loại B side-channel store, C overload field string).
7. G6: chốt ban đầu "chấp nhận row 500 hiện trong monitor", sau **bỏ hẳn** khi phát hiện Enqueue-false = id collision (§1.4).
8. Wire Input→Queue = **màu trắng** (mockup vẽ xanh dương — user sửa).

### 1.4 G6 bị loại — phát hiện từ code

`IRequestQueue.Enqueue` trả `false` **chỉ khi id đã tồn tại** (`IRequestQueue.cs:11–12`). Id được sinh sau khi check `while (queue.Contains(id) || executions.Contains(id))` (`ProxyApp.cs:157–161`) nên case này gần như không xảy ra; **nếu xảy ra = id collision** → node cũ (request thật) dùng chung id → publish `Received+Finished(500)` sẽ **ghi nhầm đóng node cũ** (request đang chạy/đang chờ bị hiện 500 đỏ).

Chốt: **không publish**, giữ nguyên log Warning (`ProxyApp.cs:188–195`). Gap được ghi nhận tại đây — nếu sau này có thay đổi semantics (ví dụ queue cho phép id trùng có chủ đích), revisit.

## 2. Architecture — mở rộng ITraceFeed (Approach A)

### 2.1 `TraceStage` +1 giá trị

```csharp
public enum TraceStage { Received, DispatchStarted, Attempt, Finished, Canceled, Parked }
```

- `Parked` = request đã từng dispatch nhưng `ReenqueueForPark` re-enqueue chờ slot capacity (`DispatcherLoop.cs:599` — cả 2 nhánh: TryEnter Full, selector-null corner).
- Anchor = Hàng đợi (§5.2 UI); **không** thêm field reason — capacity là lý do duy nhất (YAGNI).

### 2.2 `TraceEvent` — 3 field optional thêm cuối record

```csharp
public sealed record TraceEvent(..., string? Mode = null, bool? AttemptDone = null,
    RequestPriority? Priority = null,  // G3 — gửi ở Received + update demote
    string? Endpoint = null,           // G5 — "chat" | "responses" — chỉ event đầu
    bool? HeadersSent = null);         // G2 — early-headers đã commit 200 (stream)
```

- Default `null` → call site hiện có không đổi.
- Contract `Publish` fail-open giữ nguyên (spec nền §3: không ném exception ra caller).

### 2.3 Publish points

| Marker | Site | Event | Data |
|---|---|---|---|
| G1 | `DispatcherLoop.ReenqueueForPark` (`DispatcherLoop.cs:599`) | `Stage=Parked` | Id, Model, Route, Attempt (đã có) |
| G2 | `ProxyApp.RunQueueFirstAsync` — sau `ctx.Response.StartAsync()` thành công (early-headers §4.5, **stream only**) | `Stage=Received, HeadersSent=true` | merge vào node (§2.5) |
| G3+G5 | `ProxyApp.RunQueueFirstAsync` — điểm `Received` sẵn có (`ProxyApp.cs:204`) | `Received + Priority + Endpoint` | `request.Priority` (parse sẵn `ProxyApp.cs:169`), `endpoint` enum → `"chat"`/`"responses"` |
| G3 demote | `RequestQueue` — điểm enforce 1-Highest (queue-selection §3.1) | `Stage=Received, Priority=<mới>` cho request bị demote | inject `ITraceFeed?` (§2.4) |

### 2.4 DI & wiring

- `RequestQueue` ctor nhận thêm `ITraceFeed?` — **nullable**: unit test dựng `new RequestQueue()` trực tiếp không vỡ; `null` → bỏ qua publish.
- Đăng ký `AddSingleton<IRequestQueue, RequestQueue>` (`ProxyApp.cs:85`) cùng container với `ITraceFeed` (`MauiProgram.cs:63`) → resolve OK; verify không circular khi implement (`TraceFeed` không phụ thuộc queue).
- **Không đổi behavior pipeline**: publish chỉ đọc dữ liệu đã có (giữ nguyên §1.2 spec nền).

### 2.5 Merge rule (UI)

- `Received` trên node **đã tồn tại** → chỉ **merge field** (`Priority`/`Endpoint`/`HeadersSent`), **không reset Stage** — G2/G3 update không kéo dot về queue khi request đang chạy.
- `Parked` là stage thật → dot **chuyển** về anchor Hàng đợi (regression có chủ đích, hình thật cần).
- `ApiMonitorStore`: thêm case `Parked => current` (ignore — user gate Live-Trace-only); `Received`-update đã là no-op sẵn (`ApiMonitorStore.cs:210`).
- `Parked` chưa terminal → giữ trong active map; dispatch lại → `DispatchStarted` bình thường.

## 3. UI — `RequestTrace.razor`: wires & markers

### 3.1 Ba luồng wire (route mới)

Geometry theo constants sẵn: `DotStartX=14`, `QueueTop=96`, `DispatchTop=188`, `ClientTop=336`, `DispatchExitX=128` (`RequestTrace.razor:453–476`).

| Wire | Đường | Màu | Sáng khi | Tắt khi |
|---|---|---|---|---|
| ① Input→Queue | segment `x=DotStartX` từ đáy node Input (`top:8`) → đỉnh Hàng đợi (`96`) | **trắng** (gate §1.3-8) | `QueuedCount > 0` | queue rỗng |
| ② Cancel→Client | lane phải spine `x≈116`, 2 segment — skip node Thực thi | vàng | có node `Canceled` còn hạn | terminal fade (~10s) |
| ③ Lỗi→Client | lane phải, offset x khác (không chồng vàng), segment **dưới Thực thi → Client** | đỏ | có node `Finished{success=false}` còn hạn | terminal fade |

- Dot di chuyển **không code thêm**: CSS transition ~0.35s khi đổi stage (spec nền §5.2) đã cover; `AnchorOf`: `Canceled`/`Finished` → Client (`RequestTrace.razor:786`).
- Wire ③ luôn đi qua DispatchStarted (G6 đã loại — không còn case Finished khi chưa dispatch) → giữ nguyên geometry start từ dưới Thực thi.
- Marching-ants theo pattern `stroke-dasharray` sẵn có; wire chỉ vẽ khi sáng (không ornament tĩnh).

### 3.2 G1 — Park

- `AnchorOf`: thêm case `Parked` → `(DotStartX, QueueTop + DotRowOffsetY)` (cùng Received).
- Dot trắng + **ring vàng đứt** quanh dot + badge `chờ slot` (i18n); mất khi event kế (`DispatchStarted`).
- `TraceColors`: `Parked` → `trace-dot--white`.

### 3.3 G2 — HeadersSent badge

- Badge `200 ✓` (xanh lá, nét đứt) cạnh dot tại Hàng đợi khi `HeadersSent == true`.
- Non-stream: không publish → không hiện (đúng backend).

### 3.4 G3 — Priority tag

- Tag cạnh dot: `Highest` = amber, `High` = blue, `Normal` = **không hiện** (tránh noise).
- Demote → merge rule đổi tag, dot đứng yên (không reset stage).

### 3.5 G4 — Cooldown (deviation so với mockup)

- Mockup vẽ viền đỏ quanh node Provider — **app vẽ trên chip Account** (data = proxy gắn per account, gate §1.3-3).
- **Data path (dùng API có sẵn, không thêm method Core):**
  1. Mỗi tick `RunLoopAsync`: `IProxyPool.Snapshot()` (`ProxyRuntimeStatus(Id, Endpoint, IsDown, DownUntil)`, `IProxyPool.cs:39`) → tập proxy down.
  2. Khi có proxy down mới (hoặc refresh ~30s): với mỗi proxy down → `GetReverseAssignmentsAsync(proxyId)` → provider/account **trực tiếp** gán proxy đó; với mỗi provider down → `GetAssignmentsAsync(providerId)` → account **kế thừa** (ProxyIds rỗng, D1).
  3. `CooldownTracker` (lớp riêng, unit-test được — inject `Func<>` thay cho mock framework): hợp nhất → `accountName → max(DownUntil)`.
- Hiển thị: chip account trong map → viền đỏ đứt + badge `COOLDOWN · {s}s` (`DownUntil − now`, render theo tick — tick = độ mượt, giữ tần suất hiện tại). Account kế thừa provider dùng `DownUntil` của proxy provider.
- Lỗi call → catch + log once + giữ map cũ/ẩn badge, **loop không chết**.
- Chip trong danh sách `+N idle ▾` expand: **không** làm badge (chỉ chip đang hiển thị) — ghi nhận, làm nếu user cần sau.

### 3.6 G5 — Endpoint highlight

- Tách 2 dòng paths (`RequestTrace.razor:326`) thành **2 phần tử riêng**.
- Dòng của endpoint event `Received` gần nhất → **sáng xanh ~2s rồi fade** (data-bound, không loop).
- `title` trên dot = endpoint (hover xem được).

### 3.7 Legend & i18n

- Legend: + `chờ slot` (ring vàng) · + `headers đã gửi` · priority colors (amber/blue).
- Key mới ×2 dict (`Translations.cs`): `trace.park.badge`, `trace.headers.badge`, `trace.priority.high`, `trace.priority.highest`, `trace.cooldown` (format `{0}s`), `trace.legend.parked`, `trace.legend.headers`.
- Path literal (`POST /v1/chat/completions` …) **không dịch**.

## 4. Edge cases

| Case | Xử lý |
|---|---|
| E1 Demote publish | `RequestQueue(ITraceFeed?)` — null (test cũ) → bỏ qua; feed `Publish` fail-open (spec nền §3) |
| E2 ApiMonitor | `Parked => current` (ignore); `Received`-update no-op sẵn |
| E4 Countdown | theo tick `RunLoopAsync` — giữ nguyên tần suất, ghi chú độ mượt |
| E5 QueuedSnapshot bù | bù node trắng (`RequestTrace.razor:589–596`) **chỉ khi node thiếu** → không override `Stage=Parked`; ring eviction mất node → bù về Received (mất badge — chấp nhận) |
| E6 Non-stream | không publish `HeadersSent` |
| Priority Normal | tag ẩn |
| Đồng thời nhiều thread | `Publish` thread-safe + render throttle 100ms (giữ spec nền §5.4) |
| Memory | không thêm ring entry ngoài event mới; cap 60/fade 10s giữ nguyên |

## 5. Testing & gates

### 5.1 Integration test (`router balancing test/`)

| Test | Hành vi |
|---|---|
| `Enqueue_PublishesReceivedEvent_WithPriorityAndEndpoint` | Received đầu mang `Priority` + `Endpoint` đúng |
| `ReenqueueForPark_PublishesParkedStage` | capacity corner → event `Parked` (reuse fixture capacity có sẵn) |
| `EnqueueHigherPriority_DemotesQueuedRequest_PublishesReceivedUpdateWithNewPriority` | 1-Highest demote → event `Received`-update priority mới |
| `EarlyHeaders_Flush_PublishesHeadersSentOnStreamRequest` | mở rộng test early-headers sẵn có |

### 5.2 Unit test (tách class testable khỏi Razor `@code`)

| Class (tách mới) | Test |
|---|---|
| `TraceNodeAggregator` (tách `ApplyEvent`/`AnchorOf`) | `ReceivedOnExistingNode_MergesFieldsWithoutResettingStage` · `ParkedEvent_MovesNodeToQueueAnchor` · `PriorityTag_ReturnsAmberForHighest_BlueForHigh_NoneForNormal` |
| `CooldownTracker` (inject `Func<>`, không cần mock framework) | `Resolve_UsesAccountProxies_WhenAssigned` · `Resolve_FallsBackToProviderProxies` · `Resolve_ReturnsNone_WhenDirect` · `SnapshotFailure_ReturnsNone_WithoutThrowing` |
| `ApiMonitorStoreTests` | `ParkedEvent_KeepsCurrentState` |

Không test G6 (đã loại).

### 5.3 Gates

- `dotnet build router-balancing.slnx` → 0W/0E
- `dotnet test "router balancing test/router balancing test.csproj"` → xanh (2 fail known `SingleInstanceGuardTests` khi app đang chạy — tiền lệ ledger)

### 5.4 Manual checklist (MAUI app thật)

1. Ép capacity (nhiều request song song) → ring vàng "chờ slot" tại Hàng đợi; dispatch lại → badge mất, dot xanh tiếp.
2. Gửi stream request → badge `200 ✓` (stream mới có; non-stream không).
3. Request `Highest` thứ 2 khi `Highest` cũ còn chờ → tag cũ đổi giá trị, dot không nhảy.
4. Đưa proxy vào cooldown → chip account sáng viền đỏ + badge đếm ngược; hết cooldown → badge mất.
5. Gửi request `responses` → dòng `POST /v1/responses` trên node Input sáng ~2s rồi fade.
6. Cancel / lỗi thực thi → wire vàng/đỏ sáng đúng đoạn, fade cùng terminal (~10s).
7. Queue có request → wire trắng ① sáng; queue rỗng → tắt.
