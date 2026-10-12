# Thiết kế: Hiển thị Version + Auto-Update (GitHub)

- **Ngày:** 2026-10-11
- **Trạng thái:** Chờ user review
- **Phạm vi:** Windows desktop (MAUI Blazor Hybrid); các TFM khác không tham gia auto-update

## 1. Mục tiêu

1. Hiển thị version dạng rút gọn: luôn 2 số đầu, 2 số sau chỉ hiện khi khác 0.
2. Check update từ GitHub Releases (giống DMFT) + thông báo qua toast và badge trong Settings.
3. Luồng update an toàn: tải zip song song với drain danh sách thực thi, popup chờ (có Hủy), snapshot hàng đợi ra JSON trước khi tắt app, updater tách riêng cài và khởi động lại app.
4. Ở startup, nạp snapshot lại vào queue và đẩy tiếp từ hàng đợi → danh sách thực thi; xoá file sau khi nạp.

## 2. Quyết định thiết kế (đã chốt)

| # | Quyết định | Lựa chọn |
|---|---|---|
| 1 | Check update | Startup + nút thủ công trong Settings + nền lặp 24h kể từ lúc mở app |
| 2 | Thông báo | Toast (`ToastService`) + badge "Update available" trong Settings |
| 3 | Version UI | Cả Settings lẫn footer MainLayout |
| 4 | Format version | `1.0.0.0`→`1.0`, `1.0.3.0`→`1.0.3`, `1.0.0.4`→`1.0.0.4`, `1.0.3.4`→`1.0.3.4` |
| 5 | Thứ tự tải/drain | Tải zip song song với drain; popup có 2 info (tiến trình + số request đang chạy) + nút Hủy |
| 6 | Kênh phát hành | GitHub `HKStudio011/router-balancing`, asset zip chứa app + updater exe |
| 7 | Snapshot | File JSON `%AppData%/router-balancing/queue-snapshot.json`, xoá sau khi nạp |
| 8 | Đóng băng dispatch | `DrainGate` (cờ + event), `DispatcherLoop` kiểm tra cờ trước Peek |
| 9 | Thực thi lại request đã restore | Surrogate `DefaultHttpContext` (Body = `Stream.Null`) + drain pipe cho stream; dispatcher/handler không đổi |
| 10 | Khi restart | Request trong queue mất kết nối client cũ — response của lần re-execute bị bỏ đi; client tự retry/phải gửi lại (chấp nhận chạy đôi) |

## 3. Versioning

### 3.1 Nguồn version duy nhất

Trong `router-balancing.csproj`:

```xml
<Version>1.0.0.0</Version>
<ApplicationDisplayVersion>$(Version)</ApplicationDisplayVersion>
<ApplicationVersion>1</ApplicationVersion> <!-- Android int, giữ nguyên, không liên quan update -->
```

Assembly version đọc qua `Assembly.GetEntryAssembly()?.GetName().Version` (nguồn `CurrentVersion` cho update service).

### 3.2 `AppVersionFormatter` — `src/RouterBalancing.Core/Update/AppVersionFormatter.cs`

- `static string Format(Version v)`: luôn hiện `Major.Minor`; `Build` hiện nếu ≠ 0; `Revision` hiện nếu ≠ 0. Cắt trailing-zero nhưng tối thiểu 2 phần.
  - `1.0.0.0`→`1.0`; `1.0.3.0`→`1.0.3`; `1.0.0.4`→`1.0.0.4`; `1.0.3.4`→`1.0.3.4`.
- Pure function, unit test đủ các trường hợp (kể cả `Major≠0`, `Minor≠0`, Build/Revision = 0 xen kẽ).

### 3.3 So sánh release

- Tag dạng `vX.Y.Z` hoặc `vX.Y.Z.W` — strip `v`, pad phần thiếu bằng 0, so sánh từng phần (port `CompareSemanticVersions` của DMFT).
- `IsUpdateAvailable = compare(releaseTag, CurrentVersion) > 0`.

## 4. Update service — `src/RouterBalancing.Core/Update/`

Port `AppUpdateService` của DMFT, đặt trong Core để unit test được:

```
IAppUpdateService
├─ string CurrentVersion            // Version.ToString() (4 số)
├─ Task<ReleaseInfo?> CheckForUpdatesAsync()
├─ bool IsUpdateAvailable(ReleaseInfo release)
├─ Task<string?> DownloadUpdateAsync(ReleaseInfo release, IProgress<int>? progress)
├─ string? DownloadedZipPath { get; }
└─ Task<bool> InstallUpdateAsync()
```

- Endpoint: `https://api.github.com/repos/HKStudio011/router-balancing/releases/latest`; UA: `RouterBalancing/{CurrentVersion}`.
- Asset: `.zip`, không chứa `symbols` (giống DMFT).
- Download về `%Temp%/RouterBalancing_Update/<asset-name>` — mỗi lần Update bấm là tải mới (ghi đè), không reuse giữa các lần.
- `InstallUpdateAsync`: tìm `RouterBalancing.Updater.exe` cạnh app exe; `Process.Start` với `--zip --pid {Environment.ProcessId} --app-dir {AppContext.BaseDirectory}`.
- **Guard:** mọi method trả `null/false` (không ném) khi không phải Windows (`OperatingSystem.IsWindows()`); check được gọi ngầm lúc startup nên tuyệt đối không toast lỗi khi check fail — chỉ log.
- `ReleaseInfo`/`ReleaseAsset` records: giữ nguyên shape JSON của GitHub (giống DMFT).

## 5. DrainGate + DispatcherLoop (đóng băng dispatch)

### 5.1 `DrainGate` — `src/RouterBalancing.Core/Engine/DrainGate.cs`

Singleton dùng chung **giữa hai DI container** (MAUI + proxy — theo đúng pattern `ITraceFeed`/`IApiMonitorStore` đang làm: tạo ở MAUI container, `ProxyHost` forward instance vào proxy builder):

```csharp
public sealed class DrainGate
{
    public bool IsDraining { get; private set; }
    public event Action? Changed;              // begin/cancel — dispatcher wake
    public void BeginDrain();                  // idempotent; bắn Changed
    public void CancelDrain();                 // idempotent; bắn Changed
}
```

- Không chứa logic count — **UpdateCoordinator** (§7) sở hữu tư cách "chờ 2 gate xong". DrainGate thuần là cờ liên container để dispatcher thấy.

### 5.2 `DispatcherLoop` — thay đổi tối thiểu

- Đăng ký thêm `drainGate.Changed += OnWake` (subscribe/unsubscribe cạnh `queue.Changed` hiện có) — cancel drain thì dispatcher wake lại ngay.
- `TryDispatchOnceAsync`: thêm **dòng đầu** (trước `queue.Peek`):

```csharp
if (drainGate.IsDraining) return false;
```

- Request đang serve dở (đã Take, nằm trong ExecutionList) chạy bình thường đến cùng — chính là nội dung "drain".
- Request tới trong lúc đóng băng: endpoint vẫn enqueue + commit 200 (stream) như cũ, chỉ không được Take.

### 5.3 `IExecutionList` — thêm property

```csharp
/// <summary>Tổng số request đang in-flight (drain chờ về 0).</summary>
int TotalInFlight { get; }
```

Implementation: sum các count hiện có dưới lock (đã có sẵn count per provider/account).

## 6. Snapshot JSON

### 6.1 Định dạng — `%AppData%/router-balancing/queue-snapshot.json`

```json
{
  "version": 1,
  "savedAt": "2026-10-11T08:00:00+00:00",
  "requests": [
    { "id": "ab12cd34", "priority": 0, "model": "gpt-4o",
      "bodyBase64": "...", "endpoint": "chat", "isStream": true,
      "enqueuedAt": "2026-10-11T07:59:50+00:00" }
  ]
}
```

- `priority`: int theo enum `RequestPriority`; `endpoint`: `chat` | `responses`.
- Serializer: `System.Text.Json` — property naming snake_case tường minh (`JsonPropertyName`), camelCase mặc định không dùng.
- Ghi atomic: serialize → ghi `queue-snapshot.json.tmp` → `File.Move(tmp, path, overwrite: true)`.
- Path lấy từ `StoragePathProvider.GetDataDirectory()` (tự tạo thư mục nếu thiếu).

### 6.2 Timing

```
Cập nhật bấm → BeginDrain + bắt đầu tải zip (song song)
      │
      ├─ gate 1: tải zip xong (progress 100%)
      └─ gate 2: Tổng in-flight == 0
      │
CẢ HAI gate xong → snapshot queue (bao gồm request tích trong lúc đóng băng)
      │
     ghi file OK → InstallUpdateAsync (launch updater) → app.Quit()
      │
     ghi file FAIL → CancelDrain (resume dispatch) + toast lỗi → HUỶ update
```

- Cửa sổ nhỏ giữa lúc snapshot xong và process tắt: request mới vào queue sẽ mất — chấp nhận được (app đang đóng).
- Trong lúc chờ 2 gate: cancel bằng nút Hủy (§7).

### 6.3 Ai làm gì

- `QueueSnapshotWriter` (Core, **pure**: nhận `IReadOnlyList<ProxyRequest>` + đường dẫn file): chuyển sang model → ghi atomic. Unit test với temp dir, không cần DI.
- Gọi từ MAUI side qua **bridge của `ProxyHost`** (queue chỉ sống trong proxy DI container — đúng pattern `QueuedSnapshot()` sẵn có):

```csharp
// IProxyHost — false CHỈ khi ghi fail (coordinator huỷ update theo §7);
// host chưa chạy không gọi method này — coordinator skip theo §9.2
Task<bool> TryWriteQueueSnapshotAsync();
// implementation: resolve IRequestQueue từ app.Services → QueueSnapshotWriter.WriteAsync(snapshot, path)
```

## 7. UpdateCoordinator (app layer, MAUI DI singleton)

Orchestrate toàn bộ luồng — UI chỉ gọi 2 method và lắng event:

```csharp
public sealed class UpdateCoordinator
{
    public event Action<int>? DownloadProgress;     // 0..100
    public event Action? StateChanged;              // modal re-render
    public bool UpdateInProgress { get; }

    public Task StartUpdateAsync();                 // bấm "Cập nhật ngay"
    public void CancelUpdate();                     // nút Hủy trong popup
}
```

`StartUpdateAsync` (chạy nền, không block UI):

1. Guard `IsWindows` + `!UpdateInProgress`.
2. `drainGate.BeginDrain()`.
3. Bắt đầu `DownloadUpdateAsync` (report `DownloadProgress`).
4. Poll mỗi 500 ms: nếu **(download xong)** ∧ **(`proxyHost.GetTotalInFlight() == 0`)** → sang snapshot.
   - Download fail giữa chừng → `CancelDrain()` + toast lỗi → abort.
5. `proxyHost.TryWriteQueueSnapshotAsync()` (bridge §6.3):
   - proxy chưa chạy (`!IsRunning`) → **bỏ qua snapshot** (không có gì để persist — §9.2) → sang `InstallUpdateAsync()` luôn.
   - `true` → `InstallUpdateAsync()`:
      - `true` → modal sang trạng thái khoá (không Hủy) → `Application.Current.Quit()`.
      - `false` → **xoá snapshot file** (tránh restore oan ở lần boot sau) + `CancelDrain()` + toast lỗi.
   - `false` (ghi fail thật) → `CancelDrain()` + toast lỗi → abort.
6. `CancelUpdate()`: huỷ download CTS + `drainGate.CancelDrain()` + toast "Đã hủy cập nhật" — dispatcher resume đẩy queue → thực thi bình thường, không snapshot.

**Bridge đếm in-flight:** thêm `int GetTotalInFlight()` vào `IProxyHost`/`ProxyHost` — resolve `IExecutionList` từ `app.Services` (cùng pattern `QueuedSnapshot()` sẵn có), trả 0 khi proxy chưa chạy. Coordinator poll qua bridge này (không cần event mới).

## 8. Restore ở startup — `QueueSnapshotRestorer`

- **Vị trí chạy:** trong `ProxyHost.StartAsync`, sau `builder.Build()`, **trước** `app.StartAsync(cancellationToken)` — DispatcherLoop là `BackgroundService` nên chưa start, không tranh Take. Resolve từ `app.Services`.
- **Đối tượng:** file snapshot tồn tại → parse → enqueue toàn bộ → **xoá file** (yêu cầu user). Không có file → noop.
- **Không restore khi:** JSON hỏng/không parse được, `version` field ≠ 1 (không biết) → log warning + xoá file + bỏ qua.
- **Mỗi item → `ProxyRequest` surrogate:**
  - Tạo `DefaultHttpContext` giả: `Response.Body = Stream.Null`, `RequestAborted = CancellationToken.None`, Response.StatusCode = 200 (để handler ghi vô hại).
  - Giữ nguyên `Id`, `Priority`, `Model`, `Body`, `Endpoint`, `EnqueuedAt` (copy từ model); `Sequence` do `RequestQueue.Enqueue` gán lại — thứ tự relative giữ bằng cách enqueue theo đúng thứ tự trong file (file đã sort theo Priority → Sequence).
  - `isStream = true`: tạo `Pipe`, publish vào `ctx.Items[SsePipeItems.Key]` (trước khi enqueue — đúng luật two-writer của spec §2.3), spawn task surrogate-drain: đọc pipe đến khi `request.Completion.Task` hoàn tất rồi `CompleteReader` — response tự tiêu hoá.
  - Đăng ký `ctx.RequestAborted.Register` bỏ qua (token là `CancellationToken.None`, không bao giờ fire).
- **Sau khi enqueue:** `monitor.StartRequest(id, model, body)` + publish trace `Received` — request restore hiện trong monitor/trace như request thường.
- **Cancel API:** hoạt động nguyên (`queue.TryRemove` + `Completion.TrySetResult(Cancelled)` — surrogate context không can thiệp).
- **Thất bại giữa chừng từng item** (id trùng — gần như không xảy ra): skip item đó, log; vẫn xoá file sau khi xử lý xong toàn bộ (file đã đọc hết vào bộ nhớ trước khi enqueue).

## 9. UI (Blazor)

### 9.1 Settings page

- Version (đã format) — luôn thấy.
- Badge **"Update available"** — hiện khi lần check gần nhất tìm thấy release mới (state giữ trong `UpdateCheckState` singleton).
- Nút **"Kiểm tra cập nhật"** — check thủ công: đang check thì disabled + toast kết quả (có update / đã là bản mới nhất / check fail → im lặng log, không toast lỗi).
- Nút **"Cập nhật ngay"** — chỉ render khi badge hiện → mở modal cập nhật + `StartUpdateAsync()`.

### 9.2 Modal cập nhật (component Blazor, overlay full-screen, không đóng được bằng click ngoài)

Trạng thái:
1. **Đang chạy:** thanh tiến trình tải (%) + "Còn {N} request đang chạy" (poll 500 ms) + nút **Hủy**.
2. **Hoàn tất 2 gate → snapshot → cài:** hiện "Đang chuẩn bị cài đặt..." — khoá Hủy.
3. App tự tắt.

Nếu proxy chưa chạy (không có request nào): hiện "Còn 0 request đang chạy", gate 2 xong ngay.

### 9.3 MainLayout

- Footer: version đã format (đọc từ `IAppUpdateService.CurrentVersion` qua formatter).

### 9.4 Check tự động

- `UpdateCheckService` (MAUI singleton, Windows-only): chạy 1 lần sau khi app ready (fire-and-forget, delay ~5 s), rồi `PeriodicTimer` 24h lặp — **không toast khi không có update**; khi có update → toast "Có bản cập nhật mới {version}" + set badge state (`StateChanged` → Settings re-render nếu đang mở). Guard chống check chồng lấp (semaphore).

### 9.5 i18n

Tất cả chuỗi qua `Translations.cs` (EN + VI): `update.badge`, `update.check`, `update.now`, `update.downloading`, `update.draining` ("Còn {0} request đang chạy"), `update.preparing`, `update.cancel`, `update.cancelled`, `update.available`, `update.latest`, `update.failed`…

## 10. Updater tách riêng — `RouterBalancing.Updater/`

Console app `net10.0` tại root solution (mirror `DMFT.Updater`), `AssemblyName=RouterBalancing.Updater`.

`Program.cs` — cổng từ DMFT với thay đổi:

- Args: `--zip <path> --pid <pid> --app-dir <dir>` (usage + exit 1 nếu sai).
- Chờ PID thoát: timeout **60 s** (DMFT 30 s — app MAUI lớn hơn).
- Backup `router-balancing.exe` → `.bak`; self-move `RouterBalancing.Updater.exe` → `.old` (cho phép overwrite).
- `ZipFile.ExtractToDirectory(zip, appDir, overwrite: true)`.
- Start lại `router-balancing.exe` (`UseShellExecute`, `WorkingDirectory=appDir`).
- Dọn `.bak`, `.old`, zip; fail → stderr + `Press Enter` (như DMFT).

**Tích hợp build:** app csproj (hoặc Directory.Build.props) copy `RouterBalancing.Updater.exe` vào output app khi build Windows (`Condition` TFM windows). Solution thêm project updater.

**Release:** asset zip = thư mục publish của app (gồm updater exe). Packaging tự động hóa để sau (spec này chỉ chốt format asset).

## 11. Bảng xử lý lỗi

| Tình huống | Xử lý |
|---|---|
| Check fail (mạng/API) | Log, giữ nguyên badge cũ, không toast lỗi |
| Tải zip fail giữa drain | `CancelDrain()` + resume dispatch + toast lỗi, huỷ update |
| Ghi snapshot fail | `CancelDrain()` + toast lỗi, huỷ update |
| Launch updater fail | **Xoá snapshot** + `CancelDrain()` + toast lỗi |
| Bấm Hủy trong popup | Huỷ tải + `CancelDrain()` + toast "Đã hủy cập nhật" |
| User tắt app giữa drain | Exit thường — **không snapshot**, zip vẫn còn trong %Temp% (m lần Update sau tải lại) |
| File snapshot hỏng/không phải version 1 | Log warning + xoá, bỏ qua |
| Check trên non-Windows | Guard sớm, noop |

## 12. Test

**Unit (`router balancing test`):**
- `AppVersionFormatterTests` — bảng format §3.2.
- Update service: `IsUpdateAvailable` (pad, `v` prefix, bằng nhau, cũ hơn), `CheckForUpdatesAsync` trả null khi non-JSON/non-success (HttpClient stub).
- `DrainGateTests` — begin/cancel idempotent, event bắn đúng.
- `DispatcherLoop` pause/resume — item trong queue không được Take khi `IsDraining`; `CancelDrain()` → được Take lại (test双 dùng doubles hiện có của dispatcher).
- `QueueSnapshotWriter` roundtrip — ghi/đọc, atomic (tmp không sót), path tự tạo thư mục.
- `QueueSnapshotRestorerTests` — enqueue đúng thứ tự + xoá file; JSON hỏng → xoá + không enqueue; version lạ → xoá; `isStream` → có pipe trong Items; `monitor.StartRequest` được gọi.
- `IExecutionList.TotalInFlight` đúng.

**Integration:** drain flow end-to-end (TestServer): enqueue 2 request → `BeginDrain` → dispatch dừng → serve 1 đang chạy → hoàn tất → in-flight về 0 → snapshot ghi đúng 1 item còn lại trong queue.

**Build/test gate:** `dotnet build router-balancing.slnx` + `dotnet test "router balancing test/router balancing test.csproj"` phải xanh.

## 13. Phía ngoài phạm vi

- Auto-update cho Android/iOS/MacCatalyst.
- Tự động đóng gói release zip bằng CI (để bước sau).
- Kho lưu response để client retrieve lại (đã trao đổi, không làm).
- Đổi version scheme sang SemVer 2.0 với pre-release/build metadata.
