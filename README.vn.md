<p align="right"><a href="README.md">English</a> | <strong>Tiếng Việt</strong></p>

<p align="center">
  <img src="docs/design/logo/logo-badge.svg" width="128" height="128" alt="Logo Router Balancing" />
</p>

<h1 align="center">Router Balancing</h1>

<p align="center">
  <a href="https://github.com/HKStudio011/router-balancing/actions/workflows/ci.yml"><img src="https://github.com/HKStudio011/router-balancing/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <a href="https://github.com/HKStudio011/router-balancing/releases/latest"><img src="https://img.shields.io/github/v/release/HKStudio011/router-balancing" alt="Release" /></a>
  <a href="LICENSE.txt"><img src="https://img.shields.io/badge/license-Apache%202.0-blue.svg" alt="License Apache 2.0" /></a>
  <img src="https://img.shields.io/badge/.NET-10-purple.svg" alt=".NET 10" />
</p>

**Router Balancing** là ứng dụng desktop (.NET MAUI Blazor Hybrid, Windows-first) đóng vai **local proxy server** cân bằng tải request LLM: expose API chuẩn OpenAI tại `http://127.0.0.1:<port>` (mặc định **8317**), tự động chọn nhà cung cấp, kèm UI quản lý nhật ký và cài đặt.

## Tính năng

- API tương thích OpenAI (`/v1/chat/completions`, `/v1/models`) — trỏ bất kỳ client nào về proxy local
- Tự động chọn provider kèm health check và failover
- UI nhật ký request, dọn tự động (giữ 90 ngày mặc định)
- UI cài đặt: port, provider, API key (mã hóa DPAPI), ngôn ngữ, theme
- Tự cập nhật qua GitHub Releases
- UI đa ngôn ngữ (Anh / Việt), theme sáng/tối theo hệ thống

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
dotnet test "router balancing test/router balancing test.csproj" --no-banner
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

## Liên kết

- HKStudio011: [https://hkstudio011.github.io/](https://hkstudio011.github.io/)
