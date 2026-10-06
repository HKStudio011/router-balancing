# Spec — Endpoint `/v1/responses` (OpenAI Responses API)

Ngày: 2026-10-07. Trạng thái: đã duyệt conversational (chọn hướng A — passthrough + seam protocol).
Thay thế dòng "/v1/responses" trong design 2026-09-25 §178 (phần convert Anthropic hoãn vô thời hạn — xem Non-goals).

## 1. Mục tiêu

App hiện chỉ nhận `POST /v1/chat/completions`. Client dùng SDK/agent đời mới (Codex, Responses API) gọi `POST /v1/responses` sẽ 404. Mục tiêu: proxy **accept + forward + quan sát được** traffic `/v1/responses`, dùng nguyên pipeline hiện có (queue, priority, retry/failover, cancel, keep-alive, trace, monitor, journal, usage sink).

Thành công: một request `/v1/responses` (stream và non-stream) đi qua balance y hệt chat — xuất hiện trên Live Trace, API Monitor (tokens/latency/TTFT/prompt+response body), journal usage, và response trả client **byte-identical** với upstream.

## 2. Phạm vi

### In scope
- Endpoint `POST /v1/responses` + validate tối thiểu (model + input).
- Forward passthrough tới `{BaseUrl}/v1/responses` trên provider OpenAI-compatible.
- Parse protocol Responses để nuôi telemetry: usage (non-stream JSON + event `response.completed`), TTFT (delta text đầu tiên), response body tee'd.
- Tách thân endpoint chat (lambda ~150 dòng trong `ProxyApp.ConfigurePipeline`) thành **runner dùng chung** cho cả 2 endpoint.
- Seam `IProxyProtocol` (validate / upstream path / rewrite body / tee-parse); generalize handler.
- UI: INPUT node của Live Trace hiển thị cả 2 path.

### Non-goals
- Convert Responses → ChatCompletions → Anthropic Messages (translator chưa tồn tại; provider type Anthropic nhận path `/v1/responses` nguyên ven → upstream 404 → fatal cấp Provider, failover walk như mọi 404).
- Cột/bộ lọc "endpoint" trong API Monitor (model đủ phân biệt trong thực tế dùng).
- Đếm `cached_tokens` riêng (bỏ qua, chỉ input/output).
- Background mode (`background: true`), `store`, tool hosting của Responses API — body là opaque, forward nguyên ven.

## 3. Kiến trúc

### 3.1 Seam protocol

Interface mới trong `RouterBalancing.Core.Engine`:

```csharp
public interface IProxyProtocol
{
    /// <summary>Path upstream, nối vào BaseUrl đã canonicalize.</summary>
    string UpstreamPath { get; }

    /// <summary>Validate body thô từ client; lỗi → 400 OpenAI-style tại endpoint.</summary>
    ProxyValidationResult Validate(byte[] body);

    /// <summary>Đổi alias→model id thật + các rewrite bắt buộc trước khi forward.</summary>
    byte[] PrepareUpstreamBody(byte[] body, string realModelId);

    /// <summary>Tee stream/JSON upstream vào dest, trích usage + first-token + body (cap 64KB).</summary>
    Task<UsageCapture.TeeResult> TeeAsync(HttpContent source, Stream dest, CancellationToken ct);
}
```

- `ChatCompletionsProtocol`: giữ nguyên 100% hành vi hiện tại (`/v1/chat/completions`, `ChatRequestValidator`, `ChatBody.WithModel` + `UsageCapture.WithIncludeUsage`, `UsageCapture.TeeAsync`).
- `ResponsesProtocol`: mục 4.
- `ProxyValidationResult`: shape chung cho runner — `{ bool IsValid; string? ModelId; bool IsStream; string? ErrorMessage; string? ErrorParam; }` (runner ghi 400 thẳng từ message/param, không switch qua enum riêng của protocol). Kết quả tee dùng lại `UsageCapture.TeeResult`/`Usage` nguyên trạng (shape đã protocol-neutral).
- Enum `ProxyEndpoint { Chat, Responses }`; thêm property `Endpoint` (init-only) vào `ProxyRequest`. DispatcherLoop truyền `ProxyProtocols.For(request.Endpoint)` vào handler — **logic walk/retry không đổi**.

### 3.2 Handler generalize

`ChatCompletionsHandler` → **rename `ProxyRequestHandler`** (cơ khí, compiler chỉ dẫn toàn bộ call site + test):
- `PrepareAsync(HttpContext ctx, IProxyProtocol protocol)`.
- `ForwardAsync(...)` nhận protocol từ `request.Endpoint` (DispatcherLoop gọi, không đổi chữ ký public khác).
- Toàn bộ nhánh outcome (Retryable/Fatal/Passthrough/Handled), monitor hooks, journal, sink giữ nguyên — chỉ 3 điểm protocol-specific (path, rewrite, tee) tách qua interface.

`IUpstreamClient`: `PostChatCompletionAsync` → `PostAsync(Provider, string apiKey, string path, byte[] body, CancellationToken)`; header theo `ProviderType` vẫn qua `ProviderRequestFactory` (Anthropic headers giữ).

### 3.3 Endpoint runner dùng chung

Thân lambda chat hiện tại (sinh id → prepare → priority → pipe → enqueue → H1+Received → abort-register → stream loop/await → outcome → H4) tách thành method internal `ProxyEndpoints.RunQueueFirstAsync(ctx, queue, executions, handler, trace, monitor, log, protocol)`. Hai endpoint chỉ khác nhau 1 tham số:

```csharp
app.MapPost("/v1/chat/completions", (ctx, ...) => RunQueueFirstAsync(ctx, ..., ChatCompletionsProtocol.Instance));
app.MapPost("/v1/responses",        (ctx, ...) => RunQueueFirstAsync(ctx, ..., ResponsesProtocol.Instance));
```

Mọi hành vi đã test của chat endpoint (X-Request-Id, keep-alive, early-headers, cancel, H1/H4, demote Highest...) **không được đổi** — runner là pure-extract, chat tests là regression gate.

## 4. Protocol Responses — chi tiết parse

### 4.1 Validate (mirror rule chat, message OpenAI-style)
| Điều kiện | Kết quả |
|---|---|
| JSON hỏng | 400 `Invalid JSON body` |
| Thiếu/hỗng `model` (string) | 400 `Missing required parameter: 'model'.` param `model` |
| Thiếu `input` HOẶC `input` không phải string cũng không phải array | 400 `Missing required parameter: 'input'.` param `input` |
| `stream` | bool, default false — điều khiển pipe/keep-alive như chat |
| Còn lại (tools, reasoning, max_output_tokens...) | opaque, không kiểm |

### 4.2 PrepareUpstreamBody
Chỉ `ChatBody.WithModel` (alias→model id thật). **Không** inject `stream_options.include_usage` — Responses API luôn kèm usage trong event terminal.

### 4.3 Tee non-stream (JSON)
- `usage.input_tokens → PromptTokens`, `usage.output_tokens → CompletionTokens`; thiếu usage → null (fail-open).
- `ResponseBody`: raw bytes tee'd, cap 64KB + marker (dùng `ApiMonitorStore.DecodeCapped` semantics như chat).

### 4.4 Tee stream (SSE events `event:` + `data:`)
- Parse dòng `event: response.*`; chỉ parse JSON của event cần thiết (fail-open: JSON hỏng → bỏ event, không nổ request).
- **FirstTokenAt**: timestamp đầu tiên thấy `response.output_text.delta` (event `response.output_text.done` cũng tính nếu delta chưa thấy). Stream chỉ-reasoning không có delta text → TTFT null (chấp nhận, row hiện `—`).
- **Usage**: từ object `response.usage` trong event `response.completed`; best-effort đọc cả `response.incomplete`/`response.failed` nếu có `usage`.
- **ResponseBody**: tee toàn bộ byte sang dest (client nhận nguyên ven) + capture capped như chat.

### 4.5 Error path
Upstream non-2xx: reuse nguyên `RetryClassifier`/`ClassifyFatal`/`monitor.RecordError` (OpenAI-compatible error shape). Lỗi giữa stream: giữ nguyên hành vi api-monitor §7 đã amend (RecordError(0) + rethrow, row vàng khớp circle).

## 5. Trace / Monitor / Journal

- Hooks H1/RecordResponse/RecordError/Received/H4: y hệt chat, cùng store/feed — traffic responses tự xuất hiện trong panel + tiles (TodayTokens tính `input+output`).
- `TraceEvent.Model`: chuỗi `model` gốc (alias/combo) như chat.
- UI `RequestTrace.razor`: INPUT node label → 2 dòng `POST /v1/chat/completions` / `POST /v1/responses` (node cao thêm ~14px; connector spine đã nằm dưới đáy box nên không vỡ layout).

## 6. Xử lý lỗi & hợp đồng cũ

- Chat không đổi hành vi ở mọi điểm (đây là ràng buộc test, không phải kỳ vọng chung chung).
- `usageSink`/`LogRequestUsage`: chỉ ghi khi usage không-null (như chat). Chat giữ nguyên điều kiện log Debug `expectsUsage`; responses log Debug khi 2xx hoàn tất mà không trích được usage (không có điều kiện include_usage vì không inject).
- Provider type Anthropic + endpoint responses: forward thẳng, không special-case (mục 2 Non-goals).

## 7. Test

Unit (Core):
- `ResponsesProtocolTests`: validate (thiếu model / thiếu input / input string / input array / stream default), PrepareUpstreamBody đổi model không inject gì thêm.
- `ResponsesTeeTests`: non-stream usage + body cap; stream `response.completed` usage; TTFT từ `output_text.delta` đầu; event JSON hỏng không nổ; reasoning-only → TTFT null; `response.incomplete` có usage.
- Regression: `ChatRequestValidator`/`UsageCapture` tests giữ nguyên, xanh.

Integration (TestServer, pattern `ApiMonitorIntegrationTests`):
- `POST /v1/responses` 2xx non-stream: byte-identical + monitor record tokens + journal + H1/H4.
- Stream: pipe + keep-alive hoạt động, usage/TTFT vào monitor.
- Upstream 429→2xx: retry walk như chat, row Done.
- Validate fail: 400 OpenAI-shape, không enqueue, không row monitor.

Gates: Core build 0W/0E, app build 0W/0E, full suite xanh (2 mutex known).

## 8. Edge đã biết (chấp nhận, ghi để không điều tra lại)

- Upstream không phải OpenAI chuẩn, trả Responses-shape khác → parse fail-open, telemetry thiếu, request vẫn pass.
- `input` dạng file/ref content: không hiểu nội dung, prompt body trong monitor là JSON gốc (đã cap).
- reasoning-only stream: không có TTFT.
- 2 request cùng model 1 từ chat 1 từ responses: monitor không phân biệt endpoint (Non-goal).
