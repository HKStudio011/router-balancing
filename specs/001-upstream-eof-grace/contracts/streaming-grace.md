# Contract: Chat Streaming Grace

## Scope

Chỉ áp dụng cho Chat Completions streaming sau khi upstream đã có byte nội dung. Không áp dụng cho pre-content failover, non-stream aggregation, JSON response hoặc Responses protocol.

## Setting Behavior

| `upstreamIdleTimeoutSec` | EOF sau content, chưa có terminal | Kết quả |
|---|---|---|
| `0` | Giữ client mở, polling mỗi 1 giây và keep-alive mỗi 5 giây tại boundary | Không có timeout do app; chờ byte hoặc client cancellation |
| `1..3600` | Bắt đầu grace tại EOF đầu tiên, không reset khi vẫn EOF | Hết thờigian mà không có byte mới → đúng một `timeout_waiting_for_terminal` |
| Không dùng grace capability | Caller legacy | Giữ immediate-truncation behavior hiện có |

Byte mới trong grace hủy deadline và khôi phục stream bình thường. EOF mới sau khi resume bắt đầu một grace mới.

## Timeout Event

Khi grace hữu hạn hết mà vẫn không có byte mới, client nhận đúng một SSE error event tại event boundary:

```text
data: {"type":"error","error":{"message":"Upstream did not resume or send a terminal event within the configured grace period.","type":"server_error","code":"timeout_waiting_for_terminal"}}

```

Sau event này kết nối kết thúc. Không có `[DONE]`, `finish_reason` giả, retry hoặc replay sau content.

## Stream Integrity

- Byte upstream chỉ được forward khi thuộc event hoàn chỉnh; keep-alive là byte do app tạo riêng và không đi vào parser/dataset.
- Fragment chưa đóng bị giữ trong grace. Nếu timeout, fragment bị bỏ trước error event; nếu resume, fragment được ghép tiếp bằng byte gốc.
- EOF với terminal hợp lệ hoàn tất ngay. Terminal giữ nguyên exact `[DONE]` sau một optional ASCII space, chỉ `choices[0]`, và `finish_reason` string khác null theo release `v1.12.3`.
- EOF trước nội dung vẫn retry/failover. Transport exception không bị đổi thành grace. Responses và non-stream behavior không đổi.

## Data and Privacy

Completion thiếu terminal hoặc hết grace vẫn bị loại khỏi dataset. Diagnostics chỉ chứa timing, route và outcome; không chứa secret, prompt, file content hoặc raw tool arguments.
