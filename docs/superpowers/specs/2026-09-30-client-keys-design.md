# Spec: Client API Keys (multi-key inbound) + Usage Capture

**Ngày:** 2026-09-30
**Trạng thái:** Approved (brainstorm §1–§3, user chốt)
**Item:** 3 — API key multi-key (inbound)

---

## 1. Mục tiêu

Thay API key đơn trong settings bằng **nhiều client key** cho proxy, mỗi key có: tên (tag client), revoke mềm/mạnh, theo dõi request/token (daily), rate limit RPM/TPM per-key (429), và usage capture từ upstream (phần hiện chưa có trong codebase).

Không thuộc scope item này: outbound key pool/rotation (ProviderAccount), per-key auth cho client khác proxy (`/v1/models` giữ nguyên theo middleware hiện hành), distributed/multi-instance rate limit.

## 2. Data model — entity `ClientKey`

Bảng `ClientKeys` (EF Core, SQLite, migration auto):

| Field | Type | Ghi chú |
|---|---|---|
| `Id` | long | PK |
| `Name` | string ≤100 | tag client, hiển thị UI |
| `KeyHash` | string(64) | SHA-256 hex của key plaintext, **unique index**; không lưu plaintext |
| `Enabled` | bool = true | revoke mềm — tắt vẫn giữ row |
| `RequestsUsed` | long | counter daily UTC |
| `TokensUsed` | long | counter daily UTC (prompt+completion) |
| `UsageDate` | DateOnly? | ngày UTC của 2 counter — lazy reset khi lệch (pattern `ProviderAccount`) |
| `LastUsedAt` | DateTimeOffset? | lifetime |
| `RatePerMinute` | int? | null = không giới hạn |
| `TokensPerMinute` | int? | null = không giới hạn (retroactive — xem §5) |
| `CreatedAt` / `UpdatedAt` | DateTimeOffset | |

**Key plaintext format:** `sk-rb-` + 43 ký tự base64url từ CSPRNG (32 bytes). Chỉ hiển thị **1 lần** tại thời điểm tạo —之后 chỉ còn mask `sk-rb-…<4 ký tự cuối>`.

## 3. Auth semantics (single source = bảng)

- Middleware: **≥1 enabled key** → yêu cầu match; **0 enabled key** → open (giữ semantics hiện tại "chưa set key → không yêu cầu").
- Match: trích key (Bearer / `X-API-Key` như hiện tại) → SHA-256 → so `FixedTimeEquals` với từng hash trong cache enabled key.
- **Bỏ hẳn** settings `apiKey` + `apiKeyEnabled`: xóa khỏi `SettingsKeys`/`AppSettingsService`/`SettingsValidator` + UI regenerate + `GetApiKey`/`SetApiKey`/`SetApiKeyEnabled`. không còn 2 nguồn sự thật.
- `/health` luôn mở (giữ nguyên).

**Migration 1-lần** (trong `DbInitializer.Initialize`, sau `Migrate()`): nếu settings còn key (`apiKey` không rỗng) và bảng `ClientKeys` chưa có row nào → tạo row `Name = "Legacy key"`, `KeyHash = SHA256(key cũ)`, `Enabled = ApiKeyEnabled`. Migration đọc row settings **trực tiếp qua DbContext** (không qua `GetApiKey` — API đó bị xóa ở task UI/settings, thứ tự do plan chốt). Sau khi migrate thành công → xóa key cũ khỏi settings (best-effort, không throw). Idempotent: chạy lại không tạo duplicate (bảng đã có row → bỏ qua).

## 4. Middleware flow (thay `ApiKeyMiddleware`)

Cache in-memory snapshot `(Id, KeyHash, Enabled, RatePerMinute, TokensPerMinute)` của mọi enabled key — rebuild khi service CRUD phát event (pattern `SettingsChanged`). Không đụng DB mỗi request.

1. Path startswith `/health` → next (giữ nguyên).
2. Extract key từ `Authorization: Bearer` / `X-API-Key` (giữ nguyên code hiện tại).
3. Không có key nào enabled → next (open, §3).
4. SHA-256(provided) → loop `FixedTimeEquals` với từng hash enabled.
5. Không match → 401 JSON shape OpenAI (giữ nguyên payload hiện tại).
6. Match → `ctx.Items["ClientKeyId"] = id` → **rate-limit pre-check** (§5).
7. Đạt → cộng `RequestsUsed` + set `LastUsedAt` (lazy daily reset khi `UsageDate` != hôm nay UTC) → next. **Request bị 401/429 không cộng counter nào** (chỉ request được cho qua mới tính).

## 5. Rate limit (RPM/TPM)

- Window **60s fixed per-key, in-memory** (singleton service): mỗi key 1 cặp counter `(requests, tokens)` + window-start timestamp; rollover khi qua window.
- **RPM:** pre-check trước khi cho qua — `requests >= RatePerMinute` → **429**.
- **TPM:** token trong window chỉ tích lũy **sau khi response xong** (usage capture §6) → pre-check chặn request mới khi `tokens >= TokensPerMinute`. Bản chất **retroactive**: request đang chạy đầu tiên vẫn có thể vượt — đây là trade-off đã chấp nhận (token thật chỉ biết sau response).
- 429 shape: `{"error":{"message":...,"type":"rate_limit_exceeded","code":"rate_limit_exceeded"}}` + header `Retry-After` = giây còn lại của window.
- `RatePerMinute`/`TokensPerMinute` null → bỏ qua check tương ứng.

## 6. Usage capture (token từ upstream)

Phần chưa có: codebase hiện **không parse usage** (cột `LogEntry.PromptTokens/CompletionTokens` tồn tại nhưng không nơi nào ghi).

**Đọc usage:**
- `ForwardAsync` success path thay `response.Content.CopyToAsync(ctx.Response.Body)` bằng tee:
  - `content-type: text/event-stream` → forward từng chunk xuống client **đồng thời** parse SSE lines (`data: {...}`); object JSON có `usage.prompt_tokens`/`usage.completion_tokens` → lấy làm usage (lấy cái cuối cùng nếu nhiều).
  - `application/json` (non-stream) → buffer, parse `usage`, rồi ghi ra response.
- **Inject** `"stream_options":{"include_usage":true}` vào body khi request `stream=true` **và** `provider.Type == OpenAI` (body đã buffer sẵn ở `PrepareAsync`). Không inject cho `Anthropic`: nếu server tự gửi usage thì bắt được, không thì counter không tăng (**degrade, không lỗi**).
- Parse fail / không có `usage` → log Debug, request vẫn thành công bình thường.

**Ghi usage:**
1. Cộng `ClientKey.TokensUsed` (lazy daily reset) + cộng window token counter (§5).
2. Ghi `LogEntry` dòng Request với `PromptTokens`/`CompletionTokens` + `RequestId` + `ClientKeyId` — helper mới trên `ILogService` (vd `LogRequestUsage(...)`).

## 7. Journal attribution — `LogEntry.ClientKeyId`

Thêm cột `long? ClientKeyId`. Middleware ghi `ctx.Items`; `ProxyApp` (endpoint) và `ChatCompletionsHandler` (cầm `ctx`) set vào row Request chúng ghi. Row viết ở dispatcher-level không có `ctx` → null (chấp nhận — các row chính đều cover).

## 8. UI — SettingsPanel (thay section API key cũ)

- **Bảng key:** Tên · Key mask · Trạng thái (toggle) · Requests today · Tokens today · Last used · Limits (RPM/TPM) · Edit · Delete.
- **Tạo:** form (tên + RPM/TPM tùy chọn) → show plaintext 1 lần trong box + nút Copy (pattern modal/ConfirmDialog sẵn có).
- **Sửa:** chỉ Name/limits/Enabled — không bao giờ xem lại plaintext.
- **Delete:** hard revoke qua ConfirmDialog.
- **Empty state (0 key):** giải thích "chưa bật auth" + nút Create.
- CRUD key (tạo/sửa/toggle/xóa) → service phát event → middleware cache invalidate ngay (§4).

## 9. i18n

~15 keys mới namespace `clientKeys.*` (col.*, field.*, hint.*, error.*, action.*) — **cả 2 dict** `English` + `Vietnamese`, parity EN == VI.

## 10. Error handling

- 401/429 JSON shape OpenAI-style, encoder relax như `ErrorJsonOptions` sẵn có.
- Usage parse lỗi → log Debug, không throw, không fail request.
- Ghi counter DB lỗi → log Error, **không fail request** (counter là telemetry; gate thật là RPM window in-memory).

## 11. Testing

- **Hash/auth:** SHA-256 deterministic; match đúng/sai; disabled key không match; 0 enabled key → open.
- **RPM:** dưới / đúng giới hạn / vượt → 429 + `Retry-After`; window rollover reset counter.
- **TPM:** post-hoc — usage đẩy window vượt → request kế bị 429.
- **Daily reset:** `UsageDate` lệch ngày UTC → lazy reset cả 2 counter.
- **Usage capture:** non-stream JSON parse; SSE tee parse (fixture bytes, verify byte-forward nguyên vẹn + usage bắt được); inject `stream_options` (OpenAI) / không inject (Anthropic); parse fail → degrade.
- **Migration:** settings key → row Legacy; idempotent (2 lần không duplicate).
- **UI/i18n:** parity EN==VI; gates: full suite xanh, build 0W/0E.

## 12. Commit plan

1. `docs: add client keys design spec and plan`
2. `feat: add ClientKey entity with legacy settings migration`
3. `feat: authenticate proxy requests via client key table`
4. `feat: enforce per-key rate limits with in-memory windows`
5. `feat: capture upstream usage and attribute request logs`
6. `feat: add client key management UI in settings`
7. `test`/`fix` reviews + final verification (theo SDD)

*(Số thứ tự task sẽ chốt trong plan — 1 task = 1 commit.)*
