# Quickstart Validation: Upstream EOF Grace

## Prerequisites

- .NET 10 SDK và MAUI workload cho Windows build.
- Không cần provider/API key hoặc request thật: dùng scripted resumable stream, không replay production request và không ghi prompt/response content vào log.
- Đọc behavior contract tại [contracts/streaming-grace.md](contracts/streaming-grace.md) và state model tại [data-model.md](data-model.md).

## Focused Automated Validation

Từ repository root:

```powershell
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~SseDataEventParserTests|FullyQualifiedName~UsageCaptureTests|FullyQualifiedName~StreamLoopTests|FullyQualifiedName~ProxyRetryIntegrationTests"
```

Các nhóm kịch bản bắt buộc:

1. **Finite grace:** grace 3 giây không báo lỗi sớm; vẫn EOF sau final read tại deadline → đúng một `timeout_waiting_for_terminal` trong khoảng 3,0–3,5 giây.
2. **Zero/infinite:** không có timeout do app; client cancellation dừng timer/read trong 250ms và không có byte/lỗi tiếp theo.
3. **Resume:** partial event nhận EOF, byte mới sau 1,5 giây rồi terminal hợp lệ → byte được ghép đúng một lần, không duplicate parser/dataset event.
4. **Deadline rules:** EOF lặp lại không reset deadline hoặc tạo read song song; EOF sau resume bắt đầu grace mới; grace ngắn hơn 1 giây vẫn có final read tại deadline.
5. **Legacy boundaries:** pre-content EOF vẫn failover, terminal tail hợp lệ hoàn tất ngay, transport exception giữ nguyên, Responses/non-stream/JSON behavior không đổi.
6. **Framing:** keep-alive chỉ ở boundary; timeout bỏ pending fragment trước error; `upstreamBytes` và `contentBytes` đúng; không có `[DONE]` giả.
7. **Dataset:** grace timeout/client abort giữ completion incomplete và không ghi record; resume có terminal thật mới được phép capture đầy đủ.
8. **Terminal regressions:** giữ các case exact `[DONE]`, split across network chunks, multiline `[DO\nNE]`, second-choice finish và complete tail không blank line.

## Long-Running Zero Validation

Chạy kịch bản explicit với grace vô hạn ở event boundary, giữ stream ít nhất 10 phút:

- Không có timeout do app.
- Keep-alive tiếp tục mỗi 5 giây ±1 giây.
- Không có read song song và không quay vòng CPU.
- Hủy client ở cuối; toàn bộ wait/read dừng trong 250ms.

Test này là explicit long test, không nằm trong suite mặc định. Command filter chính xác sẽ trỏ tới test `TeeSse_ZeroGrace_TenMinutes_RemainsActiveUntilClientCancellation` sau khi task tạo fixture.

```powershell
dotnet test "router balancing test/router balancing test.csproj" -- --filter-method "*TeeSse_ZeroGrace_TenMinutes_RemainsActiveUntilClientCancellation"
```

## Full Verification

```powershell
dotnet test "router balancing test/router balancing test.csproj"
dotnet build "router-balancing/router-balancing.csproj" -f net10.0-windows10.0.19041.0 --no-restore
```

Trong `sidecar/opencode-proxy`:

```powershell
go test ./...
go vet ./...
```

Kết quả mong đợi: toàn bộ test pass, Windows build 0 warnings/errors, Go test/vet pass. Chạy independent review theo `AGENTS.md` trước khi merge. Không khẳng định rằng grace có thể làm HTTP stream chuẩn sống lại sau EOF; nó chỉ điều khiển thờigian chờ phía client và hỗ trợ nguồn non-standard có thể resume.
