# Spec: Early headers + SSE keep-alive cho request stream (Unsloth-style)

- **Ngày:** 2026-10-06
- **Trạng thái:** drafted — chờ user review
- **Tiền đề:**
  - [2026-09-28-queue-selection-design.md](2026-09-28-queue-selection-design.md) — queue/cancel contract §3.4, `X-Request-Id` §3.6 (HTTP API giữ nguyên); mục "UI Runtime panel, nút huỷ" hoãn từ slice này nay làm ở §5.
  - [2026-10-05-exhaustive-account-failover-design.md](2026-10-05-exhaustive-account-failover-design.md) — failover walk + exhaustion (không đổi).
  - [2026-10-06-api-monitor-design.md](2026-10-06-api-monitor-design.md) — H1–H4, TTFT, `RecordResponse`/`RecordError` (giữ; amend status semantics ở §6).
  - [2026-10-05-live-request-trace-design.md](2026-10-05-live-request-trace-design.md) — trace feed H1–H4.
  - [2026-10-04-manual-retry-revamp-design.md](2026-10-04-manual-retry-revamp-design.md) — G6 không timeout upstream (giữ nguyên).

## 1. Bối cảnh & mục tiêu

Endpoint hiện tại chỉ ghi response **sau khi outcome về** (`ProxyApp` — `await request.Completion.Task`): status + headers rời server trễ nhất là lúc stream bắt đầu. Khi request nằm trong queue/park quá lâu (priority queue — hành vi bình thường), client thấy "không có header nào" → opencode ném `ProviderHeaderTimeoutError` sau 300.000ms (`headerTimeout` mặc định).

Các hướng đã loại:

- **Tắt `headerTimeout` phía client** — điều trị triệu chứng: mất lớp bảo vệ "gửi request mà không bao giờ có reply", và không phải client nào cũng sửa được config (đã làm một lần cho 5 provider trong `opencode.json`, coi là lớp tạm, không phải giải pháp).
- **Queue-wait budget kiểu OmniRoute** (429/504 sau N giây) — **user từ chối**: priority queue sinh ra để request **chờ**; item bình thường chờ sau item `max` là đúng hành vi, không phải lỗi cần giết.

### Quyết định của user (binding)

1. **Hướng Unsloth** (unslothai/unsloth PR #7047): flush headers ngay khi vào queue + SSE keep-alive trong lúc chờ. File tham chiếu: `studio/backend/routes/inference.py` (`_openai_admission_wait_stream_chunks`, `_OPENAI_PASSTHROUGH_SSE_KEEPALIVE = ": keep-alive\n\n"`, interval 5s).
2. **Scope `stream: true`** — non-stream giữ nguyên pipeline hiện tại (Unsloth cũng chia đôi: stream flush sớm, non-stream chờ trước khi trả JSON).
3. **Trả status sớm là chủ đích** (đã có kế hoạch từ trước): wire status của stream request sau enqueue = **200 + `text/event-stream`**; mọi outcome lỗi/edge chuyển thành **event in-band** trong chính stream.
4. **Nút huỷ request đang chờ trong `RequestDetailModal`** đưa vào scope spec này (chưa tồn tại; escape hatch duy nhất ngoài việc client tự ngắt thì không chấp nhận được).
5. **Giữ nguyên**: policy không timeout upstream (G6), failover walk/exhaustion, cancel HTTP contract (200/409/404), `X-Request-Id`, trace H1–H4, monitor hooks.

### Non-goals

- Không budget/depth cap/và không kill request đang chờ trong queue.
- Không early-flush cho non-stream (vì content-type/status chưa biết trước → commit 200 + JSON sẽ làm mọi lỗi thành "200 nhưng sai nội dung").
- Không chữa "upstream treo giữ slot vĩnh viễn" (G6) — ghi nhận ở §9; escape hatch = nút huỷ + client disconnect.
- Không biến keep-alive interval thành setting (const 5s — chưa cần).
- Không đổi shape `GET /v1/requests` hay contract cancel HTTP.

## 2. Kiến trúc tổng quan

### 2.1 Luồng stream (`stream: true`) — sau feature

```
id sinh → PrepareAsync (validate fail → 400 status THẬT — TRƯỚC flush)
→ Enqueue → monitor.StartRequest (H1) → trace Received → RequestAborted.Register
→ FLUSH: 200 + text/event-stream + Cache-Control: no-cache (X-Request-Id đã set từ đầu)
→ endpoint = TAY VIẾT DUY NHẤT của response body:
     loop {
       đọc Pipe có data      → ghi ngay cho client (contentBytes += n)
       chờ > 5s mà chưa data → ghi ": keep-alive\n\n"
       Completion về         → drain Pipe → outcome theo §4.3 → kết thúc
     }

handler (2xx): TeeAsync(upstream, pipe.Writer.AsStream(), ct)   ← chỉ đổi tham số dest
               monitor.RecordResponse / usage / TTFT — GIỮ NGUYÊN 100%
```

### 2.2 Luồng non-stream — không đổi

Không flush, không Pipe: `await Completion` → ghi status/content-type/body thật như hiện tại byte-for-byte.

### 2.3 Tại sao single-writer qua Pipe

Giữ hai tay viết cùng lúc (keep-alive của endpoint + tee của handler ghi thẳng `ctx.Response.Body`) là race: hai task ghi chung stream có thể xén lẫn nhau giữa frame SSE. `System.IO.Pipelines.Pipe` giải quyết một lần:

- Endpoint tạo Pipe tại flush, để trong `ctx.Items` — cùng pattern `ClientKeyItems.RequestId` (handler đã nhận `ctx` chứ không nhận `ProxyRequest`).
- Handler2xx ghi vào `pipe.Writer.AsStream()`; **không** Complete writer.
- Endpoint (chủ sở hữu Pipe) `Writer.Complete()` duy nhất 1 lần khi `Completion` về — lúc này handler chắc chắn đã xong (hoặc chưa từng chạy) → drain kết thúc sạch, không cần handshake suspend/resume kể cả khi park giữa failover (Pipe trống → keep-alive tự tiếp).
- Backpressure tự nhiên qua `PauseWriterThreshold` — client chậm làm tee chậm theo, không phình bộ nhớ.

## 3. Data contracts

### 3.1 `ValidationResult` + cờ `IsStream`

`ChatRequestValidator` đã parse body JSON — đọc thêm `stream` (vắng/false → false):

```csharp
public readonly record struct ValidationResult(ValidationFailure Failure, string? ModelId, bool IsStream);
```

### 3.2 `PreparedChatRequest`

```csharp
public sealed record PreparedChatRequest(string ModelId, byte[] Body, bool IsStream);
```

### 3.3 `SsePipeItems` (mới, nhỏ — `Engine/`)

`const string Key` cho `ctx.Items` chứa `Pipe` (chỉ set khi `IsStream`). Handler đọc được → dest = writer stream; không có → dest = `ctx.Response.Body` (hành vi non-stream hôm nay).

### 3.4 `IRequestCancelService` (mới — §5)

```csharp
public enum RequestCancelResult { Cancelled, NotCancellable, NotFound, NotRunning }
public interface IRequestCancelService
{
    RequestCancelResult Cancel(string id);
}
```

### 3.5 In-band error event

Mỗi outcome lỗi (đã flush) → **exactly một** dòng SSE, KHÔNG `[DONE]` (Unsloth parity — lỗi thì stream kết thúc ngay sau event):

```
data: {"type":"error","error":{...}}\n\n
```

| Nguồn | `error` object |
|---|---|
| `DispatchOutcome.Error(status,msg,type,param,code)` | `{ message, type, param, code, status }` — map thẳng record |
| `DispatchOutcome.Passthrough(status, _, body, retryAfterHeader)` | parse `body` JSON: còn key `error` → `"type":"error"` + giữ nguyên phần còn lại của body; không parse được → `{ message: <body cap 64KB>, type: "upstream_error", status }`; có `retryAfterHeader` (string) → thêm `"retry_after": "<raw header>"` |
| `Cancelled` (huỷ qua API/nút popup) | `{ message: "Request cancelled.", type: "invalid_request_error", code: "request_cancelled" }` |
| `Aborted` pre-content (exception server, §4.3) | `{ message: "Internal server error", type: "server_error", code: "server_error" }` |

- Top-level `type:"error"` = bộ phân biệt mà `opencode` `parseStreamError` đòi (`body.type === "error"` → trả `isRetryable: true` → opencode tự retry — đúng mong muốn với exhaustion/429).
- `error.code` giữ OpenAI-style cho client khác đọc.
- Cap message tái dùng `ApiMonitorStore.DecodeCapped` (64KB, truncate byte trước decode).
- **Mất `Retry-After` header** trên wire (headers đã commit) → bù bằng field `retry_after` trong JSON — accepted.

## 4. Behavior

### 4.1 Flush point

Nằm trong endpoint, **sau** `RequestAborted.Register`, **trước** `await Completion` (vị trí hiện tại `ProxyApp.cs:181`):

```csharp
if (prepared.IsStream)
{
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var pipe = new Pipe();
    ctx.Items[SsePipeItems.Key] = pipe;
    try { await ctx.Response.StartAsync(ctx.RequestAborted); }
    catch (OperationCanceledException) { return; } // client ngắt ngay lúc flush
    outcome = await RunStreamOutcomeLoopAsync(ctx, pipe, request.Completion.Task);
}
else
{
    outcome = await request.Completion.Task;
}
```

- **Không có fast-fail carve-out**: stream đã qua enqueue là status 200 — kể cả resolve-fail xảy ra trong micro giây cũng đi in-band. Nhất quán hơn cho client/test (404 `model_not_found` đến qua event, message/code giữ nguyên).
- Pre-flush vẫn trả status thật: `ApiKeyMiddleware` 401, validate 400, enqueue-fail 500.

### 4.2 Keep-alive loop (`RunStreamOutcomeLoopAsync`)

```
const int KeepAliveIntervalMs = 5000;
var readTask = pipe.Reader.ReadAsync(ct).AsTask();
long contentBytes = 0;   // CHỈ tính byte từ Pipe — keep-alive không đếm
while (true)
{
    var completed = await Task.WhenAny(readTask, Task.Delay(KeepAliveIntervalMs, ct), completion);
    if (completed == readTask)        → ghi hết data (contentBytes += n); readTask = ReadAsync().AsTask();
    else if (completed is Delay)      → ghi ": keep-alive\n\n" (re-read task giữ nguyên — tái sử dụng);
                                        nếu completion xong ngay sau đó → thoát ở nhánh dưới
    else                              → break;   // Completion về
}
pipe.Writer.Complete();          // completer duy nhất — handler đã dừng
drain pipe.Reader đến completed (ReadAsync ném do writer fault mid-stream → coi là stream end);
return theo §4.3;
```

- `Task.Delay` là timer 5s lặp — khi Pipe có data liên tục thì data thắng `WhenAny` → không bao giờ gửi keep-alive thừa (LLM giữa hai token >5s thì keep-alive vẫn vào đúng chỗ trống — hợp lệ theo SSE spec, parser bỏ qua comment).
- Không deadline cho `Completion` — request chờ bao lâu tùy ý (binding decision §1).
- `ReadAsync`/`Delay` nhận `ctx.RequestAborted` → client ngắt giữa loop ném `OperationCanceledException`: endpoint bắt, không ghi gì (nhánh #6 bảng dưới) — outcome `Cancelled` + `RequestAborted` đã đủ để trace ra `Canceled`.

### 4.3 Bảng quyết định outcome (endpoint, sau loop)

| # | Condition | Wire | Trace/Monitor |
|---|---|---|---|
| 1 | `Handled` | drain xong → đóng stream (upstream `[DONE]` đã pass-through) | H4: `Finished, Success=true, status = ctx.Response.StatusCode` (=200) — code hiện tại giữ nguyên |
| 2 | `Error` (isStream) | in-band event (§3.5) | H4 logical: `Finished, false, error.Status` (ClassifyOutcome hiện tại) |
| 3 | `Passthrough` (isStream) | in-band event (§3.5) — KHÔNG status/Retry-After header | H4 logical: `Finished, <4xx>, passthrough.Status` |
| 4 | `Cancelled` + `RequestAborted` (client tự ngắt) | không ghi gì | `Canceled` (như hiện tại) |
| 5 | `Cancelled` + API/popup (client còn kết nối) | in-band `request_cancelled` | `Canceled`; log Info như hiện tại (`ProxyApp.cs:228-235`) |
| 6 | `Aborted` + `RequestAborted` (client ngắt giữa serve) | không ghi gì | `Canceled` (như hiện tại) |
| 7 | `Aborted` + không client + `contentBytes == 0` (exception trước khi có content) | in-band `server_error` | **override trước khi publish**: `Finished, false, 500` (đỏ) thay vì `Canceled` |
| 8 | `Aborted` + không client + `contentBytes > 0` (mid-stream fail) | đóng stream im lặng — KHÔNG append event, KHÔNG `[DONE]` | giữ nguyên semantics hiện tại: `Canceled` (vàng) + `RecordError(0)` của tee (b0b3ae7) |
| 9 | `Retryable`/`Fatal` lọt (bug-path `ProxyApp.cs:210`) | in-band `server_error` | log Error như hiện tại |

- Override ở #7 chỉ áp khi `IsStream` — non-stream dùng luật `HasStarted` của `DispatcherLoop:219` như hôm nay (chưa commit → `Error(500)` đỏ; mid-body → `Aborted` vàng).
- `DispatcherLoop` **không sửa gì** — mapping `HasStarted` của nó không còn quyết định outcome cho stream (endpoint tự phân loại theo bảng).
- Non-stream: toàn bộ nhánh ghi hiện tại (`WriteErrorAsync`, `Passthrough` status thật, cancel 400) giữ nguyên.
- **Known limitation (final review):** exception KHÔNG phải OCE từ client-write giữa loop (vd `IOException`) thoát cả hai catch OCE-only → `trace.Publish` bị skip, monitor row non-terminal. Reachability thấp vì Kestrel báo transport break qua `RequestAborted` → OCE. Nếu sau này broadening catch: KHÔNG tái dùng nguyên văn nhánh OCE — `await completion` deadlock-free chỉ khi `RequestAborted.Register` bảo đảm outcome; write-fault không có bảo đảm đó, cộng policy không timeout (G6) sẽ treo vô hạn.

### 4.4 Thay đổi `ChatCompletionsHandler.ForwardAsync`

1. Bọc block set status/content-type 2xx bằng `if (!ctx.Response.HasStarted)` — sau flush các set này là no-op/ignored; guard cho rõ ý định (non-stream: HasStarted false → y như cũ).
2. Chọn dest cho tee:

```csharp
var dest = ctx.Items.TryGetValue(SsePipeItems.Key, out var p) ? ((Pipe)p!).Writer.AsStream() : ctx.Response.Body;
tee = await UsageCapture.TeeAsync(response.Content, dest, ct);
```

3. **Không** Complete writer trong handler — endpoint completer duy nhất (§4.2). Tee ném → `monitor.RecordError(0)` → rethrow → `ServeAsync` catch → `Aborted` → bảng §4.3 (#7/#8 phân biệt theo `contentBytes`).
4. Các hook còn lại (`RecordResponse`, `LogForwarded`, usage sink) — không đổi.

### 4.5 Monitor / trace

- H1–H3: không đổi (H1 `StartRequest` chạy trước flush — row hiện `Queued` đúng trong lúc keep-alive).
- TTFT: `FirstTokenAt` tính tại byte **upstream** đầu tiên (`UsageCapture.cs:109`) — keep-alive là byte của endpoint, không ảnh hưởng.
- `RecordResponse`/`RecordError`: sống trong handler quanh tee — không đổi (dest có khác nhưng tee vẫn quét y hệt).
- H4 `Handled → status = ctx.Response.StatusCode`: sau flush luôn 200 = status thành công thật → code hiện tại đúng, không sửa.
- Status logical vs wire: xem §6.

## 5. Nút huỷ request trong `RequestDetailModal`

### 5.1 `RequestCancelService` (mới, `Engine/`)

Trích phần logic state từ endpoint cancel (`ProxyApp.cs:322-342`) — nguồn sự thật DUY NHẤT cho cả HTTP lẫn UI:

```csharp
public sealed class RequestCancelService(IRequestQueue queue, IExecutionList executions) : IRequestCancelService
{
    public RequestCancelResult Cancel(string id)
    {
        if (queue.TryRemove(id, out var removed))
        {
            removed.Completion.TrySetResult(new DispatchOutcome.Cancelled());
            return RequestCancelResult.Cancelled;
        }
        return executions.Contains(id) ? RequestCancelResult.NotCancellable : RequestCancelResult.NotFound;
    }
}
```

- Đăng ký singleton trong `ProxyApp.ConfigureServices` (test harness có luôn).
- Endpoint `/v1/requests/{id}/cancel` delegate service → map `Cancelled→200 {"cancelled":true}`, `NotCancellable→409 not_cancellable`, `NotFound→404 request_not_found` — **contract §3.4 giữ nguyên từng byte**.
- Race Take-vs-cancel: `TryRemove` atomic như cũ — không đổi.

### 5.2 `IProxyHost.CancelRequest` (mỗi với `QueuedSnapshot` — `ProxyHost.cs:164`)

```csharp
public RequestCancelResult CancelRequest(string id)
{
    var app = _app;
    if (app is null) return RequestCancelResult.NotRunning;
    return app.Services.GetRequiredService<IRequestCancelService>().Cancel(id);
}
```

- Bridge UI → proxy container theo đúng pattern sẵn có (queue chỉ nằm trong proxy container; MAUI không có HTTP client tới proxy và không muốn phụ thuộc port/auth).

### 5.3 UI

- `RequestDetailModal.razor`: nút **Huỷ request** (khu vực header/action của modal content) — hiển thị khi `record?.State == ApiCallState.Queued` (record từ `Store.Find(RequestId)` đã có sẵn trong component).
- Click → `Proxy.CancelRequest(RequestId)` → toast/i18n theo kết quả:

| Result | Phản hồi |
|---|---|
| `Cancelled` | toast `trace.detail.cancelDone`; row tự chuyển vàng qua feed H4 khi endpoint gốc nhận outcome |
| `NotCancellable` | toast `trace.detail.cancelBusy` (409 — vừa bị Take) |
| `NotFound` | toast `trace.detail.cancelNotFound` (đã xong/evict) |
| `NotRunning` | toast `trace.detail.cancelNotRunning` |

- Không ConfirmDialog (thao tác reversible-theo-mới: client có thể gửi lại; nhỏ giọt như cancel API).
- Client gốc đang chờ: stream → in-band `request_cancelled` (#5); non-stream → 400 như cũ.
- i18n: thêm 5 key ×2 (EN+VI) vào `Translations.cs`, prefix `trace.detail.`.

## 6. Amend spec api-monitor §3.1 (status semantics)

Sau feature, wire status của stream request luôn là 200 → `ApiCallRecord.Status` / H4 `Status` là **logical status của outcome** (exhaustion `Passthrough(429)`, resolve `Error(404)`, cancel…), không còn đồng nghĩa "status bytes trên wire" cho mọi case. Implement task này cập nhật 1 câu trong §3.1 spec api-monitor + XML doc `ApiCallRecord.Status` — monitor phục vụ debug nên logical status là giá trị đúng.

## 7. Edge cases

| Case | Xử lý |
|---|---|
| Validate 400 / enqueue-fail 500 / auth 401 | Pre-flush → status thật (giữ nguyên) |
| Client ngắt trước/trong lúc flush | `RequestAborted` → `StartAsync` ném → return im lặng; outcome `Cancelled` + client-gone → không ghi |
| Cancel-vs-Take race | `TryRemove` atomic như cũ (§3.4 spec queue-selection) |
| Park giữa failover (`ReenqueueForPark`) | Pipe trống → keep-alive tự tiếp; không cần suspend/resume |
| Upstream trả JSON dù `stream=true` | Tee path non-SSE buffer rồi ghi một lần vào Pipe — bytes lọt vào framing event-stream; hiếm, không xử lý đặc biệt |
| Thành công | `[DONE]` của upstream pass-through; **lỗi → không có `[DONE]`** |
| `Retry-After` không truyền được dạng header | Field `retry_after` trong error object (§3.5) |
| Client đọc thô, không phải SSE-compliant | Thấy comment `: keep-alive` — hợp lệ theo SSE spec (dòng bắt đầu `:`) |
| HTTP/1.1 vs HTTP/2 | Chunked/frames do Kestrel — không can thiệp |
| Proxy stop khi popup còn row | `CancelRequest` → `NotRunning` |

## 8. Testing

### Unit

- `ChatRequestValidator`: `IsStream` true/false/thiếu/không phải bool.
- In-band builder: `Error` map đủ field; `Passthrough` merge JSON có `error`; non-JSON fallback + cap; có/không `retryAfter`; `request_cancelled`.
- `RequestCancelService`: 3 nhánh; gọi lần 2 sau khi đã huỷ → `NotFound`; idempotent với `TrySetResult`.

### Integration (TestServer — pattern `ProxyQueueIntegrationTests`/`ProxyRetryIntegrationTests`)

1. Stream + capacity đầy (park) → **headers 200 + `text/event-stream` + `X-Request-Id` về ngay**, trước khi dispatcher serve.
2. Keep-alive: giữ request trong queue > 5s → nhận `: keep-alive` (dòng comment) trước khi content.
3. Stream success → byte-forward y như cũ; monitor `RecordResponse` có TTFT/usage/ResponseBody (hook không đổi).
4. Cancel queued stream (qua service/HTTP) → client nhận in-band `request_cancelled`, KHÔNG `[DONE]`; feed H4 `Canceled`; row monitor vàng.
5. Stream exhaustion → in-band có `status: 429` logical; H4 status = 429; wire = 200.
6. Stream resolve-fail → in-band `model_not_found`/404 message giữ nguyên.
7. **Non-stream: toàn bộ test status thật hiện tại giữ xanh** (400/404/429 passthrough + `Retry-After` header).
8. `Aborted` pre-content (giả lập exception trong ForwardAsync trước tee) → in-band `server_error` + H4 đỏ (`Finished,false,500`); mid-content → vàng, đóng im lặng (giữ hành vi b0b3ae7).
9. Cancel-vs-Take race — giữ test cũ (service chỉ trích logic, contract không đổi).

### E2E thủ công

opencode thật trỏ vào proxy, ép capacity 1 + request `max` phía trước → request stream chờ vô hạn không `HeaderTimeoutError`; huỷ qua popup → client nhận `request_cancelled` in-band.

### UI

Repo không có UI test framework → checklist manual: nút chỉ hiện khi `Queued`; 4 toast; proxy stop → `NotRunning`.

## 9. Risks / known limitations

- **SDK không đọc SSE error event** (OpenAI SDK thuần, một số gateway) → thấy stream kết thúc "trống" thay vì status lỗi — trade-off tự nguyện của binding decision §1.3.
- **opencode auto-retry sau in-band** (`isRetryable: true`): exhaustion → retry = mong muốn; `request_cancelled` → client có thể gửi lại (request mới, id mới) — huỷ vẫn diệt đúng request cũ; verify trong E2E.
- **Keep-alive che "upstream treo giữ slot"**: request chờ vĩnh viễn sẽ luôn có dấu hiệu sống — không còn tín hiệu timeout nào tự động cắt; escape hatch = nút huỷ (§5) + client disconnect. Bài toán slot kẹt (G6) nằm ngoài spec này.
- **`Retry-After` mất dạng header** trên stream → bù field JSON (§3.5).
- In-band shape cần verify với opencode thật (E2E) — nếu `parseStreamError` không khớp thì điều chỉnh shape trong implementation mà không đổi contract khác.

## 10. Files dự kiến

| File | Thay đổi |
|---|---|
| `Engine/ChatRequestValidator.cs` | `ValidationResult.IsStream` |
| `Engine/ChatCompletionsHandler.cs` | `PreparedChatRequest.IsStream`; guard `HasStarted` block 2xx; tee dest qua `SsePipeItems` |
| `Engine/SsePipeItems.cs` | mới — Items key |
| `Engine/SseErrorEvent.cs` | mới — builder in-band event (§3.5) |
| `Engine/IRequestCancelService.cs` + `RequestCancelService.cs` | mới — §5.1 |
| `Server/ProxyApp.cs` | flush §4.1; keep-alive/outcome loop §4.2–4.3; delegate cancel endpoint §5.1; đăng ký service |
| `Server/IProxyHost.cs` + `Server/ProxyHost.cs` | `CancelRequest` §5.2 |
| `router-balancing/Components/Shared/RequestDetailModal.razor` | nút huỷ §5.3 |
| `Localization/Translations.cs` | 5 key ×2 |
| `docs/superpowers/specs/2026-10-06-api-monitor-design.md` | amend 1 câu §3.1 (§6 đây) |
| Test projects | §8 — thêm + audit test stream hiện tại |
