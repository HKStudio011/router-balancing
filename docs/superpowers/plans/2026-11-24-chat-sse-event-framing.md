# Chat SSE Event Framing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Nhận diện terminal đúng theo SSE event cho Chat, bảo vệ tool call/training data và bổ sung LastByteGap mà không thay byte forwarding hay client contract.

**Architecture:** Thêm một `SseDataEventParser` nội bộ xử lý network chunks → SSE data payloads hoàn chỉnh, rồi dùng chung tại tee và non-stream aggregation. Các JSON parsers hiện có tiếp tục nhận payload với một `data:` prefix; ResponsesProtocol không bị đụng vì có event-name semantics riêng.

**Tech Stack:** .NET 10/C#, System.Text.Json, xUnit v3, existing `ChunkedStream` test helper.

**Spec:** `docs/superpowers/specs/2026-11-24-chat-sse-event-framing-design.md`

## Global Constraints

- Raw forwarding phải byte-for-byte; không retry sau content, không bịa `finish_reason`, không thêm dependency.
- `[DONE]` vẫn exact sau khi bỏ một optional space; hai spaces/tab không được tự nới thành terminal.
- `choices[1]` vẫn không được coi là terminal vì OpenCode chỉ đọc `choices[0]`.
- Dataset tiếp tục loại stream thật sự cụt/overflow; phải giữ nguyên tool name, arguments và finish reason cho multi-line events.
- `ResponsesProtocol` không thay đổi. Không ghi raw prompt/completion/file content vào LastByteGap logs.
- Viết test trước implementation và giữ RED evidence; không sửa production để làm test dễ hơn.

## Review Focus

- CR/LF bị tách giữa hai chunks không được tạo thêm event hoặc mất terminal — Task 1 test mọi byte một chunk.
- EOF không blank line vẫn phải flush pending line/event — Task 1 test `Finish()`.
- Multi-line `[DONE]` và pretty JSON phải dùng cùng event boundary — Task 2 test tee và aggregate.
- Hai spaces/tab và `choices[1]` không được biến thành terminal — Task 2 test tương thích với OpenCode.
- Dataset không được synthetic `stop` hoặc mất tool call khi input hợp lệ — Task 2 kiểm `CompletionJson`.
- LastByteGap chỉ xuất hiện khi EOF thiếu terminal, không lộ nội dung — Task 2 assert property/log.

---

### Task 1: Tạo SSE event parser theo chunks

**Files:**
- Create: `src/RouterBalancing.Core/Engine/SseDataEventParser.cs`
- Create: `router balancing test/Engine/SseDataEventParserTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  internal sealed class SseDataEventParser
  {
      public List<byte[]> HandleChunk(ReadOnlySpan<byte> chunk);
      public byte[]? Finish();
  }
  ```
  Mỗi returned payload có đúng một `data:` prefix; `Finish()` trả pending event cuối hoặc `null`.

- [x] **Step 1: Viết parser tests trước implementation**

Tạo `SseDataEventParserTests.cs` với independent expected payloads:

1. Hai `data:` lines cùng event được ghép bằng `\n`; `event:`/`id:`/comments bị bỏ.
2. `[DONE]` chia `data: [DO` + `data: NE]` được nhận.
3. CRLF và CR, kể cả CR và LF nằm ở hai `HandleChunk` khác nhau.
4. Feed toàn bộ multi-line tool call một byte mỗi lần: output giống feed một chunk.
5. `Finish()` flush tail không blank line; không phát event cho empty `data:` hoặc stream rỗng.
6. Chỉ bỏ một optional space: hai spaces/tab sau `data:` được giữ trong payload, không biến thành `[DONE]`.

Dùng literal fixtures tối thiểu; không gọi implementation để dựng expected values.

- [x] **Step 2: Chạy tests và xác nhận RED**

Run:

```powershell
dotnet test "router balancing test/router balancing test.csproj" --no-restore --filter "FullyQualifiedName~SseDataEventParserTests"
```

Expected: FAIL vì `SseDataEventParser` chưa tồn tại.

- [x] **Step 3: Implement parser tối thiểu**

Trong `SseDataEventParser.cs`, giữ `_line`, `_data`, `_pendingCr`. `HandleChunk` tách CR/LF/CRLF, ghép data fields cùng event bằng `\n`, blank line flush event. `Finish()` xử lý pending line rồi flush pending data. Chỉ return payload metadata; không parse JSON hay forward bytes ở class này.

- [x] **Step 4: Chạy parser tests và xác nhận GREEN**

Run cùng filter ở Step 2. Expected: tất cả PASS, output không có unexpected exception.

- [x] **Step 5: Commit Task 1**

```powershell
git add src/RouterBalancing.Core/Engine/SseDataEventParser.cs "router balancing test/Engine/SseDataEventParserTests.cs"
git commit -m "fix: parse chat SSE by event boundaries"
```

### Task 2: Tích hợp Chat tee, aggregation, dataset và LastByteGap

**Files:**
- Modify: `src/RouterBalancing.Core/Engine/UsageCapture.cs:45-46,123-215`
- Modify: `src/RouterBalancing.Core/Engine/ChatCompletionsProtocol.cs:62-112`
- Modify: `src/RouterBalancing.Core/Engine/UpstreamTruncatedException.cs`
- Modify: `src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs:222-227`
- Modify: `router balancing test/Engine/UsageCaptureTests.cs`
- Modify: `router balancing test/Engine/ChatCompletionsProtocolAggregateTests.cs`
- Modify: `router balancing test/Server/ProxyRetryIntegrationTests.cs`

**Interfaces:**
- Consumes: `SseDataEventParser.HandleChunk/Finish` từ Task 1.
- Produces:
  - `UsageCapture.TeeResult.LastByteGap { get; init; }` chỉ khi EOF thiếu terminal.
  - `UpstreamTruncatedException(TimeSpan? lastByteGap = null)` và `LastByteGap` property.
  - Handler truyền gap từ tee vào exception; message chứa `silence_gap=<seconds>s` khi có.

- [x] **Step 1: Viết integration regression tests trước production changes**

Trong `UsageCaptureTests` thêm:

1. Multi-line `write` + `finish_reason:"tool_calls"` không `[DONE]`: `EndedWithoutTerminal=false`, forward exact, `LastByteGap=null`.
2. Cùng input có `[DONE]`, `captureCompletion=true`: `CompletionComplete=true`; JSON giữ `id/model`, tool `write`, arguments và `finish_reason:"tool_calls"`; không synthetic `stop`.
3. Multi-line `[DONE]`, tail không blank line: `EndedWithoutTerminal=false`.
4. Hai spaces/tab `[DONE]` và finish chỉ ở `choices[1]`: vẫn `EndedWithoutTerminal=true`.
5. EOF thiếu terminal thật: `LastByteGap` không null, không âm và log không chứa fixture content.

Dùng `ChunkedStream` để feed toàn bộ multi-line tool call từng byte ở ít nhất một test. Trong `ChatCompletionsProtocolAggregateTests`, thêm multi-line write + `[DONE]`: aggregate trả JSON đầy đủ, không ném. Trong clean-EOF integration test, assert log có `silence_gap=` và vẫn đúng một `upstream_disconnected`.

- [x] **Step 2: Chạy integration tests và xác nhận RED**

Run:

```powershell
dotnet test "router balancing test/router balancing test.csproj" --no-restore --filter "FullyQualifiedName~UsageCaptureTests|FullyQualifiedName~ChatCompletionsProtocolAggregateTests|FullyQualifiedName~ProxyRetryIntegrationTests.Stream_WhenUpstreamEndsCleanlyMidStream"
```

Expected: FAIL vì multi-line terminal/dataset và LastByteGap chưa được xử lý.

- [x] **Step 3: Dùng parser tại hai call sites và thêm gap**

`UsageCapture.TeeSseAsync`: thay `carry`/physical-line loop bằng parser; vẫn write chunk trước metadata, gọi `ScanSseLine`, `AppendSsePayload`, `capture.HandleLine` cho từng event và `Finish()` ở EOF. Theo dõi byte cuối cùng đọc được và trả `LastByteGap` chỉ ở nhánh `EndedWithoutTerminal`.

`ChatCompletionsProtocol.AggregateSseAsync`: dùng cùng parser + `Finish()` rồi giữ `CompletedCleanly` guard hiện tại.

Thêm init-only `TeeResult.LastByteGap`, optional constructor/property của `UpstreamTruncatedException`, truyền từ handler và append `silence_gap` vào message khi có. Không đổi ResponsesProtocol hay positional parameters của `TeeResult`.

- [x] **Step 4: Chạy integration và related suites**

Run lại filter ở Step 2, sau đó:

```powershell
dotnet test "router balancing test/router balancing test.csproj" --no-restore --filter "FullyQualifiedName~UsageCaptureTests|FullyQualifiedName~ChatCompletionsProtocolAggregateTests|FullyQualifiedName~ProxyRetryIntegrationTests|FullyQualifiedName~ResponsesProtocolTests"
```

Expected: tất cả PASS, gồm clean EOF thật và existing single-line behavior.

- [x] **Step 5: Commit Task 2**

```powershell
git add src/RouterBalancing.Core/Engine/UsageCapture.cs src/RouterBalancing.Core/Engine/ChatCompletionsProtocol.cs src/RouterBalancing.Core/Engine/UpstreamTruncatedException.cs src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs "router balancing test/Engine/UsageCaptureTests.cs" "router balancing test/Engine/ChatCompletionsProtocolAggregateTests.cs" "router balancing test/Server/ProxyRetryIntegrationTests.cs"
git commit -m "fix: preserve multiline chat tool calls and EOF diagnostics"
```

### Task 3: Full verification và tài liệu

**Files:**
- Modify: `docs/superpowers/specs/2026-11-24-chat-sse-event-framing-design.md`
- Modify: `docs/superpowers/plans/2026-11-24-chat-sse-event-framing.md`

- [x] **Step 1: Chạy đầy đủ .NET verification**

```powershell
dotnet test "router balancing test/router balancing test.csproj"
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --no-restore
```

Expected: 1582+ tests PASS (tăng theo tests mới), Windows build 0 warnings/0 errors. Ghi rõ mọi pre-existing warning thay vì che giấu.

- [x] **Step 2: Chạy Go verification và diff checks**

Trong `sidecar/opencode-proxy`: `go test ./...`, `go vet ./...`. Từ repo root: `git diff --check`, `git status --short`. Expected: PASS, vet sạch, không file dataset/log/secret ngoài scope.

- [x] **Step 3: Cập nhật trạng thái/kết quả và commit docs**

Ghi kết quả verification vào spec/plan và chỉ commit hai file tài liệu:

```powershell
git add docs/superpowers/specs/2026-11-24-chat-sse-event-framing-design.md docs/superpowers/plans/2026-11-24-chat-sse-event-framing.md
git commit -m "docs: record chat SSE event framing fix"
```

- [ ] **Step 4: Independent whole-branch review (controller)**

Controller dispatch reviewer sau Task 3 report để kiểm event semantics, dataset, LastByteGap và phạm vi; Task 3 không tự dispatch subagent/reviewer. Mọi Critical/Important finding cần được xử lý trước khi đóng toàn bộ branch.
