# OpenCode Free Tier Go Sidecar — Design Spec

**Date:** 2026-10-14  
**Status:** Approved  
**Problem:** OpenCode free tier gate (`opencode.ai/zen/v1/chat/completions`) returns 403 FreeTierError for all non-OpenCode clients. The gate checks TLS fingerprint (JA3/JA4) at the Cloudflare edge. Only Electron/BoringSSL (OpenCode desktop) and Go crypto/tls fingerprints pass. .NET SocketsHttpHandler (Schannel), curl (Schannel), and Node.js (OpenSSL) all fail — even with correct headers, body, and session IDs.

## Goal

Make router-balancing's OpenCode Free provider work by routing requests through a local Go sidecar binary that uses Go's crypto/tls fingerprint (proven to pass the gate by jasonxu's opencode2api).

## Constraints

- Must work with the app's existing proxy infrastructure (SOCKS5/HTTP proxies from DB)
- Must support cross-platform builds (Windows, Android, iOS, MacCatalyst)
- Must not break existing failover logic (`ProxyRequestHandler.ClassifyFatal`)
- Go binary must be small (<10MB) and self-contained
- Proxy credentials must not be hardcoded in the Go binary

## Architecture

```
┌─────────────────────────────────────────────────────────────┐
│ .NET MAUI App                                               │
│                                                             │
│  ┌──────────────┐    ┌─────────────────┐    ┌────────────┐ │
│  │ DispatcherLoop│───>│ ProxySelection   │───>│ Sidecar    │ │
│  │              │    │ Resolver         │    │ Service    │ │
│  └──────────────┘    └─────────────────┘    └─────┬──────┘ │
│                                                    │        │
└────────────────────────────────────────────────────┼────────┘
                                                     │
                                                     │ HTTP (localhost)
                                                     ▼
                                              ┌─────────────┐
                                              │ Go Sidecar   │
                                              │ (opencode-   │
                                              │  proxy)      │
                                              └──────┬──────┘
                                                     │
                                                     │ SOCKS5/HTTP proxy
                                                     │ (Go crypto/tls)
                                                     ▼
                                              ┌─────────────┐
                                              │ opencode.ai  │
                                              │ (Cloudflare) │
                                              └─────────────┘
```

## Components

### 1. Go Sidecar Binary (`opencode-proxy`)

**Location:** `sidecar/opencode-proxy/`  
**Language:** Go 1.25+  
**Dependencies:** `golang.org/x/net/proxy` (SOCKS5 support)

**Responsibilities:**
- Listen on local HTTP port (random, reported via stdout)
- Accept POST requests to `/v1/chat/completions`
- Forward to `https://opencode.ai/zen/v1/chat/completions` with:
  - Go crypto/tls fingerprint (default)
  - Proper headers (session ID, quartet tools, stream, etc.)
  - Proxy from `X-Proxy-Url` header (SOCKS5 or HTTP)
- Stream SSE response back to .NET

**API:**
```
POST /v1/chat/completions
Headers:
  X-Proxy-Url: socks5://user:pass@host:port  (optional)
  X-Opencode-Session: ses_<timestamp><random>
  X-Opencode-Project: prj_<hex>
  X-Opencode-Request: req_<hex>
  (other OpenCode headers pass through)
Body: (JSON, same as opencode.ai expects)

Response: SSE stream (passthrough)
```

**Port Discovery:**
- Sidecar prints `LISTENING <port>` to stdout on startup
- .NET reads this line to discover the port

**Health Check:**
```
GET /health
Response: 200 OK (empty body)
```
- .NET calls this every 30s to verify sidecar is alive
- Non-200 or timeout → trigger restart

### 2. .NET Sidecar Service (`OpenCodeSidecarService`)

**Location:** `src/RouterBalancing.Core/Providers/OpenCodeSidecarService.cs`

**Responsibilities:**
- Locate platform-specific Go binary
- Start/stop sidecar process
- Health check (HTTP GET /health)
- Restart with exponential backoff on crash
- Provide `HttpClient` that routes through sidecar

**Lifecycle:**
1. App start → find binary → start process → read port from stdout
2. Health check every 30s
3. Crash → restart with backoff (1s, 2s, 4s, ... max 60s)
4. App exit → kill process tree

**Binary Location (per platform):**
- Windows: `runtimes/win-x64/native/opencode-proxy.exe` (copied to output dir)
- Android: bundled in APK as `AndroidAsset`, extracted to `FileSystem.AppDataDirectory/opencode-proxy` on first run, `chmod +x`
- iOS/MacCatalyst: bundled in app bundle as `BundleResource`, accessible via `NSBundle.MainBundle.PathForResource`

**Binary Discovery Logic:**
1. Check if binary exists in expected location
2. If Android and not extracted → extract from assets, set executable permission
3. If missing → log error, disable OpenCode Free provider (return `null` from `GetClient`)

### 3. Integration with Existing Flow

**Modified:** `OpenAiUpstreamClient.PostAsync`

```csharp
// Current: direct HTTP via SocketsHttpHandler
// New: if provider is OpenCode Free, route through sidecar

if (provider.Id == "opencode" && model.EndsWith("-free", StringComparison.OrdinalIgnoreCase))
{
    var proxy = await _proxyResolver.ResolveAsync(provider, account, ct);
    var sidecarClient = _sidecarService.GetClient(proxy);
    return await sidecarClient.PostAsync(...);
}
// else: existing path
```

**Free Tier Detection:** Model ID ends with `-free` (e.g., `mimo-v2.6-flash-free`, `space-bunny-free`). This matches the existing `Models` table where free models have the `-free` suffix.

**Proxy Resolution:**
- .NET resolves proxy from DB via `ProxySelectionResolver`
- Passes proxy URL + credentials via `X-Proxy-Url` header
- Go sidecar uses `golang.org/x/net/proxy` to dial through proxy

## Data Flow

1. User request → `DispatcherLoop` → `ProxyRequestHandler`
2. `ProxyRequestHandler` → `OpenAiUpstreamClient.PostAsync`
3. If OpenCode Free:
   - Resolve proxy from DB
   - Get sidecar `HttpClient`
   - POST to `http://127.0.0.1:{port}/v1/chat/completions`
   - Include proxy URL in `X-Proxy-Url` header
4. Go sidecar:
   - Parse proxy URL
   - Create SOCKS5/HTTP dialer
   - Dial `opencode.ai:443` through proxy
   - TLS handshake with Go crypto/tls (passes gate)
   - Send request with proper headers
   - Stream response back
5. .NET receives SSE stream → returns to caller

## Error Handling

| Scenario | Handling |
|----------|----------|
| Sidecar not running | Auto-start on first request |
| Sidecar crash | Restart with exponential backoff |
| Port conflict | Sidecar picks random port, reports via stdout |
| Proxy connection failure | Go returns 502, .NET fails over to next account/provider |
| opencode.ai returns 403 | Go passes through, .NET `ClassifyFatal` handles failover |
| Sidecar binary missing | Log error, disable OpenCode Free provider |

## Cross-Platform Builds

**Build Script:** `sidecar/build.ps1`

```powershell
# Windows
GOOS=windows GOARCH=amd64 go build -o runtimes/win-x64/native/opencode-proxy.exe

# Android (arm64)
GOOS=android GOARCH=arm64 go build -o runtimes/android-arm64/native/opencode-proxy

# iOS
GOOS=ios GOARCH=arm64 go build -o runtimes/ios-arm64/native/opencode-proxy

# MacCatalyst
GOOS=darwin GOARCH=arm64 go build -o runtimes/osx-arm64/native/opencode-proxy
```

**MAUI Integration:**
- Add `runtimes/` folder to MAUI project as `AndroidAsset` (Android) or `BundleResource` (iOS/Mac)
- For Windows: copy to output directory

## Testing

1. **Unit Tests:**
   - `OpenCodeSidecarServiceTests` — process lifecycle, port discovery, health check
   - `SidecarProxyResolutionTests` — proxy URL formatting, credential handling

2. **Integration Tests:**
   - Start sidecar, send request, verify 200 OK
   - Test with SOCKS5 proxy (local stub)
   - Test proxy failure → 502 → failover

3. **Manual Verification:**
   - Run app, send request to OpenCode Free model
   - Verify response is 200 (not 403)
   - Check logs for sidecar usage

## Out of Scope

- Rate limiting / quota tracking (handled by existing `ProviderAccount` logic)
- Response caching (handled by existing infrastructure)
- Non-OpenCode providers (unchanged)

## Success Criteria

1. OpenCode Free requests return 200 OK (not 403 FreeTierError)
2. Proxy from DB is used (verified via logs)
3. Failover works when proxy or sidecar fails
4. Sidecar starts automatically on app launch
5. Cross-platform builds succeed for all targets
