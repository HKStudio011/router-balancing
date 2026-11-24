# Agent Tool Routing Guide Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thêm hướng dẫn ngắn, thiên về ra quyết định để agent dùng đúng Superpowers, Speckit, CodeGraph, OCR, security/frontend skills và Visual Companion trong `AGENTS.md`.

**Architecture:** Một section `Bản đồ công cụ & workflow` sau CodeGraph, gồm ranh giới workflow, bảng lựa chọn theo tình huống và năm pipeline mẫu. Giữ tài liệu plugin là source of truth cho flags; AGENTS.md chỉ điều phối khi nào nên dùng từng công cụ.

**Tech Stack:** Markdown, Git, OpenCode skills/commands/plugins đã cài.

**Spec:** `docs/superpowers/specs/2026-11-23-agent-tool-routing-design.md`

## Global Constraints

- Section mới khoảng 70–90 dòng, tiếng Việt, thiên về quyết định; không sao chép tài liệu/tham số chi tiết của plugin.
- Không trộn chuỗi artifacts/workflow Superpowers và Speckit trong cùng feature; process skill trước implementation skill.
- Không sửa cấu hình global OpenCode, plugin, command, skill, quy chuẩn code hay lệnh build/test hiện có.
- Không tự `codegraph init`, tự mở Visual Companion/browser hoặc chạy OCR/Speckit/security audit ngoài điều kiện ghi rõ.
- Không tạo duplicate OCR review seat: `ocr_review` là direct OCR LLM review, `ocr delegate` là host-agent delegation.

## Review Focus

- User gọi Speckit rõ nhưng agent vẫn mở đầu bằng Superpowers planning — phải giữ Speckit làm workflow chính, chỉ dùng skill bổ trợ không thay artifacts.
- Bug report không được đi thẳng vào fix — pipeline phải bắt đầu `systematic-debugging`, CodeGraph và bằng chứng RED trước production change.
- `speckit.checklist` phải được mô tả là kiểm tra chất lượng requirements, không phải test implementation.
- Visual Companion chỉ xuất hiện khi phần trực quan là quyết định của người dùng và sau khi được chấp nhận — không tự mở browser cho quyết định kỹ thuật.
- `security-audit` full workflow chỉ khi explicit audit/pentest/full review/report; `frontend-design` không bắt buộc cho mọi CSS/bug nhỏ.

---

### Task 1: Thêm bản đồ công cụ & workflow vào AGENTS.md

**Files:**
- Modify: `AGENTS.md:36-56` (cập nhật điều kiện CodeGraph và chèn section mới ngay sau đó)
- Create: `docs/superpowers/specs/2026-11-23-agent-tool-routing-design.md`
- Create: `docs/superpowers/plans/2026-11-23-agent-tool-routing.md`

**Interfaces:**
- Consumes: các command/skill thật đã kiểm chứng `/speckit.*`, `ocr_review`, `ocr_health`, `ocr delegate preview/rule`, `security-audit`, `frontend-design`, Visual Companion và CodeGraph MCP/shell.
- Produces: hướng dẫn định tuyến duy nhất trong repo; không tạo command/skill mới.

- [ ] **Step 1: Xác nhận precheck tài liệu**

Đọc `AGENTS.md` và spec. Xác nhận CodeGraph vẫn ghi `(dự kiến)` và chưa có section `Bản đồ công cụ & workflow`; đây là hai khiếm khuyết tài liệu cần sửa. Không thay đổi các section khác.

- [ ] **Step 2: Cập nhật CodeGraph và chèn routing guide**

Đổi mô tả index thành điều kiện: CodeGraph đã cài sẵn nhưng chỉ dùng khi repo có `.codegraph`; nếu không có thì bỏ qua và không tự init.

Chèn section `## Bản đồ công cụ & workflow` sau CodeGraph với đúng các khối:

1. `Nguyên tắc chọn workflow` — Superpowers mặc định, Speckit khi được gọi rõ/chọn Spec-Driven Development, không trộn chuỗi artifacts; process trước implementation.
2. `Chọn công cụ theo tình huống` — bảng/list ngắn cho Superpowers, Speckit, CodeGraph, `ocr_review`/`ocr_health`, `ocr delegate preview/rule`, `security-audit`, `frontend-design`, Visual Companion.
3. Ghi chú Speckit chính xác: `specify → clarify → plan → tasks → analyze → implement → converge`; `checklist` kiểm requirements, `constitution` cập nhật constitution, `github.taskstoissues` tạo issues.
4. Ghi chú OCR: direct review versus delegation; dùng `preview` lấy metadata và `rule` lấy resolved rules; không trùng review seat.
5. Năm pipeline: bugfix, feature mặc định, Speckit, UI và security đúng thiết kế.

Không thêm đường dẫn cài đặt cá nhân, version, secret hoặc flags dễ lỗi thời; khi cần flags, chỉ dẫn `--help`/tài liệu chính thức.

- [ ] **Step 3: Kiểm nội dung theo acceptance checklist**

Đọc section mới và xác nhận đủ bảy nhóm người dùng yêu cầu: Superpowers, Speckit, CodeGraph, OCR delegate, security-audit, frontend-design, Visual Companion (đồng thời có `ocr_review`/`ocr_health` để phân biệt).

Kiểm năm Review Focus ở trên và các quy tắc: không tự init/browser, full security workflow đúng điều kiện, frontend-design đúng scope, không trộn workflow. Chạy:

```powershell
git diff --check AGENTS.md
git diff --stat
```

Expected: không whitespace error; diff chỉ gồm AGENTS.md và hai docs plan/spec dự kiến.

- [ ] **Step 4: Commit tài liệu**

```powershell
git add AGENTS.md docs/superpowers/specs/2026-11-23-agent-tool-routing-design.md docs/superpowers/plans/2026-11-23-agent-tool-routing.md
git commit -m "docs: add agent tool routing guide"
```

Expected: một docs commit; `git status --short` sạch.
