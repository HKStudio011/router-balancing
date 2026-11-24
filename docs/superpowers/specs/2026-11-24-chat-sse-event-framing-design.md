# Chat SSE event framing và dataset integrity — thiết kế đã duyệt

Ngày: 2026-11-24
Trạng thái: đã triển khai parser + dataset + LastByteGap; verification local hoàn tất, chờ independent whole-branch review

## Bằng chứng và mục đích

Reproduction bằng chính `UsageCapture.TeeSseAsync` đã chứng minh:

- `finish_reason:"tool_calls"` trải trên nhiều dòng `data:` bị app đánh dấu `EndedWithoutTerminal=true` dù OpenCode/Effect ghép các dòng thành một event và parse được.
- Multi-line tool call đi kèm `[DONE]` cho `CompletionComplete=true`, nhưng completion mất tool `write`, model/id bị thay bằng giá trị tổng hợp và `finish_reason` bị đổi thành `stop`.

Review cuối sửa một fixture terminal chưa đúng SSE semantics: hai data fields `data: [DO` và `data: NE]` trong cùng event phải join thành `[DO\nNE]`, nên **không** phải sentinel hợp lệ. Ngược lại, một physical line `data: [DONE]` bị chia giữa các network chunks vẫn là sentinel hợp lệ sau khi parser ghép line.

Vì vậy cần sửa cả terminal detection và dataset aggregation. Thông điệp `UpstreamTruncatedException` hiện chỉ chứng minh app không nhận diện được terminal, chưa tự nó chứng minh upstream không gửi terminal. Chưa có raw event của các request NVIDIA thật nên vẫn không loại trừ upstream/proxy cắt stream.

## Cách triển khai đã chọn

Thêm `SseDataEventParser` nội bộ cho Chat:

- Nhận bytes theo từng network chunk và giữ line chưa hoàn chỉnh.
- Nhận diện LF, CRLF và CR; xử lý CR/LF bị tách giữa hai chunks.
- Ghép mọi `data:` lines trong cùng event bằng `\n`, bỏ đúng một optional space sau `data:` cho từng dòng, rồi phát event khi gặp blank line.
- Bỏ các field khác (`event:`, `id:`, `retry:`, comments) khỏi metadata Chat; raw bytes cho client vẫn forward nguyên vẹn.
- `Finish()` xử lý pending line/event ở EOF để giữ tolerance hiện có cho tail không có blank line/newline.
- Trả payload với một `data:` prefix tổng hợp để các parser hiện có (`ScanSseLine`, `AppendSsePayload`, `ChatSseAggregateState.HandleLine`) tiếp tục hoạt động mà không thay public signature.

Dùng parser tại đúng hai call sites:

1. `UsageCapture.TeeSseAsync` — terminal/usage/monitor/dataset tee.
2. `ChatCompletionsProtocol.AggregateSseAsync` — non-stream JSON aggregation.

Không thay `ResponsesProtocol` trong task này vì state machine đó còn phụ thuộc `event:` names và có contract terminal khác. Không thêm package/dependency mới.

## LastByteGap diagnostics

- `UsageCapture` ghi nhận thời điểm byte cuối cùng đọc được và trả `LastByteGap` chỉ khi EOF thiếu terminal. Thêm property mới (không đổi positional parameters của `TeeResult`).
- `UpstreamTruncatedException` nhận optional `TimeSpan? LastByteGap` qua constructor mới/optional parameter, giữ tương thích constructor không tham số.
- `ProxyRequestHandler` truyền gap từ tee vào exception; message/log có `silence_gap` khi biết giá trị. Không thêm raw prompt/completion/file content vào log.
- Ý nghĩa: gap lớn gợi ý idle kill; gap gần 0 gợi ý cắt giữa token/lifetime/network. Đây là diagnostics, không phải bằng chứng tuyệt đối về nguyên nhân.

## Ràng buộc tương thích

- Giữ raw forwarding byte-for-byte và TTFT hiện có.
- Không retry sau khi đã forward content; pre-content retry/failover giữ nguyên.
- Không tổng hợp `finish_reason` khi upstream thật sự không gửi: exact `[DONE]` không kèm finish reason phải giữ `finish_reason:null` trong completion đã gộp.
- Sau khi bỏ một optional space, `[DONE]` vẫn phải exact như OpenCode. `[DONE]garbage`, `[DONE]x`, hai spaces/tab sau `data:` và payload nhiều data fields `[DO\nNE]` đều không được coi là terminal.
- `finish_reason` chỉ ở `choices[1]` vẫn không được coi là terminal vì OpenCode cũng chỉ đọc `choices[0]`.
- Dataset tiếp tục loại stream thật sự cụt và completion overflow; không làm thay đổi format export.
- Hermes và các OpenAI-compatible clients khác dùng cùng SSE event semantics; không thêm workaround riêng theo client.

## Các cách không chọn

- Chỉ sửa `ScanSseLine`: để lại dataset sai.
- Trim mọi whitespace hoặc đánh dấu terminal từ `choices[1]`: app có thể xanh trong khi OpenCode vẫn báo lỗi.
- Thêm thư viện SSE mới: vượt nhu cầu và mở rộng dependency.
- Retry ngay giữa stream: có thể nhân đôi text/tool call/billing.

## Regression tests bắt buộc

Viết test trước implementation và giữ bằng chứng RED từ parser hiện tại:

1. Multi-line `write` + `finish_reason:"tool_calls"` không `[DONE]`: terminal được nhận, bytes forward đúng.
2. Cùng input có `[DONE]`, `captureCompletion=true`: `CompletionComplete=true`, completion giữ `write`, arguments và `finish_reason:"tool_calls"`; không synthetic `stop`.
3. Hai data fields `data: [DO` + `data: NE]` tạo `[DO\nNE]`: tee báo thiếu terminal/capture incomplete và aggregate từ chối completion; một physical line `data: [DONE]` chia giữa network chunks vẫn terminal.
4. Không newline/blank line cuối: vẫn nhận terminal hợp lệ.
5. CRLF và CR, kể cả CR/LF bị tách giữa chunks.
6. Mỗi byte của multi-line tool call được feed như một chunk riêng: kết quả không đổi.
7. Hai spaces/tab sau `data:` cho `[DONE]`: vẫn không terminal.
8. `finish_reason` chỉ ở `choices[1]`: vẫn không terminal.
9. Non-stream `AggregateSseAsync` trên multi-line tool call: JSON đầy đủ, không ném truncation.
10. EOF thiếu terminal thật: vẫn báo lỗi và có `LastByteGap`; pre-content retry/failover không đổi.
11. `[DONE]garbage`/`[DONE]x`: tee báo thiếu terminal, payload vẫn hiện trong monitor và capture không được coi là complete; aggregate cũng từ chối.
12. Exact `[DONE]` không có finish reason: tee/capture và non-stream aggregate giữ `finish_reason:null`; các giá trị upstream thật như `stop`, `tool_calls`, `length` không đổi.

## Verification

- Focused UsageCapture và ChatCompletionsProtocol tests.
- Full .NET suite, Windows TFM build 0 warnings/0 errors.
- Go tests/vet để bảo đảm sidecar không bị ảnh hưởng (không có thay đổi Go dự kiến).
- Independent whole-diff review; kiểm không có raw request/secret trong log hoặc test data ngoài fixtures tối thiểu.

## Kết quả verification — 2026-11-24

Các lệnh chạy trên commit `074c76791bf50c01522b287ffc8d8ec175d40eac`:

- `dotnet test "router balancing test/router balancing test.csproj"`: **PASS**, 1597/1597 tests, 0 failed, 0 skipped. Build của test project còn 9 analyzer warnings ngoài phạm vi thay đổi: 6 `CA2022` trong `IdleWatchdogStreamTests.cs`, 2 `xUnit1069` trong `ProxyRequestHandlerTests.cs`, 1 `xUnit2013` trong `DatasetExportIntegrationTests.cs`.
- `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --no-restore`: **PASS**, 0 warnings/0 errors. Không chạy full local solution build vì lỗi môi trường `NETSDK1082` mobile runtime-pack đã biết; Windows TFM build là phạm vi được yêu cầu.
- Trong `sidecar/opencode-proxy`, `go test ./...`: **PASS** (cached); `go vet ./...`: **PASS**, không output. Bổ sung `go test -count=1 ./...`: lần đầu tests báo `ok` nhưng Go exit 1 ở cleanup temp test exe do file đang bị khóa trên Windows; không còn process test sau đó và rerun cùng lệnh **PASS** (0.668s).
- `git diff --check`: **PASS**, không output. `git status --short` trước khi cập nhật tài liệu chỉ có hai file spec/plan untracked; không có dataset, log hoặc secret ngoài scope.

Independent whole-branch review do controller dispatch sau Task 3 report; kết quả review chưa có tại thời điểm ghi nhận verification này.
