# Sửa kết thúc stream và tương thích OpenCode Free v1.5.1

Ngày: 2026-11-22
Trạng thái: đã triển khai (Tasks 0–3 đã duyệt); xác minh hoàn tất 2026-11-22 với ngoại lệ build solution môi trường đã ghi nhận

## Mục đích

Sửa các lỗi đã có bằng chứng, không thay đổi nguyên tắc dataset:

1. SSE đã forward nội dung nhưng EOF thiếu cả `finish_reason` lẫn `[DONE]` hiện bị ghi `Ok / HTTP 200`; OpenCode sau đó báo `OpenAI Chat stream ended without finish_reason`.
2. Synthetic title của Go sidecar chỉ có `finish_reason:null` rồi `[DONE]`, trong khi OpenCode vẫn bắt buộc non-null `finish_reason` tại `onHalt`.
3. Backport fix tương thích Free Tier từ `opencode2api` v1.5.1: thay ASCII space bằng NBSP chỉ trong mô tả cấp cao nhất của client tools.

Bằng chứng lỗi (1): 6/6 lỗi OpenCode trong log khớp warning của app trong 134–261 ms; tất cả thuộc NVIDIA NIM Free và đều được journal ghi `Ok / 200`. Dataset vẫn phải loại completion cụt, không bắt đầu lưu dữ liệu hỏng để phục vụ chẩn đoán.

## Phạm vi

### A. EOF giữa stream là lỗi, không phải thành công

- Giữ `UsageCapture.TeeSseAsync` làm nhiệm vụ forward byte + trả `TeeResult.EndedWithoutTerminal`; không đổi public positional shape của `TeeResult`.
- Trong `ProxyRequestHandler.ForwardAsync`, ngay sau tee, nếu `tee.EndedWithoutTerminal` thì ném `UpstreamTruncatedException` trong vùng `try/catch` hiện có.
- Hậu quả dùng lại pipeline hiện tại:
  - hủy đọc upstream;
  - monitor ghi network error;
  - dispatcher trả `Aborted`;
  - endpoint phát `SseErrorEvent.UpstreamLostMidStream()` (`upstream_disconnected`) sau phần nội dung đã forward;
  - journal không còn `Ok` giả; dataset capture sau tee không chạy cho response cụt.
- Không tổng hợp `finish_reason:"stop"` cho stream cụt: điều đó có thể xác nhận nhầm tool call/JSON arguments còn dở.
- EOF trước byte đầu vẫn đi marker `UpstreamDiedBeforeContentException` để retry/failover như hiện tại.
- Stream có non-null `finish_reason` hoặc `[DONE]` giữ nguyên hành vi; không mở rộng sang thay đổi contract native `[DONE]`-only.

OpenCode xử lý `event.error` trong `step` trước khi `onHalt` kiểm tra `finishReason`, nên error event hiện có là đường báo lỗi đúng và tránh chính thông báo mập mờ `stream ended without finish_reason`.

### B. Synthetic title có terminal chunk đầy đủ

Trong `sidecar/opencode-proxy/main.go`, response title streaming phát tuần tự:

1. content chunk, `finish_reason:null`;
2. chunk cùng `id/object/created/model`, `delta:{}`, `finish_reason:"stop"`;
3. `data: [DONE]`.

Giữ nguyên status 200, nội dung title và các header `X-Sidecar-*`. Behavior này tương ứng `opencode2api/internal/gateway/title.go` tại v1.5.1 (`6575c623d7fabf43522db48321439eb3fb39c0ed`).

### C. Backport NBSP cho client tool descriptions

Trong `sidecar/opencode-proxy/toolshape.go`, ở bước shaping client tools trước khi append official tools:

- Với Chat `tools[].function.description`, thay mọi ASCII space (`U+0020`) bằng NBSP (`U+00A0`).
- Chỉ đổi top-level description của client tools.
- Không đổi tên tool, parameters/schema, mô tả tham số, official tools được append, newline/tab hoặc NBSP có sẵn.
- Phép thay là idempotent khi shaping lặp lại.
- Không backport các thay đổi `client_environment`, protocol khác hoặc toàn bộ gateway của `opencode2api`.

## Các cách đã cân nhắc

1. **Chỉ sửa hai lỗi stream:** nhỏ nhất nhưng không bao gồm fix Free Tier 429 mà người dùng đã chọn backport.
2. **Hai lỗi stream + NBSP (đã chọn):** xử lý đúng bằng chứng hiện tại và một fix v1.5.1 có test/validation từ reference, giới hạn rõ trong Chat shaping.
3. **Tự thêm `finish_reason:"stop"` sau mọi EOF:** loại bỏ vì xác nhận nhầm completion/tool call dở.
4. **Sao chép nguyên error + `[DONE]` của reference vào Go sidecar:** loại bỏ vì host có thể coi `[DONE]` là thành công và còn nguy cơ trùng error event; tập trung EOF policy tại host giữ một điểm phân loại cho mọi provider.
5. **Port toàn bộ opencode2api:** loại bỏ khỏi lần sửa này vì mở rộng ngoài bằng chứng và trộn nhiều thay đổi gateway chưa cần thiết.

## Test và xác minh

Viết/chỉnh regression test trước khi sửa implementation:

- C# handler/integration: EOF sạch sau content không trả `Handled`; client nhận `upstream_disconnected`, request/monitor không còn thành công; pre-content vẫn retry/failover.
- C# giữ test tee thuần xác nhận byte-forward + cờ `EndedWithoutTerminal`; không yêu cầu thay đổi dataset.
- Go title: response có hai data chunks, chunk thứ hai `delta:{}` và `finish_reason:"stop"` ngay trước `[DONE]`; kiểm cả nhánh 429 và transport error nếu cùng helper.
- Go NBSP: chỉ client `function.description` đổi space; schema/parameter description/official tool giữ nguyên; shaping hai lần không tạo thay đổi tích lũy; tool-name restore không bị ảnh hưởng.

Sau implementation:

```powershell
dotnet test "router balancing test/router balancing test.csproj"
dotnet build router-balancing.slnx
```

```powershell
go test ./...    # trong sidecar/opencode-proxy
go vet ./...     # trong sidecar/opencode-proxy
```

Kết quả xác minh cuối 2026-11-22 trên các commit Tasks 0–3:

- Toàn bộ .NET tests: **1582/1582 PASS**, không skipped; chỉ có các warning analyzer đã tồn tại trước (CA2022, xUnit1069, xUnit2013).
- `dotnet build router-balancing.slnx`: **không xanh** do bốn lỗi `NETSDK1082` đã có từ trước, vì thiếu runtime pack `Microsoft.AspNetCore.App` cho `maccatalyst-x64`, `iossimulator-x64`, `android-x64` và `android-arm64`; hai warning CS1570 cũng là warning đã tồn tại trước. Diff Tasks 1–3 không đổi project/solution hay cấu hình build.
- Build app Windows `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`: **PASS**, 0 warning, 0 error.
- Toàn bộ Go tests: **PASS**; `go vet ./...`: **PASS**, không finding. Không cần `GOTMPDIR` thay thế trong lần xác minh cuối.

Không tuyên bố free inference live hoạt động chỉ từ model catalog hoặc release repository. Không cần thay đổi schema DB, UI hay format dataset.

## Ngoài phạm vi

- Không lưu response cụt vào training dataset.
- Không bảo chứng behavior/live gate của OpenCode upstream.
- Không thay đổi retry giữa chừng sau khi client đã nhận byte (vẫn không retry để tránh nhân đôi content/billing).
- Không giải quyết mọi biến thể SSE malformed/tail cụt ngoài các case có bằng chứng ở đây.
