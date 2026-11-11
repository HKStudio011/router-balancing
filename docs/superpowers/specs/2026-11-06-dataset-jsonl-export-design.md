# Thiết kế: Xuất request/response ra JSONL làm dữ liệu train (dataset export)

Ngày: 2026-11-06 · Trạng thái: chờ duyệt

## 1. Ý định và lý do

Hôm nay nội dung request/response chỉ sống trong RAM: `ApiMonitorStore` giữ ring 50 record
(`src/RouterBalancing.Core/Engine/ApiMonitorStore.cs`), mỗi body bị cắt ở 64KB, tắt app là mất.
User muốn gom các cặp *prompt → completion* đi qua router thành file trên đĩa để làm **dữ liệu
train model**, bật/tắt được trong Settings, chọn được thư mục lưu, mặc định là AppData.

Vì dữ liệu phục vụ train chứ không phải gỡ lỗi, file phải: **đủ nội dung** (không cắt giữa câu),
**một dòng một mẫu** (JSONL), và **cùng một shape cho cả hai endpoint** để một bộ nạp dataset
đọc được mà không cần biết request đi qua endpoint nào.

## 2. Goals

- Bật/tắt export trong Settings, **mặc định tắt**; tắt thì không tốn gì trên đường forward.
- Mỗi request của client kết thúc 2xx thành công → đúng **một dòng JSONL**.
- Hỗ trợ cả `/v1/chat/completions` và `/v1/responses`, chuẩn hoá về shape `messages` + `completion`.
- Đường dẫn lưu chọn được, mặc định `%AppData%\router-balancing\dataset`, hỗ trợ `~`.
- Quay file theo ngày, append-only, giữ được qua restart; retention cấu hình được (`0` = giữ mãi).
- Hiện trạng thái export (số dòng hôm nay, số bản bỏ, thư mục hiện tại) ở Settings.

## 3. Non-goals

- Không xem lại/dò lại dataset trong app, không import lại file đã xuất.
- Không nối các lượt của cùng một cuộc hội thoại thành trajectory (chỉ xuất cặp per-request).
  Người dùng tự ghép/dedup offline bằng `thread_hint`.
- Không anonymise/redact PII, không mã hoá file.
- Không xuất attempt lỗi, request canceled, request validate fail.
- Không dedup context lặp (OpenCode gửi lại toàn bộ lịch sử mỗi lượt — xem §7).

## 4. Anti-goals

- Dataset có mẫu bị cắt nửa (prompt/completion cụt) — thà **bỏ** record còn hơn ghi record hỏng.
- Export làm chậm, làm nghẽn hoặc làm nổ đường forward của request đang phục vụ.
- Đĩa phình không kiểm soát mà người dùng không thấy số.

## 5. Ràng buộc

- Toàn bộ pipeline forward là **fail-open**: component mới không bao giờ ném ra caller
  (multicast delegate dừng ở subscriber ném — bài học `RequestJournalWriter`, `TraceFeed.Publish`).
- `ProxyRequestHandler`, `UsageCapture`, hai protocol là những file nhạy cảm nhất repo, test dày
  nhất — sửa phải giữ nguyên hành vi hiện có khi tính năng tắt.
- `Nullable=enable`, XML doc cho public API, comment giải thích "tại sao" bằng tiếng Việt
  (AGENTS.md). Không log nội dung chứa secret; API key provider không bao giờ vào dataset.
- Build + test phải xanh: `dotnet build router-balancing.slnx`, `dotnet test "router balancing test/..."`.

## 6. Các phương án đã cân nhắc

| Phương án | Cách | Đánh đổi | Kết |
|---|---|---|---|
| **P1 — Sink ở cuối handler, protocol trả thêm completion** | `TeeAsync` cho phép gộp SSE thành completion khi cần; `ProxyRequestHandler` gọi sink một lần sau tee 2xx, tại nơi đã có `provider`/`account`/`model`/`usage` | Dữ liệu đúng, không phải parse SSE hai lần, gate được chi phí; nhưng phải sửa 3 file tee nhạy cảm | **Chọn** |
| P2 — Component tự subscribe `ITraceFeed` + tự buffer body | Service mới giữ prompt body theo `RequestId`, tự agregate khi feed báo `Finished` | Không đụng `TeeResult`, nhưng feed không mang completion nên vẫn phải móc vào tee; thêm RAM (body × request đang chạy) và thêm vòng đời entry | Loại |
| P3 — Dump `ApiMonitorStore` ra file | Ghi ring 50 record xuống đĩa | Body đã cắt 64KB, response trên đường stream là mớ fragment `data:` nối bằng `\n` → dataset rác | Loại |

Lý do P1 thắng: completion của **cả hai** endpoint đã có sẵn máy trạng thái trong protocol —
`ChatSseAggregateState.BuildCompletionJson()` (ghép `delta.content` + `tool_calls`) và
`ResponsesSseState.TerminalResponseJson` (event `response.completed` chứa nguyên object `response`).
Chỉ cần cho phép nó chạy trên đường stream và không bị chặn ở 64KB.

## 7. Thiết kế chi tiết

### 7.1 Dòng dữ liệu

```
PrepareAsync (body gốc của client, giữ nguyên byte)
  └─ ForwardAsync ── PrepareUpstreamBody (body đã rewrite: alias→model id, forced stream, include_usage)
        └─ tee/aggreg ──> TeeResult(+CompletionJson khi bật export)
              └─ DatasetSink.Capture(body GỐC + completion + route + usage)  ← một lần, chỉ 2xx
                    └─ channel bounded → writer thread → requests-YYYY-MM-DD.jsonl
```

`Capture` nhận **body gốc** (`body` trong `ForwardAsync`, spec v1-responses §3.2 giữ nguyên),
**không** phải `requestBody` đã rewrite — body rewrite mang `stream:true` và
`stream_options.include_usage` do app tự thêm, đưa vào dataset là nhiễm rác.

### 7.2 Hook point

Trong `ProxyRequestHandler.ForwardAsync`, nhánh 2xx, ngay sau khối tee/`monitor.RecordResponse`
(khoảng dòng 252–299 của file hiện tại):

- Đọc gate **một lần ở đầu** `ForwardAsync`: `var datasetOn = settings.DatasetExportEnabled;`
  rồi truyền xuống tee qua tham số `captureCompletion`. Tắt → tee y hệt hôm nay.
- Gọi `dataset.Capture(...)` sát `return new DispatchOutcome.Handled();`.
- Mỗi attempt lỗi không ghi gì; request chỉ có một attempt 2xx thắng nên mỗi request client
  đúng một dòng (retry/failover không tạo bản ghi trùng).

### 7.3 Thay đổi ở seam protocol

- `IProxyProtocol` thêm `ProxyEndpoint Endpoint { get; }` (`Chat` / `Responses` — enum đã có)
  để sink biết nguồn mà không so sánh `UpstreamPath` chuỗi.
- `TeeAsync(HttpContent source, Stream dest, CancellationToken ct, bool captureCompletion = false)`
  — thêm tham số mặc định để call site và test cũ không phải sửa hết.
- `UsageCapture.TeeResult` thêm `string? CompletionJson`. Khi `captureCompletion = true`:
  - chat (tee/`TeeSseAsync`): cho từng dòng chạy qua `ChatSseAggregateState` ngoài việc quét
    usage; cuối stream trả `BuildCompletionJson()`. Non-stream: `CompletionJson` = nguyên thân
    JSON (đã có trong tay).
  - responses: dùng `ResponsesSseState` (nó đã bắt event terminal) → `CompletionJson` =
    `TerminalResponseJson`; nếu stream chết cụt không có terminal → `CompletionJson = null`.
- **`AggregateSseAsync` KHÔNG cần tham số mới** (`ProxyRequestHandler.cs:212-214` chọn
  `aggregate ? AggregateSseAsync : TeeAsync`, và aggregate mới là đường phổ biến của chat ở app
  này: client xin JSON nhưng fingerprint ép upstream stream). Đường đó **đã** trả completion
  đầy đủ, chưa cắt, trong `TeeResult.ResponseBody` (`ChatCompletionsProtocol.cs:105-108`) —
  sink dùng luôn giá trị đó. **Tuyệt đối không áp cap 1MB vào trong `ChatSseAggregateState`**:
  state đó đang dựng byte gửi cho client, cắt ở đó là cắt response của người dùng (vi phạm §5).
  Cap 1MB chỉ là điều kiện **bỏ record** phía export, xét trên kích thước chuỗi đã có.
- **Cap cho đường export**: `Dataset.MaxFieldBytes = 1 MB` (const, không phải setting), xét ở
  writer (§7.6). Vượt cap → bỏ record + đếm. Monitor giữ nguyên cap 64KB của nó — hai đường
  độc lập, không đổi hành vi UI. Lý do: completion > 1MB gần như chắc chắn là ảnh base64 hoặc
  stream bất thường, record nửa vời trong dataset train còn tệ hơn mất mẫu.
- Đường tee SSE khi bật export phải **dừng tích lũy** agregate khi vượt cap (chỉ để chặn RAM),
  đặt cờ để writer biết mà bỏ record — không được để StringBuilder lớn vô hạn trên một stream
  dài (đây là chỗ `UsageCapture.BodyCapChars` đang bảo vệ, đường export phải tự bảo vệ mình).

### 7.4 Shape một dòng JSONL

```json
{"v":1,"ts":"2026-11-06T10:12:03Z","request_id":"a1b2c3d4","endpoint":"chat",
 "model_requested":"opencode-free/gpt-x","model_upstream":"gpt-x-2026","mode":"balancer",
 "provider":"openrouter","account":"acct-1","client_key_id":3,
 "lane":"free_fingerprint","tools_rewritten":true,
 "finish_reason":"tool_calls","usage":{"prompt_tokens":1234,"completion_tokens":567},
 "messages":[{"role":"system","content":"..."},{"role":"user","content":"..."}],
 "tools":[{"type":"function","function":{"name":"bash","parameters":{}}}],
 "completion":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"bash","arguments":"{\"cmd\":\"ls\"}"}}]},
 "unknown_tool_calls":["bash"],
 "thread_hint":"9f2c1a7b44e0d1a6"}
```

- `messages`, `tools`: lấy nguyên giá trị property từ body gốc (không chuẩn hoá, không lọc).
  Body chat không có `tools` → bỏ hẳn key, không `null`.
- `completion`: message assistant (chat: từ agregate; responses: map từ `output` — xem §7.5).
  Nhiều item `message` trong `output` → nối `content` bằng `\n` thành MỘT message; có cả
  `function_call` → `completion` mang `content` + `tool_calls` cùng lúc; `output` rỗng →
  bỏ record + đếm `SkippedEmpty`. Quy tắc này giữ mục tiêu "một shape cho cả hai endpoint".
- `lane`: `"free_fingerprint"` khi `OpenCodeFreeFingerprint.IsApplicable(provider)` và account
  dùng key rỗng (free lane), `"keyed_fingerprint"` khi provider đó dùng key thật, còn lại
  `"plain"`. Ba giá trị để offline lọc theo provenance mà không cần biết tool name.
- `tools_rewritten`: `true` khi fingerprint thực sự đổi mảng `tools` (rename casing, xoá trùng,
  chèn decoy) — ghi để offline biết `tools` trong record là **góc nhìn của client**, không phải
  thứ model thấy (sidecar Go còn thay toàn bộ `tools` nữa, app không thấy được phần đó).
- **Amend 2026-11-12 (nhánh `fix/opencode-free-gate`, spec `2026-11-10-opencode-free-gate-v2-design.md` §7.2):**
  host không còn tự suy `tools_rewritten` từ body — đọc response header `X-Sidecar-Shaped`: `true` khi
  giá trị chứa `tools` (mọi request free-lane đã shaping), `false` cho title-gen **và lane keyed
  passthrough** (`none` — lane `keyed_fingerprint` giữ nguyên tên nhưng mang nghĩa "đi qua sidecar không
  shaping"). Response có `X-Sidecar-Title: synthesized` (sidecar tự sinh completion) **không được ghi
  record** và tăng counter mới `SkippedSynthetic` trong `DatasetStats` (dòng status i18n thêm `{7}`
  tương ứng; shape record trên đĩa không đổi).
- `unknown_tool_calls`: danh sách `function.name` xuất hiện trong `completion.tool_calls` nhưng
  **không có** trong `tools` đã ghi — tính **động** bằng cách so hai tập name trong chính record.
  Không hard-code tên tool nào (kể cả quartet `bash/glob/grep/read`), nên nó vẫn đúng với client
  OpenCode v2 dùng `execute`/`opencode.*` (xem §11). Rỗng → bỏ key.
- `usage`: `null` khi upstream không trả usage (vẫn ghi dòng, người dùng lọc sau).
- `thread_hint`: 16 hex ký tự đầu của SHA-256 trên `{role, content}` của **message đầu tiên**
  (cắt 4KB đầu) — đủ để nhóm/dedup offline các lượt của cùng một phiên, vì client không gửi id phiên.
- `ts` là UTC ISO-8601 của lúc request kết thúc (tee xong).

### 7.5 Chuẩn hoá `/v1/responses` → shape chat

`input` (array hoặc string) → `messages`; `instructions` → message `system` đứng đầu:

| Item Responses | Thành phần trong dataset |
|---|---|
| `{"type":"message","role","content":[{type:input_text/output_text,text}]}` | `{role, content}` (nhiều part → nối bằng `\n`) |
| `{"type":"function_call","name","arguments","call_id"}` | assistant `{content:null,"tool_calls":[{id:call_id,type:"function",function:{name,arguments}}]}` |
| `{"type":"function_call_output","call_id","output"}` | `{role:"tool","tool_call_id":call_id,"content":output}` |
| item `{role,content}` kiểu cũ | giữ nguyên |
| `input` là string | một message `user` |
| `{"type":"reasoning",...}` | không vào `messages` → `extra.reasoning` |
| `type` lạ không biết | không vào `messages` → `extra.items` (giữ nguyên object) |

Quy tắc: **không bỏ dữ liệu**, thứ không map được thì xuống `extra`; `extra` rỗng → bỏ key.
Đây là quyết định của agent, lý do: mất item lạ thì không còn cách khôi phục, còn bộ nạp dataset
gặp `extra` thì bỏ qua được.

### 7.6 Component mới

| File | Vai trò |
|---|---|
| `src/RouterBalancing.Core/Dataset/IDatasetSink.cs` | `void Capture(DatasetCapture c)`, `DatasetStats Stats { get; }`, `event Action? Changed` — hợp đồng fail-open |
| `src/RouterBalancing.Core/Dataset/DatasetSink.cs` | channel bounded + một writer task: mở file append theo ngày, ghi từng dòng, flush; xoay ngày; purge theo retention |
| `src/RouterBalancing.Core/Dataset/DatasetRecordBuilder.cs` | body gốc + completion JSON + route → object dòng JSONL (gồm chuẩn hoá responses, `thread_hint`, cap) |
| `src/RouterBalancing.Core/Dataset/DatasetPaths.cs` | `Resolve(string? setting)` → mặc định `StoragePathProvider.GetDataDirectory()/dataset`, expand `~` (helper nhỏ riêng, không dùng `OpenCodeConfigPaths` vì khác ngữ cảnh) |
| `src/RouterBalancing.Core/Dataset/DatasetStats.cs` | `RecordsWritten`, `SkippedTooLarge`, `SkippedTruncated`, `SkippedEmpty`, `SkippedRefusal`, `SkippedQueueFull`, `BytesWritten`, `CurrentFile` |

Settings keys (`SettingsKeys`): `datasetExportEnabled` (bool, mặc định `false`),
`datasetExportPath` (string, rỗng = mặc định), `datasetRetentionDays` (int, mặc định `0` = giữ mãi).

Vận hành:
- **`Capture` trên thread request làm ít nhất có thể**: kiểm gate, đo kích thước thô
  (`body.Length`, độ dài `CompletionJson`), rồi đẩy **tham chiếu** body thô + completion +
  metadata vào queue. Parse JSON, chuẩn hoá responses, tính `thread_hint`/`unknown_tool_calls`
  và serialize chạy trên **thread writer**. Lý do: anti-goal §4 — không được làm nghẽn forward;
  parse một body vài trăm KB + SHA-256 trên thread Kestrel là sai chỗ.
- Writer chạy trên một task nền, không IO nào trên thread request.
- Channel bounded **128 record** và chặn theo tổng byte (**32 MB** đang chờ) — quá thì bỏ + đếm
  `SkippedQueueFull`. Không có guard byte thì 128 record × 1MB là 128MB RAM, không chấp nhận được
  (đây chính là hệ quả của việc giữ body thô trong queue thay vì dựng dòng JSON sớm).
- **Những bản bị loại (đếm, không ghi)** — vì "record nửa vời độc hơn mất mẫu":
  - `SkippedTooLarge`: body hoặc completion vượt `MaxFieldBytes`.
  - `SkippedTruncated`: chat có `TeeResult.EndedWithoutTerminal = true` (upstream đóng SSE
    không `[DONE]`/`finish_reason` — `ProxyRequestHandler.cs:259-269` đã log Warning cho đúng
    trường hợp này), hoặc responses có terminal là `response.incomplete` / `response.failed`
    (`ResponsesSseState.cs:258`). Cả hai đều là completion cụt giữa tool_call arguments.
  - `SkippedEmpty`: `output`/`completion` rỗng, hoặc `CompletionJson` null.
  - `SkippedRefusal`: body từ chối của chính upstream lọt trong 2xx — xem §7.8.
- Lấy `mode` + `model_requested` (combo alias client gửi) từ `IApiMonitorStore.Find(requestId)`
  tại lúc capture: monitor đã giữ sẵn `Combo`/`Mode` từ trace feed
  (`ApiMonitorStore.OnPublished`), capture chạy ngay sau `monitor.RecordResponse` nên record còn
  trong ring. Find trả null (ring đã bay record) → để `null` hai field, không đoán.
  Lý do không đổi chữ ký `ForwardAsync`: nó sẽ kéo theo `DispatcherLoop` +
  `DispatcherLoopTests`/`ProxyRequestHandlerTests` sửa theo, không đáng cho hai field phụ.
- Xoay ngày khi `TimeProvider.GetUtcNow().UtcDate` đổi (cùng quy tắc ngày UTC với stats journal);
  file mới cùng tên theo ngày mới. Khởi động lại trong ngày → append vào file đang có.
- Purge: khi bắt đầu và khi xoay ngày, xoá `requests-*.jsonl` cũ hơn `datasetRetentionDays`
  (chỉ khi `> 0`). Đặt trong sink thay vì một worker riêng vì nó chỉ cần chạy đúng hai thời điểm đó.
- `DisposeAsync` flush và đóng file khi app thoát.
- File hỏng/đĩa đầy/mất quyền ghi: log `Error` một lần cho mỗi loại lỗi (không spam mỗi dòng),
  chuyển sink về chế độ tắt cho tới khi Settings được lưu lại. Request vẫn chạy bình thường.

### 7.7 UI Settings

Trong `router-balancing/Components/Pages/SettingsPanel.razor`, một mục riêng (theo đúng khuôn
`openCodeConfigPath` đã có: field text + nút về mặc định + validate + toast):

- Toggle **Xuất dataset JSONL** (`datasetExportEnabled`).
- Field **Thư mục lưu** + nút "Về mặc định" → `%AppData%\router-balancing\dataset`.
- Number **Số ngày giữ** (0 = giữ mãi).
- Dòng trạng thái: `Đã ghi N dòng hôm nay · X MB · bỏ qua: M1 quá cỡ, M2 cụt, M3 rỗng, M4 từ chối, M5 đầy hàng đợi`
  — hiển thị đủ các counter của `DatasetStats` (bản đầu dịch gọn thành "bỏ qua M bản" + tooltip
  từng loại nếu hàng quá dài) + nút **Mở thư mục** (Windows: mở explorer vào thư mục).
- Cảnh báo: *nội dung prompt/response được ghi plaintext, không mã hóa* — vì ai đó có thể đọc
  cả code/dữ liệu của user trong đó.
- Keys i18n mới trong `router-balancing/wwwroot/translations/vi.json` + `en.json`.

### 7.8 Lọc refusal của upstream, và không hard-code tên tool OpenCode

Đây là phần giữ cho dataset còn dùng được khi upstream (OpenCode) đổi cách làm — xem §11.

- **`SkippedRefusal`**: upstream trả 2xx nhưng bên trong stream là lời từ chối chứ không phải
  completion. Ít nhất hai chuỗi đã ghi nhận thật ở upstream:
  `"free tier can only be used from within OpenCode"` và `"Upstream request failed: Invalid credential"`.
  Kiểm tra trên **completion** (không phải prompt) bằng so khớp chuỗi không phân biệt hoa/thường,
  đánh dấu là heuristic trong comment code + XML doc để người sau biết giới hạn của nó.
  Lý do bỏ hẳn thay vì ghi kèm flag: một record refusal trông như một lượt hội thoại hợp lệ nên
  nó lặng lẽ dạy model trả lời từ chối.
- **Không hard-code tên tool**: mọi quyết định loại/ghi chỉ dựa trên (a) tập name trong
  `tools` đã ghi và (b) tập name trong `completion.tool_calls`. `unknown_tool_calls` là hiệu hai
  tập đó. Không có danh sách tool constants nào trong `Core/Dataset`.
  Lý do: client OpenCode v1 gửi `bash/glob/grep/read`, client v2 gửi `execute` + tool namespace
  `opencode.*`; danh sách constants sẽ sai ngay khi upstream đổi, còn phép so sánh thì không.

### 7.9 Test

- `DatasetRecordBuilderTests`: chat body → `messages`/`completion` đúng; responses `input`/`output`
  → map đúng từng loại item; item lạ + reasoning vào `extra`; `output` có `function_call` →
  `completion` có `tool_calls` (không phải record bị bỏ); `output` có nhiều `message` → content nối
  `\n`; `thread_hint` ổn định và khác nhau khi message đầu khác; body/completion quá cap → bỏ
  record + tăng counter; `unknown_tool_calls` đúng với bộ tool v1 (`bash`) **và** bộ tool v2
  (`execute`, `opencode.session_move`) — chứng minh không có constants nào; `lane` =
  `free_fingerprint` / `keyed_fingerprint` / `plain` đúng ba nhánh; completion chứa
  `"free tier can only be used from within OpenCode"` → `SkippedRefusal`.
- `DatasetSinkTests`: bật mới ghi, tắt không ghi gì và không tạo file; xoay ngày tạo file mới;
  append khi mở lại cùng ngày; retention `0` không xoá, `>0` xoá file cũ; queue đầy thì bỏ + đếm;
  `EndedWithoutTerminal=true` → `SkippedTruncated`, responses terminal `response.incomplete` →
  `SkippedTruncated`; sink không bao giờ ném (mô phỏng IO lỗi); `Capture` không được đụng JSON
  (test bằng body hỏng: vẫn enqueue, writer mới là nơi fail-open).
- `ChatCompletionsProtocolTests` / `ResponsesProtocolTests`: `captureCompletion=true` trả
  `CompletionJson` đủ nội dung + tool_calls ghép từ delta; `false` → `CompletionJson` null và
  các assert cũ không đổi.
- Integration (mirror `router balancing test/Server/ApiMonitorIntegrationTests.cs`): gọi endpoint
  qua TestServer với upstream giả, bật export vào `Temp` → đúng một dòng JSONL, request retry
  (429 rồi 2xx) vẫn đúng một dòng.

## 8. Quyết định của agent (không phải user chốt)

1. Shape `messages` + `completion` chuẩn hoá chung cho hai endpoint, item lạ xuống `extra`.
2. Chỉ ghi request 2xx thành công; canceled/lỗi không vào dataset.
3. Không mã hoá file dataset; UI có dòng cảnh báo.
4. Cap 1MB/trường là const, vượt thì bỏ record (không setting hoá ở bản đầu).
5. `thread_hint` = hash message đầu, để dedup offline; app không tự nối hội thoại.
6. Retention purge đặt trong sink, không thêm worker mới.
7. Guard bộ nhớ queue = 128 record + 32MB tổng.
8. `IProxyProtocol.Endpoint` mới thay vì so khớp `UpstreamPath`.
9. `completion` của responses là MỘT message chat (nối nhiều `message`, gộp `function_call`
   thành `tool_calls`), không phải mảng item — giữ "một shape cho cả hai endpoint".
10. Bản cụt/rỗng bị bỏ thay vì ghi (thêm counter), kể cả khi HTTP là 2xx — anti-goal §4 thắng.
11. `mode`/`model_requested` đọc qua `IApiMonitorStore.Find(requestId)`; không đổi chữ ký
    `ForwardAsync`/`DispatcherLoop`.
12. Dựng dòng JSONL trên thread writer; `Capture` chỉ đo size + đẩy tham chiếu.
13. `AggregateSseAsync` không nhận tham số mới; completion lấy từ `TeeResult.ResponseBody` của
    đường aggregate; cap 1MB không đụng vào `ChatSseAggregateState` (đang dựng byte cho client).
14. `lane` + `tools_rewritten` + `unknown_tool_calls` là cờ để user lọc offline; dataset không
    hard-code tên tool nào của OpenCode.

## 9. Còn để cho người làm

- Tên class/file cụ thể trong `Core/Dataset` nếu group quen đặt khác.
- Chi tiết layout CSS cho mục Settings mới (the pattern có sẵn của panel).
- Chọn `TimeProvider` injection hay dùng `TimeProvider.System` ở call site DI — theo pattern
  `ApiMonitorStore`.
- Cách lấy `lane` chính xác nhất: `IsApplicable(provider)` + account key rỗng là đủ ở hook,
  nhưng nếu sau này có thêm fingerprint khác (vd. `NvidiaRequestProfile`) thì mở rộng enum —
  giữ nguyên ba giá trị hiện có để file đã xuất không đổi nghĩa.

## 10. Khối lượng

Cỡ trung bình: 5 file mới trong `Core/Dataset` + 5 file sửa
(`IProxyProtocol`, `UsageCapture`, `ChatCompletionsProtocol`, `ResponsesProtocol`,
`ProxyRequestHandler`) + `SettingsKeys`/`IAppSettingsService`/`AppSettingsService`
+ mục Settings UI + 2 file i18n + 4 nhóm test. **Không** đụng `DispatcherLoop`, sidecar Go,
schema DB (không migration — setting mới chỉ là dòng `AppSettings`).

## 11. Ghi chú upstream OpenCode v2 (kiểm tra 2026-11-07, chưa xác minh live)

Lý do ghi vào design: user hỏi fingerprint hiện tại còn đúng không khi OpenCode đã có v2.

Đã xác minh từ nguồn mở `anomalyco/opencode` (repo của OpenCode, branch `dev`, còn hoạt động):

- v2 đang là bản phát hành (issue #57346 báo lỗi trên **2.0.32** và **2.1.0**, mở, cập nhật
  03/11/2026). Model tool của v2 khác v1: catalog là tool có namespace
  (`opencode.session_move`, `opencode.list_mcp_resources`, `opencode.worktree_*`) + tool
  **`execute`** kiểu code-mode (sandbox JS, model viết code gọi `tools.<name>`, tìm tool bằng
  `search`). Chính issue đó than `Read/Grep/Glob/Bash` không có trong catalog ở mọi mode.
- Gate free-tier vẫn trả `{"error":{"type":"FreeTierError","message":"OpenCode's free tier can
  only be used from within OpenCode"}}` (issue #54338, 10/10/2026, bản 1.18.32). Cũng issue đó:
  khi hết quota, v2 báo `AI_APICallError: Upstream request failed: Invalid credential` và
  **không** kèm `Retry-After`/header reset — upstream tự lẫn lộn "refusal" với "hết quota".

Hệ quả cho code này (đối chiếu tại thời điểm viết):

- `OpenCodeFreeFingerprint.Quartet = [bash, glob, grep, read]` (dòng 38) và
  `sidecar/opencode-proxy/anonymous-tools.json` = `shell, edit, glob, grep, read` là vocabulary
  của **v1**; nếu client là v2 thì `tools` trong body không chứa bốn tên đó.
- App phân biệt quota bằng `429 + body chứa "FreeUsageLimitError"` (`DispatcherLoop.cs:669-675`)
  và 401/403 → Fatal cấp Account (`RetryClassifier.cs:17`). Nếu upstream trả refusal/quota theo
  hình "Invalid credential" (401-shaped) thì cả hai rơi vào nhánh "key sai" thay vì quota.
  User ghi nhận đúng hiện tượng này.
- **Chưa xác minh được từ máy dev**: `%APPDATA%\router-balancing` không tồn tại ở đây nên không
  có log thật để đối chiếu, và không có probe live. Hai việc đó là một spike riêng (§12).

Thiết kế này cố tình **không** dựa vào tên tool OpenCode (§7.8) nên dataset vẫn đúng bất kể câu
trả lời của spike là gì.

## 12. Việc tách ra ngoài phạm vi tính năng này

Đã ghi thêm vào `docs/backlog.md`:

1. **Fingerprint OpenCode theo v2**: quartet + `anonymous-tools.json` có còn qua gate của
   opencode.ai với client v2 không; cần probe live (gửi request có/không có quartet, có UA
   `opencode/1.18.31` vs `2.x`) rồi mới quyết định cập nhật `Quartet`/tool set sidecar.
2. **Phân loại lỗi free-tier**: tách `FreeTierError` (refusal, không mở quota window) khỏi
   quota thật (`FreeUsageLimitError`, và cả dạng "Invalid credential" không header retry-after);
   hiện chỉ có một dấu hiệu được nhận là quota toàn cục.
