<p align="right"><strong>English</strong> | <a href="README.vn.md">Tiếng Việt</a></p>

<p align="center">
  <img src="docs/design/logo/logo-badge.svg" width="128" height="128" alt="Router Balancing logo" />
</p>

<h1 align="center">Router Balancing</h1>

<p align="center">
  <a href="https://github.com/HKStudio011/router-balancing/actions/workflows/ci.yml"><img src="https://github.com/HKStudio011/router-balancing/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <a href="https://github.com/HKStudio011/router-balancing/releases/latest"><img src="https://img.shields.io/github/v/release/HKStudio011/router-balancing" alt="Release" /></a>
  <a href="LICENSE.txt"><img src="https://img.shields.io/badge/license-Apache%202.0-blue.svg" alt="License Apache 2.0" /></a>
  <img src="https://img.shields.io/badge/.NET-10-purple.svg" alt=".NET 10" />
</p>

**Router Balancing** is a Windows-first desktop app (.NET MAUI Blazor Hybrid) that acts as a **local proxy server** for load-balancing LLM requests: it exposes a standard OpenAI-compatible API at `http://127.0.0.1:<port>` (default **8317**), automatically selects a provider, and ships with a UI for logs and settings.

## Features

- OpenAI-compatible API (`/v1/chat/completions`, `/v1/models`) — point any client at the local proxy
- Automatic provider selection with health checks and failover
- Request logs UI with cleanup (90-day retention by default)
- Settings UI: port, providers, API keys (DPAPI-encrypted), language, theme
- Auto-update via GitHub Releases
- Multi-language UI (English / Vietnamese), system light/dark theme

## Requirements

- .NET 10 SDK + MAUI workload (`dotnet workload install maui`)
- Node.js 20+ — **only needed when editing the frontend** (`router-balancing/vite-project`); the built output is committed in `router-balancing/wwwroot/build/`

## Build & run

```powershell
# build app (Windows)
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo

# run
dotnet run --project router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
```

## Test

```powershell
dotnet test "router balancing test/router balancing test.csproj" --nologo
```

## Frontend (only when editing the UI)

```powershell
# workdir: router-balancing/vite-project
npm install
npm run build      # or npm run watch during development
```

## Configuration & data

| Item | Value |
|---|---|
| Default port | `8317` (range 1024–65535, change in Settings) |
| Health check | `GET /health` — no API key required |
| API key | Optional; DPAPI-encrypted (CurrentUser), constant-time comparison |
| Database | `%AppData%\router-balancing\router-balancing.db` (SQLite, auto-migration on startup) |
| Logs | Kept 90 days by default; auto-cleaned every 24h + "Clean old logs now" button in Settings |
| Language / theme | `auto` / `system` follows the OS, changeable in Settings |

## Documentation

- Design spec: [`docs/superpowers/specs/2026-09-25-router-balancing-design.md`](docs/superpowers/specs/2026-09-25-router-balancing-design.md)
- Phase 1 plan: [`docs/superpowers/plans/2026-09-25-phase1-foundation.md`](docs/superpowers/plans/2026-09-25-phase1-foundation.md)

## Links

- HKStudio011: [https://hkstudio011.github.io/](https://hkstudio011.github.io/)
