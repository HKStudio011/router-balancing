# Version Display + Auto-Update Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hiển thị version rút gọn (2 số đầu, trailing-zero cắt), check update từ GitHub Releases với toast + badge, luồng update an toàn (tải song song drain → snapshot queue JSON → updater tách riêng cài → restart), và restore snapshot ở startup.

**Architecture:** Logic testable đặt trong `RouterBalancing.Core` (`Update/` + `Engine/`); MAUI app chỉ wiring DI + UI Blazor. `DrainGate` là singleton dùng chung giữa 2 DI container (MAUI + proxy WebApplication — theo pattern `ITraceFeed` hiện có, `ProxyHost` forward instance). Snapshot ghi/đọc qua file JSON atomic; restore chạy trong `ProxyHost.StartAsync` trước `StartAsync` nên `DispatcherLoop` (BackgroundService) chưa khởi động.

**Tech Stack:** .NET 10 / MAUI Blazor Hybrid, xUnit, System.Text.Json, System.IO.Pipelines, GitHub Releases API.

**Spec:** `docs/superpowers/specs/2026-10-11-version-update-design.md` — mọi hành vi dưới đây đều lập luận từ spec; executor đọc kèm.

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — bắt buộc giữ nguyên.
- Comment giải thích "tại sao" bằng tiếng Việt; XML doc (`///`) cho public API.
- Auto-update chỉ Windows — mọi đường đi qua guard `OperatingSystem.IsWindows()` (non-Windows: check trả null/false, Start() noop).
- Version single source: `<Version>1.0.0.0</Version>` trong `router-balancing/router-balancing.csproj`; `ApplicationDisplayVersion=$(Version)`.
- Snapshot path: `%AppData%/router-balancing/queue-snapshot.json` (qua `StoragePathProvider.GetQueueSnapshotPath()`); field `version` = 1.
- GitHub repo: `HKStudio011/router-balancing`; UA `RouterBalancing/{CurrentVersion}`; asset `.zip` không chứa `symbols`.
- Updater: args `--zip <path> --pid <pid> --app-dir <dir>`, timeout chờ PID 60s, app exe `router-balancing.exe`, updater exe `RouterBalancing.Updater.exe`.
- Gate phải xanh sau mỗi task: `dotnet build router-balancing.slnx` và `dotnet test "router balancing test/router balancing test.csproj"`.
- Commit conventional (`feat:`, `test:`, `docs:`), một việc một commit, message tiếng Anh.

## Review Focus

Năm điểm spec ngầm định mà test của task sở hữu phải kẹp lại:

1. **Dispatcher resume ngay khi `CancelDrain()`** — nếu `DispatcherLoop` không subscribe `DrainGate.Changed`, queue park vĩnh viễn sau khi huỷ update. → Task 5, test `DispatcherLoop_WhenDrainCancelled_TakesQueuedRequestAgain`.
2. **`isStream` detect qua `HttpContext.Items[SsePipeItems.Key]`** — sai detection thì restore không tạo pipe → handler stream treo vô hạn. → Task 6, test `SaveAsync_MarksStreamRequest_WhenPipeKeyPresent`; Task 8, test `RestoreAsync_WhenIsStream_PublishesPipeInContextItems`.
3. **EnqueuedAt bị `RequestQueue.Enqueue` ghi đè** (`RequestQueue.cs:40`) — restorer phải set lại sau Enqueue, không thì mọi request restore có timestamp "now". → Task 8, test `RestoreAsync_PreservesEnqueuedAtFromSnapshot`.
4. **Snapshot atomic** — crash giữa ghi không được để file half-written (restore lần sau parse fail mất sạch queue). → Task 6, test `SaveAsync_WritesViaTempFile_ThenMoves`.
5. **Launch updater fail phải xoá snapshot** — nếu không, boot kế tiếp restore oan request cũ (chạy lại tốn token, không ai nhận response). → Task 9, test `StartUpdateAsync_WhenInstallFails_DeletesSnapshotAndResumesDrain`.

---

### Task 1: Version source + `AppVersionFormatter`

**Files:**
- Modify: `router-balancing/router-balancing.csproj:37-39`
- Create: `src/RouterBalancing.Core/Update/AppVersionFormatter.cs`
- Test: `router balancing test/Update/AppVersionFormatterTests.cs`

**Interfaces:**
- Produces: `public static class RouterBalancing.Core.Update.AppVersionFormatter` → `public static string Format(Version version)`.

- [ ] **Step 1: Sửa csproj — single source version**

```xml
<Version>1.0.0.0</Version>
<ApplicationDisplayVersion>$(Version)</ApplicationDisplayVersion>
<ApplicationVersion>1</ApplicationVersion>
```

Giữ nguyên `ApplicationVersion` (Android int, không liên quan update).

- [ ] **Step 2: Viết test failing**

```csharp
[Theory]
[InlineData(1, 0, 0, 0, "1.0")]
[InlineData(1, 0, 3, 0, "1.0.3")]
[InlineData(1, 0, 0, 4, "1.0.0.4")]
[InlineData(1, 0, 3, 4, "1.0.3.4")]
[InlineData(2, 5, 0, 0, "2.5")]
public void Format_ReturnsShortForm(int major, int minor, int build, int revision, string expected)
    => Assert.Equal(expected, AppVersionFormatter.Format(new Version(major, minor, build, revision)));
```

- [ ] **Step 3: Chạy test — FAIL** (class chưa tồn tại)

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter AppVersionFormatter`

- [ ] **Step 4: Implement `AppVersionFormatter.Format`**

Luôn `Major.Minor`; thêm `Build` nếu ≠ 0; thêm `Revision` nếu ≠ 0; cắt trailing-zero nhưng tối thiểu 2 phần.

- [ ] **Step 5: Chạy test — PASS** rồi commit

```bash
git add router-balancing/router-balancing.csproj src/RouterBalancing.Core/Update/AppVersionFormatter.cs "router balancing test/Update/AppVersionFormatterTests.cs"
git commit -m "feat: add version source and short-form version formatter"
```

---

### Task 2: `GitHubAppUpdateService`

**Files:**
- Create: `src/RouterBalancing.Core/Update/AppUpdateModels.cs` (ReleaseInfo, ReleaseAsset, IAppUpdateService)
- Create: `src/RouterBalancing.Core/Update/GitHubAppUpdateService.cs`
- Test: `router balancing test/Update/GitHubAppUpdateServiceTests.cs`

**Interfaces:**
- Produces (dùng bởi Task 9, 10, 12):

```csharp
public record ReleaseInfo(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("assets")] List<ReleaseAsset> Assets);
public record ReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);

public interface IAppUpdateService
{
    string CurrentVersion { get; }                       // Assembly version.ToString()
    Task<ReleaseInfo?> CheckForUpdatesAsync();           // null khi fail/non-Windows
    bool IsUpdateAvailable(ReleaseInfo release);         // so tag (strip 'v', pad 0) với CurrentVersion
    Task<string?> DownloadUpdateAsync(ReleaseInfo release, IProgress<int>? progress); // null khi fail
    string? DownloadedZipPath { get; }
    Task<bool> InstallUpdateAsync();                     // launch RouterBalancing.Updater.exe; false khi fail/non-Windows
}
```

- [ ] **Step 1: Viết tests failing** — dùng `HttpMessageHandler` stub trả JSON:

- `CheckForUpdatesAsync_ReturnsRelease_WhenSuccess` — JSON tag `v1.2.3`, 1 asset `.zip`.
- `CheckForUpdatesAsync_ReturnsNull_WhenNonSuccess` — handler trả 500.
- `IsUpdateAvailable_True_WhenTagNewer` (`v1.0.1` vs current `1.0.0.0`), `False_WhenEqualOrOlder`, `True_WhenTagShorter_Padded` (`v1.1` vs `1.0.9.0`).
- `DownloadUpdateAsync_WritesZip_AndReportsProgress` — stub stream 3 chunk, progress được gọi, file tồn tại.
- `InstallUpdateAsync_False_OnNonWindows` — chạy test trên Windows nên skip property `[PlatformSpecific]`? Thay bằng: test `InstallUpdateAsync_False_WhenUpdaterMissing` (app dir không có exe).

- [ ] **Step 2: Chạy — FAIL**; **Step 3: Implement**

Port từ `D:\Code\DMFT\DMFT\Services\Implements\AppUpdateService.cs`: endpoint `https://api.github.com/repos/HKStudio011/router-balancing/releases/latest`, UA `RouterBalancing/{CurrentVersion}`, download về `%Temp%/RouterBalancing_Update/<asset-name>` (ghi đè, không reuse), `FindUpdaterPath` = `Path.Combine(AppContext.BaseDirectory, "RouterBalancing.Updater.exe")`, args `--zip "{zip}" --pid {Environment.ProcessId} --app-dir "{AppContext.BaseDirectory}"`. Mọi method bọc try/catch → null/false; đầu method: `if (!OperatingSystem.IsWindows()) return null/false;` (trừ `IsUpdateAvailable` — pure so sánh, không cần guard). `CurrentVersion` = `Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0.0"`. Ctor nhận `HttpClient`.

- [ ] **Step 4: Chạy test — PASS**; **Step 5: Commit**

```bash
git commit -m "feat: add GitHub release update service"
```

---

### Task 3: `DrainGate`

**Files:**
- Create: `src/RouterBalancing.Core/Engine/DrainGate.cs`
- Test: `router balancing test/Engine/DrainGateTests.cs`

**Interfaces:**
- Produces (Task 4, 5, 7, 9):

```csharp
public sealed class DrainGate
{
    public bool IsDraining { get; private set; }
    public event Action? Changed;      // BeginDrain/CancelDrain — dispatcher wake
    public void BeginDrain();          // idempotent: đang true → return, không bắn event
    public void CancelDrain();         // idempotent: đang false → return
}
```

- [ ] **Step 1: Tests failing** — `BeginDrain_SetsFlagAndRaisesChanged`, `CancelDrain_ClearsFlagAndRaisesChanged`, `BeginDrain_Twice_RaisesChangedOnce`, `CancelDrain_WhenNotDraining_DoesNotRaise`.
- [ ] **Step 2: FAIL**; **Step 3: Implement** (cờ thường + event; `BeginDrain`/`CancelDrain` gọi từ UI thread — kiểm tra flag + gán trong cùng method, dispatcher chỉ đọc cờ sau khi đã nhận `Changed` event nên không cần lock); **Step 4: PASS**; **Step 5: Commit** `feat: add drain gate for update pause`.

---

### Task 4: `IExecutionList.TotalInFlight`

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/IExecutionList.cs` (thêm property + XML doc)
- Modify: `src/RouterBalancing.Core/Engine/ExecutionList.cs`
- Test: `router balancing test/Engine/ExecutionListTests.cs` (file hiện có — thêm test)

**Interfaces:**
- Produces: `int TotalInFlight { get; }` — tổng entry đang phục vụ.

- [ ] **Step 1: Test failing** — sau khi `TryEnterAsync` 2 request khác provider: `GetTotalInFlight_ReturnsSumAcrossProviders` assert `TotalInFlight == 2`; `Exit` 1 cái → còn 1.
- [ ] **Step 2: FAIL**; **Step 3: Implement** — `lock (_lock) return _entries.Count;` trong `ExecutionList`.
- [ ] **Step 4: PASS**; **Step 5: Commit** `feat: expose total in-flight count on execution list`.

---

### Task 5: `DispatcherLoop` pause khi drain + DI wiring DrainGate

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` (ctor + `ExecuteAsync` subscribe + dòng đầu `TryDispatchOnceAsync`)
- Modify: `src/RouterBalancing.Core/Server/ProxyHost.cs` (ctor thêm `DrainGate` + forward vào proxy builder)
- Modify: `router balancing test/Engine/DispatcherLoopTests.cs` (ctor call tại dòng ~129 thêm `new DrainGate()` field)
- Modify: `router balancing test/Server/ProxyHostTests.cs` (5 ctor call sites thêm `new DrainGate()`)
- Modify: `router-balancing/MauiProgram.cs` (đăng ký `builder.Services.AddSingleton<DrainGate>();` cạnh các singleton Core khác)

**Interfaces:**
- Consumes: `DrainGate` (Task 3).
- Produces:
  - `DispatcherLoop` ctor mới — `(IRequestQueue queue, IExecutionList executions, IComboResolver resolver, IModelSelector selector, ProxyRequestHandler handler, ILogService log, ITraceFeed trace, IAppSettingsService settings, DrainGate drainGate)`.
  - `ProxyHost` ctor mới — thêm `DrainGate drainGate`; trong `StartAsync` sau dòng forward `_monitor`: `builder.Services.AddSingleton(drainGate);` (cùng pattern `_trace`/`_monitor` — comment giải thích dùng chung instance với MAUI container). **Bắt buộc cùng task này** — nếu thiếu, ProxyHostTests (start host thật) ném "no service for DrainGate" vì `DispatcherLoop` đã nhận param mới.

- [ ] **Step 1: Tests failing** (thêm vào `DispatcherLoopTests`):

```csharp
[Fact]
public async Task DispatcherLoop_WhenDraining_DoesNotTakeQueuedRequest() { ... }
// Seed provider, enqueue req, BeginDrain TRƯỚC khi StartAsync, StartAsync,
// WaitUntilAsync 500ms → queue.Contains(id) vẫn true, executions chưa có id.

[Fact]
public async Task DispatcherLoop_WhenDrainCancelled_TakesQueuedRequestAgain() { ... }
// Như trên nhưng sau khi xác nhận không Take: CancelDrain() → WaitUntilAsync
// executions.Contains(id) == true (resume ngay — Review Focus #1).
```

- [ ] **Step 2: FAIL**; **Step 3: Implement** — `DispatcherLoop`: thêm param `DrainGate drainGate` (field `_drain`); trong `ExecuteAsync`: `drainGate.Changed += OnWake;` cạnh `queue.Changed`, unsubscribe trong `finally`; dòng đầu `TryDispatchOnceAsync`: `if (_drain.IsDraining) return false;` (trước `queue.Peek`). Cùng task: `ProxyHost` ctor + forward (xem Interfaces), `MauiProgram` đăng ký singleton, cập nhật 5 ctor call sites trong `ProxyHostTests` + ctor call trong `DispatcherLoopTests`.
- [ ] **Step 4: PASS** (chạy toàn bộ DispatcherLoopTests + ProxyHostTests — không regress) **Step 5: Commit** `feat: pause dispatcher take while drain gate is open`.

---

### Task 6: Snapshot model + `QueueSnapshotStore`

**Files:**
- Modify: `src/RouterBalancing.Core/Storage/StoragePathProvider.cs` (thêm `GetQueueSnapshotPath()`)
- Create: `src/RouterBalancing.Core/Update/QueueSnapshotStore.cs`
- Test: `router balancing test/Update/QueueSnapshotStoreTests.cs`

**Interfaces:**
- Produces (Task 7, 8, 9):

```csharp
public sealed record QueueSnapshot(int Version, DateTimeOffset SavedAt, List<QueueSnapshotItem> Requests);
public sealed record QueueSnapshotItem(string Id, int Priority, string Model, string BodyBase64,
    string Endpoint, bool IsStream, DateTimeOffset EnqueuedAt);

public static class QueueSnapshotStore
{
    public const int CurrentVersion = 1;
    // throws IOException/NotSupported khi ghi fail — caller xử lý
    public static Task SaveAsync(IReadOnlyList<ProxyRequest> requests, string path, CancellationToken ct = default);
    // null = file không tồn tại; ném JsonException/NotSupportedException khi corrupt
    public static Task<QueueSnapshot?> LoadAsync(string path);
}
```

- [ ] **Step 1: Tests failing** (temp dir per test):

- `SaveThenLoad_RoundTripsFields` — tạo `ProxyRequest` thật (DefaultHttpContext + MemoryStream body), 2 item 2 priority; load lại assert id/priority/model/body (decode base64)/endpoint/enqueuedAt.
- `SaveAsync_MarksStreamRequest_WhenPipeKeyPresent` — ctx.Items[SsePipeItems.Key] = new Pipe() → item.IsStream true; item không có key → false (Review Focus #2).
- `SaveAsync_WritesViaTempFile_ThenMoves` — sau khi save: file `<path>.tmp` không tồn tại, file path tồn tại, nội dung parse được (Review Focus #4).
- `LoadAsync_ReturnsNull_WhenFileMissing`.
- `LoadAsync_Throws_WhenCorruptJson` — file rác → `Assert.ThrowsAsync<JsonException>`.
- `GetQueueSnapshotPath_EndsWithFileName` — `Path.GetFileName(...) == "queue-snapshot.json"`.

- [ ] **Step 2: FAIL**; **Step 3: Implement** — `SaveAsync`: map `request.Context.Items.ContainsKey(SsePipeItems.Key)` → IsStream; `(int)request.Priority`; `Endpoint.ToString().ToLowerInvariant()`; `Convert.ToBase64String(request.Body)`; serialize `JsonSerializerOptions { WriteIndented = true }` với `[JsonPropertyName]` snake_case trên record; ghi `path + ".tmp"` rồi `File.Move(tmp, path, overwrite: true)`. `LoadAsync`: `File.Exists` check trước; deserialize; validate `Version == CurrentVersion` không đúng → ném `NotSupportedException` (restorer xoá). `SsePipeItems` là `internal` — cùng assembly Core, OK (test project có `InternalsVisibleTo`).
- [ ] **Step 4: PASS**; **Step 5: Commit** `feat: add queue snapshot json store with atomic write`.

---

### Task 7: `ProxyHost` bridge — in-flight count + ghi snapshot

**Files:**
- Modify: `src/RouterBalancing.Core/Server/IProxyHost.cs` (2 method mới)
- Modify: `src/RouterBalancing.Core/Server/ProxyHost.cs` (implementations)
- Test: `router balancing test/Server/ProxyHostTests.cs` (thêm test)

**Interfaces:**
- Consumes: `QueueSnapshotStore` (Task 6).
- Produces (Task 8, 9, 12):

```csharp
// IProxyHost thêm:
int GetTotalInFlight();                                  // 0 khi proxy chưa chạy
Task<bool> TryWriteQueueSnapshotAsync();                 // false CHỈ khi ghi fail — host chưa chạy: coordinator skip (spec §9.2)
```

- [ ] **Step 1: Test failing** — `GetTotalInFlight_ReturnsZero_WhenNotRunning`; `TryWriteQueueSnapshotAsync_ReturnsFalse_WhenNotRunning`.
- [ ] **Step 2: FAIL**; **Step 3: Implement** — `GetTotalInFlight`: `if (_app is null) return 0;` else resolve `IExecutionList` từ `_app.Services` (capture local pattern như `QueuedSnapshot`). `TryWriteQueueSnapshotAsync`: resolve `IRequestQueue`, gọi `QueueSnapshotStore.SaveAsync(queue.Snapshot(), StoragePathProvider.GetQueueSnapshotPath())`, catch mọi exception → log `Warn` → false.
- [ ] **Step 4: PASS**; **Step 5: Commit** `feat: add proxy host bridges for in-flight count and queue snapshot write`.

---

### Task 8: `QueueSnapshotRestorer`

**Files:**
- Create: `src/RouterBalancing.Core/Update/QueueSnapshotRestorer.cs`
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs` (đăng ký trong `ConfigureServices` — cạnh `AddSingleton<IRequestQueue>`)
- Modify: `src/RouterBalancing.Core/Server/ProxyHost.cs` (gọi restorer sau `builder.Build()`, trước `app.StartAsync`)
- Test: `router balancing test/Update/QueueSnapshotRestorerTests.cs`

**Interfaces:**
- Consumes: `QueueSnapshotStore` (Task 6), `ProxyRequest`/`RequestQueue`/`SsePipeItems` (Core, nội bộ).
- Produces:

```csharp
public sealed class QueueSnapshotRestorer(IRequestQueue queue, IApiMonitorStore monitor,
    ITraceFeed trace, ILogService log)
{
    // noop khi file thiếu; xoá file sau khi xử lý xong (thành công hoặc parse/version fail);
    // từng item fail (id trùng) → skip + log, vẫn tiếp tục
    public Task RestoreAsync(string path, CancellationToken ct = default);
}
```

- [ ] **Step 1: Tests failing** (temp dir, `RequestQueue` thật, fake `IApiMonitorStore`/`ITraceFeed` ghi nhận call — stub classes trong file test):

- `RestoreAsync_EnqueuesItemsInFileOrder_AndDeletesFile` — snapshot 2 item (một Highest, một Normal) → queue.Snapshot() trả đúng 2 id đúng thứ tự; file bị xoá.
- `RestoreAsync_PreservesEnqueuedAtFromSnapshot` — EnqueuedAt load về đúng giá trị trong file, KHÔNG phải UtcNow (Review Focus #3 — `RequestQueue.Enqueue` ghi đè, restorer set lại sau khi enqueue).
- `RestoreAsync_WhenIsStream_PublishesPipeInContextItems` — item isStream → surrogate context có pipe tại `SsePipeItems.Key`; item non-stream → không có key.
- `RestoreAsync_CallsMonitorStartRequest` — fake monitor nhận đúng (id, model, body).
- `RestoreAsync_Noop_WhenFileMissing`.
- `RestoreAsync_DeletesFileAndSkips_WhenCorrupt` — file rác → không enqueue gì, file biến mất.
- `RestoreAsync_DeletesFileAndSkips_WhenVersionUnknown` — version 99 → y hệt.
- `RestoreAsync_SkipsDuplicateId` — 2 item cùng id → queue chỉ có 1.

- [ ] **Step 2: FAIL**; **Step 3: Implement**

Algorithm surrogate (spec §8 — phần không determine bởi signature):

```
mỗi item: priority = (RequestPriority)item.Priority; endpoint = Enum.Parse<ProxyEndpoint>(item.Endpoint, ignoreCase: true)
ctx = new DefaultHttpContext { Response.Body = Stream.Null }; ctx.RequestAborted = CancellationToken.None
request = new ProxyRequest(item.Id, priority, item.Model, Convert.FromBase64String(item.BodyBase64), ctx, endpoint)
nếu item.IsStream:
    pipe = new Pipe(); ctx.Items[SsePipeItems.Key] = pipe
    fire-and-forget DrainPipeAsync(pipe, request.Completion.Task): loop ReadAsync → AdvanceTo(buffer.End);
    break khi result.IsCompleted || result.IsCanceled || completion.IsCompleted; finally pipe.Reader.Complete()
nếu !queue.Enqueue(request) → log Warn, continue
request.EnqueuedAt = item.EnqueuedAt   // internal set — PHẢI sau Enqueue (Review Focus #3)
monitor.StartRequest(item.Id, item.Model, bodyBytes)
trace.Publish(new TraceEvent(item.Id, TraceStage.Received, item.Model, null, null, null, null, null,
    DateTimeOffset.Now, Priority: request.Priority, Endpoint: item.Endpoint))
cuối method: nếu đã từng thấy file (parse hoặc fail) → File.Delete(path) bọc try/catch; parse fail → log Warn
```

`TraceEvent` copy shape từ `ProxyApp.cs:210-214`. Không đăng ký `RequestAborted.Register` (token None không bao giờ fire).

- [ ] **Step 4: PASS**; **Step 5: Wiring** — `ProxyApp.ConfigureServices`: `builder.Services.AddSingleton<QueueSnapshotRestorer>();`. `ProxyHost.StartAsync` sau `ProxyApp.ConfigurePipeline(app)`, trước khối try StartAsync:

```csharp
await app.Services.GetRequiredService<QueueSnapshotRestorer>()
    .RestoreAsync(StoragePathProvider.GetQueueSnapshotPath(), cancellationToken);
```

Chạy lại toàn bộ test (ProxyHostTests không regress). **Step 6: Commit** `feat: restore queue snapshot into dispatcher pipeline at proxy start`.

---

### Task 9: `IAppShutdown` + `UpdateCoordinator`

**Files:**
- Create: `src/RouterBalancing.Core/Update/UpdateCoordinator.cs` (IAppShutdown + enum UpdateError + class)
- Create: `router-balancing/Services/MauiAppShutdown.cs`
- Test: `router balancing test/Update/UpdateCoordinatorTests.cs`

**Interfaces:**
- Consumes: `IAppUpdateService` (Task 2), `DrainGate` (Task 3), `IProxyHost` (Task 7).
- Produces (Task 12):

```csharp
public enum UpdateError { DownloadFailed, SnapshotFailed, InstallFailed, DrainFailed } // DrainFailed: fix round Task 9 (append-only, UI xử lý đủ 4 giá trị)
public interface IAppShutdown { void Quit(); }

public sealed class UpdateCoordinator : IDisposable
{
    public UpdateCoordinator(IAppUpdateService updates, DrainGate gate, IProxyHost proxy,
        IAppShutdown shutdown, ILogService log, string snapshotPath);
    public bool UpdateInProgress { get; }
    public bool IsFinalizing { get; }      // true khi snapshot/install — khoá Hủy
    public int DownloadPercent { get; }    // 0..100
    public int InFlightCount { get; }
    public event Action? Changed;          // UI re-render (mọi property đổi)
    public event Action<UpdateError>? Failed;
    public event Action? Cancelled;
    public Task StartUpdateAsync(ReleaseInfo release);   // fire-and-forget nội bộ, trả về khi pipeline kết thúc
    public void CancelUpdate();                           // no-op khi IsFinalizing hoặc chưa start
    public void Dispose();                                // cancel CTS
}
```

`MauiAppShutdown`: `public sealed class MauiAppShutdown : IAppShutdown { public void Quit() => Application.Current?.Quit(); }`.

- [ ] **Step 1: Test doubles + tests failing** — fake `IAppUpdateService` (check/download/install điều khiển được: TaskCompletionSource cho download, cờ `InstallResult`), fake `IProxyHost` (implement interface, member không dùng ném `NotSupportedException`; `GetTotalInFlight` trả giá trị set được; `TryWriteQueueSnapshotAsync` ghi file thật vào temp path + trả cờ), `RecordingShutdown : IAppShutdown`. Tests:

- `StartUpdateAsync_WhenBothGatesDone_WritesSnapshotThenInstallsThenQuits` — download TCS set + InFlight 0 → WaitUntilAsync: file snapshot tồn tại (fake proxy ghi), Install được gọi, shutdown đếm 1; `IsFinalizing` true trước khi quit.
- `StartUpdateAsync_WhenDownloadFails_ResumesDrainAndRaisesFailed` — download trả null → gate.IsDraining false, Failed=DownloadFailed (Review Focus: resume — dispatcher không park vĩnh viễn).
- `StartUpdateAsync_WhenSnapshotFails_ResumesDrainAndRaisesFailed` — fake proxy TryWrite false → Failed=SnapshotFailed, gate false, không gọi install.
- `StartUpdateAsync_WhenInstallFails_DeletesSnapshotAndResumesDrain` — file snapshot có sẵn (fake ghi), install false → file bị xoá, Failed=InstallFailed, gate false (Review Focus #5).
- `StartUpdateAsync_WhenProxyInFlightPositive_WaitsBeforeSnapshot` — GetTotalInFlight = 1 → sau download xong vẫn chưa TryWrite; đổi về 0 → proceed.
- `CancelUpdate_BeforeFinalize_CancelsDownloadAndResumes` — Cancelled event, gate false, không snapshot.
- `CancelUpdate_DuringFinalize_IsNoOp`.
- `StartUpdateAsync_WhenAlreadyInProgress_Throws` — `await Assert.ThrowsAsync<InvalidOperationException>(...)`.
- `Dispose_CancelsPendingUpdate`.

- [ ] **Step 2: FAIL**; **Step 3: Implement**

Algorithm `StartUpdateAsync` (spec §6.2/§7 — không determine bởi signature):

```
guard: OperatingSystem.IsWindows() else return; guard: !UpdateInProgress else throw InvalidOperationException
UpdateInProgress=true; Changed?.Invoke()
gate.BeginDrain(); InFlightCount = proxy.GetTotalInFlight()
cts = new CancellationTokenSource()
downloadTask = Task.Run(() => updates.DownloadUpdateAsync(release, progressAdapter))  // progress → DownloadPercent + Changed
pollTask = Task.Run: loop 500ms until cts cancelled:
    InFlightCount = proxy.GetTotalInFlight(); Changed?.Invoke()
    nếu downloadTask.IsCompleted && InFlightCount == 0 → break; return success/fail
await WhenAny(pollTask, cts) → cancelled → return
nếu download fail (null path) → Fail(DownloadFailed): gate.CancelDrain(); ResetState(); return
IsFinalizing=true; Changed?.Invoke()
nếu proxy.IsRunning && !await proxy.TryWriteQueueSnapshotAsync() → Fail(SnapshotFailed) như trên; proxy chưa chạy → skip snapshot, tiếp tục install (spec §9.2 — final-review I1)
nếu !await updates.InstallUpdateAsync() → File.Delete(snapshotPath) bọc try/catch; Fail(InstallFailed); return
shutdown.Quit()
```

`Fail(...)` helper: log Warn, đặt cờ, bắn `Failed`, `gate.CancelDrain()`, `UpdateInProgress=false`, Changed. `CancelUpdate`: nếu `!UpdateInProgress || IsFinalizing` → return; `cts.Cancel()`; toast-free — bắn `Cancelled`; `gate.CancelDrain()`; `UpdateInProgress=false`; Changed. Guard thread-safety đơn giản bằng `Interlocked`/lock field `_sync`.

- [ ] **Step 4: PASS**; **Step 5: Commit** `feat: add update coordinator orchestrating download, drain, snapshot and install`.

---

### Task 10: `UpdateCheckService`

**Files:**
- Create: `src/RouterBalancing.Core/Update/UpdateCheckService.cs`
- Test: `router balancing test/Update/UpdateCheckServiceTests.cs`

**Interfaces:**
- Consumes: `IAppUpdateService` (Task 2).
- Produces (Task 12):

```csharp
public enum UpdateCheckResult { UpdateAvailable, AlreadyLatest, Failed }

public sealed class UpdateCheckService : IAsyncDisposable
{
    public UpdateCheckService(IAppUpdateService updates, ILogService log);
    public ReleaseInfo? AvailableRelease { get; }
    public bool IsUpdateAvailable { get; }
    public event Action? StateChanged;                  // badge re-render
    public event Action<ReleaseInfo>? UpdateFound;      // CHỈ khi transition none→available (toast 1 lần)
    public void Start();                                // non-Windows → noop; check sau delay 5s rồi lặp 24h
    public Task<UpdateCheckResult> CheckOnceAsync();    // manual; Failed khi check null (UI không toast lỗi)
    public ValueTask DisposeAsync();                    // cancel loop
}
```

- [ ] **Step 1: Tests failing** (fake `IAppUpdateService` như Task 9):

- `CheckOnceAsync_UpdateAvailable_SetsStateAndRaisesUpdateFound` — check 1: release mới → result UpdateAvailable, IsUpdateAvailable true, UpdateFound bắn 1 lần.
- `CheckOnceAsync_SecondTimeWithSameRelease_DoesNotRaiseUpdateFoundAgain` — transition chỉ 1 lần (badge đã thấy).
- `CheckOnceAsync_AlreadyLatest_ClearsPreviousState` — sau khi có update, check lại báo bằng nhau → IsUpdateAvailable false, AvailableRelease null.
- `CheckOnceAsync_WhenCheckReturnsNull_ReturnsFailed_StateUnchanged`.
- `Start_OnNonWindows_IsNoOp` — Start() → loop không chạy (dispose ngay, không ném).

- [ ] **Step 2: FAIL**; **Step 3: Implement** — pattern `LogRetentionWorker` (`Start()` + `PeriodicTimer`): Task.Run loop; delay đầu `Task.Delay(5s, ct)` rồi check; check = try `CheckForUpdatesAsync` catch → null; `IsUpdateAvailable(release)` để set state; PeriodicTimer 24h. Log Info khi tìm thấy, Warn khi fail (không throw).
- [ ] **Step 4: PASS**; **Step 5: Commit** `feat: add background update check service with 24h periodic timer`.

---

### Task 11: Project `RouterBalancing.Updater`

**Files:**
- Create: `RouterBalancing.Updater/RouterBalancing.Updater.csproj`
- Create: `RouterBalancing.Updater/Program.cs`
- Modify: `router-balancing.slnx` (thêm project)
- Modify: `router-balancing/router-balancing.csproj` (copy updater exe vào output TFM windows)

**Interfaces:**
- Consumed bởi Task 2 (`InstallUpdateAsync` launch exe này).
- Contract: `RouterBalancing.Updater.exe --zip <zip-path> --pid <parent-pid> --app-dir <app-directory>` — exit 0 khi thành công, 1 + stderr khi fail.

- [ ] **Step 1: Tạo csproj** — copy y hệt `D:\Code\DMFT\DMFT.Updater\DMFT.Updater.csproj` đổi `RootNamespace`/`AssemblyName` thành `RouterBalancing.Updater`.
- [ ] **Step 2: Port `Program.cs`** từ `D:\Code\DMFT\DMFT.Updater\Program.cs`: giữ nguyên structure (pattern args list, goto error, Press Enter khi fail); thay đổi: timeout `Process.WaitForExit(60_000)` (spec §10 — MAUI app nặng hơn DMFT), exe name `router-balancing.exe`, self name `RouterBalancing.Updater.exe`.
- [ ] **Step 3: Thêm vào slnx** — `<Project Path="RouterBalancing.Updater/RouterBalancing.Updater.csproj" />`.
- [ ] **Step 4: Copy wiring trong app csproj** — ItemGroup có condition windows (cạnh FrameworkReference WinForms hiện có):

```xml
<ProjectReference Include="..\RouterBalancing.Updater\RouterBalancing.Updater.csproj"
                  ReferenceOutputAssembly="false"
                  OutputItemType="Content"
                  CopyToOutputDirectory="PreserveNewest" />
```

- [ ] **Step 5: Verify build** — `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` rồi kiểm tra `RouterBalancing.Updater.exe` nằm trong output directory của app. Nếu MAUI multi-TFM không copy được theo cách này → fallback: target `AfterBuild` copy từ `..\RouterBalancing.Updater\bin\$(Configuration)\net10.0\` (MSBuild `Copy` task, Condition windows).
- [ ] **Step 6: Commit** `feat: add separate updater executable and wire it into windows build output`.

---

### Task 12: UI + i18n + DI wiring

**Files:**
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (thêm block `// Application update` ở cả 2 dict EN/VI)
- Modify: `router-balancing/MauiProgram.cs` (đăng ký 5 singleton —見 Global)
- Modify: `router-balancing/App.xaml.cs` (inject + Start UpdateCheckService; dispose khi thoát cạnh `_retention`)
- Create: `router-balancing/Components/Shared/UpdateModal.razor`
- Modify: `router-balancing/Components/Layout/MainLayout.razor` (footer version + render `<UpdateModal />` + toasts cho update events)
- Modify: `router-balancing/Components/Pages/SettingsPanel.razor` (section Application update)

**Interfaces:**
- Consumes: mọi interface Task 1-10.

- [ ] **Step 1: i18n keys** — thêm vào cả 2 dict (`English` + `Vietnamese`):

```
update.title, update.version, update.badge, update.check, update.now,
update.latest, update.available ( "{0}" ), update.downloading ( "{0}" ),
update.draining ( "{0}" ), update.preparing, update.cancel, update.cancelled,
update.failed ( "{0}" )
```

Giá trị EN/VI theo spec §9.5 (VD EN: `"Waiting for {0} request(s) to finish…"`, VI: `"Đang chờ {0} request hoàn tất…"`).

- [ ] **Step 2: DI wiring trong MauiProgram** — cạnh các singleton Core khác:

```csharp
builder.Services.AddSingleton<IAppUpdateService>(_ => new GitHubAppUpdateService(new HttpClient()));
builder.Services.AddSingleton<DrainGate>();
builder.Services.AddSingleton<UpdateCoordinator>(_ =>
    new UpdateCoordinator(_.GetService<IAppUpdateService>()!, _.GetService<DrainGate>()!,
        _.GetRequiredService<IProxyHost>(), _.GetRequiredService<IAppShutdown>(),
        _.GetRequiredService<ILogService>(), StoragePathProvider.GetQueueSnapshotPath()));
builder.Services.AddSingleton<UpdateCheckService>();
builder.Services.AddSingleton<IAppShutdown, MauiAppShutdown>();
```

(Lưu ý: viết đúng DI overload — ví dụ trên minh hoạ; implementer dùng `GetRequiredService` trong factory.)

- [ ] **Step 3: App.xaml.cs** — inject `UpdateCheckService`; trong ctor sau `_freeSync.Start()`: `_updateCheck.Start();`; trong `StopProxyOnExit` (khối dừng worker): `_updateCheck.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));`.

- [ ] **Step 4: `UpdateModal.razor`** — overlay full-screen (`fixed inset-0 z-[60] bg-black/50 flex items-center justify-center`), card hiển thị: `update.downloading` + `%` (thanh progress `<div class="h-2 rounded bg-primary" style="width:...">`), `update.draining` với `Coordinator.InFlightCount`, `update.preparing` khi `IsFinalizing` (ẩn nút Hủy), nút `update.cancel` gọi `Coordinator.CancelUpdate()`. `@implements IDisposable`, subscribe `Coordinator.Changed` → `InvokeAsync(StateHasChanged)`; component tự render rỗng khi `!UpdateInProgress`. Đặt style theo pattern Tailwind của `Modal.razor`/`ConfirmDialog.razor` hiện có.

- [ ] **Step 5: MainLayout** — inject `UpdateCheckService` + `UpdateCoordinator`; thêm footer dưới `<main>`:

```razor
<footer class="border-t border-border bg-surface px-4 py-1 text-right text-xs text-neutral-500">
    v@_appVersion
</footer>
```

`_appVersion = AppVersionFormatter.Format(Version.Parse(update.CurrentVersion));` (Task 1). Render `<UpdateModal />` trước toast stack. Subscribe: `UpdateCheckService.UpdateFound` → `Toast.Show(string.Format(L["update.available"], release.TagName))`; `Coordinator.Failed` → toast `update.failed` (lý do theo enum); `Coordinator.Cancelled` → toast `update.cancelled` (unsubscribe trong Dispose).

- [ ] **Step 6: SettingsPanel section** — thêm section cuối trang (pattern section hiện có):

```razor
<section class="mb-4 rounded border border-border bg-surface p-4">
    <h2 class="mb-3 text-base font-semibold">@L["update.title"]</h2>
    <div class="text-sm">@L["update.version"]: @_appVersion</div>
    @if (Check.IsUpdateAvailable) { <span class="...badge...">@L["update.badge"]</span> }
    <button class="btn btn-primary mt-3" disabled=@_checking @onclick=CheckAsync>@L["update.check"]</button>
    @if (Check.IsUpdateAvailable) {
        <button class="btn btn-primary mt-3" @onclick=UpdateNowAsync>@L["update.now"]</button>
    }
</section>
```

`CheckAsync`: `await Check.CheckOnceAsync()` → AvailableRelease hiện → toast `update.available`; AlreadyLatest → toast `update.latest`; Failed → im lặng (spec §11). `UpdateNowAsync`: `_ = Coordinator.StartUpdateAsync(Check.AvailableRelease!);` — **fire-and-forget, KHÔNG await** (method trả về khi update kết thúc/quit — await sẽ giữ handler mở suốt thời gian download; modal hiển thị qua `UpdateInProgress` + event `Changed` không cần await). Inject `UpdateCheckService Check`, `UpdateCoordinator Coordinator`. Hiển thị version Task 1 formatter. Subscribe `Check.StateChanged` → StateHasChanged (Dispose unsub).

- [ ] **Step 7: Build + test toàn solution** —

```bash
dotnet build router-balancing.slnx
dotnet test "router balancing test/router balancing test.csproj"
```

**Step 8: Commit** `feat: add update ui, version display and startup/periodic update check`.

---

## Out of Scope (spec §13 — không thực hiện trong plan này)

- Auto-update Android/iOS/MacCatalyst.
- CI packaging release zip (chỉ chốt format asset).
- Response storage để client retrieve lại.
- SemVer 2.0 pre-release/build metadata.
