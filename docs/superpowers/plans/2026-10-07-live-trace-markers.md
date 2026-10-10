# Live Trace Markers & Wires (G1–G5 + 3 luồng) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hiện marker/wire G1–G5 + 3 luồng route vào Live Trace app thật, theo spec đã duyệt — backend publish mở rộng `ITraceFeed`, UI badges/wires trong `RequestTrace.razor`, testable logic tách về Core.

**Architecture:** Mở rộng `TraceStage`(+`Parked`) và `TraceEvent`(+3 field optional) — publish tại các point đã có trong pipeline (không đổi behavior); UI merge rule + anchor + priority tag tách thành `TraceNodeAggregator` (Core, unit-test được); cooldown G4 qua `CooldownTracker` (Core, inject `Func<>`); wires/badges render data-bound trong Razor, không animation loop mới.

**Tech Stack:** .NET 10 / MAUI Blazor Hybrid, xUnit, ASP.NET TestHost (integration), DI singleton (`ProxyApp.ConfigureServices` + `MauiProgram`).

**Spec:** `docs/superpowers/specs/2026-10-07-live-trace-markers-design.md` — mọi tranh chấp đọc spec này; spec nền (fade/cap/throttle): `docs/superpowers/specs/2026-10-05-live-request-trace-design.md`.

## Global Constraints

- `dotnet build router-balancing.slnx` phải 0 Warning / 0 Error sau mỗi task; `dotnet test "router balancing test/router balancing test.csproj"` xanh (2 fail known `SingleInstanceGuardTests` khi app đang chạy — tiền lệ ledger).
- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên; mọi ref có thể null xử lý tường minh.
- Contract `ITraceFeed.Publish` **không bao giờ ném exception ra caller** — không thêm try/catch nào phá contract này.
- **Không đổi behavior pipeline**: publish chỉ đọc dữ liệu đã có (spec §2.4); G6 bị loại — không publish khi `Enqueue` trả `false`.
- Không marker nào ngoài Live Trace (Api Monitor / drill-down ngoài phạm vi — gate §1.3-5); `ApiMonitorStore` chỉ thêm case `Parked => current`.
- Comment code theo AGENTS.md: `//`/`///` tiếng Việt giải thích "tại sao"; commit message tiếng Anh conventional (`feat:`/`fix:`/`refactor:`/`test:`).
- i18n: mọi text hiển thị qua key `Translations.English` + `Translations.Vietnamese` (parity test tự bắt key thiếu); path literal `POST /v1/...` **không dịch**.
- Test name tiếng Anh mô tả hành vi; file test nằm trong `router balancing test/` theo namespace folder sẵn có.
- Wires chỉ vẽ khi "sáng" (data thật), marching-ants theo pattern `stroke-dasharray` sẵn có — không ornament tĩnh.

## Review Focus

5 input/condition spec im lặng nhưng dễ cắn người dùng — mỗi dòng có test ghim vào task sở hữu:

1. **Stream request giờ có thêm event `Received(HeadersSent=true)`** — test sequence đúng-từng-bước (`Chat_Success_ReceivesReceivedDispatchAttemptFinishedSequence`) chỉ dùng non-stream nên không vỡ, nhưng test stream khác có thể kỵ. → Task 3 Step 4 chạy **toàn bộ** `dotnet test` (không filter) và Task 14 chạy lại — nếu test nào kỵ, sửa test đó theo hướng filter event `HeadersSent == true` ra khỏi sequence, KHÔNG sửa behavior publish.
2. **Received-update khi node đang chạy** (G2/G3 publish giữa chừng) không được kéo dot về Hàng đợi. → Task 7 test `ReceivedOnExistingNode_MergesFieldsWithoutResettingStage` (node ở `Attempt`, nhận `Received` → Stage giữ `Attempt`, field merge, Route giữ nguyên).
3. **Thứ tự publish Parked** — `Parked` chỉ xảy ra sau khi request đã `Take` (nên `DispatchStarted` đã publish trước); nếu đảo thứ tự → node Parked không có route/màu sai. → Task 4 test khẳng định `DispatchStarted` đứng trước `Parked` trong ring.
4. **Cooldown query DB lỗi/track trễ** — lỗi mạng phải giữ map cũ + log một lần, loop render không chết; countdown hết hạn phải tự tắt badge dù chưa refresh. → Task 6 test `SnapshotFailure_ReturnsNone_WithoutThrowing` (null + onError) và `Resolve_FiltersExpiredCooldowns_ReturnsEmpty`; Task 13 build + logic `DownUntil > now` lúc render.
5. **Wire sáng sai do state cũ** — điều kiện wire phải dựa trên node **còn hạn** (terminal đã fade là node đã bị gỡ). → Task 12 dùng `_nodes.Values.Any(...)` trên dictionary đã qua `RemoveExpired` mỗi tick — test gián tiếp: Task 4/5 không thêm node terminal vĩnh viễn; kiểm chứng bằng manual checklist §5.4-6/7.

## File Structure

| Hành động | File | Trách nhiệm |
|---|---|---|
| Sửa | `src/RouterBalancing.Core/Engine/ITraceFeed.cs` | `TraceStage` +`Parked`; `TraceEvent` +3 field optional cuối record |
| Sửa | `src/RouterBalancing.Core/Engine/ApiMonitorStore.cs` | case `Parked => current` tường minh (ignore, Live-Trace-only) |
| Sửa | `src/RouterBalancing.Core/Server/ProxyApp.cs` | publish `Received+Priority+Endpoint` (G3+G5); publish `Received+HeadersSent` sau flush stream (G2) |
| Sửa | `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` | publish `Parked` trong `ReenqueueForPark` (G1) |
| Sửa | `src/RouterBalancing.Core/Engine/RequestQueue.cs` | ctor `ITraceFeed?`; publish `Received`-update khi demote/set-priority (G3) |
| Tạo | `src/RouterBalancing.Core/Proxies/CooldownTracker.cs` | proxy down → `accountName → max(DownUntil)`; inject `Func<>`/`Action<Exception>?`; fail → `null` (giữ map cũ) |
| Tạo | `src/RouterBalancing.Core/Engine/TraceNodeState.cs` | state node hiển thị (rời Razor) + enum `TraceAnchor` |
| Tạo | `src/RouterBalancing.Core/Engine/TraceNodeAggregator.cs` | `Apply` (merge rule), `AnchorOf`, `PriorityTag` — unit-test được |
| Sửa | `src/RouterBalancing.Core/Localization/Translations.cs` | 7 key × 2 dict (EN dòng ~34, VI dòng ~420) |
| Sửa | `router-balancing/Components/Shared/RequestTrace.razor` | wires, badges, park ring, endpoint highlight, cooldown chip, delegate sang aggregator |
| Sửa | `router-balancing/Components/Shared/TraceColors.cs` | case `Parked` tường minh → `trace-dot--white` |
| Tạo | `router balancing test/Engine/TraceNodeAggregatorTests.cs` | merge rule / anchor / priority tag |
| Tạo | `router balancing test/Proxies/CooldownTrackerTests.cs` | resolve 4 đường + failure |
| Sửa | `router balancing test/Engine/ApiMonitorStoreTests.cs` | `ParkedEvent_KeepsCurrentState` |
| Sửa | `router balancing test/Engine/DispatcherLoopTests.cs` | `ReenqueueForPark_PublishesParkedStage` (clone fixture :860) |
| Sửa | `router balancing test/Engine/RequestQueueTests.cs` | 2 test demote/promote publish |
| Sửa | `router balancing test/Server/TraceIntegrationTests.cs` | `Enqueue_PublishesReceivedEvent_WithPriorityAndEndpoint` |
| Sửa | `router balancing test/Server/ProxyQueueIntegrationTests.cs` | mở rộng test early-headers sẵn có (G2) |

Dependency: Task 1 → (2,3,4,5) → (6,7) → 8 → (9,10,11,12,13 tuần tự, cùng 1 file Razor) → 14.

---

### Task 1: Contract nền — `TraceStage.Parked` + 3 field `TraceEvent`

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ITraceFeed.cs:4-43` (enum + record)
- Modify: `src/RouterBalancing.Core/Engine/ApiMonitorStore.cs:207-238` (switch)
- Test: `router balancing test/Engine/ApiMonitorStoreTests.cs`

**Interfaces:**
- Consumes: không có (đầu tiên).
- Produces:
  - `enum TraceStage { Received, DispatchStarted, Attempt, Finished, Canceled, Parked }` — `Parked` thêm **cuối** enum (không đổi value của các giá trị cũ).
  - `TraceEvent(..., DateTimeOffset At, string? Mode = null, bool? AttemptDone = null, RequestPriority? Priority = null, string? Endpoint = null, bool? HeadersSent = null)` — 3 field optional cuối, default `null` → mọi call site positional hiện tại không đổi.
  - `ApiMonitorStore` case `TraceStage.Parked => current`.

- [ ] **Step 1: Viết test `ParkedEvent_KeepsCurrentState` (ApiMonitorStoreTests)**

Pattern theo test `RecordError429_ThenFeedFinishedSuccess_EndsDoneStatus200` trong cùng file (dùng helper `Ev(...)` + `_store.StartRequest` + `_feed.Publish`):

```csharp
[Fact]
public void ParkedEvent_KeepsCurrentState()
{
    _store.StartRequest("r1", "m1", Bytes(Prompt));
    _feed.Publish(Ev("r1", TraceStage.DispatchStarted, "m1", At));
    Assert.Equal(ApiCallState.Running, _store.Find("r1")!.State);

    _feed.Publish(Ev("r1", TraceStage.Parked, "m1", At));

    // Marker chỉ dành cho Live Trace (spec §2.5) — monitor không đổi state
    Assert.Equal(ApiCallState.Running, _store.Find("r1")!.State);
}
```

- [ ] **Step 2: Chạy test — verify PASS ngay (wildcard `_ => current` hiện đã cover)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ParkedEvent_KeepsCurrentState"`
Expected: PASS — đây là characterization test; Step 3 biến hành vi ngầm thành case tường minh.

- [ ] **Step 3: Sửa `ITraceFeed.cs` + `ApiMonitorStore.cs`**

- Enum: thêm `Parked` cuối enum, XML doc: `Request đã từng dispatch nhưng bị đẩy về queue chờ slot (ReenqueueForPark) — spec G1.`
- Record: thêm 3 param cuối đúng thứ tự `RequestPriority? Priority = null, string? Endpoint = null, bool? HeadersSent = null` + XML doc 3 param (G3/G5/G2).
- `ApiMonitorStore.OnPublished`: thêm dòng `TraceStage.Parked => current,` **trước** `_ => current` kèm comment `// Parked: marker Live Trace — monitor giữ nguyên state (spec §2.5)`.

- [ ] **Step 4: Build solution — compiler quét switch thiếu case**

Run: `dotnet build router-balancing.slnx`
Expected: 0W/0E. Nếu build lỗi ở switch nào đó thiếu `Parked` → thêm case theo hành vi "giữ nguyên hiện状" (mọi switch khác đều là ignore/terminal).

- [ ] **Step 5: Chạy lại test + commit**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ApiMonitorStoreTests"`
Expected: PASS (toàn bộ ApiMonitorStoreTests).

```bash
git add src/RouterBalancing.Core/Engine/ITraceFeed.cs src/RouterBalancing.Core/Engine/ApiMonitorStore.cs "router balancing test/Engine/ApiMonitorStoreTests.cs"
git commit -m "feat: add Parked stage and trace event marker fields"
```

---

### Task 2: G3+G5 — publish `Received` mang `Priority` + `Endpoint`

**Files:**
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs:204-205`
- Test: `router balancing test/Server/TraceIntegrationTests.cs`

**Interfaces:**
- Consumes: Task 1 (`TraceEvent` 3 field mới).
- Produces: mọi `Received` đầu của request tại endpoint mang `Priority` (`RequestPriority`, từ `request.Priority` — parse sẵn tại `ProxyApp.cs:169`) và `Endpoint` (`"chat"` | `"responses"` — `endpoint.ToString().ToLowerInvariant()`).

- [ ] **Step 1: Viết test `Enqueue_PublishesReceivedEvent_WithPriorityAndEndpoint` (TraceIntegrationTests)**

```csharp
[Fact]
public async Task Enqueue_PublishesReceivedEvent_WithPriorityAndEndpoint()
{
    SeedProvider(maxConcurrent: 4, "m1");
    var client = await StartAsync(new StubUpstream());

    var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
    {
        Content = ChatBody("m1"),
    };
    request.Headers.Add("X-Priority", "max");
    var response = await client.SendAsync(request);

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    var id = response.Headers.GetValues("X-Request-Id").Single();
    await WaitUntilAsync(() => EventsOf(id).Any(e => e.Stage == TraceStage.Finished),
        "terminal Finished phải có trong feed");

    var received = EventsOf(id).Single(e => e.Stage == TraceStage.Received);
    Assert.Equal(RequestPriority.Highest, received.Priority);   // "max" → Highest (parser)
    Assert.Equal("chat", received.Endpoint);
    Assert.Null(received.HeadersSent);                          // non-stream không publish G2 (spec E6)
}
```

- [ ] **Step 2: Chạy test — verify FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~Enqueue_PublishesReceivedEvent_WithPriorityAndEndpoint"`
Expected: FAIL — `Priority` là `null` (chưa publish).

- [ ] **Step 3: Sửa publish tại `ProxyApp.RunQueueFirstAsync`**

Thay dòng 204-205 bằng (named args cho 3 field mới — không đụng 9 param positional cũ):

```csharp
trace.Publish(new TraceEvent(id, TraceStage.Received, request.Model,
    null, null, null, null, null, DateTimeOffset.Now,
    Priority: request.Priority,
    Endpoint: endpoint.ToString().ToLowerInvariant()));
```

- [ ] **Step 4: Chạy test — verify PASS**

Run: lệnh Step 2
Expected: PASS.

- [ ] **Step 5: Chạy toàn bộ Server tests (phòng ngừa sequence khác) + commit**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~router_balancing_test.Server"`
Expected: PASS (field thêm không đổi stage sequence).

```bash
git add src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/Server/TraceIntegrationTests.cs"
git commit -m "feat: publish priority and endpoint on trace received event"
```

### Task 3: G2 — publish `HeadersSent` sau flush stream

**Files:**
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs:241-243`
- Test: `router balancing test/Server/ProxyQueueIntegrationTests.cs:309` (mở rộng test sẵn có — đúng yêu cầu spec §5.1 "mở rộng test early-headers sẵn có")

**Interfaces:**
- Consumes: Task 1 (`HeadersSent` field).
- Produces: request **stream** đã commit 200 → 1 event `Stage=Received, HeadersSent=true` publish ngay sau `FlushAsync` thành công (trước `RunStreamLoopAsync`); non-stream không publish.

- [ ] **Step 1: Mở rộng test `Stream_WhenProviderSaturated_FlushesHeadersImmediately` — thêm assert feed**

Sau block assert head (sau dòng `Assert.Equal(1, upstream.Calls);`), trước khi đọc stream content:

```csharp
var feed = _app!.Services.GetRequiredService<ITraceFeed>();
var streamId = resp.Headers.GetValues("X-Request-Id").Single();
for (var i = 0; i < 100 && !feed.Snapshot().Any(e => e.RequestId == streamId && e.HeadersSent == true); i++)
    await Task.Delay(50);
Assert.Contains(feed.Snapshot(),
    e => e.RequestId == streamId && e.Stage == TraceStage.Received && e.HeadersSent == true);
```

Lưu ý import: file dùng `GetRequiredService`/`ITraceFeed` sẵn (đã dùng `IRequestQueue` + DI ở harness) — thêm `using Microsoft.Extensions.DependencyInjection;` / `using RouterBalancing.Core.Engine;` nếu compiler yêu cầu.

- [ ] **Step 2: Chạy test — verify FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~Stream_WhenProviderSaturated_FlushesHeadersImmediately"`
Expected: FAIL — chưa publish `HeadersSent` (vòng poll hết 5s rồi `Assert.Contains` fail).

- [ ] **Step 3: Publish tại `ProxyApp.RunQueueFirstAsync` — sau `FlushAsync`, trước `RunStreamLoopAsync`**

Chèn ngay sau dòng 241 (`await ctx.Response.Body.FlushAsync(ctx.RequestAborted);`):

```csharp
// G2: head đã commit 200 — đánh dấu cho trace (stream only; non-stream không có flush point)
trace.Publish(new TraceEvent(id, TraceStage.Received, request.Model,
    null, null, null, null, null, DateTimeOffset.Now, HeadersSent: true));
```

- [ ] **Step 4: Chạy test — verify PASS, rồi chạy TOÀN BỘ test project (Review Focus #1)**

Run: `dotnet test "router balancing test/router balancing test.csproj"`
Expected: PASS — nếu có test sequence đúng-từng-bước nào của **stream** request fail vì event `Received` thừa: sửa TEST (filter `e.HeadersSent != true` khỏi assertion sequence), **không** sửa publish.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/Server/ProxyQueueIntegrationTests.cs"
git commit -m "feat: publish headers-sent marker for stream requests"
```

---

### Task 4: G1 — publish `Parked` trong `ReenqueueForPark`

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs:599-629`
- Test: `router balancing test/Engine/DispatcherLoopTests.cs` (clone fixture `Loop_WhenNextCandidateHasNoCapacity_ReenqueuesAndWakesWhenSlotFrees`, dòng :860)

**Interfaces:**
- Consumes: Task 1 (`TraceStage.Parked`).
- Produces: mọi lần `ReenqueueForPark` re-enqueue thành công → event `Parked` mang `Id`, `Model` (`request.Model`); `Route=null`, `Attempt=null` (node đã có Route từ event `Attempt` trước — merge rule giữ lại, spec §2.5).

- [ ] **Step 1: Viết test `ReenqueueForPark_PublishesParkedStage` (DispatcherLoopTests)**

Clone fixture `Loop_WhenNextCandidateHasNoCapacity_ReenqueuesAndWakesWhenSlotFrees` (dòng 860-889) giữ nguyên toàn bộ flow (p1/p2 maxConcurrent 1, `CapacityCornerUpstream`, r2 giữ slot p1, r1 429 → advance p1 kẹt → park), thêm subscribe + assert:

```csharp
[Fact]
public async Task ReenqueueForPark_PublishesParkedStage()
{
    var p1 = SeedProvider("p1", maxConcurrent: 1, modelId: "m1");
    var p2 = SeedProvider("p2", maxConcurrent: 1, modelId: "m1");
    var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
    var selector = new CountingSelector(new ModelSelector(_executions));
    var upstream = new CapacityCornerUpstream();
    var log = new CapturingLog();
    await StartAsync(resolver, selector, upstream, log);

    var r2 = Req("req00002");
    _queue.Enqueue(r2);
    await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5));

    var r1 = Req("req00001");
    _queue.Enqueue(r1);
    await WaitUntilAsync(() => _queue.Contains("req00001") && upstream.Calls == 2);
    await Task.Delay(200); // chắc chắn đã park

    // G1: node nhận stage Parked SAU khi đã dispatch lần đầu (Review Focus #3)
    var events = _trace.Snapshot().Where(e => e.RequestId == "req00001").ToList();
    var parked = events.Single(e => e.Stage == TraceStage.Parked);
    Assert.True(events.IndexOf(parked) > events.FindIndex(e => e.Stage == TraceStage.DispatchStarted),
        "Parked phải đứng sau DispatchStarted — request đã từng dispatch mới bị re-enqueue (spec §2.1)");
    Assert.Equal("m1", parked.Model);

    // Dọn đúng fixture gốc: release → cả 2 hoàn thành
    upstream.Release();
    Assert.IsType<DispatchOutcome.Handled>(await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    Assert.IsType<DispatchOutcome.Handled>(await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
}
```

- [ ] **Step 2: Chạy test — verify FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ReenqueueForPark_PublishesParkedStage"`
Expected: FAIL — `Single(e => e.Stage == TraceStage.Parked)` không tìm thấy.

- [ ] **Step 3: Publish tại `ReenqueueForPark` — sau khi `queue.Enqueue` thành công**

Trong `ReenqueueForPark`, ngay sau nhánh `if (!queue.Enqueue(request)) { ... return; }` và **trước** check cancel phía dưới (tức đã chắc item nằm trong queue):

```csharp
// G1: marker park — chỉ publish khi re-enqueue thật sự thành công;
// Route/Attempt null để merge không ghi đè Route đã biết từ attempt trước (spec §2.5)
trace.Publish(new TraceEvent(request.Id, TraceStage.Parked, request.Model,
    null, null, null, null, null, DateTimeOffset.Now));
```

Không publish ở nhánh `Enqueue` fail (G6 bị loại — spec §1.4) và không publish khi bị cancel ngay tại đây (request rời queue, endpoint sẽ publish `Canceled`).

- [ ] **Step 4: Chạy test — verify PASS**

Run: lệnh Step 2
Expected: PASS.

- [ ] **Step 5: Chạy toàn bộ DispatcherLoopTests + commit**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~DispatcherLoopTests"`
Expected: PASS.

```bash
git add src/RouterBalancing.Core/Engine/DispatcherLoop.cs "router balancing test/Engine/DispatcherLoopTests.cs"
git commit -m "feat: publish parked stage on capacity reenqueue"
```

---

### Task 5: G3 demote — `RequestQueue` publish `Received`-update

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/RequestQueue.cs` (ctor, `Enqueue`, `SetPriority`, `DemoteOtherHighest`)
- Test: `router balancing test/Engine/RequestQueueTests.cs`

**Interfaces:**
- Consumes: Task 1 (`TraceEvent.Priority`).
- Produces:
  - `public RequestQueue(ITraceFeed? trace = null)` — nullable + default → test cũ `new RequestQueue()` không vỡ (E1); DI (`AddSingleton<IRequestQueue, RequestQueue>` tại `ProxyApp.cs:85`) resolve `ITraceFeed` đã đăng ký.
  - Publish `Stage=Received, Priority=<mới>` **ngoài lock** cho: (a) request bị `DemoteOtherHighest` hạ Highest→High, (b) request có `SetPriority` đổi thật.
  - `DemoteOtherHighest(keep)` trả `List<ProxyRequest>` (các request bị hạ) thay vì `void`.

- [ ] **Step 1: Viết 2 test (RequestQueueTests)**

```csharp
[Fact]
public void EnqueueHigherPriority_DemotesQueuedRequest_PublishesReceivedUpdateWithNewPriority()
{
    var feed = new TraceFeed(new NullLog());
    var queue = new RequestQueue(feed);

    queue.Enqueue(Req("h1", RequestPriority.Highest));
    queue.Enqueue(Req("h2", RequestPriority.Highest)); // luật 1-Highest → h1 bị hạ

    var update = Assert.Single(feed.Snapshot());
    Assert.Equal("h1", update.RequestId);
    Assert.Equal(TraceStage.Received, update.Stage);
    Assert.Equal(RequestPriority.High, update.Priority);
    Assert.Equal(RequestPriority.High, queue.Snapshot().Single(r => r.Id == "h1").Priority); // sync state
}

[Fact]
public void SetPriority_PromoteToHighest_PublishesDemotedAndPromotedUpdates()
{
    var feed = new TraceFeed(new NullLog());
    var queue = new RequestQueue(feed);
    queue.Enqueue(Req("h1", RequestPriority.Highest));
    queue.Enqueue(Req("n1", RequestPriority.Normal));

    Assert.True(queue.SetPriority("n1", RequestPriority.Highest));

    var events = feed.Snapshot();
    Assert.Equal(RequestPriority.High, events.Single(e => e.RequestId == "h1").Priority);
    Assert.Equal(RequestPriority.Highest, events.Single(e => e.RequestId == "n1").Priority);
    Assert.All(events, e => Assert.Equal(TraceStage.Received, e.Stage));
}
```

Import: `using RouterBalancing.Core.Engine;` (có sẵn trong file), `NullLog` từ `TestDoubles.cs` (cùng assembly).

- [ ] **Step 2: Chạy 2 test — verify FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~PublishesReceivedUpdate|FullyQualifiedName~PublishesDemotedAndPromoted"`
Expected: FAIL — feed rỗng (chưa publish).

- [ ] **Step 3: Sửa `RequestQueue`**

- Thêm field `private readonly ITraceFeed? _trace;` + ctor `public RequestQueue(ITraceFeed? trace = null) => _trace = trace;`.
- `DemoteOtherHighest(keep)` → trả `List<ProxyRequest>` (collect item bị hạ, return `[]` khi không có).
- `Enqueue`: trong lock gán `demoted = ...` (chỉ khi `Priority == Highest`); sau khi thoát lock: publish từng item trong `demoted` **rồi** `Changed?.Invoke()` (giữ pattern event ngoài lock).
- `SetPriority`: capture `changed` (request đổi priority, khi khác giá trị cũ) + `demoted`; sau lock: publish `changed` + từng `demoted`, rồi `Changed?.Invoke()`.
- Helper private:

```csharp
private void PublishPriorityUpdate(ProxyRequest request) =>
    _trace?.Publish(new TraceEvent(request.Id, TraceStage.Received, request.Model,
        null, null, null, null, null, DateTimeOffset.Now, Priority: request.Priority));
```

Publish **ngoài** `_lock` (pattern `Changed?.Invoke()` ngoài lock — subscriber không được chạy khi đang giữ lock queue).

- [ ] **Step 4: Chạy 2 test — verify PASS, rồi toàn bộ RequestQueueTests**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~RequestQueueTests"`
Expected: PASS — test cũ dùng `new RequestQueue()` vẫn xanh (default `null` → bỏ qua publish, E1).

- [ ] **Step 5: Build + commit**

Run: `dotnet build router-balancing.slnx` → 0W/0E.

```bash
git add src/RouterBalancing.Core/Engine/RequestQueue.cs "router balancing test/Engine/RequestQueueTests.cs"
git commit -m "feat: publish priority updates when queue demotes highest"
```

---

### Task 6: `CooldownTracker` (G4 backend)

**Files:**
- Create: `src/RouterBalancing.Core/Proxies/CooldownTracker.cs`
- Test: `router balancing test/Proxies/CooldownTrackerTests.cs`

**Interfaces:**
- Consumes: `IProxyPool.Snapshot() → IReadOnlyList<ProxyRuntimeStatus>` (`ProxyRuntimeStatus(long Id, string Endpoint, bool IsDown, DateTimeOffset? DownUntil)`); `IProxyService.GetReverseAssignmentsAsync(CancellationToken) → IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>` (`ProxyUsage(long ScopeId, string ScopeName, bool IsProvider, ProxyMode? Mode)` — key = proxyId); `IProxyService.GetAssignmentsAsync(long, CancellationToken) → IReadOnlyList<ProxyAssignment>` (provider row `IsProvider=true` + mọi account row kèm `ProxyIds`, kể cả rỗng).
- Produces (Task 13 dùng đúng signature này):

```csharp
namespace RouterBalancing.Core.Proxies;

public sealed class CooldownTracker(
    Func<IReadOnlyList<ProxyRuntimeStatus>> snapshot,
    Func<CancellationToken, Task<IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>>> reverseAssignments,
    Func<long, CancellationToken, Task<IReadOnlyList<ProxyAssignment>>> providerAssignments,
    Action<Exception>? onError = null)
{
    /// Trả map accountName → max(DownUntil); empty = authoritative "không còn cooldown" (clear badge);
    /// null = lỗi source (giữ map cũ ở caller) + đã gọi onError.
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>?> ResolveAsync(CancellationToken ct);
}
```

Logic `ResolveAsync` (thứ tự quan trọng):
1. `try` bọc toàn bộ; mọi exception → `onError?.Invoke(ex); return null;`.
2. `down` = `snapshot()` lọc `IsDown && DownUntil > now` → `Dictionary<long, DateTimeOffset>`; **rỗng → return empty dict** (không đụng DB — authoritative clear khi cooldown hết hạn).
3. `reverse = await reverseAssignments(ct)`; với mỗi `(proxyId, usages)` có `proxyId ∈ down`:
   - usage `!IsProvider` → `Merge(result, ScopeName, DownUntil)` (account gắn trực tiếp).
   - usage `IsProvider` → dedup `HashSet<long>`; với mỗi provider: `await providerAssignments(ScopeId, ct)` → account `!IsProvider && ProxyIds.Count == 0` (kế thừa D1) → `Merge(result, Name, DownUntil)`. Account có `ProxyIds` khác rỗng **không** kế thừa (proxy riêng của nó đã xử lý qua reverse nếu down).
4. `Merge` = giữ `max(DownUntil)` khi key trùng.

- [ ] **Step 1: Viết 5 test (CooldownTrackerTests)**

Dựng tracker với lambda stub — không cần mock framework. `At = DateTimeOffset.UtcNow`; `T = At + TimeSpan.FromSeconds(45)`:

```csharp
[Fact]
public async Task Resolve_UsesAccountProxies_WhenAssigned()
{
    var tracker = new CooldownTracker(
        () => [new ProxyRuntimeStatus(1, "http://p1", true, T)],
        _ => Task.FromResult<IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>>(
            new Dictionary<long, IReadOnlyList<ProxyUsage>>
            {
                [1] = [new ProxyUsage(10, "acc1", IsProvider: false, null)],
            }),
        (_, _) => throw new InvalidOperationException("không được gọi"));

    var map = await tracker.ResolveAsync(CancellationToken.None);

    Assert.NotNull(map);
    Assert.Equal(T, map!["acc1"]);
}

[Fact]
public async Task Resolve_FallsBackToProviderProxies()
{
    var tracker = new CooldownTracker(
        () => [new ProxyRuntimeStatus(1, "http://p1", true, T)],
        _ => Task.FromResult<IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>>(
            new Dictionary<long, IReadOnlyList<ProxyUsage>>
            {
                [1] = [new ProxyUsage(100, "prov", IsProvider: true, null)],
            }),
        (pid, _) => Task.FromResult<IReadOnlyList<ProxyAssignment>>(
        [
            new() { Id = 100, Name = "prov", IsProvider = true },
            new() { Id = 11, Name = "inherited", ProxyIds = [] },                    // kế thừa
            new() { Id = 12, Name = "explicit", ProxyIds = [99] },                   // proxy 99 không down
        ]));

    var map = await tracker.ResolveAsync(CancellationToken.None);

    Assert.NotNull(map);
    Assert.Equal(T, map!["inherited"]);
    Assert.False(map.ContainsKey("explicit"));
}

[Fact]
public async Task Resolve_ReturnsNone_WhenDirect()
{
    // Proxy sống / không proxy down → map rỗng (authoritative) — caller clear badge
    var tracker = new CooldownTracker(
        () => [new ProxyRuntimeStatus(1, "http://p1", false, null)],
        _ => throw new InvalidOperationException("không được gọi"),
        (_, _) => throw new InvalidOperationException("không được gọi"));

    var map = await tracker.ResolveAsync(CancellationToken.None);

    Assert.NotNull(map);
    Assert.Empty(map!);
}

[Fact]
public async Task Resolve_FiltersExpiredCooldowns_ReturnsEmpty()
{
    var tracker = new CooldownTracker(
        () => [new ProxyRuntimeStatus(1, "http://p1", true, At)], // DownUntil <= now
        _ => throw new InvalidOperationException("không được gọi"),
        (_, _) => throw new InvalidOperationException("không được gọi"));

    var map = await tracker.ResolveAsync(CancellationToken.None);

    Assert.NotNull(map);
    Assert.Empty(map!); // countdown hết → badge tự tắt dù pool chưa flip IsDown
}

[Fact]
public async Task SnapshotFailure_ReturnsNone_WithoutThrowing()
{
    Exception? seen = null;
    var tracker = new CooldownTracker(
        () => throw new IOException("db locked"),
        _ => Task.FromResult<IReadOnlyDictionary<long, IReadOnlyList<ProxyUsage>>>(new Dictionary<long, IReadOnlyList<ProxyUsage>>()),
        (_, _) => Task.FromResult<IReadOnlyList<ProxyAssignment>>([]),
        ex => seen = ex);

    var map = await tracker.ResolveAsync(CancellationToken.None);

    Assert.Null(map);              // null = giữ map cũ ở caller (spec §3.5)
    Assert.IsType<IOException>(seen);
}
```

Ghi chú tên test: 2 test đầu + `SnapshotFailure` đúng tên spec §5.2; `Resolve_ReturnsNone_WhenDirect` giữ tên spec nhưng assert **empty** (semantic: "không có cooldown" ≠ "lỗi" — lỗi là `null`), test thứ 4 thêm cho Review Focus #4.

- [ ] **Step 2: Chạy test — verify FAIL (compile error: class chưa tồn tại)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~CooldownTrackerTests"`
Expected: FAIL/compile error `CooldownTracker` không tồn tại.

- [ ] **Step 3: Implement `CooldownTracker.cs` theo Interfaces phía trên**

- [ ] **Step 4: Chạy test — verify PASS**

Run: lệnh Step 2 → PASS.

- [ ] **Step 5: Build + commit**

Run: `dotnet build router-balancing.slnx` → 0W/0E.

```bash
git add src/RouterBalancing.Core/Proxies/CooldownTracker.cs "router balancing test/Proxies/CooldownTrackerTests.cs"
git commit -m "feat: add cooldown tracker for proxy down to account mapping"
```

### Task 7: `TraceNodeAggregator` — tách merge rule/anchor/tag khỏi Razor

**Files:**
- Create: `src/RouterBalancing.Core/Engine/TraceNodeState.cs`
- Create: `src/RouterBalancing.Core/Engine/TraceNodeAggregator.cs`
- Modify: `router-balancing/Components/Shared/RequestTrace.razor` (nested `TraceNode` class :511, `ApplyEvent` :708, `AnchorOf` :781, `ColorClass` :1004)
- Test: `router balancing test/Engine/TraceNodeAggregatorTests.cs`

**Interfaces:**
- Consumes: Task 1 (`TraceStage.Parked`, `TraceEvent` 3 field).
- Produces:

```csharp
namespace RouterBalancing.Core.Engine;

/// anchor ngữ nghĩa — UI map sang pixel (Attempt cần layout chip nên resolve ở component).
public enum TraceAnchor { Queue, Dispatch, Attempt, Client }

public sealed class TraceNodeState
{
    public required string Id { get; init; }
    public string Model { get; set; } = string.Empty;
    public TraceStage Stage { get; set; } = TraceStage.Received;
    public TraceRoute? Route { get; set; }
    public bool? Success { get; set; }
    public int? Status { get; set; }
    public string? Mode { get; set; }
    public RequestPriority? Priority { get; set; }   // G3
    public string? Endpoint { get; set; }            // G5
    public bool? HeadersSent { get; set; }           // G2
    public DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class TraceNodeAggregator(TimeSpan terminalTtl)
{
    public Dictionary<string, TraceNodeState> Nodes { get; } = [];

    /// Create-or-merge theo merge rule spec §2.5: field có giá trị mới thì ghi, null thì giữ;
    /// Stage LUÔN nhận e.Stage (Parked là stage thật — dot chuyển anchor).
    public TraceNodeState Apply(TraceEvent e);

    public static TraceAnchor AnchorOf(TraceNodeState node);
    // Received | Parked → Queue; DispatchStarted → Dispatch; Attempt → Attempt; _ → Client

    public static string? PriorityTag(RequestPriority? priority);
    // Highest → "trace-tag--prio-highest"; High → "trace-tag--prio-high"; _ → null (Normal ẩn, spec E)
}
```

`Apply` chuyển nguyên khối hiện tại của `ApplyEvent` (RequestTrace :708-743): tạo node khi thiếu (ReceivedAt = `e.At`), ghi `Model`/`Stage` luôn, các field `Route`/`Status`/`Success`/`Mode` chỉ khi `e.IsNotNull`, + 3 field mới `Priority`/`Endpoint`/`HeadersSent` cũng chỉ khi không null, `ExpiresAt = terminal` → `e.At + terminalTtl` else `null`.

- [ ] **Step 1: Viết 3 test (TraceNodeAggregatorTests)**

```csharp
[Fact]
public void ReceivedOnExistingNode_MergesFieldsWithoutResettingStage()
{
    var agg = new TraceNodeAggregator(TimeSpan.FromSeconds(10));
    var route = new TraceRoute(null, "p1", "a1");
    agg.Apply(new TraceEvent("r1", TraceStage.Attempt, "m1", route, 1, null, null, null, DateTimeOffset.Now));

    var node = agg.Apply(new TraceEvent("r1", TraceStage.Received, "m1",
        null, null, null, null, null, DateTimeOffset.Now,
        Priority: RequestPriority.High, HeadersSent: true));

    Assert.Equal(TraceStage.Attempt, node.Stage);        // KHÔNG reset — G2/G3 không kéo dot về queue
    Assert.Equal(RequestPriority.High, node.Priority);
    Assert.True(node.HeadersSent);
    Assert.Same(route, node.Route);                      // Route null trong event → giữ route đã biết
    Assert.Equal(TraceAnchor.Attempt, TraceNodeAggregator.AnchorOf(node));
}

[Fact]
public void ParkedEvent_MovesNodeToQueueAnchor()
{
    var agg = new TraceNodeAggregator(TimeSpan.FromSeconds(10));
    agg.Apply(new TraceEvent("r1", TraceStage.DispatchStarted, "m1", null, null, null, null, null, DateTimeOffset.Now));

    var node = agg.Apply(new TraceEvent("r1", TraceStage.Parked, "m1", null, null, null, null, null, DateTimeOffset.Now));

    Assert.Equal(TraceStage.Parked, node.Stage);
    Assert.Equal(TraceAnchor.Queue, TraceNodeAggregator.AnchorOf(node)); // dot chuyển về Hàng đợi
    Assert.Null(node.ExpiresAt);                           // Parked không terminal — node sống mãi
}

[Fact]
public void PriorityTag_ReturnsAmberForHighest_BlueForHigh_NoneForNormal()
{
    Assert.Equal("trace-tag--prio-highest", TraceNodeAggregator.PriorityTag(RequestPriority.Highest));
    Assert.Equal("trace-tag--prio-high", TraceNodeAggregator.PriorityTag(RequestPriority.High));
    Assert.Null(TraceNodeAggregator.PriorityTag(RequestPriority.Normal));
}
```

- [ ] **Step 2: Chạy test — verify FAIL (compile error)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~TraceNodeAggregatorTests"`
Expected: FAIL/compile error.

- [ ] **Step 3: Implement 2 file Core theo Interfaces + test**

- [ ] **Step 4: Chạy test — verify PASS**

Run: lệnh Step 2 → PASS.

- [ ] **Step 5: Refactor `RequestTrace.razor` dùng aggregator**

- Xóa nested class `TraceNode` (:511-524) → thay `TraceNode` bằng `TraceNodeState` (Core) ở toàn bộ file (`_nodes`, `TraceNode` param các record/helpers).
- Thay `Dictionary<string, TraceNode> _nodes` bằng instance `private readonly TraceNodeAggregator _agg = new(TerminalTtl);` + alias property `private Dictionary<string, TraceNodeState> Nodes => _agg.Nodes;` (hoặc sửa trực tiếp mọi `_nodes` → `_agg.Nodes` — chọn 1 cách, nhất quán).
- `ApplyEvent(e)` thành wrapper giữ bookkeeping cap-drop:

```csharp
private void ApplyEvent(TraceEvent e)
{
    var isNew = !_agg.Nodes.ContainsKey(e.RequestId);
    _agg.Apply(e);
    if (isNew && _droppedIds.Remove(e.RequestId))
        _overflow = Math.Max(0, _overflow - 1);
}
```

- `AnchorOf` pixel giữ nguyên chữ ký, delegate theo anchor ngữ nghĩa:

```csharp
private (int X, int Y) AnchorOf(TraceNodeState n) => TraceNodeAggregator.AnchorOf(n) switch
{
    TraceAnchor.Queue => (DotStartX, QueueTop + DotRowOffsetY),
    TraceAnchor.Dispatch => (DotStartX, DispatchTop + DotRowOffsetY),
    TraceAnchor.Attempt => AttemptAnchor(n),
    _ => (DotStartX, ClientTop + DotRowOffsetY),
};
```

- `ColorClass` (:1004) giữ nguyên (đọc `n.Stage`/`n.Success` — đã có `Parked` via wildcard `TraceColors`).
- `RemoveExpired`/`EnforceCap`/`RebuildBranchLayout`/`BuildGroups` chỉ đổi tên kiểu — **không đổi logic**.

- [ ] **Step 6: Build solution (gate behavior-preserving refactor)**

Run: `dotnet build router-balancing.slnx`
Expected: 0W/0E. Chạy `dotnet test "router balancing test/router balancing test.csproj"` → xanh.

- [ ] **Step 7: Commit**

```bash
git add src/RouterBalancing.Core/Engine/TraceNodeState.cs src/RouterBalancing.Core/Engine/TraceNodeAggregator.cs router-balancing/Components/Shared/RequestTrace.razor "router balancing test/Engine/TraceNodeAggregatorTests.cs"
git commit -m "refactor: extract trace node state and merge rules to core"
```

---

### Task 8: i18n — 7 key × 2 dict

**Files:**
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (EN block quanh dòng 34, VI block quanh dòng 420 — chèn cạnh các key `trace.*` hiện có)
- Test: `router balancing test/Localization/TranslationParityTests.cs` (chạy lại, không sửa)

**Interfaces:**
- Consumes: không có.
- Produces: 7 key sau — Task 9-13 chỉ dùng đúng những key này:

| Key | English | Vietnamese |
|---|---|---|
| `trace.park.badge` | `waiting for slot` | `chờ slot` |
| `trace.headers.badge` | `200 ✓` | `200 ✓` |
| `trace.priority.high` | `High` | `High` |
| `trace.priority.highest` | `Highest` | `Highest` |
| `trace.cooldown` | `COOLDOWN · {0}s` | `COOLDOWN · {0}s` |
| `trace.legend.parked` | `parked` | `chờ slot` |
| `trace.legend.headers` | `headers sent` | `đã gửi headers` |

(Giá trị 2 dict giống nhau cho badge-symbol/technical-vocab là có chủ đích — parity test chỉ so **key set**; comment giải thích trong file: `// badge symbol/vocab API (X-Priority) giữ nguyên ở 2 ngôn ngữ`.)

- [ ] **Step 1: Chạy parity test trước — verify PASS (baseline)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~TranslationParity"`
Expected: PASS.

- [ ] **Step 2: Thêm 7 key vào `English` + 7 key vào `Vietnamese`**

Chèn ngay sau block `trace.*` hiện có của mỗi dict, cùng thứ tự.

- [ ] **Step 3: Chạy parity test — verify PASS (key set khớp)**

Run: lệnh Step 1 → PASS.

- [ ] **Step 4: Build + commit**

Run: `dotnet build router-balancing.slnx` → 0W/0E.

```bash
git add src/RouterBalancing.Core/Localization/Translations.cs
git commit -m "feat: add live trace marker translation keys"
```

---

### Task 9: UI G1 — park (ring + badge + anchor + count)

**Files:**
- Modify: `router-balancing/Components/Shared/RequestTrace.razor` (style block :12-271, dot loop :411-425, badge column mới, `QueuedCount` :575, legend :429-436)
- Modify: `router-balancing/Components/Shared/TraceColors.cs:22-29`

**Interfaces:**
- Consumes: Task 7 (`TraceNodeAggregator.AnchorOf`, `TraceNodeState.Stage`), Task 8 (`trace.park.badge`, `trace.legend.parked`).
- Produces: dot `Stage=Parked` = màu trắng + ring vàng đứt; badge `chờ slot` trong cột badge Hàng đợi; `QueuedCount` gồm cả `Parked`; legend +1 mục.

- [ ] **Step 1: `TraceColors.ForStage` — case tường minh**

Thêm trước wildcard: `TraceStage.Parked => "trace-dot--white",` + comment `// Parked: dot trắng như Received — khác biệt thể hiện qua ring vàng (spec §3.2)`.

- [ ] **Step 2: CSS trong `<style>` của `RequestTrace.razor`**

```css
/* Ring vàng đứt quanh dot đang chờ slot (G1 — mockup .lt-park-ring) */
.trace-dot--park {
    outline: 1.5px dashed #eab308;
    outline-offset: 2px;
}

/* Cột badge bên phải node Hàng đợi (mockup left 132 — G1/G2/G3 dùng chung) */
.trace-queue-badges {
    position: absolute;
    display: flex;
    gap: 4px;
    align-items: center;
    white-space: nowrap;
    z-index: 5;
}

.trace-badge--park {
    color: #eab308;
    border-color: #eab308;
    border-style: dashed;
}
```

- [ ] **Step 3: Markup — ring trên dot + badge column + QueuedCount**

- Dot loop (:416-418) thêm class park:

```razor
<div class="trace-dot @ColorClass(node)@(node.Stage == TraceStage.Parked ? " trace-dot--park" : "")" @key="node.Id"
     style="left:@(group.X + i * DotStepX)px;top:@(group.Y)px"
     @onclick="() => OpenDetail(node.Id)"></div>
```

- Ngay trong vòng `@foreach (var group in BuildGroups())` (sau khi vẽ dot của group, trước badge overflow), thêm cột badge **chỉ cho group tại anchor Hàng đợi** (x = 132 phải node queue, thoáng dọc đến 168 nơi block Combo bắt đầu):

```razor
@if (group.Y == QueueTop + DotRowOffsetY)
{
    var row = 0;
    foreach (var node in group.Shown)
    {
        if (node.Stage != TraceStage.Parked)
        {
            continue;   // Task 10 sẽ nới điều kiện này cho headers/priority
        }
        <div class="trace-queue-badges" style="left:132px;top:@(group.Y - 6 + row * 14)px"
             @key="@($"badge-{node.Id}")">
            <span class="trace-badge trace-badge--park">@L["trace.park.badge"]</span>
        </div>
        row++;
    }
}
```

- `QueuedCount` (:575): `_nodes.Values.Count(n => n.Stage is TraceStage.Received or TraceStage.Parked)` — parked **đang nằm trong queue thật** nên count phải gồm (spec E5-wire ①: `QueuedCount > 0` phải sáng cả khi chỉ có parked).

- [ ] **Step 4: Verify — build solution**

Run: `dotnet build router-balancing.slnx`
Expected: 0W/0E. (UI không có unit test — test project không reference MAUI project; hành vi verify bằng manual checklist §5.4-1 ở Task 14.)

- [ ] **Step 5: Commit**

```bash
git add router-balancing/Components/Shared/RequestTrace.razor router-balancing/Components/Shared/TraceColors.cs
git commit -m "feat: show parked marker in live trace"
```

### Task 10: UI G2+G3 — badge headers & priority tag (kèm demote merge)

**Files:**
- Modify: `router-balancing/Components/Shared/RequestTrace.razor` (style block, badge column của Task 9, `ApplyEvent` wrapper)

**Interfaces:**
- Consumes: Task 7 (`TraceNodeState.Priority`/`HeadersSent`, `TraceNodeAggregator.PriorityTag`), Task 8 (`trace.headers.badge`, `trace.priority.high/highest`).
- Produces: badge `200 ✓` khi `HeadersSent == true`; tag priority cạnh dot (Highest amber / High blue / Normal ẩn); demote → `Apply` merge đổi tag, dot đứng yên.

- [ ] **Step 1: CSS**

```css
/* Tag priority cạnh dot (G3) — amber Highest / blue High, Normal ẩn (spec §3.4) */
.trace-tag--prio-highest {
    color: #eab308;
    border-color: #eab308;
}

.trace-tag--prio-high {
    color: #3b82f6;
    border-color: #3b82f6;
}

/* Badge "200 ✓" khi stream đã commit header (G2 — mockup nét đứt xanh lá) */
.trace-badge--headers {
    color: #22c55e;
    border-color: #22c55e;
    border-style: dashed;
}
```

- [ ] **Step 2: Markup — mở rộng badge column (thay block `if (node.Stage != TraceStage.Parked) continue` của Task 9)**

```razor
@if (group.Y == QueueTop + DotRowOffsetY)
{
    var row = 0;
    foreach (var node in group.Shown)
    {
        var tag = TraceNodeAggregator.PriorityTag(node.Priority);
        var headers = node.HeadersSent == true;
        var park = node.Stage == TraceStage.Parked;
        if (tag is null && !headers && !park)
        {
            continue;
        }
        <div class="trace-queue-badges" style="left:132px;top:@(group.Y - 6 + row * 14)px"
             @key="@($"badge-{node.Id}")">
            @if (tag is not null)
            {
                <span class="trace-tag @tag">@(node.Priority == RequestPriority.Highest ? L["trace.priority.highest"] : L["trace.priority.high"])</span>
            }
            @if (headers)
            {
                <span class="trace-badge trace-badge--headers">@L["trace.headers.badge"]</span>
            }
            @if (park)
            {
                <span class="trace-badge trace-badge--park">@L["trace.park.badge"]</span>
            }
        </div>
        row++;
    }
}
```

Cột badge nằm ngoài node (x=132..~202 < Combo 210) → không chồng nhau dù nhiều node; các badge khác anchor (dispatch/client/chip) không có cột này — G2/G3 quan sát chính tại Hàng đợi (spec §3.3 "cạnh dot **tại Hàng đợi**"; §3.4 demote xảy ra khi đang chờ).

- [ ] **Step 3: Verify demote merge qua unit test (đã có ở Task 7) + build**

`ReceivedOnExistingNode_MergesFieldsWithoutResettingStage` đã ghim merge rule mà badge này dựa vào — không cần test Razor mới.

Run: `dotnet build router-balancing.slnx` → 0W/0E.

- [ ] **Step 4: Commit**

```bash
git add router-balancing/Components/Shared/RequestTrace.razor
git commit -m "feat: show headers and priority badges in live trace"
```

---

### Task 11: UI G5 — highlight dòng endpoint + title trên dot

**Files:**
- Modify: `router-balancing/Components/Shared/RequestTrace.razor` (paths :326, `ApplyEvent`, `Flush` :688, dot loop :416, style block)

**Interfaces:**
- Consumes: Task 2 (`TraceEvent.Endpoint`), Task 7 (state qua `ApplyEvent` wrapper).
- Produces: field component mới `_litEndpoint: string?`, `_litExpiresAt: DateTimeOffset?` — set trong `ApplyEvent` khi `e.Endpoint is not null`, auto-clear trong `Flush` khi quá hạn 2s.

- [ ] **Step 1: CSS**

```css
/* G5: dòng endpoint đang được request dùng — sáng xanh ~2s rồi fade về màu gốc */
.trace-node__path {
    transition: color .8s ease;
}

.trace-node__path--lit {
    color: #3b82f6;
    font-weight: 600;
}
```

- [ ] **Step 2: State + logic**

- Field: `private string? _litEndpoint; private DateTimeOffset? _litExpiresAt;` và `private static readonly TimeSpan EndpointLitTtl = TimeSpan.FromSeconds(2);` (khai báo cạnh `TerminalTtl`).
- Trong `ApplyEvent` wrapper (sau `_agg.Apply(e)`):

```csharp
// G5: chỉ event nào MANG endpoint (Received đầu) mới thay ánh sáng —
// update demote/HeadersSent để null → không đụng (spec §3.6)
if (e.Endpoint is { } endpoint)
{
    _litEndpoint = endpoint;
    _litExpiresAt = e.At + EndpointLitTtl;
}
```

- Trong `Flush`, sau `RemoveExpired()`:

```csharp
if (_litExpiresAt is { } litUntil && litUntil <= DateTimeOffset.UtcNow)
{
    _litEndpoint = null;
    _litExpiresAt = null;
    changed = true;
}
```

(Tick 100ms có sẵn → hết 2s tự fade, không thêm loop.)

- [ ] **Step 3: Markup — tách 2 dòng paths + title dot**

- Paths (:326) — 2 span con, giữ nguyên outer span (font/nowrap đo sẵn):

```razor
<span class="trace-node__paths"><span class="@PathLitClass("chat")">POST /v1/chat/completions</span><br /><span class="@PathLitClass("responses")">POST /v1/responses</span></span>
```

- Helper: `private string PathLitClass(string endpoint) => _litEndpoint == endpoint ? "trace-node__path trace-node__path--lit" : "trace-node__path";`
- Dot (:416) thêm `title`: `title="@node.Endpoint"` (null → Razor không render attribute; hover xem endpoint — spec §3.6).

- [ ] **Step 4: Verify — build**

Run: `dotnet build router-balancing.slnx` → 0W/0E. (Manual §5.4-5 ở Task 14.)

- [ ] **Step 5: Commit**

```bash
git add router-balancing/Components/Shared/RequestTrace.razor
git commit -m "feat: highlight active endpoint on input node"
```

---

### Task 12: UI — 3 luồng wire + legend

**Files:**
- Modify: `router-balancing/Components/Shared/RequestTrace.razor` (svg :292-317, legend :429-436, style block, vùng @code — 3 property mới)

**Interfaces:**
- Consumes: Task 7 (`_agg.Nodes`), Task 8 (`trace.legend.parked/headers`, `trace.priority.*`).
- Produces: 3 property bool + 3 nhóm path SVG conditional.

- [ ] **Step 1: 3 property điều kiện (đọc node ĐÃ qua `RemoveExpired` mỗi tick — node terminal hết hạn là đã bị gỡ, wire tự tắt — Review Focus #5)**

```csharp
// Wire ① Input→Queue: có request thật nằm trong queue (Received lẫn Parked — spec §3.1)
private bool WireInputQueueLit =>
    _agg.Nodes.Values.Any(n => n.Stage is TraceStage.Received or TraceStage.Parked);

// Wire ② Cancel→Client: node Canceled còn hạn (đã fade = đã gỡ)
private bool WireCancelLit =>
    _agg.Nodes.Values.Any(n => n.Stage == TraceStage.Canceled);

// Wire ③ Lỗi thực thi→Client: Finished success=false còn hạn
private bool WireErrorLit =>
    _agg.Nodes.Values.Any(n => n.Stage == TraceStage.Finished && n.Success == false);
```

- [ ] **Step 2: Markup SVG — chèn sau nhóm spine (:298), trước nhóm branch paths**

Geometry cố định theo spec §3.1 (input bottom = 8+47 = 55 theo comment CSS ; yellow lane x=116 skip node Thực thi 188..244; red lane x=100 offset khác vàng):

```razor
@if (WireInputQueueLit)
{
    @* Luồng ① trắng (user gate §1.3-8): đáy node Input → đỉnh Hàng đợi *@
    <g fill="none" stroke="#ffffff" stroke-width="1.8" opacity=".9" stroke-dasharray="7 6">
        <path d="M14,55 L14,96">
            <animate attributeName="stroke-dashoffset" values="0;-26" dur="1s" repeatCount="indefinite" />
        </path>
    </g>
}
@if (WireCancelLit)
{
    @* Luồng ② vàng: 2 đoạn, bỏ qua node Thực thi *@
    <g fill="none" stroke="#eab308" stroke-width="1.8" opacity=".9" stroke-dasharray="7 6">
        <path d="M116,152 L116,188">
            <animate attributeName="stroke-dashoffset" values="0;-26" dur="1s" repeatCount="indefinite" />
        </path>
        <path d="M116,244 L116,336">
            <animate attributeName="stroke-dashoffset" values="0;-26" dur="1s" repeatCount="indefinite" />
        </path>
    </g>
}
@if (WireErrorLit)
{
    @* Luồng ③ đỏ: lane riêng (x=100) dưới Thực thi → Client — luôn đi qua DispatchStarted (G6 loại) *@
    <g fill="none" stroke="#ef4444" stroke-width="1.8" opacity=".9" stroke-dasharray="7 6">
        <path d="M100,244 L100,336">
            <animate attributeName="stroke-dashoffset" values="0;-26" dur="1s" repeatCount="indefinite" />
        </path>
    </g>
}
```

- [ ] **Step 3: Legend + CSS legend (3 mục mới, sau `trace.legend.cancelled`)**

```razor
<span class="trace-legend__item"><i class="trace-ldot trace-dot--white trace-ldot--ring"></i>@L["trace.legend.parked"]</span>
<span class="trace-legend__item"><i class="trace-ldot trace-dot--green"></i>@L["trace.legend.headers"]</span>
<span class="trace-legend__item"><i class="trace-ldot trace-dot--yellow"></i><i class="trace-ldot trace-dot--blue"></i>@L["trace.priority.highest"]/@L["trace.priority.high"]</span>
```

```css
/* Legend: ring vàng đứt = dot đang chờ slot (G1) */
.trace-ldot--ring {
    outline: 1.5px dashed #eab308;
    outline-offset: 1px;
}
```

- [ ] **Step 4: Verify — build**

Run: `dotnet build router-balancing.slnx` → 0W/0E. (Manual §5.4-6/7 ở Task 14.)

- [ ] **Step 5: Commit**

```bash
git add router-balancing/Components/Shared/RequestTrace.razor
git commit -m "feat: add live trace flow wires and legend"
```

### Task 13: UI G4 — chip `COOLDOWN · {n}s` trên account

**Files:**
- Modify: `router-balancing/Components/Shared/RequestTrace.razor` (chip loop :402-410, `@inject` phần đầu, `@code` — tracker + refresh + render, style block)
- Modify: `router-balancing/MauiProgram.cs` (chỉ nếu chưa đăng ký `IProxyService`/`IProxyPool` — kiểm tra Step 1; theo khảo sát đã có tại :84/:91 nên **không sửa**)

**Interfaces:**
- Consumes: Task 6 (`CooldownTracker`), Task 8 (`trace.cooldown`).
- Produces (chỉ trong component Razor — test project không reference MAUI project nên không unit-test được UI; logic map đã test ở Task 6):

```csharp
private CooldownTracker? _cooldown;
private IReadOnlyDictionary<string, DateTimeOffset> _cooldownMap = new Dictionary<string, DateTimeOffset>();
private long _lastDownSig;      // hash của tập down-id — đổi mới fetch
private DateTimeOffset _cooldownFetchedAt = DateTimeOffset.MinValue;
private bool _cooldownBusy;      // chống fire-and-forget chồng nhau
```

- [ ] **Step 1: Inject + build tracker trong `OnInitializedAsync`**

Thêm đầu file: `@using RouterBalancing.Core.Proxies`, `@inject IProxyPool ProxyPool`, `@inject IProxyService ProxyService`.

Trong `OnInitializedAsync` (sau khi load feed, trước khi loop render đầu tiên):

```csharp
_cooldown = new CooldownTracker(
    snapshot: () => ProxyPool.Snapshot(),
    reverseAssignments: ct => ProxyService.GetReverseAssignmentsAsync(ct),
    providerAssignments: (id, ct) => ProxyService.GetAssignmentsAsync(id, ct),
    onError: ex => Logger.LogError(ex, "cooldown snapshot failed — giữ map cũ"));
```

(`Logger` = `ILogger<RequestTrace>` inject sẵn của component; nếu chưa inject thì thêm `@inject ILogger<RequestTrace> Logger`.)

- [ ] **Step 2: Refresh trong `Flush` — khi tập down-id đổi hoặc ≥30s**

Đầu `Flush` (trước khi tính `changed`), đồng bộ giá trị rồi bắn task:

```csharp
if (_cooldown is not null && !_cooldownBusy)
{
    var down = ProxyPool.Snapshot()
        .Where(s => s.IsDown && s.DownUntil > DateTimeOffset.UtcNow)
        .Select(s => s.Id)
        .OrderBy(id => id)
        .ToList();
    var sig = down.Count == 0 ? 0 : down.Aggregate(17, (h, id) => h * 31 + id.GetHashCode());
    if (sig != _lastDownSig || DateTimeOffset.UtcNow - _cooldownFetchedAt >= TimeSpan.FromSeconds(30))
    {
        _lastDownSig = sig;
        _cooldownFetchedAt = DateTimeOffset.UtcNow;
        _ = RefreshCooldownAsync();
    }
}
```

```csharp
private async Task RefreshCooldownAsync()
{
    _cooldownBusy = true;
    try
    {
        var map = await _cooldown!.ResolveAsync(CancellationToken.None);
        if (map is not null)   // null = lỗi source → GIỮ map cũ (spec §3.5, Review Focus #4)
            await InvokeAsync(() => { _cooldownMap = map; StateHasChanged(); });
    }
    catch (Exception ex)
    {
        Logger.LogError(ex, "cooldown refresh failed — giữ map cũ");
    }
    finally
    {
        _cooldownBusy = false;
    }
}
```

Countdown render theo tick 100ms sẵn có — cần re-render khi giây hiển thị đổi: trong Flush, tính sig chuỗi `_cooldownMap` (chỉ entry `DownUntil > now`, format giây) và `changed = true` khi khác lần trước (field `_cooldownSig`).

- [ ] **Step 3: Render chip — trong loop chip (else-branch của chip idle :402-410)**

```razor
else if (TryGetCooldown(node.Model, out var remaining))
{
    <span class="trace-chip trace-chip--cooldown" title="@L["trace.cooldown"]">@string.Format(L["trace.cooldown"], (int)Math.Ceiling(remaining.TotalSeconds))</span>
}
```

```csharp
private bool TryGetCooldown(string accountName, out TimeSpan remaining)
{
    if (_cooldownMap.TryGetValue(accountName, out var downUntil))
    {
        var left = downUntil - DateTimeOffset.UtcNow;
        if (left > TimeSpan.Zero) { remaining = left; return true; }
        _cooldownMap = new Dictionary<string, DateTimeOffset>(_cooldownMap)
            { [accountName] = DateTimeOffset.MinValue };  // hết hạn → tự gỡ khỏi map
    }
    remaining = default;
    return false;
}
```

(Lý do **không đụng** `_cooldownMap[accountName]` trực tiếp khi render: Render là side-effect — mutate map trong render chỉ khi gỡ entry hết hạn, đã wrap immutable-copy; badge tự tắt đúng lúc `DownUntil` qua kể cả chưa refresh — Review Focus #4.)

```css
/* Chip proxy đang cooldown — nét đứt đỏ (spec §3.5, mockup .trace-chip--cooldown) */
.trace-chip--cooldown {
    border-style: dashed;
    border-color: #ef4444;
    color: #ef4444;
}
```

- [ ] **Step 4: Verify — build + toàn bộ test (không hồi quy DI)**

Run: `dotnet build router-balancing.slnx` → 0W/0E; `dotnet test "router balancing test/router balancing test.csproj"` → xanh.
(`IProxyPool`/`IProxyService` đã đăng ký singleton tại `MauiProgram` — Step 1 kiểm tra lại; nếu thiếu đăng ký thì thêm cùng pattern.)

- [ ] **Step 5: Commit**

```bash
git add router-balancing/Components/Shared/RequestTrace.razor
git commit -m "feat: show proxy cooldown badge on account chips"
```

---

### Task 14: Gates — full build + full test + manual checklist

**Files:** không sửa (chỉ verify + fix nếu có fail).

- [ ] **Step 1: Build toàn solution**

Run: `dotnet build router-balancing.slnx`
Expected: 0W/0E. Nếu fail → sửa đúng task tương ứng, commit `fix:`.

- [ ] **Step 2: Chạy TOÀN BỘ unit test**

Run: `dotnet test "router balancing test/router balancing test.csproj"`
Expected: toàn xanh (mọi test mới 10+ task đều PASS). 2 fail `SingleInstanceGuardTests` chỉ xảy ra khi app đang chạy — đóng app trước khi chạy. Nếu có test sequence đúng-từng-bước của stream fail → sửa test (filter `HeadersSent != true`), commit `test:`.

- [ ] **Step 3: Manual checklist — chạy app Windows**

Build & chạy: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` rồi `dotnet run --project router-balancing -f net10.0-windows10.0.19041.0`.

Mở **Live Trace** (không mở Api Monitor — scope user gate §1.3-4), gửi request, kiểm tra từng mục spec §5.4:

1. Request mới: dot xuất hiện tại Hàng đợi; `QueuedCount` tăng. Badge `chờ slot` + ring vàng đứt **chỉ** cho request `Parked` (bị đẩy về queue giữa failover khi capacity corner — xem `DispatcherLoop.ReenqueueForPark`); request chờ capacity bình thường stays `Received`, không badge/ring (theo spec §3.2 — checklist viết lại 2026-10-09 sau QA live).
2. Gửi stream request: badge `200 ✓` xuất hiện khi head trả về (trước khi content xong).
3. Đặt provider saturation → request nhận priority Highest/High: tag màu amber/blue cạnh dot tại Hàng đợi; request khác bị demote → tag đổi ngay, dot đứng yên (không nhảy).
4. Gửi request qua endpoint `/v1/responses` (hoặc chat): dòng tương ứng trong Input node sáng xanh ~2s rồi fade; hover dot thấy title endpoint.
5. Cancel 1 request đang chạy: wire vàng 2 đoạn sáng (mất node Thực thi → vẫn còn do Canceled node ở Client); wire tắt khi node fade sau ~10s.
6. Gây lỗi provider (key sai): node Finished red → wire đỏ sáng; tắt khi node fade.
7. Wire trắng sáng khi `QueuedCount > 0` (kể cả chỉ có parked), tắt khi queue rỗng.
8. Proxy down (dừng provider proxy / đợi cooldown): chip account đổi sang `COOLDOWN · {n}s` đỏ nét đứt, countdown giảm; khi hết hạn → chip tự về trạng thái cũ.
9. Legend có đủ: parked / headers sent / Highest·High.
10. Đổi ngôn ngữ (nếu app có language switch) → badge/legend đổi text theo dict.

- [ ] **Step 4: Nếu checklist fail** — quay lại task tương ứng sửa, commit `fix:` (mỗi fix 1 commit).

- [ ] **Step 5: Final commit (nếu có thay đổi từ Step 4) + báo cáo hoàn thành**

```bash
git log --oneline -15   # verify 13 commit feat/refactor + fix nếu có
```

Trình bày cho user: danh sách commit, kết quả build/test, checklist manual, ghi chú 2 known-issue: (a) test stream sequence sửa test chứ không sửa publish; (b) `SingleInstanceGuardTests` fail khi app đang mở.
