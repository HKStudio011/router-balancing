# Design Spec — router-balancing: Local LLM Proxy Load Balancer

- **Ngày**: 2026-09-25
- **Trạng thái**: Chờ review
- **Ngôn ngữ tài liệu**: Tiếng Việt (theo AGENTS.md)

---

## 1. Tổng quan & mục tiêu

**router-balancing** là một ứng dụng desktop Windows (.NET MAUI Blazor Hybrid) đóng vai trò **local proxy server**: nhận request theo chuẩn OpenAI từ client trên máy, **cân bằng tải** tới nhiều provider LLM (OpenAI-compatible và Anthropic), quản lý failover/retry, và quan sát (log, runtime, thống kê) qua một dashboard duy nhất.

### Quyết định đã chốt (brainstorm)

| # | Quyết định | Lựa chọn |
|---|---|---|
| 1 | Vai trò app | **Local proxy server** — host Kestrel, client ngoài trỏ vào |
| 2 | API expose | **Chuẩn OpenAI-only** (`/v1/chat/completions`, `/v1/models`, `/v1/responses`); backend Anthropic → tự dịch 2 chiều (kể cả stream) |
| 3 | Semantic response | **Sync chuẩn OpenAI** — giữ kết nối đến khi xong hoặc stream SSE; hàng đợi là bước nội bộ |
| 4 | Nền tảng | **Windows desktop trước** (chạy nền/tray); Android/iOS/mac để sau hoặc không làm |
| 5 | Bảo mật cửa vào | Bind `127.0.0.1` + **API key tùy chọn** trong Settings; key provider mã hóa **DPAPI** |
| 6 | Kiến trúc | **Hướng 1 — Modular monolith**: Kestrel sống trong process MAUI, DI chia sẻ |
| 7 | Ưu tiên request | 3 mức `Normal / High / Highest` — **tối đa 1 request Highest** tại một thời điểm (set mới → cũ downgrade về High) |
| 8 | RoundRobin failover | **Không cross-model failover** (lỗi → trả client); failover là đặc trưng của mode Fallback |
| 9 | Ping | **Watchdog ping định kỳ** + ping khi bấm Retry thủ công — **không** ping trước mỗi request |
| 10 | Cách client chọn combo | Trường **`model` = tên combo** (fallback: khớp model trực tiếp; không khớp → `404 model_not_found`) |
| 11 | `MaxRetry` | **Một giá trị dùng chung**: số lần retry watchdog VÀ ngưỡng lỗi liên tiếp đưa model vào ManualRetry |

---

## 2. Kiến trúc tổng thể & Hosting

### Process model

Một process duy nhất gồm:

1. **BlazorWebView** — UI dashboard (render in-process, không web server cho UI).
2. **Kestrel host** — proxy API, bind `127.0.0.1:{port}` (mặc định **8317**, đổi trong Settings; port bận → thử port kế tiếp + log warning).

### Startup flow

```
MauiProgram.CreateMauiApp()
  → đăng ký DI (Core + UI services)
  → single-instance check (named mutex — tranh chấp port)
  → auto-migration DB (Database.Migrate())  ← trước khi nhận traffic
  → khởi động Kestrel host ngầm (IHostedService, DI chia sẻ root container)
  → mở UI (BlazorWebView)
```

Đóng app → dừng host sạch (drain request đang chạy với timeout). Đóng cửa sổ mặc định = thu về tray (`NotifyIcon` trong `Platforms/Windows`: menu Mở app / Start with Windows / Exit).

### Cấu trúc solution

```
router-balancing.slnx
├── src/RouterBalancing.Core/        ← class lib net10.0 (mới)
│   ├── Domain/                      (Provider, Model, Combo, LogEntry, AppSettings…)
│   ├── Engine/                      (priority queue, dispatcher, balancer, retry/watchdog, translator, SSE)
│   ├── Server/                      (Kestrel endpoints, middleware) — FrameworkReference Microsoft.AspNetCore.App
│   └── Storage/                     (EF Core + SQLite, auto-migration, secret protector)
├── router-balancing/                ← MAUI app: UI Blazor, tray, i18n, theme, composition root
└── router balancing test/           ← xUnit → reference Core
```

### Real-time (in-process)

Event bus đơn giản (typed events) trong Core:

- `QueueChanged`, `LogEmitted`, `ModelStateChanged`, `StatsInvalidated`, `SettingsChanged`
- Component Blazor subscribe → `InvokeAsync(StateHasChanged)`.
- **Không SignalR** (UI và server cùng process).

### Dọn dẹp & tích hợp sẵn có

- **Xóa** service DMFT không liên quan: `YtDlp*`, `Download*`, `VideoLinkParser`, `SoundExtractor`, `AppUpdateService`…
- **Giữ** `ToastService` (+ `StoragePathProvider`/`AppSettingsService` nếu sửa lại cho đúng ngữ cảnh router-balancing).
- `vite-project`: build output → `wwwroot/dist/`, bỏ Bootstrap, bỏ jQuery, tích hợp `dmftTheme`, thêm Chart.js.

---

## 3. Data model & DB

**Storage**: EF Core + SQLite, file `%AppData%\router-balancing\router-balancing.db` (qua `StoragePathProvider`).

**Auto-migration**: startup gọi `Database.Migrate()` trước khi mở Kestrel — end-user không thể migrate thủ công; migration files generate lúc dev, commit vào repo.

### Entities

| Entity | Trường chính |
|---|---|
| `Provider` | Id, Name, `Type` (OpenAI/Anthropic), BaseUrl, ApiKey (**DPAPI** qua `IDataProtector`), Enabled, `MaxConcurrent` (mặc định 4), `LastTestResult` (success/fail + timestamp + message), timestamps |
| `Model` | Id, ProviderId FK, `ModelId` (string phía provider), DisplayName, Enabled, IsManual, metadata: ContextWindow, SupportsVision, SupportsThink, ThinkEfforts, Input/OutputModalities, timestamps |
| `Combo` | Id, Name, `Mode` (RoundRobin/Fallback) |
| `ComboItem` | Id, ComboId FK, Position, `TargetModelId?` **hoặc** `TargetComboId?` (self-reference; validate cycle khi lưu) |
| `LogEntry` | Id, Timestamp, Level (Info/Warning/Error), `Category` (App/Request), Message, ProviderId?, ModelId?, RequestId?, DurationMs?, PromptTokens?, CompletionTokens?, ErrorCode?, Details(JSON) |
| `AppSettings` | Key → JSON value |

**AppSettings keys** (chủ lực): `theme`, `language`, `port`, `apiKeyEnabled`, `apiKey` (DPAPI), `maxRetry`, `watchdogIntervalSec`, `defaultMaxConcurrent`, `logRetentionDays`, `closeToTray`, `startWithWindows`, `statsErrorRateThreshold`.

### Quyết định

1. **Một bảng `LogEntries`** cho cả app log lẫn request journal; Stats aggregate cùng bảng (filter `Category='Request'`). Index: `(Timestamp)`, `(Category, Timestamp, ProviderId, ModelId)`. Retention dọn tự động — mặc định **90 ngày** (> window 60 ngày).
2. **`MaxRetry` dùng chung một giá trị** (Quyết định #11): số lần retry watchdog = `60s×1 … 60s×MaxRetry`; ngưỡng "lỗi retryable liên tiếp" → ManualRetry. Reset bộ đếm khi request thành công.
3. Stats **query aggregate trực tiếp** trên SQLite (không rollup; thêm rollup sau nếu chậm).
4. Key provider mã hóa DPAPI (`System.Security.Cryptography.ProtectedData`, scope `CurrentUser`, purpose string riêng).

---

## 4. Provider/Model management

### Provider panel

- Danh sách + form **modal dialog**: Name, Type (`OpenAI`/`Anthropic`), BaseUrl, API Key, `MaxConcurrent` (spinner, mặc định 4).
- **Test connection**: `GET {base}/v1/models` (OpenAI) / `GET /v1/models` (Anthropic) với key trên form.
  - Provider **chưa lưu**: Test **pass trước** mới bật Save; fail → hiện lý do trong modal.
  - Provider **đã lưu**: badge ✅ Success / ❌ Fail + thời điểm test (lưu `LastTestResult` vào DB).
- CRUD: Thêm (modal) / Sửa (modal) / Xóa (confirm dialog — kèm cảnh báo xóa cả models).

### Model management

- **Thêm tự động**: `GET /v1/models` → list id → Save.
- **Thêm thủ công**: 1 model/lượt (input) **và bulk** (textarea, mỗi dòng 1 model id).
- **Xóa**: 1 model / xóa tất cả (confirm).
- **Active/Deactive**: toggle từng model + bật/tắt toàn bộ models trong provider.
- **Metadata auto-fetch** (ctx, vision, think, input/output support, think effort) — chain `IModelMetadataProvider`:
  1. Endpoint metadata riêng của provider nếu biết (kiểu OpenRouter: `GET /api/v1/models/{id}`);
  2. Fallback: **static catalog** model phổ biến (gpt-*, claude-*, deepseek-*) nhúng trong Core;
  3. Không có → `null`, **sửa tay được luôn** (OpenAI gốc không trả capabilities qua API).
- Metadata hiển thị dạng badge trong model list.

### Phân trang

Component `Pager` dùng chung (Providers & Models): `PageSize` (10/25/50/100) + "Trang x / y (tổng n)" + ô goto + Trước/Sau. Render khi tổng > 1 trang; state trong bộ nhớ component.

---

## 5. Engine

### Pipeline

```
HTTP request (OpenAI-style)
  → Validate + auth middleware (+ log "Request received")
  → Priority Queue (global) ── dispatcher ──► Chọn model ──► Execution List model
  → Gọi provider (dịch nếu cần) ──► response/SSE về client ──► log + token usage
```

### Priority Queue & Selection

- Key = `(Priority, Sequence)`; mức: `Normal(0) < High(1) < Highest(2)`.
  - Client set qua header tùy chọn **`X-Priority: high|max`** (mặc định normal).
  - Request **requeue từ ManualRetry luôn lấy High**.
  - Luật **1-Highest**: set `max` mới → cái cũ downgrade về `High` (enforce tập trung ở queue service).
  - Đổi priority lúc runtime (UI): push lại key mới + đánh dấu bản cũ invalid (lazy reheap) — quy mô local O(n) chấp nhận được. FIFO trong cùng priority.
- **Dispatcher** (1 task async): dequeue → resolve combo (nested recursion, cycle-check) → chọn model:
  - **RoundRobin**: xoay cursor qua model eligible; ưu tiên `in-flight = 0`, kế tiếp load thấp nhất (cursor là tie-break); model **đầy → bỏ qua, chờ slot trống**; **không cross-model failover** (Quyết định #8).
  - **Fallback**: theo thứ tự combo, lấy model đầu eligible; model đầy → **park chờ slot của chính nó** (failover chỉ khi *lỗi*, không khi *bận*).
  - Không model nào có capacity → park trong queue; scheduler đánh thức bằng event slot/state (không polling spin).
- **Nested combo**: item là sub-combo → selection **recurse** (RR xoay từng cấp, Fallback theo thứ tự từng cấp).
- **Execution List**: `ConcurrentDictionary<ModelKey, ExecutionState>` — count in-flight + danh sách request id (Runtime panel & requeue).

### Retry / Watchdog / ManualRetry

Trạng thái model: `Healthy → Degraded(backoff) → ManualRetry → (auto hoặc tay)`:

- **Phân loại lỗi**: retryable (timeout, 429, 5xx/529) vs non-retryable (401/400 — trả thẳng client, không tăng bộ đếm).
- **Bộ đếm lỗi liên tiếp per model**: +1 retryable error, reset khi thành công; đạt `MaxRetry` → **ManualRetry** (ngừng nhận request mới).
- **Watchdog** (background service): mỗi `WatchdogInterval` (setting, mặc định 60s) ping `GET /v1/models` từng provider; fail → backoff `60s×1, 60s×2 … 60s×MaxRetry` → hết → ManualRetry; thành công giữa chừng → reset Healthy. Nút **Retry thủ công**: ping ngay, pass → Healthy.
- **Requeue rule**: model/provider vào ManualRetry giữa chừng → hủy call upstream (`RequestAborted`) + chuyển request trong Execution List về queue **priority = High**; dispatcher chọn lại model.
- **Combo hết model khả dụng** (tất cả ManualRetry/disabled) → trả lỗi ngay client dạng OpenAI error JSON.
- **Failover per-request (Fallback)**: lỗi retryable → model kế tiếp trong combo; tối đa 1 lần/model/request.

### Dịch thuật & Streaming

- **OpenAI target**: passthrough (normalize nhỏ, bơm `usage` chunk cuối nếu provider thiếu).
- **Anthropic target**: `ITranslator` 2 chiều: system tách riêng, roles, multimodal, `tools/tool_calls ↔ tool_use/tool_result`, params, `stop_reason ↔ finish_reason`; **stream**: map SSE (`content_block_delta → delta chunks`) + usage.
- `/v1/responses`: passthrough với OpenAI target; Anthropic target → convert về intermediate ChatCompletions rồi dịch (tái dùng translator).
- Token usage từ `usage` / `message_delta.usage` → stats.

### Logging & cancellation

Mỗi attempt ghi 1 `LogEntry` (Category=Request: duration, status, tokens, error code). Client ngắt kết nối → hủy upstream, ghi `Cancelled`.

### Lỗi về client

Tất cả lỗi trả về dạng OpenAI error JSON: `{"error": {"message", "type", "code"}}`.

---

## 6. Runtime panel & Log panel

### Runtime panel (4 block, collapse được)

1. **Hàng chờ (Queue)** — group theo provider/model:
   - Row: request id ngắn, endpoint, thời điểm vào, thời gian chờ, **badge ưu tiên + nút đổi** `Normal ⇄ High ⇄ Highest` (nút Highest disabled khi đã có request khác giữ — tooltip "đã có request khác").
   - Header group: tổng chờ + phân bố (🔴1 🔼2 •5). Cập nhật qua `QueueChanged`.
2. **Đang thực thi (Execution List)** — theo provider/model:
   - Row: request id, model, bắt đầu, elapsed (tick/giây), token tạm tính (nếu stream có usage trung gian).
   - Header: `in-flight / MaxConcurrent` dạng progress bar.
3. **Kết quả gần nhất** — 50 record mới nhất: thời điểm, provider+model, status (✅/❌/Cancelled), tokens in/out, duration; click → **modal chi tiết** (error body, request id).
4. **Đang lỗi (ManualRetry + backoff)**:
   - Row: provider/model, lý do (ping fail / error liên tiếp), số đã thử / `MaxRetry`, **countdown lần auto-retry kế tiếp**, nút **[Retry ngay]** (khi đã hết số lần retry; trong backoff hiển thị countdown).
   - Vào ManualRetry → row warning; retry pass → xanh, biến mất sau 3s.

### Log panel

- **Filter bar**: Level (Info/Warning/Error), Category (App/Request), provider/model, text search, time range — responsive (collapse thành hàng select < 640px).
- **Live mode**: auto-prepend từ `LogEmitted` + nút Pause/Resume; auto-scroll có toggle.
- **Request journal** (Category=Request): `method path → provider/model`, status, duration, tokens — click → modal chi tiết.
- Nguồn: query `LogEntries` (page load) + event stream (live); UI giữ max ~500 row (cũ nhất drop, có "tải thêm").

---

## 7. Statistics

- Vị trí: block trên Dashboard, header **segmented control**: `Hôm nay | 24h | 7 ngày | 30 ngày | 60 ngày`.
- **Summary cards** (4): Requests (+sparkline), Errors (+**error rate %**, badge đỏ > ngưỡng setting — mặc định 10%), Tokens (tách prompt/completion), Avg duration (+P50/P95).
- **Bar chart** stack (thành công/error): bucket **1h** cho Hôm nay/24h, bucket **ngày** cho 7d/30d/60d. Chart: **Chart.js** (npm trong `vite-project`), render qua `IJSRuntime`.
- **Bảng breakdown 2 cấp** (sort requests giảm dần): Provider ▸ expand → Model; columns: Requests | Errors | Tokens in | Tokens out | Share % (bar) | Avg ms. Provider row = aggregate models con.
- **Data path**: SQL aggregate trên `LogEntries` (`Timestamp >= @from` GROUP BY bucket/provider/model). Cache 30s; refresh khi đổi window + mỗi 30s khi panel mở (event `LogEmitted` → debounce).
- Empty state: "Chưa có request nào trong khoảng này".

---

## 8. Router combos & Settings

### Router panel

- **Danh sách combo** (table): Name, Mode badge, số model/con-combo, trạng thái (active/total), Edit/Delete (confirm).
- **Form combo (modal)**: Name, Mode radio (`RoundRobin | Fallback`), **Items list** thứ tự có ý nghĩa (Fallback):
  - Mỗi dòng = `Provider+Model` (dropdown 2 cấp) **hoặc** `Combo` (dropdown "📄 Combo").
  - Thêm/xóa dòng, ↑/↓ (không drag).
  - **Cycle check**: combo con không được là tổ tiên của chính nó → lỗi trong modal, chặn Save.
  - Delete combo bị tham chiếu → cảnh báo "đang được dùng bởi X combo / Y route".
- **Cách client chọn combo** (Quyết định #10): `model` = tên combo; không khớp combo → khớp model trực tiếp; không khớp → `404 model_not_found`. `/v1/models` trả cả combo lẫn model trực tiếp.

### Settings panel (nhóm form trong Dashboard)

| Nhóm | Nội dung |
|---|---|
| **General** | Language `Auto(system)/EN/VI`, Theme `Light/Dark/System`, Close → tray, Start with Windows |
| **Server** | Port (8317, range 1024–65535, apply → restart Kestrel + toast), API key on/off + giá trị + Regenerate |
| **Engine** | `MaxRetry`, `WatchdogInterval` (≥ 10s), `MaxConcurrent` mặc định cho provider mới |
| **Data** | Retention days, đường dẫn DB (read-only), nút "Dọn log cũ ngay", ngưỡng error rate stats |

- Save từng nhóm, validate inline. Đổi engine settings → áp ngay (event `SettingsChanged`).

---

## 9. UI System

### Dashboard layout

- **1 trang duy nhất**, panel xếp dọc: `Stats → Runtime → Provider → Router → Log → Settings`.
- **`<Panel>` dùng chung**: tiêu đề + icon + nút collapse + badge count (Log → số error mới, Runtime → số chờ). **Trạng thái collapse lưu localStorage**. Collapsed → chỉ thanh tiêu đề.
- **Top bar sticky**: tên app, trạng thái server (`:8317` xanh/đỏ), toggle theme (☀/🌙/💻), toggle language (EN/VI), mini số liệu hôm nay.
- Nav anchor nhảy tới từng panel.

### Responsive

Desktop-first nhưng responsive: stats grid `4 → 2 → 1` cột (`lg:`), bảng gói `overflow-x-auto`, **modal → bottom-sheet < 640px**, filter bar Log collapse thành select.

### Component dùng chung

`<Modal>` (backdrop, ESC/click-outside, focus trap, slot Header/Body/Footer), `<ConfirmDialog>`, `<Panel>`, `<Pager>`, `<Badge>`, `<EmptyState>`, `<ToastHost>` — Tailwind, tuân thủ theme qua CSS variable.

### i18n (EN/VI)

- Custom `I18n` service: nạp `wwwroot/i18n/en.json` + `vi.json` (flat key). API `I18n.T("key")`, event `CultureChanged` → re-render.
- Default = **ngôn ngữ hệ thống** (`CultureInfo.CurrentUICulture`, fallback EN), override trong Settings. Số/date theo `CurrentCulture`.
- 1 file JSON/ngôn ngữ (scale nhỏ).

### Theme

- Giữ `dmftTheme.applyTheme` trong `main.ts`; inline script trong `index.html` đọc setting, set `data-theme` **trước render** (tránh nháy trắng); Settings gọi `applyTheme`.
- Tailwind v4 CSS-first: `@custom-variant dark (&:where([data-theme="dark"] *))` → viết `dark:` classes.

### Vite/Tailwind pipeline

- `vite.config.js`: `build.outDir = '../wwwroot/dist'`; `index.html` load `dist/main.js` (module) + CSS; **bỏ Bootstrap + jQuery**; thêm Chart.js.
- Dev: `npm run watch` (script sẵn).
- JS interop chuẩn `IJSRuntime` (theme, Chart.js, localStorage).

### Toast

`ToastService` (event sẵn) + `<ToastHost>` stack góc phải dưới, auto-dismiss, pause on hover.

---

## 10. Phases triển khai

| Phase | Nội dung | Kết quả kiểm thử |
|---|---|---|
| **1. Foundation** | Hosting (Kestrel + tray + mutex), DB + auto-migration, Settings, i18n EN/VI, theme 3 mode, layout dashboard + panel shell, Log panel, vite/tailwind pipeline, toast | App chạy, log ghi/hiển thị, theme/lang đổi được |
| **2. Provider/Model mgmt** | CRUD, test connection, auto/manual model, metadata fetch, pagination, active/deactive | UI CRUD + test pass/fail |
| **3. Engine** | Queue, balancer (RR/Fallback), execution list, retry/watchdog/manual-retry, dịch thuật OpenAI↔Anthropic, streaming, endpoints | Unit test engine + e2e qua curl |
| **4. Runtime + Stats** | Runtime panel (queue/in-flight/results/manual-retry + đổi ưu tiên), Statistics (cards/chart/breakdown) | Panel realtime, stats đúng |
| **5. Router** | Combo CRUD, cycle check, nested resolution, `/v1/models` trả combo, client chọn combo qua `model` | Combo test e2e |

Mỗi phase = spec tách (nếu cần) → plan → implement → review.

---

## 11. Testing strategy

- **Unit (xUnit, reference Core)** — bắt buộc theo AGENTS.md:
  - Engine: selection policy (RR rotation/skip full, Fallback order/park), priority queue (3 mức, luật 1-Highest, lazy reheap), retry state machine (counter/reset/backoff/ManualRetry), cycle detection combo, error classification.
  - Translator: mapping messages/tools/params 2 chiều, stream event mapping, usage extraction.
  - Storage: auto-migration trên DB trống, secret protector roundtrip.
- **Integration**: `WebApplicationFactory`/TestServer cho Server module — auth middleware, error JSON shape, `/v1/models` resolution (combo vs model vs 404).
- **E2E thủ công**: curl stream qua proxy tới provider thật (phase 3+).
- UI: smoke theo phase (không tự động hóa UI ở giai đoạn đầu).

---

## 12. Out of scope / tương lai

- Android/iOS/MacCatalyst.
- Bảng rollup stats, pricing/cost (USD) theo token.
- Admin user/roles, multi-user.
- Proxy cho provider không phải OpenAI/Anthropic format.
- Drag-and-drop reorder combo.
