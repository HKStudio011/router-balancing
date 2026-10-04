# Plan: Manual Retry 3 cấp (bỏ watchdog/MaxRetry) + Fatal → park + failover + Ping provider + Dashboard card & URL /v1 + Timeout 60s

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

- **Spec (nguồn requirement duy nhất):** `docs/superpowers/specs/2026-10-04-manual-retry-revamp-design.md` (commit `253b103`, Approved) — plan này chỉ chi tiết hóa; mọi sai khác so với spec đều đánh dấu trong bảng Deviation bên dưới.
- **Baseline:** branch `feat/lan-proxyfix-pin-peraccount` @ `253b103`, **570 tests** (đếm qua `dotnet test --list-tests`).
- **Kỳ vọng cuối:** **588 tests** (ladder bên dưới), 0 failed (hoặc đúng 2 `SingleInstanceGuardTests` khi app đang chạy — môi trường).
- **Execution:** `subagent-driven-development` — 9 task tuần tự, mỗi task 1 commit message tiếng Anh conventional.

## Goal

Hoàn thành toàn bộ spec 2026-10-04 (6 mục tiêu G1–G6):

1. **G1 — URL `/v1`:** Dashboard hiển thị URL OpenAI-style `http://127.0.0.1:{port}/v1` (loopback + LAN).
2. **G2 — Fatal → park + failover:** 401/403 → park **account**; 404+`model_not_found` → park **model**; 404 khác/mạng → park **provider**; vẫn failover tiếp; hết client mới nhận response cuối.
3. **G3 — Bỏ retry tự động:** xóa `ModelHealthStore`/`IModelHealthStore`/`ModelHealthWatchdog`, bỏ tích lũy `MaxRetry`, bỏ watchdog probe; 429/5xx vẫn `Retryable` (advance không park), 4xx khác vẫn `Passthrough`.
4. **G4 — Phục hồi thủ công:** card danh sách retry trên Dashboard + nút [Retry now] → `Unpark` + toast + log Info; ping tự phục hồi qua setting `pingParkedProviders` (mặc định tắt).
5. **G5 — Ping định kỳ:** `ProviderPingService` (BackgroundService) tick `pingIntervalSec`, probe `GET {base}/v1/models` — 401/403/404/mạng → `Park(Provider)`; skip Anthropic; bỏ qua 429/5xx.
6. **G6 — Timeout:** `ConnectTimeout` 10s → 60s (3 nơi); timeout `provider-probe` thành setting `providerProbeTimeoutSec` (default 60s) áp dụng per-request qua `DelegatingHandler`.

Ngoài phạm vi (giữ nguyên, không đụng): endpoint Anthropic `/v1/messages`, FreeModelSync, persist danh sách ra DB, nav badge, failover walk/queue/selection core.

## Architecture

- **Store mới (`src/RouterBalancing.Core/Engine/`):** `ManualRetryStore` + `IManualRetryStore` (Singleton, in-memory, 1 lock cho dict, `event Action? Changed`) — 3 cấp `Provider/Account/Model` + `ManualRetryReason`; `Park` idempotent (giữ `ParkedAt`, update `Reason`), tự log Warn transition đúng 1 lần qua `SafeLog`. Thay `ModelHealthStore` + `ModelHealthWatchdog` (2 file này **xóa**).
- **Phân loại (`ChatCompletionsHandler.ForwardAsync`):** outcome mới `DispatchOutcome.Fatal(ManualRetryLevel, long Id, string ModelId, ManualRetryReason, int? Status, string? ContentType, byte[] Body, TimeSpan? RetryAfter)`; 401/403→Account, 404+`error.code=model_not_found`→Model, 404 khác→Provider, mạng→Provider(`Status=null`); 429/408/5xx vẫn `Retryable`; 4xx khác vẫn `Passthrough`.
- **Walk (`DispatcherLoop`):** nhánh `Retryable or Fatal` gộp — `Fatal` → `store.Park` (bọc try) → MarkTried → advance; `FilterRemaining` thêm `IsProviderParked` / `IsModelParked` / provider không còn TK `Enabled && !IsAccountParked`; xóa `RecordSuccess`/`RecordExhaustion`/`RetryState.TriedModels`; `LastRetryable` → `LastFailure` (nhận từ cả 2 outcome).
- **Capacity (`ExecutionList`):** inject store — `TryEnter` loại TK đang park (filter **sau khi materialize** — EF không translate method store); mọi TK park → sentinel `0` → forward 503, không treo.
- **Gate (`ProxyApp`):** `IManualRetryStore.IsModelParked(prepared.ModelId)` → 503 trước khi vào queue — contract message `The model '{model}' is temporarily unchanged`.
- **Ping (`src/RouterBalancing.Core/Providers/ProviderPingService.cs`):** `BackgroundService` (Singleton + hosted qua factory) — tick `pingIntervalSec`, probe qua client `provider-probe` đã gắn `ProviderProbeTimeoutHandler`, set `ProxyTarget.Current` quanh call; fatal → Park, `pingParkedProviders=true` → 2xx Unpark.
- **UI (Razor):** `Dashboard.razor` — URL `/v1` (T1) + card danh sách + [Retry now] (T8); `SettingsPanel.razor` — gỡ MaxRetry/watchdog, thêm ping/probe fields (T6).
- **Settings:** +`providerProbeTimeoutSec` (T2), +`pingIntervalSec`/`pingParkedProviders` & gỡ `maxRetry`/`watchdogIntervalSec` (T6).
- **i18n:** mọi key thêm/sửa đều phải có ở **CẢ 2 dict** `English` + `Vietnamese` — `TranslationParityTests` enforce.

## Tech stack

.NET 10 / EF Core 8 (SQLite) / ASP.NET Core Kestrel + TestServer + `BackgroundService` / xUnit / .NET MAUI Blazor Hybrid (Razor Components) / Tailwind-classes UI.

## File structure (tóm tắt)

| Hành động | File |
|---|---|
| Tạo | `src/RouterBalancing.Core/Providers/ProviderProbeTimeoutHandler.cs` (T2) |
| Tạo | `src/RouterBalancing.Core/Engine/IManualRetryStore.cs`, `src/RouterBalancing.Core/Engine/ManualRetryStore.cs` (T3) |
| Tạo | `src/RouterBalancing.Core/Providers/ProviderPingService.cs` (T7) |
| Tạo | `router balancing test/Providers/ProviderProbeTimeoutHandlerTests.cs` (T2), `router balancing test/Engine/ManualRetryStoreTests.cs` (T3), `router balancing test/Providers/ProviderPingServiceTests.cs` (T7) |
| Sửa | `Dashboard.razor` (T1+T8), `MauiProgram.cs` (T2+T4), `ProxyApp.cs` (T2+T4+T5+T7), `ProxyHost.cs` (T4), `DispatchOutcome.cs` (T4), `ChatCompletionsHandler.cs` (T4), `RetryState.cs` (T4+T5), `DispatcherLoop.cs` (T4+T5), `ExecutionList.cs` (T5; interface `IExecutionList` giữ nguyên), 5 file `Settings/*` (T2+T6), `Translations.cs` (T2+T6+T8), `SettingsPanel.razor` (T6), 8 file test sửa (T2–T5: SettingsValidator/AppSettingsService/ChatCompletionsHandler/DispatcherLoop/RetryState/ProxyRetryIntegration/ExecutionList/ModelSelector Tests) |
| Xóa (T5) | `src/RouterBalancing.Core/Engine/IModelHealthStore.cs`, `ModelHealthStore.cs`, `ModelHealthWatchdog.cs`; `router balancing test/Engine/ModelHealthStoreTests.cs`, `ModelHealthWatchdogTests.cs`; `scripts/e2e-3c.sh` |

## Global constraints (mọi task)

- **REQUIRED SUB-SKILL:** `subagent-driven-development` — controller dispatch subagent từng task: task brief (verbatim từ plan) → implement TDD → review 2-axis (Spec/Standards) → fix loop → Approved → commit.
- **Gates** (repo root; trước gate 2 kiểm tra app MAUI: `Get-Process router-balancing -ErrorAction SilentlyContinue` — nếu có process → chấp nhận đúng 2 fail `SingleInstanceGuardTests`, nếu không → yêu cầu 0 failed):
  1. `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental` → 0 warning / 0 error.
  2. `dotnet test "router balancing test/router balancing test.csproj"` → all pass (570 → 588 theo ladder).
  3. `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → 0 warning / 0 error — **chỉ khi task chạm `router-balancing/`** (Razor/MauiProgram/csproj).
  - Quy tắc: **luôn** gate 1 + gate 2 sau mỗi task; gate 3 khi task chạm `router-balancing/`.
- **Flake policy** (rerun ≤3 lần + isolation, KHÔNG accept flake): `ProxyControlApiTests` (File.Delete IOException), `ProxyRetryIntegrationTests`, `ProxyQueueIntegrationTests.Cancel_QueuedRequest_OriginReceives400WithMatchedRequestId`.
- **Không push.** 1 task = 1 commit, message tiếng Anh conventional (`feat:`/`refactor:`/`fix:`/`docs:`).
- **Git:** repo có ~23 file phantom (khác biệt CRLF) — **chỉ `git add` đúng file của task**; app pid 38160 **không kill**; MAUI file-lock → đóng app trước gate 3/final.
- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên; không commit secret; không debug print; không TODO vô chủ.
- Comment: XML doc (`///`) cho public API + giải thích "tại sao" tiếng Việt; exception message tiếng Anh; identifier tiếng Anh.
- Unit test name mô tả hành vi: `Method_WhenX_ExpectsY`.
- CodeGraph không index repo này → dùng Read/Grep/Glob; `InternalsVisibleTo "router balancing test"` đã có.

## Bảng deviation (spec → plan)

| # | Spec nói | Plan làm | Lý do |
|---|---|---|---|
| V1 | §3.6 gỡ/thêm settings theo thứ tự tự nhiên | Đảo thứ tự: **T2** thêm `providerProbeTimeoutSec` (backend + i18n, CHƯA có UI field), **T6** gỡ `maxRetry`/`watchdogIntervalSec` + thêm `pingIntervalSec`/`pingParkedProviders` + sửa `SettingsPanel` một lần | timeout (G6) là dependency sớm; gỡ settings đụng 4 test → gộp mọi churn settings vào 1 task |
| D1 | §2.2 `Fatal(Level, Id, ModelId, Status, ContentType, Body, RetryAfter)` | Thêm field **`Reason` (`ManualRetryReason`)** | store log Warn cần lý do (`Đưa ... : {reason}`); dispatcher là nơi duy nhất biết lý do phân loại |
| V2 | §3.2 nhận diện `error.code == "model_not_found"` | Best-effort `JsonDocument.Parse` trong `try/catch (JsonException)`; body hỏng → 404 thường (park provider); so `ordinal-ignore-case` (`MODEL_NOT_FOUND` cũng khớp) | body hỏng KHÔNG được phá phân loại 401 (401/403 không cần parse — check status trước) |
| V3 | §3.3 "provider mọi TK đều `!Enabled` hoặc `IsAccountParked` → loại candidate" | `candidate.Provider.Accounts?.Any(a => a.Enabled && !store.IsAccountParked(a.Id)) != false` (null-nav) | provider thiếu nav `Accounts` (data cũ) → coi như còn TK, tránh filter oan |
| V4 | spec không nêu chuẩn hóa key | Store chuẩn hóa key **trong `Park`** (Provider/Account → `ModelId=""`; Model → `Id=0`) | mọi caller không phải nhớ convention; entry luôn đúng shape; test xác nhận |
| V5 | §3.3/§4: message 503 giữ nguyên; §5 log gate đổi wording | Gate log → `Từ chối request mới: model '{id}' đang trong danh sách retry thủ công` | 2 test chỉ assert substring `Từ chối request mới` + `'m1'` (ProxyRetryIntegrationTests:270/325) → an toàn; wording mới bỏ chữ "ManualRetry" của store cũ |
| V6 | §2.2 "đăng ký client `provider-probe` cho container proxy" | Đăng ký ở **T7** cùng `ProviderPingService` | test container không cần client này trước T7 — tránh đăng ký chết |
| V7 | §3.5 "`TimeProvider` inject — unit test điều khiển thời gian" | PingService: `await Task.Delay(delay, ct)` delay-first + expose `public Task PingAllAsync(CancellationToken)` cho test; **không** inject TimeProvider | test gọi thẳng `PingAllAsync` → không cần fake đồng hồ; YAGNI |
| V8 | spec không nhắc | T5 xóa luôn `scripts/e2e-3c.sh` | script e2e watchdog (đã grep: refs chỉ trong docs) — watchdog bị xóa ở spec này, giữ lại gây confusing |
| V9 | §5 "Advance failover — giữ nguyên" | `LogAdvance(request, candidate, int? status)` — câu `lỗi retryable (HTTP 401)` giữ nguyên cho cả Fatal | integration test assert câu log này; Fatal đi cùng nhánh log với Retryable |

## Test ladder

| Task | Δ tests | Expected |
|---|---|---|
| Baseline @ `253b103` | — | 570 |
| T1 Dashboard URL `/v1` | 0 (gate 3) | 570 |
| T2 Timeout 60s + setting probe | +4 `ProviderProbeTimeoutHandlerTests` (sửa 2 test Settings) | 574 |
| T3 `ManualRetryStore` 3 cấp | +10 `ManualRetryStoreTests` | 584 |
| T4 `Fatal` phân loại + loop park/failover | +9 (`ChatCompletionsHandlerTests` +7, `DispatcherLoopTests` +2; `RetryStateTests` rewrite net 0) | 593 |
| T5 Xóa store/watchdog cũ + filter/gate/ExecutionList | −18 (xóa 2 file test −22; `DispatcherLoopTests` +2/−2 net 0; `ProxyRetryIntegrationTests` +2, `ExecutionListTests` +2) | 575 |
| T6 Gỡ maxRetry/watchdog + thêm ping/probe UI | 0 (sửa 2 test Settings, count giữ nguyên) | 575 |
| T7 `ProviderPingService` | +13 (11 fact + theory 401/403 = 2 case) | 588 |
| T8 Dashboard card retry | 0 (checklist tay) | 588 |
| T9 Gates toàn cục + checklist + ledger | 0 | **588** |

---

## Task 1: Dashboard — hiển thị URL OpenAI-style `/v1` (G1)

### Files

- Modify: `router-balancing/Components/Pages/Dashboard.razor:21` (URL loopback), `:27` (URL LAN).

### Interfaces

- Consumes: `IProxyHost.Port`, `LanUrlProvider.GetUrls(int port)`, setting `lanAccess` — không đổi.
- Produces: 2 dòng render dạng `http://127.0.0.1:{port}/v1` và `{lanUrl}/v1` — Task 8 (card Dashboard) và checklist tay Task 9 dựa vào layout card Status hiện tại này.

### Steps

- [ ] **Step 1: Sửa URL loopback (line 21)**

  ```razor
  <!-- Trước -->
  <div class="mt-1 font-mono text-sm">http://127.0.0.1:@Proxy.Port</div>
  <!-- Sau -->
  <div class="mt-1 font-mono text-sm">http://127.0.0.1:@Proxy.Port/v1</div>
  ```

- [ ] **Step 2: Sửa URL LAN (line 27, bên trong `@foreach (var url in LanUrls)`)**

  ```razor
  <!-- Trước -->
  <div class="mt-1 font-mono text-sm">@url</div>
  <!-- Sau -->
  <div class="mt-1 font-mono text-sm">@url/v1</div>
  ```

  Lưu ý: Razor implicit expression kết thúc tại `/` → `@Proxy.Port/v1` render `...:8317/v1`. Nếu gate 3 báo lỗi identifier `v1` (Razor parse divergence hiếm) → đổi sang `@(Proxy.Port)/v1` và `@(url)/v1` rồi chạy lại gate 3.

- [ ] **Step 3: Gate 3 — build MAUI (Razor compile)**

  Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`
  Expected: 0 Warning / 0 Error.

- [ ] **Step 4: Gate 1 + 2 (rule chung — không sửa Core/test nhưng vẫn chạy)**

  Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental` rồi `dotnet test "router balancing test/router balancing test.csproj"`
  Expected: 0W/0E; 570 pass (hoặc đúng 2 fail `SingleInstanceGuardTests` nếu app đang chạy).

- [ ] **Step 5: Commit**

  ```bash
  git add router-balancing/Components/Pages/Dashboard.razor
  git commit -m "feat: show OpenAI-style /v1 URL on dashboard"
  ```

  (Kiểm tra `git status` — chỉ file này được staged; 23 file phantom CRLF KHÔNG add.)

---

## Task 2: Timeout 60s + setting `providerProbeTimeoutSec` (G6)

### Files

- Create: `src/RouterBalancing.Core/Providers/ProviderProbeTimeoutHandler.cs`.
- Modify: `src/RouterBalancing.Core/Settings/SettingsKeys.cs` (+1 const), `src/RouterBalancing.Core/Settings/IAppSettingsService.cs` (+1 prop), `src/RouterBalancing.Core/Settings/SettingsDraft.cs` (+1 prop), `src/RouterBalancing.Core/Settings/SettingsValidator.cs` (+1 rule), `src/RouterBalancing.Core/Settings/AppSettingsService.cs` (+1 prop).
- Modify: `router-balancing/MauiProgram.cs:93` và `:101` (ConnectTimeout → 60s), `:104-106` (client provider-probe timeout per-request), sau `:79` (đăng ký handler).
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs:53` (ConnectTimeout upstream chat → 60s).
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (+4 key = 2 × 2 dict).
- Test: tạo `router balancing test/Providers/ProviderProbeTimeoutHandlerTests.cs`; sửa `router balancing test/Settings/SettingsValidatorTests.cs`, `router balancing test/Settings/AppSettingsServiceTests.cs`.

### Interfaces

- Consumes: `IAppSettingsService.Set<T>/Get<T>`, `AppSettingsService(IDbContextFactory<RouterBalancingDbContext>)`, `DbInitializer.Initialize(factory)`, `TestDb`, pattern `DelegatingHandler` (xem `ProxyHealthHandler`).
- Produces (Task 6 + Task 7 dựa vào đây):
  - `SettingsKeys.ProviderProbeTimeoutSec` = `"providerProbeTimeoutSec"` (const).
  - `IAppSettingsService.ProviderProbeTimeoutSec` → `int`, default **60**.
  - `SettingsDraft.ProviderProbeTimeoutSec` → `int` = 60 (Task 6 bind input + `SaveEngine`).
  - Validator: lỗi `settings.error.probeTimeout` khi `ProviderProbeTimeoutSec` không thuộc `1..600` (field-name `nameof(SettingsDraft.ProviderProbeTimeoutSec)`).
  - `ProviderProbeTimeoutHandler(IAppSettingsService settings)` — DelegatingHandler gắn vào client `ProviderRequestFactory.HttpClientName`; Task 7 (ping) dùng lại client này.

### Steps

- [ ] **Step 1: RED — tạo test mới + sửa 2 test Settings (3 file)**

  Tạo `router balancing test/Providers/ProviderProbeTimeoutHandlerTests.cs`:

  ```csharp
  using System.Diagnostics;
  using System.Net;
  using RouterBalancing.Core.Providers;
  using RouterBalancing.Core.Settings;
  using RouterBalancing.Core.Storage;

  namespace router_balancing_test.Providers;

  public class ProviderProbeTimeoutHandlerTests : IDisposable
  {
      private readonly TestDb _db = new();

      public ProviderProbeTimeoutHandlerTests()
      {
          // TestDb trống — service đọc AppSettings nên phải migrate trước
          DbInitializer.Initialize(_db.CreateFactory());
      }

      public void Dispose() => _db.Dispose();

      /// <summary>Delay 30s — nếu timeout setting không cắt thì test fail (đợi hết delay).</summary>
      private sealed class StubInnerHandler : HttpMessageHandler
      {
          public int DelayMs { get; set; } = 30_000;

          protected override async Task<HttpResponseMessage> SendAsync(
              HttpRequestMessage request, CancellationToken cancellationToken)
          {
              await Task.Delay(DelayMs, cancellationToken);
              return new HttpResponseMessage(HttpStatusCode.OK);
          }
      }

      private (ProviderProbeTimeoutHandler Handler, AppSettingsService Settings, StubInnerHandler Inner)
          CreateHandler(int? probeTimeoutSec = null)
      {
          var settings = new AppSettingsService(_db.CreateFactory());
          if (probeTimeoutSec is { } sec)
              settings.Set(SettingsKeys.ProviderProbeTimeoutSec, sec);
          var inner = new StubInnerHandler();
          var handler = new ProviderProbeTimeoutHandler(settings) { InnerHandler = inner };
          return (handler, settings, inner);
      }

      // HttpMessageInvoker không wrap exception như HttpClient — message timeout của handler về nguyên vẹn
      private static async Task<HttpResponseMessage> SendAsync(
          HttpMessageHandler handler, CancellationToken ct = default)
      {
          using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
          return await invoker.SendAsync(
              new HttpRequestMessage(HttpMethod.Get, "http://localhost/"), ct);
      }

      [Fact]
      public async Task SendAsync_WhenProbeTimeoutElapsed_ThrowsTaskCanceledWithTimeoutMessage()
      {
          var (handler, _, _) = CreateHandler(probeTimeoutSec: 1);

          var sw = Stopwatch.StartNew();
          var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => SendAsync(handler));
          sw.Stop();

          // Setting 1s nhưng inner delay 30s — phải cắt ở ~1s, không đợi hết delay
          Assert.Contains("exceeded 1s timeout", ex.Message);
          Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"elapsed={sw.Elapsed}");
      }

      [Fact]
      public async Task SendAsync_WhenSettingChangedAfterHandlerCreated_UsesNewValueOnNextRequest()
      {
          var (handler, settings, _) = CreateHandler();           // default 60s lúc tạo handler
          settings.Set(SettingsKeys.ProviderProbeTimeoutSec, 1);  // đổi SAU — request kế phải đọc lại

          var sw = Stopwatch.StartNew();
          var ex = await Assert.ThrowsAsync<TaskCanceledException>(() => SendAsync(handler));
          sw.Stop();

          // Chứng minh per-request read (không cache trong ctor) — spec §3.8
          Assert.Contains("exceeded 1s timeout", ex.Message);
          Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"elapsed={sw.Elapsed}");
      }

      [Fact]
      public async Task SendAsync_WhenCallerCancels_PropagatesWithoutTimeoutWrap()
      {
          var (handler, _, _) = CreateHandler(); // default 60s — KHÔNG được tự timeout
          using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

          var sw = Stopwatch.StartNew();
          var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
              () => SendAsync(handler, cts.Token));
          sw.Stop();

          // Cancel của caller phải đi qua nguyên vẹn — không đổi thành timeout message
          Assert.DoesNotContain("exceeded", ex.Message);
          Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"elapsed={sw.Elapsed}");
      }

      [Fact]
      public async Task SendAsync_WhenCallCompletesInTime_ReturnsResponse()
      {
          var (handler, _, inner) = CreateHandler();
          inner.DelayMs = 0;

          var response = await SendAsync(handler);

          Assert.Equal(HttpStatusCode.OK, response.StatusCode);
      }
  }
  ```

  Sửa `router balancing test/Settings/SettingsValidatorTests.cs` — thêm dòng bold vào `ValidDraft()` (sau `WatchdogIntervalSec = 60,`):

  ```csharp
          ProviderProbeTimeoutSec = 60,
  ```

  Sửa test `Validate_WhenEngineValuesOutOfRange_ReturnsEachFieldError` — thêm `ProviderProbeTimeoutSec = 0,` vào object initializer và đổi thành:

  ```csharp
      [Fact]
      public void Validate_WhenEngineValuesOutOfRange_ReturnsEachFieldError()
      {
          var errors = SettingsValidator.Validate(ValidDraft() with
          {
              MaxRetry = 0,
              WatchdogIntervalSec = 5,
              ProviderProbeTimeoutSec = 0,
              LogRetentionDays = 0,
              StatsErrorRateThreshold = 101,
          });

          Assert.Equal(5, errors.Count);
          Assert.Equal("settings.error.maxRetry", errors[nameof(SettingsDraft.MaxRetry)]);
          Assert.Equal("settings.error.watchdog", errors[nameof(SettingsDraft.WatchdogIntervalSec)]);
          Assert.Equal("settings.error.probeTimeout", errors[nameof(SettingsDraft.ProviderProbeTimeoutSec)]);
          Assert.Equal("settings.error.retention", errors[nameof(SettingsDraft.LogRetentionDays)]);
          Assert.Equal("settings.error.threshold", errors[nameof(SettingsDraft.StatsErrorRateThreshold)]);
      }
  ```

  Sửa `router balancing test/Settings/AppSettingsServiceTests.cs` — trong `Get_MissingKey_ReturnsDefault`, sau `Assert.Equal(3, service.MaxRetry);` thêm:

  ```csharp
          Assert.Equal(60, service.ProviderProbeTimeoutSec);
  ```

- [ ] **Step 2: Chạy test — kỳ vọng RED (compile error)**

  Run: `dotnet build "router balancing test/router balancing test.csproj" --no-incremental`
  Expected: **FAIL** — `error CS0246: ProviderProbeTimeoutHandler` không tồn tại; `CS0117: SettingsDraft/SettingsKeys/IAppSettingsService` không có `ProviderProbeTimeoutSec`.

- [ ] **Step 3: Implement settings plumbing (5 file Core/Settings)**

  `SettingsKeys.cs` — thêm trước `LogRetentionDays`:

  ```csharp
      /// <summary>Timeout per-request của client provider-probe (spec manual-retry §3.8) — 1..600 giây.</summary>
      public const string ProviderProbeTimeoutSec = "providerProbeTimeoutSec";
  ```

  `IAppSettingsService.cs` — thêm sau `int WatchdogIntervalSec { get; }`:

  ```csharp
      /// <summary>Timeout per-request của client provider-probe — đọc tại mỗi request (§3.8).</summary>
      int ProviderProbeTimeoutSec { get; }
  ```

  `SettingsDraft.cs` — thêm sau `WatchdogIntervalSec`:

  ```csharp
      public int ProviderProbeTimeoutSec { get; set; } = 60;
  ```

  `SettingsValidator.cs` — thêm sau rule `WatchdogIntervalSec`:

  ```csharp
          if (draft.ProviderProbeTimeoutSec is < 1 or > 600)
              errors[nameof(SettingsDraft.ProviderProbeTimeoutSec)] = "settings.error.probeTimeout";
  ```

  `AppSettingsService.cs` — thêm sau `WatchdogIntervalSec`:

  ```csharp
      public int ProviderProbeTimeoutSec => Get(SettingsKeys.ProviderProbeTimeoutSec, 60);
  ```

- [ ] **Step 4: Tạo `ProviderProbeTimeoutHandler`**

  Tạo `src/RouterBalancing.Core/Providers/ProviderProbeTimeoutHandler.cs`:

  ```csharp
  using RouterBalancing.Core.Settings;

  namespace RouterBalancing.Core.Providers;

  /// <summary>
  /// Áp dụng setting <c>providerProbeTimeoutSec</c> cho mỗi request của client provider-probe
  /// (spec manual-retry §3.8) — đọc <see cref="IAppSettingsService"/> tại thời điểm gửi nên
  /// đổi setting có hiệu lực ngay, không cần rebuild HttpClient.
  /// </summary>
  public sealed class ProviderProbeTimeoutHandler : DelegatingHandler
  {
      private readonly IAppSettingsService _settings;

      public ProviderProbeTimeoutHandler(IAppSettingsService settings)
      {
          _settings = settings;
      }

      /// <inheritdoc/>
      protected override async Task<HttpResponseMessage> SendAsync(
          HttpRequestMessage request, CancellationToken cancellationToken)
      {
          // Linked CTS: timeout của handler KHÔNG được cancel token gốc của caller —
          // caller tự huỷ (TestConnection/FetchModels...) vẫn nhận OperationCanceledException bình thường.
          using var timeoutCts =
              CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
          timeoutCts.CancelAfter(TimeSpan.FromSeconds(_settings.ProviderProbeTimeoutSec));
          try
          {
              return await base.SendAsync(request, timeoutCts.Token);
          }
          catch (OperationCanceledException) when (
              timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
          {
              // Đổi thành TaskCanceledException có message timeout — caller code đang bắt
              // sẵn TaskCanceledException (xem ProviderEndpointMetadataProvider) không đổi hành vi.
              throw new TaskCanceledException(
                  $"Provider probe exceeded {_settings.ProviderProbeTimeoutSec}s timeout.");
          }
      }
  }
  ```

- [ ] **Step 5: MauiProgram — ConnectTimeout 60s + gắn handler (4 chỗ)**

  `router-balancing/MauiProgram.cs`:

  1. Line 93 (block `AddHttpClient(ProviderRequestFactory.HttpClientName)` đầu tiên): `ConnectTimeout = TimeSpan.FromSeconds(10),` → `ConnectTimeout = TimeSpan.FromSeconds(60),`.
  2. Line 101 (block `FreeModelSyncService.HttpClientName`): y hệt → `60`.
  3. Sau line 79 (`builder.Services.AddTransient<ProxyHealthHandler>();`) thêm:

     ```csharp
             builder.Services.AddTransient<ProviderProbeTimeoutHandler>();  // probe timeout per request (settings.providerProbeTimeoutSec)
     ```

  4. Lines 104-106 thay toàn bộ block:

     ```csharp
                 // Named client cho test connection/fetch models/metadata — timeout per-request
                 // qua ProviderProbeTimeoutHandler (spec manual-retry §3.8): đổi setting có hiệu lực ngay
                 builder.Services.AddHttpClient(ProviderRequestFactory.HttpClientName,
                     client => client.Timeout = Timeout.InfiniteTimeSpan)
                     .AddHttpMessageHandler<ProviderProbeTimeoutHandler>();
     ```

     (`Timeout.InfiniteTimeSpan` = `System.Threading.Timeout`, đã có qua ImplicitUsings; ProxyApp.cs:48 cũng dùng sẵn.)

- [ ] **Step 6: ProxyApp — ConnectTimeout upstream chat → 60s**

  `src/RouterBalancing.Core/Server/ProxyApp.cs:53`:

  ```csharp
              ConnectTimeout = TimeSpan.FromSeconds(60),
  ```

- [ ] **Step 7: Translations — 4 key (2 EN + 2 VI)**

  `src/RouterBalancing.Core/Localization/Translations.cs` — chèn chính xác tại các anchor:

  - EN, **sau** `["settings.field.watchdog"] = "Watchdog interval (s)",` (line 52):

    ```csharp
            ["settings.field.probeTimeout"] = "Provider probe timeout (s)",
    ```

  - EN, **sau** `["settings.error.watchdog"] = "Watchdog interval must be between 10 and 86400 seconds.",` (line 64):

    ```csharp
            ["settings.error.probeTimeout"] = "Provider probe timeout must be between 1 and 600 seconds.",
    ```

  - VI, **sau** `["settings.field.watchdog"] = "Chu kỳ watchdog (giây)",` (line 372):

    ```csharp
            ["settings.field.probeTimeout"] = "Timeout probe provider (giây)",
    ```

  - VI, **sau** `["settings.error.watchdog"] = "Chu kỳ watchdog phải từ 10–86400 giây.",` (line 384):

    ```csharp
            ["settings.error.probeTimeout"] = "Timeout probe provider phải trong khoảng 1–600 giây.",
    ```

  (2 key này sẽ được `SettingsPanel` dùng ở Task 6; chưa thêm field UI ở task này.)

- [ ] **Step 8: Gate 1 — build Core**

  Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental`
  Expected: 0 Warning / 0 Error.

- [ ] **Step 9: Chạy test GREEN (filtered trước, rồi toàn bộ)**

  Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProviderProbeTimeoutHandlerTests|FullyQualifiedName~SettingsValidatorTests|FullyQualifiedName~AppSettingsServiceTests"`
  Expected: 4 + 5 + 5 = 14 pass, 0 fail.

- [ ] **Step 10: Gate 2 — toàn bộ test**

  Run: `dotnet test "router balancing test/router balancing test.csproj"`
  Expected: **574 pass** (570 + 4), 0 failed (hoặc đúng 2 `SingleInstanceGuardTests`).

- [ ] **Step 11: Gate 3 — build MAUI (đụng MauiProgram.cs)**

  Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`
  Expected: 0 Warning / 0 Error. (Đóng app MAUI trước nếu đang chạy — tránh file-lock.)

- [ ] **Step 12: Commit**

  ```bash
  git add src/RouterBalancing.Core/Providers/ProviderProbeTimeoutHandler.cs src/RouterBalancing.Core/Settings/SettingsKeys.cs src/RouterBalancing.Core/Settings/IAppSettingsService.cs src/RouterBalancing.Core/Settings/SettingsDraft.cs src/RouterBalancing.Core/Settings/SettingsValidator.cs src/RouterBalancing.Core/Settings/AppSettingsService.cs src/RouterBalancing.Core/Localization/Translations.cs src/RouterBalancing.Core/Server/ProxyApp.cs router-balancing/MauiProgram.cs "router balancing test/Providers/ProviderProbeTimeoutHandlerTests.cs" "router balancing test/Settings/SettingsValidatorTests.cs" "router balancing test/Settings/AppSettingsServiceTests.cs"
  git commit -m "feat: raise connect timeouts to 60s and make probe timeout configurable"
  ```

  (Kiểm tra `git status` trước khi add — chỉ 12 file trên; phantom CRLF KHÔNG add.)

---

## Task 3: `ManualRetryStore` — store 3 cấp in-memory (G4/G2 nền tảng)

### Files

- Create: `src/RouterBalancing.Core/Engine/IManualRetryStore.cs` (enums + record + interface).
- Create: `src/RouterBalancing.Core/Engine/ManualRetryStore.cs`.
- Test: create `router balancing test/Engine/ManualRetryStoreTests.cs`.

### Interfaces

- Consumes: `ILogService.Warn(string, LogCategory)`/`Info`, `TimeProvider.GetUtcNow()` (đã đăng ký singleton `TimeProvider.System` ở MauiProgram:76 + ProxyApp:67), pattern `SafeLog` + `CapturingLog`/`FakeTime` (copy từ `ModelHealthStoreTests` — file này bị xóa ở Task 5 nên double phải sống trong test mới).
- Produces (Task 4/5/7/8 dùng đúng tên này):
  - `enum ManualRetryLevel { Provider, Account, Model }`, `enum ManualRetryReason { Unauthorized, NotFound, ModelNotFound, Unreachable }`.
  - `sealed record ManualRetryEntry(ManualRetryLevel Level, long Id, string ModelId, ManualRetryReason Reason, DateTimeOffset ParkedAt)`.
  - `interface IManualRetryStore` — `event Action? Changed`; `void Park(ManualRetryLevel level, long id, string modelId, ManualRetryReason reason)`; `void Unpark(ManualRetryLevel level, long id, string modelId)`; `bool IsProviderParked(long providerId)`; `bool IsAccountParked(long accountId)`; `bool IsModelParked(string modelId)`; `IReadOnlyList<ManualRetryEntry> GetEntries()`.
  - `class ManualRetryStore(ILogService log, TimeProvider time) : IManualRetryStore` — Task 4 đăng ký DI với ctor đúng 2 tham số này.
  - Quy ước (V4): `Park` chuẩn hóa **trong store** — `Provider`/`Account` → `ModelId=""`; `Model` → `Id=0`. Level log lowercase (`provider`/`account`/`model`), key log = Id invariant (level Provider/Account) hoặc ModelId (level Model).

### Steps

- [ ] **Step 1: RED — tạo test (10 facts)**

  Tạo `router balancing test/Engine/ManualRetryStoreTests.cs`:

  ```csharp
  using RouterBalancing.Core.Engine;
  using RouterBalancing.Core.Logging;

  namespace router_balancing_test.Engine;

  public class ManualRetryStoreTests
  {
      private readonly CapturingLog _log = new();
      private readonly FakeTime _time = new();

      private ManualRetryStore CreateSut() => new(_log, _time);

      [Fact]
      public void Park_WhenNewProviderEntry_ParksWarnsOnceAndFiresChanged()
      {
          var sut = CreateSut();
          var changed = 0;
          sut.Changed += () => changed++;

          sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable);

          Assert.True(sut.IsProviderParked(1));
          var entry = Assert.Single(sut.GetEntries());
          Assert.Equal(ManualRetryLevel.Provider, entry.Level);
          Assert.Equal(1, entry.Id);
          Assert.Equal("", entry.ModelId);
          Assert.Equal(ManualRetryReason.Unreachable, entry.Reason);
          Assert.Equal(_time.GetUtcNow(), entry.ParkedAt);
          var warn = Assert.Single(_log.Warns);
          Assert.Contains("Đưa provider '1' vào danh sách retry thủ công: Unreachable", warn);
          Assert.Equal(1, changed);
      }

      [Fact]
      public void Park_WhenAccountEntry_ModelIdNormalizedToEmpty()
      {
          var sut = CreateSut();

          sut.Park(ManualRetryLevel.Account, 7, "ignored", ManualRetryReason.Unauthorized);

          var entry = Assert.Single(sut.GetEntries());
          Assert.Equal("", entry.ModelId); // V4: account không mang ModelId
          Assert.True(sut.IsAccountParked(7));
      }

      [Fact]
      public void Park_WhenModelEntry_IdNormalizedToZero()
      {
          var sut = CreateSut();

          sut.Park(ManualRetryLevel.Model, 42, "gpt-x", ManualRetryReason.ModelNotFound);

          var entry = Assert.Single(sut.GetEntries());
          Assert.Equal(0, entry.Id); // V4: model key = ModelId, Id luôn 0
          Assert.Equal("gpt-x", entry.ModelId);
          Assert.True(sut.IsModelParked("gpt-x"));
      }

      [Fact]
      public void Park_WhenAlreadyParkedSameReason_KeepsParkedAtAndStaysSilent()
      {
          var sut = CreateSut();
          var changed = 0;
          sut.Changed += () => changed++;
          sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable);
          _time.Advance(TimeSpan.FromMinutes(5));

          sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable);

          var entry = Assert.Single(sut.GetEntries());
          Assert.Equal(_time.GetUtcNow().AddMinutes(-5), entry.ParkedAt); // giữ ParkedAt cũ
          Assert.Single(_log.Warns);  // không log lần 2
          Assert.Equal(1, changed);   // không event
      }

      [Fact]
      public void Park_WhenReasonChanged_UpdatesReasonFiresChangedKeepsParkedAt()
      {
          var sut = CreateSut();
          var changed = 0;
          sut.Changed += () => changed++;
          sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable);
          _time.Advance(TimeSpan.FromMinutes(5));

          sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unauthorized);

          var entry = Assert.Single(sut.GetEntries());
          Assert.Equal(ManualRetryReason.Unauthorized, entry.Reason);
          Assert.Equal(_time.GetUtcNow().AddMinutes(-5), entry.ParkedAt);
          Assert.Single(_log.Warns);  // transition log chỉ 1 lần
          Assert.Equal(2, changed);   // UI vẫn được báo để render lý do mới
      }

      [Fact]
      public void Unpark_WhenEntryExists_RemovesAndFiresChanged()
      {
          var sut = CreateSut();
          var changed = 0;
          sut.Changed += () => changed++;
          sut.Park(ManualRetryLevel.Model, 0, "m1", ManualRetryReason.ModelNotFound);

          sut.Unpark(ManualRetryLevel.Model, 0, "m1");

          Assert.False(sut.IsModelParked("m1"));
          Assert.Empty(sut.GetEntries());
          Assert.Equal(2, changed);
          Assert.Single(_log.Warns); // Unpark không log — UI/ping tự log Info (§5)
      }

      [Fact]
      public void Unpark_WhenEntryMissing_IsNoOp()
      {
          var sut = CreateSut();
          var changed = 0;
          sut.Changed += () => changed++;

          sut.Unpark(ManualRetryLevel.Provider, 99, "");

          Assert.Equal(0, changed);
          Assert.Empty(_log.Warns);
      }

      [Fact]
      public void GetEntries_OrdersByParkedAtDescending()
      {
          var sut = CreateSut();
          sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable);
          _time.Advance(TimeSpan.FromMinutes(1));
          sut.Park(ManualRetryLevel.Account, 2, "", ManualRetryReason.Unauthorized);
          _time.Advance(TimeSpan.FromMinutes(1));
          sut.Park(ManualRetryLevel.Model, 0, "m1", ManualRetryReason.ModelNotFound);

          var entries = sut.GetEntries();

          Assert.Equal(3, entries.Count);
          Assert.Equal("m1", entries[0].ModelId); // mới nhất trước
          Assert.Equal(ManualRetryLevel.Account, entries[1].Level);
          Assert.Equal(ManualRetryLevel.Provider, entries[2].Level);
      }

      [Fact]
      public void Park_Parallel400Times_LogsTransitionAndFiresChangedExactlyOnce()
      {
          var sut = CreateSut();
          var changed = 0;
          sut.Changed += () => Interlocked.Increment(ref changed);

          Parallel.For(0, 400, _ =>
              sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable));

          Assert.True(sut.IsProviderParked(1));
          // Check-then-act dưới lock: đúng 1 thread thấy transition
          Assert.Single(_log.Warns);
          Assert.Equal(1, changed);
      }

      [Fact]
      public void IsParked_WhenTwoLevelsShareSameId_KeepsEntriesIndependent()
      {
          var sut = CreateSut();
          sut.Park(ManualRetryLevel.Provider, 1, "", ManualRetryReason.Unreachable);
          sut.Park(ManualRetryLevel.Account, 1, "", ManualRetryReason.Unauthorized);

          // Cùng id 1 nhưng 2 cấp là 2 entry độc lập
          Assert.True(sut.IsProviderParked(1));
          Assert.True(sut.IsAccountParked(1));
          Assert.False(sut.IsModelParked(""));
          Assert.False(sut.IsAccountParked(2));
          Assert.Equal(2, sut.GetEntries().Count);
      }

      private sealed class FakeTime : TimeProvider
      {
          private DateTimeOffset _now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

          public override DateTimeOffset GetUtcNow() => _now;

          public void Advance(TimeSpan by) => _now += by;
      }

      private sealed class CapturingLog : ILogService
      {
          public List<string> Infos { get; } = [];
          public List<string> Warns { get; } = [];
          public List<string> Errors { get; } = [];

          public event Action<LogEntry>? LogAdded { add { } remove { } }
          public void Write(LogEntry entry) { }
          public void Info(string message, LogCategory category = LogCategory.App) => Infos.Add(message);
          public void Warn(string message, LogCategory category = LogCategory.App) => Warns.Add(message);
          public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
              Errors.Add(message);
          public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) { }
          public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
          public int Count(LogQuery query) => 0;
      }
  }
  ```

- [ ] **Step 2: Chạy build — kỳ vọng RED**

  Run: `dotnet build "router balancing test/router balancing test.csproj" --no-incremental`
  Expected: **FAIL** — `error CS0246: ManualRetryStore` / `ManualRetryLevel` không tồn tại.

- [ ] **Step 3: Tạo `IManualRetryStore.cs` (enums + record + interface)**

  Tạo `src/RouterBalancing.Core/Engine/IManualRetryStore.cs`:

  ```csharp
  namespace RouterBalancing.Core.Engine;

  /// <summary>Cấp gây lỗi — quyết định entity nào bị đưa vào danh sách retry thủ công.</summary>
  public enum ManualRetryLevel
  {
      /// <summary>Provider chết / sai endpoint — do ping hoặc lỗi mạng request-time.</summary>
      Provider,

      /// <summary>Sai auth (401/403) — auth nằm ở account (spec §1.3 #6).</summary>
      Account,

      /// <summary>Model không tồn tại ở provider (404 + error.code=model_not_found).</summary>
      Model,
  }

  /// <summary>Lý do vào danh sách — hiển thị trong UI và log transition.</summary>
  public enum ManualRetryReason
  {
      Unauthorized,
      NotFound,
      ModelNotFound,
      Unreachable,
  }

  /// <summary>Level + Id (providerId/accountId) + ModelId ("" với Provider/Account) — key định danh entry.</summary>
  public sealed record ManualRetryEntry(
      ManualRetryLevel Level, long Id, string ModelId,
      ManualRetryReason Reason, DateTimeOffset ParkedAt);

  /// <summary>
  /// Danh sách retry thủ công 3 cấp (in-memory, spec manual-retry §2.1) — thay ModelHealthStore.
  /// Thread-safe; không persist DB (restart = danh sách sạch, request/ping park lại khi lỗi còn).
  /// </summary>
  public interface IManualRetryStore
  {
      /// <summary>Phát sau mỗi lần Park/Unpark làm thay đổi danh sách — UI re-render.</summary>
      event Action? Changed;

      /// <summary>
      /// Đưa entity vào danh sách. Idempotent: đã park → giữ <c>ParkedAt</c>, cập nhật <paramref name="reason"/>;
      /// cùng lý do → im lặng (không log, không event). Log Warn transition đúng 1 lần.
      /// </summary>
      void Park(ManualRetryLevel level, long id, string modelId, ManualRetryReason reason);

      /// <summary>Gỡ entity khỏi danh sách — entry không tồn tại thì no-op.</summary>
      void Unpark(ManualRetryLevel level, long id, string modelId);

      /// <summary>Provider có đang trong danh sách không.</summary>
      bool IsProviderParked(long providerId);

      /// <summary>Account có đang trong danh sách không.</summary>
      bool IsAccountParked(long accountId);

      /// <summary>Model (exact-id) có đang trong danh sách không.</summary>
      bool IsModelParked(string modelId);

      /// <summary>Toàn bộ entry, xếp theo <see cref="ManualRetryEntry.ParkedAt"/> giảm dần (mới nhất trước).</summary>
      IReadOnlyList<ManualRetryEntry> GetEntries();
  }
  ```

- [ ] **Step 4: Tạo `ManualRetryStore.cs`**

  Tạo `src/RouterBalancing.Core/Engine/ManualRetryStore.cs`:

  ```csharp
  using RouterBalancing.Core.Logging;

  namespace RouterBalancing.Core.Engine;

  /// <inheritdoc cref="IManualRetryStore"/>
  public sealed class ManualRetryStore(ILogService log, TimeProvider time) : IManualRetryStore
  {
      private readonly object _lock = new();
      private readonly Dictionary<(ManualRetryLevel Level, long Id, string ModelId), ManualRetryEntry>
          _entries = new();

      /// <inheritdoc/>
      public event Action? Changed;

      /// <inheritdoc/>
      public void Park(ManualRetryLevel level, long id, string modelId, ManualRetryReason reason)
      {
          // V4: chuẩn hóa key trong store — mọi caller không phải nhớ convention (§2.1)
          if (level == ManualRetryLevel.Model)
              id = 0;
          else
              modelId = "";
          var key = (level, id, modelId);

          string? message = null;
          lock (_lock)
          {
              if (_entries.TryGetValue(key, out var existing))
              {
                  if (existing.Reason == reason)
                      return; // đã ở đúng state — im lặng, không log/không event
                  _entries[key] = existing with { Reason = reason };
              }
              else
              {
                  _entries[key] = new ManualRetryEntry(level, id, modelId, reason, time.GetUtcNow());
                  // Transition log đúng 1 lần (§5) — level lowercase theo i18n key, không log body/key
                  message = $"Đưa {LevelName(level)} '{KeyText(level, id, modelId)}' " +
                      $"vào danh sách retry thủ công: {reason}";
              }
          }
          // Log + event NGOÀI lock: subscriber chậm/SQLite lỗi không được giữ lock hay phá caller
          if (message is not null)
              SafeLog(() => log.Warn(message, LogCategory.App));
          Changed?.Invoke();
      }

      /// <inheritdoc/>
      public void Unpark(ManualRetryLevel level, long id, string modelId)
      {
          if (level == ManualRetryLevel.Model)
              id = 0;
          else
              modelId = "";
          bool removed;
          lock (_lock)
              removed = _entries.Remove((level, id, modelId));
          if (removed)
              Changed?.Invoke(); // log Info phục hồi do caller (Dashboard/ping) ghi — store không log (§5)
      }

      /// <inheritdoc/>
      public bool IsProviderParked(long providerId)
      {
          lock (_lock)
              return _entries.ContainsKey((ManualRetryLevel.Provider, providerId, ""));
      }

      /// <inheritdoc/>
      public bool IsAccountParked(long accountId)
      {
          lock (_lock)
              return _entries.ContainsKey((ManualRetryLevel.Account, accountId, ""));
      }

      /// <inheritdoc/>
      public bool IsModelParked(string modelId)
      {
          lock (_lock)
              return _entries.ContainsKey((ManualRetryLevel.Model, 0, modelId));
      }

      /// <inheritdoc/>
      public IReadOnlyList<ManualRetryEntry> GetEntries()
      {
          lock (_lock)
              return _entries.Values.OrderByDescending(e => e.ParkedAt).ToList();
      }

      private static string LevelName(ManualRetryLevel level) => level switch
      {
          ManualRetryLevel.Provider => "provider",
          ManualRetryLevel.Account => "account",
          _ => "model",
      };

      private static string KeyText(ManualRetryLevel level, long id, string modelId) =>
          level == ManualRetryLevel.Model
              ? modelId
              : id.ToString(System.Globalization.CultureInfo.InvariantCulture);

      /// <summary>Ghi log bọc nuốt — store cam kết không ném ra caller; SQLite lỗi không được phá request path.</summary>
      private static void SafeLog(Action write)
      {
          try
          {
              write();
          }
          catch
          {
              // Nuốt chủ đích: backend log (SQLite) không được làm hỏng request path (xem ModelHealthStore)
          }
      }
  }
  ```

- [ ] **Step 5: Gate 1 — build Core**

  Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental`
  Expected: 0 Warning / 0 Error.

- [ ] **Step 6: Chạy test GREEN**

  Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ManualRetryStoreTests"`
  Expected: 10 pass, 0 fail.

- [ ] **Step 7: Gate 2 — toàn bộ test**

  Run: `dotnet test "router balancing test/router balancing test.csproj"`
  Expected: **584 pass** (574 + 10), 0 failed (hoặc đúng 2 `SingleInstanceGuardTests`).

- [ ] **Step 8: Commit**

  ```bash
  git add src/RouterBalancing.Core/Engine/IManualRetryStore.cs src/RouterBalancing.Core/Engine/ManualRetryStore.cs "router balancing test/Engine/ManualRetryStoreTests.cs"
  git commit -m "feat: add three-level manual retry store"
  ```

---

## Task 4: `DispatchOutcome.Fatal` — phân loại lỗi + park/failover trong walk (G2/G3)

### Files

- Modify: `src/RouterBalancing.Core/Engine/DispatchOutcome.cs` (+record `Fatal`), `src/RouterBalancing.Core/Engine/RetryState.cs` (`LastRetryable` → `LastFailure`), `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs` (network catch + nhánh error + 2 helper mới), `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` (ctor + nhánh merged + `LogAdvance`), `src/RouterBalancing.Core/Server/ProxyApp.cs` (đăng ký store + guard `or Fatal`), `src/RouterBalancing.Core/Server/ProxyHost.cs` (ctor + truyền store), `router-balancing/MauiProgram.cs` (using + đăng ký store).
- Test: sửa `router balancing test/Engine/ChatCompletionsHandlerTests.cs` (+7, rewrite 1), `router balancing test/Engine/DispatcherLoopTests.cs` (StartAsync +2, extend network), `router balancing test/Engine/RetryStateTests.cs` (rewrite 2 fact), `router balancing test/Server/ProxyHostTests.cs` (3 ctor call).

### Interfaces

- Consumes (Task 3): `IManualRetryStore.Park/IsXxxParked/GetEntries`, `ManualRetryLevel`, `ManualRetryReason`, `ManualRetryStore(ILogService, TimeProvider)`.
- Produces:
  - `DispatchOutcome.Fatal(ManualRetryLevel Level, long Id, string ModelId, ManualRetryReason Reason, int? Status, string? ContentType, byte[] Body, TimeSpan? RetryAfter)` — **D1**: thêm `Reason` so với spec §2.2. `Status=null` ⇔ lỗi mạng.
  - `RetryState.Failure(int? Status, string? ContentType, byte[] Body, TimeSpan? RetryAfter)` nested record + property `RetryState.LastFailure` (thay `LastRetryable`).
  - `DispatcherLoop(IRequestQueue, IExecutionList, IComboResolver, IModelSelector, ChatCompletionsHandler, ILogService, IModelHealthStore, IManualRetryStore)` — **8 tham số**, `store` cuối cùng (Task 5 sẽ bỏ `health`).
  - `ProxyHost(..., IProxyPool pool, IManualRetryStore manualRetryStore)` — 7 tham số.
  - Proxy container resolve được `IManualRetryStore` (if-absent factory) — Task 7 (ping) và Task 5 (gate/ExecutionList) dùng.
  - **Lưu ý N2:** guard endpoint `or DispatchOutcome.Fatal` đổi ngay tại task này.

### Steps

- [ ] **Step 1: RED — sửa test (4 file, +9 fact mới)**

  **1a. `router balancing test/Engine/RetryStateTests.cs` — rewrite 2 fact (giữ 3 test):**

  Thay fact `RetryState_OnNewRequest_HasNothingTriedAndNoLastRetryable` (dòng 7-17):

  ```csharp
      [Fact]
      public void RetryState_OnNewRequest_HasNothingTriedAndNoLastFailure()
      {
          var state = new RetryState();

          Assert.False(state.HasTried);
          Assert.Equal(0, state.TriedCount);
          Assert.Null(state.LastFailure);
          Assert.False(state.IsTried(1, "m1"));
      }
  ```

  (Bỏ `Assert.Empty(state.TriedModels)` — property `TriedModels` bị xóa trong Task 5; test không được phụ thuộc.)

  Thay fact `MarkTried_SameModelOnTwoProviders_CountsTwoTriesButOneDistinctModel` (dòng 19-32):

  ```csharp
      [Fact]
      public void MarkTried_SameModelOnTwoProviders_CountsTwoTries()
      {
          var state = new RetryState();

          state.MarkTried(1, "m1");
          state.MarkTried(2, "m1");

          Assert.True(state.HasTried);
          Assert.Equal(2, state.TriedCount);
          // Cặp (provider, model) là key — cùng model 2 provider vẫn failover độc lập
          Assert.True(state.IsTried(1, "m1"));
          Assert.True(state.IsTried(2, "m1"));
      }
  ```

  Fact 3 (`IsTried_MarksOnlyTheExactProviderModelPair`) giữ nguyên.

  **1b. `router balancing test/Engine/ChatCompletionsHandlerTests.cs` — rewrite 1 + thêm 7.**

  Rewrite fact `ForwardAsync_WhenUpstreamThrows_ReturnsNetworkRetryableAndLogsError` (dòng 210-228), đổi tên:

  ```csharp
      [Fact]
      public async Task ForwardAsync_WhenUpstreamThrows_ReturnsFatalProviderAndLogsError()
      {
          var log = new CapturingLog();
          var provider = SeedProvider();
          var sut = Create(new ThrowingUpstream(new HttpRequestException("connection refused")), log);
          var ctx = Ctx();

          var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), default);

          // Mạng = Fatal(Provider, Status null) — dispatcher park + advance (spec §3.2);
          // 502 chỉ sinh ở exhaustion khi attempt cuối là mạng (§4)
          var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
          Assert.Equal(ManualRetryLevel.Provider, fatal.Level);
          Assert.Equal(provider.Id, fatal.Id);
          Assert.Equal("", fatal.ModelId);
          Assert.Equal(ManualRetryReason.Unreachable, fatal.Reason);
          Assert.Null(fatal.Status);
          Assert.Null(fatal.ContentType);
          Assert.Empty(fatal.Body);
          Assert.Null(fatal.RetryAfter);
          Assert.Single(log.Errors); // giữ log Error 3A
          Assert.Empty(log.Infos);
      }
  ```

  Thêm **7 fact MỚI** ngay sau fact `ForwardAsync_WhenUpstream400_ReturnsPassthroughWithoutWritingResponse` (sau dòng 294):

  ```csharp
      [Fact]
      public async Task ForwardAsync_WhenUpstream401_ReturnsFatalAccountCarryingPayload()
      {
          var log = new CapturingLog();
          var provider = SeedProvider();
          var upstreamBody = """{"error":{"message":"invalid api key"}}""";
          var sut = Create(new StubUpstream(() => Upstream(401, upstreamBody)), log);
          var ctx = Ctx();

          var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), default);

          // 401 = auth sai ở account (spec §1.3 #6/§3.2) — payload giữ cho exhaustion passthrough (§4)
          var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
          Assert.Equal(ManualRetryLevel.Account, fatal.Level);
          Assert.Equal(AccountIdOf(provider), fatal.Id);
          Assert.Equal("", fatal.ModelId);
          Assert.Equal(ManualRetryReason.Unauthorized, fatal.Reason);
          Assert.Equal(401, fatal.Status);
          Assert.StartsWith("application/json", fatal.ContentType);
          Assert.Equal(upstreamBody, Encoding.UTF8.GetString(fatal.Body));
          // Handler không ghi response — dispatcher/endpoint quyết định (parity 3A)
          var (status, _, body) = await ReadAsync(ctx);
          Assert.Equal(200, status);
          Assert.Equal(string.Empty, body);
      }

      [Fact]
      public async Task ForwardAsync_WhenUpstream403_ReturnsFatalAccount()
      {
          var provider = SeedProvider();
          var sut = Create(new StubUpstream(() => Upstream(403, """{"error":{"message":"forbidden"}}""")));
          var ctx = Ctx();

          var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), default);

          // 403 cùng nhóm lỗi auth với 401 → account cấp (spec §3.2)
          var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
          Assert.Equal(ManualRetryLevel.Account, fatal.Level);
          Assert.Equal(ManualRetryReason.Unauthorized, fatal.Reason);
          Assert.Equal(403, fatal.Status);
      }

      [Fact]
      public async Task ForwardAsync_WhenUpstream404ModelNotFound_ReturnsFatalModel()
      {
          var provider = SeedProvider();
          var upstreamBody =
              """{"error":{"message":"The model does not exist","code":"model_not_found"}}""";
          var sut = Create(new StubUpstream(() => Upstream(404, upstreamBody)));
          var ctx = Ctx();

          var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), default);

          // 404 + error.code=model_not_found = model sai — park model cấp với exact ModelId (§3.2)
          var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
          Assert.Equal(ManualRetryLevel.Model, fatal.Level);
          Assert.Equal(0, fatal.Id);
          Assert.Equal("gpt-4o-mini", fatal.ModelId);
          Assert.Equal(ManualRetryReason.ModelNotFound, fatal.Reason);
          Assert.Equal(404, fatal.Status);
          Assert.Equal(upstreamBody, Encoding.UTF8.GetString(fatal.Body));
      }

      [Fact]
      public async Task ForwardAsync_WhenUpstream404CodeUppercase_MatchesCaseInsensitive()
      {
          var provider = SeedProvider();
          var sut = Create(new StubUpstream(() => Upstream(404,
              """{"error":{"message":"nope","code":"MODEL_NOT_FOUND"}}""")));
          var ctx = Ctx();

          var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), default);

          // ordinal-ignore-case — provider code khác casing vẫn nhận diện (V2)
          var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
          Assert.Equal(ManualRetryLevel.Model, fatal.Level);
          Assert.Equal(ManualRetryReason.ModelNotFound, fatal.Reason);
      }

      [Fact]
      public async Task ForwardAsync_WhenUpstream404PlainBody_ReturnsFatalProvider()
      {
          var provider = SeedProvider();
          var sut = Create(new StubUpstream(() => Upstream(404,
              """{"error":{"message":"not found"}}""")));
          var ctx = Ctx();

          var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), default);

          // 404 thường = sai endpoint/provider chết — park provider cấp (§3.2)
          var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
          Assert.Equal(ManualRetryLevel.Provider, fatal.Level);
          Assert.Equal(provider.Id, fatal.Id);
          Assert.Equal("", fatal.ModelId);
          Assert.Equal(ManualRetryReason.NotFound, fatal.Reason);
      }

      [Fact]
      public async Task ForwardAsync_WhenUpstream404BodyIsNotJson_ReturnsFatalProvider()
      {
          var provider = SeedProvider();
          var sut = Create(new StubUpstream(() => Upstream(404, "<html>404</html>", "text/html")));
          var ctx = Ctx();

          var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), default);

          // Body không parse được → coi 404 thường, không crash phân loại (V2)
          var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
          Assert.Equal(ManualRetryLevel.Provider, fatal.Level);
          Assert.Equal(ManualRetryReason.NotFound, fatal.Reason);
      }

      [Fact]
      public async Task ForwardAsync_WhenUpstream401BodyIsNotJson_StillReturnsFatalAccount()
      {
          var provider = SeedProvider();
          var sut = Create(new StubUpstream(() => Upstream(401, "oops", "text/plain")));
          var ctx = Ctx();

          var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), AccountIdOf(provider), default);

          // 401 check theo TRẠNG THÁI trước, không parse body — body hỏng vẫn Fatal(Account)
          var fatal = Assert.IsType<DispatchOutcome.Fatal>(outcome);
          Assert.Equal(ManualRetryLevel.Account, fatal.Level);
          Assert.Equal(ManualRetryReason.Unauthorized, fatal.Reason);
      }
  ```

  **1c. `router balancing test/Engine/DispatcherLoopTests.cs` — sửa StartAsync + extend network + thêm 2 fact.**

  Sửa helper `StartAsync` (dòng 80-88) — thêm tham số `store` SAU `health`:

  ```csharp
      private async Task StartAsync(IComboResolver resolver, IModelSelector selector,
          IUpstreamClient upstream, CapturingLog log, ModelHealthStore? health = null,
          ManualRetryStore? store = null)
      {
          _settings ??= new AppSettingsService(_db.CreateFactory());
          health ??= new ModelHealthStore(_settings, log, TimeProvider.System);
          store ??= new ManualRetryStore(log, TimeProvider.System);
          var handler = new ChatCompletionsHandler(upstream, _protector, log, new NullUsageSink());
          _loop = new DispatcherLoop(_queue, _executions, resolver, selector, handler, log, health, store);
          await _loop.StartAsync(CancellationToken.None);
      }
  ```

  Thêm helper (đặt ngay sau `StartAsync`):

  ```csharp
      private long AccountIdOf(long providerId)
      {
          using var db = _db.CreateFactory().CreateDbContext();
          return db.Providers.Include(p => p.Accounts)
              .First(p => p.Id == providerId).Accounts[0].Id;
      }
  ```

  Extend fact `Loop_WhenAllCandidatesFailWithNetworkError_CompletesError502` (dòng 546-570) — thay dòng `await StartAsync(resolver, selector, upstream, log);` thành:

  ```csharp
          var store = new ManualRetryStore(log, TimeProvider.System);
          await StartAsync(resolver, selector, upstream, log, store: store);
  ```

  và thêm 3 assert sau assert `thất bại sau 2 candidate`:

  ```csharp
          // Mạng = Fatal(Provider) — park cả 2 provider, request sau bị filter (spec §3.2)
          Assert.True(store.IsProviderParked(p1));
          Assert.True(store.IsProviderParked(p2));
  ```

  Thêm **2 fact MỚI** (đặt sau fact `Loop_WhenCandidateReturns400_CompletesPassthroughWithoutAdvancing`, ~dòng 624):

  ```csharp
      [Fact]
      public async Task Loop_WhenFirstCandidate401_ParksAccountAndAdvancesToSecondProvider()
      {
          var p1 = SeedProvider("p1", modelId: "m1");
          var p2 = SeedProvider("p2", modelId: "m1"); // cùng model 2 provider — RR sort (p1, p2)
          var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
          var selector = new CountingSelector(new ModelSelector(_executions));
          var upstream = new ScriptedUpstream(p => p.Name == "p1"
              ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
              {
                  Content = new StringContent("""{"error":{"message":"invalid key"}}""",
                      Encoding.UTF8, "application/json"),
              }
              : Sse());
          var log = new CapturingLog();
          var store = new ManualRetryStore(log, TimeProvider.System);
          await StartAsync(resolver, selector, upstream, log, store: store);

          var request = Req("req00001");
          _queue.Enqueue(request);

          var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

          // Fatal 401 → park ĐÚNG cấp account rồi vẫn failover — client thấy 200 của p2 (§1.3 #4)
          Assert.IsType<DispatchOutcome.Handled>(outcome);
          Assert.Equal(2, upstream.Calls);
          Assert.True(store.IsAccountParked(AccountIdOf(p1)));
          Assert.False(store.IsAccountParked(AccountIdOf(p2)));
          Assert.False(store.IsProviderParked(p1)); // 401 cấp account, không phải provider (§3.2)
          Assert.Contains(log.Warns, w =>
              w.Contains("Chuyển candidate kế") && w.Contains("'p1'/'m1'")
              && w.Contains("HTTP 401") && w.Contains("req00001"));
      }

      [Fact]
      public async Task Loop_WhenAllCandidatesFatal404_CompletesPassthroughAndParksEachProvider()
      {
          var p1 = SeedProvider("p1", modelId: "m1");
          var p2 = SeedProvider("p2", modelId: "m2");
          var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.Fallback));
          var selector = new CountingSelector(new ModelSelector(_executions));
          var upstream = new ScriptedUpstream(p => new HttpResponseMessage(HttpStatusCode.NotFound)
          {
              Content = new StringContent(
                  $"{{\"error\":{{\"message\":\"from-{p.Name}\"}}}}", Encoding.UTF8, "application/json"),
          });
          var log = new CapturingLog();
          var store = new ManualRetryStore(log, TimeProvider.System);
          await StartAsync(resolver, selector, upstream, log, store: store);

          var request = Req("req00001");
          _queue.Enqueue(request);

          var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

          // 404 thường = provider sai endpoint — park từng provider, hết candidate
          // passthrough nguyên response cuối (§4), có log Error exhaustion (§5)
          var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome);
          Assert.Equal(404, passthrough.Status);
          Assert.Contains("from-p2", Encoding.UTF8.GetString(passthrough.Body));
          Assert.Equal(2, upstream.Calls);
          Assert.True(store.IsProviderParked(p1));
          Assert.True(store.IsProviderParked(p2));
          Assert.Contains(log.Errors,
              e => e.Contains("req00001") && e.Contains("thất bại sau 2 candidate"));
          Assert.Contains(log.Warns, w => w.Contains("Chuyển candidate kế") && w.Contains("HTTP 404"));
      }
  ```

  **1d. `router balancing test/Server/ProxyHostTests.cs` — using + 3 ctor call.**

  Thêm using (sau `using RouterBalancing.Core.Domain;`):

  ```csharp
  using RouterBalancing.Core.Engine;
  ```

  Sửa **cả 3 chỗ** `new ProxyHost(...)` (dòng 44-45, 170-171, 185-186) — thêm tham số thứ 7:

  ```csharp
          var host = new ProxyHost(_settings, _log, _db.CreateFactory(), new DpapiSecretProtector(), _clientKeys,
              new DirectProxyPool(), new ManualRetryStore(_log, TimeProvider.System));
  ```

- [ ] **Step 2: Chạy build test — kỳ vọng RED**

  Run: `dotnet build "router balancing test/router balancing test.csproj" --no-incremental`
  Expected: **FAIL** — `CS0117 DispatchOutcome không có Fatal`, `CS1729 RetryState ctor`, `CS7036 DispatcherLoop thiếu tham số`, `CS7036 ProxyHost thiếu tham số`, `CS1061 RetryState không có LastFailure`.

- [ ] **Step 3: `DispatchOutcome.cs` — thêm record `Fatal`**

  Thêm sau record `Retryable` (dòng 37):

  ```csharp
      /// <summary>
      /// Lỗi fatal (401/403/404/mạng) — tín hiệu NỘI BỘ: dispatcher park entity đúng cấp rồi
      /// advance như <see cref="Retryable"/> (spec manual-retry §2.2/§3.3); chỉ dispatcher nhìn thấy.
    /// </summary>
    /// <param name="Level">Cấp gây lỗi — quyết định entity nào vào danh sách retry.</param>
    /// <param name="Id">providerId/accountId; luôn 0 với <see cref="ManualRetryLevel.Model"/>.</param>
    /// <param name="ModelId">Model id với cấp <see cref="ManualRetryLevel.Model"/>; ngược lại "".</param>
    /// <param name="Reason">Lý do — store log transition, UI hiển thị (D1).</param>
    /// <param name="Status"><see langword="null"/> = lỗi mạng (không có HTTP response nào).</param>
    /// <param name="ContentType">Content-Type upstream trả (<see langword="null"/> khi lỗi mạng).</param>
    /// <param name="Body">Body đã buffer — giữ nguyên cho exhaustion passthrough (§4).</param>
    /// <param name="RetryAfter"><c>Retry-After</c> đã parse (null khi không có).</param>
    public sealed record Fatal(ManualRetryLevel Level, long Id, string ModelId,
        ManualRetryReason Reason, int? Status, string? ContentType, byte[] Body,
        TimeSpan? RetryAfter) : DispatchOutcome;
  ```

- [ ] **Step 4: `RetryState.cs` — `LastRetryable` → `LastFailure`**

  Thay property dòng 14-15 (giữ nguyên `HasTried`/`TriedCount`/`TriedModels`/`MarkTried`/`IsTried` — `TriedModels` xóa ở Task 5):

  ```csharp
      /// <summary>Thất bại gần nhất của 1 attempt — exhaustion convert thành Passthrough/Error(502).</summary>
      /// <param name="Status"><see langword="null"/> = lỗi mạng (không có HTTP response nào).</param>
      /// <param name="ContentType">Content-Type upstream trả (null khi lỗi mạng).</param>
      /// <param name="Body">Body đã buffer — response lỗi nhỏ, chưa commit (rỗng khi lỗi mạng).</param>
      /// <param name="RetryAfter"><c>Retry-After</c> đã parse — format lại header khi passthrough.</param>
      public sealed record Failure(int? Status, string? ContentType, byte[] Body, TimeSpan? RetryAfter);

      /// <summary>
      /// Thất bại gần nhất — nhận từ cả <see cref="DispatchOutcome.Retryable"/> lẫn
      /// <see cref="DispatchOutcome.Fatal"/>; attempt cuối quyết định exhaustion (spec §2.2).
      /// </summary>
      public Failure? LastFailure { get; set; }
  ```

- [ ] **Step 5: `ChatCompletionsHandler.cs` — network catch + ClassifyFatal + IsModelNotFound**

  5a. Thay return trong network catch (dòng 130-142) — chỉ sửa comment + dòng return, giữ nguyên log:

  ```csharp
                  // Lỗi upstream thật → Fatal(Provider, Status null) — dispatcher park + advance
                  // (spec §3.2); client tự ngắt (RequestAborted) thì propagate (hành vi 3A)
                  log.Write(new LogEntry
                  {
                      Severity = LogSeverity.Error,
                      Category = LogCategory.Request,
                      Message = $"Không kết nối được upstream '{provider.Name}'.",
                      Details = ex.ToString(),
                      ErrorCode = ex.GetType().Name,
                      RequestId = ClientKeyItems.RequestIdOf(ctx),
                      ClientKeyId = ClientKeyItems.IdOf(ctx),
                  });
                  return new DispatchOutcome.Fatal(ManualRetryLevel.Provider, provider.Id, "",
                      ManualRetryReason.Unreachable, null, null, [], null);
  ```

  5b. Thay nhánh error (dòng 180-191):

  ```csharp
                  // Lỗi chưa commit (vừa nhận header) — buffer để dispatcher quyết định advance/passthrough
                  var errorBody = await response.Content.ReadAsByteArrayAsync(ct);
                  var contentType = response.Content.Headers.ContentType?.ToString();
                  var retryAfterRaw = response.Headers.RetryAfter?.ToString();
                  if (RetryClassifier.IsRetryable(response.StatusCode))
                      return new DispatchOutcome.Retryable((int)response.StatusCode, contentType,
                          errorBody, RetryAfterParser.Parse(retryAfterRaw, DateTimeOffset.UtcNow));

                  // Journal Info chung cho Fatal lẫn Passthrough (parity quen sát 3A)
                  LogForwarded(ctx, provider, model, response, stopwatch);
                  return ClassifyFatal((int)response.StatusCode, provider, model, accountId,
                      errorBody, contentType, retryAfterRaw,
                      RetryAfterParser.Parse(retryAfterRaw, DateTimeOffset.UtcNow));
  ```

  5c. Thêm 2 helper `private static` ngay trước method `LogForwarded` (dòng ~199):

  ```csharp
      /// <summary>
      /// Phân loại lỗi non-retryable (spec manual-retry §3.2): 401/403 → account (auth nằm ở
      /// account — §1.3 #6); 404 có <c>error.code=model_not_found</c> → model; 404 còn lại →
      /// provider; status khác (400/409/422...) → Passthrough giữ nguyên hành vi 3A.
      /// Payload giữ nguyên cho cả Fatal — exhaustion passthrough nguyên response cuối (§4).
      /// </summary>
      private static DispatchOutcome ClassifyFatal(int status, Provider provider, Model model,
          long accountId, byte[] body, string? contentType, string? retryAfterRaw, TimeSpan? retryAfter)
      {
          if (status is 401 or 403)
          {
              return new DispatchOutcome.Fatal(ManualRetryLevel.Account, accountId, "",
                  ManualRetryReason.Unauthorized, status, contentType, body, retryAfter);
          }

          if (status == 404)
          {
              return IsModelNotFound(body)
                  ? new DispatchOutcome.Fatal(ManualRetryLevel.Model, 0, model.ModelId,
                      ManualRetryReason.ModelNotFound, status, contentType, body, retryAfter)
                  : new DispatchOutcome.Fatal(ManualRetryLevel.Provider, provider.Id, "",
                      ManualRetryReason.NotFound, status, contentType, body, retryAfter);
          }

          return new DispatchOutcome.Passthrough(status, contentType, body, retryAfterRaw);
      }

      /// <summary>
      /// Nhận diện <c>error.code == "model_not_found"</c> best-effort (V2): body hỏng/không phải
      /// JSON → coi 404 thường (park provider); so sánh ordinal-ignore-case.
      /// </summary>
      private static bool IsModelNotFound(ReadOnlySpan<byte> body)
      {
          try
          {
              using var doc = JsonDocument.Parse(body);
              return doc.RootElement.ValueKind == JsonValueKind.Object
                  && doc.RootElement.TryGetProperty("error", out var error)
                  && error.ValueKind == JsonValueKind.Object
                  && error.TryGetProperty("code", out var code)
                  && code.ValueKind == JsonValueKind.String
                  && string.Equals(code.GetString(), "model_not_found",
                      StringComparison.OrdinalIgnoreCase);
          }
          catch (JsonException)
          {
              return false;
          }
      }
  ```

  (`using System.Text.Json;` đã có ở dòng 2; comment XML doc của `ForwardAsync` — cập nhật mô tả: lỗi fatal (401/403/404/mạng) → `DispatchOutcome.Fatal`.)

- [ ] **Step 6: `DispatcherLoop.cs` — ctor + nhánh merged + LogAdvance**

  6a. Ctor (dòng 13-20) — thêm `IManualRetryStore store` cuối:

  ```csharp
  public sealed class DispatcherLoop(
      IRequestQueue queue,
      IExecutionList executions,
      IComboResolver resolver,
      IModelSelector selector,
      ChatCompletionsHandler handler,
      ILogService log,
      IModelHealthStore health,
      IManualRetryStore store) : BackgroundService
  ```

  6b. Thay nhánh `if (outcome is DispatchOutcome.Retryable retryable)` (dòng 183-250) — toàn bộ phần đầu đến `LogAdvance`:

  ```csharp
              if (outcome is DispatchOutcome.Retryable or DispatchOutcome.Fatal)
              {
                  request.Retry.MarkTried(candidate.Provider.Id, candidate.Model.ModelId);
                  // LastFailure nhận từ CẢ 2 outcome — attempt cuối quyết định exhaustion (§2.2)
                  request.Retry.LastFailure = outcome switch
                  {
                      DispatchOutcome.Retryable r =>
                          new RetryState.Failure(r.Status, r.ContentType, r.Body, r.RetryAfter),
                      DispatchOutcome.Fatal f =>
                          new RetryState.Failure(f.Status, f.ContentType, f.Body, f.RetryAfter),
                      _ => request.Retry.LastFailure,
                  };
                  if (outcome is DispatchOutcome.Fatal fatal)
                  {
                      try
                      {
                          // Park TRƯỚC khi filter — request kế loại entity ngay (§3.3);
                          // store KHÔNG được phá walk: lỗi log/lock chỉ nuốt (I2)
                          store.Park(fatal.Level, fatal.Id, fatal.ModelId, fatal.Reason);
                      }
                      catch
                      {
                          // Nuốt chủ đích: store lỗi không được chặn failover
                      }
                  }

                  var next = FilterRemaining(remaining, request);
                  if (next.Count == 0)
                  {
                      // RecordExhaustion TRƯỚC Exit — state phải xong trước khi Exited wake dispatch
                      // request đang queue, nếu không request kế sẽ gọi tiếp vào entity vừa chết (§3.4)
                      var exhausted = CompleteExhaustion(request);
                      executions.Exit(request.Id);
                      request.Completion.TrySetResult(exhausted);
                      return;
                  }

                  var advanceStatus = outcome switch
                  {
                      DispatchOutcome.Retryable r => r.Status,
                      DispatchOutcome.Fatal f => f.Status,
                      _ => null,
                  };
                  LogAdvance(request, candidate, advanceStatus);
  ```

  (Các dòng còn lại của nhánh — `executions.Exit` → select/tryenter → `continue` — **giữ nguyên y hệt** từ dòng 200 trở đi, chỉ đổi `retryable.Status` không còn tồn tại — đã thay bằng `advanceStatus`.)

  6c. `FilterRemaining` (dòng 287-293) — chỉ sửa comment (behavior T5):

  ```csharp
      /// <summary>Candidate chưa thử + model chưa bị park — dùng cho dispatch đầu và mỗi bước walk (§3.2/§3.4).</summary>
  ```

  6d. `RecordExhaustion` (dòng 306-330) — sửa 1 dòng: `var retryAfter = request.Retry.LastFailure?.RetryAfter;`.

  6e. `ExhaustionOutcome` (dòng 333-341) — sửa 1 dòng: `var last = request.Retry.LastFailure;`.

  6f. `LogAdvance` (dòng 344-358) — đổi签名 nhận `int? status` (V9 — Fatal ghi câu log y như Retryable):

  ```csharp
      /// <summary>Log Warn advance failover — chỉ khi thật sự còn candidate kế (spec §5); bọc nuốt (I2).</summary>
      private void LogAdvance(ProxyRequest request, ModelCandidate failed, int? status)
      {
          var reason = status is { } code ? $"HTTP {code}" : "lỗi mạng";
          try
          {
              log.Warn(
                  $"Chuyển candidate kế: '{failed.Provider.Name}'/'{failed.Model.ModelId}' " +
                  $"lỗi retryable ({reason}) — request {request.Id}", LogCategory.Request);
          }
          catch
          {
              // Nuốt chủ đích: log không được chặn walk
          }
      }
  ```

- [ ] **Step 7: `ProxyApp.cs` — đăng ký store + guard `or Fatal`**

  7a. Thay block sau đăng ký `IProxyPool` (sau dòng 37):

  ```csharp
          // ManualRetryStore dùng chung 1 instance/proxy container (spec manual-retry §2.2):
          // app container đã đăng ký (ProxyHost truyền singleton) → không ghi đè;
          // test container gọi thẳng ConfigureServices → tự tạo từ log + TimeProvider đã đăng ký
          if (!builder.Services.Any(d => d.ServiceType == typeof(IManualRetryStore)))
          {
              builder.Services.AddSingleton<IManualRetryStore>(
                  sp => new ManualRetryStore(sp.GetRequiredService<ILogService>(),
                      sp.GetRequiredService<TimeProvider>()));
          }
  ```

  7b. Thay guard (dòng 180-193):

  ```csharp
              else if (outcome is DispatchOutcome.Retryable or DispatchOutcome.Fatal)
              {
                  // Dispatcher đã convert Retryable/Fatal → Passthrough/Error (spec §2.2) — tới đây là bug
                  log.Write(new LogEntry
                  {
                      Severity = LogSeverity.Error,
                      Category = LogCategory.Request,
                      Message = $"Outcome nội bộ (Retryable/Fatal) lọt tới endpoint request {id}.",
                      RequestId = id,
                      ClientKeyId = ClientKeyItems.IdOf(ctx),
                  });
                  await ChatCompletionsHandler.WriteErrorAsync(ctx, 500, "Internal server error",
                      "server_error", null, null);
              }
  ```

  (Gate enqueue dòng 108-124 vẫn dùng `IModelHealthStore` — đổi sang store ở Task 5.)

- [ ] **Step 8: `ProxyHost.cs` — ctor + truyền store vào proxy container**

  8a. Thêm using (trước `using RouterBalancing.Core.Logging;`):

  ```csharp
  using RouterBalancing.Core.Engine;
  ```

  8b. Thêm field + tham số ctor (dòng 20-45):

  ```csharp
      private readonly IAppSettingsService _settings;
      private readonly ILogService _log;
      private readonly IDbContextFactory<RouterBalancingDbContext> _db;
      private readonly ISecretProtector _protector;
      private readonly IClientKeyService _clientKeys;
      private readonly IProxyPool _pool;
      private readonly IManualRetryStore _manualRetryStore;
      private readonly SemaphoreSlim _gate = new(1, 1);
      private WebApplication? _app;
      private bool _disposed;

      public int? Port { get; private set; }

      public bool IsRunning => _app is not null;

      public event Action? StateChanged;

      public ProxyHost(IAppSettingsService settings, ILogService log, IDbContextFactory<RouterBalancingDbContext> db,
          ISecretProtector protector, IClientKeyService clientKeys, IProxyPool pool,
          IManualRetryStore manualRetryStore)
      {
          _settings = settings;
          _log = log;
          _db = db;
          _protector = protector;
          _clientKeys = clientKeys;
          _pool = pool;
          _manualRetryStore = manualRetryStore;
      }
  ```

  8c. Trong `StartAsync`, thêm sau `builder.Services.AddSingleton(_clientKeys);` (dòng 73), TRƯỚC `ProxyApp.ConfigureServices`:

  ```csharp
              // Danh sách retry thủ công — share đúng instance với UI (spec manual-retry §2.2)
              builder.Services.AddSingleton(_manualRetryStore);
  ```

- [ ] **Step 9: `MauiProgram.cs` — using + đăng ký store**

  9a. Thêm using sau `using RouterBalancing.Core.Combos;` (dòng 7):

  ```csharp
  using RouterBalancing.Core.Engine;
  ```

  9b. Thêm đăng ký sau dòng 77 (`AddSingleton<IProxyPool, ProxyPool>()`):

  ```csharp
              // Danh sách retry thủ công 3 cấp (spec manual-retry §2.1) — ProxyHost chuyển
              // đúng instance này sang proxy container
              builder.Services.AddSingleton<IManualRetryStore, ManualRetryStore>();
  ```

- [ ] **Step 10: Gate 1 — build Core**

  Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental`
  Expected: 0 Warning / 0 Error.

- [ ] **Step 11: Chạy test GREEN (filtered trước)**

  Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ChatCompletionsHandlerTests|FullyQualifiedName~DispatcherLoopTests|FullyQualifiedName~RetryStateTests|FullyQualifiedName~ProxyHostTests"`
  Expected: 23 + 19 + 3 + (số fact ProxyHostTests hiện có) pass, 0 fail.

- [ ] **Step 12: Gate 2 — toàn bộ test**

  Run: `dotnet test "router balancing test/router balancing test.csproj"`
  Expected: **593 pass** (584 + 9), 0 failed (hoặc đúng 2 `SingleInstanceGuardTests`).

- [ ] **Step 13: Gate 3 — build MAUI (đụng MauiProgram.cs)**

  Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`
  Expected: 0 Warning / 0 Error.

- [ ] **Step 14: Commit**

  ```bash
  git add src/RouterBalancing.Core/Engine/DispatchOutcome.cs src/RouterBalancing.Core/Engine/RetryState.cs src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs src/RouterBalancing.Core/Engine/DispatcherLoop.cs src/RouterBalancing.Core/Server/ProxyApp.cs src/RouterBalancing.Core/Server/ProxyHost.cs router-balancing/MauiProgram.cs "router balancing test/Engine/ChatCompletionsHandlerTests.cs" "router balancing test/Engine/DispatcherLoopTests.cs" "router balancing test/Engine/RetryStateTests.cs" "router balancing test/Server/ProxyHostTests.cs"
  git commit -m "feat: park fatal upstream errors and fail over"
  ```

  (Kiểm tra `git status` trước — đúng 11 file; phantom CRLF KHÔNG add.)

---

## Task 5: Drive walk, gate, capacity từ `ManualRetryStore` — bỏ health/watchdog (G2/G4)

Refactor toàn bộ tín hiệu availability sang store 3 cấp: `DispatcherLoop` filter theo `IManualRetryStore`, `ProxyApp` gate theo store, `ExecutionList` bỏ TK parked. Xóa `ModelHealthStore`/`ModelHealthWatchdog` + test của chúng (circuit MaxRetry/Watchdog hết vai trò — spec manual-retry §3).

### Files

- Modify: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` (bỏ `health`, `FilterRemaining` 4 điều kiện, bỏ `RecordSuccess` + health-loop trong `RecordExhaustion`), `src/RouterBalancing.Core/Engine/ExecutionList.cs` (ctor 2 tham số + filter TK parked trong `LoadCapacityAsync`), `src/RouterBalancing.Core/Engine/RetryState.cs` (xóa `TriedModels`), `src/RouterBalancing.Core/Server/ProxyApp.cs` (xóa DI health/watchdog, gate → store + message V5).
- Delete: `src/RouterBalancing.Core/Engine/IModelHealthStore.cs` (chứa `ManualRetryModel`), `src/RouterBalancing.Core/Engine/ModelHealthStore.cs`, `src/RouterBalancing.Core/Engine/ModelHealthWatchdog.cs`, `router balancing test/Engine/ModelHealthStoreTests.cs`, `router balancing test/Engine/ModelHealthWatchdogTests.cs`, `scripts/e2e-3c.sh` (V8 — e2e dùng watchdog/MaxRetry đã bỏ).
- Test: sửa `router balancing test/Engine/DispatcherLoopTests.cs` (helper + rewrite 3/xóa 2/+2), `router balancing test/Server/ProxyRetryIntegrationTests.cs` (thay upstream helper + rewrite 4/+2), `router balancing test/Engine/ExecutionListTests.cs` (`CreateSut` +2), `router balancing test/Engine/ModelSelectorTests.cs` (ctor 2 tham số).

### Interfaces

- Consumes (Task 3+4): `IManualRetryStore` đầy đủ (`Park`/`Unpark`/`IsXxxParked`/`GetEntries`/`Changed`), `DispatchOutcome.Fatal`, `RetryState.LastFailure`/`Failure`.
- Produces:
  - `DispatcherLoop(IRequestQueue, IExecutionList, IComboResolver, IModelSelector, ChatCompletionsHandler, ILogService, IManualRetryStore)` — **7 tham số** (bỏ `IModelHealthStore`).
  - `ExecutionList(IDbContextFactory<RouterBalancingDbContext> db, IManualRetryStore store)` — 2 tham số; **interface `IExecutionList` giữ nguyên**.
  - `FilterRemaining` = chưa tried ∧ chưa parked (model/provider) ∧ còn TK `Enabled && !IsAccountParked` (**V3**: nav `Accounts` null → `?.Any != false` → không loại — `ExecutionList` tự filter sau materialize).
  - Gate endpoint: `IManualRetryStore.IsModelParked(prepared.ModelId)` + message **V5**; guard lỗi nội bộ (`or Fatal`) đã làm ở Task 4.
  - **Lưu ý sentinel:** provider chỉ còn TK parked → bị loại ngay tại `FilterRemaining`; sentinel `AccountId=0` còn hiệu lực ở `ExecutionList` (race giữa filter và enter, hoặc nav chưa load) — khi đó `TryEnter` tạo entry 0 → forward 503 thay vì park treo (Unpark không fire `Exited`).
  - `TimeProvider.System` trong proxy container **giữ lại** — fallback factory của `ManualRetryStore` (Task 4) resolve từ đó.

### Steps

- [ ] **Step 1: RED — `DispatcherLoopTests.cs` sửa helper**

  1a. Ctor (dòng 25-29) — `ExecutionList` giờ cần store (chỉ dùng cho filter TK parked; loop test không park account cấp trong ExecutionList nên NullLog là đủ):

  ```csharp
      public DispatcherLoopTests()
      {
          DbInitializer.Initialize(_db.CreateFactory());
          _executions = new ExecutionList(_db.CreateFactory(),
              new ManualRetryStore(new NullLog(), TimeProvider.System));
      }
  ```

  1b. Thay helper `StartAsync` (dòng 80-88) — bỏ `health`, bỏ `_settings`:

  ```csharp
      private async Task StartAsync(IComboResolver resolver, IModelSelector selector,
          IUpstreamClient upstream, CapturingLog log, ManualRetryStore? store = null)
      {
          store ??= new ManualRetryStore(log, TimeProvider.System);
          var handler = new ChatCompletionsHandler(upstream, _protector, log, new NullUsageSink());
          _loop = new DispatcherLoop(_queue, _executions, resolver, selector, handler, log, store);
          await _loop.StartAsync(CancellationToken.None);
      }
  ```

  1c. **Xóa** helper `NewStore` (dòng 90-95), xóa field `private AppSettingsService? _settings;` (dòng 23), xóa `_settings?.Dispose();` trong `Dispose` (dòng 37), xóa `using RouterBalancing.Core.Settings;` (dòng 9).

  (Fact T4-added gọi `StartAsync(..., store: store)` — named arg vẫn khớp. Helper `AccountIdOf` (T4) giữ nguyên.)

- [ ] **Step 2: `DispatcherLoopTests.cs` — rewrite 3 fact + xóa 2 fact**

  2a. Rewrite fact `Loop_WhenAllModelsInManualRetry_CompletesError503WithoutUpstreamCall` → đổi tên:

  ```csharp
      [Fact]
      public async Task Loop_WhenAllModelsParked_CompletesError503WithoutUpstreamCall()
      {
          var p1 = SeedProvider("p1", modelId: "m1");
          var p2 = SeedProvider("p2", modelId: "m2");
          var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.Fallback));
          var selector = new CountingSelector(new ModelSelector(_executions));
          var upstream = new ScriptedUpstream(_ => Sse());
          var log = new CapturingLog();
          var store = new ManualRetryStore(log, TimeProvider.System);
          store.Park(ManualRetryLevel.Model, 0, "m1", ManualRetryReason.ModelNotFound);
          store.Park(ManualRetryLevel.Model, 0, "m2", ManualRetryReason.ModelNotFound);
          await StartAsync(resolver, selector, upstream, log, store: store);

          var request = Req("req00001");
          _queue.Enqueue(request);

          var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

          // Walk rỗng ngay từ đầu (chưa thử gì) → 503, KHÔNG gọi upstream (§3.3)
          var error = Assert.IsType<DispatchOutcome.Error>(outcome);
          Assert.Equal(503, error.Status);
          Assert.Equal("The model 'm1' is temporarily unavailable", error.Message);
          Assert.Equal(0, upstream.Calls);
      }
  ```

  2b. Rewrite fact `Loop_WhenSomeModelsInManualRetry_SkipsDeadModelAndServesHealthyOne` → đổi tên + pre-park thay vì `RecordFailure`:

  ```csharp
      [Fact]
      public async Task Loop_WhenSomeModelsParked_SkipsParkedModelAndServesHealthyOne()
      {
          var p1 = SeedProvider("p1", modelId: "m1");
          var p2 = SeedProvider("p2", modelId: "m2");
          var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.Fallback));
          var selector = new CountingSelector(new ModelSelector(_executions));
          var upstream = new ScriptedUpstream(_ => Sse());
          var log = new CapturingLog();
          var store = new ManualRetryStore(log, TimeProvider.System);
          store.Park(ManualRetryLevel.Model, 0, "m1", ManualRetryReason.ModelNotFound); // chỉ m1 parked
          await StartAsync(resolver, selector, upstream, log, store: store);

          var request = Req("req00001");
          _queue.Enqueue(request);

          var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

          // Walk filter: model parked bị loại từ đầu — không advance, không Warn "Chuyển candidate kế"
          Assert.IsType<DispatchOutcome.Handled>(outcome);
          Assert.Equal(1, upstream.Calls);
          Assert.Contains(log.Infos, i => i.Contains("m2") && i.Contains("p2"));
          Assert.DoesNotContain(log.Warns, w => w.Contains("Chuyển candidate kế"));
      }
  ```

  2c. **Xóa 2 fact** (health không còn — hành vicovered bởi store unit test + ping test T7):

  - `Loop_WhenServedRequestGets2xx_ResetsModelConsecutiveFailureCounter`
  - `Loop_WhenExhaustionTriedTwoDistinctModels_IncrementsFailureCounterOfEachTriedModel`

  2d. Rewrite fact `Loop_WhenRequeuedRequestHasNoUntriedCandidates_CompletesExhaustionInsteadOfGate503` (giữ tên — thay pre-seed fuse bằng pre-park):

  ```csharp
      [Fact]
      public async Task Loop_WhenRequeuedRequestHasNoUntriedCandidates_CompletesExhaustionInsteadOfGate503()
      {
          // p1 giữ slot cho request kẹp (call#1); p2 là attempt đã thử của r1
          var p1 = SeedProvider("p1", maxConcurrent: 1, modelId: "m1");
          var p2 = SeedProvider("p2", modelId: "m2");
          var resolver = new ModelMapResolver(new Dictionary<string, SelectionResult>
          {
              ["m1"] = new SelectionSuccess([Candidate(p1)], ComboMode.Fallback),
              ["combo1"] = new SelectionSuccess([Candidate(p2), Candidate(p1)], ComboMode.Fallback),
          });
          var selector = new CountingSelector(new ModelSelector(_executions));
          var upstream = new GatedBadRequestUpstream();
          var log = new CapturingLog();
          var store = new ManualRetryStore(log, TimeProvider.System);
          await StartAsync(resolver, selector, upstream, log, store: store);

          // r2 kẹt trên p1 (giữ trọn slot duy nhất của p1)
          var r2 = Req("req00001", "m1");
          _queue.Enqueue(r2);
          await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5));

          // r1: p2 lỗi 429 → advance p1 nhưng p1 hết slot → re-enqueue (HasTried = true)
          var r1 = Req("req00002", "combo1");
          _queue.Enqueue(r1);
          await WaitUntilAsync(() => _queue.Contains("req00002") && upstream.Calls == 2);
          await Task.Delay(200); // chắc chắn đã park — không còn call nào chạy dở
          Assert.False(r1.Completion.Task.IsCompleted);

          // Park model m1 (candidate còn lại của r1) trong lúc r1 đang park
          store.Park(ManualRetryLevel.Model, 0, "m1", ManualRetryReason.ModelNotFound);
          Assert.True(store.IsModelParked("m1"));

          // r2 kết thúc 400 non-retryable → passthrough ngay (không đụng store)
          upstream.Release();
          var outcome2 = await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
          Assert.IsType<DispatchOutcome.Passthrough>(outcome2);

          // Dispatch lại: p2 đã tried + m1 đang parked → rỗng mà HasTried →
          // CompleteExhaustion (log Error) + Passthrough attempt cuối — KHÔNG phải 503 gate
          var outcome1 = await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
          var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome1);
          Assert.Equal(429, passthrough.Status);
          Assert.Equal("""{"error":{"message":"from-p2"}}""", Encoding.UTF8.GetString(passthrough.Body));
          Assert.Equal(2, upstream.Calls); // nhánh này không serve thêm upstream
          Assert.Contains(log.Errors,
              e => e.Contains("req00002") && e.Contains("thất bại sau 1 candidate"));
      }
  ```

- [ ] **Step 3: `DispatcherLoopTests.cs` — thêm 2 fact MỚI** (đặt sau fact `Loop_WhenCandidateReturns400_CompletesPassthroughWithoutAdvancing`)

  ```csharp
      [Fact]
      public async Task Loop_WhenProviderParked_SkipsCandidateAndServesOtherProvider()
      {
          var p1 = SeedProvider("p1", modelId: "m1");
          var p2 = SeedProvider("p2", modelId: "m1"); // cùng model 2 provider — RR sort (p1, p2)
          var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
          var selector = new CountingSelector(new ModelSelector(_executions));
          var upstream = new ScriptedUpstream(_ => Sse());
          var log = new CapturingLog();
          var store = new ManualRetryStore(log, TimeProvider.System);
          store.Park(ManualRetryLevel.Provider, p1, "", ManualRetryReason.Unreachable);
          await StartAsync(resolver, selector, upstream, log, store: store);

          var request = Req("req00001");
          _queue.Enqueue(request);

          var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

          // Provider parked bị loại từ dispatch đầu — chỉ p2 serve, không advance (§3.3)
          Assert.IsType<DispatchOutcome.Handled>(outcome);
          Assert.Equal(1, upstream.Calls);
          var info = Assert.Single(log.Infos);
          Assert.Contains("m1", info);
          Assert.Contains("p2", info);
          Assert.DoesNotContain("p1", info);
          Assert.DoesNotContain(log.Warns, w => w.Contains("Chuyển candidate kế"));
      }

      [Fact]
      public async Task Loop_WhenAllAccountsParked_Completes503WithoutUpstreamCall()
      {
          var pid = SeedProvider("p1"); // 1 TK enabled "a1"
          long accountId;
          using (var db = _db.CreateFactory().CreateDbContext())
              accountId = db.ProviderAccounts.Single(a => a.ProviderId == pid).Id;
          var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
          var selector = new CountingSelector(new ModelSelector(_executions));
          var upstream = new ScriptedUpstream(_ => Sse());
          var log = new CapturingLog();
          var store = new ManualRetryStore(log, TimeProvider.System);
          store.Park(ManualRetryLevel.Account, accountId, "", ManualRetryReason.Unauthorized);
          await StartAsync(resolver, selector, upstream, log, store: store);

          var request = Req("req00001");
          _queue.Enqueue(request);

          var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

          // Provider/model còn sống nhưng mọi TK enabled đã parked → walk rỗng →
          // 503 (chưa thử gì), KHÔNG gọi upstream (§3.3)
          var error = Assert.IsType<DispatchOutcome.Error>(outcome);
          Assert.Equal(503, error.Status);
          Assert.Equal("The model 'm1' is temporarily unavailable", error.Message);
          Assert.Equal(0, upstream.Calls);
      }
  ```

- [ ] **Step 4: `ProxyRetryIntegrationTests.cs` — thay helper + rewrite 4 fact + 2 fact mới**

  4a. Thay class `Gated429Upstream` (dòng 107-128 — chỉ fact sắp rewrite dùng) bằng `GatedFatalUpstream`:

  ```csharp
      // Call#1 giữ tới khi Release (request 2 kịp vào queue); sau Release trả 404 model_not_found
      // → Fatal cấp Model → park — model "tự chết" giữa 2 request
      private sealed class GatedFatalUpstream : IUpstreamClient
      {
          private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
          private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
          private int _calls;

          public int Calls => Volatile.Read(ref _calls);
          public Task Entered => _entered.Task;
          public void Release() => _release.TrySetResult();

          public async Task<HttpResponseMessage> PostChatCompletionAsync(
              Provider provider, string apiKey, byte[] body, CancellationToken ct)
          {
              if (Interlocked.Increment(ref _calls) == 1)
              {
                  _entered.TrySetResult();
                  await _release.Task;
              }
              return ModelNotFound404();
          }
      }
  ```

  Thêm helper tĩnh (đặt cạnh `Resp429`):

  ```csharp
      private static HttpResponseMessage ModelNotFound404() => new(HttpStatusCode.NotFound)
      {
          Content = new StringContent(
              """{"error":{"code":"model_not_found","message":"The model 'm1' does not exist"}}""",
              Encoding.UTF8, "application/json"),
      };
  ```

  4b. Rewrite fact `Chat_WhenModelExhaustsMaxRetry_RejectsNextRequestWith503BeforeQueue` (dòng 248-272) → đổi tên:

  ```csharp
      [Fact]
      public async Task Chat_WhenModelParkedByFatal_RejectsNextRequestWith503BeforeQueue()
      {
          SeedProvider("p1", maxConcurrent: 4, "m1");
          var upstream = new ScriptedUpstream(_ => ModelNotFound404());
          var client = await StartAsync(upstream);

          var first = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
          Assert.Equal(HttpStatusCode.NotFound, first.StatusCode); // Fatal → exhaustion passthrough attempt cuối
          Assert.Equal(1, upstream.Calls);

          var second = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

          // Gate (§3.3): 503 TRƯỚC khi vào queue — log Warn là assertion phân biệt
          // với walk-rỗng 503 của dispatcher (status/message giống hệt, không log)
          Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
          var error = (await ReadJson(second)).GetProperty("error");
          Assert.Equal("The model 'm1' is temporarily unavailable",
              error.GetProperty("message").GetString());
          Assert.Equal(1, upstream.Calls);
          Assert.Contains(_messages, m =>
              m.Contains("Từ chối request mới") && m.Contains("'m1'"));
          Assert.Empty(_app!.Services.GetRequiredService<IRequestQueue>().Snapshot());

          // Model parked thật trong store share với UI/ping
          Assert.True(_app.Services.GetRequiredService<IManualRetryStore>().IsModelParked("m1"));
      }
  ```

  4c. Rewrite fact `Chat_WhenComboHasManualRetryModel_SkipsDeadModelAndServesHealthyOne` (dòng 274-294) → đổi tên + pre-park:

  ```csharp
      [Fact]
      public async Task Chat_WhenComboHasParkedModel_SkipsDeadModelAndServesHealthyOne()
      {
          SeedProvider("p1", maxConcurrent: 4, "mA");
          SeedProvider("p2", maxConcurrent: 4, "mB");
          SeedCombo("combo-1", ComboMode.Fallback, (0, ModelKey("mA")), (1, ModelKey("mB")));
          var upstream = new ScriptedUpstream(p => p.Name == "p1" ? Resp429() : Sse());
          var client = await StartAsync(upstream);

          // Pre-park mA qua store singleton DI — dispatcher dùng đúng instance (Task 4 if-absent)
          var store = _app!.Services.GetRequiredService<IManualRetryStore>();
          store.Park(ManualRetryLevel.Model, 0, "mA", ManualRetryReason.ModelNotFound);

          var response = await client.PostAsync("/v1/chat/completions", ChatBody("combo-1"));

          // Walk filter loại mA từ đầu — chỉ mB@p2 được serve, không advance (§3.4)
          Assert.Equal(HttpStatusCode.OK, response.StatusCode);
          Assert.Equal(1, upstream.Calls);
          Assert.DoesNotContain(_messages, m => m.Contains("Chuyển candidate kế"));
      }
  ```

  4d. Rewrite fact `Chat_WhenFuseOpensWhileRequestQueued_QueuedGets503AndNewRejectedAtGate` (dòng 296-328) → đổi tên, dùng `GatedFatalUpstream`:

  ```csharp
      [Fact]
      public async Task Chat_WhenModelParksWhileRequestQueued_QueuedGets503AndNewRejectedAtGate()
      {
          SeedProvider("p1", maxConcurrent: 1, "m1");
          var upstream = new GatedFatalUpstream();
          var client = await StartAsync(upstream);

          var serving = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
          await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // call#1 giữ trọn slot
          var queued = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
          await WaitForQueuedIdAsync("m1"); // request 2 nằm trong queue, chưa dispatch (slot kín)

          upstream.Release();
          var first = await serving.WaitAsync(TimeSpan.FromSeconds(5));
          Assert.Equal(HttpStatusCode.NotFound, first.StatusCode); // Fatal → exhaustion passthrough

          // Park chạy TRƯỚC Exit (Task 4) → model parked trước khi wake dispatch request kế
          var store = _app!.Services.GetRequiredService<IManualRetryStore>();
          Assert.True(store.IsModelParked("m1"));

          // Request 2 đi dispatcher: walk rỗng (chưa thử gì) → 503, không log gate
          var second = await queued.WaitAsync(TimeSpan.FromSeconds(5));
          Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
          var walkError = (await ReadJson(second)).GetProperty("error");
          Assert.Equal("The model 'm1' is temporarily unavailable",
              walkError.GetProperty("message").GetString());

          // Request 3 bị chặn tại endpoint gate (log Warn phân biệt với walk-rỗng)
          var third = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
          Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);

          Assert.Contains(_messages, m =>
              m.Contains("Từ chối request mới") && m.Contains("'m1'"));
          Assert.Equal(1, upstream.Calls); // không ai gọi upstream sau khi model parked
          Assert.Empty(_app!.Services.GetRequiredService<IRequestQueue>().Snapshot());
      }
  ```

  4e. Rewrite fact `Chat_AfterProbeSucceeds_ModelServesRequestsAgain` (dòng 330-353) → đổi tên, unpark thủ công (probe watchdog đã xóa — ping tự phục hồi tới Task 7):

  ```csharp
      [Fact]
      public async Task Chat_AfterManualUnpark_ServesRequestsAgain()
      {
          SeedProvider("p1", maxConcurrent: 4, "m1");
          var failUpstream = true; // closure — đổi được giữa chừng (upstream "phục hồi")
          var upstream = new ScriptedUpstream(_ => failUpstream ? ModelNotFound404() : Sse());
          var client = await StartAsync(upstream);

          var first = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
          Assert.Equal(HttpStatusCode.NotFound, first.StatusCode); // passthrough attempt cuối
          var store = _app!.Services.GetRequiredService<IManualRetryStore>();
          Assert.True(store.IsModelParked("m1"));

          // Model parked → request mới bị gate chặn
          var blocked = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
          Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
          Assert.Equal(1, upstream.Calls);

          // Upstream phục hồi + user bấm [Retry now] (Task 8) → Unpark → serve lại
          failUpstream = false;
          store.Unpark(ManualRetryLevel.Model, 0, "m1");

          var second = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
          Assert.Equal(HttpStatusCode.OK, second.StatusCode);
          Assert.Equal(2, upstream.Calls);
      }
  ```

  4f. **2 fact MỚI** (đặt cuối class):

  ```csharp
      [Fact]
      public async Task Chat_WhenAccountParkedByFatal401_NextRequestFailsOverToOtherProvider()
      {
          SeedProvider("p1", maxConcurrent: 4, "m1");
          SeedProvider("p2", maxConcurrent: 4, "m1");
          var upstream = new ScriptedUpstream(p => p.Name == "p1"
              ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
              {
                  Content = new StringContent("""{"error":{"message":"Incorrect API key"}}""",
                      Encoding.UTF8, "application/json"),
              }
              : Sse());
          var client = await StartAsync(upstream);

          var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

          // 401 @p1 → Fatal cấp Account → park account + advance p2 — client thấy 200 (§1.3 #4)
          Assert.Equal(HttpStatusCode.OK, response.StatusCode);
          Assert.Equal(2, upstream.Calls);
          Assert.Contains(_messages, m =>
              m.Contains("Chuyển candidate kế") && m.Contains("HTTP 401"));
          var store = _app!.Services.GetRequiredService<IManualRetryStore>();
          Assert.Contains(store.GetEntries(), e =>
              e.Level == ManualRetryLevel.Account && e.Reason == ManualRetryReason.Unauthorized);

          // Request kế: p1 không còn TK dùng được → chỉ p2 serve (nếu p1 còn được chọn sẽ là 5 call)
          var second = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
          Assert.Equal(HttpStatusCode.OK, second.StatusCode);
          Assert.Equal(3, upstream.Calls);
      }

      [Fact]
      public async Task Chat_WhenNetworkErrorParksProvider_NextRequestFailsOverToOtherProvider()
      {
          SeedProvider("p1", maxConcurrent: 4, "m1");
          SeedProvider("p2", maxConcurrent: 4, "m1");
          var upstream = new ScriptedUpstream(p => p.Name == "p1"
              ? throw new HttpRequestException("connection refused")
              : Sse());
          var client = await StartAsync(upstream);

          var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

          // Mạng @p1 → Fatal(Provider) → park provider + advance p2 — client thấy 200 (§3.2)
          Assert.Equal(HttpStatusCode.OK, response.StatusCode);
          Assert.Equal(2, upstream.Calls);
          Assert.Contains(_messages, m =>
              m.Contains("Chuyển candidate kế") && m.Contains("lỗi mạng"));
          var store = _app!.Services.GetRequiredService<IManualRetryStore>();
          Assert.Contains(store.GetEntries(), e =>
              e.Level == ManualRetryLevel.Provider && e.Reason == ManualRetryReason.Unreachable);

          // Request kế: p1 parked bị loại từ đầu → chỉ p2 (bug sẽ ra 5 call)
          var second = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
          Assert.Equal(HttpStatusCode.OK, second.StatusCode);
          Assert.Equal(3, upstream.Calls);
      }
  ```

  (`_settings` + using `RouterBalancing.Core.Settings` giữ nguyên — `_settings` vẫn là `IAppSettingsService` cho container; các fact không còn gọi `_settings.Set(MaxRetry, ...)`.)

- [ ] **Step 5: `ExecutionListTests.cs` + `ModelSelectorTests.cs` — ctor 2 tham số + 2 fact mới**

  5a. `ExecutionListTests.cs` — thay `CreateSut` (dòng 42):

  ```csharp
      private ExecutionList CreateSut(ManualRetryStore? store = null) => new(
          _db.CreateFactory(), store ?? new ManualRetryStore(new NullLog(), TimeProvider.System));
  ```

  Thêm 2 fact (đặt trước helper `EnabledAccountIds`):

  ```csharp
      [Fact]
      public async Task TryEnter_WhenAccountParked_SkipsParkedAccount()
      {
          var pid = SeedProvider("p1", maxConcurrent: 4, accountCount: 2);
          var accounts = EnabledAccountIds(pid); // Id tăng dần
          var store = new ManualRetryStore(new NullLog(), TimeProvider.System);
          store.Park(ManualRetryLevel.Account, accounts[0], "", ManualRetryReason.Unauthorized);
          var sut = CreateSut(store);

          var ok = await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default);

          Assert.Equal(accounts[1], ok); // TK parked không bao giờ được chọn (manual-retry §3.3)
      }

      [Fact]
      public async Task TryEnter_WhenAllAccountsParked_ReturnsSentinelZeroAndCreatesEntry()
      {
          var pid = SeedProvider("p1", maxConcurrent: 4, accountCount: 2);
          var store = new ManualRetryStore(new NullLog(), TimeProvider.System);
          foreach (var id in EnabledAccountIds(pid))
              store.Park(ManualRetryLevel.Account, id, "", ManualRetryReason.Unauthorized);
          var sut = CreateSut(store);

          var sentinel = await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default);

          // Mọi TK enabled parked → sentinel 0 (y như 0 TK enabled) — park vô hạn sẽ treo vĩnh viễn
          // vì Unpark không fire Exited (không có wake signal) — V1 + manual-retry §3.3
          Assert.Equal(0, sentinel);
          var entry = Assert.Single(sut.Snapshot());
          Assert.Equal(0, entry.AccountId);
          Assert.Equal(string.Empty, entry.AccountName);
      }
  ```

  5b. `ModelSelectorTests.cs` — ctor (dòng 16):

  ```csharp
          _executions = new ExecutionList(_db.CreateFactory(),
              new ManualRetryStore(new NullLog(), TimeProvider.System));
  ```

- [ ] **Step 6: Xóa 2 file test health + build RED**

  ```bash
  git rm "router balancing test/Engine/ModelHealthStoreTests.cs" "router balancing test/Engine/ModelHealthWatchdogTests.cs"
  dotnet build "router balancing test/router balancing test.csproj" --no-incremental
  ```

  Expected: **FAIL** — `CS7036 DispatcherLoop không có ctor khớp 7 tham số` (src còn 8), `CS7036 ExecutionList không có ctor khớp 2 tham số` ×3 (DispatcherLoopTests, ExecutionListTests, ModelSelectorTests).

- [ ] **Step 7: `DispatcherLoop.cs` — bỏ health, filter theo store**

  7a. Ctor (dòng 13-20) — bỏ `IModelHealthStore health`:

  ```csharp
  public sealed class DispatcherLoop(
      IRequestQueue queue,
      IExecutionList executions,
      IComboResolver resolver,
      IModelSelector selector,
      ChatCompletionsHandler handler,
      ILogService log,
      IManualRetryStore store) : BackgroundService
  ```

  7b. `ServeAsync` XML doc (dòng 145-149):

  ```csharp
      /// <summary>
      /// Serve 1 request qua vòng walk: mỗi candidate 1 lần, Retryable/Fatal → Exit + advance kế;
      /// hết list → CompleteExhaustion (log Error) + Passthrough/Error502. Fire-and-forget từ
      /// <see cref="TryDispatchOnceAsync"/> — không block dispatcher loop.
      /// </summary>
  ```

  7c. Nhánh sau merged `Retryable or Fatal` — comment exhaustion (dòng 1483-1484) đổi thành:

  ```csharp
                      // CompleteExhaustion TRƯỚC Exit — log exhaustion ghi xong trước khi Exited
                      // wake dispatch request đang queue, nếu không log bị xót (I2)
  ```

  7d. Nhánh `Handled` (dòng 252-266) — bỏ block `health.RecordSuccess`, sửa comment:

  ```csharp
            // Không phải Retryable/Fatal: trả slot rồi complete (Handled/Passthrough/Error/Cancelled/Aborted)
            executions.Exit(request.Id);
            request.Completion.TrySetResult(outcome);
            return;
  ```

  7e. Thay `FilterRemaining` (dòng 287-293):

  ```csharp
      /// <summary>
      /// Candidate chưa thử + chưa parked (model/provider/account) — dùng cho dispatch đầu
      /// và mỗi bước walk (§3.2/§3.4).
      /// </summary>
      private IReadOnlyList<ModelCandidate> FilterRemaining(IReadOnlyList<ModelCandidate> candidates,
          ProxyRequest request) =>
          candidates
              .Where(c => !request.Retry.IsTried(c.Provider.Id, c.Model.ModelId)
                  && !store.IsModelParked(c.Model.ModelId)
                  && !store.IsProviderParked(c.Provider.Id)
                  && HasEnabledUnparkedAccount(c))
              .ToList();

      /// <summary>
      /// Còn TK enabled chưa parked (V3): nav Accounts chưa load (null) → không loại ở đây —
      /// ExecutionList tự filter sau materialize; mọi TK enabled parked → loại candidate
      /// (giữ lại thì TryEnter tạo sentinel 0 forward 503 thay vì chọn TK healthy).
      /// </summary>
      private bool HasEnabledUnparkedAccount(ModelCandidate candidate) =>
          candidate.Provider.Accounts?.Any(a => a.Enabled && !store.IsAccountParked(a.Id)) != false;
  ```

  7f. `CompleteExhaustion` + `RecordExhaustion` (dòng 295-330) — bỏ health-loop, chỉ còn log:

  ```csharp
      /// <summary>Ghi log Error exhaustion rồi trả outcome — gọi 2 nơi (§3.3/§3.4).</summary>
      private DispatchOutcome CompleteExhaustion(ProxyRequest request)
      {
          RecordExhaustion(request);
          return ExhaustionOutcome(request);
      }

      /// <summary>
      /// Log Error exhaustion (§5) — không còn record failure theo model (circuit đã bỏ,
      /// ManualRetry do Fatal park chủ động). Bọc try: log ném không được chặn TrySetResult (I2).
      /// </summary>
      private void RecordExhaustion(ProxyRequest request)
      {
          try
          {
              log.Error(
                  $"Request {request.Id} thất bại sau {request.Retry.TriedCount} candidate — " +
                  "chuyển phản hồi cuối về client", category: LogCategory.Request);
          }
          catch
          {
              // Nuốt chủ đích: log không được phá outcome
          }
      }
  ```

- [ ] **Step 8: `ExecutionList.cs` — ctor + filter TK parked**

  8a. Ctor (dòng 10):

  ```csharp
  public sealed class ExecutionList(IDbContextFactory<RouterBalancingDbContext> db,
      IManualRetryStore store) : IExecutionList
  ```

  8b. Comment sentinel trong `CanEnterAsync` (dòng 26-27) và `TryEnterAsync` (dòng 49):

  ```csharp
              // Sentinel (V1): 0 TK enabled hoặc mọi TK enabled đã parked → TryEnter vẫn tạo
              // entry AccountId=0 để forward 503; park ở đây sẽ treo vĩnh viễn vì không có
              // wake signal nào khi user bật lại TK / unpark (Unpark chỉ fire Changed)
  ```

  8c. `LoadCapacityAsync` — doc + filter sau materialize (dòng 116-139):

  ```csharp
      /// <summary>
      /// Query MaxConcurrent + TK enabled (Id, Name, Priority) mới nhất từ DB rồi loại TK
      /// đang parked (store in-memory — không dịch được sang SQL nên filter sau materialize);
      /// provider không tồn tại → null (D-B7 — không dùng FirstOrDefault = 0 vì 0 giờ là "không giới hạn").
      /// </summary>
      private async Task<(int Max, List<AccountSlot> Accounts)?> LoadCapacityAsync(
          long providerId, CancellationToken ct)
      {
          using var context = await db.CreateDbContextAsync(ct);
          var provider = await context.Providers.AsNoTracking()
              .Where(p => p.Id == providerId)
              .Select(p => new
              {
                  p.MaxConcurrent,
                  Accounts = p.Accounts
                      .Where(a => a.Enabled)
                      .Select(a => new { a.Id, a.Name, a.Priority })
                      .ToList(),
              })
              .FirstOrDefaultAsync(ct);
          return provider is null
              ? null
              : (provider.MaxConcurrent, provider.Accounts
                  .Where(a => !store.IsAccountParked(a.Id))
                  .Select(a => new AccountSlot(a.Id, a.Name, a.Priority)).ToList());
      }
  ```

- [ ] **Step 9: `RetryState.cs` — xóa `TriedModels`**

  Xóa property `TriedModels` (dòng 23-25) — chỉ còn `HasTried`/`TriedCount`/`MarkTried`/`IsTried` + `LastFailure`. Sửa doc class (dòng 3-9): "lỗi retryable gần nhất (spec 3C §3.2)" → "thất bại gần nhất (spec manual-retry §2.2)". (Test đã bỏ assert `TriedModels` từ Task 4.)

- [ ] **Step 10: `ProxyApp.cs` — bỏ DI health/watchdog, gate → store (V5)**

  10a. Xóa block đăng ký health + watchdog (dòng 65-67 comment cũ + 73-78):

  ```csharp
          builder.Services.AddSingleton(TimeProvider.System);
  ```

  Thay comment của dòng `AddSingleton(TimeProvider.System)` (dòng 65-67):

  ```csharp
          // Đồng hồ system — ManualRetryStore (if-absent factory phía trên) và service
          // time-sensitive resolve cùng 1 instance
  ```

  (Xóa trọn dòng 73 `AddSingleton<IModelHealthStore, ModelHealthStore>();` + dòng 75-78 2 dòng comment + 2 dòng watchdog.)

  10b. Gate lambda (dòng 91-92) — đổi param:

  ```csharp
            async (HttpContext ctx, IRequestQueue queue, IExecutionList executions,
                ChatCompletionsHandler handler, ILogService log, IManualRetryStore store) =>
  ```

  10c. Condition + comment + message (dòng 108-116):

  ```csharp
              // Gate manual-retry (spec manual-retry §3.3): model đang parked → 503 §4 TRƯỚC khi vào queue.
              // Combo name chưa resolve lúc này — gate cấp provider/account nằm ở walk (FilterRemaining)
              if (store.IsModelParked(prepared.ModelId))
              {
                  log.Write(new LogEntry
                  {
                      Severity = LogSeverity.Warning,
                      Category = LogCategory.Request,
                      Message = $"Từ chối request mới: model '{prepared.ModelId}' đang trong danh sách retry thủ công",
                      RequestId = id,
                      ClientKeyId = ClientKeyItems.IdOf(ctx),
                  });
  ```

  (Khối `WriteErrorAsync(503, $"The model '{prepared.ModelId}' is temporarily unavailable", ...)` giữ nguyên — contract lỗi không đổi. **V5:** 2 test chỉ assert substring `Từ chối request mới` + `'m1'` nên message mới an toàn.)

- [ ] **Step 11: Sửa 3 comment stale + xóa 3 file src + `scripts/e2e-3c.sh` (V8) + verify 0 ref**

  11a. Sửa comment còn lại mô tả store/watchdog đã xóa (code giữ lại nên doc phải đúng):

  - `src/RouterBalancing.Core/Engine/DispatchOutcome.cs:35` — param `RetryAfter` của `Retryable`:

    ```csharp
        /// <param name="RetryAfter"><c>Retry-After</c> đã parse — forward vào <c>Passthrough.RetryAfterHeader</c> khi exhaustion (§3.3).</param>
    ```

  - `src/RouterBalancing.Core/Engine/RetryAfterParser.cs:5-9` — XML doc class:

    ```csharp
    /// <summary>
    /// Parse header <c>Retry-After</c> (delta-seconds | HTTP-date), clamp 0..3600s —
    /// spec 3C §3.6. Giá trị đi vào <c>DispatchOutcome.Retryable</c>, exhaustion forward lại
    /// client qua <c>Passthrough</c>; request KHÔNG bao giờ chờ (đã chốt Phương án 1).
    /// </summary>
    ```

  - `src/RouterBalancing.Core/Server/ProxyApp.cs:217` — comment `/health`:

    ```csharp
            // /health mở luôn (middleware bỏ qua path này) — monitor/ping bên ngoài dùng
    ```

  11b. `git rm` + verify:

  ```bash
  git rm src/RouterBalancing.Core/Engine/IModelHealthStore.cs src/RouterBalancing.Core/Engine/ModelHealthStore.cs src/RouterBalancing.Core/Engine/ModelHealthWatchdog.cs scripts/e2e-3c.sh
  rg -n 'IModelHealthStore|ModelHealthStore|ModelHealthWatchdog|ManualRetryModel|GetManualRetryModels|ProbeDueAsync|IsManualRetry|e2e-3c|nextProbeAt' --glob '!docs/**' .
  ```

  Expected: 0 kết quả (ngoài file plan/spec trong `docs/` — đã loại glob).

- [ ] **Step 12: Gate 1 — build Core**

  Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental`
  Expected: 0 Warning / 0 Error.

- [ ] **Step 13: Chạy test filtered (GREEN từng phần)**

  Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~DispatcherLoopTests|FullyQualifiedName~ProxyRetryIntegrationTests|FullyQualifiedName~ExecutionListTests|FullyQualifiedName~ModelSelectorTests|FullyQualifiedName~RetryStateTests"`
  Expected: **55 pass** (19 + 9 + 17 + 7 + 3), 0 fail.

- [ ] **Step 14: Gate 2 — toàn bộ test**

  Run: `dotnet test "router balancing test/router balancing test.csproj"`
  Expected: **575 pass** (593 − 22 health tests − 2 loop facts + 2 loop mới + 2 integration + 2 execution), 0 failed (hoặc đúng 2 `SingleInstanceGuardTests`).

- [ ] **Step 15: Commit** (không đụng `router-balancing/` → bỏ gate 3 MAUI)

  ```bash
  git add src/RouterBalancing.Core/Engine/DispatcherLoop.cs src/RouterBalancing.Core/Engine/ExecutionList.cs src/RouterBalancing.Core/Engine/RetryState.cs src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/Engine/DispatcherLoopTests.cs" "router balancing test/Engine/ExecutionListTests.cs" "router balancing test/Engine/ModelSelectorTests.cs" "router balancing test/Server/ProxyRetryIntegrationTests.cs"
  git commit -m "refactor: drive walk, gate and capacity from manual retry store"
  ```

  (6 file `git rm` ở Step 6/11 đã staged — `git status` tổng cộng **14 path**; phantom CRLF KHÔNG add.)

---

## Task 6: Gỡ settings `maxRetry`/`watchdogIntervalSec` + thêm settings ping (G5)

Xóa circuit/watchdog khỏi bảng Settings (backend, validator, UI Engine, i18n) và thay bằng 2 setting ping mới: `pingIntervalSec` (chu kỳ `ProviderPingService` — Task 7) + `pingParkedProviders` (tự phục hồi qua ping). Đồng thời gắn field UI `providerProbeTimeoutSec` (backend đã làm ở Task 2) vào đúng đây — sửa `SettingsPanel` chỉ một lần (V1).

### Files

- Modify: `src/RouterBalancing.Core/Settings/SettingsKeys.cs`, `src/RouterBalancing.Core/Settings/IAppSettingsService.cs`, `src/RouterBalancing.Core/Settings/SettingsDraft.cs`, `src/RouterBalancing.Core/Settings/SettingsValidator.cs`, `src/RouterBalancing.Core/Settings/AppSettingsService.cs` (gỡ 2 + thêm 2 ở mỗi file), `src/RouterBalancing.Core/Localization/Translations.cs` (−4 key, +3 key, mỗi key có EN lẫn VI), `router-balancing/Components/Pages/SettingsPanel.razor` (Engine section + `EngineFields` + `LoadDraft` + `SaveEngine`).
- Test: sửa `router balancing test/Settings/SettingsValidatorTests.cs`, `router balancing test/Settings/AppSettingsServiceTests.cs` (không thêm/xóa fact → ladder +0).

### Interfaces

- Consumes (Task 2): `SettingsKeys.ProviderProbeTimeoutSec`, `IAppSettingsService.ProviderProbeTimeoutSec`, i18n `settings.field.probeTimeout`/`settings.error.probeTimeout` (Task 2 chưa gắn UI — Task 6 gắn).
- Produces (Task 7 dùng):
  - `SettingsKeys.PingIntervalSec` = `"pingIntervalSec"`, `SettingsKeys.PingParkedProviders` = `"pingParkedProviders"`.
  - `IAppSettingsService.PingIntervalSec` → `int`, default **60**; `IAppSettingsService.PingParkedProviders` → `bool`, default **false**.
  - Rule validator: `PingIntervalSec ∈ 10..86400` → lỗi `settings.error.pingInterval` (field-name `nameof(SettingsDraft.PingIntervalSec)`).
- Row mồ côi `maxRetry`/`watchdogIntervalSec` trong bảng `AppSettings` — vô hại, không migration (spec §3.6).

### Steps

- [ ] **Step 1: RED — sửa 2 file test**

  1a. `router balancing test/Settings/SettingsValidatorTests.cs` — `ValidDraft()` (dòng 7-16): xóa `MaxRetry = 3,` và `WatchdogIntervalSec = 60,`, thêm `PingIntervalSec = 60,` (file post-T2 đã có `ProviderProbeTimeoutSec = 60,` — giữ nguyên):

  ```csharp
      private static SettingsDraft ValidDraft() => new()
      {
          Language = "auto",
          Theme = "system",
          Port = 8317,
          PingIntervalSec = 60,
          ProviderProbeTimeoutSec = 60,
          LogRetentionDays = 90,
          StatsErrorRateThreshold = 10,
      };
  ```

  1b. Fact `Validate_WhenEngineValuesOutOfRange_ReturnsEachFieldError` — xóa `MaxRetry = 0,` + `WatchdogIntervalSec = 5,`, thêm `PingIntervalSec = 5,`; assert còn 4 error (post-T2 là 5 — xóa 2, thêm 1):

  ```csharp
      [Fact]
      public void Validate_WhenEngineValuesOutOfRange_ReturnsEachFieldError()
      {
          var errors = SettingsValidator.Validate(ValidDraft() with
          {
              PingIntervalSec = 5,
              ProviderProbeTimeoutSec = 0,
              LogRetentionDays = 0,
              StatsErrorRateThreshold = 101,
          });

          Assert.Equal(4, errors.Count);
          Assert.Equal("settings.error.pingInterval", errors[nameof(SettingsDraft.PingIntervalSec)]);
          Assert.Equal("settings.error.probeTimeout", errors[nameof(SettingsDraft.ProviderProbeTimeoutSec)]);
          Assert.Equal("settings.error.retention", errors[nameof(SettingsDraft.LogRetentionDays)]);
          Assert.Equal("settings.error.threshold", errors[nameof(SettingsDraft.StatsErrorRateThreshold)]);
      }
  ```

  1c. `router balancing test/Settings/AppSettingsServiceTests.cs` — `Get_MissingKey_ReturnsDefault`: thay `Assert.Equal(3, service.MaxRetry);` (post-T2 dòng kế tiếp là `Assert.Equal(60, service.ProviderProbeTimeoutSec);` — giữ) bằng 2 dòng:

  ```csharp
          Assert.Equal(60, service.PingIntervalSec);
          Assert.False(service.PingParkedProviders);
  ```

  Fact `Set_ThenGet_ReturnsValue` (dòng 32-40) — thay toàn bộ:

  ```csharp
      [Fact]
      public void Set_ThenGet_ReturnsValue()
      {
          using var service = Create();

          service.Set(SettingsKeys.PingIntervalSec, 30);

          Assert.Equal(30, service.PingIntervalSec);
      }
  ```

- [ ] **Step 2: Chạy test — kỳ vọng RED (compile error)**

  Run: `dotnet build "router balancing test/router balancing test.csproj" --no-incremental`
  Expected: FAIL — `CS0117: SettingsDraft/SettingsKeys` không có `PingIntervalSec`; `CS1061: IAppSettingsService` không có `PingIntervalSec`/`PingParkedProviders`.

- [ ] **Step 3: Core/Settings — 5 file (gỡ 2, thêm 2)**

  `SettingsKeys.cs` — thay block `MaxRetry` (kèm XML doc) + `WatchdogIntervalSec` (dòng 21-24; ngay sau là `ProviderProbeTimeoutSec` do T2 thêm) bằng:

  ```csharp
      /// <summary>Chu kỳ ping provider của BackgroundService — 10..86400 giây.</summary>
      public const string PingIntervalSec = "pingIntervalSec";

      /// <summary>Ping cả provider đang parked để tự phục hồi — mặc định false.</summary>
      public const string PingParkedProviders = "pingParkedProviders";
  ```

  `IAppSettingsService.cs` — thay 2 property `MaxRetry`/`WatchdogIntervalSec` (dòng 23-25) bằng:

  ```csharp
      /// <summary>Chu kỳ ping provider (BackgroundService) — đọc mỗi tick, đổi setting có hiệu lực ngay.</summary>
      int PingIntervalSec { get; }

      /// <summary>Ping cả provider đang parked để tự phục hồi — mặc định false.</summary>
      bool PingParkedProviders { get; }
  ```

  `SettingsDraft.cs` — thay 2 property (dòng 22-24) bằng:

  ```csharp
      public int PingIntervalSec { get; set; } = 60;

      public bool PingParkedProviders { get; set; }
  ```

  `SettingsValidator.cs` — thay 2 rule (dòng 24-28; rule `ProviderProbeTimeoutSec` do T2 thêm nằm ngay sau — giữ nguyên) bằng:

  ```csharp
          if (draft.PingIntervalSec is < 10 or > 86400)
              errors[nameof(SettingsDraft.PingIntervalSec)] = "settings.error.pingInterval";
  ```

  `AppSettingsService.cs` — thay 2 getter (dòng 47-49) bằng:

  ```csharp
      public int PingIntervalSec => Get(SettingsKeys.PingIntervalSec, 60);

      public bool PingParkedProviders => Get(SettingsKeys.PingParkedProviders, false);
  ```

  Cùng file — sửa comment dòng 14 (watchdog đã xóa): `// Watchdog/ProxyHost đọc cache song song với thread gọi Set` → `// ProviderPingService/ProxyHost đọc cache song song với thread gọi Set`.

- [ ] **Step 4: `SettingsPanel.razor` — Engine section + 3 đoạn code**

  4a. Thay toàn bộ `<section>` Engine (dòng 164-190) — 2 input + 2 error block + checkbox, theo đúng pattern section Server (input grid → error → checkbox `mt-3` → nút Save):

  ```razor
  <section class="mb-4 rounded border border-border bg-surface p-4">
      <h2 class="mb-3 text-base font-semibold">@L["settings.group.engine"]</h2>

      <div class="grid gap-3 sm:grid-cols-2">
          <label class="flex flex-col gap-1 text-sm">
              @L["settings.field.pingInterval"]
              <input class="rounded border border-border bg-surface px-2 py-1.5"
                     type="number" @bind="_draft.PingIntervalSec" />
          </label>
          <label class="flex flex-col gap-1 text-sm">
              @L["settings.field.probeTimeout"]
              <input class="rounded border border-border bg-surface px-2 py-1.5"
                     type="number" @bind="_draft.ProviderProbeTimeoutSec" />
          </label>
      </div>

      @if (Error(nameof(SettingsDraft.PingIntervalSec)) is { } pingIntervalError)
      {
          <div class="mt-1 text-sm text-danger">@L[pingIntervalError]</div>
      }
      @if (Error(nameof(SettingsDraft.ProviderProbeTimeoutSec)) is { } probeTimeoutError)
      {
          <div class="mt-1 text-sm text-danger">@L[probeTimeoutError]</div>
      }

      <label class="mt-3 flex items-center gap-2 text-sm">
          <input type="checkbox" @bind="_draft.PingParkedProviders" />
          @L["settings.field.pingParked"]
      </label>

      <button class="btn btn-primary mt-3" @onclick="SaveEngine">@L["settings.action.save"]</button>
  </section>
  ```

  4b. `EngineFields` (dòng 279-283):

  ```csharp
      private static readonly string[] EngineFields =
      [
          nameof(SettingsDraft.PingIntervalSec),
          nameof(SettingsDraft.ProviderProbeTimeoutSec),
      ];
  ```

  (`PingParkedProviders` là bool, validator không có rule → không cần trong nhóm validate — `ValidateFor` chỉ lọc lỗi của nhóm.)

  4c. `LoadDraft()` (dòng 311-323) — thay 2 dòng `MaxRetry = Settings.MaxRetry,` + `WatchdogIntervalSec = Settings.WatchdogIntervalSec,` bằng 3 dòng (Task 2 chưa đụng method này):

  ```csharp
          PingIntervalSec = Settings.PingIntervalSec,
          PingParkedProviders = Settings.PingParkedProviders,
          ProviderProbeTimeoutSec = Settings.ProviderProbeTimeoutSec,
  ```

  4d. `SaveEngine()` (dòng 411-419) — 3 `Set`:

  ```csharp
      private void SaveEngine()
      {
          if (!ValidateFor(EngineFields)) return;

          Settings.Set(SettingsKeys.PingIntervalSec, _draft.PingIntervalSec);
          Settings.Set(SettingsKeys.PingParkedProviders, _draft.PingParkedProviders);
          Settings.Set(SettingsKeys.ProviderProbeTimeoutSec, _draft.ProviderProbeTimeoutSec);
          // Engine đọc setting mỗi request qua SettingsChanged — giá trị mới áp dụng ngay
          Toast.Show(L["settings.msg.saved"], ToastSeverity.Success);
      }
  ```

- [ ] **Step 5: Translations — xóa 4 key, thêm 3 key (2 dict, key-set EN = VI giữ nguyên)**

  `src/RouterBalancing.Core/Localization/Translations.cs`:

  - EN zone `settings.field.*` — thay 2 dòng `["settings.field.maxRetry"] = "Max retries",` + `["settings.field.watchdog"] = "Watchdog interval (s)",` (dòng 51-52; post-T2 dòng kế tiếp là `settings.field.probeTimeout` — giữ) bằng:

    ```csharp
            ["settings.field.pingInterval"] = "Ping interval (s)",
            ["settings.field.pingParked"] = "Auto-retry parked providers via ping",
    ```

  - EN zone `settings.error.*` — thay 2 dòng `settings.error.maxRetry` + `settings.error.watchdog` (post-T2 nằm ngay trước `settings.error.probeTimeout`) bằng:

    ```csharp
            ["settings.error.pingInterval"] = "Ping interval must be between 10 and 86400 seconds.",
    ```

  - VI zone `settings.field.*` — thay 2 dòng `["settings.field.maxRetry"] = "Số lần thử lại tối đa",` + `["settings.field.watchdog"] = "Chu kỳ watchdog (giây)",` (dòng 371-372) bằng:

    ```csharp
            ["settings.field.pingInterval"] = "Chu kỳ ping (giây)",
            ["settings.field.pingParked"] = "Tự retry provider đang chờ qua ping",
    ```

  - VI zone `settings.error.*` — thay 2 dòng `settings.error.maxRetry` + `settings.error.watchdog` (dòng 383-384) bằng:

    ```csharp
            ["settings.error.pingInterval"] = "Chu kỳ ping phải trong khoảng 10–86400 giây.",
    ```

- [ ] **Step 6: Verify 0 ref còn lại**

  ```bash
  rg -n 'MaxRetry|maxRetry|WatchdogIntervalSec|watchdogIntervalSec|settings\.field\.maxRetry|settings\.error\.maxRetry|settings\.field\.watchdog|settings\.error\.watchdog' src/ router-balancing/ "router balancing test/"
  ```

  Expected: 0 kết quả (ProxyRetryIntegration hết ref từ Task 5; health/watchdog tests đã xóa từ Task 5; `docs/` không nằm trong 3 thư mục trên).

- [ ] **Step 7: Gate 1 — build Core**

  Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental`
  Expected: 0 Warning / 0 Error.

- [ ] **Step 8: Chạy test filtered (GREEN)**

  Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~SettingsValidatorTests|FullyQualifiedName~AppSettingsServiceTests"`
  Expected: **10 pass** (5 + 5), 0 fail.

- [ ] **Step 9: Gate 2 — toàn bộ test**

  Run: `dotnet test "router balancing test/router balancing test.csproj"`
  Expected: **575 pass** (+0 — chỉ sửa fact có sẵn), 0 failed (hoặc đúng 2 `SingleInstanceGuardTests`).

- [ ] **Step 10: Gate 3 — build MAUI (đụng `SettingsPanel.razor`)**

  Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`
  Expected: 0 Warning / 0 Error. (Đóng app MAUI trước nếu đang chạy — tránh file-lock.)

- [ ] **Step 11: Commit**

  ```bash
  git add src/RouterBalancing.Core/Settings/SettingsKeys.cs src/RouterBalancing.Core/Settings/IAppSettingsService.cs src/RouterBalancing.Core/Settings/SettingsDraft.cs src/RouterBalancing.Core/Settings/SettingsValidator.cs src/RouterBalancing.Core/Settings/AppSettingsService.cs src/RouterBalancing.Core/Localization/Translations.cs router-balancing/Components/Pages/SettingsPanel.razor "router balancing test/Settings/SettingsValidatorTests.cs" "router balancing test/Settings/AppSettingsServiceTests.cs"
  git commit -m "feat: replace max retry settings with ping settings"
  ```

  (9 file — kiểm tra `git status` trước khi add; phantom CRLF KHÔNG add.)

---

## Task 7: `ProviderPingService` — ping định kỳ + DI proxy container (G5)

`BackgroundService` ping `GET /v1/models` mọi provider enabled mỗi `pingIntervalSec`: 401/403 → park `Unauthorized`, 404 → park `NotFound`, mạng/timeout → park `Unreachable`, 429/5xx → bỏ qua; 2xx + provider đang park + `pingParkedProviders=true` → `Unpark` + log Info. Đăng ký singleton + hosted qua factory ở `ProxyApp` (thay registrations watchdog đã xóa ở Task 5) + đăng ký client `provider-probe` cho container proxy (V6).

### Files

- Create: `src/RouterBalancing.Core/Providers/ProviderPingService.cs`, `router balancing test/Providers/ProviderPingServiceTests.cs` (13 test = 11 fact + theory 401/403).
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs` (using `Providers` + client `provider-probe` + DI ping; chiếm chỗ registrations watchdog bị xóa Task 5).

### Interfaces

- Consumes: `IManualRetryStore` (`Park`/`Unpark`/`IsProviderParked`/`IsAccountParked` — Task 3), `SettingsKeys.PingIntervalSec`/`PingParkedProviders` (Task 6), `ProviderRequestFactory.Create` + `HttpClientName` (`provider-probe` đã có `ProviderProbeTimeoutHandler` — Task 2), `ProxyTarget.Current` (pattern `TestConnectionAsync`), `ManualRetryLevel`/`ManualRetryReason`.
- Produces (Task 8 dùng): service đăng ký `AddSingleton<ProviderPingService>()` + `AddHostedService(sp => sp.GetRequiredService<ProviderPingService>())` trong proxy container.
- **V7 (deviation):** `ExecuteAsync` delay-first `Task.Delay(settings.PingIntervalSec, stoppingToken)` + method public `PingAllAsync(ct)` để test gọi trực tiếp — **không inject `TimeProvider`** (spec §3.5 ghi TimeProvider; delay-first + public method đã đủ test được, tránh thêm ctor param). Delay-first cũng khiến hosted service trong integration test (TestServer) không ping thật — test kết thúc trước tick đầu 60s.
- **Log (spec §5):** Info `Provider '{tên}' phục hồi qua ping — tự gỡ khỏi danh sách` (chỉ khi unpark); Warn `Ping provider '{tên}' thất bại ({reason}) — đưa vào danh sách retry thủ công` — `{reason}` = `HTTP 401`/`HTTP 404`/`lỗi mạng`; Warn transition của store (`Đưa provider '{id}' vào ...`) do `ManualRetryStore` tự ghi. Ping Warn **chỉ ghi khi state đổi** (`wasParked == false`) — đã parked thì store (cùng lý do → im lặng) và ping cùng im lặng → không lặp mỗi tick. `{tên}` = `provider.Name` (ping load entity đầy đủ; store chỉ có Id).
- **Key resolution:** `Accounts.FirstOrDefault(a => a.Enabled && !store.IsAccountParked(a.Id))` — không TK hợp lệ → bỏ qua provider trong lượt này; TK có但 `ApiKeyEncrypted` rỗng → key `""` → không header auth (D7).

### Steps

- [ ] **Step 1: RED — tạo test (11 fact + theory 401/403 = 13)**

  Tạo `router balancing test/Providers/ProviderPingServiceTests.cs`:

  ```csharp
  using System.Net;
  using Microsoft.EntityFrameworkCore;
  using RouterBalancing.Core.Domain;
  using RouterBalancing.Core.Engine;
  using RouterBalancing.Core.Logging;
  using RouterBalancing.Core.Providers;
  using RouterBalancing.Core.Proxies;
  using RouterBalancing.Core.Security;
  using RouterBalancing.Core.Settings;
  using RouterBalancing.Core.Storage;

  namespace router_balancing_test.Providers;

  public class ProviderPingServiceTests : IDisposable
  {
      private readonly TestDb _testDb = new();
      private readonly IDbContextFactory<RouterBalancingDbContext> _db;
      private readonly ISecretProtector _protector = new DpapiSecretProtector();
      private readonly CapturingLog _log = new();
      private readonly ManualRetryStore _store;
      private readonly AppSettingsService _settings;

      public ProviderPingServiceTests()
      {
          _db = _testDb.CreateFactory();
          DbInitializer.Initialize(_db);
          _store = new ManualRetryStore(_log, TimeProvider.System);
          _settings = new AppSettingsService(_db);
      }

      public void Dispose()
      {
          _settings.Dispose();
          _testDb.Dispose();
      }

      /// <summary>Response scriptable; ghi URL + auth header để assert; ném được (mạng/timeout).</summary>
      private sealed class ProbeHandler : HttpMessageHandler
      {
          public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
          public Exception? Throw { get; set; }
          public List<string> SentUrls { get; } = [];
          public List<string?> SentAuthHeaders { get; } = [];

          protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
          {
              SentUrls.Add(request.RequestUri!.ToString());
              SentAuthHeaders.Add(request.Headers.Authorization?.ToString());
              if (Throw is { } ex) throw ex;
              return Task.FromResult(new HttpResponseMessage(Status)
              {
                  Content = new StringContent("{}"),
              });
          }
      }

      private sealed class StubFactory(ProbeHandler handler) : IHttpClientFactory
      {
          public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
      }

      private sealed class CapturingLog : ILogService
      {
          public List<string> Infos { get; } = [];
          public List<string> Warns { get; } = [];
          public List<string> Errors { get; } = [];

          public void Info(string message, LogCategory category = LogCategory.App) => Infos.Add(message);
          public void Warn(string message, LogCategory category = LogCategory.App) => Warns.Add(message);
          public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
              Errors.Add(message);
      }

      private long SeedProvider(string name, ProviderType type = ProviderType.OpenAI,
          bool enabled = true, string apiKey = "sk-test")
      {
          using var db = _db.CreateDbContext();
          var provider = new Provider
          {
              Name = name,
              Type = type,
              BaseUrl = "https://api.example.com",
              MaxConcurrent = 4,
              Enabled = enabled,
              Accounts =
              [
                  new ProviderAccount
                  {
                      Name = $"{name}-a",
                      Enabled = true,
                      ApiKeyEncrypted = apiKey.Length > 0 ? _protector.Protect(apiKey) : string.Empty,
                  },
              ],
          };
          db.Providers.Add(provider);
          db.SaveChanges();
          return provider.Id;
      }

      private ProviderPingService CreateService(ProbeHandler handler) =>
          new(_db, new StubFactory(handler), _protector, _settings, _store, _log);

      [Fact]
      public async Task PingAllAsync_WhenUpstream2xx_KeepsStateQuietAndSendsGetModels()
      {
          var handler = new ProbeHandler();
          var id = SeedProvider("p1");
          var service = CreateService(handler);

          await service.PingAllAsync(CancellationToken.None);

          Assert.False(_store.IsProviderParked(id));
          Assert.EndsWith("/v1/models", Assert.Single(handler.SentUrls));
          Assert.Equal("Bearer sk-test", Assert.Single(handler.SentAuthHeaders));
          Assert.Empty(_log.Infos);
          Assert.Empty(_log.Warns);
          Assert.Empty(_log.Errors);
          Assert.Null(ProxyTarget.Current.Value); // reset sau call (finally)
      }

      [Theory]
      [InlineData(401)]
      [InlineData(403)]
      public async Task PingAllAsync_WhenUpstreamUnauthorized_ParksProvider(int status)
      {
          var handler = new ProbeHandler { Status = (HttpStatusCode)status };
          var id = SeedProvider("p1");
          var service = CreateService(handler);

          await service.PingAllAsync(CancellationToken.None);

          Assert.True(_store.IsProviderParked(id));
          var entry = Assert.Single(_store.GetEntries());
          Assert.Equal(ManualRetryLevel.Provider, entry.Level);
          Assert.Equal(ManualRetryReason.Unauthorized, entry.Reason);
          // 2 Warn: store (transition) + ping service (spec §5) — assert câu của ping
          Assert.Contains(_log.Warns,
              w => w.Contains($"Ping provider 'p1' thất bại (HTTP {status}) — đưa vào danh sách retry thủ công"));
      }

      [Fact]
      public async Task PingAllAsync_WhenUpstream404_ParksProviderWithNotFoundReason()
      {
          var handler = new ProbeHandler { Status = HttpStatusCode.NotFound };
          var id = SeedProvider("p1");
          var service = CreateService(handler);

          await service.PingAllAsync(CancellationToken.None);

          Assert.True(_store.IsProviderParked(id));
          Assert.Equal(ManualRetryReason.NotFound, Assert.Single(_store.GetEntries()).Reason);
          Assert.Contains(_log.Warns, w => w.Contains("Ping provider 'p1' thất bại (HTTP 404)"));
      }

      [Fact]
      public async Task PingAllAsync_WhenNetworkError_ParksProviderWithUnreachableReason()
      {
          var handler = new ProbeHandler { Throw = new HttpRequestException("No such host is known.") };
          var id = SeedProvider("p1");
          var service = CreateService(handler);

          await service.PingAllAsync(CancellationToken.None);

          Assert.True(_store.IsProviderParked(id));
          Assert.Equal(ManualRetryReason.Unreachable, Assert.Single(_store.GetEntries()).Reason);
          Assert.Contains(_log.Warns, w => w.Contains("Ping provider 'p1' thất bại (lỗi mạng)"));
      }

      [Fact]
      public async Task PingAllAsync_WhenTimeout_ParksProviderWithUnreachableReason()
      {
          // TaskCanceledException = timeout từ ProviderProbeTimeoutHandler — không phải host stop
          // (ct = None → catch thứ nhất có filter false, rơi vào catch TCE)
          var handler = new ProbeHandler { Throw = new TaskCanceledException("Provider probe exceeded 60s timeout.") };
          var id = SeedProvider("p1");
          var service = CreateService(handler);

          await service.PingAllAsync(CancellationToken.None);

          Assert.True(_store.IsProviderParked(id));
          Assert.Equal(ManualRetryReason.Unreachable, Assert.Single(_store.GetEntries()).Reason);
          Assert.Contains(_log.Warns, w => w.Contains("lỗi mạng"));
      }

      [Fact]
      public async Task PingAllAsync_WhenUpstream429_DoesNotPark()
      {
          var handler = new ProbeHandler { Status = HttpStatusCode.TooManyRequests };
          var id = SeedProvider("p1");
          var service = CreateService(handler);

          await service.PingAllAsync(CancellationToken.None);

          Assert.False(_store.IsProviderParked(id));
          Assert.Single(handler.SentUrls); // vẫn ping — chỉ không park
          Assert.Empty(_log.Warns);
      }

      [Fact]
      public async Task PingAllAsync_WhenUpstream500_DoesNotPark()
      {
          var handler = new ProbeHandler { Status = HttpStatusCode.InternalServerError };
          var id = SeedProvider("p1");
          var service = CreateService(handler);

          await service.PingAllAsync(CancellationToken.None);

          Assert.False(_store.IsProviderParked(id));
          Assert.Empty(_log.Warns);
      }

      [Fact]
      public async Task PingAllAsync_WhenProviderParkedAndAutoRetryOff_SkipsPing()
      {
          var handler = new ProbeHandler();
          var id = SeedProvider("p1");
          _store.Park(ManualRetryLevel.Provider, id, "", ManualRetryReason.Unauthorized);
          _log.Warns.Clear(); // chỉ quan tâm log phát sinh MỚI khi ping
          var service = CreateService(handler);

          await service.PingAllAsync(CancellationToken.None); // PingParkedProviders mặc định false

          Assert.Empty(handler.SentUrls);
          Assert.True(_store.IsProviderParked(id));
          Assert.Empty(_log.Warns);
      }

      [Fact]
      public async Task PingAllAsync_WhenProviderParkedAndAutoRetryOn2xx_UnparksAndLogsInfo()
      {
          var handler = new ProbeHandler();
          var id = SeedProvider("p1");
          _settings.Set(SettingsKeys.PingParkedProviders, true);
          _store.Park(ManualRetryLevel.Provider, id, "", ManualRetryReason.Unauthorized);
          _log.Warns.Clear();
          var service = CreateService(handler);

          await service.PingAllAsync(CancellationToken.None);

          Assert.False(_store.IsProviderParked(id));
          Assert.Contains(_log.Infos,
              i => i.Contains("Provider 'p1' phục hồi qua ping — tự gỡ khỏi danh sách"));
          Assert.Empty(_log.Warns);
      }

      [Fact]
      public async Task PingAllAsync_WhenProviderParkedAndAutoRetryOnStillFailing_KeepsParkWithoutDuplicateLog()
      {
          var handler = new ProbeHandler { Status = HttpStatusCode.Unauthorized };
          var id = SeedProvider("p1");
          _settings.Set(SettingsKeys.PingParkedProviders, true);
          _store.Park(ManualRetryLevel.Provider, id, "", ManualRetryReason.Unauthorized);
          _log.Warns.Clear();
          var service = CreateService(handler);

          await service.PingAllAsync(CancellationToken.None);

          Assert.True(_store.IsProviderParked(id)); // giữ nguyên — không đổi trạng thái
          Assert.Single(handler.SentUrls);          // vẫn ping (flag bật)
          Assert.Empty(_log.Warns);                 // không lặp log mỗi tick
      }

      [Fact]
      public async Task PingAllAsync_WhenProviderAnthropic_SkipsPing()
      {
          var handler = new ProbeHandler();
          var id = SeedProvider("p1", ProviderType.Anthropic);
          var service = CreateService(handler);

          await service.PingAllAsync(CancellationToken.None);

          Assert.Empty(handler.SentUrls);
          Assert.False(_store.IsProviderParked(id));
          Assert.Empty(_log.Warns);
      }

      [Fact]
      public async Task PingAllAsync_WhenNoEnabledUnparkedAccount_SkipsProvider()
      {
          var handler = new ProbeHandler();
          var id = SeedProvider("p1");
          using (var db = _db.CreateDbContext())
          {
              var account = db.ProviderAccounts.Single(a => a.ProviderId == id);
              account.Enabled = false;
              db.SaveChanges();
          }
          var service = CreateService(handler);

          await service.PingAllAsync(CancellationToken.None);

          Assert.Empty(handler.SentUrls); // không còn TK hợp lệ → bỏ qua lượt ping
          Assert.False(_store.IsProviderParked(id));
          Assert.Empty(_log.Warns);
      }
  }
  ```

  (Case TK no-key `ApiKeyEncrypted=""` → không header auth đã phủ qua `ProviderTestConnectionTests` ở factory; ping dùng cùng `ProviderRequestFactory.Create` — không thêm fact để giữ đúng 13. Provider preset trong `DbInitializer` đều `Enabled=false` → không lọt vào lượt ping.)

- [ ] **Step 2: Chạy test — kỳ vọng RED (compile error)**

  Run: `dotnet build "router balancing test/router balancing test.csproj" --no-incremental`
  Expected: FAIL — `CS0246: ProviderPingService` không tồn tại.

- [ ] **Step 3: Tạo `ProviderPingService`**

  Tạo `src/RouterBalancing.Core/Providers/ProviderPingService.cs`:

  ```csharp
  using Microsoft.EntityFrameworkCore;
  using Microsoft.Extensions.Hosting;
  using RouterBalancing.Core.Domain;
  using RouterBalancing.Core.Engine;
  using RouterBalancing.Core.Logging;
  using RouterBalancing.Core.Proxies;
  using RouterBalancing.Core.Security;
  using RouterBalancing.Core.Settings;
  using RouterBalancing.Core.Storage;

  namespace RouterBalancing.Core.Providers;

  /// <summary>
  /// Ping định kỳ <c>GET /v1/models</c> mọi provider enabled (spec manual-retry §3.5):
  /// 401/403 → park <c>Unauthorized</c>, 404 → <c>NotFound</c>, mạng/timeout → <c>Unreachable</c>,
  /// 429/5xx bỏ qua; 2xx + đang park + <c>pingParkedProviders</c> → unpark. Hosted service;
  /// unit test gọi trực tiếp <see cref="PingAllAsync"/> (V7: delay-first, không fake timer).
  /// </summary>
  public sealed class ProviderPingService(
      IDbContextFactory<RouterBalancingDbContext> db,
      IHttpClientFactory http,
      ISecretProtector protector,
      IAppSettingsService settings,
      IManualRetryStore store,
      ILogService log) : BackgroundService
  {
      /// <summary>
      /// Vòng đời hosted: delay TRƯỚC tick đầu (app vừa lên không ping ngay — tránh kẹp
      /// startup) rồi ping 1 lượt; interval đọc per-call — đổi setting có hiệu lực ngay.
      /// Host stop → thoát im lặng.
      /// </summary>
      protected override async Task ExecuteAsync(CancellationToken stoppingToken)
      {
          while (!stoppingToken.IsCancellationRequested)
          {
              try
              {
                  await Task.Delay(TimeSpan.FromSeconds(settings.PingIntervalSec), stoppingToken);
                  await PingAllAsync(stoppingToken);
              }
              catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
              {
                  break; // host stop — không phải lỗi
              }
          }
      }

      /// <summary>
      /// Ping 1 lượt mọi provider enabled — public để unit test gọi trực tiếp, không chờ tick.
      /// </summary>
      /// <param name="ct">Token hủy theo host stop.</param>
      public async Task PingAllAsync(CancellationToken ct)
      {
          await using var context = await db.CreateDbContextAsync(ct);
          var providers = await context.Providers.AsNoTracking()
              .Where(p => p.Enabled)
              .Include(p => p.Accounts)
              .OrderBy(p => p.Id)
              .ToListAsync(ct);

          foreach (var provider in providers)
          {
              if (provider.Type == ProviderType.Anthropic)
                  continue; // app chưa serve Anthropic — ping chỉ tạo noise (§3.5)

              var wasParked = store.IsProviderParked(provider.Id);
              if (wasParked && !settings.PingParkedProviders)
                  continue; // mặc định không ping provider đang parked (§3.5)

              // TK enabled đầu tiên chưa park — tránh dùng key của TK đã bị park rồi oan
              // park provider; không còn TK hợp lệ → bỏ qua provider trong lượt này
              var account = provider.Accounts
                  .FirstOrDefault(a => a.Enabled && !store.IsAccountParked(a.Id));
              if (account is null)
                  continue;

              await PingOneAsync(provider, account, wasParked, ct);
          }
      }

      private async Task PingOneAsync(Provider provider, ProviderAccount account,
          bool wasParked, CancellationToken ct)
      {
          try
          {
              var key = account.ApiKeyEncrypted.Length > 0
                  ? protector.Unprotect(account.ApiKeyEncrypted)
                  : string.Empty; // TK no-key → không header auth (D7)

              // Ping đi đúng proxy của provider (giống TestConnectionAsync, D4)
              ProxyTarget.Current.Value = new ProxyTarget(provider, null);
              try
              {
                  using var request = ProviderRequestFactory.Create(provider, key);
                  using var response = await http
                      .CreateClient(ProviderRequestFactory.HttpClientName)
                      .SendAsync(request, ct);

                  if (response.IsSuccessStatusCode)
                  {
                      if (wasParked)
                      {
                          store.Unpark(ManualRetryLevel.Provider, provider.Id, "");
                          SafeLog(() => log.Info(
                              $"Provider '{provider.Name}' phục hồi qua ping — tự gỡ khỏi danh sách",
                              LogCategory.App));
                      }
                      return;
                  }

                  var code = (int)response.StatusCode;
                  ManualRetryReason? reason = code switch
                  {
                      401 or 403 => ManualRetryReason.Unauthorized,
                      404 => ManualRetryReason.NotFound,
                      _ => null,
                  };
                  if (reason is { } r)
                      ParkAndWarn(provider, wasParked, r, $"HTTP {code}");
                  // 429/5xx/4xx khác — lỗi tạm thời, failover/request lo (§3.5): bỏ qua
              }
              finally
              {
                  ProxyTarget.Current.Value = null;
              }
          }
          catch (OperationCanceledException) when (ct.IsCancellationRequested)
          {
              throw; // host stop giữa ping — không phải lỗi
          }
          catch (HttpRequestException)
          {
              ParkAndWarn(provider, wasParked, ManualRetryReason.Unreachable, "lỗi mạng");
          }
          catch (TaskCanceledException)
          {
              // Timeout per-request của ProviderProbeTimeoutHandler (§3.8) → coi như unreachable
              ParkAndWarn(provider, wasParked, ManualRetryReason.Unreachable, "lỗi mạng");
          }
          catch (Exception ex)
          {
              // Bug/DPAPI hỏng/EF — KHÔNG park để tránh park oan; log Error best-effort (I2)
              SafeLog(() => log.Error(
                  $"Lỗi ping provider '{provider.Name}': {ex.Message}", ex, LogCategory.App));
          }
      }

      private void ParkAndWarn(Provider provider, bool wasParked,
          ManualRetryReason reason, string reasonText)
      {
          try
          {
              // Store tự ghi Warn transition đúng 1 lần (đã SafeLog trong store)
              store.Park(ManualRetryLevel.Provider, provider.Id, "", reason);
          }
          catch
          {
              // Nuốt chủ đích: store lỗi không được phá vòng ping
          }

          // Chỉ log khi state đổi — đã parked thì store (cùng lý do) và ping cùng im lặng
          if (wasParked) return;
          SafeLog(() => log.Warn(
              $"Ping provider '{provider.Name}' thất bại ({reasonText}) — đưa vào danh sách retry thủ công",
              LogCategory.App));
      }

      /// <summary>Log ném (SQLite sập...) không được phá vòng ping — pattern SafeLog của store (I2).</summary>
      private static void SafeLog(Action action)
      {
          try
          {
              action();
          }
          catch
          {
              // Nuốt chủ đích
          }
      }
  }
  ```

- [ ] **Step 4: `ProxyApp.cs` — using + client `provider-probe` + DI ping (V6)**

  4a. Thêm using (vị trí alphabet, sau `RouterBalancing.Core.Logging`):

  ```csharp
  using RouterBalancing.Core.Providers;
  ```

  4b. Sau block `AddHttpClient(OpenAiUpstreamClient.HttpClientName, ...)` (kết thúc dòng `.AddHttpMessageHandler<ProxyHealthHandler>();` lần đầu — trước comment `builder.Services.AddSingleton<IRequestQueue, RequestQueue>();`) thêm:

  ```csharp
          // provider-probe (spec manual-retry §3.5, V6): client cho ProviderPingService
          // trong container proxy — connect 60s (G6), per-request timeout qua
          // ProviderProbeTimeoutHandler (đổi setting có hiệu lực ngay), proxy pool y hệt MauiProgram
          builder.Services.AddHttpClient(ProviderRequestFactory.HttpClientName,
              client => client.Timeout = Timeout.InfiniteTimeSpan)
              .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
              {
                  Proxy = new RoundRobinWebProxy(),
                  UseProxy = true,
                  ConnectTimeout = TimeSpan.FromSeconds(60),
              })
              .AddHttpMessageHandler<ProxyHealthHandler>()
              .AddHttpMessageHandler<ProviderProbeTimeoutHandler>();
  ```

  4c. Ngay sau `builder.Services.AddHostedService<DispatcherLoop>();` (thay registrations watchdog đã xóa ở Task 5):

  ```csharp
          // Ping định kỳ provider (spec manual-retry §3.5): singleton + hosted qua factory
          // lấy ĐÚNG instance — integration test resolve được rồi gọi PingAllAsync trực tiếp
          builder.Services.AddSingleton<ProviderPingService>();
          builder.Services.AddHostedService(sp => sp.GetRequiredService<ProviderPingService>());
  ```

- [ ] **Step 5: Gate 1 — build Core**

  Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental`
  Expected: 0 Warning / 0 Error.

- [ ] **Step 6: Chạy test filtered (GREEN)**

  Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProviderPingServiceTests"`
  Expected: **13 pass** (11 fact + 2 InlineData), 0 fail.

- [ ] **Step 7: Gate 2 — toàn bộ test**

  Run: `dotnet test "router balancing test/router balancing test.csproj"`
  Expected: **588 pass** (575 + 13), 0 failed (hoặc đúng 2 `SingleInstanceGuardTests`).

- [ ] **Step 8: Commit** (chỉ src Core + test → bỏ gate 3 MAUI)

  ```bash
  git add src/RouterBalancing.Core/Providers/ProviderPingService.cs src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/Providers/ProviderPingServiceTests.cs"
  git commit -m "feat: add periodic provider ping with optional auto-recovery"
  ```

  (3 file — kiểm tra `git status`; phantom CRLF KHÔNG add.)

---

## Task 8: Dashboard card "Manual retry list" (G5, spec §3.2/§6.3)

Card danh sách provider/model đang park trên `Dashboard`, nút **Retry now** unpark + toast + log. Backend (`IManualRetryStore` + `ManualRetryEntry`) đã có từ Task 3-4; Task 8 chỉ UI.

### Files

- Modify: `router-balancing/Components/Pages/Dashboard.razor` (inject 2 service, card, handler, i18n keys), `src/RouterBalancing.Core/Localization/Translations.cs` (+10 key × 2 dict).
- KHÔNG sửa: `router-balancing/Components/_Imports.razor` (giữ 15 dòng — 2 using cần thêm đặt trực tiếp trong Dashboard, post-line 2).

### Interfaces

- Consumes: `IManualRetryStore` (`GetEntries()` → `IReadOnlyList<ManualRetryEntry>`; `Changed` event; `Unpark(level, id, modelId)`), `IProviderService.ListAsync()`, `IToastService.Show(message, ToastSeverity)`, `ILogService.Info`, `IRuntimeLocalizer` (`L["..."]`).
- Produces: 10 i18n key (Insert sau `common.copied` (EN line ~30) / sau `settings.error.pingInterval` block (VI)): `manualRetry.title`, `manualRetry.action.retry`, `manualRetry.level.provider/account/model`, `manualRetry.reason.unauthorized/notFound/modelNotFound/unreachable`, `manualRetry.msg.unparked` — giá trị EN/VI theo bảng §3.6 spec (khớp exact: `Retry now`, `Provider`, `Account`, `Model`, `Unauthorized`, `Not found`, `Model not found`, `Unreachable`, `{key} resumed to rotation` / `{key} đã trở lại vòng xoay`).
- **Xử lý event thread-safety:** store `Changed` bắn từ background thread → luôn `await InvokeAsync(ReloadEntriesAsync)`; `Proxy.StateChanged` (signalr) → `_ = InvokeAsync(...)`.

### Steps

- [ ] **Step 1: RED — test i18n parity sẽ fail vì thiếu key (optional pre-check)**

  Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~TranslationParityTests"`
  Expected: PASS hiện tại (chưa thêm key) — làm baseline để Step 5 assert lại. (Không RED bắt buộc: T8 là UI, test chứng minh = parity + gate build.)

- [ ] **Step 2: `Dashboard.razor` — usings + inject + state**

  2a. Sau dòng `@using Microsoft.AspNetCore.SignalR` (hoặc dòng `@using` cuối, trước `@inject`) thêm:

  ```razor
  @using RouterBalancing.Core.Engine
  @using RouterBalancing.Core.Providers
  ```

  2b. Thêm inject (sau inject cuối cùng):

  ```razor
  @inject IManualRetryStore RetryStore
  @inject IProviderService ProviderService
  ```

  2c. Trong `@code`, thêm field + lifecycle (bổ sung cho `OnInitializedAsync`/`Dispose` hiện có — nếu đã có method thì merge vào body):

  ```csharp
  private IReadOnlyList<ManualRetryEntry> _retryEntries = [];
  private Dictionary<long, string> _providerNames = [];

  private async Task ReloadEntriesAsync()
  {
      var providers = await ProviderService.ListAsync();
      _providerNames = providers
          .SelectMany(p => p.Accounts.Select(a => (p.Id, p.Name)))
          .ToDictionary(x => x.Id, x => x.Name);
      _retryEntries = RetryStore.GetEntries(); // gán CUỐI — tránh render dở dang
  }

  private async Task HandleRetryStoreChangedAsync() =>
      await InvokeAsync(async () => { await ReloadEntriesAsync(); StateHasChanged(); });

  private string GetEntryDisplayName(ManualRetryEntry entry) => entry.Level switch
  {
      ManualRetryLevel.Provider when _providerNames.TryGetValue(entry.Id, out var n) => n,
      ManualRetryLevel.Provider => $"#{entry.Id}",
      ManualRetryLevel.Account => $"#{entry.Id}",
      _ => entry.ModelId,
  };

  private async Task RetryNowAsync(ManualRetryEntry entry)
  {
      RetryStore.Unpark(entry.Level, entry.Id, entry.ModelId);
      Log.Info($"'{GetEntryDisplayName(entry)}' được retry thủ công — trở lại vòng xoay");
      await Toast.Show(L["manualRetry.msg.unparked"], ToastSeverity.Success);
      await ReloadEntriesAsync();
      StateHasChanged();
  }
  ```

  Trong `OnInitializedAsync` (merge vào body hiện có): `RetryStore.Changed += HandleRetryStoreChangedAsync;` + `await ReloadEntriesAsync();`
  Trong `Dispose()`: `RetryStore.Changed -= HandleRetryStoreChangedAsync;` (bên cạnh unsub hiện có).

- [ ] **Step 3: `Dashboard.razor` — card UI (sau card Status, cùng nhịp grid)**

  Sau card Status (`@* Status *@ ... </div>` đóng card thứ 3) — chèn TRƯỚC khi đóng `dashboard-grid`:

  ```razor
  @* Manual retry (spec §3.2): hiện provider/model đang park — nút Retry now unpark ngay *@
  @if (_retryEntries.Count > 0)
  {
      <div class="stat-card" data-testid="manual-retry-list">
          <div class="stat-label">@L["manualRetry.title"]</div>
          @foreach (var entry in _retryEntries)
          {
              <div class="manual-retry-row" @key="entry">
                  <span class="retry-level-badge">@L[$"manualRetry.level.{entry.Level.ToString().ToLowerInvariant()}"]</span>
                  <span class="retry-target">@GetEntryDisplayName(entry)</span>
                  <span class="retry-reason">@L[$"manualRetry.reason.{entry.Reason.ToString().ToLowerInvariant()}"]</span>
                  <button class="retry-now-btn" @onclick="() => RetryNowAsync(entry)">
                      @L["manualRetry.action.retry"]
                  </button>
              </div>
          }
      </div>
  }
  ```

  CSS (trong scope CSS hiện có của Dashboard, thêm rule):

  ```css
  /* Level badge: chừa chỗ cho label dạng Provider/Account/Model — width đủ cho text dài nhất */
  .retry-level-badge { min-width: 76px; text-align: center; }
  .manual-retry-row { display: flex; gap: 8px; align-items: center; padding: 4px 0; }
  .retry-target { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  ```

- [ ] **Step 4: i18n — +10 key × 2 dict**

  4a. EN (`Translations.cs`, en-dict): Insert SAU dòng chứa `["common.copied"]`:

  ```csharp
      ["manualRetry.title"] = "Manual retry list",
      ["manualRetry.action.retry"] = "Retry now",
      ["manualRetry.level.provider"] = "Provider",
      ["manualRetry.level.account"] = "Account",
      ["manualRetry.level.model"] = "Model",
      ["manualRetry.reason.unauthorized"] = "Unauthorized",
      ["manualRetry.reason.notFound"] = "Not found",
      ["manualRetry.reason.modelNotFound"] = "Model not found",
      ["manualRetry.reason.unreachable"] = "Unreachable",
      ["manualRetry.msg.unparked"] = "{key} resumed to rotation",
  ```

  4b. VI (dict tiếng Việt — insert cùng vị trí tương ứng, SAU block `settings.error.pingInterval`):

  ```csharp
      ["manualRetry.title"] = "Danh sách retry thủ công",
      ["manualRetry.action.retry"] = "Retry ngay",
      ["manualRetry.level.provider"] = "Nhà cung cấp",
      ["manualRetry.level.account"] = "Tài khoản",
      ["manualRetry.level.model"] = "Model",
      ["manualRetry.reason.unauthorized"] = "Không được phép",
      ["manualRetry.reason.notFound"] = "Không tìm thấy",
      ["manualRetry.reason.modelNotFound"] = "Không tìm thấy model",
      ["manualRetry.reason.unreachable"] = "Không thể kết nối",
      ["manualRetry.msg.unparked"] = "{key} đã trở lại vòng xoay",
  ```

  (`Toast.Show` truyền raw key? KHÔNG — giả định `Toast.Show` nhận text đã dịch; nếu signature hiện tại nhận key thì dùng `L["..."]` như Step 2.)

- [ ] **Step 5: Verify i18n parity + key được dùng**

  Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~TranslationParityTests"`
  Expected: PASS (EN/VI cùng key-set).

- [ ] **Step 6: Gate 1 — build Core**

  Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental`
  Expected: 0 Warning / 0 Error.

- [ ] **Step 7: Gate 2 — toàn bộ test**

  Run: `dotnet test "router balancing test/router balancing test.csproj"`
  Expected: **588 pass** (T8 +0 test — parity test đã có), 0 failed (hoặc đúng 2 `SingleInstanceGuardTests`).

- [ ] **Step 8: Gate 3 — build MAUI (chạm `router-balancing/`)**

  Đóng app đang chạy (pid 38160) nếu đang mở.
  Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --no-incremental`
  Expected: 0 Error.

- [ ] **Step 9: Commit** (3 file)

  ```bash
  git add router-balancing/Components/Pages/Dashboard.razor src/RouterBalancing.Core/Localization/Translations.cs
  git commit -m "feat: add manual retry list card to dashboard"
  ```

---

## Task 9: Final verification + ledger + commit plan (G6)

Chạy full gate cuối, xác minh ladder, ghi ledger, commit plan file.

### Files

- Modify: `.superpowers/sdd/progress.md` (append ledger batch), `docs/superpowers/plans/2026-10-04-manual-retry-revamp.md` (commit lần cuối — anchor `<!-- APPEND -->` đã bị gỡ khi viết xong plan).

### Steps

- [ ] **Step 1: Gate 1 — build Core**

  Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental`
  Expected: 0 Warning / 0 Error.

- [ ] **Step 2: Gate 2 — toàn bộ test**

  Run: `dotnet test "router balancing test/router balancing test.csproj"`
  Expected: **588 pass** (hoặc 586 + đúng 2 `SingleInstanceGuardTests` nếu app đang chạy).

- [ ] **Step 3: Gate 3 — build MAUI**

  Đóng app nếu đang mở.
  Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --no-incremental`
  Expected: 0 Error.

- [ ] **Step 4: Gate 4 — Vite build**

  Run: `npm run build` (workdir `router-balancing/vite-project`)
  Expected: 0 Error.

- [ ] **Step 5: Gate 5 — full solution build**

  Run: `dotnet build router-balancing.slnx --no-incremental`
  Expected: 0 Error (Warnings không mới — so với baseline).

- [ ] **Step 6: i18n parity + final check**

  Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~TranslationParityTests"`
  Expected: PASS.

- [ ] **Step 7: Manual checklist (spec §6.3)** — xác nhận từng mục:

  - [ ] Gõ prompt trong app → fail 1 provider → failover sang provider khác (4xx Fatal → `Failover` outcome).
  - [ ] Provider bị park (`Unreachable`/`Unauthorized`) → hiện trong Dashboard card "Manual retry list".
  - [ ] Bấm **Retry now** → provider rời card, quay lại vòng xoay (log Info `{key}... trở lại vòng xoay`).
  - [ ] Settings → Engine: `Ping interval (s)` + `Auto-retry parked providers via ping` hiển thị; lưu giá trị ping interval ngoài 10–86400 → lỗi `settings.error.pingInterval`.
  - [ ] `pingParkedProviders=true` + provider park do mạng → sau tick ping thành công → tự unpark + Info log.
  - [ ] URL `/v1` hiển thị trên Dashboard (Task 1).
  - [ ] Model `maxRetries=0` + 404 model → `model_not_found` → park (Model level) + failover (Task 4).

- [ ] **Step 8: `git status` — chỉ phantom + 2 file ledger/plan**

  Run: `git status --short`
  Expected: 23 file phantom CRLF (KHÔNG add) + `.superpowers/sdd/progress.md` + `docs/superpowers/plans/2026-10-04-manual-retry-revamp.md`.

- [ ] **Step 9: Append ledger**

  Append vào `.superpowers/sdd/progress.md`:

  ```markdown
  # Batch 5
  - Plan: docs/superpowers/plans/2026-10-04-manual-retry-revamp.md
  - Branch: feat/lan-proxyfix-pin-peraccount
  - Baseline: 570
  - Actual: <số pass gate 2 (Step 2)>
  - Specs: docs/superpowers/specs/2026-10-04-manual-retry-revamp-design.md @ 253b103
  ```

- [ ] **Step 10: Commit plan + ledger**

  ```bash
  git add docs/superpowers/plans/2026-10-04-manual-retry-revamp.md .superpowers/sdd/progress.md
  git commit -m "docs: add manual retry revamp implementation plan"
  ```

- [ ] **Step 11: Report** — ladder thực tế từng task vs bảng, các deviation đã phát hiện khi chạy (nếu có), và hỏi người dùng execution mode (**Subagent-driven** khuyến nghị / Inline).

---

## Notes for executor

- **Không push.** Mỗi task 1 commit, đúng message trên.
- **Flaky rerun ≤3**: `ProxyControlApiTests`, `ProxyRetryIntegrationTests`, `ProxyQueueTests` — nếu fail lần 1 → rerun `dotnet test --filter`; fail lần 3 → dừng báo cáo.
- **`SingleInstanceGuardTests` 2 fail**: app pid 38160 đang giữ port → đóng app hoặc bỏ qua đúng 2.
- **CRLF phantom**: chỉ `git add` file list trong commit steps.
- **App MAUI đang build/file-lock**: đóng trước gate 3/final.
- Test RED bắt buộc ở: T2 (task 6 timeout handler chưa có), T3 (store chưa có), T4 (outcome/passthrough chưa đổi), T5 (tham chiếu store chưa vào engine), T6 (settings đổi tên). T1, T7, T8 có thể GREEN-first (UI/test-only) — chấp nhận theo mục tiêu gate.
