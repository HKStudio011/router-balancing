# Live Request Trace Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sơ đồ động trên Dashboard hiển thị mỗi request là circle di chuyển qua Input → Hàng đợi → Thực thi → (Combo → Provider → Account) → Client, kèm drill-down modal timeline attempt.

**Architecture:** `ITraceFeed` singleton in-process (Core.Engine) nhận 6 điểm publish từ pipeline (ProxyApp H1/H4, DispatcherLoop H2/H3a/H3b), trả ring buffer 200 + active map; component `RequestTrace.razor` subscribe, throttle 100ms, render circle tuyệt đối + CSS transition. Proxy container nhận cùng instance feed qua ProxyHost (pattern `_settings`/`_log`).

**Tech Stack:** .NET 10 / C# (nullable enable), ASP.NET Core minimal API, Blazor Hybrid Razor Components, xUnit, Tailwind v4.

**Spec:** `docs/superpowers/specs/2026-10-05-live-request-trace-design.md` — plan này argue từ spec; executor đọc cả 2. Spec đã kèm 3 amendment do quá trình khảo sát: (A) H3 tách start/result (`AttemptDone`), (B) field `Mode` + `AttemptDone` trong `TraceEvent`, (C) `IProxyHost.QueuedSnapshot()` thay `IRequestQueue.Snapshot()` cho UI. Spec §2.1 mô tả DI chạy qua **2 container** (MAUI + proxy) cùng 1 instance.

## Global Constraints

- **Không đổi behavior dispatch/failover/queue** — chỉ thêm dòng publish; publish không được làm chậm/thỏng pipeline (spec §1.2).
- **`Publish` không bao giờ ném exception ra caller** — bọc try/catch, lỗi ghi `ILogService.Error` (bọc thêm: log nổ cũng không được thoát — nuốt chủ đích kèm comment why) (spec §3).
- Hằng số: ring **200**, cap node hiển thị **60**, fade terminal **10s** (`ExpiresAt = At + 10s`), throttle render **100ms**, dot/stage tối đa **6 + chip `+N`**, chip account tối đa **4 + `+N idle ▾`** (spec §5).
- Stage→vị trí/màu: `Received`→Hàng đợi/trắng; `DispatchStarted`→Thực thi/xanh; `Attempt`→chip Account/xanh; `Finished`→Client/xanh lá (Success) hoặc đỏ; `Canceled`→Client/vàng (spec G2 + checklist §7.3).
- i18n: mọi text hiển thị qua `Translations.cs`, key thêm **cả 2 dict** EN/VI — `TranslationParityTests` bắt buộc xanh.
- Comment: `///` XML doc cho public API; `//` why (tiếng Việt) — theo AGENTS.md. `Nullable=enable`, `ImplicitUsings=enable`.
- Gates (mỗi task Core: build + test; task cuối: đủ 3): Core build 0W/0E; app build `-f net10.0-windows10.0.19041.0` 0W/0E; `dotnet test` xanh (2 fail known `SingleInstanceGuardTests` khi app đang chạy → bỏ qua, tiền lệ ledger).
- Commit mỗi task, message tiếng Anh conventional, **chỉ stage file thuộc task**.

## Review Focus

1. **H4 mapping sai** — `Aborted` (client ngắt giữa stream) mà hiện đỏ là Finished thay vì vàng Canceled → test `ClassifyOutcomeTests` [Theory] covering Handled/Passthrough 200/503/Error 400/Cancelled/Aborted (Task 3).
2. **Mất `ComboName`** khi dispatcher re-select (dòng 132) hoặc failover-advance trong `ServeAsync` (dòng 321) → test `Dispatch_PublishesTraceSequence_WithComboNameInRoute` assert `Route.Combo == "combo-x"` (Task 4); 2 site pass-through là code-review point của Task 2/4.
3. **Publish đồng thời nhiều thread** mất/duplicated event → test `Publish_FromManyThreads_PreservesAllEvents` — 8 thread × 20 event → Snapshot()==160 (Task 1).
4. **Lỗi mạng ghi nhầm `FailureKind="http"`** — `Status=null` phải ra `"network"` (parity RetryState) → integration `NetworkError_RecordsFailureKindNetwork` (Task 4).
5. **2 container dùng 2 feed khác nhau** (UI không thấy event / PurgeAll sai instance) → test `ProxyHostTests.StopAsync_PurgesTraceFeed` (shared instance qua ctor ProxyHost, Task 3) + registration `MauiProgram` review point.

## File Structure

| Hành động | File | Trách nhiệm |
|---|---|---|
| Tạo | `src/RouterBalancing.Core/Engine/ITraceFeed.cs` | `TraceStage`, `TraceRoute`, `TraceEvent`, `ITraceFeed` |
| Tạo | `src/RouterBalancing.Core/Engine/TraceFeed.cs` | Ring 200 + active map, thread-safe, PurgeAll |
| Sửa | `src/RouterBalancing.Core/Engine/IComboResolver.cs` | `SelectionSuccess.ComboName` |
| Sửa | `src/RouterBalancing.Core/Engine/ComboResolver.cs` | Set `ComboName` khi resolve qua combo |
| Sửa | `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` | Ctor + H2/H3a/H3b + pass `comboName` |
| Sửa | `src/RouterBalancing.Core/Server/ProxyApp.cs` | DI fallback, H1/H4, `ClassifyOutcome` |
| Sửa | `src/RouterBalancing.Core/Server/ProxyHost.cs`, `IProxyHost.cs` | Shared instance feed, `PurgeAll` khi stop, `QueuedSnapshot()` |
| Sửa | `router-balancing/MauiProgram.cs` | Đăng ký `ITraceFeed → TraceFeed` |
| Tạo | `router-balancing/Components/Shared/RequestTrace.razor` | Section sơ đồ (state, render, modal) |
| Sửa | `router-balancing/Components/Pages/Dashboard.razor` | Chèn `<RequestTrace />` |
| Sửa | `src/RouterBalancing.Core/Localization/Translations.cs` | 24 key `trace.*` ×2 dict |
| Tạo | `router balancing test/Engine/TraceFeedTests.cs` | Feed unit tests |
| Tạo | `router balancing test/Server/ClassifyOutcomeTests.cs` | H4 mapping |
| Tạo | `router balancing test/Server/TraceIntegrationTests.cs` | Full-flow sequence |
| Sửa | `router balancing test/Engine/DispatcherLoopTests.cs`, `ComboResolverTests.cs`, `Server/ProxyHostTests.cs` | Ctor + hook tests |

---

### Task 1: TraceFeed (model + feed + unit test)

**Files:**
- Create: `src/RouterBalancing.Core/Engine/ITraceFeed.cs`
- Create: `src/RouterBalancing.Core/Engine/TraceFeed.cs`
- Test: `router balancing test/Engine/TraceFeedTests.cs`

**Interfaces:**
- Consumes: `ILogService` (`RouterBalancing.Core.Logging`) — đã đăng ký DI.
- Produces (Task 3/4/5 phụ thuộc — đúng tên này):

```csharp
public enum TraceStage { Received, DispatchStarted, Attempt, Finished, Canceled }
public sealed record TraceRoute(string? Combo, string? Provider, string? Account);
public sealed record TraceEvent(
    string RequestId, TraceStage Stage, string Model, TraceRoute? Route,
    int? Attempt, int? Status, bool? Success, string? FailureKind,
    DateTimeOffset At, string? Mode = null, bool? AttemptDone = null);

public interface ITraceFeed
{
    event Action<TraceEvent>? Published;
    void Publish(TraceEvent e);                 // không bao giờ ném ra caller
    IReadOnlyList<TraceEvent> Snapshot();       // tối đa 200, cũ nhất bị drop
    void PurgeAll();
}
public sealed class TraceFeed(ILogService log) : ITraceFeed
{
    internal IReadOnlyList<TraceEvent> ActiveSnapshot();  // latest event/RequestId chưa terminal — test qua InternalsVisibleTo
}
```

- Ràng buộc: `Publish` mutate ring+active map dưới `lock`; invoke `Published` **ngoài** lock; toàn bộ method bọc try/catch → log → không ném (contract). Terminal (`Finished`/`Canceled`) → remove khỏi active map; `Received`/`DispatchStarted`/`Attempt` → upsert. Ring cap `200`.

- [ ] **Step 1: Viết test failing** — `router balancing test/Engine/TraceFeedTests.cs` (dùng `NullLog` từ `TestDoubles.cs`):

```csharp
[Fact] public void Publish_RaisesPublished_WithEvent()
    // subscribe → feed.Publish(ev) → Assert.Same(ev, received)
[Fact] public void Snapshot_LateSubscriber_GetsRecentEvents_ReturnsLast200()
    // publish 250 event (At tăng dần) → Snapshot().Count==200, Snapshot()[0].Model=="m51"
[Fact] public void ActiveSnapshot_RequestWithoutTerminal_IncludesId_AfterTerminalRemovesIt()
    // Received → ActiveSnapshot chứa id; Publish Finished → còn? KHÔNG (remove); case 2: Received rồi Canceled → remove
[Fact] public void Publish_DoesNotThrow_ToCaller()
    // Published subscriber ném InvalidOperationException → Publish không ném; lần Publish thứ 2 vẫn raise bình thường
[Fact] public void PurgeAll_ClearsState()
    // publish 1 → Snapshot==1 && ActiveSnapshot==1 → PurgeAll → cả 2 rỗng
[Fact] public void Publish_FromManyThreads_PreservesAllEvents()
    // 8 thread × 20 event id duy nhất → Parallel/Task.WhenAll → Snapshot().Count==160
```

- [ ] **Step 2: Chạy test — kỳ vọng FAIL (compile error, chưa có `TraceFeed`)**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter TraceFeedTests
```

- [ ] **Step 3: Implement** `ITraceFeed.cs` (types + interface, XML doc) và `TraceFeed.cs`: `object _gate`, `Queue<TraceEvent> _ring` (drop khi >200), `Dictionary<string, TraceEvent> _active`; `ActiveSnapshot` internal trả `List<TraceEvent>` copy dưới lock.

- [ ] **Step 4: Chạy test — kỳ vọng PASS**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter TraceFeedTests
```

- [ ] **Step 5: Gates + commit**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
dotnet test "router balancing test/router balancing test.csproj"                     # xanh (2 known fail)
git add src/RouterBalancing.Core/Engine/ITraceFeed.cs src/RouterBalancing.Core/Engine/TraceFeed.cs "router balancing test/Engine/TraceFeedTests.cs"
git commit -m "feat: add in-process trace feed with ring buffer and active map"
```

---

### Task 2: `SelectionSuccess.ComboName`

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/IComboResolver.cs:14` (record)
- Modify: `src/RouterBalancing.Core/Engine/ComboResolver.cs` (`ResolveAsync` dòng 16–30, `Finalize` dòng 122, dòng 136)
- Modify: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs:132` (pass-through khi re-select)
- Test: `router balancing test/Engine/ComboResolverTests.cs`

**Interfaces:**
- Consumes: —
- Produces: `public sealed record SelectionSuccess(IReadOnlyList<ModelCandidate> Candidates, ComboMode Mode, string? ComboName = null)` — param **optional** nên toàn bộ site `new SelectionSuccess(...)` 2 tham số hiện có (DispatcherLoopTests ×~30, ModelSelectorTests, ComboResolverTests) **không vỡ**. Task 4 đọc `success.ComboName`.

- [ ] **Step 1: Viết 2 test failing** (mirror pattern seed combo có sẵn trong `ComboResolverTests`):

```csharp
[Fact] public void ComboResolver_ResolveByComboName_SetsComboNameOnSelection()
    // seed combo "ai-fast" chứa model m1 → ResolveAsync("ai-fast") →
    //   Assert.IsType<SelectionSuccess>; Assert.Equal("ai-fast", s.ComboName)
[Fact] public void ComboResolver_ResolveByModelId_ComboNameIsNull()
    // ResolveAsync("m1") (match model id) → s.ComboName == null
```

- [ ] **Step 2: Chạy — kỳ vọng FAIL**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter ComboResolverTests
```

- [ ] **Step 3: Implement** — thêm `string? ComboName = null` vào `SelectionSuccess`; trong `ResolveAsync` track `string? comboName` (set `combo.Name` khi vào nhánh `LoadComboAsync`), truyền qua `Finalize(model, candidates, mode, comboName)` → `SelectionSuccess(..., comboName)`; `DispatcherLoop` dòng 132: `new SelectionSuccess(remaining, success.Mode, success.ComboName)`.

- [ ] **Step 4: Chạy — kỳ vọng PASS** (cùng lệnh Step 2)

- [ ] **Step 5: Gates + commit**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
dotnet test "router balancing test/router balancing test.csproj"                     # xanh
git add src/RouterBalancing.Core/Engine/IComboResolver.cs src/RouterBalancing.Core/Engine/ComboResolver.cs src/RouterBalancing.Core/Engine/DispatcherLoop.cs "router balancing test/Engine/ComboResolverTests.cs"
git commit -m "feat: expose combo name on selection result"
```

---

### Task 3: H1/H4 publish + DI wiring 2 container + PurgeAll + QueuedSnapshot

**Files:**
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs` — `ConfigureServices` (dòng 29–90), endpoint lambda `MapPost` (dòng 114–216)
- Modify: `src/RouterBalancing.Core/Server/ProxyHost.cs` — ctor (dòng 37–46), `StartAsync` (dòng 69–78), `StopAsync` (sau dòng 130 `await app.DisposeAsync();`)
- Modify: `src/RouterBalancing.Core/Server/IProxyHost.cs`
- Modify: `router-balancing/MauiProgram.cs` (gần dòng 60, sau `ILogService`)
- Test: `router balancing test/Server/ClassifyOutcomeTests.cs` (tạo), `router balancing test/Server/ProxyHostTests.cs` (3 site ctor + test mới)

**Interfaces:**
- Consumes: Task 1 — `ITraceFeed`, `TraceEvent`, `TraceStage`.
- Produces:
  - `internal static (TraceStage Stage, bool? Success, int? Status) ProxyApp.ClassifyOutcome(DispatchOutcome outcome)` — test qua `InternalsVisibleTo` (đã có trong csproj).
  - Ctor `ProxyHost(..., IProxyPool pool, ITraceFeed trace)` — param **cuối cùng**.
  - `IProxyHost.QueuedSnapshot()` trả `IReadOnlyList<QueuedTraceItem>`; `public sealed record QueuedTraceItem(string Id, string Model)` (đặt cùng file `IProxyHost.cs`).
  - DI: proxy container có `ITraceFeed` (instance từ ProxyHost, fallback nếu test tự dựng).

- [ ] **Step 1: Viết test failing**

`ClassifyOutcomeTests.cs` ([Theory] + `MemberData` 6 dòng, `NullLog` không cần):

| outcome | Stage | Success | Status |
|---|---|---|---|
| `new DispatchOutcome.Handled()` | Finished | true | null |
| `new DispatchOutcome.Passthrough(200, null, [], null)` | Finished | true | 200 |
| `new DispatchOutcome.Passthrough(503, null, [], null)` | Finished | false | 503 |
| `new DispatchOutcome.Error(400, "m", "invalid_request_error", null, null)` | Finished | false | 400 |
| `new DispatchOutcome.Cancelled()` | Canceled | null | null |
| `new DispatchOutcome.Aborted()` | Canceled | null | null |

```csharp
public static IEnumerable<object[]> Cases() => /* 6 dòng trên */;
[Theory][MemberData(nameof(Cases))]
public void ClassifyOutcome_MapsDispatchOutcomeToTraceStage(DispatchOutcome o, TraceStage stage, bool? success, int? status)
```

`ProxyHostTests.StopAsync_PurgesTraceFeed`:

```csharp
var feed = new TraceFeed(_log);
var host = new ProxyHost(_settings, _log, _db.CreateFactory(), new DpapiSecretProtector(), _clientKeys, pool, feed);
// (pool: double/instance mà 3 site ctor sẵn có đang dùng — copy y hệt)
feed.Publish(new TraceEvent("id1", TraceStage.Received, "m1", null, null, null, null, null, DateTimeOffset.Now));
Assert.Single(feed.Snapshot());
await host.StartAsync(); await host.StopAsync();
Assert.Empty(feed.Snapshot()); Assert.Empty(feed.ActiveSnapshot());
```

- [ ] **Step 2: Chạy — kỳ vọng FAIL** (compile: `ClassifyOutcome` chưa tồn tại, ctor `ProxyHost` lệch arity)

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "ClassifyOutcomeTests|ProxyHostTests"
```

- [ ] **Step 3: Implement**

1. `ProxyApp.ClassifyOutcome` — mapping đúng bảng Step 1; nhánh `Retryable r → (Finished, false, r.Status)` và `Fatal f → (Finished, false, f.Status)` (bug-path defensive — endpoint không bao giờ nhận 2 loại này, comment why).
2. `ConfigureServices`: sau dòng 37 thêm (pattern `IProxyPool`):

```csharp
if (!builder.Services.Any(d => d.ServiceType == typeof(ITraceFeed)))
    builder.Services.AddSingleton<ITraceFeed, TraceFeed>();  // fallback cho test harness; app đã đăng ký trước ở StartAsync
```

3. Endpoint lambda `MapPost`: thêm param `ITraceFeed trace`.
   - **H1** — ngay sau khối `if (!queue.Enqueue(request)) { ... return; }` (sau dòng 149): `trace.Publish(new TraceEvent(id, TraceStage.Received, request.Model, null, null, null, null, null, DateTimeOffset.Now));`
   - **H4** — ngay sau `var outcome = await request.Completion.Task;` (dòng 168), **trước** khối ghi response: `ClassifyOutcome` → publish `(id, stage, prepared.ModelId, null, null, status, success, null, DateTimeOffset.Now)` + `//` why trước: publish tại đây để response write nổ (client ngắt giữa passthrough) không làm circle terminal bị mồ côi.
4. `ProxyHost`: field `_trace`; gán ctor; `StartAsync` — sau nhóm `AddSingleton(_clientKeys)` (dòng 74) thêm `builder.Services.AddSingleton(_trace);` (comment why: cùng instance với MAUI container — UI đọc đúng feed pipeline ghi); `StopAsync` — sau `await app.DisposeAsync();` thêm `_trace.PurgeAll(); //` proxy stop → skeleton UI về idle (spec §6).
5. `IProxyHost` + `ProxyHost.QueuedSnapshot()`:

```csharp
/// <summary>Snapshot id+model request còn trong queue — UI bù node trắng khi feed chưa có (spec trace §5.3).</summary>
IReadOnlyList<QueuedTraceItem> QueuedSnapshot();
// Impl: _app null → []; còn lại app.Services.GetRequiredService<IRequestQueue>().Snapshot() → Select(r => new QueuedTraceItem(r.Id, r.Model))
```

6. `MauiProgram.cs`: `builder.Services.AddSingleton<ITraceFeed, TraceFeed>();` sau `ILogService` (dòng 60).
7. Cập nhật 3 site `new ProxyHost(...)` trong `ProxyHostTests` (thêm `new TraceFeed(_log)` — param cuối).

- [ ] **Step 4: Chạy — kỳ vọng PASS** (cùng lệnh Step 2)

- [ ] **Step 5: Gates + commit**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
dotnet test "router balancing test/router balancing test.csproj"                     # xanh
git add src/RouterBalancing.Core/Server/ProxyApp.cs src/RouterBalancing.Core/Server/ProxyHost.cs src/RouterBalancing.Core/Server/IProxyHost.cs router-balancing/MauiProgram.cs "router balancing test/Server/ClassifyOutcomeTests.cs" "router balancing test/Server/ProxyHostTests.cs"
git commit -m "feat: publish received/finished trace events and wire trace feed DI"
```

---

### Task 4: H2/H3a/H3b publish + integration test

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` — ctor (dòng 15–21), `TryDispatchOnceAsync` (H2 sau dòng 157), `ServeAsync` (signature dòng 170, H3a trước dòng 184, H3b sau dòng 226, pass-through dòng 321)
- Modify: `router balancing test/Engine/DispatcherLoopTests.cs` (ctor dòng 117 + test mới)
- Test: `router balancing test/Server/TraceIntegrationTests.cs` (tạo)

**Interfaces:**
- Consumes: Task 1 (`ITraceFeed`), Task 2 (`SelectionSuccess.ComboName`), Task 3 (H1/H4 để sequence đầy đủ).
- Produces: thứ tự event guarantees: `Received → DispatchStarted → (Attempt{done=false} → Attempt{done=true}?)* → Finished|Canceled`. `ServeAsync(..., string? comboName)` — param mới **cuối**.

- [ ] **Step 1: Viết test failing — dispatcher level**

`DispatcherLoopTests` — cập nhật ctor: field `private readonly TraceFeed _trace = new(log);` (log mà file đang dùng) truyền vào `new DispatcherLoop(..., _trace)`. Test mới (mirror harness enqueue/serve có sẵn ở dòng ~432):

```csharp
[Fact] public async Task Dispatch_PublishesTraceSequence_WithComboNameInRoute()
    // StubResolver(SelectionSuccess([candidate], ComboMode.RoundRobin, "combo-x"))
    // subscribe _trace.Published → enqueue → await completion →
    // events: DispatchStarted(Mode=="RoundRobin", Route==null)
    //       → Attempt(Attempt==1, AttemptDone==false, Route==(combo-x, p.Name, <account>))
    //       → Finished(Success==true)
```

- [ ] **Step 2: Viết test failing — integration** (`TraceIntegrationTests.cs` — copy harness `ProxyQueueIntegrationTests`: `TestDb`, `SeedProvider`, `Sse()`, `GatedUpstream`, `WaitForQueuedIdAsync`, `CancelAsync`; **khác duy nhất**: tạo `var feed = new TraceFeed(_log);` và `builder.Services.AddSingleton<ITraceFeed>(feed);` **TRƯỚC** `ProxyApp.ConfigureServices` → fallback nhường chỗ; assert qua `feed.Snapshot()` — snapshot trả theo **thứ tự publish**, không sort lại):

```csharp
[Fact] public async Task Chat_Success_ReceivesReceivedDispatchAttemptFinishedSequence()
    // 1 provider "p-main" / 1 account "a1" / model; StubUpstream SSE 200 →
    // stages theo thứ tự: Received(Route null) → DispatchStarted(Route null)
    //   → Attempt{AttemptDone=false, Attempt=1, Route=(null, "p-main", "a1")} → Finished{Success=true}
[Fact] public async Task QueuedRequest_Cancel_PublishesCanceled()
    // MaxConcurrent=1 + GatedUpstream giữ request 1 → request 2 vào queue (WaitForQueuedId)
    //   → CancelAsync(id2) → feed có Canceled cho id2; release → id1 Finished{true}
[Fact] public async Task Provider429_Failover_PublishesAttemptDoneWith429ThenNextProvider()
    // ScriptedUpstream pattern (mirror ProxyRetryIntegrationTests): p1 luôn 429, p2 200 →
    //   có Attempt{AttemptDone=true, Status=429, FailureKind="http", Route.Provider=="p1"}
    //   → sau đó Attempt{AttemptDone=false, Route.Provider=="p2"} → Finished{true}
[Fact] public async Task NetworkError_RecordsFailureKindNetwork()
    // upstream ném HttpRequestException (mirror test "AllProvidersFailWithNetworkError" đang có) →
    //   có Attempt{AttemptDone=true, Status=null, FailureKind=="network"}
```

- [ ] **Step 3: Chạy — kỳ vọng FAIL** (cùng 2 lệnh filter `DispatcherLoopTests` / `TraceIntegrationTests`)

- [ ] **Step 4: Implement**

1. Ctor thêm `ITraceFeed trace` (param cuối).
2. **H2** — sau khi `queue.Take` thành công (sau dòng 157), trước `_ = ServeAsync(...)`:

```csharp
trace.Publish(new TraceEvent(request.Id, TraceStage.DispatchStarted, request.Model,
    null, null, null, null, DateTimeOffset.Now, success.Mode.ToString()));
```

3. `ServeAsync(..., ComboMode mode, long accountId, string? comboName)` — thêm param cuối; call site dòng 159 truyền `success.ComboName`; dòng 321 `new SelectionSuccess(next, mode, comboName)` (Review Focus #2 — pass-through tại đây **bắt buộc**).
4. **H3a** — ngay trước `outcome = await handler.ForwardAsync(...)` (dòng 184):

```csharp
var attemptNo = request.Retry.Attempts + 1;
var attemptRoute = new TraceRoute(comboName, candidate.Provider.Name, AccountNameOf(candidate, accountId));
trace.Publish(new TraceEvent(request.Id, TraceStage.Attempt, request.Model, attemptRoute,
    attemptNo, null, null, null, DateTimeOffset.Now, null, AttemptDone: false));
```

5. **H3b** — ngay sau `request.Retry.RecordAttempt(...)` (dòng 225–226), cùng `attemptRoute`/`attemptNo` (attemptNo tại đây = `request.Retry.Attempts` sau khi RecordAttempt tăng — dùng lại biến tính được, không nhầm index): `Status: status`, `FailureKind: status is null ? "network" : "http"` (comment why parity RetryState — status null = lỗi mạng), `AttemptDone: true`.

- [ ] **Step 5: Chạy cả 2 nhóm test — kỳ vọng PASS** (lệnh Step 3)

- [ ] **Step 6: Gates + commit**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
dotnet test "router balancing test/router balancing test.csproj"                     # xanh
git add src/RouterBalancing.Core/Engine/DispatcherLoop.cs "router balancing test/Engine/DispatcherLoopTests.cs" "router balancing test/Server/TraceIntegrationTests.cs"
git commit -m "feat: publish dispatch and attempt trace events with integration tests"
```

---

### Task 5: `RequestTrace.razor` core (state + skeleton + circle + counters)

**Files:**
- Create: `router-balancing/Components/Shared/RequestTrace.razor`
- Modify: `router-balancing/Components/Pages/Dashboard.razor` — chèn `<RequestTrace />` sau status card (sau dòng 45), trước `@code`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` — 14 key ×2 dict

**Interfaces:**
- Consumes: Task 1 (`ITraceFeed` inject), Task 3 (`IProxyHost.QueuedSnapshot()`), Modal pattern (`Shared/Modal.razor` — dùng ở Task 7), CSS nguồn `vite-project/src/css/style.css` (không cần rebuild — dùng `<style>` inline trong component, priority 7 theo comment đầu style.css).
- Produces (Task 6/7 sửa cùng file — pin tên): field `_nodes` (`Dictionary<string, TraceNode>`), `_inbox` (`ConcurrentQueue<TraceEvent>`), class `TraceNode`, method `ApplyEvent(TraceEvent)`, `AnchorOf(TraceNode) (int X, int Y)`, `ColorClass(TraceNode) string`, `OpenDetail(string id)` (Task 7).
- i18n keys (14): `trace.title`, `trace.legend.queued/running/ok/error/cancelled`, `trace.count.queued/running/ok/error/cancelled`, `trace.more` (`+{0}`), `trace.idleAccounts` (`+{0} idle`), `trace.fadeHint` — EN/VI cả 2 dict, gần block `dashboard.*`.

- [ ] **Step 1: Thêm i18n keys** (Translations.cs ×2 dict) → chạy parity:

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter TranslationParityTests
```

Expected: PASS.

- [ ] **Step 2: Implement `RequestTrace.razor`**

Ngữ nghĩa (không unit test — project không có bUnit; gate = build):

- `@inject ITraceFeed Feed`, `@inject IProxyHost Proxy`, `@inject LocalizationService L`; `@implements IAsyncDisposable`.
- State: `TraceNode { Id, Model, Stage, Route, Success, Status, Mode, ReceivedAt, ExpiresAt }`; `_nodes` + `_inbox`; `ApplyEvent` theo bảng Global Constraints (terminal set `ExpiresAt = At + 10s`; `Attempt` cập nhật `Route`/`Status`).
- Lifecycle: `OnInitializedAsync` → `Feed.Published += e => _inbox.Enqueue(e)`; rebuild từ `Feed.Snapshot()`; bù `Proxy.QueuedSnapshot()` (id chưa có → node `Received` tại Hàng đợi); loop `Task.Delay(100)` + `CancellationTokenSource`: drain `_inbox` → `ApplyEvent` → dọn node quá `ExpiresAt` (tick 100ms — nhỏ hơn 500ms spec, vô hại, ghi `//` why) → nếu có thay đổi `InvokeAsync(StateHasChanged)` (throttle 100ms). `DisposeAsync`: unsubscribe + cancel.
- Cap 60: khi vượt — drop node terminal cũ nhất trước; vẫn vượt → giữ 60 mới nhất + đếm `_overflow` → badge `trace.more`.
- Render: header (`trace.title` + 5 counter đếm từ `_nodes`) → canvas `position:relative; width:740px` + `<style>` inline (`.trace-canvas`, `.trace-dot{transition:left .35s ease, top .35s ease; border-radius:50%}` + 5 màu `.trace-dot--white/blue/green/red/yellow` + `.trace-node` — **lấy giá trị màu/coords từ mockup** `.superpowers/brainstorm/1249-1791207966/content/trace-design-v2.html`).
- Skeleton 4 node spine **luôn vẽ** (fixed coords theo mockup: Input top 8, Hàng đợi 96, Thực thi 188, Client 336, left 8, width 120) + legend 5 màu + `trace.fadeHint`.
- Circle: mỗi node hiển thị = 1 `<div class="trace-dot" @key="n.Id" style="left..top..">`, `AnchorOf` theo bảng stage→anchor (`Received`→Hàng đợi, `DispatchStarted`→Thực thi, `Attempt`→Thực thi *(Task 6 đổi sang chip Account)*, `Finished`/`Canceled`→Client), stack ngang +16px/dot, max 6 dot/node + badge `trace.more`.
- Ornament: 2 `<circle>` SVG `animateMotion` lặp vô hạn trên path Input→Queue và Thực thi→Client (trang trí, coords mockup).

- [ ] **Step 3: Chèn vào Dashboard** — `<RequestTrace />` + `@using RouterBalancing.Core.Engine` nếu `_Imports` chưa có.

- [ ] **Step 4: Build + commit**

```bash
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # 0W/0E
dotnet test "router balancing test/router balancing test.csproj"                        # xanh (parity)
git add router-balancing/Components/Shared/RequestTrace.razor router-balancing/Components/Pages/Dashboard.razor src/RouterBalancing.Core/Localization/Translations.cs
git commit -m "feat: add live request trace section to dashboard"
```

---

### Task 6: Route tree nhánh (Combo → Provider → Account) + chip

**Files:**
- Modify: `router-balancing/Components/Shared/RequestTrace.razor` (cùng file — thêm nhánh, đổi anchor `Attempt`)

**Interfaces:**
- Consumes: `_nodes`/`ApplyEvent`/`AnchorOf` (Task 5), `IProviderService.ListAsync()` (đã DI — MAUI `ProviderService`), `TraceRoute` từ `Attempt` events (H3a).
- Produces: anchor `Attempt` stage → vị trí chip Account của group route; `_providers` cache (`IReadOnlyList<Provider>` load 1 lần `OnInitializedAsync`, fire-and-forget + try/catch log — **deviation có chủ đích** khỏi chữ "lazy khi expand" trong spec §5.5: cần `N` cho label `+N idle` ngay khi render, expand chỉ hiện danh sách inline).

- [ ] **Step 1: Implement**

- Group `_nodes` còn hạn (chưa expire, `Route?.Provider != null`) theo `(Combo, Provider, Account)` — **chỉ render group có node tham chiếu** (spec §5.1).
- Layout cột fixed theo mockup: Combo left 210, Provider left 410, Account left 596; block xếp dọc theo thứ tự xuất hiện, row height hằng số (mockup: combo block ~128px, provider ~110px, account chip ~34px) — toan do tọa độ viết bằng C# trong `AnchorOf`, không JS interop.
- Connector SVG: đường `Thực thi → Combo → Provider → Account` per group; group có node `Status >= 400 || Success == false` → nét **đỏ**, ngược lại **xanh dương marching-ants** (`stroke-dasharray:7 6` + animate `stroke-dashoffset`, copy từ mockup).
- Node nhánh render kiểu `.n`/`.acct` mockup: Combo hiện tên + mode (`node.Mode`), Provider hiện tên + tag status lỗi (`node.Status`), Account chip hiện tên.
- Chip account: max 4 (active trước) + chip `trace.idleAccounts` (`IProviderService.ListAsync()` tổng số TK của provider − số active) → click expand inline danh sách (tên TK + dot active suy ra route node đang sống).
- `AnchorOf`: `Attempt` → chip Account của group node (fallback Thực thi nếu group chưa render); dot xanh pulse tại provider/account đang có request (keyframes pulse trong `<style>`).

- [ ] **Step 2: Build + commit**

```bash
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # 0W/0E
git add router-balancing/Components/Shared/RequestTrace.razor
git commit -m "feat: render active route tree with account chips in trace section"
```

---

### Task 7: Drill-down modal timeline

**Files:**
- Modify: `router-balancing/Components/Shared/RequestTrace.razor`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (10 key ×2 dict)

**Interfaces:**
- Consumes: `Modal` (`Visible`/`Title`/`OnClose`/`ChildContent` — mirror `ConfirmDialog.razor`), `Feed.Snapshot()` (ring = trail nguồn), `_nodes` (header modal).
- Produces: `private void OpenDetail(string id)`, `CloseDetail()`; field `_selectedId`.

- [ ] **Step 1: Thêm 10 key** → chạy `--filter TranslationParityTests` → PASS:
  `trace.detail.title`, `trace.detail.started`, `trace.detail.duration`, `trace.detail.received`, `trace.detail.dispatch`, `trace.detail.attempt`, `trace.detail.done`, `trace.detail.failed`, `trace.detail.cancelled`, `trace.detail.idle`.

- [ ] **Step 2: Implement**

- Circle `@onclick` → `OpenDetail(n.Id)` (Task 5 circle markup thêm handler).
- `<Modal Visible="@(_selectedId is not null)" Title="@L["trace.detail.title"]" OnClose="CloseDetail">`:
  - Header: `req #{Id}` (font-mono, truncate), model, `trace.detail.started` + `HH:mm:ss.fff`, `trace.detail.duration` = `last.At − ReceivedAt` (định dạng ms/s).
  - Trail: `Feed.Snapshot().Where(e => e.RequestId == _selectedId)` (thứ tự publish) → row `+Nms` (tính từ event Received đầu) + tag:
    - `Received` → tag `trace.detail.received` + `Input → queue`
    - `DispatchStarted` → tag `trace.detail.dispatch` + `Mode`
    - `Attempt{AttemptDone=false}` → tag `trace.detail.attempt` + `Attempt` + `Combo/Provider/Account` (thiếu → `—`)
    - `Attempt{AttemptDone=true}` → tag kèm `Status` (màu bad nếu >=400; `FailureKind=="network"` → ghi "network")
    - `Finished` → tag `trace.detail.done` (Success) / `trace.detail.failed`; hiện `Status`
    - `Canceled` → tag `trace.detail.cancelled`
  - Attempt start chưa có done ngay trước `Finished` → row attempt đó nhận status từ `Finished` (gộp, không hiện 2 dòng trùng).
  - Trail rỗng → `trace.detail.idle`.
- Đóng: Esc/backdrop/`OnClose` (Modal tự xử lý) → `_selectedId = null`.

- [ ] **Step 3: Build + test + commit**

```bash
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # 0W/0E
dotnet test "router balancing test/router balancing test.csproj"                        # xanh
git add router-balancing/Components/Shared/RequestTrace.razor src/RouterBalancing.Core/Localization/Translations.cs
git commit -m "feat: add request detail drill-down modal to trace section"
```

---

### Task 8: Gates tổng + manual checklist

**Files:** không tạo/sửa (chỉ verify + báo cáo).

- [ ] **Step 1: 3 gates**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental            # 0W/0E
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0          # 0W/0E
dotnet test "router balancing test/router balancing test.csproj"                              # xanh (2 known SingleInstanceGuard fail nếu app đang mở → bỏ qua)
```

- [ ] **Step 2: Chạy app thật với user — checklist spec §7.3:**
  1. Gửi request → circle trắng vào Hàng đợi → xanh Thực thi → (xanh ở chip Account) → xanh lá ở Client, fade ~10s.
  2. Gửi request lỗi → circle đỏ.
  3. Cancel → circle vàng.
  4. Click circle → modal trail đúng các bước.
  5. Queue dài / nhiều account → chip `+N`, không tràn node.
  6. Dừng proxy → section về idle, không node mồ côi (PurgeAll).
  7. Mở Dashboard khi request đang chờ → node trắng tại Hàng đợi (bù `QueuedSnapshot`).
- [ ] **Step 3: Báo cáo kết quả + hỏi user** có muốn commit update ledger `.superpowers/sdd/progress.md` (đánh dấu #4b xong) không — **không tự commit ledger**.
