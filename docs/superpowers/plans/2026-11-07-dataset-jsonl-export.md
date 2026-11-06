# Dataset JSONL Export Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Xuất mỗi request LLM thành công thành một dòng JSONL trên đĩa (thư mục chọn được, mặc định AppData) để người dùng làm dữ liệu train model.

**Architecture:** Một ống export riêng tách khỏi đường hiển thị API Monitor. `ProxyRequestHandler` gọi `IDatasetSink.Capture` một lần sau tee 2xx, đưa **body gốc của client** + completion dựng từ máy trạng thái SSE đã có sẵn trong protocol. Sink giữ queue bounded và một writer thread ghi file `requests-YYYY-MM-DD.jsonl`; mọi lỗi chỉ log (fail-open).

**Tech Stack:** .NET 10, C# (`System.Text.Json`, `System.Threading.Channels`), xUnit, .NET MAUI Blazor Hybrid cho UI Settings.

**Spec:** `docs/superpowers/specs/2026-11-06-dataset-jsonl-export-design.md` — plan này implement spec đó; đọc cả hai.

## Global Constraints

- Bật `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên (AGENTS.md).
- XML doc (`///`) bắt buộc cho public/protected type/method; comment `//` giải thích "tại sao" bằng tiếng Việt, không dùng `/* */` trong C#.
- Không dependency mới — chỉ BCL + những package repo đang dùng.
- Không migration DB: setting mới chỉ là dòng trong bảng `AppSettings` (key/value JSON).
- Mọi component mới trong pipeline request phải fail-open: không bao giờ ném ra caller (pattern `ApiMonitorStore`/`TraceFeed`).
- Build: `dotnet build router-balancing.slnx`. Test: `dotnet test "router balancing test/router balancing test.csproj"` (thư mục test có dấu cách — luôn bọc nháy kép).
- Commit message tiếng Anh, conventional commit (`feat:`/`test:`/`refactor:`/`docs:`).
- Tên dataset file: `requests-YYYY-MM-DD.jsonl` (UTC date), thư mục mặc định `%AppData%\router-balancing\dataset`.

## Review Focus

Bốn lớp input mà test của từng task chưa chắc chạm, nhưng hỏng là dữ liệu train hỏng:

1. **Body/completion đúng ngưỡng 1MB** (±1 byte) — phải ghi đúng ở `MaxFieldBytes - 1`, bỏ ở `MaxFieldBytes`. Test ở Task 3.
2. **`CompletionJson` là cả object chat.completion (non-stream + aggregate) chứ không phải message** — builder phải tự mò `choices[0].message`; nếu không, mọi bản chat non-stream bị loại hoặc rỗng. Test ở Task 3.
3. **`completion` có `tool_calls` nhưng `content` rỗng/null** — đây là mẫu hợp lệ, KHÔNG được tính là "rỗng". Test ở Task 3.
4. **Đĩa lỗi giữa chừng (file bị khoá / hết dung lượng / quyền ghi)** — request đang phục vụ vẫn chạy bình thường, sink ngừng ghi và log đúng một lần, không spam mỗi dòng. Test ở Task 4.
5. **Thư mục đích chưa tồn tại hoặc không tạo được** — tự tạo khi bật; tạo không được thì tắt export + log một lần, không nổ UI. Test ở Task 4.

---

### Task 1: Setting keys + service + draft + validator

**Files:**
- Modify: `src/RouterBalancing.Core/Settings/SettingsKeys.cs`
- Modify: `src/RouterBalancing.Core/Settings/IAppSettingsService.cs`
- Modify: `src/RouterBalancing.Core/Settings/AppSettingsService.cs`
- Modify: `src/RouterBalancing.Core/Settings/SettingsDraft.cs`
- Modify: `src/RouterBalancing.Core/Settings/SettingsValidator.cs`
- Test: `router balancing test/Settings/SettingsValidatorTests.cs`

**Interfaces:**
- Consumes: `IAppSettingsService.Get<T>/Set<T>` (đã có).
- Produces: `IAppSettingsService.DatasetExportEnabled { get; }` (bool, mặc định `false`); `IAppSettingsService.DatasetExportPath { get; }` (string, `""` = chưa set); `IAppSettingsService.DatasetRetentionDays { get; }` (int, mặc định `0`); `SettingsKeys.DatasetExportEnabled = "datasetExportEnabled"`, `SettingsKeys.DatasetExportPath = "datasetExportPath"`, `SettingsKeys.DatasetRetentionDays = "datasetRetentionDays"`; ba property tương ứng trên `SettingsDraft`.

- [ ] **Step 1:** Trong `SettingsValidatorTests`, thêm ba test fail-first:
      `Validate_DatasetRetentionDaysZero_AcceptsZeroAsKeepForever`,
      `Validate_DatasetRetentionDaysAbove3650_ReturnsError` (error key `settings.error.datasetRetention`),
      `Validate_DatasetExportPathTooLong_ReturnsError` (error key `settings.error.datasetPath`, ngưỡng 260 ký tự).
- [ ] **Step 2:** Chạy `dotnet test "router balancing test/router balancing test.csproj" --filter FullyQualifiedName~SettingsValidatorTests` — expectation: FAIL (property chưa tồn tại, không compile được).
- [ ] **Step 3:** Thêm 3 key vào `SettingsKeys`, 3 property vào `SettingsDraft` (default `false` / `""` / `0`), 3 property read-only vào `IAppSettingsService`, và implement trong `AppSettingsService` theo đúng khuôn `LogRetentionDays` (`Get(SettingsKeys.X, default)`). Thêm 2 rule vào `SettingsValidator.Validate`: `DatasetRetentionDays is < 0 or > 3650`, `DatasetExportPath.Length > 260`.
- [ ] **Step 4:** Thêm test round-trip vào `router balancing test/Settings/AppSettingsServiceTests.cs` (mirror test `OpenCodeConfigPath` hiện có): `Set` rồi đọc `DatasetExportEnabled`/`DatasetRetentionDays`/`DatasetExportPath` qua service mới cùng DB phải trả đúng giá trị.
- [ ] **Step 5:** Chạy lại filter ở Step 2 + `--filter FullyQualifiedName~AppSettingsServiceTests` — expectation: PASS.
- [ ] **Step 6:** Commit: `git add -A && git commit -m "feat: add dataset export settings keys and validation"`

---

### Task 2: Kiểu giá trị + giải đường dẫn

**Files:**
- Create: `src/RouterBalancing.Core/Dataset/DatasetLane.cs`
- Create: `src/RouterBalancing.Core/Dataset/DatasetSkipReason.cs`
- Create: `src/RouterBalancing.Core/Dataset/DatasetStats.cs`
- Create: `src/RouterBalancing.Core/Dataset/DatasetCapture.cs`
- Create: `src/RouterBalancing.Core/Dataset/DatasetPaths.cs`
- Test: `router balancing test/Dataset/DatasetPathsTests.cs`

**Interfaces:**
- Consumes: `RouterBalancing.Core.Storage.StoragePathProvider.GetDataDirectory()` (đã có), `RouterBalancing.Core.Engine.ProxyEndpoint` (enum đã có: `Chat`, `Responses`).
- Produces (dùng ở Task 3/4/7 — giữ đúng tên và kiểu):
  - `public enum DatasetLane { Plain, FreeFingerprint, KeyedFingerprint }`
  - `public enum DatasetSkipReason { TooLarge, Truncated, Empty, Refusal }`
  - `public sealed record DatasetStats(int RecordsWritten, int SkippedTooLarge, int SkippedTruncated, int SkippedEmpty, int SkippedRefusal, int SkippedQueueFull, long BytesWritten, string? CurrentFile)`
  - `public sealed record DatasetCapture(string RequestId, ProxyEndpoint Endpoint, DateTimeOffset FinishedAt, byte[] ClientBody, string? CompletionJson, bool CompletionComplete, string Provider, string Account, string ModelUpstream, string? ModelRequested, string? Mode, long? ClientKeyId, int? PromptTokens, int? CompletionTokens, DatasetLane Lane, bool ToolsRewritten)`
  - `public static class DatasetPaths` với `const string FileNamePrefix = "requests-"`, `const string FileNameExtension = ".jsonl"`, `static string DefaultDirectory()`, `static string Resolve(string? setting)`, `static string FileName(DateOnly utcDay)`.

- [ ] **Step 1:** Test fail-first trong `DatasetPathsTests`:
      `Resolve_EmptyOrNull_ReturnsAppDataSubfolder` (bằng `Path.Combine(StoragePathProvider.GetDataDirectory(), "dataset")`),
      `Resolve_TildePath_ExpandsUserProfile` (`~/rb-ds` → `Path.Combine(UserProfile, "rb-ds")`, separator chuẩn hoá như `OpenCodeConfigPaths.Expand`),
      `Resolve_AbsolutePath_KeptVerbatim`,
      `FileName_UsesUtcDayAndJsonlExtension` (`= "requests-2026-11-06.jsonl"`).
- [ ] **Step 2:** Chạy `--filter FullyQualifiedName~DatasetPathsTests` — expectation: FAIL (chưa compile).
- [ ] **Step 3:** Tạo 5 file. `DatasetPaths.Resolve` sao chép logic expand `~` của `Tools/OpenCodeConfigPaths.cs` (viết helper riêng trong `DatasetPaths`, comment chỉ rõ vì sao không tái dùng `OpenCodeConfigPaths`: khác ngữ cảnh file/dir).
- [ ] **Step 4:** Chạy filter ở Step 2 — expectation: PASS.
- [ ] **Step 5:** Commit: `git commit -am "feat: add dataset export value types and path resolution"`

---

### Task 3: `DatasetRecordBuilder` — chuẩn hoá, cờ, rules bỏ record

**Files:**
- Create: `src/RouterBalancing.Core/Dataset/DatasetRecordBuilder.cs`
- Create: `src/RouterBalancing.Core/Dataset/DatasetBuildResult.cs`
- Test: `router balancing test/Dataset/DatasetRecordBuilderTests.cs`

**Interfaces:**
- Consumes: `DatasetCapture`, `DatasetSkipReason`, `DatasetLane` (Task 2).
- Produces:
  - `public sealed record DatasetBuildResult(string? JsonLine, DatasetSkipReason? Skip)`
  - `public static class DatasetRecordBuilder` với `const int MaxFieldBytes = 1024 * 1024`, `static DatasetBuildResult Build(DatasetCapture capture)`.
  - Khoá JSON line (schema `v:1`): `v`, `ts`, `request_id`, `endpoint` (`"chat"`|`"responses"`), `model_requested`, `model_upstream`, `mode`, `provider`, `account`, `client_key_id`, `lane`, `tools_rewritten`, `finish_reason`, `usage{prompt_tokens,completion_tokens}`, `messages`, `tools`, `completion`, `unknown_tool_calls`, `extra`, `thread_hint`. Field `null`/rỗng thì **bỏ hẳn key**, không ghi `null` (trừ `completion.content` — giữ `null` tường minh theo shape OpenAI khi có `tool_calls`).

- [ ] **Step 1:** Test fail-first — chat, `CompletionJson` là **cả object** `chat.completion`:
      `Build_ChatNonStreamCompletion_ExtractsChoiceMessage`
      (body `{"model":"m","messages":[{"role":"user","content":"hi"}]}`, completion
      `{"id":"1","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":3}}`
      → dòng JSON có `completion.content == "hello"`, `finish_reason == "stop"`, `usage.prompt_tokens == 2`, `endpoint == "chat"`, `messages[0].content == "hi"`).
- [ ] **Step 2:** Test fail-first — completion có `tool_calls` mà `content` null:
      `Build_ChatToolCallCompletion_ToolCallsPreserved_NotSkippedEmpty`
      → `Skip is null`, `completion.tool_calls[0].function.name == "bash"`, `unknown_tool_calls` chứa `"bash"` khi body không khai báo tool nào.
- [ ] **Step 3:** Test fail-first — `unknown_tool_calls` với bộ tool OpenCode v2:
      `Build_OpenCodeV2ToolNames_UnknownSetMatches` (body khai báo `execute` + `opencode.session_move`, completion gọi `execute` + `read_file` → `unknown_tool_calls == ["read_file"]`) và
      `Build_ChatV1Quartet_KnownToolsProduceNoUnknownKey` (completion gọi `bash` khi body có `bash` → **không có** key `unknown_tool_calls`).
- [ ] **Step 4:** Test fail-first — responses normalisation (§7.5 của spec):
      `Build_ResponsesInputItems_MapToChatMessages` (`instructions` → message `system` đầu; item `message` nhiều part → nối `\n`; item `function_call` → assistant `tool_calls`; item `function_call_output` → `{role:"tool", tool_call_id, content}`; `input` là string → một message `user`),
      `Build_ResponsesOutputWithFunctionCall_CompletionHasToolCalls`,
      `Build_ResponsesMultipleMessageItems_ContentJoinedByNewline`,
      `Build_ResponsesReasoningItem_GoesToExtraNotMessages`.
- [ ] **Step 5:** Test fail-first — rules bỏ record (§7.6 + Review Focus 1):
      `Build_CompletionIncomplete_SkipsTruncated` (`CompletionComplete=false`),
      `Build_EmptyCompletionJson_SkipsEmpty`,
      `Build_BodyAtMaxFieldBytes_Writes` (body dài đúng `MaxFieldBytes - 10` byte → `Skip is null`),
      `Build_BodyOverMaxFieldBytes_SkipsTooLarge`,
      `Build_CompletionRefusalPhrase_SkipsRefusal` (completion chứa `"OpenCode's free tier can only be used from within OpenCode"`, kiểm không phân biệt hoa/thường),
      `Build_CompletionInvalidCredentialPhrase_SkipsRefusal`.
- [ ] **Step 6:** Test fail-first — `thread_hint` ổn định:
      `ThreadHint_SameFirstMessage_SameHint` và `ThreadHint_DifferentFirstMessage_DifferentHint`;
      `Build_ClientBodyInvalidJson_SkipsEmpty` (không được ném exception).
- [ ] **Step 7:** Chạy `--filter FullyQualifiedName~DatasetRecordBuilderTests` — expectation: FAIL toàn bộ.
- [ ] **Step 8:** Implement `DatasetRecordBuilder.Build`. Ba điểm cần quyết định rõ trong code:
      (a) parse `ClientBody` một lần bằng `JsonDocument`, lấy nguyên `messages`/`tools` qua `GetRawText()` rồi `JsonNode.Parse` để chèn vào line (giữ byte-for-byte ngữ nghĩa, không tự escape lại cả body);
      (b) với `endpoint == Chat`, completion lấy từ `choices[0].message` (object), không phải cả object response;
      (c) `unknown_tool_calls` = hiệu hai tập tên (`tools[].function.name` ∪ `completion.tool_calls[].function.name`) — **không** có danh sách tool constants nào trong file này; comment XML ghi rõ heuristic refusal là heuristic.
      `thread_hint` = 16 hex đầu của SHA-256 trên UTF-8 của chuỗi `role + "\n" + content` của message đầu, cắt 4096 char đầu.
- [ ] **Step 9:** Chạy filter ở Step 7 — expectation: PASS.
- [ ] **Step 10:** Commit: `git commit -am "feat: build dataset JSONL records from proxied chat and responses traffic"`

---

### Task 4: `DatasetSink` — queue, writer thread, file theo ngày, retention

**Files:**
- Create: `src/RouterBalancing.Core/Dataset/IDatasetSink.cs`
- Create: `src/RouterBalancing.Core/Dataset/DatasetSink.cs`
- Test: `router balancing test/Dataset/DatasetSinkTests.cs`

**Interfaces:**
- Consumes: `DatasetRecordBuilder.Build` + `DatasetBuildResult` (Task 3), `DatasetPaths` (Task 2), `IAppSettingsService` (Task 1), `ILogService` (đã có), `TimeProvider` (đã có), `ManualTimeProvider` trong test project (`router balancing test/Engine/ManualTimeProvider.cs`).
- Produces:
  - `public interface IDatasetSink { event Action Changed; void Capture(DatasetCapture capture); DatasetStats Stats { get; } }` — hợp đồng: `Capture` không bao giờ ném, không IO.
  - `public sealed class DatasetSink : IDatasetSink, IAsyncDisposable` với ctor `DatasetSink(IAppSettingsService settings, ILogService log, TimeProvider time)`.
  - Hằng số nội bộ: `QueueDepthLimit = 128`, `QueueByteLimit = 32 * 1024 * 1024`, `MaxIoErrorLog = 1`.

- [ ] **Step 1:** Test fail-first (harness: `ManualTimeProvider` + `NullLog` + `AppSettingsService` trên `TestDb`, thư mục `Path.Combine(Path.GetTempPath(), "rb-ds-" + Guid.NewGuid().ToString("N"))`, luôn xoá ở `Dispose`):
      `Capture_DisabledExport_WritesNothingAndCreatesNoDirectory`,
      `Capture_TwoCaptures_AppendOneLineEachToTodaysFile` (đọc `File.ReadAllLines`, assert `request_id` của từng dòng và `v == 1`),
      `Capture_DayRollover_OpensNewFileForNewUtcDay` (đẩy `ManualTimeProvider` sang ngày hôm sau, assert có 2 file `requests-*.jsonl`).
- [ ] **Step 2:** Test fail-first — restart trong cùng ngày:
      `Constructor_SameDayExistingFile_AppendsDoesNotTruncate` (viết sẵn 1 dòng rồi tạo sink mới, assert 2 dòng).
- [ ] **Step 3:** Test fail-first — retention (§ spec §7.6):
      `Purge_RetentionZero_KeepsAllFiles`,
      `Purge_RetentionThreeDays_DeletesOlderFiles` (tạo file `requests-2026-11-01.jsonl`, clock 2026-11-07, `DatasetRetentionDays=3` → file cũ bị xoá, file hôm nay còn).
- [ ] **Step 4:** Test fail-first — counters + fail-open (Review Focus 1, 4, 5):
      `Capture_RecordTooLarge_IncrementsSkippedTooLarge_StillServing`,
      `Capture_QueueFull_DropsAndIncrementsSkippedQueueFull` (đẩy 129 capture khi writer bị chặn, assert `Stats.SkippedQueueFull >= 1` và không ném),
      `Capture_DestinationUnwritable_LogsOnceAndStopsWriting` (dùng một **file** thay cho thư mục ở đường dẫn đích → `Directory.CreateDirectory` ném; gọi `Capture` 5 lần, assert `ILogService.Error` được gọi đúng 1 lần và request path không ném),
      `Capture_StatsRecordsWrittenAndBytesWrittenIncrease`.
- [ ] **Step 5:** Chạy `--filter FullyQualifiedName~DatasetSinkTests` — expectation: FAIL toàn bộ.
- [ ] **Step 6:** Implement `DatasetSink`. Cấu trúc: `Channel<DatasetCapture>` bounded 128 (`FullMode.Wait` + `TryWrite` thủ công để tự đếm `SkippedQueueFull`), counter byte `Interlocked` (thêm khi enqueue, trừ khi dequeue, vượt `QueueByteLimit` → drop), một `_writerTask` khởi tạo lười trong `EnsureStarted()` (lock), vòng `await foreach` ghi `StreamWriter` (AutoFlush `true`), xoay ngày + purge khi `time.GetUtcNow().UtcDate` đổi. `Capture`: đọc `settings.DatasetExportEnabled` → false là return; compute kích thước thô (`capture.ClientBody.LongLength` + `capture.CompletionJson?.Length`) và `DatasetRecordBuilder.Build` **không** chạy ở đây — builder chạy trong writer loop (đọc từ capture) vì nó parse/SHA-256. Toàn bộ entry point bọc `try/catch` + `SafeLog` theo khuôn `ApiMonitorStore.SafeLog`.
- [ ] **Step 7:** Thêm test: `Sink_RecordsWrittenCounterCountsLineNotCapture` (một capture bị builder bỏ → `RecordsWritten` không tăng) rồi chạy lại filter — expectation: PASS.
- [ ] **Step 8:** Commit: `git commit -am "feat: write dataset records to daily JSONL files from a background sink"`

---

### Task 5: Seam protocol chat — `ChatSseAggregateState` dùng chung + `captureCompletion`

**Files:**
- Create: `src/RouterBalancing.Core/Engine/ChatSseAggregateState.cs`
- Modify: `src/RouterBalancing.Core/Engine/ChatCompletionsProtocol.cs` (xoá nested class cũ, dùng class mới; thêm `Endpoint`)
- Modify: `src/RouterBalancing.Core/Engine/IProxyProtocol.cs`
- Modify: `src/RouterBalancing.Core/Engine/UsageCapture.cs`
- Test: `router balancing test/Engine/UsageCaptureTests.cs`, `router balancing test/Engine/ChatCompletionsProtocolTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class ChatSseAggregateState(int? contentBudgetChars = null)` trong namespace `RouterBalancing.Core.Engine` — giữ **nguyên** members cũ (`Usage`, `FirstTokenAt`, `CompletedCleanly`, `HandleLine(ReadOnlySpan<byte>)`, `BuildCompletionJson()`), thêm `bool Overflowed { get; }` (true khi chạm `contentBudgetChars` và ngừng tích luỹ; `null` budget = không giới hạn, hành vi y hệt cũ).
  - `IProxyProtocol.Endpoint` (`ProxyEndpoint`), `IProxyProtocol.TeeAsync(HttpContent source, Stream dest, CancellationToken ct, bool captureCompletion = false)`.
  - `UsageCapture.TeeAsync(HttpContent source, Stream dest, CancellationToken ct, bool captureCompletion = false)`.
  - `UsageCapture.TeeResult(Usage? Usage, DateTimeOffset? FirstTokenAt, string? ResponseBody, bool EndedWithoutTerminal = false, string? CompletionJson = null, bool CompletionComplete = true)`.
- Consumes ở task sau: `TeeResult.CompletionJson` là **nguyên object** `chat.completion` (đường tee và aggregate), `CompletionComplete == false` khi stream cụt.

- [ ] **Step 1:** Test fail-first trong `UsageCaptureTests`:
      `Tee_Sse_CaptureCompletion_True_BuildsFullMessageFromDeltas`
      (SSE 3 chunk `data:` với `delta.content` `"He"`,`"llo"` + `finish_reason` ở chunk cuối + `data: [DONE]`, `captureCompletion: true` → `CompletionJson` parse được và `choices[0].message.content == "Hello"`, `CompletionComplete == true`),
      `Tee_Sse_CaptureCompletion_Default_NullCompletionJson` (hành vi cũ không đổi),
      `Tee_Sse_CaptureCompletion_StreamCuts_SetsCompletionCompleteFalse` (EOF không `[DONE]`/`finish_reason` → `EndedWithoutTerminal == true` và `CompletionComplete == false`).
- [ ] **Step 2:** Test fail-first: `Tee_Sse_CaptureCompletion_ContentOverBudget_SetsOverflowedAndKeepsClientBytes` — body stream có content > budget; assert **byte client nhận được vẫn nguyên vẹn** (dest bằng đúng SSE input ban đầu) và `CompletionComplete == false`. Đây là test chốt Review Focus "cap export không được cắt byte của client".
- [ ] **Step 3:** Test không đổi hành vi: chạy nguyên `ChatCompletionsProtocolTests` hiện có **trước khi** refactor (baseline xanh), và assert lại sau refactor ở Step 6.
- [ ] **Step 4:** Chạy `--filter FullyQualifiedName~UsageCaptureTests` — expectation: FAIL.
- [ ] **Step 5:** Tách `ChatSseAggregateState` ra file mới (git mv không cần thiết — copy nguyên khối class từ `ChatCompletionsProtocol.cs` dòng 111-230, đổi `private sealed` → `internal sealed`, thêm budget + `Overflowed` ở chỗ đang `Append` content và ở `AccumulateToolCalls`), cho `ChatCompletionsProtocol.AggregateSseAsync` dùng class mới với budget `null`.
- [ ] **Step 6:** Sửa `UsageCapture`: `TeeAsync`/`TeeSseAsync` nhận `captureCompletion`; khi true, mỗi dòng SSE đưa vào `ChatSseAggregateState` (budget `DatasetRecordBuilder.MaxFieldBytes`) ngoài `ScanSseLine`/`AppendSsePayload`; cuối stream `CompletionJson = state.BuildCompletionJson()` (chỉ khi `!state.Overflowed`), `CompletionComplete = state.CompletedCleanly`. Nhánh non-stream JSON: `CompletionJson = body` (nguyên thân, **không** cắt 64KB — `ResponseBody` vẫn cắt như cũ cho monitor).
- [ ] **Step 7:** Thêm `Endpoint` vào `ChatCompletionsProtocol` (trả `ProxyEndpoint.Chat`) và `IProxyProtocol`.
- [ ] **Step 8:** Chạy `--filter FullyQualifiedName~UsageCaptureTests|FullyQualifiedName~ChatCompletionsProtocolTests` — expectation: PASS (cả test cũ lẫn mới).
- [ ] **Step 9:** Commit: `git commit -am "refactor: share SSE chat aggregator and expose full completion for dataset export"`

---

### Task 6: Seam protocol responses — `CompletionJson` từ event terminal

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ResponsesProtocol.cs`
- Modify: `src/RouterBalancing.Core/Engine/ResponsesProtocol.cs` — `ResponsesSseState` thêm flag event terminal
- Test: `router balancing test/Engine/ResponsesProtocolTests.cs`

**Interfaces:**
- Consumes: `UsageCapture.TeeResult` chữ ký mới (Task 5).
- Produces: `ResponsesProtocol.Endpoint => ProxyEndpoint.Responses`; `TeeAsync(..., captureCompletion)` trả `CompletionJson` = object `response` lấy từ event terminal, `CompletionComplete = (event terminal là response.completed)`.

- [ ] **Step 1:** Test fail-first trong `ResponsesProtocolTests`:
      `Tee_Sse_CaptureCompletion_CompletionJsonIsTerminalResponseObject`
      (SSE có `event: response.output_text.delta` + `data:` vài lần, rồi `event: response.completed` + `data:{"type":"response.completed","response":{...,"output":[...],"usage":{...}}}` → `CompletionJson` parse ra object có `output` và `usage`, `CompletionComplete == true`),
      `Tee_Sse_CompletedFalseWhenTerminalIncomplete` (`event: response.incomplete` → `CompletionComplete == false`),
      `Tee_Sse_CaptureCompletion_False_LeavesCompletionJsonNull` (hành vi cũ).
- [ ] **Step 2:** Chạy `--filter FullyQualifiedName~ResponsesProtocolTests` — expectation: FAIL.
- [ ] **Step 3:** Implement: trong `ResponsesSseState` thêm `bool CompletedOk` (true khi event terminal tên `response.completed`, giữ `false` nếu chỉ có `response.incomplete`/`response.failed`); `ResponsesProtocol.TeeAsync` nhận `captureCompletion` và set `CompletionJson = captureCompletion ? state.TerminalResponseJson : null`, `CompletionComplete = state.CompletedOk`. Không đổi `CaptureHead` (cap 64KB cho monitor). Thêm `Endpoint`.
- [ ] **Step 4:** Chạy filter ở Step 2 — expectation: PASS.
- [ ] **Step 5:** Commit: `git commit -am "feat: capture responses-api terminal object as dataset completion"`

---

### Task 7: Nối vào pipeline — gate, capture, DI, shutdown

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs`
- Modify: `src/RouterBalancing.Core/Providers/OpenCodeFreeFingerprint.cs`
- Modify: `router-balancing/MauiProgram.cs`
- Modify: `router-balancing/App.xaml.cs`
- Test: `router balancing test/Engine/OpenCodeFreeFingerprintTests.cs` (thêm), `router balancing test/Engine/DispatcherLoopTests.cs` (sửa ctor)

**Interfaces:**
- Consumes: `IDatasetSink.Capture` + `DatasetCapture` + `DatasetLane` (Task 2/4), `TeeResult.CompletionJson`/`CompletionComplete` (Task 5/6), `IProxyProtocol.Endpoint` (Task 5/6).
- Produces:
  - `ProxyRequestHandler(..., IAppSettingsService settings, IDatasetSink dataset)` — thêm **tham số cuối** vào primary ctor (call site test duy nhất: `router balancing test/Engine/DispatcherLoopTests.cs:127`).
  - `internal static bool OpenCodeFreeFingerprint.RewritesTools(byte[] body)`.

- [ ] **Step 1:** Test fail-first cho fingerprint: `RewritesTools_ClientDeclaresBashWithUpperCase_ReturnsTrue` (body có `Bash` → rename), `RewritesTools_BodyWithoutTools_ReturnsFalse` (không có `tools` nhưng fingerprint sẽ chèn decoy → **true**; nếu code hiện tại chỉ chèn decoy khi có `tools` thì đọc lại `ApplyTools` và chốt test theo hành vi thật, comment ghi rõ hành vi nào được kiểm chứng), `RewritesTools_ExactQuartetNames_ReturnsFalse`, `RewritesTools_InvalidJson_ReturnsFalse`.
- [ ] **Step 2:** Chạy `--filter FullyQualifiedName~OpenCodeFreeFingerprintTests` — expectation: FAIL.
- [ ] **Step 3:** Implement `RewritesTools` bằng cách parse body ra `JsonObject` rồi gọi **chính** `ApplyTools(copy, out _)` đang dùng trong `TransformBody` (không nhân bản logic so sánh), trả về cờ `changed`.
- [ ] **Step 4:** Chạy filter ở Step 2 — expectation: PASS.
- [ ] **Step 5:** Trong `ProxyRequestHandler`: thêm `IDatasetSink dataset` vào primary ctor; đầu `ForwardAsync` thêm `var datasetOn = settings.DatasetExportEnabled;`; chuyển `datasetOn` vào `protocol.TeeAsync(..., captureCompletion: datasetOn)`; ngay trước `return new DispatchOutcome.Handled();` gọi (biến `aggregate` đã tồn tại ở dòng ~180 của file hiện tại):
      ```csharp
      if (datasetOn)
      {
          // aggregate = client non-stream nhưng upstream bị ép stream: AggregateSseAsync đã trả
          // completion hoàn chỉnh trong ResponseBody, không có CompletionJson (nó không nhận
          // captureCompletion). Lấy ResponseBody ở nhánh đó — không thì mọi request kiểu này
          // bị sink bỏ vì CompletionJson null.
          var completion = aggregate ? tee.ResponseBody : tee.CompletionJson;
          var monitorRecord = monitor.Find(requestId);
          dataset.Capture(new DatasetCapture(requestId, protocol.Endpoint, DateTimeOffset.UtcNow, body,
              completion, tee.CompletionComplete, provider.Name, account.Name, model.ModelId,
              monitorRecord?.Model, monitorRecord?.Mode, ClientKeyItems.IdOf(ctx),
              tee.Usage?.PromptTokens, tee.Usage?.CompletionTokens,
              LaneFor(provider, account), OpenCodeFreeFingerprint.RewritesTools(body)));
      }
      ```
      với `private static DatasetLane LaneFor(Provider provider, ProviderAccount account)` — `Plain` khi `!OpenCodeFreeFingerprint.IsApplicable(provider)`, ngược lại `FreeFingerprint` khi `account.ApiKeyEncrypted` rỗng còn `KeyedFingerprint`. `body` ở đây là **body gốc** của tham số `ForwardAsync`, không phải `requestBody` đã rewrite (comment giải thích vì sao).
- [ ] **Step 6:** Sửa `DispatcherLoopTests.cs:127` truyền `new DatasetSink(...)`. Dùng sink thật với settings tắt (để không ghi file):
      `new DatasetSink(_settings, log, TimeProvider.System)` và settings đã `Set(SettingsKeys.DatasetExportEnabled, false)`.
- [ ] **Step 7:** Ghi vào `router-balancing/MauiProgram.cs`: `builder.Services.AddSingleton<IDatasetSink, DatasetSink>();` cạnh `IApiMonitorStore` (dòng ~72). Ghi vào `router-balancing/App.xaml.cs`: thêm param ctor `DatasetSink dataset` + field `_dataset`, và cạnh `_retention.DisposeAsync()...Wait(2s)` (dòng ~303) thêm `_dataset.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));` — comment: flush dòng cuối trước khi process thoát.
- [ ] **Step 8:** Chạy `dotnet build router-balancing.slnx` — expectation: build 0 warning mới; rồi chạy toàn bộ test: `dotnet test "router balancing test/router balancing test.csproj"` — expectation: PASS.
- [ ] **Step 9:** Commit: `git commit -am "feat: capture dataset records at the end of a successful forward"`

---

### Task 8: Integration test đầu-cuối qua TestServer

**Files:**
- Create: `router balancing test/Server/DatasetExportIntegrationTests.cs`
- Test: chính file này

**Interfaces:**
- Consumes: harness trong `router balancing test/Server/ApiMonitorIntegrationTests.cs` (`TestDb`, `AppSettingsService`, `LogService`, `DpapiSecretProtector`, `SeedProvider`, `StubUpstream`/`ScriptedUpstream`, `StartAsync(IUpstreamClient)`), `IDatasetSink`.

- [ ] **Step 1:** Sao chép harness của `ApiMonitorIntegrationTests.cs` (ctor `TestDb`/`_settings`/`_log`/`_protector`, `SeedProvider`, `ScriptedUpstream`, `StartAsync`) sang `DatasetExportIntegrationTests`, có đăng ký sẵn `DatasetSink` chỉ vào thư mục temp (`_settings.Set(SettingsKeys.DatasetExportPath, _dir)` + `DatasetExportEnabled=true`).
- [ ] **Step 2:** Test `ChatSuccess_WritesExactlyOneDatasetLine`: upstream trả SSE chat 2xx một message hoàn chỉnh → `File.ReadAllLines` trên `requests-<utc ngày chạy>.jsonl` có **đúng 1 dòng**, `request_id` khớp id pipeline trả về, `endpoint == "chat"`, `completion.content` khớp.
- [ ] **Step 3:** Test `TransientRetryThenSuccess_WritesOneLineNotTwo`: `ScriptedUpstream` trả 429 cho attempt đầu (`_settings.Set(SettingsKeys.TransientMaxRetries, 1)`), SSE 2xx cho attempt sau → đúng 1 dòng, `provider`/`account` là attempt thắng.
- [ ] **Step 4:** Test `ClientNonStreamUpstreamForcedToSse_AggregatedCompletionStillWritesOneLine`: client gửi body **không** có `"stream": true`, `ScriptedUpstream` trả `text/event-stream` (đúng nhánh `aggregate` của handler, đường phổ biến khi fingerprint ép stream) → file có 1 dòng và `completion.content` khớp, tức `CompletionJson` được lấy từ `ResponseBody` của `AggregateSseAsync` chứ không null.
- [ ] **Step 5:** Test `ResponsesSuccess_WritesNormalizedMessages`: upstream trả SSE `/v1/responses` với terminal `response.completed` → 1 dòng, `endpoint == "responses"`, `messages` có message user, `completion` map từ `output`.
- [ ] **Step 6:** Test `Upstream400_WritesNoDatasetLine` (mirror `Upstream400_MonitorRecordErrorWithBodyAndErrorState`) → file không tồn tại hoặc rỗng.
- [ ] **Step 7:** Chạy `--filter FullyQualifiedName~DatasetExportIntegrationTests` — expectation: PASS (các test đã viết ở bước 1-6; nếu fail tức là wiring Task 7 sai, sửa wiring chứ không sửa test).
- [ ] **Step 8:** Commit: `git commit -am "test: cover dataset export end to end through the proxy pipeline"`

---

### Task 9: UI Settings + i18n

**Files:**
- Modify: `router-balancing/Components/Pages/SettingsPanel.razor`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs`
- Test: `router balancing test/Settings/SettingsDraftTests.cs` (create — chỉ assert default, vì panel là UI)

**Interfaces:**
- Consumes: `SettingsDraft` 3 property (Task 1), `IDatasetSink.Stats` + `Changed` (Task 4), `DatasetPaths.Resolve` (Task 2), `SettingsValidator` 2 rule (Task 1).
- Produces: key i18n mới — `settings.section.dataset`, `settings.field.datasetEnabled`, `settings.field.datasetPath`, `settings.field.datasetRetention`, `settings.action.datasetDefaultPath`, `settings.action.datasetOpenFolder`, `settings.hint.datasetPlaintext`, `settings.hint.datasetForever`, `settings.status.datasetStats`, `settings.error.datasetPath`, `settings.error.datasetRetention`, `settings.msg.datasetSaved`.

- [ ] **Step 1:** Test fail-first trong file test mới: `SettingsDraft_DatasetDefaults_AreOffForever` (assert `DatasetExportEnabled == false`, `DatasetExportPath == ""`, `DatasetRetentionDays == 0`).
- [ ] **Step 2:** Chạy `--filter FullyQualifiedName~SettingsDraftTests` — expectation: PASS ngay (defaults đã có từ Task 1) — giữ test này làm chốt chống đổi default.
- [ ] **Step 3:** Thêm 12 key vào **cả hai** dictionary trong `Translations.cs` (`English` ~dòng 10, `Vietnamese` ~dòng 600), text Việt có dấu cho bản Việt, đặt cạnh cụm `settings.field.dbPath`.
- [ ] **Step 4:** Trong `SettingsPanel.razor`: thêm vào `LoadDraft()` 3 dòng map setting → draft; vào nhóm save phù hợp (`SaveData()`, dòng ~520) 3 dòng `Settings.Set(...)`; thêm markup mục mới theo đúng khuôn khối `openCodeConfigPath` (dòng ~66-82): checkbox `@bind="_draft.DatasetExportEnabled"`, input text `@bind="_draft.DatasetExportPath"` + nút "về mặc định" gọi `DatasetPaths.Resolve(null)`, input number `@bind="_draft.DatasetRetentionDays"`, `Error(nameof(SettingsDraft.DatasetExportPath))` / `Error(nameof(SettingsDraft.DatasetRetentionDays))` render như khối cũ, dòng trạng thái đọc `Dataset.Stats` (`@L["settings.status.datasetStats"]` với N/M/X điền tham số), nút mở thư mục.
- [ ] **Step 5:** Inject `IDatasetSink Dataset` vào panel; `OnInitialized` subscribe `Dataset.Changed += OnDatasetChanged`, handler bọc `try/catch` + `InvokeAsync(StateHasChanged)` theo khuôn `ApiMonitorPanel.OnChanged`; `Dispose` unsubscribe.
- [ ] **Step 6:** Chạy `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` — expectation: build PASS.
- [ ] **Step 7:** Bật app lên bằng tay theo checklist: `dotnet build` xong chạy app, Settings → bật export → gửi 1 request qua proxy bằng client bất kỳ → bấm "Mở thư mục" thấy `requests-<date>.jsonl` có 1 dòng; tắt export → request mới không thêm dòng. Ghi kết quả kiểm tra vào commit message.
- [ ] **Step 8:** Commit: `git commit -am "feat: add dataset export section to settings screen"`

---

## Không làm trong plan này

- `docs/backlog.md` E1 (probe live gate opencode.ai với tool kiểu v2) và E2 (tách `FreeTierError` khỏi quota) — hai việc riêng, đã ghi backlog.
- Dedup/nối hội thoại offline (`thread_hint` chỉ là móc nối).
- Xem lại dataset trong app, import lại file đã xuất, mã hoá file.
