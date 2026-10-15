# OpenCode Free Tier Go Sidecar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make OpenCode Free provider work by routing requests through a local Go sidecar that uses Go's crypto/tls fingerprint (passes the JA3/JA4 gate).

**Architecture:** Go binary `opencode-proxy` listens on localhost, forwards requests to opencode.ai with Go TLS fingerprint. .NET `OpenCodeSidecarHandler` intercepts OpenCode Free requests in the HttpClient pipeline, routes to sidecar with proxy URL from `ProxyContext.Current`. Sidecar uses the proxy for the outbound connection.

**Scope decision (2026-10-14):** Windows-only for the sidecar integration — build script targets win-x64 only; other TFMs keep the existing HttpClient path. WebView2 approach was ruled out by spike: headed/headless Edge browser fetch → 403 FreeTierError with all header variants (Origin/sec-ch-ua/Referer stripped, UA flag) — only Go crypto/tls (jasonxu reference, 200 OK) and Electron desktop pass the gate. Go 1.27.0 installed via winget.

**Tech Stack:** Go 1.27 (sidecar), .NET 10 (host app), `golang.org/x/net/proxy` (SOCKS5), GitHub Actions (CI binary build)

**Spec:** `docs/superpowers/specs/2026-10-14-opencode-sidecar-design.md`

## Global Constraints

- Go binary must be self-contained, <10MB
- Proxy credentials must not be hardcoded in Go binary — passed via `X-Proxy-Url` header
- Must work with existing proxy infrastructure (`ProxyHealthHandler`, `ProxyContext`, `ProxyPool`)
- Free tier detection: model ID ends with `-free`
- Sidecar health check: `GET /health` returns 200 OK
- Port discovery: sidecar prints `LISTENING <port>` to stdout on startup

## Review Focus

1. **Sidecar crash recovery** — if Go process dies mid-request, `OpenCodeSidecarHandler` must throw `HttpRequestException` with inner `SocketException` so `ProxyHealthHandler.IsProxyConnectFailure` retries with different proxy
2. **Proxy URL encoding** — credentials with special characters (`@`, `:`, `#`) must be URL-encoded in `X-Proxy-Url` header
3. **SSE streaming** — sidecar must stream response chunks as they arrive, not buffer entire response
4. **Process cleanup** — sidecar process must be killed on app exit (no orphan processes)

---

### Task 1: Go Sidecar — HTTP Server & TLS Forwarding

**Files:**
- Create: `sidecar/opencode-proxy/main.go`
- Create: `sidecar/opencode-proxy/go.mod`
- Create: `sidecar/opencode-proxy/go.sum` (via `go mod tidy`)

**Interfaces:**
- Consumes: none (standalone)
- Produces: HTTP server on `127.0.0.1:<random-port>`, endpoints:
  - `POST /v1/chat/completions` — forwards to `https://opencode.ai/zen/v1/chat/completions`
  - `GET /health` — returns 200 OK
  - Startup output: `LISTENING <port>\n` to stdout

- [ ] **Step 1: Initialize Go module**

```bash
cd sidecar/opencode-proxy
go mod init github.com/HKStudio011/router-balancing/sidecar/opencode-proxy
go get golang.org/x/net/proxy
```

- [ ] **Step 2: Write `main.go` with HTTP server skeleton**

Structure:
- `main()` — parse flags, start HTTP server on `127.0.0.1:0` (random port), print `LISTENING <port>`
- `handleHealth(w, r)` — return 200 OK
- `handleChat(w, r)` — read `X-Proxy-Url` header, build HTTP client with proxy, forward request to opencode.ai, stream response

Key implementation details:
- Use `http.Server` with `ReadTimeout: 0` (no timeout for streaming)
- Target URL: `https://opencode.ai/zen/v1/chat/completions` (hardcode — sidecar is OpenCode-specific)
- Copy all request headers except `Host`, `Content-Length`, `X-Proxy-Url`
- Set `Authorization: Bearer public` if not present
- Stream response using `io.Copy(w, resp.Body)` after copying response headers

- [ ] **Step 3: Implement proxy support**

```go
func buildTransport(proxyURL string) (*http.Transport, error) {
    transport := &http.Transport{
        TLSHandshakeTimeout: 10 * time.Second,
    }
    if proxyURL == "" {
        return transport, nil
    }
    u, err := url.Parse(proxyURL)
    if err != nil {
        return nil, err
    }
    // SOCKS5
    if u.Scheme == "socks5" {
        dialer, err := proxy.FromURL(u, proxy.Direct)
        if err != nil {
            return nil, err
        }
        transport.DialContext = func(ctx context.Context, network, addr string) (net.Conn, error) {
            return dialer.Dial(network, addr)
        }
        return transport, nil
    }
    // HTTP proxy
    transport.Proxy = http.ProxyURL(u)
    return transport, nil
}
```

- [ ] **Step 4: Build and test manually**

```bash
cd sidecar/opencode-proxy
go build -o opencode-proxy.exe .
.\opencode-proxy.exe
# Should print: LISTENING <port>
# Test health: curl http://127.0.0.1:<port>/health → 200 OK
# Test chat: curl -X POST http://127.0.0.1:<port>/v1/chat/completions -d '{...}' → 200 OK
```

- [ ] **Step 5: Commit**

```bash
git add sidecar/opencode-proxy/
git commit -m "feat: add Go sidecar for OpenCode free tier TLS fingerprint"
```

---

### Task 2: Go Sidecar — Build & Commit Binary

**Decision (user):** Build một lần local, commit binary vào repo (`runtimes/win-x64/native/opencode-proxy.exe`). Không cần CI — .exe chạy trên mọi máy Windows x64.

**Files:**
- Create: `sidecar/build.ps1`
- Create: `runtimes/win-x64/native/opencode-proxy.exe` (committed binary)

**Interfaces:**
- Consumes: `sidecar/opencode-proxy/main.go`
- Produces: `runtimes/win-x64/native/opencode-proxy.exe`

- [ ] **Step 1: Write build script (win-x64 only)**

```powershell
# sidecar/build.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$outPath = Join-Path $root "runtimes/win-x64/native/opencode-proxy.exe"
New-Item -ItemType Directory -Force -Path (Split-Path $outPath -Parent) | Out-Null

Push-Location (Join-Path $PSScriptRoot "opencode-proxy")
$env:GOOS = "windows"
$env:GOARCH = "amd64"
$env:CGO_ENABLED = "0"
go build -ldflags="-s -w" -o $outPath .
Pop-Location
Write-Host "Built: $outPath"
```

- [ ] **Step 2: Run build script and verify binary**

```powershell
./sidecar/build.ps1
Test-Path runtimes/win-x64/native/opencode-proxy.exe   # → True
```

- [ ] **Step 3: Commit binary + script**

```bash
git add sidecar/build.ps1 runtimes/win-x64/native/opencode-proxy.exe
git commit -m "feat: build and commit Go sidecar binary for Windows"
```

---

### Task 3: .NET Sidecar Service — Process Management

**Files:**
- Create: `src/RouterBalancing.Core/Providers/OpenCodeSidecarService.cs`
- Create: `src/RouterBalancing.Core/Providers/IOpenCodeSidecarService.cs`
- Test: `router balancing test/Providers/OpenCodeSidecarServiceTests.cs`

**Interfaces:**
- Consumes: `IOpenCodeSidecarService` interface
- Produces:
  ```csharp
  public interface IOpenCodeSidecarService : IDisposable
  {
      /// <summary>Base URL của sidecar (http://127.0.0.1:{port}) — null nếu chưa start hoặc binary missing.</summary>
      string? BaseUrl { get; }
      
      /// <summary>Start sidecar nếu chưa chạy. Trả false nếu binary missing.</summary>
      Task<bool> EnsureStartedAsync(CancellationToken ct = default);
  }
  ```

- [ ] **Step 1: Write failing test for `EnsureStartedAsync`**

```csharp
[Fact]
public async Task EnsureStartedAsync_WhenBinaryMissing_ReturnsFalse()
{
    var service = new OpenCodeSidecarService("/nonexistent/path", NullLogger.Instance);
    var result = await service.EnsureStartedAsync();
    Assert.False(result);
    Assert.Null(service.BaseUrl);
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "OpenCodeSidecarService"
# Expected: FAIL — class doesn't exist
```

- [ ] **Step 3: Implement `IOpenCodeSidecarService` and `OpenCodeSidecarService`**

Implementation:
- Constructor: `(string binaryPath, ILogger logger)`
- `EnsureStartedAsync`:
  1. If `BaseUrl` already set and process alive → return true
  2. If binary doesn't exist → return false
  3. Start process with `ProcessStartInfo` (redirect stdout, no window)
  4. Read first line from stdout, parse `LISTENING <port>`
  5. Set `BaseUrl = $"http://127.0.0.1:{port}"`
  6. Start background task for health check (every 30s)
- `Dispose`: kill process tree

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "OpenCodeSidecarService"
# Expected: PASS
```

- [ ] **Step 5: Write test for health check restart**

```csharp
[Fact]
public async Task EnsureStartedAsync_WhenProcessDies_RestartsOnNextCall()
{
    // Start with valid binary, kill process, call again → should restart
    // (Uses real Go binary if available, or mock)
}
```

- [ ] **Step 6: Implement health check restart logic**

- [ ] **Step 7: Run all tests and commit**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "OpenCodeSidecarService"
git add src/RouterBalancing.Core/Providers/OpenCodeSidecarService.cs src/RouterBalancing.Core/Providers/IOpenCodeSidecarService.cs "router balancing test/Providers/OpenCodeSidecarServiceTests.cs"
git commit -m "feat: add OpenCodeSidecarService for Go sidecar process management"
```

---

### Task 4: .NET Sidecar Handler — Request Routing

**Files:**
- Create: `src/RouterBalancing.Core/Providers/OpenCodeSidecarHandler.cs`
- Test: `router balancing test/Providers/OpenCodeSidecarHandlerTests.cs`

**Interfaces:**
- Consumes: `IOpenCodeSidecarService`, `ProxyContext.Current` (for proxy URL)
- Produces:
  ```csharp
  public sealed class OpenCodeSidecarHandler : DelegatingHandler
  {
      public OpenCodeSidecarHandler(IOpenCodeSidecarService sidecar, HttpMessageHandler innerHandler);
  }
  ```

- [ ] **Step 1: Write failing test for non-OpenCode request passthrough**

```csharp
[Fact]
public async Task SendAsync_WhenRequestNotToOpencode_PassesThrough()
{
    var mockHandler = new Mock<HttpMessageHandler>();
    // ... setup mock to return 200
    var sidecar = new Mock<IOpenCodeSidecarService>();
    var handler = new OpenCodeSidecarHandler(sidecar.Object, mockHandler.Object);
    
    var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
    var response = await handler.SendAsync(request, CancellationToken.None);
    
    Assert.Equal(200, (int)response.StatusCode);
    // Verify sidecar was NOT called
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "OpenCodeSidecarHandler"
# Expected: FAIL — class doesn't exist
```

- [ ] **Step 3: Implement `OpenCodeSidecarHandler`**

Logic:
1. Check if request URI host is `opencode.ai` AND path contains `/chat/completions`
2. If not → `return await base.SendAsync(request, ct)` (passthrough)
3. If yes:
   a. Get `ProxyContext.Current` → format proxy URL (`scheme://user:pass@host:port`)
   b. Call `sidecar.EnsureStartedAsync(ct)` → if false, throw `HttpRequestException` with `SocketException` inner
   c. Create new request to `{sidecar.BaseUrl}/v1/chat/completions`
   d. Copy all headers except `Host`, `Content-Length`, `X-Proxy-Url`
   e. Add `X-Proxy-Url` header with proxy URL (URL-encode credentials)
   f. Send via `base.SendAsync`
   g. If response is 502 with `X-Proxy-Failure` header → throw `HttpRequestException` with `SocketException` inner (so `ProxyHealthHandler` retries)

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "OpenCodeSidecarHandler"
# Expected: PASS
```

- [ ] **Step 5: Write test for OpenCode request routing**

```csharp
[Fact]
public async Task SendAsync_WhenRequestToOpencode_RoutesThroughSidecar()
{
    // Setup mock sidecar with BaseUrl
    // Setup mock inner handler
    // Send request to https://opencode.ai/zen/v1/chat/completions
    // Verify request was sent to sidecar.BaseUrl
    // Verify X-Proxy-Url header was set
}
```

- [ ] **Step 6: Implement and test proxy URL formatting**

```csharp
private static string FormatProxyUrl(ProxyAttempt attempt)
{
    var builder = new UriBuilder(attempt.Uri);
    if (attempt.Username != null)
    {
        builder.UserName = Uri.EscapeDataString(attempt.Username);
        builder.Password = Uri.EscapeDataString(attempt.Password ?? "");
    }
    return builder.Uri.ToString();
}
```

- [ ] **Step 7: Run all tests and commit**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "OpenCodeSidecarHandler"
git add src/RouterBalancing.Core/Providers/OpenCodeSidecarHandler.cs "router balancing test/Providers/OpenCodeSidecarHandlerTests.cs"
git commit -m "feat: add OpenCodeSidecarHandler for routing OpenCode requests through sidecar"
```

---

### Task 5: Integration — Register Handler in HttpClient Pipeline

**Files:**
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs` (ConfigureServices + upstream pipeline)

**Interfaces:**
- Consumes: `OpenCodeSidecarHandler`, `OpenCodeSidecarService`
- Produces: sidecar handler registered in upstream HttpClient pipeline

- [ ] **Step 1: Register `IOpenCodeSidecarService` in DI**

In `ConfigureServices`:
```csharp
services.AddSingleton<IOpenCodeSidecarService>(sp =>
{
    var binaryPath = ResolveSidecarBinaryPath();
    return new OpenCodeSidecarService(binaryPath, sp.GetRequiredService<ILogger<OpenCodeSidecarService>>());
});
```

- [ ] **Step 2: Add `OpenCodeSidecarHandler` to upstream HttpClient pipeline**

In `CreateUpstreamHandler`, add handler AFTER `ProxyHealthHandler`:
```csharp
var handler = new ProxyHealthHandler(pool, log, resolver)
{
    InnerHandler = new OpenCodeSidecarHandler(sidecar, new SocketsHttpHandler { ... })
};
```

- [ ] **Step 3: Implement `ResolveSidecarBinaryPath` (Windows-only)**

```csharp
private static string ResolveSidecarBinaryPath() =>
    Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native", "opencode-proxy.exe");
```

- [ ] **Step 4: Build and verify no regressions**

```bash
dotnet build router-balancing.slnx
dotnet test "router balancing test/router balancing test.csproj"
```

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Server/ProxyApp.cs
git commit -m "feat: register OpenCodeSidecarHandler in upstream HttpClient pipeline"
```

---

### Task 6: MAUI Project — Bundle Go Binary (Windows)

**Files:**
- Modify: `router-balancing/router-balancing.csproj`

**Interfaces:**
- Consumes: `runtimes/win-x64/native/opencode-proxy.exe` (committed)
- Produces: binary copied to output directory on Windows builds

- [ ] **Step 1: Copy binary to output on Windows TFM**

```xml
<ItemGroup Condition="$([MSBuild]::GetTargetPlatformIdentifier('$(TargetFramework)')) == 'windows'">
    <Content Include="..\runtimes\win-x64\native\opencode-proxy.exe"
             CopyToOutputDirectory="PreserveNewest"
             Link="runtimes\win-x64\native\opencode-proxy.exe" />
</ItemGroup>
```

- [ ] **Step 2: Build MAUI app for Windows and verify binary is copied**

```bash
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
Test-Path bin/Debug/net10.0-windows10.0.19041.0/runtimes/win-x64/native/opencode-proxy.exe
```

- [ ] **Step 3: Commit**

```bash
git add router-balancing/router-balancing.csproj
git commit -m "feat: bundle Go sidecar binary in MAUI app for Windows"
```

---

### Task 7: End-to-End Verification

**Files:**
- None (manual verification)

**Interfaces:**
- Consumes: all previous tasks
- Produces: verified working OpenCode Free tier

- [ ] **Step 1: Build and run app on Windows**

```bash
dotnet build router-balancing.slnx
dotnet run --project router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
```

- [ ] **Step 2: Verify sidecar starts**

- Check logs for `OpenCode sidecar started on http://127.0.0.1:<port>`
- Verify process is running: `Get-Process opencode-proxy`

- [ ] **Step 3: Send test request to OpenCode Free model**

```bash
curl -X POST http://localhost:8317/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{"model":"mimo-v2.6-flash-free","messages":[{"role":"user","content":"hi"}],"stream":true}'
```

- Expected: 200 OK with SSE stream (not 403 FreeTierError)

- [ ] **Step 4: Verify proxy is used**

- Check logs for `Proxy 161.118.197.171` (or whatever proxy is configured)
- Verify sidecar received `X-Proxy-Url` header

- [ ] **Step 5: Test failover**

- Disable proxy in DB → request should still work (direct)
- Kill sidecar process → request should fail over to next provider

- [ ] **Step 6: Final commit**

```bash
git add -A
git commit -m "feat: OpenCode Free tier working via Go sidecar"
```
