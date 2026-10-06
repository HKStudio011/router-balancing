# API Monitor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Panel API Monitor (tiles + bảng per-request + popup chi tiết dùng chung với Live Trace) hiển thị metrics/token/latency/TTFT/throughput của request, dữ liệu in-memory ring 50, không persist DB.

**Architecture:** Store singleton `IApiMonitorStore` (Core.Engine) ghi tại 3 hook — H1 endpoint (`StartRequest`), ForwardAsync 2xx (`RecordResponse`) / lỗi (`RecordError`) — và tự subscribe `ITraceFeed.Published` để nhận lifecycle/State/Route (H4 authoritative). UI: `ApiMonitorPanel.razor` (bảng + tiles, cột phải Dashboard) và `RequestDetailModal.razor` (tách từ Task 7 cũ, join theo RequestId giữa feed + store).

**Tech Stack:** .NET 10 / MAUI Blazor Hybrid, Blazor Components, xUnit, hiện có `ITraceFeed`/`TraceFeed` pattern.

**Spec:** `docs/superpowers/specs/2026-10-06-api-monitor-design.md`

**Mockup (đã duyệt):** `.superpowers/brainstorm/w49485-1791256812/content/dashboard-monitor.html` (panel), `popup.html` (popup stack), `layout.html` (grid 2 cột).

## Global Constraints

- **i18n 100%**: mọi text hiển thị qua `src/RouterBalancing.Core/Localization/Translations.cs` — cả 2 dict EN/VI, `TranslationParityTests` phải xanh. Không hardcode label.
- **Hằng số (verbatim từ spec)**: ring monitor = **50** record (khác ring trace 60 — độc lập); body cap = **64 * 1024 byte/ký tự** + marker `[truncated]`; "Token hôm nay" reset theo **ngày UTC**.
- **H4 (feed) authoritative**: `State/Status/Success/CompletedAt` cuối cùng do feed `Finished`/`Canceled` quyết định; `RecordError` chỉ bổ sung `ErrorBody`/`Status` tạm — **không** set `State=Error` khi walk còn retry.
- **Store fail-open**: mọi entry point (`StartRequest`/`RecordResponse`/`RecordError`/handler feed/invocation `Changed`) bọc try/catch + `ILogService` — monitor **không được nổ request**, contract như `TraceFeed.Publish`.
- **Tee không đổi byte client nhận** — `UsageCapture.TeeAsync` chỉ thêm metadata vào `TeeResult`.
- Không thêm NuGet package; không JS interop; `Nullable=enable`, `ImplicitUsings=enable`.
- Gates mỗi task: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental` → 0W/0E; app `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → 0W/0E (task Core-only bỏ gate app); `dotnet test "router balancing test/router balancing test.csproj"` xanh (ngoại lệ known: 2 `SingleInstanceGuardTests` khi app đang chạy; temp-DB file-lock flake → rerun).
- Branch `master`; commit message English conventional; comment VI why-not-what; XML doc `///` cho public API.

## Review Focus

1. **Body cap 64KB** — prompt/response/error quá lớn phải truncate + marker, không OOM/row multi-MB → test ở Task 1 (`TeeAsync_*_BodyOverCap_TruncatesWithMarker`) + Task 2 (`StartRequest_LongPrompt_TruncatesAt64KWithMarker`).
2. **Retry walk**: attempt 429 ghi `RecordError` rồi attempt sau 2xx — request phải kết thúc `Done/Status 200`, `ErrorBody` bị xóa, không kẹt `Error` → Task 2 (`RecordError429_ThenFeedFinishedSuccess_EndsDone`) + Task 3 (`Provider429ThenOk_RecordErrorClearedByFinalSuccess`).
3. **Tee byte-identity** — stream hỏng = outage; 3 test byte-forward hiện có của `UsageCaptureTests` phải tiếp tục xanh sau khi đổi `TeeResult` → Task 1 step chạy toàn bộ `UsageCaptureTests`.
4. **Hai container cùng instance** — panel (MAUI container) phải đọc đúng store mà hooks (proxy container) ghi vào → Task 3 pre-register instance trước `ProxyApp.ConfigureServices` (mirror `TraceIntegrationTests:136-138`) + `AddSingleton(_monitor)` trong `ProxyHost.StartAsync` cùng dòng với `_trace`.
5. **Subscriber nổ không được lan** — UI `Changed` handler / feed handler throw không được nổ caller (endpoint/handler) → Task 2 (`Changed_SubscriberThrows_DoesNotPropagate`, `Published_UnknownRequest_CreatesSkeletonNoThrow`) + test thread-smoke.

---

### Task 1: `UsageCapture.TeeResult`

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/UsageCapture.cs`
- Modify: `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs:154` (call site — chỉ đổi kiểu trả về, hook monitor là Task 3)
- Test: `router balancing test/Engine/UsageCaptureTests.cs`

**Interfaces:**
- Consumes: không (static, nội bộ Core).
- Produces (Task 2/3 dùng):
  ```csharp
  public sealed record TeeResult(Usage? Usage, DateTimeOffset? FirstTokenAt, string? ResponseBody);
  // UsageCapture.TeeAsync(HttpContent source, Stream dest, CancellationToken ct) → Task<TeeResult>
  // TeeSseAsync cũng đổi sang Task<TeeResult>
  ```

- [ ] **Step 1: Viết failing tests**

Thêm vào `UsageCaptureTests.cs` (giữ nguyên 3 test cũ, chỉ đổi phần assert `usage` → `result.Usage`):

```csharp
[Fact]
public async Task TeeAsync_Sse_ReportsFirstTokenAtAndAccumulatesDataWithoutDone()

[Fact]
public async Task TeeAsync_NotStream_FirstTokenAtNull_ReturnsBodyAndUsage()

[Fact]
public async Task TeeAsync_BodyOverCap_TruncatesWithMarker()
```

Assertion chính:
- SSE (2 dòng `data:` + `[DONE]`): `FirstTokenAt` không null; `ResponseBody` chứa payload 2 dòng, **không** chứa `[DONE]`.
- JSON non-stream có usage: `FirstTokenAt` null; `ResponseBody` = toàn thân body; `Usage` parse được.
- Body 70KB ASCII: `ResponseBody` kết thúc bằng `[truncated]`, `Length <= 64 * 1024 + "[truncated]".Length`.

- [ ] **Step 2: Chạy verify FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~UsageCaptureTests`
Expected: FAIL (TeeAsync chưa trả `TeeResult` — compile error / assert mismatch).

- [ ] **Step 3: Implement `TeeResult` trong `UsageCapture.cs`**

- Thêm `public sealed record TeeResult(Usage? Usage, DateTimeOffset? FirstTokenAt, string? ResponseBody);` (XML doc).
- `TeeAsync`/`TeeSseAsync` trả `TeeResult` thay `Usage?`:
  - SSE: `FirstTokenAt = DateTimeOffset.UtcNow` tính tại **lần read byte > 0 đầu tiên**; `ResponseBody` tích lũy payload các dòng `data:` (decode UTF-8, nối `"\n"`, bỏ `[DONE]`), dừng tích lũy khi vượt `64 * 1024` ký tự + gắn 1 lần `"\n[truncated]"`.
  - Non-SSE: `FirstTokenAt = null`; `ResponseBody` = UTF-8 của payload đã buffer (truncate `64 * 1024` + marker nếu dài hơn); `Usage = ParseUsage(payload)` — logic parse giữ nguyên.
- Byte-forward cho client giữ nguyên tuyệt đối (ghi `dest` trước/mọi chunk như cũ).
- Sửa call site `ChatCompletionsHandler.cs:154`: `var tee = await UsageCapture.TeeAsync(...)`, tham chiếu `usage` → `tee.Usage`.

- [ ] **Step 4: Chạy verify PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~UsageCaptureTests` → PASS hết (bao gồm 3 test cũ — byte-identity).
Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental` → 0W/0E.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Engine/UsageCapture.cs src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs "router balancing test/Engine/UsageCaptureTests.cs"
git commit -m "feat: return TeeResult with first-token timestamp and response body from tee"
```

---

### Task 2: `IApiMonitorStore` + `ApiMonitorStore`

**Files:**
- Create: `src/RouterBalancing.Core/Engine/IApiMonitorStore.cs`
- Create: `src/RouterBalancing.Core/Engine/ApiMonitorStore.cs`
- Test: `router balancing test/Engine/ApiMonitorStoreTests.cs`
- Create (test helper): `router balancing test/Engine/ManualTimeProvider.cs`

**Interfaces:**
- Consumes: `ITraceFeed`/`TraceEvent`/`TraceStage` (đã có), `ILogService`.
- Produces (Task 3/4/5 dùng):
  ```csharp
  public enum ApiCallState { Queued, Running, Done, Error, Cancelled }

  public sealed record ApiCallRecord(
      string RequestId, string Model, DateTimeOffset StartedAt, ApiCallState State,
      DateTimeOffset? FirstTokenAt, DateTimeOffset? CompletedAt,
      int? Status, bool? Success, string? FailureKind,
      int? PromptTokens, int? CompletionTokens,
      string? Combo, string? Provider, string? Account, string? Mode,
      string? PromptBody, string? ResponseBody, string? ErrorBody);

  public interface IApiMonitorStore
  {
      event Action Changed;
      long TodayTokens { get; }
      IReadOnlyList<ApiCallRecord> Snapshot();   // ≤50, newest-first
      ApiCallRecord? Find(string requestId);
      void StartRequest(string requestId, string model, byte[] promptBody);
      void RecordResponse(string requestId, int? promptTokens, int? completionTokens,
          DateTimeOffset? firstTokenAt, string? responseBody);
      void RecordError(string requestId, int status, string? errorBody);
  }
  // ctor ApiMonitorStore(ITraceFeed feed, ILogService log, TimeProvider time)
  ```

- [ ] **Step 1: Viết failing tests**

`ApiMonitorStoreTests.cs` (helper: `TraceFeed(_log)` thật + `ApiMonitorStore(feed, log, new ManualTimeProvider())`; log theo pattern `TraceFeedTests`):

```csharp
[Fact] public void StartRequest_ThenRecordResponse_UpsertsSameRecord_KeepsFirstSeenOrder()
[Fact] public void StartRequest_LongPrompt_TruncatesAt64KWithMarker()
[Fact] public void StartBeyond50_EvictsOldest_NewestFirstInSnapshot()
[Fact] public void FeedEvents_DrivesStateRouteAndCompletedAt()
[Fact] public void FeedCanceled_SetsCancelledState()
[Fact] public void RecordError429_ThenFeedFinishedSuccess_EndsDoneStatus200()
[Fact] public void RecordErrorNetwork0_SetsNullStatusAndNetworkKind()
[Fact] public void FeedFinishedNetworkFailure_SetsErrorState()
[Fact] public void RecordResponse_ClearsErrorBody()
[Fact] public void Changed_SubscriberThrows_DoesNotPropagate()
[Fact] public void Published_UnknownRequest_CreatesSkeletonNoThrow()
[Fact] public void TodayTokens_Accumulates_AndResetsOnUtcDateChange()
[Fact] public async Task Snapshot_ConcurrentWriters_DoNotThrow()
```

Assertion chính:
- `FeedEvents_...`: publish DispatchStarted(Mode) → `State=Running`, `Mode` set; Attempt(Route combo/provider/account) → route set, `State=Running` vẫn giữ prompt; Finished(Status=200, Success=true) → `State=Done`, `CompletedAt=e.At`.
- `RecordError429_...`: `RecordError(id, 429, body)` (State chưa Error) → publish Finished{Status=200, Success=true} → `State=Done`, `Status=200`, `ErrorBody=null` (RecordResponse xóa).
- `RecordErrorNetwork0_...`: `RecordError(id, 0, null)` → `Status=null`, `FailureKind="network"`; rồi Finished{Status=null, Success=false} → `State=Error`, `FailureKind` giữ `"network"`.
- `TodayTokens_...`: 2 record (10+20, 5+5) → `TodayTokens=40`; `ManualTimeProvider` nhảy sang hôm sau → record mới → `TodayTokens=6`.
- `Changed_...`: subscribe handler nổ → `StartRequest` vẫn ghi record, không exception ra caller.
- Concurrency: 4 task song song `StartRequest`+`RecordResponse`+`Snapshot`/`Find` → không nổ.

- [ ] **Step 2: Chạy verify FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~ApiMonitorStoreTests`
Expected: FAIL (type chưa tồn tại — compile error).

- [ ] **Step 3: Implement store**

- `IApiMonitorStore.cs`: enum + record + interface như Interfaces block (XML doc đầy đủ; `Changed` giải thích contract fail-open).
- `ApiMonitorStore.cs`:
  - State: `List<ApiCallRecord> _calls` (thứ tự first-seen, cap 50, drop index 0), `object _gate`, `long _todayTokens`, `DateTime _today` (UTC date), `TimeProvider _time`.
  - Ctor: `feed.Published += OnPublished` — **toàn bộ handler** bọc try/catch + `log.Write(Error)` (why-comment: multicast delegate dừng ở subscriber nổ → publish sẽ nổ endpoint).
  - Switch `e.Stage`: `Received` → nếu record thiếu tạo skeleton (State=Queued, Model=e.Model, StartedAt=e.At); `DispatchStarted` → State=Running, Mode=e.Mode; `Attempt` → State=Running, ghi `Combo/Provider/Account` từ `e.Route` (attempt cuối thắng); `Finished` → State = `e.Success == true ? Done : Error`, `Status = e.Status ?? giữ cũ`, `Success`, `CompletedAt = e.At`; `Canceled` → State=Cancelled, `CompletedAt = e.At`.
  - `StartRequest`: upsert record (State=Queued, Model, StartedAt=UtcNow, PromptBody truncate) — giữ vị trí first-seen.
  - `RecordResponse`: upsert tokens/FirstTokenAt/ResponseBody, `ErrorBody=null`; `TodayTokens += prompt+completion ?? 0` (reset `_today` nếu `_time.GetUtcNow().Date` đổi).
  - `RecordError`: upsert `ErrorBody` (truncate), `Status = status > 0 ? status : null`, `FailureKind = status > 0 ? "http" : "network"` — **không** đụng `State`.
  - Truncate helper chung: cap `64 * 1024` + marker `[truncated]`.
  - Mọi mutation dưới lock `_gate`; **fire `Changed` ngoài lock** qua `InvokeChanged()` bọc try/catch + log (why-comment: subscriber nổ không được nổ caller, cũng không giữ lock khi gọi ra ngoài).
  - `Snapshot()`: lock → trả array đảo ngược (newest-first); `Find`: lock.

- [ ] **Step 4: Chạy verify PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~ApiMonitorStoreTests` → PASS.
Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental` → 0W/0E.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Engine/IApiMonitorStore.cs src/RouterBalancing.Core/Engine/ApiMonitorStore.cs "router balancing test/Engine/ApiMonitorStoreTests.cs" "router balancing test/Engine/ManualTimeProvider.cs"
git commit -m "feat: add in-memory api monitor store with feed lifecycle subscription"
```

---

### Task 3: DI wiring + 3 hook sites + integration tests

**Files:**
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs` (fallback đăng ký ~:41; endpoint lambda ~:117-155)
- Modify: `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs` (ctor + 3 hook)
- Modify: `src/RouterBalancing.Core/Server/ProxyHost.cs` (ctor + field + `AddSingleton(_monitor)` ~:78)
- Modify: `router-balancing/MauiProgram.cs` (đăng ký sau dòng 63)
- Modify: `router balancing test/Server/ProxyHostTests.cs` (4 call site :45, :171, :186, :226)
- Test: `router balancing test/Server/ApiMonitorIntegrationTests.cs`

**Interfaces:**
- Consumes: `IApiMonitorStore` (Task 2), fallback DI pattern của `ITraceFeed` (`ProxyApp.cs:40-41`, `ProxyHost.cs:78`, `MauiProgram.cs:63`), harness `TraceIntegrationTests.StartAsync` (:124-149).
- Produces: store được inject tại endpoint + handler + 2 container cùng instance (UI Task 4 đọc qua DI).

- [ ] **Step 1: Viết failing integration tests**

`ApiMonitorIntegrationTests.cs` — copy harness `TraceIntegrationTests` (StartAsync, SeedProvider, Stub/Gated/ScriptedUpstream, WaitUntilAsync, ChatBody) và **pre-register cả feed lẫn store** trước `ProxyApp.ConfigureServices`:

```csharp
var feed = new TraceFeed(_log);
var store = new ApiMonitorStore(feed, _log, TimeProvider.System);
builder.Services.AddSingleton<ITraceFeed>(feed);
builder.Services.AddSingleton<IApiMonitorStore>(store);
```

```csharp
[Fact] public async Task Chat_Success_MonitorRecordHasTokensStateRouteAndResponse()
[Fact] public async Task Provider429ThenOk_RecordErrorClearedByFinalSuccess()
[Fact] public async Task Upstream400_MonitorRecordErrorWithBodyAndErrorState()
[Fact] public async Task QueuedRequest_Cancel_MonitorRecordCancelled()
[Fact] public async Task NetworkFailure_MonitorRecordNetworkError()
```

Assertion chính (poll `WaitUntilAsync(() => store.Find(id) is { State: ... })` — H4 chạy sau khi client nhận response):
- Test 1: SSE stub **có usage** (`data:` usage `prompt_tokens:7/completion_tokens:5` + chunk thường) → `State=Done`, `PromptTokens=7`, `CompletionTokens=5`, `Status=200`, `Success=true`, `Provider="p-main"`, `Account="a1"`, `ResponseBody` chứa `data:`, `PromptBody` chứa `"messages"`.
- Test 2: p1→429, p2→SSE ok → `State=Done`, `Status=200`, `ErrorBody=null` (xóa sau RecordResponse).
- Test 3: upstream 400 JSON → `State=Error`, `Status=400`, `ErrorBody` chứa `"error"`.
- Test 4: cancel request đang queued → `State=Cancelled`.
- Test 5: `ScriptedUpstream` throw `HttpRequestException` (1 provider) → `State=Error`, `FailureKind="network"`.

- [ ] **Step 2: Chạy verify FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~ApiMonitorIntegrationTests`
Expected: FAIL (hook chưa ghi — record không tồn tại / State sai).

- [ ] **Step 3: Wiring DI + hooks**

- `ProxyApp.cs` — sau fallback trace (~:41):
  ```csharp
  if (!builder.Services.Any(d => d.ServiceType == typeof(IApiMonitorStore)))
      builder.Services.AddSingleton<IApiMonitorStore, ApiMonitorStore>(); // fallback test harness; app đã đăng ký instance ở StartAsync
  ```
  Endpoint lambda thêm param `IApiMonitorStore monitor`; **ngay trước** `trace.Publish(Received)` (~:154): `monitor.StartRequest(id, request.Model, prepared.Body);`
- `ChatCompletionsHandler.cs`:
  - ctor thêm `IApiMonitorStore monitor` (sau `usageSink`).
  - Sau `TeeAsync` (~:154): `monitor.RecordResponse(ClientKeyItems.RequestIdOf(ctx), tee.Usage?.PromptTokens, tee.Usage?.CompletionTokens, tee.FirstTokenAt, tee.ResponseBody);` — **không** đặt trong nhánh `usage is not null`.
  - Sau khi buffer `errorBody` (~:182), **trước** `RetryClassifier.IsRetryable`: `monitor.RecordError(ClientKeyItems.RequestIdOf(ctx), (int)response.StatusCode, Encoding.UTF8.GetString(errorBody));` (truncate trong store — gọi cho cả retryable/fatal/passthrough).
  - Nhánh `catch` network (~:143), trước `return Fatal`: `monitor.RecordError(ClientKeyItems.RequestIdOf(ctx), 0, null);`
  - **Không** bọc try/catch thêm tại call site (store contract fail-open — why-comment nếu cần).
- `ProxyHost.cs`: ctor thêm `IApiMonitorStore monitor` (sau `ITraceFeed trace`), field `_monitor`, và `builder.Services.AddSingleton(_monitor);` ngay sau `AddSingleton(_trace)` (~:78).
- `MauiProgram.cs`: `builder.Services.AddSingleton<IApiMonitorStore, ApiMonitorStore>();` sau dòng 63 (đăng ký feed).
- `ProxyHostTests.cs`: 4 site `new ProxyHost(...)` thêm tham số — mirror cách tham số feed đang được truyền tại mỗi site.

- [ ] **Step 4: Chạy verify PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~ApiMonitorIntegrationTests` → PASS.
Run: `dotnet test "router balancing test/router balancing test.csproj"` → xanh (2 known SingleInstanceGuard nếu app chạy; flake temp-DB → rerun).
Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental` → 0W/0E.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Server/ProxyApp.cs src/RouterBalancing.Core/Server/ProxyHost.cs src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs router-balancing/MauiProgram.cs "router balancing test/Server/ProxyHostTests.cs" "router balancing test/Server/ApiMonitorIntegrationTests.cs"
git commit -m "feat: hook api monitor store into request pipeline and DI"
```

---

### Task 4: `TraceColors` + `MonitorFormat` + i18n + `ApiMonitorPanel` + Dashboard grid

**Files:**
- Create: `router-balancing/Components/Shared/TraceColors.cs`
- Create: `router-balancing/Components/Shared/MonitorFormat.cs`
- Create: `router-balancing/Components/Shared/ApiMonitorPanel.razor`
- Modify: `router-balancing/Components/Shared/RequestTrace.razor` (`ColorClass` ~:1222 delegate sang helper; `FormatDuration` ~:1181 delegate)
- Modify: `router-balancing/Components/Pages/Dashboard.razor` (~:48 — wrapper grid + `<style>`)
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (11 key ×2 dict)

**Interfaces:**
- Consumes: `IApiMonitorStore`/`ApiCallRecord`/`ApiCallState` (Task 2), CSS class sẵn có `trace-dot--white/blue/green/red/yellow`.
- Produces (Task 5 dùng): `TraceColors.ForStage(TraceStage, bool?)`, `TraceColors.ForState(ApiCallState)`, `MonitorFormat.Duration/Latency/Ttft/Throughput/Tokens`; panel đặt row **chưa có click** (Task 5 bổ sung).

- [ ] **Step 1: Thêm i18n keys + parity test**

11 key mới trong **cả** dict EN/VI (`Translations.cs`, đặt sau block `trace.node.*`):

| Key | EN | VI |
|---|---|---|
| `monitor.title` | API Monitor | API Monitor |
| `monitor.tiles.live` | Live | Đang chạy |
| `monitor.tiles.errors` | Errors | Lỗi |
| `monitor.tiles.avgLatency` | Avg latency | Latency TB |
| `monitor.tiles.tokensToday` | Tokens today | Token hôm nay |
| `monitor.col.model` | Model | Model |
| `monitor.col.route` | Route | Route |
| `monitor.col.latency` | Latency | Latency |
| `monitor.col.ttft` | TTFT | TTFT |
| `monitor.col.tokens` | Tokens | Tokens |
| `monitor.empty` | No requests yet. | Chưa có request nào. |

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~TranslationParityTests` → PASS (verify step).

- [ ] **Step 2: Implement `TraceColors` + `MonitorFormat`**

- `TraceColors.cs` (internal static, XML doc):
  - `ForStage(TraceStage stage, bool? success)` — move body switch từ `RequestTrace.ColorClass` (Received→white, DispatchStarted/Attempt→blue, Canceled→yellow, Finished→success?green:red).
  - `ForState(ApiCallState state)` — Queued→white, Running→blue, Done→green, Error→red, Cancelled→yellow.
  - `RequestTrace.ColorClass` delegate sang `TraceColors.ForStage` (1 dòng — tại sao: một nguồn sự thật cho màu).
- `MonitorFormat.cs` (internal static):
  - `Duration(TimeSpan)` — rule hiện có: `<1s → "123ms"`, `≥1s → "1.234s"`; `RequestTrace.FormatDuration` delegate sang đây.
  - `Latency(ApiCallRecord)` — `CompletedAt−StartedAt` → `Duration`; thiếu → `"—"`.
  - `Ttft(ApiCallRecord)` — `FirstTokenAt−StartedAt` khi `FirstTokenAt` có giá trị → `Duration`; non-stream/chưa xong → `"—"`.
  - `Throughput(ApiCallRecord)` — `CompletionTokens / (CompletedAt−FirstTokenAt).TotalSeconds` khi đủ 2 điều kiện và >0 → số 1 chữ số thập phân (invariant); thiếu → `"—"`.
  - `Tokens(ApiCallRecord)` — cả null → `"—"`; else `$"↑{p ?? "—"} ↓{c ?? "—"}"`.

- [ ] **Step 3: Implement `ApiMonitorPanel.razor` + Dashboard grid**

Panel (theo mockup `dashboard-monitor.html`, `<style>` inline — precedent `RequestTrace`):
- `@inject IApiMonitorStore Store`, `@inject LocalizationService L`.
- State: `IReadOnlyList<ApiCallRecord> _rows`, `bool _disposed`; `OnInitialized`: `Store.Changed += OnChanged` + snapshot đầu; handler: try/catch → `InvokeAsync(() => { if (!_disposed) { _rows = Store.Snapshot(); StateHasChanged(); } })` (why-comment: store singleton + thread publish — mirror lesson `RequestTrace.OnPublished`); `DisposeAsync` unsubscribe.
- 4 tiles (Live = `State==Running`, Errors = `State==Error` — không tính Cancelled, Avg latency = trung bình `Latency` các row có `CompletedAt`, Token hôm nay = `Store.TodayTokens`).
- Bảng ≤50 row newest-first: dot `class="@TraceColors.ForState(r.State)"` + `r.Model` + route (`Combo · Provider · Account`, thiếu → `—`) + `MonitorFormat.Latency/Ttft/Tokens`; row `Running` có CSS pulse (`.monitor-dot--pulse` animation); `_rows` rỗng → `L["monitor.empty"]`.
- **Chưa có click/modal** (Task 5).
- Dashboard: wrapper `<div class="dash-trace-grid">` quanh `<RequestTrace />` + `<ApiMonitorPanel />`; CSS: `grid-template-columns: auto minmax(360px, 1fr); gap: 16px;` + `@media (max-width: 1180px) { .dash-trace-grid { grid-template-columns: 1fr; } }`.

- [ ] **Step 4: Build gates**

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → 0W/0E.
Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental` → 0W/0E.
Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~TranslationParityTests` → PASS.

- [ ] **Step 5: Commit**

```bash
git add router-balancing/Components/Shared/TraceColors.cs router-balancing/Components/Shared/MonitorFormat.cs router-balancing/Components/Shared/ApiMonitorPanel.razor router-balancing/Components/Shared/RequestTrace.razor router-balancing/Components/Pages/Dashboard.razor src/RouterBalancing.Core/Localization/Translations.cs
git commit -m "feat: add api monitor panel beside live trace on dashboard"
```

---

### Task 5: `RequestDetailModal` (dùng chung) + wire 2 nguồn + i18n

**Files:**
- Create: `router-balancing/Components/Shared/RequestDetailModal.razor`
- Modify: `router-balancing/Components/Shared/RequestTrace.razor` (gỡ markup modal ~:484-535 + members chuyển đi; render component mới; giữ `_selectedId`/`OpenDetail`/`CloseDetail`)
- Modify: `router-balancing/Components/Shared/ApiMonitorPanel.razor` (row click + embed modal)
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (10 key ×2 dict)

**Interfaces:**
- Consumes: `ITraceFeed.Snapshot()`, `IApiMonitorStore.Find()` (Task 2), `TraceColors`/`MonitorFormat` (Task 4), markup/CSS `.trace-detail-*` hiện có trong `RequestTrace`.
- Produces: component dùng cho cả 2 panel.

- [ ] **Step 1: Thêm i18n keys + parity test**

10 key (cả dict, đặt sau block `monitor.*`):

| Key | EN | VI |
|---|---|---|
| `detail.latency` | Latency | Latency |
| `detail.ttft` | TTFT | TTFT |
| `detail.promptTokens` | Prompt tokens | Token prompt |
| `detail.completionTokens` | Completion tokens | Token hoàn thành |
| `detail.throughput` | Throughput (tok/s) | Tốc độ (tok/s) |
| `detail.status` | Status | Trạng thái |
| `detail.prompt` | Prompt | Prompt |
| `detail.response` | Response | Response |
| `detail.unavailable` | No longer in memory. | Không còn trong bộ nhớ. |
| `detail.traceGone` | Beyond trace history. | Đã vượt lịch sử trace. |

Run parity test → PASS.

- [ ] **Step 2: Tách `RequestDetailModal.razor` (nội dung hiện có, chưa thêm metrics)**

- Parameters: `[Parameter] public string? RequestId`, `[Parameter] public EventCallback OnClose`; `Visible = RequestId is not null`; `Title = L["trace.detail.title"]`; inject `ITraceFeed Feed`, `IApiMonitorStore Store`, `LocalizationService L`.
- Chuyển nguyên khối từ `RequestTrace.razor`: markup modal (:484-535), `BuildDetail`, `BuildTrailRows` (giữ nguyên merge-rule attempt/Finished — **không đổi**), `AttemptLabel`, `RouteLabel`, `StatusLabel`, records `DetailView`/`TrailRow`, CSS `.trace-detail-*` trong `<style>`; `FormatDuration` → dùng `MonitorFormat.Duration`.
- `RequestTrace`: render `<RequestDetailModal RequestId="_selectedId" OnClose="CloseDetail" />`; xóa markup/members đã chuyển. Build + parity → PASS (checkpoint green).

- [ ] **Step 3: Thêm metrics + prompt/response + fallback vào modal**

- **Metrics strip** (dải trên, theo `popup.html`): từ `Store.Find(RequestId)`:
  - `MonitorFormat.Latency / Ttft / Throughput`; `detail.promptTokens` hiển thị `r.PromptTokens ?? "—"`, `detail.completionTokens` hiển thị `r.CompletionTokens ?? "—"` (không dùng `MonitorFormat.Tokens` — chuỗi gộp ↑↓ dành cho bảng); status text + dot màu `TraceColors.ForState`.
  - Record null → dải hiện `detail.unavailable` (thay số).
- **Prompt/Response** (`<details>` collapse, mặc định đóng) từ `PromptBody`/`ResponseBody`; record null → `detail.unavailable`.
- **Trail** giữ nguyên; rule fallback (spec §6.3): trail rỗng **và** record có → hiện `detail.traceGone`; cả hai rỗng → `trace.detail.idle`.

- [ ] **Step 4: Wire panel row click**

`ApiMonitorPanel`: thêm `string? _selectedId` + `@onclick` trên row → `OpenDetail`; render `<RequestDetailModal RequestId="_selectedId" OnClose="() => _selectedId = null" />`.

- [ ] **Step 5: Build gates + commit**

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → 0W/0E.
Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~TranslationParityTests` → PASS.

```bash
git add router-balancing/Components/Shared/RequestDetailModal.razor router-balancing/Components/Shared/RequestTrace.razor router-balancing/Components/Shared/ApiMonitorPanel.razor src/RouterBalancing.Core/Localization/Translations.cs
git commit -m "feat: extract shared request detail modal with monitor metrics and response bodies"
```

---

### Task 6: Final gates + manual checklist

**Files:** không tạo/sửa (chỉ verify).

- [ ] **Step 1: 3 gates**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental   # 0W/0E
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 # 0W/0E
dotnet test "router balancing test/router balancing test.csproj"                      # xanh
```
Known exception: 2 `SingleInstanceGuardTests` khi app đang mở (rerun với app đóng); temp-DB file-lock flake trong `ProxyQueueIntegrationTests`/`ApiMonitorIntegrationTests` → rerun, ghi ledger nếu lặp.

- [ ] **Step 2: Cung cấp checklist manual cho user chạy app** (spec §8)

Row màu khớp circle · 4 tiles đúng (gồm Token hôm nay tăng) · TTFT/throughput hiển thị khi stream, `—` khi non-stream · popup mở được từ **cả circle và row**, nội dung trùng · prompt/response collapse + fallback khi out-of-window · stack responsive < 1180px · bảng cap 50 row.

- [ ] **Step 3: Ghi kết quả vào ledger** `.superpowers/sdd/<workspace>/progress.md` (tạo mới nếu chưa có workspace cho plan này — convention: `.superpowers/sdd/2026-10-06-api-monitor/`).
