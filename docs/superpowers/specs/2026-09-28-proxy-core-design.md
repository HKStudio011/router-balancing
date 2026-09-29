# Spec: Proxy Core (Phase 3, Slice 3A)

- **Ngày:** 2026-09-28
- **Trạng thái:** Chờ user review
- **Slice:** 3A trong roadmap 5 slice Phase 3 (3A → 3B → 3C → 3D → 3E)
- **Điều kiện đầu:** Phase 2 hoàn tất + batch cleanup merge tại `ff64ebd` (master, 183/0)

## 1. Mục tiêu & phạm vi

Xây dựng đường request cốt lõi của proxy LLM: `POST /v1/chat/completions` nhận request dạng OpenAI, validate, tìm provider tương ứng, **passthrough streaming nguyên văn** tới upstream OpenAI-compatible, trả kết quả về client và ghi log.

### 1.1 Trong phạm vi (3A)

- Endpoint `POST /v1/chat/completions` (endpoint `GET /v1/models` + `GET /health` đã có từ trước, không đổi).
- Validate body theo rule tối thiểu (§3) — chỉ kiểm tra presence/type, không interpret nội dung.
- Resolve `model` → provider (chỉ model id chính xác, §2.3).
- Auth upstream bằng key của account enabled đầu tiên (reuse `ProviderKeyResolver` — §5.1).
- Passthrough streaming: status + `Content-Type` + body (JSON lẫn SSE) chuyển nguyên trạng.
- Error contract dạng JSON OpenAI (§4).
- Logging qua `ILogService` (§6).

### 1.2 Ngoài phạm vi (rõ ràng — slice sau)

| Việc | Slice |
|---|---|
| Chọn provider giữa nhiều provider trùng model (selection, weighting, queue, `MaxConcurrent`) | 3B |
| Retry / circuit breaker / xử lý 429 chủ động | 3C |
| Chọn account theo usage/quota, cập nhật `TokensUsed`/`RequestsUsed` | 3D |
| Dịch Anthropic Messages ↔ OpenAI Chat, streaming dịch 2 chiều, `/v1/responses` | 3E |
| Model combo (nested/combo list) → chưa map được về 1 model id | 3B |

### 1.3 Các quyết định đã chốt (user gate)

1. `model` = **chỉ id chính xác**; unknown → 404 OpenAI JSON; combo/nested → 3B.
2. 3A **OpenAI-only**: model của provider `Anthropic` → 503 `server_error` + log Warn; dịch thuật → 3E.
3. Verify: **Unit + Integration (in-proc, mock upstream) + e2e curl script local** (§7).
4. Handoff Phase 2 follow-ups: batch cleanup đã hoàn tất trước khi spec này viết.

## 2. Architecture

### 2.1 Component mới (`src/RouterBalancing.Core/Engine/`)

| Type | Responsibility | Phụ thuộc |
|---|---|---|
| `ChatRequestValidator` (static) | Parse `JsonDocument` từ body thô + rule §3 → `ValidationResult` typed (thành công / failure kèm status + reason enum — payload do handler sinh, xem §4) | không |
| `IModelResolver` + `ModelResolver` | `model id` → `ResolvedModel(provider, model)` hoặc `ResolveFailure` phân loại (`NotFound` / `AnthropicNotSupported`); query EF + `Include(Accounts)` | `IDbContextFactory` |
| `IUpstreamClient` + `OpenAiUpstreamClient` | Gửi POST body thô tới `{BaseUrl}/v1/chat/completions`, nhận **key plaintext qua tham số** → trả `HttpResponseMessage` (đã đọc header, body còn stream) | `IHttpClientFactory` (client `"upstream"`) |
| `ChatCompletionsHandler` | Orchestrator: validate → resolve → giải mã key → upstream → copy response → log | các type trên + `ISecretProtector` + `ILogService` |
| `ProxyApp` (static, `Server/`) | **2 method** (tách vì middleware/map phải chạy sau `builder.Build()`, DI đóng băng sau Build): `ConfigureServices(WebApplicationBuilder, ISecretProtector)` đăng ký DI; `ConfigurePipeline(WebApplication)` gắn middleware + map route. **Seam testability** — `ProxyHost` và integration test dùng chung | — |

Mỗi unit test được độc lập; handler nhận `HttpContext` qua tham số (singleton DI được, không giữ state per-request).

### 2.2 Thay đổi ở `ProxyHost`

- Constructor nhận thêm `ISecretProtector` (đăng ký singleton trong `MauiProgram` sẵn — dòng 55).
- `StartAsync`: sau khi tạo builder + đăng ký 3 singleton hiện tại, gọi `ProxyApp.ConfigureServices(builder, protector)` → `Build()` → `ProxyApp.ConfigurePipeline(app)` thay cho `UseMiddleware` + `MapEndpoints` nội tuyến. Logic `/health`, `/v1/models` chuyển vào `ProxyApp.ConfigurePipeline` **nguyên văn** (không đổi hành vi).
- DI container của `ProxyHost` **tách biệt** với MauiProgram — vì vậy `ProxyApp.Configure` phải tự đăng ký mọi thứ engine cần (không thừa hưởng từ app container).

### 2.3 Resolve model

```
ModelResolver.ResolveAsync(modelId, ct):
  m = db.Models.Where(x => x.ModelId == modelId && x.Enabled
                           && x.Provider != null && x.Provider.Enabled)
       .Include(x => x.Provider.Accounts)
       .OrderBy(x => x.Id)
       .FirstOrDefaultAsync()
  m == null                → NotFound           → 404 (§4)
  m.Provider.Type Anthropic → AnthropicNotSupported → 503 (§4)
  else                     → ResolvedModel(provider, model)
```

- Trùng `ModelId` nhiều provider: lấy `Id` nhỏ nhất — **chọn đúng provider là việc 3B** (3A chỉ cần 1 đường đi xác định).
- `Include(Accounts)` bắt buộc: `ProviderKeyResolver.ResolveFirstEnabledKey` cần nav `Accounts`.

## 3. Routing & validation

- Method/path: `POST /v1/chat/completions` — map qua `app.MapPost(...)` trong `ProxyApp.Configure`.
- Auth client: **không đổi** — `ApiKeyMiddleware` xử lý trước (Bearer hoặc `X-API-Key`, 401 `invalid_api_key` đã có).
- Body: đọc **raw bytes** một lần; parse bằng `JsonDocument` để validate; **forward nguyên bytes gốc** (không serialize lại — tránh đổi key order/whitespace vô ích).

| # | Rule | Sai | Kết quả |
|---|---|---|---|
| V1 | Body parse được JSON | JSON hỏng | 400 `Invalid JSON body` (§4) |
| V2 | Có trường `model`, là string, non-empty | thiếu / sai kiểu / rỗng | 400, `param: "model"` |
| V3 | Có trường `messages`, là array, không rỗng | thiếu / sai kiểu / rỗng | 400, `param: "messages"` |

- Mọi trường khác (`stream`, `temperature`, `max_tokens`, …) **không validate, không sửa** — passthrough nguyên văn. `stream: false` chạy cùng 1 đường (upstream tự trả JSON thường).
- Content-Type yêu cầu `application/json` không bắt buộc kiểm — parse JSON là đủ (client lười đặt header vẫn hoạt động).

## 4. Error contract

Mọi lỗi **từ proxy** trả `Content-Type: application/json`, shape:

```json
{ "error": { "message": "...", "type": "...", "param": "...", "code": "..." } }
```

| Tình huống | Status | message (EN, client-facing) | type | param | code | Log |
|---|---|---|---|---|---|---|
| JSON hỏng (V1) | 400 | `Invalid JSON body` | `invalid_request_error` | `null` | `null` | Warn |
| Thiếu/sai `model` (V2) | 400 | `Missing required parameter: 'model'.` | `invalid_request_error` | `model` | `null` | Warn |
| Thiếu/sai `messages` (V3) | 400 | `Missing required parameter: 'messages'.` | `invalid_request_error` | `messages` | `null` | Warn |
| Model unknown / đã tắt / provider đã tắt | 404 | `The model '{model}' does not exist` | `invalid_request_error` | `model` | `model_not_found` | Warn |
| Provider là Anthropic | 503 | `The model '{model}' is not supported yet` | `server_error` | `null` | `null` | Warn |
| Provider không có account enabled nào (key = null) | 503 | `No enabled API key for provider '{providerName}'` | `server_error` | `null` | `null` | Warn |
| Lỗi mạng / không kết nối được upstream | 502 | `Upstream provider request failed` | `server_error` | `null` | `null` | Error (kèm exception message) |

- **Lỗi từ upstream trả về (4xx/5xx của provider)**: passthrough **nguyên status + body** — proxy không dịch, không wrap. (Retry 429 là việc 3C.)
- Payload lỗi đồng nhất kiểu với `ApiKeyMiddleware` đã có (401 giữ nguyên, không đổi).
- **Nguồn sinh payload (pin để không có 2 nơi viết JSON lỗi):** validator và resolver chỉ trả về **failure typed** (status + reason enum + dữ liệu đi kèm như model/provider); riêng `ChatCompletionsHandler` có helper nội bộ map reason → payload JSON theo bảng trên.
- Message lỗi dùng **tiếng Anh** (client-facing API, theo precedent `Invalid or missing API key`); log dùng tiếng Việt theo convention codebase.

## 5. Upstream call & streaming

### 5.1 Auth & URL

- Key: `ProviderKeyResolver.ResolveFirstEnabledKey(provider, protector)` — reuse nguyên (Priority tăng dần, tie-break Id tăng dần, null nếu không có). Đây chính là fallback mà XML doc của nó mô tả "trước khi Phase 3 có select" — 3D sẽ thay bằng selection + usage thật.
- URL: `ProviderUrl.Canonicalize(provider.BaseUrl) + "/v1/chat/completions"` — cùng rule canonicalize với `ProviderRequestFactory` (path bắt đầu `/v1`).
- Header: **mở rộng `ProviderRequestFactory`** với tham số optional `HttpMethod method = HttpMethod.Get` + `StringContent? content = null` (5 caller hiện tại không đổi hành vi); header theo `ProviderType` chỉ viết 1 chỗ. 3A chỉ dùng nhánh `OpenAI` (Anthropic đã bị chặn từ resolve, nhưng nhánh header có sẵn không đụng tới).

### 5.2 HttpClient

- Named client **mới** `"upstream"` — đăng ký trong `ProxyApp.Configure`:
  - `Timeout = Timeout.InfiniteTimeSpan` (**bắt buộc** — default 100s sẽ cắt stream dài; `"provider-probe"` timeout 10s chỉ dành cho probe).
  - `SocketsHttpHandler.ConnectTimeout = 10s` (fail nhanh khi upstream chết).
- Không dùng `"provider-probe"` (10s timeout) cho streaming — tuyệt đối không đổi timeout của nó (probe/test connection đang dựa).

### 5.3 Streaming path

```
handler:
  send with HttpCompletionOption.ResponseHeadersRead (đọc header trước, body còn stream)
  ctx.Response.StatusCode = upstream.StatusCode
  copy header Content-Type (SSE: text/event-stream / JSON: application/json)
  await upstream.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted)
```

- Không buffer toàn bộ body; không parse SSE (3E mới dịch nội dung).
- `CancellationToken` = `ctx.RequestAborted` cho cả send và copy — client disconnect → hủy upstream request.
- `using` response upstream (dispose cả stream khi xong).

## 6. Logging (`ILogService`)

| Sự kiện | Level | Nội dung |
|---|---|---|
| **Request thành công** (upstream trả response, bất kể status) | Info | `Chuyển tiếp '{model}' → '{provider}': HTTP {status} trong {ms}ms` |
| Validation fail / 404 / 503 (§4 các dòng Warn) | Warn | lý do cụ thể, kèm model/provider nếu đã biết — **đây là dòng log duy nhất** của request đó |
| Lỗi mạng upstream (502) | Error | kèm `ex.Message` — cũng là dòng log duy nhất của request |

- **Mỗi request đúng 1 dòng log** (Info hoặc Warn hoặc Error — không cộng dồn).
- **Không bao giờ log** nội dung `messages`, body request, hay API key (bảo mật — AGENTS.md).

## 7. Testing strategy

### 7.1 Unit

| Test class | Case chính |
|---|---|
| `ChatRequestValidatorTests` | JSON hỏng; thiếu `model`; `model` không phải string/rỗng; thiếu `messages`; array rỗng; hợp lệ (kể cả thiếu optional fields) |
| `ModelResolverTests` | model OK (temp SQLite theo pattern `ProviderServiceTests`); unknown → NotFound; model tắt / provider tắt → NotFound; provider Anthropic → AnthropicNotSupported; trùng ModelId → lấy Id nhỏ nhất; `Accounts` được Include (key resolve hoạt động) |
| `ChatCompletionsHandlerTests` | stub `IUpstreamClient` (test double nested private): passthrough 200 + Content-Type; upstream trả 429 → passthrough nguyên status/body; upstream ném HttpRequestException → 502; resolve fail → đúng payload §4; key null → 503 |

### 7.2 Integration (in-proc, mock upstream)

- ⚠️ **Deviation đã nhận thức:** `WebApplicationFactory<T>` chính thức **không khả thi** — host nằm trong class library `RouterBalancing.Core`, không có `Program` entry point. Thay bằng **`Microsoft.AspNetCore.TestHost` 10.0.12**: test tự tạo `WebApplication` qua seam `ProxyApp.ConfigureServices` → (đăng ký stub) → `Build()` → `ProxyApp.ConfigurePipeline` + `builder.WebHost.UseTestServer()` → `GetTestClient()`; test project cần thêm `<FrameworkReference Include="Microsoft.AspNetCore.App" />`. Cùng mục tiêu (in-proc, không socket, thay thế được upstream), ít cơ chế hơn.
- Mock upstream: stub `IUpstreamClient` trả `HttpResponseMessage` canned (kể cả `StreamContent` giả lập SSE) — đăng ký đè sau `ProxyApp.Configure`.
- Case: toàn pipeline gồm `ApiKeyMiddleware` (401 khi thiếu key client); happy-path 200 + SSE body giữ nguyên từng byte; 400/404/503 đúng §4; `/v1/models` + `/health` không đổi hành vi (regression); upstream 429 passthrough.

### 7.3 e2e curl (script local)

- `scripts/e2e-3a.sh` (Git Bash) + `scripts/mock-upstream.mjs` (node, mock HTTP server — nhận POST `/v1/chat/completions`, trả SSE giả + echo header).
- **Prerequisites** (in trong header script, fail-fast với message rõ nếu thiếu):
  1. App đang chạy, proxy bind `127.0.0.1:8317` (port setting mặc định).
  2. Provider `e2e-mock`: `BaseUrl = http://127.0.0.1:9999`, type OpenAI, ≥1 model enabled, ≥1 account key enabled.
  3. Node + curl có sẵn.
- Assertions: `/health` → 200; happy-path `stream:true` → 200 + body chứa `data:`; JSON hỏng → 400 + `invalid_request_error`; model lạ → 404 + `model_not_found`.
- Script **không** tự sửa DB/UI — chỉ curl + so sánh status/JSON.

## 8. Handoff sang slice sau (điều 3B cần biết)

- Seam để 3B chèn selection: sau `ModelResolver` trả về danh sách candidate (3A đã để thứ tự `Id` tăng dần) — 3B thay bằng weighted/queue selection, **không đổi** validator, error contract, streaming, logging.
- `MaxConcurrent` (đã có trên `Provider`) chưa dùng ở 3A — 3B dùng làm giới hạn channel/semaphore.
- `IUpstreamClient` là nơi 3C bọc retry (với kết quả `HttpResponseMessage` đã có) và 3D đổi key resolution.

## 9. Ràng buộc chất lượng (theo AGENTS.md)

- XML doc `///` mọi public member; comment "why" tiếng Việt.
- Test doubles nested private trong file test; test name mô tả hành vi.
- Commit conventional tiếng Anh, mỗi task một commit.
- Gates cuối slice: `dotnet build` 0W/0E, `dotnet test` toàn suite xanh (baseline 183 + test mới), parity i18n giữ nguyên (3A **không thêm key UI** — chỉ log/error message tiếng Anh không qua i18n).
