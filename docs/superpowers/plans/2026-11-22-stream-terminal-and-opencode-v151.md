# Stream Terminal and OpenCode Free v1.5.1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Báo đúng lỗi khi Chat SSE bị cắt, làm synthetic title tương thích strict OpenCode client và backport fix NBSP của opencode2api v1.5.1 mà không làm hỏng dataset hoặc tool shaping.

**Architecture:** Giữ `UsageCapture` làm tee/observer và chuyển `EndedWithoutTerminal` thành lỗi tại `ProxyRequestHandler`, tái sử dụng đường `Aborted` → in-band `upstream_disconnected` hiện có. Go sidecar phát terminal chunk riêng cho synthetic title và chỉ đổi ASCII space trong mô tả top-level của client tool được rename, trước khi append official tools.

**Tech Stack:** .NET 10/C#, ASP.NET Core, xUnit v3, Go `net/http`, Go standard testing.

**Spec:** `docs/superpowers/specs/2026-11-22-stream-terminal-and-opencode-v151-design.md`

## Global Constraints

- Giữ `UsageCapture.TeeResult` và public positional shape hiện tại; không thay đổi DB, UI hoặc format dataset.
- Không tổng hợp `finish_reason:"stop"` cho stream bị cắt; dataset vẫn loại completion cụt.
- Đã forward byte cho client thì không retry; EOF trước byte đầu vẫn retry/failover theo marker hiện tại.
- Không thêm error + `[DONE]` trong Go sidecar để tránh host đánh dấu thành công hoặc duplicate error.
- NBSP chỉ áp dụng cho Chat `tools[].function.description` của client tools; không đổi schema, mô tả tham số, official tools, newline/tab hay NBSP có sẵn.
- Comment/tài liệu tiếng Việt; identifier và commit message tiếng Anh; không commit secret hoặc dữ liệu request từ `E:\Data`.

## Review Focus

- EOF trước content phải giữ retry/failover — Task 1 chạy lại toàn bộ `ProxyRetryIntegrationTests`.
- EOF sau content chỉ thêm một `upstream_disconnected`, không `[DONE]`, không finish reason giả — Task 1 assert chính xác body.
- Title từ cả 429 lẫn transport error phải dùng cùng terminal sequence và giữ metadata chunk — Task 2 kiểm cả hai test.
- Shaping lặp hoặc body đã append official tools không được làm official descriptions mang NBSP — Task 3 gọi `RewriteBody`/`AppendOfficial` hai lần và so official bytes/descriptions.
- Tool có description chứa newline/tab/NBSP, schema và parameter descriptions phải giữ nguyên — Task 3 assert từng field.

---

### Task 1: Biến clean EOF sau content thành lỗi upstream thật

**Files:**
- Modify: `router balancing test/Server/ProxyRetryIntegrationTests.cs:619-645`
- Modify: `src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs:215-226,289-307`
- Modify: `src/RouterBalancing.Core/Engine/UsageCapture.cs:188-206`
- Modify: `src/RouterBalancing.Core/Engine/UpstreamTruncatedException.cs:3-18`
- Modify: `router balancing test/Engine/UsageCaptureTests.cs:367-384` (chỉ làm rõ contract tee/comment nếu cần)

**Interfaces:**
- Consumes: `UsageCapture.TeeResult.EndedWithoutTerminal`, `UpstreamTruncatedException : IOException`, đường `DispatchOutcome.Aborted` và `SseErrorEvent.UpstreamLostMidStream()` hiện có.
- Produces: `ProxyRequestHandler.ForwardAsync` ném `UpstreamTruncatedException` khi tee trả `EndedWithoutTerminal=true`; không có signature public mới.

- [x] **Step 1: Đổi integration test hiện có thành failing regression test**

Đổi tên `Stream_WhenUpstreamEndsCleanlyMidStream_NoInjectedErrorAndDoneState` thành `Stream_WhenUpstreamEndsCleanlyMidStream_EmitsInBandUpstreamLostEvent`.

Giữ fixture `SseCleanEof()` và các timeout hiện tại. Expected behavior mới:

```csharp
Assert.StartsWith("data: {\"x\":1}\n\n", body);
Assert.Contains("\"code\":\"upstream_disconnected\"", body);
Assert.Contains("\"type\":\"error\"", body);
Assert.DoesNotContain("[DONE]", body);
Assert.DoesNotContain("finish_reason", body);
Assert.Equal(ApiCallState.Cancelled, record!.State);
Assert.Equal("network", record.FailureKind);
Assert.Equal(1, upstream.Calls);
Assert.Contains(_messages, message => message.Contains("Stream chết sau 15 byte content")
    && message.Contains("upstream_disconnected") && message.Contains(id));
```

- [x] **Step 2: Chạy test và xác nhận fail vì hành vi cũ**

Run:

```powershell
dotnet test "router balancing test/router balancing test.csproj" --no-restore --filter "FullyQualifiedName~ProxyRetryIntegrationTests.Stream_WhenUpstreamEndsCleanlyMidStream_EmitsInBandUpstreamLostEvent"
```

Expected: FAIL — body chỉ có `data: {"x":1}`, không có `upstream_disconnected`, monitor vẫn `Done`.

- [x] **Step 3: Chuyển cờ EOF thiếu terminal thành exception tại handler**

Trong `ProxyRequestHandler.ForwardAsync`, đặt kiểm tra ngay sau `AggregateSseAsync`/`TeeAsync` và vẫn trong `try` hiện tại:

```csharp
if (tee.EndedWithoutTerminal)
    throw new UpstreamTruncatedException();
```

Xóa nhánh sau tee hiện chỉ log `đóng SSE ... coi như kết thúc stream`; catch tổng quát hiện có sẽ hủy upstream, ghi monitor/log và rethrow. Không thêm catch mới và không retry sau content.

Cập nhật comment tại `UsageCapture.cs` và XML doc `UpstreamTruncatedException.cs`: tee chỉ báo cờ; handler chuyển cờ thành lỗi mid-content để endpoint phát `upstream_disconnected`. Giữ nguyên `TeeSseAsync`, `TeeResult`, dataset và nhánh pre-content.

- [x] **Step 4: Chạy regression test và các test liên quan**

Run:

```powershell
dotnet test "router balancing test/router balancing test.csproj" --no-restore --filter "FullyQualifiedName~ProxyRetryIntegrationTests|FullyQualifiedName~UsageCaptureTests"
```

Expected: tất cả PASS, gồm clean EOF mới, mid-stream IOException cũ, pre-content retry và các stream có `[DONE]`/`finish_reason`.

- [x] **Step 5: Commit Task 1**

```powershell
git add "src/RouterBalancing.Core/Engine/ProxyRequestHandler.cs" "src/RouterBalancing.Core/Engine/UsageCapture.cs" "src/RouterBalancing.Core/Engine/UpstreamTruncatedException.cs" "router balancing test/Server/ProxyRetryIntegrationTests.cs" "router balancing test/Engine/UsageCaptureTests.cs"
git commit -m "fix: report truncated chat streams to clients"
```

### Task 2: Thêm terminal chunk cho synthetic title

**Files:**
- Modify: `sidecar/opencode-proxy/main.go:364-399`
- Modify: `sidecar/opencode-proxy/pipeline_test.go:450-525,568-599`

**Interfaces:**
- Consumes: `writeSynthesizedTitle(http.ResponseWriter, map[string]any)` và fixture `titleRequestBody`.
- Produces: SSE title gồm content chunk (`finish_reason:nil`) → empty-delta chunk (`finish_reason:"stop"`) → `data: [DONE]`; hai chunk dùng cùng `id`, `object`, `created`, `model`.

- [x] **Step 1: Thêm assertion terminal sequence cho cả hai fallback test**

Tạo test helper `requireSynthesizedTitleTerminal(t *testing.T, body []byte)` trong `pipeline_test.go`: parse mọi dòng `data:` khác `[DONE]`, yêu cầu đúng hai JSON chunks; chunk 0 giữ content title và `finish_reason=nil`, chunk 1 có `delta:{}`, `finish_reason:"stop"`; metadata của hai chunk khớp nhau và `[DONE]` đứng sau chúng.

Gọi helper từ cả `TestPipeline_TitleRequest_Upstream429_Synthesized200` và `TestPipeline_TitleRequest_TransportError_Synthesized200`.

- [x] **Step 2: Chạy hai test và xác nhận fail**

Run trong `sidecar/opencode-proxy`:

```powershell
go test . -run 'TestPipeline_TitleRequest_(Upstream429|TransportError)_Synthesized200' -count=1
```

Expected: FAIL — hiện chỉ có một JSON chunk và không có non-null `finish_reason`.

- [x] **Step 3: Dựng hai chunks bằng cùng encoder trong `writeSynthesizedTitle`**

Dùng một closure encoder nhận `(delta map[string]any, finishReason any)` để giữ cùng metadata và cấu trúc choice. Encode content chunk trước, terminal chunk (`map[string]any{}`, `"stop"`) sau; giữ error handling marshal, headers, status và flush hiện tại. Ghi `[DONE]` sau hai chunks rồi `gofmt` hai file đã sửa.

- [x] **Step 4: Chạy title tests và toàn bộ Go tests**

Run trong `sidecar/opencode-proxy`:

```powershell
go test . -run 'TestPipeline_TitleRequest_(Upstream429|TransportError)_Synthesized200' -count=1
go test ./...
```

Expected: tất cả PASS.

- [x] **Step 5: Commit Task 2**

```powershell
git add sidecar/opencode-proxy/main.go sidecar/opencode-proxy/pipeline_test.go
git commit -m "fix: finish synthesized title streams with stop"
```

### Task 3: Backport NBSP shaping từ opencode2api v1.5.1

**Files:**
- Modify: `sidecar/opencode-proxy/toolshape.go:206-228`
- Modify: `sidecar/opencode-proxy/toolshape_test.go`

**Interfaces:**
- Consumes: `ToolRenames.RewriteBody(map[string]any)` và `ToolRenames.AppendOfficial(map[string]any)`.
- Produces: chỉ description của client tool được rename có ASCII spaces thành NBSP; không có signature mới hoặc dependency mới.

- [x] **Step 1: Viết `TestRewriteBody_NBSPOnlyClientTopLevelDescription`**

Fixture gồm một client function tool có:

- `description = "run commands\tfast\nnext\u00a0line"`;
- parameter schema và parameter description chứa ASCII spaces;
- `RewriteBody` rồi `AppendOfficial`, lặp chuỗi này hai lần để kiểm idempotence và official tools không bị sửa.

Expected:

```text
client description = "run\u00a0commands\tfast\nnext\u00a0line"
parameter/schema descriptions = nguyên byte
client name restore mapping = không đổi
official entries = cùng JSON với asset trước shaping
```

Cũng chạy lại `TestRewriteBody_RenamesToolsAndAllReferences` để bảo đảm reference rewriting không đổi.

- [x] **Step 2: Chạy test và xác nhận fail**

Run trong `sidecar/opencode-proxy`:

```powershell
go test . -run 'TestRewriteBody_NBSPOnlyClientTopLevelDescription' -count=1
```

Expected: FAIL — client top-level description vẫn chứa ASCII spaces.

- [x] **Step 3: Áp dụng NBSP chỉ trong nhánh rename client tool**

Trong vòng lặp `tools` của `RewriteBody`, bên trong nhánh tìm thấy `r.toWire[name]` (client được khai báo), nếu `fn["description"]` là string thì gán:

```go
fn["description"] = strings.ReplaceAll(description, " ", "\u00a0")
```

Không đệ quy schema, không đụng `messages`, không chạy trên official tools hoặc entry không thuộc `r.toWire`. `strings` đã được file sử dụng nên không thêm dependency. Sau sửa chạy `gofmt`.

- [x] **Step 4: Chạy shaping tests, toàn bộ Go tests và vet**

Run trong `sidecar/opencode-proxy`:

```powershell
go test . -run 'TestRewriteBody_|TestAppendOfficial_' -count=1
go test ./...
go vet ./...
```

Expected: tất cả PASS; `go vet` không có finding.

- [x] **Step 5: Commit Task 3**

```powershell
git add sidecar/opencode-proxy/toolshape.go sidecar/opencode-proxy/toolshape_test.go
git commit -m "fix: shape client tool descriptions with NBSP"
```

### Task 4: Xác minh toàn bộ và lưu tài liệu

**Files:**
- Modify: `docs/superpowers/specs/2026-11-22-stream-terminal-and-opencode-v151-design.md` (cập nhật trạng thái/kết quả thực tế nếu cần)
- Modify: `docs/superpowers/plans/2026-11-22-stream-terminal-and-opencode-v151.md` (đánh dấu các bước đã hoàn tất)

- [x] **Step 1: Chạy đầy đủ .NET verification**

Run từ repo root:

```powershell
dotnet test "router balancing test/router balancing test.csproj"
dotnet build router-balancing.slnx
```

Expected: toàn bộ tests PASS; build thành công. Chỉ ghi nhận warning đã tồn tại trước; mọi lỗi mới phải được điều tra, không bỏ qua.

Kết quả: 1582/1582 tests PASS. Full solution build vẫn fail với bốn `NETSDK1082` đã có từ trước cho runtime pack `Microsoft.AspNetCore.App` của các RID mobile (`maccatalyst-x64`, `iossimulator-x64`, `android-x64`, `android-arm64`); ba commit implementation không đổi project/solution hay cấu hình build. Build app Windows bổ sung bằng `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` PASS, 0 warning/0 error. Các warning analyzer và CS1570 đều thuộc baseline đã tồn tại trước.

- [x] **Step 2: Chạy lại đầy đủ Go verification**

Run trong `sidecar/opencode-proxy`:

```powershell
go test ./...
go vet ./...
```

Expected: PASS và vet sạch.

Kết quả: `go test ./...` PASS; `go vet ./...` exit 0, không output/finding. Không phát sinh lỗi cleanup temp trên Windows trong lần xác minh này.

- [x] **Step 3: Rà diff, cập nhật tài liệu và commit docs**

Kiểm `git diff --check`, `git status --short` và diff của ba commit trước. Xác nhận không có file dataset/log/secret, không có thay đổi ngoài phạm vi. Cập nhật trạng thái tài liệu rồi commit:

Kết quả rà diff: ba commit implementation chỉ đổi đúng chín file production/test trong phạm vi Tasks 1–3; không có file dataset, log, secret hoặc thay đổi ngoài phạm vi. `git diff --check` không báo lỗi whitespace.

```powershell
git add docs/superpowers/specs/2026-11-22-stream-terminal-and-opencode-v151-design.md docs/superpowers/plans/2026-11-22-stream-terminal-and-opencode-v151.md
git commit -m "docs: record stream terminal compatibility fixes"
```
