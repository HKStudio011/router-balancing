# Plan: Fix update fail — sidecar opencode-proxy giữ lock file (v1.5.2)

## Context

- **Bug:** Update app qua auto-updater fail với `"Timed out waiting for application files to be released — update aborted, application folder untouched."` — lặp lại nhiều lần (15/10: v1.4.0/v1.4.1; 16/10: ~10 lần với v1.5.1).
- **Root cause (đã xác minh bằng chứng sống):** `OpenCodeSidecarService` spawn `opencode-proxy.exe` (Go sidecar "go slider" cho OpenCode free tier) làm process con. Khi app thoát bằng `Application.Current.Quit()` (luồng update), `Dispose()` của service không được gọi → sidecar mồ côi sống tiếp (Go binary không monitor parent), giữ lock exclusive trên chính file exe của mình trong `<appDir>\runtimes\win-x64\native\opencode-proxy.exe`. Zip release chứa file này → updater `WaitUntilWritable` mở exclusive fail mỗi pass → timeout 60s → abort.
- **Bằng chứng:** 2 process opencode-proxy.exe (PID 47212, 31508, spawn 00:08 SA từ app PID 48124 đã chết từ lâu) vẫn lock file tại `D:\Program Files\Router Balancing` — kill chúng → file unlock ngay.
- **Spec liên quan:** `docs/superpowers/specs/2026-10-14-opencode-sidecar-design.md`; update flow: spec §6-§11 (`2026-10-11-version-update-design.md`).
- **Constraint:** appDir production = `D:\Program Files\Router Balancing` (v1.4.1, 1193 files, self-contained); zip release self-contained bắt buộc (`VerifySelfContained`); `dotnet build router-balancing.slnx` fail android TFM (dùng build TFM windows); suite có 3 baseline fail môi trường.

## Global Constraints

- Build app: `dotnet build "router-balancing/router-balancing.csproj" -f net10.0-windows10.0.19041.0 --nologo -v q` → 0 warn/0 err.
- Test: `dotnet test "router balancing test/router balancing test.csproj" --nologo` — baseline fail đã biết: `SingleInstanceGuardTests` x2 (app thật đang giữ lock), `Cancel_QueuedRequest_OriginReceives400WithMatchedRequestId`, `Stream_LargeUpstreamBody_CompletesWithoutDeadlock`, `ProxyOutboundHttpStubTests.SendAsync_AllProxiesDead_AttemptsDirect` (flaky).
- Master protected: branch → PR → `gh pr merge --merge --delete-branch`.
- AGENTS.md: XML doc + comment why tiếng Việt; conventional commit tiếng Anh; TDD red-green.

## Tasks

### Task 1 — Unblock ngay (không commit): kill orphan opencode-proxy.exe trên máy dev
- Kill các process con mồ côi giữ lock `<appDir>\runtimes\win-x64\native\opencode-proxy.exe`.
- Verify: file mở exclusive được.

### Task 2 — App side: sidecar vào Windows Job Object (KILL_ON_JOB_CLOSE)
- TDD: `WindowsProcessJob` helper (P/Invoke CreateJobObject/SetInformationJobObject(KILL_ON_JOB_CLOSE)/AssignProcessToJobObject/CloseHandle) — test: gán process dummy vào job, dispose job → process chết.
- `OpenCodeSidecarService`: tạo job khi start sidecar (Windows-only), gán process vào job. Job handle sống suốt app process → OS tự kill sidecar khi app thoát bất kỳ cách nào (Quit/crash/Environment.Exit) — không dựa vào managed Dispose.
- Non-Windows: bỏ qua (sidecar Windows-only theo design).

### Task 3 — Updater side: dọn process mồ côi trong appDir trước khi ghi đè
- Sau khi app cha thoát (trước WaitUntilWritable): kill mọi process có `MainModule.FileName` nằm dưới appDir (opencode-proxy mồ côi của bản cũ + sidecar tương lai). Skip chính updater. Bắt AccessDenied/Win32Exception từng process, continue.
- Hàm match path tách pure (`IsProcessUnderDirectory`) — test: đúng dưới appDir, prefix-collision (`C:\App Files` vs `C:\App`), path null/khác ổ.
- Integration test: spawn process từ temp dir → helper kill → verify exited.
- Updater là console project net10 — nếu không có test project riêng, thêm test vào test project với InternalsVisibleTo hoặc tách helper static public internal + test qua project reference (kiểm tra cấu hình hiện tại của Updater csproj).

### Task 4 — WaitUntilWritable diagnostic: liệt kê hết file locked + locker
- Không break ở file đầu — mỗi pass thu thập toàn bộ file không mở exclusive được.
- Khi timeout: in console + ghi append `%TEMP%\RouterBalancing_Update\update.log`: danh sách locked file (cap 20) + process giữ lock (Restart Manager API RmStartSession/RmRegisterResources/RmGetList; fallback: scan process MainModule khớp locked exe).
- Test: WaitUntilWritable trên fixture có file bị lock bởi process test → trả về danh sách đúng.

### Task 5 — Verify toàn cục
- Build windows TFM 0/0; chạy full suite; fix regression nếu có (TDD).

### Task 6 — Release v1.5.2
- Bump csproj: Version 1.5.2.0, ApplicationDisplayVersion 1.5.2, ApplicationVersion 14.
- Branch `fix/update-sidecar-lock` → PR → merge → tag v1.5.2 → `gh release create v1.5.2` (notes: Fixes — sidecar job object + updater orphan cleanup + diagnostics; hướng dẫn user update thẳng từ bản cũ).
- Kiểm tra workflow release tạo zip+sig assets thế nào (`.github/workflows`) và làm đúng pipeline.

## Commit Plan

1. `fix: kill opencode sidecar via job object on app exit` (Task 2)
2. `fix: updater kills orphan processes under app dir before overwrite` (Task 3)
3. `feat: updater reports locked files and locking processes on timeout` (Task 4)
4. `chore: bump version to 1.5.2` (Task 6)
5. `docs: add sidecar update-lock fix plan` (plan file)

## Review Focus (Final Review)

- Job object: handle lifetime (không leak), nested job compatibility, service restart sau khi sidecar crash (job còn dùng lại được? — Assign lại process mới vào cùng job OK).
- Updater kill: có kill lầm process khác user không (chỉ kill exe nằm TRONG appDir; so sánh có trailing separator); updater tự skip (shadow copy ở %TEMP% — khác appDir, nhưng vẫn explicit skip CurrentProcess).
- Quyền: kill process cùng user OK không cần admin; AccessDenied bị nuốt có chủ đích (comment why).
- Diagnostic: không ghi nội dung secret; log file trong %TEMP% vô hại.
