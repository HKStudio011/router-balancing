# Feature Specification: Upstream EOF Grace

**Feature Branch**: `feature/upstream-eof-grace`

**Created**: 2026-11-26

**Status**: Draft

**Input**: User description: "Khi upstream kết thúc stream nhưng chưa có finish_reason, client được giữ kết nối và chờ trong một khoảng ân hạn. Kiểm tra byte mới mỗi 1 giây, keep-alive mỗi 5 giây; giá trị 0 chờ tới khi client tự ngắt, giá trị lớn hơn 0 báo timeout chờ terminal khi hết thờigian cấu hình. Không replay hoặc bịa terminal."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Chờ terminal trong một khoảng ân hạn có giới hạn (Priority: P1)

Ngườig vận hành cấu hình một khoảng thờigian lớn hơn 0. Khi upstream đã gửi một phần nội dung nhưng kết thúc trước khi cung cấp terminal hợp lệ, client vẫn giữ kết nối và nhận keep-alive trong khi hệ thống chờ terminal hoặc dữ liệu tiếp theo. Chỉ khi hết khoảng ân hạn mà không có dữ liệu mới, client mới nhận lỗi timeout chờ terminal.

**Why this priority**: Đây là hành vi cốt lõi giúp ngườig vận hành chủ động chọn thờigian chờ thay vì nhận lỗi ngay khi upstream kết thúc bất thường.

**Independent Test**: Cấu hình 3 giây, kết thúc upstream sau một phần nội dung mà không gửi terminal, rồi quan sát client. Không có lỗi trước 3 giây, keep-alive vẫn tới định kỳ và lỗi timeout chờ terminal xuất hiện sau deadline.

**Acceptance Scenarios**:

1. **Given** stream đã có nội dung nhưng chưa có terminal, **When** upstream kết thúc với cấu hình 3 giây, **Then** client vẫn mở và không nhận lỗi trước deadline.
2. **Given** không có byte mới trong suốt 3 giây ân hạn, **When** deadline được đạt, **Then** client nhận đúng một lỗi timeout chờ terminal và kết nối kết thúc.
3. **Given** khoảng ân hạn đang chạy, **When** mỗi chu kỳ 1 giây trôi qua, **Then** hệ thống thực hiện đúng một lần kiểm tra dữ liệu mới mà không tạo vòng lặp đọc liên tục.

---

### User Story 2 - Để client toàn quyền quyết định thờigian chờ (Priority: P2)

Ngườig vận hành đặt giá trị 0. Sau khi upstream đã gửi một phần nội dung nhưng kết thúc thiếu terminal, hệ thống giữ kết nối phía client và tiếp tục keep-alive vô thờigian. Hệ thống không tự tạo lỗi timeout; chỉ client quyết định khi nào ngắt hoặc kết thúc chờ.

**Why this priority**: Ngườig dùng cần một cách kiểm tra provider/client timeout độc lập với timeout do app tạo ra, thay vì bị app kết thúc sớm.

**Independent Test**: Đặt giá trị 0, tạo EOF thiếu terminal và giữ request trong một khoảng kiểm tra dài hơn ngưỡng 300 giây trước đây. Không có lỗi do app tạo; keep-alive vẫn đều. Khi client ngắt, mọi chờ đợi được hủy.

**Acceptance Scenarios**:

1. **Given** cấu hình 0 và stream đã có nội dung nhưng chưa có terminal, **When** upstream kết thúc, **Then** client vẫn mở và không nhận lỗi do app tạo trong suốt thờigian chờ.
2. **Given** client đang chờ vô hạn, **When** client tự ngắt, **Then** mọi polling và keep-alive dừng ngay mà không có lỗi bổ sung.
3. **Given** kết nối vẫn đang chờ, **When** chu kỳ 5 giây tới, **Then** client nhận keep-alive tại boundary hợp lệ và không có byte nội dung bị thay đổi.

---

### User Story 3 - Tiếp tục stream nếu dữ liệu xuất hiện lại (Priority: P2)

Trong khoảng ân hạn, nếu nguồn dữ liệu non-standard gửi byte mới sau một lần kết thúc tạm thởi, client nhận tiếp dữ liệu đó theo đúng thứ tự và hệ thống quay lại trạng thái stream bình thường. Nếu sau đó terminal hợp lệ xuất hiện, request hoàn tất mà không có lỗi timeout.

**Why this priority**: Grace period chỉ có giá trị nếu hệ thống không bỏ lỡ hoặc làm hỏng dữ liệu có thể xuất hiện trở lại.

**Independent Test**: Tạo một lần kết thúc tạm thởi thiếu terminal, gửi byte mới sau 1,5 giây rồi gửi terminal hợp lệ. Không có lỗi timeout và toàn bộ byte nội dung được giữ nguyên.

**Acceptance Scenarios**:

1. **Given** grace period đang chạy, **When** byte mới xuất hiện trước deadline, **Then** hệ thống hủy deadline hiện tại và tiếp tục xử lý stream bình thường.
2. **Given** dữ liệu mới được tiếp nhận trong grace, **When** terminal hợp lệ xuất hiện sau đó, **Then** request hoàn tất bình thường mà không có lỗi do grace tạo.
3. **Given** stream non-standard kết thúc tạm thởi lần nữa trước terminal, **When** lần kết thúc mới xảy ra, **Then** một grace period mới bắt đầu với đầy đủ thờigian cấu hình.

---

### Edge Cases

- Upstream kết thúc trước khi có bất kỳ nội dung nào: giữ retry/failover hiện có, không giữ client trong grace.
- Upstream kết thúc sau khi đã có terminal hợp lệ: hoàn tất ngay, không tạo grace.
- Upstream gặp lỗi transport thay vì kết thúc bằng EOF: giữ phân loại lỗi mạng hiện có, không biến thành grace.
- Client ngắt trong lúc chờ hoặc trong lần kiểm tra dữ liệu kế tiếp: hủy ngay mọi timer, polling và ghi dữ liệu tiếp theo.
- Còn một SSE event chưa đóng khi bắt đầu grace: không gửi fragment; keep-alive chỉ đi ở event boundary.
- Byte mới giúp hoàn tất một event trong grace: ghép và forward đúng byte theo thứ tự.
- Deadline đến khi vẫn còn fragment chưa đóng: loại bỏ fragment trước khi gửi lỗi tại event boundary.
- Thờigian cấu hình nhỏ hơn chu kỳ polling 1 giây: chỉ chờ phần thờigian còn lại, kiểm tra cuối tại deadline rồi mới báo lỗi.
- Nhiều lần EOF liên tiếp không có byte: không reset deadline của cùng một grace và không tạo polling song song.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Sau khi upstream đã cung cấp nội dung nhưng kết thúc thiếu terminal hợp lệ, hệ thống MUST giữ kết nối phía client trong grace period thay vì báo lỗi ngay.
- **FR-002**: Với cấu hình lớn hơn 0, hệ thống MUST bắt đầu deadline từ lần EOF đầu tiên của mỗi grace period và MUST NOT reset deadline khi các lần đọc tiếp theo vẫn trả EOF.
- **FR-003**: Với cấu hình lớn hơn 0 và không có byte mới, hệ thống MUST gửi đúng một lỗi timeout chờ terminal khi hết thờigian cấu hình; lỗi này MUST NOT được trình bày là lỗi watchdog EOF 300 giây cố định.
- **FR-004**: Với cấu hình 0, hệ thống MUST chờ cho tới khi client tự ngắt hoặc dữ liệu/terminal xuất hiện, và MUST NOT tự tạo timeout cho client.
- **FR-005**: Trong grace period, hệ thống MUST kiểm tra dữ liệu mới theo chu kỳ cố định 1 giây, với phần chờ cuối được rút ngắn theo deadline nếu cần; không được có hai polling song song cho cùng stream.
- **FR-006**: Nếu byte mới xuất hiện trong grace, hệ thống MUST hủy deadline hiện tại, giữ nguyên thứ tự byte và quay lại xử lý stream bình thường. Một EOF mới trước terminal bắt đầu một grace mới.
- **FR-007**: Hệ thống MUST gửi keep-alive cho client theo chu kỳ 5 giây tại event boundary trong suốt grace, kể cả khi cấu hình là 0.
- **FR-008**: Client cancellation MUST hủy ngay deadline, polling và mọi ghi dữ liệu tiếp theo; không tạo lỗi timeout sau khi client đã ngắt.
- **FR-009**: Hệ thống MUST NOT thay đổi byte nội dung đã nhận, bịa `finish_reason`, tự thêm `[DONE]`, replay request sau khi client đã nhận nội dung, hoặc biến completion cụt thành thành công.
- **FR-010**: Fragment chưa đóng MUST NOT được nối với keep-alive hoặc lỗi. Khi kết thúc bằng timeout, fragment đó bị loại bỏ trước khi lỗi được gửi tại event boundary.
- **FR-011**: EOF trước nội dung MUST giữ retry/failover hiện có; EOF sau terminal hợp lệ MUST hoàn tất bình thường; lỗi transport khác EOF MUST giữ phân loại hiện có.
- **FR-012**: Completion bị cụt hoặc chưa có terminal MUST tiếp tục bị loại khỏi dataset bất kể client chờ bao lâu hoặc kết thúc bằng cách nào.
- **FR-013**: Grace period chỉ áp dụng cho Chat Completions streaming sau khi đã có nội dung; các giao thức streaming khác và non-stream aggregation MUST giữ hành vi hiện tại.
- **FR-014**: Logs/diagnostics MAY ghi thờigian EOF, deadline, số lần polling và kết quả grace nhưng MUST NOT ghi secret, prompt, nội dung file hoặc raw tool arguments.

### Key Entities *(include if feature involves data)*

- **Grace Policy**: Giá trị ngườig dùng cấu hình gồm thờigian chờ tối đa hoặc 0 = vô hạn, chu kỳ kiểm tra 1 giây và chu kỳ keep-alive 5 giây.
- **Grace Session**: Trạng thái của một lần chờ sau EOF gồm thờigian bắt đầu, deadline tùy chọn, trạng thái polling và kết quả cuối cùng.
- **Client Stream**: Kết nối phía client được giữ mở trong grace, có trạng thái event boundary và khả năng bị client hủy bất cứ lúc nào.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Với cấu hình thử nghiệm 3 giây và không có byte mới, 100% request không nhận lỗi trước 3 giây và nhận đúng một timeout trong khoảng 3,0–3,5 giây kể từ EOF đầu tiên.
- **SC-002**: Với cấu hình 0 và client stream đang ở event boundary, một request thử nghiệm kéo dài ít nhất 10 phút không nhận bất kỳ timeout nào do app tạo và vẫn nhận keep-alive theo chu kỳ 5 giây với sai số tối đa 1 giây mỗi chu kỳ.
- **SC-003**: Khi byte mới xuất hiện sau 1,5 giây trong grace, 100% byte nội dung và terminal hợp lệ được giữ nguyên, không có timeout hoặc duplicate polling.
- **SC-004**: Khi client ngắt trong grace, mọi timer/polling dừng trong tối đa 250ms và không có lỗi hoặc byte mới được ghi sau đó.
- **SC-005**: 100% completion thiếu terminal tiếp tục bị loại khỏi dataset và không có request nào bị replay sau khi client đã nhận nội dung.
- **SC-006**: Các kịch bản EOF trước nội dung, terminal hợp lệ, lỗi transport và non-stream giữ 100% hành vi hiện có; toàn bộ test suite hiện hữu vẫn đạt.

## Assumptions

- Grace là cơ chế điều khiển trải nghiệm chờ của client, không đảm bảo một HTTP stream chuẩn có thể gửi byte mới sau EOF. Khả năng tiếp tục chỉ được hỗ trợ cho nguồn non-standard có hành vi đó.
- Phạm vi ưu tiên là Chat Completions streaming đã có nội dung. Không giữ non-stream response mở chỉ để gửi keep-alive không tương thích.
- EOF trước nội dung vẫn an toàn để retry/failover và không cần làm chậm quá trình phục hồi bằng grace.
- Ngườig vận hành chấp nhận rằng cấu hình 0 có thể giữ tài nguyên kết nối vô thờigian cho tới khi client ngắt.
- Không thay đổi chính sách retry hiện có, schema lỗi ngoài thông tin cần thiết để phân biệt timeout chờ terminal, hoặc dữ liệu dataset hợp lệ.
