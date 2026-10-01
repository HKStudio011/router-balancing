# Spec: Outbound Proxy Pool (item 5)

**Date:** 2026-10-01 · **Status:** drafted, chờ user review · **Branch:** `feat/retry-circuit-3c`

- **Amend 2026-10-01 (khi viết plan, verify từ source `dotnet/runtime`):**
  1. `ReportFailure` trả `bool` (`true` = vừa chuyển sống→down) — handler log Warn duy nhất lần đầu, pool không cần biết message lỗi (§4.1, §7).
  2. **Auth KHÔNG dùng userinfo trong proxy URI** — .NET 10 không parse userinfo (`dotnet/runtime#125341`, fix chỉ vào .NET 11). Thay bằng `RoundRobinWebProxy.Credentials` trả `ICredentials` ổn định (`DynamicProxyCredentials`), `GetCredential()` đọc `ProxyContext.Current` → credential đúng theo proxy đang dùng (§4.3, §4.6). Verified: `HttpConnectionPoolManager.SendAsync` gọi `_proxy.GetProxy` **per-request** (không cache theo destination), connection pool key gồm `proxyUri` (tái sử dụng kết nối per-proxy), `socks5://` scheme native.
  3. `ProxyAttempt` mang `Username`/`Password` (đã decrypt, trong memory) song song `Uri` không userinfo; `Endpoint` display không credentials.

## 1. Mục tiêu & phạm vi

Thêm **pool proxy outbound toàn cục**: mọi request HTTP mà app gửi **tới provider** đi qua 1 proxy được chọn round-robin từ pool — mục đích phân tán IP, tránh rate-limit/geo-block/ban theo IP.

**Trong scope:**
- Entity `OutboundProxy` (DB) + migration; service CRUD + Test (echo IP); trang `/proxies`.
- Round-robin chọn proxy sống; health (down + cooldown 60s + passive recover); failover trong cùng request; fallback **direct** khi pool rỗng hoặc tất cả down.
- Protocol: **HTTP(S)** và **SOCKS5** (cả hai có thể có username/password). .NET 6+ hỗ trợ `socks5://` native — không thêm thư viện ngoài.
- Áp dụng cho **mọi outbound tới provider**: chat forwarding (`upstream`), provider probe/test/metadata (`provider-probe`), free-model sync (`free-model-sync`) — plan step 1 survey `AddHttpClient` để xác nhận không sót client nào.

**Non-goals:** geo/country tag, weighted RR, sticky session, per-provider pool, SOCKS4, persist health qua restart, UDP, API xoay proxy cho client bên ngoài, auto-điền danh sách proxy từ nguồn ngoài.

**Không đổi:** inbound proxy Kestrel (127.0.0.1:8317) và toàn bộ đường request nội bộ.

## 2. Quyết định đã chốt (user)

| # | Quyết định |
|---|---|
| 1 | Global pool — không gán per-provider |
| 2 | Round-robin qua proxy sống |
| 3 | Mọi outbound tới provider đi qua pool (kể cả test key/probe/sync) |
| 4 | Proxy lỗi → đánh down + cooldown, **failover** sang proxy kế trong cùng request; hết proxy sống → **direct**; auto-recover sau cooldown |
| 5 | Hỗ trợ HTTP + SOCKS5, có auth |
| 6 | Trang `/proxies` riêng (theo pattern `Providers.razor`) |
| 7 | Nút Test = GET echo IP qua proxy đó, hiển thị IP egress |
| 8 | Approach A: dynamic `IWebProxy` + failover `DelegatingHandler` |

## 3. Data model

### 3.1 Entity `OutboundProxy`

`src/RouterBalancing.Core/Domain/Entities/OutboundProxy.cs`, table `OutboundProxies`
(tên `Proxy` thuần sẽ trỏng với `ProxyHost`/`ProxyApp`/`ProxyRequest`).

| Field | Type | Ghi chú |
|---|---|---|
| `Id` | `long` | EF tự sinh |
| `Scheme` | `string` | `"http"` hoặc `"socks5"` — validate ở service |
| `Host` | `string` | bắt buộc, không rỗng |
| `Port` | `int` | 1–65535 |
| `Username` | `string?` | optional |
| `PasswordEncrypted` | `string?` | DPAPI qua `ISecretProtector` — pattern `ProviderAccount.ApiKeyEncrypted`; không bao giờ log/plaintext |
| `Enabled` | `bool` | default `true` — tắt để giữ mà không dùng |
| `LastTestSuccess` | `bool?` | null = chưa test |
| `LastTestAt` | `DateTimeOffset?` | |
| `LastTestMessage` | `string?` | kết quả/endpoint lỗi |
| `LastTestIp` | `string?` | IP egress trả về echo |
| `CreatedAt` / `UpdatedAt` | `DateTimeOffset` | |

- **Không có Name** — hiển thị theo `scheme://host:port`.
- **Không persist runtime down-state** — health sống in-memory ở pool.
- EF: thêm `DbSet<OutboundProxy>`; migration `AddOutboundProxies` (3 artifacts); **không seed** — pool rỗng = direct = hành vi hiện tại không đổi.

### 3.2 Validation (`ProxyDraft`)

- `Scheme ∈ {"http","socks5"}` (case-insensitive, lưu lowercase); `Host` không rỗng/whitespace; `Port ∈ [1,65535]`.
- Unique theo `scheme://host:port` (case-insensitive host) → `InvalidOperationException`/`ArgumentException` theo pattern service hiện có.
- `Username`/`Password` optional; khi sửa, `Password = null` trong draft ⇒ **giữ** password đã encrypt. **Username để trống khi update ⇒ xóa toàn bộ auth** (username + password) — username rỗng thắng, không để password mồ côi không username.

## 4. Core — pool, chọn proxy, failover

### 4.1 `IProxyPool` (singleton, namespace `RouterBalancing.Core.Proxies`)

```csharp
public interface IProxyPool
{
    /// <summary>Chọn proxy kế theo round-robin trong số proxy sống (Enabled và không trong cooldown).
    /// Trả <see langword="null"/> = direct (pool rỗng hoặc tất cả down).</summary>
    ProxyAttempt? GetNext();

    /// <summary>Ghi nhận lỗi kết nối tới proxy — đánh down + cooldown <c>DownCooldown</c>.
    /// Trả <see langword="true"/> khi vừa chuyển sống→down (log Warn duy nhất lần đầu);
    /// proxy đã down = no-op, trả <see langword="false"/>.</summary>
    bool ReportFailure(long proxyId);

    /// <summary>Ghi nhận thành công — reset trạng thái failure của proxy.</summary>
    void ReportSuccess(long proxyId);

    /// <summary>Bắn lại snapshot từ DB — service CRUD gọi sau mọi thao tác.</summary>
    void Invalidate();

    /// <summary>Trạng thái runtime hiện tại cho UI (danh sách proxy Enabled).</summary>
    IReadOnlyList<ProxyRuntimeStatus> Snapshot();
}
```

- `ProxyAttempt` (record): `long Id`, `Uri Uri` (**không** userinfo), `string? Username`, `string? Password` (đã decrypt, chỉ sống trong memory), `string Endpoint` (`scheme://host:port`, **không** chứa credentials — display/log).
- `ProxyRuntimeStatus` (record): `long Id`, `string Endpoint`, `bool IsDown`, `DateTimeOffset? DownUntil`.
- **Snapshot từ DB**: chỉ `Enabled = true`; decrypt password vào `ProxyAttempt` (không đưa vào `Uri`). Cache trong memory; `Invalidate()` reload (CRUD gọi sau Create/Update/Delete/SetEnabled).
- **RR + health in-memory**: cursor RR tăng đều qua danh sách proxy sống (thứ tự `Id`); `ReportFailure` → `DownUntil = now + DownCooldown` (**const 60s**), `ReportSuccess` → reset. `ReportFailure` với proxy **đã down** = no-op (không gia hạn cooldown, không log lại — nhiều request cùng fail song song không đụng cooldown của nhau). Passive recover: hết cooldown → proxy quay lại RR (không probe riêng); fail tiếp → cooldown lại. Reset toàn bộ khi restart app — chấp nhận.
- `GetNext()` bỏ qua down; hết proxy sống → `null`.
- Thread-safe (lock hoặc concurrent structure); `TimeProvider` inject để test cooldown.

### 4.2 `ProxyContext` — correlation pick ↔ failure

`IWebProxy.GetProxy(Uri)` chỉ nhận destination, không biết request nào gọi → dùng **`AsyncLocal<ProxyAttempt?>`** (static holder `ProxyContext`):

- `ProxyHealthHandler` set `ProxyContext.Current = pick` **trước** khi gọi inner; giá trị flows theo execution context xuống `SocketsHttpHandler` → `RoundRobinWebProxy.GetProxy` đọc đúng attempt của request đó.
- Không có scope (không qua handler) → bypass (direct) — deterministic, không RR ngầm ngoài luồng health.

### 4.3 `RoundRobinWebProxy : IWebProxy`

- `GetProxy(dest)` → `ProxyContext.Current?.Uri`; `IsBypassed(dest)` → `ProxyContext.Current is null`.
- Kết nối tới cùng proxy tái sử dụng nhờ connection grouping per-proxy có sẵn của `SocketsHttpHandler`.

### 4.4 `ProxyHealthHandler : DelegatingHandler`

Vòng lặp cho mỗi request (budget = **số proxy sống lúc bắt đầu request** — tránh vòng lặp vô hạn):

1. `pick = pool.GetNext()`; nếu `null` → attempt direct cuối (không report health vì không dính proxy nào).
2. Set `ProxyContext.Current = pick` → `base.SendAsync(request, ct)`.
3. **Thành công** (có response) → `ReportSuccess(pick.Id)` (nếu pick != null) → trả response.
4. **Lỗi connect-phase** — chỉ thỏa mãn TẤT CẢ: `HttpRequestException` (inner `SocketException` **hoặc** inner timeout **hoặc** message chứa `407`) **VÀ** `pick != null` **VÀ** budget còn lượt → nếu `ReportFailure(pick.Id)` trả `true` → log Warn (§7) → quay vòng (pick kế sẽ tự skip proxy vừa down).
   - **Response 407 cuối cùng** (proxy yêu cầu auth sai/không có — chỉ proxy sinh được 407, không phải upstream): cũng coi là proxy fail — `ReportFailure` + log + quay vòng như trên. (Flow 407-nội-bộ do .NET tự retry với `GetCredential` xảy ra bên trong `SendAsync` — handler chỉ thấy kết quả cuối.)
5. Hết budget hoặc lỗi khác → ném nguyên (không wrap) — caller hiện có xử lý như nay.

**Replay guard:** chỉ retry khi `request.Content is null || request.Content is ByteArrayContent || request.Content is StringContent || request.Content is FormUrlEncodedContent` (đều buffer lại được). Content khác → không retry, ném ngay. Toàn bộ outbound hiện tại của app đều khớp nhóm này.

**Không đụng health khi:** response trả về từ upstream (4xx/5xx, kể cả 502 sinh tại proxy — proxy vẫn sống); lỗi đọc **giữa stream SSE** (đã trả response — provider-retry hiện có lo; không attempt lại ở handler này).

### 4.5 Wiring named clients

```csharp
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    Proxy = new RoundRobinWebProxy(pool),
    // cấu hình sẵn có (PooledConnectionLifetime...) giữ nguyên
})
.AddHttpMessageHandler<ProxyHealthHandler>()
```

Áp dụng cho **cả 3 client**: `upstream` (`ProxyApp.ConfigureServices`), `provider-probe`, `free-model-sync` (`MauiProgram`).

**2 container DI — một instance pool duy nhất:**
- `ProxyHost` tự tạo `WebApplicationBuilder` riêng (đã biết từ proxy-core spec) ⇒ `ProxyApp.ConfigureServices(builder, protector, IProxyPool pool)` nhận **instance** từ container ngoài; `ProxyHost` inject `IProxyPool` (đăng ký ở `MauiProgram`) và forward vào → `AddSingleton(pool)`.
- RR cursor + down-state dùng chung giữa chat-forward và probe/sync.
- Ký thay đổi → sửa call site integration test hiện có (~4 chỗ gọi `ProxyApp.ConfigureServices`).

### 4.6 Auth (`DynamicProxyCredentials`)

- **Không** nhúng userinfo vào `Uri` (§ Amend 2.0 — .NET 10 không parse; fix vào .NET 11).
- `RoundRobinWebProxy.Credentials` trả về **1 instance `DynamicProxyCredentials` ổn định** (đọc 1 lần lúc `SocketsHttpHandler` construct — getter phải trả object, không evaluate pick tại đó).
- `DynamicProxyCredentials.GetCredential(uri, authType)` đọc `ProxyContext.Current` → `NetworkCredential(Username, Password)`; proxy hiện tại không có auth → trả `null`.
- Cơ chế này phục vụ **cả 2 flow**: HTTP proxy 407-challenge (`AuthenticationHelper` gọi `ProxyCredentials.GetCredential` trong cùng execution flow của request) và SOCKS5 handshake. Verify thực nghiệm bằng stub-proxy tests (§8) — nếu SOCKS5 trên .NET 10 không gọi `GetCredential` → fallback plan ghi trong plan (quyết định khi test đỏ).

## 5. Service — CRUD + Test

`IProxyService` (singleton, `Core/Proxies/`, pattern `ProviderService`: `IDbContextFactory` + `ISecretProtector` + inject `IProxyPool`):

```csharp
Task<IReadOnlyList<OutboundProxy>> ListAsync(CancellationToken ct = default);
Task<OutboundProxy> CreateAsync(ProxyDraft draft, CancellationToken ct = default);
Task<OutboundProxy> UpdateAsync(long id, ProxyDraft draft, CancellationToken ct = default);
Task DeleteAsync(long id, CancellationToken ct = default);
Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);
Task<ProxyTestResult> TestAsync(long id, CancellationToken ct = default);
```

- `ProxyTestResult` (record): `bool Success`, `string? Ip`, `string Message`, `DateTimeOffset At`.
- Tách seam: **`IProxyEchoClient`** (`GetIpAsync(ProxyEchoTarget, ct)` → IP) — `ProxyService` inject, impl mặc định `ProxyEchoClient` tạo `HttpClient` riêng mỗi lần (không qua named client/pool); unit test stub seam, không network.
- Mọi thao tác CRUD/toggle → persist `LastTest*` không đổi; **mọi thao tác CRUD/toggle → `pool.Invalidate()`**.
- `TestAsync`:
  - Tạo `HttpClient` **riêng** mỗi lần (không qua named client/pool) với `SocketsHttpHandler { Proxy = WebProxy(uri cố định của proxy này) }`, timeout 10s.
  - `GET https://api.ipify.org?format=json` → parse field `ip` → persist `LastTestSuccess/At/Message/Ip`.
  - **Không** đụng health state (`ReportSuccess/Failure` không gọi) — test tay ≠ runtime health.
  - Ghi `LastTestMessage` gồm thông điệp lỗi (không chứa credentials).
- Lỗi CRUD theo pattern service hiện có (`KeyNotFoundException` khi id không tồn tại…); XML doc đủ cho public API.

## 6. UI — trang `/proxies`

- File mới `Components/Pages/Proxies.razor` (`@page "/proxies"`), NavMenu thêm 1 `NavLink` (icon + label `proxies`).
- **Table**: endpoint (`scheme://host:port`) · badge scheme · cột Auth (có/không — chỉ hiện `•••` khi có username, không hiện password) · toggle `Enabled` · **status runtime** từ `pool.Snapshot()` (badge `Down (còn Xs)` / trống) · Test result (`✓ ip · giờ` / `✗ message`) · actions: **Test**, Sửa, Xóa (`ConfirmDialog`).
- **Modal** (`Modal.razor` sẵn): scheme (select http/socks5), host, port, username, password (`type=password`; **không** hiện lại giá trị cũ; sửa để trống = giữ nguyên).
- Empty state: pool rỗng → dòng hướng dẫn "Chưa có proxy — request đi direct".
- Status đọc lúc load trang (sau mỗi hành động reload) — **không** auto-refresh countdown.
- **i18n**: mọi key mới (`nav.proxies`, `proxies.*`) phải có ở **cả 2 dict** `English` và `Vietnamese` trong `Translations.cs`.
- Razor UTF-8 trực tiếp, không HTML entity; comment `@* *@` chỉ khi markup không tự mô tả.

## 7. Logging

Qua `ILogService`, message tiếng Việt (theo chuẩn repo), không bao giờ chứa credentials:

| Event | Level | Format |
|---|---|---|
| Proxy bị đánh down | `Warn` | `Proxy {endpoint} đánh dấu down 60s sau lỗi kết nối: {lỗi}` — do `ProxyHealthHandler` ghi **chỉ khi `ReportFailure` trả `true`** (chuyển sống→down), không lặp mỗi request |
| Request failover thành công sau khi đổi proxy | — | không log (response đã đi qua log request hiện có) |
| Test echo | — | không log (đã persist `LastTest*` + hiện UI) |

Lỗi từ upstream: giữ nguyên log hiện có, không đổi.

## 8. Testing strategy (TDD)

| File | Pin hành vi |
|---|---|
| `Core/Proxies/ProxyPoolTests.cs` | RR đúng thứ tự proxy sống; bỏ qua down; pool rỗng/tất cả down → `null`; `ReportFailure` → down đúng cooldown (`TimeProvider` fake) → advance → recover; `ReportSuccess` reset; `Invalidate` reload từ stub context; `GetNext` nhiều thread an toàn |
| `Core/Proxies/ProxyHealthHandlerTests.cs` | connect-fail → `ReportFailure` + retry proxy kế → thành công; upstream 500 → **không** report; 407 → coi là proxy fail; hết budget → ném; success → `ReportSuccess`; content không replayable → không retry |
| `Core/Proxies/RoundRobinWebProxyTests.cs` | có scope → `GetProxy` trả `Uri` của attempt (không userinfo); không scope → `IsBypassed` = true; `Credentials` = instance ổn định; `DynamicProxyCredentials.GetCredential` đọc đúng AsyncLocal (set → NetworkCredential, null → null) |
| `Core/Proxies/ProxyServiceTests.cs` | validation (scheme/port/duplicate/host rỗng); password encrypt at rest + decrypt khi pool nạp; `Password=null` khi update = giữ cũ; username trống khi update = xóa auth; CRUD + `SetEnabled`; mọi CRUD gọi `Invalidate`; `TestAsync` success/fail với echo seam stub |
| `Server/ProxyOutboundHttpStubTests.cs` | stub proxy cục bộ (`TcpListener` HTTP): GetProxy per-request (2 request → 2 pick RR); 407 → `GetCredential` (AsyncLocal) gửi Basic auth → pass; proxy chết (port đóng) → `ReportFailure` + failover proxy kế; tất cả chết → direct vẫn tới destination stub |
| `Server/ProxyOutboundSocksStubTests.cs` | stub SOCKS5 cục bộ (RFC1928 + RFC1929): handshake nhận đúng username/password của pick (pin `GetCredential` với socks trên .NET 10 — risk §11.4); no-auth variant; socks chết → failover |
| Migration test | pattern `AddProviderAccountsMigrationTests` — 3 artifacts của `AddOutboundProxies` coherent |

- Test project thêm gì tùy plan (không thêm thư viện ngoài ngoài xUnit đã có).
- **Không** test qua proxy internet thật (flaky) — echo IP chỉ test bằng seam stub; smoke thật do user tự test với proxy của họ.

## 9. Smoke checklist (controller chạy cuối plan, qua CDP như provider-free)

1. `/proxies` mở được, empty state đúng.
2. Thêm proxy HTTP + SOCKS5 (kèm auth) → row hiện đủ field, password không lộ.
3. Sửa port → persist; sửa password để trống → giữ mật khẩu cũ (verify qua decrypt trong log-less way: Test vẫn chạy).
4. Duplicate `scheme://host:port` → báo lỗi đúng.
5. Toggle Enabled → persist; Xóa → confirm → biến mất.
6. Nút Test (dùng stub/local hoặc proxy user cung cấp) → `LastTest*` cập nhật.
7. Đổi Enabled → log/đối chiếu `pool.Invalidate` hoạt động (row mới có effect ngay — verify qua unit test nếu không có proxy thật).
8. Fallback: pool rỗng → app vẫn forward direct (hành vi hiện tại không đổi — chạy 1 request qua proxy thật không cấu hình gì).

## 10. File map (dự kiến — plan chốt lại)

**Tạo:** `Domain/Entities/OutboundProxy.cs` · `Core/Proxies/{IProxyPool, ProxyPool, ProxyAttempt, ProxyRuntimeStatus, ProxyContext, RoundRobinWebProxy, DynamicProxyCredentials, ProxyHealthHandler, IProxyService, ProxyService, ProxyDraft, ProxyValidator, ProxyValidationException, ProxyTestResult, IProxyEchoClient, ProxyEchoClient}.cs` · `Components/Pages/Proxies.razor` · migration `AddOutboundProxies` · test files ở §8.

**Sửa:** `RouterBalancingDbContext` (DbSet + unique index) · `MauiProgram` (DI pool/service/echo + wire 2 named client) · `ProxyApp.ConfigureServices` (ký +nhận pool + wire `upstream`) · `ProxyHost` (inject/forward pool) · `NavMenu.razor` · `Translations.cs` (2 dict) · 4 integration test call site `ConfigureServices`.

## 11. Risks / open items

1. ~~**Userinfo auth behavior**~~ — **đã resolve**: .NET 10 không parse userinfo (`dotnet/runtime#125341`) → thay bằng `DynamicProxyCredentials` (§4.6); pin thực nghiệm bằng stub HTTP (407→Basic) + stub SOCKS (RFC1929 handshake).
2. ~~**AsyncLocal flows vào `GetProxy`**~~ — đã verify từ source: `HttpConnectionPoolManager.SendAsync` gọi `_proxy.GetProxy` per-request trong cùng flow; stub test pin lại.
3. ~~`socks5://` native~~ — đã verify từ source (`HttpUtilities.IsSocksScheme` + `SocksTunnel` pool kinds).
4. **SOCKS5 handshake có gọi `ProxyCredentials.GetCredential` trên .NET 10** — chưa verify source; stub SOCKS test (§8) sẽ pin. Nếu đỏ → fallback: `RoundRobinWebProxy.Credentials` trả `NetworkCredential` tĩnh cho tới khi probe đủ (hoặc nâng .NET 11) — quyết định khi test chạy.
