# OpenCode Free Gate v2 — sidecar Go đạt parity client 2.x chính chủ

**Date:** 2026-11-10
**Branch:** `fix/opencode-free-gate`
**Status:** Đề xuất — chờ duyệt (đã qua builder-check round 1; các mục đã chốt ghi ở §11)

## 1. Vấn đề và lý do

Toàn bộ traffic OpenCode Free hiện chết ở 403 `"OpenCode's free tier can only be used from within OpenCode"`. Đây là **cửa từ chối theo shape request** (không phải quota — nó không bao giờ hết hạn theo chu kỳ reset free). Sidecar `opencode-proxy` của ta vẫn nói contract **v1** (đúc kết 2026-10-14, lane `/zen`), trong khi upstream đã chuyển free lane sang origin `/inference` với shape của **CLI 2.x chạy trên Bun**:

Gap đã xác minh bằng probe + đối chiếu implementation tham chiếu đang chạy được (jasonxu114514/opencode2api, GPL-3.0 — chỉ đọc để hiểu hợp đồng, không copy):

| Hạng mục | Sidecar hiện tại | Hợp đồng v2 |
|---|---|---|
| `x-opencode-project` | 40-hex **cố định** (`sha256("opencode-project\0router-balancing")`, `main.go:187-190`) | **ngẫu nhiên 40-hex mỗi request khi client không tự chỉ định** — quota free được sổ theo *(project id, egress)*; id chia sẻ cố định = một bucket, đến exit phổ biến đã "pre-burned" |
| Session id time-part | 36-bit **từ hash** (`main.go:299-321`) → probe 2026-11 decode ra lệch **17–340 ngày** so với thực tế | **slow clock**: `UTC midnight hôm nay − offset ≤ 30 ngày` (offset từ hash + salt `bootNonce` mỗi process); upstream trả **429** "Free usage limit reached" cho session ngoài cửa sổ plausible — reference ghi nhận đúng pattern này giết cả lane |
| Tools | 5 tool (file `anonymous-tools.json`) | **13 tool chính thức v2**: `edit, execute, glob, grep, question, read, shell, skill, subagent, webfetch, websearch, write, patch` |
| System prompt | không inject; chỉ paraphrase chữ ký title-gen thời v1 | **bắt buộc inject system prompt CLI chính chủ** (marker `"You are opencode, an interactive CLI tool"`) + env block + skills listing |
| Tool names của client | chỉ chuẩn hóa casing quartet | mọi tool client bị **đổi tên thành `clint_<12-hex SHA-256>`** + rewrite lịch sử tool-call; tên client "sống sót" trên wire là thứ free tier từ chối |
| UA | `opencode/latest/2.0.29/cli` (shape đúng, version cũ) | `opencode/latest/2.1.1/cli` |

Lane v1 `/zen` đã bị retire cho free models; free chỉ còn trên `/inference`.

Bức tranh catalog **live 2026-11-10** (nhà chức trách cho phạm vi model): free set còn sống (provider `opencode`, cost 0, không deprecated) = 11 model, **tất cả chat-family** (`@ai-sdk/openai-compatible`, `provider.api = https://opencode.ai/inference/openai/v1`): `big-pickle, glm-5.3-flash-free, longcat-2.5-free, deepseek-v4.1-flash-free, nemotron-3.5-lightning-free, nemotron-3.5-super-vl-free, inkling-2-small-free, mimo-v2.6-flash-free, iceman-free, beam-free, flint-40b-free`. Các model `-free` họ responses (`muse-spark-*-free`, `crouton-alpha-free`) và anthropic (`minimax-m*-free`, `qwen3.6-plus-free`) đều **đã deprecated** — detector của app lọc deprecated nên chúng không chọn được qua router. Kết luận phạm vi: **branch này chỉ cần đường chat-family**; mọi họ model free khác = chờ OpenCode mở lại, bridge là ticket riêng khi đó.

## 2. Mục tiêu / Non-goals / Anti-goals

**Mục tiêu**
1. Free lane chạy lại: request qua sidecar với shaping v2 đầy đủ → 200 + completion hợp lệ (bằng chứng live, §8).
2. Sidecar là **authority duy nhất** về shaping (identity + body + path) cho free lane host `opencode.ai` — quyết định **A**.
3. C# phân loại lỗi 3 chiều để failover/cooldown đúng nghĩa (§7.3); **giải quyết backlog E1 + E2** (`docs/backlog.md` §E) trong cùng work này.
4. Model discovery giữ theo catalog `models.opencode.ai/api.json` như pipeline free-sync hiện có (quyết định **A2** — không thêm cơ chế mới, xem §7.4).
5. Lane **keyed** opencode.ai (Bearer key thật) giữ chạy qua sidecar — chế độ passthrough, không shaping anonymous (§5.2; quyết định Q1 của user 2026-11-10).
6. Không regress: dataset export, title-gen qua router, proxy pool SOCKS5/HTTP, failover hiện hữu.

**Non-goals**
- Bridge giao thức chat→responses / chat→anthropic (§1: hiện không còn model free nào cần bridge).
- Traffic `/v1/responses` của client hitting provider opencode.ai: **hôm nay chưa từng chạy** (reroute của `OpenCodeSidecarHandler` chỉ bắt `/v1/chat/completions`; responses đi thẳng Schannel → 403). Sau branch này vẫn 403 như cũ — **không phải regression**; reroute+shape responses là ticket riêng nếu có nhu cầu.
- Provider `opencode-go`: **không tồn tại trong app** (đã kiểm tra nguồn) — khỏi phạm vi; lane keyed của host `opencode.ai` mới là vấn đề (§5.2).
- Availability probe tiêu tốn quota (backlog); admin/rotation/WARP của reference.

**Anti-goals**
- Burn egress của máy vào dấu hiệu IP-abuse (probe có kỷ luật, §8).
- Ship code/data **copy** từ repo GPL-3.0 vào repo Apache-2.0 này.
- Phá `ClassifyFatal`/quota-cooldown/failover đang chạy trên production.

## 3. Ràng buộc

- **License:** chỉ **re-implement từ hợp đồng đã document**. Assets chính thức (system prompt v2, 13 tool definitions) **tái tạo từ nguồn MIT của dự án OpenCode** (anomalyco/opencode, pin tag tương ứng CLI 2.1.1 — prompt/tool description nằm trong nguồn CLI; tool schemas convert zod→JSON-schema), kèm attribution trong header file. **Không** chép `opencode_prompt_v2.txt`, `opencode_tools.json` của opencode2api. Fallback nếu extract từ nguồn gãy: **tự capture wire format từ CLI chính chủ** qua capture proxy cục bộ (dữ kiện, không phái sinh GPL).
- Sidecar: Go toolchain **theo `go.mod` hiện tại (1.24, `GOTOOLCHAIN=local`)**; deps tối thiểu — hiện chỉ `golang.org/x/net/proxy`; **thêm `github.com/refraction-networking/utls` (BSD-2) CHỈ nếu stage 2 TLS kích hoạt** (§5.9; bump `go.mod` nếu utls đòi).
- C#: không dependency mới; build gate `dotnet build "router-balancing/router-balancing.csproj" -f net10.0-windows10.0.19041.0`; test xUnit + `go test`.
- Hợp đồng host↔sidecar: giữ `LISTENING <port>`, `GET /health`, `POST /v1/chat/completions`, header `X-Proxy-Url`; **thêm đúng 2 response header thông tin từ sidecar về host** (§5.7: `X-Sidecar-Shaped`, `X-Sidecar-Title`) — không protocol mới.
- Probe live: ngân sách **3 request**, theo quyết định V1 của user (§8).

## 4. Các hướng đã cân nhắc

**A — Sidecar là authority shaping duy nhất (CHỌN).** C# bỏ transform body + identity headers cho opencode.ai, chỉ giữ lane selection (`IsApplicable`) + phân loại lỗi. Trade-off: sidecar phình thêm module (prompt/toolshape), nhưng shaping chỉ còn MỘT chỗ, và mọi leo thang TLS (uTLS/Bun ClientHello) đương nhiên nằm phía Go. Reference đã chứng minh hướng này chạy được production.

**B — Cập nhật fingerprint C# lên v2 (LOẠI).** .NET không tự tạo ClientHello của runtime khác (Schannel); nếu gate soi TLS thì đường này cụt; shaping hai nơi dễ lệch — chính là bài học v1 khi header C# thành dead weight vì sidecar set lại toàn bộ.

**C — Port nguyên gateway reference (LOẠI).** Vi phạm license; phần lớn tính năng (admin UI, key rotation, WARP, model quotas) ta không cần.

**Kiểm chứng: V1 — live probe có kỷ luật (user chọn 2026-11-09)** thay vì V2 chỉ offline (không có bằng chứng gate đã mở trước merge).

## 5. Hợp đồng v2 (trích yếu — binding cho implementation)

### 5.1 Lane và endpoint
- Origin free: `https://opencode.ai/inference`. Client 2.1 post theo **họ protocol**: Chat → `/inference/openai/v1/chat/completions`; (Responses → `/inference/openai/v1/responses`; Anthropic → `/inference/anthropic/v1/messages?beta=true` + `anthropic-beta: interleaved-thinking-2025-05-14` + `anthropic-version: 2023-06-01` — **chỉ ghi để tham chiếu, branch này không dùng** vì không còn model free non-chat sống, §1).
- Sidecar nhận body chat từ host (hợp đồng host↔sidecar không đổi) → **luôn post route family chat** `/inference/openai/v1/chat/completions`. Không bảng protocol per-model, không header định tuyến (xem §7.4 cho lập luận).
- Catalog là nguồn tri thức path/family cho tương lai: provider `opencode` api `/inference/openai/v1`; protocol per-model suy từ npm SDK (`@ai-sdk/anthropic`→Anthropic, `@ai-sdk/openai`→Responses, `openai-compatible`→Chat) — chỉ dùng cho ticket bridge sau này.

### 5.2 Auth + phân tách lane theo credential
Sidecar nhận request từ host đã forward nguyên `Authorization` thật (`OpenCodeSidecarHandler.CloneForSidecarAsync` copy toàn bộ header — hợp đồng host↔sidecar không đổi). Sidecar quyết định lane:
- **Keyless** (không có `Authorization`, hoặc `Bearer public` bất kể case) → free lane: set `Authorization: Bearer public` (**case-sensitive** — "Bearer PUBLIC" bị gate chặn) + shaping anonymous đầy đủ (§5.4–§5.8).
- **Keyed** (`Bearer <key thật>`) → keyed lane: giữ nguyên key; **không** inject prompt, **không** rename tools, **không** append bộ 13 — body client đi y nguyên (reference chỉ áp `prepareAnonymousBody` cho anonymous lane); vẫn set bộ identity header v2 (§5.3) vì client OpenCode thật có tài khoản cũng gửi đúng bộ đó; route chat family như free (§5.1).
- Trạng thái hiện tại: keyed traffic **đã** trôi qua sidecar với shaping v1 anonymous + key thật (sidecar chỉ fallback `Bearer public` khi thiếu auth — `main.go:152-154`) — chưa từng verify, coi như đã hỏng; branch này sửa thành passthrough đúng nghĩa.

### 5.3 Header set v2
Set đúng các header (không copy header client): `x-opencode-client: cli`; `x-opencode-session`, `x-session-affinity`, `X-Session-Id` = root session; `x-opencode-session-id` = child session (bằng root khi không subagent); `User-Agent: opencode/latest/2.1.1/cli`; `x-opencode-project` = 40-hex (§5.5). **Cấm** `x-opencode-request` và project `prj_*` (gate 403); parent session (nếu có) thêm `x-opencode-parent-session-id` + `x-parent-session-id`.

### 5.4 Identity: session (slow clock) + project
- **Session**: `ses_` + 12 hex + 14 Base62. 12 hex = layout `Identifier.descending()`: `~(ts_ms<<12 | 1) & 0xFFFFFFFFFFFF` (low 12 bits luôn `ffe`, counter 1 trong cửa sổ accepted `ffe..fc0`). **ts_ms không lấy từ hash**: `offset = hash[0..3] mod (30 ngày/ms)`; `ts = UTC midnight hôm nay − offset` → id ổn định cả ngày; salt `bootNonce` 16 byte ngẫu nhiên mỗi process. Session official hợp lệ từ client → **giữ nguyên** (prompt-cache affinity). Session ngoài cửa sổ plausible → 429 giống hệt quota hết.
- **Project**: client gửi `x-opencode-project` đã dạng 40-hex (git-root-commit — đúng cái CLI thật gửi, ổn định theo repo) → **giữ nguyên**; giá trị khác (kể cả `prj_*`) → hash `sha256("opencode-project\0"+value)[:20]` ra 40-hex; client không gửi → **RandomProjectID() mỗi request** (20 byte random → 40 hex). *Lập luận (controller): giữ project thật của CLI = hành vi giống hệt người dùng CLI thật (bucket stable theo repo là trạng thái "bình thường" mà gate thiết kế cho); rotate *vô điều kiện* mọi request sẽ làm toàn bộ traffic CLI trông như repo mới mỗi turn — pattern bất thường mà chính reference không làm.*

### 5.5 Body shaping (chat)
`stream:true` (bắt buộc — free tier chỉ nhận agent-shaped streaming), `stream_options.include_usage=true`, `max_completion_tokens=32000` khi body không có limit nào, và **thứ tự key như client 2.x serialize**: `model, messages, tools, stream, stream_options, max_completion_tokens`, phần còn lại sort alphabet rồi mới ghi.

### 5.6 System prompt + tools
- **Prompt**: `officialPrompt_v2 + env_block + skills_listing` prepended là **một** system message duy nhất; mọi `system`/`developer` của client gộp lại (giữ thứ tự, cách bằng dòng trắng) sau prompt chính. Body đã chứa marker `"You are opencode, an interactive CLI tool"` → không inject nữa (idempotent). env block: "You are powered by the model named <model>… <env> Working directory / Workspace root / Is directory a git repo: yes / Platform (win32) / Today's date</env>"; skills: block `customize-opencode` tĩnh.
- **Tools**: mọi tool client (chat shape `tools[].function.name`) → `clint_` + 12 hex đầu `SHA-256(tên)`; trùng thì `_2`, `_3`…; tên ≤ 64 ký tự. **Lý do: free tier từ chối body còn vocabulary của client khác.** Rewrite mọi tham chiếu để request tự nhất quán: `tool_calls` trong history, messages `role:"tool"`, `tool_choice` chỉ định tên; tham chiếu tool không còn khai báo → drop cả pair. Append **bộ 13 chính thức** sau tools client (shape `{type:"function", function:{name, description, parameters, strict:false}}`; `shell` description rewrite "on Linux using bash"→"on Windows using powershell" trên host Windows).
- **Response restore**: sidecar trả SSE với tên tool map NGƯỢC về tên client gốc (exact wire name; official alias — client khai `Bash`, model gọi `shell` → trả `Bash`; best-effort case/shape fold; không bao giờ trả tên `clint_*`/tool lạ cho client).

### 5.7 Tín hiệu sidecar → host (2 header thông tin)
Sidecar set trên response trả về host:
- `X-Sidecar-Shaped: tools+prompt` (free lane đã shaping) / `none` (title-gen **hoặc lane keyed**: không đụng tools array) — host dùng cho cờ dataset §7.2.
- `X-Sidecar-Title: synthesized` — chỉ khi sidecar tổng hợp title (xem 5.8); host dùng để **skip record dataset** (completion không phải do model sinh).
Host không suy đoán shaping từ nội dung — hết duplicate logic giữa hai ngôn ngữ.

### 5.8 Title-generation (giữ traffic title-gen của opencode CLI qua router)
Request title = **không tools**, ≤3 messages, không turn assistant/tool, chứa một trong các đoạn prompt title chính chủ (v2: "you name conversations so the user can find them later…", "reply with a title and nothing else"…; v1: user mở đầu "generate a title for this conversation"). Title request: vẫn inject official prompt, **không** đụng tools (`X-Sidecar-Shaped: none`); nếu upstream trả 429/lỗi transport → sidecar tổng hợp 200 với title ngắn từ dòng đầu user message (≤6 từ/≤40 ký tự, fallback "New Session") + header `X-Sidecar-Title: synthesized`. Paraphrase chữ ký title-gen thời v1 trong `main.go`: **bỏ** (artefact của content-gate v1; reference v2 không cần) — rollback = thêm lại paraphrase cũ (git history) nếu thực tế thấy title bị 403.

### 5.9 Phân loại lỗi 3 chiều
1. **Shape-403** (message "…within OpenCode"): chính gate từ chối theo hình thể → deterministic; retry account/egress khác với cùng body = cùng lỗi.
2. **IP-abuse-403**: 403 + marker (lowercase): `abusive traffic` / `abuse traffic` / `abuse of traffic`, hoặc `free models aren't available` + `from your current network` → **egress bị chặn**, không phải request; retry cùng exit chỉ kéo dài penalty.
3. **Quota-429**: hoặc `error.type=FreeUsageLimitError` (shape cũ, đã có trong code), **hoặc** shape v2 theo **backlog E2**: `429` + `AI_APICallError` + `Invalid credential` (upstream issue #54338, hết quota mà không kèm `Retry-After`) → cả hai đều mở gate quota toàn-provider. *Hệ quả E1: quartet v1 biến mất — bộ 13 tool v2 + prompt chính chủ là shape hợp lệ duy nhất; branch này chính là câu trả lời E1.*
Lưu ý: session id không plausible cũng trả 429 y hệt quota → trong probe, 429 = "inconclusive".
Sidecar **pass-through nguyên trạng status + body** upstream; phân loại nằm ở C# (§7.3).

### 5.10 TLS — hai chặng
- **Stage 1 (mặc định):** giữ `crypto/tls` Go. Bằng chứng: probe 2026-10-14/15 — fingerprint Go vượt gate /inference (Schannel/OpenSSL bị 403); 403 hiện tại mang message shape-gate → thủ phạm chính là shape lệch.
- **Stage 2 (kích hoạt bởi probe):** nếu shaping v2 đầy đủ vẫn shape-403 → transport mô phỏng **Bun ClientHello qua uTLS**: JA4 `t13d1713h1` — 16 cipher suites (3 TLS1.3 trước, rồi ECDHE ECDSA/RSA GCM+CBC, RSA GCM+CBC), extension order: SNI, ExtendedMasterSecret, RenegotiationInfo, SupportedCurves (X25519MLKEM768, X25519, P-256, P-384), SupportedPoints, SessionTicket, ALPN `http/1.1` (duy nhất), StatusRequest, SignatureAlgorithms (9 scheme), SCT, KeyShare (X25519MLKEM768 + X25519), PSK modes (DHE), SupportedVersions (1.3, 1.2); không GREASE, không padding; HTTP/1.1. Spec là **dữ kiện fingerprint công khai** — tự viết `ClientHelloSpec` uTLS trong repo ta, không copy code reference. Thêm header order/casing HTTP/1.1 theo Bun chỉ nếu probe buộc.

## 6. Thiết kế phía sidecar (`sidecar/opencode-proxy/`)

Module trong package `main`:

| File | Vai trò |
|---|---|
| `main.go` | handler/transport/health — giữ hợp đồng host; pipeline: read body → **detect lane keyed/keyless (§5.2)** → `shapeBody` (chỉ free lane) → upstream request (path §5.1, headers §5.3-5.4, auth §5.2) → stream về + `restoreToolNames` trên SSE (chỉ free lane) + 2 header §5.7 |
| `identity.go` | `canonicalSessionID` slow-clock + bootNonce; `projectID`/`randomProjectID`; session signal từ header client / conversation seed (system prompt + first user turn — hội thoại nhiều turn giữ stable khi history dài) |
| `prompt.go` | official prompt v2 (embed `prompt_v2.txt` tái tạo từ nguồn MIT + attribution), env block, skills, fold system/developer |
| `toolshape.go` | `clint_` rename + rewrite tham chiếu + bộ 13 (embed `tools_v2.json` tái tạo từ nguồn MIT) + response restore map (SSE-aware: chỉ sửa `tool_calls[].function.name` trong payload `data:` JSON, không đụng byte khác) |
| `body.go` | stream/usage/max_completion_tokens + thứ tự key chat |
| `title.go` | detect title request + synthesize title fallback |
| `probe_test.go` | live probe (build tag `liveprobe`, §8) |

Xóa: `anonymous-tools.json` cũ, paraphrase title-gen v1, hash-time session, project id cố định.

Test offline (`go test`): golden fixtures tự viết theo hợp đồng (không copy payload upstream) — **lane detect** (keyless/`Bearer public` bất kể case/key thật), **keyed passthrough** (body byte-identical, key giữ nguyên, identity headers vẫn có, `X-Sidecar-Shaped: none`, không restore tool name), session window (ts decode trong [hôm nay−31d, hôm nay], suffix `ffe`, stable trong cùng "ngày"), project 40hex random/giữ client 40hex/hash value khác, prompt injection idempotent, clint_ rename deterministic + collision `_2`, history rewrite 3 dạng, official set 13 tên + `strict:false` + powershell rewrite, key order body, title detect ±, restore map (wire→client, alias Bash↔shell), 2 header §5.7 có đúng chỗ, passthrough status/body.

## 7. Thiết kế phía C# (`RouterBalancing.Core`)

### 7.1 `OpenCodeFreeFingerprint` — cắt theo decision A
- Xóa: `Apply`, `TransformBody`, `ApplyTools`, quartet/decoys, `ComputeRenameMap`, `RestoreMapFor`, `DecoyNamesFor`, `FindDecoyCalls`, mint session/request-id (v1-era). `ProviderRequestFactory` ngừng gọi fingerprint cho opencode.ai.
- Giữ: `IsApplicable` (lane selection: `OpenCodeSidecarHandler` reroute, `DatasetLane`, cờ §7.2).
- Tín hiệu chẩn đoán decoy-call trong `ProxyRequestHandler` (~:438): thay bằng cảnh báo "model gọi tool không có trong danh sách client khai báo" — diff tên tool trong response (đã restore) với names trong request body; dữ liệu có sẵn ở tee.

### 7.2 Dataset
- `tools_rewritten` = response header `X-Sidecar-Shaped` chứa `tools` (host không tự đoán). Với free lane v2: true cho mọi request thường, false cho title-gen **và lane keyed** (passthrough — không rename). Lane `keyed_fingerprint` trong spec dataset sửa nghĩa theo hướng này (amendment nhóm F).
- Response có `X-Sidecar-Title: synthesized` → **skip record** (completion không do model sinh), counter mới `SkippedSynthetic` trong `DatasetStats` + i18n dòng status (record shape không đổi — counters chỉ là stats UI).

### 7.3 Phân loại lỗi (§5.9 → pipeline failover)
- `ProxyRequestHandler.ClassifyFatal` tách 403 theo marker:
  - **IP-abuse-403** → `DispatchOutcome.Blocked` (mới): dispatcher **không rotate account** (cùng egress = cùng câu trả lời); proxy của attempt bị `ReportBlocked(proxyId, TTL=15m)` trong proxy pool (cooldown riêng, tách `ReportFailure`); `ProxyHealthHandler` **không được** `ReportSuccess` cho response Blocked (hiện tại nó credit pool cho MỌI HTTP response — phải sửa). Còn exit khác trong pool → thử exit khác.
  - **Không proxy cấu hình (direct egress) hoặc hết exit** → mở **gate toàn-provider** dạng `ProviderQuotaGate` (message/i18n riêng "OpenCode chặn free traffic từ mạng hiện tại — đổi egress hoặc chờ", badge theo pattern `QuotaCooldownBadge` hiện có; TTL constant 15m, không setting mới — setting hóa là backlog).
  - **Shape-403** → deterministic: fail nhanh không quay vòng lane cùng shape; log ERROR kèm requestId (báo hiệu gate đổi contract → cần update sidecar).
- 429 quota: mở rộng `IsProviderWideQuotaFailure` nhận cả shape E2 (`AI_APICallError` + `Invalid credential`) → cùng provider-wide gate + Retry-After nếu có; cập nhật backlog E2 → `[x]` khi merge.
- Sidecar pass-through status/body → không protocol mới, chỉ 2 header §5.7.

### 7.4 Model discovery (decision A2 — tối giản)
- **Không** thêm header protocol (`X-Router-Protocol` từng đề xuất — bỏ) và **không** lưu protocol per-model vào capability metadata (bảng `ModelCapability` là boolean-token, thêm cột = migration không cần thiết khi mọi model free sống đều chat-family, §1).
- A2 trong branch này = **giữ nguyên pipeline free-sync hiện có** (`FreeModelSyncWorker`/`FreeModelDetector` đọc `models.opencode.ai/api.json`, lọc deprecated — app đã làm); sidecar route cố định chat-family. Tri thức per-model-protocol chỉ tồn tại trong §5.1 + ticket bridge (backlog) nếu OpenCode mở lại model free non-chat.

## 8. Kiểm chứng V1 — live probe (user-approved 2026-11-09)

- Vị trí: `sidecar/opencode-proxy/probe_test.go`, `//go:build liveprobe` — `go test -tags liveprobe -run TestLiveProbe` (không bao giờ chạy trong suite thường/CI).
- Ngân sách 3 request, tuần tự, theo catalog live §1:
  1. **chat-family**: `glm-5.3-flash-free` — route `/inference/openai/v1/chat/completions`, shaping v2 đầy đủ, payload tối thiểu ("Reply with exactly: OK").
  2. ~~anthropic-family~~, 3. ~~responses-family~~ — **hủy**: không còn model free sống nào hai họ này (§1); ngân sách còn lại **dự phòng cho stage 2 TLS** (1 request re-probe sau khi bật uTLS nếu stage 1 fail) và cho model swap ở điều kiện dưới.
- Acceptance: 200 + completion không rỗng.
- Điều kiện: **IP-abuse-403 → dừng cứng toàn bộ probe**, không retry, báo cáo (nguy cơ đào sâu dấu vết egress). 400 "Model is unavailable"/410 → model hết chỗ/deprecated: inconclusive, **được swap sang model khác trong cùng live set** (tính 1 request). 429 → inconclusive, không retry (project id mới đã là biến khác). Shape-403 → bật stage 2 (§5.10) rồi probe lại — vẫn trong tổng ngân sách 3.
- Ghi kết quả (status, request id upstream, snippet message — không ghi nội dung hội thoại) vào báo cáo implementer + ledger SDD.
- Ngân sách 3 request chỉ dành cho **free lane**. Lane keyed verify bằng unit test offline (fixtures §6); probe live keyed chỉ chạy **nếu user cấp key thật tại thời điểm execute** (chi phí pay-per-use, tách khỏi ngân sách free) — mặc định không chạy.

## 9. Việc để cho builder (plan)

Phân rã commit, nội dung fixture, danh sách API C# xóa/thay + call site, cách extract prompt/tool từ nguồn anomalyco/opencode (convert zod→JSON-schema hay capture), tên setting/i18n counter mới. Số dòng xấp xỉ: sidecar +~700/−~120; C# −~450 (fingerprint) +~250 (Blocked outcome + pool cooldown + gate + counter Synthetic); test Go +~450; test C# +~250. (Đã tăng so với draft đầu vì §7.3 đầy đủ tầng.)

## 10. Rủi ro

| Rủi ro | Giảm thiểu |
|---|---|
| Gate soi TLS (stage 1 fail) | Stage 2 pre-scope; probe trả lời trong 1 request; uTLS BSD-2; ngân sách probe đã dự phòng |
| Nội dung prompt/tools tái tạo lệch bản gốc mà gate soi chi tiết (3 request không chẩn đoán nổi) | extract từ đúng tag MIT của CLI + fixtures; fallback tự capture wire từ CLI; bằng chứng lịch sử: gate reject stub/empty tools → kiểm tra ở mức name+schema, không phải byte-equality |
| Free model set tiếp tục drift | pipeline free-sync của app đã lọc deprecated; probe tái chạy được (tag) khi cần |
| Project id random (cho client không gửi project) bị nhìn là abuse | reference production đang ship đúng chiến thuật (project+egress ledger); marker IP-abuse có xử lý 3 chiều §7.3 |
| Bỏ paraphrase title-gen làm hỏng title của opencode CLI qua router | detect + fallback (§5.8); rollback một lệnh git |
| Xóa fingerprint C# ảnh hưởng dataset/diagnostics | §7.1-7.2 giữ cờ qua header + thay tín hiệu chẩn đoán; integration tests dataset phải giữ xanh |
| `ProxyHealthHandler` credit pool cho Blocked (làm hỏng cooldown) | đã nêu đích danh trong §7.3 — test pin |

## 11. Quyết định đã chốt (record)

**User đã chốt:**
- **A**: sidecar là authority shaping duy nhất cho free lane; C# giữ lane selection + error classification.
- **A2**: discovery qua `models.opencode.ai/api.json` (pipeline sync hiện có) + fallback tĩnh; không quota-consuming probe (backlog).
- **V1** (2026-11-09): verify bằng live probe có kỷ luật, ≤3 request.

**Controller đã chốt (user phủ quyết được khi duyệt):**
1. TLS hai chặng (stage 1 Go crypto/tls; stage 2 uTLS Bun ClientHello khi probe buộc) — §5.10.
2. Title-gen: paraphrase v1 → detect + synthesized fallback + header §5.7 — §5.8.
3. Route family chat cố định; **không** header protocol / không migration storage protocol per-model; model free non-chat (hiện deprecated) → ticket bridge khi có nhu cầu — §5.1, §7.4.
4. `/v1/responses` của client trên provider opencode.ai: ngoài phạm vi (chưa từng hoạt động — không phải regression) — §2.
5. Project id: giữ 40-hex của client; random per-request khi client không gửi; hash nếu giá trị dạng khác — §5.4.
6. IP-abuse: `Blocked` outcome + `ReportBlocked` per-exit 15m + provider-wide gate khi direct egress/hết exit + sửa credit path `ProxyHealthHandler` — §7.3.
7. Assets prompt/tools tái tạo từ nguồn MIT anomalyco/opencode pin tag, fallback tự capture; không copy GPL — §3.
8. Dataset: `tools_rewritten` + skip synthesized-title qua 2 header sidecar; counter `SkippedSynthetic` mới — §5.7, §7.2.
9. Quota-429 nhận cả shape E2 (`AI_APICallError`+`Invalid credential`) → E2 đóng trong work này — §5.9, §7.3.
10. Lane keyed = passthrough: giữ key thật, body không đụng, vẫn set identity headers v2, route chat family như free; `X-Sidecar-Shaped: none` — §5.2.

**User đã chốt tiếp (2026-11-10, Q1):** lane **keyed opencode.ai phải giữ chạy qua sidecar** → chế độ passthrough không shaping anonymous (§5.2). Provider `opencode-go` không tồn tại trong app (đã kiểm tra nguồn) — khỏi lo. Verify keyed: offline + live chỉ khi user cấp key (§8).
