# Spec: Event-boundary buffering cho SSE + chẩn đoán upstream silence (chống CF read timeout)

- **Ngày:** 2026-11-07 (cập nhật sau review)
- **Trạng thái:** drafted — chờ user review; chưa mô tả implementation đã hoàn thành
- **Tiền đề:**
  - [2026-10-06-early-headers-keepalive-design.md](2026-10-06-early-headers-keepalive-design.md) — early headers + keep-alive 5s (đã implement, commit `9da30ed`); spec này bịt lỗ còn lại.
  - Bug 2026-10-16: keep-alive chèn giữa dòng data chưa đóng phá framing → fix bằng cách **skip** keep-alive khi mid-line — chính cái skip đó tạo lỗ cho Cloudflare cắt (spec này thay cơ chế).

## 1. Bối cảnh & mục tiêu

Cloudflare edge có **proxy read timeout ~100s** (Free/Pro/Business; Enterprise mới tăng được): request nào không có byte chảy trong ngưỡng đó bị cắt — kể cả SSE stream đang chạy. App đã gửi `: keep-alive\n\n` mỗi 5s (`ProxyApp.RunStreamLoopAsync`) nhưng **skip khi byte cuối forward không phải `\n`** (mid-line). Upstream dừng >100s giữa một dòng SSE (thinking dài sau chunk partial, TCP split + pause) → chân client im lặng quá ngưỡng → CF cắt.

Hai lỗi bổ sung từ review cần tránh:

1. Nếu upstream trickle một event chưa đóng, mỗi read có thể re-arm `Task.Delay(5s)` và keep-alive bị trì hoãn vô hạn. Vì vậy deadline phải độc lập với read activity.
2. Nếu outcome `Aborted` xảy ra khi pending còn một fragment chưa đóng, flush fragment rồi append in-band error sẽ tạo frame hỏng. Không được nối error sau fragment.

Mục tiêu:

1. **Chân client không im lặng >5s khi upstream còn sống:** chỉ forward các SSE event trọn vẹn; keep-alive được gửi theo absolute/monotonic deadline, không bị read làm trì hoãn.
2. **Framing an toàn cả khi abort:** không bao giờ nối in-band error hoặc `[DONE]` sau một fragment chưa đóng.
3. **Death do upstream im lặng có log đúng mức chứng cứ:** phân biệt watchdog của app với disconnect ngoài; Cloudflare chỉ là một nguyên nhân khả dĩ, không phải kết luận chắc chắn.

### Non-goals

- **Non-stream: không làm gì** (user chốt bỏ fix 2 — không warning log, không padding). Non-stream >100s qua tunnel CF giữ nguyên là known limitation.
- Không đổi in-band error event schema, monitor/trace semantics, policy không retry mid-stream, hoặc ý nghĩa phân loại #7/#8 về việc upstream đã có content hay chưa.
- Không đảm bảo rằng Cloudflare là nguyên nhân đóng kết nối; cần log edge/origin để kết luận nếu có.

## 2. Thiết kế

### 2.1 Event-boundary buffering + keep-alive theo deadline (Fix 1) — `ProxyApp.RunStreamLoopAsync`

#### Buffer theo event

- Buffer các read từ Pipe vào `pending`; **chỉ viết xuống wire phần tới và gồm boundary event cuối** (`\n\n` hoặc `\r\n\r\n`). Phần event chưa đóng giữ lại cho lần đọc kế.
- Nhận diện boundary theo byte thật, không theo `Encoding`/text decode; giữ nguyên byte khi flush event hoàn chỉnh.
- Endpoint là tay viết `Response.Body` duy nhất. Không đưa buffer vào `UsageCapture`, vì tee còn phục vụ non-stream/aggregate.

#### Keep-alive theo absolute deadline

Dùng `Stopwatch.GetTimestamp()` (monotonic):

1. Khởi tạo `nextKeepAliveAt = now + KeepAliveIntervalMs`.
2. Mỗi vòng tính `remaining = max(0, nextKeepAliveAt - now)` và tạo `delayTask = Task.Delay(remaining, ct)`, chờ `readTask`, `delayTask`, `completion`.
3. Khi `now >= nextKeepAliveAt`, ghi `: keep-alive\n\n` nếu wire đang ở event boundary, rồi đặt `nextKeepAliveAt = now + KeepAliveIntervalMs`.
4. **Không reset deadline khi upstream read thành công.** Nếu trickle đến sau deadline, deadline vẫn thắng ngay vòng tiếp theo.
5. Chỉ gửi comment khi wire đang ở event boundary. Vì mọi event forward đều trọn vẹn, boundary luôn đúng tại thời điểm gửi comment.

Như vậy, trickle nhanh hơn mỗi 5s cũng không thể làm keep-alive im lặng qua nhiều chu kỳ.

#### Buffer cap bắt buộc

- `MaxPendingEventBytes = 1 * 1024 * 1024`.
- Nếu pending vượt cap mà chưa có boundary: dừng đường upstream bằng `UpstreamStreamDiedException("pending SSE event exceeds 1 MiB")` để giữ classification/logging hiện có. **Không forward event cụt và không retry mid-stream.**
- Cộng `upstreamBytes` bằng byte đã nhận trước khi dừng; `contentBytes` chỉ gồm byte event hoàn chỉnh đã forward thật.

### 2.2 Graceful drain vs abnormal drain

Tách hai đường để không phá frame:

| Trường hợp | Hành vi bắt buộc |
|---|---|
| Graceful completion (`[DONE]`/upstream đóng sạch) | Flush chỉ các event đã đóng. Nếu còn fragment chưa đóng, loại bỏ fragment đó thay vì nối `[DONE]` vào giữa nó. `[DONE]` bắt đầu tại event boundary. |
| `Aborted`/upstream error/in-band `upstream_disconnected` | Loại bỏ fragment chưa đóng **trước khi** append in-band error. Error luôn bắt đầu tại event boundary, không bao giờ nối vào `data:` fragment. |
| Client abort (`RequestAborted`) | Dừng ghi; pending bị bỏ, không flush. |

### 2.3 Hai byte counters, giữ nguyên semantics #7/#8

- `upstreamBytes`: tổng byte upstream đã nhận, kể cả byte trong event chưa đóng hoặc bị loại bỏ vì cap/abort. Dùng để phân loại upstream **đã có content** hay chưa.
- `contentBytes`: tổng byte event hoàn chỉnh thật sự forward xuống client, **không gồm keep-alive và không gồm fragment bị loại bỏ**. Dùng cho diagnostics nói chính xác bao nhiêu byte đã lên wire.

Phân loại #7 (chưa có content) / #8 (đã có content) dựa trên `upstreamBytes`, nên việc loại bỏ fragment vì framing/cap không làm mất dấu vết upstream đã gửi dữ liệu. `contentBytes` không còn bị dùng nhầm làm bằng chứng rằng upstream chưa gửi gì.

### 2.4 Chẩn đoán upstream silence (Fix 3) — refine helper sẵn có

`DispatcherLoop.StreamDeathHint` và test upstream-death hint đã tồn tại tại review base. Không thêm pattern match thứ hai và không ghi `Details = ex.ToString()` như thể đó là behavior mới.

Chỉ **refine helper hiện có** với hai message khác biệt theo exception:

| Exception | Message bổ sung |
|---|---|
| `UpstreamStreamDiedException` (kèm `SilenceGap`) | "Kết nối bị đóng ngoài sau {silence}s im lặng — nếu upstream đi qua Cloudflare tunnel, **có thể** là CF proxy read timeout (~100s); cần đối chiếu edge/origin logs để xác nhận." |
| `UpstreamIdleTimeoutException` (kèm threshold) | "App cắt theo watchdog {threshold}s vì upstream im lặng quá ngưỡng (`upstreamIdleTimeoutSec`)." |

Giữ `Details = ex.ToString()` và diagnostics hiện có, không duplicate hint cũ. Không đụng `UpstreamDiedBeforeContentException` (chết trước content, retry an toàn). Mid-stream vẫn `Aborted` + in-band `upstream_disconnected` theo schema hiện có.

## 3. Edge cases

| Case | Kết quả bắt buộc |
|---|---|
| Trickle partial event nhanh hơn mỗi 5s qua nhiều chu kỳ | Keep-alive vẫn được gửi theo deadline; event chưa đóng không bị forward. |
| Upstream pause >5s giữa dòng data | Keep-alive chảy đều ở boundary; khi resume, event được ghép nguyên byte. |
| Event CRLF (`\r\n\r\n`) | Flush đúng boundary, không treo buffer. |
| Pending vượt 1 MiB không có boundary | Dừng bằng `UpstreamStreamDiedException`, không forward partial, không retry mid-stream. |
| Partial event + `Aborted` | Fragment bị loại bỏ; in-band error bắt đầu tại boundary và toàn bộ frames parse được. |
| Partial event + graceful end | Chỉ event đã đóng được flush; fragment không được nối vào `[DONE]`. |
| Fragment bị loại bỏ sau khi upstream đã gửi content | `upstreamBytes > 0` giữ phân loại #8; `contentBytes` chỉ phản ánh event hoàn chỉnh thật sự forward. |
| Client ngắt trong lúc pending giữ byte | Không ghi thêm gì; pending bị bỏ. |

## 4. Testing

### Unit — `StreamLoopTests.cs`

- Trickle partial event nhanh hơn mỗi 5s qua **ít nhất 3 keep-alive intervals**: comments xuất hiện định kỳ, không forward incomplete event.
- Pause giữa dòng rồi resume: byte của event ghép lại đúng nguyên vẹn, không dính comment vào JSON.
- CRLF flush tại `\r\n\r\n`.
- Pending vượt 1 MiB: dừng bằng `UpstreamStreamDiedException`, không forward partial và không retry.
- Partial + `Aborted`: toàn bộ frames parse được; error bắt đầu tại boundary, không nối fragment.
- Partial + graceful end: chỉ event hoàn chỉnh được flush; `[DONE]` đứng sau boundary.
- `upstreamBytes` và `contentBytes` được kiểm riêng cho cả fragment bị loại bỏ, giữ đúng phân loại #7/#8.
- Các test keep-alive/abort hiện có giữ xanh không đổi.

### Unit — `DispatcherLoopTests.cs`

- Với `UpstreamStreamDiedException`: hint có silence gap và nói CF chỉ là **possible cause**, không khẳng định "không phải lỗi upstream".
- Với `UpstreamIdleTimeoutException`: hint nói rõ app cắt theo watchdog + threshold.
- Test upstream-death hint hiện có vẫn xanh và không duplicate diagnostic cũ.

### Integration — `ProxyQueueIntegrationTests.cs`

- Partial event gửi trickle → client vẫn nhận comment theo deadline; sau resume, toàn bộ SSE hợp lệ và byte đúng (qua `StripKeepAlive`).
- Partial event + `Aborted`: toàn bộ frame parse được, không có `upstream_disconnected` dính vào fragment.
- Pending >1 MiB: upstream bị dừng, client không nhận event cụt; log/classification giữ đúng.
- `contentBytes` không bao gồm comment/fragment bị loại; `upstreamBytes` vẫn phản ánh byte nhận được.

## 5. Files dự kiến

| File | Thay đổi |
|---|---|
| `src/RouterBalancing.Core/Server/ProxyApp.cs` | Event buffering, monotonic keep-alive deadline, pending cap, graceful/abnormal drain, hai byte counters |
| `src/RouterBalancing.Core/Engine/DispatcherLoop.cs` | Refine `StreamDeathHint` sẵn có với silence gap và possible-CF wording |
| `router balancing test/Server/StreamLoopTests.cs` | Trickle/deadline, cap, graceful/abnormal drain và counters |
| `router balancing test/Server/ProxyQueueIntegrationTests.cs` | Trickle + abort framing integration |
| `router balancing test/Engine/DispatcherLoopTests.cs` | Possible-CF/watchdog hint và no-duplicate regression |
