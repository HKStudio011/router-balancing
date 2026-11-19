# Dataset OCR Hardening Design

**Trạng thái:** Đã triển khai
**Ngày:** 2026-11-19
**Phạm vi:** 13 findings từ OCR delegate review về `DatasetSink`, `UsageCapture`, `DatasetPayloadTransformer` và test/integration/localization.

## 1. Mục đích

Sau khi export large text/base64 hoạt động, OCR delegate review phát hiện các lỗi có thể làm mất record, xoá dữ liệu người dùng, tạo dangling manifest và vượt ngân sách RAM, cùng các lỗ hổng test che giấu regression. Mục tiêu là xử lý toàn bộ findings theo từng nhóm nhỏ có test và review, **không viết lại pipeline dataset**.

## 2. Constraints và non-goals

- Giữ queue 128 record/32 MiB, synchronous `Capture` không IO, route normal/large/payload và response client nguyên vẹn.
- `InlineMaxFieldBytes = 1_048_576`, `MaxExportFieldBytes = 16_777_216`; >16 MiB vẫn `TooLarge`.
- Không raw overflow, `CaptureAsync`, extra backpressure, blob dedup, cloud storage hay database mới.
- `datasetDay` vẫn từ `FinishedAt` UTC; `exportId` GUID N; manifest paths dùng `/`.
- Manifest v2 vẫn chỉ ghi sau khi source documents và binaries đã flush/rename thành công.
- Retention `0` giữ mãi và dữ liệu không thuộc dataset phải sống sót.
- Không sửa metadata-provider edits hay file idle-timeout ngoài phạm vi.
- Không đổi state machine lỗi ghi record hiện có ngoài các nhánh cần retain/replay đã nêu.

## 3. Các phương án

### A. Incremental hardening — **chọn**

Bốn nhóm TDD độc lập: data-loss prevention, retention, semantic fidelity và test matrix. Dễ review/rollback từng nhóm và chứng minh được interaction giữa chúng.

### B. Một fix wave duy nhất

Ít commit hơn nhưng diff lớn, khó cô lập lỗi recovery/retention. Không chọn.

### C. Chỉ xử lý High

Vẫn còn UTF-8 memory overrun, recovery race và nhiều lỗ hổng test. Không chọn vì OCR đã chỉ ra rủi ro thực tế.

## 4. Nhóm 1 — Data-loss prevention và recovery

### 4.1 Mở payload writer trước artifact IO

Hiện `WritePayload` ghi source documents/binaries rồi mới `_payloadWriter ??= OpenWriter(path)`. Thay đổi:

1. `Process` xác định payload manifest path và mở `_payloadWriter` trong catch riêng **trước khi gọi `WritePayload`**.
2. Nếu mở thất bại: `RetainCaptureForReplay(capture)`, đặt `_disabled = true` trước log, `InvokeChanged()` và return. Không có artifact nào được tạo.
3. `WritePayload` nhận writer đã mở và vẫn ghi source documents/binaries trước, manifest sau cùng.

Lỗi artifact sau khi writer đã mở tiếp tục theo record-error semantics hiện có; không được ghi manifest hoặc tăng stats. Khi replay, GUID mới tránh collision với artifact đợt lỗi trước.

### 4.2 Handoff writer khi recovery

Race hiện tại: Settings reset `_disabled` và một `Capture` duy nhất enqueue khi `_writerTask` cũ chưa `IsCompleted`; `EnsureStarted` return, writer cũ thoát và không có writer mới.

Thiết kế dùng **recovery generation + handoff gate**:

1. `OnSettingsChanged` tăng `_settingsGeneration` bằng `Interlocked` rồi reset error counters/`_disabled` như hiện tại. Mỗi failure ghi nhận generation tại thời điểm failure.
2. Một save chỉ là recovery nếu xảy ra **sau failure đã publish**. Save trước failure không được coi là recovery: failure sau đó vẫn disable sink tới save tiếp theo, dù capture lỡ enqueue trong khoảng hở.
3. Sau `CloseFiles()` trong `WriterLoopAsync.finally`, khóa `_startGate`, xoá reference task đang kết thúc và chỉ handoff khi:
   - chưa disposed và channel chưa complete;
   - không có failure nào sau generation mới nhất (`_disabled == false` tại điểm kiểm tra);
   - `EnsureStarted()` được gọi trong cùng lock, không xoá task mới do thread khác vừa tạo.
4. `DisposeAsync` cũng khóa `_startGate` khi đặt `_disposed`, complete channel và capture task reference để chờ. Nhờ đó dispose và handoff được linearize: dispose trước thì không có writer mới; handoff trước thì dispose chờ task mới theo timeout hiện có.
5. Hard postcondition: không có handoff sau dispose hoặc khi failure vẫn đang active tại generation hiện tại.

Test bao phủ cả ba race:

- Settings save sau khi writer đang thoát, chỉ một capture mới → pending replay trước rồi queue drain, không cần capture mồi.
- Settings save trước failure publish → không được coi là recovery; failure vẫn disable cho tới save sau đó.
- Dispose đua với handoff → không có writer mới sau dispose; nếu handoff thắng, dispose capture và chờ đúng task mới.

### 4.3 Partial artifact failures

Giữ manifest-last nhưng bổ sung test lỗi tại:

- `completion.json` sau khi `request.json` đã rename.
- Binary sau khi cả hai source documents đã rename.
- Payload JSONL open trước mọi artifact.

Mỗi trường hợp phải assert: không manifest, không `.tmp-*`, counters không tăng, recovery không phát sinh manifest từ artifact cũ/chưa đầy đủ. Final artifact orphan, nếu có, chỉ được xử lý theo contract hiện có; không thêm transactional rollback ngoài scope.

## 5. Nhóm 2 — Retention an toàn và nhất quán

**Phạm vi concurrency:** các bảo đảm dưới đây áp cho **một `DatasetSink` và không có writer/process ngoài sửa filesystem trong lúc purge**. Không thêm cross-process lock. Nếu user/process khác thêm/xoá entry đồng thời sau validation, kết quả không được đảm bảo; không dùng điều này để biện minh việc xoá entry lạ đã có từ trước tại thời điểm kiểm tra.

### 5.1 Chỉ xoá layout sink sở hữu

Thay `Directory.Delete(dayDir, recursive: true)` bằng:

1. Trong mỗi `payloads/YYYY-MM-DD`, chỉ xét export directories có tên GUID N 32 hex.
2. Export directory chỉ được xoá khi toàn bộ entries khớp layout cho phép:
   - `request.json`, `completion.json`;
   - `(image|audio|file)-NNN-<8 lowercase hex>.<allowlisted extension|bin>`;
   - không có subdirectory hoặc entry lạ.
3. Nếu có entry lạ, bỏ qua toàn bộ export directory đó; không xoá một phần để tránh xoá nhầm dữ liệu người dùng.
4. Sau khi xoá export directories được phép, chỉ `Directory.Delete(dayDir, recursive: false)` khi rỗng. Entry lạ được giữ nguyên và làm day directory không rỗng.

### 5.2 Liên kết manifest và payload theo `datasetDay`

- Nếu `requests-payload-YYYY-MM-DD.jsonl` tồn tại nhưng không xoá được → bỏ qua toàn bộ payload purge của ngày đó, giữ reference integrity.
- Nếu manifest xoá thành công hoặc không còn tồn tại → được purge các export directories nhận diện được của ngày đó.
- Nếu payload directory không xoá được → lần purge sau vẫn retry, kể cả khi manifest đã mất trước đó.

Test gồm user file + nested directory trong expired day, manifest bị lock, payload directory bị lock, retry sau khi unlock và cả ba lanes normal/large/payload.

## 6. Nhóm 3 — Semantic fidelity và memory budget

### 6.1 Dataset SSE budget theo serialized UTF-8 size

Không đổi character aggregation dành cho client. `ChatSseAggregateState` nhận thêm mode opt-in cho dataset:

- Mặc định: giữ `contentBudgetChars` và hành vi cũ.
- Dataset capture: `budgetInUtf8Bytes = true`; `UsageCapture` chỉ bật mode này khi `captureCompletion = true`.

Trong dataset mode, mỗi text/tool-argument delta được tính bằng:

```csharp
Encoding.UTF8.GetByteCount(
    JsonSerializer.Serialize(text, DatasetJsonOptions)) - 2; // trừ hai quote bao quanh
```

`DatasetJsonOptions` dùng **default JSON encoder của `JsonNode.ToJsonString()`**, vì đó là serializer authoritative mà `ChatSseAggregateState.BuildCompletionJson()` đang dùng. Không dùng relaxed encoder và không đổi `BuildCompletionJson` vì method đó còn phục vụ client aggregation. Cộng dồn theo delta; nếu surrogate pair bị tách giữa delta, kết quả có thể ước lượng cao hơn và overflow sớm — chấp nhận vì an toàn bộ nhớ, không được overflow muộn. `BuildCompletionJson` vẫn kiểm exact 16 MiB bằng UTF-8 ở builder.

### 6.2 PNG signature đầy đủ

Thay signature 4 byte bằng đủ 8 byte:

```text
89 50 4E 47 0D 0A 1A 0A
```

Thêm positive full signature và negative prefix-only test; JPEG/WebP/unknown fallback giữ nguyên.

### 6.3 Semantic-lossless source documents

Cho cả request và completion:

- Clone JSON nguồn, thay duy nhất giá trị tại `json_pointer` bằng expected `payload://...`.
- `JsonNode.DeepEquals` với transformer result và với document đọc lại từ đĩa.
- Bao phủ metadata nhiều cấp, JSON null, arrays, `model`, `role`, `stream_options`, completion metadata và fields không được v1 normalize.
- Pointer escaping phải resolve theo RFC 6901 trên expected/source document, không chỉ index bằng tên property gốc.

## 7. Nhóm 4 — Test matrix và consumer-visible behavior

### 7.1 Thresholds và base64 cutoff

- Pin literal `1_048_576` và `16_777_216`; test không tự thay expected khi constants đổi sai.
- Body/completion tại N-1/N/N+1 của 1 MiB và 16 MiB, gồm multibyte text đo byte count chính xác.
- Tee đúng budget 16 MiB vẫn complete, +1 byte mới overflow.
- Base64 encoded length N giữ inline/v1 nguyên vẹn; N+4 externalize. Thêm case decoded dưới 1 MiB nhưng encoded vượt 1 MiB để chứng minh cutoff theo encoded bytes.
- Ordinary long text chỉ gồm alphabet base64/padding ở field thường phải trả null và giữ nguyên bytes.

### 7.2 Integration bytes

- Large text: observe và so exact upstream request bytes.
- Large text SSE và large-base64 SSE: so exact client response bytes với upstream fixture.
- Giữ mọi assertion dataset hiện có.

### 7.3 Localization call site

- Kiểm tra `DatasetStatsLine()` thật với `DatasetStats` có mọi field khác nhau.
- EN/VI assert đúng cặp label-value và mỗi placeholder `{8}`–`{11}` xuất hiện đúng một lần; không chỉ `string.Format` resource với mảng tự tạo.

## 8. Thứ tự triển khai và review

1. **Nhóm 1:** payload-open replay + writer handoff + partial artifact failures.
2. **Nhóm 2:** ownership-safe và manifest-consistent retention.
3. **Nhóm 3:** UTF-8 budget + PNG signature + semantic-lossless documents.
4. **Nhóm 4:** boundary/integration/localization matrix.

Mỗi nhóm yêu cầu RED trước implementation, GREEN sau implementation, focused tests rồi full suite. Nhóm 2 và 4 chạy thêm Windows MAUI build. Sau cùng chạy full suite, Windows build, `git diff --check` và OCR delegate review lại toàn bộ diff hardening.

## 9. Tiêu chí chấp nhận

- Không còn đường mở payload JSONL nào làm mất capture trước khi có cơ hội replay.
- Recovery với đúng một capture cũng drain được pending + queue mà không cần capture mồi khi save xảy ra sau failure; save trước failure không được nhầm là recovery và không có handoff sau dispose.
- Retention không xoá entry người dùng hoặc để manifest trỏ tới artifact đã mất.
- Dataset SSE không thể vượt budget do UTF-8/JSON escaping ngoài overhead nhỏ đã chấp nhận.
- Deep comparison chứng minh mọi field ngoài payload target được giữ nguyên.
- Toàn bộ 13 findings có regression coverage trực tiếp, full suite xanh và OCR re-review không còn finding tương ứng.
