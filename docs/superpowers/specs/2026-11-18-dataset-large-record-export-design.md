# Dataset JSONL — text record lớn và base64 externalization

**Trạng thái:** Đã triển khai
**Ngày:** 2026-11-18
**Phạm vi:** record có trường ≤16 MiB; payload >16 MiB vẫn bị loại như hiện tại.

## 1. Mục đích

Hiện `DatasetRecordBuilder.MaxFieldBytes = 1 MiB`. Chỉ cần `ClientBody` hoặc `CompletionJson` vượt 1 MiB, toàn bộ record bị loại (`SkippedTooLarge`) dù request/response hoàn chỉnh.

Thiết kế này giải quyết hai nhóm record đang bị loại:

1. **Text record lớn:** context/completion >1 MiB đến 16 MiB, bao gồm phần lớn cuộc hội thoại 1M tokens thông thường. Token không phải byte, nên không khẳng định mọi cuộc hội thoại 1M+ đều nằm trong giới hạn.
2. **Base64 payload lớn:** ảnh/audio/file base64 >1 MiB đến 16 MiB được decode thành binary files, JSONL chỉ giữ manifest/reference.

Không làm raw overflow >16 MiB hoặc rewrite streaming pipeline ở bản này.

## 2. Ràng buộc

- `Capture` trên request thread vẫn không parse/không IO.
- Queue giữ nguyên **128 record/32 MiB pending bytes**; vượt guard → `SkippedQueueFull`.
- Writer xử lý tuần tự từng record, nên binary/source documents và manifest được ghi trong cùng một lượt, không có spool task chạy song song với retention.
- Không cắt request/response gửi cho client.
- Dataset và payload là plaintext/binary không mã hoá, tuân cùng cảnh báo bảo mật và retention.
- Record đã bị bỏ trước khi triển khai không thể phục hồi.

## 3. Goals / Non-goals / Anti-goals

### Goals

1. Mọi record hiện đang export được giữ nguyên file và schema v1.
2. Text record >1 MiB đến 16 MiB/trường được ghi đầy đủ sang file large cùng schema v1.
3. Base64 payload >1 MiB được externalize thành binary + manifest v2, không nhúng chuỗi lớn vào JSONL.
4. Manifest v2 phải chứa đủ source-shaped documents để resolve `json_pointer` và khôi phục dữ liệu ở mức semantic.
5. Rotation, retention, thống kê, fail-open và dispose bao phủ JSONL lẫn payload files.

### Non-goals

- Không hỗ trợ record >16 MiB/trường bằng raw spool/backpressure ở bản này.
- Không thu thập raw SSE bytes mới ngoài dữ liệu `CompletionJson` mà pipeline hiện đã capture.
- Không dedup giữa request, mã hoá, cloud storage hoặc database mới.
- Không xây dataset loader/training pipeline; chỉ cung cấp format v2 đủ rõ để loader đọc lại.
- Không thay đổi response client, retry/failover hoặc API Monitor.

### Anti-goals

- Không truncate request/response.
- Không externalize base64 ≤1 MiB và làm record hiện hữu biến mất khỏi file v1 thường.
- Không nhận diện base64 bằng heuristic trên mọi chuỗi dài.
- Không ghi manifest trước khi payload/source documents đã flush hoàn chỉnh.
- Không dùng `requestId` 8 ký tự làm unique key duy nhất cho đường dẫn v2.

## 4. Phương án đã chọn

### A. Chỉ tăng cap 1 MiB

Đơn giản nhưng chỉ dời ngưỡng bị loại và không xử lý được bản chất base64. **Không chọn.**

### B. Hai làn v1 + v2 payload manifest — **chọn**

- Record thường giữ v1.
- Text large dùng v1 large.
- Base64 large dùng v2 manifest + binary/source documents.
- Giới hạn cứng vẫn 16 MiB/trường, phù hợp queue 32 MiB hiện tại.

### C. Raw spool mọi payload >16 MiB

Cần tee/spool SSE ngay từ byte đầu, backpressure ngoài `ForwardAsync`, active-spool retention và format archive mới. **Không làm ở bản này** vì phạm vi/rủi ro lớn hơn nhiều so với nhu cầu hiện tại.

## 5. Quy tắc phân loại record

Xét `ClientBody.Length` và UTF-8 byte count của `CompletionJson` theo thứ tự:

1. Một trường >16 MiB → `DatasetSkipReason.TooLarge`, không tạo artifact.
2. Có base64 hợp lệ với encoded payload >1 MiB → **v2 payload manifest**.
3. Không có base64 large, một trường >1 MiB → **v1 large**.
4. Cả hai ≤1 MiB → **v1 thường** như hiện tại.

Như vậy:

- Base64 ≤1 MiB vẫn inline trong v1 thường như trước.
- Record hiện đang export được không bị chuyển hướng.
- Record vừa có text lớn vừa có base64 large ưu tiên v2.
- `Truncated`, `Empty`, `Refusal`, `Synthetic` giữ nguyên thứ tự/semantics hiện có và không được ghi như mẫu training mới.

## 6. Layout lưu trữ

Cùng thư mục dataset, theo `datasetDay` UTC của record:

```text
requests-YYYY-MM-DD.jsonl           # v1, record thường
requests-large-YYYY-MM-DD.jsonl     # v1, text record >1 MiB đến 16 MiB/trường
requests-payload-YYYY-MM-DD.jsonl   # v2, record có base64 externalized
payloads/YYYY-MM-DD/<exportId>/
    request.json                    # source-shaped request + payload URLs
    completion.json                 # source-shaped completion + payload URLs
    image-000-<hash-prefix>.png
    audio-001-<hash-prefix>.bin
```

### 6.1 `datasetDay`

- `datasetDay = DateOnly.FromDateTime(capture.FinishedAt.UtcDateTime)` và được dùng thống nhất cho JSONL, payload directory, manifest và retention của record.
- Writer hiện xử lý tuần tự nên rotation theo `datasetDay` của record đang xử lý; nếu qua nửa đêm trong queue, record vẫn về đúng ngày `FinishedAt`.
- Retention chạy trên cùng writer task, không chạy giữa lúc ghi payload của record đang xử lý.

### 6.2 `exportId`

- Mỗi record v2 có `exportId = Guid.NewGuid().ToString("N")`.
- `request_id` vẫn giữ id tương quan với proxy; `exportId` là identity duy nhất của artifact v2.
- Mọi đường dẫn binary/source documents dùng `exportId`, tránh collision khi `requestId` trùng trong lịch sử.

## 7. Source-shaped documents và `json_pointer`

V2 không dựa pointer trên document v1 đã normalize. Mỗi manifest có:

```json
{
  "v": 2,
  "ts": "2026-11-18T04:00:00Z",
  "request_id": "a1b2c3d4",
  "export_id": "<32 hex>",
  "endpoint": "chat",
  "model_upstream": "z-ai/glm-5.3-flash",
  "documents": {
    "request": "payloads/2026-11-18/<exportId>/request.json",
    "completion": "payloads/2026-11-18/<exportId>/completion.json"
  },
  "payload_refs": [
    {
      "document": "request",
      "json_pointer": "/messages/0/content/1/image_url/url",
      "url": "payload://<exportId>/image-000-9f2c1a7b.png",
      "path": "payloads/2026-11-18/<exportId>/image-000-9f2c1a7b.png",
      "kind": "base64",
      "media_type": "image/png",
      "bytes": 1234567,
      "sha256": "..."
    }
  ]
}
```

Định nghĩa documents:

- `request.json`: JSON gốc của `ClientBody`, giữ toàn bộ fields, chỉ thay giá trị base64 được externalize bằng `payload://...` tại đúng vị trí.
- `completion.json`: JSON mà pipeline hiện capture trong `CompletionJson`, giữ toàn bộ fields hiện có và thay giá trị base64 tương ứng:
  - Chat SSE → object `chat.completion` đã aggregate.
  - Responses SSE → terminal `response` object.
  - Non-stream → response JSON nguyên thân.
- `json_pointer` resolve trong document được chọn bởi field `document`, không phải trong manifest.
- V2 là **semantic-lossless so với dữ liệu capture hiện có**, không phải byte-for-byte raw network archive. Binary + MIME + JSON Pointer cho phép loader khôi phục data URI/base64 khi cần.
- `path` là relative path trong dataset root; không ghi absolute path hoặc secret.

## 8. Nhận diện và ghi base64

Phạm vi nhận diện có chủ đích:

1. String có data URI hợp lệ `data:<media-type>;base64,<payload>`.
2. Các field schema rõ ràng như `b64_json` và `input_audio.data` khi giá trị là base64 hợp lệ.
3. Chỉ externalize khi **encoded payload >1 MiB**; payload ≤1 MiB giữ inline để bảo toàn record v1 hiện hữu và tránh file nhỏ không cần thiết.

Quy tắc:

- Decode trên writer task, không trên request thread.
- Tên file gồm sequence + SHA-256 prefix + extension từ MIME allowlist, fallback `.bin`.
- SHA-256 dùng kiểm tra toàn vẹn, không dedup giữa request.
- Ghi source documents và binary bằng file tạm trong cùng thư mục, flush rồi atomic rename; **manifest được ghi cuối cùng** sau khi mọi artifact hoàn chỉnh.
- Chuỗi base64 không hợp lệ giữ nguyên và không làm record chuyển v2.
- Không decode text/code chỉ vì có ký tự giống base64.

## 9. Writer, IO lỗi, rotation, retention

- Một writer task quản lý ba loại JSONL; large/payload mở lười khi có record tương ứng.
- `RecordsWritten`, `BytesWritten`, `CurrentFile` là tổng/đường dẫn ghi gần nhất trên cả ba JSONL.
- Đổi `datasetDay` hoặc dispose: flush/đóng mọi writer.
- Retention xoá đúng ba pattern JSONL và `payloads/YYYY-MM-DD` tương ứng; retention `0` giữ mãi.
- Không xoá dữ liệu khác do người dùng tạo; không có dedup nên không cần refcount GC.
- Không ghi manifest khi artifact chưa flush thành công; dọn file tạm khi có thể.
- Giữ state machine lỗi IO hiện tại: lỗi ghi record đầu tiên log/drop theo giới hạn, lỗi lặp mới disable chờ Settings được lưu; lỗi `EnsureFileForCurrentDay` giữ hành vi hiện có.
- Không fallback âm thầm giữa v1/v2 hoặc giữa file thường/large/payload.

## 10. Stats và UI

`DatasetStats` thêm:

- `LargeRecordsWritten`: số dòng v1 trong file large.
- `PayloadRecordsWritten`: số manifest v2 trong file payload.
- `ExternalizedPayloadCount`: số binary payload đã externalize.
- `ExternalizedPayloadBytes`: tổng byte binary thực tế đã externalize.

`RecordsWritten`, `BytesWritten`, `CurrentFile` giữ vai trò tổng như trên. `SkippedTooLarge` nghĩa là vượt 16 MiB/trường; `SkippedQueueFull` giữ nguyên nghĩa.

UI Settings hiển thị thêm số dòng large/payload và dung lượng externalized; không thêm setting mới ở bản đầu. Cập nhật cả `vi.json` và `en.json`.

## 11. Tương thích

- File/record v1 hiện có không migration.
- Record hiện đang export được vẫn xuất hiện đúng file v1 thường như trước; base64 externalization chỉ áp cho nhóm >1 MiB vốn đang bị drop.
- File large và payload mới là opt-in cho consumer; consumer phải đọc field `v` và resolve documents/payload refs khi dùng v2.
- Không migration DB, không thay đổi `DatasetCapture`, `TeeResult` hay `ProxyRequestHandler` ngoài budget tích luỹ SSE từ 1 MiB lên 16 MiB.
- SSE vượt 16 MiB vẫn là dữ liệu không export được; không thay đổi counter `Truncated` hiện có ở bản này và không gọi nó là raw archive.

## 12. Triển khai theo hai giai đoạn

### Giai đoạn 1 — Text record lớn

- Hai ngưỡng 1/16 MiB, file large v1, stats/rotation/retention và budget tích luỹ SSE 16 MiB.
- Test boundary 1 MiB và 16 MiB ±1 byte cho body/completion.

### Giai đoạn 2 — Base64 externalization

- Data URI + field base64 rõ ràng, ngưỡng >1 MiB, source documents, binary files, manifest v2, `exportId`, atomic write và retention payload.
- Test Chat/Responses, ảnh/audio/file, base64 ≤1 MiB giữ v1, JSON Pointer và SHA-256.

Mỗi giai đoạn phải giữ full suite xanh trước khi chuyển tiếp.

## 13. Test bắt buộc

### Builder / payload transformer

- Boundary 1 MiB và 16 MiB ±1 byte cho cả body/completion, tính UTF-8 bytes.
- Text large giữ nguyên schema v1 và toàn bộ messages/completion.
- Base64 ≤1 MiB giữ đúng file v1 hiện hữu.
- Data URI hợp lệ → decode đúng bytes/MIME/reference; chuỗi base64 không hợp lệ giữ nguyên.
- Chuỗi text dài không phải data URI không bị nhận nhầm base64.
- Chat `image_url`, Responses `input_image`, `b64_json`, `input_audio.data`.
- `json_pointer` resolve trong đúng source-shaped request/completion document.
- `exportId` khác nhau giữa hai record cùng `request_id`; không ghi đè artifact.
- `datasetDay` thống nhất giữa manifest/source/binary khi record kết thúc sát hoặc qua nửa đêm.
- `Truncated`, `Empty`, `Refusal`, `Synthetic` giữ semantics hiện có.

### Sink / storage

- Record thường chỉ vào file thường; text large chỉ vào large; record externalized chỉ vào payload.
- `RecordsWritten`, `LargeRecordsWritten`, `PayloadRecordsWritten`, externalized bytes và `CurrentFile` đúng khi ghi xen kẽ.
- Rotation/dispose flush mọi writer; retention xoá đúng cả JSONL/payload và giữ dữ liệu không thuộc dataset.
- Lỗi artifact không tạo manifest nửa vời; state machine disable/recovery đúng hiện tại.
- Queue thường 128 record/32 MiB không đổi.

### Integration

- Request >1 MiB nhưng ≤16 MiB: response client không đổi, chỉ một dòng full v1 trong large.
- Request có ảnh base64 >1 MiB: provider nhận nguyên body; manifest trỏ tới binary đúng SHA-256 và source document resolve được.
- Full suite và Windows MAUI build sau mỗi giai đoạn.

## 14. Quyết định do agent chọn (người dùng đã uỷ quyền chọn hướng)

1. Ba loại artifact: v1 thường, v1 large text, v2 payload manifest + source/binary documents.
2. Text large giữ cap 16 MiB/trường; không làm raw overflow >16 MiB ở bản này.
3. Base64 externalization chỉ áp cho encoded payload >1 MiB để không chuyển hướng record hiện đang export được.
4. V2 lưu source-shaped documents, semantic-lossless so với dữ liệu capture hiện có; không giả vờ là raw network archive.
5. Chọn per-export payload directory với GUID, chưa dedup để retention đơn giản và an toàn.
6. Không thêm CaptureAsync/backpressure hoặc spool task song song trong phạm vi này.
