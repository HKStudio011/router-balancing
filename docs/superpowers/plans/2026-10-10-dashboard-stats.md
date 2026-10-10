# Dashboard Stats Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Panel thống kê lịch sử trên Dashboard — journal writer ghi 1 dòng terminal/request vào `LogEntries`, `StatsService` aggregate theo preset Hôm nay/7 ngày/30 ngày, UI cards + chart SVG + breakdown theo Provider/Model/Client key.

**Architecture:** `RequestJournalWriter` subscribe `ITraceFeed.Published`, ghi dòng terminal (Outcome/ProviderKey/ModelKey/HttpStatus/DurationMs/ClientKeyId, fail-open) rồi fire `IStatsInvalidationSource`. `StatsService` đọc journal + dòng usage (join theo RequestId) trả `StatsSnapshot`. `StatsPanel` (Blazor) debounce 2s render layout A dưới grid trace+monitor.

**Tech Stack:** .NET 10, EF Core SQLite (migration `dotnet ef`), xUnit + `TestDb`, MAUI Blazor Hybrid, Tailwind pre-compile, SVG tay (không thêm dependency JS).

**Spec:** `docs/superpowers/specs/2026-10-10-dashboard-stats-design.md` (đã user-review; amendments `a6621a9`)

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên; nullable phải xử lý tường minh.
- Tiếng Việt cho comment giải thích "tại sao"; XML doc (`///`) cho public API; identifier tiếng Anh.
- i18n: mọi text hiển thị qua `LocalizationService` — thêm key vào **cả 2** dictionary EN/VI trong `Translations.cs`.
- Không nuốt exception; journal writer fail-open (log + không nổ feed); không log secret.
- Logic (writer, service) **bắt buộc** có unit test; UI kiểm thử thủ công (test project không có bUnit).
- Build + test xanh trước khi commit mỗi task: `dotnet build router-balancing.slnx` và `dotnet test "router balancing test/router balancing test.csproj"`.
- Commit conventional (`feat:`/`test:`/`refactor:`), message tiếng Anh, một task một commit (tối đa 2 khi tách schema/test).

## Review Focus

1. **Received phát 2 lần (G2 HeadersSent)** — writer không được đè entry cũ, sai `DurationMs` nếu `StartedAt` bị update → test `ReceivedPublishedTwice_KeepsOriginalStartedAt` (Task 3).
2. **DB write nổ trong feed handler** — chặn các subscriber phía sau (bài học RequestTrace) → test `DbFailure_LogsAndDoesNotInvalidate` chứng minh không throw + không invalidate (Task 3).
3. **Error rate tính nhầm Cancelled** — cancel là chủ đích client, vào denominator thì sai lệch → test `ErrorRate_ExcludesCancelled` (Task 4).
4. **Cửa sổ UTC midnight / offset lệch** — ProxyApp publish `DateTimeOffset.Now` (local offset); so sánh range SQLite an toàn chỉ khi store UTC → writer chuẩn hóa `ToUniversalTime()` (Task 3) + test `TodayWindow_UsesUtcMidnight` (Task 4).
5. **p95 sai rank** — nearest-rank `ceil(0.95n)` thay vì index floor → test `P95_NearestRank` với chuỗi latency biết trước (Task 4).

---

### Task 1: Schema — `RequestOutcome` + cột mới + migration

**Files:**
- Create: `src/RouterBalancing.Core/Domain/RequestOutcome.cs`
- Modify: `src/RouterBalancing.Core/Domain/Entities/LogEntry.cs`
- Create: `src/RouterBalancing.Core/Storage/Migrations/*_AddStatsJournalColumns.cs` (via EF tool)
- Test: `router balancing test/Storage/AddStatsJournalColumnsMigrationTests.cs`

**Interfaces:**
- Produces: enum `RequestOutcome { Ok=1, Error=2, Cancelled=3 }` (namespace `RouterBalancing.Core.Domain`); `LogEntry.Outcome` (`RequestOutcome?`), `LogEntry.ProviderKey`/`ModelKey` (`string?`), `LogEntry.HttpStatus` (`int?`). Task 3/4 dùng các cột này.

- [ ] **Step 1: Viết failing migration test** — pattern `AddOutboundProxyMigrationTests.cs`:

```csharp
[Fact]
public void Migrate_AddsFourNullableStatsColumns()
{
    using var db = new RouterBalancingDbContext(...);
    db.Database.Migrate();
    // pragma via Microsoft.Data.Sqlite connection:
    // 4 cột mới tồn tại, CLR type nullable, dòng cũ (insert 1 LogEntry cũ) Outcome = NULL
}
```

- [ ] **Step 2: Run fail** — `dotnet test "router balancing test/router balancing test.csproj" --filter AddStatsJournalColumns` → FAIL (cột chưa tồn tại).
- [ ] **Step 3: Thêm enum + cột vào `LogEntry`** — enum file mới; 4 property nullable, XML doc (Outcome: `null` = dòng không phải terminal).
- [ ] **Step 4: Generate migration** (từ repo root): `dotnet ef migrations add AddStatsJournalColumns` — kiểm tra `Up()` chỉ `AddColumn` ×4 (tất cả nullable, không default), `Down()` `DropColumn` ×4.
- [ ] **Step 5: Run pass** — cùng filter → PASS.
- [ ] **Step 6: Full build + commit** — `dotnet build router-balancing.slnx`; `git add` các file + `git commit -m "feat: add stats journal schema columns and outcome enum"`.

### Task 2: `TraceEvent.ClientKeyId` + publish tại Received

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ITraceFeed.cs` (record `TraceEvent`)
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs:208-211` (Received đầu tiên của endpoint dùng chung)
- Test: `router balancing test/Server/TraceIntegrationTests.cs`

**Interfaces:**
- Produces: `TraceEvent(..., long? ClientKeyId = null)` — optional param cuối; Task 3 đọc `e.ClientKeyId`. Mọi site `new TraceEvent(...)` dùng positional args hiện có **không đổi** (param mới có default).

- [ ] **Step 1: Failing integration test** — thêm vào `TraceIntegrationTests.cs`, mirror `Enqueue_PublishesReceivedEvent_WithPriorityAndEndpoint` nhưng **bật API key auth** (seed `ClientKeys` + `apiKeyEnabled=1`, gửi header `X-API-Key` — xem setup `ApiKeyMiddlewareTests`):

```csharp
[Fact]
public async Task Received_CarriesClientKeyId_WhenApiKeyEnabled() { ... Assert.Equal(seededKeyId, received.ClientKeyId); }
```

- [ ] **Step 2: Run fail** — `dotnet test ... --filter Received_CarriesClientKeyId` → FAIL (CS7036/compile hoặc assert null).
- [ ] **Step 3: Implement** — thêm param `long? ClientKeyId = null` vào record `TraceEvent`; tại **Received đầu tiên** (`ProxyApp.cs:208`) truyền `ClientKeyId: ClientKeyItems.IdOf(ctx)`. Publish G2 (dòng 249) và terminal (dòng 286) **không** truyền (giữ null).
- [ ] **Step 4: Run pass** — filter trên `TraceIntegrationTests` → PASS; chạy cả `Server` tests để chắc không regression.
- [ ] **Step 5: Commit** — `git commit -m "feat: carry client key id on trace received event"`.

### Task 3: `RequestJournalWriter` + `IStatsInvalidationSource` + DI wiring

**Files:**
- Create: `src/RouterBalancing.Core/Stats/IStatsInvalidationSource.cs`, `StatsInvalidationSource.cs`, `IRequestJournalWriter.cs`, `RequestJournalWriter.cs`
- Modify: `router-balancing/MauiProgram.cs` (đăng ký 2 singleton: `IStatsInvalidationSource` + `IRequestJournalWriter` — đăng ký thêm `IStatsService` ở Task 4)
- Modify: `src/RouterBalancing.Core/Server/ProxyHost.cs:39-41` (ctor + eager)
- Modify: `router balancing test/Server/ProxyHostTests.cs` (5 sites `new ProxyHost(...)`)
- Test: `router balancing test/Stats/RequestJournalWriterTests.cs`; Double: `router balancing test/TestDoubles.cs` (thêm `NullJournalWriter`)

**Interfaces:**
- Consumes: `TraceEvent.ClientKeyId` (Task 2), schema (Task 1).
- Produces:
  - `IStatsInvalidationSource { event Action Changed; void Invalidate(); }`
  - `IRequestJournalWriter` (marker interface rỗng — dùng cho eager + stub test)
  - `RequestJournalWriter(ITraceFeed feed, IDbContextFactory<RouterBalancingDbContext> db, ILogService log, IStatsInvalidationSource invalidation, TimeProvider clock)` — subscribe `Published` trong ctor (mirror `ApiMonitorStore`, singleton không unsubscribe).
  - Task 4 consume `IStatsInvalidationSource`; Task 6 consume để refresh.

- [ ] **Step 1: Failing tests** — tạo `RequestJournalWriterTests` với fixture: real `TraceFeed` + real `StatsInvalidationSource` + `TestDb` (factory đã `Migrate()`) + `CapturingLog` (copy pattern `ProxyRequestHandlerTests`). Helper `Ev(...)` copy từ `ApiMonitorStoreTests` + param `clientKeyId`. Đủ 8 test:

```csharp
Finished_Success_WritesOkRowWithRouteAndDuration      // Received(t0) → Attempt(route "prov1"/"m1") → Finished(t0+1.5s, 200, true), ClientKeyId=7
                                                      // → 1 row: Outcome=Ok, ProviderKey="prov1", ModelKey="m1", HttpStatus=200, DurationMs=1500, ClientKeyId=7, Timestamp=UTC
Finished_Failure_WritesErrorRowWithDerivedFailureKind // Finished(503,false) → ErrorCode="http"; Finished(null,false) → "network"
Canceled_WritesCancelledRow                           // Canceled → Outcome=Cancelled, Severity=Info
ReceivedPublishedTwice_KeepsOriginalStartedAt         // Received(t0) → Received(t0+5s, HeadersSent) → Finished(t0+2s) → DurationMs=2000 (không phải −3000/5000)
MultipleAttempts_LatestAttemptWinsRoute               // Attempt("p1") → Attempt("p2") → Finished → ProviderKey="p2"
DbFailure_LogsAndDoesNotInvalidate                     // factory nổ (dispose db file hoặc fake factory nổ) → Publish không throw; invalidation.Changed không fire; log ghi Error
Write_Success_InvalidatesSource                        // sau terminal → Changed fire đúng 1 lần
StaleEntry_OlderThan30Minutes_IsEvicted                // Received(t0) → Attempt(t0+31m) (sweep) → Finished(t0+32m) → 0 row
```

- [ ] **Step 2: Run fail** — `dotnet test ... --filter RequestJournalWriter` → FAIL (chưa có type).
- [ ] **Step 3: Implement `StatsInvalidationSource` + `IRequestJournalWriter` + `NullJournalWriter` double.**
- [ ] **Step 4: Implement `RequestJournalWriter`:**
  - Active map `ConcurrentDictionary<string, Pending>`; `Pending` = record `(DateTimeOffset StartedAt, string Model, long? ClientKeyId, string? Combo, string? Provider, string? Account)`.
  - Handler `OnPublished(TraceEvent e)`: **try/catch toàn bộ** (log, không nổ); sweep entry `StartedAt < _clock.GetUtcNow() − 30 phút` (inject `TimeProvider` — DI đã có singleton; test truyền `TimeProvider.System`); switch stage:
    - `Received`: `TryAdd` (không đè).
    - `DispatchStarted`/`Attempt`: update Mode/Route nếu entry còn (attempt đè route).
    - `Finished`: outcome = `Success==true ? Ok : Error`; `ErrorCode` = Ok→null, Error→`Status is > 0 ? "http" : "network"`.
    - `Canceled`: outcome `Cancelled`.
    - Terminal: build `LogEntry` theo bảng spec §4 (`Timestamp = e.At.ToUniversalTime()`, Severity `Error` cho Error/`Info` còn lại, Message format spec §4, `Category=Request`, `RequestId`), `Add` + `SaveChanges` trong try/catch; thành công → `invalidation.Invalidate()`; finally `TryRemove` entry.
- [ ] **Step 5: Run pass** — filter `RequestJournalWriter` → PASS.
- [ ] **Step 6: DI wiring** — MauiProgram: `AddSingleton<IStatsInvalidationSource, StatsInvalidationSource>()`, `AddSingleton<IRequestJournalWriter, RequestJournalWriter>()` (gần `ITraceFeed`/`IApiMonitorStore`, dòng ~63-66); `ProxyHost` ctor thêm param `IRequestJournalWriter journalWriter` + `_ = journalWriter; // eager-create singleton (mirror ITraceFeed)`; sửa 5 chỗ `new ProxyHost(...)` trong `ProxyHostTests` truyền `new NullJournalWriter()`.
- [ ] **Step 7: Full test + commit** — `dotnet test` toàn bộ → xanh; `git commit -m "feat: journal request terminal rows into log entries"`.

### Task 4: `IStatsService` + `StatsService`

**Files:**
- Create: `src/RouterBalancing.Core/Stats/StatsRange.cs` (enum + records cùng file hoặc `StatsModels.cs`), `IStatsService.cs`, `StatsService.cs`
- Modify: `router-balancing/MauiProgram.cs` (đăng ký `IStatsService`)
- Test: `router balancing test/Stats/StatsServiceTests.cs`; Double: `TestDoubles.cs` (thêm `FixedTimeProvider`)

**Interfaces:**
- Consumes: schema Task 1 (dòng terminal + usage rows `LogRequestUsage`).
- Produces (đúng spec §3.4 — sao chép nguyên văn):

```csharp
public enum StatsRange { Today, Last7Days, Last30Days }
public sealed record StatsBucket(DateTimeOffset BucketStart, int Requests, int Errors);
public sealed record StatsBreakdownRow(string Key, int Requests, int Errors, long Tokens);
public sealed record StatsSnapshot(
    DateTimeOffset From, DateTimeOffset To,
    int Requests, int Ok, int Errors, int Cancelled,
    double ErrorRatePercent, int? AvgLatencyMs, int? P95LatencyMs,
    long PromptTokens, long CompletionTokens,
    IReadOnlyList<StatsBucket> Buckets,
    IReadOnlyList<StatsBreakdownRow> ByProvider,
    IReadOnlyList<StatsBreakdownRow> ByModel,
    IReadOnlyList<StatsBreakdownRow> ByClientKey);
public interface IStatsService
{
    Task<StatsSnapshot> GetRangeAsync(StatsRange range, CancellationToken ct = default);
}
```

- Task 6 consume toàn bộ records.

- [ ] **Step 1: Failing tests** — `StatsServiceTests`, fixture: `TestDb` (migrate) seed trực tiếp `LogEntry` (terminal: Outcome/DurationMs/ProviderKey/ModelKey/ClientKeyId/Timestamp UTC) + usage rows (`Category=Request`, `PromptTokens/CompletionTokens`, `RequestId`); service ctor `(IDbContextFactory<RouterBalancingDbContext> db, TimeProvider clock)` với `FixedTimeProvider` (test double override `GetUtcNow`). 6 test:

```csharp
ErrorRate_ExcludesCancelled       // 8 Ok, 1 Error, 3 Cancelled → ErrorRatePercent == 10.0 (1/(8+1))
P95_NearestRank                   // 20 Ok, DurationMs 1..20 → p95 == 19 (ceil(0.95*20)=19 → index 18 sorted desc)
TodayWindow_UsesUtcMidnight       // row tại (midnight UTC hôm nay − 1ms) không tính; row tại midnight tính; row future không tính
Breakdown_JoinsUsageTokensByRequestId // terminal ProviderKey="p1" + usage cùng RequestId → ByProvider["p1"].Tokens khớp tổng; requests từ terminal
EmptyWindow_ReturnsZerosAndNullLatency // snapshot 0s, Avg/P95 null, breakdown rỗng
TodayBuckets_Hourly24             // Buckets.Count==24, BucketStart = 00h..23h UTC; Errors nằm đúng bucket theo Timestamp
```

- [ ] **Step 2: Run fail** — filter `StatsService` → FAIL.
- [ ] **Step 3: Implement** — `GetRangeAsync`: tính window từ `_clock.GetUtcNow()` (Today: midnight UTC→now; 7/30: midnight −6/−29 ngày →now); **2 query**: terminal rows (`Category=Request && Outcome != null && Timestamp ∈ window`) và usage rows (`Category=Request && PromptTokens != null && Timestamp ∈ window`) — select đúng cột cần; aggregate LINQ-to-objects (volumes desktop; không rollup — đúng master spec): đếm theo Outcome; avg/p95 trên DurationMs của Ok (p95 nearest-rank `List.Sort` desc + index `ceil(0.95n)-1`); tokens sum từ usage; buckets sinh đủ (24 giờ hoặc N ngày, UTC); breakdown group theo `ProviderKey ?? "—"` / `ModelKey ?? "—"` / `ClientKeyId?.ToString() ?? "—"`, tokens gán từ join usage qua dictionary `RequestId`; sort Requests desc. `ct` truyền qua query EF.
- [ ] **Step 4: Run pass** — filter → PASS.
- [ ] **Step 5: DI + commit** — MauiProgram `AddSingleton<IStatsService, StatsService>()`; `dotnet test` toàn bộ; `git commit -m "feat: add stats service aggregating request journal"`.

### Task 5: i18n keys + `MonitorFormat.Compact` + `StatsChart`

**Files:**
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (2 dictionary)
- Modify: `router-balancing/Components/Shared/MonitorFormat.cs`
- Create: `router-balancing/Components/Shared/StatsChart.razor`

**Interfaces:**
- Consumes: `StatsBucket` (Task 4).
- Produces: `MonitorFormat.Compact(long value)` → `"999"` / `"12,3k"` / `"1,2M"` (1 chữ số thập phân cho k/M, `CultureInfo.CurrentCulture`); `<StatsChart Buckets="IReadOnlyList<StatsBucket>" />` — SVG bar stacked, requests `#3b82f6`, errors `#ef4444`, legend 2 mục, label `{hour:00}h` (Today) / `{dd/MM}` (ngày) tự suy từ số bucket; `Buckets` rỗng → render `EmptyState`. Task 6 mount.

- [ ] **Step 1: i18n keys** — thêm vào dictionary EN + VI (giá trị theo spec §7):

```
stats.title = "Statistics" / "Thống kê"
stats.range.today = "Today" / "Hôm nay"
stats.range.7d = "7 days" / "7 ngày"
stats.range.30d = "30 days" / "30 ngày"
stats.kpi.requests = "Requests" / "Requests"
stats.kpi.errorRate = "Error rate" / "Tỷ lệ lỗi"
stats.kpi.latency = "Latency" / "Latency"
stats.kpi.tokens = "Tokens" / "Tokens"
stats.kpi.p95 = "p95 {0}" / "p95 {0}"
stats.threshold.under = "✓ under threshold {0}%" / "✓ dưới ngưỡng {0}%"
stats.threshold.over = "⚠ at/over threshold {0}%" / "⚠ bằng/vượt ngưỡng {0}%"
stats.chart.requests = "requests" / "request"
stats.chart.errors = "errors" / "lỗi"
stats.tab.provider = "Provider" / "Provider"
stats.tab.model = "Model" / "Model"
stats.tab.clientKey = "Client key" / "Client key"
stats.col.name = "Name" / "Tên"
stats.col.requests = "Requests" / "Requests"
stats.col.errors = "Errors" / "Lỗi"
stats.col.errorRate = "Error rate" / "Tỷ lệ lỗi"
stats.col.tokens = "Tokens" / "Tokens"
stats.more = "… and {0} more rows" / "… còn {0} dòng"
stats.empty = "No requests in this period" / "Chưa có request trong khoảng thời gian này"
stats.refreshFailed = "Refresh failed — showing last data" / "Không làm mới được — đang hiển thị số cũ"
```

- [ ] **Step 2: `MonitorFormat.Compact`** — helper static, XML doc; không test (UI helper, pattern `MonitorFormat` hiện không có test).
- [ ] **Step 3: `StatsChart.razor`** — pure presentational, không inject service; CSS style inline theo pattern `ApiMonitorPanel` (dùng CSS vars theme); SVG tính `max = Max(requests+errors)` làm thang; bar 0 → không vẽ segment nhưng vẫn có label.
- [ ] **Step 4: Build + commit** — `dotnet build router-balancing.slnx` (compile Razor); `git commit -m "feat: add stats chart component and i18n strings"`.

### Task 6: `StatsPanel` + mount vào Dashboard

**Files:**
- Create: `router-balancing/Components/Shared/StatsPanel.razor`
- Modify: `router-balancing/Components/Pages/Dashboard.razor` (sau `.dash-trace-grid`, dòng ~67)

**Interfaces:**
- Consumes: `IStatsService`, `IStatsInvalidationSource`, `StatsSnapshot/StatsRange/StatsBreakdownRow`, `StatsChart`, `MonitorFormat.Compact/Duration`, `EmptyState`, keys `stats.*`, `Settings.StatsErrorRateThreshold`.

- [ ] **Step 1: Implement `StatsPanel.razor`** (mirror subscribe/dispose pattern của `ApiMonitorPanel` — `_disposed` flag, `InvokeAsync`, unsubscribe `Changed` trong `DisposeAsync`):
  - State: `StatsRange _range = StatsRange.Today`; `StatsSnapshot? _snapshot`; `string? _selectedTab` (`"provider"|"model"|"clientKey"`); `CancellationTokenSource? _debounce`.
  - `OnInitialized`: subscribe `Changed`; `RefreshAsync()` lần đầu.
  - `OnChanged`: cancel `_debounce` cũ, `Task.Delay(2000)` mới → `RefreshAsync()` (fire-and-forget có try/catch; check `_disposed` trước StateHasChanged).
  - `RefreshAsync`: `_snapshot = await Stats.GetRangeAsync(_range)` (try/catch → log + cờ `_refreshFailed` hiển thị `stats.refreshFailed`); đọc lại threshold mỗi lần fetch.
  - Render: header + chips (3 nút, active theo `_range`, onclick set + fetch ngay); 4 KPI card (Requests `N0`; Error rate `0.0"%"` + badge under `text-success`/over `text-warning` khi `>= threshold`; Latency `MonitorFormat.Duration(TimeSpan.FromMilliseconds(avg))` + `stats.kpi.p95`; Tokens `Compact`); `<StatsChart Buckets="_snapshot.Buckets" />`; tabs + bảng breakdown (cột theo tab; **Error rate per row = `Errors/Requests`** — breakdown row không tách Cancelled, khác định nghĩa global; cap 20 dòng + `stats.more`); `_snapshot is null` → card skeleton "—" rỗng; `Requests==0` → `EmptyState` cho chart + bảng.
- [ ] **Step 2: Mount** — `<StatsPanel />` sau `</div>` của `.dash-trace-grid` trong `Dashboard.razor`.
- [ ] **Step 3: Build + full test** — `dotnet build router-balancing.slnx`, `dotnet test` → xanh.
- [ ] **Step 4: Manual checklist** (ghi kết quả vào commit message cuối hoặc PR note): chạy app → panel hiện dưới trace; chip đổi range hoạt động; gửi vài request qua curl `127.0.0.1:8317/v1/chat/completions` → số liệu tăng sau ≤2s; đổi theme light/dark đúng màu; đổi ngôn ngữ EN/VI đủ key.
- [ ] **Step 5: Commit** — `git commit -m "feat: add dashboard stats panel with history aggregates"`.
