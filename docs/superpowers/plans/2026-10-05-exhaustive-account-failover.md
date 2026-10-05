# Exhaustive Account Failover Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Walk mới tự thử mọi TK enabled × mọi provider trong combo trước khi trả lỗi client; gỡ toàn bộ ManualRetryStore/ping; log mỗi attempt fail đầy đủ provider/TK/status.

**Architecture:** Approach 1 từ spec — `RetryState` mở rộng (TK/provider đã thử + attempt trail), `ExecutionList.TryEnterAsync` tri-state kèm `excludedAccounts`, `DispatcherLoop.ServeAsync` rẽ nhánh account/candidate-advance theo `FailoverLevel`. Tầng chọn provider (RR/Fallback), queue priority, capacity giữ nguyên.

**Tech Stack:** .NET 10 / C# (MAUI Blazor Hybrid), xUnit, EF Core (SQLite).

**Spec:** `docs/superpowers/specs/2026-10-05-exhaustive-account-failover-design.md` — executor đọc kèm; mọi quyết định dẫn chiếu spec.

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên; 0 warning khi build.
- **Không đụng** file dirty ngoài phạm vi: `src/RouterBalancing.Core/Providers/ProviderRequestFactory.cs`, `src/RouterBalancing.Core/Providers/NvidiaRequestProfile.cs`, `src/RouterBalancing.Core/Providers/ProviderAttribution.cs` + test tương ứng (không phải của plan này).
- Log: hiển thị `provider.Name`/`account.Name`; **không** ghi body request, API key, prompt.
- Comment: tiếng Việt, giải thích "why"; XML doc cho public API.
- Commit: conventional English, 1 việc 1 commit (message gợi ý từng task).
- Gates cuối: `dotnet test` (0 failed) + `dotnet build router-balancing.slnx` (0 warning) + build app `-f net10.0-windows10.0.19041.0`.

## Review Focus

5 input/failure modes spec ngầm định mà test các task phải ghim:

1. **Provider chết có nhiều model trong combo** — lỗi mạng 1 model phải bỏ **cả provider** (mọi model), không tốn timeout thử model khác. → Task 5: `ServeAsync_WhenNetworkError_SkipsProvider_WithoutTryingOtherAccounts`.
2. **Capacity-park giữa walk rồi resume** — sau khi park/wake lại, TK & provider đã fail **không được thử lại** (`RetryState` sống qua re-enqueue). → Task 5: `ServeAsync_WhenUntriedAccountsFull_Reenqueues_AndDoesNotRetryTriedAccounts`.
3. **TK bị disable/xoá giữa walk (snapshot stale)** — `TryEnter` trả `NoAccountLeft` phải **advance**, không park treo, không loop vô hạn (mỗi vòng `NoAccountLeft` đánh dấu 1 pair → hữu hạn). → Task 4: `TryEnterAsync_WhenAllEnabledAccountsExcluded_ReturnsNoAccountLeft`; Task 5: `ServeAsync_WhenNoAccountLeft_MarksPairAndAdvances`.
4. **Exclude được scope theo provider** — TK đã fail ở provider A không được chặn provider B (401 hết TK A → vẫn thử B). → Task 5: `ServeAsync_WhenAllAccountsOfProviderAUnauthorized_MovesToProviderB`.
5. **Exhaustion contract nguyên vẹn** — attempt cuối có HTTP → passthrough **nguyên body + Retry-After**; toàn mạng → 502; client abort giữa account-switch → `Aborted`, không append lỗi vào stream. → Task 5: `ServeAsync_WhenExhausted_PassesThroughLastHttpResponse` (assert `Retry-After`), `ServeAsync_WhenExhaustedOnNetworkFailure_Returns502`, `ServeAsync_WhenClientAbortsDuringAccountSwitch_ReturnsAborted`.

---

### Task 0: Revert work session trước (chưa commit)

**Files:**
- Revert (git checkout HEAD): toàn bộ file modified của session khôi phục settings + tách ping — danh sách lấy từ `git status` (12 file: `SettingsKeys.cs`, `IAppSettingsService.cs`, `AppSettingsService.cs`, `SettingsDraft.cs`, `SettingsValidator.cs`, `Translations.cs`, `SettingsPanel.razor`, `ProviderPingService.cs`, `ProxyApp.cs` + 3 test `SettingsValidatorTests/AppSettingsServiceTests/ProviderPingServiceTests`).
- Delete: `router balancing test/Localization/SettingsTranslationTests.cs` (untracked, tạo trong session đó).

**Interfaces:** Không — về trạng thái HEAD `9b4e9af`. Theo spec §1.3 #4, `maxRetry`/`watchdogIntervalSec` đã bị gỡ từ commit `6432fb2` nên revert = hai key này tự biến mất (không cần task gỡ riêng).

- [ ] **Step 1:** Chạy `git status --short`, xác nhận đúng 12 file modified kể trên + 5 file untracked ngoài phạm vi + spec đã commit.
- [ ] **Step 2:** `git checkout -- <12 file>` và xoá `SettingsTranslationTests.cs`.
- [ ] **Step 3:** `git status --short` — chỉ còn: `ProviderRequestFactory.cs` (M), `NvidiaRequestProfile.cs`/`NvidiaRequestProfileTests.cs`/`ProviderAttribution.cs`/`ProviderAttributionTests.cs` (??). Nếu còn file khác → DỪNG, hỏi người dùng.
- [ ] **Step 4:** Chạy `dotnet test "router balancing test/router balancing test.csproj"` → Expected: xanh (trạng thái HEAD).
- [ ] **Step 5:** Không commit (đây là discard). Ghi chú vào log task: "maxRetry/watchdog đã absent tại HEAD".

### Task 1: Xoá `ProviderPingService` + ping settings

**Files:**
- Delete: `src/RouterBalancing.Core/Providers/ProviderPingService.cs`, `router balancing test/Providers/ProviderPingServiceTests.cs`
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs` (bỏ đăng ký `ProviderPingService` singleton+hosted ~dòng 102-103 và comment liên quan; **giữ** client `provider-probe`, **giữ** đăng ký `TimeProvider`), `Settings/SettingsKeys.cs`, `Settings/IAppSettingsService.cs`, `Settings/AppSettingsService.cs`, `Settings/SettingsDraft.cs`, `Settings/SettingsValidator.cs`, `router-balancing/Components/Pages/SettingsPanel.razor`, `src/RouterBalancing.Core/Localization/Translations.cs`
- Test: `router balancing test/Settings/SettingsValidatorTests.cs`, `router balancing test/Settings/AppSettingsServiceTests.cs`

**Interfaces:**
- Consumes: trạng thái Task 0 (HEAD).
- Produces: `IAppSettingsService` không còn `PingIntervalSec`/`PingParkedProviders` (hai key này KHÔNG tồn tại ở HEAD: `pingParkedIntervalSec` đã bị Task 0 revert). Consumer khác của ping: không còn.

- [ ] **Step 1 (dọn test):** Xoá file `ProviderPingServiceTests.cs`. Trong `SettingsValidatorTests` xoá case validate ping (`PingIntervalSec < 10` → `settings.error.pingInterval`) và property `PingIntervalSec` trong fixture; trong `AppSettingsServiceTests` xoá assert default/set/get ping. Chạy `dotnet build` → Expected: PASS (xoá thuần — RED không áp dụng; bước xác minh hành vi là grep Step 4 + suite Step 5).
- [ ] **Step 2:** Xoá 2 key `PingIntervalSec`, `PingParkedProviders` khỏi `SettingsKeys`; property tương ứng khỏi `IAppSettingsService`/`AppSettingsService`; prop khỏi `SettingsDraft`; rule validate khỏi `SettingsValidator`; field + checkbox + error-block + draft mapping trong `SettingsPanel.razor`; key i18n `settings.field.pingInterval`, `settings.field.pingParked`, `settings.error.pingInterval` khỏi `Translations.cs`.
- [ ] **Step 3:** Xoá `ProviderPingService.cs`; gỡ 2 dòng đăng ký + comment ping trong `ProxyApp.cs`.
- [ ] **Step 4:** Verify hết reference: `grep -ri "ProviderPingService|PingIntervalSec|PingParked" src router-balancing "router balancing test"` → Expected: 0 match.
- [ ] **Step 5:** `dotnet test` → Expected: xanh; `dotnet build router-balancing.slnx` → 0 warning.
- [ ] **Step 6:** Commit `refactor: remove provider ping service and ping settings`.

### Task 2: Xoá `ManualRetryStore`/parking + giới thiệu `FailoverLevel`

**Files:**
- Create: `src/RouterBalancing.Core/Engine/FailoverLevel.cs`
- Delete: `src/RouterBalancing.Core/Engine/ManualRetryStore.cs`, `IManualRetryStore.cs` (chứa `ManualRetryLevel`/`ManualRetryReason`/`ManualRetryEntry`), `ManualRetryI18n.cs`; test `ManualRetryStoreTests.cs`, `ManualRetryI18nTests.cs`
- Modify: `DispatchOutcome.cs` (Fatal reshape), `ChatCompletionsHandler.cs` (`ClassifyFatal`), `DispatcherLoop.cs` (bỏ ctor `store`, bỏ khối `store.Park`, `FilterRemaining` bỏ store-check), `ExecutionList.cs` (bỏ ctor `store`, bỏ filter parked trong `LoadCapacityAsync`), `Server/ProxyApp.cs` (factory if-absent, tham số `store`, gate `IsModelParked → 503`), `Server/ProxyHost.cs` (field/ctor/passthrough `_manualRetryStore`), `MauiProgram.cs:81`, `Components/Pages/Dashboard.razor` (card retry + code-behind), `Localization/Translations.cs` (block `manualRetry.*`)
- Test: `ChatCompletionsHandlerTests.cs` (assert → `FailoverLevel`, bỏ assert `Reason`), `DispatcherLoopTests.cs` (xoá toàn bộ block park-based ~dòng 552-865, bỏ fixture `ManualRetryStore`), `ExecutionListTests.cs` (xoá store param + 2 test parked ~336-357), `ModelSelectorTests.cs` + `ProxyHostTests.cs` (cập nhật ctor), `ProxyRetryIntegrationTests.cs` (xoá 5 test dùng store, giữ test queue/passthrough)

**Interfaces:**
- Consumes: trạng thái Task 1.
- Produces (Task 5 dùng):
  ```csharp
  public enum FailoverLevel { Account, Provider, Model }
  // DispatchOutcome.cs
  public sealed record Fatal(FailoverLevel Level, int? Status, string? ContentType,
      byte[] Body, TimeSpan? RetryAfter) : DispatchOutcome;
  ```
  - `DispatcherLoop` ctor: `(IRequestQueue, IExecutionList, IComboResolver, IModelSelector, ChatCompletionsHandler, ILogService)` — **bỏ `IManualRetryStore`**.
  - `ExecutionList` ctor: `(IDbContextFactory<RouterBalancingDbContext>)` — bỏ store.
  - `FilterRemaining`: còn `!IsTried(pair) && HasEnabledUntriedAccount(c)` (chỉ `a.Enabled`, không parked).

- [ ] **Step 1 (RED):** Trong `ChatCompletionsHandlerTests` đổi 7 assert phân loại: `ManualRetryLevel.X` → `FailoverLevel.X` (mạng→`Provider`, 401/403→`Account`, 404 model→`Model`, 404 khác→`Provider`), xoá mọi assert `ManualRetryReason`. Xoá `ManualRetryStoreTests.cs`, `ManualRetryI18nTests.cs`; xoá các block park-based trong `DispatcherLoopTests`/`ExecutionListTests`; sửa ctor fixture `DispatcherLoopTests`/`ModelSelectorTests`/`ProxyHostTests`/`ExecutionListTests` (bỏ store); xoá 5 store-test trong `ProxyRetryIntegrationTests`. Chạy `dotnet build` → Expected: FAIL (`FailoverLevel` chưa tồn tại, `ManualRetryLevel` biến mất).
- [ ] **Step 2:** Tạo `FailoverLevel.cs`; reshape `Fatal` (bỏ `Level` cũ type, `Id`, `ModelId`, `Reason`); cập nhật `ClassifyFatal` trả `FailoverLevel` theo bảng spec §3.1.
- [ ] **Step 3:** `DispatcherLoop`: bỏ tham số `store` ctor + import; xoá khối `store.Park` trong `ServeAsync` (giữ nguyên `MarkTried` + `LastFailure` cho Retryable/Fatal); `FilterRemaining` bỏ `IsXxxParked`, `HasEnabledUnparkedAccount` → `HasEnabledUntriedAccount` chỉ check `a.Enabled`.
- [ ] **Step 4:** `ExecutionList`: bỏ ctor `store` + dòng filter parked trong `LoadCapacityAsync`. `ProxyApp`: xoá factory store, tham số `store`, dòng gate `IsModelParked` (endpoint giờ enqueue thẳng); `ProxyHost` + `MauiProgram`: bỏ đăng ký/field `IManualRetryStore`. `Dashboard.razor`: xoá card + toàn bộ code retry. `Translations.cs`: xoá block `manualRetry.*`. Xoá 3 file core store.
- [ ] **Step 5:** `grep -ri "ManualRetry|IManualRetryStore|IsModelParked|ManualRetryI18n" src router-balancing "router balancing test"` → Expected: 0 match. `dotnet test` → xanh; `dotnet build router-balancing.slnx` → 0 warning.
- [ ] **Step 6:** Commit `refactor: remove manual retry store and parking in favor of failover levels`.

### Task 3: Mở rộng `RetryState`

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/RetryState.cs`
- Test: `router balancing test/Engine/RetryStateTests.cs` (mở rộng)

**Interfaces:**
- Consumes: trạng thái Task 2.
- Produces (Task 5/6 dùng):
  ```csharp
  public int Attempts { get; }                        // số lần gọi ForwardAsync
  public IReadOnlyList<AttemptRecord> Trail { get; }   // lịch sử attempt cho exhaustion Details
  public sealed record AttemptRecord(string Provider, string Model, string Account, int? Status);
  public void RecordAttempt(string provider, string model, string account, int? status);
  public void MarkAccountTried(long providerId, long accountId);
  public bool IsAccountTried(long providerId, long accountId);
  public void MarkProviderFailed(long providerId);
  public bool IsProviderFailed(long providerId);
  ```
  - `HasTried`/`TriedCount`/`MarkTried(pair)`/`IsTried(pair)`/`LastFailure` **giữ nguyên**.
  - Quy ước (spec §2.3): cấp **Account** fail → chỉ `MarkAccountTried` (KHÔNG mark pair — pair đánh dấu sau ở `NoAccountLeft`); cấp **Provider** → `MarkProviderFailed` **và** `MarkTried(pair)` (đảm bảo `HasTried=true` → exhaustion thay vì 503 walk-rỗng); cấp **Model** → `MarkTried(pair)`.

- [ ] **Step 1 (RED):** Thêm test vào `RetryStateTests`: `RetryState_MarkAccountTried_IsScopedPerProvider` (TK id=7 provider1 tried không ảnh hưởng provider2), `RetryState_MarkProviderFailed_AndIsProviderFailed`, `RetryState_RecordAttempt_IncrementsAttemptsAndAppendsTrail` (2 lần gọi → `Attempts==2`, `Trail` giữ thứ tự, `Status=null` cho network), test cũ giữ nguyên. Chạy → Expected: FAIL (chưa có API).
- [ ] **Step 2:** Triển khai 4 nhóm API trên (dòng `Trạng thái` XML doc tiếng Việt nêu quy ước trên).
- [ ] **Step 3:** `dotnet test --filter RetryStateTests` → xanh.
- [ ] **Step 4:** Commit `feat: extend retry state with account and provider failure tracking`.

### Task 4: `TryEnterResult` tri-state + `excludedAccounts`

**Files:**
- Create: `src/RouterBalancing.Core/Engine/TryEnterResult.cs`
- Modify: `src/RouterBalancing.Core/Engine/IExecutionList.cs`, `ExecutionList.cs`, `DispatcherLoop.cs` (2 điểm gọi — map kết quả mới, **chưa** đổi semantics walk)
- Test: `router balancing test/Engine/ExecutionListTests.cs`

**Interfaces:**
- Consumes: trạng thái Task 2 (ctor không store).
- Produces (Task 5 dùng):
  ```csharp
  public abstract record TryEnterResult
  {
      public sealed record Entered(long AccountId) : TryEnterResult; // gồm sentinel 0
      public sealed record Full : TryEnterResult;
      public sealed record NoAccountLeft : TryEnterResult;
  }
  // IExecutionList
  Task<TryEnterResult> TryEnterAsync(long providerId, string requestId, string providerName,
      string modelId, RequestPriority priority, DateTimeOffset enqueuedAt,
      IReadOnlySet<long>? excludedAccounts, CancellationToken ct);
  ```
  - Thứ tự logic trong `ExecutionList` (spec §2.2): `capacity==null` → `Full`; `Accounts.Count==0` → `Entered(0)`; `Accounts − excluded` rỗng → `NoAccountLeft`; còn nhưng hết capacity → `Full`; còn capacity → chọn (giữ nguyên least-in-flight + RR tie) → `Entered(id)`.
  - `DispatcherLoop` Task 4 (chưa phải Task 5): truyền `excludedAccounts: null`; map `Entered(e) → e.AccountId`, `Full → park`, `NoAccountLeft → Full` (unreachable khi exclude=null — thêm comment why: chỉ trường hợp stale mới tới được nhánh này, Task 5 sẽ rẽ nhánh đúng).

- [ ] **Step 1 (RED):** Thêm `ExecutionListTests`: `TryEnterAsync_WhenAccountExcluded_DoesNotPickItAgain`, `TryEnterAsync_WhenUntriedAccountsAllFull_ReturnsFull`, `TryEnterAsync_WhenAllEnabledAccountsExcluded_ReturnsNoAccountLeft`, `TryEnterAsync_WhenNoEnabledAccounts_ReturnsSentinelZero` (trả `Entered(0)`), `TryEnterAsync_WhenProviderMissing_ReturnsFull`, `TryEnterAsync_WithoutExclusion_RoundRobinsAsBefore` (giữ hành vi cũ). Cập nhật các test cũ gọi `TryEnterAsync` (thêm `null`). Chạy → Expected: FAIL compile (kiểu trả về đổi).
- [ ] **Step 2:** Triển khai `TryEnterResult` + logic thứ tự trên; sửa `IExecutionList`; sửa 2 điểm gọi trong `DispatcherLoop` theo mục "DispatcherLoop Task 4" phía trên.
- [ ] **Step 3:** `dotnet test --filter ExecutionListTests` + `DispatcherLoopTests` → xanh (walk cũ chưa đổi hành vi).
- [ ] **Step 4:** Commit `feat: add tri-state try-enter with account exclusion`.

### Task 5: Walk mới trong `DispatcherLoop.ServeAsync` (lõi)

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` (`ServeAsync`, `FilterRemaining`, bỏ helper `HasEnabledUnparkedAccount` cũ nếu còn)
- Test: `router balancing test/Engine/DispatcherLoopTests.cs`

**Interfaces:**
- Consumes: `FailoverLevel` + `Fatal` reshape (Task 2), `RetryState` API (Task 3), `TryEnterResult` + `excludedAccounts` (Task 4).
- Produces: semantics walk cho Task 6 (log đọc cùng nhánh rẽ) — không đổi signature.

**Thuật toán (spec §2.4/§3.1 — implementer không tự quyết):**

1. Sau `ForwardAsync`, nếu outcome không phải `Retryable`/`Fatal` → giữ nguyên (Exit + complete).
2. Gọi `request.Retry.RecordAttempt(provider.Name, model.ModelId, accountName hoặc "-", statusOrNull)`; cập nhật `LastFailure` (giữ khối hiện tại); accountName tra từ `candidate.Provider.Accounts` theo `accountId` (không có → `"-"`).
3. Rẽ theo cấp:
   - **Account** (`Retryable` hoặc `Fatal(Account)`): `MarkAccountTried` → log (Task 6) → `Exit` → `TryEnter(same provider, exclude = accounts của candidate.Provider mà IsAccountTried)`:
     - `Entered(id)` → `accountId = id`, `continue` (cùng candidate).
     - `Full` → `ReenqueueForPark` (KHÔNG mark pair).
     - `NoAccountLeft` → `MarkTried(pair)` → candidate-advance.
   - **Model** (`Fatal(Model)`): `MarkTried(pair)` → log → Exit → candidate-advance.
   - **Provider** (`Fatal(Provider)`): `MarkProviderFailed(id)` **+** `MarkTried(pair)` → log → Exit → candidate-advance.
4. **Candidate-advance** (= code advance hiện tại, giữ nguyên try/catch/abort): `FilterRemaining` → rỗng → `CompleteExhaustion` + Exit + complete; còn → selector → `TryEnter(new, exclude = tried của provider mới)` → `Entered` continue / `Full` park / `NoAccountLeft` → `MarkTried(pair)` → lặp lại candidate-advance (mỗi vòng đánh dấu 1 pair → hữu hạn).
5. `FilterRemaining` mới: `!IsTried(pair) && !IsProviderFailed(pid) && HasEnabledUntriedAccount(c)` với `HasEnabledUntriedAccount(c) = c.Provider.Accounts?.Any(a => a.Enabled && !IsAccountTried(pid, a.Id)) != false`.

- [ ] **Step 1 (RED):** Trong `DispatcherLoopTests` thêm/đổi các test (dùng fixture fake upstream + `CapturingLog` có sẵn; mỗi test đặt tên đúng hành vi):
  - `ServeAsync_When401_TriesNextAccountOfSameProvider_BeforeChangingProvider` — provider A có TK1/TK2: TK1→401, TK2→200; assert upstream nhận request trên TK2 và KHÔNG gửi sang provider B.
  - `ServeAsync_When429_TriesNextAccountOfSameProvider` — như trên với 429.
  - `ServeAsync_WhenAllAccountsTried_MovesToNextProvider` — A/TK1 401, A/TK2 403 → request đi B, 200.
  - `ServeAsync_WhenAllAccountsOfProviderAUnauthorized_MovesToProviderB` — (Review Focus #4) exclude scope per-provider: B/TK3 vẫn được chọn.
  - `ServeAsync_WhenNetworkError_SkipsProvider_WithoutTryingOtherAccounts` — combo có A/m1 + A/m2 + B/m1; A/m1 network fail → KHÔNG attempt nào tới A/m2 (dựa on số lần gọi upstream), request hoàn tất qua B.
  - `ServeAsync_WhenModelNotFound_SkipsCandidate` — 404 body `error.code=model_not_found` → skip đúng pair, model khác cùng provider vẫn thử.
  - `ServeAsync_When404Other_SkipsProvider` — 404 thường → bỏ nguyên provider.
  - `ServeAsync_WhenNoAccountLeft_MarksPairAndAdvances` — (Review Focus #3) fake `TryEnter` trả `NoAccountLeft` → pair bị đánh dấu, advance, không loop.
  - `ServeAsync_WhenUntriedAccountsFull_Reenqueues_AndDoesNotRetryTriedAccounts` — (Review Focus #2) park khi đầy → `Exited` wake → chỉ thử TK chưa fail; kết thúc bằng exhaustion.
  - `ServeAsync_WhenExhausted_PassesThroughLastHttpResponse` — attempt cuối 429 body X + header `Retry-After: 7` → client nhận passthrough status 429 + body X + header `7`.
  - `ServeAsync_WhenExhaustedOnNetworkFailure_Returns502`.
  - `ServeAsync_WhenClientAbortsDuringAccountSwitch_ReturnsAborted` — token abort sau fail đầu → `Aborted`, không ghi JSON vào stream.
  - `TryDispatch_WhenComboFallback_KeepsPositionalOrderAcrossProviders` — Fallback: A hết TK rồi mới B (thứ tự Position).
  - Walk rỗng chưa thử ai → 503 `temporarily unavailable` (giữ test cũ nếu có).
  - **Đổi các test cũ** đang assert "401 → nhảy provider ngay" (hành vi 1 TK/provider) sang kỳ vọng account kế. Chạy → Expected: FAIL.
- [ ] **Step 2:** Triển khai thuật toán 1-5 phía trên trong `ServeAsync`/`FilterRemaining`.
- [ ] **Step 3:** `dotnet test --filter DispatcherLoopTests` → xanh; sau đó `dotnet test` toàn suite (các test khác bị ảnh hưởng gián tiếp — sửa nếu sai semantics mới, không bao giờ sửa để "pass ngược" spec).
- [ ] **Step 4:** Commit `feat: walk all provider accounts before failing over to next provider`.

### Task 6: Log walk (attempt Warn + exhaustion Error)

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` (thêm helper log, xoá `LogAdvance`, sửa `RecordExhaustion`)
- Test: `router balancing test/Engine/DispatcherLoopTests.cs`

**Interfaces:**
- Consumes: `RetryState.Attempts`/`Trail`/`RecordAttempt` (Task 3, đã gọi ở Task 5), nhánh rẽ Task 5.
- Produces: — (log là hành vi quan sát).

**Quyết định (spec §5):**
- Format Warn: `Request {id} — attempt {k}/{N} fail: provider '{p}'/'{m}' account '{a}' HTTP {status|lỗi mạng} → {action}`; `action` ∈ `chuyển TK kế`/`chuyển provider kế`/`chuyển candidate kế`/`chờ slot`/`exhausted` — lấy từ nhánh rẽ đã đi.
- `k = request.Retry.Attempts` (sau khi `RecordAttempt`); `N` = `Attempts` tại thời điểm vào `ServeAsync` + tổng `a.Enabled` của danh sách `remaining` lúc đó (snapshot, ước lượng).
- `Details` = snippet body lỗi (JSON bytes → chuỗi UTF-8, cắt **500** ký tự, bọc try) ; lỗi mạng (`Status=null`) → Details = `"lỗi mạng"` (exception đã có ở dòng handler).
- Exhaustion: giữ 1 dòng `Request {id} thất bại sau {Attempts} attempt — chuyển phản hồi cuối về client` (đổi từ "candidate"), `Details` = JSON mảng từ `Trail`: serialize **anon object** `{ p = r.Provider, m = r.Model, a = r.Account, s = r.Status }` để ra đúng tên khóa ngắn.
- **Xoá `LogAdvance`**. Mọi log bọc try/catch nuốt (I2). `LogCategory.Request`.

- [ ] **Step 1 (RED):** 2 test mới: `ServeAsync_LogsWarnPerFailedAttempt_WithProviderAndAccountNames` — assert `CapturingLog` chứa đúng 1 dòng Warn mỗi attempt fail, substring `'provider-a'/'m1'`, `account 'tk-2'`, `HTTP 429`, `attempt 2/`, action `chuyển TK kế`; KHÔNG còn dòng Warn cũ `"Chuyển candidate kế"`. `ServeAsync_WhenExhausted_LogsErrorWithAttemptTrailInDetails` — dòng Error chứa `{Attempts} attempt`; `Details` parse được JSON mảng, phần tử cuối khớp provider/account/status của attempt cuối. Chạy → FAIL (chưa có code log).
- [ ] **Step 2:** Triển khai helper log + sửa `RecordExhaustion` theo quyết định; xoá `LogAdvance`.
- [ ] **Step 3:** `dotnet test --filter DispatcherLoopTests` → xanh.
- [ ] **Step 4:** Commit `feat: log per-attempt walk failures and exhaustion trail`.

### Task 7: Integration test mới

**Files:**
- Modify: `router balancing test/Server/ProxyRetryIntegrationTests.cs` (thay 5 store-test đã xoá ở Task 2)

**Interfaces:**
- Consumes: toàn bộ Task 1-6 (app chạy được với walk mới).

- [ ] **Step 1 (RED):** 4 test end-to-end (TestServer + fake upstream, theo §6.2 spec):
  - `Chat_When401OnFirstAccount_SucceedsOnSecondAccountOfSameProvider` — client 200, upstream chỉ thấy A/TK1 (401) rồi A/TK2 (200), không thấy B.
  - `Chat_WhenAllAccountsOfProviderUnauthorized_FailsOverToNextProvider` — client 200 qua B.
  - `Chat_WhenProviderNetworkDead_TriesProviderOnceThenSucceedsElsewhere` — đếm attempt: đúng 1 lần network tới A (mọi model), 200 qua B.
  - `Chat_WhenAllUpstreamFail_PassesThroughLastResponse` / `..._Returns502WhenAllNetworkFailures`.
  - Giữ xanh các test passthrough 4xx / validate / resolve / no-key / cancel hiện có.
- [ ] **Step 2:** Chạy → FAIL → bổ sung implementation nếu thiếu (hiếm — chỉ wiring) → PASS.
- [ ] **Step 3:** `dotnet test` toàn suite → xanh.
- [ ] **Step 4:** Commit `test: cover exhaustive account failover end to end`.

### Task 8: Gates cuối

- [ ] **Step 1:** Sạch reference: `grep -riE "ManualRetry|ProviderPing|PingParked|pingInterval|IsModelParked|manualRetry|watchdog|maxRetry" src router-balancing "router balancing test"` → Expected: 0 match.
- [ ] **Step 2:** `dotnet test` → 0 failed; `dotnet build router-balancing.slnx` → 0 Warning/0 Error; `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → 0 Warning.
- [ ] **Step 3:** `git status --short` — chỉ còn file ngoài phạm vi (ProviderRequestFactory, Nvidia*, ProviderAttribution*).
- [ ] **Step 4:** Nếu phải sửa gì ở các bước trên → commit riêng `fix: ...`; nếu tất cả xanh từ đầu → không commit thêm.
