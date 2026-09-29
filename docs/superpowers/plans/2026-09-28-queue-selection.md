# Plan: Queue & Selection (Slice 3B)

- **Spec:** `docs/superpowers/specs/2026-09-28-queue-selection-design.md` (approved, commit `d035258`) — mọi review đối chiếu spec theo section.
- **Điều kiện đầu:** master `@04071ab` (3A merged) — **226 passed / 0 failed**.
- **Branch thực thi:** `feat/queue-selection-3b` (SDD: implementer + reviewer mỗi task).
- **Kết thúc:** 9 tasks, **273 tests**, e2e 3A vẫn ALL PASS. KHÔNG push.

## Mục tiêu & phạm vi

Chèn **priority queue + dispatcher + selection** vào giữa validate và upstream (spec §1): queue-first dispatcher, park vô hạn khi thiếu capacity, `X-Priority` + luật 1-Highest, `ExecutionList` (MaxConcurrent theo Provider), chọn RR/Fallback với combo nested, control API `GET /v1/requests` + `POST /v1/requests/{id}/cancel`, header `X-Request-Id`.

Ngoài phạm vi (không đụng): retry/failover (3C), account weighting (3D), Anthropic (3E), UI cancel/priority (slices sau), expose `SetPriority` endpoint.

## Ràng buộc toàn cục (áp dụng từng task)

1. **Gate mỗi task:** chạy `dotnet test "router balancing test/router balancing test.csproj" --nologo` phải xanh đúng số gate của task trong bảng ladder. **Đóng app MAUI đang mở trước khi test.**
2. **Compile:** `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo` → 0 Warning / 0 Error ở cuối mỗi task. KHÔNG build `.slnx` (NETSDK1082 pre-existing).
3. **Parity i18n** (nếu task thêm key): `Select-String -Path "src/RouterBalancing.Core/Localization/Translations.cs" -Pattern '\["'` → EN < 232, VI >= 232 = **208/208**. Slice 3B không thêm UI key → parity giữ nguyên.
4. Comment "why" tiếng Việt; XML doc `///` mọi public/protected member; identifier + commit message tiếng Anh conventional; test doubles là nested private trong file test; error client-facing tiếng Anh (giữ message 3A, ngoại lệ chốt: Anthropic-503 = ``The model '{id}' is not supported yet``), log tiếng Việt; KHÔNG log body/messages/API key.
5. Test name mô tả hành vi (`Enqueue_Highest_DemotesExistingHighestToHigh`).
6. Đúng 1 commit/task, message ghi sẵn ở cuối task. KHÔNG push.
7. Mỗi task có test mới đều **TDD**: viết test → chạy thấy FAIL (CS0246/hành vi) → viết implementation → PASS → full suite → commit. Ngoại lệ: **T7 (wiring/xóa)** không sinh test mới — 8 test integration 3A + full suite là safety net; **T9 (verification)** test mong đợi PASS ngay, FAIL = bug ở task trước → sửa đúng chỗ đó, không nới assertion.

## Ladder test (arithmetic thống nhất toàn plan)

| Task | Gate sau task | Delta | Ghi chú |
|---|---|---|---|
| — | 226 | base | 3A merged |
| T1 Queue | **240** | +14 | 13 queue + 1 parser |
| T2 ExecutionList | **248** | +8 | |
| T3 ComboResolver | **261** | +13 | tạo `ResolveFailure.cs` (move enum) |
| T4 ModelSelector | **268** | +7 | |
| T5 Handler split | **266** | −2 | handler tests 10 → 8 |
| T6 DispatcherLoop | **272** | +6 | |
| T7 Wiring + xóa cũ | **265** | −7 | xóa `ModelResolverTests` (7 test) |
| T8 Control API | **270** | +5 | |
| T9 Integration | **273** | +3 | saturation/cancel/snapshot |

Cuối mỗi task chạy **full suite** (không filter) trước khi commit.

## Kiến trúc — các chốt kỹ thuật (spec §2 + chi tiết plan)

1. **Outcome:** `DispatchOutcome` là **abstract record** (không enum thuần — `Error` cần mang payload) với nested `Handled`/`Cancelled`/`Aborted`/`Error(Status, Message, Type, Param, Code)`.
2. **Queue API:** `Take(id)` (dispatcher — **không** fire Changed, nó tự drain tiếp) và `TryRemove(id)` (cancel/abort — fire Changed để re-evaluate head) cùng 1 atomic core `Remove(id, fireChanged)`. Đặt vậy để wake count determinist (test park không flaky).
3. **Dispatcher order capacity:** `TryEnter` (reserve slot) **trước** `TryRemove` (Take) — giữ invariant của spec §3.4: sau khi item rời queue, `ExecutionList.Contains` đã true → cancel luôn ra 409 chứ không 404. `TryRemove` fail → `Exit` ngay (cancel thắng).
4. **Validate ở endpoint, resolve ở dispatcher:** `PrepareAsync` (buffer + validate, trả `null` = đã ghi 400) chạy trước enqueue; resolve-failure 404/503 do dispatcher log (message 3A giữ nguyên) + `Complete(Error)` → endpoint ghi response.
5. **Handler:** ctor bỏ `IModelResolver`; `ForwardAsync` trả `DispatchOutcome` (không tự ghi lỗi); `WriteErrorAsync` thành `internal static` để endpoint tái dùng (`ErrorJsonOptions` private, chỉ dùng trong đó).
6. **MaxConcurrent:** query DB **mỗi lần** `TryEnter`/`CanEnter` (giá trị mới nhất, không cache); provider không tồn tại → coi như hết slot (`false`).
7. **RR cursor:** eligible = `CanEnter` true; sort `(inFlight, ProviderId)`; chọn `eligible[cursor % count]`; `Interlocked.Increment(ref _cursor)` **ngay sau khi chọn** (không cần biết Take thắng/thua — cursor chỉ tie-break). Test với count ổn định → 0,1,2,0.
8. **Cancel log:** endpoint gốc (nơi await) ghi `Info "Đã huỷ request {id} (đang chờ), model {model}."` khi outcome `Cancelled` — cancel endpoint **không log** (tránh trùng). `RequestAborted` callback ghi `Info "Request {id} bị client ngắt khi đang chờ."` khi gỡ được item.
9. **Register sau Enqueue:** `RequestAborted.Register` chạy callback ngay nếu token đã cancel → nếu register TRƯỚC enqueue thì item bị enqueue sau khi "đã huỷ" → rò rỉ. Enqueue trước, register sau.
10. **Snapshot shape:** 2 nguồn concat bằng anonymous type **cùng shape** (`provider` cast `(string?)`, `startedAt` cast `(DateTimeOffset?)`); priority map `Highest → "max"` (vocab input `X-Priority: high|max`), `High → "high"`, `Normal → "normal"`; `elapsedMs` = now − (`startedAt` nếu serving, ngược lại `enqueuedAt`).

## Cấu trúc file

**Tạo mới (`src/RouterBalancing.Core/Engine/`):** `RequestPriority.cs`, `RequestPriorityParser.cs`, `RequestId.cs`, `DispatchOutcome.cs`, `ProxyRequest.cs`, `IRequestQueue.cs`, `RequestQueue.cs` (T1); `ResolveFailure.cs` (T3 — move enum); `IExecutionList.cs`, `ExecutionList.cs` (T2); `IComboResolver.cs`, `ComboResolver.cs` (T3); `IModelSelector.cs`, `ModelSelector.cs` (T4); `IChatForwarder.cs` không cần — handler concrete (T5); `DispatcherLoop.cs` (T6).

**Sửa:** `ChatCompletionsHandler.cs` (T5), `IModelResolver.cs` (T3 bỏ enum), `Server/ProxyApp.cs` (T5 interim, T7 final, T8 control API).

**Xóa (T7):** `Engine/ModelResolver.cs`, `Engine/IModelResolver.cs`, `router balancing test/Engine/ModelResolverTests.cs`.

**Test mới (`router balancing test/`):** `Engine/RequestQueueTests.cs`, `Engine/RequestPriorityParserTests.cs` (T1); `Engine/ExecutionListTests.cs` (T2); `Engine/ComboResolverTests.cs` (T3); `Engine/ModelSelectorTests.cs` (T4); `Engine/DispatcherLoopTests.cs` (T6); `Server/ProxyControlApiTests.cs` (T8); `Server/ProxyQueueIntegrationTests.cs` (T9). Sửa: `Engine/ChatCompletionsHandlerTests.cs` (T5).

---

## Task 1 — Priority queue + primitives (TDD)

**Files (create):**

- `src/RouterBalancing.Core/Engine/RequestPriority.cs`
- `src/RouterBalancing.Core/Engine/RequestPriorityParser.cs`
- `src/RouterBalancing.Core/Engine/RequestId.cs`
- `src/RouterBalancing.Core/Engine/DispatchOutcome.cs`
- `src/RouterBalancing.Core/Engine/ProxyRequest.cs`
- `src/RouterBalancing.Core/Engine/IRequestQueue.cs`
- `src/RouterBalancing.Core/Engine/RequestQueue.cs`
- `router balancing test/Engine/RequestQueueTests.cs`
- `router balancing test/Engine/RequestPriorityParserTests.cs`

**Consumes:** không (tạo mới toàn bộ); `HttpContext` (test tạo `DefaultHttpContext`).
**Produces (T2+ dùng):** `ProxyRequest`, `DispatchOutcome`, `IRequestQueue`/`RequestQueue` (singleton DI ở T7), `RequestPriority`, `RequestPriorityParser`, `RequestId`.

### Step 1: Viết test (FAIL)

`router balancing test/Engine/RequestQueueTests.cs` — namespace `router_balancing_test.Engine` (theo style `ModelResolverTests.cs`):

```csharp
using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class RequestQueueTests
{
    private readonly RequestQueue _queue = new();

    private static ProxyRequest Req(string id, RequestPriority priority = RequestPriority.Normal)
        => new(id, priority, "m1", Encoding.UTF8.GetBytes("{}"), new DefaultHttpContext());

    [Fact]
    public void Enqueue_New_FiresChangedAndPeekReturnsFifoHead()
    {
        var changed = 0;
        _queue.Changed += () => changed++;

        Assert.True(_queue.Enqueue(Req("r1")));
        Assert.True(_queue.Enqueue(Req("r2")));
        Assert.Equal(2, changed);

        Assert.True(_queue.Peek(out var head));
        Assert.Equal("r1", head!.Id);
        // Peek là read-only — không fire Changed
        Assert.Equal(2, changed);
    }

    [Fact]
    public void Enqueue_DuplicateId_ReturnsFalseWithoutStateChange()
    {
        Assert.True(_queue.Enqueue(Req("r1")));
        var changed = 0;
        _queue.Changed += () => changed++;

        Assert.False(_queue.Enqueue(Req("r1")));

        Assert.Equal(0, changed);
        Assert.True(_queue.Peek(out var head));
        Assert.Equal("r1", head!.Id);
    }

    [Fact]
    public void Enqueue_HighPriority_DequeuesBeforeNormal()
    {
        _queue.Enqueue(Req("n1", RequestPriority.Normal));
        _queue.Enqueue(Req("n2", RequestPriority.Normal));
        _queue.Enqueue(Req("h1", RequestPriority.High));

        Assert.True(_queue.Take("h1", out var first));
        Assert.Equal("h1", first!.Id);
        Assert.True(_queue.Take("n1", out var second));
        Assert.Equal("n1", second!.Id);
    }

    [Fact]
    public void Enqueue_Highest_DemotesExistingHighestToHigh()
    {
        _queue.Enqueue(Req("old", RequestPriority.Highest));
        _queue.Enqueue(Req("new", RequestPriority.Highest));

        // Luật 1-Highest: item cũ downgrade xuống High (spec §3.1)
        Assert.True(_queue.Take("new", out var first));
        Assert.Equal("new", first!.Id);
        Assert.True(_queue.Take("old", out var second));
        Assert.Equal("old", second!.Id);
        Assert.Equal(RequestPriority.High, second.Priority);
        Assert.False(_queue.Contains("old"));
    }

    [Fact]
    public void Enqueue_WhenOnlyHighest_PeekReturnsItWithHighestPriority()
    {
        _queue.Enqueue(Req("only", RequestPriority.Highest));

        Assert.True(_queue.Peek(out var head));
        Assert.Equal("only", head!.Id);
        Assert.Equal(RequestPriority.Highest, head.Priority);
    }

    [Fact]
    public void Peek_WhenQueueEmpty_ReturnsFalse()
    {
        Assert.False(_queue.Peek(out var none));
        Assert.Null(none);
    }

    [Fact]
    public void Take_RemovesRequestedItem_LeavingFollowingItem()
    {
        _queue.Enqueue(Req("r1"));
        _queue.Enqueue(Req("r2"));

        Assert.True(_queue.Take("r1", out var taken));
        Assert.Equal("r1", taken!.Id);
        Assert.False(_queue.Contains("r1"));

        Assert.True(_queue.Peek(out var head));
        Assert.Equal("r2", head!.Id);
    }

    [Fact]
    public void Take_WhenItemAlreadyRemoved_ReturnsFalse()
    {
        _queue.Enqueue(Req("r1"));

        Assert.True(_queue.TryRemove("r1", out _));
        Assert.False(_queue.Take("r1", out var none));
        Assert.Null(none);
    }

    [Fact]
    public void TryRemove_UnknownId_ReturnsFalse()
    {
        Assert.False(_queue.TryRemove("ghost", out var none));
        Assert.Null(none);
    }

    [Fact]
    public void SetPriority_PromoteNormalToHigh_ReordersIntoHighBucket()
    {
        _queue.Enqueue(Req("h1", RequestPriority.High));
        _queue.Enqueue(Req("n1", RequestPriority.Normal));
        _queue.Enqueue(Req("n2", RequestPriority.Normal));

        Assert.True(_queue.SetPriority("n2", RequestPriority.High));

        // n2 vào High bucket theo sequence cũ (3) nên đứng sau h1 (1) — nhưng vẫn trước n1
        Assert.True(_queue.Take("h1", out var first));
        Assert.True(_queue.Take("n2", out var second));
        Assert.Equal(RequestPriority.High, second!.Priority);
        Assert.True(_queue.Take("n1", out var third));
        Assert.Equal("n1", third!.Id);

        Assert.False(_queue.SetPriority("ghost", RequestPriority.High));
    }

    [Fact]
    public void SetPriority_PromoteToHighest_DemotesExistingHighest()
    {
        _queue.Enqueue(Req("h1", RequestPriority.Highest));
        _queue.Enqueue(Req("n1", RequestPriority.Normal));

        Assert.True(_queue.SetPriority("n1", RequestPriority.Highest));

        Assert.True(_queue.Take("n1", out var first));
        Assert.Equal("n1", first!.Id);
        Assert.True(_queue.Take("h1", out var second));
        Assert.Equal(RequestPriority.High, second!.Priority);
    }

    [Fact]
    public void Snapshot_ReturnsAllItemsInPriorityThenSequenceOrder()
    {
        _queue.Enqueue(Req("n1"));
        _queue.Enqueue(Req("h1", RequestPriority.Highest));
        _queue.Enqueue(Req("n2"));
        _queue.Enqueue(Req("h2", RequestPriority.High));

        var ids = _queue.Snapshot().Select(r => r.Id).ToArray();

        // Highest (1-Highest: h1 không bị demote vì không có item Highest mới vào sau)
        Assert.Equal(["h1", "h2", "n1", "n2"], ids);
    }

    [Fact]
    public void Contains_ReturnsTrueOnlyWhileItemInQueue()
    {
        _queue.Enqueue(Req("r1"));

        Assert.True(_queue.Contains("r1"));
        Assert.False(_queue.Contains("ghost"));

        _queue.Take("r1", out _);
        Assert.False(_queue.Contains("r1"));
    }
}
```

`router balancing test/Engine/RequestPriorityParserTests.cs`:

```csharp
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class RequestPriorityParserTests
{
    [Fact]
    public void Parse_IsLenient_MapsHighMaxOnlyAndNormalForEverythingElse()
    {
        // Spec §3.1: chỉ "high"/"max" (case-insensitive, trim) — giá trị lạ → Normal, không 400
        Assert.Equal(RequestPriority.Normal, RequestPriorityParser.Parse(null));
        Assert.Equal(RequestPriority.Normal, RequestPriorityParser.Parse(""));
        Assert.Equal(RequestPriority.Normal, RequestPriorityParser.Parse("normal"));
        Assert.Equal(RequestPriority.High, RequestPriorityParser.Parse("high"));
        Assert.Equal(RequestPriority.High, RequestPriorityParser.Parse("High"));
        Assert.Equal(RequestPriority.High, RequestPriorityParser.Parse("  high  "));
        Assert.Equal(RequestPriority.Highest, RequestPriorityParser.Parse("max"));
        Assert.Equal(RequestPriority.Highest, RequestPriorityParser.Parse("MAX"));
        Assert.Equal(RequestPriority.Normal, RequestPriorityParser.Parse("urgent"));
        Assert.Equal(RequestPriority.Normal, RequestPriorityParser.Parse("2"));
    }
}
```

Chạy: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~RequestQueueTests|FullyQualifiedName~RequestPriorityParserTests" --nologo` → **FAIL** `CS0246` (các type chưa tồn tại).

### Step 2: Viết implementation

`RequestPriority.cs`:

```csharp
namespace RouterBalancing.Core.Engine;

/// <summary>Mức ưu tiên request trong queue — luật 1-Highest xem <see cref="RequestQueue"/>.</summary>
public enum RequestPriority
{
    Normal = 0,
    High = 1,
    Highest = 2,
}
```

`RequestPriorityParser.cs`:

```csharp
namespace RouterBalancing.Core.Engine;

/// <summary>Parse header <c>X-Priority</c> theo luật lenient của spec §3.1.</summary>
public static class RequestPriorityParser
{
    /// <summary>
    /// Chỉ <c>high</c>/<c>max</c> (case-insensitive, đã trim) được nhận — mọi giá trị khác
    /// (kể cả rỗng/không có header) trả <see cref="RequestPriority.Normal"/>; không reject 400.
    /// </summary>
    public static RequestPriority Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "high" => RequestPriority.High,
        "max" => RequestPriority.Highest,
        _ => RequestPriority.Normal,
    };
}
```

`RequestId.cs`:

```csharp
namespace RouterBalancing.Core.Engine;

/// <summary>Sinh id ngắn cho request (spec §3.6).</summary>
public static class RequestId
{
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>8 ký tự base36 (a-z, 0-9) ngẫu nhiên — endpoint tự retry nếu va chạm.</summary>
    public static string New()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];
        return new string(chars);
    }
}
```

`DispatchOutcome.cs`:

```csharp
namespace RouterBalancing.Core.Engine;

/// <summary>
/// Kết quả dispatch 1 request — endpoint ghi response theo outcome
/// (Handled/Aborted không ghi gì; Error/Cancelled endpoint ghi JSON — spec §2.1).
/// Abstract record (không enum) vì <see cref="Error"/> cần mang payload.
/// </summary>
public abstract record DispatchOutcome
{
    /// <summary>Handler đã ghi response (stream/JSON pass-through) — endpoint không làm gì thêm.</summary>
    public sealed record Handled : DispatchOutcome;

    /// <summary>Request bị huỷ qua cancel API — endpoint ghi 400 <c>request_cancelled</c>.</summary>
    public sealed record Cancelled : DispatchOutcome;

    /// <summary>Client đã ngắt — endpoint không ghi gì (response đã đóng).</summary>
    public sealed record Aborted : DispatchOutcome;

    /// <summary>Lỗi cần endpoint ghi JSON lỗi OpenAI-style.</summary>
    /// <param name="Status">HTTP statuscode.</param>
    /// <param name="Message">Nội dung <c>error.message</c> (tiếng Anh, y như 3A).</param>
    /// <param name="Type">Nội dung <c>error.type</c>.</param>
    /// <param name="Param">Nội dung <c>error.param</c> (nullable).</param>
    /// <param name="Code">Nội dung <c>error.code</c> (nullable).</param>
    public sealed record Error(int Status, string Message, string Type, string? Param, string? Code)
        : DispatchOutcome;
}
```

`ProxyRequest.cs`:

```csharp
using Microsoft.AspNetCore.Http;

namespace RouterBalancing.Core.Engine;

/// <summary>Đại diện 1 request đang nằm trong <see cref="IRequestQueue"/> (spec §2.1).</summary>
/// <param name="Id">Id sinh trước validate (endpoint).</param>
/// <param name="Priority">Mức ưu tiên từ header <c>X-Priority</c>.</param>
/// <param name="Model">Chuỗi <c>model</c> gốc client gửi (model id hoặc tên combo).</param>
/// <param name="Body">Body JSON đã buffer — dispatcher truyền thẳng khi serve.</param>
/// <param name="Context">HttpContext gốc — handler ghi response vào đây.</param>
public sealed class ProxyRequest(
    string id, RequestPriority priority, string model, byte[] body, HttpContext context)
{
    /// <summary>Mã request (8 ký tự base36).</summary>
    public string Id { get; } = id;

    /// <summary>Mức ưu tiên hiện tại — đổi được khi demote 1-Highest hoặc SetPriority.</summary>
    public RequestPriority Priority { get; internal set; } = priority;

    /// <summary>Tên model/combo gốc.</summary>
    public string Model { get; } = model;

    /// <summary>Body đã buffer.</summary>
    public byte[] Body { get; } = body;

    /// <summary>HttpContext của client gọi tới.</summary>
    public HttpContext Context { get; } = context;

    /// <summary>Sequence tăng dần toàn cục — key thứ 2 trong bucket (FIFO theo mức).</summary>
    public long Sequence { get; internal set; }

    /// <summary>Thời điểm vào queue — do <see cref="IRequestQueue.Enqueue"/> gán.</summary>
    public DateTimeOffset EnqueuedAt { get; internal set; } = DateTimeOffset.UtcNow;

    /// <summary>Chỗ dispatcher báo outcome cho endpoint đang await — không dùng khi còn trong queue.</summary>
    public TaskCompletionSource<DispatchOutcome> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
```

`IRequestQueue.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;

namespace RouterBalancing.Core.Engine;

/// <summary>Priority queue toàn cục cho request chờ dispatch (spec §2.1).</summary>
public interface IRequestQueue
{
    /// <summary>Bắn sau Enqueue/TryRemove/SetPriority — dispatcher tự Take thì KHÔNG bắn (nó tự drain tiếp), Peek không bắn. Dispatcher dùng làm wake signal.</summary>
    event Action? Changed;

    /// <summary>Thêm request (gán Sequence + EnqueuedAt). Trả <see langword="false"/> nếu id đã tồn tại.</summary>
    bool Enqueue(ProxyRequest request);

    /// <summary>Xem đầu queue theo (Priority, Sequence) mà không lấy ra.</summary>
    bool Peek([NotNullWhen(true)] out ProxyRequest? request);

    /// <summary>Gỡ request theo id — dispatcher gọi sau khi đã giữ slot (atomic với cancel: ai gỡ được thì thắng). KHÔNG fire Changed — dispatcher tự drain tiếp.</summary>
    bool Take(string id, [NotNullWhen(true)] out ProxyRequest? request);

    /// <summary>Gỡ request theo id — cancel API / RequestAborted callback gọi. Fire Changed để dispatcher re-evaluate head.</summary>
    bool TryRemove(string id, [NotNullWhen(true)] out ProxyRequest? request);

    /// <summary>Đổi mức ưu tiên (chưa expose endpoint — cho UI slice sau).</summary>
    bool SetPriority(string id, RequestPriority priority);

    /// <summary>Kiểm tra id còn nằm trong queue không (endpoint dùng khi sinh id tránh va chạm).</summary>
    bool Contains(string id);

    /// <summary>Snapshot toàn bộ item theo thứ tự Priority rồi Sequence — data source cho <c>GET /v1/requests</c>.</summary>
    IReadOnlyList<ProxyRequest> Snapshot();
}
```

`RequestQueue.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// 3 bucket <see cref="SortedList{TKey,TValue}"/> theo mức ưu tiên, key = Sequence tăng dần
/// (FIFO trong cùng mức); dictionary id để tra nhanh. Mọi mutate dưới 1 lock —
/// Enqueue/TryRemove/SetPriority atomic với nhau (race cancel-vs-Take: ai lấy được lock sau thắng).
/// </summary>
public sealed class RequestQueue : IRequestQueue
{
    private readonly object _lock = new();
    private readonly SortedList<long, ProxyRequest> _highest = [];
    private readonly SortedList<long, ProxyRequest> _high = [];
    private readonly SortedList<long, ProxyRequest> _normal = [];
    private readonly Dictionary<string, ProxyRequest> _byId = new();
    private long _sequence;

    public event Action? Changed;

    public bool Enqueue(ProxyRequest request)
    {
        lock (_lock)
        {
            if (_byId.ContainsKey(request.Id))
                return false;
            request.Sequence = ++_sequence;
            request.EnqueuedAt = DateTimeOffset.UtcNow;
            Bucket(request.Priority).Add(request.Sequence, request);
            _byId[request.Id] = request;
            if (request.Priority == RequestPriority.Highest)
                DemoteOtherHighest(keep: request);
        }
        Changed?.Invoke();
        return true;
    }

    public bool Peek([NotNullWhen(true)] out ProxyRequest? request)
    {
        lock (_lock)
        {
            if (TryPeek(_highest, out request) || TryPeek(_high, out request) || TryPeek(_normal, out request))
                return true;
            request = null;
            return false;
        }
    }

    public bool Take(string id, [NotNullWhen(true)] out ProxyRequest? request) =>
        Remove(id, fireChanged: false, out request);

    public bool TryRemove(string id, [NotNullWhen(true)] out ProxyRequest? request) =>
        Remove(id, fireChanged: true, out request);

    private bool Remove(string id, bool fireChanged, [NotNullWhen(true)] out ProxyRequest? request)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var removed))
            {
                request = null;
                return false;
            }
            _byId.Remove(id);
            Bucket(removed.Priority).Remove(removed.Sequence);
            request = removed;
        }
        if (fireChanged)
            Changed?.Invoke();
        return true;
    }

    public bool SetPriority(string id, RequestPriority priority)
    {
        lock (_lock)
        {
            if (!_byId.TryGetValue(id, out var request))
                return false;
            if (request.Priority == priority)
                return true;
            Bucket(request.Priority).Remove(request.Sequence);
            request.Priority = priority;
            Bucket(priority).Add(request.Sequence, request);
            // Thăng cấp lên Highest → hạ cấp các Highest khác (luật 1-Highest có hiệu lực cả ở đây)
            if (priority == RequestPriority.Highest)
                DemoteOtherHighest(keep: request);
        }
        Changed?.Invoke();
        return true;
    }

    public bool Contains(string id)
    {
        lock (_lock)
            return _byId.ContainsKey(id);
    }

    public IReadOnlyList<ProxyRequest> Snapshot()
    {
        lock (_lock)
        {
            List<ProxyRequest> all = [.. _highest.Values, .. _high.Values, .. _normal.Values];
            return all;
        }
    }

    private void DemoteOtherHighest(ProxyRequest keep)
    {
        // Copy key trước khi sửa — không sửa SortedList khi đang enumerate
        var demote = _highest.Keys.Where(k => k != keep.Sequence).ToList();
        foreach (var key in demote)
        {
            var item = _highest[key];
            _highest.Remove(key);
            item.Priority = RequestPriority.High;
            _high.Add(key, item);
        }
    }

    private SortedList<long, ProxyRequest> Bucket(RequestPriority priority) => priority switch
    {
        RequestPriority.Highest => _highest,
        RequestPriority.High => _high,
        _ => _normal,
    };

    private static bool TryPeek(SortedList<long, ProxyRequest> bucket,
        [NotNullWhen(true)] out ProxyRequest? request)
    {
        if (bucket.Count > 0)
        {
            request = bucket.Values[0];
            return true;
        }
        request = null;
        return false;
    }
}
```

### Step 3: Chạy test

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~RequestQueueTests|FullyQualifiedName~RequestPriorityParserTests" --nologo
```

**Expected: PASS 14/14** (13 queue + 1 parser [Fact]).

### Step 4: Full suite + commit

```bash
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

**Gate: 240 passed / 0 failed.**

```bash
git add src/RouterBalancing.Core/Engine/RequestPriority.cs src/RouterBalancing.Core/Engine/RequestPriorityParser.cs src/RouterBalancing.Core/Engine/RequestId.cs src/RouterBalancing.Core/Engine/DispatchOutcome.cs src/RouterBalancing.Core/Engine/ProxyRequest.cs src/RouterBalancing.Core/Engine/IRequestQueue.cs src/RouterBalancing.Core/Engine/RequestQueue.cs "router balancing test/Engine/RequestQueueTests.cs" "router balancing test/Engine/RequestPriorityParserTests.cs"
git commit -m "feat: add request priority queue with 1-highest rule"
```

---

## Task 2 — ExecutionList (TDD)

**Files (create):**

- `src/RouterBalancing.Core/Engine/ExecutionEntry.cs` (record — tách file cho rõ)
- `src/RouterBalancing.Core/Engine/IExecutionList.cs`
- `src/RouterBalancing.Core/Engine/ExecutionList.cs`
- `router balancing test/Engine/ExecutionListTests.cs`

**Consumes:** `RequestPriority` (T1), `Provider.MaxConcurrent` (đã có, default 4), `IDbContextFactory`.
**Produces (T4, T6, T7, T8 dùng):** `IExecutionList`/`ExecutionList` singleton, `ExecutionEntry`, event `Exited`.

### Step 1: Viết test (FAIL)

`router balancing test/Engine/ExecutionListTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

public class ExecutionListTests : IDisposable
{
    private readonly TestDb _db = new();

    public ExecutionListTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    private long SeedProvider(string name, int maxConcurrent = 4)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            BaseUrl = "https://api.openai.com",
            MaxConcurrent = maxConcurrent,
        };
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider.Id;
    }

    private ExecutionList CreateSut() => new(_db.CreateFactory());

    private static DateTimeOffset Enq => DateTimeOffset.UtcNow.AddMinutes(-1);

    [Fact]
    public async Task TryEnter_WhenBelowMax_ReturnsTrueAndTracksEntryWithAllFields()
    {
        var pid = SeedProvider("p1", maxConcurrent: 2);
        var sut = CreateSut();

        var ok = await sut.TryEnterAsync(pid, "req00001", "p1", "gpt-4o-mini",
            RequestPriority.High, Enq, default);

        Assert.True(ok);
        Assert.True(sut.Contains("req00001"));
        var entry = Assert.Single(sut.Snapshot());
        Assert.Equal("req00001", entry.RequestId);
        Assert.Equal(pid, entry.ProviderId);
        Assert.Equal("p1", entry.ProviderName);
        Assert.Equal("gpt-4o-mini", entry.Model);
        Assert.Equal(RequestPriority.High, entry.Priority);
        Assert.Equal(Enq, entry.EnqueuedAt);
        Assert.True(entry.StartedAt >= entry.EnqueuedAt);
    }

    [Fact]
    public async Task TryEnter_WhenAtMax_ReturnsFalse()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1);
        var sut = CreateSut();
        Assert.True(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default));

        Assert.False(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, default));
        Assert.False(sut.Contains("req00002"));
    }

    [Fact]
    public async Task TryEnter_WhenProviderMissing_ReturnsFalse()
    {
        var sut = CreateSut();

        // Provider không tồn tại → MaxConcurrent = 0 → không bao giờ enter (spec §2.1)
        Assert.False(await sut.TryEnterAsync(999, "req00001", "ghost", "m", RequestPriority.Normal, Enq, default));
    }

    [Fact]
    public async Task TryEnter_ReflectsLatestMaxConcurrentFromDb()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1);
        var sut = CreateSut();
        Assert.True(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default));
        Assert.False(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, default));

        using (var db = _db.CreateFactory().CreateDbContext())
        {
            var p = await db.Providers.FirstAsync(x => x.Id == pid);
            p.MaxConcurrent = 2;
            await db.SaveChangesAsync();
        }

        // Không cache — giá trị mới nhất từ DB tại mỗi lần Enter (spec §2.1)
        Assert.True(await sut.TryEnterAsync(pid, "req00002", "p1", "m", RequestPriority.Normal, Enq, default));
    }

    [Fact]
    public async Task Exit_RemovesEntryAndFiresExitedOnce()
    {
        var pid = SeedProvider("p1");
        var sut = CreateSut();
        await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default);
        var fired = 0;
        sut.Exited += () => fired++;

        sut.Exit("req00001");

        Assert.Equal(1, fired);
        Assert.False(sut.Contains("req00001"));
        Assert.Equal(0, sut.GetInFlight(pid));
    }

    [Fact]
    public async Task Exit_UnknownId_DoesNotFireExited()
    {
        var pid = SeedProvider("p1");
        var sut = CreateSut();
        await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default);
        var fired = 0;
        sut.Exited += () => fired++;

        sut.Exit("ghost");

        Assert.Equal(0, fired);
        Assert.True(sut.Contains("req00001"));
    }

    [Fact]
    public async Task CanEnter_ChecksWithoutMutating()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1);
        var sut = CreateSut();

        Assert.True(await sut.CanEnterAsync(pid, default));
        Assert.Equal(0, sut.GetInFlight(pid)); // check không mutate — selector dùng được nhiều lần

        Assert.True(await sut.TryEnterAsync(pid, "req00001", "p1", "m", RequestPriority.Normal, Enq, default));
        Assert.False(await sut.CanEnterAsync(pid, default));
    }

    [Fact]
    public async Task Snapshot_ReturnsAllLiveEntriesAcrossProviders()
    {
        var p1 = SeedProvider("p1");
        var p2 = SeedProvider("p2");
        var sut = CreateSut();
        await sut.TryEnterAsync(p1, "req00001", "p1", "m1", RequestPriority.Normal, Enq, default);
        await sut.TryEnterAsync(p2, "req00002", "p2", "m2", RequestPriority.Highest, Enq, default);

        var snapshot = sut.Snapshot();

        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, e => e.RequestId == "req00001" && e.ProviderId == p1);
        Assert.Contains(snapshot, e => e.RequestId == "req00002" && e.Priority == RequestPriority.Highest);
        Assert.Equal(1, sut.GetInFlight(p1));
        Assert.Equal(1, sut.GetInFlight(p2));
    }
}
```

Chạy `--filter "FullyQualifiedName~ExecutionListTests"` → **FAIL** `CS0246`.

### Step 2: Viết implementation

`ExecutionEntry.cs`:

```csharp
namespace RouterBalancing.Core.Engine;

/// <summary>1 request đang được phục vụ — data source cho snapshot và cancel-409 (spec §2.1).</summary>
/// <param name="RequestId">Id request (8 ký tự).</param>
/// <param name="ProviderId">Provider đang giữ request.</param>
/// <param name="ProviderName">Tên provider (snapshot không cần join DB).</param>
/// <param name="Model">ModelId đang serve.</param>
/// <param name="Priority">Priority tại thời điểm enqueue.</param>
/// <param name="EnqueuedAt">Thời điểm vào queue.</param>
/// <param name="StartedAt">Thời điểm bắt đầu serve (lúc TryEnter thành công).</param>
public sealed record ExecutionEntry(
    string RequestId,
    long ProviderId,
    string ProviderName,
    string Model,
    RequestPriority Priority,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset StartedAt);
```

`IExecutionList.cs`:

```csharp
namespace RouterBalancing.Core.Engine;

/// <summary>Track request đang phục vụ + enforce <c>Provider.MaxConcurrent</c> (spec §2.1).</summary>
public interface IExecutionList
{
    /// <summary>Bắn sau mỗi Exit thành công — dispatcher dùng làm wake signal để thử request bị park.</summary>
    event Action? Exited;

    /// <summary>Hỏi xem provider còn slot không — KHÔNG mutate (selector dùng khi chọn).</summary>
    Task<bool> CanEnterAsync(long providerId, CancellationToken ct);

    /// <summary>Reserve 1 slot nếu còn chỗ (query <c>MaxConcurrent</c> mới nhất từ DB). Trả <see langword="false"/> = hết slot hoặc provider không tồn tại.</summary>
    Task<bool> TryEnterAsync(long providerId, string requestId, string providerName, string modelId,
        RequestPriority priority, DateTimeOffset enqueuedAt, CancellationToken ct);

    /// <summary>Trả slot + gỡ entry + bắn <see cref="Exited"/>. Idempotent: id không có thì không bắn event.</summary>
    void Exit(string requestId);

    /// <summary>Id có đang phục vụ không (cancel endpoint phân biệt 409 vs 404).</summary>
    bool Contains(string requestId);

    /// <summary>Số request in-flight của provider.</summary>
    int GetInFlight(long providerId);

    /// <summary>Snapshot toàn bộ entry đang phục vụ — data source cho <c>GET /v1/requests</c>.</summary>
    IReadOnlyList<ExecutionEntry> Snapshot();
}
```

`ExecutionList.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Dictionary id → entry dưới 1 lock (check-then-add atomic với Exit) —
/// local app, contention thấp nên lock đơn giản hơn ConcurrentDictionary + Interlocked.
/// </summary>
public sealed class ExecutionList(IDbContextFactory<RouterBalancingDbContext> db) : IExecutionList
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ExecutionEntry> _entries = new();

    public event Action? Exited;

    public async Task<bool> CanEnterAsync(long providerId, CancellationToken ct)
    {
        var max = await MaxConcurrentAsync(providerId, ct);
        lock (_lock)
            return CountInFlight(providerId) < max;
    }

    public async Task<bool> TryEnterAsync(long providerId, string requestId, string providerName,
        string modelId, RequestPriority priority, DateTimeOffset enqueuedAt, CancellationToken ct)
    {
        // Query tại mỗi lần Enter — chỉnh MaxConcurrent trong UI có hiệu lực ngay (spec §2.1)
        var max = await MaxConcurrentAsync(providerId, ct);
        lock (_lock)
        {
            if (CountInFlight(providerId) >= max)
                return false;
            _entries[requestId] = new ExecutionEntry(
                requestId, providerId, providerName, modelId, priority, enqueuedAt, DateTimeOffset.UtcNow);
            return true;
        }
    }

    public void Exit(string requestId)
    {
        lock (_lock)
        {
            if (!_entries.Remove(requestId))
                return;
        }
        Exited?.Invoke();
    }

    public bool Contains(string requestId)
    {
        lock (_lock)
            return _entries.ContainsKey(requestId);
    }

    public int GetInFlight(long providerId)
    {
        lock (_lock)
            return CountInFlight(providerId);
    }

    public IReadOnlyList<ExecutionEntry> Snapshot()
    {
        lock (_lock)
            return _entries.Values.ToList();
    }

    private int CountInFlight(long providerId) =>
        _entries.Values.Count(e => e.ProviderId == providerId);

    /// <summary>Provider không tồn tại → FirstOrDefault = 0 → không ai enter được (tránh serve vào provider đã xoá).</summary>
    private async Task<int> MaxConcurrentAsync(long providerId, CancellationToken ct)
    {
        using var context = await db.CreateDbContextAsync(ct);
        return await context.Providers.AsNoTracking()
            .Where(p => p.Id == providerId)
            .Select(p => p.MaxConcurrent)
            .FirstOrDefaultAsync(ct);
    }
}
```

### Step 3: Chạy test

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ExecutionListTests" --nologo
```

**Expected: PASS 8/8.**

### Step 4: Full suite + commit

```bash
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

**Gate: 248 passed / 0 failed.**

```bash
git add src/RouterBalancing.Core/Engine/ExecutionEntry.cs src/RouterBalancing.Core/Engine/IExecutionList.cs src/RouterBalancing.Core/Engine/ExecutionList.cs "router balancing test/Engine/ExecutionListTests.cs"
git commit -m "feat: add execution list enforcing per-provider concurrency"
```

---

## Task 3 — ComboResolver + move ResolveFailure (TDD)

**Files:**

- **Create** `src/RouterBalancing.Core/Engine/ResolveFailure.cs` — **move** enum từ `IModelResolver.cs` (T7 sẽ xóa file cũ; nếu không move ở đây sẽ vỡ compile khi xóa).
- **Edit** `src/RouterBalancing.Core/Engine/IModelResolver.cs` — xóa block enum, giữ records + interface (ModelResolver vẫn compile).
- **Create** `src/RouterBalancing.Core/Engine/IComboResolver.cs` — interface + `ModelCandidate` + `SelectionResult`/`SelectionSuccess`/`SelectionFailure` (mẫu 3A: records cùng file interface).
- **Create** `src/RouterBalancing.Core/Engine/ComboResolver.cs`
- **Create** `router balancing test/Engine/ComboResolverTests.cs`

**Consumes:** `ResolveFailure` (move), domain `Combo`/`ComboItem`/`ComboMode`, `ProviderType`, `ILogService`.
**Produces (T4, T6 dùng):** `IComboResolver.ResolveAsync(string model, ct) → SelectionResult`.

### Quy tắc resolve (spec §3.2 — hành vi test bám đây)

1. Query **model id thật**: `m.ModelId == model && m.Enabled && m.Provider!.Enabled` + `Include(Provider).ThenInclude(Accounts)`.
2. Rỗng → tra `Combo.Name` (items theo `Position`, recurse `TargetComboId` với `path` set — cycle Warn + bỏ item; item chết → bỏ).
3. `Finalize`: dedup `(ProviderId, ModelId)` → nếu mode `RoundRobin` sort `(Provider.Id, Model.ModelId)` (RR cần thứ tự ổn định — **không sort khi Fallback**, giữ đúng thứ tự Position/traversal) → filter `ProviderType.OpenAI` (giữ thứ tự) → còn gì → `SelectionSuccess`; chỉ còn Anthropic → `AnthropicNotSupported`; rỗng → `NotFound` (message 404 dùng **model string gốc**).
4. Mode: model path → `RoundRobin`; combo path → `combo.Mode` (của combo gốc).

### Step 1: Viết `ResolveFailure.cs` + sửa `IModelResolver.cs` + test (FAIL)

`ResolveFailure.cs` (move nguyên văn từ `IModelResolver.cs`, giữ comment):

```csharp
namespace RouterBalancing.Core.Engine;

/// <summary>Phân loại lỗi resolve model (spec 3A §2.3).</summary>
public enum ResolveFailure
{
    /// <summary>Model không tồn tại / đã tắt / provider đã tắt.</summary>
    NotFound,

    /// <summary>Model chỉ có ở provider Anthropic — chưa hỗ trợ (3E).</summary>
    AnthropicNotSupported,
}
```

`IModelResolver.cs`: xóa toàn bộ block `public enum ResolveFailure { ... }` (records + interface giữ nguyên).

`router balancing test/Engine/ComboResolverTests.cs` — 13 test:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

public class ComboResolverTests : IDisposable
{
    private readonly TestDb _db = new();

    public ComboResolverTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    private long SeedProvider(string name, ProviderType type = ProviderType.OpenAI,
        bool enabled = true, params string[] modelIds)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            Type = type,
            BaseUrl = "https://api.openai.com",
            Enabled = enabled,
        };
        foreach (var id in modelIds)
            provider.Models.Add(new Model { ModelId = id, Enabled = true });
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider.Id;
    }

    private long AddModel(long providerId, string modelId, bool enabled = true)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var model = new Model { ModelId = modelId, Enabled = enabled };
        db.Providers.Find(providerId)!.Models.Add(model);
        db.SaveChanges();
        return model.Id;
    }

    private long SeedCombo(string name, ComboMode mode,
        params (int Position, long? TargetModelId, long? TargetComboId)[] items)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var combo = new Combo { Name = name, Mode = mode };
        foreach (var (position, targetModel, targetCombo) in items)
            combo.Items.Add(new ComboItem
            {
                Position = position,
                TargetModelId = targetModel,
                TargetComboId = targetCombo,
            });
        db.Combos.Add(combo);
        db.SaveChanges();
        return combo.Id;
    }

    private void AddComboItem(long comboId, int position, long? targetModelId, long? targetComboId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        db.ComboItems.Add(new ComboItem
        {
            ComboId = comboId,
            Position = position,
            TargetModelId = targetModelId,
            TargetComboId = targetComboId,
        });
        db.SaveChanges();
    }

    private long ModelKey(string modelId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        return db.Models.First(m => m.ModelId == modelId).Id;
    }

    private ComboResolver CreateSut(out CapturingLog log)
    {
        log = new CapturingLog();
        return new(_db.CreateFactory(), log);
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

**13 test cases dưới đây — viết đầy đủ dùng helpers ở trên:**

1. `Resolve_WhenModelIdMatches_ReturnsAllOpenAiCandidatesSortedByProviderId` — seed `zeta` (OpenAI, model `shared`), `alpha` (OpenAI, model `shared`), `anth` (Anthropic, model `shared`) **theo thứ tự đó** (zeta id < alpha id); thêm 1 `ProviderAccount` cho zeta. `ResolveAsync("shared")` → `SelectionSuccess`, `Mode == RoundRobin`, 2 candidate `[zeta, alpha]` (sort theo Provider.Id — không phải Anthropic, không phải tên), `Candidates[0].Provider.Accounts` có account (Include hoạt động).

2. `Resolve_WhenModelIdNoMatch_FallsBackToComboByName` — seed provider `p1` + model `m1`; `SeedCombo("fast", RoundRobin, (1, modelM1Id, null))`; `ResolveAsync("fast")` → `SelectionSuccess`, `Mode == RoundRobin`, 1 candidate model `m1`.

3. `Resolve_WhenNothingMatches_ReturnsNotFoundWithOriginalModelId` — `ResolveAsync("ghost")` → `SelectionFailure("ghost", NotFound)`.

4. `Resolve_ComboFallbackMode_ReturnsCandidatesInPositionOrder` — seed `p1` (model `m-pos2`) rồi `p2` (model `m-pos1`) — p1 id < p2 id; combo `fb` mode `Fallback`, items: `(1, m-pos1, null)`, `(2, m-pos2, null)`; resolve → `Mode == Fallback`, candidates `[p2/m-pos1, p1/m-pos2]` — đúng Position, **không** sort theo Provider.Id.

5. `Resolve_ComboNestedTwoLevels_CombinesInTraversalOrder` — provider với 3 model `mA`, `mB`, `mC`; combo `child` (Fallback): items `(1, mA, null)`, `(2, mB, null)`; combo `root` (Fallback): items `(1, null, childId)`, `(2, mC, null)`; resolve("root") → 3 candidate `[mA, mB, mC]` (nested inline, đúng cấp) + `Mode == Fallback`.

6. `Resolve_ComboItemTargetDisabled_SkipsItem` — combo RR items: model disabled, model thuộc provider disabled, model live → chỉ trả 1 candidate live.

7. `Resolve_WhenComboContainsCycle_SkipsCyclicalItemAndLogsWarn` — seed provider + model `mX`; `aId = SeedCombo("root", RoundRobin, (1, mX, null))`; `bId = SeedCombo("child", RoundRobin, (1, mX, null))`; `AddComboItem(aId, 2, null, bId)` (A→B, B không trỏ về A — cần vòng: `AddComboItem(bId, 2, null, aId)` → B trỏ về A); resolve("root") → không treo, dedup còn 1 candidate `mX`; `log.Warns` có message chứa `"có vòng lặp"` và `"bỏ qua item"`.

8. `Resolve_WhenAllCandidatesAnthropic_ReturnsAnthropicNotSupported` — seed Anthropic provider + model `s1`; resolve("s1") → `SelectionFailure("s1", AnthropicNotSupported)`.

9. `Resolve_WhenComboYieldsOnlyAnthropic_ReturnsAnthropicNotSupported` — combo items trỏ model của provider Anthropic → `AnthropicNotSupported`.

10. `Resolve_ComboItemsAllDead_ReturnsNotFound` — combo tồn tại nhưng mọi item trỏ model disabled → `SelectionFailure(model-string-gốc, NotFound)`.

11. `Resolve_WhenModelDisabled_FallsThroughToComboWithSameName` — model `dual` disabled (provider enabled) + combo tên `dual` trỏ model `mLive` → `SelectionSuccess` 1 candidate `mLive`.

12. `Resolve_IncludesProviderAccounts_ForKeyResolution` — model match + 1 account enabled → `Candidates[0].Provider.Accounts` chứa account (3A parity: key resolve hoạt động).

13. `Resolve_WhenProviderDisabled_ReturnsNotFound` — provider disabled (có model) + không có combo → `SelectionFailure(..., NotFound)` (3A parity: 404 trước 503).

Chạy `--filter "FullyQualifiedName~ComboResolverTests"` → **FAIL** `CS0246` (interface/records chưa tồn tại).

### Step 2: Viết implementation

`IComboResolver.cs`:

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>Candidate đã resolve: provider + model tương ứng (T4 chọn, T6 serve).</summary>
public sealed record ModelCandidate(Provider Provider, Model Model);

/// <summary>Kết quả resolve theo model string (spec §3.2).</summary>
public abstract record SelectionResult;

/// <summary>Resolve thành công — danh sách candidate + mode để chọn (spec §3.3).</summary>
/// <param name="Candidates">Đã dedup + filter OpenAI; RR sort (ProviderId, ModelId), Fallback giữ thứ tự Position.</param>
/// <param name="Mode">RoundRobin với model id; combo mode khi resolve qua combo.</param>
public sealed record SelectionSuccess(IReadOnlyList<ModelCandidate> Candidates, ComboMode Mode)
    : SelectionResult;

/// <summary>Resolve thất bại — endpoint/dispatcher ghi lỗi theo <paramref name="Reason"/>.</summary>
/// <param name="ModelId">Chuỗi model client gửi (message 404 dùng chuỗi này).</param>
/// <param name="Reason">NotFound → 404; AnthropicNotSupported → 503.</param>
public sealed record SelectionFailure(string ModelId, ResolveFailure Reason) : SelectionResult;

/// <summary>Resolve model string → candidate list (model id trước, combo name sau — spec §3.2).</summary>
public interface IComboResolver
{
    /// <param name="model">Chuỗi <c>model</c> client gửi (model id hoặc tên combo).</param>
    Task<SelectionResult> ResolveAsync(string model, CancellationToken ct);
}
```

`ComboResolver.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Resolve model/combo theo spec §3.2: model id thật ưu tiên, không có thì tra Combo.Name
/// (items theo Position, recurse lồng nhau có cycle-guard).
/// </summary>
public sealed class ComboResolver(
    IDbContextFactory<RouterBalancingDbContext> db, ILogService log) : IComboResolver
{
    public async Task<SelectionResult> ResolveAsync(string model, CancellationToken ct)
    {
        var candidates = await QueryCandidatesAsync(m => m.ModelId == model, ct);
        var mode = ComboMode.RoundRobin;
        if (candidates.Count == 0)
        {
            var combo = await LoadComboAsync(model, ct);
            if (combo is null)
                return new SelectionFailure(model, ResolveFailure.NotFound);
            mode = combo.Mode;
            candidates = await ResolveComboAsync(combo, [combo.Id], ct);
        }

        return Finalize(model, candidates, mode);
    }

    /// <summary>Query model enabled + provider enabled, Include Accounts — giống 3A nhưng trả tất cả candidate.</summary>
    private async Task<List<ModelCandidate>> QueryCandidatesAsync(
        System.Linq.Expressions.Expression<Func<Model, bool>> predicate, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        var models = await context.Models.AsNoTracking()
            .Where(m => m.Enabled && m.Provider!.Enabled)
            .Where(predicate)
            .Include(m => m.Provider!)
            .ThenInclude(p => p.Accounts)
            .OrderBy(m => m.Provider!.Id)
            .ThenBy(m => m.ModelId)
            .ToListAsync(ct);
        // Map sau khi materialize — Include bị EF bỏ qua nếu có Select projection (Accounts sẽ rỗng)
        return models.Select(m => new ModelCandidate(m.Provider!, m)).ToList();
    }

    private async Task<Combo?> LoadComboAsync(string name, CancellationToken ct)
    {
        await using var context = await db.CreateDbContextAsync(ct);
        return await context.Combos.AsNoTracking()
            .Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Name == name, ct);
    }

    private async Task<List<ModelCandidate>> ResolveComboAsync(
        Combo combo, HashSet<long> path, CancellationToken ct)
    {
        var result = new List<ModelCandidate>();
        foreach (var item in combo.Items.OrderBy(i => i.Position))
        {
            if (item.TargetModelId is long modelKey)
            {
                result.AddRange(await QueryCandidatesAsync(m => m.Id == modelKey, ct));
            }
            else if (item.TargetComboId is long comboKey)
            {
                if (path.Contains(comboKey))
                {
                    // Validate lúc lưu đã chặn cycle — đây là defense, bỏ qua item gây vòng (spec §3.2)
                    log.Warn($"Combo {combo.Name} có vòng lặp - bỏ qua item {item.Id}.", LogCategory.App);
                    continue;
                }
                await using var context = await db.CreateDbContextAsync(ct);
                var child = await context.Combos.AsNoTracking()
                    .Include(c => c.Items)
                    .FirstOrDefaultAsync(c => c.Id == comboKey, ct);
                if (child is null)
                    continue;
                path.Add(comboKey);
                result.AddRange(await ResolveComboAsync(child, path, ct));
                path.Remove(comboKey); // path-based: diamond không phải cycle
            }
        }
        return result;
    }

    private static SelectionResult Finalize(string model, List<ModelCandidate> candidates, ComboMode mode)
    {
        if (candidates.Count == 0)
            return new SelectionFailure(model, ResolveFailure.NotFound);

        var deduped = candidates
            .DistinctBy(c => (c.Provider.Id, c.Model.ModelId))
            .ToList();
        // RR cần thứ tự ổn định (ProviderId, ModelId); Fallback giữ nguyên thứ tự Position (spec §3.3)
        if (mode == ComboMode.RoundRobin)
            deduped = deduped.OrderBy(c => c.Provider.Id).ThenBy(c => c.Model.ModelId).ToList();

        var openAi = deduped.Where(c => c.Provider.Type == ProviderType.OpenAI).ToList();
        if (openAi.Count > 0)
            return new SelectionSuccess(openAi, mode);

        // Chỉ còn candidate Anthropic → 503 (giữ semantics 3A, không rơi xuống 404)
        return new SelectionFailure(model, ResolveFailure.AnthropicNotSupported);
    }
}
```

`ComboResolver` **không** khai báo `using Microsoft.EntityFrameworkCore;` thừa — cần cho `Include/FirstOrDefaultAsync/ToListAsync` (giữ). `ModelCandidate(m.Provider!, m)` — `Select` sau `Include` EF tự materialize nav ✓ (không project sang anonymous).

### Step 3: Chạy test

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ComboResolverTests" --nologo
```

**Expected: PASS 13/13** (đồng thời các test cũ dùng `ResolveFailure` vẫn xanh — enum cùng namespace).

### Step 4: Full suite + commit

**Gate: 261 passed / 0 failed.**

```bash
git add src/RouterBalancing.Core/Engine/ResolveFailure.cs src/RouterBalancing.Core/Engine/IModelResolver.cs src/RouterBalancing.Core/Engine/IComboResolver.cs src/RouterBalancing.Core/Engine/ComboResolver.cs "router balancing test/Engine/ComboResolverTests.cs"
git commit -m "feat: add combo resolver with nested cycle guard"
```

---

## Task 4 — ModelSelector (TDD)

**Files (create):** `src/RouterBalancing.Core/Engine/IModelSelector.cs`, `src/RouterBalancing.Core/Engine/ModelSelector.cs`, `router balancing test/Engine/ModelSelectorTests.cs`.

**Consumes:** `SelectionSuccess` (T3), `IExecutionList` (T2), `ComboMode`.
**Produces (T6 dùng):** `TrySelectAsync` — trả `null` = **park** (không Take, item ở lại queue).

### Thuật toán (spec §3.3)

- **Fallback:** chỉ xét `Candidates[0]` (thứ tự Position) — `CanEnter` false → `null` (park chờ đúng nó, **không** nhảy kế).
- **RR:** lọc candidate `CanEnter` true → rỗng → `null`; sort `(GetInFlight, Provider.Id)`; chọn `ordered[cursor % count]` với cursor đọc- rồi-tăng (`Interlocked.Read`/`Interlocked.Increment`) — lần đầu idx 0, wrap bằng `%` → test ổn định `[A,B,A,B]`.

### Step 1: Viết test (FAIL)

`router balancing test/Engine/ModelSelectorTests.cs` — dùng **ExecutionList thật** + TestDb (không mock capacity):

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

public class ModelSelectorTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ExecutionList _executions;

    public ModelSelectorTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _executions = new ExecutionList(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    private long SeedProvider(string name, int maxConcurrent = 4)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            BaseUrl = "https://api.openai.com",
            MaxConcurrent = maxConcurrent,
        };
        provider.Models.Add(new Model { ModelId = $"m-{name}", Enabled = true });
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider.Id;
    }

    private ModelCandidate Candidate(long providerId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = db.Providers.Include(p => p.Models).First(p => p.Id == providerId);
        return new ModelCandidate(provider, provider.Models[0]);
    }

    private async Task OccupyAsync(long providerId, int times)
    {
        for (var i = 0; i < times; i++)
        {
            var ok = await _executions.TryEnterAsync(providerId, $"req{providerId}-{i}",
                "p", "m", RequestPriority.Normal, DateTimeOffset.UtcNow, default);
            Assert.True(ok);
        }
    }

    private ModelSelector CreateSut() => new(_executions);

    [Fact]
    public async Task TrySelect_RR_AllIdle_SelectsFirstByProviderIdWithCursorZero()
    {
        var a = SeedProvider("a");
        var b = SeedProvider("b");
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.RoundRobin);

        var chosen = await sut.TrySelectAsync(selection, default);

        Assert.Equal(a, chosen!.Provider.Id);
    }

    [Fact]
    public async Task TrySelect_RR_PrefersIdleCandidateOverLoadedLowerId()
    {
        var a = SeedProvider("a");
        var b = SeedProvider("b");
        await OccupyAsync(a, 1);
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.RoundRobin);

        var chosen = await sut.TrySelectAsync(selection, default);

        Assert.Equal(b, chosen!.Provider.Id);
    }

    [Fact]
    public async Task TrySelect_RR_WhenAllLoaded_PicksLeastInFlight()
    {
        var a = SeedProvider("a");
        var b = SeedProvider("b");
        await OccupyAsync(a, 2);
        await OccupyAsync(b, 1);
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.RoundRobin);

        var chosen = await sut.TrySelectAsync(selection, default);

        Assert.Equal(b, chosen!.Provider.Id);
    }

    [Fact]
    public async Task TrySelect_RR_TieRotatesByCursor()
    {
        var a = SeedProvider("a");
        var b = SeedProvider("b");
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.RoundRobin);

        var picks = new List<long>();
        for (var i = 0; i < 4; i++)
            picks.Add((await sut.TrySelectAsync(selection, default))!.Provider.Id);

        Assert.Equal([a, b, a, b], picks);
    }

    [Fact]
    public async Task TrySelect_RR_SkipsCandidateAtCapacity()
    {
        var a = SeedProvider("a", maxConcurrent: 1);
        var b = SeedProvider("b");
        await OccupyAsync(a, 1); // a đầy
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.RoundRobin);

        var chosen = await sut.TrySelectAsync(selection, default);

        Assert.Equal(b, chosen!.Provider.Id);
    }

    [Fact]
    public async Task TrySelect_RR_AllAtCapacity_ReturnsNullToPark()
    {
        var a = SeedProvider("a", maxConcurrent: 1);
        var b = SeedProvider("b", maxConcurrent: 1);
        await OccupyAsync(a, 1);
        await OccupyAsync(b, 1);
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.RoundRobin);

        Assert.Null(await sut.TrySelectAsync(selection, default));
    }

    [Fact]
    public async Task TrySelect_Fallback_ParksWhenFirstCandidateFull_WithoutJumping()
    {
        var a = SeedProvider("a", maxConcurrent: 1);
        var b = SeedProvider("b");
        var sut = CreateSut();
        var selection = new SelectionSuccess([Candidate(a), Candidate(b)], ComboMode.Fallback);

        var first = await sut.TrySelectAsync(selection, default);
        Assert.Equal(a, first!.Provider.Id);

        await OccupyAsync(a, 1); // vị trí đầu đầy
        Assert.Null(await sut.TrySelectAsync(selection, default)); // park chờ a — không nhảy sang b
        Assert.Equal(0, _executions.GetInFlight(b)); // b không bị đụng tới
    }
}
```

Chạy `--filter "FullyQualifiedName~ModelSelectorTests"` → **FAIL** `CS0246`.

### Step 2: Viết implementation

`IModelSelector.cs`:

```csharp
namespace RouterBalancing.Core.Engine;

/// <summary>Chọn 1 candidate để dispatch theo mode (spec §3.3).</summary>
public interface IModelSelector
{
    /// <summary>
    /// Chọn candidate còn slot; trả <see langword="null"/> = park (không đủ capacity —
    /// dispatcher không Take, item ở lại queue chờ <c>Exited</c>/<c>Changed</c>).
    /// </summary>
    Task<ModelCandidate?> TrySelectAsync(SelectionSuccess selection, CancellationToken ct);
}
```

`ModelSelector.cs`:

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// RR: lọc còn slot → sort (in-flight, ProviderId) → chọn theo cursor toàn cục.
/// Fallback: chỉ xét đúng vị trí đầu — đầy thì park (failover-on-error là 3C).
/// </summary>
public sealed class ModelSelector(IExecutionList executions) : IModelSelector
{
    private long _cursor;

    public async Task<ModelCandidate?> TrySelectAsync(SelectionSuccess selection, CancellationToken ct)
    {
        var candidates = selection.Candidates;
        if (candidates.Count == 0)
            return null;

        if (selection.Mode == ComboMode.Fallback)
        {
            var first = candidates[0];
            return await executions.CanEnterAsync(first.Provider.Id, ct) ? first : null;
        }

        List<ModelCandidate> eligible = [];
        foreach (var candidate in candidates)
        {
            if (await executions.CanEnterAsync(candidate.Provider.Id, ct))
                eligible.Add(candidate);
        }
        if (eligible.Count == 0)
            return null;

        var inFlight = new Dictionary<long, int>();
        foreach (var candidate in eligible)
            inFlight[candidate.Provider.Id] = executions.GetInFlight(candidate.Provider.Id);

        var ordered = eligible
            .OrderBy(c => inFlight[c.Provider.Id])
            .ThenBy(c => c.Provider.Id)
            .ToList();

        // Cursor tăng mỗi lượt chọn — tie-break RR trong nhóm load bằng nhau; test ổn định nhờ % count
        var cursor = (uint)Interlocked.Read(ref _cursor);
        Interlocked.Increment(ref _cursor);
        return ordered[(int)(cursor % (uint)ordered.Count)];
    }
}
```

Lưu ý namespace: file `Domain/Enums/ComboMode.cs` vẫn khai `namespace RouterBalancing.Core.Domain` — **không** có `RouterBalancing.Core.Domain.Enums`, đừng thêm using đó (CS0234).

### Step 3: Chạy test

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ModelSelectorTests" --nologo
```

**Expected: PASS 7/7.**

### Step 4: Full suite + commit

**Gate: 268 passed / 0 failed.**

```bash
git add src/RouterBalancing.Core/Engine/IModelSelector.cs src/RouterBalancing.Core/Engine/ModelSelector.cs "router balancing test/Engine/ModelSelectorTests.cs"
git commit -m "feat: add model selector with round robin and fallback"
```

---

## Task 5 — Tách ChatCompletionsHandler + interim endpoint (TDD)

**Gate: 266 (268 − 2)** — handler tests 10 → 8 (2 test resolve-failure chuyển sang DispatcherLoopTests ở T6).

**Files:**

- **Rewrite** `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs`
- **Edit** `src/RouterBalancing.Core/Server/ProxyApp.cs` — endpoint interim (validate → `IModelResolver` → `ForwardAsync`); **T7 sẽ thay endpoint này bằng queue-first** — giữ hành vi 3A y hệt để integration test xanh.
- **Rewrite** `router balancing test/Engine/ChatCompletionsHandlerTests.cs`

**Consumes:** `DispatchOutcome`, `PreparedChatRequest` (tạo mới), `ProviderKeyResolver`, `ChatRequestValidator`, `IModelResolver` (interim — xóa ở T7).
**Produces (T6, T7 dùng):** `PrepareAsync`, `ForwardAsync`, `internal static WriteErrorAsync`.

### Step 1: Viết test mới (FAIL/FAIL hành vi)

File `ChatCompletionsHandlerTests.cs` viết lại đầy đủ — giữ nguyên helpers 3A (`Body`, `ValidJson`, `SeedProvider`, `Ctx`, `ReadAsync`, `Upstream`, `StubUpstream`, `ThrowingUpstream`, `CapturingLog`), **bỏ** `Success()`/`StubResolver`, `Create` còn `Create(IUpstreamClient, CapturingLog?)`. 8 test:

```csharp
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;

namespace router_balancing_test.Engine;

public class ChatCompletionsHandlerTests
{
    private readonly DpapiSecretProtector _protector = new();

    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);
    private const string ValidJson = """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}]}""";

    private Provider SeedProvider(bool withKey = true)
    {
        var provider = new Provider
        {
            Name = "openai-main",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.openai.com",
        };
        provider.Models.Add(new Model { ModelId = "gpt-4o-mini", Enabled = true });
        if (withKey)
            provider.Accounts.Add(new ProviderAccount
            {
                Name = "a1",
                Enabled = true,
                ApiKeyEncrypted = _protector.Protect("sk-live"),
            });
        return provider;
    }

    private static Model ModelOf(Provider provider) => provider.Models[0];

    private static DefaultHttpContext Ctx(string? json = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(json is null ? [] : Body(json));
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<(int Status, string? ContentType, string Body)> ReadAsync(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        return (ctx.Response.StatusCode, ctx.Response.ContentType, await reader.ReadToEndAsync());
    }

    private static HttpResponseMessage Upstream(int status, string body, string mediaType = "application/json") =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class StubUpstream(Func<HttpResponseMessage> factory) : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct) =>
            Task.FromResult(factory());
    }

    private sealed class ThrowingUpstream(Exception ex) : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(ex);
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

    private ChatCompletionsHandler Create(IUpstreamClient upstream, CapturingLog? log = null) =>
        new(upstream, _protector, log ?? new CapturingLog());

    [Fact]
    public async Task PrepareAsync_WhenJsonInvalid_Returns400OpenAiShapeAndSingleWarn()
    {
        var log = new CapturingLog();
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx("{broken");

        var prepared = await sut.PrepareAsync(ctx);

        Assert.Null(prepared);
        var (status, contentType, body) = await ReadAsync(ctx);
        Assert.Equal(400, status);
        Assert.StartsWith("application/json", contentType);
        Assert.Contains("Invalid JSON body", body);
        Assert.Contains("\"invalid_request_error\"", body);
        Assert.Single(log.Warns);
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task PrepareAsync_WhenModelMissing_Returns400ParamModel()
    {
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")));
        var ctx = Ctx("""{"messages":[{"role":"user"}]}""");

        var prepared = await sut.PrepareAsync(ctx);

        Assert.Null(prepared);
        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(400, status);
        Assert.Contains("Missing required parameter: 'model'.", body);
        Assert.Contains("\"param\":\"model\"", body);
    }

    [Fact]
    public async Task PrepareAsync_WhenMessagesMissing_Returns400ParamMessages()
    {
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")));
        var ctx = Ctx("""{"model":"gpt-4o-mini"}""");

        var prepared = await sut.PrepareAsync(ctx);

        Assert.Null(prepared);
        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(400, status);
        Assert.Contains("Missing required parameter: 'messages'.", body);
        Assert.Contains("\"param\":\"messages\"", body);
    }

    [Fact]
    public async Task ForwardAsync_WhenNoEnabledKey_ReturnsError503WithoutWritingResponse()
    {
        var log = new CapturingLog();
        var provider = SeedProvider(withKey: false);
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(503, error.Status);
        Assert.Contains("No enabled API key for provider 'openai-main'", error.Message);
        Assert.Single(log.Warns);
        // Handler không tự ghi response lỗi — endpoint ghi theo outcome (spec §2.1)
        Assert.Equal(200, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstreamThrows_ReturnsError502AndLogsError()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var sut = Create(new ThrowingUpstream(new HttpRequestException("connection refused")), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(502, error.Status);
        Assert.Equal("Upstream provider request failed", error.Message);
        Assert.Single(log.Errors);
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstream429_PassesStatusAndBodyThroughWithInfoLog()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var upstreamBody = """{"error":{"message":"rate limited"}}""";
        var sut = Create(new StubUpstream(() => Upstream(429, upstreamBody)), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        var (status, contentType, body) = await ReadAsync(ctx);
        Assert.Equal(429, status);
        Assert.Equal(upstreamBody, body);
        Assert.StartsWith("application/json", contentType);
        Assert.Single(log.Infos);
        Assert.Empty(log.Warns);
    }

    [Fact]
    public async Task ForwardAsync_WhenUpstreamSse_PassesStreamBytesUnchanged()
    {
        var provider = SeedProvider();
        var sut = Create(new StubUpstream(() => Upstream(200, "data: {\"x\":1}\n\ndata: [DONE]\n\n",
            "text/event-stream")));
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        var (status, contentType, body) = await ReadAsync(ctx);
        Assert.Equal(200, status);
        Assert.StartsWith("text/event-stream", contentType);
        Assert.Equal("data: {\"x\":1}\n\ndata: [DONE]\n\n", body);
    }

    [Fact]
    public async Task ForwardAsync_WhenSuccess_LogsExactlyOneInfoWithModelAndProvider()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Single(log.Infos);
        Assert.Contains("gpt-4o-mini", log.Infos[0]);
        Assert.Contains("openai-main", log.Infos[0]);
        Assert.Contains("HTTP 200", log.Infos[0]);
        Assert.Empty(log.Warns);
        Assert.Empty(log.Errors);
    }
}
```

Chạy `--filter "FullyQualifiedName~ChatCompletionsHandlerTests"` → FAIL (handler chưa có `PrepareAsync`/`ForwardAsync`, ctor sai chữ ký).

### Step 2: Viết implementation

`ChatCompletionsHandler.cs` viết lại đầy đủ:

```csharp
using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Phần handler của pipeline queue-first (3B): <see cref="PrepareAsync"/> (buffer + validate —
/// endpoint gọi trước enqueue) và <see cref="ForwardAsync"/> (key → upstream → stream —
/// dispatcher gọi khi đã giữ slot). Singleton, không giữ state per-request.
/// </summary>
public sealed class ChatCompletionsHandler(
    IUpstreamClient upstream,
    ISecretProtector protector,
    ILogService log)
{
    /// <summary>Body đã buffer + model id đã validate — input cho enqueue.</summary>
    public sealed record PreparedChatRequest(string ModelId, byte[] Body);

    /// <summary>
    /// Buffer body rồi validate theo rule 3A. Lỗi validate → ghi 400 OpenAI-style NGAY
    /// (không qua queue, header <c>X-Request-Id</c> đã được endpoint set trước đó) và trả
    /// <see langword="null"/>; hợp lệ → trả prepared request.
    /// </summary>
    /// <param name="ctx">HttpContext của request gốc.</param>
    public async Task<PreparedChatRequest?> PrepareAsync(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;

        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(buffer, ct);
            body = buffer.ToArray();
        }

        var validation = ChatRequestValidator.Validate(body);
        if (!validation.IsValid)
        {
            var (message, param) = validation.Failure switch
            {
                ValidationFailure.MissingModel =>
                    ("Missing required parameter: 'model'.", "model"),
                ValidationFailure.MissingMessages =>
                    ("Missing required parameter: 'messages'.", "messages"),
                _ => ("Invalid JSON body", null),
            };
            log.Warn($"Yêu cầu chat không hợp lệ: {validation.Failure}.", LogCategory.Request);
            await WriteErrorAsync(ctx, 400, message, "invalid_request_error", param, null);
            return null;
        }

        return new PreparedChatRequest(validation.ModelId!, body);
    }

    /// <summary>
    /// Forward request đã resolve lên upstream và stream response về client. Trả
    /// <see cref="DispatchOutcome.Handled"/> khi đã ghi response xong, hoặc
    /// <see cref="DispatchOutcome.Error"/> (endpoint ghi JSON) cho lỗi 503/502 —
    /// KHÔNG tự ghi response lỗi. Client abort / lỗi khác để propagate cho dispatcher bắt.
    /// </summary>
    /// <param name="ctx">HttpContext gốc (để ghi status/content-type/stream).</param>
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
            // Chỉ bắt lỗi upstream thật — client tự ngắt (RequestAborted) thì propagate (hành vi 3A)
            log.Error($"Không kết nối được upstream '{provider.Name}'.", ex, LogCategory.Request);
            return new DispatchOutcome.Error(502, "Upstream provider request failed", "server_error", null, null);
        }

        using (response)
        {
            ctx.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType)
                ctx.Response.ContentType = contentType.ToString();

            await response.Content.CopyToAsync(ctx.Response.Body, ct);
            log.Info(
                $"Chuyển tiếp '{model.ModelId}' → '{provider.Name}': " +
                $"HTTP {(int)response.StatusCode} trong {stopwatch.ElapsedMilliseconds}ms",
                LogCategory.Request);
            return new DispatchOutcome.Handled();
        }
    }

    // Encoder relax giữ nguyên apostrophe (0x27) trong message — default encoder escape
    // apostrophe thành chuỗi unicode, sai contract OpenAI (spec §4) — GIỮ NGUYÊN comment 3A
    private static readonly JsonSerializerOptions ErrorJsonOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Ghi JSON lỗi OpenAI-style — tái dùng cho endpoint (validate, resolve, cancel).</summary>
    internal static async Task WriteErrorAsync(HttpContext ctx, int status, string message,
        string type, string? param, string? code)
    {
        ctx.Response.StatusCode = status;
        // Serialize trực tiếp (không WriteAsJsonAsync) để ContentType đúng như middleware: application/json
        ctx.Response.ContentType = "application/json";
        var payload = JsonSerializer.Serialize(new { error = new { message, type, param, code } }, ErrorJsonOptions);
        await ctx.Response.WriteAsync(payload, ctx.RequestAborted);
    }
}
```

`ProxyApp.cs` — sửa **usings** (thêm `using RouterBalancing.Core.Logging;`) và thay endpoint chat bằng interim:

```csharp
        // Interim 3B (T5): validate → resolve (3A) → forward. T7 thay bằng queue-first.
        app.MapPost("/v1/chat/completions",
            async (HttpContext ctx, IModelResolver resolver, ILogService log,
                ChatCompletionsHandler handler) =>
        {
            var prepared = await handler.PrepareAsync(ctx);
            if (prepared is null)
                return;

            var resolved = await resolver.ResolveAsync(prepared.ModelId, ctx.RequestAborted);
            if (resolved is ModelResolveFailure failure)
            {
                if (failure.Reason == ResolveFailure.NotFound)
                {
                    log.Warn($"Model '{failure.ModelId}' không tồn tại hoặc đã tắt.", LogCategory.Request);
                    await ChatCompletionsHandler.WriteErrorAsync(ctx, 404,
                        $"The model '{failure.ModelId}' does not exist",
                        "invalid_request_error", "model", "model_not_found");
                }
                else
                {
                    log.Warn($"Model '{failure.ModelId}' thuộc provider Anthropic — chưa hỗ trợ (3E).",
                        LogCategory.Request);
                    await ChatCompletionsHandler.WriteErrorAsync(ctx, 503,
                        $"The model '{failure.ModelId}' is not supported yet", "server_error", null, null);
                }

                return;
            }

            var success = (ModelResolveSuccess)resolved;
            var outcome = await handler.ForwardAsync(ctx, success.Provider, success.Model,
                prepared.Body, ctx.RequestAborted);
            if (outcome is DispatchOutcome.Error error)
            {
                await ChatCompletionsHandler.WriteErrorAsync(ctx, error.Status, error.Message,
                    error.Type, error.Param, error.Code);
            }
        });
```

### Step 3: Chạy test

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ChatCompletionsHandlerTests" --nologo
```

**Expected: PASS 8/8.**

### Step 4: Full suite + commit

**Gate: 266 passed / 0 failed** (integration 8 test 3A đi qua interim endpoint — hành vi giữ nguyên).

```bash
git add src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/Engine/ChatCompletionsHandlerTests.cs"
git commit -m "refactor: split chat handler into prepare and forward"
```

---

## Task 6 — DispatcherLoop (TDD)

**Files (create):** `src/RouterBalancing.Core/Engine/DispatcherLoop.cs`, `router balancing test/Engine/DispatcherLoopTests.cs`.

**Consumes:** toàn bộ T1–T5 (`IRequestQueue`, `IExecutionList`, `IComboResolver`, `IModelSelector`, `ChatCompletionsHandler`, `DispatchOutcome`).
**Produces (T7):** hosted service đăng ký DI.

### Step 1: Viết test (FAIL)

`router balancing test/Engine/DispatcherLoopTests.cs` — dựng queue/execution **thật** + TestDb (MaxConcurrent query DB thật), resolver/selector/upstream là doubles:

```csharp
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Engine;

public class DispatcherLoopTests : IDisposable
{
    private const string SseBody = "data: {\"x\":1}\n\ndata: [DONE]\n\n";

    private readonly TestDb _db = new();
    private readonly RequestQueue _queue = new();
    private readonly ExecutionList _executions;
    private readonly DpapiSecretProtector _protector = new();
    private DispatcherLoop? _loop;

    public DispatcherLoopTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _executions = new ExecutionList(_db.CreateFactory());
    }

    public void Dispose()
    {
        // StopAsync (IHostedService) trả Task — không có AsTask(); ?. nuốt cả chuỗi khi loop chưa khởi tạo
        _loop?.StopAsync().GetAwaiter().GetResult();
        _db.Dispose();
    }

    private long SeedProvider(string name, int maxConcurrent = 4, string modelId = "m1")
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            BaseUrl = "https://api.openai.com",
            MaxConcurrent = maxConcurrent,
        };
        provider.Models.Add(new Model { ModelId = modelId, Enabled = true });
        provider.Accounts.Add(new ProviderAccount
        {
            Name = "a1",
            Enabled = true,
            ApiKeyEncrypted = _protector.Protect("sk-live"),
        });
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider.Id;
    }

    private ModelCandidate Candidate(long providerId)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = db.Providers.Include(p => p.Models).First(p => p.Id == providerId);
        return new ModelCandidate(provider, provider.Models[0]);
    }

    private static ProxyRequest Req(string id)
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return new ProxyRequest(id, RequestPriority.Normal, "m1", Encoding.UTF8.GetBytes("{}"), ctx);
    }

    private async Task StartAsync(IComboResolver resolver, IModelSelector selector,
        IUpstreamClient upstream, CapturingLog log)
    {
        var handler = new ChatCompletionsHandler(upstream, _protector, log);
        _loop = new DispatcherLoop(_queue, _executions, resolver, selector, handler, log);
        await _loop.StartAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
            await Task.Delay(50);
        Assert.True(condition(), "điều kiện không đạt trong 5s");
    }

    private sealed class StubResolver(SelectionResult result) : IComboResolver
    {
        public Task<SelectionResult> ResolveAsync(string model, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class ThrowingOnceResolver(SelectionResult result) : IComboResolver
    {
        private int _calls;

        public Task<SelectionResult> ResolveAsync(string model, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new InvalidOperationException("boom");
            return Task.FromResult(result);
        }
    }

    private sealed class GatingResolver(SelectionResult result) : IComboResolver
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void OpenGate() => _gate.TrySetResult();

        public Task<SelectionResult> ResolveAsync(string model, CancellationToken ct)
        {
            _entered.TrySetResult();
            return AwaitGateAsync();
        }

        private async Task<SelectionResult> AwaitGateAsync()
        {
            await _gate.Task;
            return result;
        }
    }

    private sealed class CountingSelector(IModelSelector inner) : IModelSelector
    {
        public int Calls;

        public Task<ModelCandidate?> TrySelectAsync(SelectionSuccess selection, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return inner.TrySelectAsync(selection, ct);
        }
    }

    private sealed class StubUpstream : IUpstreamClient
    {
        public int Calls;

        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SseBody, Encoding.UTF8, "text/event-stream"),
            });
        }
    }

    private sealed class GatedUpstream : IUpstreamClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            _entered.TrySetResult();
            await _release.Task;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SseBody, Encoding.UTF8, "text/event-stream"),
            };
        }
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

    [Fact]
    public async Task Loop_WhenRequestEnqueued_ResolvesSelectsServesAndCompletesHandled()
    {
        var pid = SeedProvider("p1");
        var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new StubUpstream();
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.False(_queue.Contains("req00001"));
        Assert.False(_executions.Contains("req00001"));
        Assert.Equal(1, upstream.Calls);
        Assert.Single(log.Infos);
        Assert.Contains("m1", log.Infos[0]);
        Assert.Contains("p1", log.Infos[0]);
    }

    [Fact]
    public async Task Loop_WhenProviderSaturated_ParksOnceAndWakesOnExited()
    {
        var pid = SeedProvider("p1", maxConcurrent: 1);
        var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new GatedUpstream();
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var r1 = Req("req00001");
        var r2 = Req("req00002");
        _queue.Enqueue(r1);
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // r1 giữ trọn slot
        _queue.Enqueue(r2);

        // Park: đúng 1 lần thử chọn cho r2 rồi đứng yên — KHÔNG polling capacity lặp lại
        await Task.Delay(300);
        Assert.Equal(2, selector.Calls);
        Assert.False(_executions.Contains("req00002"));
        Assert.False(r2.Completion.Task.IsCompleted);

        // r1 xong → Exit bắn Exited → wake → r2 được serve (park vô hạn, không 503)
        upstream.Release();
        Assert.IsType<DispatchOutcome.Handled>(await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<DispatchOutcome.Handled>(await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, _executions.GetInFlight(pid));
    }

    [Fact]
    public async Task Loop_WhenClientAbortsWhileQueued_DoesNotServeAndKeepsRunning()
    {
        var pid = SeedProvider("p1");
        var resolver = new GatingResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new StubUpstream();
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        // Callback copy đúng logic Register của endpoint chat (T7) — spec §3.4/§5 "RequestAborted
        // queued": còn trong queue → TryRemove + log Info + Cancelled, không serve
        using var abort = new CancellationTokenSource();
        var r1 = Req("req00001");
        r1.Context.RequestAborted = abort.Token;
        r1.Context.RequestAborted.Register(() =>
        {
            if (_queue.TryRemove(r1.Id, out var removed))
            {
                log.Info($"Request {r1.Id} bị client ngắt khi đang chờ.", LogCategory.Request);
                removed.Completion.TrySetResult(new DispatchOutcome.Cancelled());
            }
        });
        _queue.Enqueue(r1);
        await resolver.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // dispatcher kẹt trong resolve

        // Client ngắt giữa chừng: TryRemove thắng trước Take — race một bên thắng, không item lơ lửng
        abort.Cancel();
        resolver.OpenGate(); // nhả dispatcher — Take fail → Exit slot + bỏ qua (spec §3.4)

        var r2 = Req("req00002");
        _queue.Enqueue(r2);
        var outcome2 = await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsType<DispatchOutcome.Handled>(outcome2);
        Assert.IsType<DispatchOutcome.Cancelled>(await r1.Completion.Task);
        Assert.False(_queue.Contains("req00001"));
        Assert.Equal(1, upstream.Calls); // chỉ r2 tới upstream — r1 không bao giờ được serve
        Assert.False(_executions.Contains("req00001"));
        Assert.Contains(log.Infos, m => m.Contains(r1.Id) && m.Contains("ngắt"));
    }

    [Fact]
    public async Task Loop_WhenResolveReturnsNotFound_TakesItemAndCompletesError404()
    {
        var resolver = new StubResolver(new SelectionFailure("m1", ResolveFailure.NotFound));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, new StubUpstream(), log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(404, error.Status);
        Assert.Equal("The model 'm1' does not exist", error.Message);
        Assert.Equal("model", error.Param);
        Assert.Equal("model_not_found", error.Code);
        Assert.False(_queue.Contains("req00001")); // item được gỡ khỏi queue khi resolve fail
        Assert.Single(log.Warns);
        Assert.Contains("không tồn tại", log.Warns[0]);
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task Loop_WhenResolveReturnsAnthropic_CompletesError503()
    {
        var resolver = new StubResolver(new SelectionFailure("m1", ResolveFailure.AnthropicNotSupported));
        var selector = new CountingSelector(new ModelSelector(_executions));
        var log = new CapturingLog();
        await StartAsync(resolver, selector, new StubUpstream(), log);

        var request = Req("req00001");
        _queue.Enqueue(request);

        var outcome = await request.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var error = Assert.IsType<DispatchOutcome.Error>(outcome);
        Assert.Equal(503, error.Status);
        Assert.Equal("The model 'm1' is not supported yet", error.Message);
        Assert.Equal("server_error", error.Type);
        Assert.Null(error.Param);
        Assert.Null(error.Code);
        Assert.Single(log.Warns);
        Assert.Contains("Anthropic", log.Warns[0]);
    }

    [Fact]
    public async Task Loop_WhenResolverThrows_LogsErrorAndKeepsServing()
    {
        var pid = SeedProvider("p1");
        var success = new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin);
        var resolver = new ThrowingOnceResolver(success);
        var selector = new CountingSelector(new ModelSelector(_executions));
        var upstream = new StubUpstream();
        var log = new CapturingLog();
        await StartAsync(resolver, selector, upstream, log);

        var r1 = Req("req00001");
        _queue.Enqueue(r1);
        await WaitUntilAsync(() => log.Errors.Count == 1);

        Assert.Contains("Lỗi dispatcher", log.Errors[0]);
        Assert.True(_queue.Contains("req00001")); // exception trước Take — item giữ nguyên

        // Event sau (enqueue r2) → retry r1 (lần gọi resolver thứ 2 thành công) → cả 2 được serve
        var r2 = Req("req00002");
        _queue.Enqueue(r2);

        Assert.IsType<DispatchOutcome.Handled>(await r1.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<DispatchOutcome.Handled>(await r2.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, upstream.Calls);
        Assert.Single(log.Errors);
    }
}
```

Chạy `--filter "FullyQualifiedName~DispatcherLoopTests"` → FAIL `CS0246` (`DispatcherLoop` chưa tồn tại).

### Step 2: Viết implementation

`src/RouterBalancing.Core/Engine/DispatcherLoop.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Queue-first dispatcher (spec §2.1): chờ event → Peek → resolve → chọn → TryEnter → Take
/// atomic → serve song song. Thiếu capacity: KHÔNG Take (item ở lại queue — park vô hạn),
/// chờ <see cref="IRequestQueue.Changed"/> | <see cref="IExecutionList.Exited"/> đánh thức.
/// </summary>
public sealed class DispatcherLoop(
    IRequestQueue queue,
    IExecutionList executions,
    IComboResolver resolver,
    IModelSelector selector,
    ChatCompletionsHandler handler,
    ILogService log) : BackgroundService
{
    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Subscribe TRƯỚC lần drain đầu — event xảy ra giữa "queue rỗng" và "chờ" không bị mất
        queue.Changed += OnWake;
        executions.Exited += OnWake;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    while (await TryDispatchOnceAsync(stoppingToken))
                    {
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Vòng lặp phải sống: item đầu giữ nguyên, event sau sẽ retry (spec §4)
                    log.Error($"Lỗi dispatcher: {ex.Message}", ex, LogCategory.App);
                }

                try
                {
                    await _wake.WaitAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            queue.Changed -= OnWake;
            executions.Exited -= OnWake;
        }
    }

    /// <summary>Event handler — chỉ Release, không làm gì đồng bộ (bắn từ thread khác).</summary>
    private void OnWake() => _wake.Release();

    /// <summary>Một vòng dispatch. Trả <see langword="true"/> nếu có tiến triển (drain tiếp), false = park/rỗng → chờ event.</summary>
    private async Task<bool> TryDispatchOnceAsync(CancellationToken ct)
    {
        if (!queue.Peek(out var request))
            return false;

        var selection = await resolver.ResolveAsync(request.Model, ct);
        if (selection is SelectionFailure failure)
        {
            // Resolve fail = xong xét — gỡ khỏi queue rồi báo outcome để endpoint ghi lỗi
            if (!queue.TryRemove(request.Id, out _))
                return true; // vừa bị cancel/abort gỡ — bên kia đã báo outcome rồi
            LogResolveFailure(failure);
            request.Completion.TrySetResult(CreateResolveError(failure));
            return true;
        }

        var success = (SelectionSuccess)selection;
        var candidate = await selector.TrySelectAsync(success, ct);
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

        _ = ServeAsync(taken, candidate);
        return true;
    }

    private async Task ServeAsync(ProxyRequest request, ModelCandidate candidate)
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
            log.Error($"Lỗi dispatcher: {ex.Message}", ex, LogCategory.Request);
            outcome = new DispatchOutcome.Error(500, "Internal server error", "server_error", null, null);
        }
        finally
        {
            // Exit TRƯỚC khi báo outcome — cancel endpoint kiểm tra Contains thấy đúng ngay (spec §2.3)
            executions.Exit(request.Id);
        }

        request.Completion.TrySetResult(outcome);
    }

    /// <summary>Payload lỗi resolve — GIỮ NGUYÊN message/type/param/code 3A, chỉ đổi nơi gọi (spec §4).</summary>
    private static DispatchOutcome.Error CreateResolveError(SelectionFailure failure) =>
        failure.Reason == ResolveFailure.NotFound
            ? new(404, $"The model '{failure.ModelId}' does not exist",
                "invalid_request_error", "model", "model_not_found")
            : new(503, $"The model '{failure.ModelId}' is not supported yet",
                "server_error", null, null);

    private void LogResolveFailure(SelectionFailure failure)
    {
        if (failure.Reason == ResolveFailure.NotFound)
            log.Warn($"Model '{failure.ModelId}' không tồn tại hoặc đã tắt.", LogCategory.Request);
        else
            log.Warn($"Model '{failure.ModelId}' thuộc provider Anthropic — chưa hỗ trợ (3E).",
                LogCategory.Request);
    }
}
```

### Step 3: Chạy test

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~DispatcherLoopTests" --nologo
```

**Expected: PASS 6/6.** Lưu ý: nếu test park flaky (selector.Calls ≠ 2) → kiểm tra lại `Take` **không** fire `Changed` (chốt kỹ thuật §2) trước khi chỉnh test.

### Step 4: Full suite + commit

**Gate: 272 passed / 0 failed.**

```bash
git add src/RouterBalancing.Core/Engine/DispatcherLoop.cs "router balancing test/Engine/DispatcherLoopTests.cs"
git commit -m "feat: add queue-first dispatcher loop with park and wake"
```

---

## Task 7 — Wire queue-first pipeline + bỏ ModelResolver (wiring)

**Files (edit):** `src/RouterBalancing.Core/Server/ProxyApp.cs`.
**Files (delete):** `src/RouterBalancing.Core/Engine/ModelResolver.cs`, `src/RouterBalancing.Core/Engine/IModelResolver.cs`, `router balancing test/Engine/ModelResolverTests.cs`.

**Consumes:** T1–T6 (đủ type + dispatcher singleton sống trong HostedService); T3 đã **move enum `ResolveFailure`** sang `ResolveFailure.cs` → xóa `IModelResolver.cs` không vỡ compile (records `ModelResolveSuccess/Failure` khi đó chỉ còn trong 3 file sắp xóa + endpoint interim trong `ProxyApp` — Step 1 thay endpoint trước, Step 2 mới xóa).
**Produces (T8):** endpoint chat queue-first đã đăng ký DI — control API map tiếp vào `ConfigurePipeline`.

### Step 1: Đổi DI + thay endpoint

`ProxyApp.cs` — trong `ConfigureServices`, **bỏ** dòng `AddSingleton<IModelResolver, ModelResolver>()`, **đổi** thành block queue-first (giữ nguyên `AddSingleton(protector)` + block `AddHttpClient` phía trên):

```csharp
        // Queue-first 3B (spec §2.1): endpoint chỉ enqueue + chờ outcome;
        // DispatcherLoop (hosted service) resolve → chọn → serve.
        builder.Services.AddSingleton<IRequestQueue, RequestQueue>();
        builder.Services.AddSingleton<IExecutionList, ExecutionList>();
        builder.Services.AddSingleton<IComboResolver, ComboResolver>();
        builder.Services.AddSingleton<IModelSelector, ModelSelector>();
        builder.Services.AddSingleton<IUpstreamClient, OpenAiUpstreamClient>();
        builder.Services.AddSingleton<ChatCompletionsHandler>();
        builder.Services.AddHostedService<DispatcherLoop>();
```

Trong `ConfigurePipeline`, **thay endpoint chat interim** (toàn bộ lambda T5) bằng:

```csharp
        // Queue-first 3B (spec §2.1): validate → enqueue → chờ dispatcher → ghi response theo outcome
        app.MapPost("/v1/chat/completions",
            async (HttpContext ctx, IRequestQueue queue, IExecutionList executions,
                ChatCompletionsHandler handler, ILogService log) =>
        {
            // Sinh id TRƯỚC prepare — trả X-Request-Id để client đối chiếu với GET /v1/requests (spec §3.6)
            string id;
            do
            {
                id = RequestId.New();
            }
            while (queue.Contains(id) || executions.Contains(id));
            ctx.Response.Headers["X-Request-Id"] = id;

            var prepared = await handler.PrepareAsync(ctx);
            if (prepared is null)
                return; // validate fail — PrepareAsync đã ghi 400, chưa enqueue

            var priority = RequestPriorityParser.Parse(ctx.Request.Headers["X-Priority"].ToString());
            var request = new ProxyRequest(id, priority, prepared.ModelId, prepared.Body, ctx);

            if (!queue.Enqueue(request))
            {
                // id vừa sinh nên gần như không xảy ra — không được nuốt im lặng
                log.Warn($"Không enqueue được request {id}.", LogCategory.Request);
                await ChatCompletionsHandler.WriteErrorAsync(ctx, 500, "Internal server error",
                    "server_error", null, null);
                return;
            }

            // Đăng ký SAU Enqueue: dispatcher đã Take thì TryRemove false → serve tự cắt stream (spec §3.4)
            ctx.RequestAborted.Register(() =>
            {
                if (queue.TryRemove(id, out var removed))
                {
                    log.Info($"Request {id} bị client ngắt khi đang chờ.", LogCategory.Request);
                    removed.Completion.TrySetResult(new DispatchOutcome.Cancelled());
                }
            });

            var outcome = await request.Completion.Task;

            if (outcome is DispatchOutcome.Error error)
            {
                await ChatCompletionsHandler.WriteErrorAsync(ctx, error.Status, error.Message,
                    error.Type, error.Param, error.Code);
            }
            else if (outcome is DispatchOutcome.Cancelled
                     && !ctx.RequestAborted.IsCancellationRequested)
            {
                // Huỷ qua control API (client còn kết nối) — ghi log + 400 request_cancelled (spec §5)
                log.Info($"Đã huỷ request {id} (đang chờ), model {prepared.ModelId}.",
                    LogCategory.Request);
                await ChatCompletionsHandler.WriteErrorAsync(ctx, 400, "Request cancelled.",
                    "invalid_request_error", null, "request_cancelled");
            }
            // Handled / Aborted / Cancelled do client ngắt: response đã ghi hoặc kết nối đã đóng
        });
```

Usings của `ProxyApp.cs` đã đủ từ T5 (`RouterBalancing.Core.Logging` thêm ở T5); **không** thêm using mới.

### Step 2: Xóa ModelResolver

```bash
git rm src/RouterBalancing.Core/Engine/ModelResolver.cs src/RouterBalancing.Core/Engine/IModelResolver.cs "router balancing test/Engine/ModelResolverTests.cs"
```

Verify không còn tham chiếu (kết quả mong đợi: **rỗng**):

```bash
Select-String -Path (Get-ChildItem -Recurse -Filter *.cs src, "router balancing test").FullName -Pattern "IModelResolver|ModelResolve"
```

Lưu ý: `ResolveFailure` **vẫn còn** (đã move sang `ResolveFailure.cs` ở T3) — pattern trên không match nó.

### Step 3: Chạy test

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProxyAppChatIntegrationTests" --nologo
```

**Expected: PASS 8/8** — 8 integration test 3A đi qua endpoint queue-first (404/503/502/validate vẫn y hệt payload 3A).

```bash
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

**Expected: PASS 265** (272 − 7 `ModelResolverTests`).

### Step 4: Full gate + commit

**Gate: 265 passed / 0 failed.**

```bash
git add src/RouterBalancing.Core/Server/ProxyApp.cs src/RouterBalancing.Core/Engine/ModelResolver.cs src/RouterBalancing.Core/Engine/IModelResolver.cs "router balancing test/Engine/ModelResolverTests.cs"
git commit -m "refactor: wire queue-first chat pipeline and drop model resolver"
```

---

## Task 8 — Control API: snapshot + cancel (TDD)

**Files (create):** `router balancing test/Server/ProxyControlApiTests.cs`.
**Files (edit):** `src/RouterBalancing.Core/Server/ProxyApp.cs` (`MapEndpoints`).

**Consumes:** T7 (queue/execution đã DI), `IRequestQueue.Snapshot`, `IExecutionList.Snapshot/Contains`, `DispatchOutcome.Cancelled`, `WriteErrorAsync`.
**Produces (T9):** 2 endpoint — integration test dùng để discovery id.

Contract theo spec §3.4–3.5 (giữ nguyên text này):
- `GET /v1/requests` → `{"requests":[{id,state,priority,model,provider,enqueuedAt,startedAt,elapsedMs,cancelable}]}` — `state` = `queued`|`serving`; `provider` null khi queued; `elapsedMs` tính từ `enqueuedAt` (queued) hoặc `startedAt` (serving); `cancelable = state=="queued"`; priority map theo **vocab input** `X-Priority`: `normal`/`high`/`max` (`Highest → "max"` — client gửi thẳng lại được header, parser chỉ nhận `high`/`max`; xem chốt kỹ thuật §10).
- `POST /v1/requests/{id}/cancel` — trong queue → `TryRemove` atomic → TCS `Cancelled` → **200** `{"cancelled":true}`; đang serving → **409** `code:"not_cancellable"`; không tồn tại ở cả 2 → **404** `code:"request_not_found"`.

### Step 1: Viết test (FAIL)

`router balancing test/Server/ProxyControlApiTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
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
/// Control API 3B (spec §3.4–3.5): GET /v1/requests + POST /v1/requests/{id}/cancel.
/// </summary>
public class ProxyControlApiTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly DpapiSecretProtector _protector = new();
    private WebApplication? _app;
    private HttpClient? _client;

    public ProxyControlApiTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory, new DpapiSecretProtector());
        _log = new LogService(factory);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _log.Dispose();
        _settings.Dispose();
        _db.Dispose();
    }

    private void SeedProvider(int maxConcurrent, params string[] modelIds)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = "p-main",
            BaseUrl = "https://api.openai.com",
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

    private sealed class StubUpstream : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct) =>
            Task.FromResult(Sse());
    }

    private sealed class GatedUpstream : IUpstreamClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            _entered.TrySetResult();
            await _release.Task;
            return Sse();
        }
    }

    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"x\":1}\n\ndata: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

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

    private Task<HttpResponseMessage> CancelAsync(string id) =>
        _client!.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/v1/requests/{id}/cancel"));

    /// <summary>Chờ request thực sự vào queue (dispatcher có thể đang park) — poll tối đa 5s.</summary>
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

    private async Task<string> FindRequestIdAsync(string state)
    {
        var response = await _client!.GetAsync("/v1/requests");
        var requests = (await ReadJson(response)).GetProperty("requests").EnumerateArray();
        return requests.Single(r => r.GetProperty("state").GetString() == state)
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Snapshot_WhenEmpty_ReturnsEmptyArray()
    {
        var client = await StartAsync(new StubUpstream());

        var response = await client.GetAsync("/v1/requests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Empty(json.GetProperty("requests").EnumerateArray().ToList());
    }

    [Fact]
    public async Task Snapshot_WhenQueuedAndServing_ShowsBothStatesAndCancelable()
    {
        // 1 provider, MaxConcurrent=1: giữ slot bằng request đang serve → request thứ 2 park trong queue
        SeedProvider(maxConcurrent: 1, "m-served", "m-queued");
        var upstream = new GatedUpstream();
        var client = await StartAsync(upstream);

        var servingTask = client.PostAsync("/v1/chat/completions", ChatBody("m-served"));
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var parkedTask = client.PostAsync("/v1/chat/completions", ChatBody("m-queued"));
        var queuedId = await WaitForQueuedIdAsync("m-queued");

        var response = await client.GetAsync("/v1/requests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var requests = (await ReadJson(response)).GetProperty("requests")
            .EnumerateArray().ToList();
        Assert.Equal(2, requests.Count);
        var serving = requests.Single(r => r.GetProperty("state").GetString() == "serving");
        var queued = requests.Single(r => r.GetProperty("state").GetString() == "queued");

        Assert.False(serving.GetProperty("cancelable").GetBoolean());
        Assert.Equal("p-main", serving.GetProperty("provider").GetString());
        Assert.NotEqual(JsonValueKind.Null, serving.GetProperty("startedAt").ValueKind);

        Assert.True(queued.GetProperty("cancelable").GetBoolean());
        Assert.Equal(queuedId, queued.GetProperty("id").GetString());
        Assert.Equal(JsonValueKind.Null, queued.GetProperty("provider").ValueKind);
        Assert.Equal(JsonValueKind.Null, queued.GetProperty("startedAt").ValueKind);

        // Dọn để test không treo Dispose: huỷ request đang chờ, mở gate cho request đang serve
        var cancel = await CancelAsync(queuedId);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await parkedTask).StatusCode);
        upstream.Release();
        Assert.Equal(HttpStatusCode.OK, (await servingTask).StatusCode);
    }

    [Fact]
    public async Task Cancel_WhenIdUnknown_Returns404RequestNotFound()
    {
        var client = await StartAsync(new StubUpstream());

        var response = await CancelAsync("zzzzzzzz");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("request_not_found", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Cancel_WhenRequestIsServing_Returns409NotCancellable()
    {
        SeedProvider(maxConcurrent: 1, "m1");
        var upstream = new GatedUpstream();
        var client = await StartAsync(upstream);

        var pending = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var servingId = await FindRequestIdAsync("serving");

        var response = await CancelAsync(servingId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("not_cancellable", json.GetProperty("error").GetProperty("code").GetString());

        upstream.Release();
        Assert.Equal(HttpStatusCode.OK, (await pending).StatusCode);
    }

    [Fact]
    public async Task Cancel_WhenRequestIsQueued_Returns200AndOriginGets400()
    {
        // MaxConcurrent=0: resolve được nhưng không bao giờ vào serve — request kẹt trong queue
        SeedProvider(maxConcurrent: 0, "m1");
        var client = await StartAsync(new StubUpstream());

        var pending = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        var queuedId = await WaitForQueuedIdAsync("m1");

        var response = await CancelAsync(queuedId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJson(response);
        Assert.True(json.GetProperty("cancelled").GetBoolean());

        // Endpoint gốc đang await TCS → nhận Cancelled → ghi 400 request_cancelled (spec §3.4)
        var origin = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.BadRequest, origin.StatusCode);
        var error = (await ReadJson(origin)).GetProperty("error");
        Assert.Equal("request_cancelled", error.GetProperty("code").GetString());
        Assert.Equal(queuedId, origin.Headers.GetValues("X-Request-Id").Single());
    }
}
```

Chạy `--filter "FullyQualifiedName~ProxyControlApiTests"` → FAIL (route chưa có: 404 không có payload `error`, snapshot shape sai).

### Step 2: Viết implementation

`ProxyApp.MapEndpoints` — thêm 2 endpoint (sau endpoint `/v1/models`):

```csharp
        // Snapshot request đang chờ + đang phục vụ (spec §3.5) — đọc 2 nguồn trong 1 lần,
        // KHÔNG đồng bộ hóa: id có thể chuyển state giữa 2 lần đọc, cancel tự chịu 404/409
        app.MapGet("/v1/requests", (IRequestQueue queue, IExecutionList executions) =>
        {
            var now = DateTimeOffset.UtcNow;
            var queued = queue.Snapshot().Select(r => new
            {
                id = r.Id,
                state = "queued",
                priority = SnapshotPriority(r.Priority),
                model = r.Model,
                provider = (string?)null,
                enqueuedAt = r.EnqueuedAt,
                startedAt = (DateTimeOffset?)null,
                elapsedMs = (long)(now - r.EnqueuedAt).TotalMilliseconds,
                cancelable = true,
            });
            var serving = executions.Snapshot().Select(e => new
            {
                id = e.RequestId,
                state = "serving",
                priority = SnapshotPriority(e.Priority),
                model = e.Model,
                provider = (string?)e.ProviderName,
                enqueuedAt = e.EnqueuedAt,
                startedAt = (DateTimeOffset?)e.StartedAt,
                elapsedMs = (long)(now - e.StartedAt).TotalMilliseconds,
                cancelable = false,
            });

            return Results.Json(new { requests = queued.Concat(serving).ToList() });
        });

        // Chỉ huỷ được request còn trong queue (spec §3.4): 200 / 409 / 404
        app.MapPost("/v1/requests/{id}/cancel", async (string id, HttpContext ctx,
            IRequestQueue queue, IExecutionList executions) =>
        {
            // TryRemove atomic với Take — thắng thì 200, thua thì rơi vào nhánh 409/404
            if (queue.TryRemove(id, out var removed))
            {
                removed.Completion.TrySetResult(new DispatchOutcome.Cancelled());
                await Results.Json(new { cancelled = true }).ExecuteAsync(ctx);
                return;
            }

            if (executions.Contains(id))
            {
                await ChatCompletionsHandler.WriteErrorAsync(ctx, 409,
                    $"The request '{id}' is not cancellable", "invalid_request_error", null,
                    "not_cancellable");
                return;
            }

            await ChatCompletionsHandler.WriteErrorAsync(ctx, 404,
                $"The request '{id}' does not exist", "invalid_request_error", null,
                "request_not_found");
        });
```

Thêm helper **private static** trong class `ProxyApp` (gọi từ 2 lambda trên):

```csharp
    /// <summary>
    /// Snapshot map ngược vocab input: Highest → "max" (không phải "highest") để client
    /// gửi thẳng giá trị này lại làm <c>X-Priority</c> — parser chỉ nhận high/max (chốt kỹ thuật §10).
    /// </summary>
    private static string SnapshotPriority(RequestPriority priority) => priority switch
    {
        RequestPriority.Highest => "max",
        RequestPriority.High => "high",
        _ => "normal",
    };
```

Lưu ý biên dịch:
- 2 anonymous type phải **cùng tên/kiểu/thứ tự property** để `Concat` hợp lệ — `provider`/`startedAt` cast tường minh `(string?)`/`(DateTimeOffset?)` ở **cả 2** nhánh.
- Không assert format chuỗi ISO trong test (STJ có thể viết `+00:00` hoặc `Z`) — test chỉ check field/shape.
- Không đăng ký thêm DI gì — `IRequestQueue`/`IExecutionList` đã singleton từ T7; middleware API key đã bao trùm `/v1/*` (chỉ `/health` được miễn).

### Step 3: Chạy test

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProxyControlApiTests" --nologo
```

**Expected: PASS 5/5.**

### Step 4: Full suite + commit

**Gate: 270 passed / 0 failed.**

```bash
git add src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/Server/ProxyControlApiTests.cs"
git commit -m "feat: add request snapshot and cancel api"
```

---

## Task 9 — Integration test: saturation + cancel flow (verification)

**Files (create):** `router balancing test/Server/ProxyQueueIntegrationTests.cs`.

**Consumes:** T1–T8 — **không có implementation mới**. 3 test này verify end-to-end hành vi engine qua TestServer: park-vô-hu-hạn khi hết slot, huỷ queued qua snapshot discovery, priority `X-Priority` + timing trong snapshot.
**Produces:** gate cuối 273.

⚠️ **Kỳ vọng PASS ngay khi viết.** Nếu FAIL → bug ở task trước (T6/T7/T8) — sửa đúng chỗ đó, **không** nới lỏng assertion cho xanh.

### Step 1: Viết test

`router balancing test/Server/ProxyQueueIntegrationTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
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
/// Integration 3B (spec §5): saturation → park, cancel queued → 400 gốc,
/// snapshot priority/timing — đi qua endpoint thật + dispatcher thật.
/// </summary>
public class ProxyQueueIntegrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly DpapiSecretProtector _protector = new();
    private WebApplication? _app;
    private HttpClient? _client;

    public ProxyQueueIntegrationTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory, new DpapiSecretProtector());
        _log = new LogService(factory);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _log.Dispose();
        _settings.Dispose();
        _db.Dispose();
    }

    private void SeedProvider(int maxConcurrent, params string[] modelIds)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = "p-main",
            BaseUrl = "https://api.openai.com",
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

    private sealed class StubUpstream : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct) =>
            Task.FromResult(Sse());
    }

    private sealed class GatedUpstream : IUpstreamClient
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Calls;
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            _entered.TrySetResult();
            await _release.Task;
            return Sse();
        }
    }

    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"x\":1}\n\ndata: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

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

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, string id) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"/v1/requests/{id}/cancel"));

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

    private async Task<string> FindRequestIdByStateAsync(HttpClient client, string state)
    {
        var response = await client.GetAsync("/v1/requests");
        var requests = (await ReadJson(response)).GetProperty("requests").EnumerateArray();
        return requests.Single(r => r.GetProperty("state").GetString() == state)
            .GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Chat_WhenProviderSaturated_ParksSecondRequestUntilSlotFree()
    {
        SeedProvider(maxConcurrent: 1, "m1");
        var upstream = new GatedUpstream();
        var client = await StartAsync(upstream);

        var first = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5)); // request 1 giữ trọn slot
        var second = client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Park vô hạn (spec §1.1): KHÔNG trả 503 ngay — request 2 chờ event, không polling
        await Task.Delay(300);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, upstream.Calls); // bound: request 2 phải nằm trong queue, chưa tới upstream

        // Request 1 xong → Exit → bắn Exited → wake → request 2 được serve
        upstream.Release();
        var r1 = await first.WaitAsync(TimeSpan.FromSeconds(5));
        var r2 = await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, r2.StatusCode);
        Assert.Equal(2, upstream.Calls);
        var id1 = r1.Headers.GetValues("X-Request-Id").Single();
        var id2 = r2.Headers.GetValues("X-Request-Id").Single();
        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public async Task Cancel_QueuedRequest_OriginReceives400WithMatchedRequestId()
    {
        SeedProvider(maxConcurrent: 1, "m1");
        var upstream = new GatedUpstream();
        var client = await StartAsync(upstream);

        var serving = client.PostAsync("/v1/chat/completions", ChatBody("m1"));
        await upstream.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        var queued = client.PostAsync("/v1/chat/completions", ChatBody("m1"));

        // Client chưa nhận header (endpoint còn chờ) — tự discover id qua snapshot (spec §3.5)
        var queuedId = await FindRequestIdByStateAsync(client, "queued");
        var cancel = await CancelAsync(client, queuedId);

        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.True((await ReadJson(cancel)).GetProperty("cancelled").GetBoolean());

        // Endpoint gốc await TCS → Cancelled → 400 request_cancelled, header khớp id đã huỷ
        var origin = await queued.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.BadRequest, origin.StatusCode);
        var error = (await ReadJson(origin)).GetProperty("error");
        Assert.Equal("request_cancelled", error.GetProperty("code").GetString());
        Assert.Equal(queuedId, origin.Headers.GetValues("X-Request-Id").Single());

        // Request đang phục vụ không bị ảnh hưởng — vẫn trả 200 bình thường
        upstream.Release();
        var ok = await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Snapshot_ReflectsPriorityHeaderAndTiming()
    {
        // MaxConcurrent=0 để request kẹt trong queue đủ lâu đọc snapshot
        SeedProvider(maxConcurrent: 0, "m1");
        var client = await StartAsync(new StubUpstream());

        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = ChatBody("m1"),
        };
        request.Headers.Add("X-Priority", "max");
        using var abort = new CancellationTokenSource();
        var pending = client.SendAsync(request, abort.Token);
        var queuedId = await WaitForQueuedIdAsync("m1");

        var response = await client.GetAsync("/v1/requests");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = (await ReadJson(response)).GetProperty("requests").EnumerateArray()
            .Single(r => r.GetProperty("id").GetString() == queuedId);
        Assert.Equal("queued", entry.GetProperty("state").GetString());
        Assert.Equal("m1", entry.GetProperty("model").GetString());
        // "max" chứ không phải "highest" — round-trip với vocab input X-Priority (chốt §10)
        Assert.Equal("max", entry.GetProperty("priority").GetString());
        Assert.True(entry.GetProperty("elapsedMs").GetInt64() >= 0);
        Assert.True(DateTimeOffset.TryParse(entry.GetProperty("enqueuedAt").GetString(), out _));
        Assert.True(entry.GetProperty("cancelable").GetBoolean());

        // Dọn có kiểm chứng (spec §3.4): client ngắt khi đang chờ → đúng dòng
        // ctx.RequestAborted.Register trong endpoint (T7) phải gỡ item + log Info — Dispose không treo
        var logged = new List<LogEntry>();
        void OnLog(LogEntry entry) => logged.Add(entry);
        _log.LogAdded += OnLog;
        try
        {
            abort.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            // Poll cho tới khi log xuất hiện — log chạy ngay sau TryRemove trong callback
            // (cùng thread), nên thấy log là chắc item đã rời queue
            var sawLog = () => logged.Exists(e => e.Message.Contains(queuedId) && e.Message.Contains("ngắt"));
            for (var i = 0; i < 100 && !sawLog(); i++)
                await Task.Delay(50);
            Assert.False(_app.Services.GetRequiredService<IRequestQueue>().Contains(queuedId),
                "Register của endpoint phải gỡ item khỏi queue");
            Assert.Contains(logged, e => e.Message.Contains(queuedId) && e.Message.Contains("ngắt"));
        }
        finally
        {
            _log.LogAdded -= OnLog;
        }
    }
}
```

### Step 2: Chạy test

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProxyQueueIntegrationTests" --nologo
```

**Expected: PASS 3/3 ngay.** FAIL → quay lại task phát sinh bug (T6 park/wake, T7 endpoint outcome, T8 snapshot/cancel), sửa rồi chạy lại — không sửa test cho xanh.

### Step 3: Full suite + commit

**Gate: 273 passed / 0 failed.**

```bash
git add "router balancing test/Server/ProxyQueueIntegrationTests.cs"
git commit -m "feat: add queue integration tests for saturation and cancel"
```

---

## Final Gates (chạy đủ trước khi báo hoàn thành 3B)

⚠️ Đóng app MAUI đang mở trước khi chạy test/build. Không `git push` ở bất kỳ bước nào.

1. **Build app** — yêu cầu **0 Warning / 0 Error**:

   ```bash
   dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo
   ```

2. **Full test** — yêu cầu **273 passed / 0 failed**:

   ```bash
   dotnet test "router balancing test/router balancing test.csproj" --nologo
   ```

3. **Parity i18n** — 3B không thêm key UI, yêu cầu EN=208 (dòng < 232) + VI=208 (dòng ≥ 232):

   ```powershell
   $m = Select-String -Path "src/RouterBalancing.Core/Localization/Translations.cs" -Pattern '\["'
   @($m | Where-Object LineNumber -lt 232).Count   # EN → phải = 208
   @($m | Where-Object LineNumber -ge 232).Count   # VI → phải = 208
   ```

4. **Git state** — 9 commit 3B đúng message như bảng ladder, working tree sạch:

   ```bash
   git status --short    # rỗng
   git log --oneline -10 # 9 commit 3B trên đầu, không commit lạ
   ```

5. **e2e smoke** (tay, ngoài `dotnet test` — xem header `scripts/e2e-3a.sh`):
   - App đang chạy + `node scripts/mock-upstream.mjs 9999` + provider `e2e-mock` (BaseUrl `http://127.0.0.1:9999`, ≥1 model enabled, ≥1 account key enabled).

   ```bash
   bash scripts/e2e-3a.sh    # yêu cầu ALL PASS
   ```

6. **Ledger** — cập nhật `.superpowers/sdd/progress.md`: 9/9 tasks done + mọi mục Minor/deferred mà reviewer để lại.

Mọi gate fail → sửa + chạy lại đúng gate đó trước khi báo done. Báo xong mới offer merge (local, không push).
