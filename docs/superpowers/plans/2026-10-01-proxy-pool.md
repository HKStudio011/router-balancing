# Outbound Proxy Pool (item 5) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pool proxy outbound toàn cục — mọi request HTTP app gửi tới provider đi qua proxy được chọn round-robin (HTTP + SOCKS5, có auth); lỗi proxy → đánh down + cooldown 60s + failover trong cùng request; hết proxy sống → direct; trang `/proxies` CRUD + Test echo IP — spec: `docs/superpowers/specs/2026-10-01-proxy-pool-design.md`.

**Architecture:** `OutboundProxy` (DB, DPAPI password) + singleton `ProxyPool` (RR in-memory + cooldown qua `TimeProvider`) + `ProxyHealthHandler` (DelegatingHandler failover, budget = số proxy sống) set `ProxyContext` (AsyncLocal) → `RoundRobinWebProxy.GetProxy` trả proxy của request đó; auth qua `DynamicProxyCredentials` (`.NET 10` không parse userinfo trong proxy URI — `dotnet/runtime#125341`). Service `ProxyService` (CRUD + `Invalidate` pool) + `ProxyEchoClient` (Test echo IP, seam riêng); wiring 3 named client `upstream`/`provider-probe`/`free-model-sync`; UI `Proxies.razor` theo pattern `Combos.razor`.

**Tech Stack:** .NET 10 / EF Core 10 (SQLite, auto-migration), xUnit, `System.Net.Sockets` (stub TCP test — không thư viện ngoài).

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên; TFM app: `net10.0-windows10.0.19041.0`.
- Comment tiếng Việt cho "tại sao", XML doc cho public API; không thêm comment thừa / TODO vô chủ.
- **Exception message tiếng Anh** (consistent với `Provider {id} not found.` hiện có); comment giải thích tiếng Việt.
- i18n: mọi key mới phải có trong **cả 2 dict** `English` và `Vietnamese` của `Translations.cs` (parity test tự bắt).
- 1 task = 1 commit; commit message tiếng Anh conventional (`feat:`/`docs:`/`fix:`).
- Không nuốt exception — chỗ plan nêu best-effort được catch **kèm log + comment** (giải phóng message tiếng Việt qua `ILogService`); không `catch {}` rỗng.
- Razor: UTF-8 trực tiếp, **không** HTML entity (guard `RazorParameterEntityTests` — chỉ bắt attribute dạng `&#nnnn;` làm trọn giá trị, ký tự `✓✗—••` trực tiếp là OK).
- **Không bao giờ log/password trong message** — endpoint (`scheme://host:port`) OK, password không.
- App `router-balancing` phải **đóng** trước khi build TFM Windows / chạy test (SingleInstanceGuard giữ mutex).
- Gates chuẩn (mỗi task, trước commit):
  - `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj`
  - `dotnet test "router balancing test/router balancing test.csproj"`
  - Task đụng UI/MauiProgram/App/ProxyApp/ProxyHost: thêm `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`
  - **Không** dùng `dotnet build router-balancing.slnx` làm gate (6 lỗi pre-existing NETSDK1082 + DLL lock).
- Test flake đã biết: `ProxyControlApiTests` teardown IOException, `ProxyRetryIntegrationTests` fuse-open 503-vs-429, `ProxyQueueIntegrationTests` teardown → chạy lại PASS; test fail do app đang mở → đóng app, chạy lại.
- Baseline test hiện tại: **447** (395 `[Fact]` + 11 `[Theory]`).
- Migration command chuẩn (Task 1):
  ```powershell
  dotnet ef migrations add AddOutboundProxies --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations
  ```

## File Structure

| Hành động | File | Trách nhiệm |
|---|---|---|
| Create | `src/RouterBalancing.Core/Domain/Entities/OutboundProxy.cs` | Entity bảng `OutboundProxies` |
| Modify | `src/RouterBalancing.Core/Storage/RouterBalancingDbContext.cs` | `DbSet<OutboundProxy>` + unique index 3 cột |
| Create | (dotnet-ef) `Storage/Migrations/<ts>_AddOutboundProxies.cs` + `.Designer.cs` + snapshot | Bảng mới |
| Create | `src/RouterBalancing.Core/Proxies/ProxyAttempt.cs` | record `ProxyAttempt` |
| Create | `src/RouterBalancing.Core/Proxies/ProxyRuntimeStatus.cs` | record `ProxyRuntimeStatus` |
| Create | `src/RouterBalancing.Core/Proxies/IProxyPool.cs` | Contract pool (GetNext/Report*/Invalidate/Snapshot) |
| Create | `src/RouterBalancing.Core/Proxies/ProxyPool.cs` | RR + cooldown 60s + load từ DB |
| Create | `src/RouterBalancing.Core/Proxies/ProxyContext.cs` | `AsyncLocal<ProxyAttempt?>` correlation |
| Create | `src/RouterBalancing.Core/Proxies/DynamicProxyCredentials.cs` | `ICredentials` đọc AsyncLocal |
| Create | `src/RouterBalancing.Core/Proxies/RoundRobinWebProxy.cs` | `IWebProxy` đọc ProxyContext |
| Create | `src/RouterBalancing.Core/Proxies/ProxyHealthHandler.cs` | Failover `DelegatingHandler` |
| Create | `src/RouterBalancing.Core/Proxies/ProxyDraft.cs` + `ProxyValidator.cs` + `ProxyValidationException.cs` | Form draft + validate key i18n |
| Create | `src/RouterBalancing.Core/Proxies/IProxyService.cs` + `ProxyService.cs` | CRUD + Invalidate pool (+ TestAsync ở Task 7) |
| Create | `src/RouterBalancing.Core/Proxies/ProxyTestResult.cs` + `IProxyEchoClient.cs` + `ProxyEchoClient.cs` | Test echo IP (seam) — Task 7 |
| Modify | `src/RouterBalancing.Core/Server/ProxyApp.cs` | Ký `ConfigureServices(+IProxyPool)` + wire `upstream` |
| Modify | `src/RouterBalancing.Core/Server/ProxyHost.cs` | Inject + forward `IProxyPool` |
| Modify | `router-balancing/MauiProgram.cs` | DI pool/service/echo/handler + wire 2 named client |
| Modify | `router-balancing/Components/Layout/NavMenu.razor` | + NavLink `proxies` |
| Modify | `src/RouterBalancing.Core/Localization/Translations.cs` | + 33 key ×2 dict (cuối mỗi dict) |
| Create | `router-balancing/Components/Pages/Proxies.razor` | Trang CRUD `/proxies` |
| Create (test) | `router balancing test/Storage/AddOutboundProxyMigrationTests.cs` | 3 artifacts coherent |
| Create (test) | `router balancing test/Proxies/ProxyServiceTests.cs` | CRUD/validation/encrypt/Invalidate |
| Create (test) | `router balancing test/Proxies/ProxyPoolTests.cs` | RR/cooldown/recover/Invalidate/thread-safety |
| Create (test) | `router balancing test/Proxies/RoundRobinWebProxyTests.cs` | GetProxy/IsBypassed/Credentials/AsyncLocal |
| Create (test) | `router balancing test/Proxies/ProxyHealthHandlerTests.cs` | failover/407/budget/replay-guard |
| Create (test) | `router balancing test/Server/LocalHttpServer.cs` + `LocalHttpProxyStub.cs` | Stub destination + HTTP proxy (TcpListener) |
| Create (test) | `router balancing test/Proxies/ProxyOutboundHttpStubTests.cs` | RR/407-auth/failover/direct — end-to-end |
| Create (test) | `router balancing test/Server/LocalSocks5Stub.cs` | Stub SOCKS5 (RFC1928+RFC1929) |
| Create (test) | `router balancing test/Proxies/ProxyOutboundSocksStubTests.cs` | Handshake auth/no-auth/failover |
| Modify (test) | `router balancing test/TestDoubles.cs` | + `DirectProxyPool` (Task 8) |
| Modify (test) | `router balancing test/Server/ProxyRetryIntegrationTests.cs`, `ProxyAppChatIntegrationTests.cs`, `ProxyControlApiTests.cs`, `ProxyQueueIntegrationTests.cs` | Call site `ConfigureServices(+pool)` |
| Modify (test) | `router balancing test/Server/ProxyHostTests.cs` | 3 chỗ `new ProxyHost(...)` + pool |

## Design decisions locked in this plan (spec gap → plan resolve)

1. **`RoundRobinWebProxy` parameterless** — chỉ đọc `ProxyContext`, không cần `IProxyPool` (spec §4.5 sketch ghi `new RoundRobinWebProxy(pool)` nhưng mô tả §4.3 không dùng pool; ctor không tham số đơn giản hơn và ít coupling).
2. **Budget = `Math.Max(1, Snapshot().Count(!IsDown))`** — chặn race budget=0 nhưng `GetNext` vẫn trả proxy (CRUD giữa chừng) gây `throw null`; nhánh hết budget ném lại `lastFailure` qua `ExceptionDispatchInfo` (không wrap).
3. **Retry bằng clone request, KHÔNG gửi lại cùng instance** — mỗi attempt `CloneRequest(method/uri/headers/version + body bytes đã buffer)`; body đọc 1 lần bằng `ReadAsByteArrayAsync` khi content thuộc nhóm replayable (`ByteArrayContent`/`StringContent`/`FormUrlEncodedContent` — cả 3 đều là/kế thừa `ByteArrayContent`, giữ tường minh theo spec §4.4). Content khác → gửi `original` đúng 1 lần, lỗi → ném ngay.
4. **`ProxyPool` lazy-reload**: `Invalidate()` chỉ set dirty; lần truy cập kế (dưới lock) mới đọc DB. **Giữ down-state** của proxy còn tồn tại qua reload (CRUD 1 proxy không reset cooldown proxy khác), cursor RR reset về 0 sau rebuild. Đọc DB lỗi → `_log.Error` + giữ list cũ + `_dirty=false` (lần `Invalidate` sau thử lại) — không nuốt im lặng.
5. **Invariant `PasswordEncrypted != null ⇒ Username != null`** ở service: create username trống → không lưu password; update username trống → xóa cả username + password (spec §3.2 — không để password mồ côi).
6. **Duplicate endpoint**: check trong service theo `Scheme == && Port == && Host.ToLower() == hostLower` (EF dịch `ToLower()` → SQLite `lower()`, host case-insensitive) — key `proxies.error.duplicate` gắn field `Host`. Unique index 3 cột trong DbContext là **backstop** (SQLite index không case-insensitive được).
7. **Migration test đặt ở `router balancing test/Storage/`** (theo pattern `AddProviderAccountsMigrationTests` — cùng họ hàng storage), không phải `Server/`.
8. **`ProxyEchoClient`** dùng `WebProxy(uri)` + `Credentials = NetworkCredential` tường minh (không userinfo — .NET 10 không parse); tạo `HttpClient`/`SocketsHttpHandler` riêng mỗi lần, timeout 10s, KHÔNG qua named client/pool, KHÔNG đụng health.
9. **Stub tests dùng destination `http://127.0.0.1`** (LocalHttpServer) → proxy thấy request absolute-URI, **không cần CONNECT tunnel** — đủ pin GetProxy-per-request/GetCredential/failover. Stub chỉ xử lý request GET không body (các kịch bản §8 chỉ cần GET; POST/replay-guard pin ở unit test Task 4).
10. **Wiring 2 named client thêm `ConnectTimeout = 10s`** cho `SocketsHttpHandler` — proxy chết fail nhanh (HttpRequestException inner TimeoutException → retryable) thay vì treo tới HttpClient timeout (TaskCanceledException → không failover). `upstream` đã có sẵn ConnectTimeout 10s (ProxyApp hiện tại).
11. **`ProxyService` ctor chuyển thẳng 4 tham số ở Task 7** (thêm `IProxyEchoClient`) và sửa chỗ construct trong `ProxyServiceTests` — không tạo overload 3 tham số song song (tránh 2 ctor 1 class cho 1 transition tạm).
12. **`TestAsync` bắt `HttpRequestException`/`TaskCanceledException`/`InvalidOperationException`/`CryptographicException` → persist `LastTest*`** (pattern `ProviderService.TestConnectionAsync`); thêm catch `OperationCanceledException` khi `ct.IsCancellationRequested` → **rethrow** (không nuốt hủy của caller). Timeout HttpClient (ct chưa hủy) rơi vào `TaskCanceledException` → fail có lý do ✓.
13. **Classifier `IsProxyConnectFailure`** = `HttpRequestException` VÀ (inner `SocketException` HOẶC inner `TimeoutException` HOẶC message chứa `"407"`). Nếu stub test Task 5/6 cho shape lỗi khác (SOCKS fail surface khác) → **mở rộng classifier + cập nhật unit test tương ứng** (vẫn trong intent spec §4.4 — failover phải chạy); nhưng nếu đỏ do **SOCKS handshake không gọi `GetCredential`** (risk §11.4) → **STOP, báo user chọn fallback** — không tự quyết (các phương án: chờ .NET 11 / credential tĩnh / drop auth SOCKS — mỗi phương án phá 1 yêu cầu).
14. **Không có key i18n cho cột Auth/`✓✗`** — hiển thị ký tự trực tiếp (`—`, `•••`, `✓ ip`, `✗ message`); tái dùng `providers.action.edit`/`providers.action.delete`/`confirm.cancel`/`settings.action.save`/`dashboard.msg.failed` (đã có sẵn, đúng giá trị "Edit"/"Delete"/"Cancel"/"Save"/failed).

---

### Task 0: Commit the plan

**Files:**
- Create: `docs/superpowers/plans/2026-10-01-proxy-pool.md` (file này)

**Interfaces:**
- Consumes: —
- Produces: plan file để SDD dispatch theo task number.

- [ ] **Step 1: Verify spec đã commit, tree chỉ có plan file**

Run:
```powershell
git log --oneline -1   # kỳ vọng: 0a58cc6 docs: amend proxy pool spec with verified .NET proxy internals
git status --short     # chỉ thấy ?? docs/superpowers/plans/2026-10-01-proxy-pool.md
```
Expected: HEAD = `0a58cc6`; chỉ có plan file mới.

- [ ] **Step 2: Commit**

```powershell
git add docs/superpowers/plans/2026-10-01-proxy-pool.md
git commit -m "docs: add outbound proxy pool implementation plan"
```

---

### Task 1: Entity `OutboundProxy` + DbContext + migration + migration test

**Files:**
- Create: `src/RouterBalancing.Core/Domain/Entities/OutboundProxy.cs`
- Modify: `src/RouterBalancing.Core/Storage/RouterBalancingDbContext.cs`
- Create (dotnet-ef): `src/RouterBalancing.Core/Storage/Migrations/<ts>_AddOutboundProxies.cs` + `.Designer.cs` + snapshot
- Create (test): `router balancing test/Storage/AddOutboundProxyMigrationTests.cs`

**Interfaces:**
- Consumes: —
- Produces (Task 2+): entity `OutboundProxy` (bảng `OutboundProxies`) + DbSet + index — `ProxyPool`/`ProxyService` đọc ghi bảng này.

- [ ] **Step 1: Tạo entity `OutboundProxy.cs`**

Tạo `src/RouterBalancing.Core/Domain/Entities/OutboundProxy.cs`:

```csharp
namespace RouterBalancing.Core.Domain;

/// <summary>
/// Proxy outbound toàn cục — mọi request tới provider đi qua pool (spec proxy-pool §3.1).
/// Tên đầy đủ (không phải <c>Proxy</c> thuần) để không trỏng với ProxyHost/ProxyApp/ProxyRequest.
/// Không persist runtime down-state — health sống in-memory ở <c>ProxyPool</c>.
/// </summary>
public class OutboundProxy
{
    public long Id { get; set; }

    /// <summary><c>"http"</c> hoặc <c>"socks5"</c> — validate ở <c>ProxyValidator</c>, luôn lưu lowercase.</summary>
    public string Scheme { get; set; } = "http";

    /// <summary>Hostname/IP của proxy — bắt buộc, không rỗng.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Cổng 1–65535.</summary>
    public int Port { get; set; }

    /// <summary>Tên đăng nhập proxy — null = không auth.</summary>
    public string? Username { get; set; }

    /// <summary>
    /// Password đã DPAPI qua <see cref="Security.ISecretProtector"/> (pattern ProviderAccount.ApiKeyEncrypted)
    /// — không bao giờ log/plaintext. Invariant: != null ⇒ <see cref="Username"/> != null.
    /// </summary>
    public string? PasswordEncrypted { get; set; }

    /// <summary>Tắt để giữ proxy mà không dùng trong pool — default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Kết quả test echo lần cuối — null = chưa test.</summary>
    public bool? LastTestSuccess { get; set; }

    public DateTimeOffset? LastTestAt { get; set; }

    /// <summary>Thông điệp kết quả/lỗi (không chứa credentials).</summary>
    public string? LastTestMessage { get; set; }

    /// <summary>IP egress echo trả về.</summary>
    public string? LastTestIp { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 2: Đăng ký vào `RouterBalancingDbContext`**

**(a)** Thêm DbSet — sau `public DbSet<ClientKey> ClientKeys => Set<ClientKey>();` (dòng ~22):

```csharp
    /// <summary>Bảng proxy outbound toàn cục — pool round-robin đọc khi Invalidate (spec proxy-pool §3.1).</summary>
    public DbSet<OutboundProxy> OutboundProxies => Set<OutboundProxy>();
```

**(b)** Trong `OnModelCreating` — thêm block cuối (sau `modelBuilder.Entity<ClientKey>`):

```csharp
        modelBuilder.Entity<OutboundProxy>(e =>
        {
            e.Property(x => x.Scheme).IsRequired().HasMaxLength(10);
            e.Property(x => x.Host).IsRequired().HasMaxLength(255);
            e.Property(x => x.Username).HasMaxLength(200);
            e.Property(x => x.LastTestMessage).HasMaxLength(500);
            e.Property(x => x.LastTestIp).HasMaxLength(64);
            // Backstop chống trùng endpoint — service check Host.ToLower() ordinal-ignore-case
            // là chủ yếu (SQLite unique index không case-insensitive được, xem Design decision 6)
            e.HasIndex(x => new { x.Scheme, x.Host, x.Port }).IsUnique();
        });
```

- [ ] **Step 3: Chạy migration**

```powershell
dotnet ef migrations add AddOutboundProxies --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations
```

Expected: file `<ts>_AddOutboundProxies.cs` với `Up()` = `CreateTable` bảng `OutboundProxies` đủ 13 cột (`Id` INTEGER PK AUTOINCREMENT, `Scheme`/`Host` TEXT NOT NULL, `Port` INTEGER NOT NULL, `Username`/`PasswordEncrypted`/`LastTestMessage`/`LastTestIp` TEXT NULL, `LastTestSuccess` INTEGER NULL, `LastTestAt` DATETIMEOFFSET TEXT NULL, `Enabled` INTEGER NOT NULL, `CreatedAt`/`UpdatedAt` DATETIMEOFFSET NOT NULL) + `CreateIndex` unique 3 cột; `Down()` = `DropTable` + `DropIndex`; snapshot cập nhật. **Ghi lại giá trị `<ts>` (vd `20261001143000`) để điền `TargetId` ở Step 4.**

- [ ] **Step 4: Migration test `AddOutboundProxyMigrationTests`**

Tạo `router balancing test/Storage/AddOutboundProxyMigrationTests.cs` — điền `TargetId` bằng `<ts>_AddOutboundProxies` vừa sinh ở Step 3:

```csharp
// router balancing test/Storage/AddOutboundProxyMigrationTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

/// <summary>
/// Round-trip schema AddOutboundProxies: Up tạo bảng OutboundProxies (spec proxy-pool §3.1),
/// Down thả bảng. Pattern AddProviderAccountsMigrationTests — migrate tới đúng migration
/// rồi mới seed, không Migrate() mặc định trên DB trống.
/// </summary>
public class AddOutboundProxyMigrationTests : IDisposable
{
    private const string PreviousId = "20261001121706_AddFreeProviderPresets";
    // Điền sau khi chạy dotnet ef (Task 1 Step 3) — dạng <ts>_AddOutboundProxies
    private const string TargetId = "REPLACE_WITH_GENERATED_TARGET_ID";
    private const string Timestamp = "'2026-10-01T00:00:00+00:00'";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void Up_CreatesOutboundProxiesTable_AcceptingFullRow()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(PreviousId);
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='OutboundProxies'")));

        db.GetService<IMigrator>().Migrate(TargetId);

        // Đủ 13 cột theo §3.1 — insert raw SQL với mọi cột NOT NULL
        db.Database.ExecuteSqlRaw($"""
            INSERT INTO OutboundProxies (Scheme, Host, Port, Username, PasswordEncrypted, Enabled,
                                         LastTestSuccess, LastTestAt, LastTestMessage, LastTestIp,
                                         CreatedAt, UpdatedAt)
            VALUES ('http', '127.0.0.1', 8080, 'u', 'enc', 1, NULL, NULL, NULL, NULL, {Timestamp}, {Timestamp});
            """);
        var row = db.OutboundProxies.Single();
        Assert.Equal("http", row.Scheme);
        Assert.Equal("127.0.0.1", row.Host);
        Assert.Equal(8080, row.Port);
        Assert.True(row.Enabled);
        // Unique index 3 cột tồn tại (backstop Design decision 6) —
        // HasIndex().IsUnique() sinh CREATE UNIQUE INDEX → origin='c'
        // ('u' chỉ dành cho UNIQUE table constraint), "unique"=1 xác nhận unique
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_index_list('OutboundProxies') "
            + "WHERE name='IX_OutboundProxies_Scheme_Host_Port' AND origin='c' AND \"unique\"=1")));
    }

    [Fact]
    public void Down_DropsOutboundProxiesTable()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(TargetId);
        db.Database.ExecuteSqlRaw($"""
            INSERT INTO OutboundProxies (Scheme, Host, Port, Username, PasswordEncrypted, Enabled,
                                         LastTestSuccess, LastTestAt, LastTestMessage, LastTestIp,
                                         CreatedAt, UpdatedAt)
            VALUES ('http', 'h', 1, NULL, NULL, 1, NULL, NULL, NULL, NULL, {Timestamp}, {Timestamp});
            """);

        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='OutboundProxies'")));
    }

    /// <summary>Scalar qua ADO thuần — tránh alias "Value" của EF SqlQuery và trần thuật ngữ EF.</summary>
    private object? Scalar(string sql)
    {
        // Pooling=False như TestDb: pool giữ handle file → Dispose không xóa được (IOException trên Windows)
        using var conn = new SqliteConnection($"Data Source={_testDb.DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
```

**Chú ý Step 4:** nếu unique index không xuất hiện trong migration (EF đặt tên `IX_OutboundProxies_Scheme_Host_Port`, assert `origin='c' AND "unique"=1` — `HasIndex().IsUnique()` sinh `CREATE UNIQUE INDEX`, không phải table constraint) → kiểm tra lại Step 2(b); `TargetId` sai format sẽ fail ngay khi `Migrate(TargetId)` ném "migration not found".

- [ ] **Step 5: Gates + Commit**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: build 0 lỗi; test **tất cả pass** (447 + 2 mới = 449). Flake đã biết → chạy lại.

```powershell
git add -A
git commit -m "feat: add outbound proxy entity and migration"
```

---

### Task 2: Contracts — pool interface + records + draft/validator + `ProxyService` (chưa `TestAsync`) + tests

**Files:**
- Create: `src/RouterBalancing.Core/Proxies/ProxyAttempt.cs`, `ProxyRuntimeStatus.cs`, `IProxyPool.cs`
- Create: `src/RouterBalancing.Core/Proxies/ProxyDraft.cs`, `ProxyValidator.cs`, `ProxyValidationException.cs`
- Create: `src/RouterBalancing.Core/Proxies/IProxyService.cs`, `ProxyService.cs`
- Create (test): `router balancing test/Proxies/ProxyServiceTests.cs`

**Interfaces:**
- Consumes: entity `OutboundProxy` + DbSet (Task 1), `ISecretProtector`, `IDbContextFactory`, pattern `ProviderService`/`ProviderValidator`/`ProviderValidationException`.
- Produces (Task 3+): `IProxyPool` (Task 3/4/8 cài/wiring), `ProxyAttempt`/`ProxyRuntimeStatus` (Task 3/4), `IProxyService`/`ProxyService` (Task 7 thêm `TestAsync`, Task 8 DI, Task 9 UI), `ProxyValidationException` (Task 9 UI catch).

- [ ] **Step 1: `ProxyAttempt` + `ProxyRuntimeStatus`**

Tạo `src/RouterBalancing.Core/Proxies/ProxyAttempt.cs`:

```csharp
namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Một proxy được chọn cho 1 attempt — sống trong memory, không bao giờ persist hay log <see cref="Password"/>.
/// <see cref="Uri"/> KHÔNG chứa userinfo (.NET 10 không parse — dotnet/runtime#125341);
/// auth đi qua <c>DynamicProxyCredentials</c> đọc <see cref="Username"/>/<see cref="Password"/> (spec §4.6).
/// </summary>
/// <param name="Id">Id hàng <c>OutboundProxy</c>.</param>
/// <param name="Uri">URI kết nối <c>scheme://host:port</c> — không userinfo.</param>
/// <param name="Username">Tên đăng nhập đã giải mã từ DB — null = không auth.</param>
/// <param name="Password">Password đã decrypt, chỉ sống trong memory — null = không auth.</param>
/// <param name="Endpoint">Display/log <c>scheme://host:port</c> — không chứa credentials.</param>
public sealed record ProxyAttempt(long Id, Uri Uri, string? Username, string? Password, string Endpoint);
```

Tạo `src/RouterBalancing.Core/Proxies/ProxyRuntimeStatus.cs`:

```csharp
namespace RouterBalancing.Core.Proxies;

/// <summary>Trạng thái runtime 1 proxy cho UI — chụp tại1 thời điểm, không tự refresh (spec §4.1).</summary>
/// <param name="Id">Id hàng <c>OutboundProxy</c>.</param>
/// <param name="Endpoint"><c>scheme://host:port</c>.</param>
/// <param name="IsDown">Đang trong cooldown do lỗi kết nối.</param>
/// <param name="DownUntil">Hết hạn cooldown này proxy quay lại round-robin (passive recover).</param>
public sealed record ProxyRuntimeStatus(long Id, string Endpoint, bool IsDown, DateTimeOffset? DownUntil);
```

- [ ] **Step 2: `IProxyPool`**

Tạo `src/RouterBalancing.Core/Proxies/IProxyPool.cs`:

```csharp
namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Pool proxy outbound singleton — round-robin qua proxy sống, health in-memory
/// (spec proxy-pool §4.1). Trả <see langword="null"/> từ <see cref="GetNext"/> = direct.
/// </summary>
public interface IProxyPool
{
    /// <summary>Chọn proxy kế theo round-robin trong số proxy sống (Enabled và không trong cooldown).</summary>
    ProxyAttempt? GetNext();

    /// <summary>
    /// Ghi nhận lỗi kết nối tới proxy — đánh down + cooldown 60s.
    /// Trả <see langword="true"/> khi vừa chuyển sống→down (log Warn duy nhất lần đầu);
    /// proxy đã down = no-op, trả <see langword="false"/>.
    /// </summary>
    bool ReportFailure(long proxyId);

    /// <summary>Ghi nhận thành công — reset trạng thái failure của proxy.</summary>
    void ReportSuccess(long proxyId);

    /// <summary>Bắn lại snapshot từ DB — service CRUD gọi sau mọi thao tác.</summary>
    void Invalidate();

    /// <summary>Trạng thái runtime hiện tại cho UI (danh sách proxy Enabled).</summary>
    IReadOnlyList<ProxyRuntimeStatus> Snapshot();
}
```

- [ ] **Step 3: `ProxyDraft` + `ProxyValidator` + `ProxyValidationException`**

Tạo `src/RouterBalancing.Core/Proxies/ProxyDraft.cs`:

```csharp
namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Bản nháp form proxy. Property mutable (không phải positional record) để Blazor <c>@bind</c>
/// ghi được — giống <c>ProviderDraft</c>.
/// </summary>
public sealed record ProxyDraft
{
    public string Scheme { get; set; } = "http";

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    /// <summary>Trống = không có username; khi update để trống ⇒ xóa toàn bộ auth (spec §3.2).</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// <see langword="null"/> = giữ password đã lưu (update) / không đặt (create);
    /// trống khi save cũng được coi là null (UI gán trước khi gọi service).
    /// </summary>
    public string? Password { get; set; }
}
```

Tạo `src/RouterBalancing.Core/Proxies/ProxyValidator.cs`:

```csharp
namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Validate bản nháp form proxy (không cần DB — duplicate ở service).
/// Trả key lỗi i18n (không text literal) — pattern <c>ProviderValidator</c>.
/// </summary>
public static class ProxyValidator
{
    /// <summary>Kiểm tra toàn bộ rule; dictionary rỗng = hợp lệ.</summary>
    public static IReadOnlyDictionary<string, string> Validate(ProxyDraft draft)
    {
        var errors = new Dictionary<string, string>();

        if (draft.Scheme?.Trim().ToLowerInvariant() is not ("http" or "socks5"))
        {
            errors[nameof(ProxyDraft.Scheme)] = "proxies.error.scheme";
        }

        if (string.IsNullOrWhiteSpace(draft.Host))
        {
            errors[nameof(ProxyDraft.Host)] = "proxies.error.host";
        }

        if (draft.Port is < 1 or > 65535)
        {
            errors[nameof(ProxyDraft.Port)] = "proxies.error.port";
        }

        return errors;
    }
}
```

Tạo `src/RouterBalancing.Core/Proxies/ProxyValidationException.cs`:

```csharp
namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Lỗi business khi tạo/cập nhật proxy (duplicate endpoint…) — mang dict key i18n cùng format
/// <see cref="ProxyValidator.Validate"/> để UI hiển thị field-level (pattern ProviderValidationException).
/// </summary>
public sealed class ProxyValidationException(IReadOnlyDictionary<string, string> errors)
    : Exception("Proxy validation failed.")
{
    /// <summary>Field name (nameof property) → i18n key lỗi.</summary>
    public IReadOnlyDictionary<string, string> Errors { get; } = errors;
}
```

- [ ] **Step 4: `IProxyService` + `ProxyService`**

Tạo `src/RouterBalancing.Core/Proxies/IProxyService.cs`:

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Proxies;

/// <summary>CRUD proxy outbound — mọi thao tác gọi <c>IProxyPool.Invalidate</c> (spec proxy-pool §5).</summary>
public interface IProxyService
{
    /// <summary>Tất cả proxy theo Id — UI tự lọc Enabled; không trả password plaintext.</summary>
    Task<IReadOnlyList<OutboundProxy>> ListAsync(CancellationToken ct = default);

    /// <summary>Tạo proxy mới — encrypt password, invalidate pool.</summary>
    /// <exception cref="ProxyValidationException">Scheme/host/port không hợp lệ hoặc endpoint trùng.</exception>
    Task<OutboundProxy> CreateAsync(ProxyDraft draft, CancellationToken ct = default);

    /// <summary>
    /// Cập nhật proxy — <c>Password = null</c> giữ password cũ; username trống xóa toàn bộ auth.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Không có proxy với <paramref name="id"/>.</exception>
    /// <exception cref="ProxyValidationException">Rule validate/duplicate fail.</exception>
    Task<OutboundProxy> UpdateAsync(long id, ProxyDraft draft, CancellationToken ct = default);

    /// <exception cref="KeyNotFoundException">Không có proxy với <paramref name="id"/>.</exception>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <exception cref="KeyNotFoundException">Không có proxy với <paramref name="id"/>.</exception>
    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);
}
```

Tạo `src/RouterBalancing.Core/Proxies/ProxyService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Proxies;

/// <inheritdoc cref="IProxyService"/>
public sealed class ProxyService : IProxyService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly IProxyPool _pool;

    /// <inheritdoc/>
    public ProxyService(IDbContextFactory<RouterBalancingDbContext> db, ISecretProtector protector, IProxyPool pool)
    {
        _db = db;
        _protector = protector;
        _pool = pool;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OutboundProxy>> ListAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.OutboundProxies.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<OutboundProxy> CreateAsync(ProxyDraft draft, CancellationToken ct = default)
    {
        var (scheme, host, port, username) = Validate(draft);
        using var db = _db.CreateDbContext();
        await EnsureEndpointUsableAsync(db, scheme, host, port, excludeId: null, ct);

        var proxy = new OutboundProxy
        {
            Scheme = scheme,
            Host = host,
            Port = port,
            Username = username,
            // Invariant Design decision 5: username trống → không lưu password
            PasswordEncrypted = username is null || draft.Password is null
                ? null
                : _protector.Protect(draft.Password),
        };

        db.OutboundProxies.Add(proxy);
        await db.SaveChangesAsync(ct);
        _pool.Invalidate();
        return proxy;
    }

    /// <inheritdoc/>
    public async Task<OutboundProxy> UpdateAsync(long id, ProxyDraft draft, CancellationToken ct = default)
    {
        var (scheme, host, port, username) = Validate(draft);
        using var db = _db.CreateDbContext();
        var proxy = await db.OutboundProxies.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Proxy {id} not found.");
        await EnsureEndpointUsableAsync(db, scheme, host, port, excludeId: id, ct);

        proxy.Scheme = scheme;
        proxy.Host = host;
        proxy.Port = port;
        if (username is null)
        {
            // Username trống ⇒ xóa toàn bộ auth — không để password mồ côi (spec §3.2)
            proxy.Username = null;
            proxy.PasswordEncrypted = null;
        }
        else
        {
            proxy.Username = username;
            // Password null ⇒ giữ password đã encrypt (spec §3.2)
            if (draft.Password is not null)
            {
                proxy.PasswordEncrypted = _protector.Protect(draft.Password);
            }
        }

        proxy.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _pool.Invalidate();
        return proxy;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var proxy = await db.OutboundProxies.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Proxy {id} not found.");

        db.OutboundProxies.Remove(proxy);
        await db.SaveChangesAsync(ct);
        _pool.Invalidate();
    }

    /// <inheritdoc/>
    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var proxy = await db.OutboundProxies.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Proxy {id} not found.");

        proxy.Enabled = enabled;
        proxy.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _pool.Invalidate();
    }

    /// <summary>
    /// Validate draft thuần (không DB) + chuẩn hóa (scheme lowercase, trim host/username) —
    /// ném ProxyValidationException với dict key i18n.
    /// </summary>
    private static (string Scheme, string Host, int Port, string? Username) Validate(ProxyDraft draft)
    {
        var errors = ProxyValidator.Validate(draft);
        if (errors.Count > 0)
        {
            throw new ProxyValidationException(errors);
        }

        var username = string.IsNullOrWhiteSpace(draft.Username) ? null : draft.Username.Trim();
        return (draft.Scheme.Trim().ToLowerInvariant(), draft.Host.Trim(), draft.Port, username);
    }

    /// <summary>
    /// Unique endpoint theo <c>scheme://host:port</c>, host case-insensitive (Design decision 6) —
    /// cần DB nên tách khỏi ProxyValidator; ném ProxyValidationException gắn lỗi vào field Host.
    /// </summary>
    private static async Task EnsureEndpointUsableAsync(
        RouterBalancingDbContext db, string scheme, string host, int port, long? excludeId, CancellationToken ct)
    {
        // EF dịch ToLower() → SQLite lower() — so sánh được server-side
        var hostLower = host.ToLowerInvariant();
        var duplicate = await db.OutboundProxies.AsNoTracking()
            .AnyAsync(p => p.Scheme == scheme && p.Port == port && p.Host.ToLower() == hostLower
                && (excludeId == null || p.Id != excludeId), ct);
        if (duplicate)
        {
            throw new ProxyValidationException(new Dictionary<string, string>
            {
                [nameof(ProxyDraft.Host)] = "proxies.error.duplicate",
            });
        }
    }
}
```

- [ ] **Step 5: Test `ProxyServiceTests`**

Tạo `router balancing test/Proxies/ProxyServiceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Proxies;

public class ProxyServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly FakePool _pool = new();

    public ProxyServiceTests()
    {
        _factory = _db.CreateFactory();
        DbInitializer.Initialize(_factory);
    }

    public void Dispose() => _db.Dispose();

    private ProxyService CreateService() => new(_factory, _protector, _pool);

    private static ProxyDraft Draft(
        string scheme = "http", string host = "127.0.0.1", int port = 8080,
        string username = "", string? password = null) =>
        new() { Scheme = scheme, Host = host, Port = port, Username = username, Password = password };

    [Fact]
    public async Task CreateAsync_InvalidScheme_ThrowsValidationError()
    {
        var ex = await Assert.ThrowsAsync<ProxyValidationException>(
            () => CreateService().CreateAsync(Draft(scheme: "ftp")));

        Assert.Equal("proxies.error.scheme", ex.Errors[nameof(ProxyDraft.Scheme)]);
    }

    [Fact]
    public async Task CreateAsync_HostBlank_ThrowsValidationError()
    {
        var ex = await Assert.ThrowsAsync<ProxyValidationException>(
            () => CreateService().CreateAsync(Draft(host: "  ")));

        Assert.Equal("proxies.error.host", ex.Errors[nameof(ProxyDraft.Host)]);
    }

    [Fact]
    public async Task CreateAsync_PortOutOfRange_ThrowsValidationError()
    {
        var ex = await Assert.ThrowsAsync<ProxyValidationException>(
            () => CreateService().CreateAsync(Draft(port: 70000)));

        Assert.Equal("proxies.error.port", ex.Errors[nameof(ProxyDraft.Port)]);
    }

    [Fact]
    public async Task CreateAsync_DuplicateEndpoint_CaseInsensitiveHost_Throws()
    {
        var service = CreateService();
        await service.CreateAsync(Draft(host: "LocalHost", port: 9000));

        // Host hoa/thường không phân biệt (Design decision 6)
        var ex = await Assert.ThrowsAsync<ProxyValidationException>(
            () => service.CreateAsync(Draft(host: "localhost", port: 9000)));

        Assert.Equal("proxies.error.duplicate", ex.Errors[nameof(ProxyDraft.Host)]);
        // Khác scheme vẫn tạo được
        await service.CreateAsync(Draft(scheme: "socks5", host: "localhost", port: 9000));
    }

    [Fact]
    public async Task CreateAsync_WithCredentials_EncryptsPasswordAtRest()
    {
        var created = await CreateService().CreateAsync(Draft(username: "user", password: "secret"));

        using var db = _factory.CreateDbContext();
        var raw = await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.NotNull(raw.PasswordEncrypted);
        Assert.NotEqual("secret", raw.PasswordEncrypted); // không plaintext trong DB
        Assert.Equal("secret", _protector.Unprotect(raw.PasswordEncrypted!)); // decrypt đúng
    }

    [Fact]
    public async Task CreateAsync_UsernameBlank_DropsPassword()
    {
        var created = await CreateService().CreateAsync(Draft(username: "", password: "orphan"));

        using var db = _factory.CreateDbContext();
        var raw = await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        // Invariant Design decision 5 — không password mồ côi không username
        Assert.Null(raw.Username);
        Assert.Null(raw.PasswordEncrypted);
    }

    [Fact]
    public async Task UpdateAsync_PasswordNull_KeepsExistingPassword()
    {
        var service = CreateService();
        var created = await service.CreateAsync(Draft(username: "user", password: "old-pass"));

        await service.UpdateAsync(created.Id,
            Draft(host: "10.0.0.2", port: 3128, username: "user", password: null));

        using var db = _factory.CreateDbContext();
        var raw = await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Equal("old-pass", _protector.Unprotect(raw.PasswordEncrypted!));
        Assert.Equal("10.0.0.2", raw.Host);
    }

    [Fact]
    public async Task UpdateAsync_UsernameBlank_ClearsAuth()
    {
        var service = CreateService();
        var created = await service.CreateAsync(Draft(username: "user", password: "old-pass"));

        await service.UpdateAsync(created.Id, Draft(username: "", password: "ignored"));

        using var db = _factory.CreateDbContext();
        var raw = await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id);
        Assert.Null(raw.Username);
        Assert.Null(raw.PasswordEncrypted); // username rỗng thắng — xóa cả cặp (spec §3.2)
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_ThrowsKeyNotFound()
        => await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateService().UpdateAsync(999_999, Draft()));

    [Fact]
    public async Task DeleteAsync_UnknownId_ThrowsKeyNotFound()
        => await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateService().DeleteAsync(999_999));

    [Fact]
    public async Task SetEnabledAsync_TogglesRow()
    {
        var service = CreateService();
        var created = await service.CreateAsync(Draft());

        await service.SetEnabledAsync(created.Id, enabled: false);

        using var db = _factory.CreateDbContext();
        Assert.False((await db.OutboundProxies.AsNoTracking().SingleAsync(p => p.Id == created.Id)).Enabled);
    }

    [Fact]
    public async Task EveryCrudOperation_InvalidatesPool()
    {
        var service = CreateService();
        var created = await service.CreateAsync(Draft(port: 9001));

        await service.UpdateAsync(created.Id, Draft(port: 9001, host: "127.0.0.1"));
        await service.SetEnabledAsync(created.Id, false);
        await service.DeleteAsync(created.Id);

        Assert.Equal(4, _pool.Invalidations); // Create + Update + SetEnabled + Delete
    }

    [Fact]
    public async Task ListAsync_ReturnsRowsOrderedById()
    {
        var service = CreateService();
        await service.CreateAsync(Draft(port: 9002));
        await service.CreateAsync(Draft(port: 9003));

        var list = await service.ListAsync();

        Assert.Equal(2, list.Count);
        Assert.True(list[0].Id < list[1].Id);
    }

    /// <summary>IProxyPool ghi nhận Invalidate — health methods không dùng ở service test này.</summary>
    private sealed class FakePool : IProxyPool
    {
        public int Invalidations;

        public ProxyAttempt? GetNext() => null;

        public bool ReportFailure(long proxyId) => false;

        public void ReportSuccess(long proxyId)
        {
        }

        public void Invalidate() => Invalidations++;

        public IReadOnlyList<ProxyRuntimeStatus> Snapshot() => [];
    }
}
```

- [ ] **Step 6: Gates + Commit**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: 0 lỗi; toàn bộ pass (449 + 13 = 462).

```powershell
git add -A
git commit -m "feat: add proxy pool contracts and proxy crud service"
```

---

### Task 3: `ProxyPool` — RR + cooldown + load DB + tests

**Files:**
- Create: `src/RouterBalancing.Core/Proxies/ProxyPool.cs`
- Create (test): `router balancing test/Proxies/ProxyPoolTests.cs`

**Interfaces:**
- Consumes: `IProxyPool`/`ProxyAttempt`/`ProxyRuntimeStatus` (Task 2), entity `OutboundProxy` + DbSet (Task 1), `ISecretProtector`, `TimeProvider` (DI đăng ký ở Task 8), `ILogService`.
- Produces (Task 4/5/6/8): singleton `ProxyPool` — `ProxyHealthHandler`/stub tests gọi `GetNext`/`Report*`; MauiProgram đăng ký `IProxyPool` ở Task 8.

- [ ] **Step 1: `ProxyPool.cs`**

Tạo `src/RouterBalancing.Core/Proxies/ProxyPool.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Proxies;

/// <inheritdoc cref="IProxyPool"/>
public sealed class ProxyPool : IProxyPool
{
    /// <summary>Cooldown sau lỗi kết nối — passive recover, không probe riêng (spec §4.1).</summary>
    public static readonly TimeSpan DownCooldown = TimeSpan.FromSeconds(60);

    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _time;
    private readonly ILogService _log;
    private readonly object _gate = new();

    // ===== State dưới lock — 1 list entries + down-state + cursor RR =====
    private List<ProxyAttempt> _entries = [];
    private Dictionary<long, DateTimeOffset> _downUntil = [];
    private int _cursor;
    private bool _dirty = true;

    /// <inheritdoc/>
    public ProxyPool(IDbContextFactory<RouterBalancingDbContext> db, ISecretProtector protector,
        TimeProvider time, ILogService log)
    {
        _db = db;
        _protector = protector;
        _time = time;
        _log = log;
    }

    /// <inheritdoc/>
    public ProxyAttempt? GetNext()
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_entries.Count == 0)
            {
                return null; // pool rỗng → direct
            }

            var now = _time.GetUtcNow();
            for (var step = 0; step < _entries.Count; step++)
            {
                var index = (_cursor + step) % _entries.Count;
                if (IsDown(index, now))
                {
                    continue;
                }

                _cursor = (index + 1) % _entries.Count;
                return _entries[index];
            }
            return null; // tất cả down → direct
        }
    }

    /// <inheritdoc/>
    public bool ReportFailure(long proxyId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!_entries.Any(e => e.Id == proxyId))
            {
                return false;
            }

            var now = _time.GetUtcNow();
            // Đã down = no-op: nhiều request fail song song không đụng cooldown của nhau,
            // không gia hạn, không trả true → handler không log lặp (spec §4.1)
            if (_downUntil.TryGetValue(proxyId, out var until) && until > now)
            {
                return false;
            }

            _downUntil[proxyId] = now + DownCooldown;
            return true;
        }
    }

    /// <inheritdoc/>
    public void ReportSuccess(long proxyId)
    {
        lock (_gate)
        {
            EnsureLoaded();
            _downUntil.Remove(proxyId);
        }
    }

    /// <inheritdoc/>
    public void Invalidate()
    {
        // Chỉ set dirty — reload ở lần truy cập kế (dưới lock) để CRUD không block request
        // đang chạy; xem Design decision 4.
        lock (_gate)
        {
            _dirty = true;
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProxyRuntimeStatus> Snapshot()
    {
        lock (_gate)
        {
            EnsureLoaded();
            var now = _time.GetUtcNow();
            var result = new List<ProxyRuntimeStatus>(_entries.Count);
            foreach (var entry in _entries)
            {
                var down = _downUntil.TryGetValue(entry.Id, out var until) && until > now;
                result.Add(new ProxyRuntimeStatus(entry.Id, entry.Endpoint, down, down ? until : null));
            }
            return result;
        }
    }

    private bool IsDown(int index, DateTimeOffset now) =>
        _downUntil.TryGetValue(_entries[index].Id, out var until) && until > now;

    private void EnsureLoaded()
    {
        if (_dirty)
        {
            Reload();
        }
    }

    /// <summary>
    /// Nạp lại list Enabled từ DB, decrypt password vào <see cref="ProxyAttempt"/>.
    /// Giữ down-state của proxy còn tồn tại (CRUD 1 proxy không reset cooldown proxy khác —
    /// Design decision 4); lỗi đọc DB → log Error + giữ list cũ (traffic vẫn chạy).
    /// </summary>
    private void Reload()
    {
        List<ProxyAttempt> loaded;
        try
        {
            using var db = _db.CreateDbContext();
            var rows = db.OutboundProxies.AsNoTracking()
                .Where(p => p.Enabled)
                .OrderBy(p => p.Id)
                .ToList();
            loaded = rows.Select(row => new ProxyAttempt(
                row.Id,
                // Uri không userinfo — .NET 10 không parse (dotnet/runtime#125341)
                new Uri($"{row.Scheme}://{row.Host}:{row.Port}"),
                row.Username,
                row.PasswordEncrypted is null ? null : _protector.Unprotect(row.PasswordEncrypted),
                $"{row.Scheme}://{row.Host}:{row.Port}")).ToList();
        }
        catch (Exception ex)
        {
            // Không nuốt: log + giữ list cũ để request vẫn đi được; không để dirty lặp —
            // lần Invalidate kế (CRUD kế) sẽ thử đọc DB lại
            _log.Error("Không nạp được danh sách proxy từ DB.", ex);
            _dirty = false;
            return;
        }

        var ids = loaded.Select(e => e.Id).ToHashSet();
        _downUntil = _downUntil.Where(kv => ids.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        _entries = loaded;
        _cursor = 0;
        _dirty = false;
    }
}
```

- [ ] **Step 2: Test `ProxyPoolTests`**

Tạo `router balancing test/Proxies/ProxyPoolTests.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Proxies;

public class ProxyPoolTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public ProxyPoolTests()
    {
        _factory = _db.CreateFactory();
        DbInitializer.Initialize(_factory);
    }

    public void Dispose() => _db.Dispose();

    private async Task<long> AddProxyAsync(
        string host = "127.0.0.1", int port = 8080, string scheme = "http",
        bool enabled = true, string? username = null, string? password = null)
    {
        using var db = _factory.CreateDbContext();
        var row = new OutboundProxy
        {
            Scheme = scheme,
            Host = host,
            Port = port,
            Enabled = enabled,
            Username = username,
            PasswordEncrypted = password is null ? null : _protector.Protect(password),
        };
        db.OutboundProxies.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private ProxyPool CreatePool(TimeProvider? time = null) =>
        new(_factory, _protector, time ?? TimeProvider.System, new NullLog());

    [Fact]
    public async Task GetNext_TwoProxies_RoundRobinsInIdOrder()
    {
        await AddProxyAsync(port: 8001);
        await AddProxyAsync(port: 8002);
        var pool = CreatePool();

        var first = pool.GetNext();
        var second = pool.GetNext();
        var third = pool.GetNext();

        Assert.Equal("http://127.0.0.1:8001", first!.Endpoint);
        Assert.Equal("http://127.0.0.1:8002", second!.Endpoint);
        Assert.Equal("http://127.0.0.1:8001", third!.Endpoint);
    }

    [Fact]
    public async Task GetNext_SkipsDisabledProxy()
    {
        await AddProxyAsync(port: 8001, enabled: false);
        await AddProxyAsync(port: 8002);
        var pool = CreatePool();

        Assert.Equal("http://127.0.0.1:8002", pool.GetNext()!.Endpoint);
        Assert.Equal("http://127.0.0.1:8002", pool.GetNext()!.Endpoint); // chỉ 1 proxy sống
    }

    [Fact]
    public void GetNext_EmptyPool_ReturnsNull() => Assert.Null(CreatePool().GetNext());

    [Fact]
    public async Task GetNext_AllProxiesDown_ReturnsNull()
    {
        var id = await AddProxyAsync();
        var pool = CreatePool();

        Assert.True(pool.ReportFailure(id));
        Assert.Null(pool.GetNext());
    }

    [Fact]
    public async Task GetNext_DownProxy_RecoversAfterCooldown()
    {
        var id = await AddProxyAsync();
        var time = new FakeTime(Start);
        var pool = CreatePool(time);
        pool.ReportFailure(id);

        Assert.Null(pool.GetNext());
        time.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(pool.GetNext()); // còn 1s cooldown

        time.Advance(TimeSpan.FromSeconds(2)); // tổng 61s — passive recover, không cần ReportSuccess
        Assert.NotNull(pool.GetNext());
    }

    [Fact]
    public async Task ReportFailure_FirstTime_ReturnsTrueAndShowsInSnapshot()
    {
        var id = await AddProxyAsync();
        var pool = CreatePool();

        Assert.True(pool.ReportFailure(id));
        var status = Assert.Single(pool.Snapshot());
        Assert.True(status.IsDown);
        Assert.Equal(id, status.Id);
        Assert.NotNull(status.DownUntil);
    }

    [Fact]
    public async Task ReportFailure_AlreadyDown_ReturnsFalseAndKeepsOriginalCooldown()
    {
        var id = await AddProxyAsync();
        var time = new FakeTime(Start);
        var pool = CreatePool(time);
        pool.ReportFailure(id); // down tới Start+60s

        time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(pool.ReportFailure(id)); // no-op — không gia hạn cooldown

        time.Advance(TimeSpan.FromSeconds(31)); // tổng 61s > cooldown gốc 60s
        Assert.NotNull(pool.GetNext()); // nếu bị gia hạn thì giờ này vẫn down
    }

    [Fact]
    public async Task ReportSuccess_ClearsDownState()
    {
        var id = await AddProxyAsync();
        var pool = CreatePool();
        pool.ReportFailure(id);

        pool.ReportSuccess(id);

        Assert.NotNull(pool.GetNext());
        Assert.False(pool.Snapshot().Single().IsDown);
    }

    [Fact]
    public async Task Invalidate_PicksUpNewProxy()
    {
        var pool = CreatePool();
        Assert.Null(pool.GetNext()); // pool rỗng

        await AddProxyAsync(port: 8010);
        pool.Invalidate();

        Assert.Equal("http://127.0.0.1:8010", pool.GetNext()!.Endpoint);
    }

    [Fact]
    public async Task Invalidate_PreservesDownStateOfExistingProxies()
    {
        var downId = await AddProxyAsync(port: 8021);
        await AddProxyAsync(port: 8022);
        var pool = CreatePool();
        pool.ReportFailure(downId);

        await AddProxyAsync(port: 8023);
        pool.Invalidate(); // reload — down-state của proxy cũ phải giữ (Design decision 4)

        var picks = new HashSet<string>();
        for (var i = 0; i < 2; i++)
        {
            picks.Add(pool.GetNext()!.Endpoint);
        }
        Assert.DoesNotContain("http://127.0.0.1:8021", picks);
        Assert.Contains("http://127.0.0.1:8023", picks); // proxy mới vào pool
    }

    [Fact]
    public async Task GetNext_DecryptsPassword_AndUriHasNoUserinfo()
    {
        await AddProxyAsync(username: "user", password: "secret");
        var pool = CreatePool();

        var attempt = pool.GetNext()!;

        Assert.Equal("user", attempt.Username);
        Assert.Equal("secret", attempt.Password); // đã decrypt trong memory
        Assert.Equal(string.Empty, attempt.Uri.UserInfo); // không userinfo trong Uri
        Assert.Equal("http://127.0.0.1:8080", attempt.Endpoint); // display không credentials
    }

    [Fact]
    public async Task GetNext_ParallelCalls_AreThreadSafe()
    {
        await AddProxyAsync(port: 8031);
        await AddProxyAsync(port: 8032);
        var pool = CreatePool();
        var results = new ConcurrentBag<ProxyAttempt?>();

        Parallel.For(0, 200, _ => results.Add(pool.GetNext()));

        Assert.Equal(200, results.Count);
        Assert.All(results, r => Assert.NotNull(r));
    }

    /// <summary>Clock fake theo pattern ClientKeyRateLimiterTests.FakeTime.</summary>
    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }
}
```

- [ ] **Step 3: Gates + Commit**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: 0 lỗi; toàn bộ pass (462 + 12 = 474).

```powershell
git add -A
git commit -m "feat: add round robin proxy pool with cooldown health"
```

---

### Task 4: `ProxyContext` + `DynamicProxyCredentials` + `RoundRobinWebProxy` + `ProxyHealthHandler` + unit tests

**Files:**
- Create: `src/RouterBalancing.Core/Proxies/ProxyContext.cs`, `DynamicProxyCredentials.cs`, `RoundRobinWebProxy.cs`, `ProxyHealthHandler.cs`
- Create (test): `router balancing test/Proxies/RoundRobinWebProxyTests.cs`, `router balancing test/Proxies/ProxyHealthHandlerTests.cs`

**Interfaces:**
- Consumes: `IProxyPool`/`ProxyAttempt` (Task 2), `ILogService`, spec §4.2–4.4 + §7.
- Produces (Task 5/6/8): `ProxyHealthHandler` + `RoundRobinWebProxy` ghép thành pipeline trong stub tests (Task 5/6) và DI wiring (Task 8).

- [ ] **Step 1: `ProxyContext.cs`**

Tạo `src/RouterBalancing.Core/Proxies/ProxyContext.cs`:

```csharp
namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Correlation giữa pick của <see cref="ProxyHealthHandler"/> và
/// <see cref="RoundRobinWebProxy.GetProxy"/> — <c>IWebProxy.GetProxy(Uri)</c> chỉ nhận
/// destination nên dùng <see cref="AsyncLocal{T}"/> để biết request nào đang gọi (spec §4.2).
/// Không có scope (không qua handler) → bypass (direct) — deterministic, không RR ngầm.
/// </summary>
public static class ProxyContext
{
    private static readonly AsyncLocal<ProxyAttempt?> s_current = new();

    /// <summary>Proxy đang phục vụ request hiện tại — <see langword="null"/> = direct.</summary>
    public static ProxyAttempt? Current
    {
        get => s_current.Value;
        set => s_current.Value = value;
    }
}
```

- [ ] **Step 2: `DynamicProxyCredentials.cs`**

Tạo `src/RouterBalancing.Core/Proxies/DynamicProxyCredentials.cs`:

```csharp
using System.Net;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Credentials động theo request: .NET 10 KHÔNG parse userinfo trong proxy URI
/// (dotnet/runtime#125341, fix vào .NET 11) nên auth phải đi qua
/// <see cref="IWebProxy.Credentials"/>. Phục vụ cả HTTP 407-challenge lẫn SOCKS5 handshake
/// — verify bằng stub tests (spec §4.6, risk §11.4).
/// </summary>
public sealed class DynamicProxyCredentials : ICredentials
{
    /// <summary>
    /// 1 instance ổn định — <c>SocketsHttpHandler</c> đọc <c>Proxy.Credentials</c> đúng 1 lần
    /// lúc construct, getter không được evaluate pick tại đó.
    /// </summary>
    public static DynamicProxyCredentials Instance { get; } = new();

    private DynamicProxyCredentials()
    {
    }

    /// <inheritdoc/>
    public NetworkCredential? GetCredential(Uri? uri, string authType) =>
        ProxyContext.Current is { Username: { Length: > 0 } username } attempt
            ? new NetworkCredential(username, attempt.Password ?? string.Empty)
            : null;
}
```

- [ ] **Step 3: `RoundRobinWebProxy.cs`**

Tạo `src/RouterBalancing.Core/Proxies/RoundRobinWebProxy.cs`:

```csharp
using System.Net;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// <see cref="IWebProxy"/> đọc <see cref="ProxyContext"/> — proxy nào dùng cho request do
/// <see cref="ProxyHealthHandler"/> quyết định; không có scope = bypass (direct) (spec §4.3).
/// Kết nối tới cùng proxy tái sử dụng nhờ connection grouping per-proxy có sẵn của SocketsHttpHandler.
/// </summary>
public sealed class RoundRobinWebProxy : IWebProxy
{
    /// <inheritdoc/>
    public ICredentials? Credentials => DynamicProxyCredentials.Instance;

    /// <inheritdoc/>
    public Uri? GetProxy(Uri destination) => ProxyContext.Current?.Uri;

    /// <inheritdoc/>
    public bool IsBypassed(Uri host) => ProxyContext.Current is null;
}
```

- [ ] **Step 4: `ProxyHealthHandler.cs`**

Tạo `src/RouterBalancing.Core/Proxies/ProxyHealthHandler.cs`:

```csharp
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Failover trong cùng request (spec proxy-pool §4.4): pick proxy round-robin, lỗi
/// connect-phase (HttpRequestException + inner socket/timeout/message "407") hoặc response
/// 407 → đánh down + log Warn (chỉ lần sống→down) + quay vòng proxy kế; hết proxy sống →
/// attempt direct cuối; lỗi khác → ném nguyên, không wrap.
/// </summary>
public sealed class ProxyHealthHandler : DelegatingHandler
{
    private readonly IProxyPool _pool;
    private readonly ILogService _log;

    /// <summary>Handler theo DI của HttpClientFactory — transient, InnerHandler do factory gán.</summary>
    public ProxyHealthHandler(IProxyPool pool, ILogService log)
    {
        _pool = pool;
        _log = log;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Budget = số proxy sống lúc bắt đầu request, tối thiểu 1 — tránh race budget=0
        // nhưng GetNext vẫn trả proxy (CRUD thêm giữa chừng) khiến throw null (Design decision 2)
        var budget = Math.Max(1, _pool.Snapshot().Count(s => !s.IsDown));

        // Body buffer 1 lần cho mọi attempt — content không replay được thì gửi original 1 lần
        var replayable = request.Content is null
            || request.Content is ByteArrayContent
            || request.Content is StringContent
            || request.Content is FormUrlEncodedContent;
        byte[]? body = request.Content is not null && replayable
            ? await request.Content.ReadAsByteArrayAsync(cancellationToken)
            : null;

        Exception? lastFailure = null;
        var proxyAttempts = 0;

        while (true)
        {
            var pick = _pool.GetNext();
            if (pick is null)
            {
                // Pool rỗng/tất cả down — attempt direct cuối, không report health
                // (không dính proxy nào). Direct lỗi → tự ném ra caller.
                return await SendOnceAsync(request, body, replayable, cancellationToken);
            }

            if (proxyAttempts >= budget && lastFailure is not null)
            {
                // Hết lượt retry mà vẫn còn proxy sống — ném lại lỗi cuối, không wrap (§4.4)
                ExceptionDispatchInfo.Capture(lastFailure).Throw();
            }
            proxyAttempts++;

            ProxyContext.Current = pick;
            try
            {
                var response = await SendOnceAsync(request, body, replayable, cancellationToken);

                if ((int)response.StatusCode == 407)
                {
                    // Chỉ proxy sinh được 407 (auth sai/không có) — coi là proxy fail (§4.4 bước 4)
                    response.Dispose();
                    lastFailure = new HttpRequestException(
                        "The proxy server returned HTTP 407 (Proxy Authentication Required).");
                    if (_pool.ReportFailure(pick.Id))
                    {
                        LogDown(pick, lastFailure);
                    }
                    continue;
                }

                // Có response từ upstream (kể cả 5xx — 502 sinh tại proxy vẫn tính proxy sống)
                _pool.ReportSuccess(pick.Id);
                return response;
            }
            catch (Exception ex) when (IsProxyConnectFailure(ex))
            {
                lastFailure = ex;
                if (_pool.ReportFailure(pick.Id))
                {
                    LogDown(pick, ex);
                }

                if (!replayable)
                {
                    throw; // content không buffer được → không retry, ném ngay (§4.4 replay guard)
                }
                // Quay vòng — pick kế tự skip proxy vừa down; hết proxy sống → nhánh direct
            }
            finally
            {
                // Reset trước khi rời attempt — response đã nhận xong, kết nối đã mở sẵn
                ProxyContext.Current = null;
            }
        }
    }

    /// <summary>Gửi 1 attempt — không replay được thì gửi original, còn lại clone từ body đã buffer.</summary>
    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpRequestMessage request, byte[]? body, bool replayable, CancellationToken ct)
    {
        if (!replayable)
        {
            return await base.SendAsync(request, ct);
        }

        // Clone từng attempt: không gửi lại cùng HttpRequestMessage (Design decision 3)
        using var clone = CloneRequest(request, body);
        return await base.SendAsync(clone, ct);
    }

    /// <summary>Copy method/uri/headers/version + body buffer — request mới cho từng attempt.</summary>
    private static HttpRequestMessage CloneRequest(HttpRequestMessage original, byte[]? body)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy,
        };
        foreach (var header in original.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (body is not null)
        {
            clone.Content = new ByteArrayContent(body);
            foreach (var header in original.Content!.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    /// <summary>Lỗi connect-phase tới proxy — chỉ những kiểu này mới failover (spec §4.4).</summary>
    private static bool IsProxyConnectFailure(Exception ex) =>
        ex is HttpRequestException && (
            ex.InnerException is SocketException
            || ex.InnerException is TimeoutException
            || ex.Message.Contains("407", StringComparison.Ordinal));

    private void LogDown(ProxyAttempt pick, Exception ex) =>
        // Chỉ khi ReportFailure trả true (sống→down) — không lặp mỗi request (spec §7);
        // message KHÔNG chứa credentials, chỉ endpoint + nội dung lỗi
        _log.Warn($"Proxy {pick.Endpoint} đánh dấu down 60s sau lỗi kết nối: {ex.Message}");
}
```

**Chú ý:** sau khi biên dịch, kiểm tra warning `CS0162`/unreachable không được có; `ExceptionDispatchInfo.Throw()` trả `void` nên code sau nhánh nếu không chạy — intended.

- [ ] **Step 5: Test `RoundRobinWebProxyTests`**

Tạo `router balancing test/Proxies/RoundRobinWebProxyTests.cs`:

```csharp
using System.Net;
using RouterBalancing.Core.Proxies;

namespace router_balancing_test.Proxies;

public class RoundRobinWebProxyTests
{
    private static readonly Uri Destination = new("https://api.example.com/v1/chat/completions");

    private static ProxyAttempt Attempt() =>
        new(1, new Uri("http://127.0.0.1:8080"), "alice", "s3cret", "http://127.0.0.1:8080");

    [Fact]
    public void GetProxy_WithScopedAttempt_ReturnsAttemptUriWithoutUserinfo()
    {
        var proxy = new RoundRobinWebProxy();
        ProxyContext.Current = Attempt();
        try
        {
            Assert.Equal(new Uri("http://127.0.0.1:8080"), proxy.GetProxy(Destination));
            Assert.False(proxy.IsBypassed(Destination));
        }
        finally
        {
            ProxyContext.Current = null;
        }
    }

    [Fact]
    public void GetProxy_NoScope_BypassesToDirect()
    {
        var proxy = new RoundRobinWebProxy();

        Assert.True(proxy.IsBypassed(Destination));
        Assert.Null(proxy.GetProxy(Destination));
    }

    [Fact]
    public void Credentials_IsStableDynamicInstance()
    {
        var proxy = new RoundRobinWebProxy();

        Assert.Same(DynamicProxyCredentials.Instance, proxy.Credentials);
        Assert.Same(proxy.Credentials, proxy.Credentials); // handler đọc 1 lần lúc construct
    }

    [Fact]
    public void DynamicProxyCredentials_GetCredential_ReadsAsyncLocal()
    {
        var proxy = new RoundRobinWebProxy();
        ProxyContext.Current = Attempt();
        try
        {
            var credential = proxy.Credentials!.GetCredential(Destination, "Basic");

            Assert.NotNull(credential);
            Assert.Equal("alice", credential!.UserName);
            Assert.Equal("s3cret", credential.Password);
        }
        finally
        {
            ProxyContext.Current = null;
        }

        // Không scope → không auth (direct / proxy không auth)
        Assert.Null(DynamicProxyCredentials.Instance.GetCredential(Destination, "Basic"));
    }
}
```

- [ ] **Step 6: Test `ProxyHealthHandlerTests`**

Tạo `router balancing test/Proxies/ProxyHealthHandlerTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using RouterBalancing.Core.Proxies;

namespace router_balancing_test.Proxies;

public class ProxyHealthHandlerTests
{
    private static ProxyAttempt Attempt(long id, int port) =>
        new(id, new Uri($"http://127.0.0.1:{port}"), null, null, $"http://127.0.0.1:{port}");

    private static HttpRequestException ConnectFailure() =>
        new("Connection refused", new SocketException((int)SocketError.ConnectionRefused));

    [Fact]
    public async Task SendAsync_ConnectFailure_ReportsAndFailsOverToNextProxy()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(call => call == 1 ? throw ConnectFailure() : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Calls);
        Assert.Equal([1L], pool.Failures);
        Assert.Equal([2L], pool.Successes);
        // Attempt 1 qua p1, attempt 2 qua p2 — context ghi đúng pick của từng call
        Assert.Equal(1L, stub.ContextAtCall[0]!.Id);
        Assert.Equal(2L, stub.ContextAtCall[1]!.Id);
        Assert.Null(ProxyContext.Current); // reset sau khi handler xong
    }

    [Fact]
    public async Task SendAsync_Upstream500_ReportsSuccessNotFailure()
    {
        var pool = new FakePool().Add(Attempt(1, 8001));
        var stub = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Empty(pool.Failures); // 5xx từ upstream ≠ proxy fail (§4.4)
        Assert.Equal([1L], pool.Successes);
    }

    [Fact]
    public async Task SendAsync_407Response_TreatedAsProxyFailure()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(call => call == 1
            ? new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired)
            : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([1L], pool.Failures); // 407 = proxy fail, failover sang p2
        Assert.Equal([2L], pool.Successes);
    }

    [Fact]
    public async Task SendAsync_AllProxiesFail_FallsBackToDirect()
    {
        var pool = new FakePool().Add(Attempt(1, 8001));
        var stub = new StubHandler(call => call == 1 ? throw ConnectFailure() : Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Calls);
        Assert.Equal([1L], pool.Failures);
        Assert.Null(stub.ContextAtCall[1]); // direct attempt — không dính proxy nào
    }

    [Fact]
    public async Task SendAsync_BudgetExhausted_ThrowsLastFailureWithoutRetry()
    {
        // FakePool không gỡ proxy khỏi list → GetNext luôn trả → pin nhánh hết budget
        var pool = new FakePool(removeOnFailure: false).Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(_ => throw ConnectFailure());
        using var client = ClientFor(pool, stub);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync("http://upstream.example/v1/chat"));

        Assert.Equal(2, stub.Calls); // 2 attempt theo budget, không có attempt thứ 3
        Assert.Null(ProxyContext.Current);
    }

    [Fact]
    public async Task SendAsync_NonReplayableContent_DoesNotRetry()
    {
        var pool = new FakePool().Add(Attempt(1, 8001), Attempt(2, 8002));
        var stub = new StubHandler(_ => throw ConnectFailure());
        using var client = ClientFor(pool, stub);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://upstream.example/v1/chat")
        {
            Content = new OpaqueContent(),
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));

        Assert.Equal(1, stub.Calls); // không retry — ném ngay (§4.4 replay guard)
        Assert.Equal([1L], pool.Failures);
    }

    [Fact]
    public async Task SendAsync_EmptyPool_AttemptsDirectImmediately()
    {
        var pool = new FakePool();
        var stub = new StubHandler(_ => Ok());
        using var client = ClientFor(pool, stub);

        var response = await client.GetAsync("http://upstream.example/v1/chat");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(stub.ContextAtCall[0]); // không scope → IsBypassed → direct
        Assert.Empty(pool.Failures);
    }

    private static HttpClient ClientFor(FakePool pool, StubHandler stub) =>
        new(new ProxyHealthHandler(pool, new NullLog()) { InnerHandler = stub });

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);

    /// <summary>
    /// Pool scriptable: ghi lại report thành công/thất bại; <c>removeOnFailure=false</c> giữ proxy trong list
    /// để test nhánh hết budget (GetNext vẫn trả proxy sau khi đã fail).
    /// </summary>
    private sealed class FakePool(bool removeOnFailure = true) : IProxyPool
    {
        private readonly List<ProxyAttempt> _alive = [];
        private int _cursor;

        public List<long> Failures { get; } = [];

        public List<long> Successes { get; } = [];

        public FakePool Add(params ProxyAttempt[] attempts)
        {
            _alive.AddRange(attempts);
            return this;
        }

        public ProxyAttempt? GetNext()
        {
            if (_alive.Count == 0)
            {
                return null;
            }

            var pick = _alive[_cursor % _alive.Count];
            _cursor++;
            return pick;
        }

        public bool ReportFailure(long proxyId)
        {
            Failures.Add(proxyId);
            return removeOnFailure ? _alive.RemoveAll(a => a.Id == proxyId) > 0 : true;
        }

        public void ReportSuccess(long proxyId) => Successes.Add(proxyId);

        public void Invalidate()
        {
        }

        public IReadOnlyList<ProxyRuntimeStatus> Snapshot() =>
            _alive.Select(a => new ProxyRuntimeStatus(a.Id, a.Endpoint, false, null)).ToList();
    }

    /// <summary>Inner handler ghi lại call count + ProxyContext tại thời điểm gọi — script có thể ném.</summary>
    private sealed class StubHandler(Func<int, HttpResponseMessage> script) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public List<ProxyAttempt?> ContextAtCall { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            ContextAtCall.Add(ProxyContext.Current);
            return Task.FromResult(script(Calls));
        }
    }

    /// <summary>Content không thuộc nhóm replayable (không kế thừa ByteArrayContent).</summary>
    private sealed class OpaqueContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync("x"u8.ToArray()).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = -1;
            return false;
        }
    }
}
```

- [ ] **Step 7: Gates + Commit**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: 0 lỗi; toàn bộ pass (474 + 11 = 485). Nếu test nào fail do exception shape bất ngờ (đặc biệt cách .NET wrap lỗi) → soi lại `IsProxyConnectFailure` đúng intent §4.4 trước khi sửa assertion.

```powershell
git add -A
git commit -m "feat: add proxy health handler with in-request failover"
```

---

### Task 5: HTTP proxy stub + outbound integration tests

**Files:**
- Create: `router balancing test/Server/LocalHttpServer.cs`, `router balancing test/Server/LocalHttpProxyStub.cs`
- Create (test): `router balancing test/Proxies/ProxyOutboundHttpStubTests.cs`

**Interfaces:**
- Consumes: `ProxyPool` (Task 3), `ProxyHealthHandler`/`RoundRobinWebProxy` (Task 4).
- Produces (Task 6/7/10): `LocalHttpServer` tái dùng cho SOCKS stub test (Task 6) và smoke; stub chứng minh end-to-end GET qua proxy tuyệt đối-URI (không CONNECT — spec §4.6, risk §11.5).

- [ ] **Step 1: `LocalHttpServer.cs` — destination server**

Tạo `router balancing test/Server/LocalHttpServer.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace router_balancing_test.Server;

/// <summary>
/// HTTP destination server trên 127.0.0.1 (port random): nhận GET, trả JSON
/// <c>{"ok":true}</c>, luôn đóng kết nối sau response (<c>Connection: close</c>) —
/// stub forward bằng read-to-EOF được, deterministic (spec §4.6).
/// </summary>
public sealed class LocalHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private int _requestsHandled;

    public int Port { get; }

    /// <summary>Số request đã nhận — assert direct/proxy tới destination.</summary>
    public int RequestsHandled => Volatile.Read(ref _requestsHandled);

    public LocalHttpServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return; // listener đóng lúc Dispose — không phải lỗi hệ thống
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            await TcpHeadReader.ReadHeadAsync(stream);
            Interlocked.Increment(ref _requestsHandled);

            var body = "{\"ok\":true}"u8.ToArray();
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                + body.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(head);
            await stream.WriteAsync(body);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(
            e => e is OperationCanceledException or SocketException or ObjectDisposedException))
        {
            // chấp nhận được khi dispose giữa chừng — không nuốt lỗi khác
        }

        _cts.Dispose();
    }
}
```

Cùng file (hoặc file lân cận `TcpHeadReader.cs` — internal static, dùng chung cho stub):

```csharp
using System.Net.Sockets;
using System.Text;

namespace router_balancing_test.Server;

/// <summary>Đọc HTTP head (headers đến dòng trống) byte-by-byte — tránh cần parser đầy đủ.</summary>
internal static class TcpHeadReader
{
    public static async Task<string> ReadHeadAsync(NetworkStream stream)
    {
        var ms = new MemoryStream();
        var buffer = new byte[1];
        while (ms.Length < 16 * 1024)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            ms.WriteByte(buffer[0]);
            if (ms.Length >= 4)
            {
                var bytes = ms.GetBuffer();
                if (bytes[ms.Length - 4] == (byte)'\r'
                    && bytes[ms.Length - 3] == (byte)'\n'
                    && bytes[ms.Length - 2] == (byte)'\r'
                    && bytes[ms.Length - 1] == (byte)'\n')
                {
                    break;
                }
            }
        }

        return Encoding.ASCII.GetString(ms.ToArray());
    }
}
```

- [ ] **Step 2: `LocalHttpProxyStub.cs` — HTTP forward proxy**

Tạo `router balancing test/Server/LocalHttpProxyStub.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace router_balancing_test.Server;

/// <summary>
/// HTTP forward proxy stub (chỉ GET, tuyệt đối-URI, không CONNECT — spec §4.6):
/// ghi lại request line + Proxy-Authorization, optional bắt Basic auth (sai/thiếu → 407),
/// đúng thì forward tới target (origin-form, <c>Connection: close</c>) và pipe response về.
/// </summary>
public sealed class LocalHttpProxyStub : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly List<string> _requestLines = [];
    private readonly List<string?> _proxyAuthorizationHeaders = [];
    private int _requestsHandled;

    public int Port { get; }

    /// <summary>Yêu cầu Basic auth với credential này — không set thì bỏ qua auth.</summary>
    public string? RequireUser { get; init; }

    public string? RequirePassword { get; init; }

    public bool RequireAuth => RequireUser is not null;

    /// <summary>Số request đã forward (không tính request bị 407).</summary>
    public int RequestsHandled => Volatile.Read(ref _requestsHandled);

    public IReadOnlyList<string> RequestLines
    {
        get
        {
            lock (_requestLines)
            {
                return _requestLines.ToArray();
            }
        }
    }

    public IReadOnlyList<string?> ProxyAuthorizationHeaders
    {
        get
        {
            lock (_proxyAuthorizationHeaders)
            {
                return _proxyAuthorizationHeaders.ToArray();
            }
        }
    }

    public LocalHttpProxyStub()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return; // listener đóng lúc Dispose
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            var stream = client.GetStream();
            var head = await TcpHeadReader.ReadHeadAsync(stream);
            var lines = head.Split("\r\n");

            var requestLine = lines.Length > 0 ? lines[0] : string.Empty;
            var authHeader = GetHeader(lines, "proxy-authorization");
            lock (_requestLines)
            {
                _requestLines.Add(requestLine);
                _proxyAuthorizationHeaders.Add(authHeader);
            }

            if (RequireAuth && authHeader != ExpectedBasicHeader())
            {
                await stream.WriteAsync(
                    "HTTP/1.1 407 Proxy Authentication Required\r\n"
                    + "Proxy-Authenticate: Basic realm=\"stub\"\r\n"
                    + "Content-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
                return;
            }

            await ForwardAsync(stream, requestLine, lines);
        }
    }

    private static async Task ForwardAsync(NetworkStream clientStream, string requestLine, string[] lines)
    {
        var parts = requestLine.Split(' ');
        if (parts.Length < 3 || parts[0] != "GET")
        {
            // Stub chỉ phục vụ GET absolute-URI — method khác là bug của caller test
            await clientStream.WriteAsync(
                "HTTP/1.1 405 Method Not Allowed\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray());
            return;
        }

        var target = new Uri(parts[1]);
        using var forward = new TcpClient();
        await forward.ConnectAsync(target.Host, target.Port);
        var forwardStream = forward.GetStream();

        // Rewrite absolute-URI → origin-form, bỏ header riêng của proxy, ép close để read-to-EOF
        var sb = new StringBuilder();
        sb.Append(parts[0]).Append(' ').Append(target.PathAndQuery).Append(" HTTP/1.1\r\n");
        foreach (var line in lines.Skip(1))
        {
            if (line.Length == 0
                || line.StartsWith("Proxy-Connection", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            sb.Append(line).Append("\r\n");
        }

        sb.Append("Connection: close\r\n\r\n");
        await forwardStream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));

        Interlocked.Increment(ref _requestsHandled);
        await forwardStream.CopyToAsync(clientStream); // EOF khi destination đóng → đóng client
    }

    private string ExpectedBasicHeader() =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{RequireUser}:{RequirePassword}"));

    private static string? GetHeader(string[] lines, string name) =>
        lines.Skip(1)
            .FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            ?[(name.Length + 1)..].Trim();

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(
            e => e is OperationCanceledException or SocketException or ObjectDisposedException))
        {
            // chấp nhận được khi dispose giữa chừng
        }

        _cts.Dispose();
    }
}
```

- [ ] **Step 3: Test `ProxyOutboundHttpStubTests` — pipeline thật: pool + handler + webproxy + stub + destination**

Tạo `router balancing test/Proxies/ProxyOutboundHttpStubTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;
using router_balancing_test.Server;

namespace router_balancing_test.Proxies;

public class ProxyOutboundHttpStubTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly LocalHttpServer _destination = new();
    private readonly List<IDisposable> _stubs = [];

    public ProxyOutboundHttpStubTests()
    {
        _factory = _db.CreateFactory();
        DbInitializer.Initialize(_factory);
    }

    public void Dispose()
    {
        foreach (var stub in _stubs)
        {
            stub.Dispose();
        }

        _destination.Dispose();
        _db.Dispose();
    }

    private LocalHttpProxyStub AddStub(string? user = null, string? password = null)
    {
        var stub = new LocalHttpProxyStub { RequireUser = user, RequirePassword = password };
        _stubs.Add(stub);
        return stub;
    }

    private static int ReserveClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port; // port vừa đóng — connect failover nhanh (connection refused)
    }

    private async Task<long> AddRowAsync(int port, string? username = null, string? password = null)
    {
        using var db = _factory.CreateDbContext();
        var row = new OutboundProxy
        {
            Scheme = "http",
            Host = "127.0.0.1",
            Port = port,
            Enabled = true,
            Username = username,
            PasswordEncrypted = password is null ? null : _protector.Protect(password),
        };
        db.OutboundProxies.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>Pipeline 1 chiều đúng như MauiProgram sẽ wire (Task 8): handler → SocketsHttpHandler.</summary>
    private (HttpClient client, ProxyPool pool) CreateClient()
    {
        var pool = new ProxyPool(_factory, _protector, TimeProvider.System, new NullLog());
        var handler = new ProxyHealthHandler(pool, new NullLog())
        {
            InnerHandler = new SocketsHttpHandler
            {
                Proxy = new RoundRobinWebProxy(),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(3),
            },
        };
        return (new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) }, pool);
    }

    private Uri DestinationUrl() => new($"http://127.0.0.1:{_destination.Port}/v1/chat/completions");

    [Fact]
    public async Task SendAsync_TwoProxies_DistributesEvenlyAcrossStubs()
    {
        var stubA = AddStub();
        var stubB = AddStub();
        await AddRowAsync(stubA.Port);
        await AddRowAsync(stubB.Port);
        var (client, _) = CreateClient();

        for (var i = 0; i < 6; i++)
        {
            var response = await client.GetAsync(DestinationUrl());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(3, stubA.RequestsHandled); // RR đều 3/3 — pin per-request (§4.1)
        Assert.Equal(3, stubB.RequestsHandled);
        Assert.Equal(6, _destination.RequestsHandled); // mọi request đều tới destination
        Assert.All(stubA.RequestLines.Concat(stubB.RequestLines),
            line => Assert.StartsWith("GET http://", line)); // absolute-URI, không CONNECT
    }

    [Fact]
    public async Task SendAsync_WithProxyCredentials_SendsBasicAuthorization()
    {
        var stub = AddStub(user: "alice", password: "s3cret");
        await AddRowAsync(stub.Port, username: "alice", password: "s3cret");
        var (client, _) = CreateClient();

        var response = await client.GetAsync(DestinationUrl());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var expected = "Basic " + Convert.ToBase64String(
            System.Text.Encoding.UTF8.GetBytes("alice:s3cret"));
        // Lần 1: 407 challenge (header thiếu/sai — nếu .NET tự retry nội bộ), lần cuối: đúng.
        // Nếu .NET KHÔNG tự retry thách thức 407 với proxy credentials, test này fail →
        // xem lại cách SocketsHttpHandler xử lý Proxy-Authorization trước khi đổi assertion.
        Assert.Equal(expected, stub.ProxyAuthorizationHeaders[^1]);
        Assert.Equal(1, stub.RequestsHandled);
        Assert.Equal(1, _destination.RequestsHandled);
    }

    [Fact]
    public async Task SendAsync_DeadProxy_FailsOverAndMarksDown()
    {
        var deadPort = ReserveClosedPort();
        var deadId = await AddRowAsync(deadPort);
        var live = AddStub();
        await AddRowAsync(live.Port);
        var (client, pool) = CreateClient();

        var response = await client.GetAsync(DestinationUrl());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, live.RequestsHandled);
        Assert.Equal(1, _destination.RequestsHandled);
        Assert.True(pool.Snapshot().Single(s => s.Id == deadId).IsDown); // passive health
    }

    [Fact]
    public async Task SendAsync_AllProxiesDead_AttemptsDirect()
    {
        await AddRowAsync(ReserveClosedPort());
        await AddRowAsync(ReserveClosedPort());
        var (client, pool) = CreateClient();

        var response = await client.GetAsync(DestinationUrl());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _destination.RequestsHandled); // direct tới destination
        Assert.All(pool.Snapshot(), s => Assert.True(s.IsDown)); // cả 2 down → budget hết → direct
    }
}
```

- [ ] **Step 4: Gates + Commit**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: 0 lỗi; toàn bộ pass (485 + 4 = 489). Lưu ý flakes đã biết: nếu `SendAsync_WithProxyCredentials_*` fail đúng ở assertion header → dừng, xem lại SocketsHttpHandler challenge retry (comment trong test) **trước** khi sửa assertion.

```powershell
git add -A
git commit -m "test: add outbound http proxy stub integration"
```

---

### Task 6: SOCKS5 stub + outbound SOCKS tests — **RED gate: dừng hỏi user nếu auth-SOCKS đỏ**

**Files:**
- Create: `router balancing test/Server/LocalSocks5Stub.cs`
- Create (test): `router balancing test/Proxies/ProxyOutboundSocksStubTests.cs`

**Interfaces:**
- Consumes: `ProxyPool` (Task 3), `ProxyHealthHandler`/`RoundRobinWebProxy` (Task 4), `LocalHttpServer` (Task 5).
- Produces: xác nhận (hoặc phủ định — spec risk §11.4) giả định *"SOCKS5 handshake gọi `DynamicProxyCredentials.GetCredential`"* trước khi ship.

> **STOP rule (spec §11.4):** test auth-SOCKS **đỏ** → dừng toàn bộ flow, báo user đúng trạng thái + nguyên nhân, chờ quyết định (bỏ auth-SOCKS khỏi scope / chấp nhận limitation / hướng khác). **Không** sửa assertion cho xanh, không skip test, không tự bẻ classifier cho qua.

- [ ] **Step 1: `LocalSocks5Stub.cs` — RFC1928 + RFC1929, tunnel GET**

Tạo `router balancing test/Server/LocalSocks5Stub.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace router_balancing_test.Server;

/// <summary>
/// SOCKS5 stub (RFC1928, optional username/password RFC1929): greeting → auth (nếu bật)
/// → CONNECT (ATYP 01/03/04) → tunnel raw TCP tới target (spec §4.6, risk §11.4/11.5).
/// Ghi lại username đã handshake để test xác minh credentials đi qua AsyncLocal.
/// </summary>
public sealed class LocalSocks5Stub : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly List<string> _usernames = [];
    private int _tunnelsEstablished;
    private int _authFailures;

    public int Port { get; }

    /// <summary>Yêu cầu RFC1929 username/password — không set thì method no-auth.</summary>
    public string? RequireUser { get; init; }

    public string? RequirePassword { get; init; }

    public bool RequireAuth => RequireUser is not null;

    public int TunnelsEstablished => Volatile.Read(ref _tunnelsEstablished);

    public int AuthFailures => Volatile.Read(ref _authFailures);

    public IReadOnlyList<string> Usernames
    {
        get
        {
            lock (_usernames)
            {
                return _usernames.ToArray();
            }
        }
    }

    public LocalSocks5Stub()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return; // listener đóng lúc Dispose
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();

                // ===== Greeting: 05 <nmethods> <methods> =====
                var greeting = await ReadExactAsync(stream, 2);
                var methods = await ReadExactAsync(stream, greeting[1]);
                if (RequireAuth)
                {
                    if (!methods.Contains(0x02))
                    {
                        await stream.WriteAsync(new byte[] { 0x05, 0xFF });
                        Interlocked.Increment(ref _authFailures);
                        return;
                    }

                    await stream.WriteAsync(new byte[] { 0x05, 0x02 });

                    // ===== RFC1929: 01 <ulen> <user> <plen> <pass> =====
                    var verUlen = await ReadExactAsync(stream, 2);
                    var user = Encoding.UTF8.GetString(await ReadExactAsync(stream, verUlen[1]));
                    var plen = (await ReadExactAsync(stream, 1))[0];
                    var pass = Encoding.UTF8.GetString(await ReadExactAsync(stream, plen));
                    lock (_usernames)
                    {
                        _usernames.Add(user);
                    }

                    if (user != RequireUser || pass != RequirePassword)
                    {
                        await stream.WriteAsync(new byte[] { 0x01, 0x01 });
                        Interlocked.Increment(ref _authFailures);
                        return;
                    }

                    await stream.WriteAsync(new byte[] { 0x01, 0x00 });
                }
                else
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0x00 });
                }

                // ===== CONNECT: 05 01 00 <atyp> <addr> <port> =====
                var request = await ReadExactAsync(stream, 4);
                if (request[1] != 0x01)
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                (var host, var port) = await ReadAddressAsync(stream, request[3]);
                using var forward = new TcpClient();
                try
                {
                    await forward.ConnectAsync(host, port);
                }
                catch (SocketException)
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                // Bind addr 0.0.0.0:0 — client không quan tâm giá trị bind
                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 });
                Interlocked.Increment(ref _tunnelsEstablished);

                // ===== Pipe 2 chiều: client ↔ target (Connection: close từ destination → EOF) =====
                var forwardStream = forward.GetStream();
                var targetToClient = forwardStream.CopyToAsync(stream);
                var clientToTarget = stream.CopyToAsync(forwardStream);
                await Task.WhenAny(targetToClient, clientToTarget);
            }
            catch (IOException)
            {
                // client đóng giữa handshake — bình thường khi test dispose/dọn dẹp
            }
            catch (ObjectDisposedException)
            {
                // stream đóng khi dispose — bình thường
            }
        }
    }

    private static async Task<(string host, int port)> ReadAddressAsync(NetworkStream stream, byte atyp)
    {
        string host;
        if (atyp == 0x01)
        {
            var addr = await ReadExactAsync(stream, 4);
            host = $"{addr[0]}.{addr[1]}.{addr[2]}.{addr[3]}";
        }
        else if (atyp == 0x03)
        {
            var length = (await ReadExactAsync(stream, 1))[0];
            host = Encoding.ASCII.GetString(await ReadExactAsync(stream, length));
        }
        else if (atyp == 0x04)
        {
            var addr = await ReadExactAsync(stream, 16);
            host = new IPAddress(addr).ToString();
        }
        else
        {
            throw new IOException($"SOCKS5 atyp không hỗ trợ: 0x{atyp:X2}");
        }

        var portBytes = await ReadExactAsync(stream, 2);
        return (host, (portBytes[0] << 8) | portBytes[1]);
    }

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0)
            {
                throw new IOException("Client đóng kết nối giữa handshake SOCKS5.");
            }

            offset += read;
        }

        return buffer;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(
            e => e is OperationCanceledException or SocketException or ObjectDisposedException))
        {
            // chấp nhận được khi dispose giữa chừng
        }

        _cts.Dispose();
    }
}
```

- [ ] **Step 2: Test `ProxyOutboundSocksStubTests` — 2 test (1 no-auth, 1 auth = RED gate)**

Tạo `router balancing test/Proxies/ProxyOutboundSocksStubTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;
using router_balancing_test.Server;

namespace router_balancing_test.Proxies;

public class ProxyOutboundSocksStubTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly LocalHttpServer _destination = new();
    private readonly List<IDisposable> _stubs = [];

    public ProxyOutboundSocksStubTests()
    {
        _factory = _db.CreateFactory();
        DbInitializer.Initialize(_factory);
    }

    public void Dispose()
    {
        foreach (var stub in _stubs)
        {
            stub.Dispose();
        }

        _destination.Dispose();
        _db.Dispose();
    }

    private async Task<long> AddRowAsync(int port, string scheme,
        string? username = null, string? password = null)
    {
        using var db = _factory.CreateDbContext();
        var row = new OutboundProxy
        {
            Scheme = scheme,
            Host = "127.0.0.1",
            Port = port,
            Enabled = true,
            Username = username,
            PasswordEncrypted = password is null ? null : _protector.Protect(password),
        };
        db.OutboundProxies.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private (HttpClient client, ProxyPool pool) CreateClient()
    {
        var pool = new ProxyPool(_factory, _protector, TimeProvider.System, new NullLog());
        var handler = new ProxyHealthHandler(pool, new NullLog())
        {
            InnerHandler = new SocketsHttpHandler
            {
                Proxy = new RoundRobinWebProxy(),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(3),
            },
        };
        return (new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) }, pool);
    }

    private Uri DestinationUrl() => new($"http://127.0.0.1:{_destination.Port}/v1/chat/completions");

    [Fact]
    public async Task SendAsync_Socks5NoAuth_TunnelsToDestination()
    {
        var stub = new LocalSocks5Stub();
        _stubs.Add(stub);
        await AddRowAsync(stub.Port, scheme: "socks5");
        var (client, _) = CreateClient();

        var response = await client.GetAsync(DestinationUrl());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, stub.TunnelsEstablished); // native socks5: greeting → CONNECT → tunnel
        Assert.Equal(1, _destination.RequestsHandled);
    }

    [Fact]
    public async Task SendAsync_Socks5WithCredentials_AuthenticatesViaAsyncLocal()
    {
        var stub = new LocalSocks5Stub { RequireUser = "alice", RequirePassword = "s3cret" };
        _stubs.Add(stub);
        await AddRowAsync(stub.Port, scheme: "socks5", username: "alice", password: "s3cret");
        var (client, _) = CreateClient();

        var response = await client.GetAsync(DestinationUrl());

        // ===== RED GATE (spec §11.4) =====
        // Nếu HttpRequestException (handshake fail vì .NET không gọi GetCredential cho
        // SOCKS5) → STOP, báo user, KHÔNG sửa assertion/skip.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, stub.AuthFailures);
        Assert.Equal("alice", Assert.Single(stub.Usernames)); // credentials đi qua AsyncLocal
        Assert.Equal(1, _destination.RequestsHandled);
    }
}
```

- [ ] **Step 3: Gates có điều kiện — chạy filter riêng trước**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProxyOutboundSocksStubTests"
```

**Gate A — `SendAsync_Socks5NoAuth_TunnelsToDestination` phải xanh** (native socks5 đã verify ở khâu explore). Nếu nó đỏ → STOP sớm, báo user (scope socks5:// tổng thể bị nghi vấn).

**Gate B — `SendAsync_Socks5WithCredentials_AuthenticatesViaAsyncLocal`:**

| Kết quả | Hành động |
|---|---|
| Xanh | Chạy full suite (`dotnet test ...` không filter, kỳ vọng 489 + 2 = 491) → commit. |
| **Đỏ** | **STOP.** Không commit, không sửa test. Báo user: đúng risk §11.4 — `.NET 10 SOCKS5 handshake không gọi `ICredentials.GetCredential` (hay exception shape khác)`, kèm exception thật + log. Chờ user chọn phương án rồi mới tiếp. |

```powershell
git add -A
git commit -m "test: add socks5 proxy stub integration"
```

*(Commit này chỉ thực hiện khi cả 2 test xanh hoặc user đã quyết định phương án xử lý Gate B.)*

---

### Task 7: `IProxyEchoClient`/`ProxyEchoClient` + `ProxyService.TestAsync`

**Files:**
- Create: `src/RouterBalancing.Core/Proxies/ProxyTestResult.cs`, `IProxyEchoClient.cs`, `ProxyEchoClient.cs`
- Edit: `src/RouterBalancing.Core/Proxies/IProxyService.cs`, `ProxyService.cs` (ctor + `TestAsync`)
- Edit (test): `router balancing test/Proxies/ProxyServiceTests.cs` (sửa ctor + 4 test mới)
- Create (test): `router balancing test/Proxies/ProxyEchoClientTests.cs`

**Interfaces:**
- Consumes: `ProxyAttempt` (Task 2), `LocalHttpServer` + `LocalHttpProxyStub` (Task 5).
- Produces (Task 8): `IProxyEchoClient` đăng ký DI, truyền vào `ProxyService` ctor.

- [ ] **Step 1: `ProxyTestResult.cs`**

```csharp
namespace RouterBalancing.Core.Proxies;

/// <summary>Kết quả 1 lần test proxy thủ công — nút Test ở trang Proxies (spec §5.4).</summary>
public sealed record ProxyTestResult(bool Success, string? Error, int? HttpStatus, TimeSpan Elapsed, string? Ip = null);
```

- [ ] **Step 2: `IProxyEchoClient.cs`**

```csharp
namespace RouterBalancing.Core.Proxies;

/// <summary>Gửi 1 request echo qua proxy cụ thể để test tay — tách interface để unit test mock (spec §4.5).</summary>
public interface IProxyEchoClient
{
    /// <summary>Gửi request echo qua <paramref name="proxy"/> (credentials đã decrypt).</summary>
    /// <param name="proxy">Proxy cần test.</param>
    /// <param name="ct">Token hủy của caller — hủy thật phải ném <see cref="OperationCanceledException"/>.</param>
    /// <exception cref="HttpRequestException">Không kết nối qua được proxy hoặc upstream non-2xx.</exception>
    /// <exception cref="TaskCanceledException">Timeout 10s của HttpClient (không do <paramref name="ct"/>).</exception>
    Task<ProxyTestResult> EchoAsync(ProxyAttempt proxy, CancellationToken ct);
}
```

- [ ] **Step 3: `ProxyEchoClient.cs`**

Tạo `src/RouterBalancing.Core/Proxies/ProxyEchoClient.cs`:

```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Echo client cho test tay: HttpClient + WebProxy **cố định** cho đúng proxy cần test,
/// Credentials tường minh (không qua <see cref="ProxyContext"/>, không qua
/// <see cref="ProxyHealthHandler"/> — test tay không đụng health pool, spec §4.5).
/// Kết nối 10s, timeout tổng 10s.
/// </summary>
public sealed class ProxyEchoClient : IProxyEchoClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly Uri _echoUrl;

    /// <param name="time">TimeProvider lấy từ DI (đo elapsed).</param>
    /// <param name="echoUrl">Endpoint echo — test truyền destination của LocalHttpServer.</param>
    public ProxyEchoClient(TimeProvider time,
        string echoUrl = "https://api.ipify.org/?format=json")
    {
        _time = time;
        _echoUrl = new Uri(echoUrl);
    }

    /// <inheritdoc/>
    public async Task<ProxyTestResult> EchoAsync(ProxyAttempt proxy, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(proxy.Uri)
            {
                Credentials = proxy.Username is null
                    ? null
                    : new NetworkCredential(proxy.Username, proxy.Password ?? string.Empty),
            },
            UseProxy = true,
            ConnectTimeout = Timeout,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout };

        var started = Stopwatch.GetTimestamp();
        using var response = await http.GetAsync(_echoUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        var elapsed = Stopwatch.GetElapsedTime(started);
        response.EnsureSuccessStatusCode(); // non-2xx → HttpRequestException, để ProxyService persist

        // IP egress best-effort — echo URL dạng {"ip":"..."}; body khác (stub trả {"ok":true})
        // hay JSON hỏng → Ip = null, test vẫn pass (không fail chỉ vì thiếu IP)
        string? ip = null;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (json.RootElement.TryGetProperty("ip", out var ipElement))
            {
                ip = ipElement.GetString();
            }
        }
        catch (JsonException)
        {
            // body không phải JSON {"ip"} — chấp nhận, Ip null
        }

        return new ProxyTestResult(true, null, (int)response.StatusCode, elapsed, ip);
    }
}
```

*Lưu ý:* nếu toàn file dùng `Stopwatch` trực tiếp thì bỏ `TimeProvider` khỏi ctor — giữ nhất quán với phần còn lại của codebase (chỉ dùng 1 trong 2, không lẫn). DI (Task 8) đăng ký khớp ctor đã chốt.

- [ ] **Step 4: `IProxyService` + `ProxyService.TestAsync`**

1. Mở `IProxyService.cs`, thêm:

```csharp
    /// <summary>Test kết nối thủ công qua proxy — persist LastTest* (spec §5.4).</summary>
    Task<ProxyTestResult> TestAsync(long proxyId, CancellationToken ct);
```

2. Mở `ProxyService.cs`: **đọc ctor hiện tại trước**, thêm `IProxyEchoClient echo` vào **cuối** danh sách tham số (không tạo overload — sửa mọi chỗ `new ProxyService(`, Design decision 5).

3. Grep toàn solution tìm điểm gọi cần sửa (kể cả test) — dùng **Grep tool**: pattern `new ProxyService\(`, include `*.cs`, path = repo root → sửa mọi file trả về.

4. Implement `TestAsync` — **giữ nguyên pattern DbContext/decrypt helper đang có trong `ProxyService.cs`** (không chống lại style hiện có):

```csharp
    /// <inheritdoc/>
    public async Task<ProxyTestResult> TestAsync(long proxyId, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var row = await db.OutboundProxies.FirstOrDefaultAsync(p => p.Id == proxyId)
            ?? throw new KeyNotFoundException($"Proxy {proxyId} not found.");

        var attempt = new ProxyAttempt(
            row.Id,
            new Uri($"{row.Scheme}://{row.Host}:{row.Port}"),
            row.Username,
            row.PasswordEncrypted is null ? null : _protector.Unprotect(row.PasswordEncrypted),
            $"{row.Scheme}://{row.Host}:{row.Port}");

        try
        {
            var result = await _echo.EchoAsync(attempt, ct);
            row.LastTestAt = DateTimeOffset.UtcNow;
            row.LastTestSuccess = true;
            row.LastTestMessage = null;
            row.LastTestIp = result.Ip;
            await db.SaveChangesAsync();
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // người dùng hủy thật → không persist, không nuốt
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or InvalidOperationException or CryptographicException)
        {
            // Timeout (TaskCanceled), lỗi mạng, lỗi decrypt — persist để UI hiện lỗi
            row.LastTestAt = DateTimeOffset.UtcNow;
            row.LastTestSuccess = false;
            row.LastTestMessage = ex.Message;
            row.LastTestIp = null; // không để IP cũ của lần test thành công trước hiển thị kèm lỗi mới
            await db.SaveChangesAsync();
            return new ProxyTestResult(false, ex.Message, null, TimeSpan.Zero);
        }
    }
```

Nếu file có sẵn helper build `ProxyAttempt`/Uri từ row (từ CRUD/validate) → **tái dùng**, đừng nhân bản.

**Sau khi sửa:** DI ở `MauiProgram`/`ProxyApp` chưa có `IProxyEchoClient` → **compile vẫn OK** (DI resolve lúc runtime); wiring register ở Task 8. Unit test không qua DI → không ảnh hưởng.

- [ ] **Step 5: Sửa `ProxyServiceTests` + 4 test mới**

1. Sửa toàn bộ `new ProxyService(...)` hiện có trong test truyền thêm stub echo (field mutable để test mới override):

```csharp
    private StubEchoClient _echo = new(_ => new ProxyTestResult(true, null, 200, TimeSpan.FromMilliseconds(12)));
```

2. `StubEchoClient` (private sealed trong test file):

```csharp
    private sealed class StubEchoClient(Func<ProxyTestResult> script) : IProxyEchoClient
    {
        public Task<ProxyTestResult> EchoAsync(ProxyAttempt proxy, CancellationToken ct) =>
            Task.FromResult(script());
    }
```

3. Thêm 4 test (tên mô tả hành vi, pattern DB/protector như các test CRUD hiện có):

```csharp
    [Fact]
    public async Task TestAsync_EchoSucceeds_PersistsLastTestSuccess()
    {
        var id = await CreateAsync(/* params chuẩn của file */);
        _echo = new StubEchoClient(() => new ProxyTestResult(true, null, 200, TimeSpan.FromMilliseconds(30), "203.0.113.7"));

        var result = await Service.TestAsync(id, CancellationToken.None);

        Assert.True(result.Success);
        var row = await GetRowAsync(id);
        Assert.True(row.LastTestSuccess);
        Assert.NotNull(row.LastTestAt);
        Assert.Null(row.LastTestMessage);
        Assert.Equal("203.0.113.7", row.LastTestIp);
    }

    [Fact]
    public async Task TestAsync_EchoThrowsHttpRequestException_PersistsError()
    {
        var id = await CreateAsync();
        _echo = new StubEchoClient(() => throw new HttpRequestException("Connection refused"));

        var result = await Service.TestAsync(id, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("Connection refused", result.Error);
        var row = await GetRowAsync(id);
        Assert.False(row.LastTestSuccess);
        Assert.Equal("Connection refused", row.LastTestMessage);
        Assert.NotNull(row.LastTestAt);
    }

    [Fact]
    public async Task TestAsync_UserCancels_RethrowsWithoutPersisting()
    {
        var id = await CreateAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _echo = new StubEchoClient(() => throw new OperationCanceledException(cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service.TestAsync(id, cts.Token));

        var row = await GetRowAsync(id);
        Assert.Null(row.LastTestAt); // hủy thật → không ghi gì
    }

    [Fact]
    public async Task TestAsync_Timeout_PersistsError()
    {
        var id = await CreateAsync();
        _echo = new StubEchoClient(() => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"));

        var result = await Service.TestAsync(id, CancellationToken.None);

        Assert.False(result.Success);
        var row = await GetRowAsync(id);
        Assert.False(row.LastTestSuccess);
        Assert.NotNull(row.LastTestMessage);
    }
```

*(Điều chỉnh tên helper `CreateAsync`/`GetRowAsync`/`Service` theo đúng convention đang có trong `ProxyServiceTests.cs` — đọc file trước khi viết.)*

- [ ] **Step 6: `ProxyEchoClientTests` — echo thật qua stub**

Tạo `router balancing test/Proxies/ProxyEchoClientTests.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;
using router_balancing_test.Server;

namespace router_balancing_test.Proxies;

public class ProxyEchoClientTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly LocalHttpServer _destination = new();
    private readonly List<IDisposable> _stubs = [];

    public ProxyEchoClientTests()
    {
        _factory = _db.CreateFactory();
        DbInitializer.Initialize(_factory);
    }

    public void Dispose()
    {
        foreach (var stub in _stubs)
        {
            stub.Dispose();
        }

        _destination.Dispose();
        _db.Dispose();
    }

    [Fact]
    public async Task EchoAsync_ThroughStubProxy_ReturnsSuccess()
    {
        var stub = new LocalHttpProxyStub();
        _stubs.Add(stub);
        var echo = new ProxyEchoClient(TimeProvider.System, $"http://127.0.0.1:{_destination.Port}/ip");
        var attempt = new ProxyAttempt(1, new Uri($"http://127.0.0.1:{stub.Port}"),
            null, null, $"http://127.0.0.1:{stub.Port}");

        var result = await echo.EchoAsync(attempt, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(200, result.HttpStatus);
        Assert.Null(result.Error);
        Assert.Equal(1, stub.RequestsHandled);
        Assert.Equal(1, _destination.RequestsHandled);
    }

    [Fact]
    public async Task EchoAsync_DeadProxy_ThrowsHttpRequestException()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        var echo = new ProxyEchoClient(TimeProvider.System, $"http://127.0.0.1:{_destination.Port}/ip");
        var attempt = new ProxyAttempt(1, new Uri($"http://127.0.0.1:{deadPort}"),
            null, null, $"http://127.0.0.1:{deadPort}");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => echo.EchoAsync(attempt, CancellationToken.None));
        Assert.Equal(0, _destination.RequestsHandled);
    }
}
```

- [ ] **Step 7: Gates + Commit**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
```

Expected: 0 lỗi; toàn bộ pass (491 + 4 + 2 = **497**).

```powershell
git add -A
git commit -m "feat: add manual proxy connection test via echo client"
```

---

### Task 8: DI wiring — pool + echo + health handler vào pipeline

**Files:**
- Edit: `router-balancing/MauiProgram.cs`
- Edit: `src/RouterBalancing.Core/Server/ProxyApp.cs` (ký mới `ConfigureServices`), `ProxyHost.cs` (inject/forward pool)
- Edit (test): `router balancing test/TestDoubles.cs` (+`DirectProxyPool`), `ProxyControlApiTests.cs`, `ProxyRetryIntegrationTests.cs`, `ProxyQueueIntegrationTests.cs`, `ProxyAppChatIntegrationTests.cs` (4 call site `ConfigureServices`), `ProxyHostTests.cs` (3 `new ProxyHost(` — dòng ~44/155/169)

**Interfaces:**
- Consumes: mọi type Tasks 1–7; `IProxyPool`/`IProxyService`/`IProxyEchoClient`/`ProxyHealthHandler`/`RoundRobinWebProxy`.
- Produces: pipeline chạy thật trong app + test containers; T9 (UI) chỉ cần `IProxyService`.

- [ ] **Step 1: Khám phá trước khi sửa (không code mù)** — dùng **Grep tool** (không có `grep` trong PowerShell):

   - `ConfigureServices\(` trong `src/RouterBalancing.Core/Server` → ký hiện tại của `ProxyApp.ConfigureServices`.
   - `new ProxyHost\(` trong `router balancing test` → 3 site trong `ProxyHostTests.cs`.
   - `AddHttpClient` trong `router-balancing/MauiProgram.cs` → cấu hình sẵn của `provider-probe`/`free-model-sync`.
   - `new ProxyService\(` (include `*.cs`, cả repo) → điểm gọi cần sửa do ctor thêm tham số.
   - `TimeProvider` trong `router-balancing/MauiProgram.cs` → đã đăng ký chưa.

Ghi lại: (a) ctor `ProxyHost` hiện có + chỗ nó gọi `ProxyApp.ConfigureServices` (~dòng 66); (b) cấu hình sẵn của named client `provider-probe`/`free-model-sync` (base address, timeout, có `ConfigurePrimaryHttpMessageHandler` chưa — nếu **rồi** thì thay option trong handler cũ, không đăng ký lần 2); (c) `ProxyService` đã `AddSingleton` chưa; (d) `TimeProvider` đã đăng ký chưa.

- [ ] **Step 2: `DirectProxyPool` trong `TestDoubles.cs`**

Thêm (namespace/`partial` theo file hiện có):

```csharp
    /// <summary>Pool luôn trả direct — các test cũ không phụ thuộc behavior proxy.</summary>
    public sealed class DirectProxyPool : IProxyPool
    {
        public ProxyAttempt? GetNext() => null;

        public bool ReportFailure(long proxyId) => false;

        public void ReportSuccess(long proxyId)
        {
        }

        public void Invalidate()
        {
        }

        public IReadOnlyList<ProxyRuntimeStatus> Snapshot() => [];
    }
```

- [ ] **Step 3: `ProxyApp.ConfigureServices` ký mới + upstream wiring**

Ở `ProxyApp.cs`:

1. Ký hàm: `ConfigureServices(IServiceCollection services, ISecretProtector protector, IProxyPool pool)`.
2. Đăng ký pool **có guard tránh duplicate** (MauiProgram có thể đã đăng ký trước):

```csharp
    // Test container chỉ gọi ConfigureServices → pool instance (DirectProxyPool) vào;
    // app container có thể đã đăng ký ProxyPool trước → không ghi đè
    if (!services.Any(d => d.ServiceType == typeof(IProxyPool)))
    {
        services.AddSingleton(pool);
    }

    services.AddTransient<ProxyHealthHandler>();
```

3. Wire health vào **upstream client** của ProxyApp (named client / `AddHttpClient` đang tồn tại — bước 1 đã tìm ra):

```csharp
    services.AddHttpClient(/* TÊN CLIENT HIỆN CÓ */)
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            Proxy = new RoundRobinWebProxy(),
            UseProxy = true,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        .AddHttpMessageHandler<ProxyHealthHandler>();
```

*(Nếu client đã có `ConfigurePrimaryHttpMessageHandler` → giữ nguyên các option khác, chỉ thêm `Proxy`/`UseProxy`/`ConnectTimeout` vào SocketsHttpHandler đó.)*

4. `ProxyHost.cs`: thêm param `IProxyPool pool` vào ctor, forward vào `ProxyApp.ConfigureServices(builder, protector, pool)`.

5. Cập nhật **5 call site** `ProxyApp.ConfigureServices(builder, _protector)`:
   - `ProxyHost.cs` (đã inject ở trên);
   - 4 file integration tests → truyền `new DirectProxyPool()` (giữ test cũ không đổi behavior).
6. Cập nhật **3 chỗ `new ProxyHost(`** trong `ProxyHostTests.cs` → truyền `new DirectProxyPool()`.

- [ ] **Step 4: `MauiProgram.cs` wiring**

Thêm/chỉnh (giữ nguyên cấu hình sẵn của các client):

```csharp
using System.Net.Http;
using RouterBalancing.Core.Proxies;
```

```csharp
builder.Services.AddSingleton(TimeProvider.System);           // nếu chưa có — ProxyPool/ProxyEchoClient cần
builder.Services.AddSingleton<IProxyPool, ProxyPool>();       // singleton: giữ down-state + RR cursor
builder.Services.AddSingleton<IProxyEchoClient, ProxyEchoClient>();
builder.Services.AddTransient<ProxyHealthHandler>();          // transient per HttpClient pipeline
// IProxyService đã AddSingleton từ trước (đọc lại để xác nhận) — ctor mới do DI tự resolve
```

```csharp
// provider-probe + free-model-sync: 2 outbound point dùng proxy (spec §6)
builder.Services.AddHttpClient("provider-probe")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        Proxy = new RoundRobinWebProxy(),
        UseProxy = true,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    .AddHttpMessageHandler<ProxyHealthHandler>();

builder.Services.AddHttpClient("free-model-sync")
    /* y như trên */
    ;
```

*(Chú ý thứ tự fluent: `AddHttpClient(name)` lần 2 trả về builder cùng registry — config dồn vào 1 client, không tạo client thứ 3.)*

- [ ] **Step 5: Gates + Commit**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
```

Expected: 0 lỗi (0 warning mới), toàn bộ test pass (**497**), app build OK. App phải **đóng** trước khi build Windows. Nếu thiếu đăng ký DI (lỗi runtime mới — ví dụ `IProxyEchoClient` chưa resolve) → fix trong task này, không dời sang T9.

```powershell
git add -A
git commit -m "feat: wire outbound proxy pool into http client pipelines"
```

---

### Task 9: i18n + NavMenu + trang `Proxies.razor`

**Files:**
- Edit: `src/RouterBalancing.Core/Localization/Translations.cs` (33 key × 2 dict)
- Edit: `router-balancing/Components/Layout/NavMenu.razor`
- Create: `router-balancing/Components/Pages/Proxies.razor`

**Interfaces:**
- Consumes: `IProxyService`/`IProxyPool` (Tasks 2–8), pattern UI từ `Providers.razor` + `Modal` + `ConfirmDialog`.
- Produces: trang `/proxies` — smoke checklist Task 10.

- [ ] **Step 0: Đọc trước khi viết (bắt buộc)**

Đọc `Providers.razor` (pattern localizer key, form/EditForm, toast/message, toggle, ConfirmDialog), `Modal` component đang dùng, `IProxyService.cs` (tên method CRUD thật). **Page mới phải khớp 100% các pattern này** — không tự chế component/API mới.

- [ ] **Step 1: `Translations.cs` — chèn 33 key cuối mỗi dict**

Anchor: ngay sau entry `["accounts.confirm.delete"]` (EN ~dòng 269, VI ~dòng 529) — **cuối dictionary, trước dấu `};` của dict đó**.

```csharp
        // ===== EN (English dict) — chèn sau ["accounts.confirm.delete"] =====
        ["nav.proxies"] = "Proxies",
        ["proxies.title"] = "Outbound Proxy Pool",
        ["proxies.add"] = "Add Proxy",
        ["proxies.empty"] = "No proxies configured. Requests go direct until you add one.",
        ["proxies.col.endpoint"] = "Endpoint",
        ["proxies.col.auth"] = "Auth",
        ["proxies.col.enabled"] = "Enabled",
        ["proxies.col.status"] = "Status",
        ["proxies.col.lastTest"] = "Last test",
        ["proxies.col.actions"] = "Actions",
        ["proxies.status.down"] = "Down",
        ["proxies.status.ok"] = "Ready",
        ["proxies.auth.none"] = "None",
        ["proxies.lastTest.never"] = "Never",
        ["proxies.action.test"] = "Test",
        ["proxies.action.testing"] = "Testing...",
        ["proxies.form.addTitle"] = "Add outbound proxy",
        ["proxies.form.editTitle"] = "Edit outbound proxy",
        ["proxies.field.scheme"] = "Scheme",
        ["proxies.field.host"] = "Host",
        ["proxies.field.port"] = "Port",
        ["proxies.field.username"] = "Username",
        ["proxies.field.password"] = "Password",
        ["proxies.hint.password"] = "Leave blank to keep the current password.",
        ["proxies.schemes.hint"] = "http: HTTP proxy (CONNECT for HTTPS) · socks5: SOCKS5",
        ["proxies.error.host"] = "Host is required.",
        ["proxies.error.port"] = "Port must be between 1 and 65535.",
        ["proxies.error.duplicate"] = "A proxy with this host already exists.",
        ["proxies.error.scheme"] = "Scheme must be http or socks5.",
        ["proxies.msg.saved"] = "Proxy saved.",
        ["proxies.msg.deleted"] = "Proxy deleted.",
        ["proxies.msg.testFailed"] = "Proxy test failed.",
        ["proxies.confirm.delete"] = "Delete this proxy?",
```

```csharp
        // ===== VI (Vietnamese dict) — chèn cùng vị trí =====
        ["nav.proxies"] = "Proxy",
        ["proxies.title"] = "Pool Proxy Outbound",
        ["proxies.add"] = "Thêm proxy",
        ["proxies.empty"] = "Chưa cấu hình proxy nào. Request đi trực tiếp cho tới khi bạn thêm proxy.",
        ["proxies.col.endpoint"] = "Endpoint",
        ["proxies.col.auth"] = "Xác thực",
        ["proxies.col.enabled"] = "Bật/tắt",
        ["proxies.col.status"] = "Trạng thái",
        ["proxies.col.lastTest"] = "Lần test cuối",
        ["proxies.col.actions"] = "Thao tác",
        ["proxies.status.down"] = "Lỗi",
        ["proxies.status.ok"] = "Sẵn sàng",
        ["proxies.auth.none"] = "Không",
        ["proxies.lastTest.never"] = "Chưa test",
        ["proxies.action.test"] = "Test",
        ["proxies.action.testing"] = "Đang test...",
        ["proxies.form.addTitle"] = "Thêm proxy outbound",
        ["proxies.form.editTitle"] = "Sửa proxy outbound",
        ["proxies.field.scheme"] = "Scheme",
        ["proxies.field.host"] = "Host",
        ["proxies.field.port"] = "Port",
        ["proxies.field.username"] = "Tên đăng nhập",
        ["proxies.field.password"] = "Mật khẩu",
        ["proxies.hint.password"] = "Để trống để giữ nguyên mật khẩu hiện tại.",
        ["proxies.schemes.hint"] = "http: HTTP proxy (CONNECT cho HTTPS) · socks5: SOCKS5",
        ["proxies.error.host"] = "Host là bắt buộc.",
        ["proxies.error.port"] = "Port phải từ 1 đến 65535.",
        ["proxies.error.duplicate"] = "Đã tồn tại proxy với host này.",
        ["proxies.error.scheme"] = "Scheme phải là http hoặc socks5.",
        ["proxies.msg.saved"] = "Đã lưu proxy.",
        ["proxies.msg.deleted"] = "Đã xóa proxy.",
        ["proxies.msg.testFailed"] = "Test proxy thất bại.",
        ["proxies.confirm.delete"] = "Xóa proxy này?",
```

**Kiểm tra:** đủ 33 key mỗi dict; **không** HTML entity (RazorParameterEntityTests); file UTF-8.

- [ ] **Step 2: `NavMenu.razor` — NavLink `/proxies` chèn ngay sau mục Logs**

Copy nguyên cấu trúc NavLink hiện có (class icon `bi-*` + span text), đổi href + key:

```razor
    <div class="nav-item px-3">
        <NavLink class="nav-link" href="proxies">
            <span class="bi bi-globe2" aria-hidden="true"></span> @T["nav.proxies"]
        </NavLink>
    </div>
```

*(Giữ đúng icon-set + markup mà các mục nav khác đang dùng — nếu icon class khác format trên thì theo chúng.)*

- [ ] **Step 3: `Proxies.razor` — trang CRUD + Test + Status**

Đọc `Providers.razor` xong → viết full page theo skeleton dưới đây, **thay các placeholder inject/pattern bằng tên thật**:

```razor
@page "/proxies"
@using RouterBalancing.Core.Proxies
@inject IProxyService ProxyService
@inject IProxyPool ProxyPool
@* Thêm đúng inject localizer/toast/ConfirmDialog mà Providers.razor dùng *@

<PageTitle>@T["proxies.title"]</PageTitle>

<h3>@T["proxies.title"]</h3>

<button class="btn btn-primary" @onclick="OpenAdd">@T["proxies.add"]</button>

@if (_rows.Count == 0 && !_loading)
{
    <p class="text-muted mt-3">@T["proxies.empty"]</p>
}
else
{
    <table class="table mt-3">
        <thead>
            <tr>
                <th>@T["proxies.col.endpoint"]</th>
                <th>@T["proxies.col.auth"]</th>
                <th>@T["proxies.col.enabled"]</th>
                <th>@T["proxies.col.status"]</th>
                <th>@T["proxies.col.lastTest"]</th>
                <th>@T["proxies.col.actions"]</th>
            </tr>
        </thead>
        <tbody>
            @foreach (var row in _rows)
            {
                @* @key để toggle/test không re-render cả bảng *@
                <tr @key="row.Id">
                    <td>@EndpointOf(row)</td>
                    <td>@(row.Username ?? T["proxies.auth.none"])</td>
                    <td>
                        @* Toggle theo đúng pattern enable/disable đang có trong Providers.razor *@
                        <button class="btn btn-sm @(row.Enabled ? "btn-outline-secondary" : "btn-outline-dark")"
                                @onclick="() => ToggleAsync(row)">
                            @(row.Enabled ? "ON" : "OFF")
                        </button>
                    </td>
                    <td>
                        @if (_downIds.Contains(row.Id))
                        {
                            <span class="text-danger">@T["proxies.status.down"]</span>
                        }
                        else
                        {
                            <span>@T["proxies.status.ok"]</span>
                        }
                    </td>
                    <td>
                        @if (row.LastTestAt is null)
                        {
                            @T["proxies.lastTest.never"]
                        }
                        else
                        {
                            @(row.LastTestAt.Value.ToLocalTime().ToString("g"))
                            @if (row.LastTestSuccess == false)
                            {
                                <div class="text-danger small">@row.LastTestMessage</div>
                            }
                        }
                    </td>
                    <td>
                        <button class="btn btn-sm btn-outline-primary" disabled="@(_testingId == row.Id)"
                                @onclick="() => TestAsync(row)">
                            @(_testingId == row.Id ? T["proxies.action.testing"] : T["proxies.action.test"])
                        </button>
                        <button class="btn btn-sm btn-outline-secondary"
                                @onclick="() => OpenEdit(row)">@T["providers.action.edit"]</button>
                        <button class="btn btn-sm btn-outline-danger"
                                @onclick="() => AskDelete(row)">@T["providers.action.delete"]</button>
                    </td>
                </tr>
            }
        </tbody>
    </table>
}

@* Modal form + ConfirmDialog: copy đúng component usage từ Providers.razor,
   tiêu đề form đổi theo _editing.Id (addTitle/editTitle), fields:
   scheme (select http/socks5), host, port, username, password (input password,
   placeholder hint.password khi đang edit), hint schemes.hint, lỗi hiển thị qua _formError *@

@code {
    private List<OutboundProxy> _rows = [];
    private HashSet<long> _downIds = [];
    private bool _loading;
    private OutboundProxy? _editing;          // null = đóng modal; Id == 0 = add mới
    private long? _testingId;
    private string? _formError;
    private string _scheme = "http";
    private string _host = string.Empty;
    private int _port = 8080;
    private string _username = string.Empty;
    private string _password = string.Empty;   // rỗng = giữ mật khẩu cũ khi edit
    // Timer pattern theo Providers.razor (30s) — PeriodicTimer/Task + dispose

    protected override async Task OnInitializedAsync()
    {
        await ReloadAsync();
        // start refresh loop y như Providers.razor (30s): reload rows + downIds
    }

    private async Task ReloadAsync()
    {
        _rows = await ProxyService.ListAsync();          // theo tên method thật
        _downIds = ProxyPool.Snapshot().Where(s => s.IsDown).Select(s => s.Id).ToHashSet();
        _loading = false;
        StateHasChanged();
    }

    private static string EndpointOf(OutboundProxy row) =>
        $"{row.Scheme}://{row.Host}:{row.Port}";

    private void OpenAdd()
    {
        _editing = new OutboundProxy();
        _scheme = "http"; _host = string.Empty; _port = 8080;
        _username = string.Empty; _password = string.Empty; _formError = null;
    }

    private void OpenEdit(OutboundProxy row)
    {
        _editing = row;
        _scheme = row.Scheme; _host = row.Host; _port = row.Port;
        _username = row.Username ?? string.Empty;
        _password = string.Empty; _formError = null;  // giữ mật khẩu cũ
    }

    private async Task SaveAsync()
    {
        // Validate client theo pattern Providers: host rỗng → proxies.error.host;
        // port 1..65535 → proxies.error.port; scheme ∈ {http, socks5} → proxies.error.scheme
        // Gọi ProxyService.SaveAsync(...) (tên thật) với password chỉ gửi khi khác rỗng;
        // catch ProxyValidationException → hiện ex.Message (server kiểm duplicate) —
        //   chứa "duplicate" → proxies.error.duplicate, nếu server đã map sẵn thì dùng key đó
        // thành công → toast proxies.msg.saved + ReloadAsync + đóng modal
    }

    private async Task ToggleAsync(OutboundProxy row)
    {
        // gọi method toggle/toggle-enabled của IProxyService — pattern Providers
        await ReloadAsync();
    }

    private async Task TestAsync(OutboundProxy row)
    {
        _testingId = row.Id;
        try
        {
            var result = await ProxyService.TestAsync(row.Id, CancellationToken.None);
            if (!result.Success)
            {
                // toast/message proxies.msg.testFailed — lỗi chi tiết đã hiện trong cột Last test
            }
        }
        finally
        {
            _testingId = null;
            await ReloadAsync();
        }
    }

    private void AskDelete(OutboundProxy row) { /* mở ConfirmDialog — copy Providers */ }

    private async Task DeleteAsync()
    {
        // ProxyService.DeleteAsync(...) → toast proxies.msg.deleted + ReloadAsync
    }
}
```

**Checklist bắt buộc trước khi xong task:** Razor UTF-8 trực tiếp (không `&#nnnn;`); mọi text hiển thị đi qua key (có key trong 2 dict); không logic business trong Razor beyond gọi service; downIds refresh theo timer như Providers; test button disabled khi đang chạy.

- [ ] **Step 4: Gates + Commit**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
```

Expected: 0 lỗi (kể cả RazorParameterEntityTests), toàn bộ test pass (**497**), app build OK (app đóng trước khi build).

```powershell
git add -A
git commit -m "feat: add outbound proxies management page"
```

---

### Task 10: Final verification + smoke + self-review

**Files:**
- Edit (chỉ nếu phát hiện lỗi): file tương ứng → commit riêng `fix:` cho lỗi đó.
- Edit: `.superpowers/sdd/progress.md` (ledger — ghi item 5 outcome).

- [ ] **Step 1: Full gates cuối**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
```

Expected: **0 lỗi, 0 warning mới, toàn bộ test pass (≈497 — nếu lệch thì đối chiếu task nào, không bịa số)**. App đóng trước khi build. Flakes đã biết (ProxyControlApiTests teardown IOException, ProxyRetry/QueueIntegration) → chạy lại lần 2 trước khi kết luận fail.

- [ ] **Step 2: Repo hygiene**

```powershell
git status            # sạch — không file lạ/untracked ngoài .superpowers/
git log --oneline -15 # đủ 10 commit T0..T9, message conventional, một việc/commit
git diff main...HEAD --stat   # (hoặc feat/retry-circuit-3c) — chỉ file thuộc plan
```

- [ ] **Step 3: Placeholder sweep** — dùng **Grep tool**:

   - pattern `REPLACE_WITH_GENERATED_TARGET_ID`, path `router balancing test` → **PHẢI rỗng** (đã điền ID thật ở T1).
   - pattern `TODO`, include `*.cs`/`*.razor` → không có TODO mới vô chủ.
   - pattern `&#`, path `router-balancing/Components/Pages` → rỗng (Razor UTF-8 trực tiếp, không HTML entity).

- [ ] **Step 4: Smoke thủ công trên app Windows (spec §9)**

```powershell
dotnet run --project router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
```

Checklist (tick lần lượt, fail → ghi issue + fix commit `fix:`):
1. Nav có mục **Proxies** → mở `/proxies` thấy empty state (key `proxies.empty`).
2. **Add** proxy `http://127.0.0.1:<cổng stub hoặc port chết>` → lưu được, toast `proxies.msg.saved`, hàng hiện trong bảng.
3. Thêm proxy **cùng host** (khác port) → báo `proxies.error.duplicate` (server-side).
4. Toggle ON/OFF → đổi trạng thái, persist sau khi reload app.
5. **Test** với port chết → cột Last test hiện thời gian + lỗi đỏ (`LastTestSuccess=false`); với proxy/stub sống (hoặc `http://api.ipify.org` nếu có mạng) → Success.
6. **Delete** → ConfirmDialog (`proxies.confirm.delete` + `confirm.cancel`) → xóa, toast `proxies.msg.deleted`.
7. Với pool **rỗng** (xóa hết / tắt hết): app vẫn sync/probe bình thường (direct) — không lỗi.
8. Đổi ngôn ngữ EN/VI → toàn bộ text trang proxy dịch đúng (33 key ×2).

- [ ] **Step 5: Self-review plan ↔ spec**

Soi lại `docs/superpowers/specs/2026-10-01-proxy-pool-design.md` từng mục §1–§11: mỗi yêu cầu có ít nhất 1 test/unit test hoặc checklist smoke tương ứng; quyết định Design decisions 1–14 trong plan không bị đảo ở code thật; comment code tiếng Việt why-not-what; không nuốt exception; không secret/log credential.

- [ ] **Step 6: Ledger + báo cáo**

Ghi `.superpowers/sdd/progress.md`: item 5 = COMPLETE (hoặc BLOCKED + lý do), danh sách commit, test count cuối. Báo user xong — **không merge/push**; hỏi user cách integrate (commit trực tiếp trên feat/retry-circuit-3c đã là xong, hay cần gì thêm).

---

## Self-review checklist (cuối cùng, trước khi báo hoàn thành plan)

- [ ] Không còn sentinel kết thúc (`NEXT` marker) trong file.
- [ ] Mọi path file trong plan tồn tại hoặc được tạo đúng chỗ (grep thử 10 path ngẫu nhiên).
- [ ] Mọi lệnh `dotnet ...` trong plan chạy được từ repo root (cwd = `D:\Code\router-balancing`).
- [ ] Test name trong plan khớp file/class thực sự được tạo ở task tương ứng.
- [ ] Expected test count nhất quán giữa các task (449 → 462 → 474 → 485 → 489 → 491 → 497 → 497).
- [ ] STOP rules (T6 Gate B) và "đọc trước khi sửa" (T8 Step 1, T9 Step 0) còn nguyên vẹn.
- [ ] Không commit/push trong lúc viết plan — commit `docs:` duy nhất cho chính plan này.
