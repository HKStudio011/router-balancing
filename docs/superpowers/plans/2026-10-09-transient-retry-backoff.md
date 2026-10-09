# Transient Retry Backoff Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Retry transient upstream errors (null/408/5xx — gồm 504) trên **cùng (provider, model, account)** với exponential backoff + jitter TRƯỚC khi failover, ngân sách per-request, cấu hình qua Settings.

**Architecture:** Nhánh mới trong `DispatcherLoop.ServeAsync` đặt sau journal, trước nhánh `level` routing — giữ nguyên slot/candidate/account, `Task.Delay(wait, RequestAborted)` rồi `continue` vòng serve. `BackoffPolicy` static tách riêng math; settings đọc tại mỗi quyết định retry (pattern `ProviderProbeTimeoutHandler`). 429 KHÔNG thuộc transient — rotate TK ngay như hiện tại.

**Tech Stack:** .NET 10 / C# primary-constructor, xUnit, Blazor SettingsPanel, bash e2e.

**Spec:** `docs/superpowers/specs/2026-10-09-transient-retry-backoff-design.md` (commit `d74e59d`)

## Global Constraints

- Transient = `status is null` (lỗi mạng), `408`, `500..599`; **429 KHÔNG transient** (rotate TK ngay). Chỉ xét với `DispatchOutcome.Retryable` — `Fatal` (401/403/404) đi nhánh failover cũ, không retry.
- Settings: `transientMaxRetries` default **5**, range **0..10** (0 = tắt, back-compat y hệt code cũ); `transientBackoffBaseMs` default **1000**, range **250..4000**.
- Backoff: `wait(n) = min(baseMs × 2^(n-1), 4000) + jitter uniform[0..25%]`, n = `TransientRetries` sau khi ++ (1-based); **cap áp TRƯỚC jitter**; n ≤ 1 → baseMs (không nhân) + jitter.
- `TransientRetries` sống trên `RetryState` — per-request, **không reset** khi park/advance account.
- Không đổi error contract, không thêm delay seam `IDelay` (test dùng base 250ms), không trace stage mới, không entity DB/endpoint mới.
- i18n convention: `settings.field.*` + `settings.error.*`, đủ `English` + `Vietnamese` (TranslationParityTests tự enforce).
- Log retry dùng `LogAttemptFail` với severity **Info**; mọi caller cũ giữ **Warning**.
- Gate mỗi task: `dotnet test "router balancing test/router balancing test.csproj"` 0 failed trước khi commit. UI task + cuối slice: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` 0W0E.
- **Commit staging CỰC KỲ NGHIÊM NGẶT:** working tree đang có thay đổi v1-responses + live-trace-markers chưa commit. Mỗi task chỉ `git add` đúng các file trong task — **KHÔNG BAO GIỜ** `git add .` hay `git add -A`. Commit message tiếng Anh, conventional.
- **Cố ý KHÔNG đụng** `router balancing test/Server/ResponsesEndpointIntegrationTests.cs` (file đang chứa thay đổi v1-responses chưa commit — đụng sẽ commit lẫn). Test 500 trong file đó vẫn pass với default-on (không assert số lần gọi upstream), chỉ chậm hơn ~15s — chấp nhận.

## Review Focus

1. **429 là Retryable nhưng không được retry cùng TK** — nếu quên `IsTransient` trong nhánh mới, 429 sẽ backoff thay vì rotate → test `Serve_When429_RotatesAccountWithoutSameAccountRetry` (Task 6).
2. **Abort trong delay làm rò slot** — thiếu `Exit` → capacity leak, request sau park vô hạn → test `Serve_WhenClientAbortsDuringBackoff_ExitsSlotAndCompletesAborted` + `Contains=false` (Task 7).
3. **Default-on làm test hiện có chậm/red** — bất kỳ test nào upstream ném exception hoặc trả 5xx qua dispatcher đều sẽ retry ×5 (~15s) → pin baseline `TransientMaxRetries=0` trong harness `DispatcherLoopTests.StartAsync` + 3 file integration (Task 6, cùng commit với behavior change).
4. **Cap 4000ms áp trước jitter** — nếu cap sau jitter thì `Delay(3,1000)` không bao giờ > 4000 → test `Delay_ExponentialCappedWithJitter_ReturnsInRange` assert InRange [4000..5000] (Task 1).
5. **Budget per-request sống qua rotate** — sau khi hết budget trên TK1, TK2 không được retry tiếp → test `Serve_When504Persists_ExhaustsBudgetThenRotatesAndPassesThrough` assert đúng 4 call (Task 7).

---

### Task 1: BackoffPolicy

**Files:**
- Create: `src/RouterBalancing.Core/Engine/BackoffPolicy.cs`
- Test: `router balancing test/Engine/BackoffPolicyTests.cs` (mới)

**Interfaces:**
- Consumes: không có (static, RNG inject qua tham số).
- Produces: `public static TimeSpan BackoffPolicy.Delay(int retryNumber, int baseMs)` và overload `Delay(int retryNumber, int baseMs, Random rng)` — Task 6 dùng overload mặc định.

- [ ] **Step 1: Viết failing tests**

```csharp
public class BackoffPolicyTests
{
    [Theory]
    [InlineData(1, 1000, 1000, 1250)]   // n=1: base, không nhân
    [InlineData(2, 1000, 2000, 2500)]   // n=2: ×2
    [InlineData(3, 1000, 4000, 5000)]   // n=3: ×4, chạm cap 4000 + jitter ≤25%
    [InlineData(4, 1000, 4000, 5000)]   // n=4: cap giữ nguyên
    [InlineData(10, 1000, 4000, 5000)]  // n lớn: vẫn cap
    [InlineData(0, 1000, 1000, 1250)]   // n ≤ 1 → base
    [InlineData(-3, 1000, 1000, 1250)]
    public void Delay_ExponentialCappedWithJitter_ReturnsInRange(int n, int baseMs, int low, int high)
    {
        var rng = new Random(42);
        var wait = BackoffPolicy.Delay(n, baseMs, rng);
        Assert.InRange(wait.TotalMilliseconds, low, high);
    }

    [Fact]
    public void Delay_SameSeed_ProducesSameWait()
    {
        var a = BackoffPolicy.Delay(2, 1000, new Random(7));
        var b = BackoffPolicy.Delay(2, 1000, new Random(7));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Delay_DefaultOverload_UsesSharedRandom_InRange()
    {
        var wait = BackoffPolicy.Delay(1, 250);
        Assert.InRange(wait.TotalMilliseconds, 250, 312.5);
    }
}
```

- [ ] **Step 2: Chạy verify FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~BackoffPolicyTests`
Expected: FAIL — `BackoffPolicy` chưa tồn tại (compile error).

- [ ] **Step 3: Implement `BackoffPolicy`**

Trong `src/RouterBalancing.Core/Engine/BackoffPolicy.cs` — `public static class BackoffPolicy`, XML doc (tiếng Việt) nêu spec transient-retry §2.1/§3.3. Công thức: `rawMs = retryNumber <= 1 ? baseMs : Math.Min((long)baseMs * (1L << Math.Min(retryNumber - 1, 20)), 4000)` (dùng `long` tránh tràn khi shift lớn), `jitter = rng.NextDouble() * 0.25 * rawMs`, trả `TimeSpan.FromMilliseconds(rawMs + jitter)`. Overload mặc định gọi overload RNG với `Random.Shared`.

- [ ] **Step 4: Chạy verify PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~BackoffPolicyTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Engine/BackoffPolicy.cs "router balancing test/Engine/BackoffPolicyTests.cs"
git commit -m "feat: add BackoffPolicy with exponential backoff and jitter"
```

---

### Task 2: RetryClassifier.IsTransient

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/RetryClassifier.cs`
- Test: `router balancing test/Engine/RetryClassifierTests.cs`

**Interfaces:**
- Consumes: không có.
- Produces: `public static bool RetryClassifier.IsTransient(int? status)` — Task 6 dùng trong nhánh retry.

- [ ] **Step 1: Viết failing tests** (append vào `RetryClassifierTests`)

```csharp
[Theory]
[InlineData(null)]                         // lỗi mạng/timeout — catch filter 3A
[InlineData(408)]
[InlineData(500)]
[InlineData(502)]
[InlineData(503)]
[InlineData(504)]
public void IsTransient_NetworkTimeoutAnd5xx_ReturnsTrue(int? status)
    => Assert.True(RetryClassifier.IsTransient(status));

[Theory]
[InlineData(429)]                          // 429 KHÔNG transient — rotate TK ngay (§1.3)
[InlineData(400)]
[InlineData(401)]
public void IsTransient_RateLimitAndClientErrors_ReturnsFalse(int? status)
    => Assert.False(RetryClassifier.IsTransient(status));
```

- [ ] **Step 2: Chạy verify FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~RetryClassifierTests`
Expected: FAIL compile — `IsTransient` chưa tồn tại.

- [ ] **Step 3: Implement `IsTransient(int? status)`**

Thêm vào `RetryClassifier` (giữ `IsRetryable` nguyên): `public static bool IsTransient(int? status) => status is null or 408 or (>= 500 and <= 599);` — XML doc nêu true với mạng/408/5xx, false với 429 (và 4xx khác, tuy 4xx không Retryable về đây).

- [ ] **Step 4: Chạy verify PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~RetryClassifierTests`
Expected: PASS (giữ nguyên test cũ + 2 test mới).

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Engine/RetryClassifier.cs "router balancing test/Engine/RetryClassifierTests.cs"
git commit -m "feat: add RetryClassifier.IsTransient for transient classification"
```

---

### Task 3: Settings core (keys, properties, draft, validator)

**Files:**
- Modify: `src/RouterBalancing.Core/Settings/SettingsKeys.cs`
- Modify: `src/RouterBalancing.Core/Settings/IAppSettingsService.cs`
- Modify: `src/RouterBalancing.Core/Settings/AppSettingsService.cs`
- Modify: `src/RouterBalancing.Core/Settings/SettingsDraft.cs`
- Modify: `src/RouterBalancing.Core/Settings/SettingsValidator.cs`
- Test: `router balancing test/Settings/SettingsValidatorTests.cs`
- Test: `router balancing test/Settings/AppSettingsServiceTests.cs`

**Interfaces:**
- Consumes: pattern `Get(key, default)` của `AppSettingsService`.
- Produces:
  - `SettingsKeys.TransientMaxRetries` ("transientMaxRetries"), `SettingsKeys.TransientBackoffBaseMs` ("transientBackoffBaseMs")
  - `IAppSettingsService.TransientMaxRetries` (int, default 5), `.TransientBackoffBaseMs` (int, default 1000)
  - `SettingsDraft.TransientMaxRetries` (=5), `.TransientBackoffBaseMs` (=1000)
  - Validator lỗi: `"settings.error.transientRetries"` (0..10), `"settings.error.transientBackoffBase"` (250..4000)
  - Task 4 (UI) và Task 6 (dispatcher) consume các tên này.

- [ ] **Step 1: Viết failing tests**

`SettingsValidatorTests` — thêm 2 dòng vào `ValidDraft()`: `TransientMaxRetries = 5, TransientBackoffBaseMs = 1000;` và 2 test:

```csharp
[Fact]
public void Validate_WhenTransientValuesAtRangeEdges_ReturnsEmpty()
{
    var errors = SettingsValidator.Validate(ValidDraft() with
    {
        TransientMaxRetries = 0,          // 0 hợp lệ = tắt
        TransientBackoffBaseMs = 250,     // min hợp lệ
    });
    Assert.Empty(errors);
}

[Fact]
public void Validate_WhenTransientValuesOutOfRange_ReturnsEachFieldError()
{
    var low = SettingsValidator.Validate(ValidDraft() with { TransientMaxRetries = -1, TransientBackoffBaseMs = 249 });
    var high = SettingsValidator.Validate(ValidDraft() with { TransientMaxRetries = 11, TransientBackoffBaseMs = 4001 });

    Assert.Equal("settings.error.transientRetries", low[nameof(SettingsDraft.TransientMaxRetries)]);
    Assert.Equal("settings.error.transientBackoffBase", low[nameof(SettingsDraft.TransientBackoffBaseMs)]);
    Assert.Equal("settings.error.transientRetries", high[nameof(SettingsDraft.TransientMaxRetries)]);
    Assert.Equal("settings.error.transientBackoffBase", high[nameof(SettingsDraft.TransientBackoffBaseMs)]);
}
```

`AppSettingsServiceTests.Get_MissingKey_ReturnsDefault` — append 2 assert:

```csharp
Assert.Equal(5, service.TransientMaxRetries);
Assert.Equal(1000, service.TransientBackoffBaseMs);
```

- [ ] **Step 2: Chạy verify FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~SettingsValidatorTests|FullyQualifiedName~AppSettingsServiceTests"`
Expected: FAIL compile — properties/keys chưa tồn tại.

- [ ] **Step 3: Implement settings core**

- `SettingsKeys`: 2 const mới kèm XML doc (default/range).
- `IAppSettingsService`: 2 property + XML doc.
- `AppSettingsService`: `public int TransientMaxRetries => Get(SettingsKeys.TransientMaxRetries, 5);` và `TransientBackoffBaseMs => Get(..., 1000);` (pattern `ProviderProbeTimeoutSec`).
- `SettingsDraft`: 2 property mutable, default 5 / 1000.
- `SettingsValidator`: 2 rule — `draft.TransientMaxRetries is < 0 or > 10` → `"settings.error.transientRetries"`; `draft.TransientBackoffBaseMs is < 250 or > 4000` → `"settings.error.transientBackoffBase"`.

- [ ] **Step 4: Chạy verify PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~SettingsValidatorTests|FullyQualifiedName~AppSettingsServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Settings/SettingsKeys.cs src/RouterBalancing.Core/Settings/IAppSettingsService.cs src/RouterBalancing.Core/Settings/AppSettingsService.cs src/RouterBalancing.Core/Settings/SettingsDraft.cs src/RouterBalancing.Core/Settings/SettingsValidator.cs "router balancing test/Settings/SettingsValidatorTests.cs" "router balancing test/Settings/AppSettingsServiceTests.cs"
git commit -m "feat: add transient retry settings keys and validation"
```

---

### Task 4: i18n + Settings UI

**Files:**
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs`
- Modify: `router-balancing/Components/Pages/SettingsPanel.razor` (section Engine ~dòng 164–181; `@code`: `EngineFields` ~270, `LoadDraft` ~301, `SaveEngine` ~400)

**Interfaces:**
- Consumes: `SettingsDraft.TransientMaxRetries/TransientBackoffBaseMs`, validator error keys (Task 3).
- Produces: 4 key i18n — UI render; Task 6 không phụ thuộc.

- [ ] **Step 1: Thêm 4 key vào cả `English` và `Vietnamese` dictionaries**

| Key | English | Vietnamese |
|---|---|---|
| `settings.field.transientRetries` | `Transient retries per request` | `Số retry transient mỗi request` |
| `settings.error.transientRetries` | `Transient retries must be between 0 and 10.` | `Số retry transient phải nằm trong khoảng 0–10.` |
| `settings.field.transientBackoffBase` | `Transient backoff base (ms)` | `Cơ sở backoff retry (ms)` |
| `settings.error.transientBackoffBase` | `Transient backoff base must be between 250 and 4000 ms.` | `Cơ sở backoff retry phải nằm trong khoảng 250–4000 ms.` |

Đặt cạnh các key `settings.field.probeTimeout` / `settings.error.probeTimeout` hiện có (dòng ~128 và ~524).

- [ ] **Step 2: Mở rộng SettingsPanel section Engine**

- Markup: thêm 2 `<label>` number input (pattern `probeTimeout`, `@bind="_draft.TransientMaxRetries"` / `@bind="_draft.TransientBackoffBaseMs"`) vào `div.grid` hiện có; thêm 2 khối `@if (Error(nameof(...)))` bên dưới (pattern `probeTimeoutError`).
- `EngineFields`: thêm `nameof(SettingsDraft.TransientMaxRetries)`, `nameof(SettingsDraft.TransientBackoffBaseMs)`.
- `LoadDraft()`: gán 2 property từ `Settings.TransientMaxRetries` / `Settings.TransientBackoffBaseMs`.
- `SaveEngine()`: `Settings.Set(SettingsKeys.TransientMaxRetries, _draft.TransientMaxRetries);` + `Settings.Set(SettingsKeys.TransientBackoffBaseMs, _draft.TransientBackoffBaseMs);` (comment pattern: engine đọc setting mỗi request → áp dụng ngay).

- [ ] **Step 3: Chạy verify**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~TranslationParityTests` → PASS.
Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → 0 Warning / 0 Error.

- [ ] **Step 4: Commit**

```bash
git add src/RouterBalancing.Core/Localization/Translations.cs router-balancing/Components/Pages/SettingsPanel.razor
git commit -m "feat: add transient retry settings UI with i18n"
```

---

### Task 5: RetryState.TransientRetries

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/RetryState.cs`
- Test: `router balancing test/Engine/RetryStateTests.cs`

**Interfaces:**
- Consumes: không có.
- Produces: `public int RetryState.TransientRetries { get; set; }` — Task 6 đọc/ghi.

- [ ] **Step 1: Viết failing test** (append vào `RetryStateTests`)

```csharp
[Fact]
public void TransientRetries_DefaultsZero_AndIsSettablePerRequest()
{
    var state = new RetryState();
    Assert.Equal(0, state.TransientRetries);

    state.TransientRetries++;
    Assert.Equal(1, state.TransientRetries);
}
```

- [ ] **Step 2: Chạy verify FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~RetryStateTests`
Expected: FAIL compile.

- [ ] **Step 3: Implement property**

Thêm vào `RetryState` (XML doc: số retry transient đã dùng, per-request, sống qua park — spec transient-retry §2.2):

```csharp
public int TransientRetries { get; set; }
```

- [ ] **Step 4: Chạy verify PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~RetryStateTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Engine/RetryState.cs "router balancing test/Engine/RetryStateTests.cs"
git commit -m "feat: add TransientRetries counter to RetryState"
```

---

### Task 6: Dispatcher nhánh retry + baseline test pin

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` (primary ctor dòng 15–22; nhánh mới sau journal ~dòng 257, trước comment `// Retryable (429/408/5xx)...` dòng 259; `LogAttemptFail` ~dòng 550)
- Test: `router balancing test/Engine/DispatcherLoopTests.cs` (field `_settings`, ctor, `StartAsync` dòng 116–127, helper `Resp504`, test mới)
- Modify (pin baseline, **KHÔNG sửa nội dung khác**): `router balancing test/Server/ApiMonitorIntegrationTests.cs`, `router balancing test/Server/ProxyRetryIntegrationTests.cs`, `router balancing test/Server/TraceIntegrationTests.cs` (ctor — 1 dòng `_settings.Set(SettingsKeys.TransientMaxRetries, 0);` + comment lý do baseline)

**Interfaces:**
- Consumes: `RetryClassifier.IsTransient(int?)` (Task 2), `BackoffPolicy.Delay(int, int)` (Task 1), `RetryState.TransientRetries` (Task 5), `IAppSettingsService.TransientMaxRetries/TransientBackoffBaseMs` (Task 3).
- Produces: dispatcher retry behavior; ctor `DispatcherLoop(..., ILogService log, ITraceFeed trace, IAppSettingsService settings)` — mọi nơi construct (chỉ `DispatcherLoopTests.StartAsync`) phải cập nhật.

- [ ] **Step 1: Cập nhật harness test — settings + baseline pin**

Trong `DispatcherLoopTests`:
- Field `private readonly AppSettingsService _settings;` — init trong ctor sau `DbInitializer.Initialize` (cần DB đã migrate): `_settings = new AppSettingsService(_db.CreateFactory());`; Dispose thêm `_settings.Dispose();`. Thêm `using RouterBalancing.Core.Settings;`.
- `StartAsync`: thêm `_settings.Set(SettingsKeys.TransientMaxRetries, 0); // baseline opt-out — test transient riêng Set lại (đọc tại mỗi quyết định retry)` TRƯỚC khi construct loop; ctor call mới: `new DispatcherLoop(_queue, executions ?? _executions, resolver, selector, handler, log, _trace, _settings)`.
- Helper mới (pattern `Resp429` dòng 283): `private static HttpResponseMessage Resp504(string body = """{"error":{"message":"gateway timeout"}}""") => new(HttpStatusCode.GatewayTimeout) { Content = new StringContent(body, Encoding.UTF8, "application/json") };`

Trong 3 file integration (ApiMonitor/ProxyRetry/Trace): sau dòng `_settings = new AppSettingsService(factory);` thêm `_settings.Set(SettingsKeys.TransientMaxRetries, 0);` + comment `// baseline opt-out — test transient riêng của slice transient-retry opt-in`. Thêm `using RouterBalancing.Core.Settings;` nếu thiếu.

- [ ] **Step 2: Chạy full suite verify baseline xanh**

Run: `dotnet test "router balancing test/router balancing test.csproj"`
Expected: PASS — chứng minh pin đủ chỗ, chưa có behavior change.

- [ ] **Step 3: Viết failing tests cho nhánh retry** (append vào `DispatcherLoopTests`)

```csharp
[Fact]
public async Task Serve_When504Once_RetriesSameAccountThenSucceeds()
{
    var pid = SeedProvider("p1");
    var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
    var selector = new CountingSelector(new ModelSelector(_executions));
    var calls = 0;
    var upstream = new ScriptedUpstream(_ =>
        Interlocked.Increment(ref calls) == 1 ? Resp504() : Sse());
    var log = new CapturingLog();
    await StartAsync(resolver, selector, upstream, log);
    // Set SAU StartAsync — StartAsync pin baseline 0; dispatcher đọc setting tại mỗi quyết định retry
    _settings.Set(SettingsKeys.TransientMaxRetries, 5);
    _settings.Set(SettingsKeys.TransientBackoffBaseMs, 250);

    var request = Req("req00001");
    _queue.Enqueue(request);
    var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.IsType<DispatchOutcome.Handled>(outcome);
    Assert.Equal(2, request.Retry.Attempts);
    Assert.Equal(2, upstream.Calls);
    Assert.Equal(1, request.Retry.TransientRetries);
    Assert.Contains(log.Infos, m => m.Contains("chờ") && m.Contains("retry (1/5)"));
    Assert.False(_executions.Contains("req00001"));
}

[Fact]
public async Task Serve_When429_RotatesAccountWithoutSameAccountRetry()
{
    var pid = SeedProvider("p1", accounts: 2);
    var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
    var selector = new CountingSelector(new ModelSelector(_executions));
    // 429 chỉ cho TK a1 — a2 OK; factory nhận (provider, apiKey) để xác nhận rotate
    var upstream = new WalkUpstream((_, apiKey, _, _) =>
        Task.FromResult(apiKey == "sk-p1-a1" ? Resp429() : Sse()));
    var log = new CapturingLog();
    await StartAsync(resolver, selector, upstream, log);
    _settings.Set(SettingsKeys.TransientMaxRetries, 5);
    _settings.Set(SettingsKeys.TransientBackoffBaseMs, 250);

    var request = Req("req00001");
    _queue.Enqueue(request);
    var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.IsType<DispatchOutcome.Handled>(outcome);
    Assert.Equal(2, upstream.Calls.Count);
    Assert.Equal("sk-p1-a1", upstream.Calls[0].Key);
    Assert.Equal("sk-p1-a2", upstream.Calls[1].Key);   // rotate ngay, không retry cùng TK
    Assert.Equal(0, request.Retry.TransientRetries);
    Assert.DoesNotContain(log.Infos, m => m.Contains("retry"));
    Assert.Contains(log.Warns, m => m.Contains("chuyển TK kế"));
}

[Fact]
public async Task Serve_WhenTransientRetriesDisabled_PassthroughWithoutRetry()
{
    var pid = SeedProvider("p1");
    var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
    var selector = new CountingSelector(new ModelSelector(_executions));
    var upstream = new ScriptedUpstream(_ => Resp504());
    var log = new CapturingLog();
    await StartAsync(resolver, selector, upstream, log);
    _settings.Set(SettingsKeys.TransientMaxRetries, 0);   // 0 = tắt (back-compat §3.3)
    _settings.Set(SettingsKeys.TransientBackoffBaseMs, 250);

    var request = Req("req00001");
    _queue.Enqueue(request);
    var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

    var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome);
    Assert.Equal(504, passthrough.Status);
    Assert.Equal(1, upstream.Calls);          // đúng 1 call — không retry
    Assert.Equal(0, request.Retry.TransientRetries);
}
```

Lưu ý: `Set` SAU `StartAsync` nhưng TRƯỚC khi enqueue — dispatcher đọc setting tại mỗi quyết định retry (read-at-decision-time).

- [ ] **Step 4: Chạy verify FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~Serve_When504Once|FullyQualifiedName~Serve_When429|FullyQualifiedName~Serve_WhenTransientRetriesDisabled"`
Expected: FAIL — nhánh retry chưa có: test 504-once fail (Attempts=1, Passthrough thay vì Handled).

- [ ] **Step 5: Implement nhánh retry trong `DispatcherLoop`**

1. Primary ctor: thêm `IAppSettingsService settings` làm tham số cuối + `using RouterBalancing.Core.Settings;`.
2. `LogAttemptFail`: thêm tham số cuối `LogSeverity severity = LogSeverity.Warning`, dùng `Severity = severity` thay vì hardcode; cập nhật XML doc (`severity` — Info cho dòng retry chờ, Warning cho các caller fail khác).
3. Nhánh retry — chèn SAU toán tử gán `request.Retry.LastFailure = ...` (kết thúc ~dòng 257), TRƯỚC comment `// Retryable (429/408/5xx) dispatcher gán cấp Account` (dòng 259):

```csharp
// Retry transient cùng (provider, model, account) trước khi failover (spec transient-retry §3.2)
if (outcome is DispatchOutcome.Retryable rt
    && RetryClassifier.IsTransient(rt.Status)
    && request.Retry.TransientRetries < settings.TransientMaxRetries)
{
    request.Retry.TransientRetries++;
    var wait = BackoffPolicy.Delay(request.Retry.TransientRetries, settings.TransientBackoffBaseMs);
    LogAttemptFail(request, candidate, accountId, attemptBudget,
        $"chờ {wait.TotalMilliseconds:0}ms retry ({request.Retry.TransientRetries}/{settings.TransientMaxRetries})",
        LogSeverity.Info);
    try
    {
        await Task.Delay(wait, request.Context.RequestAborted);
    }
    catch (OperationCanceledException)
    {
        // Client abort trong delay — mirror nhánh abort hiện có: trả slot, báo Aborted
        executions.Exit(request.Id);
        request.Completion.TrySetResult(new DispatchOutcome.Aborted());
        return;
    }
    continue; // vòng serve — publish Attempt start mới (attemptNo++), CÙNG route
}
```

Slot vẫn giữ trong lúc chờ (không Exit trước delay) — request khác thấy Full → park bình thường.

- [ ] **Step 6: Chạy verify PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~Serve_When504Once|FullyQualifiedName~Serve_When429|FullyQualifiedName~Serve_WhenTransientRetriesDisabled"`
Expected: PASS.

- [ ] **Step 7: Chạy full suite verify không regression**

Run: `dotnet test "router balancing test/router balancing test.csproj"`
Expected: PASS 0 failed — caller cũ `LogAttemptFail` giữ Warning nên assert Warns cũ không đổi.

- [ ] **Step 8: Commit**

```bash
git add src/RouterBalancing.Core/Engine/DispatcherLoop.cs "router balancing test/Engine/DispatcherLoopTests.cs" "router balancing test/Server/ApiMonitorIntegrationTests.cs" "router balancing test/Server/ProxyRetryIntegrationTests.cs" "router balancing test/Server/TraceIntegrationTests.cs"
git commit -m "feat: retry transient upstream errors with backoff in dispatcher"
```

---

### Task 7: Lifecycle edge cases (unit)

**Files:**
- Test: `router balancing test/Engine/DispatcherLoopTests.cs` (test mới; fix code trong `DispatcherLoop.cs` nếu test đỏ — staging kèm file fix nếu có)

**Interfaces:**
- Consumes: toàn bộ behavior + harness Task 6.
- Produces: không có interface mới — phủ spec §6.1 còn lại; trace "attempt events lặp cùng route" (§6.2) cover ở mức unit với harness `_trace.Published` sẵn có thay vì integration.

- [ ] **Step 1: Viết 6 tests** (append vào `DispatcherLoopTests`; nếu impl Task 6 đúng, các test này PASS ngay — chúng là regression pin; nếu FAIL → fix `DispatcherLoop.cs` trong task này)

```csharp
[Fact]
public async Task Serve_WhenNetworkError_RetriesSameAccount()
{
    var pid = SeedProvider("p1");
    var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
    var selector = new CountingSelector(new ModelSelector(_executions));
    var calls = 0;
    var upstream = new WalkUpstream((_, _, _, _) =>
        Interlocked.Increment(ref calls) == 1
            ? throw new HttpRequestException("connection refused")
            : Task.FromResult(Sse()));   // pattern dòng 1102 hiện có
    var log = new CapturingLog();
    await StartAsync(resolver, selector, upstream, log);
    _settings.Set(SettingsKeys.TransientMaxRetries, 5);   // Set SAU StartAsync (baseline pin 0)
    _settings.Set(SettingsKeys.TransientBackoffBaseMs, 250);

    var request = Req("req00001");
    _queue.Enqueue(request);
    var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.IsType<DispatchOutcome.Handled>(outcome);
    Assert.Equal(2, request.Retry.Attempts);
    Assert.Equal(1, request.Retry.TransientRetries);
    Assert.All(upstream.Calls, c => Assert.Equal("sk-p1-a1", c.Key)); // cùng TK
}

[Fact]
public async Task Serve_WhenFatal401_RotatesWithoutSameAccountRetry()
{
    var pid = SeedProvider("p1", accounts: 2);
    var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
    var selector = new CountingSelector(new ModelSelector(_executions));
    var upstream = new WalkUpstream((_, apiKey, _, _) =>
        Task.FromResult(apiKey == "sk-p1-a1" ? Resp401() : Sse()));
    var log = new CapturingLog();
    await StartAsync(resolver, selector, upstream, log);
    _settings.Set(SettingsKeys.TransientMaxRetries, 5);   // Set SAU StartAsync (baseline pin 0)
    _settings.Set(SettingsKeys.TransientBackoffBaseMs, 250);

    var request = Req("req00001");
    _queue.Enqueue(request);
    var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

    Assert.IsType<DispatchOutcome.Handled>(outcome);
    Assert.Equal(0, request.Retry.TransientRetries);        // 401 Fatal — không transient-retry
    Assert.Equal("sk-p1-a2", upstream.Calls[1].Key);        // rotate TK kế
}

[Fact]
public async Task Serve_When504Persists_ExhaustsBudgetThenRotatesAndPassesThrough()
{
    var pid = SeedProvider("p1", accounts: 2);
    var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
    var selector = new CountingSelector(new ModelSelector(_executions));
    var upstream = new WalkUpstream((_, _, _, _) => Task.FromResult(Resp504()));
    await StartAsync(resolver, selector, upstream, new CapturingLog());
    _settings.Set(SettingsKeys.TransientMaxRetries, 2);     // Set SAU StartAsync — ngân sách 2 cho TOÀN request
    _settings.Set(SettingsKeys.TransientBackoffBaseMs, 250);

    var request = Req("req00001");
    _queue.Enqueue(request);
    var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

    // a1: attempt 1 + 2 retry = 3 call; hết budget → rotate a2: 1 call, không retry tiếp → exhaustion
    var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome);
    Assert.Equal(504, passthrough.Status);
    Assert.Equal(4, upstream.Calls.Count);
    Assert.Equal(2, request.Retry.TransientRetries);
    Assert.Equal(3, upstream.Calls.Count(c => c.Key == "sk-p1-a1"));
    Assert.Equal(1, upstream.Calls.Count(c => c.Key == "sk-p1-a2"));
}

[Fact]
public async Task Serve_WhenClientAbortsDuringBackoff_ExitsSlotAndCompletesAborted()
{
    var pid = SeedProvider("p1");
    var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
    var selector = new CountingSelector(new ModelSelector(_executions));
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    var upstream = new ScriptedUpstream(_ =>
    {
        if (Interlocked.Increment(ref calls) == 1)
        {
            entered.TrySetResult();
            return Resp504();
        }
        return Sse();
    });
    await StartAsync(resolver, selector, upstream, new CapturingLog());
    _settings.Set(SettingsKeys.TransientMaxRetries, 5);   // Set SAU StartAsync (baseline pin 0)
    _settings.Set(SettingsKeys.TransientBackoffBaseMs, 2000); // delay đủ dài để abort chắc chắn nằm trong window

    using var abort = new CancellationTokenSource();
    var request = Req("req00001");
    request.Context.RequestAborted = abort.Token;
    _queue.Enqueue(request);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));   // call#1 xong, dispatcher vào delay 2s
    await Task.Delay(150);                                   // journal xong, đang trong Task.Delay
    abort.Cancel();

    var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert.IsType<DispatchOutcome.Aborted>(outcome);
    Assert.False(_executions.Contains("req00001"));          // slot đã trả — không rò
}

[Fact]
public async Task Serve_WhenWaitingBackoff_HoldsSlotAndParksOtherRequest()
{
    var pid = SeedProvider("p1", maxConcurrent: 1);          // 1 slot duy nhất
    var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
    var selector = new CountingSelector(new ModelSelector(_executions));
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var calls = 0;
    var upstream = new ScriptedUpstream(_ =>
    {
        if (Interlocked.Increment(ref calls) == 1)
        {
            entered.TrySetResult();
            return Resp504();
        }
        return Sse();
    });
    var log = new CapturingLog();
    await StartAsync(resolver, selector, upstream, log);
    _settings.Set(SettingsKeys.TransientMaxRetries, 3);   // Set SAU StartAsync (baseline pin 0)
    _settings.Set(SettingsKeys.TransientBackoffBaseMs, 2000);

    var r1 = Req("req00001");
    _queue.Enqueue(r1);
    await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));   // r1 fail 504, chuẩn bị delay
    await Task.Delay(100);
    var r2 = Req("req00002");
    _queue.Enqueue(r2);

    await Task.Delay(300);                                   // r1 đang trong delay — slot vẫn giữ
    Assert.False(_executions.Contains("req00002"));
    Assert.False(r2.Completion.Task.IsCompleted);            // r2 park, không 503

    Assert.IsType<DispatchOutcome.Handled>(await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    Assert.IsType<DispatchOutcome.Handled>(await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    Assert.Equal(0, _executions.GetInFlight(pid));
}

[Fact]
public async Task Serve_WhenRetryOccurs_PublishesAttemptPairsWithSameRoute()
{
    var pid = SeedProvider("p1");
    var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
    var selector = new CountingSelector(new ModelSelector(_executions));
    var calls = 0;
    var upstream = new ScriptedUpstream(_ =>
        Interlocked.Increment(ref calls) == 1 ? Resp504() : Sse());
    await StartAsync(resolver, selector, upstream, new CapturingLog());
    _settings.Set(SettingsKeys.TransientMaxRetries, 5);   // Set SAU StartAsync (baseline pin 0)
    _settings.Set(SettingsKeys.TransientBackoffBaseMs, 250);

    var events = new List<TraceEvent>();
    _trace.Published += e => events.Add(e);

    var request = Req("req00001");
    _queue.Enqueue(request);
    Assert.IsType<DispatchOutcome.Handled>(
        await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));

    // DispatchStarted + 2 cặp (start, done) — retry là attempt thật, route lặp (§3.2)
    var attempts = events.Where(e => e.Stage == TraceStage.Attempt).ToList();
    Assert.Equal(4, attempts.Count);
    Assert.Equal(1, attempts[0].Attempt); Assert.False(attempts[0].AttemptDone);
    Assert.Equal(1, attempts[1].Attempt); Assert.True(attempts[1].AttemptDone);
    Assert.Equal(2, attempts[2].Attempt); Assert.False(attempts[2].AttemptDone);
    Assert.Equal(2, attempts[3].Attempt); Assert.True(attempts[3].AttemptDone);
    var routes = attempts.Select(a => a.Route).Distinct().ToList();
    Assert.Single(routes);                                     // CÙNG combo/provider/account
}
```

- [ ] **Step 2: Chạy verify**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~DispatcherLoopTests`
Expected: PASS — nếu test nào FAIL, fix tối thiểu trong `DispatcherLoop.cs` rồi chạy lại (giữ hành vi spec §3.2).

- [ ] **Step 3: Chạy full suite**

Run: `dotnet test "router balancing test/router balancing test.csproj"`
Expected: PASS 0 failed.

- [ ] **Step 4: Commit** (kèm file fix nếu có)

```bash
git add "router balancing test/Engine/DispatcherLoopTests.cs"
git commit -m "test: cover transient retry lifecycle edge cases in dispatcher"
```

---

### Task 8: Integration tests (TestServer + fake upstream)

**Files:**
- Create: `router balancing test/Server/ProxyTransientRetryIntegrationTests.cs`

**Interfaces:**
- Consumes: harness pattern của `ApiMonitorIntegrationTests` (feed/store đăng ký TRƯỚC `ProxyApp.ConfigureServices`, `_store.Find(id)`, `X-Request-Id`); settings keys (Task 3).
- Produces: không — verification slice cuối.

- [ ] **Step 1: Viết file test mới** — mirror TOÀN BỘ harness của `ApiMonitorIntegrationTests` (cùng file nguồn cho ctor/Dispose/`SeedProvider`/`StartAsync` feed+store dòng 136–164/`ScriptedUpstream` `Func<Provider, HttpResponseMessage>`/`Sse()`/`ChatBody`/`WaitUntilAsync`/`_store.Find(id)`): thêm field `_store`, helper `Resp504(string body = """{"error":{"message":"gateway timeout"}}""")` (pattern `ProxyRetryIntegrationTests`). Test:

```csharp
[Fact]
public async Task TransientRetry_504TwiceThenOk_Returns200AndMonitorDoneWith200()
{
    _settings.Set(SettingsKeys.TransientMaxRetries, 5);
    _settings.Set(SettingsKeys.TransientBackoffBaseMs, 250);
    SeedProvider(maxConcurrent: 4, "m1");
    var calls = 0;
    var upstream = new ScriptedUpstream(_ =>
        Interlocked.Increment(ref calls) <= 2 ? Resp504() : Sse());
    var client = await StartAsync(upstream);

    var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    Assert.Equal(3, calls);                                    // 504×2 retry + attempt 3 OK
    var id = response.Headers.GetValues("X-Request-Id").Single();
    await WaitUntilAsync(() => _store.Find(id) is { State: ApiCallState.Done },
        "request 504→504→200 phải chốt Done");
    var record = _store.Find(id)!;
    Assert.Equal(200, record.Status);                          // upsert — attempt cuối thắng
    Assert.True(record.Success);
}

[Fact]
public async Task TransientRetry_504Forever_PassesThroughFinal504Intact()
{
    _settings.Set(SettingsKeys.TransientMaxRetries, 2);        // ngắn để test nhanh
    _settings.Set(SettingsKeys.TransientBackoffBaseMs, 250);
    SeedProvider(maxConcurrent: 4, "m1");
    var upstream = new ScriptedUpstream(_ => Resp504("""{"error":{"message":"gateway timeout"}}"""));
    var client = await StartAsync(upstream);

    var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

    // Hết ngân sách → exhaustion passthrough nguyên response cuối (hành vi cũ giữ nguyên §3.4)
    Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
    Assert.Contains("gateway timeout", await response.Content.ReadAsStringAsync());
}
```

Lưu ý: `Resp504` là helper mới của file (ApiMonitor harness chưa có); `Sse()` dùng lại của harness mirror.

- [ ] **Step 2: Chạy verify PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~ProxyTransientRetryIntegrationTests`
Expected: PASS (2 tests).

- [ ] **Step 3: Chạy full suite**

Run: `dotnet test "router balancing test/router balancing test.csproj"`
Expected: PASS 0 failed.

- [ ] **Step 4: Commit**

```bash
git add "router balancing test/Server/ProxyTransientRetryIntegrationTests.cs"
git commit -m "test: add transient retry integration tests"
```

---

### Task 9: e2e mock + smoke script

**Files:**
- Modify: `scripts/mock-upstream.mjs`
- Modify: `scripts/e2e-3a.sh`

**Interfaces:**
- Consumes: pattern `/__config {"failFirst":N}` hiện có.
- Produces: `/__config {"failFirst":N,"failStatus":S}` — S mặc định 429 (giữ hành vi cũ); section e2e mới trong `e2e-3a.sh`.

- [ ] **Step 1: Mở rộng mock — configurable failStatus**

- Thêm `let failStatus = 429;` cạnh `failFirst`; `/__config` parse thêm `failStatus` (`Number(JSON.parse(body).failStatus ?? 429)`).
- Trong nhánh fail: `res.writeHead(failStatus, ...)` — header `retry-after: 5` chỉ khi `failStatus === 429` (504 không có header này); body JSON error message đổi theo status (vd `"gateway timeout"` khi 504). Cập nhật comment đầu file.

- [ ] **Step 2: Thêm section transient vào `e2e-3a.sh`** (trước khối `if [[ $FAIL -eq 0 ]]`)

```bash
# --- Transient backoff (spec transient-retry §6.3): 504×2 đầu rồi 200, retry cùng TK ---
MOCK="${MOCK:-http://127.0.0.1:9999}"
if curl -sf -X POST "$MOCK/__config" -H 'Content-Type: application/json' \
    -d '{"failFirst":2,"failStatus":504}' >/dev/null; then
  START=$SECONDS
  check "504 x2 dau -> retry backoff, 200" 200 \
    -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json' \
    -d "{\"model\":\"$MODEL_ID\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}"
  expect_body '[DONE]' "transient retry SSE ket thuc [DONE]"
  # base 1000ms: 2 lần retry ~1s+2s ≥ 3s — xác nhận CÓ chờ backoff, không failover ngay
  if (( SECONDS - START >= 3 )); then
    echo "PASS - backoff cho >=3s truoc khi thanh cong"
  else
    echo "FAIL - backoff qua nhanh ($(( SECONDS - START ))s, mong >=3s)"
    FAIL=1
  fi
  curl -sf -X POST "$MOCK/__config" -H 'Content-Type: application/json' \
    -d '{"failFirst":0,"failStatus":429}' >/dev/null || true   # reset mock
else
  echo "FAIL - khong cau hinh duoc mock (mock-upstream chay tai $MOCK?)"
  FAIL=1
fi
```

- [ ] **Step 3: Chạy verify (tay — cần app đang chạy)**

Prerequisites như header e2e-3a.sh (app + mock). Chạy: `node scripts/mock-upstream.mjs 9999 &` rồi `bash scripts/e2e-3a.sh`.
Expected: ALL PASS — gồm cả checks mới. Nếu chưa chạy được app, ghi chú trong handoff để user chạy xác nhận.

- [ ] **Step 4: Commit**

```bash
git add scripts/mock-upstream.mjs scripts/e2e-3a.sh
git commit -m "test: extend e2e mock and smoke script for transient backoff"
```

---

## Gates cuối slice (chạy sau Task 9, không commit thêm)

- [ ] `dotnet test "router balancing test/router balancing test.csproj"` → 0 failed.
- [ ] `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → 0 Warning / 0 Error.
- [ ] `bash scripts/e2e-3a.sh` (app + mock đang chạy) → ALL PASS.
- [ ] `git status` → các thay đổi v1-responses / live-trace-markers còn nguyên trạng thái uncommitted; 9 commit của slice đứng riêng trên nhánh.
