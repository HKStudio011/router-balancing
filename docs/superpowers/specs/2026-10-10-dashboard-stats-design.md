# Spec: Thống kê Dashboard (Stats Panel)

- **Ngày:** 2026-10-10
- **Trạng thái:** Draft — chờ user review
- **Tiền đề:** Master spec `2026-09-25-router-balancing-design.md` §3 (stats aggregate trên SQLite, không rollup) + Phase 4 "Statistics (cards/chart/breakdown)"; Live Trace `2026-10-05` + API Monitor `2026-10-06` đã ship (roadmap #4b). Tính năng này là nửa còn lại của #4a (thống kê).

## 1. Bối cảnh & mục tiêu

Dashboard hiện có strip "50 request gần nhất" (ApiMonitorPanel) — vai trò **realtime phiên hiện tại**. Chưa có thống kê **lịch sử**: error rate (setting `StatsErrorRateThreshold` tồn tại nhưng chưa nơi nào dùng), latency p95, breakdown theo provider/model/client key, xu hướng theo thời gian.

Mục tiêu: panel **Thống kê** trên Dashboard trả lời 3 câu hỏi — sức khỏe hệ thống (error rate vs ngưỡng), lượng sử dụng (requests/tokens theo nơi), hiệu năng (latency avg/p95) — trên khoảng thời gian chọn được, dữ liệu từ SQLite (retention 90 ngày).

### Quyết định của user (binding)

1. **3 mục tiêu:** sức khỏe + lượng sử dụng + hiệu năng.
2. **Ranh giới:** strip ApiMonitorPanel giữ vai realtime; stats mới = **lịch sử tổng hợp từ SQLite** — không trùng nguồn số liệu.
3. **Khoảng thời gian:** preset **Hôm nay** (mặc định) / **7 ngày** / **30 ngày** — không date picker.
4. **Hướng A đầy đủ:** enrich journal + KPI cards + breakdown table + **chart SVG tự vẽ** (không thêm dependency JS).
5. **Layout A (mockup `9261010192125ops2/content/stats-layout.html`):** stacked full-width — cards → chart → bảng, đặt dưới grid Live Trace + API Monitor.

### Non-goals

- Pricing/cost USD theo token (master spec §12 — để sau).
- Bảng rollup; poll định kỳ; export CSV.
- Đổi layout Live Trace/API Monitor hiện tại; đếm realtime (strip đã làm).
- Breakdown theo Combo/Account (chỉ Provider/Model/Client key).

## 2. Kiến trúc tổng quan

```
ITraceFeed.Published ──► RequestJournalWriter (Core, singleton)
    (Received/…/Finished)       │ ghi 1 dòng terminal/request (fail-open)
                                ▼
                     LogEntries (Category=Request, Outcome NOT NULL)
                                │
              IStatsInvalidationSource.Invalidate() sau ghi thành công
                                │ Changed (debounce 2s UI)
                                ▼
                     IStatsService.GetRangeAsync(range)  ── aggregate SQLite
                                ▼
                     StatsPanel.razor (Dashboard, layout A)
```

- Journal writer **subscribe `ITraceFeed.Published`** (cùng pattern `ApiMonitorStore`) — không đụng Dispatcher/handler; các dòng journal cũ (LogForwarded, usage, edge-case) giữ nguyên, stats chỉ đọc dòng có `Outcome NOT NULL`.
- Tokens: dòng usage hiện có (`LogRequestUsage`) đã mang `RequestId + PromptTokens + CompletionTokens` — stats **join** theo `RequestId` (feed không mang token).

## 3. Data contracts

### 3.1 Extension `TraceEvent` — thêm `ClientKeyId`

```csharp
public sealed record TraceEvent(..., long? ClientKeyId = null);
```

- Optional param, default `null` — mọi site publish hiện có không đổi.
- Chỉ site **Received** trong `ProxyApp` (cả 2 endpoint) điền `ClientKeyItems.IdOf(ctx)`; các stage khác giữ `null`. Journal writer carry qua active map (xem §4).

### 3.2 `LogEntry` — migration `AddStatsJournalColumns`

| Cột mới | Loại | Ý nghĩa |
|---|---|---|
| `Outcome` | `int?` | `RequestOutcome` — **null = dòng cũ/không phải terminal** |
| `ProviderKey` | `text?` | `Route.Provider` (tên provider **tại thời điểm request**) |
| `ModelKey` | `text?` | alias model (`TraceEvent.Model`) |
| `HttpStatus` | `int?` | `TraceEvent.Status` |

- `DurationMs`, `ErrorCode`, `ClientKeyId`, `RequestId` — cột sẵn có, journal writer điền (`ErrorCode` nhận `"http"`/`"network"` suy ra từ `Status` — xem §4).
- Giữ nguyên `ProviderId`/`ModelId` (long, chưa dùng) — stats không đụng tới.
- **Không thêm index mới** — truy vấn filter qua prefix `(Category, Timestamp)` của index sẵn có; volume desktop (≤90 ngày) đủ nhanh.

```csharp
public enum RequestOutcome { Ok = 1, Error = 2, Cancelled = 3 }
```

### 3.3 `IStatsInvalidationSource`

```csharp
public interface IStatsInvalidationSource
{
    event Action Changed;
    void Invalidate();
}
```

### 3.4 `IStatsService` (Core.Stats)

```csharp
public enum StatsRange { Today, Last7Days, Last30Days }

public sealed record StatsBucket(DateTimeOffset BucketStart, int Requests, int Errors);

public sealed record StatsBreakdownRow(string Key, int Requests, int Errors, long Tokens);

public sealed record StatsSnapshot(
    DateTimeOffset From, DateTimeOffset To,
    int Requests, int Ok, int Errors, int Cancelled,
    double ErrorRatePercent,                 // Errors / (Ok+Errors) × 100; 0 khi mẫu rỗng
    int? AvgLatencyMs, int? P95LatencyMs,    // chỉ Ok; null khi không có mẫu
    long PromptTokens, long CompletionTokens,
    IReadOnlyList<StatsBucket> Buckets,
    IReadOnlyList<StatsBreakdownRow> ByProvider,   // sort Requests desc
    IReadOnlyList<StatsBreakdownRow> ByModel,
    IReadOnlyList<StatsBreakdownRow> ByClientKey); // Key = ClientKeyId.ToString() hoặc "—"

public interface IStatsService
{
    Task<StatsSnapshot> GetRangeAsync(StatsRange range, CancellationToken ct = default);
}
```

- Key `"—"` (một nhóm duy nhất) cho mọi giá trị NULL: `ClientKeyId NULL` (auth mở), `ProviderKey`/`ModelKey` NULL (request huỷ khi chưa attempt / resolve lỗi trước khi chọn route).
- Breakdown trả **toàn bộ** dòng sort desc; UI tự cap hiển thị (xem §7).

## 4. `RequestJournalWriter` (Core.Engine)

- Singleton, ctor nhận `ITraceFeed`, `IDbContextFactory<RouterBalancingDbContext>`, `ILogService`, `IStatsInvalidationSource`; subscribe `Published` (mirror `ApiMonitorStore`).
- **Active map** `Dictionary<string, Pending>` theo `RequestId`: `Received` → tạo entry `(StartedAt=e.At, Model, ClientKeyId)` **chỉ khi chưa có entry** (endpoint publish `Received` lần 2 với `HeadersSent=true` — spec early-headers G2 — không được đè `StartedAt`); `DispatchStarted`/`Attempt` → cập nhật `Combo/Provider/Account` (attempt mới nhất đè); terminal (`Finished`/`Canceled`) → ghi dòng rồi **xóa entry**.
- **Sweep an toàn:** trước mỗi lần xử lý event, xóa entry có `StartedAt` cũ hơn **30 phút** (giới hạn leak khi request không bao giờ tới terminal — ví dụ enqueue fail không publish terminal).
- **`Timestamp = e.At.ToUniversalTime()`** — ProxyApp publish bằng `DateTimeOffset.Now` (offset local); chuẩn hóa UTC để so sánh cửa sổ range an toàn trên SQLite.
- **Ghi dòng terminal** (1 dòng/request):

| Cột | Finished | Canceled |
|---|---|---|
| `Outcome` | `Ok` nếu `Success==true`, ngược lại `Error` | `Cancelled` |
| `Timestamp` | `e.At.ToUniversalTime()` | như vậy |
| `DurationMs` | `(int)(e.At − StartedAt).TotalMilliseconds` | như vậy |
| `ProviderKey`/`ModelKey` | `Route.Provider` / `Model` từ entry | như vậy |
| `HttpStatus` | `e.Status` | null |
| `ErrorCode` | Ok → null; Error → **writer tự suy ra** `"http"` nếu `Status > 0`, ngược lại `"network"` (mirror `ApiMonitorStore.RecordError` — event terminal **không** mang `FailureKind`) | null |
| `ClientKeyId` | từ entry | như vậy |
| `Severity` | `Info` (Ok) / `Error` (Error) | `Info` |
| `Message` | `"Request {id} hoàn tất: {model} → {provider}, HTTP {status}, {ms}ms"` (Error/Cancelled tương ứng)"thất bại"/"bị huỷ") | |

- **Fail-open:** mọi lỗi DB bị bắt + `Log.Error` — **không nổ** ra feed (contract multicast delegate); ghi thất bại → **không** gọi `Invalidate()`.
- Ghi thành công → `Invalidate()`.
- **DI:** MauiProgram đăng ký `IStatsInvalidationSource`, `IRequestJournalWriter`, `IStatsService` (singleton); `ProxyHost` ctor nhận thêm `IRequestJournalWriter` **chỉ để eager-create** singleton từ app container (mirror cách `ITraceFeed`/`IApiMonitorStore` được tạo sớm — writer không cần đăng ký vào proxy container vì không endpoint nào inject nó).

## 5. Định nghĩa số liệu (`StatsService`)

- **Cửa sổ UTC:** `Today` = [nửa đêm UTC hôm nay, now]; `Last7Days` = [nửa đêm UTC − 6 ngày, now]; `Last30Days` = [nửa đêm UTC − 29 ngày, now]. Range đến luôn = now (không đóng cửa sổ cuối ngày).
- **Requests/Ok/Errors/Cancelled:** đếm dòng terminal trong cửa sổ theo `Outcome`.
- **ErrorRate%** = `Errors / (Ok + Errors) × 100` — **không tính Cancelled** (hủy là chủ đích client — đồng bộ `ApiMonitorPanel.ErrorCount`); mẫu rỗng → 0. Làm tròn 1 chữ số thập phân khi hiển thị.
- **Latency:** chỉ dòng `Outcome=Ok` có `DurationMs NOT NULL`. `Avg` = trung bình; `P95` = nearest-rank: `ORDER BY DurationMs ASC LIMIT 1 OFFSET (ceil(0.95·n) − 1)` (ASC — offset tính từ giá trị nhỏ nhất để lấy đúng rank `ceil(0.95·n)`; DESC sẽ lấy sai quantile).
- **Tokens:** `SUM(PromptTokens/CompletionTokens)` trên dòng usage trong cửa sổ; breakdown tokens theo key = join usage ↔ terminal theo `RequestId`.
- **Buckets:** `Today` → 24 bucket **giờ** UTC (00h…23h); `Last7Days`/`Last30Days` → bucket **ngày** UTC. Đếm terminal rows theo `Timestamp`; bucket rỗng trả 0 (UI vẽ bar 0).
- **Snapshot `From`:** = start của cửa sổ; `To` = now.

## 6. Refresh

- UI subscribe `Changed` → **debounce 2s (trailing)** → `GetRangeAsync(range hiện tại)`; giữ số cũ đang hiển thị khi refetch (không nháy về rỗng).
- Fetch ngay khi: render lần đầu; đổi preset; sau debounce.
- **Không poll timer** — miss event → số cũ hơn vài giây, chấp nhận được.
- Lỗi query: `Log.Error`, giữ snapshot cũ; badge nhỏ "không làm mới được" dưới tiêu đề panel (i18n), không crash.
- Đọc lại `StatsErrorRateThreshold` mỗi lần fetch (đổi setting có hiệu lực ở lần refresh kế tiếp).

## 7. UI — `StatsPanel` (layout A)

- **Mount:** `Dashboard.razor` sau `.dash-trace-grid`. Component: `Components/Shared/StatsPanel.razor` + `Components/Shared/StatsChart.razor` (SVG).
- **Header:** `📊 @L["stats.title"]` + chips range: **Hôm nay** (mặc định) · 7 ngày · 30 ngày (active chip màu primary — theo mockup).
- **KPI cards** (grid `4 → 2 → 1` cột, responsive chuẩn app):

| Card | Nội dung |
|---|---|
| Requests | `Snapshot.Requests` (`N0`) |
| Error rate | `%` (1 số thập phân) + badge: ✓ dưới ngưỡng (xanh) / ⚠ bằng hoặc vượt ngưỡng (vàng/đỏ theo `StatsErrorRateThreshold`) |
| Latency | avg (`MonitorFormat.Duration`) + dòng phụ `p95 …` |
| Tokens | `Prompt+Completion` compact (`1 234`, `12,3k`, `1,2M`) |

- **Chart SVG** (`StatsChart`): bar stacked per bucket — requests (fill `#3b82f6`) + errors (fill `#ef4444`, chồng lên). Legend 2 mục. Không data → render `EmptyState`.
- **Breakdown:** tabs `Provider | Model | Client key`; bảng cột: Tên · Requests · Lỗi · Error rate · Tokens (tab Client key: Error rate tính trên mẫu có của key đó); sort desc đã có từ service; UI hiện tối đa **20 dòng** + dòng "… còn N dòng" khi trội.
- **Empty** (0 request trong cửa sổ): `EmptyState` component — "Chưa có request trong khoảng thời gian này" (chart + bảng); KPI hiện `0`/`—`.
- **i18n EN/VI** keys `stats.*`: `stats.title`, `stats.range.today/7d/30d`, `stats.kpi.requests/errorRate/latency/tokens`, `stats.kpi.p95`, `stats.threshold.under/over`, `stats.chart.requests/errors`, `stats.tab.provider/model/clientKey`, `stats.col.name/requests/errors/errorRate/tokens`, `stats.more`, `stats.empty`, `stats.refreshFailed`.
- Colors dùng bảng màu trace (`#3b82f6/#ef4444/#22c55e/#eab308`) + CSS vars theme; dot provider tái dùng pattern `monitor-dot`.

## 8. Testing (xUnit — `router balancing test`)

1. **`RequestJournalWriterTests`** (fake feed publish thẳng `TraceEvent`, `TestDb` SQLite):
   - `Finished_Success_WritesOkRowWithRouteAndDuration` — đúng Outcome/ProviderKey/ModelKey/HttpStatus/DurationMs/ClientKeyId.
   - `Finished_Failure_WritesErrorRowWithDerivedFailureKind` (Status 503 → "http"; Status null → "network").
   - `Canceled_WritesCancelledRow`.
   - `ReceivedPublishedTwice_KeepsOriginalStartedAt` (G2 HeadersSent re-publish).
   - `MultipleAttempts_LatestAttemptWinsRoute`.
   - `DbFailure_LogsAndDoesNotInvalidate` + không nổ feed.
   - `Write_Success_InvalidatesSource`.
   - `StaleEntry_OlderThan30Minutes_IsEvicted`.
2. **`StatsServiceTests`** (`TestDb`, seed LogEntry/usage trực tiếp):
   - `ErrorRate_ExcludesCancelled`.
   - `P95_NearestRank`.
   - `TodayWindow_UsesUtcMidnight` (boundary trước/nửa đêm UTC).
   - `Breakdown_JoinsUsageTokensByRequestId`.
   - `EmptyWindow_ReturnsZerosAndNullLatency`.
   - `TodayBuckets_Hourly24`.
3. **Migration test** kiểu `AddProxyAssignmentsMigrationTests`: cột mới nullable, dòng cũ `Outcome NULL` không bị stats đếm.
4. UI: kiểm thử thủ công (test project không có bUnit).

## 9. Out of scope / tương lai

- Chart theo provider (stacked theo key), tooltip hover chi tiết.
- Breakdown theo Combo/Account; filter theo model cụ thể.
- Cost USD, rollup table, export.
- Cảnh báo push khi vượt ngưỡng (hiện chỉ badge tĩnh).
