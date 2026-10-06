# /v1/responses Endpoint Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thêm endpoint `POST /v1/responses` (OpenAI Responses API) passthrough vào pipeline queue-first hiện có, telemetry (trace/monitor/journal) nhìn thấy traffic responses.

**Architecture:** Seam `IProxyProtocol` tách 3 điểm protocol-specific (validate / upstream path+rewrite / tee-parse) ra khỏi `ProxyRequestHandler` (rename từ `ChatCompletionsHandler`) và endpoint runner dùng chung `RunQueueFirstAsync`. Chat = hành vi không đổi (regression gate); Responses = protocol mới parse usage/TTFT từ event `response.*`.

**Tech Stack:** .NET 10, ASP.NET Core minimal API (Kestrel in-process), System.Text.Json, xUnit. Không thêm NuGet.

**Spec:** `docs/superpowers/specs/2026-10-07-v1-responses-design.md` (đọc trước khi làm mọi task).

## Global Constraints

- Chat behavior **không đổi ở mọi điểm** — toàn bộ test chat hiện có phải giữ xanh sau mọi task.
- Response trả client **byte-identical** với upstream (2xx tee, non-2xx buffer/passthrough như chat).
- Body cap 64*1024 byte + marker `[truncated]` 1 lần (reuse `ApiMonitorStore.DecodeCapped` semantics).
- Parse fail-open: JSON/event hỏng → telemetry thiếu, request không nổ.
- Provider type Anthropic: forward thẳng path `/v1/responses`, không convert (spec §2 Non-goals).
- Không NuGet mới; `Nullable=enable`; comment VI cho logic mới; commit message English conventional.
- Gates mỗi task: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --no-incremental` 0W/0E; `dotnet test "router balancing test/router balancing test.csproj"` xanh (known: 2 `SingleInstanceGuardTests` fail khi app đang chạy; flake temp-DB → rerun). Task UI thêm gate `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` 0W/0E.

## Review Focus

- Upstream 2xx không kèm `usage` (gateway không chuẩn) → request vẫn pass, tokens `—`, log Debug correlate id (Task 3 test).
- SSE event JSON hỏng giữa dòng → tee không nổ, client nhận đủ byte (Task 3 test).
- Stream chỉ-reasoning (không `output_text.delta`) → TTFT null, usage vẫn đọc từ terminal event (Task 3 test).
- Non-2xx trên responses (429/401/404) → phân loại retry/fatal y hệt chat, `monitor.RecordError` ghi cho mọi nhánh (Task 4 test).
- Client gọi `/v1/responses` validate fail → 400 OpenAI-shape, không row monitor/trace (Task 4 test).

---

### Task 1: Protocol seam + generalize handler (pure refactor, chat không đổi)

**Files:**
- Create: `src/RouterBalancing.Core/Engine/IProxyProtocol.cs`, `src/RouterBalancing.Core/Engine/ChatCompletionsProtocol.cs`, `src/RouterBalancing.Core/Engine/ProxyProtocols.cs`
- Rename+Modify: `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs` → `ProxyRequestHandler.cs`
- Modify: `src/RouterBalancing.Core/Engine/IUpstreamClient.cs`, `src/RouterBalancing.Core/Engine/OpenAiUpstreamClient.cs`, `src/RouterBalancing.Core/Engine/ProxyRequest.cs`, `src/RouterBalancing.Core/Engine/DispatcherLoop.cs`, `src/RouterBalancing.Core/Server/ProxyApp.cs` (DI:90, endpoint lambda:124-351 gọi qua protocol), `router-balancing/MauiProgram.cs` nếu có reference
- Test: rename `router balancing test/Engine/ChatCompletionsHandlerTests.cs` → `ProxyRequestHandlerTests.cs` + update fakes `IUpstreamClient`

**Interfaces:**
- Produces:
```csharp
public enum ProxyEndpoint { Chat = 0, Responses = 1 }

public interface IProxyProtocol
{
    string UpstreamPath { get; } // "/v1/chat/completions"
    ProxyValidationResult Validate(byte[] body);
    (byte[] Body, bool ExpectsUsage) PrepareUpstreamBody(byte[] body, string realModelId, ProviderType providerType);
    Task<UsageCapture.TeeResult> TeeAsync(HttpContent source, Stream dest, CancellationToken ct);
}

public readonly record struct ProxyValidationResult(
    bool IsValid, string? ModelId, bool IsStream, string? ErrorMessage, string? ErrorParam);

public static class ProxyProtocols
{
    public static IProxyProtocol Chat { get; }
    public static IProxyProtocol Responses { get; } // Task 2 mới có body thật — tạm throw NotImplementedException
    public static IProxyProtocol For(ProxyEndpoint endpoint);
}

// ProxyRequest: thêm optional ctor param cuối, property read-only
public ProxyEndpoint Endpoint { get; } // default Chat — call site cũ không đổi

// ProxyRequestHandler (rename từ ChatCompletionsHandler):
public Task<PreparedProxyRequest?> PrepareAsync(HttpContext ctx, IProxyProtocol protocol); // 400 ghi từ ErrorMessage/ErrorParam
public Task<DispatchOutcome> ForwardAsync(HttpContext ctx, Provider provider, Model model,
    byte[] body, long accountId, IProxyProtocol protocol, CancellationToken ct);
public sealed record PreparedProxyRequest(string ModelId, byte[] Body, bool IsStream);
internal static Task WriteErrorAsync(...); // giữ nguyên, static trên class mới

// IUpstreamClient: PostChatCompletionAsync → 
Task<HttpResponseMessage> PostAsync(Provider provider, string apiKey, string path, byte[] body, CancellationToken ct);
```
- `ChatCompletionsProtocol`: `Validate` bọc `ChatRequestValidator` và map `ValidationFailure` → message/param **đúng chuỗi hiện ở ProxyRequestHandler.PrepareAsync:51-58** (move switch vào protocol, không đổi text). `PrepareUpstreamBody` = `ChatBody.WithModel` + `UsageCapture.WithIncludeUsage` *(plan refine spec §3.1: thêm param `ProviderType` vì `WithIncludeUsage` cần nó — spec giữ signature 2 tham số là thiếu)*. `TeeAsync` delegate `UsageCapture.TeeAsync`.
- DispatcherLoop: call site (~194-205) truyền `ProxyProtocols.For(request.Endpoint)`.

- [ ] **Step 1:** Tạo `IProxyProtocol` + `ProxyValidationResult` + `ProxyEndpoint` + `ProxyProtocols` (Responses throw `NotImplementedException` — Task 2 thay).
- [ ] **Step 2:** Rename handler, generalize `PrepareAsync`/`ForwardAsync` theo Interfaces trên; body ForwardAsync giữ nguyên từng dòng, chỉ thay 3 điểm: path (`upstream.PostAsync(provider, key, protocol.UpstreamPath, ...)`), rewrite (`protocol.PrepareUpstreamBody`), tee (`protocol.TeeAsync`).
- [ ] **Step 3:** Generalize `IUpstreamClient`/`OpenAiUpstreamClient`; update mọi fake trong test.
- [ ] **Step 4:** `ProxyApp`: DI đăng ký `ProxyRequestHandler`; endpoint lambda chat gọi `PrepareAsync(ctx, ProxyProtocols.Chat)` + `new ProxyRequest(..., ProxyEndpoint.Chat)`; các `ChatCompletionsHandler.WriteErrorAsync` → `ProxyRequestHandler.WriteErrorAsync`.
- [ ] **Step 5:** Chạy full suite. Expected: **xanh toàn bộ** (chỉ sửa compile, không đổi assertion). Đây là gate của task — refactor không hành vi mới.
- [ ] **Step 6:** Commit `refactor: extract proxy protocol seam behind shared forward handler`

### Task 2: ResponsesProtocol — validate + prepare body

**Files:**
- Create: `src/RouterBalancing.Core/Engine/ResponsesProtocol.cs`
- Modify: `ProxyProtocols.cs` (bỏ throw)
- Test: `router balancing test/Engine/ResponsesProtocolTests.cs`

**Interfaces:**
- Consumes: `IProxyProtocol`, `ProxyValidationResult` (Task 1), `ChatBody.WithModel`.
- Produces: `ResponsesProtocol : IProxyProtocol` (`UpstreamPath = "/v1/responses"`); singleton qua `ProxyProtocols.Responses`.

- [ ] **Step 1 (RED):** Viết tests trong `ResponsesProtocolTests`:
```csharp
Validate_WhenModelMissing_Returns400Message   // ErrorMessage "Missing required parameter: 'model'." ErrorParam "model"
Validate_WhenInputMissing_Returns400ForInput  // "Missing required parameter: 'input'." / "input"
Validate_WhenInputNumber_Returns400ForInput   // input: 42 → fail
Validate_WhenInputString_AcceptsAndReadsStreamLiteralTrueOnly // "stream":true → IsStream; "1"/"TRUE" → false
Validate_WhenInputArrayNonEmpty_Accepts       // input:[{...}] → valid, opaque
Validate_WhenInvalidJson_ReturnsInvalidJson   // ErrorMessage "Invalid JSON body", ErrorParam null
PrepareUpstreamBody_SwapsModelInjectsNothingElse // output parse được model mới; KHÔNG có "stream_options" key
```
- [ ] **Step 2:** Run `dotnet test ... --filter FullyQualifiedName~ResponsesProtocolTests` → FAIL (class chưa tồn tại).
- [ ] **Step 3:** Implement `ResponsesProtocol.Validate` theo spec §4.1 (root object → model string non-empty → input là String **hoặc** Array; stream literal true). `PrepareUpstreamBody` = `ChatBody.WithModel(body, realModelId)` trả `(rewritten, false)`.
- [ ] **Step 4:** Run filter → PASS. Full suite xanh. Commit `feat: add responses protocol validation and body preparation`

### Task 3: ResponsesProtocol.TeeAsync — parse usage/TTFT non-stream + SSE

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ResponsesProtocol.cs`
- Test: `router balancing test/Engine/ResponsesProtocolTests.cs` (thêm)

**Interfaces:**
- Consumes: `UsageCapture.TeeResult`/`Usage` (shape cũ), `ApiMonitorStore.DecodeCapped`.
- Produces: `Task<UsageCapture.TeeResult> TeeAsync(HttpContent source, Stream dest, CancellationToken ct)` — copy toàn bộ byte source→dest không đổi, đồng thời parse.

- [ ] **Step 1 (RED):** Thêm tests:
```csharp
Tee_NonStreamJson_ExtractsUsageAndResponseBody   // {"usage":{"input_tokens":11,"output_tokens":7}} → Usage(11,7); ResponseBody = raw (capped); dest byte-identical
Tee_Stream_UsageFromCompletedEvent               // event: response.completed + data response.usage → Usage; FirstTokenAt not null khi có response.output_text.delta trước đó
Tee_Stream_FirstOutputTextDeltaSetsFirstToken    // FirstTokenAt == time delta đầu (ManualTimeProvider/FakeTimeProvider nếu có pattern sẵn ở UsageCaptureTests — theo pattern test tee chat hiện có)
Tee_Stream_MalformedEventDataJson_FailsOpen      // data: {sai → không nổ, dest đủ byte, Usage null
Tee_Stream_ReasoningOnly_NoTtftButUsage          // không output_text.delta → FirstTokenAt null; completed usage vẫn đọc
Tee_Stream_IncompleteEvent_UsageRead             // response.incomplete có usage → Usage not null
Tee_NonStreamJson_NoUsage_FailsOpen              // usage thiếu → Usage null, không nổ (Review Focus #1)
Tee_Stream_BodyCappedAt64KWithMarker             // ResponseBody ≤ 64K+marker, 1 marker
```
- [ ] **Step 2:** Run filter ResponsesProtocolTests → FAIL các test mới.
- [ ] **Step 3:** Implement: đọc stream theo line SSE (`event:`/`data:`, bỏ qua comment/keep-alive) — state machine nhỏ: `firstTokenAt` set tại `response.output_text.delta` (hoặc `.done` nếu delta chưa thấy); terminal event (`response.completed|incomplete|failed`) parse `response.usage.input_tokens/output_tokens`. Non-stream (content-type không `text/event-stream` hoặc không có `event:` line đầu): parse JSON root `usage`. Capture raw bytes để tee `ResponseBody` qua `ApiMonitorStore.DecodeCapped`. Mọi parse trong try/catch fail-open (log qua tham số `ILogService`? Không — protocol không có DI; nuốt + trả null, pattern UsageCapture hiện tại).
- [ ] **Step 4:** Run filter → PASS; full suite xanh. Commit `feat: parse responses usage and first-token in protocol tee`

### Task 4: Wire endpoint /v1/responses qua runner dùng chung

**Files:**
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs` (124-351)
- Test: `router balancing test/Server/ResponsesEndpointIntegrationTests.cs` (pattern `ApiMonitorIntegrationTests`)

**Interfaces:**
- Consumes: `ProxyProtocols.Chat/Responses`, `ProxyRequestHandler`, `ProxyRequest(..., endpoint)`.
- Produces: `internal static Task RunQueueFirstAsync(HttpContext ctx, IRequestQueue queue, IExecutionList executions, ProxyRequestHandler handler, ILogService log, ITraceFeed trace, IApiMonitorStore monitor, IProxyProtocol protocol, ProxyEndpoint endpoint)` — body là **pure move** của lambda chat hiện tại (id → prepare → priority → pipe → enqueue → H1+Received → abort-register → stream loop → outcome → H4 → ghi response).

- [ ] **Step 1:** Extract runner (pure move, không sửa logic); chat endpoint = `MapPost("/v1/chat/completions", (ctx, deps...) => RunQueueFirstAsync(ctx, ..., ProxyProtocols.Chat, ProxyEndpoint.Chat))`. Chạy full suite → xanh (chat regression gate, không sửa test).
- [ ] **Step 2 (RED):** Tests integration:
```csharp
ResponsesNonStream_TwoXx_PassesThroughBytesAndRecordsMonitor  // fake upstream trả /v1/responses JSON+usage → client nhận byte-identical; fake CAPTURE path == "/v1/responses"; monitor record tokens 11/7, Done
ResponsesStream_TwoXx_TtftAndUsageReachMonitor                // SSE events → row có TTFT + tokens
ResponsesValidateFail_Writes400AndNoQueueOrMonitorRow         // body thiếu input → 400 param 'input'; Snapshot() rỗng; trace không có Received
ResponsesUpstream429ThenOk_RetryWalk_EndsDone                 // như pattern Provider429ThenOk của chat
ResponsesErrorNon2xx_RecordsErrorOnMonitor                    // 500 upstream → RecordError status 500 (Review Focus #4)
```
- [ ] **Step 3:** Run filter → FAIL (endpoint chưa map).
- [ ] **Step 4:** Thêm `MapPost("/v1/responses", ... => RunQueueFirstAsync(..., ProxyProtocols.Responses, ProxyEndpoint.Responses))`.
- [ ] **Step 5:** Run filter → PASS; full suite xanh. Commit `feat: expose v1 responses endpoint through queue-first pipeline`

### Task 5: UI — INPUT node hiện cả 2 endpoint + final gates

**Files:**
- Modify: `router-balancing/Components/Shared/RequestTrace.razor` (~315-318)

**Interfaces:** Consumes: không (markup tĩnh). Produces: không.

- [ ] **Step 1:** Đổi label INPUT node từ 1 dòng `POST /v1/chat/completions` thành 2 dòng `POST /v1/chat/completions` / `POST /v1/responses` (font size như hiện tại; node cao thêm ~1 dòng — connector spine đã nằm dưới đáy box nên không cần chỉnh coords; kiểm `CanvasHeight` 415 vẫn đủ vì INPUT ở top 8, các node dưới không đổi).
- [ ] **Step 2:** Gate: app build `-f net10.0-windows10.0.19041.0` 0W/0E + Core build + full suite xanh.
- [ ] **Step 3:** Commit `feat: show both proxy endpoints in live trace input node`
- [ ] **Step 4:** Chạy 3 gate lần cuối + checklist thủ công cho user (append vào report): gọi thử `/v1/responses` bằng curl/SDK → row hiện trên Live Trace + API Monitor với tokens; chat không đổi hành vi.

---

## Self-review notes (đã chạy)

- Spec coverage: §3.1→Task1, §4.1/4.2→Task2, §4.3/4.4→Task3, §3.3/§5 hooks→Task4, §5 UI→Task5, §7 tests→Tasks 2-4, §8 edges→Task3 tests. Non-goals không có task (đúng).
- Type consistency: `ProxyValidationResult`/`PreparedProxyRequest`/`PostAsync`/`ProxyEndpoint` thống nhất Task 1↔4.
- Review Focus: 5 dòng đều có test pin ở Task 3/4.
