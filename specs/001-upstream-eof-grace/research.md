# Research: Upstream EOF Grace

## Baseline

Dùng release `v1.12.3` làm baseline. Không lấy các dirty changes ngoài feature worktree làm nguồn sự thật, đặc biệt phải giữ exact terminal handling: `SequenceEqual("[DONE]"u8)`, một optional ASCII space, chỉ `choices[0]`, `finish_reason` string khác null và không chấp nhận multiline `[DO\nNE]`.

## Decision 1: Grace nằm trong Chat tee, không nằm ở handler/stream wrapper

**Chosen:** Thay nhánh EOF break trong `UsageCapture.TeeSseAsync` bằng state machine `Active → GraceAfterEof → Completed/TimedOut/ClientAborted/TransportFailed`.

**Rationale:** Đây là nơi duy nhất giữ đủ source, destination, parser, `wroteAny`, terminal, usage, monitor/dataset state và cancellation. `ProxyRequestHandler` chỉ nhận kết quả sau khi parser đã kết thúc; `IdleWatchdogStream` không biết terminal; Responses và non-stream có contract riêng.

**Alternatives rejected:** Grace trong handler (mất parser state), trong stream wrapper (không biết terminal và ảnh hưởng protocol khác), trong aggregate/Responses (phá scope).

## Decision 2: EOF probe không được phá hủy parser

**Chosen:** Thêm `SseDataEventParser.PeekFinish()` clone `_line`, `_data`, `_pendingCr` rồi gọi `Finish()` trên clone. Chỉ commit tail khi terminal hoặc khi đi đường final phù hợp; probe non-terminal khi vào grace bị bỏ.

**Rationale:** `Finish()` hiện flush và clear state. Nếu gọi ở EOF tạm thởi, byte resume không thể ghép tiếp chính xác. Clone giữ nguyên multiline/CR/LF/optional-space semantics và tránh duplicate usage/dataset/tool-call aggregation.

**Alternatives rejected:** Gọi `Finish()` rồi reconstruct parser, tự thêm `\n\n`, quét raw response text, hoặc apply tail rồi hy vọng aggregation idempotent.

## Decision 3: Truyền grace qua Chat-only capability theo request

**Chosen:** Thêm internal `IChatEofGraceTee` do `ChatCompletionsProtocol` implement. Handler chỉ dùng capability khi `aggregate == false`. `IProxyProtocol` và `ResponsesProtocol` giữ nguyên signature/behavior.

**Rationale:** `ProxyProtocols.Chat` là singleton nên không thể chứa setting mutable. Capability tránh đổi mọi protocol/test double và tránh ambient `AsyncLocal`.

**Tri-state:** `null` = legacy immediate-truncation caller; `Timeout.InfiniteTimeSpan` = setting `0`; positive `TimeSpan` = finite grace. Production Chat streaming luôn opt-in bằng setting đọc một lần cùng `idleSec`.

**Alternatives rejected:** Thêm tham số vào mọi `IProxyProtocol`, mutable singleton property, ambient context.

## Decision 4: Một polling tuần tự, monotonic deadline và cancellation-aware

**Chosen:** Poll 1 giây bằng một read duy nhất tại mỗi thờigian điểm; finite grace dùng monotonic timestamp. Khi còn dưới 1 giây chỉ chờ phần còn lại và có final read tại deadline. Deadline bao trùm cả delay lẫn read có thể block. Internal clock có monotonic timestamp + delay để test deterministic; production dùng `Stopwatch`/`Task.Delay`.

**Rationale:** Tránh quay vòng CPU và read song song. Client cancellation phải đi vào delay/read và OCE caller được propagate nguyên vẹn; khi deadline và cancellation cùng xảy ra phải ưu tiên kiểm tra caller trước khi báo timeout.

**Alternatives rejected:** Wall-clock `DateTime.Now`, polling task song song với normal reads, chỉ kiểm deadline giữa các read, hoặc dựa vào outer test timeout thay cho SLA.

## Decision 5: Tái sử dụng endpoint keep-alive, nhưng phải implement event buffering

**Chosen:** Giữ request chưa complete để `ProxyApp.RunStreamLoopAsync` tiếp tục keep-alive 5 giây. Đồng thởi thay immediate-forward bằng event-boundary buffering với pending cap 1 MiB, `upstreamBytes` và `contentBytes` riêng.

**Rationale:** Tee chưa return nên dispatcher chưa complete và endpoint vẫn chờ pipe/delay/completion. Không cần vòng keep-alive thứ hai. Tuy nhiên hiện `atLineStart` chỉ kiểm LF và drain forward mọi fragment, nên có thể gửi keep-alive sai boundary hoặc dán error vào partial event. Event buffering là cách duy nhất vừa drop fragment vừa gửi error độc lập đúng frame.

**Alternatives rejected:** Chỉ kiểm trailing LF, thêm newline trước error, suppress error khi mid-event (không thỏa timeout contract), hoặc bỏ qua phần buffering (không thỏa FR-007/FR-010).

**Boundary rules:** Chỉ flush event hoàn chỉnh; keep-alive chỉ khi wire ở boundary; abnormal timeout drop pending; client abort không flush; `contentBytes` chỉ tính event đã ghi, còn `upstreamBytes` tính mọi byte nhận để tránh misclassify partial-only response.

## Decision 6: Timeout có error code riêng nhưng vẫn là mid-content abort

**Chosen:** `TeeResult.TerminalWaitTimedOut` init-only → `ProxyRequestHandler` ném `UpstreamTerminalTimeoutException` → `DispatchOutcome.Aborted` có init-only flag → endpoint phát `timeout_waiting_for_terminal` qua `SseErrorEvent` builder mới. Monitor/trace giữ mid-content network failure; các lỗi khác giữ `upstream_disconnected`.

**Rationale:** Ngườig dùng cần phân biệt timeout chờ terminal với disconnect thông thường, nhưng vẫn phải giữ no-retry-after-content và không bịa `[DONE]`/`finish_reason`.

**Alternatives rejected:** Dùng lại `UpstreamTruncatedException`/chỉ `upstream_disconnected` cho mọi lỗi (mất phân biệt yêu cầu), hoặc map Fatal/Retryable (có thể replay request sau content).

## Decision 7: Dataset và terminal behavior không đổi

**Chosen:** Grace timeout giữ `EndedWithoutTerminal=true`, `TerminalWaitTimedOut=true`, `CompletionComplete=false`; handler không capture record. Resume với terminal thật mới được phép capture bình thường. Byte resume đi qua cùng đường forward/parse như byte ban đầu.

**Rationale:** Client chờ lâu không làm completion cụt trở nên hợp lệ. Không replay, không synthetic terminal và không thay đổi exact terminal rules.

## Decision 8: Test bằng scripted resumable stream và explicit long test

**Chosen:** Test double cho phép `content → temporary EOF → bytes/terminal`, ghi timestamps/read count/max concurrency và tôn trọng cancellation. Finite deadline dùng deterministic clock; outer timeout chỉ là deadlock guard. Thêm explicit long test 10 phút cho zero grace, không đưa vào suite mặc định.

**Rationale:** HTTP stream chuẩn thường EOF vĩnh viễn, nên resume phải được mô hình hóa tường minh. Polling cùng stream không phải replay; tạo HTTP request mới mới là replay bị cấm.

**Timing guidance:** Real-time integration chỉ assert lower bound hợp lý và margin rộng; precise deadline dùng deterministic clock. Cancellation SLA 250ms được assert riêng, với điều kiện stream tôn trọng token.

## Existing Tests Needing Contract Updates

- `ForwardAsync_WhenUpstreamEofWithIdleDisabled_LogsAppliedIdleTimeout` không thể tiếp tục mong immediate exception với setting `0`; chuyển kỳ vọng sang cancellation sau khi chứng minh grace đang hoạt động.
- Các UsageCapture/ProxyRetry test muốn immediate truncation phải dùng legacy no-grace option hoặc finite grace để không treo.
- `RunStreamLoop_DataLineNotTerminated_SkipsKeepAliveUntilBoundary` được thay bằng event-boundary expectations: fragment chưa forward, wire vẫn ở boundary nên keep-alive an toàn.
- Thêm partial + Aborted drain test và actual endpoint integration cho standalone `timeout_waiting_for_terminal`, no retry/no DONE/no dataset record.
