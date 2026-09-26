# router-balancing

**router-balancing** là ứng dụng desktop (.NET MAUI Blazor Hybrid, Windows-first) đóng vai **local proxy server** cân bằng tải request LLM: expose API chuẩn OpenAI tại `http://127.0.0.1:<port>` (mặc định **8317**), tự động chọn nhà cung cấp, kèm UI quản lý nhật ký và cài đặt.

## Yêu cầu

- .NET 10 SDK + MAUI workload (`dotnet workload install maui`)
- Node.js 20+ — **chỉ cần khi sửa frontend** (`router-balancing/vite-project`); bản build đã commit sẵn trong `router-balancing/wwwroot/build/`

## Build & chạy

```powershell
# build app (Windows)
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo

# chạy
dotnet run --project router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
```

## Test

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

## Frontend (chỉ khi sửa UI)

```powershell
# workdir: router-balancing/vite-project
npm install
npm run build      # hoặc npm run watch khi dev
```

## Cấu hình & dữ liệu

| Hạng mục | Giá trị |
|---|---|
| Port mặc định | `8317` (range 1024–65535, đổi trong Settings) |
| Health check | `GET /health` — không cần API key |
| API key | Tùy chọn; mã hóa DPAPI (CurrentUser), so sánh constant-time |
| DB | `%AppData%\router-balancing\router-balancing.db` (SQLite, auto-migration khi khởi động) |
| Nhật ký | Giữ 90 ngày mặc định; dọn tự động 24h/lần + nút "Dọn log cũ ngay" trong Settings |
| Ngôn ngữ / theme | `auto` / `system` theo hệ thống, đổi được trong Settings |

## Tài liệu

- Spec: [`docs/superpowers/specs/2026-09-25-router-balancing-design.md`](docs/superpowers/specs/2026-09-25-router-balancing-design.md)
- Plan Phase 1: [`docs/superpowers/plans/2026-09-25-phase1-foundation.md`](docs/superpowers/plans/2026-09-25-phase1-foundation.md)
