---
description: "Task list for Upstream EOF Grace implementation"
---

# Tasks: Upstream EOF Grace

**Input**: Design documents from `/specs/001-upstream-eof-grace/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: TDD required. Every behavior change starts with a failing test that is observed RED before production implementation.

**Organization**: Foundational parser/buffering work first, then finite grace (MVP), infinite grace, and resumed streams.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies on incomplete tasks)
- **[Story]**: US1 = finite grace; US2 = zero/infinite grace; US3 = resumed stream

## Phase 1: Setup (Shared Infrastructure)

- [X] T001 Create cancellation-aware scripted resumable stream and deterministic monotonic/delay clock in `router balancing test/Engine/ResumableSseStream.cs` and `router balancing test/Engine/DeterministicGraceClock.cs`; support content/EOF/exception steps, read count/timestamps/max concurrency and verify no test utility logs payload content

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Preserve parser state and guarantee safe SSE boundaries before adding grace behavior

- [X] T002 [P] Add failing `PeekFinish` tests for partial data, multiline/pending CR, repeated peek and resume without duplication in `router balancing test/Engine/SseDataEventParserTests.cs`; cite `data-model.md → Parser State`
- [X] T003 [P] Implement non-destructive `PeekFinish()` by cloning `_line`, `_data`, `_pendingCr` in `src/RouterBalancing.Core/Engine/SseDataEventParser.cs` (depends on T002); leave existing `Finish()` and exact terminal behavior unchanged; cite `data-model.md → Parser State._line`, `Parser State._data`, `Parser State._pendingCr`
- [X] T004 [P] Add failing event-boundary tests for complete-event flush, safe keep-alive, abnormal fragment drop, pending cap and separate byte counters in `router balancing test/Server/StreamLoopTests.cs`; cite `data-model.md → Grace Policy.PendingEventLimit`, `Grace Policy.KeepAliveInterval`, `Client Pending Buffer.upstreamBytes`, `Client Pending Buffer.contentBytes`, `Client Pending Buffer.pending`
- [X] T005 [P] Implement 1 MiB event-boundary buffering and separate upstream/content counters in `src/RouterBalancing.Core/Server/ProxyApp.cs` (depends on T004); preserve complete-event byte order and keep endpoint as sole pipe completer; cite `data-model.md → Grace Policy.PendingEventLimit`, `Grace Policy.KeepAliveInterval`, `Client Pending Buffer.upstreamBytes`, `Client Pending Buffer.contentBytes`, `Client Pending Buffer.pending`
- [X] T006 [P] Add/confirm legacy no-grace, pre-content EOF, transport exception and exact-terminal regression tests in `router balancing test/Engine/UsageCaptureTests.cs`, keeping release `v1.12.3` terminal cases rather than the dirty worktree versions
- [X] T007 Implement internal tri-state grace options and production monotonic/delay clock in `src/RouterBalancing.Core/Engine/UsageCapture.cs` (depends on T001 and T006): `null` = legacy, infinite = setting 0, positive = finite; no behavior change for callers that omit options. Cite `data-model.md → Grace Policy.GracePeriod`, `Grace Policy.PollInterval`

**Checkpoint**: Parser peek, endpoint buffering and legacy behavior are green independently

---

## Phase 3: User Story 1 - Finite Grace (Priority: P1) 🎯 MVP

**Goal**: After post-content EOF without terminal, wait the configured finite grace and emit exactly one terminal-wait timeout only at deadline

**Independent Test**: A 3-second scripted EOF produces no early error, polls at 1-second cadence, then emits one standalone `timeout_waiting_for_terminal` between 3.0–3.5 seconds with no retry or synthetic terminal

### Tests for User Story 1 ⚠️ Write first and observe RED

- [X] T008 [P] [US1] Add failing finite-grace tests for monotonic deadline, no reset on repeated EOF, final read at deadline, grace shorter than poll, pre-content and transport paths in `router balancing test/Engine/UsageCaptureTests.cs`; cite `data-model.md → Grace Policy.PollInterval` and `Grace Session.TimedOut`
- [X] T009 [P] [US1] Add failing handler/endpoint tests for distinct timeout exception/outcome, one `timeout_waiting_for_terminal`, no retry/no DONE/no dataset record and standalone framing in `router balancing test/Engine/ProxyRequestHandlerTests.cs` and `router balancing test/Server/ProxyRetryIntegrationTests.cs`; reference `contracts/streaming-grace.md → Timeout Event`

### Implementation for User Story 1

- [X] T010 [US1] Implement the sequential Active/GraceAfterEof finite state machine in `src/RouterBalancing.Core/Engine/UsageCapture.cs` (depends on T003, T005, T007, T008): one outstanding read, monotonic deadline, final read and cancellation-aware delay/read; cite `data-model.md → Grace Session` and `Grace Session Transitions`
- [X] T011 [US1] Add init-only `TerminalWaitTimedOut` metadata and `UpstreamTerminalTimeoutException` in `src/RouterBalancing.Core/Engine/UsageCapture.cs` and `src/RouterBalancing.Core/Engine/UpstreamTerminalTimeoutException.cs` (depends on T010); keep `EndedWithoutTerminal`/`CompletionComplete` semantics per `data-model.md → Timeout Metadata.TeeResult.TerminalWaitTimedOut`
- [X] T012 [US1] Implement per-request Chat-only capability and handler plumbing in `src/RouterBalancing.Core/Engine/IChatEofGraceTee.cs`, `src/RouterBalancing.Core/Engine/ChatCompletionsProtocol.cs` and `src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs` (depends on T011); map setting 0 to infinite and do not alter `IProxyProtocol` or Responses signatures
- [X] T013 [US1] Map timeout through Aborted flag and standalone SSE error builder in `src/RouterBalancing.Core/Engine/DispatchOutcome.cs`, `src/RouterBalancing.Core/Engine/DispatcherLoop.cs`, `src/RouterBalancing.Core/Engine/SseErrorEvent.cs` and `src/RouterBalancing.Core/Server/ProxyApp.cs` (depends on T009 and T011); no Fatal/Retryable conversion; cite `data-model.md → Timeout Metadata.Aborted.TerminalWaitTimedOut`
- [X] T014 [US1] Run the finite-grace, parser, handler and endpoint focused tests and verify exact-terminal/byte-forward/no-dataset regressions in `router balancing test/Engine/UsageCaptureTests.cs`, `router balancing test/Engine/SseDataEventParserTests.cs`, `router balancing test/Engine/ProxyRequestHandlerTests.cs` and `router balancing test/Server/ProxyRetryIntegrationTests.cs`

**Checkpoint**: User Story 1 is independently functional and green

---

## Phase 4: User Story 2 - Zero/Infinite Grace (Priority: P2)

**Goal**: Setting 0 keeps the client open without app-generated timeout until bytes arrive or the client cancels

**Independent Test**: A zero-grace scripted EOF stays active with sequential polling and keep-alive; cancellation stops both server and endpoint work within 250ms

### Tests for User Story 2 ⚠️ Write first and observe RED

- [X] T015 [P] [US2] Add failing infinite-grace polling and strict client-cancellation tests in `router balancing test/Engine/UsageCaptureTests.cs`, including no overlapping reads, no app timeout and OCE propagation
- [X] T016 [P] [US2] Add failing endpoint keep-alive/client-abort coverage and update the old immediate-zero expectation in `router balancing test/Server/ProxyRetryIntegrationTests.cs` and `router balancing test/Engine/ProxyRequestHandlerTests.cs`; assert both server-side upstream cancellation and endpoint completion, not only client read completion

### Implementation for User Story 2

- [X] T017 [US2] Implement infinite-grace and cancellation behavior in `src/RouterBalancing.Core/Engine/UsageCapture.cs` and `src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs` (depends on T010, T012, T015, T016); pass caller token through every delay/read and never translate caller OCE into timeout
- [X] T018 [US2] Update legacy immediate-EOF fixtures to explicit no-grace or finite-grace options where appropriate in `router balancing test/Engine/UsageCaptureTests.cs` and `router balancing test/Server/ProxyRetryIntegrationTests.cs` (depends on T017), preventing hangs while preserving legacy API coverage
- [X] T019 [US2] Add the explicit 10-minute zero-grace validation and its runnable filter in `router balancing test/Engine/UsageCaptureTests.cs` and `specs/001-upstream-eof-grace/quickstart.md`; keep it out of the default suite and assert keep-alive only at boundary

**Checkpoint**: User Story 2 is independently functional and green

---

## Phase 5: User Story 3 - Resumed Stream (Priority: P2)

**Goal**: New bytes during grace resume normal streaming with exact bytes/parser state; a later EOF gets a fresh full grace

**Independent Test**: Partial event receives temporary EOF, bytes resume after 1.5 seconds, terminal arrives and all content is forwarded/captured exactly once without timeout

### Tests for User Story 3 ⚠️ Write first and observe RED

- [X] T020 [P] [US3] Add failing partial-event resume, fresh-grace-after-resume, repeated peek and no-duplicate aggregation tests in `router balancing test/Engine/UsageCaptureTests.cs` and `router balancing test/Engine/SseDataEventParserTests.cs`; cite `data-model.md → Parser State` and `Grace Session Transitions` 6–7
- [X] T021 [P] [US3] Add failing resumed-byte framing/dataset integration tests in `router balancing test/Server/ProxyRetryIntegrationTests.cs`: pending event completes and flushes once, real terminal completes, no replay and no truncated record

### Implementation for User Story 3

- [X] T022 [US3] Implement resumed-byte transitions and fresh grace handling in `src/RouterBalancing.Core/Engine/UsageCapture.cs`, with parser adjustments limited to `src/RouterBalancing.Core/Engine/SseDataEventParser.cs` only if a RED test proves necessary (depends on T020 and T021); forward original bytes before processing and never feed synthetic delimiters
- [X] T023 [US3] Run resumed-stream, exact-terminal, dataset and event-boundary regressions in `router balancing test/Engine/UsageCaptureTests.cs`, `router balancing test/Engine/SseDataEventParserTests.cs`, `router balancing test/Server/StreamLoopTests.cs` and `router balancing test/Server/ProxyRetryIntegrationTests.cs`

**Checkpoint**: All three user stories are independently functional and green

---

## Phase 6: Polish & Cross-Cutting Concerns

- [X] T024 [P] Update setting/grace semantics and safe diagnostics without payload content in `src/RouterBalancing.Core/Settings/IAppSettingsService.cs`, `src/RouterBalancing.Core/Settings/SettingsKeys.cs`, `src/RouterBalancing.Core/Engine/UsageCapture.cs`, `src/RouterBalancing.Core/Engine/UpstreamTerminalTimeoutException.cs` and `specs/001-upstream-eof-grace/quickstart.md`; retain label `Canh im lặng upstream (giây, 0 = tắt)` unless product copy is separately approved
- [X] T025 Run all automated validation in `specs/001-upstream-eof-grace/quickstart.md`: focused tests, full .NET suite, Windows TFM build and Go `test`/`vet` in `sidecar/opencode-proxy`; record actual counts and any environmental exceptions without hiding failures
- [X] T026 Perform independent OCR Delegate review of the whole change in `src/RouterBalancing.Core` and `router balancing test`, address Critical/Important findings with regression tests and rerun affected validation; update `specs/001-upstream-eof-grace/quickstart.md` with review evidence
- [X] T027 Reconcile implementation against every acceptance criterion and update artifacts in `specs/001-upstream-eof-grace/spec.md`, `specs/001-upstream-eof-grace/plan.md`, `specs/001-upstream-eof-grace/tasks.md` and `specs/001-upstream-eof-grace/quickstart.md`; run `/speckit.converge` for any remaining gap before completion

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (T001)**: Starts immediately.
- **Foundational (T002–T007)**: Depends on T001 and BLOCKS all user stories.
- **US1 (T008–T014)**: MVP; depends on foundational checkpoint.
- **US2 (T015–T019)**: Reuses US1 state machine and timeout plumbing; independently tested after US1.
- **US3 (T020–T023)**: Reuses parser peek, grace state machine and dataset paths; independently tested after US1/US2.
- **Polish (T024–T027)**: Depends on selected story checkpoints and finally all three stories.

### Within Each Story

- Tests MUST be observed failing before production implementation.
- Parser/options/buffering before state machine; state machine before protocol/outcome plumbing; focused integration before story completion.
- Complete and validate one story before moving to the next in the recommended single-agent sequence.

### Parallel Opportunities

- T002, T004 and T006 touch different test files and can run together.
- T003 and T005 can run together after their respective RED tests.
- T008 and T009 can run together; T015 and T016 can run together; T020 and T021 can run together.
- T024 documentation can run while unrelated verification is in progress, but final validation/review waits for all edits.

---

## Parallel Example: Foundational

```text
Task: T002 Add PeekFinish failing tests in SseDataEventParserTests.cs
Task: T004 Add event-boundary failing tests in StreamLoopTests.cs
Task: T006 Confirm legacy no-grace and exact-terminal regressions in UsageCaptureTests.cs
```

## Parallel Example: User Story 1

```text
Task: T008 Add finite-grace failing tests in UsageCaptureTests.cs
Task: T009 Add timeout handler/endpoint failing tests in ProxyRequestHandlerTests.cs and ProxyRetryIntegrationTests.cs
```

---

## Implementation Strategy

### MVP First (User Story 1 Only)

1. Complete T001–T007 and verify parser/buffering/legacy behavior.
2. Complete T008–T014.
3. Stop and demonstrate finite 3-second grace with standalone `timeout_waiting_for_terminal`, no retry and no dataset record.

### Incremental Delivery

1. Foundational checkpoint.
2. US1 finite grace checkpoint (MVP).
3. US2 zero/infinite grace and cancellation checkpoint.
4. US3 resume and dataset/byte-integrity checkpoint.
5. Full verification, independent review and Spec Kit convergence.

### Commit Strategy

Commit after each foundational/story checkpoint with one focused conventional-commit message. Do not include unrelated dirty-worktree changes or production secrets/data.
