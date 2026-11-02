# Spec: Thông báo lỗi ra desktop + tên client trong chi tiết request (+ 3 sửa nhanh)

- **Ngày:** 2026-11-02
- **Trạng thái:** Draft (chờ duyệt)
- **Nguồn gốc:** yêu cầu trực tiếp của user trong phiên 2026-11-02
- **Baseline:** master `d072c8a` (v1.8.4)

## 1. Vấn đề

App chạy nền rất nhiều (mặc định `closeToTray=true`), nhưng mọi tín hiệu lỗi chỉ nằm trong nhật ký:

- `ILogService` ghi entry `Severity=Error` (request fail, proxy/sidecar/update lỗi) → chỉ xem được khi user **tự mở** Log panel.
- `ToastService` (`router-balancing/Services/ToastService.cs`) chỉ render trong `MainLayout`, nên khi app thu về khay hoặc cửa sổ không focus thì toast **không tồn tại** với user. Hệ quả: provider chết hàng loạt, proxy không khởi động được… có thể âm thầm cả ngày.

Ba vấn đề phụ kèm theo trong cùng đợt:

- Chi tiết request (`RequestDetailModal`) không cho biết request đó của **client key nào** — journal chỉ lưu `ClientKeyId`, không hiển thị tên.
- `MonitorFormat.Compact` (`router-balancing/Components/Shared/MonitorFormat.cs:80`) dừng ở `M` → số token lớn hiển thị `1500.0M`.
- Cửa sổ thống kê "Hôm nay / 7 ngày / 30 ngày" tính theo **nửa đêm UTC** (`src/RouterBalancing.Core/Stats/StatsService.cs:101`), tile "Σ Token hôm nay" cũng theo ngày UTC (`src/RouterBalancing.Core/Engine/ApiMonitorStore.cs:42`) → ở máy UTC+7, biểu đồ "Hôm nay" bắt đầu lúc 07:00 giờ máy, nhìn như thiếu 7 tiếng dữ liệu.
- Strip đếm request trong API Monitor: dot `<i class="monitor-dot …">` (`router-balancing/Components/Shared/ApiMonitorPanel.razor:117`) là phần tử `inline` nên `width/height: 10px` bị bỏ qua; `border: 1.5px solid #9aa0aa` của `.trace-dot--white` (`RequestTrace.razor:132`) vẽ thành hai vạch dọc → user thấy dấu `|` trước số, không phải dot tròn.

## 2. Quyết định

### 2.1. Nguồn lỗi: đúng dòng nhật ký, nhưng nhật ký request không đi qua `ILogService`

`ErrorAlertAggregator` (mới, `src/RouterBalancing.Core/Notifications/`) nhận entry từ **hai** nguồn và chỉ lấy `Severity == LogSeverity.Error`:

1. `ILogService.LogAdded` — mọi lỗi nền/app-level (proxy, sidecar, update, DB, middleware).
2. `IRequestJournalWriter` — thêm `event Action<LogEntry>? EntryWritten`, `RequestJournalWriter.WriteTerminal` bắn **sau khi ghi DB thành công** (fail-open: subscriber nổ chỉ log, không làm request/`WriteTerminal` chết).

Lý do bắt buộc có nguồn 2: dòng journal của request (kể cả mọi request thất bại) được ghi bằng `db.LogEntries.Add(...)` trực tiếp trong `RequestJournalWriter.WriteTerminal` (`src/RouterBalancing.Core/Stats/RequestJournalWriter.cs:167`), **không** qua `LogService.Write` → `LogAdded` chưa bao giờ nổ cho request. Chỉ subscribe `LogAdded` sẽ bỏ lỡ đúng kịch bản motivating (provider chết hàng loạt khi app đang ở khay).

Đã cân nhắc và **bác** phương án chuyển `WriteTerminal` sang `ILogService.Write`: LogPanel subscribe `LogAdded` nên mỗi request (kể cả 2xx) sẽ được prepend live → mỗi request một lần re-render panel, cộng rủi ro double-persist/vòng lặp `NotifySubscribers`.

- `Warning` **không** thông báo (chỉ Error). Chủ ý: Warning dày đặc (retry, park, chờ slot) sẽ nhấn chìm lỗi thật.
- Aggregator chạy trong `RouterBalancing.Core`, **không** tham khảo Blazor/WinForms/MAUI. App layer cung cấp đúng một interface đọc trạng thái cửa sổ — `IDesktopState.IsInteractive` (`true` khi user đang thực sự nhìn vào app: cửa sổ hiện + đang focus); phần desktop còn lại (`ITrayService.Notify`, toast trong app) nằm ở app layer (§2.3).
- Fail-open như mọi subscriber khác của `LogAdded`: handler không được ném (đã có cơ chế cách ly ở `LogService.NotifySubscribers`, nhưng aggregator vẫn tự bọc try/catch vì nó chạy trên thread request). Không được block/chiếm thread request: chỉ enqueue rồi return.

### 2.2. Gộp lỗi: một cụm lỗi = đúng một thông báo

- Cửa sổ gộp **30 giây** (hằng số `AggregationWindowSec = 30`): entry Error đầu tiên mở cửa sổ; hết cửa sổ, cả cụm bắn **một** thông báo rồi cửa sổ mới bắt đầu khi có lỗi mới.
- Nội dung do **Core** dựng qua `LocalizationService` (đã có trong `RouterBalancing.Core`), để message format unit test được mà không cần app project; chỉ dùng dữ liệu đã scrub của nhật ký:
  - 1 lỗi trong cụm: message của entry đó (`LogEntry.Message`).
  - N>1 lỗi: `"{count} lỗi trong {seconds} giây · gần nhất: {lastMessage}"`.
  - `LogEntry.Details` (stack trace) **không** đưa vào thông báo; chỉ message. Message đã được các call site viết sẵn không chứa secret/prompt — aggregator vẫn cắt cứng 200 ký tự để phòng message dài từ third-party.
- Aggregator phơi `event Action<ErrorAlert>? AlertRaised` (singleton). App layer không được tự subscribe `ILogService` để dựng thông báo — một nguồn sự kiện duy nhất giúp quyết định kênh chỉ xảy ra **một lần** (tránh race 2 subscriber cùng quyết định).
- Việc "chọn kênh nào" là logic thuần, đặt ở Core: `NotificationChannelPolicy.Resolve(isInteractive, notificationsEnabled)` → `InAppToast | DesktopNotification | None`, có unit test. App layer chỉ có nhiệm vụ thực thi kênh đã chọn.
- Lọc nhiễu trước khi vào cụm: xem §2.2.1.
- Không debounce kéo dài vô hạn: cụm 30 giây đứng sau cụm 30 giây khác là hợp lệ (user xác nhận muốn vậy).
- Aggregator giữ `TimeProvider` để test được cửa sổ 30s không cần chờ thật; **cơ chế đánh thức** (loop `PeriodicTimer` như `UpdateCheckService`, hay flush_when-flush) để builder chọn, miễn: (a) test chứng minh được "10 lỗi trong 30s → đúng 1 thông báo, 11 lỗi vắt qua 2 cửa sổ → 2 thông báo", (b) không có busy-wait.

#### 2.2.1. Lọc nhiễu: lỗi nào đáng làm phiền user

`ProxyApp.ClassifyOutcome` (`src/RouterBalancing.Core/Server/ProxyApp.cs:620`) map **mọi** `Passthrough` có status ≥ 400 thành `Success=false` → dòng journal `Severity=Error`. Nghĩa là request fail do chính phía client (body hỏng, payload không parse được, model không có trong combo) cũng là Error. Nếu báo hết: một client đang gửi request hỏng liên tục sẽ tạo một notification mỗi 30 giây, vô hạn.

Quy tắc lọc (trước khi entry vào cụm 30s):

| Entry | Thông báo? |
|---|---|
| `Category=App`, `Severity=Error` (proxy/sidecar/update/DB/middleware) | Có, không cần ngưỡng |
| `Category=Request`, `Severity=Error`, `ProviderKey != null` (đã dispatch tới tài khoản provider) | Có |
| `Category=Request`, `Severity=Error`, `ProviderKey == null` (parse lỗi, resolve không ra model, auth inbound fail — request chưa từng tới upstream) | Không, chỉ ghi log |
| `Severity=Warning/Info/Debug` | Không |

`ProviderKey` đến từ `DispatchStarted`/`Attempt` trong trace (`RequestJournalWriter.BuildEntry`), nên `null` = request chết trước khi tới upstream = lỗi do caller hoặc cấu hình, user vẫn thấy đầy đủ trong Log panel. **User chốt 2026-11-02:** notification chỉ cho lỗi do app/phía upstream gây ra; lỗi do client gây ra chỉ ghi log.

### 2.3. Chọn kênh: toast trong app hay notification của Windows

`ErrorNotifier` (mới, `router-balancing/Services/`, **singleton**) subscribe `ErrorAlertAggregator.AlertRaised`, gọi `NotificationChannelPolicy.Resolve(...)` (Core, §2.2) rồi thực thi kênh:

| Trạng thái app | Kênh | Cách thực thi |
|---|---|---|
| Cửa sổ hiện + đang focus | Toast trong app | `ErrorNotifier` bắn `event Action<ErrorAlert>? InAppAlertRequested`; `MainLayout` (nơi đang có `ToastService` scoped) subscribe và gọi `Toast.Show(msg, ToastSeverity.Error)` |
| Thu về khay (`HideMainWindow`) hoặc cửa sổ mở mà không focus | Notification của Windows | `ITrayService.Notify(title, text)` → `NotifyIcon.ShowBalloonTip` |
| `notifyErrors=false` | Không gì cả | return sớm |

- **Không** inject `ToastService` vào service singleton: `ToastService` là **scoped** (`router-balancing/MauiProgram.cs:86`, cùng lý do với `ThemeService`), singleton resolve từ root provider sẽ lấy sai instance (hoặc fail scope validation). Pattern của repo là service singleton bắn event, `MainLayout` trong Blazor scope tự gọi `Toast.Show` (xem `MainLayout.razor:102-119` của update-check).
- Kênh `InAppToast` mà `InAppAlertRequested` **không có subscriber** (circuit Blazor chưa render lần đầu, hoặc circuit đã dispose khi cửa sổ vẫn focus) thì `ErrorNotifier` rơi về kênh desktop qua đúng đường `Dispatcher.Dispatch` sẵn có — quyết định chốt để không alert nào bị rơi im lặng, kể cả kịch bản app hỏng ngay từ khởi động.
- `IDesktopState` do `router-balancing/App.xaml.cs` cài đặt, gồm 2 cờ app tự đặt (không P/Invoke `GetForegroundWindow`):
  - `_windowVisible`: `false` từ `HideMainWindow` (đường `closeToTray`), `true` từ `ShowMainWindow`.
  - `_windowActive`: WinUI `Microsoft.UI.Xaml.Window.Activated` / `Deactivated`, gắn trong cùng chỗ `AttachCloseToTray` đang lấy handler WinUI (`App.xaml.cs:222`) — nền tảng khác không có tray thật nên không cần cài.
- Kênh desktop dùng `NotifyIcon.ShowBalloonTip` trên `TrayService` sẵn có (`router-balancing/Platforms/Windows/TrayService.cs`), **không** dùng `ToastNotificationManager` của WinUI: app chạy unpackaged nên toast WinUI phải đăng ký AUMID + shortcut Start Menu mới hiện được, còn balloon tip của NotifyIcon hiện vào Action Center trên Win10/11 mà không thêm dependency nào.
- `ShowBalloonTip` phải gọi trên UI thread (thread tạo `NotifyIcon`) — `ErrorNotifier` bọc trong `Dispatcher.Dispatch` vì `AlertRaised` tới từ worker/flush loop.
- Mở rộng `ITrayService` (`src/RouterBalancing.Core/Platform/ITrayService.cs`):
  - `void Notify(string title, string message)` — WinForms: `ShowBalloonTip(6000, title, text, ToolTipIcon.Error)`; `NullTrayService` no-op (Android/iOS/mac không có tray thật).
  - `event Action? NotificationActivated` — từ `NotifyIcon.BalloonTipClicked` (chỉ bắn khi user bấm balloon tip **đang còn hiển thị** — event tới từ `NIN_BALLOONUSERCLICK`; mục đã vào Action Center không bắn event). Hệ quả: affordance "bấm thông báo để mở app" chỉ tồn tại trong ~6 giây balloon còn trên màn hình — chấp nhận, vì Log panel vẫn mở được từ menu khay.
- Text balloon tip dài bị Windows cắt; message đã cap 200 ký tự ở §2.2 nên nằm trong ngưỡng an toàn.

### 2.4. Bấm notification → Log panel đã lọc lỗi

- `NotificationActivated` → (1) `ShowMainWindow()` như đường `OpenRequested` hiện có, (2) điều hướng tới `/logs?severity=Error`.
- `NavigationManager` **không** inject được vào service singleton trong MAUI Blazor Hybrid (app chưa từng dùng `NavigationManager` ở đâu). Pattern: singleton `AppNavigation` (mới, `router-balancing/Services/`) có `void Capture(NavigationManager nav)` được `MainLayout.OnInitialized` gọi (circuit render lần đầu là capture xong), và `NavigateToLogs()` gọi trong `Dispatcher.Dispatch`.
- `NotificationActivated` bắn **trước khi** circuit render lần đầu (rất hiếm: app chưa từng mở cửa sổ mà đã có notification được bấm) → chỉ `ShowMainWindow()`, bỏ qua navigation. Chấp nhận mất navigation một lần, không queue, không throw.
- `LogPanel` (`router-balancing/Components/Pages/LogPanel.razor`) thêm `[SupplyParameterFromQuery]` cho `severity` và `category`, nạp vào `_severity`/`_category` **trước** `Load()`; nếu page đã mở sẵn thì `OnParametersSet` re-apply filter và `Load()` lại. Query string sai (vd `severity=foo`) → coi như không có filter (đúng luật `Enum.TryParse` hiện có ở `MinSeverity`).
- Hệ quả đã biết: LogPanel load dòng request từ DB, không prepend live, nên dòng request lỗi vừa xảy ra đã có trong bảng (page vừa điều hướng là query mới).

### 2.5. Setting bật/tắt

- Key mới `notifyErrors` (SettingsKeys → `NotifyErrors`), `IAppSettingsService.NotifyErrors => Get(SettingsKeys.NotifyErrors, true)` — mặc định **bật**.
- Vào `SettingsDraft` + `SettingsValidator` (bool, không cần validate) + checkbox trong section General của `SettingsPanel.razor`, cạnh "Close → tray".
- Tắt setting = aggregator vẫn chạy nhưng router không bắn kênh nào (không unsubscribe để khỏi phải reconnect khi bật lại).

### 2.6. Tên client trong popup chi tiết request

- `ApiCallRecord` (`src/RouterBalancing.Core/Engine/IApiMonitorStore.cs:44`) thêm `long? ClientKeyId`. `ApiMonitorStore.OnPublished` lấy từ `TraceEvent.ClientKeyId` (đã có, chỉ mang ở event `Received` đầu) và carry qua các lần update trạng thái — cùng luật "không đè giá trị đã có" mà `RequestJournalWriter.MergeClientKeyId` đang dùng.
- `IClientKeyService` thêm `Task<string?> GetNameAsync(long id, CancellationToken ct = default)` (SQLite local, gọi 1 lần khi mở modal — không cần cache). Trả `null` khi key đã bị xóa.
- `RequestDetailModal`: strip header thêm dòng "client" với giá trị `ClientKey.Name`; `ClientKeyId is null` (app chạy không bật API key, hoặc request trước khi có auth) → **không render dòng nào** (controller ruling 2026-11-02, thay cho `—`: không có client key là trường hợp đơn người dùng phổ biến, một field luôn rỗng chỉ tổ nhiễu); key đã xóa → `#{id}`.
- **Không** thêm cột Client vào bảng API Monitor trong đợt này (bảng 5 cột đã kín, cần quyết định layout riêng).

### 2.7. Ba sửa nhanh

1. **Hậu tố tỷ cho số token.** Helper compact hiện là `MonitorFormat.Compact` (`internal static`, nằm trong app project trong khi test project chỉ tham chiếu `RouterBalancing.Core` — `router balancing test/router balancing test.csproj:57`), nên phần dựng chuỗi số phải **tách sang Core**: `NumberFormat.Compact(long)` ở `src/RouterBalancing.Core/Display/NumberFormat.cs`, test được; `MonitorFormat.Compact` giữ nguyên hành vi "thiếu dữ liệu → `—`" và delegate phần số cho helper Core (các call site trong Razor không đổi).
   - Thêm nhánh `≥ 1e9 → "1.2B"`; **giữ nguyên** bộ hậu tố hiện tại `k` (chữ thường) / `M` / thêm `B`, giữ 1 chữ số thập phân và `CultureInfo.CurrentCulture` như nhánh M.
   - Builder soát `StatsPanel.razor` / `StatsChart.razor` / `Dashboard.razor`: chỗ nào tự format số lớn thì chuyển qua helper.
2. **Cửa sổ thống kê theo giờ máy** — không dùng "offset của `now`": `TimeProvider.System` (`MauiProgram.cs:98`) trả `DateTimeOffset` có offset = 0, nên mọi code chỉ dựa offset của `now` sẽ vẫn là UTC ngoài production.
   - Đăng ký `builder.Services.AddSingleton(TimeZoneInfo.Local)`; `StatsService` và `ApiMonitorStore` nhận thêm `TimeZoneInfo` (ctor, mặc định `TimeZoneInfo.Utc` cho test cũ không quan tâm múi giờ).
   - `StatsService.ResolveWindow`: mốc "nửa đêm" và bucket giờ/dịch tính bằng `TimeZoneInfo.ConvertTimeFromUtc` trên múi được inject → "Hôm nay" = [00:00 giờ máy, now].
   - `ApiMonitorStore.RollToday`/`_today`: ngày theo múi inject, không phải `time.GetUtcNow().Date`.
   - Label trục giờ trong `StatsChart` in theo đúng offset của `StatsBucket.StartsAt` (không force về UTC).
   - Múi có DST: `Today` vẫn đúng 24 bucket × 1 giờ tính từ nửa đêm local → ngày 25 giờ có bucket cuối nằm ngoài cửa sổ, ngày 23 giờ có bucket rỗng. Chấp nhận, không viết logic DST (VN không có DST).
   - **Ngoại lệ có chủ ý:** `UsageDailyService.TodayUtc()` vẫn là ngày UTC. `UsageDaily.Date` là PK của ledger đã ghi; đổi sang ngày local phải migration lại dữ liệu cũ và làm lệch lịch sử đã hiển thị. Hệ quả đã chấp nhận: tile "Token hôm nay" và bảng "Hôm nay" trong Tools/Usage có thể lệch nhau ~7 tiếng ở múi UTC+7. Nếu user muốn thống nhất, đó là việc riêng, không nằm ở đây.
3. **Dấu `|` trước số đếm trong strip API Monitor** (user báo 2 lần, `|2 chờ` và `|0 chờ` — cùng **một** element, `<i class="monitor-dot trace-dot--white">` ở `ApiMonitorPanel.razor:117`, không phụ thuộc giá trị đếm). Nguyên nhân: `.monitor-dot` chỉ có `width/height/border-radius` (`ApiMonitorPanel.razor:87`) trong khi `<i>` là `display:inline` → 2 thuộc tính size bị bỏ qua, còn `border: 1.5px solid #9aa0aa` của `.trace-dot--white` (`RequestTrace.razor:132`) vẫn vẽ → hai vạch dọc cao bằng line-height, nhìn thành dấu `|`. Chỉ dot **trắng** (Queued) lộ lỗi; dot xanh/đỏ/xanh lá/vàng chỉ có `background` nên inline thì vô hình hoàn toàn.
   - Sửa: `.monitor-dot { display: inline-block; box-sizing: border-box; }` (thêm `flex: none` để không bị flex strip co lại). Legend của Live Trace đã đúng sẵn (`.trace-ldot` có `display:inline-block` + `box-sizing:border-box`, `RequestTrace.razor:214`) nên không đụng tới.
   - Rà `.detail-dot` trong `RequestDetailModal.razor:92` (dot cùng họ) và sửa tương tự nếu cũng thiếu.
   - Strip vẫn hiển thị cả 5 mục kể cả khi đếm 0 (`● 0 chờ`), giữ đúng layout hiện tại.

## 3. Mục tiêu / ngoài mục tiêu / phản mục tiêu

**Mục tiêu**
- Không còn lỗi nào trong nhật ký mà user không hề được báo khi app đang chạy nền.
- Một đợt lỗi dày đặc chỉ tạo **một** notification.
- Tên client key xuất hiện trong popup chi tiết request, không cần mở Settings.

**Ngoài mục tiêu**
- Không thêm phân loại lỗi mới, không đổi cách Log panel hoạt động (trừ filter từ query).
- Không làm notification cho Android/iOS/macOS (`NullTrayService` no-op).
- Không thêm cột Client vào bảng API Monitor.
- Không migration DB, không đổi schema.
- Không cấu hình tiếng chuông/độ dài hiện/Action Center theo category.

**Phản mục tiêu** (làm vậy là thất bại dù tính năng "chạy")
- Spam: mỗi request lỗi một notification, hoặc notification lặp liên tục mỗi vài giây.
- Notification làm chậm hay làm hỏng đường request — đường `LogAdded` phải luôn non-blocking.
- Lộ nội dung request/API key trong text notification (chỉ dùng `Message`, cap 200 ký tự, không `Details`).
- User tắt app vì notification quá ồn → setting phải có và mặc định dễ thấy.

## 4. Rủi ro & điểm cần chú ý

- `ILogService.LogAdded` và `EntryWritten` bắn trên thread request/worker: state của aggregator phải guard bằng lock (hoặc `ConcurrentQueue` + flush đơn), và mọi thao tác desktop/UI phải về `Dispatcher`.
- Không được inject `ToastService` (scoped) hay `NavigationManager` vào service singleton — cả hai đều là bẫy DI của MAUI Blazor Hybrid; dùng event + `Capture()` như §2.3/§2.4.
- `BalloonTipClicked` chỉ hoạt động khi `NotifyIcon` còn sống; `TrayService.Dispose` khi thoát app nên notification không tới được nữa là đúng.
- Windows 11 có thể gộp balloon tips vào Action Center theo app (hiện tên lấy từ `NotifyIcon.Text` = `app.title`) — hành vi này chấp nhận.
- Test trên CI: aggregator dùng `TimeProvider` giả, không `Thread.Sleep`.
- `TranslationParityTests` bắt buộc key mới có đủ EN + VI.

## 5. Test plan

Toàn bộ unit test nằm trong `router balancing test/`, chỉ test code thuộc `RouterBalancing.Core` (test project không reference app project) — đó là lý do aggregator, message format, channel policy và `NumberFormat` đều ở Core (§2.1, §2.2, §2.7).

- `ErrorAlertAggregatorTests`
  - Lọc theo bảng §2.2.1: `Category=App` + Error → vào cụm; `Category=Request` + Error + `ProviderKey != null` → vào cụm; `Category=Request` + Error + `ProviderKey == null` → **không** vào cụm; Warning/Info → không vào cụm.
  - 10 entry Error trong 30s → đúng **1** alert, `Count = 10`, message lấy từ lỗi cuối.
  - 11 entry vắt qua 2 cửa sổ 30s → đúng 2 alert.
  - Subscriber của `AlertRaised` ném exception → không lan tới caller (`ILogService`/`WriteTerminal`).
  - Không có lỗi → không có alert, không có busy-loop (`TimeProvider` giả, không `Thread.Sleep`).
- `ErrorAlertMessageTests` (qua `LocalizationService` với EN và VI): 1 lỗi → message của lỗi đó; N>1 → `"{count} lỗi trong {seconds} giây · gần nhất: …"`; message > 200 ký tự → bị cắt, không chứa `Details`.
- `NotificationChannelPolicyTests`: interactive + bật setting → `InAppToast`; hidden hoặc không focus → `DesktopNotification`; `NotifyErrors=false` → `None` (kể cả khi interactive).
- `NumberFormatTests`: `999` → `999`; `1_500` → `1,5k`; `1_500_000` → `1,5M`; `1_200_000_000` → `1,2B`; `15_000_000_000` → `15,0B`; `999_999` → `999,9k` (giữ luật clamp hiện có). Helper nhận `IFormatProvider` (mặc định `CultureInfo.CurrentCulture`) để test pin `InvariantCulture`, không phụ thuộc culture của máy CI.
- `StatsServiceTests` với `TimeZoneInfo.CreateFixedTimeZone(id, +7h, …)`: `StatsRange.Today` bắt đầu tại nửa đêm của múi đó, bucket đầu = [00:00 +07, 01:00 +07); `Last7Days`/`Last30Days` cũng theo mốc nửa đêm múi đó. Test cũ giữ nguyên với `TimeZoneInfo.Utc`.
- `ApiMonitorStoreTests`: tile "Token hôm nay" roll tại nửa đêm **của múi inject** (entry 23:30 +07 và 00:30 +07 không chung ngày); `Received` event có `ClientKeyId` → record vẫn giữ `ClientKeyId` sau update Finished; không có → null.
- `RequestJournalWriterTests`: `EntryWritten` bắn đúng một lần cho mỗi dòng terminal, sau khi DB commit; lỗi DB → không bắn event.
- `ClientKeyServiceTests`: `GetNameAsync` id tồn tại → `Name`; id đã xóa → `null`.
- Settings: `AppSettingsService.NotifyErrors` mặc định `true`; round-trip qua `SettingsDraft`/`SettingsValidator`.

Không có test tự động cho balloon tip thật và cho decision focus của WinUI (cần desktop) → xác minh thủ công trên Windows: thu app về khay, bắn ~10 request lỗi thật, kiểm tra chỉ có **1** notification, bấm notification thấy Log panel mở ra đã lọc Error; mở cửa sổ + focus rồi gây lỗi → chỉ toast trong app, không có notification.

## 6. Điểm chạm dự kiến

| File | Thay đổi |
|---|---|
| `src/RouterBalancing.Core/Notifications/ErrorAlertAggregator.cs` (mới) | 2 nguồn lỗi, lọc theo §2.2.1, cụm 30s, `AlertRaised` |
| `src/RouterBalancing.Core/Notifications/ErrorAlert.cs` (mới) | record alert (`Count`, `Message`, `FirstAt`) |
| `src/RouterBalancing.Core/Notifications/ErrorAlertMessage.cs` (mới) | dựng text theo i18n + cap 200 ký tự |
| `src/RouterBalancing.Core/Notifications/NotificationChannelPolicy.cs` (mới) | `Resolve(isInteractive, enabled)` → `InAppToast / DesktopNotification / None` |
| `src/RouterBalancing.Core/Platform/IDesktopState.cs` (mới) | `bool IsInteractive` (visible + active) |
| `src/RouterBalancing.Core/Platform/ITrayService.cs` | `Notify(title, message)`, `event NotificationActivated` |
| `src/RouterBalancing.Core/Stats/IRequestJournalWriter.cs` + `RequestJournalWriter.cs` | `event EntryWritten` sau khi commit |
| `src/RouterBalancing.Core/Display/NumberFormat.cs` (mới) | `Compact` với nhánh `B`, nhận `IFormatProvider` |
| `src/RouterBalancing.Core/Settings/{SettingsKeys,IAppSettingsService,AppSettingsService,SettingsDraft,SettingsValidator}.cs` | `notifyErrors` mặc định `true` |
| `src/RouterBalancing.Core/Engine/IApiMonitorStore.cs` + `ApiMonitorStore.cs` | `long? ClientKeyId` trên `ApiCallRecord`, carry từ `Received`; ngày "hôm nay" theo `TimeZoneInfo` inject |
| `src/RouterBalancing.Core/Server/{IClientKeyService,ClientKeyService}.cs` | `GetNameAsync(long id, CancellationToken)` |
| `src/RouterBalancing.Core/Stats/StatsService.cs` | cửa sổ Today/7/30 theo `TimeZoneInfo` inject |
| `src/RouterBalancing.Core/Localization/Translations.cs` | key EN/VI: message notification, setting, dòng "Client" |
| `router-balancing/Services/ErrorNotifier.cs` (mới, singleton) | subscribe `AlertRaised`, thực thi kênh, `InAppAlertRequested`, `Dispatcher.Dispatch` |
| `router-balancing/Services/AppNavigation.cs` (mới, singleton) | `Capture(NavigationManager)` + `NavigateToLogs()` |
| `router-balancing/App.xaml.cs` | cài `IDesktopState` (`_windowVisible` + WinUI `Activated/Deactivated`), nối `NotificationActivated` → show + navigate |
| `router-balancing/Components/Layout/MainLayout.razor` | subscribe `InAppAlertRequested` → `Toast.Show(..., Error)`; `OnInitialized` gọi `AppNavigation.Capture(NavigationManager)` |
| `router-balancing/MauiProgram.cs` | đăng ký aggregator/notifier/AppNavigation + `TimeZoneInfo.Local` |
| `router-balancing/Platforms/Windows/TrayService.cs` | `ShowBalloonTip` + `BalloonTipClicked` |
| `router-balancing/Platforms/{Android,iOS,MacCatalyst}/**NullTrayService*` | no-op theo interface mới |
| `router-balancing/Components/Pages/SettingsPanel.razor` | checkbox General cho `notifyErrors` |
| `router-balancing/Components/Pages/LogPanel.razor` | `[SupplyParameterFromQuery]` severity/category + re-apply khi parameters đổi |
| `router-balancing/Components/Shared/RequestDetailModal.razor` | dòng "Client" trong header; soát dot |
| `router-balancing/Components/Shared/MonitorFormat.cs` | delegate phần số sang `NumberFormat` |
| `router-balancing/Components/Shared/ApiMonitorPanel.razor` | `display:inline-block` + `box-sizing:border-box` cho `.monitor-dot` (hết dấu `\|`) |
