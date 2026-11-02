# Error Notification + Client Name Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Báo lỗi ra desktop Windows khi app đang ở khay/không focus (gộp 30 giây = 1 thông báo), hiện tên client key trong popup chi tiết request, và sửa 3 lỗi hiển thị (hậu tố B, khung giờ thống kê theo giờ máy, dấu `|` trước số đếm).

**Architecture:** Phần logic (gom lỗi, dựng message, chọn kênh, format số, cửa sổ thống kê) nằm trong `RouterBalancing.Core` để unit test được — test project chỉ reference Core. App layer (`router-balancing/`) chỉ còn phần glue: WinForms `NotifyIcon.ShowBalloonTip`, `ToastService` scoped, `NavigationManager` capture, DI wiring.

**Tech Stack:** .NET 10, xUnit, EF Core + SQLite, .NET MAUI Blazor Hybrid, WinForms `NotifyIcon` (chỉ TFM windows), `TimeProvider`/`TimeZoneInfo` cho thời gian.

**Spec:** `docs/superpowers/specs/2026-11-02-error-notify-client-name-design.md`

**Execution route:** Subagent-driven (user chọn 2026-11-02). Mỗi task: 1 implementer subagent mới → 1 task reviewer (spec + quality) → fix round + scoped re-review nếu cần. Ledger recovery: `.superpowers/sdd/2026-11-02-error-notify-client-name/progress.md`. Không có inline execution.

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên; code mới không được có warning NULLABLE.
- XML doc (`///`) bắt buộc cho public type/method/property; comment `//` giải thích "tại sao", tiếng Việt theo AGENTS.md; không dùng `/* */` trong C#.
- Không thêm package NuGet mới.
- Không migration DB, không đổi schema.
- Key i18n phải có đủ **cả EN và VI** trong `src/RouterBalancing.Core/Localization/Translations.cs` (`TranslationParityTests` sẽfail nếu thiếu).
- Text thông báo chỉ dùng `LogEntry.Message`, cắt 200 ký tự, **không** chứa `LogEntry.Details`/stack trace/API key.
- Tên test mô tả hành vi: `Method_WhenCondition_ExpectedResult`.
- Build app: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`; build solution: `dotnet build router-balancing.slnx`; test: `dotnet test "router balancing test/router balancing test.csproj"`.
- Commit message tiếng Anh, conventional commit (`feat:` / `fix:` / `test:` / `docs:`).

## Review Focus

- Request fail **chưa từng tới upstream** (`Category=Request`, `ProviderKey == null` — parse lỗi, model không có trong combo, auth inbound fail) → phải **không** có thông báo, chỉ có dòng log (đây là nhóm input dễ spam notification nhất). → Task 7.
- Chuỗi lỗi vắt qua ranh giới cửa sổ 30 giây → đúng **một** alert cho mỗi cửa sổ, không phải mỗi request một alert. → Task 7.
- Entry lỗi được phát từ thread request/worker, và subscriber của `AlertRaised` ném exception → request đang chạy không bị ảnh hưởng, không nuốt crash. → Task 7.
- Bấm notification khi Blazor circuit chưa render lần đầu (app chưa từng mở cửa sổ) → chỉ mở cửa sổ, không crash, không queue navigation. → Task 9.
- Máy chạy culture VI và múi giờ +07: số compact phải ra `1,2B` (không phải `1.2B` cứng), và bucket "Hôm nay" đầu tiên phải là 00:00 +07 chứ không phải 07:00 giờ máy. → Task 1, Task 3.

---

### Task 1: Hậu tố `B` cho số token (`NumberFormat.Compact` trong Core)

**Files:**
- Create: `src/RouterBalancing.Core/Display/NumberFormat.cs`
- Test: `router balancing test/Display/NumberFormatTests.cs`
- Modify: `router-balancing/Components/Shared/MonitorFormat.cs:80-93`

**Interfaces:**
- Produces: `public static string NumberFormat.Compact(long value, IFormatProvider? provider = null)` — `provider` mặc định `CultureInfo.CurrentCulture`.

- [x] **Step 1: Viết test fail** trong `router balancing test/Display/NumberFormatTests.cs` (namespace `router_balancing_test.Display`, `CultureInfo.InvariantCulture` được pin để không phụ thuộc máy CI):
  - `Compact_Below1000_NoSuffix` → `999` → `"999"`
  - `Compact_Thousands_kSuffix` → `1_500` → `"1.5k"`
  - `Compact_JustBelowMillion_ClampsTo999.9k` → `999_999` → `"999.9k"` (giữ luật clamp hiện có ở `MonitorFormat.cs:91`)
  - `Compact_Millions_MSuffix` → `1_500_000` → `"1.5M"`
  - `Compact_Billions_BSuffix` → `1_200_000_000` → `"1.2B"`
  - `Compact_TensOfBillions_BSuffix` → `15_000_000_000` → `"15.0B"`
  - `Compact_UsesProvidedFormatProvider` → `1_500` với `CultureInfo.GetCultureInfo("vi-VN")` → `"1,5k"`
- [x] **Step 2: Chạy test, xác nhận fail** — `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~NumberFormatTests` → fail vì `NumberFormat` chưa tồn tại (CS0103/CS0246).
- [x] **Step 3: Cài `NumberFormat.Compact`** trong `src/RouterBalancing.Core/Display/NumberFormat.cs`: chuyển nguyên logic `MonitorFormat.Compact` sang, giữ thứ tự nhánh `M` trước rồi `k` (điều kiện `<= -1_000_000 or >= 1_000_000`), thêm nhánh `B` **trước** nhánh `M` (`value is <= -1_000_000_000 or >= 1_000_000_000`), mọi `CultureInfo.CurrentCulture` thay bằng `provider ?? CultureInfo.CurrentCulture`. Lý do thứ tự: `1_200_000_000` mà rơi vào nhánh M sẽ ra `1200.0M`.
- [x] **Step 4: Chạy test, xác nhận pass** (cùng lệnh Step 2).
- [x] **Step 5: `MonitorFormat.Compact` delegate** — giữ nguyên nhánh "thiếu dữ liệu → `—`" của các hàm khác trong `MonitorFormat`, còn `Compact(long)` chỉ còn `=> NumberFormat.Compact(value);` (giữ XML doc, ghi rõ helper nằm ở Core).
- [x] **Step 6: Build app + chạy toàn bộ test** — `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` rồi `dotnet test "router balancing test/router balancing test.csproj"` → 0 warning mới, toàn bộ test xanh.
- [x] **Step 7: Commit** — `git add src/RouterBalancing.Core/Display router-balancing/Components/Shared/MonitorFormat.cs "router balancing test/Display"` → `feat: add billions suffix to compact token formatting`

---

### Task 2: Hết dấu `|` trước số đếm trong strip API Monitor

**Files:**
- Modify: `router-balancing/Components/Shared/ApiMonitorPanel.razor:87-91`
- Kiểm tra (không sửa nếu đã đúng): `router-balancing/Components/Shared/RequestTrace.razor:214` (`.trace-ldot`), `router-balancing/Components/Shared/RequestDetailModal.razor:92` (dot trong strip chi tiết)

**Interfaces:** không có API mới — chỉ CSS.

- [x] **Step 1: Sửa CSS** — thêm `display: inline-block; box-sizing: border-box; flex: none;` vào rule `.monitor-dot` (rule hiện chỉ có `width/height/border-radius`). Comment giải thích tại sao: `<i>` mặc định `display:inline` nên `width/height` bị bỏ qua, `border: 1.5px solid` của `.trace-dot--white` (`RequestTrace.razor:132`) vẽ hai vạch dọc = dấu `|`.
- [x] **Step 2: Rà `.detail-dot`** trong `RequestDetailModal.razor` — nếu rule cũng chỉ có `width/height/border-radius` thì thêm `display: inline-block; box-sizing: border-box;` tương tự; nếu đã có sẵn thì không đổi.
- [x] **Step 3: Build app Windows** — `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` → xanh.
- [ ] **Step 4: Xác minh thủ công** — chạy app (`dotnet build` + F5 hoặc `dotnet run -f net10.0-windows10.0.19041.0 --project router-balancing/router-balancing.csproj`), mở Dashboard: 5 mục trong strip (`… chờ / … chạy / … xong / … lỗi / … huỷ`) đều có **chấm tròn** màu, không còn dấu `|` kể cả khi số đếm là 0.  (Chưa chạy được trong môi trường headless — user cần xác minh trên app thật.)
- [x] **Step 5: Commit** — `fix: render monitor status dots as circles instead of vertical bars`

---

### Task 3: Cửa sổ thống kê "Hôm nay" theo giờ máy

**Files:**
- Modify: `src/RouterBalancing.Core/Stats/StatsService.cs:21-27,101-106`
- Modify: `src/RouterBalancing.Core/Engine/ApiMonitorStore.cs:34-48,294-302`
- Modify: `router-balancing/MauiProgram.cs` (đăng ký `TimeZoneInfo`)
- Modify: `src/RouterBalancing.Core/Stats/StatsModels.cs:4` (doc `BucketStart` không còn "luôn UTC")
- Test: `router balancing test/Stats/StatsServiceTests.cs`, `router balancing test/Engine/ApiMonitorStoreTests.cs`

**Interfaces:**
- Produces: `StatsService(IDbContextFactory<RouterBalancingDbContext> db, TimeProvider clock, TimeZoneInfo? zone = null)`; `ApiMonitorStore(ITraceFeed feed, ILogService log, TimeProvider time, TimeZoneInfo? zone = null)`. Mặc định `TimeZoneInfo.Utc` — **tham số optional để toàn bộ call site hiện có (kể cả ~10 chỗ trong test, vd `ProxyHostTests.cs:48`) giữ nguyên và vẫn nghĩa như cũ.**

- [x] **Step 1: Viết test fail** trong `StatsServiceTests.cs` (dùng `TimeZoneInfo.CreateFixedTimeZone("UTC+07", TimeSpan.FromHours(7), "+07", "+07")` và `Now = 2026-10-11 15:30 +07` — tức `08:30 UTC`):
  - `GetRangeAsync_Today_WindowStartsAtMidnightOfInjectedZone` → `snapshot.From == 2026-10-11 00:00 +07` và `snapshot.Buckets[0].BucketStart.Offset == TimeSpan.FromHours(7)`.
  - `GetRangeAsync_Today_RequestAt01amZone_FallsInBucket1` → seed `LogEntry(Outcome=Ok, Timestamp=2026-10-11 01:30 +07)` → `snapshot.Buckets[1].Requests == 1`, `snapshot.Buckets[0].Requests == 0` (chứng minh request 08:30 UTC = 15:30 +07 không còn nằm ở bucket của 08:30 "giờ UTC").
  - `GetRangeAsync_Last7Days_WindowStartsAtMidnightOfZoneMinus6Days`.
- [x] **Step 2: Chạy test, xác nhận fail** — `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~StatsServiceTests` → 3 test mới fail (cửa sổ hiện tại là nửa đêm UTC).
- [x] **Step 3: Cài trong `StatsService`** — ctor thêm `TimeZoneInfo? zone = null` (field `_zone`), `ResolveWindow` đổi `var midnightUtc = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);` thành tính nửa đêm **của múi**: `var localDate = TimeZoneInfo.ConvertTimeFromUtc(now.UtcDateTime, _zone).Date;` rồi `from = new DateTimeOffset(localDate, _zone.GetUtcOffset(localDate))` cho `Today`, và `localDate.AddDays(-6)`/`(-29)` cho 7/30 ngày. Giữ nguyên `bucketCount`/`bucketStep` (24 × 1h, 7/30 × 1 ngày).
- [x] **Step 4: Chạy test, xác nhận pass** (cùng lệnh Step 2) — các test cũ trong file vẫn phải xanh (chúng dùng `TimeZoneInfo.Utc` mặc định).
- [x] **Step 5: Viết test fail cho `ApiMonitorStore`** trong `ApiMonitorStoreTests.cs`: dùng `ManualTimeProvider` (`router balancing test/Engine/ManualTimeProvider.cs`) + `TimeZoneInfo.CreateFixedTimeZone("UTC+07", +7h…)` — `TodayTokens_RollsAtMidnightOfInjectedZone`: ghi response tokens lúc `2026-10-11 23:30 +07`, rồi `SetUtcNow(2026-10-12 00:30 +07)` rồi ghi tokens nữa → đọc `TodayTokens` phải chỉ tính phần của ngày mới.
- [x] **Step 6: Chạy test, xác nhận fail** — `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~ApiMonitorStoreTests`.
- [x] **Step 7: Cài trong `ApiMonitorStore`** — ctor thêm `TimeZoneInfo? zone = null`; `RollToday()` và field `_today` dùng `DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(_time.GetUtcNow().UtcDateTime, _zone))`; cập nhật XML doc của tham số `time` (không còn "ngày UTC").
- [x] **Step 8: Chạy test ApiMonitorStore + StatsService, xác nhận pass.**
- [x] **Step 9: Nối DI** — trong `MauiProgram.cs`, thêm `builder.Services.AddSingleton(TimeZoneInfo.Local);` cạnh `AddSingleton(TimeProvider.System)` (dòng 98) — `StatsService`/`ApiMonitorStore` resolve qua ctor. Ghi comment: `UsageDailyService` **không** dùng múi này (ledger vẫn ngày UTC, spec §2.7.2).
- [x] **Step 10: Build + test toàn bộ + commit** — `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 && dotnet test "router balancing test/router balancing test.csproj"` → `fix: align stats day windows with machine time zone`

---

### Task 4: Event `EntryWritten` trên journal writer

**Files:**
- Modify: `src/RouterBalancing.Core/Stats/IRequestJournalWriter.cs`
- Modify: `src/RouterBalancing.Core/Stats/RequestJournalWriter.cs:158-183`
- Modify: `router balancing test/TestDoubles.cs:22-25` (`NullJournalWriter`)
- Test: `router balancing test/Stats/RequestJournalWriterTests.cs`

**Interfaces:**
- Produces: `event Action<LogEntry>? EntryWritten` trên `IRequestJournalWriter` — bắn **sau khi** dòng terminal commit thành công, đúng **một** lần cho mỗi request terminal.

- [x] **Step 1: Viết test fail** trong `RequestJournalWriterTests.cs`:
  - `WriteTerminal_FiresEntryWrittenOnceWithCommittedRow` — publish `Received` rồi `Finished(Success=false, Status=500)` qua `ITraceFeed` → đúng 1 event, `entry.Severity == LogSeverity.Error`, `entry.Outcome == RequestOutcome.Error`, và dòng đó **đã có** trong `db.LogEntries` (query lại DB).
  - `WriteTerminal_FailureToCommit_DoesNotFireEntryWritten` — DB đóng (dispose factory / connection đã đóng) → không có event, không ném ra caller.
  - `Received_Alone_DoesNotFireEntryWritten`.
- [x] **Step 2: Chạy test, xác nhận fail** — `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~RequestJournalWriterTests` (CS1061: `EntryWritten` chưa có).
- [x] **Step 3: Thêm event** vào `IRequestJournalWriter` (XML doc nêu: nguồn sự kiện cho thông báo lỗi desktop; request journal ghi thẳng `db.LogEntries.Add` nên `ILogService.LogAdded` **không** nổ cho request). `RequestJournalWriter` bắn event trong `WriteTerminal` ở nhánh `if (written)` — bọc mỗi subscriber trong try/catch + `SafeLog(nameof(WriteTerminal), ex)` theo đúng luật fail-open của repo. `NullJournalWriter` trong `TestDoubles.cs` thêm `public event Action<LogEntry>? EntryWritten { add { } remove { } }`.
- [x] **Step 4: Chạy test, xác nhận pass.**
- [x] **Step 5: Build + commit** — `feat: expose journal EntryWritten event for error alerts`

---

### Task 5: Setting `notifyErrors` (mặc định bật) + checkbox General

**Files:**
- Modify: `src/RouterBalancing.Core/Settings/SettingsKeys.cs:17-19`, `IAppSettingsService.cs:19-21`, `AppSettingsService.cs:50-52`, `SettingsDraft.cs:18-20`, `SettingsValidator.cs`
- Modify: `router-balancing/Components/Pages/SettingsPanel.razor:56,387,426`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (EN + VI)
- Test: `router balancing test/Settings/AppSettingsServiceTests.cs` (thêm vào file nếu đã có, nếu chưa có thì tạo)

**Interfaces:**
- Produces: `IAppSettingsService.NotifyErrors { get; }` (key `notifyErrors`, mặc định `true`); `SettingsDraft.NotifyErrors { get; set; } = true`.

- [x] **Step 1: Viết test fail** `NotifyErrors_DefaultsTrue` (factory DB rỗng → `settings.NotifyErrors == true`) và `NotifyErrors_RoundTripsThroughSet` (`Settings.Set(SettingsKeys.NotifyErrors, false)` → đọc lại `false`).
- [x] **Step 2: Chạy test, xác nhận fail** — filter `~AppSettingsServiceTests`.
- [x] **Step 3: Cài setting** — hằng `public const string NotifyErrors = "notifyErrors";`, property interface, `AppSettingsService`: `public bool NotifyErrors => Get(SettingsKeys.NotifyErrors, true);`, `SettingsDraft.NotifyErrors = true`. `SettingsValidator`: không cần rule cho bool — để nguyên (ghi chú nếu validator có danh sách key bắt buộc phải liệt kê).
- [x] **Step 4: Chạy test, xác nhận pass.**
- [x] **Step 5: Nút UI + i18n** — thêm checkbox vào section General của `SettingsPanel.razor` ngay dưới checkbox `CloseToTray` (pattern y hệt dòng 56), `_draft.NotifyErrors = Settings.NotifyErrors;` ở chỗ load (gần dòng 387) và `Settings.Set(SettingsKeys.NotifyErrors, _draft.NotifyErrors);` ở chỗ save (gần dòng 426). Thêm key vào **cả hai** dict trong `Translations.cs`: `settings.notifyErrors` = `"Notify errors when the app is in the background"` / `"Thông báo lỗi khi app đang chạy nền"`.
- [x] **Step 6: Build + test + commit** — `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 && dotnet test "router balancing test/router balancing test.csproj"` → `feat: add notifyErrors setting (default on)`

---

### Task 6: `ErrorAlert`, message dựng theo i18n, và policy chọn kênh (Core thuần)

**Files:**
- Create: `src/RouterBalancing.Core/Notifications/ErrorAlert.cs`
- Create: `src/RouterBalancing.Core/Notifications/ErrorAlertMessage.cs`
- Create: `src/RouterBalancing.Core/Notifications/NotificationChannelPolicy.cs`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (EN + VI)
- Test: `router balancing test/Notifications/ErrorAlertMessageTests.cs`, `router balancing test/Notifications/NotificationChannelPolicyTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ErrorAlert(int Count, string Title, string Message, DateTimeOffset FirstAt, DateTimeOffset LastAt);`
  - `public static class ErrorAlertMessage { public const int MaxMessageChars = 200; public static string Build(Func<string,string> t, int count, TimeSpan window, string lastMessage); public static string Truncate(string value); }` (`t` là translator — call site truyền `key => L[key]`; dùng `Func` thay vì inject `LocalizationService` để test không phải fake settings).
  - `public enum NotificationChannel { None, InAppToast, DesktopNotification }`
  - `public static class NotificationChannelPolicy { public static NotificationChannel Resolve(bool isInteractive, bool notificationsEnabled); }`

- [x] **Step 1: Viết test fail cho `ErrorAlertMessage`** — translator là dictionary EN giả:
  - `Build_SingleError_UsesErrorTextAsIs` → count=1, message `"Proxy start failed"` → `"Proxy start failed"` (không bọc cluster text).
  - `Build_ManyErrors_UsesClusterTemplate` → count=12, window=30s, last `"Request r1 failed: gpt → openai, HTTP 500, 120ms"` → `"{12} lỗi trong {30} giây · gần nhất: {…}"` đúng template VI.
  - `Build_TruncatesMessageOver200Chars` → message lỗi dài hơn `MaxMessageChars` → phần message trong kết quả bị cắt còn `MaxMessageChars` ký tự + marker `"…"`, và không chứa phần giữa của message dài đó.
  - `Truncate_Exactly200Chars_NoEllipsis` → `Truncate(new string('a', 200))` trả nguyên bản, `Truncate(new string('a', 201))` dài 201 ký tự và kết thúc bằng `"…"`.
- [x] **Step 2: Chạy test, xác nhận fail** — filter `~ErrorAlertMessageTests`.
- [x] **Step 3: Cài `ErrorAlert` + `ErrorAlertMessage`**, thêm key i18n vào **cả hai** dict: `notify.error.title` = `"router-balancing: error"` / `"router-balancing: lỗi"`, `notify.error.cluster` = `"{0} errors in {1} seconds · last: {2}"` / `"{0} lỗi trong {1} giây · gần nhất: {2}"`.
- [x] **Step 4: Chạy test, xác nhận pass.**
- [x] **Step 5: Viết test fail cho `NotificationChannelPolicy`** — `Resolve_InteractiveAndEnabled_ReturnsInAppToast`, `Resolve_NotInteractiveAndEnabled_ReturnsDesktopNotification`, `Resolve_Disabled_ReturnsNone` (kể cả interactive).
- [x] **Step 6: Chạy test, xác nhận fail** rồi cài policy (switch 2 dòng), chạy lại đến khi pass.
- [x] **Step 7: Build + test + commit** — `feat: add error alert model, i18n message and channel policy`

---

### Task 7: `ErrorAlertAggregator` — 2 nguồn lỗi, lọc nhiễu, cụm 30 giây

**Files:**
- Create: `src/RouterBalancing.Core/Notifications/ErrorAlertAggregator.cs`
- Test: `router balancing test/Notifications/ErrorAlertAggregatorTests.cs`

**Interfaces:**
- Consumes: `ILogService.LogAdded` (nguồn sẵn có), `IRequestJournalWriter.EntryWritten` (Task 4), `ErrorAlert`/`ErrorAlertMessage` (Task 6).
- Produces:
  - `public ErrorAlertAggregator(ILogService log, IRequestJournalWriter journal, TimeProvider clock, Func<string, string> translate)` — ctor subscribe cả hai nguồn, mỗi handler bọc try/catch; `translate` là translator i18n (app truyền `key => L[key]`, task 9 nối trong `MauiProgram`). **Không** inject setting ở đây — việc bật/tắt nằm ở `NotificationChannelPolicy` (Task 6/9), aggregator chỉ gom.
  - `public event Action<ErrorAlert>? AlertRaised;`
  - `public void Observe(LogEntry entry)` (public để test bơm entry; 2 subscription trong ctor chỉ gọi nó).
  - `public int Flush()` — đóng cửa sổ nếu đã quá 30s và có entry; trả số alert đã bắn.
  - `public void Start()` / `public void Dispose()` — loop `PeriodicTimer` 1 giây gọi `Flush()` (pattern như `UpdateCheckService.Start`).
  - `internal const int AggregationWindowSec = 30;`

- [x] **Step 1: Viết test fail** — helper test: `ManualTimeProvider` (`router balancing test/Engine/ManualTimeProvider.cs`), translator là `Dictionary<string,string>` EN giả (`{"notify.error.title","t"}, {"notify.error.cluster","{0}|{1}|{2}"}`):
  - `Observe_AppCategoryError_EntersCluster` → `Flush()` sau 30s → 1 alert, `Count == 1`.
  - `Observe_RequestErrorWithProviderKey_EntersCluster` → `Category=Request, Severity=Error, ProviderKey="openai"` → 1 alert.
  - `Observe_RequestErrorWithoutProviderKey_DoesNotAlert` → `ProviderKey = null` → `Flush()` trả 0, `AlertRaised` không bắn (Review Focus #1).
  - `Observe_WarningOrInfo_DoesNotAlert`.
  - `Flush_TenErrorsInWindow_RaisesExactlyOneAlert` → 10 entry (9 request + 1 app) trong 30s → đúng 1 alert, `Count == 10`, `Message` dựng từ lỗi cuối; `Flush()` lần nữa trả 0 (cửa sổ đã đóng) (Review Focus #2).
  - `Flush_WindowBoundary_ElevenErrors_RaisesTwoAlerts` → 11 entry với mốc thời gian vắt qua ranh giới 30s → 2 alert.
  - `AlertRaisedSubscriberThrows_DoesNotPropagateToCaller` → subscriber ném → `Observe`/`Flush` không ném.
- [x] **Step 2: Chạy test, xác nhận fail** — filter `~ErrorAlertAggregatorTests`.
- [x] **Step 3: Cài `ErrorAlertAggregator`** — giữ buffer `List<LogEntry>` + `_windowStart` guard bằng `lock (_gate)` (nguồn bắn từ thread request/worker); lọc theo bảng spec §2.2.1; `Flush()`: nếu `_entries.Count == 0` hoặc `clock.GetUtcNow() - _windowStart < TimeSpan.FromSeconds(AggregationWindowSec)` → trả 0; ngược lại dựng `ErrorAlert` qua `ErrorAlertMessage` (title `translate("notify.error.title")`, window `AggregationWindowSec`), bắn `AlertRaised` **ngoài** lock, mỗi subscriber bọc try/catch. `Start()` chỉ tạo loop `PeriodicTimer(TimeSpan.FromSeconds(1))` một lần; `Dispose()` cancel + dispose timer; lỗi trong loop thì `catch (Exception ex) { log.Error(...) }` — không `Thread.Sleep`.
- [x] **Step 4: Chạy test, xác nhận pass** (toàn bộ 8 test ở Step 1).
- [x] **Step 5: Build + test + commit** — `feat: aggregate error log entries into 30s alert clusters`

---

### Task 8: `IDesktopState` + `WindowState` + kênh tray (`ShowBalloonTip`)

**Files:**
- Create: `src/RouterBalancing.Core/Platform/IDesktopState.cs`
- Create: `router-balancing/Services/WindowState.cs`
- Modify: `src/RouterBalancing.Core/Platform/ITrayService.cs`
- Modify: `src/RouterBalancing.Core/Platform/NullTrayService.cs`
- Modify: `router-balancing/Platforms/Windows/TrayService.cs`
- Modify: `router-balancing/App.xaml.cs:30-48,86-88,139-154,222-248`
- Test: `router balancing test/Platform/WindowStatePolicyTests.cs` (chỉ test phần logic tính, nếu tách được)

**Interfaces:**
- Produces:
  - `public interface IDesktopState { bool IsInteractive { get; } }` (Core)
  - `public sealed class WindowState : IDesktopState` (app) với `void SetVisible(bool)`, `void SetFocused(bool)`; `IsInteractive => _visible && _focused`.
  - `ITrayService`: `void Notify(string title, string message)` + `event Action? NotificationActivated`.
- [x] **Step 1: Thêm interface/property** vào Core (`IDesktopState`, `ITrayService.Notify`, `event NotificationActivated`) và `NullTrayService` (no-op + event rỗng `{ add { } remove { } }`).
- [x] **Step 2: Viết `WindowState`** trong `router-balancing/Services/WindowState.cs` — 2 cờbool, `IsInteractive` là tích của chúng; mặc định `_visible = true, _focused = false` (chưa có window activation event thì coi như chưa interactive → không bắn toast sai lúc khởi động; notification desktop vẫn tới được, chấp nhận).
- [x] **Step 3: Cài `TrayService.Notify`** — `NotifyIcon.ShowBalloonTip(6000, title, message, ToolTipIcon.Error)`, guard `_icon is null → return`; subscribe `_icon.BalloonTipClicked += (_, _) => NotificationActivated?.Invoke();` trong `Initialize()`; comment lý do dùng balloon tip thay WinUI toast (app unpackaged → toast cần AUMID + Start Menu shortcut).
- [x] **Step 4: Nối cờ trong `App.xaml.cs`** — inject `WindowState` vào ctor `App`; `OnMainWindowClosing` gọi `_window.SetVisible(false)` ngay sau `appWindow.Hide()`; `ShowMainWindow` gọi `_window.SetVisible(true)`; trong `AttachCloseToTray` (đã có handler WinUI `native`), subscribe `native.Activated += (_, _) => _window.SetFocused(true);` và `native.Deactivated += (_, _) => _window.SetFocused(false);` — cùng chỗ gỡ subscription cũ khi handler bị tạo lại (giống pattern `_trayCloseAppWindow`).
- [x] **Step 5: Đăng ký DI** — `MauiProgram.cs`: `builder.Services.AddSingleton<WindowState>(); builder.Services.AddSingleton<IDesktopState>(sp => sp.GetRequiredService<WindowState>());`
- [ ] **Step 6: Build Windows + chạy app xác minh thủ công** — build TFM windows xanh; chạy app, bấm thu về khay, mở lại bằng double-click tray: không log lỗi subscription, badge trạng thái vẫn đúng.  (Chưa chạy được trong môi trường headless — user cần xác minh trên app thật.)
- [x] **Step 7: Commit** — `feat: expose window focus state and tray balloon notification channel`

---

### Task 9: Nối `ErrorNotifier`, navigation tới Log panel đã lọc lỗi

**Files:**
- Create: `router-balancing/Services/ErrorNotifier.cs`
- Create: `router-balancing/Services/AppNavigation.cs`
- Modify: `router-balancing/App.xaml.cs` (ctor + `NotificationActivated`)
- Modify: `router-balancing/Components/Layout/MainLayout.razor:55-76`
- Modify: `router-balancing/Components/Pages/LogPanel.razor:140-184`
- Modify: `router-balancing/MauiProgram.cs`
- Test: `router balancing test/Components/LogPanelQueryFilterTests.cs` (test bằng đọc source `.razor` như text — pattern đã có ở `router balancing test/Components/RazorParameterEntityTests.cs:22`)

**Interfaces:**
- Consumes: `ErrorAlertAggregator.AlertRaised` (Task 7), `IDesktopState` + `ITrayService.Notify/NotificationActivated` (Task 8), `ToastService` (scoped, sẵn có).
- Produces:
  - `public sealed class ErrorNotifier : IDisposable` (singleton, app) — ctor `(ErrorAlertAggregator alerts, IDesktopState desktop, ITrayService tray, IAppSettingsService settings, ILogService log)`; `event Action<ErrorAlert>? InAppAlertRequested;`.
  - `public sealed class AppNavigation` (singleton, app) — `void Capture(NavigationManager nav)`, `bool TryNavigateToErrorLog()` (điều hướng `/logs?severity=Error`).

- [x] **Step 1: Viết test fail cho `LogPanel`** (`LogPanelQueryFilterTests.cs`, đọc text file `router-balancing/Components/Pages/LogPanel.razor`): assert file chứa `[SupplyParameterFromQuery(Name = "severity")]`, `[SupplyParameterFromQuery(Name = "category")]`, và `@page "/logs"` vẫn còn.
- [x] **Step 2: Chạy test, xác nhận fail** — filter `~LogPanelQueryFilterTests`.
- [x] **Step 3: Sửa `LogPanel.razor`** — thêm 2 property `[Parameter, SupplyParameterFromQuery(Name = "severity")] public string? SeverityQuery { get; set; }` và `…CategoryQuery`; trong `OnInitialized()` gán `_severity = SeverityQuery ?? _severity;` `_category = CategoryQuery ?? _category;` **trước** `Load()`; thêm `OnParametersSet()` re-apply khi `SeverityQuery` khác `_severity` hiện tại thì gán + `Load()` (giá trị query không parse được → `MinSeverity`/`Category` đã tự trả null như code hiện có, không cần validate thêm).
- [x] **Step 4: Chạy test, xác nhận pass.**
- [x] **Step 5: Viết `AppNavigation`** — field `NavigationManager? _nav`; `Capture` ghi lại; `TryNavigateToErrorLog()` trả false khi `_nav is null`, ngược lại `_nav.NavigateTo("/logs?severity=Error")` rồi true. XML doc ghi rõ: capture từ `MainLayout.OnInitializedAsync` vì `NavigationManager` không inject được vào singleton trong MAUI Blazor Hybrid.
- [x] **Step 6: Viết `ErrorNotifier`** — subscribe `alerts.AlertRaised` trong ctor; handler: `var channel = NotificationChannelPolicy.Resolve(desktop.IsInteractive, settings.NotifyErrors);`
  - `InAppToast` → `InAppAlertRequested?.Invoke(alert)` (MainLayout gọi `Toast.Show`).
  - `DesktopNotification` → `Application.Current?.Dispatcher.Dispatch(() => tray.Notify(alert.Title, alert.Message))` (Dispatcher của MAUI, không đòi hỏi thread gọi là UI thread — `AlertRaised` tới từ loop `PeriodicTimer`); `Application.Current is null` → gọi thẳng `tray.Notify(...)` trong cùng `try/catch (Exception ex) { log.Error("Không gửi được thông báo lỗi ra desktop.", ex); }`.
  - `None` → return.
  - Unsubscribe trong `Dispose()`.
- [x] **Step 7: Nối `MainLayout.razor`** — inject `AppNavigation` + `ErrorNotifier`; trong `OnInitializedAsync` (dòng 55-76) gọi `Nav.Capture(NavigationManager);` rồi `Notifier.InAppAlertRequested += OnInAppAlert;` với handler `_ = InvokeAsync(() => { Toast.Show(a.Message, ToastSeverity.Error); StateHasChanged(); });`; unsubscribe ở `Dispose` hiện có (dòng 142).
- [x] **Step 8: Nối `App.xaml.cs`** — ctor `App` thêm tham số `AppNavigation nav, ErrorAlertAggregator alerts, ErrorNotifier notifier` (chỉ dùng `alerts` + `nav`; `notifier` resolve để ctor subscribe của nó được gắn từ sớm, ghi comment vì `notifier` không được dùng trực tiếp); thêm `_tray.NotificationActivated += OnNotificationActivated;` cạnh `_tray.OpenRequested += ShowMainWindow;` (dòng 86) với `OnNotificationActivated() { ShowMainWindow(); if (!_nav.TryNavigateToErrorLog()) _log.Warn("Notification được bấm trước khi UI sẵn sàng — chỉ mở cửa sổ."); }` (Review Focus #4); gọi `_alerts.Start();` cùng chỗ `_updateCheck.Start();` (dòng 84) và `_alerts.Dispose();` trong `StopProxyOnExit()`.
- [x] **Step 9: Đăng ký DI trong `MauiProgram.cs`** — `AddSingleton<ErrorAlertAggregator>(sp => new ErrorAlertAggregator(sp.GetRequiredService<ILogService>(), sp.GetRequiredService<IRequestJournalWriter>(), sp.GetRequiredService<TimeProvider>(), key => sp.GetRequiredService<LocalizationService>()[key]));`, `AddSingleton<ErrorNotifier>()`, `AddSingleton<AppNavigation>()`. Factory tường minh vì translator là `Func<string,string>` — DI mặc định không resolve được delegate.
- [x] **Step 10: Build + test toàn bộ** — `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 && dotnet test "router balancing test/router balancing test.csproj"` → xanh, không warning.
- [ ] **Step 11: Xác minh thủ công (bắt buộc, spec §5)** — chạy app:  (Chưa chạy được trong môi trường headless — user cần xác minh trên app thật.)
  - Gây ~10 request lỗi thật (provider key sai / model không tồn tại ở upstream đang chọn) trong <30s, app thu về khay → **đúng 1** balloon tip (Action Center có mục); **bấm ngay khi balloon còn hiển thị (~6s)** → cửa sổ mở + Log panel hiện ra đã lọc `Severity=Error`, filter bar mở. Bấm mục đã nằm trong Action Center thì không có navigation (giới hạn của `BalloonTipClicked` — spec §2.3); đường thay thế là bấm khay.
  - Lặp lại khi cửa sổ đang focus → chỉ toast đỏ trong app, **không** có notification.
  - Gây request lỗi do client (body sai JSON) → chỉ có dòng log, không notification.
  - Tắt setting "Thông báo lỗi khi app đang chạy nền" → không còn notification/toast lỗi.
  - Đổi language sang EN → text notification là bản EN.
- [x] **Step 12: Commit** — `feat: notify error clusters via toast or tray notification and deep-link to filtered log`

---

### Task 10: Tên client key trong popup chi tiết request

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/IApiMonitorStore.cs:44-50` (`ApiCallRecord`) và `ApiMonitorStore.cs:200-240` (`OnPublished`)
- Modify: `src/RouterBalancing.Core/Server/IClientKeyService.cs`, `src/RouterBalancing.Core/Server/ClientKeyService.cs`
- Modify: `router-balancing/Components/Shared/RequestDetailModal.razor:186-202` (+ inject service)
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (EN + VI)
- Test: `router balancing test/Engine/ApiMonitorStoreTests.cs`, `router balancing test/Server/ClientKeyServiceTests.cs`

**Interfaces:**
- Produces:
  - `ApiCallRecord(..., string? ErrorBody, long? ClientKeyId = null)` — **tham số positional mới đặt cuối và optional** để ~10 call site hiện có (kể cả trong test) không đổi.
  - `IClientKeyService.GetNameAsync(long id, CancellationToken ct = default)` → `Task<string?>` (`null` khi id không còn trong bảng `ClientKeys`).
- [x] **Step 1: Viết test fail cho `ApiMonitorStore`** trong `ApiMonitorStoreTests.cs`:
  - `Published_ReceivedWithClientKeyId_StoresOnRecord` → publish `TraceEvent(RequestId, Received, "gpt", …, ClientKeyId: 42)` → `store.Find(id)!.ClientKeyId == 42`.
  - `Published_ClientKeyIdSurvivesTerminalUpdate` → cùng request publish thêm `Finished(Success=false)` → record vẫn `ClientKeyId == 42` và `State == Error`.
  - `Published_WithoutClientKeyId_NullOnRecord` → publish `Received` không có `ClientKeyId` → `ClientKeyId is null`.
- [x] **Step 2: Chạy test, xác nhận fail** — `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~ApiMonitorStoreTests`.
- [x] **Step 3: Cài `ApiCallRecord.ClientKeyId`** (tham số cuối, optional) + trong `ApiMonitorStore.OnPublished` đổi nhánh `TraceStage.Received => current` thành `current with { ClientKeyId = e.ClientKeyId ?? current.ClientKeyId }` (luật "không đè giá trị đã có" như `RequestJournalWriter.MergeClientKeyId`); cập nhật XML doc `<param>` của record.
- [x] **Step 4: Chạy test, xác nhận pass.**
- [x] **Step 5: Viết test fail cho `GetNameAsync`** trong `ClientKeyServiceTests.cs`: `GetNameAsync_ExistingKey_ReturnsName` (seed key `Name="Claude Code"`, id trả về → `"Claude Code"`); `GetNameAsync_DeletedKey_ReturnsNull` (delete rồi gọi → `null`).
- [x] **Step 6: Chạy test, xác nhận fail** — filter `~ClientKeyServiceTests`, rồi cài `GetNameAsync` trong `ClientKeyService` (`AsNoTracking().Where(k => k.Id == id).Select(k => k.Name).FirstOrDefaultAsync(ct)`) + khai báo trên interface với XML doc, chạy lại tới khi pass.
- [x] **Step 7: Hiện trong `RequestDetailModal`** — inject `IClientKeyService`; thêm field `private string? _clientLabel;` và trong `OnParametersSetAsync` (thêm nếu component chưa có): khi `RequestId is { } rid && Store.Find(rid)?.ClientKeyId is { } id` thì `_clientLabel = await ClientKeys.GetNameAsync(id) ?? $"#{id}";` ngược lại `_clientLabel = null;`. Render trong `trace-detail-meta` (dòng 189-202): `@if (_clientLabel is not null) { <span>@L["trace.detail.client"] @_clientLabel</span> }` — request không có client key (app tắt API key) thì không render dòng nào.
- [x] **Step 8: Thêm key i18n** vào **cả hai** dict trong `Translations.cs` (gần `trace.detail.started`, dòng 51 và 595): `trace.detail.client` = `"client"` / `"client"`.
- [x] **Step 9: Build + test toàn bộ** — build TFM windows + `dotnet test "router balancing test/router balancing test.csproj"` → xanh, `TranslationParityTests` xanh.
- [ ] **Step 10: Xác minh thủ công** — thêm 2 client key trong Settings → gửi request bằng key đó → mở popup chi tiết request (click row trong API Monitor) → thấy `client Claude Code`; request bằng key vừa bị xóa → thấy `client #<id>`.  (Chưa chạy được trong môi trường headless — user cần xác minh trên app thật.)
- [x] **Step 11: Commit** — `feat: show client key name in request detail`
