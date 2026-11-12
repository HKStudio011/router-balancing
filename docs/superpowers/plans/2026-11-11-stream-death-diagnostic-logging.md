# Log chẩn đoán stream chết giữa chừng — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mỗi ca stream SSE chết giữa chừng để lại đủ log trong app (elapsed, account, attempt #, route proxy, số byte đã gửi, hint) mà không đổi hành vi forward/retry/alert.

**Architecture:** Không thêm bảng/UI/event mới — làm giàu 3 điểm log đã có sẵn ngay tại nơi cái chết được quan sát: (1) `ProxyRequestHandler` catch giữa stream, (2) `DispatcherLoop` serve-catch, (3) endpoint `Aborted` trong `ProxyApp`. Ba dòng nối nhau bằng `RequestId` sẵn có.

**Tech Stack:** .NET 10, C#, xUnit, ASP.NET Core (Kestrel/TestServer), EF Core (đọc thông qua `ProxySelectionResolver` in-memory, không query DB).

**Spec:** `docs/superpowers/specs/2026-11-11-stream-death-diagnostic-logging-design.md`

## Global Constraints

- **Không đổi hành vi**: forward/retry/failover/buffering/keep-alive/in-band event, và **không đổi UI/schema/API**. Chỉ thêm dòng log.
- **Severity (D1)**: điểm #1 và #3 = `LogSeverity.Warning`; điểm #2 giữ `LogSeverity.Error` (alert `ErrorAlertAggregator` đếm `Error` + `ProviderKey` non-null → giữ đúng 1 alert/ca chết, không nhân đôi).
- **Không đặt `ProviderKey`/`ModelKey` trên dòng #1 và #3** — chỉ `RequestId` + `ClientKeyId`, tránh thay đổi semantics thống kê.
- **D4**: mọi dòng chẩn đoán chỉ ghi khi client **còn nối** (`!ctx.RequestAborted.IsCancellationRequested`).
- **An toàn đường lỗi**: khối chẩn đoán ở điểm #1 bọc `try/catch` nuốt chủ đích — log/resolve ném không được thay thế exception gốc đang rethrow (quy ước có sẵn ở `DispatcherLoop` dòng 270, `ProxyRequestHandler` dòng 286–289).
- **Không log secret/PII**: route dùng `ProxyRuntimeStatus.Endpoint` (`scheme://host:port`, không credentials); `Details` chỉ có stack exception.
- **Quy ước code** (AGENTS.md): comment tiếng Việt giải thích *why*; XML doc cho public API; `Nullable`/`ImplicitUsings` giữ nguyên; commit tiếng Anh, conventional commit, mỗi commit một việc.
- **Kiểm chứng bắt buộc trước khi báo xong**: `dotnet build router-balancing.slnx` và `dotnet test "router balancing test/router balancing test.csproj"` đều xanh.

## Review Focus

Những trường hợp spec không nói tới nhưng dễ vỡ khi dùng thật — mỗi dòng đều có test pin ở task tương ứng (trừ dòng cuối, pin bằng negative assertion + review):

1. **`resolver.Resolve`/`pool.Snapshot()` ném trong lúc stream chết** → request phải vẫn ném đúng exception gốc (không bị thay thế bởi lỗi log). *Pin: try/catch + code review Task 1 Step 3.*
2. **Client abort mid-stream** → không dòng chẩn đoán nào, outcome không đổi (D4). *Pin: Task 1 abort test.*
3. **Alert không tăng** → mỗi ca chết vẫn đúng 1 dòng `Error` alertable. *Pin: Task 2 test `Assert.Single(...Error...)`.*
4. **`contentBytes` đúng bằng byte content thật trên wire** (keep-alive không đếm) → log #3 phải khớp. *Pin: Task 3 test 15 byte.*
5. **Nhánh #7 (`Aborted` + 0 byte)** cực hiếm → chỉ được thêm log, không được đổi in-band `server_error`/outcome. *Pin: negative assertion Task 3 (không lẫn vào #8) + review diff.*

---

## File Structure

| File | Trách nhiệm sau thay đổi |
|---|---|
| `src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs` | Điểm #1: log elapsed/account/route + helper `DescribeProxyRoute` |
| `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` | Điểm #2: enrich message + helper `StreamDeathHint` |
| `src/RouterBalancing.Core/Server/ProxyApp.cs` | Điểm #3: log 2 nhánh `Aborted` |
| `router balancing test/Engine/ProxyRequestHandlerTests.cs` | Factory cập nhật ctor + test điểm #1 + test D4 |
| `router balancing test/Engine/DispatcherLoopTests.cs` | Test double `ThrowingUpstream` + 2 test điểm #2 |
| `router balancing test/Server/ProxyRetryIntegrationTests.cs` | Pin điểm #3 qua `_messages` |

---

### Task 1: Điểm #1 — log chẩn đoán trong `ProxyRequestHandler`

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs:23-30` (ctor), `:246-259` (catch giữa stream), `:423-436` (thêm helper cạnh `LogForwarded`)
- Test: `router balancing test/Engine/ProxyRequestHandlerTests.cs:173-186` (factory), `:863-894` (test mid-stream), `:896+` (test mới cho D4)

**Interfaces:**
- Consumes: `IProxySelectionResolver.Resolve(Provider, ProviderAccount?) → ProxySelection`; `IProxyPool.Snapshot() → IReadOnlyList<ProxyRuntimeStatus>` với `ProxyRuntimeStatus(long Id, string Endpoint, bool IsDown, DateTimeOffset? DownUntil)`; `ProxySelection.ProxyIds`, `.Mode`, `.IsDirect`; `CleanDirect` default trong test là `DirectProxyPool` (`router balancing test/TestDoubles.cs:42`).
- Produces: ctor mới `ProxyRequestHandler(IUpstreamClient, ISecretProtector, ILogService, IRequestUsageSink, IApiMonitorStore, IAppSettingsService, IDatasetSink, IProxySelectionResolver, IProxyPool)`; private `string DescribeProxyRoute(Provider provider, ProviderAccount account)`; dòng log Warning khớp regex `^Upstream '.+' đứt stream sau \d+ms — account '.+', route .+\.$`.

- [ ] **Step 1: Commit spec đã duyệt**

```bash
git add docs/superpowers/specs/2026-11-11-stream-death-diagnostic-logging-design.md
git commit -m "docs: add stream death diagnostic logging design"
```

- [ ] **Step 2: Viết test đỏ (cập nhật factory + assert log #1)**

Trong `router balancing test/Engine/ProxyRequestHandlerTests.cs`, thêm `using RouterBalancing.Core.Proxies;` và sửa factory:

```csharp
private ProxyRequestHandler Create(IUpstreamClient upstream, CapturingLog? log = null,
    IRequestUsageSink? sink = null, ApiMonitorStore? monitor = null,
    StubSettings? settings = null, IDatasetSink? dataset = null)
{
    var effectiveLog = log ?? new CapturingLog();
    var effectiveSettings = settings ?? new StubSettings();
    // DirectProxyPool/ProxySelectionResolver: test này không phụ thuộc proxy — route log ra "direct"
    return new(upstream, _protector, effectiveLog, sink ?? new NullUsageSink(),
        monitor ?? new ApiMonitorStore(new TraceFeed(effectiveLog), effectiveLog,
            TimeProvider.System),
        effectiveSettings,
        dataset ?? new DatasetSink(effectiveSettings, effectiveLog, TimeProvider.System),
        new ProxySelectionResolver(), new DirectProxyPool());
}
```

Mở rộng `ForwardAsync_WhenTeeFailsMidStream_RethrowsAndRecordsNetworkKind` (sau assert `record.FirstTokenAt`):

```csharp
// Điểm #1 (spec §4.1): log chẩn đoán đọc được ngay — elapsed + account + route, Warning
// (không phải Error — tránh đếm đôi cảnh báo, D1), ErrorCode là type lỗi đọc gốc
var diag = Assert.Single(log.Entries.Where(e => e.Message.Contains("đứt stream")));
Assert.Equal(LogSeverity.Warning, diag.Severity);
Assert.Equal(LogCategory.Request, diag.Category);
Assert.Equal("req-mid", diag.RequestId);
Assert.Matches(@"^Upstream 'openai-main' đứt stream sau \d+ms — account 'a1', route direct\.$",
    diag.Message);
Assert.Equal("IOException", diag.ErrorCode);
Assert.Contains("upstream stream broke mid-chunk", diag.Details);
```

- [ ] **Step 3: Chạy test để xác nhận đỏ**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ForwardAsync_WhenTeeFailsMidStream"`
Expected: FAIL — không biên dịch được (`ProxyRequestHandler` chưa có 9 tham số / chưa có dòng log).

- [ ] **Step 4: Implement điểm #1**

Ctor (`ProxyRequestHandler.cs:23-30`) — thêm 2 tham số nguồn DI:

```csharp
public sealed class ProxyRequestHandler(
    IUpstreamClient upstream,
    ISecretProtector protector,
    ILogService log,
    IRequestUsageSink usageSink,
    IApiMonitorStore monitor,
    IAppSettingsService settings,
    IDatasetSink dataset,
    IProxySelectionResolver proxySelectionResolver,
    IProxyPool proxyPool)
```

Trong `catch` giữa stream (`:246-259`), giữ nguyên `upstreamCts.Cancel();` và `monitor.RecordError` guard, thêm **trước `throw;`**:

```csharp
// Điểm #1 (spec §4.1): nơi DUY NHẤT còn stopwatch/account/route — exception rời handler
// thì dispatcher chỉ còn message. Warning (không Error) đúng D1; chỉ ghi khi client còn
// nối vì client tự ngắt không phải sự cố upstream (D4).
if (!ctx.RequestAborted.IsCancellationRequested)
{
    try
    {
        log.Write(new LogEntry
        {
            Severity = LogSeverity.Warning,
            Category = LogCategory.Request,
            Message = $"Upstream '{provider.Name}' đứt stream sau {stopwatch.ElapsedMilliseconds}ms "
                      + $"— account '{account.Name}', route {DescribeProxyRoute(provider, account)}.",
            Details = ex.ToString(),
            // Type lỗi đọc GỐC (watchdog bọc thành UpstreamStreamDiedException) — cùng quy ước
            // với catch pre-content để filter được HttpIOException trong DB
            ErrorCode = (ex.InnerException ?? ex).GetType().Name,
            RequestId = ClientKeyItems.RequestIdOf(ctx),
            ClientKeyId = ClientKeyItems.IdOf(ctx),
        });
    }
    catch
    {
        // Nuốt chủ đích: chẩn đoán không được thay thế exception gốc đang rethrow
    }
}
```

Thêm helper cạnh `LogForwarded` (`:423`):

```csharp
// Route CẤU HÌNH + down-state, không phải hop thật: ProxyContext.Current đã bị reset sau khi
// nhận header nên hop không còn truy được (spec §4.1/D3). Endpoint không chứa credentials.
private string DescribeProxyRoute(Provider provider, ProviderAccount account)
{
    var selection = proxySelectionResolver.Resolve(provider, account);
    if (selection.IsDirect)
        return "direct";

    var ids = selection.ProxyIds!;
    var live = proxyPool.Snapshot().Where(s => ids.Contains(s.Id)).ToList();
    var endpoints = string.Join(", ", live.Select(s => $"{s.Id}:{s.Endpoint}{(s.IsDown ? "(down)" : "")}"));
    return $"{selection.Mode} ids=[{string.Join(",", ids)}] enabled=[{endpoints}]";
}
```

- [ ] **Step 5: Chạy test để xác nhận xanh**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ForwardAsync_WhenTeeFailsMidStream"`
Expected: PASS.

- [ ] **Step 6: Thêm test pin D4 (client abort không sinh dòng chẩn đoán)**

Thêm test mới vào `ProxyRequestHandlerTests` (đặt sau test mid-stream). Test này **pass ngay** — nó khóa bất biến, không cần code mới:

```csharp
[Fact]
public async Task ForwardAsync_WhenClientAbortedMidStream_WritesNoStreamDeathDiagnostic()
{
    var log = new CapturingLog();
    var sut = Create(new StubUpstream(() => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new BrokenSseContent(),
    }), log);
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    var ctx = Ctx();
    ctx.RequestAborted = cts.Token; // client tự ngắt — không phải sự cố upstream (D4)
    ctx.Items[ClientKeyItems.RequestId] = "req-abort";
    ctx.Items[SsePipeItems.Key] = new Pipe();
    var provider = SeedProvider();

    await Assert.ThrowsAnyAsync<Exception>(() =>
        sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson),
            AccountIdOf(provider), ProxyProtocols.Chat, default));

    Assert.DoesNotContain(log.Entries, e => e.Message.Contains("đứt stream"));
}
```

- [ ] **Step 7: Chạy test + toàn bộ suite (phát hiện test đếm `Warns` bị ảnh hưởng)**

Run: `dotnet test "router balancing test/router balancing test.csproj"`
Expected: PASS toàn bộ. Nếu test nào `Assert.Single(log.Warns)`/`Assert.Empty(log.Warns)` vỡ vì dòng chẩn đoán mới, chỉ nới assertion cho đúng dòng mới (giữ nguyên số dòng cũ) — không đổi production code.

- [ ] **Step 8: Commit**

```bash
git add src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs "router balancing test/Engine/ProxyRequestHandlerTests.cs"
git commit -m "feat: log elapsed, account and proxy route when upstream stream dies"
```

---

### Task 2: Điểm #2 — enrich serve-catch của `DispatcherLoop`

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs:274-284` (dòng `Message = $"Lỗi dispatcher: {ex.Message}"` trong `catch (Exception ex)` của `ServeAsync`), thêm helper cạnh `AccountNameOf` (`:778`)
- Test: `router balancing test/Engine/DispatcherLoopTests.cs:262-275` (thêm `ThrowingUpstream`), test mới cạnh `Loop_WhenResolverThrows_LogsErrorAndKeepsServing` (`:711`)

**Interfaces:**
- Consumes: `attemptNo` (`DispatcherLoop.cs:241`), `AccountNameOf(ModelCandidate, long)` (`:778`), `request.Context.Response.HasStarted`, `UpstreamStreamDiedException(Exception inner, TimeSpan silenceGap)` (internal, test project thấy được — `IdleWatchdogStreamTests.cs:62`), `UpstreamIdleTimeoutException` (internal).
- Produces: message khớp `Lỗi dispatcher: .+ — attempt #\d+, account '.+', (chưa gửi gì → 500|stream đã gửi → Aborted)\.`; helper `private static string StreamDeathHint(Exception ex)`.

- [ ] **Step 1: Viết 2 test đỏ + test double**

Thêm double vào `DispatcherLoopTests` (cạnh `StubUpstream`):

```csharp
private sealed class ThrowingUpstream(Exception ex) : IUpstreamClient
{
    public Task<HttpResponseMessage> PostAsync(
        Provider provider, string apiKey, string path, byte[] body, string idempotencyKey, CancellationToken ct) =>
        Task.FromException<HttpResponseMessage>(ex);
}
```

Thêm 2 test:

```csharp
[Fact]
public async Task ServeAsync_WhenForwardThrows_LogsAttemptAccountAndOutcome()
{
    var pid = SeedProvider("p1"); // TK mặc định "a1"
    var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
    var selector = new CountingSelector(new ModelSelector(_executions));
    // Exception ngoài nhóm mạng → handler không nuốt, dispatcher catch là nơi duy nhất ghi log
    var upstream = new ThrowingUpstream(new InvalidOperationException("boom"));
    var log = new CapturingLog();

    var r1 = Req("req00001");
    _queue.Enqueue(r1);
    await StartAsync(resolver, selector, upstream, log);

    Assert.IsType<DispatchOutcome.Error>(await r1.Completion.Task.WaitAsync(TestTimeouts.Rendezvous));

    // Đúng 1 dòng Error/ca chết — enrichment không nhân đôi cảnh báo (D1/Review Focus 3)
    var entry = Assert.Single(log.Entries, e => e.Severity == LogSeverity.Error);
    Assert.Contains("Lỗi dispatcher", entry.Message);
    Assert.Contains("attempt #1", entry.Message);
    Assert.Contains("account 'a1'", entry.Message);
    Assert.Contains("chưa gửi gì → 500", entry.Message);
    Assert.DoesNotContain("Chẩn đoán", entry.Details); // hint chỉ cho lỗi stream (D2)
}

[Fact]
public async Task ServeAsync_WhenUpstreamStreamDied_AppendsStreamDeathHint()
{
    var pid = SeedProvider("p1");
    var resolver = new StubResolver(new SelectionSuccess([Candidate(pid)], ComboMode.RoundRobin));
    var selector = new CountingSelector(new ModelSelector(_executions));
    var upstream = new ThrowingUpstream(
        new UpstreamStreamDiedException(new IOException("response ended prematurely"), TimeSpan.Zero));
    var log = new CapturingLog();

    var r1 = Req("req00001");
    _queue.Enqueue(r1);
    await StartAsync(resolver, selector, upstream, log);

    Assert.IsType<DispatchOutcome.Error>(await r1.Completion.Task.WaitAsync(TestTimeouts.Rendezvous));

    var entry = Assert.Single(log.Entries, e => e.Severity == LogSeverity.Error);
    Assert.Contains("attempt #1", entry.Message);
    Assert.Contains("account 'a1'", entry.Message);
    Assert.Contains("Chẩn đoán", entry.Details);
    Assert.Contains("không phải client cancel", entry.Details);
}
```

- [ ] **Step 2: Chạy test để xác nhận đỏ**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ServeAsync_When"`
Expected: FAIL — message chưa có `attempt #1`/`account 'a1'`, `Details` chưa có hint.

- [ ] **Step 3: Implement enrich + helper**

Thay `log.Write(...)` tại `DispatcherLoop.cs:274-284` (`Message` + `Details`; các field còn lại giữ nguyên):

```csharp
log.Write(new LogEntry
{
    Severity = LogSeverity.Error,
    Category = LogCategory.Request,
    // attempt/TK/kết luận: dispatcher là nơi DUY NHẤT biết attempt nào và client nhận gì
    // (handler không thấy attempt; endpoint không thấy exception) — spec §4.2
    Message = $"Lỗi dispatcher: {ex.Message} — attempt #{attemptNo}, "
              + $"account '{AccountNameOf(candidate, accountId)}', "
              + $"{(request.Context.Response.HasStarted ? "stream đã gửi → Aborted" : "chưa gửi gì → 500")}.",
    Details = ex.ToString() + StreamDeathHint(ex),
    ErrorCode = ex.GetType().Name,
    RequestId = request.Id,
    ProviderKey = candidate.Provider.Name,
    ModelKey = candidate.Model.ModelId,
});
```

Thêm helper cạnh `AccountNameOf` (`:778`):

```csharp
// Hint đọc-được-ngay cho người trực ca (spec §4.2/D2) — chỉ gắn cho 2 exception stream đã biết
// để không nhiễu khi lỗi dispatcher là do resolver/DB.
private static string StreamDeathHint(Exception ex) =>
    ex is UpstreamStreamDiedException or UpstreamIdleTimeoutException
        ? "\nChẩn đoán: upstream đóng/RST giữa stream (\"response ended prematurely\") — không "
          + "phải client cancel; đối chiếu dòng cùng RequestId ở handler (elapsed + route) "
          + "trước khi nghi proxy/hạ tầng."
        : "";
```

- [ ] **Step 4: Chạy test để xác nhận xanh**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ServeAsync_When"`
Expected: PASS (2 test).

- [ ] **Step 5: Chạy toàn bộ suite**

Run: `dotnet test "router balancing test/router balancing test.csproj"`
Expected: PASS — đặc biệt `Loop_WhenResolverThrows_LogsErrorAndKeepsServing` (anchor `"Lỗi dispatcher"` ở catch cấp loop `DispatcherLoop.cs:64`, không phải catch này — không được sửa nó).

- [ ] **Step 6: Commit**

```bash
git add src/RouterBalancing.Core/Engine/DispatcherLoop.cs "router balancing test/Engine/DispatcherLoopTests.cs"
git commit -m "feat: enrich dispatcher error log with attempt, account and stream-death hint"
```

---

### Task 3: Điểm #3 — log số byte đã gửi ở nhánh `Aborted`

**Files:**
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs:404-415`
- Test: `router balancing test/Server/ProxyRetryIntegrationTests.cs:583-610` (mở rộng), `:526-551` (negative assert)

**Interfaces:**
- Consumes: `contentBytes` (từ `RunStreamLoopAsync`, `ProxyApp.cs:318-319`), `id`, `_messages` (capture mọi `LogAdded`, `ProxyRetryIntegrationTests.cs:30,43`), `ThrowingStream.FirstChunk = "data: {\"x\":1}\n\n"u8.ToArray()` (15 byte, `:241`).
- Produces: message khớp `Stream chết (trước khi có byte content nào|sau \d+ byte content) — trả in-band (server_error|upstream_disconnected) \(request .+\)\.`

- [ ] **Step 1: Viết test đỏ (mở rộng integration sẵn có)**

Trong `Stream_WhenUpstreamDiesMidContent_EmitsInBandUpstreamLostEvent`, sau khối assert monitor (`Assert.Equal("network", record.FailureKind);`) và trước `Assert.Equal(1, upstream.Calls);`, thêm:

```csharp
// Điểm #3 (spec §4.3): số byte content đã tới client trước khi stream cụt — keep-alive KHÔNG đếm
Assert.Contains(_messages, m => m.Contains("Stream chết sau 15 byte content")
    && m.Contains("upstream_disconnected") && m.Contains(id));
```

Trong `Stream_WhenUpstreamDiesBeforeContent_Emits502InBandAndErrorRow`, sau `Assert.Equal("network", record.FailureKind);`, thêm:

```csharp
// Pre-content đi nhánh Fatal/exhaustion (in-band 502) — KHÔNG được lẫn thành log #8
Assert.DoesNotContain(_messages, m => m.Contains("Stream chết sau"));
```

- [ ] **Step 2: Chạy test để xác nhận đỏ**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~Stream_WhenUpstreamDies"`
Expected: FAIL ở test mid-content (`_messages` chưa có dòng `Stream chết sau 15 byte content`); test pre-content PASS (negative assert).

- [ ] **Step 3: Implement log #3 ở 2 nhánh**

Trong `case DispatchOutcome.Aborted when contentBytes == 0 ...` (`ProxyApp.cs:404`):

```csharp
// Điểm #3 (spec §4.3): breadcrumb "sự thật trên wire" — ghi TRƯỚC WriteInBand để còn dấu
// vết cả khi client đã đóng kết nối; Warning (không Error) đúng D1
log.Write(new LogEntry
{
    Severity = LogSeverity.Warning,
    Category = LogCategory.Request,
    Message = $"Stream chết trước khi có byte content nào — trả in-band server_error (request {id}).",
    RequestId = id,
    ClientKeyId = ClientKeyItems.IdOf(ctx),
});
await WriteInBand(ctx, SseErrorEvent.ServerFault());
```

Trong `case DispatchOutcome.Aborted when contentBytes > 0 ...` (`ProxyApp.cs:409`):

```csharp
log.Write(new LogEntry
{
    Severity = LogSeverity.Warning,
    Category = LogCategory.Request,
    Message = $"Stream chết sau {contentBytes} byte content — trả in-band upstream_disconnected (request {id}).",
    RequestId = id,
    ClientKeyId = ClientKeyItems.IdOf(ctx),
});
await WriteInBand(ctx, SseErrorEvent.UpstreamLostMidStream());
```

Không đổi điều kiện `when`, không đổi `WriteInBand`.

- [ ] **Step 4: Chạy test để xác nhận xanh**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~Stream_WhenUpstreamDies"`
Expected: PASS (cả 2 test).

- [ ] **Step 5: Chạy toàn bộ suite**

Run: `dotnet test "router balancing test/router balancing test.csproj"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/Server/ProxyRetryIntegrationTests.cs"
git commit -m "feat: log forwarded byte count when aborted stream reaches endpoint"
```

---

### Task 4: Xác nhận cuối (build solution + toàn bộ test + đóng spec)

**Files:**
- Modify: `docs/superpowers/specs/2026-11-11-stream-death-diagnostic-logging-design.md:4` (dòng `**Trạng thái:** chờ duyệt`)

**Interfaces:**
- Consumes: toàn bộ thay đổi Task 1–3.
- Produces: không có API mới — chỉ xác nhận chất lượng.

- [ ] **Step 1: Build toàn solution**

Run: `dotnet build router-balancing.slnx`
Expected: `Build succeeded`, 0 warning mới (không có CS86xx/CS0618 phát sinh).

- [ ] **Step 2: Chạy toàn bộ test lần cuối**

Run: `dotnet test "router balancing test/router balancing test.csproj"`
Expected: PASS toàn bộ, số test = số cũ + 3 test mới (2 ở `ProxyRequestHandlerTests`, 2 ở `DispatcherLoopTests` — trừ khi tách/gộp, miễn không có test nào đỏ).

- [ ] **Step 3: Đối chiếu Review Focus**

Kiểm tra bằng mắt trên diff (`git diff master -- src "router balancing test"`):

- `DescribeProxyRoute` nằm trong khối `try/catch` nuốt chủ đích ở điểm #1.
- Không có `ProviderKey`/`ModelKey` ở dòng #1/#3.
- `WriteInBand`/`conditions when` không đổi so với trước.
- Không có secret/URL có credentials trong message/Details.

- [ ] **Step 4: Đóng spec + commit**

Sửa dòng 4 của spec thành:

```markdown
- **Trạng thái:** đã triển khai 2026-11-11
```

```bash
git add docs/superpowers/specs/2026-11-11-stream-death-diagnostic-logging-design.md
git commit -m "docs: mark stream death diagnostic logging design as implemented"
```
