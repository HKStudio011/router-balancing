# Bản đồ công cụ & workflow trong AGENTS.md — thiết kế đã duyệt

Ngày: 2026-11-23
Trạng thái: đã duyệt hướng ngắn, thiên về ra quyết định

## Mục đích

Bổ sung hướng dẫn để agent chọn đúng công cụ đã cài trong môi trường OpenCode, tránh bỏ qua skill/command phù hợp và tránh trộn các workflow mâu thuẫn nhau. Đây là thay đổi tài liệu hướng dẫn vận hành, không thay production code hay cấu hình plugin.

## Phạm vi nội dung

Thêm section `Bản đồ công cụ & workflow` ngay sau section CodeGraph trong `AGENTS.md`, khoảng 70–90 dòng:

1. **Nguyên tắc ưu tiên và ranh giới workflow**
   - Superpowers là đường mặc định cho feature/bug ngoài lệnh gọi riêng.
   - Speckit được dùng khi người dùng gọi rõ `/speckit.*` hoặc chọn Spec-Driven Development.
   - Không trộn artifacts và chuỗi duyệt của Superpowers với Speckit trong cùng một feature; skill bổ trợ như CodeGraph/TDD/review vẫn dùng được nhưng phải theo workflow chính đã chọn.
   - Process skill đi trước implementation skill; không dùng công cụ chỉ vì có sẵn.

2. **Bảng chọn công cụ theo tình huống**
   - Superpowers: `using-superpowers`, `brainstorming`, `systematic-debugging`, TDD, `writing-plans`, execution và verification.
   - Speckit: `specify → clarify → plan → tasks → analyze → implement → converge`; ghi chú riêng `checklist` kiểm tra chất lượng requirements, `constitution` chỉ cập nhật constitution, `github.taskstoissues` chuyển tasks thành issues.
   - CodeGraph: dùng trước grep/read khi repo có `.codegraph`; không tự `codegraph init`.
   - `ocr_review`/`ocr_health`: review trực tiếp bằng OCR LLM hoặc kiểm tra cấu hình; preview scope trước khi cần.
   - `ocr delegate`: `preview` lấy file/mode/ref metadata, `rule` lấy resolved rules cho host-agent delegation, không gọi LLM qua OCR; không tạo thêm review seat trùng với `ocr_review`.
   - `security-audit`: dùng cho security question/review; full workflow chỉ khi explicit audit/pentest/full review/report.
   - `frontend-design`: UI mới hoặc reshaping UI; không bắt buộc cho mọi CSS/bug nhỏ.
   - Visual Companion của `brainstorming`: chỉ đề xuất just-in-time khi phần trực quan là quyết định của người dùng, chỉ dùng sau khi được chấp nhận và đọc `visual-companion.md`; không dùng cho quyết định thuần kỹ thuật.

3. **Năm pipeline mẫu ngắn**
   - Bugfix: systematic-debugging → CodeGraph → TDD → focused/full tests → review.
   - Feature mặc định: brainstorming → design được duyệt → plan → TDD/execution → verification/review.
   - Speckit: specify/clarify → plan → tasks → analyze → implement → converge.
   - UI: frontend-design → Visual Companion nếu cần người dùng chọn aesthetics → implementation/review.
   - Security: xác định scope → security-audit đúng mức → ghi findings theo severity và evidence.

4. **Chỉnh mô tả CodeGraph hiện có**
   - Đổi `Repo này (dự kiến) được index` thành hướng dẫn dựa trên điều kiện `.codegraph` thật; CodeGraph đã cài sẵn nhưng chỉ dùng khi repo có index.
   - Giữ phần hướng dẫn MCP/shell và không tự init, tránh trùng nội dung ở section mới bằng cách dẫn lại section CodeGraph.

## Các cách đã cân nhắc

- **Ngắn, thiên về ra quyết định (đã chọn):** dễ agent đọc và áp dụng, ít trùng tài liệu plugin.
- **Tham chiếu đầy đủ mọi command/tham số:** không chọn vì làm AGENTS.md quá dài và dễ lỗi thời; help của từng tool vẫn là source of truth cho flags.

## Ngoài phạm vi

- Không thay đổi quy chuẩn code, comment, ngôn ngữ, lệnh build/test hiện có.
- Không sửa cấu hình global OpenCode, plugin, command hoặc skill đã cài.
- Không mô tả version/tham số chi tiết có thể thay đổi; khi cần flags, agent dùng `--help` hoặc tài liệu chính thức.
- Không bắt agent gọi mọi công cụ trong mọi task, không tự mở browser, không tự chạy OCR/Speckit/security audit ngoài điều kiện ghi rõ.

## Xác minh

- Đọc lại section mới theo checklist từng công cụ người dùng yêu cầu.
- Kiểm các tên command thật: `/speckit.*`, `ocr_review`, `ocr_health`, `ocr delegate preview/rule`, `security-audit`, `frontend-design`, Visual Companion.
- Kiểm không mâu thuẫn với quy tắc Superpowers hiện có và hướng dẫn CodeGraph ở đầu file.
- `git diff --check AGENTS.md`; review diff chỉ gồm tài liệu dự kiến.
