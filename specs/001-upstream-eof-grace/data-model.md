# Data Model: Upstream EOF Grace

## Grace Policy

| Canonical Field | Rule |
|---|---|
| `Grace Policy.GracePeriod` | `null` = legacy immediate-truncation caller; `Timeout.InfiniteTimeSpan` = setting `0`; positive `TimeSpan` = grace hữu hạn |
| `Grace Policy.PollInterval` | Cố định 1 giây; phần chờ cuối được giới hạn bởi deadline còn lại |
| `Grace Policy.KeepAliveInterval` | Dùng chu kỳ 5 giây hiện có của endpoint, chỉ ghi tại event boundary |
| `Grace Policy.PendingEventLimit` | 1 MiB cho fragment phía client; vượt giới hạn là lỗi stream, không tiếp tục tích lũy vô hạn |

Giá trị setting được đọc một lần cho mỗi forward attempt. Không dùng mutable state trên protocol singleton.

## Grace Session

| State | Dữ liệu cần giữ | Nghĩa |
|---|---|---|
| `Active` | Source/destination, parser, usage, monitor/dataset state, `wroteAny`, timestamps | Đang đọc và forward bình thường |
| `GraceAfterEof` | Toàn bộ state của `Active`, `graceStartedAt`, deadline tùy chọn, một read đang chờ | Đã EOF sau content nhưng chưa có terminal; đang chờ byte hoặc client cancellation |
| `Completed` | Terminal hợp lệ và usage/completion đã quan sát | Kết thúc thành công |
| `TimedOut` | `EndedWithoutTerminal=true`, `TerminalWaitTimedOut=true`, `CompletionComplete=false` | Hết grace hữu hạn mà không có byte mới |
| `ClientAborted` | Cancellation token của client | Client chủ động kết thúc; không thêm lỗi |
| `TransportFailed` | Exception gốc | Lỗi đọc/transport; không đổi thành grace hoặc pre-content retry |
| `LegacyTruncated` | `EndedWithoutTerminal=true`, không có grace option | Caller cũ giữ immediate-truncation result |

## Grace Session Transitions

1. `Active + bytes` → forward byte gốc trước, parse/aggregate đúng một lần, tiếp tục `Active`.
2. `Active + EOF + tail terminal` → commit tail đúng một lần → `Completed`.
3. `Active + EOF trước content` → existing pre-content failover marker, không vào grace.
4. `Active + EOF sau content + no-grace option` → `LegacyTruncated`.
5. `Active + EOF sau content + grace option` → `GraceAfterEof`, parser được giữ nguyên sau non-destructive peek.
6. `GraceAfterEof + bytes` → forward/parse qua cùng đường `Active`; deadline cũ bị hủy → `Active`.
7. `Active + EOF mới sau khi resume` → một `GraceAfterEof` mới với đầy đủ policy.
8. `GraceAfterEof + EOF trước deadline` → giữ nguyên deadline và chỉ một read tiếp theo.
9. `GraceAfterEof + final read tại deadline + EOF` → `TimedOut`.
10. Bất kỳ state nào + terminal hợp lệ → `Completed`.
11. Bất kỳ state nào + client cancellation → `ClientAborted` và hủy timer/read.
12. Bất kỳ state nào + transport exception → `TransportFailed` với exception gốc.

## Parser State

| Canonical Field | Rule |
|---|---|
| `Parser State._line` | Clone nguyên trạng thái dòng đang tích lũy khi peek EOF |
| `Parser State._data` | Clone toàn bộ multiline data đang tích lũy, kể cả trạng thái null |
| `Parser State._pendingCr` | Clone cờ CR pending để CR/LF semantics không đổi khi resume |

`SseDataEventParser.PeekFinish()` chạy `Finish()` trên clone để dùng lại chính xác multiline/CR/LF/optional-space semantics mà không tiêu hủy parser thật.

- Tail terminal: commit một lần và kết thúc.
- Tail non-terminal khi bắt đầu grace: bỏ probe, không cập nhật usage/monitor/dataset.
- Legacy EOF hoặc final timeout: commit/drop theo contract tương ứng, không double-count event.

## Client Pending Buffer

| Canonical Field | Rule |
|---|---|
| `Client Pending Buffer.upstreamBytes` | Mọi byte upstream nhận được, kể cả fragment chưa forward |
| `Client Pending Buffer.contentBytes` | Chỉ event hoàn chỉnh thật sự ghi xuống client; không gồm keep-alive và fragment bị drop |
| `Client Pending Buffer.pending` | Byte của một event chưa đủ boundary, giới hạn bởi `Grace Policy.PendingEventLimit` |

Grace timeout được phép bỏ `Client Pending Buffer.pending` trước khi append lỗi. Client cancellation bỏ pending mà không flush. Grace resume tiếp tục ghép pending cho tới boundary rồi flush đúng thứ tự byte.

## Timeout Metadata

| Canonical Field | Rule |
|---|---|
| `Timeout Metadata.TeeResult.TerminalWaitTimedOut` | Init-only flag trên `UsageCapture.TeeResult`; positional contract không đổi |
| `Timeout Metadata.Aborted.TerminalWaitTimedOut` | Init-only flag trên `DispatchOutcome.Aborted`; constructor hiện có không đổi |

Monitor/trace vẫn là mid-content network failure; flag chỉ thay đổi lựa chọn error event và diagnostics.
