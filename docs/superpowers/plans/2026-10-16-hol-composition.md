# Fix HOL Blocking + Combo Composition (Cây Mode Đệ Quy) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** (A) Sửa head-of-line blocking — request đang park chờ provider đầy không chặn request khác dispatch sang provider rảnh; (B) Sửa composition combo lồng nhau — mode của combo con được tôn trọng (thay vì bị flatten bỏ), áp dụng đệ quy mọi mức; trace hiển thị đường đi `cha › con` + chip mode (đã duyệt mockup B).

**Architecture:**
- **(A)** `TryDispatchOnceAsync` chuyển từ `Peek`-đầu sang **scan-past-park**: quét `Snapshot()` theo (Priority→Sequence), item park → bỏ qua giữ nguyên vị trí, item dispatch được → `Take` + serve. `ReenqueueForPark` (ServeAsync path) **giữ nguyên**.
- **(B)** `ComboResolver` trả về **cây** `SelectionNode` (thay list phẳng); `ModelSelector` đi đệ quy cây theo mode từng cấp (FB=item đầu-or-park; RR=lọc eligible-sort min-inFlight-cursor); `DispatcherLoop` prune cây theo tried-pairs + derive `ComboPath` cho trace; `RequestTrace` vẽ breadcrumb B.

**Tech Stack:** .NET 10 / C#, xUnit, EF Core (in-memory test), Blazor (RequestTrace.razor).

**Spec:** Mô tả đầy đủ đã được user duyệt (HARD GATE) trong phiên brainstorming này — tóm tắt trong Global Constraints + từng Task. Cập nhật `docs/superpowers/specs/2026-09-28-queue-selection-design.md` ở Task 8.

## Global Constraints

- **Không đổi** `ReenqueueForPark` (DispatcherLoop.cs:666-706) — ServeAsync account-full path vẫn re-enqueue chờ `Exited`/`Changed` (user yêu cầu rõ).
- **Giữ luật park hiện hành** áp dụng đệ quy mỗi cấp: Fallback=item đầu mà full→park (không nhảy kế); RR=lọc eligible, không eligible→park. Park = wake re-select, KHÔNG advance trong cùng lần dispatch.
- **Failover luật:** lỗi attempt → `MarkTried(pair)` → prune → re-select đệ quy. Cha FB: item đầu hết lá untried→sang item kế. Cha RR: quay tiếp item còn eligible. Hết lá→exhaustion.
- **RR unit sort key:** `min` in-flight các lá hợp lệ của đơn vị, tie-break theo tên unit/leaf (đã duyệt lựa chọn 1). Cursor RR toàn cục giữ nguyên (`_cursor`, Interlocked).
- **Trace B:** node COMBO vẽ breadcrumb `cha › con` + chip mode nối bằng `→` (đã duyệt mockup B).
- **Backward compat:** `SelectionSuccess` thêm `Root = null` optional — existing tests (flat path, model-id resolve) không đổi hành vi. Flat path (`Root == null`) = code y hệt hôm nay.
- `Nullable=enable`, `ImplicitUsings=enable`. Comment "tại sao" tiếng Việt; XML doc public API. Build: `dotnet build router-balancing.slnx`. Test: `dotnet test "router balancing test/router balancing test.csproj" --nologo -v q`.
- **Baseline test:** 3-4 fail pre-existing (`SingleInstanceGuardTests` ×2 + network flakes) — KHÔNG phải regression. Xác nhận bằng `--filter` isolate.

## Review Focus

1. **O(n) resolve mỗi scan** — request park vẫn resolve lại mỗi lần wake (DB query per item trong Snapshot). Chấp nhận được cho desktop; KHÔNG optimize cache (YAGNI) — ghi chú trong code comment.
2. **Race Snapshot→Take** — item bị cancel giữa Snapshot và processing → `queue.Take`/`TryRemove` fail → skip (continue), không throw.
3. **Diamond combo (2 nhánh tham chiếu cùng con)** — leaf pair xuất hiện 2 đường; `MarkTried` prune cả 2 (pair đã thử là đã thử). Test: diamond resolve + failover không infinite-loop.
4. **RR cursor semantics với unit** — cursor toàn cục, mỗi RR-unit pick tăng 1, `% eligible.Count` (unit khác nhau count khác nhau) → phân phối ~đều. Test: 2 unit quay đều.
5. **Trace ComboPath sau prune** — `ComboPathOf(root, candidate)` trên cây đã prune; picked candidate NOT tried → path intact. Test: nested route trace có đủ tên+mode từng cấp.

---

## Part A — Sửa HOL Blocking

### Task 1: Scan-Past-Park trong DispatcherLoop

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs:92-182` (`TryDispatchOnceAsync` → scan + tách per-item)
- Test: `router balancing test/Engine/DispatcherLoopTests.cs`

**Interfaces:**
- Consumes: `IRequestQueue.Snapshot()` (đã có, IRequestQueue.cs:30 — trả theo Priority→Sequence), test harness có sẵn trong DispatcherLoopTests (`SeedProvider(name, maxConcurrent)`, `Req(id)`, `StartAsync(resolver, selector, upstream, log)`, `StubResolver`, `StubResolver` map theo model string, `GatedUpstream`, `CapturingLog`, `_executions`, `_queue`).
- Produces: `TryDispatchOnceAsync` giữ signature `Task<bool>` (true = có tiến triển → loop gọi lại; false = không item nào dispatch được → chờ `_wake`). Per-item logic tách thành method riêng (tên do implementer chọn, gợi ý `TryDispatchItemAsync`).

- [ ] **Step 1: Viết failing test** — `Loop_ParkedHead_DoesNotBlockRequestForOtherProvider`:

```csharp
[Fact]
public async Task Loop_ParkedHead_DoesNotBlockRequestForOtherProvider()
{
    var pidA = SeedProvider("pA", maxConcurrent: 1);
    var pidB = SeedProvider("pB", maxConcurrent: 1);
    // StubResolver map theo model string: "mA"→[candA], "mB"→[candB]
    var resolver = new StubResolver(new Dictionary<string, SelectionResult>
    {
        ["mA"] = new SelectionSuccess([Candidate(pidA)], ComboMode.RoundRobin),
        ["mB"] = new SelectionSuccess([Candidate(pidB)], ComboMode.RoundRobin),
    });
    var selector = new CountingSelector(new ModelSelector(_executions));
    var upstream = new GatedUpstream(); // giữ request đầu đến khi Release()
    var log = new CapturingLog();
    await StartAsync(resolver, selector, upstream, log);

    var r1 = Req("req00001"); r1.Model = "mA";
    var r2 = Req("req00002"); r2.Model = "mB";
    _queue.Enqueue(r1);
    await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // r1占据了 pA slot
    await Task.Delay(200);
    _queue.Enqueue(r2);

    // r2 phải dispatch NGAY (pB trống) dù r1 đang park ở đầu queue
    await Task.Delay(300);
    Assert.True(_executions.Contains("req00002"), "r2 phải được dispatch sang pB");
    // r1 vẫn giữ vị trí chờ slot của pA — KHÔNG bị mất/complete
    Assert.True(_queue.Contains("req00001") || _executions.Contains("req00001"));

    upstream.Release();
    Assert.IsType<DispatchOutcome.Handled>(await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    Assert.IsType<DispatchOutcome.Handled>(await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
}
```

(Lưu ý implementer: `StubResolver` hiện có thể chỉ nhận 1 `SelectionSuccess` — mở rộng thành map theo model string nếu cần; `Req(id)` có thể cần set `Model` property — kiểm tra harness thực tế. `GatedUpstream` giữ request đầu đến khi `Release()`.)

- [ ] **Step 2: Chạy test → FAIL** — hiện tại: r1 park ở đầu → `TryDispatchOnceAsync` return false → loop dừng → r2 KHÔNG dispatch (chờ wake mà pB không bao giờ Exit).

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter Loop_ParkedHead_DoesNotBlockRequestForOtherProvider --nologo -v q`
Expected: FAIL (`Assert.True(_executions.Contains("req00002"))` — false).

- [ ] **Step 3: Implement scan-past-park** trong `DispatcherLoop.cs`:
  - Tách body per-item hiện tại (từ sau drain-check + Peek) thành `TryDispatchItemAsync(ProxyRequest request, CancellationToken ct)` trả `Task<bool>` (true = progress: dispatch hoặc error-complete; false = park/skip).
  - `TryDispatchOnceAsync`: drain-check đầu (giữ) → `foreach (var item in queue.Snapshot())` → `if (await TryDispatchItemAsync(item, ct)) return true;` (progress → outer loop gọi lại, Snapshot mới) → hết list → `return false`.
  - Giữ nguyên toàn bộ logic per-item: resolve-failure → Take + complete error + return true; walk select/TryEnter: Entered → Take + serve + return true; selector null / TryEnter Full → return false (park → scan skip, KHÔNG re-enqueue — item giữ vị trí); NoAccountLeft → MarkTried + loop (giữ).
  - Comment "tại sao" (tiếng Việt): scan-past-park — request park chờ capacity A không chặn request khác dispatch sang provider rảnh (review Focus 1: O(n) resolve/scan chấp nhận được cho desktop).
  - **KHÔNG sửa** `ReenqueueForPark` hay ServeAsync.

- [ ] **Step 4: Chạy toàn bộ DispatcherLoopTests → PASS** — đặc biệt `Loop_WhenProviderSaturated_ParksOnceAndWakesOnExited` (cùng provider → r2 vẫn park, không skip — park behavior giữ nguyên) + test mới PASS. Baseline: chỉ 3-4 fail pre-existing.

- [ ] **Step 5: Commit** `fix: scan past parked queue items so other providers dispatch`

---

## Part B — Composition (Cây Mode Đệ Quy)

### Task 2: Tree Types + SelectionSuccess.Root

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/IComboResolver.cs` (thêm types + sửa record)
- Test: `router balancing test/Engine/ComboResolverTests.cs`

**Interfaces:**
- Consumes: `ModelCandidate(Provider, Model)` (IComboResolver.cs:6), `ComboMode` (ComboMode.cs).
- Produces: `SelectionNode` (abstract record), `CandidateLeaf(ModelCandidate Candidate)`, `ComboUnit(string Name, ComboMode Mode, IReadOnlyList<SelectionNode> Children)`; `SelectionSuccess(IReadOnlyList<ModelCandidate> Candidates, ComboMode Mode, string? ComboName = null, SelectionNode? Root = null)`.

- [ ] **Step 1: Viết failing test**:

```csharp
[Fact]
public void SelectionTree_NestedFallbackWithRoundRobinChild_HasExpectedShape()
{
    var leafA = new CandidateLeaf(new ModelCandidate(providerA, modelA));
    var leafB = new CandidateLeaf(new ModelCandidate(providerB, modelB));
    var leafC = new CandidateLeaf(new ModelCandidate(providerC, modelC));
    var child = new ComboUnit("child", ComboMode.RoundRobin, [leafB, leafC]);
    var root = new ComboUnit("root", ComboMode.Fallback, [leafA, child]);

    Assert.Equal(ComboMode.Fallback, root.Mode);
    Assert.IsType<CandidateLeaf>(root.Children[0]);
    var childUnit = Assert.IsType<ComboUnit>(root.Children[1]);
    Assert.Equal(ComboMode.RoundRobin, childUnit.Mode);
    Assert.Equal(2, childUnit.Children.Count);
}
```

- [ ] **Step 2: Chạy → FAIL** (compile error: type chưa tồn tại).

- [ ] **Step 3: Implement types** trong `IComboResolver.cs` (sau `ModelCandidate`):

```csharp
public abstract record SelectionNode;
public sealed record CandidateLeaf(ModelCandidate Candidate) : SelectionNode;
public sealed record ComboUnit(string Name, ComboMode Mode, IReadOnlyList<SelectionNode> Children) : SelectionNode;
```

Sửa `SelectionSuccess` thêm param cuối `SelectionNode? Root = null` (XML doc: cây selection khi resolve qua combo; null = flat path model-id).

- [ ] **Step 4: Chạy test → PASS** + `dotnet build router-balancing.slnx` 0 lỗi.

- [ ] **Step 5: Commit** `feat: add selection tree types for combo composition`

---

### Task 3: ComboResolver Xây Cây (Thay Flatten)

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ComboResolver.cs` (`ResolveComboAsync` trả tree; `Finalize` build Root + prune OpenAI)
- Test: `router balancing test/Engine/ComboResolverTests.cs`

**Interfaces:**
- Consumes: Task 2 types; `QueryCandidatesAsync` (đã có, ComboResolver.cs:64).
- Produces: `ResolveAsync` trả `SelectionSuccess` với `Root != null` khi resolve qua combo; `Root == null` khi resolve model-id (flat RR, giữ nguyên). Flat `Candidates` = flatten(tree) dedup (ProviderId, ModelId) — backward field cho exhaustion-check/attemptBudget/tests.

- [ ] **Step 1: Viết failing test** — dùng fixture builder combo hiện có trong ComboResolverTests, mở rộng cho nested:

```csharp
[Fact]
public async Task Resolve_ComboNestedChild_KeepsChildModeAndTreeShape()
{
    // Seed: combo "root" FB: item0→model X, item1→combo "child" RR: item0→model Y, item1→model Z
    var result = await _resolver.ResolveAsync("root", CancellationToken.None);
    var success = Assert.IsType<SelectionSuccess>(result);

    Assert.NotNull(success.Root);
    var rootUnit = Assert.IsType<ComboUnit>(success.Root);
    Assert.Equal(ComboMode.Fallback, rootUnit.Mode);
    Assert.Equal("root", rootUnit.Name);
    Assert.IsType<CandidateLeaf>(rootUnit.Children[0]); // X
    var child = Assert.IsType<ComboUnit>(rootUnit.Children[1]);
    Assert.Equal(ComboMode.RoundRobin, child.Mode);
    Assert.Equal("child", child.Name);
    Assert.Equal(2, child.Children.Count); // Y, Z theo Position
    // Flat Candidates vẫn chứa đủ X, Y, Z (dedup)
    Assert.Equal(3, success.Candidates.Count);
}
```

Test thứ hai (diamond — Review Focus 3): `Resolve_ComboDiamondSameChildTwice_FlattensDedupAndTreeKeepsBoth` — 2 item con trỏ cùng 1 combo child chứa model Y → flat `Candidates` có 1 Y (dedup), tree có 2 nhánh (mỗi nhánh 1 leaf Y).

- [ ] **Step 2: Chạy → FAIL** (Root null — flatten cũ).

- [ ] **Step 3: Implement**:
  - `ResolveComboAsync` trả `List<SelectionNode>`: `TargetModelId` → `QueryCandidatesAsync` → wrap từng `CandidateLeaf`; `TargetComboId` → cycle-check (giữ logic path-based hiện tại) → load child → recurse → wrap `ComboUnit(child.Name, child.Mode, childChildren)`; item null/dangling → skip (giữ).
  - `Finalize` combo path: `root = ComboUnit(combo.Name, combo.Mode, nodes)` → **prune OpenAI**: bỏ leaf không phải `ProviderType.OpenAI`, gỡ unit rỗng → root rỗng → `AnthropicNotSupported` (giữ semantics); flatten tree → dedup → flat `Candidates`. KHÔNG RR-sort tree children (Fallback cần Position order; selector sort at pick time).
  - Flat path (`Root == null`, model-id resolve): giữ nguyên y hệt (RR sort, dedup, OpenAI filter).
  - Dedup tree cross-branch (diamond): flatten-dedup cho flat field; tree giữ nguyên (MarkTried prune pair ở Task 5).

- [ ] **Step 4: Chạy toàn bộ ComboResolverTests → PASS** (existing flatten tests: flat `Candidates` derived từ tree giống hệt output cũ).

- [ ] **Step 5: Commit** `feat: resolver builds selection tree keeping child combo modes`

---

### Task 4: ModelSelector Đi Đệ Quy Cây

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ModelSelector.cs` (thêm tree path; flat path giữ nguyên)
- Test: `router balancing test/Engine/ModelSelectorTests.cs`

**Interfaces:**
- Consumes: Task 2 types; `IExecutionList.CanEnterAsync(providerId, ct)`, `GetInFlight(providerId)` (đã có); `_cursor` toàn cục hiện có.
- Produces: `TrySelectAsync(SelectionSuccess, ct)` — `Root == null` → flat logic y hệt; `Root != null` → `SelectInNodeAsync(Root)` trả leaf candidate hoặc null (park).

- [ ] **Step 1: Viết failing tests** (3):

```csharp
[Fact]
public async Task TrySelect_TreeFallback_FirstUnitFull_ParksWithoutSkipping()
{
    // root FB: [unitRR(chỉ leaf full), leafFree] → null (park, không nhảy leafFree)
}

[Fact]
public async Task TrySelect_TreeFallback_FirstUnitChild_DescendsByChildMode()
{
    // root FB: [unitRR(leafFree1, leafFree2), leafOther] → trả 1 trong 2 leaf của unitRR (RR tôn trọng)
}

[Fact]
public async Task TrySelect_TreeRoundRobin_FullLeafAndFullUnit_AreSkipped()
{
    // root RR: [leafFull, unitRR(leafFreeA, leafFreeB), leafFreeC] → không trả leafFull;
    // eligible = [unit, leafFreeC] theo min-inFlight; unit được chọn → descend → 1 trong 2 leafFreeA/B
}

[Fact]
public async Task TrySelect_TreeRoundRobin_TwoUnits_CursorRotatesAcrossUnits()
{
    // root RR: [unitX(1 leaf free), unitY(1 leaf free)] — gọi nhiều lần:
    // cursor toàn cục quay đều 2 unit (mỗi unit get ~n/2 pick); không bao giờ trả null
}
```

(Fixture: mock/stub `IExecutionList` với `CanEnterAsync` false cho provider full, `GetInFlight` trả giá trị khác nhau để assert min-inFlight sort.)

- [ ] **Step 2: Chạy → FAIL** (selector chưa tree-aware).

- [ ] **Step 3: Implement** tree branch trong `ModelSelector`:
  - `SelectInNodeAsync(SelectionNode node, ct)` → `ModelCandidate?`:
    - `CandidateLeaf`: `CanEnterAsync(leaf.Candidate.Provider.Id) ? leaf.Candidate : null`.
    - `ComboUnit` FB: `Children.Count == 0 ? null : SelectInNodeAsync(Children[0], ct)` — descendant null → null (park, không nhảy).
    - `ComboUnit` RR: `eligible = Children.Where(child => await HasEligibleLeafAsync(child, ct))`; rỗng → null; sort `(await MinInFlightOfEligibleAsync(child), child's tiebreak name)`; pick `cursor % count` (tăng `_cursor` Interlocked); `SelectInNodeAsync(picked, ct)`.
  - `HasEligibleLeafAsync(node, ct)`: leaf → CanEnter; unit → `Children.Any(async...)` (dùng loop, không Any async).
  - `MinInFlightOfEligibleAsync(node, ct)`: leaf → CanEnter ? GetInFlight : null; unit → min non-null các con.
  - Tie-break sort: min-inFlight bằng nhau → theo `Name` (ComboUnit) hoặc `(ProviderId, ModelId)` (leaf) — deterministic cho test.
  - `TrySelectAsync`: `if (selection.Root is { } root) return await SelectInNodeAsync(root, ct);` — rồi flat logic cũ.

- [ ] **Step 4: Chạy toàn bộ ModelSelectorTests + 3 test mới → PASS**.

- [ ] **Step 5: Commit** `feat: selector walks selection tree with recursive mode laws`

---

### Task 5: Dispatcher Prune Cây + Walk Đệ Quy

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` (walk truyền `Root`; prune theo tried)
- Test: `router balancing test/Engine/DispatcherLoopTests.cs`

**Interfaces:**
- Consumes: Task 2-4; `RetryState.IsTried(long providerId, string modelId)` (ĐÃ CÓ, RetryState.cs:72); `ProxyRequest.Retry`.
- Produces: static helper `PruneTriedTree(SelectionNode node, ProxyRequest request)` → `SelectionNode?` (null = subtree exhausted); walk (dispatch + serve advance) truyền `SelectionSuccess(prunedFlat, Mode, ComboName, prunedRoot)`.

- [ ] **Step 1: Viết failing test**:

```csharp
[Fact]
public async Task Loop_NestedFallbackChildExhausted_AdvancesToNextRootItem()
{
    // Combo "top" FB: item0→combo "kid" RR [leafY]; item1→leafX
    // leafY trả 429 Retryable (MarkTried) → kid prune rỗng → walk re-select → leafX serve OK
    // Assert: outcome Handled; attempt trail chứa cả Y rồi X; KHÔNG exhaustion sớm
}
```

- [ ] **Step 2: Chạy → FAIL** (walk filter flat list nhưng `Root` cũ còn leafY → selector tree-walk trả lại Y đã tried).

- [ ] **Step 3: Implement**:
  - `PruneTriedTree`: leaf → `request.Retry.IsTried(Provider.Id, Model.ModelId) ? null : leaf`; unit → prune children → rỗng ? null : new `ComboUnit(same name/mode, prunedChildren)`.
  - Walk dispatch (`TryDispatchItemAsync`): sau khi có `success` có `Root != null` → `pruned = PruneTriedTree(success.Root, request)` → null → exhaustion/503 path (giữ); còn → `TrySelectAsync(new SelectionSuccess(prunedFlat, success.Mode, success.ComboName, pruned))`. Flat `remaining` (FilterRemaining) giữ cho exhaustion-check + attemptBudget.
  - Walk serve (`ServeAsync` candidate-advance ~line 374-444): tương tự — prune tree theo `request.Retry` + truyền `prunedRoot` vào `SelectionSuccess` re-select.
  - `Root == null` → y hệt hiện tại.

- [ ] **Step 4: Chạy toàn bộ DispatcherLoopTests → PASS**.

- [ ] **Step 5: Commit** `feat: dispatcher prunes selection tree on failover walk`

---

### Task 6: TraceRoute.ComboPath + Dispatcher Publish

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ITraceFeed.cs` (`TraceHop` + `TraceRoute.ComboPath`)
- Modify: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` (attemptRoute kèm path)
- Test: `router balancing test/Engine/DispatcherLoopTests.cs` (hoặc TraceNodeAggregatorTests)

**Interfaces:**
- Consumes: Task 2 tree; `ServeAsync` attemptRoute site (DispatcherLoop.cs:205-206).
- Produces: `TraceHop(string Name, ComboMode Mode)` record; `TraceRoute(string? Combo, string? Provider, string? Account, IReadOnlyList<TraceHop>? ComboPath = null)`; `ComboPathOf(SelectionNode root, ModelCandidate target)` → `IReadOnlyList<TraceHop>` (DFS root→leaf; leaf match theo ProviderId+ModelId).

- [ ] **Step 1: Viết failing test** — `Dispatch_NestedRoute_PublishesComboPathWithEachHopMode`:

```csharp
[Fact]
public async Task Dispatch_NestedRoute_PublishesComboPathWithEachHopMode()
{
    // Combo root FB → child RR → leaf; dispatch 1 request
    // Assert: trace event Attempt có Route.ComboPath == [hop("root", Fallback), hop("child", RoundRobin)]
    // và Route.Combo == "root", Provider = leaf provider
}
```

- [ ] **Step 2: Chạy → FAIL** (ComboPath null).

- [ ] **Step 3: Implement**:
  - `TraceHop` record + `TraceRoute.ComboPath` optional (ITraceFeed.cs).
  - `ComboPathOf(root, target)`: DFS; leaf match `(Provider.Id, Model.ModelId)`; trả danh sách hop root→leaf (không gồm leaf). Multi-match (diamond) → trả path đầu tiên tìm thấy.
  - Dispatcher: `attemptRoute = new TraceRoute(comboName, candidate.Provider.Name, AccountNameOf(...), success.Root is null ? null : ComboPathOf(success.Root, candidate))` — **lưu ý**: ServeAsync cần nhận tree (truyền `SelectionSuccess` hoặc root param — implementer chọn cách ít churn nhất; gợi ý: đổi param `ServeAsync(..., SelectionNode? root, ...)` hoặc truyền cả success record).
  - `TraceNodeAggregator.Apply` không đổi (Route record replace nguyên — đã chứa ComboPath).
  - `ApiMonitorStore` giữ nguyên (Mode root-level đủ).

- [ ] **Step 4: Chạy TraceFeedTests + TraceNodeAggregatorTests + DispatcherLoopTests → PASS.**

- [ ] **Step 5: Commit** `feat: trace carries nested combo path for live display`

---

### Task 7: RequestTrace UI Breadcrumb (Mockup B)

**Files:**
- Modify: `router-balancing/Components/Shared/RequestTrace.razor` (`ComboBlock` ~line 826 + node render ~499-508 + layout build ~1642)
- Test: manual visual (app MAUI) — không có component test infra cho WebView render.

**Interfaces:**
- Consumes: Task 6 `TraceRoute.ComboPath`; `ComboBlock(string Name, string? Mode, int Top, bool Running)` hiện có (DisplayName = `Name · Mode`).
- Produces: node COMBO hiển thị breadcrumb `cha › con` + chip mode nối `→` (mockup B đã duyệt).

- [ ] **Step 1: Implement**:
  - `ComboBlock` thêm param `IReadOnlyList<TraceHop>? Path = null`.
  - `DisplayName`: path null/1 hop → giữ `Name · Mode` (backward); ≥2 hop → breadcrumb `string.Join(" › ", path.Select(h => h.Name))`.
  - Chip mode: render dưới breadcrumb — `FB → RR` dạng `<span class="trace-chip">Fallback</span> → <span>RoundRobin</span>` (label viết tắt: FB/RR; i18n nếu có key sẵn — kiểm Translations). CSS nhỏ thêm trong style block hiện có.
  - Layout build (~1642): tạo `ComboBlock` từ `node.Route.ComboPath` khi có; fallback `Combo`+`Mode` (node.Mode) khi null.
  - Giữ nguyên layout position/top/width (1 node COMBO — mockup B).

- [ ] **Step 2: Verify visual** — `dotnet build` MAUI Windows → chạy app → tạo combo nested (FB→RR) → dispatch request qua proxy → mở Live Trace → xác nhận node COMBO: breadcrumb `root › child`, chips `Fallback → RoundRobin`, không vỡ layout.

- [ ] **Step 3: Commit** `feat: live trace shows nested combo breadcrumb with mode chips`

---

### Task 8: Enum Doc Fix + Spec Update

**Files:**
- Modify: `src/RouterBalancing.Core/Domain/Enums/ComboMode.cs:7` (comment RR)
- Modify: `docs/superpowers/specs/2026-09-28-queue-selection-design.md`

**Interfaces:**
- Consumes: Task 1-7.
- Produces: doc đúng hiện trạng + design đã duyệt.

- [ ] **Step 1: Fix enum doc** — `RoundRobin` XML doc: "Chia đều request cho các model trong combo (lọc eligible, sort min-in-flight, cursor); lỗi retryable → rotate sang candidate kế (RR quay vòng, khác Fallback theo thứ tự Position)." (Hiện trạng: dispatcher re-select RR sau MarkTried — comment cũ "không cross-model failover" sai.)

- [ ] **Step 2: Update spec** `2026-09-28-queue-selection-design.md` — thêm 2 mục: (1) **Composition** — cây `SelectionNode`, luật FB/RR đệ quy mỗi cấp, RR unit = min-inFlight, park-unit-full, prune-pair; (2) **Scan-past-park** — dispatcher quét Snapshot, park giữ vị trí, `ReenqueueForPark` giữ nguyên. Link plan này.

- [ ] **Step 3: Commit** `docs: fix RR mode comment and record composition + HOL design`

---

## Notes cho Executor

- **Thứ tự:** Task 1 (Part A,独立 ship được) → Task 2-8 (Part B) → Task 7 (UI) cần Task 6 trước.
- **Flat backward compat là critical:** mọi Task giữ `Root=null` path y hệt hiện tại. Existing test fail = regression → fix ngay, không tiếp.
- **Không optimize O(n) resolve trong scan** (Review Focus 1) — YAGNI.
- **`ReenqueueForPark` KHÔNG sửa** (Global Constraint 1).
- **StubResolver mở rộng:** nếu test cần map theo model string, mở rộng stub (dictionary) thay vì hard-code 1 kết quả — giữ stub chung nhiều test.
- Test network flakes isolate bằng `--filter`; baseline 3-4 fail pre-existing.
- Ghi chú visual Task 7: `MauiXamlInflator=SourceGen` — build target `net10.0-windows10.0.19041.0`.
