# Log chẩn đoán stream chết giữa chừng — thiết kế

- **Ngày:** 2026-11-11
- **Trạng thái:** đã triển khai 2026-11-12
- **Liên quan:** `docs/superpowers/specs/2026-11-07-cf-idle-timeout-hardening-design.md` (premise "CF idle timeout là thủ phạm chính" đã bị dữ liệu production bác bỏ — xem §1)

## 1. Vì sao

Người dùng cần trả lời được câu "vì sao stream SSE bị ngắt giữa dòng trả lời" **ngay trong app** (Logs panel đọc bảng `LogEntries`), không cần cắm debugger hay đọc DB thô.

Điều tra trên bản copy DB production (read-only) kết luận:

- 325–329 event `UpstreamStreamDiedException`; **toàn bộ** inner là `HttpIOException: The response ended prematurely. (ResponseEnded)`.
- 87% (285/329) có gap < 1s → upstream **chủ động RST/đóng kết nối**, không phải idle timeout.
- Chỉ 1 event rơi vào vùng ~95–105s (dấu hiệu Cloudflare idle); không có cụm nào ở các mốc tròn 29/60/100s.
- 93% event nằm ở Unsloth Cloud (+ Unsloth Cloud Temp); NVIDIA NIM đi **cùng proxy**, lưu lượng còn lớn hơn nhưng chỉ 4 event → thủ phạm là origin Unsloth, không phải proxy/SOCKS5.
- 149 event proxy down/failover nhưng gần như không event stream death trong +5 phút sau đó → proxy không giết tunnel đang bay.
- Client-abort **không** tạo `UpstreamStreamDiedException` (OCE được re-throw) → 325 event là cắt phía upstream thật.

Vấn đề: log hiện tại không chứng minh được chuỗi trên khi sự cố xảy ra lại, và không định vị được attempt/TK/route/byte đã gửi:

| Thiếu | Cần để trả lời |
|---|---|
| `elapsed` từ lúc gửi request tới lúc chết | Chết ngay (RST sớm) hay chết sau 5 phút (idle/timeout)? |
| Số byte content đã tới client | Client cụt giữa JSON hay chưa nhận gì? (phân loại #7/#8) |
| TK (account) đang phục vụ + attempt # | Chết ở attempt mấy, TK nào — có phải TK hay bị origin cắt? |
| Route proxy đã cấu hình + trạng thái down | Loại trừ/khẳng định proxy mà không cần đọc code |
| Một dòng "hint" đọc được ngay | Người trực ca không phải đoán `response ended prematurely` nghĩa là gì |

## 2. Mục tiêu / Không mục tiêu

**Mục tiêu**

- Thêm 3 điểm log để mỗi ca stream chết có đủ 5 field trên, đọc được trong Logs panel hiện có.
- Dòng log phải **correlate được** bằng `RequestId` sẵn có.
- Không đổi UI, schema DB, hay API.

**Không mục tiêu**

- Không đổi hành vi forward/retry/failover/keep-alive/buffering.
- Không sửa origin Unsloth, không đổi proxy, không đổi `upstreamIdleTimeoutSec`.
- Không thêm metric/bảng mới, không đổi `IApiMonitorStore`/`IRequestUsageSink`.
- Không log nội dung request/response (tránh secret/PII).

**Anti-goals (coi như thất bại nếu)**

- Log mới làm tăng notification (ErrorAlertAggregator) hoặc nhiễu Warns trong test/UI.
- Log ném exception và thay thế exception gốc trên đường rethrow.
- Một ca stream chết vẫn không trả lời được "chết sau bao lâu, ở attempt nào, byte bao nhiêu, route nào".

## 3. Phương án

| # | Cách làm | Đánh đổi | Kết luận |
|---|---|---|---|
| **A** | **Làm giàu 3 điểm log đã có sẵn nơi quan sát cái chết** (handler mid-stream, dispatcher serve-catch, endpoint Aborted) | Rẻ, không đổi behavior, không đụng schema/UI; một ca chết sinh 2–3 dòng nhưng mỗi dòng một tầng dữ liệu khác nhau | **Chọn** |
| B | Ghi lại đúng hop proxy đã dùng vào `ctx.Items` tại lúc nhận header rồi log hop đó | Chính xác hơn "route cấu hình", nhưng phải sửa `ProxyHealthHandler` — đường chung của mọi request; điều tra đã loại proxy nên lợi ích thấp so với rủi ro | Loại (ghi chú follow-up nếu sau này cần đúng hop) |
| C | Bảng/event vòng đời stream (start/bytes/death) + UI riêng | Nhiều thông tin nhất nhưng thêm schema + UI + migration, vượt phạm vi "chỉ thêm log" | Loại |

## 4. Thiết kế

### 4.1 Điểm #1 — `ProxyRequestHandler.ForwardAsync`, catch giữa stream

Vị trí: `catch` tổng quát trong khối tee (`src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs`, ~dòng 246), **trước** `throw`.

- Chỉ ghi khi `!ctx.RequestAborted.IsCancellationRequested` — client tự ngắt không phải sự cố upstream, mirror guard `monitor.RecordError` sẵn có.
- `Severity = Warning` (xem quyết định D1), `Category = Request`.
- `Message`: `Upstream '{provider.Name}' đứt stream sau {stopwatch.ElapsedMilliseconds}ms — account '{account.Name}', route {route}.`
- `Details`: `ex.ToString()` (stack đầy đủ).
- `ErrorCode`: `(ex.InnerException ?? ex).GetType().Name` — cùng quy ước với catch pre-content, cho phép filter `HttpIOException` trong DB.
- `RequestId`, `ClientKeyId`: như các dòng cùng handler.
- `route` = helper mới `DescribeProxyRoute(provider, account)`:
  - `resolver.Resolve(provider, account)` (`IProxySelectionResolver`, đã có sẵn entity snapshot nên không query DB).
  - `IsDirect` → `direct`.
  - Ngược lại: `{Mode} ids=[{ProxyIds}] enabled=[{id}:{Endpoint}...]`, mỗi proxy down thêm hậu tố `(down)`; không proxy nào enabled → `enabled=[]`.
  - `Endpoint` là `scheme://host:port` từ `ProxyRuntimeStatus` — không chứa credentials.
- Toàn bộ khối chẩn đoán bọc `try/catch` nuốt chủ đích (cùng lý do với `DispatcherLoop` dòng 270): log/resolve ném không được thay thế exception gốc đang rethrow.

Cần thêm 2 dependency vào ctor `ProxyRequestHandler`: `IProxySelectionResolver`, `IProxyPool` (đã đăng ký singleton ở `MauiProgram.cs:139` và `ProxyApp.cs:67`).

> Vì sao log "route cấu hình" chứ không phải "proxy đã dùng": `ProxyContext.Current` bị `ProxyHealthHandler` reset về `null` sau khi nhận header, nên tại thời điểm stream chết không còn hop thật. Route + down-state vẫn đủ để loại trừ proxy (nếu mọi proxy cấu hình đều sống mà origin chết → proxy không phải nguyên nhân).

### 4.2 Điểm #2 — `DispatcherLoop.ServeAsync`, serve-catch

Vị trí: `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` ~dòng 274–284 (dòng `"Lỗi dispatcher: {ex.Message}"` trong `catch (Exception ex)` sau `ForwardAsync`).

- Giữ nguyên `Severity = Error`, `Category = Request`, `ProviderKey`, `ModelKey`, `RequestId` — **không đổi đường alert** (mỗi ca chết vẫn đúng 1 dòng Error alertable như hôm nay).
- `Message` bổ sung: `attempt #{attemptNo}`, `account '{AccountNameOf(candidate, accountId)}'`, và kết luận `stream đã commit → Aborted` hoặc `chưa commit → 500` (dựa trên `request.Context.Response.HasStarted`).
- `Details` = `ex.ToString()` + hint **chỉ khi** `ex` là `UpstreamStreamDiedException` hoặc `UpstreamIdleTimeoutException`:
  ```
  Chẩn đoán: upstream đóng/RST giữa stream ("response ended prematurely") — không phải
  client cancel; đối chiếu dòng cùng RequestId ở handler (elapsed + route) trước khi
  nghi proxy/hạ tầng.
  ```
- Không đổi `ErrorCode`.
- Lưu ý: `DispatcherLoopTests.Loop_WhenResolverThrows_LogsErrorAndKeepsServing` anchor `"Lỗi dispatcher"` ở **catch cấp loop (dòng 64)**, không phải catch này — không phải sửa test đó.

### 4.3 Điểm #3 — `ProxyApp.RunQueueFirstAsync`, nhánh `Aborted`

Vị trí: `src/RouterBalancing.Core/Server/ProxyApp.cs` ~dòng 404–415, hai nhánh `#7` (`contentBytes == 0`) và `#8` (`contentBytes > 0`), cả hai đã có guard `!ctx.RequestAborted.IsCancellationRequested`.

- `Severity = Warning`, `Category = Request`, `RequestId = id`, `ClientKeyId = ClientKeyItems.IdOf(ctx)`.
- #7: `Stream chết trước khi có byte content nào — trả in-band server_error (request {id}).`
- #8: `Stream chết sau {contentBytes} byte content — trả in-band upstream_disconnected (request {id}).`
- Chỉ là breadcrumb "sự thật trên wire"; không đổi `WriteInBand`, không đổi `ClassifyOutcome`/monitor.

### 4.4 Bảng tổng hợp

| # | Nơi | Severity | Dữ liệu riêng |
|---|---|---|---|
| 1 | Handler mid-stream catch | Warning | elapsed, account, route proxy + down-state, inner exception type |
| 2 | Dispatcher serve-catch | Error (như cũ) | attempt #, account, kết luận Aborted/500, hint |
| 3 | Endpoint Aborted #7/#8 | Warning | contentBytes đã gửi tới client, event in-band đã ghi |

Ba dòng nối nhau bằng `RequestId` — chính là cách người dùng đọc Logs panel hiện tại.

## 5. Quyết định của tôi (người dùng chỉ cần check)

- **D1 — #1 và #3 dùng `Warning`, không phải `Error`.** `ErrorAlertAggregator` gom mọi `Error` có `ProviderKey` trong cửa sổ 30s; nếu #1 cũng `Error` thì mỗi ca chết bị đếm 2 lần trong cùng cụm notification (đổi hành vi alert). `Warning` vẫn hiển thị đầy đủ trong Logs panel và khớp tiền lệ "Warning để còn dấu vết chẩn đoán" của `tee.EndedWithoutTerminal`.
- **D2 — hint chỉ gắn cho 2 exception stream** (`UpstreamStreamDiedException`, `UpstreamIdleTimeoutException`), không gắn cho mọi exception dispatcher (tránh nhiễu khi resolver/DB lỗi).
- **D3 — route là cấu hình + down-state, không phải hop thật** (lý do ở §4.1). Nếu sau này cần hop thật, đó là thay đổi riêng ở `ProxyHealthHandler` (phương án B).
- **D4 — không log khi client abort** ở cả 3 điểm (giữ nguyên semantics hiện tại: client tự ngắt không phải lỗi upstream).

## 6. File dự kiến chạm

| File | Thay đổi |
|---|---|
| `src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs` | +2 dependency ctor; log #1; helper `DescribeProxyRoute` |
| `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` | enrich message #2 + helper hint |
| `src/RouterBalancing.Core/Server/ProxyApp.cs` | log #3 ở 2 nhánh Aborted |
| `router balancing test/Engine/ProxyRequestHandlerTests.cs` | cập nhật factory `Create(...)` (truyền `new ProxySelectionResolver()`, `DirectProxyPool`) + assert log #1 |
| `router balancing test/Engine/DispatcherLoopTests.cs` | test mới: mid-stream exception → message chứa attempt #/account/hint |
| `router balancing test/Server/ProxyRetryIntegrationTests.cs` | mở rộng `Stream_WhenUpstreamDiesMidContent_...` assert log #3 + contentBytes |

## 7. Kiểm chứng

```bash
dotnet build router-balancing.slnx
dotnet test "router balancing test/router balancing test.csproj"
```

Test mới/cập nhật phải khẳng định:

1. Mid-stream death (client còn nối) → đúng 1 `Warning` #1 với `RequestId`, chứa `đứt stream`, elapsed, `route direct` (stub provider không proxy).
2. Client abort mid-stream → **không** có `Warning` #1 (chỉ rethrow như cũ).
3. Dispatcher nhận mid-stream exception → message chứa `attempt #1`, tên account, `Aborted`; `Details` chứa hint; vẫn đúng 1 `Error` (không tăng số alert).
4. Integration: upstream chết sau `data: {"x":1}\n\n` → log #3 chứa số byte > 0 và `upstream_disconnected`; ca chết trước content → `server_error`, byte = 0.
5. Toàn bộ test hiện có xanh — đặc biệt `DispatcherLoopTests.Loop_WhenResolverThrows_LogsErrorAndKeepsServing` và các test đếm `log.Warns`/`log.Errors`.

## 8. Rủi ro

- **Thêm dòng Warning vào `CapturingLog.Warns`** có thể làm vỡ test đang `Assert.Single(log.Warns)` nếu test đó vô tình đi qua nhánh mid-stream — sẽ xử lý trong plan (chạy test để phát hiện, chỉ nới assertion khi đúng là dòng chẩn đoán mới).
- **`pool.Snapshot()` trong catch**: nếu ném sẽ bị nuốt bởi khối try/catch chẩn đoán, không ảnh hưởng request.
- **Log dài thêm** ở Message #2 — chấp nhận, vì mục tiêu là đọc được ngay trong panel.
