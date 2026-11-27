# Implementation Plan: Upstream EOF Grace

**Branch**: `feature/upstream-eof-grace` | **Date**: 2026-11-26 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/001-upstream-eof-grace/spec.md`

**Note**: This template is filled in by the `/speckit.plan` command; its definition describes the execution workflow.

## Summary

Thêm grace sau EOF cho Chat streaming đã có content: tee giữ client mở, polling tuần tự mỗi 1 giây, tái sử dụng keep-alive 5 giây và cho phép byte non-standard resume. Setting `0` map thành infinite grace; giá trị dương kết thúc bằng `timeout_waiting_for_terminal` tại deadline. Chat-only capability truyền setting theo request mà không đổi Responses/non-stream; `PeekFinish()` bảo toàn parser. Endpoint được nâng lên event-boundary buffering để keep-alive/error không bao giờ dính fragment, đồng thởi giữ byte-forward, pre-content failover, no-replay và dataset exclusion.

## Technical Context

**Language/Version**: C# / .NET 10, nullable reference types enabled

**Primary Dependencies**: ASP.NET Core Kestrel, `System.IO.Pipelines`, SSE parser/state hiện có, .NET MAUI Blazor Hybrid cho Settings; không thêm dependency production

**Storage**: SQLite qua cấu hình `upstreamIdleTimeoutSec` hiện có; không thay đổi schema hoặc migration

**Testing**: xUnit v3 qua Microsoft.Testing.Platform, deterministic scripted stream/clock, focused unit/integration tests rồi full suite; Go `test`/`vet` và Windows TFM build

**Target Platform**: RouterBalancing.Core chạy trong proxy host của app .NET MAUI; Windows là release target, giữ Core tương thích các nền hiện có

**Project Type**: .NET MAUI Blazor Hybrid desktop app với embedded OpenAI-compatible proxy

**Performance Goals**: Không quay vòng đọc khi EOF; polling 1 giây/stream, keep-alive 5 giây khi ở event boundary, timeout trong 500ms sau deadline và client cancellation trong 250ms

**Constraints**: Byte-forward nguyên vẹn; không replay sau content; không bịa terminal; ResponsesProtocol và non-stream aggregation không đổi; completion cụt không vào dataset; không log secret/prompt/raw tool arguments; thay đổi bằng TDD

**Scale/Scope**: Mỗi Chat streaming request đã có content và thiếu terminal; giá trị 0 được phép giữ kết nối vô hạn theo lựa chọn của ngườig vận hành; pending event cap 1 MiB

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Constitution file chưa định nghĩa principle cụ thể. Áp dụng quality gates từ `AGENTS.md` và spec:

- PASS — Spec đã có acceptance criteria; không tạo plan song song ngoài Spec Kit.
- PASS — TDD bắt buộc: test RED trước production change cho từng hành vi mới.
- PASS — Giữ nullable, byte-forward, no-retry-after-content và truncated-dataset exclusion.
- PASS — Không secret/prompt/file content/raw tool arguments trong diagnostics.
- PASS — Phải chạy focused/full tests, Windows build và independent review trước khi kết luận.
- PASS — ResponsesProtocol, non-stream aggregation và pre-content failover giữ nguyên hành vi.
- PASS — Event-boundary buffering giữ nguyên thứ tự/nội dung byte của event hoàn chỉnh; chỉ thay đổi thờigian flush và loại fragment khi abnormal theo spec.

**Post-design re-check:** PASS — `research.md`, `data-model.md`, `contracts/` và `quickstart.md` không mở rộng sang replay, storage migration, Responses hoặc synthetic terminal. Không có violation cần biện minh.

## Project Structure

### Documentation (this feature)

```text
specs/001-upstream-eof-grace/
├── plan.md
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   └── streaming-grace.md
└── tasks.md             # /speckit.tasks output
```

### Source Code (repository root)

```text
src/RouterBalancing.Core/
├── Engine/
│   ├── UsageCapture.cs                 # Active/grace state machine
│   ├── SseDataEventParser.cs           # Non-destructive PeekFinish
│   ├── ChatCompletionsProtocol.cs      # Chat-only grace capability
│   ├── IChatEofGraceTee.cs             # Per-request internal capability
│   ├── UpstreamTerminalTimeoutException.cs
│   ├── DispatchOutcome.cs              # Init-only timeout flag
│   └── SseErrorEvent.cs                # Distinct timeout event builder
└── Server/
    └── ProxyApp.cs                     # Event buffering, keep-alive/drain/error

router balancing test/
├── Engine/                # Grace/parser/protocol regression tests
└── Server/                # Keep-alive, cancellation và boundary integration tests
```

**Structure Decision**: Tái sử dụng Engine/Server và test projects hiện có. Không tạo project mới, không thay đổi ResponsesProtocol hoặc storage.

## Complexity Tracking

Event-boundary buffering là phần mở rộng bắt buộc của FR-007/FR-010, không phải refactor tùy ý: immediate forwarding không thể vừa drop fragment đã gửi vừa phát error độc lập đúng SSE boundary. Các thành phần khác chọn seam nhỏ nhất có đủ state; không có violation của constitution/project gates cần biện minh thêm.
