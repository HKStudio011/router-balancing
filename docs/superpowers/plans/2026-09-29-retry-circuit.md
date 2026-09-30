# Plan: Retry, Circuit & Watchdog (Slice 3C)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thêm failover walk khi lỗi retryable, circuit breaker per-model (`ManualRetry` + watchdog probe) và floor `Retry-After` vào pipeline queue-first 3A+3B — data layer.

**Architecture:** Dispatcher `ServeAsync` thành vòng walk: mỗi candidate thử 1 lần, `Retryable` → `Exit` slot + advance candidate kế; hết list → `RecordFailure` từng model đã thử rồi passthrough response cuối / 502. `ModelHealthStore` (singleton, 1 lock) mở fuse khi đủ `MaxRetry` exhaustion; endpoint gate request mới 503; `ModelHealthWatchdog` (hosted) probe chat 1 token để đóng fuse.

**Tech Stack:** .NET 10 / ASP.NET Core minimal API, xUnit, TestServer, `TimeProvider` (BCL), không package mới.

- **Spec:** `docs/superpowers/specs/2026-09-29-retry-circuit-design.md` (approved `f266ac3`, amend `9a1e4eb`) — mọi review đối chiếu spec theo section.
- **Điều kiện đầu:** master `@9a1e4eb` — **273 passed / 0 failed**.
- **Branch thực thi:** `feat/retry-circuit-3c` (SDD: implementer + reviewer mỗi task).
- **Kết thúc:** 9 tasks, **336 tests**, e2e 3A vẫn ALL PASS + e2e 3C ALL PASS. KHÔNG push.

## Mục tiêu & phạm vi

Thêm **failover walk** (lỗi retryable → candidate kế, không requeue-vì-lỗi, không chờ backoff), **circuit per-model** (exhaustion +1 → đủ `MaxRetry` → `ManualRetry`; gate request mới 503; walk skip candidate chết), **watchdog probe** (chat `max_tokens:1` → 2xx đóng fuse) và **floor `Retry-After`** cho `nextProbeAt`.

Ngoài phạm vi (không đụng): UI [Retry now]/panel health (slice sau), account weighting (3D), Anthropic `/v1/responses` (3E), chờ backoff trong request (đã chốt Phương án 1), hủy request cũ khi fuse mở.

## Ràng buộc toàn cục (áp dụng từng task)

1. **Gate mỗi task:** chạy `dotnet test "router balancing test/router balancing test.csproj" --nologo` phải xanh đúng số gate của task trong bảng ladder. **Đóng app MAUI đang mở trước khi test.**
2. **Compile:** `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo` → 0 Warning / 0 Error ở cuối mỗi task. KHÔNG build `.slnx` (NETSDK1082 pre-existing).
3. **KHÔNG thêm settings key mới, KHÔNG thêm i18n key** — dùng `MaxRetry` (default 3) + `WatchdogIntervalSec` (default 60) sẵn có. Parity i18n giữ **208/208**: `Select-String -Path "src/RouterBalancing.Core/Localization/Translations.cs" -Pattern '\["'` → EN (dòng < 232) = 208, VI (≥ 232) = 208.
4. Comment "why" tiếng Việt; XML doc `///` mọi public/protected member; identifier + commit message tiếng Anh conventional; test doubles là nested private trong file test; error client-facing tiếng Anh (message 503 mới = `The model '{model}' is temporarily unavailable`, literal, không i18n); log tiếng Việt; KHÔNG log body/messages/API key.
5. Test name mô tả hành vi (`Loop_WhenFirstCandidate429_AdvancesToSecondProviderAndCompletesHandled`).
6. Đúng 1 commit/task, message ghi sẵn ở cuối task. KHÔNG push.
7. **TDD mọi task có test mới**: viết test → chạy thấy FAIL (CS0246/hành vi) → viết implementation → PASS → full suite → commit. Ngoại lệ: **T8 (e2e script)** không sinh test `dotnet test` mới — gate = full suite giữ nguyên **333** + e2e tay; **T9 (verification)** — test mong đợi PASS ngay, FAIL = bug ở task trước → sửa đúng chỗ đó, không nõn assertion.
8. Log/lỗi luôn bọc try/catch inline theo pattern I2 đã có: **outcome phải luôn tới `TrySetResult`** (log ném SQLite không được chặn); store cam kết "không ném" (SafeLog nội bộ) nhưng dispatcher vẫn bọc các chỗ gọi trực tiếp log.

## Ladder test (arithmetic thống nhất toàn plan)

| Task | Gate sau task | Delta | Ghi chú |
|---|---|---|---|
| — | 273 | base | amend spec `9a1e4eb`, 273/0 đã verify |
| T1 RetryClassifier + RetryAfterParser | **291** | +18 | classifier 2 theory × 6 row = 12 + parser 6 |
| T2 Outcome + RetryState | **294** | +3 | |
| T3 ModelHealthStore | **306** | +12 | |
| T4 Handler classification | **309** | +3 | 2 test amended tại chỗ (không đổi số) |
| T5 Dispatcher walk | **317** | +8 | + DI store/TimeProvider |
| T6 Gate + endpoint + integration | **323** | +6 | |
| T7 Watchdog + probe recovery | **333** | +10 | 9 unit + 1 integration |
| T8 e2e scripts | **333** | +0 | e2e tay ngoài `dotnet test` |
| T9 Final Gates | **333** | 0 | verification |

Cuối mỗi task chạy **full suite** (không filter) trước khi commit.

## Kiến trúc — các chốt kỹ thuật (spec §2–§5 + plan)

1. **Walk sống trong `ServeAsync`** (fire-and-forget từ `TryDispatchOnceAsync` — không block loop): mỗi attempt `ForwardAsync` → **`Exit` slot** → switch outcome. `Handled`(2xx) → `RecordSuccess(model)`; `Passthrough/Error/Cancelled/Aborted` → complete ngay (không record); `Retryable` → `MarkTried(pair)` + `LastRetryable` → filter remaining (chưa tried ∪ chưa ManualRetry) → rỗng → `CompleteExhaustion`; còn lại → select + `TryEnter` → fail = capacity corner.
2. **Capacity corner = re-enqueue (park 3B)**, KHÔNG phải requeue-vì-lỗi: request đã `Take` được đưa vào lại queue chờ `Exited`/`Changed`; check `RequestAborted` trước, sau `Enqueue` re-check (race callback đã lỡ `TryRemove` — gỡ lại bằng `TryRemove` + `Cancelled`); `Enqueue` false → log Error + `Error(500)` defensive. Không vòng lặp nóng: dispatch lại chỉ xét candidate chưa thử (failed-set trên `ProxyRequest.Retry`); hết candidate chưa thử mà `HasTried` → `CompleteExhaustion`.
3. **`TryDispatchOnceAsync`**: resolve → filter bỏ ManualRetry → rỗng: `HasTried` ? `CompleteExhaustion` : **503** `The model '{request.Model}' is temporarily unavailable` (dùng chuỗi client gửi; không log mới) → `selector.TrySelectAsync(success with { Candidates = remaining })` → null/TryEnter fail → park (giữ 3B) → `Take` → `ServeAsync(taken, candidate, remaining, mode)`.
4. **`CompleteExhaustion`** (dùng 2 nơi, log TRƯỚC `TrySetResult` — I2): foreach `TriedModels` distinct → `RecordFailure(modelId, LastRetryable.RetryAfter)`; log.Error `Request {id} thất bại sau {n} candidate — chuyển phản hồi cuối về client`; outcome = `LastRetryable.Status != null ? Passthrough(status, ct, body, FormatRetryAfter(RetryAfter)) : Error(502, "Upstream provider request failed", "server_error", null, null)`.
5. **Handler `ForwardAsync`**: 2xx → stream `Handled` + Info (giữ nguyên); lỗi → buffer `ReadAsByteArrayAsync` → `RetryClassifier.IsRetryable` ? `Retryable(status, ct, body, RetryAfterParser.Parse(response.Headers.RetryAfter, UtcNow))` (KHÔNG ghi, KHÔNG Info — dispatcher ghi Warn/Err) : `Passthrough(status, ct, body, RetryAfter thô)` + Info giữ nguyên (parity quen sát 3A); network catch → `Retryable(null, null, [], null)` + giữ log Error; no-key `Error(503)` giữ nguyên; client abort propagate.
6. **Store API (amend §2.1)**: `IsManualRetry` / `RecordSuccess` / `RecordFailure(modelId, retryAfter?)` / `GetManualRetryModels()` → `ManualRetryModel` / `RecordProbeFailure(modelId, retryAfter?)` → `ProbeFailureResult`. State: entry chỉ tồn tại khi `consecutiveFailures ≥ 1`; `FuseOpen = consecutiveFailures ≥ MaxRetry` (MaxRetry đọc per-call từ settings); fuse mở → `NextProbeAt = now` floor `retryAfter`; probe fail → `NextProbeAt = now + max(60s×attemptsMade, retryAfter)`; `attemptsMade ≥ MaxRetry` → `NextProbeAt = null` (hết lượt). Store **tự log 4 event §5** (mở fuse Warn / recover Info / probe fail Warn / hết lượt Warn) qua SafeLog; `RecordSuccess` chỉ log recover khi vừa từ `FuseOpen` về.
7. **`RetryState` trên `ProxyRequest`**: `HashSet<(long ProviderId, string ModelId)>` + `LastRetryable` — không lock (1 logical owner = dispatcher; happens-before qua queue lock: ghi trước `Enqueue`, đọc sau `Peek`/`Take`). Key = cặp (provider, model): cùng model 2 provider vẫn failover được; `RecordFailure` theo distinct `ModelId` (Quyết định #4: +1/exhaustion).
8. **Gate endpoint** (sau `PrepareAsync`, trước `Enqueue`): `IsManualRetry(prepared.ModelId)` → log.Warn `Từ chối request mới: model '{id}' đang ManualRetry` (không dấu chấm) + 503 §4. Endpoint thêm nhánh `Passthrough` (status + content-type + body + copy `RetryAfterHeader`) và `Retryable` defensive (log.Error + 500).
9. **Watchdog**: `BackgroundService` loop = `Task.Delay(WatchdogIntervalSec)` thật TRƯỚC → `ProbeDueAsync(ct)` (**public — unit test gọi trực tiếp, không fake timer**); `ProbeDueAsync` quét `GetManualRetryModels()` có `NextProbeAt ≤ now` → resolve → `Candidates[0]` → `ResolveFirstEnabledKey` → body `{model, messages:[ping], max_tokens:1, stream:false}` (Dictionary serialize để giữ key snake_case) → 2xx `RecordSuccess`, còn lại/network/resolve-fail/no-key → `RecordProbeFailure`. `TimeProvider` inject cho cả store và watchdog (lịch probe), test set giờ chủ động — không `Thread.Sleep`.
10. **DI** (`ProxyApp.ConfigureServices`): `AddSingleton(TimeProvider.System)` + `AddSingleton<IModelHealthStore, ModelHealthStore>` (T5) + `AddSingleton<ModelHealthWatchdog>()` + `AddHostedService(sp => sp.GetRequiredService<ModelHealthWatchdog>())` (T7 — 1 instance duy nhất, test resolve thẳng được). Test register `IAppSettingsService`/`ILogService`/stub upstream SAU `ConfigureServices` → last wins (giữ pattern 3B).
11. **Race đã chấp nhận (document, không fix):** client ngắt giữa `Exit` và bước kế → outcome `Aborted`/`Cancelled` (idempotent TCS); re-enqueue reset `Sequence`/`EnqueuedAt` (elapsed restart) — accepted.

## Cấu trúc file

**Tạo mới (`src/RouterBalancing.Core/Engine/`):** `RetryClassifier.cs`, `RetryAfterParser.cs` (T1); `RetryState.cs` (T2); `IModelHealthStore.cs`, `ModelHealthStore.cs` (T3); `ModelHealthWatchdog.cs` (T7).

**Sửa:** `DispatchOutcome.cs` + `ProxyRequest.cs` (T2); `ChatCompletionsHandler.cs` + `DispatcherLoop.cs` (convert Retryable→Passthrough) + `Server/ProxyApp.cs` (nhánh Passthrough) (T4); `DispatcherLoop.cs` (walk) + `Server/ProxyApp.cs` (DI store/TimeProvider) (T5); `Server/ProxyApp.cs` (gate + nhánh Retryable defensive) (T6); `Server/ProxyApp.cs` (watchdog DI) (T7).

**Test mới (`router balancing test/`):** `Engine/RetryClassifierTests.cs`, `Engine/RetryAfterParserTests.cs` (T1); `Engine/RetryStateTests.cs` (T2); `Engine/ModelHealthStoreTests.cs` (T3); `Engine/ModelHealthWatchdogTests.cs` (T7); `Server/ProxyRetryIntegrationTests.cs` (T6, +1 test ở T7). Sửa: `Engine/ChatCompletionsHandlerTests.cs` (T4), `Engine/DispatcherLoopTests.cs` (T5).

**e2e:** `scripts/mock-upstream.mjs` (mở rộng failure mode, T8); `scripts/e2e-3c.sh` (T8, mới).

---

## Task 1 — RetryClassifier + RetryAfterParser (TDD)

**Files (create):**
- `src/RouterBalancing.Core/Engine/RetryClassifier.cs`
- `src/RouterBalancing.Core/Engine/RetryAfterParser.cs`
- `router balancing test/Engine/RetryClassifierTests.cs`
- `router balancing test/Engine/RetryAfterParserTests.cs`

**Consumes:** BCL (`System.Net.Http.Headers.RetryConditionHeaderValue`).
**Produces (T4, T7 dùng):** `RetryClassifier.IsRetryable(HttpStatusCode status) → bool`; `RetryAfterParser.Parse(string? headerValue, DateTimeOffset now) → TimeSpan?`.

### Step 1: Viết test (FAIL)

`router balancing test/Engine/RetryClassifierTests.cs`:

```csharp
using System.Net;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class RetryClassifierTests
{
    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]         // 429
    [InlineData(HttpStatusCode.RequestTimeout)]          // 408
    [InlineData(HttpStatusCode.InternalServerError)]     // 500
    [InlineData(HttpStatusCode.BadGateway)]              // 502
    [InlineData(HttpStatusCode.ServiceUnavailable)]      // 503
    [InlineData(HttpStatusCode.GatewayTimeout)]          // 504
    public void IsRetryable_RateLimitTimeoutAnd5xx_ReturnsTrue(HttpStatusCode status)
        => Assert.True(RetryClassifier.IsRetryable(status));

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]              // 400
    [InlineData(HttpStatusCode.Unauthorized)]            // 401 — key sai, không retry (§1.4)
    [InlineData(HttpStatusCode.Forbidden)]               // 403
    [InlineData(HttpStatusCode.NotFound)]                 // 404
    [InlineData(HttpStatusCode.Conflict)]                 // 409
    [InlineData(HttpStatusCode.UnprocessableEntity)]      // 422
    public void IsRetryable_ClientErrors_ReturnsFalse(HttpStatusCode status)
        => Assert.False(RetryClassifier.IsRetryable(status));
}
```

`router balancing test/Engine/RetryAfterParserTests.cs`:

```csharp
using System.Net.Http.Headers;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class RetryAfterParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Parse_DeltaSeconds_ReturnsSeconds()
        => Assert.Equal(TimeSpan.FromSeconds(120), RetryAfterParser.Parse("120", Now));

    [Fact]
    public void Parse_HttpDateFuture_ReturnsTimeUntilDate()
    {
        var header = new RetryConditionHeaderValue(Now.AddSeconds(90)).ToString();

        Assert.Equal(TimeSpan.FromSeconds(90), RetryAfterParser.Parse(header, Now));
    }

    [Fact]
    public void Parse_HttpDatePast_ReturnsZero()
    {
        var header = new RetryConditionHeaderValue(Now.AddSeconds(-60)).ToString();

        Assert.Equal(TimeSpan.Zero, RetryAfterParser.Parse(header, Now));
    }

    [Fact]
    public void Parse_AboveOneHour_ClampsTo3600()
        => Assert.Equal(TimeSpan.FromSeconds(3600), RetryAfterParser.Parse("7200", Now));

    [Fact]
    public void Parse_MissingHeader_ReturnsNull()
        => Assert.Null(RetryAfterParser.Parse(null, Now));

    [Fact]
    public void Parse_MalformedValue_ReturnsNull()
        => Assert.Null(RetryAfterParser.Parse("later", Now));
}
```

### Step 2: Chạy test — mong đợi FAIL

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "RetryClassifier|RetryAfterParser"
```

Expected: **FAIL** — `error CS0103: The name 'RetryClassifier' does not exist` (cùng `RetryAfterParser`) ở 2 file test.

### Step 3: Viết implementation

`src/RouterBalancing.Core/Engine/RetryClassifier.cs`:

```csharp
using System.Net;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Phân loại lỗi upstream retryable vs non-retryable — 1 điểm duy nhất cho handler
/// (spec 3C §3.1). Lỗi mạng/timeout KHÔNG đi qua đây: handler bắt bằng catch filter
/// sẵn có (3A) rồi trả <see cref="DispatchOutcome.Retryable"/> với <c>Status = null</c>.
/// </summary>
public static class RetryClassifier
{
    /// <summary>
    /// <see langword="true"/> với 429/408/5xx (advance candidate kế);
    /// <see langword="false"/> với 4xx còn lại — kể cả 401/403 key sai (passthrough ngay, §1.4).
    /// Chỉ gọi khi đã là lỗi (không phân loại 2xx/3xx).
    /// </summary>
    public static bool IsRetryable(HttpStatusCode status) => status switch
    {
        HttpStatusCode.TooManyRequests => true,
        HttpStatusCode.RequestTimeout => true,
        _ => (int)status is >= 500 and <= 599,
    };
}
```

`src/RouterBalancing.Core/Engine/RetryAfterParser.cs`:

```csharp
using System.Net.Http.Headers;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Parse header <c>Retry-After</c> (delta-seconds | HTTP-date), clamp 0..3600s —
/// spec 3C §3.6. Dùng duy nhất làm floor <c>nextProbeAt</c> trong store/watchdog;
/// request KHÔNG bao giờ chờ (đã chốt Phương án 1).
/// </summary>
public static class RetryAfterParser
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(3600);

    /// <param name="headerValue">Giá trị header thô; <see langword="null"/> khi thiếu hoặc không parse được.</param>
    /// <param name="now">Mốc "bây giờ" để tính HTTP-date — inject để test không lệch đồng hồ.</param>
    /// <returns>Khoảng chờ trong [0, 3600]s; date quá khứ → 0; thiếu/thông lệ → <see langword="null"/>.</returns>
    public static TimeSpan? Parse(string? headerValue, DateTimeOffset now)
    {
        if (headerValue is null)
            return null;
        if (!RetryConditionHeaderValue.TryParse(headerValue, out var value) || value is null)
            return null;
        if (value.Delta is { } delta)
            return Clamp(delta);
        if (value.Date is { } date)
            return Clamp(date - now);
        return null;
    }

    private static TimeSpan Clamp(TimeSpan value) =>
        value <= TimeSpan.Zero ? TimeSpan.Zero : value > MaxDelay ? MaxDelay : value;
}
```

### Step 4: Chạy test — mong đợi PASS

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "RetryClassifier|RetryAfterParser"
```

Expected: **18 passed / 0 failed**. Sau đó full suite:

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **291 passed / 0 failed**.

### Step 5: Commit

```bash
git add src/RouterBalancing.Core/Engine/RetryClassifier.cs src/RouterBalancing.Core/Engine/RetryAfterParser.cs "router balancing test/Engine/RetryClassifierTests.cs" "router balancing test/Engine/RetryAfterParserTests.cs"
git commit -m "feat: add retry classifier and retry after parser"
```

---

## Task 2 — DispatchOutcome.Retryable/Passthrough + RetryState (TDD)

**Files:**
- **Edit** `src/RouterBalancing.Core/Engine/DispatchOutcome.cs` — thêm 2 record
- **Edit** `src/RouterBalancing.Core/Engine/ProxyRequest.cs` — thêm property `Retry`
- **Create** `src/RouterBalancing.Core/Engine/RetryState.cs`
- **Create** `router balancing test/Engine/RetryStateTests.cs`

**Consumes:** `DispatchOutcome` (T1-đã-có).
**Produces (T4, T5 dùng):** `DispatchOutcome.Retryable`, `DispatchOutcome.Passthrough`, `ProxyRequest.Retry` (`RetryState`).

### Step 1: Viết test (FAIL)

`router balancing test/Engine/RetryStateTests.cs`:

```csharp
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class RetryStateTests
{
    [Fact]
    public void RetryState_OnNewRequest_HasNothingTriedAndNoLastRetryable()
    {
        var state = new RetryState();

        Assert.False(state.HasTried);
        Assert.Equal(0, state.TriedCount);
        Assert.Empty(state.TriedModels);
        Assert.Null(state.LastRetryable);
        Assert.False(state.IsTried(1, "m1"));
    }

    [Fact]
    public void MarkTried_SameModelOnTwoProviders_CountsTwoTriesButOneDistinctModel()
    {
        var state = new RetryState();

        state.MarkTried(1, "m1");
        state.MarkTried(2, "m1");

        Assert.True(state.HasTried);
        Assert.Equal(2, state.TriedCount);
        // Distinct theo model — RecordFailure +1/exhaustion theo model (Quyết định #4),
        // không phải theo lần thử
        Assert.Equal(new[] { "m1" }, state.TriedModels);
    }

    [Fact]
    public void IsTried_MarksOnlyTheExactProviderModelPair()
    {
        var state = new RetryState();
        state.MarkTried(1, "m1");

        Assert.True(state.IsTried(1, "m1"));
        Assert.False(state.IsTried(1, "m2"));
        Assert.False(state.IsTried(2, "m1"));
    }
}
```

### Step 2: Chạy test — mong đợi FAIL

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "RetryStateTests"
```

Expected: **FAIL** — `error CS0246: The name 'RetryState' does not exist`.

### Step 3: Viết implementation

`DispatchOutcome.cs` — thêm 2 record vào abstract record (giữ nguyên 4 record cũ):

```csharp
    /// <summary>
    /// Lỗi retryable (429/408/5xx/lỗi mạng) — tín hiệu NỘI BỘ, chỉ dispatcher nhìn thấy;
    /// luôn được convert trước khi outcome về endpoint (spec 3C §2.2).
    /// </summary>
    /// <param name="Status"><see langword="null"/> = lỗi mạng (không có HTTP response nào).</param>
    /// <param name="ContentType">Content-Type upstream trả (<see langword="null"/> khi lỗi mạng).</param>
    /// <param name="Body">Body đã buffer — response lỗi nhỏ, chưa commit (rỗng khi lỗi mạng).</param>
    /// <param name="RetryAfter"><c>Retry-After</c> đã parse — floor <c>nextProbeAt</c> khi exhaustion (§3.6).</param>
    public sealed record Retryable(int? Status, string? ContentType, byte[] Body, TimeSpan? RetryAfter)
        : DispatchOutcome;

    /// <summary>
    /// Response cần endpoint ghi NGUYÊN status + content-type + body (passthrough byte —
    /// exhaustion §3.3 và 4xx non-retryable, quan sát client y hệt 3A).
    /// </summary>
    /// <param name="RetryAfterHeader">Giá trị thô <c>Retry-After</c> copy lại cho client (null khi không có).</param>
    public sealed record Passthrough(int Status, string? ContentType, byte[] Body, string? RetryAfterHeader)
        : DispatchOutcome;
```

`ProxyRequest.cs` — thêm (sau property `Completion`):

```csharp
    /// <summary>Cặp (provider, model) đã thử + lỗi retryable gần nhất — sống qua park/capacity corner re-enqueue (spec 3C §3.2).</summary>
    public RetryState Retry { get; } = new();
```

`src/RouterBalancing.Core/Engine/RetryState.cs`:

```csharp
namespace RouterBalancing.Core.Engine;

/// <summary>
/// Trạng thái retry của MỘT request: tập (providerId, modelId) đã thử + lỗi retryable
/// gần nhất (spec 3C §3.2). Sống trên <see cref="ProxyRequest"/> nên giữ nguyên qua park
/// và capacity-corner re-enqueue — dispatch lại chỉ xét candidate chưa thử, không có
/// vòng lặp nóng. Không lock: 1 logical owner (dispatcher serve task); happens-before
/// qua queue lock (ghi trước khi re-Enqueue, đọc sau Peek/Take).
/// </summary>
public sealed class RetryState
{
    private readonly HashSet<(long ProviderId, string ModelId)> _tried = [];

    /// <summary>Lỗi retryable gần nhất của request — exhaustion convert thành Passthrough/Error(502).</summary>
    public DispatchOutcome.Retryable? LastRetryable { get; set; }

    /// <summary>Đã thử ít nhất 1 candidate chưa (phân biệt 503 walk-rỗng vs exhaustion).</summary>
    public bool HasTried => _tried.Count > 0;

    /// <summary>Số lần thử — cùng 1 model trên 2 provider tính 2 (2 cặp khác nhau).</summary>
    public int TriedCount => _tried.Count;

    /// <summary>Distinct model id đã thử — <c>RecordFailure</c> theo model (Quyết định #4: +1/exhaustion).</summary>
    public IReadOnlyList<string> TriedModels =>
        _tried.Select(p => p.ModelId).Distinct().ToList();

    /// <summary>Ghi nhận đã thử 1 candidate.</summary>
    /// <param name="providerId">Id provider của candidate.</param>
    /// <param name="modelId">Model id của candidate.</param>
    public void MarkTried(long providerId, string modelId) => _tried.Add((providerId, modelId));

    /// <summary>Cặp (provider, model) này đã thử chưa — filter candidate khi walk/dispatch lại.</summary>
    /// <param name="providerId">Id provider của candidate.</param>
    /// <param name="modelId">Model id của candidate.</param>
    public bool IsTried(long providerId, string modelId) => _tried.Contains((providerId, modelId));
}
```

### Step 4: Chạy test — mong đợi PASS

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "RetryStateTests"
```

Expected: **3 passed / 0 failed**. Sau đó full suite:

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **294 passed / 0 failed**.

### Step 5: Commit

```bash
git add src/RouterBalancing.Core/Engine/DispatchOutcome.cs src/RouterBalancing.Core/Engine/ProxyRequest.cs src/RouterBalancing.Core/Engine/RetryState.cs "router balancing test/Engine/RetryStateTests.cs"
git commit -m "feat: add retry outcome records and retry state"
```

---

## Task 3 — ModelHealthStore (TDD)

**Files (create):**
- `src/RouterBalancing.Core/Engine/IModelHealthStore.cs` (interface + 2 record)
- `src/RouterBalancing.Core/Engine/ModelHealthStore.cs`
- `router balancing test/Engine/ModelHealthStoreTests.cs`

**Consumes:** `IAppSettingsService.MaxRetry` (default 3), `ILogService`, `TimeProvider` (BCL).
**Produces (T5 dispatcher, T6 gate, T7 watchdog dùng):** `IModelHealthStore` singleton — `IsManualRetry` / `RecordSuccess` / `RecordFailure` / `GetManualRetryModels` / `RecordProbeFailure` (API amend `9a1e4eb` §2.1).

### Semantics chốt (spec §3.4 + §5 — test bám đây)

- Entry chỉ tồn tại khi `consecutiveFailures ≥ 1`; **fuse mở** ⇔ `consecutiveFailures ≥ MaxRetry` (đọc per-call từ settings).
- Mở fuse (transition Healthy→ManualRetry) → `NextProbeAt = now + (retryAfter ?? 0)`, log Warn **đúng 1 lần**; các `RecordFailure` kế khi fuse đã mở **không đè** lịch probe.
- `RecordSuccess` → xóa entry; chỉ log Info recover khi vừa từ fuse mở về.
- `RecordProbeFailure`: model không tồn tại/hơi còn healthy → `(0, null)` side-effect-free; ngược lại `attemptsMade+1`, hết `MaxRetry` → `NextProbeAt = null` (Warn hết lượt), chưa hết → `now + max(60s×attemptsMade, retryAfter)` (Warn `lần k/max` + `secs`).
- 4 log §5 sinh trong store qua **SafeLog** (nuốt exception có chủ đích — I2); log ghi **ngoài lock** nhưng chỉ 1 thread nào thấy transition mới được log → không race.

### Step 1: Viết test (FAIL)

`router balancing test/Engine/ModelHealthStoreTests.cs`:

```csharp
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

public class ModelHealthStoreTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly CapturingLog _log = new();
    private readonly FakeTime _time = new();

    public ModelHealthStoreTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _settings = new AppSettingsService(_db.CreateFactory(), new DpapiSecretProtector());
    }

    public void Dispose()
    {
        _settings.Dispose();
        _db.Dispose();
    }

    private ModelHealthStore CreateSut() => new(_settings, _log, _time);

    // Mở fuse bằng đúng MaxRetry lần RecordFailure — retryAfter (nếu có) chỉ gửi ở lần
    // cuối cùng (đúng transition: floor lịch probe khi MỞ fuse, không gửi ở các lần dưới ngưỡng)
    private void OpenFuse(ModelHealthStore sut, string modelId, TimeSpan? retryAfter = null)
    {
        var max = _settings.MaxRetry;
        for (var i = 1; i <= max; i++)
            sut.RecordFailure(modelId, i == max ? retryAfter : null);
    }

    [Fact]
    public void RecordFailure_UnknownModel_KeepsHealthy()
    {
        var sut = CreateSut();

        sut.RecordFailure("m1");

        // Dưới ngưỡng MaxRetry=3 → chưa mở fuse, cũng chưa hiện trong list ManualRetry
        Assert.False(sut.IsManualRetry("m1"));
        Assert.Empty(sut.GetManualRetryModels());
    }

    [Fact]
    public void RecordFailure_BelowMaxRetry_StaysHealthyAndSilent()
    {
        var sut = CreateSut();
        sut.RecordFailure("m1");
        sut.RecordFailure("m1");

        Assert.False(sut.IsManualRetry("m1"));
        // Chưa transition → không log Warn nào (chỉ 4 event §5 được ghi)
        Assert.Empty(_log.Warns);
    }

    [Fact]
    public void RecordFailure_AtMaxRetry_OpensFuseWithWarnAndDueNow()
    {
        var sut = CreateSut();
        sut.RecordFailure("m1");
        sut.RecordFailure("m1");
        sut.RecordFailure("m1");

        Assert.True(sut.IsManualRetry("m1"));
        var model = Assert.Single(sut.GetManualRetryModels());
        Assert.Equal("m1", model.ModelId);
        // Vừa mở fuse, chưa probe lần nào — lịch probe = ngay bây giờ
        Assert.Equal(0, model.AttemptsMade);
        Assert.Equal(_time.GetUtcNow(), model.NextProbeAt);
        var warn = Assert.Single(_log.Warns);
        Assert.Contains("chuyển sang ManualRetry", warn);
        Assert.Contains("3 lỗi liên tiếp", warn);
    }

    [Fact]
    public void RecordFailure_WhileFuseOpen_DoesNotLogWarnTwice()
    {
        var sut = CreateSut();
        OpenFuse(sut, "m1");
        sut.RecordFailure("m1"); // lỗi thêm khi fuse đã mở — KHÔNG log Warn lần 2

        Assert.True(sut.IsManualRetry("m1"));
        Assert.Single(_log.Warns);
    }

    [Fact]
    public void RecordSuccess_WhileManualRetry_LogsRecoverInfoAndClearsState()
    {
        var sut = CreateSut();
        OpenFuse(sut, "m1");

        sut.RecordSuccess("m1");

        Assert.False(sut.IsManualRetry("m1"));
        Assert.Empty(sut.GetManualRetryModels());
        var info = Assert.Single(_log.Infos);
        Assert.Contains("phục hồi", info);

        // Entry đã xóa hoàn toàn — lần lỗi kế bắt đầu từ 0, không phải "vừa recover"
        sut.RecordFailure("m1");
        Assert.False(sut.IsManualRetry("m1"));
        Assert.Single(_log.Infos); // không log recover lần 2
    }

    [Fact]
    public void RecordSuccess_BelowMaxRetry_ResetsCounterSilently()
    {
        var sut = CreateSut();
        sut.RecordFailure("m1");
        sut.RecordFailure("m1");

        sut.RecordSuccess("m1");

        // Chưa từng mở fuse → không có Info recover; không có Warn nào
        Assert.Empty(_log.Infos);
        Assert.Empty(_log.Warns);
        // Counter đã reset — 2 lần lỗi nữa (tổng kể cả trước success = 4 nếu không reset)
        // vẫn dưới ngưỡng 3 của chuỗi liên tiếp mới
        sut.RecordFailure("m1");
        sut.RecordFailure("m1");
        Assert.False(sut.IsManualRetry("m1"));
    }

    [Fact]
    public void RecordFailure_WithRetryAfter_FloorsNextProbeAtWhenFuseOpens()
    {
        _settings.Set(SettingsKeys.MaxRetry, 1);
        var sut = CreateSut();

        sut.RecordFailure("m1", TimeSpan.FromSeconds(300));

        var model = Assert.Single(sut.GetManualRetryModels());
        // Floor Retry-After: lịch probe = now + 300s chứ không phải now (spec §3.6)
        Assert.Equal(_time.GetUtcNow().AddSeconds(300), model.NextProbeAt);
    }

    [Fact]
    public void RecordProbeFailure_BackoffDoublesEachAttempt()
    {
        var sut = CreateSut();
        OpenFuse(sut, "m1");

        var first = sut.RecordProbeFailure("m1");
        Assert.Equal(1, first.AttemptsMade);
        Assert.Equal(_time.GetUtcNow().AddSeconds(60), first.NextProbeAt);

        var second = sut.RecordProbeFailure("m1");
        Assert.Equal(2, second.AttemptsMade);
        Assert.Equal(_time.GetUtcNow().AddSeconds(120), second.NextProbeAt);

        // 3 Warn = 1 mở fuse (từ OpenFuse) + 2 probe fail — mỗi lần 1 dòng, không lặp
        Assert.Equal(3, _log.Warns.Count);
        Assert.Contains(_log.Warns,
            w => w.Contains("Probe model 'm1'") && w.Contains("lần 1/3") && w.Contains("60s"));
        Assert.Contains(_log.Warns, w => w.Contains("lần 2/3") && w.Contains("120s"));
    }

    [Fact]
    public void RecordProbeFailure_RetryAfterLargerThanBackoff_FloorsToRetryAfter()
    {
        var sut = CreateSut();
        OpenFuse(sut, "m1");

        var result = sut.RecordProbeFailure("m1", TimeSpan.FromSeconds(300));

        Assert.Equal(1, result.AttemptsMade);
        // max(60s×1, 300s) = 300s — Retry-After thắng backoff (spec §3.6)
        Assert.Equal(_time.GetUtcNow().AddSeconds(300), result.NextProbeAt);
    }

    [Fact]
    public void RecordProbeFailure_AtMaxRetry_ReturnsNullAndLogsExhausted()
    {
        _settings.Set(SettingsKeys.MaxRetry, 1);
        var sut = CreateSut();
        sut.RecordFailure("m1"); // MaxRetry=1 → mở fuse ngay lần đầu

        var result = sut.RecordProbeFailure("m1");

        Assert.Equal(1, result.AttemptsMade);
        Assert.Null(result.NextProbeAt); // hết lượt — watchdog không probe nữa
        Assert.Contains(_log.Warns, w => w.Contains("hết lượt probe tự động"));
    }

    [Fact]
    public void RecordFailure_Parallel400Times_LogsFuseOpenExactlyOnce()
    {
        var sut = CreateSut();

        Parallel.For(0, 400, _ => sut.RecordFailure("m1"));

        Assert.True(sut.IsManualRetry("m1"));
        // Transition log dưới lock check-then-act: đúng 1 thread thấy Healthy→ManualRetry
        Assert.Equal(1, _log.Warns.Count(w => w.Contains("chuyển sang ManualRetry")));
    }

    [Fact]
    public void RecordProbeFailure_UnknownModel_ReturnsZeroAndKeepsHealthy()
    {
        var sut = CreateSut();

        var result = sut.RecordProbeFailure("ghost");

        Assert.Equal(0, result.AttemptsMade);
        Assert.Null(result.NextProbeAt);
        Assert.False(sut.IsManualRetry("ghost"));
        Assert.Empty(_log.Warns);
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;
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
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }
}
```

### Step 2: Chạy test — mong đợi FAIL

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "ModelHealthStoreTests"
```

Expected: **FAIL** — `error CS0246: The name 'ModelHealthStore' does not exist`.

### Step 3: Viết implementation

`src/RouterBalancing.Core/Engine/IModelHealthStore.cs`:

```csharp
namespace RouterBalancing.Core.Engine;

/// <summary>Model đang ManualRetry — data source cho gate walk (T5) và watchdog (T7).</summary>
/// <param name="ModelId">Model id.</param>
/// <param name="AttemptsMade">Số probe đã tính (0 = fuse vừa mở, chưa probe).</param>
/// <param name="NextProbeAt">Lịch probe kế; <see langword="null"/> = hết lượt probe tự động.</param>
public sealed record ManualRetryModel(string ModelId, int AttemptsMade, DateTimeOffset? NextProbeAt);

/// <summary>Kết quả ghi 1 probe thất bại.</summary>
/// <param name="AttemptsMade">Số probe đã tính sau lần gọi này (0 = model không trong state mở fuse).</param>
/// <param name="NextProbeAt">Lịch probe kế; <see langword="null"/> = model không tồn tại hoặc hết lượt.</param>
public sealed record ProbeFailureResult(int AttemptsMade, DateTimeOffset? NextProbeAt);

/// <summary>
/// Circuit breaker per-model (spec 3C §3.4): <c>Healthy</c> (không entry) |
/// <c>ManualRetry(consecutiveFailures, attemptsMade, nextProbeAt)</c>. Singleton, 1 lock
/// cho dict — thread-safe. Store TỰ ghi 4 log state-transition của §5 qua SafeLog
/// (log không được phá request path — I2). Đồng hồ inject qua <see cref="TimeProvider"/>.
/// </summary>
public interface IModelHealthStore
{
    /// <summary>Model đang mở fuse không — chưa từng lỗi = <see langword="false"/>.</summary>
    /// <param name="modelId">Model cần kiểm tra.</param>
    bool IsManualRetry(string modelId);

    /// <summary>2xx — xóa entry + hủy lịch probe; nếu vừa từ ManualRetry → log Info recover.</summary>
    /// <param name="modelId">Model vừa thành công.</param>
    void RecordSuccess(string modelId);

    /// <summary>
    /// Một request exhaustion retryable — +1 <c>consecutiveFailures</c>; đạt <c>MaxRetry</c> →
    /// mở fuse (<c>NextProbeAt = now</c> floor <paramref name="retryAfter"/>), log Warn chuyển
    /// trạng thái đúng 1 lần; fuse đang mở → chỉ cộng, không đè lịch probe.
    /// </summary>
    /// <param name="modelId">Model cần cộng lỗi.</param>
    /// <param name="retryAfter"><c>Retry-After</c> đã parse — floor lịch probe khi mở fuse (§3.6).</param>
    void RecordFailure(string modelId, TimeSpan? retryAfter = null);

    /// <summary>Danh sách model đang ManualRetry — watchdog quét theo lịch (§3.5).</summary>
    IReadOnlyList<ManualRetryModel> GetManualRetryModels();

    /// <summary>
    /// Ghi 1 probe thất bại: <c>attemptsMade+1</c>; đạt <c>MaxRetry</c> → <c>NextProbeAt = null</c>
    /// (Warn hết lượt); ngược lại <c>now + max(60s×attemptsMade, retryAfter)</c> (Warn backoff).
    /// </summary>
    /// <param name="modelId">Model probe fail.</param>
    /// <param name="retryAfter"><c>Retry-After</c> của probe (null khi không có / lỗi mạng).</param>
    /// <returns>Lượt probe + lịch kế; model không tồn tại hoặc chưa mở fuse → (0, <see langword="null"/>) side-effect-free.</returns>
    ProbeFailureResult RecordProbeFailure(string modelId, TimeSpan? retryAfter = null);
}
```

`src/RouterBalancing.Core/Engine/ModelHealthStore.cs`:

```csharp
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Settings;

namespace RouterBalancing.Core.Engine;

/// <inheritdoc cref="IModelHealthStore"/>
public sealed class ModelHealthStore(
    IAppSettingsService settings, ILogService log, TimeProvider time) : IModelHealthStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();

    /// <inheritdoc/>
    public bool IsManualRetry(string modelId)
    {
        // MaxRetry đọc per-call ngoài lock — setting đổi có hiệu lực ngay, tránh lock nesting
        var maxRetry = settings.MaxRetry;
        lock (_lock)
            return _entries.TryGetValue(modelId, out var entry)
                && entry.ConsecutiveFailures >= maxRetry;
    }

    /// <inheritdoc/>
    public void RecordSuccess(string modelId)
    {
        var maxRetry = settings.MaxRetry;
        bool wasOpen;
        lock (_lock)
        {
            if (!_entries.TryGetValue(modelId, out var entry))
                return;
            wasOpen = entry.ConsecutiveFailures >= maxRetry;
            _entries.Remove(modelId);
        }
        // Recover là transition hiếm — log ngoài lock, chỉ khi vừa từ fuse mở về (§5)
        if (wasOpen)
            SafeLog(() => log.Info($"Model '{modelId}' phục hồi — trở lại Healthy.", LogCategory.App));
    }

    /// <inheritdoc/>
    public void RecordFailure(string modelId, TimeSpan? retryAfter = null)
    {
        var maxRetry = settings.MaxRetry;
        var justOpened = false;
        var failures = 0;
        lock (_lock)
        {
            if (!_entries.TryGetValue(modelId, out var entry))
                _entries[modelId] = entry = new Entry();

            // wasOpen tính TRƯỚC khi cộng — chỉ lần vượt ngưỡng mới là transition (log 1 lần)
            var wasOpen = entry.ConsecutiveFailures >= maxRetry;
            entry.ConsecutiveFailures++;
            failures = entry.ConsecutiveFailures;
            if (!wasOpen && failures >= maxRetry)
            {
                // Mở fuse: floor Retry-After (§3.6); các RecordFailure sau không đè lịch probe
                // watchdog đang dùng
                entry.NextProbeAt = time.GetUtcNow() + (retryAfter ?? TimeSpan.Zero);
                justOpened = true;
            }
        }
        if (justOpened)
            SafeLog(() => log.Warn(
                $"Model '{modelId}' chuyển sang ManualRetry sau {failures} lỗi liên tiếp.",
                LogCategory.App));
    }

    /// <inheritdoc/>
    public IReadOnlyList<ManualRetryModel> GetManualRetryModels()
    {
        var maxRetry = settings.MaxRetry;
        lock (_lock)
            return _entries
                .Where(kv => kv.Value.ConsecutiveFailures >= maxRetry)
                .Select(kv => new ManualRetryModel(kv.Key, kv.Value.AttemptsMade, kv.Value.NextProbeAt))
                .ToList();
    }

    /// <inheritdoc/>
    public ProbeFailureResult RecordProbeFailure(string modelId, TimeSpan? retryAfter = null)
    {
        var maxRetry = settings.MaxRetry;
        ProbeFailureResult result;
        string? message = null;
        lock (_lock)
        {
            if (!_entries.TryGetValue(modelId, out var entry)
                || entry.ConsecutiveFailures < maxRetry)
            {
                // Chưa mở fuse / model lạ — probe không nên gọi hàm này: side-effect-free (§2.1 amend)
                result = new ProbeFailureResult(0, null);
            }
            else
            {
                entry.AttemptsMade++;
                if (entry.AttemptsMade >= maxRetry)
                {
                    entry.NextProbeAt = null;
                    message = $"Model '{modelId}' hết lượt probe tự động — chờ Retry now (slice UI)";
                }
                else
                {
                    var backoff = TimeSpan.FromSeconds(60 * entry.AttemptsMade);
                    var wait = retryAfter is { } ra && ra > backoff ? ra : backoff;
                    entry.NextProbeAt = time.GetUtcNow() + wait;
                    message =
                        $"Probe model '{modelId}' thất bại (lần {entry.AttemptsMade}/{maxRetry}) — " +
                        $"thử lại sau {(long)wait.TotalSeconds}s";
                }
                result = new ProbeFailureResult(entry.AttemptsMade, entry.NextProbeAt);
            }
        }
        if (message is not null)
            SafeLog(() => log.Warn(message, LogCategory.App));
        return result;
    }

    /// <summary>Ghi log bọc nuốt — store cam kết không ném ra caller; SQLite lỗi không được phá outcome (I2).</summary>
    private static void SafeLog(Action write)
    {
        try
        {
            write();
        }
        catch
        {
            // Nuốt chủ đích: backend log (SQLite) không được làm hỏng request path/circuit state
        }
    }

    private sealed class Entry
    {
        public int ConsecutiveFailures;
        public int AttemptsMade;
        public DateTimeOffset? NextProbeAt;
    }
}
```

### Step 4: Chạy test — mong đợi PASS

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "ModelHealthStoreTests"
```

Expected: **12 passed / 0 failed**. Sau đó full suite:

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **306 passed / 0 failed**.

### Step 5: Commit

```bash
git add src/RouterBalancing.Core/Engine/IModelHealthStore.cs src/RouterBalancing.Core/Engine/ModelHealthStore.cs "router balancing test/Engine/ModelHealthStoreTests.cs"
git commit -m "feat: add model health store with circuit breaker state"
```

---

## Task 4 — Handler classification + dispatcher convert + endpoint Passthrough

**⚠️ Task này gồm CẢ conversion path** (không chỉ handler): nếu chỉ đổi handler thì test integration có sẵn `ProxyAppChatIntegrationTests.Chat_WhenUpstream429_PassesStatusAndBodyThrough` (client phải thấy 429) sẽ RED — dispatcher phải convert `Retryable → Passthrough/502` ngay ở T4 (T5 sẽ thay nhánh này bằng walk + `RecordExhaustion`).

**Files:**
- **Edit** `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs` — `ForwardAsync`
- **Edit** `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` — `ServeAsync` convert + helper
- **Edit** `src/RouterBalancing.Core/Server/ProxyApp.cs` — nhánh `Passthrough`
- **Edit** `router balancing test/Engine/ChatCompletionsHandlerTests.cs` — 2 test sửa tại chỗ + 3 test mới

**Consumes:** `RetryClassifier`/`RetryAfterParser` (T1), `DispatchOutcome.Retryable/Passthrough` (T2).
**Produces (T5, T6 dùng):** handler trả `Retryable`/`Passthrough`; endpoint ghi `Passthrough`.

### Step 1: Sửa 2 test cũ + viết 3 test mới (FAIL)

**Sửa tại chỗ** trong `ChatCompletionsHandlerTests.cs` (rename + đổi assertion — 2 test này đổi hành vi):

```csharp
    [Fact]
    public async Task ForwardAsync_WhenUpstream429_ReturnsRetryableWithoutWritingResponse()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var upstreamBody = """{"error":{"message":"rate limited"}}""";
        var sut = Create(new StubUpstream(() => Upstream(429, upstreamBody)), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        // Handler KHÔNG ghi response 429 — dispatcher walk quyết định advance/passthrough (spec §2.2)
        var retryable = Assert.IsType<DispatchOutcome.Retryable>(outcome);
        Assert.Equal(429, retryable.Status);
        Assert.Equal(upstreamBody, Encoding.UTF8.GetString(retryable.Body));
        Assert.StartsWith("application/json", retryable.ContentType);
        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(200, status);
        Assert.Equal(string.Empty, body);
        // Không Info ở nhánh retryable — Warn/Err do dispatcher ghi (§5)
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstreamThrows_ReturnsNetworkRetryableAndLogsError()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var sut = Create(new ThrowingUpstream(new HttpRequestException("connection refused")), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        // Lỗi mạng = retryable Status null — 502 chỉ sinh ở exhaustion (T5 convert, spec §2.2)
        var retryable = Assert.IsType<DispatchOutcome.Retryable>(outcome);
        Assert.Null(retryable.Status);
        Assert.Null(retryable.ContentType);
        Assert.Empty(retryable.Body);
        Assert.Null(retryable.RetryAfter);
        Assert.Single(log.Errors); // giữ log Error 3A
        Assert.Empty(log.Infos);
    }
```

**Thêm 3 test mới:**

```csharp
    [Fact]
    public async Task ForwardAsync_WhenUpstream500_ReturnsRetryableWithBufferedBody()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var upstreamBody = """{"error":{"message":"internal"}}""";
        var sut = Create(new StubUpstream(() => Upstream(500, upstreamBody)), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        var retryable = Assert.IsType<DispatchOutcome.Retryable>(outcome);
        Assert.Equal(500, retryable.Status);
        Assert.Equal(upstreamBody, Encoding.UTF8.GetString(retryable.Body));
        // Response chưa commit — ctx untouched để dispatcher advance candidate kế
        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(200, status);
        Assert.Equal(string.Empty, body);
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream400_ReturnsPassthroughWithoutWritingResponse()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var upstreamBody = """{"error":{"message":"bad request"}}""";
        var sut = Create(new StubUpstream(() => Upstream(400, upstreamBody)), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        // Non-retryable — endpoint ghi (quan sát client y hệt 3A), không advance
        var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome);
        Assert.Equal(400, passthrough.Status);
        Assert.Equal(upstreamBody, Encoding.UTF8.GetString(passthrough.Body));
        Assert.Null(passthrough.RetryAfterHeader);
        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(200, status);
        Assert.Equal(string.Empty, body);
        Assert.Single(log.Infos); // Info giữ nguyên cho passthrough (parity quen sát 3A)
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream429WithRetryAfter_ParsesRetryAfterIntoRetryable()
    {
        var provider = SeedProvider();
        var response = Upstream(429, "{}");
        response.Headers.TryAddWithoutValidation("Retry-After", "30");
        var sut = Create(new StubUpstream(() => response));
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        var retryable = Assert.IsType<DispatchOutcome.Retryable>(outcome);
        // Delta-seconds parse được → floor nextProbeAt khi exhaustion (§3.6)
        Assert.Equal(TimeSpan.FromSeconds(30), retryable.RetryAfter);
    }
```

### Step 2: Chạy test — mong đợi FAIL

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "ChatCompletionsHandlerTests"
```

Expected: **11 tests — 5 failed / 6 passed** (5 test đều fail hành vi: `Assert.IsType() Failure: returned DispatchOutcome.Handled/Error thay vì Retryable/Passthrough`).

### Step 3: Viết implementation

**`ChatCompletionsHandler.cs`** — thay method `ForwardAsync` (giữ nguyên `PrepareAsync`/`WriteErrorAsync`):

```csharp
    /// <summary>
    /// Forward request đã resolve lên upstream. 2xx → stream + <see cref="DispatchOutcome.Handled"/>;
    /// lỗi retryable (429/408/5xx/network) → buffer + <see cref="DispatchOutcome.Retryable"/>
    /// (KHÔNG ghi response — dispatcher walk, spec §2.2); 4xx còn lại →
    /// <see cref="DispatchOutcome.Passthrough"/> (endpoint ghi, quen sát 3A); no-key → Error(503);
    /// client abort propagate.
    /// </summary>
    /// <param name="ctx">HttpContext gốc (ghi status/content-type/stream khi 2xx).</param>
    /// <param name="provider">Provider đã chọn.</param>
    /// <param name="model">Model đã chọn (log Info).</param>
    /// <param name="body">Body JSON gốc.</param>
    /// <param name="ct">Token — dùng <c>ctx.RequestAborted</c> để disconnect cắt stream.</param>
    public async Task<DispatchOutcome> ForwardAsync(HttpContext ctx, Provider provider, Model model,
        byte[] body, CancellationToken ct)
    {
        var key = ProviderKeyResolver.ResolveFirstEnabledKey(provider, protector);
        if (key is null)
        {
            log.Warn($"Provider '{provider.Name}' không có account enabled nào.", LogCategory.Request);
            return new DispatchOutcome.Error(503,
                $"No enabled API key for provider '{provider.Name}'", "server_error", null, null);
        }

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await upstream.PostChatCompletionAsync(provider, key, body, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !ctx.RequestAborted.IsCancellationRequested)
        {
            // Lỗi upstream thật → retryable Status null (502 sinh ở exhaustion — T5);
            // client tự ngắt (RequestAborted) thì propagate (hành vi 3A)
            log.Error($"Không kết nối được upstream '{provider.Name}'.", ex, LogCategory.Request);
            return new DispatchOutcome.Retryable(null, null, [], null);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                // 2xx giữ nguyên 3A/3B: stream thẳng, không buffer
                ctx.Response.StatusCode = (int)response.StatusCode;
                if (response.Content.Headers.ContentType is { } okType)
                    ctx.Response.ContentType = okType.ToString();
                await response.Content.CopyToAsync(ctx.Response.Body, ct);
                LogForwarded(provider, model, response, stopwatch);
                return new DispatchOutcome.Handled();
            }

            // Lỗi chưa commit (vừa nhận header) — buffer để dispatcher quyết định advance/passthrough
            var errorBody = await response.Content.ReadAsByteArrayAsync(ct);
            var contentType = response.Content.Headers.ContentType?.ToString();
            var retryAfterRaw = response.Headers.RetryAfter?.ToString();
            if (RetryClassifier.IsRetryable(response.StatusCode))
                return new DispatchOutcome.Retryable((int)response.StatusCode, contentType,
                    errorBody, RetryAfterParser.Parse(retryAfterRaw, DateTimeOffset.UtcNow));

            LogForwarded(provider, model, response, stopwatch);
            return new DispatchOutcome.Passthrough((int)response.StatusCode, contentType,
                errorBody, retryAfterRaw);
        }
    }

    // Tách helper để 2 nhánh (2xx/passthrough) ghi Info đúng 1 lần, không trùng chữ ký log
    private void LogForwarded(Provider provider, Model model, HttpResponseMessage response,
        Stopwatch stopwatch) =>
        log.Info(
            $"Chuyển tiếp '{model.ModelId}' → '{provider.Name}': " +
            $"HTTP {(int)response.StatusCode} trong {stopwatch.ElapsedMilliseconds}ms",
            LogCategory.Request);
```

**`DispatcherLoop.cs`** — thêm `using System.Globalization;` (nếu chưa có), trong `ServeAsync` **trước** `request.Completion.TrySetResult(outcome);` (sau `finally { executions.Exit(...) }`):

```csharp
        // Dispatcher là nơi duy nhất convert Retryable — endpoint chỉ thấy Passthrough/Error
        // (spec §2.2). T5 sẽ thay nhánh này bằng walk (advance) + RecordExhaustion.
        if (outcome is DispatchOutcome.Retryable retryable)
            outcome = ConvertRetryable(retryable);

        request.Completion.TrySetResult(outcome);
```

Thêm 2 helper private (cuối class):

```csharp
    /// <summary>
    /// Retryable → outcome endpoint ghi được: có HTTP response → Passthrough (client thấy
    /// đúng response cuối như 3A); lỗi mạng → Error 502 (exhaustion contract §3.3).
    /// </summary>
    private static DispatchOutcome ConvertRetryable(DispatchOutcome.Retryable retryable) =>
        retryable.Status is { } status
            ? new DispatchOutcome.Passthrough(status, retryable.ContentType, retryable.Body,
                FormatRetryAfter(retryable.RetryAfter))
            : new DispatchOutcome.Error(502, "Upstream provider request failed", "server_error",
                null, null);

    /// <summary>TimeSpan → chuỗi delta-seconds (InvariantCulture, ceiling) cho header Retry-After.</summary>
    private static string? FormatRetryAfter(TimeSpan? retryAfter) =>
        retryAfter is { } value
            ? ((int)Math.Ceiling(value.TotalSeconds))
                .ToString(CultureInfo.InvariantCulture)
            : null;
```

**`ProxyApp.cs`** — trong endpoint chat, thêm nhánh sau khiếu `Error` (trước nhánh `Cancelled`):

```csharp
            else if (outcome is DispatchOutcome.Passthrough passthrough)
            {
                // Ghi nguyên response cuối — byte passthrough không JSON wrap (spec 3C §3.3);
                // Retry-After copy lại cho client (§3.6)
                ctx.Response.StatusCode = passthrough.Status;
                if (passthrough.ContentType is not null)
                    ctx.Response.ContentType = passthrough.ContentType;
                if (passthrough.RetryAfterHeader is not null)
                    ctx.Response.Headers["Retry-After"] = passthrough.RetryAfterHeader;
                await ctx.Response.Body.WriteAsync(passthrough.Body, ctx.RequestAborted);
            }
```

### Step 4: Chạy test — mong đợi PASS

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "ChatCompletionsHandlerTests"
```

Expected: **11 passed / 0 failed**. Full suite (bao gồm integration 429 có sẵn phải xanh):

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **309 passed / 0 failed**.

### Step 5: Commit

```bash
git add src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs src/RouterBalancing.Core/Engine/DispatcherLoop.cs src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/Engine/ChatCompletionsHandlerTests.cs"
git commit -m "feat: classify upstream errors into retryable outcomes"
```

---

## Task 5 — Dispatcher walk + DI store/TimeProvider (TDD)

**Files:**
- **Edit** `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` — ctor + `TryDispatchOnceAsync` + `ServeAsync` (walk) + helpers
- **Edit** `src/RouterBalancing.Core/Server/ProxyApp.cs` — DI `TimeProvider.System` + `IModelHealthStore`
- **Edit** `router balancing test/Engine/DispatcherLoopTests.cs` — `StartAsync` + 8 test mới + doubles

**Consumes:** `RetryState` (T2), `IModelHealthStore` (T3), handler `Retryable` (T4).
**Produces (T6, T7 dùng):** walk failover + `CompleteExhaustion`/`RecordExhaustion`/`ExhaustionOutcome` + store singleton trong DI.

### Cấu trúc walk (chốt — test bám đây)

- `TryDispatchOnceAsync`: resolve → `FilterRemaining` (bỏ ManualRetry + đã thử) → rỗng: `TryRemove` → `HasTried ? CompleteExhaustion : Error(503 The model '{request.Model}' is tạm thời...)` (không log mới) → select/TryEnter như 3B → `ServeAsync(taken, candidate, remaining, mode)`.
- `ServeAsync` = vòng `while(true)`: ForwardAsync (try/catch y hệt 3B) → **Retryable**: `MarkTried` + `LastRetryable` → filter `next` → rỗng: `CompleteExhaustion` (**TRƯỚC** `Exit` — fuse mở trước wake Exited, chống request kế dispatch tiếp vào model vừa chết) → `Exit` → `TrySetResult` return; còn lại: `LogAdvance` → **`Exit` trước advance** → `TrySelectAsync`+`TryEnter` (OCE-when-abort → Aborted; Exception → log inline + HasStarted?Aborted:Error500 — Exit đã xong) → null → `ReenqueueForPark` return → ok: `continue` với candidate/remaining mới. **Không retry**: `Exit` → `Handled ? RecordSuccess(model)` (bọc try) → `TrySetResult` return.
- Kiểm chứng net10.0: `is not T x` + declaration **hợp lệ** (CS8780 chỉ chặn designator dưới **`or`**: `x is A or B b`). Dù vậy T5 dùng positive `if (outcome is DispatchOutcome.Retryable retryable) { ... }` rồi fallthrough nhánh không-retryable — control flow tường minh hơn.

### Step 1: Sửa test (FAIL)

`DispatcherLoopTests.cs` — **sửa tại chỗ** 4 chỗ:

1. Thêm `using RouterBalancing.Core.Settings;` (giữ các using khác).
2. Field + Dispose:

```csharp
    private AppSettingsService? _settings;
    // trong Dispose(), sau StopAsync:
    _settings?.Dispose();
```

3. Thay `StartAsync` hiện tại + thêm `NewStore`:

```csharp
    private async Task StartAsync(IComboResolver resolver, IModelSelector selector,
        IUpstreamClient upstream, CapturingLog log, ModelHealthStore? health = null)
    {
        _settings ??= new AppSettingsService(_db.CreateFactory(), _protector);
        health ??= new ModelHealthStore(_settings, log, TimeProvider.System);
        var handler = new ChatCompletionsHandler(upstream, _protector, log);
        _loop = new DispatcherLoop(_queue, _executions, resolver, selector, handler, log, health);
        await _loop.StartAsync(CancellationToken.None);
    }

    // Test pre-seed fuse cần đúng instance store mà loop sẽ dùng
    private ModelHealthStore NewStore(CapturingLog log)
    {
        _settings ??= new AppSettingsService(_db.CreateFactory(), _protector);
        return new ModelHealthStore(_settings, log, TimeProvider.System);
    }
```

4. Thêm doubles + helpers (cùng khu vực các double hiện có):

```csharp
    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(SseBody, Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Resp429(string body = """{"error":{"message":"rate limited"}}""") =>
        new(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private sealed class ScriptedUpstream(Func<Provider, HttpResponseMessage> factory) : IUpstreamClient
    {
        public int Calls;

        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            // Factory throw được (mạng giả) — ném đồng bộ, handler bắt trong try có sẵn
            return Task.FromResult(factory(provider));
        }
    }

    // Góc capacity: call#1 giữ slot tới khi Release, call#2 lỗi 429 (advance), call#3+ OK
    private sealed class CapacityCornerUpstream : IUpstreamClient
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
            var call = Interlocked.Increment(ref _calls);
            if (call == 1)
            {
                _entered.TrySetResult();
                await _release.Task;
                return Sse();
            }
            return call == 2 ? Resp429() : Sse();
        }
    }
```

**Thêm 8 test mới:**

```csharp
    [Fact]
    public async Task Loop_WhenFirstCandidate429_AdvancesToSecondProviderAndCompletesHandled()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m1"); // cùng model 2 provider — RR sort (p1, p2)
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(p => p.Name == "p1" ? Resp429() : Sse());
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(2, upstream.Calls);
        // Advance Warn nêu đúng provider/model/status/request rồi chuyển candidate kế (§5)
        Assert.Contains(log.Warns, w =>
            w.Contains("Chuyển candidate kế") && w.Contains("'p1'/'m1'")
            && w.Contains("HTTP 429") && w.Contains("req00001"));
        Assert.Contains(log.Infos, i => i.Contains("m1") && i.Contains("p2")); // chỉ Info lần thành công
        Assert.False(_executions.Contains("req00001"));
    }

    [Fact]
    public async Task Loop_WhenAllCandidates429_CompletesPassthroughWithLastResponseAndLogsExhaustion()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m1");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(p => p.Name == "p1"
            ? Resp429("""{"error":{"message":"from-p1"}}""")
            : Resp429("""{"error":{"message":"from-p2"}}"""));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Exhaustion contract: attempt cuối (p2) quyết định — passthrough nguyên response đó (§3.3)
        var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome);
        Assert.Equal(429, passthrough.Status);
        Assert.Equal("""{"error":{"message":"from-p2"}}""", Encoding.UTF8.GetString(passthrough.Body));
        Assert.Equal(2, upstream.Calls);
        Assert.Contains(log.Errors,
            e => e.Contains("req00001") && e.Contains("thất bại sau 2 candidate"));
        // Đúng 1 Warn advance (p1→p2) — hết candidate nên không log lần 2
        Assert.Single(log.Warns);
        // Handler không tự ghi response — ctx untouched cho endpoint ghi
        Assert.Equal(200, request.Context.Response.StatusCode);
        Assert.Equal(0, ((MemoryStream)request.Context.Response.Body).Length);
    }

    [Fact]
    public async Task Loop_WhenAllCandidatesFailWithNetworkError_CompletesError502()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m1");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(_ => throw new HttpRequestException("connection refused"));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Attempt cuối là mạng → 502 y như 3A, không passthrough body rỗng (§3.3)
        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(502, error.Status);
        Assert.Equal("Upstream provider request failed", error.Message);
        Assert.Equal(2, upstream.Calls);
        // 2 lỗi mạng của handler + 1 exhaustion của dispatcher
        Assert.Equal(3, log.Errors.Count);
        Assert.Contains(log.Errors, e => e.Contains("thất bại sau 2 candidate"));
    }

    [Fact]
    public async Task Loop_WhenFirstCandidate429ThenNetworkError_CompletesError502OnLastAttempt()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m1");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(p => p.Name == "p1"
            ? Resp429()
            : throw new HttpRequestException("connection refused"));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Attempt cuối (mạng) quyết định — không lấy response 429 của attempt đầu (§3.3)
        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(502, error.Status);
        Assert.Equal("Upstream provider request failed", error.Message);
        Assert.Equal(2, upstream.Calls);
        Assert.Contains(log.Warns, w => w.Contains("HTTP 429"));
    }

    [Fact]
    public async Task Loop_WhenCandidateReturns400_CompletesPassthroughWithoutAdvancing()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m1");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"bad"}}""", Encoding.UTF8, "application/json"),
        });
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // 4xx non-retryable: passthrough NGAY — không advance, không cộng counter (§1.4)
        var passthrough = Assert.IsType<DispatchOutcome.Passthrough>(outcome);
        Assert.Equal(400, passthrough.Status);
        Assert.Equal(1, upstream.Calls);
        Assert.Empty(log.Warns);
        Assert.Empty(log.Errors);
        Assert.Contains(log.Infos, i => i.Contains("HTTP 400")); // Info parity 3A
    }

    [Fact]
    public async Task Loop_WhenAllModelsInManualRetry_CompletesError503WithoutUpstreamCall()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m2");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.Fallback));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(_ => Sse());
        var log = new CapturingLog();
        var store = NewStore(log);
        // Mở fuse cả 2 model — 3 lần RecordFailure mỗi model (MaxRetry=3)
        for (var i = 0; i < 3; i++)
        {
            store.RecordFailure("m1");
            store.RecordFailure("m2");
        }
        await StartAsync(resolver, selector, upstream, log, store);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Walk rỗng ngay từ đầu (chưa thử gì) → 503, KHÔNG gọi upstream (§3.3)
        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(503, error.Status);
        Assert.Equal("The model 'm1' is temporarily unavailable", error.Message);
        Assert.Equal(0, upstream.Calls);
    }

    [Fact]
    public async Task Loop_WhenSomeModelsInManualRetry_SkipsDeadModelAndServesHealthyOne()
    {
        var p1 = SeedProvider("p1", modelId: "m1");
        var p2 = SeedProvider("p2", modelId: "m2");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.Fallback));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new ScriptedUpstream(_ => Sse());
        var log = new CapturingLog();
        var store = NewStore(log);
        for (var i = 0; i < 3; i++)
            store.RecordFailure("m1"); // chỉ m1 mở fuse
        await StartAsync(resolver, selector, upstream, log, store);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Gate walk: model chết bị loại từ đầu — không advance, không Warn "Chuyển candidate kế"
        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Equal(1, upstream.Calls);
        Assert.Contains(log.Infos, i => i.Contains("m2") && i.Contains("p2"));
        Assert.DoesNotContain(log.Warns, w => w.Contains("Chuyển candidate kế"));
    }

    [Fact]
    public async Task Loop_WhenNextCandidateHasNoCapacity_ReenqueuesAndWakesWhenSlotFrees()
    {
        var p1 = SeedProvider("p1", maxConcurrent: 1, modelId: "m1");
        var p2 = SeedProvider("p2", maxConcurrent: 1, modelId: "m1");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(p1), Candidate(p2)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new CapacityCornerUpstream();
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        // r2 enqueue trước → cursor 0 → p1, call#1 giữ trọn slot (gated)
        var r2 = Req("req00002");
        _queue.Enqueue(r2);
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        // r1 → p2 (p1 bận), call#2 → 429 → advance p1 nhưng p1 kẹt → re-enqueue (park)
        var r1 = Req("req00001");
        _queue.Enqueue(r1);
        await WaitUntilAsync(() => _queue.Contains("req00001") && upstream.Calls == 2);
        await Task.Delay(200); // chắc chắn đã park — không còn call nào chạy dở
        Assert.Equal(2, upstream.Calls);
        Assert.False(r1.Completion.Task.IsCompleted);

        // r2 xong → Exit p1 → Exited wake → r1 dispatch lại (chỉ còn p1 chưa thử) → call#3 OK
        upstream.Release();
        Assert.IsType<DispatchOutcome.Handled>(await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<DispatchOutcome.Handled>(await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, upstream.Calls);
        Assert.False(_queue.Contains("req00001"));
    }
```

### Step 2: Chạy test — mong đợi FAIL

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "DispatcherLoopTests"
```

Expected: **FAIL (compile)** — `error CS0246: The name 'ModelHealthStore' does not exist` + `CS7036` (ctor `DispatcherLoop` chưa nhận tham số `health`) trong file test.

### Step 3: Viết implementation

**`DispatcherLoop.cs`** — thêm `using System.Globalization;` (đầu file); ctor nhận thêm `IModelHealthStore health` (**tham số cuối**):

```csharp
public sealed class DispatcherLoop(
    IRequestQueue queue,
    IExecutionList executions,
    IComboResolver resolver,
    IModelSelector selector,
    ChatCompletionsHandler handler,
    ILogService log,
    IModelHealthStore health) : BackgroundService
```

**Thay `TryDispatchOnceAsync`** (giữ nguyên phần resolve-failure đầu method):

```csharp
        var success = (SelectionSuccess)selection;

        // Walk 3C: bỏ candidate model đang ManualRetry + candidate đã thử trong request này (§3.2/§3.4)
        var remaining = FilterRemaining(success.Candidates, request);
        if (remaining.Count == 0)
        {
            if (!queue.TryRemove(request.Id, out _))
                return true; // vừa bị cancel/abort gỡ — bên kia đã báo outcome rồi
            request.Completion.TrySetResult(request.Retry.HasTried
                ? CompleteExhaustion(request)
                : new DispatchOutcome.Error(503,
                    $"The model '{request.Model}' is temporarily unavailable",
                    "server_error", null, null));
            return true;
        }

        var candidate = await selector.TrySelectAsync(
            new SelectionSuccess(remaining, success.Mode), ct);
        if (candidate is null)
            return false; // park — item KHÔNG bị Take, chờ Changed|Exited

        if (!await executions.TryEnterAsync(candidate.Provider.Id, request.Id,
                candidate.Provider.Name, candidate.Model.ModelId, request.Priority,
                request.EnqueuedAt, ct))
            return false; // capacity vừa hết — park, Exited sẽ đánh thức

        if (!queue.Take(request.Id, out var taken))
        {
            // Take fail = item vừa bị huỷ/abort giữa TryEnter và Take — trả slot ngay (spec §3.3)
            executions.Exit(request.Id);
            return true;
        }

        _ = ServeAsync(taken, candidate, remaining, success.Mode);
        return true;
    }
```

**Thay `ServeAsync`** (thay toàn bộ method cũ — không còn `finally { Exit }` vì Exit theo từng nhánh):

```csharp
    /// <summary>
    /// Serve 1 request qua vòng walk: mỗi candidate 1 lần, Retryable → Exit + advance kế;
    /// hết list → RecordExhaustion + Passthrough/Error502. Fire-and-forget từ
    /// <see cref="TryDispatchOnceAsync"/> — không block dispatcher loop.
    /// </summary>
    private async Task ServeAsync(ProxyRequest request, ModelCandidate candidate,
        IReadOnlyList<ModelCandidate> remaining, ComboMode mode)
    {
        while (true)
        {
            DispatchOutcome outcome;
            try
            {
                // RequestAborted của client — disconnect giữa chừng cắt stream, không phải lỗi upstream (3A)
                outcome = await handler.ForwardAsync(request.Context, candidate.Provider,
                    candidate.Model, request.Body, request.Context.RequestAborted);
            }
            catch (OperationCanceledException) when (request.Context.RequestAborted.IsCancellationRequested)
            {
                outcome = new DispatchOutcome.Aborted(); // client ngắt giữa serve — không 502 (3A parity)
            }
            catch (Exception ex)
            {
                // Response đã commit (stream giữa chừng) → Aborted: endpoint KHÔNG append JSON 500 vào stream (I1).
                // Log bọc INLINE: nếu ném, outcome sẽ không bao giờ ghi → endpoint treo vĩnhễn (I2)
                try
                {
                    log.Error($"Lỗi dispatcher: {ex.Message}", ex, LogCategory.Request);
                }
                catch
                {
                    // Nuốt chủ đích: "log không được làm hỏng request path"
                }
                outcome = request.Context.Response.HasStarted
                    ? new DispatchOutcome.Aborted()
                    : new DispatchOutcome.Error(500, "Internal server error", "server_error", null, null);
            }

            if (outcome is DispatchOutcome.Retryable retryable)
            {
                request.Retry.MarkTried(candidate.Provider.Id, candidate.Model.ModelId);
                request.Retry.LastRetryable = retryable;

                var next = FilterRemaining(remaining, request);
                if (next.Count == 0)
                {
                    // RecordExhaustion TRƯỚC Exit — fuse phải mở trước khi Exited wake dispatch
                    // request đang queue, nếu không request kế sẽ gọi tiếp vào model vừa chết (§3.4)
                    var exhausted = CompleteExhaustion(request);
                    executions.Exit(request.Id);
                    request.Completion.TrySetResult(exhausted);
                    return;
                }

                LogAdvance(request, candidate, retryable);
                // Exit TRƯỚC khi advance — trả slot ngay, không giữ trong lúc chọn candidate kế
                executions.Exit(request.Id);

                ModelCandidate? nextCandidate;
                try
                {
                    nextCandidate = await selector.TrySelectAsync(
                        new SelectionSuccess(next, mode), request.Context.RequestAborted);
                    if (nextCandidate is not null
                        && !await executions.TryEnterAsync(nextCandidate.Provider.Id, request.Id,
                            nextCandidate.Provider.Name, nextCandidate.Model.ModelId,
                            request.Priority, request.EnqueuedAt, request.Context.RequestAborted))
                        nextCandidate = null; // capacity corner — park lại
                }
                catch (OperationCanceledException) when (request.Context.RequestAborted.IsCancellationRequested)
                {
                    // Exit đã xong trước advance — chỉ cần báo outcome (idempotent TCS)
                    request.Completion.TrySetResult(new DispatchOutcome.Aborted());
                    return;
                }
                catch (Exception ex)
                {
                    // Slot đã trả — lỗi select/enter phải thành outcome, không được nuốt (I2)
                    try
                    {
                        log.Error($"Lỗi dispatcher: {ex.Message}", ex, LogCategory.Request);
                    }
                    catch
                    {
                        // Nuốt chủ đích: "log không được làm hỏng request path"
                    }
                    request.Completion.TrySetResult(request.Context.Response.HasStarted
                        ? new DispatchOutcome.Aborted()
                        : new DispatchOutcome.Error(500, "Internal server error", "server_error", null, null));
                    return;
                }

                if (nextCandidate is null)
                {
                    ReenqueueForPark(request);
                    return;
                }

                candidate = nextCandidate;
                remaining = next;
                continue;
            }

            // Không phải Retryable: trả slot rồi complete (Handled/ Passthrough/Error/Cancelled/Aborted)
            executions.Exit(request.Id);
            if (outcome is DispatchOutcome.Handled)
            {
                try
                {
                    health.RecordSuccess(candidate.Model.ModelId); // 2xx reset counter (§3.4)
                }
                catch
                {
                    // Nuốt chủ đích: store lỗi không được chặn outcome (I2)
                }
            }
            request.Completion.TrySetResult(outcome);
            return;
        }
    }
```

**Thêm helpers** (cuối class, cạnh `CreateResolveError`):

```csharp
    /// <summary>Candidate chưa thử + model chưa ManualRetry — dùng cho dispatch đầu và mỗi bước walk (§3.2/§3.4).</summary>
    private IReadOnlyList<ModelCandidate> FilterRemaining(IReadOnlyList<ModelCandidate> candidates,
        ProxyRequest request) =>
        candidates
            .Where(c => !health.IsManualRetry(c.Model.ModelId)
                && !request.Retry.IsTried(c.Provider.Id, c.Model.ModelId))
            .ToList();

    /// <summary>Ghi failure từng distinct model đã thử rồi trả outcome exhaustion — gọi 2 nơi (§3.3/§3.4).</summary>
    private DispatchOutcome CompleteExhaustion(ProxyRequest request)
    {
        RecordExhaustion(request);
        return ExhaustionOutcome(request);
    }

    /// <summary>
    /// +1/exhaustion cho từng distinct model đã thử (Quyết định #4) + log Error exhaustion.
    /// Mọi chỗ gọi bọc try — log/store ném không được chặn TrySetResult (I2).
    /// </summary>
    private void RecordExhaustion(ProxyRequest request)
    {
        var retryAfter = request.Retry.LastRetryable?.RetryAfter;
        foreach (var modelId in request.Retry.TriedModels)
        {
            try
            {
                health.RecordFailure(modelId, retryAfter);
            }
            catch
            {
                // Nuốt chủ đích: store lỗi không được phá outcome
            }
        }
        try
        {
            log.Error(
                $"Request {request.Id} thất bại sau {request.Retry.TriedCount} candidate — " +
                "chuyển phản hồi cuối về client", LogCategory.Request);
        }
        catch
        {
            // Nuốt chủ đích: log không được phá outcome
        }
    }

    /// <summary>Exhaustion contract §3.3: attempt cuối có HTTP → Passthrough nguyên; mạng → Error 502 (y như 3A).</summary>
    private static DispatchOutcome ExhaustionOutcome(ProxyRequest request)
    {
        var last = request.Retry.LastRetryable;
        return last?.Status is { } status
            ? new DispatchOutcome.Passthrough(status, last.ContentType, last.Body,
                FormatRetryAfter(last.RetryAfter))
            : new DispatchOutcome.Error(502, "Upstream provider request failed", "server_error",
                null, null);
    }

    /// <summary>Log Warn advance failover — chỉ khi thật sự còn candidate kế (spec §5); bọc nuốt (I2).</summary>
    private void LogAdvance(ProxyRequest request, ModelCandidate failed,
        DispatchOutcome.Retryable retryable)
    {
        var reason = retryable.Status is { } status ? $"HTTP {status}" : "lỗi mạng";
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

    /// <summary>
    /// Capacity corner sau advance: re-enqueue vào queue chờ <c>Exited</c>/<c>Changed</c> (park 3B)
    /// — KHÔNG phải requeue-vì-lỗi: candidate đã thử vẫn bị exclude qua <see cref="RetryState"/> (§3.2).
    /// </summary>
    private void ReenqueueForPark(ProxyRequest request)
    {
        if (request.Context.RequestAborted.IsCancellationRequested)
        {
            request.Completion.TrySetResult(new DispatchOutcome.Aborted());
            return;
        }
        if (!queue.Enqueue(request))
        {
            // Id đã có trong queue (rare) — không được nuốt im lặng, outcome luôn phải tới endpoint
            try
            {
                log.Error($"Không enqueue lại được request {request.Id} khi chờ slot.",
                    LogCategory.Request);
            }
            catch
            {
                // Nuốt chủ đích: log không được phá outcome
            }
            request.Completion.TrySetResult(new DispatchOutcome.Error(500, "Internal server error",
                "server_error", null, null));
            return;
        }
        // Token cancel giữa check trên và Enqueue: callback Register (endpoint) đã lỡ fire khi
        // item chưa trong queue → tự gỡ lại + Cancelled (spec §3.4); TrySetResult idempotent
        if (request.Context.RequestAborted.IsCancellationRequested
            && queue.TryRemove(request.Id, out _))
        {
            request.Completion.TrySetResult(new DispatchOutcome.Cancelled());
        }
    }
```

**XÓA** helper `ConvertRetryable` (thay bằng `CompleteExhaustion`/`ExhaustionOutcome`); **GIỮ** `FormatRetryAfter` (T4 đã thêm — `ExhaustionOutcome` dùng).

**`ProxyApp.ConfigureServices`** — thêm cạnh `AddHostedService<DispatcherLoop>()`:

```csharp
        // Circuit per-model 3C: store singleton + đồng hồ system —
        // DispatcherLoop (T5), gate endpoint (T6), watchdog (T7) dùng chung 1 instance
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IModelHealthStore, ModelHealthStore>();
        builder.Services.AddHostedService<DispatcherLoop>();
```

### Step 4: Chạy test — mong đợi PASS

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "DispatcherLoopTests"
```

Expected: **14 passed / 0 failed** (6 cũ + 8 mới). Full suite:

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **317 passed / 0 failed**.

### Step 5: Commit

```bash
git add src/RouterBalancing.Core/Engine/DispatcherLoop.cs src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/Engine/DispatcherLoopTests.cs"
git commit -m "feat: implement failover walk with circuit-aware dispatch"
```

## Task 6 — Gate endpoint + Retryable defensive + integration 3C (TDD)

**Files:**
- **Edit** `src/RouterBalancing.Core/Server/ProxyApp.cs` — gate sau `PrepareAsync` + nhánh `Retryable` + lambda param `IModelHealthStore`
- **Create** `router balancing test/Server/ProxyRetryIntegrationTests.cs` — harness + 6 test

**Consumes:** `IModelHealthStore` (T3), walk `CompleteExhaustion` (T5), endpoint `Passthrough` (T4).
**Produces (T7 dùng):** gate §3.4/§4; harness integration 3C (T7 thêm 1 test vào cùng file).

### Semantics chốt (spec §2.2/§3.4/§4/§5 — test bám đây)

- Gate **SAU** `PrepareAsync`, TRƯỚC enqueue — chỉ check **exact-id** (chuỗi client gửi); combo name chưa resolve lúc này → không gate (walk filter T5 lo — §2.2).
- Log Warn đúng chuỗi §5 **không dấu chấm**: `Từ chối request mới: model '{id}' đang ManualRetry` (`LogCategory.Request`).
- 503 dùng message §4: `The model '{model}' is temporarily unavailable` — literal EN, không i18n.
- Nhánh `Retryable` defensive: dispatcher đã convert (T4/T5) — tới endpoint là bug → `log.Error` + 500.
- **2 test gate fail pre-impl nhờ assertion log**: status 503 giống hệt walk-rỗng 503 (T5) — chỉ log Warn `Từ chối request mới` phân biệt endpoint-gate vs dispatcher-walk.

### Step 1: Viết 6 integration test (FAIL)

`router balancing test/Server/ProxyRetryIntegrationTests.cs` (create — harness copy từ `ProxyQueueIntegrationTests.cs`):

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

/// <summary>
/// Integration 3C (spec §6.2): failover 429→2xx, exhaustion passthrough/502, gate 503
/// (enqueue), combo skip model ManualRetry — đi qua endpoint + dispatcher + store thật.
/// </summary>
public class ProxyRetryIntegrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly DpapiSecretProtector _protector = new();
    private readonly List<string> _messages = [];
    private WebApplication? _app;
    private HttpClient? _client;

    public ProxyRetryIntegrationTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory, new DpapiSecretProtector());
        _log = new LogService(factory);
        // Capture mọi log Write — assert theo nội dung: gate Warn §5 là dấu hiệu phân biệt
        // endpoint-gate với walk-rỗng 503 của dispatcher (status giống nhau)
        _log.LogAdded += entry => _messages.Add(entry.Message);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _log.Dispose();
        _settings.Dispose();
        _db.Dispose();
    }

    private void SeedProvider(string name, int maxConcurrent, params string[] modelIds)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            BaseUrl = "https://api.openai.com",
            // ComboResolver Finalize lọc OpenAI — thiếu là resolve trả rỗng
            Type = ProviderType.OpenAI,
            MaxConcurrent = maxConcurrent,
        };
        foreach (var id in modelIds)
            provider.Models.Add(new Model { ModelId = id, Enabled = true });
        provider.Accounts.Add(new ProviderAccount
        {
            Name = "a1",
            Enabled = true,
            ApiKeyEncrypted = _protector.Protect("sk-live"),
        });
        db.Providers.Add(provider);
        db.SaveChanges();
    }

    private long ModelKey(string modelId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        return db.Models.Single(m => m.ModelId == modelId).Id;
    }

    private void SeedCombo(string name, ComboMode mode, params (int Position, long ModelId)[] items)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var combo = new Combo { Name = name, Mode = mode };
        foreach (var (position, modelId) in items)
            combo.Items.Add(new ComboItem { Position = position, TargetModelId = modelId });
        db.Combos.Add(combo);
        db.SaveChanges();
    }

    private sealed class ScriptedUpstream(Func<Provider, HttpResponseMessage> factory) : IUpstreamClient
    {
        public int Calls;

        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            // Factory throw được (mạng giả) — ném đồng bộ, handler bắt trong try có sẵn
            return Task.FromResult(factory(provider));
        }
    }

    // Call#1 giữ tới khi Release (request 2 kịp vào queue); call kế trả 429 ngay
    private sealed class Gated429Upstream : IUpstreamClient
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
            return Resp429();
        }
    }

    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"x\":1}\n\ndata: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Resp429(
        string body = """{"error":{"message":"rate limited"}}""", string? retryAfter = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (retryAfter is not null)
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private async Task<HttpClient> StartAsync(IUpstreamClient upstream)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAppSettingsService>(_settings);
        builder.Services.AddSingleton<ILogService>(_log);
        builder.Services.AddSingleton<IDbContextFactory<RouterBalancingDbContext>>(_db.CreateFactory());

        ProxyApp.ConfigureServices(builder, _protector);
        // Đăng ký SAU ConfigureServices → wins (last registration), stub thay OpenAiUpstreamClient
        builder.Services.AddSingleton(upstream);

        var app = builder.Build();
        ProxyApp.ConfigurePipeline(app);
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
        return _client;
    }

    private static StringContent Json(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static StringContent ChatBody(string model) =>
        Json($"{{\"model\":\"{model}\",\"messages\":[{{\"role\":\"user\"}}]}}");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private async Task<string> WaitForQueuedIdAsync(string model)
    {
        var queue = _app!.Services.GetRequiredService<IRequestQueue>();
        for (var i = 0; i < 100; i++)
        {
            var hit = queue.Snapshot().FirstOrDefault(r => r.Model == model);
            if (hit is not null)
                return hit.Id;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Request '{model}' không vào queue trong 5s.");
    }

    [Fact]
    public async Task Chat_WhenFirstProvider429_SecondProviderSucceeds_ClientSeesOnly200()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(p => p.Name == "p1" ? Resp429() : Sse());
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Failover trong 1 request: client chỉ thấy 200 — 429 của p1 không lọt ra ngoài (§3.2)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data:", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, upstream.Calls);
        Assert.Contains(_messages, m =>
            m.Contains("Chuyển candidate kế") && m.Contains("HTTP 429")); // Warn §5
    }

    [Fact]
    public async Task Chat_WhenAllProviders429_PassesLastResponseThroughWithRetryAfterHeader()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(p => p.Name == "p1"
            ? Resp429("""{"error":{"message":"from-p1"}}""")
            : Resp429("""{"error":{"message":"from-p2"}}""", retryAfter: "30"));
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Exhaustion: attempt cuối (p2, cursor 0→p1 rồi advance cursor 1→p2) quyết định —
        // passthrough nguyên response đó + Retry-After parse rồi format lại cho client (§3.3/§3.6)
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("""{"error":{"message":"from-p2"}}""",
            await response.Content.ReadAsStringAsync());
        Assert.Equal("30", response.Headers.GetValues("Retry-After").Single());
        Assert.Equal(2, upstream.Calls);
    }

    [Fact]
    public async Task Chat_WhenAllProvidersFailWithNetworkError_Returns502()
    {
        SeedProvider("p1", maxConcurrent: 4, "m1");
        SeedProvider("p2", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(_ => throw new HttpRequestException("connection refused"));
        var client = await StartAsync(upstream);

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Toàn lỗi mạng → attempt cuối là mạng → 502 y như 3A (§3.3)
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var error = (await ReadJson(response)).GetProperty("error");
        Assert.Equal("Upstream provider request failed", error.GetProperty("message").GetString());
        Assert.Equal(2, upstream.Calls);
    }

    [Fact]
    public async Task Chat_WhenModelExhaustsMaxRetry_RejectsNextRequestWith503BeforeQueue()
    {
        _settings.Set(SettingsKeys.MaxRetry, 1); // 1 exhaustion = mở fuse
        SeedProvider("p1", maxConcurrent: 4, "m1");
        var upstream = new ScriptedUpstream(_ => Resp429());
        var client = await StartAsync(upstream);

        var first = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode); // passthrough attempt cuối
        Assert.Equal(1, upstream.Calls);

        var second = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Gate enqueue (§3.4/§4): 503 TRƯỚC khi vào queue — log Warn là assertion phân biệt
        // với walk-rỗng 503 của dispatcher (status/message giống hệt, không log)
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        var error = (await ReadJson(second)).GetProperty("error");
        Assert.Equal("The model 'm1' is temporarily unavailable",
            error.GetProperty("message").GetString());
        Assert.Equal(1, upstream.Calls);
        Assert.Contains(_messages, m =>
            m.Contains("Từ chối request mới") && m.Contains("'m1'"));
        Assert.Empty(_app!.Services.GetRequiredService<IRequestQueue>().Snapshot());
    }

    [Fact]
    public async Task Chat_WhenComboHasManualRetryModel_SkipsDeadModelAndServesHealthyOne()
    {
        SeedProvider("p1", maxConcurrent: 4, "mA");
        SeedProvider("p2", maxConcurrent: 4, "mB");
        SeedCombo("combo-1", ComboMode.Fallback, (0, ModelKey("mA")), (1, ModelKey("mB")));
        var upstream = new ScriptedUpstream(p => p.Name == "p1" ? Resp429() : Sse());
        var client = await StartAsync(upstream);

        // Pre-seed fuse mA qua store singleton DI — dispatcher dùng đúng instance (T5)
        var store = _app!.Services.GetRequiredService<IModelHealthStore>();
        for (var i = 0; i < _settings.MaxRetry; i++)
            store.RecordFailure("mA");

        var response = await client.PostAsync("/v1/chat/completions", ChatBody("combo-1"));

        // Walk filter loại mA từ đầu — chỉ mB@p2 được serve, không advance (§3.4)
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, upstream.Calls);
        Assert.DoesNotContain(_messages, m => m.Contains("Chuyển candidate kế"));
    }

    [Fact]
    public async Task Chat_WhenFuseOpensWhileRequestQueued_QueuedGets503AndNewRejectedAtGate()
    {
        _settings.Set(SettingsKeys.MaxRetry, 1);
        SeedProvider("p1", maxConcurrent: 1, "m1");
        var upstream = new Gated429Upstream();
        var client = await StartAsync(upstream);

        var serving = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // call#1 giữ trọn slot
        var queued = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        await WaitForQueuedIdAsync("m1"); // request 2 nằm trong queue, chưa dispatch

        upstream.Release();
        var first = await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode); // exhaustion → passthrough

        // RecordExhaustion chạy TRƯỚC Exit (T5) → fuse mở trước khi wake dispatch request kế
        var second = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);
        var walkError = (await ReadJson(second)).GetProperty("error");
        Assert.Equal("The model 'm1' is temporarily unavailable",
            walkError.GetProperty("message").GetString());

        var third = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);

        // Chỉ endpoint gate ghi dòng này — request 2 đi dispatcher walk (không log), request 3 bị chặn tại endpoint
        Assert.Contains(_messages, m =>
            m.Contains("Từ chối request mới") && m.Contains("'m1'"));
        Assert.Equal(1, upstream.Calls); // không ai gọi upstream sau khi fuse mở
        Assert.Empty(_app!.Services.GetRequiredService<IRequestQueue>().Snapshot());
    }
```

### Step 2: Chạy test — mong đợi FAIL

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "ProxyRetryIntegrationTests"
```

Expected: **6 tests — 4 passed / 2 failed** (4 test failover/passthrough/502/combo đã xanh từ T4+T5; 2 test gate fail `Assert.Contains` — chưa có log `Từ chối request mới` vì endpoint chưa có gate; status 503 của walk-rỗng không phân biệt được nên assertion log là red signal).

### Step 3: Viết implementation

**`ProxyApp.cs`** — 3 chỗ:

1. Lambda endpoint chat, thêm param `IModelHealthStore health` cuối danh sách:

```csharp
        app.MapPost("/v1/chat/completions",
            async (HttpContext ctx, IRequestQueue queue, IExecutionList executions,
                ChatCompletionsHandler handler, ILogService log, IModelHealthStore health) =>
```

2. Ngay sau khối `if (prepared is null) return;` (TRƯỚC khi parse priority/enqueue):

```csharp
            // Gate 3C (spec §3.4): exact-id đang ManualRetry → 503 §4 TRƯỚC khi vào queue.
            // Combo name chưa resolve lúc này — gate combo nằm ở walk (T5 filter bỏ candidate)
            if (health.IsManualRetry(prepared.ModelId))
            {
                log.Warn($"Từ chối request mới: model '{prepared.ModelId}' đang ManualRetry",
                    LogCategory.Request);
                await ChatCompletionsHandler.WriteErrorAsync(ctx, 503,
                    $"The model '{prepared.ModelId}' is temporarily unavailable",
                    "server_error", null, null);
                return;
            }
```

3. Trong chuỗi outcome, thêm nhánh sau nhánh `Passthrough` (T4):

```csharp
            else if (outcome is DispatchOutcome.Retryable)
            {
                // Dispatcher đã convert Retryable → Passthrough/Error (spec §2.2) — tới đây là bug
                log.Error($"Outcome Retryable lọt tới endpoint request {id}.", LogCategory.Request);
                await ChatCompletionsHandler.WriteErrorAsync(ctx, 500, "Internal server error",
                    "server_error", null, null);
            }
```

### Step 4: Chạy test — mong đợi PASS

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "ProxyRetryIntegrationTests"
```

Expected: **6 passed / 0 failed**. Full suite:

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **323 passed / 0 failed**.

### Step 5: Commit

```bash
git add src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/Server/ProxyRetryIntegrationTests.cs"
git commit -m "feat: gate manual retry models at proxy endpoint"
```

---

## Task 7 — ModelHealthWatchdog + DI + probe-recovery integration (TDD)

**Files:**
- **Create** `src/RouterBalancing.Core/Engine/ModelHealthWatchdog.cs`
- **Edit** `src/RouterBalancing.Core/Server/ProxyApp.cs` — DI watchdog singleton + hosted qua factory
- **Create** `router balancing test/Engine/ModelHealthWatchdogTests.cs` — 9 unit test
- **Edit** `router balancing test/Server/ProxyRetryIntegrationTests.cs` — +1 test probe recovery

**Consumes:** `ManualRetryModel`/`RecordSuccess`/`RecordProbeFailure` (T3), `RetryAfterParser` (T1), combo resolve + `ResolveFirstEnabledKey` (3A), DI store/TimeProvider (T5).
**Produces:** watchdog probe loop §3.5 — slice UI sau đọc `IModelHealthStore` (spec §7).

### Semantics chốt (spec §2.1/§3.5/§3.6/§5 — test bám đây)

- `ExecuteAsync` = `Task.Delay(WatchdogIntervalSec, TimeProvider, ct)` thật → `ProbeDueAsync(ct)`; **`ProbeDueAsync` public** — unit test gọi trực tiếp, không fake timer.
- `ProbeDueAsync`: quét `GetManualRetryModels()`, chỉ probe model có `NextProbeAt ≤ now` (`null` = hết lượt → bỏ qua).
- Probe: `ResolveAsync(modelId)` → `SelectionFailure`/rỗng → `RecordProbeFailure` (không gọi upstream); `Candidates[0]` → `ResolveFirstEnabledKey` null → `RecordProbeFailure`; body = **Dictionary serialize** (giữ `max_tokens`/`stream` snake_case) `{model, messages:[{role:user,content:ping}], max_tokens:1, stream:false}`; 2xx → `RecordSuccess`, còn lại → `RecordProbeFailure(m, RetryAfterParser.Parse(response.Headers.RetryAfter, now))`.
- Catch tách: `OperationCanceledException when ct` → **rethrow** (host stop, không tính probe fail); mọi lỗi khác → `RecordProbeFailure` + `log.Error` (bọc nuốt log — I2). Store tự ghi Warn §5.
- DI: `AddSingleton<ModelHealthWatchdog>()` + `AddHostedService(sp => sp.GetRequiredService<ModelHealthWatchdog>())` — 1 instance, test resolve thẳng được.

### Step 1: Viết test (FAIL)

**(A) Tạo `router balancing test/Engine/ModelHealthWatchdogTests.cs`** — 9 test:

```csharp
using System.Net;
using System.Text;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

/// <summary>
/// Unit watchdog 3C (spec §3.5): probe due / backoff 60s×n floor Retry-After /
/// dừng ở MaxRetry / recover 2xx — TimeProvider fake, không chờ timer thật.
/// </summary>
public class ModelHealthWatchdogTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly CapturingLog _log = new();
    private readonly FakeTime _time = new();
    private readonly DpapiSecretProtector _protector = new();
    private readonly ModelHealthStore _store;

    public ModelHealthWatchdogTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _settings = new AppSettingsService(_db.CreateFactory(), new DpapiSecretProtector());
        _store = new ModelHealthStore(_settings, _log, _time);
    }

    public void Dispose()
    {
        _settings.Dispose();
        _db.Dispose();
    }

    // Mở fuse = MaxRetry lần RecordFailure; retryAfter (nếu có) chỉ ở lần cuối —
    // đúng transition floor lịch probe khi MỞ fuse (§3.6)
    private void OpenFuse(string modelId, TimeSpan? retryAfter = null)
    {
        var max = _settings.MaxRetry;
        for (var i = 1; i <= max; i++)
            _store.RecordFailure(modelId, i == max ? retryAfter : null);
    }

    private ModelHealthWatchdog CreateWatchdog(IUpstreamClient upstream,
        IComboResolver? resolver = null) =>
        new(_store, resolver ?? new StubResolver(Candidate()), upstream, _protector,
            _settings, _log, _time);

    // Candidate in-memory (không qua DB) — resolver stub trả thẳng
    private ModelCandidate Candidate(bool withKey = true)
    {
        var provider = new Provider
        {
            Id = 1,
            Name = "p1",
            BaseUrl = "https://api.openai.com",
            Type = ProviderType.OpenAI,
        };
        if (withKey)
            provider.Accounts.Add(new ProviderAccount
            {
                Name = "a1",
                Enabled = true,
                ApiKeyEncrypted = _protector.Protect("sk-live"),
            });
        return new ModelCandidate(provider, new Model { Id = 1, ModelId = "m1", Enabled = true });
    }

    [Fact]
    public async Task ProbeDueAsync_WhenNextProbeNotDue_DoesNotCallUpstream()
    {
        OpenFuse("m1", retryAfter: TimeSpan.FromSeconds(600));
        var upstream = new ScriptedUpstream(() => Sse());
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None);

        // Fuse mở floor Retry-After 600s → chưa tới lịch, lần quét đầu không probe (§3.6)
        Assert.Equal(0, upstream.Calls);
        Assert.Equal(_time.GetUtcNow().AddSeconds(600),
            Assert.Single(_store.GetManualRetryModels()).NextProbeAt);
    }

    [Fact]
    public async Task ProbeDueAsync_WhenProbeSucceeds_RecoversModelAndLogsInfo()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => Sse());
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None);

        Assert.Equal(1, upstream.Calls);
        Assert.False(_store.IsManualRetry("m1")); // 2xx → RecordSuccess → về Healthy (§3.5)
        Assert.Contains(_log.Infos, i => i.Contains("phục hồi")); // log Info §5 do store ghi
    }

    [Fact]
    public async Task ProbeDueAsync_SendsMinimalChatBodyWithOneToken()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => Sse());
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None);

        // Body probe = chat tối thiểu 1 token, stream:false — Dictionary serialize giữ snake_case (§3.5)
        var json = Encoding.UTF8.GetString(upstream.LastBody!);
        Assert.Contains("\"model\":\"m1\"", json);
        Assert.Contains("\"content\":\"ping\"", json);
        Assert.Contains("\"max_tokens\":1", json);
        Assert.Contains("\"stream\":false", json);
    }

    [Fact]
    public async Task ProbeDueAsync_WhenProbe429_SchedulesSixtySecondBackoff()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => Resp429());
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None);

        // 60s × attemptsMade=1 (§3.5)
        Assert.Equal(_time.GetUtcNow().AddSeconds(60),
            Assert.Single(_store.GetManualRetryModels()).NextProbeAt);

        // Chưa tới lịch 60s → lần quét kế không probe lại
        await sut.ProbeDueAsync(CancellationToken.None);
        Assert.Equal(1, upstream.Calls);
    }

    [Fact]
    public async Task ProbeDueAsync_WhenProbe429WithRetryAfter_FloorsBackoffToRetryAfter()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => Resp429(retryAfter: "300"));
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None);

        // max(60s×1, 300s) = 300s — Retry-After thắng backoff (§3.6)
        Assert.Equal(_time.GetUtcNow().AddSeconds(300),
            Assert.Single(_store.GetManualRetryModels()).NextProbeAt);
    }

    [Fact]
    public async Task ProbeDueAsync_WhenModelNotResolvable_RecordsProbeFailureWithoutCall()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => Sse());
        var sut = CreateWatchdog(upstream,
            new StubResolver(new SelectionFailure("m1", ResolveFailure.NotFound)));

        await sut.ProbeDueAsync(CancellationToken.None);

        // Model gỡ khỏi DB while fuse mở — không gọi upstream, vẫn lùi lịch (§3.5)
        Assert.Equal(0, upstream.Calls);
        Assert.Equal(_time.GetUtcNow().AddSeconds(60),
            Assert.Single(_store.GetManualRetryModels()).NextProbeAt);
    }

    [Fact]
    public async Task ProbeDueAsync_WhenProviderHasNoEnabledKey_RecordsProbeFailureWithoutCall()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => Sse());
        var sut = CreateWatchdog(upstream, new StubResolver(Candidate(withKey: false)));

        await sut.ProbeDueAsync(CancellationToken.None);

        Assert.Equal(0, upstream.Calls);
        Assert.Equal(_time.GetUtcNow().AddSeconds(60),
            Assert.Single(_store.GetManualRetryModels()).NextProbeAt);
    }

    [Fact]
    public async Task ProbeDueAsync_WhenUpstreamThrows_RecordsProbeFailureAndLogsWarn()
    {
        OpenFuse("m1");
        var upstream = new ScriptedUpstream(() => throw new HttpRequestException("connection refused"));
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None);

        Assert.Equal(_time.GetUtcNow().AddSeconds(60),
            Assert.Single(_store.GetManualRetryModels()).NextProbeAt);
        // Warn do store ghi (§5) — watchdog bắt lỗi rồi gọi RecordProbeFailure, không nuốt
        Assert.Contains(_log.Warns,
            w => w.Contains("Probe model 'm1'") && w.Contains("lần 1/3"));
    }

    [Fact]
    public async Task ProbeDueAsync_WhenMaxRetryReached_StopsProbing()
    {
        _settings.Set(SettingsKeys.MaxRetry, 1);
        _store.RecordFailure("m1"); // MaxRetry=1 → mở fuse ngay, lịch probe = now
        var upstream = new ScriptedUpstream(() => Resp429());
        var sut = CreateWatchdog(upstream);

        await sut.ProbeDueAsync(CancellationToken.None); // probe fail → hết lượt (NextProbeAt = null)

        _time.Advance(TimeSpan.FromHours(2));
        await sut.ProbeDueAsync(CancellationToken.None);

        // Hết lượt probe tự động — ở lại ManualRetry, không probe vô hạn (§1.4/§3.5)
        Assert.Equal(1, upstream.Calls);
        Assert.Null(Assert.Single(_store.GetManualRetryModels()).NextProbeAt);
        Assert.Contains(_log.Warns, w => w.Contains("hết lượt probe tự động"));
    }

    private sealed class StubResolver : IComboResolver
    {
        private readonly SelectionResult _result;

        // CreateWatchdog gọi StubResolver(Candidate()) — ModelCandidate chưa phải SelectionResult,
        // bọc thành Success (watchdog chỉ lấy Candidates[0], không quan tâm Mode)
        public StubResolver(ModelCandidate candidate) =>
            _result = new SelectionSuccess([candidate], ComboMode.RoundRobin);

        public StubResolver(SelectionResult result) => _result = result;

        public Task<SelectionResult> ResolveAsync(string model, CancellationToken ct) =>
            Task.FromResult(_result);
    }

    private sealed class ScriptedUpstream(Func<HttpResponseMessage> factory) : IUpstreamClient
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);
        public byte[]? LastBody { get; private set; }

        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            LastBody = body;
            return Task.FromResult(factory());
        }
    }

    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"x\":1}\n\ndata: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

    private static HttpResponseMessage Resp429(string? retryAfter = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("""{"error":{"message":"rate limited"}}""",
                Encoding.UTF8, "application/json"),
        };
        if (retryAfter is not null)
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        return response;
    }

    private sealed class FakeTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

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
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }
}
```

**(B) Thêm 1 integration test** vào cuối `ProxyRetryIntegrationTests.cs` (trước dấu `}` đóng class):

```csharp
    [Fact]
    public async Task Chat_AfterProbeSucceeds_ModelServesRequestsAgain()
    {
        _settings.Set(SettingsKeys.MaxRetry, 1); // 1 exhaustion = mở fuse
        SeedProvider("p1", maxConcurrent: 4, "m1");
        var failUpstream = true; // closure — đổi được giữa chừng (upstream "phục hồi")
        var upstream = new ScriptedUpstream(_ => failUpstream ? Resp429() : Sse());
        var client = await StartAsync(upstream);

        var first = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        var store = _app!.Services.GetRequiredService<IModelHealthStore>();
        Assert.True(store.IsManualRetry("m1"));

        // Fuse mở, lịch probe = ngay → probe 2xx đóng fuse (§3.5)
        failUpstream = false;
        var watchdog = _app.Services.GetRequiredService<ModelHealthWatchdog>();
        await watchdog.ProbeDueAsync(CancellationToken.None);

        Assert.False(store.IsManualRetry("m1"));

        var second = await client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode); // model phục hồi — serve lại bình thường
    }
```

### Step 2: Chạy test — mong đợi FAIL

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "ModelHealthWatchdogTests"
```

Expected: **FAIL (compile)** — `error CS0246: The name 'ModelHealthWatchdog' does not exist` (2 file test đều tham chiếu — filter không chạy được do test project không build).

### Step 3: Viết implementation

**`src/RouterBalancing.Core/Engine/ModelHealthWatchdog.cs`** (create):

```csharp
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Settings;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Watchdog circuit 3C (spec §3.5): định kỳ probe model <c>ManualRetry</c> bằng chat 1 token —
/// 2xx đóng fuse, fail lùi lịch 60s×n floor <c>Retry-After</c>. Hosted service; unit test gọi
/// trực tiếp <see cref="ProbeDueAsync"/> (không fake timer). Đồng hồ inject qua <see cref="TimeProvider"/>.
/// </summary>
public sealed class ModelHealthWatchdog(
    IModelHealthStore health,
    IComboResolver resolver,
    IUpstreamClient upstream,
    ISecretProtector protector,
    IAppSettingsService settings,
    ILogService log,
    TimeProvider time) : BackgroundService
{
    /// <summary>
    /// Vòng đời hosted: delay thật <c>WatchdogIntervalSec</c> (đọc per-call — đổi setting
    /// có hiệu lực ngay) rồi quét 1 lượt. Host stop → thoát im lặng, không ghi probe fail.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.WatchdogIntervalSec), time, stoppingToken);
                await ProbeDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // host stop — không phải lỗi
            }
        }
    }

    /// <summary>
    /// Quét model <c>ManualRetry</c> có lịch probe ≤ <c>now</c> rồi probe từng model —
    /// public để unit test gọi trực tiếp, không chờ timer (spec §3.5).
    /// </summary>
    /// <param name="ct">Token hủy theo host stop.</param>
    public async Task ProbeDueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var model in health.GetManualRetryModels())
        {
            if (model.NextProbeAt is not { } due || due > now)
                continue; // chưa tới lịch / hết lượt probe tự động (NextProbeAt = null)
            await ProbeOneAsync(model.ModelId, ct);
        }
    }

    private async Task ProbeOneAsync(string modelId, CancellationToken ct)
    {
        try
        {
            var selection = await resolver.ResolveAsync(modelId, ct);
            if (selection is not SelectionSuccess success || success.Candidates.Count == 0)
            {
                // Model gỡ khỏi DB/combo while fuse mở — vẫn ghi probe fail để lùi lịch
                health.RecordProbeFailure(modelId);
                return;
            }

            var candidate = success.Candidates[0]; // provider enabled đầu tiên của model
            var key = ProviderKeyResolver.ResolveFirstEnabledKey(candidate.Provider, protector);
            if (key is null)
            {
                health.RecordProbeFailure(modelId);
                return;
            }

            // Dictionary thay vì record/anonymous: giữ key snake_case đúng contract OpenAI
            var body = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["model"] = candidate.Model.ModelId,
                ["messages"] = new[]
                {
                    new Dictionary<string, string> { ["role"] = "user", ["content"] = "ping" },
                },
                ["max_tokens"] = 1,
                ["stream"] = false,
            });

            using var response = await upstream.PostChatCompletionAsync(
                candidate.Provider, key, body, ct);
            if (response.IsSuccessStatusCode)
                health.RecordSuccess(modelId);
            else
                health.RecordProbeFailure(modelId,
                    RetryAfterParser.Parse(response.Headers.RetryAfter?.ToString(),
                        time.GetUtcNow()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host stop giữa probe — không tính là probe fail
        }
        catch (Exception ex)
        {
            // Mạng chết/lỗi khác khi probe là trạng thái bình thường. State TRƯỚC (entry là
            // memory, không throw), log SAU best-effort — log (I2) không được phá vòng watchdog
            health.RecordProbeFailure(modelId);
            try
            {
                log.Error($"Lỗi probe model '{modelId}': {ex.Message}", ex, LogCategory.App);
            }
            catch
            {
                // Nuốt chủ đích (pattern SafeLog của store): SQLite sập thì bỏ chi tiết log,
                // lịch probe vẫn đã lùi ở trên
            }
        }
    }
}
```

**`ProxyApp.cs`** — thêm 2 dòng sau dòng `builder.Services.AddHostedService<DispatcherLoop>();` (khối T5):

```csharp
        // Watchdog 3C (spec §2.1): singleton + hosted qua factory lấy ĐÚNG instance này —
        // integration test resolve ModelHealthWatchdog từ Services rồi gọi ProbeDueAsync
        builder.Services.AddSingleton<ModelHealthWatchdog>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ModelHealthWatchdog>());
```

### Step 4: Chạy test — mong đợi PASS

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "ModelHealthWatchdogTests"
```

Expected: **9 passed / 0 failed**. Full suite (bao gồm integration probe-recovery):

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **333 passed / 0 failed**.

### Step 5: Commit

```bash
git add src/RouterBalancing.Core/Engine/ModelHealthWatchdog.cs src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/Engine/ModelHealthWatchdogTests.cs" "router balancing test/Server/ProxyRetryIntegrationTests.cs"
git commit -m "feat: add watchdog probe loop for manual retry models"
```

---

## Task 8 — e2e mock failure mode + e2e-3c script (ngoại lệ TDD)

**Files:**
- **Edit** `scripts/mock-upstream.mjs` — stateful `failFirst` + `POST /__config`
- **Create** `scripts/e2e-3c.sh`

**Consumes:** hành vi T1–T7 (failover, exhaustion passthrough, Retry-After, gate 503).
**Produces:** e2e gate cuối slice (spec §6.3) — chạy tay, không thuộc `dotnet test`.

> **Ngoại lệ TDD** (Ràng buộc #7): không sinh test `dotnet test` mới — gate = full suite giữ nguyên **333** + 2 script e2e chạy tay ALL PASS.

### Chuẩn bị (trước khi chạy)

1. Sửa mock → **restart node** (`node scripts/mock-upstream.mjs 9999` — kill bản cũ trước).
2. App đang chạy; **MaxRetry = 3 (default)**.
3. 2 provider `e2e-mock` + `e2e-mock-2`: BaseUrl `http://127.0.0.1:9999`, type OpenAI, mỗi provider ≥1 model enabled id `e2e-mock-3c`, ≥1 account key enabled. Cùng 1 mock → fail state dùng chung, thứ tự provider **không quan trọng** (failover/exhaustion đều chạy đủ 2 provider).
4. **Circuit in-memory**: model `e2e-mock-3c` CHƯA từng mở fuse trong app đang chạy — chạy lại e2e-3c cần **restart app**. `e2e-3a` dùng model khác → chạy **TRƯỚC** không ảnh hưởng.

### Step 1: Sửa `scripts/mock-upstream.mjs` (toàn bộ file)

```js
// Mock upstream OpenAI-compatible cho e2e slice 3A/3C.
// Nhận POST /v1/chat/completions (yêu cầu Bearer) và trả SSE echo — không phụ thuộc mạng thật.
// 3C: POST /__config {"failFirst":N} → N request chat kế trả 429 (retry-after: 5) rồi tự
// phục hồi — stateful fail N lần đầu để test failover/exhaustion/gate trên cùng mock.
import http from 'node:http';

const PORT = Number(process.argv[2] ?? 9999);
let failFirst = 0; // số request chat còn phải fail trước khi trở lại 200

const server = http.createServer((req, res) => {
  const chunks = [];
  req.on('data', (c) => chunks.push(c));
  req.on('end', () => {
    const body = Buffer.concat(chunks).toString('utf8');

    if (req.method === 'POST' && req.url === '/__config') {
      failFirst = Number(JSON.parse(body).failFirst ?? 0);
      res.writeHead(200, { 'content-type': 'application/json' });
      res.end('{}');
      return;
    }

    if (req.method !== 'POST' || !req.url?.includes('/v1/chat/completions')) {
      res.writeHead(404, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ error: { message: 'not found' } }));
      return;
    }

    const auth = req.headers.authorization ?? '';
    if (!auth.startsWith('Bearer ') || auth.length <= 'Bearer '.length) {
      res.writeHead(401, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ error: { message: 'missing bearer', type: 'invalid_request_error' } }));
      return;
    }

    if (failFirst > 0) {
      failFirst -= 1;
      res.writeHead(429, { 'content-type': 'application/json', 'retry-after': '5' });
      res.end(JSON.stringify({ error: { message: 'rate limited', type: 'rate_limit_error' } }));
      return;
    }

    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.write(`data: ${JSON.stringify({ echo: JSON.parse(body), key: auth.slice(7) })}\n\n`);
    res.write('data: [DONE]\n\n');
    res.end();
  });
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`mock upstream listening on http://127.0.0.1:${PORT}`);
});
```

> Compat 3A: `failFirst` mặc định `0` → `e2e-3a` không đổi hành vi; `/__config` đứng TRƯỚC nhánh 404.

### Step 2: Tạo `scripts/e2e-3c.sh`

```bash
#!/usr/bin/env bash
# e2e smoke slice 3C (retry, circuit & watchdog) — chạy TAY, không thuộc dotnet test.
# Prerequisites (fail-fast nếu thiếu):
#   1. App đang chạy, proxy tại $BASE (mặc định http://127.0.0.1:8317), MaxRetry = 3 (default)
#   2. node scripts/mock-upstream.mjs 9999 đang chạy (bản 3C có POST /__config)
#   3. 2 provider 'e2e-mock' + 'e2e-mock-2': BaseUrl http://127.0.0.1:9999, type OpenAI,
#      mỗi provider >=1 model enabled id $MODEL_ID_3C (mặc định e2e-mock-3c), >=1 account key.
#      Cùng 1 mock nên thứ tự provider không quan trọng.
#   4. Model e2e-mock-3c CHƯA mở fuse trong app đang chạy — circuit in-memory, chạy lại
#      e2e-3c cần restart app. e2e-3a dùng model khác nên chạy TRƯỚC không ảnh hưởng.
# Cách chạy: bash scripts/e2e-3c.sh   (hoặc MODEL_ID_3C=... BASE=... MOCK=... bash scripts/e2e-3c.sh)
set -euo pipefail

BASE="${BASE:-http://127.0.0.1:8317}"
MOCK="${MOCK:-http://127.0.0.1:9999}"
MODEL_ID_3C="${MODEL_ID_3C:-e2e-mock-3c}"
BODY_FILE="$(mktemp)"
HDR_FILE="$(mktemp)"
trap 'rm -f "$BODY_FILE" "$HDR_FILE"' EXIT
FAIL=0

check() { # $1 = tên, $2 = status mong đợi, còn lại = args curl (luôn kèm -D để expect_header)
  local name="$1" expected="$2" actual
  shift 2
  actual=$(curl -s -o "$BODY_FILE" -D "$HDR_FILE" -w '%{http_code}' "$@") || actual=000
  if [[ "$actual" == "$expected" ]]; then
    echo "PASS - $name"
  else
    echo "FAIL - $name (nhận $actual, mong đợi $expected)"
    cat "$BODY_FILE"; echo
    FAIL=1
  fi
}

expect_body() { # $1 = chuỗi cần có trong body, $2 = tên
  if grep -qF "$1" "$BODY_FILE"; then
    echo "PASS - $2"
  else
    echo "FAIL - $2 (không thấy '$1')"
    cat "$BODY_FILE"; echo
    FAIL=1
  fi
}

expect_header() { # $1 = chuỗi cần có trong header (không phân biệt hoa thường), $2 = tên
  if grep -qiF "$1" "$HDR_FILE"; then
    echo "PASS - $2"
  else
    echo "FAIL - $2 (không thấy '$1')"
    cat "$HDR_FILE"; echo
    FAIL=1
  fi
}

mock_config() { # $1 = số request chat fail (429) đầu tiên
  if ! curl -sf -X POST "$MOCK/__config" -H 'Content-Type: application/json' \
      -d "{\"failFirst\":$1}" >/dev/null; then
    echo "FAIL - mock_config failFirst=$1 (mock chưa chạy bản 3C /__config?)"
    FAIL=1
  fi
}

if ! curl -sf "$BASE/health" >/dev/null; then
  echo "FAIL - proxy không phản hồi tại $BASE/health (app chưa chạy?)"
  exit 1
fi
echo "PASS - proxy alive"

CHAT=( -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json'
  -d "{\"model\":\"$MODEL_ID_3C\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}" )

# Phase 1 — failover: mock fail đúng 1 lần đầu → provider đầu 429, candidate kế 200 (§3.2)
mock_config 1
check "failover: first 429 -> next candidate 200" 200 "${CHAT[@]}"
expect_body 'data:' "failover: client chỉ thấy 200 SSE"

# Phase 2-4 — exhaustion (mock luôn 429): passthrough response cuối + giữ Retry-After (§3.3/§3.6)
# Mỗi request = +1 distinct model → 3 request = đủ MaxRetry mở fuse
mock_config 99
check "exhaustion #1 -> 429 passthrough" 429 "${CHAT[@]}"
expect_body 'rate limited' "exhaustion #1: body provider nguyên"
expect_header 'retry-after: 5' "exhaustion #1: client nhận lại Retry-After"
check "exhaustion #2 -> 429 passthrough" 429 "${CHAT[@]}"
check "exhaustion #3 -> 429 passthrough (fuse mo)" 429 "${CHAT[@]}"

# Phase 5 — gate: đủ MaxRetry=3 → request mới 503 TRƯỚC khi vào queue (§3.4/§4)
check "gate -> 503 before queue" 503 "${CHAT[@]}"
expect_body 'temporarily unavailable' "gate: message 503 mới"
check "gate la 2 -> 503" 503 "${CHAT[@]}"

if [[ $FAIL -eq 0 ]]; then
  echo "ALL PASS"
else
  echo "CÓ TEST FAIL"
  exit 1
fi
```

### Step 3: Chạy e2e — mong đợi ALL PASS

```bash
bash scripts/e2e-3a.sh     # compat: mock default failFirst=0 — 5 checks ALL PASS
bash scripts/e2e-3c.sh     # 11 PASS + ALL PASS (failover 200, 3×429, 2×503)
```

- e2e-3a FAIL → sửa mock (đừng sửa script 3a).
- e2e-3c FAIL ở phase 2-5 → kiểm tra lại circuit đã sạch (restart app) / MaxRetry=3 / provider + model đúng.
- Chạy lại e2e-3c lần 2 mà **không restart app** → phase 1 sẽ FAIL (fuse đã mở từ lần chạy trước) — đây là hành vi dự kiến, không phải bug.

### Step 4: Gate dotnet — giữ nguyên

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo
```

Expected: **333 passed / 0 failed** + 0 Warning / 0 Error (T8 không sửa code app/test).

### Step 5: Commit

```bash
git add scripts/mock-upstream.mjs scripts/e2e-3c.sh
git commit -m "test: add e2e smoke script for retry circuit"
```

---

## Task 9 — Final Gates (verification — không commit mới)

**Files:** không sửa file nào.
**Consumes:** T1–T8 đã commit trên `feat/retry-circuit-3c`.
**Produces:** kết quả verify cuối → offer merge local (KHÔNG push).

> T9 là task verification: không viết test mới, không sửa code, **không commit** (ngoại lệ rule "1 commit/task" — ladder +0).

### Kỳ vọng git trước khi verify

- **master:** `9a1e4eb` + 1 commit plan `docs: add retry circuit implementation plan for slice 3c` (commit ngay trước khi tạo branch).
- **`feat/retry-circuit-3c`:** đúng 8 commit T1–T8 kể từ `9a1e4eb`.

```bash
git log --oneline 9a1e4eb..HEAD   # 8 dòng: T1..T8
git status --short                # sạch
```

### Step 1: Full suite

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

Expected: **336 passed / 0 failed**. FAIL = bug ở task trước → sửa đúng chỗ đó, **không nõn assertion**.

### Step 2: Build

```powershell
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo
```

Expected: **0 Warning / 0 Error**. KHÔNG build `.slnx` (NETSDK1082 pre-existing).

### Step 3: Parity i18n (Ràng buộc #3)

```powershell
Select-String -Path "src/RouterBalancing.Core/Localization/Translations.cs" -Pattern '\["'
```

Expected: EN (dòng < 232) = **208**, VI (≥ 232) = **208** — 3C không thêm key i18n.

### Step 4: e2e tay (app + mock đang chạy, circuit sạch)

```bash
bash scripts/e2e-3a.sh    # ALL PASS — parity 3A
bash scripts/e2e-3c.sh    # ALL PASS — failover/exhaustion passthrough/gate 503
```

### Step 5: Review toàn branch

```powershell
& "C:\Program Files\Git\bin\bash.exe" "C:\Users\trucn\.cache\opencode\packages\superpowers@git+https_\github.com\obra\superpowers.git\node_modules\superpowers\skills\subagent-driven-development\scripts\review-package" 9a1e4eb HEAD
```

Critical/Important → sửa + commit fix trên branch + re-review; Minor → ghi ledger `.superpowers/sdd/progress.md`. Đối chiếu spec amend `9a1e4eb` theo section.

### Step 6: Báo user + offer merge

Kết quả cần nêu: **336/0**, build **0W/0E**, parity **208/208**, e2e **ALL PASS ×2**, ledger **9/9**. Offer **merge local** `feat/retry-circuit-3c` → master (KHÔNG push — push là quyết định của user).
