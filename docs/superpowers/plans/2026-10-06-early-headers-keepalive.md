# Early Headers + SSE Keep-alive Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Flush status 200 + `text/event-stream` ngay khi request stream vào queue (trước khi dispatcher serve), giữ kết nối sống bằng SSE keep-alive 5s, chuyển mọi outcome lỗi thành event in-band, và thêm nút huỷ request trong popup chi tiết.

**Architecture:** Endpoint tạo `System.IO.Pipelines.Pipe` tại flush, giữ vai trò tay viết DUY NHẤT response (drain pipe + keep-alive); handler tee chỉ đổi `dest` sang pipe writer — monitor/usage/TTFT/DispatcherLoop không đổi. Lỗi chưa serve được huỷ qua `IRequestCancelService` — nguồn sự thật chung cho HTTP cancel contract và nút UI.

**Tech Stack:** .NET 10 / ASP.NET Core (TestServer integration tests), xUnit, System.IO.Pipelines.

**Spec:** `docs/superpowers/specs/2026-10-06-early-headers-keepalive-design.md` (đã user-review — mọi quyết định số/ký hiệu lấy từ đây).

## Global Constraints

- Sau MỖI task: `dotnet build router-balancing.slnx` và `dotnet test "router balancing test/router balancing test.csproj"` phải xanh (task có step riêng để chạy).
- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên; comment giải thích "tại sao" tiếng Việt, XML doc cho public API (AGENTS.md).
- **Non-stream giữ nguyên byte-for-byte**: status thật, `Retry-After` header, cancel 400 — không được đổi bất kỳ test hiện tại nào.
- HTTP cancel contract bất biến: `POST /v1/requests/{id}/cancel` → 200 `{"cancelled":true}` / 409 `not_cancellable` / 404 `request_not_found`.
- Stream sau enqueue: wire status **luôn 200** + `text/event-stream`; lỗi → đúng MỘT event `data: {"type":"error",...}\n\n`; **không** `[DONE]` sau lỗi.
- Keep-alive: const 5000ms, byte chính xác `": keep-alive\n\n"`.
- Commit conventional, message tiếng Anh, một việc một commit.

## Review Focus

5 failure mode spec hàm ý nhưng không task nào test trực tiếp — mỗi dòng kèm test sở hữu:

1. **Keep-alive xen giữa làm hỏng framing SSE** (hai tay viết/race) → Task 4: `Stream_Success_ForwardsUpstreamBytesExactly` — lọc block `: keep-alive`, phần còn lại phải **byte-equal** upstream.
2. **Deadlock backpressure** (upstream > 64KB `PauseWriterThreshold`, client đọc chậm) → Task 4: `Stream_LargeUpstreamBody_CompletesWithoutDeadlock`.
3. **Client abort khi đang chờ** làm kẹt queue/hang Dispose/H4 mồ côi → Task 4: `Stream_ClientAbortQueued_RemovesFromQueueAndCompletes` — assert queue rỗng + monitor State `Cancelled` + không treo.
4. **Regression non-stream** (status thật, cancel 400, Retry-After) → Task 4 step: chạy **full suite** — gate `ProxyControlApiTests`/`Cancel_QueuedRequest_OriginReceives400WithMatchedRequestId`/`ProxyRetryIntegrationTests` phải giữ xanh.
5. **Format keep-alive sai chuẩn SSE** (client bỏ) → Task 4: `Stream_QueuedLongerThanKeepAlive_ReceivesCommentLine` — assert đúng `": keep-alive"` + kết thúc bằng dòng trống.

## File Structure

| File | Trách nhiệm | Task |
|---|---|---|
| `src/RouterBalancing.Core/Engine/ChatRequestValidator.cs` | Parse `stream` → `ValidationResult.IsStream` | 1 |
| `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs` | `PreparedChatRequest.IsStream`; tee dest qua pipe; guard HasStarted | 1, 4 |
| `src/RouterBalancing.Core/Engine/SseErrorEvent.cs` | Builder event in-band (pure, không I/O) | 2 |
| `src/RouterBalancing.Core/Engine/IRequestCancelService.cs` | Enum `RequestCancelResult` + interface | 3 |
| `src/RouterBalancing.Core/Engine/RequestCancelService.cs` | Logic huỷ atomic (TryRemove/TrySetResult) | 3 |
| `src/RouterBalancing.Core/Engine/SsePipeItems.cs` | Items key chứa `Pipe` | 4 |
| `src/RouterBalancing.Core/Server/ProxyApp.cs` | Flush + keep-alive loop + classification + in-band write; đăng ký service; delegate cancel endpoint | 3, 4 |
| `src/RouterBalancing.Core/Server/IProxyHost.cs` / `ProxyHost.cs` | `CancelRequest` bridge UI → container proxy | 5 |
| `router-balancing/Components/Shared/RequestDetailModal.razor` | Nút huỷ khi `State == Queued` | 5 |
| `src/RouterBalancing.Core/Localization/Translations.cs` | 5 key × EN/VI | 5 |
| `docs/superpowers/specs/2026-10-06-api-monitor-design.md` | Amend §3.1 status semantics | 4 |

---

### Task 1: `IsStream` xuyên suốt validator → prepared request

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ChatRequestValidator.cs:24` (record) + các `return new ValidationResult(...)` ở dòng 51/61/66/71/74
- Modify: `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs:25` (record), `:69` (construction)
- Test: `router balancing test/Engine/ChatRequestValidatorTests.cs` (bổ sung)

**Interfaces:**
- Consumes: không có.
- Produces: `ValidationResult(ValidationFailure Failure, string? ModelId, bool IsStream)` (record struct); `PreparedChatRequest(string ModelId, byte[] Body, bool IsStream)` — Task 4 đọc `prepared.IsStream`.

- [ ] **Step 1: Thêm 4 test case vào `ChatRequestValidatorTests` (theo naming `Validate_...` hiện có trong file)**

```
Validate_StreamTrue_IsStreamTrue          // body có "stream": true  → IsStream true
Validate_StreamMissing_IsStreamFalse      // không có stream          → false
Validate_StreamFalseOrNotBool_IsStreamFalse // "stream": false và "stream": "true" (string) → false
Validate_StreamStillParsesModelAndMessages // "stream": true nhưng thiếu messages → Failure=MissingMessages, IsStream=false
```

Mỗi test parse kết quả `ChatRequestValidator.Validate(body)` với body JSON dựng inline (pattern sẵn trong file).

- [ ] **Step 2: Chạy test — mong FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ChatRequestValidatorTests"`
Expected: FAIL — compile error `The 'ValidationResult' does not contain a constructor for 3 arguments` (chưa có param) — đúng lý do đỏ.

- [ ] **Step 3: Implement**

- `ValidationResult` thêm param thứ 3 `bool IsStream`; mọi nhánh failure trả `false`.
- Nhánh hợp lệ (dòng 74): đọc `root.TryGetProperty("stream", ...)` — đúng `JsonValueKind.True` → `true`, ngược lại `false` (đã có `using System.Text.Json`).
- XML doc 2 param mới/cũ cập nhật.
- `ChatCompletionsHandler`: record thêm `bool IsStream`; `PrepareAsync` trả `new PreparedChatRequest(validation.ModelId!, body, validation.IsStream)`.
- Kiểm tra bằng grep không còn chỗ nào dựng `ValidationResult(` 2 tham số hay `PreparedChatRequest(` 2 tham số.

- [ ] **Step 4: Chạy lại toàn bộ test project**

Run: `dotnet test "router balancing test/router balancing test.csproj"`
Expected: PASS toàn bộ (chỉ có test mới thêm + cũ, không lệch khác).

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Engine/ChatRequestValidator.cs src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs "router balancing test/Engine/ChatRequestValidatorTests.cs"
git commit -m "feat: parse stream flag in chat request validation"
```

---

### Task 2: `SseErrorEvent` — builder event in-band

**Files:**
- Create: `src/RouterBalancing.Core/Engine/SseErrorEvent.cs`
- Test: `router balancing test/Engine/SseErrorEventTests.cs`

**Interfaces:**
- Consumes: `ApiMonitorStore.DecodeCapped(byte[])` (internal static — đã có, dùng cho cap message 64KB).
- Produces (toàn bộ `internal static`, trả `byte[]` đã encode UTF-8, dạng `data: {json}\n\n`):
  - `byte[] Error(int status, string message, string type, string? param, string? code)`
  - `byte[] Passthrough(int status, byte[] body, string? retryAfterHeader)`
  - `byte[] Cancelled()`
  - `byte[] ServerFault()`
- Task 4 gọi đúng 4 method này.

- [ ] **Step 1: Viết test (red) — `SseErrorEventTests`**

Assert dạng **string chính xác** (chuyển `byte[]` sang string, so sánh cả đuôi `\n\n`):

```
Error_WhenGiven_ProducesTypeErrorEnvelope
  Error(404, "The model 'x' does not exist", "invalid_request_error", "model", "model_not_found")
  → data: {"type":"error","error":{"message":"The model 'x' does not exist","type":"invalid_request_error","param":"model","code":"model_not_found","status":404}}
     + "\n\n"

Error_WhenParamNull_SerializesNull            // param/code null → "param":null (parity WriteErrorAsync)
Cancelled_ProducesRequestCancelledEvent       // đúng spec §3.5: message "Request cancelled.", type invalid_request_error, code request_cancelled (KHÔNG có status)
ServerFault_ProducesServerErrorEvent          // message "Internal server error", type+code "server_error"

Passthrough_WhenBodyHasErrorKey_MergesTypeAndRetryAfter
  body = {"error":{"message":"rate limited"}} , retryAfterHeader = "30"
  → parse JSON: có key type=="error", error.message giữ nguyên, error.retry_after=="30"   (assert parsed, không so string thô)

Passthrough_WhenBodyNotJson_FallsBackToUpstreamError
  body = "<html>oops</html>" (bytes), status 400, retryAfterHeader null
  → parsed: type=="error", error.type=="upstream_error", error.status==400,
            error.message bắt đầu bằng "<html>oops" (qua DecodeCapped)

Passthrough_WhenBodyJsonButNoErrorKey_FallsBack
  body = {"message":"weird"} → fallback như trên (không merge), error.message chứa "weird"
```

- [ ] **Step 2: Chạy — mong FAIL** (class chưa tồn tại → compile error)

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~SseErrorEventTests"`

- [ ] **Step 3: Implement `SseErrorEvent`**

- Serializer options riêng: `JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }` — **bắt buộc** giữ apostrophe (contract OpenAI, cùng lý do `ChatCompletionsHandler.ErrorJsonOptions`).
- `Error`: serialize anonymous object `{ type = "error", error = new { message, type, param, code, status } }`.
- `Cancelled`/`ServerFault`: tự dựng object minimal đúng shape spec §3.5 (không có `status`).
- `Passthrough`: `JsonNode.Parse(body)` trong try/catch `JsonException`:
  - parse OK **và** root là `JsonObject` **và** `ContainsKey("error")` → set `obj["type"] = "error"`; nếu `retryAfterHeader != null` và `obj["error"]` là `JsonObject err` → `err["retry_after"] = retryAfterHeader`; serialize.
  - ngược lại → object `{ type = "error", error = new { message = DecodeCapped(body), type = "upstream_error", status, retry_after? } }`.
- Mọi method trả `Encoding.UTF8.GetBytes($"data: {json}\n\n")`.
- XML doc each method (public surface nội bộ — ghi contract shape).

- [ ] **Step 4: Chạy — mong PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~SseErrorEventTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Engine/SseErrorEvent.cs "router balancing test/Engine/SseErrorEventTests.cs"
git commit -m "feat: add sse in-band error event builder"
```

---

### Task 3: `RequestCancelService` + delegate cancel endpoint

**Files:**
- Create: `src/RouterBalancing.Core/Engine/IRequestCancelService.cs` (enum + interface cùng file)
- Create: `src/RouterBalancing.Core/Engine/RequestCancelService.cs`
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs:29-45` (`ConfigureServices` — đăng ký), `:321-344` (`MapPost /v1/requests/{id}/cancel` — delegate)
- Test: `router balancing test/Engine/RequestCancelServiceTests.cs`

**Interfaces:**
- Consumes: `IRequestQueue.TryRemove(string, out ProxyRequest?)`, `IExecutionList.Contains(string)`, `ProxyRequest.Completion` (TaskCompletionSource<DispatchOutcome>).
- Produces:
  ```csharp
  public enum RequestCancelResult { Cancelled, NotCancellable, NotFound, NotRunning }
  public interface IRequestCancelService { RequestCancelResult Cancel(string id); }
  ```
  Đăng ký singleton trong proxy container. Task 5 (`ProxyHost.CancelRequest`) và cancel endpoint dùng cùng enum/interface này. **Service không bao giờ trả `NotRunning`** (không biết trạng thái host).

- [ ] **Step 1: Viết 4 test red — `RequestCancelServiceTests`**

Pattern: copy setup của `ExecutionListTests` (class `IDisposable`, `TestDb`, `DbInitializer.Initialize`, `CreateSut => new ExecutionList(_db.CreateFactory())`) + `new RequestQueue()` (parameterless) + `new DefaultHttpContext()` để dựng `ProxyRequest`.

```
Cancel_WhenQueued_RemovesFromQueueAndCompletesWithCancelled
  enqueue ProxyRequest → svc.Cancel(id) == Cancelled
  + queue.Contains(id) == false
  + request.Completion.Task đã hoàn thành với outcome is DispatchOutcome.Cancelled

Cancel_WhenServing_ReturnsNotCancellable
  SeedProvider (MaxConcurrent≥1, 1 account enabled — pattern SeedProvider của ProxyQueueIntegrationTests)
  → executions.TryEnterAsync(providerId, "req-1", ...) == Entered (xem ExecutionListTests cách gọi)
  → svc.Cancel("req-1") == NotCancellable

Cancel_UnknownId_ReturnsNotFound
Cancel_AfterAlreadyCancelled_ReturnsNotFound   // idempotent: Cancel 2 lần → lần 2 NotFound
```

- [ ] **Step 2: Chạy — mong FAIL** (chưa có class)

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~RequestCancelServiceTests"`

- [ ] **Step 3: Implement**

- `IRequestCancelService.cs`: enum (XML doc từng giá trị, ghi rõ NotRunning chỉ do ProxyHost trả) + interface.
- `RequestCancelService`: primary ctor `(IRequestQueue queue, IExecutionList executions)`, body đúng 3 nhánh như pseudo-code trong spec §5.1.
- `ProxyApp.ConfigureServices`: `builder.Services.AddSingleton<IRequestCancelService, RequestCancelService>();` (đặt cạnh các đăng ký singleton ở region dòng 40-48).
- Cancel endpoint (`ProxyApp.cs:321-344`): thêm param `IRequestCancelService cancelSvc`; thay 3 nhánh logic queue/executions bằng `switch (cancelSvc.Cancel(id))`:
  - `Cancelled` → giữ nguyên body `Results.Json(new { cancelled = true })`
  - `NotCancellable` → giữ nguyên `WriteErrorAsync(409, ..., "not_cancellable")`
  - `NotFound or NotRunning` → giữ nguyên `WriteErrorAsync(404, ..., "request_not_found")` (NotRunning không tới được từ service — gộp nhánh để switch exhaustive, comment why).
  - Giữ nguyên comment `// TryRemove atomic với Take...` (dịch nghĩa: logic giờ nằm trong service).

- [ ] **Step 4: Chạy test mới + toàn bộ suite**

Run: `dotnet test "router balancing test/router balancing test.csproj"`
Expected: PASS toàn bộ — đặc biệt `ProxyControlApiTests` (contract cancel) và `ProxyQueueIntegrationTests.Cancel_QueuedRequest_OriginReceives400WithMatchedRequestId` không được đổi.

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Engine/IRequestCancelService.cs src/RouterBalancing.Core/Engine/RequestCancelService.cs src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/Engine/RequestCancelServiceTests.cs"
git commit -m "refactor: extract request cancel logic to IRequestCancelService"
```

---

### Task 4: Flush sớm + keep-alive pipe + in-band outcomes (lõi feature)

**Files:**
- Create: `src/RouterBalancing.Core/Engine/SsePipeItems.cs`
- Modify: `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs:155-174` (block 2xx)
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs:181-239` (endpoint) + method mới + const
- Modify: `docs/superpowers/specs/2026-10-06-api-monitor-design.md` §3.1
- Test: `router balancing test/Server/ProxyQueueIntegrationTests.cs` (+6 test), `router balancing test/Server/ProxyRetryIntegrationTests.cs` (+4 test)

**Interfaces:**
- Consumes: `prepared.IsStream` (Task 1), `SseErrorEvent.*` (Task 2), `RequestCancelService`/enum (Task 3 — endpoint cancel đã delegate, test cancel dùng HTTP API như cũ).
- Produces: `SsePipeItems.Key` (const string, value `"__RouterBalancing.SsePipe"`); behavior contract cho Task 5: request stream đã qua enqueue luôn trả 200+event-stream, client huỷ nhận in-band `request_cancelled` (Task 5 chỉ thêm nút gọi cùng service).

- [ ] **Step 1: Helper + 6 test queue-phase (red) trong `ProxyQueueIntegrationTests`**

Thêm helper (pattern `ChatBody` sẵn có):

```csharp
private static StringContent StreamChatBody(string model) =>
    Json($"{{\"model\":\"{model}\",\"messages\":[{{\"role\":\"user\"}}],\"stream\":true}}");
// SendAsync với HttpCompletionOption.ResponseHeadersRead — PostAsync mặc định buffer toàn body sẽ treo
private static Task<HttpResponseMessage> SendStreamAsync(HttpClient client, string model) =>
    client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        { Content = StreamChatBody(model) }, HttpCompletionOption.ResponseHeadersRead);
```

6 test (mọi test giữ slot bằng `GatedUpstream` + `SeedProvider(maxConcurrent: 1, "m1")` như test hiện tại; **timeout `.WaitAsync(TimeSpan)` tường minh ở mọi await — không để treo vô hạn**):

1. `Stream_WhenProviderSaturated_FlushesHeadersImmediately`
   `var resp = await SendStreamAsync(client, "m1").WaitAsync(2s)` — phải về trong khi vẫn queued (upstream.Calls==1, chưa Release).
   Assert: `200`, `Content.Headers.ContentType.MediaType == "text/event-stream"`, header `X-Request-Id` có, `Cache-Control` chứa `no-cache`, `resp` task chưa complete nội dung.

2. `Stream_QueuedLongerThanKeepAlive_ReceivesCommentLine`
   Sau khi có headers → đọc dòng đầu từ `resp.Content.ReadAsStreamAsync()` bằng `ReadLineAsync(cts.Token)` với timeout 8s → assert dòng == `": keep-alive"` (kết thúc blank line — đọc thêm 1 dòng rỗng). Rồi `upstream.Release()` → đọc hết còn lại chứa `"data: {\"x\":1}"`.
   *(Test này ~5.5s — chấp nhận, ghi chú trong XML doc test.)*

3. `Stream_Success_ForwardsUpstreamBytesExactly`
   Không saturation: `SendStreamAsync` → `ReadToEndAsync` (timeout 5s).
   Parse: tách body theo `"\n\n"`, lọc BỎ block == `": keep-alive"`, join lại bằng `"\n\n"` → phải **bằng đúng** chuỗi `Sse()` của upstream (`data: {"x":1}\n\ndata: [DONE]\n\n`). Assert thêm `Contains("data: [DONE]")`.
   *(Đây là test chống race hai tay viết — Review Focus #1.)*

4. `Stream_CancelQueued_ReceivesInBandRequestCancelledWithoutDone`
   Saturation → `SendStreamAsync` (headers về) → `CancelAsync(client, queuedId)` (helper sẵn có) == 200 → `ReadToEndAsync` origin (timeout 5s):
   assert `Contains("data: {\"type\":\"error\"")`, `Contains("\"code\":\"request_cancelled\"")`, `!Contains("[DONE]")`.

5. `Stream_LargeUpstreamBody_CompletesWithoutDeadlock` (Review Focus #2)
   Thêm stub trả body lớn: overload `Sse(string payload)`; payload 1MB = lặp `data: {"n":N}\n\n` ~200k lần + `data: [DONE]\n\n`. Không saturation (đọc hết ngay, nhưng 1MB vượt PauseWriterThreshold 64KB → ép pipe backpressure thật).
   `ReadToEndAsync` timeout 10s → assert `EndsWith("data: [DONE]\n\n")` + `Length == payload.Length` (sau khi filter keep-alive như test 3 — hoặc so len trực tiếp vì không có keep-alive trong <5s... an toàn: filter như test 3 rồi so length + tail).

6. `Stream_ClientAbortQueued_RemovesFromQueueAndCompletes` (Review Focus #3)
   Saturation → `SendStreamAsync` với CTS (`SendAsync(..., cts.Token)`) → chờ `WaitForQueuedIdAsync("m1")` → `cts.Cancel()` → assert.ThrowsAny OCE.
   Poll `_app.Services.GetRequiredService<IRequestQueue>().Contains(id)` == false (pattern 100×50ms như test Snapshot).
   Poll `Store` (`_app.Services.GetRequiredService<IApiMonitorStore>()`).Find(id) → `State == ApiCallState.Cancelled` (H4 Canceled — chứng publish không bị bỏ sót khi abort giữa loop).
   Cuối test: `upstream.Release()` + await request holder cũ (pattern `finally` hiện tại) — Dispose không treo.

- [ ] **Step 2: Chạy — mong FAIL đúng lý do**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProxyQueueIntegrationTests"`
Expected: các test mới FAIL với `TimeoutException` (headers chưa flush — endpoint còn `await Completion`) — đúng lý do đỏ. Test cũ trong file vẫn PASS.

- [ ] **Step 3: 4 test outcome-phase (red) trong `ProxyRetryIntegrationTests`**

Thêm helper local:

```csharp
private static StringContent StreamChatBody(string model) => ...(giống Step 1)
private sealed class ThrowingContent(bool writeFirst) : HttpContent
{
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        if (writeFirst) await stream.WriteAsync("data: {\"x\":1}\n\n"u8.ToArray());
        throw new IOException("boom");
    }
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
}
```

1. `Stream_AllUpstreamFail_PassesThroughLastResponseInBand` — clone setup/assert values của `Chat_WhenAllUpstreamFail_PassesThroughLastResponse` (dòng 318, cùng `Resp429` shapes + retryAfter), chỉ đổi body sang `stream:true` + đọc full response:
   assert 200 + `text/event-stream`; block `data:` chứa `"type":"error"` + message/status y như test gốc (đọc giá trị expectation từ test đó, không hardcode lại suy đoán) + nếu test gốc có retryAfter "30" → `Contains("\"retry_after\":\"30\"")` + `!Contains("[DONE]")`.

2. `Stream_AllNetworkFail_Returns502InBand` — clone `Chat_WhenAllUpstreamFail_Returns502WhenAllNetworkFailures` (dòng 344, upstream throw `HttpRequestException`) với `stream:true`:
   assert 200 + event-stream + `Contains("\"status\":502")` + message y như test gốc + `!Contains("[DONE]")`.

3. `Stream_WhenUpstreamDiesBeforeContent_ReturnsServerFaultInBandAndRedRow`
   `ScriptedUpstream` trả `new HttpResponseMessage(OK){ Content = new ThrowingContent(writeFirst: false) }`; `stream:true` (không saturation):
   assert 200 + event-stream; body `Contains("\"code\":\"server_error\"")` + `Contains("\"type\":\"error\"")` + `!Contains("[DONE]")`;
   monitor: `_app.Services.GetRequiredService<IApiMonitorStore>().Find(id)` → `State == ApiCallState.Error`, `Status == 500` (override đỏ — spec §4.3 #7). `id` lấy từ `X-Request-Id` của response.

4. `Stream_WhenUpstreamDiesMidContent_ClosesSilentlyAndYellowRow`
   `ThrowingContent(writeFirst: true)` + `stream:true`:
   assert body nhận được **chính xác** `"data: {\"x\":1}\n\n"` (không append thêm gì — không error event, không `[DONE]`, Review Focus #1/#4):
   monitor `Find(id)` → `State == ApiCallState.Cancelled` + `FailureKind == "network"` (giữ chủ đích vàng của `b0b3ae7` — `RecordError(0)` của tee-catch).

- [ ] **Step 4: Chạy — mong FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProxyRetryIntegrationTests"`
Expected: 4 test mới FAIL (thân stream vẫn đi nhánh non-stream / treo), test cũ PASS.

- [ ] **Step 5: Implement `SsePipeItems`**

`Engine/SsePipeItems.cs`: `internal static class SsePipeItems { public const string Key = "__RouterBalancing.SsePipe"; }` + XML doc: giá trị `Pipe` được endpoint set tại flush, handler đọc làm dest.

- [ ] **Step 6: Implement handler dest switch (`ChatCompletionsHandler.ForwardAsync`, block 2xx)**

- Bọc `ctx.Response.StatusCode`/`ContentType` (dòng 158-160) trong `if (!ctx.Response.HasStarted) { ... }` — comment why: sau flush các set này vô nghĩa; non-stream luôn vào nhánh này (y như cũ).
- Trước try tee:

```csharp
Stream dest = ctx.Items.TryGetValue(SsePipeItems.Key, out var pipeObj)
    ? ((Pipe)pipeObj!).Writer.AsStream()
    : ctx.Response.Body;
tee = await UsageCapture.TeeAsync(response.Content, dest, ct);
```

- **KHÔNG** dispose/Complete writer trong handler (endpoint là completer duy nhất — giải thích trong comment why). `System.IO.Pipelines` đã sẵn trong shared framework, không cần package thêm.

- [ ] **Step 7: Implement endpoint flush + loop + in-band (`ProxyApp.cs`)**

Đoạn `var outcome = await request.Completion.Task;` (dòng 181) thành:

```csharp
DispatchOutcome outcome;
Pipe? ssePipe = null;
long contentBytes = 0;   // CHỈ byte từ pipe — keep-alive không đếm (phân loại #7/#8)
if (prepared.IsStream)
{
    ctx.Response.StatusCode = 200;
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    var pipe = new Pipe();
    ctx.Items[SsePipeItems.Key] = pipe;
    try
    {
        await ctx.Response.StartAsync(ctx.RequestAborted);
        ssePipe = pipe;
        (outcome, contentBytes) = await RunStreamLoopAsync(ctx, pipe, request.Completion.Task);
    }
    catch (OperationCanceledException)
    {
        // Client ngắt trước khi header commit — Register/Dispatcher đã set outcome;
        // bỏ pipe khỏi Items để nhánh ghi đi đường non-stream như cũ
        ctx.Items.Remove(SsePipeItems.Key);
        ssePipe = null;
        outcome = await request.Completion.Task;
    }
}
else
{
    outcome = await request.Completion.Task;
}
```

Method mới `private static async Task<(DispatchOutcome Outcome, long ContentBytes)> RunStreamLoopAsync(HttpContext ctx, Pipe pipe, Task<DispatchOutcome> completion)` — **pseudocode algorithm** (phần body này test không xác định được, viết đúng cấu trúc sau):

```
ct = ctx.RequestAborted
readTask = pipe.Reader.ReadAsync(ct).AsTask()
contentBytes = 0
try:
  loop:
    winner = await Task.WhenAny(readTask, Task.Delay(KeepAliveIntervalMs, ct), completion)
    if winner == completion: break
    if winner == readTask:
        result = await readTask
        if result.Buffer.Length > 0:
            foreach segment in result.Buffer: await ctx.Response.Body.WriteAsync(segment, ct); contentBytes += len
            pipe.Reader.AdvanceTo(result.Buffer.End)
        else:
            pipe.Reader.AdvanceTo(result.Buffer.Start)     // empty read, re-arm
        readTask = pipe.Reader.ReadAsync(ct).AsTask()
    else:  // KeepAliveIntervalMs trôi qua chưa có data → keep-alive
        await ctx.Response.Body.WriteAsync(KeepAliveComment, ct)
catch (OperationCanceledException):
    outcome = await completion      // Register/Dispatcher LUÔN set outcome khi client abort — không deadlock
    return (outcome, contentBytes)
// Completion về: completer duy nhất
pipe.Writer.Complete()
// Drain MUST ghi nốt buffer còn lại cho client VÀ cộng contentBytes — số này phân loại #7/#8:
// Aborted mid-content có thể tới qua completion khi pipe còn byte chưa forward (dispatcher set
// Aborted sau khi handler ném) → nếu bỏ qua drain thì contentBytes==0 sai → in-band #7 thay vì im lặng #8.
while true:
    try:
        result = await pipe.Reader.ReadAsync(CancellationToken.None)
        if result.Buffer.Length > 0:
            foreach segment in result.Buffer: await ctx.Response.Body.WriteAsync(segment, CancellationToken.None); contentBytes += len
        pipe.Reader.AdvanceTo(result.Buffer.End)
        if result.IsCompleted: break
    catch (Exception):
        // IOException/OCE khi client đã chết lúc drain — KHÔNG được propagate:
        // publish H4 chưa chạy (sau outcome) → phải rơi về classification như hành vi
        // "viết response có thể nổ" đã phòng ở dòng 183. contentBytes tới đây đủ phân loại.
        break
return (await completion, contentBytes)
```

Sau classify `ClassifyOutcome` — chèn **override trước khi publish** (spec §4.3 #7):

```csharp
if (prepared.IsStream && contentBytes == 0 && outcome is DispatchOutcome.Aborted
    && !ctx.RequestAborted.IsCancellationRequested)
{
    stage = TraceStage.Finished; success = false; status = 500;
}
```

Viết block: tách `if (ssePipe is not null)` — các nhánh:

| Outcome | Việc làm |
|---|---|
| `Error e` | `WriteInBand(SseErrorEvent.Error(e.Status, e.Message, e.Type, e.Param, e.Code))` |
| `Passthrough p` | `WriteInBand(SseErrorEvent.Passthrough(p.Status, p.Body, p.RetryAfterHeader))` |
| `Retryable or Fatal` | giữ nguyên log Error cũ (dòng 213-220) + `WriteInBand(SseErrorEvent.ServerFault())` |
| `Cancelled && !RequestAborted` | giữ nguyên log Info cũ (dòng 228-235) + `WriteInBand(SseErrorEvent.Cancelled())` |
| `Aborted && contentBytes==0 && !RequestAborted` | `WriteInBand(SseErrorEvent.ServerFault())` (#7) |
| còn lại (`Handled`, `Cancelled`+abort, `Aborted` mid-content) | không ghi gì |

`else` → giữ nguyên **nguyên văn** block non-stream hiện tại (Error/Passthrough/Retryable-Fatal/Cancelled). Helper local:

```csharp
static Task WriteInBand(HttpContext ctx, byte[] evt) =>
    ctx.Response.Body.WriteAsync(evt, ctx.RequestAborted);
```

OCE khi WriteInBand (client vừa chết) được phép propagate — comment why: publish H4 đã chạy TRƯỚC write (đúng chủ đích dòng 183), giống hành vi passthrough hiện tại.

Const: `private const int KeepAliveIntervalMs = 5000;` + `private static readonly byte[] KeepAliveComment = ": keep-alive\n\n"u8.ToArray();` (dùng trong loop).

- [ ] **Step 8: Chạy 2 file integration**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProxyQueueIntegrationTests|FullyQualifiedName~ProxyRetryIntegrationTests"`
Expected: PASS toàn bộ (mới + cũ). Chưa xanh thì sửa đến khi xanh — KHÔNG nới assertion test cũ.

- [ ] **Step 9: Chạy FULL suite — gate regression non-stream (Review Focus #4)**

Run: `dotnet build router-balancing.slnx` rồi `dotnet test "router balancing test/router balancing test.csproj"`
Expected: PASS 100% — đặc biệt `ProxyControlApiTests`, `ApiMonitorIntegrationTests`, `TraceIntegrationTests`, `ClassifyOutcomeTests`.

- [ ] **Step 10: Amend spec api-monitor §3.1**

Mở `docs/superpowers/specs/2026-10-06-api-monitor-design.md` §3.1 (bảng `ApiCallRecord`, dòng `Status`) — thêm 1 câu (tiếng Việt) sau bảng: với request `stream=true` sau feature early-headers, `Status` là **logical status của outcome** (vd exhaustion 429, resolve 404, fault 500) — wire status luôn 200, không phản ánh status bytes trên wire.

- [ ] **Step 11: Commit**

```bash
git add src/RouterBalancing.Core/Engine/SsePipeItems.cs src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/Server/ProxyQueueIntegrationTests.cs" "router balancing test/Server/ProxyRetryIntegrationTests.cs" docs/superpowers/specs/2026-10-06-api-monitor-design.md
git commit -m "feat: flush stream headers at enqueue with sse keepalive and in-band errors"
```

---

### Task 5: Nút huỷ request trong `RequestDetailModal`

**Files:**
- Modify: `src/RouterBalancing.Core/Server/IProxyHost.cs` (method mới), `src/RouterBalancing.Core/Server/ProxyHost.cs` (impl — pattern `QueuedSnapshot` dòng 164-171)
- Modify: `router-balancing/Components/Shared/RequestDetailModal.razor` (inject + nút + handler)
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (5 key ×2: block EN ~dòng 49-58, block VI ~dòng 430-439)
- Test: `router balancing test/Server/ProxyHostTests.cs` (bổ sung)

**Interfaces:**
- Consumes: `IRequestCancelService` + `RequestCancelResult` (Task 3, resolve qua `app.Services`); `ApiCallState.Queued`; `IProxyHost` đã đăng ký MAUI singleton (`MauiProgram.cs:92`), `_Imports.razor` đã có `@using RouterBalancing.Core.Server`.
- Produces: `RequestCancelResult IProxyHost.CancelRequest(string id)`; i18n keys `trace.detail.cancel` / `.cancelDone` / `.cancelBusy` / `.cancelNotFound` / `.cancelNotRunning`.

- [ ] **Step 1: Test red — `ProxyHostTests`**

```csharp
[Fact]
public void CancelRequest_WhenNotStarted_ReturnsNotRunning()
{
    using var host = ... (ctor pattern trong file, KHÔNG StartAsync)
    Assert.Equal(RequestCancelResult.NotRunning, host.CancelRequest("any"));
}
```

Chạy: `dotnet test ... --filter "FullyQualifiedName~ProxyHostTests"` → FAIL (method chưa có trên interface — compile error).

- [ ] **Step 2: Implement bridge**

- `IProxyHost`: `RequestCancelResult CancelRequest(string id);` + XML doc (giống style `QueuedSnapshot`).
- `ProxyHost`: copy pattern `QueuedSnapshot`:

```csharp
public RequestCancelResult CancelRequest(string id)
{
    var app = _app;
    if (app is null) return RequestCancelResult.NotRunning;
    return app.Services.GetRequiredService<IRequestCancelService>().Cancel(id);
}
```

Comment why: queue chỉ sống trong container proxy — MAUI không có đường HTTP tới proxy, bridge qua DI đúng pattern `QueuedSnapshot`.
Chạy lại `--filter ProxyHostTests` → PASS.

- [ ] **Step 3: Nút + handler trong `RequestDetailModal.razor`**

- Thêm `@inject IProxyHost Proxy` và `@inject ToastService Toast` (namespace `router_balancing.Services` — thêm `@using router_balancing.Services` nếu chưa có trong file).
- Trong `.trace-detail-head`, ngay sau div `.trace-detail-meta`:

```razor
@if (record?.State == ApiCallState.Queued)
{
    <button type="button" class="btn btn-outline-danger" @onclick="CancelQueuedAsync">
        @L["trace.detail.cancel"]
    </button>
}
```

(`record` đã khai báo ở dòng 131 của cùng block — nằm trong scope). Class `btn btn-outline-danger` có sẵn (precedent `Combos.razor:50`).

- Handler trong `@code`:

```csharp
private void CancelQueuedAsync()
{
    if (RequestId is not { } id) return;
    var result = Proxy.CancelRequest(id);
    switch (result)
    {
        case RequestCancelResult.Cancelled:    Toast.Show(L["trace.detail.cancelDone"], ToastSeverity.Success); break;
        case RequestCancelResult.NotCancellable: Toast.Show(L["trace.detail.cancelBusy"], ToastSeverity.Error); break;
        case RequestCancelResult.NotFound:     Toast.Show(L["trace.detail.cancelNotFound"], ToastSeverity.Info); break;
        default:                               Toast.Show(L["trace.detail.cancelNotRunning"], ToastSeverity.Error); break;
    }
}
```

(`ToastSeverity` chỉ có Info/Success/Error — NotFound dùng Info, ghi why comment.) Row tự chuyển vàng khi endpoint gốc publish H4 Canceled → panel re-render → nút tự biến — không cần force refresh.

- [ ] **Step 4: i18n — 5 key ×2 (EN block + VI block `Translations.cs`)**

| Key | EN | VI |
|---|---|---|
| `trace.detail.cancel` | Cancel request | Huỷ request |
| `trace.detail.cancelDone` | Request cancelled. | Đã huỷ request. |
| `trace.detail.cancelBusy` | Request is already being served. | Request đang được xử lý. |
| `trace.detail.cancelNotFound` | Request no longer exists. | Request không còn tồn tại. |
| `trace.detail.cancelNotRunning` | Proxy is not running. | Proxy chưa chạy. |

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~TranslationParityTests"` → PASS (không thiếu key hai bên).

- [ ] **Step 5: Build + full suite**

Run: `dotnet build router-balancing.slnx` && `dotnet test "router balancing test/router balancing test.csproj"` → xanh toàn bộ.

- [ ] **Step 6: Checklist manual (không có UI test framework — spec §8)**

Chạy app MAUI (`dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`), checklist:
- [ ] Mở popup chi tiết request đang `queued` → thấy nút "Huỷ request"; request đã Done/Error → KHÔNG thấy nút.
- [ ] Click huỷ → toast xanh + request gốc (opencode/HTTP client) nhận in-band `request_cancelled`.
- [ ] Click khi vừa được serve (giữ slot release đúng lúc) → toast đỏ `cancelBusy`.
- [ ] Proxy đang stop → row cũ còn trong store → click → toast đỏ `cancelNotRunning`.
- [ ] Test E2E: opencode thật + capacity 1 + request `max` phía trước → không `HeaderTimeoutError` sau >5s (có keep-alive), huỷ qua popup hoạt động.

- [ ] **Step 7: Commit**

```bash
git add src/RouterBalancing.Core/Server/IProxyHost.cs src/RouterBalancing.Core/Server/ProxyHost.cs router-balancing/Components/Shared/RequestDetailModal.razor src/RouterBalancing.Core/Localization/Translations.cs "router balancing test/Server/ProxyHostTests.cs"
git commit -m "feat: add cancel button to request detail modal"
```
