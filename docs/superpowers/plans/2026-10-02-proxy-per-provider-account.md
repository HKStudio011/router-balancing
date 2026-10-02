# Proxy theo Provider & Account Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Gán proxy outbound theo provider và account (M2M, nhiều proxy, mode Robin/Fallback), không gán → Direct; request path + probe/sync respect proxy của provider/account; UI quản lý gán.

**Architecture:** 2 junction table `ProviderProxies`/`ProviderAccountProxies` + `ProxyMode?` trên `Provider`/`ProviderAccount`; `IProxySelectionResolver` đọc entity đã-load để chọn (most-specific-wins, account override provider); `ProxyTarget` (AsyncLocal) mang (provider, account) vào `ProxyHealthHandler` để dispatch Direct/RoundRobin/Fallback. Health/cooldown toàn cầu per-endpoint giữ nguyên trên `ProxyPool`; `RoundRobinWebProxy`/`ProxyContext`/`DynamicProxyCredentials` không đổi. Context null (path nội bộ/test) → giữ behavior global pool cũ.

**Tech Stack:** .NET 10, EF Core SQLite (SQLite migrations), xUnit, MAUI Blazor Hybrid, Tailwind (pre-compile).

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên.
- Comment tiếng Việt cho "tại sao"; XML doc cho public API; exception message tiếng Anh.
- Không log password/credentials; không render `PasswordEncrypted`.
- 1 task = 1 commit; message tiếng Anh, conventional (`feat:`/`fix:`/`test:`/`docs:`).
- Test infra: `TestDb` (file SQLite trong temp), `DbInitializer.Initialize(factory)`, `DpapiSecretProtector` (trong `TestDoubles.cs`), `NullLog`.
- Gates mỗi task (từ repo root, app `router-balancing` phải đóng):
  1. `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj`
  2. `dotnet test "router balancing test/router balancing test.csproj"`
- **Known flakes** (rerun → PASS, đừng chấp nhận fail thật): `ProxyControlApiTests`, `ProxyRetryIntegrationTests`, `ProxyQueueIntegrationTests`.
- i18n: key mới chèn cuối dict `English` + `Vietnamese` trong `src/RouterBalancing.Core/Localization/Translations.cs`, sau key cuối hiện tại (`proxies.status.downUntil`); không dùng HTML entity cho tiếng Việt.
- **Không** build `.slnx`; build app chỉ `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0` (Task 7).
- Migration: sinh bằng `dotnet ef migrations add <name>` (EF tool có sẵn, .NET 10.0.401). Migration mới là `20261002XXXXXX_AddProxyAssignments`.

---

## File Map (decomposition)

| File | Trách nhiệm |
|---|---|
| `src/.../Domain/Enums/ProxyMode.cs` (mới) | Enum mode gán proxy |
| `src/.../Domain/Entities/ProviderProxy.cs` (mới) | Junction provider↔proxy |
| `src/.../Domain/Entities/ProviderAccountProxy.cs` (mới) | Junction account↔proxy |
| `src/.../Domain/Entities/Provider.cs` (sửa) | `ProxyMode?`, `ProviderProxies` |
| `src/.../Domain/Entities/ProviderAccount.cs` (sửa) | `ProxyMode?`, `AccountProxies` |
| `src/.../Storage/RouterBalancingDbContext.cs` (sửa) | Cấu hình 2 junction |
| `src/.../Storage/Migrations/20261002XXXXXX_AddProxyAssignments.cs` (mới) | Migration |
| `src/.../Proxies/ProxySelection.cs` (mới) | Record kết quả chọn |
| `src/.../Proxies/ProxyTarget.cs` (mới) | AsyncLocal (provider, account) |
| `src/.../Proxies/IProxySelectionResolver.cs` + `ProxySelectionResolver.cs` (mới) | Chọn most-specific-wins |
| `src/.../Proxies/IProxyPool.cs` + `ProxyPool.cs` (sửa) | `GetNext(allowedIds)`, `GetLivingInOrder(ids)` |
| `src/.../Proxies/ProxyHealthHandler.cs` (sửa) | Dispatch Direct/RoundRobin/Fallback |
| `src/.../Proxies/IProxyService.cs` + `ProxyService.cs` (sửa) | CRUD assignment |
| `src/.../Providers/ProviderKeyResolver.cs` (sửa) | `ResolveFirstEnabledAccount` |
| `src/.../Engine/ChatCompletionsHandler.cs` (sửa) | Set `ProxyTarget.Current` |
| `src/.../Providers/ProviderService.cs` (sửa) | Set context trong test connection |
| `src/.../Providers/ProviderAccountService.cs` (sửa) | Set context trong test all |
| `src/.../Providers/FreeModelSyncService.cs` (sửa) | Set context trong send |
| `src/.../Server/ProxyApp.cs` + `router-balancing/MauiProgram.cs` (sửa) | Đăng ký `IProxySelectionResolver` |
| `router-balancing/Components/Pages/Proxies.razor` (sửa) | Section gán proxy |
| `src/.../Localization/Translations.cs` (sửa) | i18n keys mới |
| Test: `Storage/AddProxyAssignmentsMigrationTests.cs`, `Proxies/ProxySelectionResolverTests.cs`, `Proxies/ProxyHealthHandlerTests.cs` (bổ sung), `Proxies/ProxyPoolTests.cs` (bổ sung), `Proxies/ProxyServiceTests.cs` (bổ sung), `Proxies/ProxyAssignmentIntegrationTests.cs` (mới) |

---

### Task 1: Data model + migration + migration test

**Files:**
- Create: `src/RouterBalancing.Core/Domain/Enums/ProxyMode.cs`
- Create: `src/RouterBalancing.Core/Domain/Entities/ProviderProxy.cs`
- Create: `src/RouterBalancing.Core/Domain/Entities/ProviderAccountProxy.cs`
- Modify: `src/RouterBalancing.Core/Domain/Entities/Provider.cs`
- Modify: `src/RouterBalancing.Core/Domain/Entities/ProviderAccount.cs`
- Modify: `src/RouterBalancing.Core/Storage/RouterBalancingDbContext.cs`
- Create: `src/RouterBalancing.Core/Storage/Migrations/20261002XXXXXX_AddProxyAssignments.cs` (via EF)
- Test: `router balancing test/Storage/AddProxyAssignmentsMigrationTests.cs`

**Interfaces:**
- Produces: enum `ProxyMode { RoundRobin, Fallback }`; junctions `ProviderProxy(ProviderId, ProxyId)`, `ProviderAccountProxy(AccountId, ProxyId)`; `Provider.ProxyMode?`, `Provider.ProviderProxies` (List<ProviderProxy>); `ProviderAccount.ProxyMode?`, `ProviderAccount.AccountProxies` (List<ProviderAccountProxy>).
- Consumes: none (new).

- [ ] **Step 1: Create `ProxyMode.cs`**

```csharp
namespace RouterBalancing.Core.Domain.Enums;

/// <summary>
/// Cách dùng tập proxy đã gán cho provider/account (spec proxy-per-provider §2):
/// <c>RoundRobin</c> = RR + cooldown; <c>Fallback</c> = thử theo ProxyId tăng, fail → kế.
/// </summary>
public enum ProxyMode
{
    RoundRobin = 0,
    Fallback = 1,
}
```

- [ ] **Step 2: Create junction entities**

`ProviderProxy.cs`:
```csharp
namespace RouterBalancing.Core.Domain.Entities;

/// <summary>Junction Provider ↔ OutboundProxy — gán proxy cho provider (M2M).</summary>
public class ProviderProxy
{
    public long ProviderId { get; set; }
    public long ProxyId { get; set; }

    public Provider? Provider { get; set; } = null!;
    public OutboundProxy? Proxy { get; set; } = null!;
}
```

`ProviderAccountProxy.cs`:
```csharp
namespace RouterBalancing.Core.Domain.Entities;

/// <summary>Junction ProviderAccount ↔ OutboundProxy — gán proxy cho account (override provider).</summary>
public class ProviderAccountProxy
{
    public long AccountId { get; set; }
    public long ProxyId { get; set; }

    public ProviderAccount? Account { get; set; } = null!;
    public OutboundProxy? Proxy { get; set; } = null!;
}
```

- [ ] **Step 3: Add properties to `Provider.cs`** — sau `public List<Model> Models` (line ~41), thêm:

```csharp
    /// <summary>Proxy outbound gán cho provider (M2M). Rỗng = chưa gán → direct (D2).</summary>
    public List<ProviderProxy> ProviderProxies { get; set; } = [];

    /// <summary>Mode dùng tập proxy trên; null = kế thừa từ account (nếu có) hoặc RoundRobin.</summary>
    public ProxyMode? ProxyMode { get; set; }
```
(thêm `using RouterBalancing.Core.Domain.Entities;` không cần — cùng namespace `Domain` qua `Domain/Entities`; nhưng `ProxyMode` trong `Domain/Enums` → thêm `using RouterBalancing.Core.Domain.Enums;` ở đầu file.)

- [ ] **Step 4: Add properties to `ProviderAccount.cs`** — cuối file, thêm:

```csharp
    /// <summary>Proxy outbound gán cho account (override provider). Rỗng = kế thừa provider.</summary>
    public List<ProviderAccountProxy> AccountProxies { get; set; } = [];

    /// <summary>Mode dùng tập proxy trên; null = kế thừa provider.</summary>
    public ProxyMode? ProxyMode { get; set; }
```

- [ ] **Step 5: DbContext junction config** — trong `OnModelCreating`, sau block `OutboundProxy` (trước `}` đóng method), thêm:

```csharp
        modelBuilder.Entity<ProviderProxy>(e =>
        {
            e.HasKey(x => new { x.ProviderId, x.ProxyId });
            e.HasOne(x => x.Provider)
                .WithMany(p => p.ProviderProxies)
                .HasForeignKey(x => x.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Proxy)
                .WithMany()
                .HasForeignKey(x => x.ProxyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProviderAccountProxy>(e =>
        {
            e.HasKey(x => new { x.AccountId, x.ProxyId });
            e.HasOne(x => x.Account)
                .WithMany(a => a.AccountProxies)
                .HasForeignKey(x => x.AccountId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Proxy)
                .WithMany()
                .HasForeignKey(x => x.ProxyId)
                .OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 6: Generate migration**

Run (từ repo root):
```bash
dotnet ef migrations add AddProxyAssignments
```
Mileage: file `20261002XXXXXX_AddProxyAssignments.cs` + `.Designer.cs` tạo mới. Mở file, kiểm tra `Up()` tạo 2 bảng + `ALTER TABLE Provider ADD ProxyMode` + `ALTER TABLE ProviderAccounts ADD ProxyMode`; `Down()` xóa 2 bảng + drop 2 cột.

- [ ] **Step 7: Migration test** — tạo `router balancing test/Storage/AddProxyAssignmentsMigrationTests.cs`, pattern `AddOutboundProxyMigrationTests.cs`:

```csharp
// router balancing test/Storage/AddProxyAssignmentsMigrationTests.cs
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

/// <summary>
/// Round-trip schema AddProxyAssignments: Up thêm 2 junction table + 2 cột ProxyMode;
/// Down xóa. Pattern AddOutboundProxyMigrationTests.
/// </summary>
public class AddProxyAssignmentsMigrationTests : IDisposable
{
    private const string PreviousId = "20261002005636_AddOutboundProxies";
    // Điền sau khi chạy dotnet ef (Task 1 Step 6) — dạng <ts>_AddProxyAssignments
    private const string TargetId = "20261002XXXXXX_AddProxyAssignments";
    private const string Timestamp = "'2026-10-02T00:00:00+00:00'";

    private readonly TestDb _testDb = new();

    public void Dispose() => _testDb.Dispose();

    [Fact]
    public void Up_CreatesJunctionTablesAndModeColumns()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProviderProxies'")));
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProviderAccountProxies'")));

        db.GetService<IMigrator>().Migrate(TargetId);

        // 2 junction table xuất hiện
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProviderProxies'")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProviderAccountProxies'")));

        // Cột ProxyMode xuất hiện trên Provider + ProviderAccounts
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('Provider') WHERE name='ProxyMode'")));
        Assert.Equal(1L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM pragma_table_info('ProviderAccounts') WHERE name='ProxyMode'")));
    }

    [Fact]
    public void Down_DropsJunctionTablesAndModeColumns()
    {
        using var db = _testDb.CreateFactory().CreateDbContext();
        db.GetService<IMigrator>().Migrate(TargetId);

        db.Database.ExecuteSqlRaw($"""
            INSERT INTO ProviderProxies (ProviderId, ProxyId) VALUES (1, 2);
            INSERT INTO ProviderAccountProxies (AccountId, ProxyId) VALUES (3, 2);
            """);

        db.GetService<IMigrator>().Migrate(PreviousId);

        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProviderProxies'")));
        Assert.Equal(0L, Convert.ToInt64(Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='ProviderAccountProxies'")));
    }

    private object? Scalar(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={_testDb.DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
```
(Sửa `TargetId` + tên PK theo migration thực tế sau khi `dotnet ef` chạy.)

- [ ] **Step 8: Run gates + commit**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
git add -A
git commit -m "feat: add proxy assignment data model and migration"
```
Expected: build 0 lỗi/warning; test PASSED (mới +1 test).

---

### Task 2: ProxySelection/ProxyTarget + Resolver + tests

**Files:**
- Create: `src/RouterBalancing.Core/Proxies/ProxySelection.cs`
- Create: `src/RouterBalancing.Core/Proxies/ProxyTarget.cs`
- Create: `src/RouterBalancing.Core/Proxies/IProxySelectionResolver.cs`
- Create: `src/RouterBalancing.Core/Proxies/ProxySelectionResolver.cs`
- Test: `router balancing test/Proxies/ProxySelectionResolverTests.cs`

**Interfaces:**
- Produces: `ProxySelection(ProxyMode? Mode, IReadOnlyList<long>? ProxyIds)` + `ProxySelection.Direct` + `IsDirect`; `ProxyTarget(Provider, ProviderAccount?)` + `ProxyTarget.Current` (AsyncLocal); `IProxySelectionResolver.Resolve(Provider, ProviderAccount?) → ProxySelection`; `ProxySelectionResolver`.
- Consumes: `ProxyMode`, `Provider`, `ProviderAccount`, `ProviderProxies`, `AccountProxies` (Task 1).

- [ ] **Step 1: Create `ProxySelection.cs`**

```csharp
using RouterBalancing.Core.Domain.Enums;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Kết quả chọn proxy cho 1 request (spec proxy-per-provider §4):
/// Direct = không proxy (không health); có <c>ProxyIds</c> = dùng tập đó với <c>Mode</c>.
/// </summary>
public sealed record ProxySelection(ProxyMode? Mode, IReadOnlyList<long>? ProxyIds)
{
    /// <summary>Tất cả proxy tắt — không proxy, không health (D2).</summary>
    public static readonly ProxySelection Direct = new(null, null);

    /// <summary>Sẽ dùng proxy (tập + mode), trái ngược Direct.</summary>
    public bool IsDirect => ProxyIds is null;
}
```

- [ ] **Step 2: Create `ProxyTarget.cs`**

```csharp
using RouterBalancing.Core.Domain.Entities;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Mục tiêu proxy của request hiện tại — <see cref="AsyncLocal{T}"/> để
/// <see cref="ProxyHealthHandler"/> đọc mà không cần truyền qua ctor (spec §4.4).
/// <c>null</c> = request không qua assignment (path nội bộ/test) → handler giữ
/// behavior global pool (D7).
/// </summary>
public sealed record ProxyTarget(Provider Provider, ProviderAccount? Account)
{
    private static readonly AsyncLocal<ProxyTarget?> s_current = new();

    /// <summary>Request đang xử lý; reset <c>null</c> sau khi xong (finally).</summary>
    public static AsyncLocal<ProxyTarget?> Current => s_current;
}
```

- [ ] **Step 3: Create `IProxySelectionResolver.cs`**

```csharp
using RouterBalancing.Core.Domain.Entities;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// Chọn proxy theo Provider + Account (most-specific-wins, D1): account có proxy →
/// override provider; không thì provider; không thì Direct.
/// </summary>
public interface IProxySelectionResolver
{
    /// <summary>
    /// Entity provider/account phải đã load junction (Include ProviderProxies /
    /// AccountProxies) — resolver chỉ đọc, không query DB.
    /// </summary>
    ProxySelection Resolve(Provider provider, ProviderAccount? account);
}
```

- [ ] **Step 4: Create `ProxySelectionResolver.cs`**

```csharp
using RouterBalancing.Core.Domain.Entities;

namespace RouterBalancing.Core.Proxies;

/// <summary>
/// most-specific-wins (D1): account gán proxy → dùng tập + mode của account;
/// không thì provider; không thì Direct. Mode inherit:
/// account.Mode ?? provider.Mode ?? RoundRobin (D6).
/// </summary>
public sealed class ProxySelectionResolver : IProxySelectionResolver
{
    public ProxySelection Resolve(Provider provider, ProviderAccount? account)
    {
        var accountProxies = account?.AccountProxies;
        if (accountProxies is not null && accountProxies.Count > 0)
        {
            return new ProxySelection(
                account.ProxyMode ?? provider.ProxyMode ?? ProxyMode.RoundRobin,
                accountProxies.Select(x => x.ProxyId).ToList());
        }

        var providerProxies = provider.ProviderProxies;
        if (providerProxies is not null && providerProxies.Count > 0)
        {
            return new ProxySelection(
                provider.ProxyMode ?? ProxyMode.RoundRobin,
                providerProxies.Select(x => x.ProxyId).ToList());
        }

        return ProxySelection.Direct;
    }
}
```

- [ ] **Step 5: Write failing test** — `router balancing test/Proxies/ProxySelectionResolverTests.cs`:

```csharp
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Domain.Entities;
using RouterBalancing.Core.Proxies;

namespace router_balancing_test.Proxies;

/// <summary>Resolver most-specific-wins (D1) + mode inherit (D6) — không cần DB.</summary>
public class ProxySelectionResolverTests
{
    private static readonly IProxySelectionResolver Resolver = new ProxySelectionResolver();

    private static Provider ProviderWithProxies(params long[] ids) => new()
    {
        Id = 1,
        Name = "Test",
        Type = ProviderType.OpenAI,
        BaseUrl = "https://api.example.com",
        ProxyMode = ProxyMode.RoundRobin,
        ProviderProxies = ids.Select(id => new ProviderProxy { ProviderId = 1, ProxyId = id }).ToList(),
    };

    private static ProviderAccount AccountWithProxies(params long[] ids) => new()
    {
        Id = 10,
        ProviderId = 1,
        Name = "Acc",
        AccountProxies = ids.Select(id => new ProviderAccountProxy { AccountId = 10, ProxyId = id }).ToList(),
    };

    [Fact]
    public void Resolve_NoProxies_ReturnsDirect()
    {
        var sel = Resolver.Resolve(ProviderWithProxies(), null);
        Assert.Same(ProxySelection.Direct, sel);
        Assert.True(sel.IsDirect);
        Assert.Null(sel.ProxyIds);
    }

    [Fact]
    public void Resolve_ProviderOnly_ReturnsProviderSetAndMode()
    {
        var provider = ProviderWithProxies(100, 200);
        provider.ProxyMode = ProxyMode.Fallback;
        var sel = Resolver.Resolve(provider, null);
        Assert.False(sel.IsDirect);
        Assert.Equal(ProxyMode.Fallback, sel.Mode);
        Assert.Equal(new[] { 100L, 200L }, sel.ProxyIds!.ToList());
    }

    [Fact]
    public void Resolve_AccountOnly_ReturnsAccountSet()
    {
        var provider = new Provider { Id = 1, Name = "Test", Type = ProviderType.OpenAI, BaseUrl = "https://a.com" };
        var account = AccountWithProxies(300, 400);
        account.ProxyMode = ProxyMode.Fallback;
        var sel = Resolver.Resolve(provider, account);
        Assert.Equal(ProxyMode.Fallback, sel.Mode);
        Assert.Equal(new[] { 300L, 400L }, sel.ProxyIds!.ToList());
    }

    [Fact]
    public void Resolve_AccountOverridesProvider_UsesAccountSet()
    {
        var provider = ProviderWithProxies(100, 200);
        var account = AccountWithProxies(300, 400);
        account.ProxyMode = ProxyMode.Fallback;
        var sel = Resolver.Resolve(provider, account);
        Assert.Equal(ProxyMode.Fallback, sel.Mode);
        Assert.Equal(new[] { 300L, 400L }, sel.ProxyIds!.ToList()); // không phải [100,200]
    }

    [Fact]
    public void Resolve_ModeInherit_AccountNullProviderMode()
    {
        var provider = ProviderWithProxies(100);
        provider.ProxyMode = ProxyMode.Fallback;
        var account = new ProviderAccount { Id = 10, ProviderId = 1, Name = "Acc" }; // ProxyMode null
        var sel = Resolver.Resolve(provider, account);
        Assert.Equal(ProxyMode.Fallback, sel.Mode);
    }

    [Fact]
    public void Resolve_ModeInherit_BothNull_ReturnsRoundRobin()
    {
        var provider = ProviderWithProxies(100);
        provider.ProxyMode = null;
        var account = new ProviderAccount { Id = 10, ProviderId = 1, Name = "Acc" };
        account.ProxyMode = null;
        var sel = Resolver.Resolve(provider, account);
        Assert.Equal(ProxyMode.RoundRobin, sel.Mode);
    }

    [Fact]
    public void Resolve_EmptyAccountProxies_FallsThroughToProvider()
    {
        var provider = ProviderWithProxies(100);
        var account = new ProviderAccount { Id = 10, ProviderId = 1, Name = "Acc", AccountProxies = [] };
        var sel = Resolver.Resolve(provider, account);
        Assert.Equal(new[] { 100L }, sel.ProxyIds!.ToList());
    }
}
```

- [ ] **Step 6: Run test**

```bash
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedDisplayName~ProxySelectionResolverTests"
```
Expected: 7 PASSED.

- [ ] **Step 7: Run full gates + commit**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
git add -A
git commit -m "feat: add proxy selection resolver and proxy target context"
```

---

### Task 3: IProxyPool/ProxyPool extension + tests

**Files:**
- Modify: `src/RouterBalancing.Core/Proxies/IProxyPool.cs`
- Modify: `src/RouterBalancing.Core/Proxies/ProxyPool.cs`
- Test: `router balancing test/Proxies/ProxyPoolTests.cs` (bổ sung)

**Interfaces:**
- Produces: `IProxyPool.GetNext(IReadOnlyList<long>? allowedIds)`; `IProxyPool.GetLivingInOrder(IReadOnlyList<long> ids)`.
- Consumes: existing pool state (`_entries`, `_downUntil`, `_cursor`, `IsDown`, `EnsureLoaded`).

- [ ] **Step 1: Extend `IProxyPool.cs`** — thêm 2 method sau `GetNext()`:

```csharp
    /// <summary>
    /// Chọn proxy kế theo round-robin trong số proxy sống, chỉ trong tập
    /// <paramref name="allowedIds"/> khi có (null = toàn bộ, như <see cref="GetNext"/>).
    /// Trả <see langword="null"/> = direct (không còn proxy sống trong tập).
    /// </summary>
    ProxyAttempt? GetNext(IReadOnlyList<long>? allowedIds);

    /// <summary>
    /// Proxy sống trong <paramref name="ids"/> (không cooldown), sort theo ProxyId tăng —
    /// cho Fallback (thử theo thứ tự, không RR).
    /// </summary>
    IReadOnlyList<ProxyAttempt> GetLivingInOrder(IReadOnlyList<long> ids);
```

- [ ] **Step 2: Implement in `ProxyPool.cs`** — đổi `GetNext()` thành delegate + thêm 2 method. Thay block `GetNext()` hiện tại (dòng ~38-62) bằng:

```csharp
    /// <inheritdoc/>
    public ProxyAttempt? GetNext() => GetNext(null);

    /// <inheritdoc/>
    public ProxyAttempt? GetNext(IReadOnlyList<long>? allowedIds)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var now = _time.GetUtcNow();
            for (var step = 0; step < _entries.Count; step++)
            {
                var index = (_cursor + step) % _entries.Count;
                if (IsDown(index, now))
                {
                    continue;
                }
                if (allowedIds is not null && !allowedIds.Contains(_entries[index].Id))
                {
                    continue;
                }

                _cursor = (index + 1) % _entries.Count;
                return _entries[index];
            }
            return null; // không còn proxy sống trong tập → direct
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ProxyAttempt> GetLivingInOrder(IReadOnlyList<long> ids)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var now = _time.GetUtcNow();
            return _entries
                .Where(e => ids.Contains(e.Id))
                .Where(e => !IsDown(_entries.IndexOf(e), now))
                .OrderBy(e => e.Id)
                .ToList();
        }
    }
```
(`IsDown(int, DateTimeOffset)` hiện có phục vụ cả 2 method.)

- [ ] **Step 3: Add tests** — cuối `ProxyPoolTests.cs`, thêm (dùng `_db`/`_factory`/`_protector` + `CreatePool()` + `AddRowAsync` sẵn có):

```csharp
    [Fact]
    public async Task GetNext_NullAllowedId_MatchesGlobal()
    {
        var pool = CreatePool();
        var a = await AddRowAsync(port: 9000);
        var b = await AddRowAsync(port: 9001);
        pool.Invalidate();
        var first = pool.GetNext();
        Assert.NotNull(first);
        var second = pool.GetNext();
        Assert.NotNull(second);
        Assert.NotEqual(first!.Id, second!.Id); // RR, giống GetNext() gốc
    }

    [Fact]
    public async Task GetNext_AllowedIds_ReturnsOnlyInSet()
    {
        var pool = CreatePool();
        var inSet = await AddRowAsync(port: 9100);
        var inSet2 = await AddRowAsync(port: 9101);
        var outSet = await AddRowAsync(port: 9102);
        pool.Invalidate();
        var ids = new[] { inSet, inSet2 };
        for (var i = 0; i < 6; i++)
        {
            var pick = pool.GetNext(ids);
            Assert.NotNull(pick);
            Assert.Contains(pick!.Id, ids);
        }
        // outSet không bao giờ được chọn
        var picks = new List<long>();
        for (var i = 0; i < 6; i++) picks.Add(pool.GetNext(ids)!.Id);
        Assert.DoesNotContain(outSet, picks);
    }

    [Fact]
    public async Task GetNext_AllowedIds_SkipsDown()
    {
        var pool = CreatePool();
        var a = await AddRowAsync(port: 9200);
        var b = await AddRowAsync(port: 9201);
        pool.Invalidate();
        pool.ReportFailure(a); // a down
        var ids = new[] { a, b };
        var pick = pool.GetNext(ids);
        Assert.NotNull(pick);
        Assert.Equal(b, pick!.Id); // bỏ qua a down
    }

    [Fact]
    public async Task GetNext_AllowedIds_AllDown_ReturnsNull()
    {
        var pool = CreatePool();
        var a = await AddRowAsync(port: 9300);
        var b = await AddRowAsync(port: 9301);
        pool.Invalidate();
        pool.ReportFailure(a);
        pool.ReportFailure(b);
        Assert.Null(pool.GetNext(new[] { a, b }));
    }

    [Fact]
    public async Task GetLivingInOrder_SortsByIdAndSkipsDown()
    {
        var pool = CreatePool();
        var a = await AddRowAsync(port: 9400);
        var b = await AddRowAsync(port: 9401);
        var c = await AddRowAsync(port: 9402);
        pool.Invalidate();
        pool.ReportFailure(b); // b down
        var ordered = pool.GetLivingInOrder(new[] { c, a, b });
        Assert.Equal(new[] { a, c }, ordered.Select(x => x.Id).ToList());
        Assert.Empty(pool.GetLivingInOrder(new[] { b }));
    }
```

- [ ] **Step 4: Run gates + commit**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
git add -A
git commit -m "feat: add scoped proxy pool selection"
```
Expected: test PASSED (mới +5).

---

### Task 4: ProxyHealthHandler dispatch by mode + tests

**Files:**
- Modify: `src/RouterBalancing.Core/Proxies/ProxyHealthHandler.cs`
- Test: `router balancing test/Proxies/ProxyHealthHandlerTests.cs` (bổ sung)
- Modify: `router balancing test/Proxies/ProxyOutboundHttpStubTests.cs` (CreateClient ctor)
- Modify: `router balancing test/Proxies/ProxyOutboundSocksStubTests.cs` (CreateClient ctor)

**Interfaces:**
- Consumes: `IProxySelectionResolver.Resolve`, `ProxyTarget.Current`, `IProxyPool.GetNext(allowedIds)/GetLivingInOrder(ids)`.
- Produces: handler dispatch Direct/RoundRobin/Fallback; `ProxyHealthHandler(IProxyPool, ILogService, IProxySelectionResolver)` ctor.

- [ ] **Step 0: Extend both `FakePool` test doubles** (interface now has 2 new methods). Trong `router balancing test/Proxies/ProxyHealthHandlerTests.cs` (class `FakePool`, ~dòng 207) và `ProxyServiceTests.cs` (class `FakePool`, ~dòng 274), thêm 2 method:

```csharp
    public ProxyAttempt? GetNext(IReadOnlyList<long>? allowedIds)
    {
        var candidates = allowedIds is not null
            ? _alive.Where(a => allowedIds.Contains(a.Id)).ToList()
            : _alive;
        if (candidates.Count == 0) return null;
        var pick = candidates[_cursor % candidates.Count];
        _cursor++;
        return pick;
    }

    public IReadOnlyList<ProxyAttempt> GetLivingInOrder(IReadOnlyList<long> ids) =>
        _alive.Where(a => ids.Contains(a.Id)).OrderBy(a => a.Id).ToList();
```

- [ ] **Step 1: Add resolver + context read in `ProxyHealthHandler.cs`**

Thêm `using RouterBalancing.Core.Domain.Enums;` + `using RouterBalancing.Core.Proxies;` (đã có `Proxies`). Thêm field:
```csharp
    private readonly IProxySelectionResolver _resolver;
```
Đổi ctor:
```csharp
    public ProxyHealthHandler(IProxyPool pool, ILogService log, IProxySelectionResolver resolver)
    {
        _pool = pool;
        _log = log;
        _resolver = resolver;
    }
```
Đầu `SendAsync` (sau dòng `protected override async Task<HttpResponseMessage> SendAsync(...)`), thêm:
```csharp
        // Request có assignment (ChatCompletionsHandler / probe / sync set) → dispatch
        // Direct/RoundRobin/Fallback theo provider+account; null → behavior global pool (D7).
        var target = ProxyTarget.Current;
        if (target is not null)
        {
            return await SendWithAssignmentAsync(request, target, cancellationToken);
        }
```
(Làm thế này giữ nguyên toàn bộ body `SendAsync` cũ — không đổi, chỉ thêm 3 dòng đầu.)

- [ ] **Step 2: Add `SendWithAssignmentAsync`** — thêm method private cuối class (trước `CloneRequest`), code đầy đủ:

```csharp
    /// <summary>
    /// Dispatch theo assignment của request: Direct (không proxy), RoundRobin (RR scoped),
    /// Fallback (thử theo ProxyId tăng, fail → kế, hết → direct). Replay guard + 407
    /// cùng logic với global pool (spec §4.4).
    /// </summary>
    private async Task<HttpResponseMessage> SendWithAssignmentAsync(
        HttpRequestMessage request, ProxyTarget target, CancellationToken ct)
    {
        var selection = _resolver.Resolve(target.Provider, target.Account);
        if (selection.IsDirect)
        {
            // Direct: không proxy, không health — request đi thẳng.
            ProxyContext.Current = null;
            return await base.SendAsync(request, ct);
        }

        var ids = selection.ProxyIds!;
        var mode = selection.Mode ?? ProxyMode.RoundRobin;

        // Body buffer 1 lần cho mọi attempt (replay guard, §4.4).
        var replayable = request.Content is null
            || request.Content is ByteArrayContent
            || request.Content is StringContent
            || request.Content is FormUrlEncodedContent;
        byte[]? body = request.Content is not null && replayable
            ? await request.Content.ReadAsByteArrayAsync(ct)
            : null;

        if (mode == ProxyMode.RoundRobin)
        {
            var budget = Math.Max(1, _pool.GetLivingInOrder(ids).Count);
            Exception? lastFailure = null;
            var attempts = 0;
            while (true)
            {
                var pick = _pool.GetNext(ids);
                if (pick is null)
                {
                    return await SendOnceAsync(request, body, replayable, ct); // hết tập → direct
                }

                if (attempts >= budget && lastFailure is not null)
                {
                    ExceptionDispatchInfo.Capture(lastFailure).Throw();
                }
                attempts++;

                ProxyContext.Current = pick;
                HttpResponseMessage response;
                try
                {
                    response = await SendOnceAsync(request, body, replayable, ct);
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
                        throw;
                    }
                    continue;
                }
                finally
                {
                    ProxyContext.Current = null;
                }

                if ((int)response.StatusCode == 407)
                {
                    response.Dispose();
                    var fail = new HttpRequestException(
                        "The proxy server returned HTTP 407 (Proxy Authentication Required).");
                    if (_pool.ReportFailure(pick.Id))
                    {
                        LogDown(pick, fail);
                    }
                    if (!replayable)
                    {
                        throw fail;
                    }
                    continue;
                }

                _pool.ReportSuccess(pick.Id);
                return response;
            }
        }

        // Fallback: thử theo ProxyId tăng; fail → proxy kế; hết tập → direct.
        foreach (var pick in _pool.GetLivingInOrder(ids))
        {
            ProxyContext.Current = pick;
            HttpResponseMessage response;
            try
            {
                response = await SendOnceAsync(request, body, replayable, ct);
            }
            catch (Exception ex) when (IsProxyConnectFailure(ex))
            {
                if (_pool.ReportFailure(pick.Id))
                {
                    LogDown(pick, ex);
                }
                if (!replayable)
                {
                    throw;
                }
                continue;
            }
            finally
            {
                ProxyContext.Current = null;
            }

            if ((int)response.StatusCode == 407)
            {
                response.Dispose();
                var fail = new HttpRequestException(
                    "The proxy server returned HTTP 407 (Proxy Authentication Required).");
                if (_pool.ReportFailure(pick.Id))
                {
                    LogDown(pick, fail);
                }
                if (!replayable)
                {
                    throw fail;
                }
                continue;
            }

            _pool.ReportSuccess(pick.Id);
            return response;
        }

        // hết proxy trong tập sống → direct.
        return await SendOnceAsync(request, body, replayable, ct);
    }
```

- [ ] **Step 3: Update test `ClientFor`/`CreateClient` helpers**

Trong `ProxyHealthHandlerTests.cs`, helper `ClientFor` (gần dòng 199) `new(new ProxyHealthHandler(pool, new NullLog()) { InnerHandler = stub })` → thêm param resolver:
```csharp
        new(new ProxyHealthHandler(pool, new NullLog(), new ProxySelectionResolver()) { InnerHandler = stub })
```
Trong `ProxyOutboundHttpStubTests.cs` `CreateClient()` (dòng ~74) và `ProxyOutboundSocksStubTests.cs` (dòng ~58), `new ProxyHealthHandler(pool, new NullLog())` → `new ProxyHealthHandler(pool, new NullLog(), new ProxySelectionResolver())`.

- [ ] **Step 4: Add fallback/direct tests** — cuối `ProxyHealthHandlerTests.cs`. Cần helper tạo provider+target (xem Task 2 helper). Thêm:

```csharp
    // ===== Helper cho assignment tests (dùng TestDb + real stubs, pattern ProxyOutboundHttpStubTests) =====
    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly LocalHttpServer _destination = new();
    private readonly List<IDisposable> _stubs = [];
    public ProxyHealthHandlerTests()
    {
        _factory = _db.CreateFactory();
        DbInitializer.Initialize(_factory);
    }
    public void Dispose()
    {
        foreach (var s in _stubs) s.Dispose();
        _destination.Dispose();
        _db.Dispose();
    }

    private async Task<long> AddRowAsync(int port, string? username = null, string? password = null)
    {
        using var db = _factory.CreateDbContext();
        var row = new OutboundProxy { Scheme = "http", Host = "127.0.0.1", Port = port,
            Enabled = true, Username = username,
            PasswordEncrypted = password is null ? null : _protector.Protect(password) };
        db.OutboundProxies.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    private (HttpClient client, ProxyPool pool) CreateClient()
    {
        var pool = new ProxyPool(_factory, _protector, TimeProvider.System, new NullLog());
        var handler = new ProxyHealthHandler(pool, new NullLog(), new ProxySelectionResolver())
        {
            InnerHandler = new SocketsHttpHandler { Proxy = new RoundRobinWebProxy(), UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(3) },
        };
        return (new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) }, pool);
    }

    private Uri DestinationUrl() => new($"http://127.0.0.1:{_destination.Port}/v1/chat/completions");

    private static Provider ProviderWithProxies(params long[] proxyIds) => new()
    {
        Id = 1, Name = "Test", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com",
        ProxyMode = ProxyMode.RoundRobin,
        ProviderProxies = proxyIds.Select(id => new ProviderProxy { ProviderId = 1, ProxyId = id }).ToList(),
    };

    [Fact]
    public async Task SendAsync_DirectTarget_SendsDirectNoProxy()
    {
        var provider = new Provider { Id = 1, Name = "Test", Type = ProviderType.OpenAI, BaseUrl = "https://a.com" };
        await AddRowAsync(ReserveClosedPort()); // 1 proxy trong pool nhưng không assign
        var (client, pool) = CreateClient();
        ProxyTarget.Current = new ProxyTarget(provider, null); // Direct (không ProxyProxies)
        try
        {
            var response = await client.GetAsync(DestinationUrl());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, _destination.RequestsHandled);
        }
        finally { ProxyTarget.Current = null; }
    }

    [Fact]
    public async Task SendAsync_RoundRobinTarget_OnlyAssignedProxiesReceiveTraffic()
    {
        var liveA = new LocalHttpProxyStub(); _stubs.Add(liveA);
        var liveB = new LocalHttpProxyStub(); _stubs.Add(liveB);
        var unusedA = new LocalHttpProxyStub(); _stubs.Add(unusedA);
        var unusedB = new LocalHttpProxyStub(); _stubs.Add(unusedB);
        var idA = await AddRowAsync(liveA.Port);
        var idB = await AddRowAsync(liveB.Port);
        await AddRowAsync(unusedA.Port);
        await AddRowAsync(unusedB.Port);
        var provider = ProviderWithProxies(idA, idB);
        var (client, _) = CreateClient();
        ProxyTarget.Current = new ProxyTarget(provider, null);
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var response = await client.GetAsync(DestinationUrl());
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            Assert.Equal(3, liveA.RequestsHandled);
            Assert.Equal(3, liveB.RequestsHandled);
            Assert.Equal(0, unusedA.RequestsHandled); // không trong set → không dùng
            Assert.Equal(0, unusedB.RequestsHandled);
        }
        finally { ProxyTarget.Current = null; }
    }

    [Fact]
    public async Task SendAsync_FallbackTarget_FailsOverInOrder()
    {
        var deadPort = ReserveClosedPort();
        var live = new LocalHttpProxyStub(); _stubs.Add(live);
        var deadId = await AddRowAsync(deadPort);
        var liveId = await AddRowAsync(live.Port);
        var provider = ProviderWithProxies(deadId, liveId);
        provider.ProxyMode = ProxyMode.Fallback;
        var (client, pool) = CreateClient();
        ProxyTarget.Current = new ProxyTarget(provider, null);
        try
        {
            var response = await client.GetAsync(DestinationUrl());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, live.RequestsHandled);
            Assert.Equal(1, _destination.RequestsHandled);
            Assert.True(pool.Snapshot().Single(s => s.Id == deadId).IsDown);
        }
        finally { ProxyTarget.Current = null; }
    }

    [Fact]
    public async Task SendAsync_FallbackTarget_AllDown_GoesDirect()
    {
        await AddRowAsync(ReserveClosedPort());
        await AddRowAsync(ReserveClosedPort());
        var provider = ProviderWithProxies(1, 2);
        provider.ProxyMode = ProxyMode.Fallback;
        var (client, pool) = CreateClient();
        ProxyTarget.Current = new ProxyTarget(provider, null);
        try
        {
            var response = await client.GetAsync(DestinationUrl());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, _destination.RequestsHandled);
        }
        finally { ProxyTarget.Current = null; }
    }
```
*(Implementer viết đầy đủ theo pattern `ProxyOutboundHttpStubTests.cs` — `AddStub`, `AddRowAsync`, `ReserveClosedPort`, `CreateClient`, `DestinationUrl`.)*

- [ ] **Step 5: Run gates + commit**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
git add -A
git commit -m "feat: dispatch proxy health handler by provider assignment"
```
Expected: test PASSED (mới +4-5).

---

### Task 5: IProxyService/ProxyService assignment CRUD + tests

**Files:**
- Modify: `src/RouterBalancing.Core/Proxies/IProxyService.cs`
- Modify: `src/RouterBalancing.Core/Proxies/ProxyService.cs`
- Test: `router balancing test/Proxies/ProxyServiceTests.cs` (bổ sung)

**Interfaces:**
- Produces: `IProxyService.GetAssignmentsAsync(providerId)`, `AssignProviderProxiesAsync(providerId, proxyIds, mode)`, `AssignAccountProxiesAsync(accountId, proxyIds, mode)`.
- Consumes: `RouterBalancingDbContext` (Providers, ProviderAccounts, OutboundProxies, ProviderProxies, ProviderAccountProxies).

- [ ] **Step 1: Add methods to `IProxyService.cs`** — thêm cuối interface:

```csharp
    /// <summary>Gán proxy của provider (M2M) + mode. proxyIds rỗng = gỡ toàn bộ.</summary>
    Task AssignProviderProxiesAsync(long providerId, IReadOnlyList<long> proxyIds,
        ProxyMode? mode, CancellationToken ct = default);

    /// <summary>Gán proxy của account (override provider) + mode. proxyIds rỗng = gỡ toàn bộ.</summary>
    Task AssignAccountProxiesAsync(long accountId, IReadOnlyList<long> proxyIds,
        ProxyMode? mode, CancellationToken ct = default);

    /// <summary>Gán proxy hiện tại (provider + account) cho UI.</summary>
    Task<IReadOnlyList<ProxyAssignment>> GetAssignmentsAsync(long providerId, CancellationToken ct = default);
```

- [ ] **Step 2: Create `ProxyAssignment.cs`** — `src/RouterBalancing.Core/Proxies/ProxyAssignment.cs`:

```csharp
using RouterBalancing.Core.Domain.Enums;

namespace RouterBalancing.Core.Proxies;

/// <summary>Thông tin gán proxy của 1 provider hoặc 1 account — cho UI hiển thị.</summary>
public sealed record ProxyAssignment
{
    public long Id { get; init; }
    public string Name { get; init; }
    public bool IsProvider { get; init; }
    public ProxyMode? Mode { get; init; }
    public IReadOnlyList<long> ProxyIds { get; init; } = [];
}
```

- [ ] **Step 3: Implement in `ProxyService.cs`** — thêm 3 method + helper. Pattern: load entity + validate proxy tồn tại & enabled → set junction (EF tự upsert/delete) + mode → save → `Invalidate()`.

```csharp
    private async Task ValidateProxyIdsAsync(long[] ids, IDbContextFactory<RouterBalancingDbContext> db, CancellationToken ct)
    {
        var all = await db.CreateDbContext().OutboundProxies.AsNoTracking()
            .Select(p => p.Id).ToHashSetAsync(ct);
        foreach (var id in ids)
        {
            if (!all.Contains(id))
                throw new KeyNotFoundException($"Proxy {id} not found.");
        }
    }

    public async Task AssignProviderProxiesAsync(long providerId, IReadOnlyList<long> proxyIds,
        ProxyMode? mode, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new KeyNotFoundException($"Provider {providerId} not found.");
        await ValidateProxyIdsAsync(proxyIds, _db, ct);

        provider.ProviderProxies = proxyIds.Select(id => new ProviderProxy { ProviderId = provider.Id, ProxyId = id }).ToList();
        provider.ProxyMode = mode;
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _pool.Invalidate();
    }

    public async Task AssignAccountProxiesAsync(long accountId, IReadOnlyList<long> proxyIds,
        ProxyMode? mode, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var account = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new KeyNotFoundException($"Account {accountId} not found.");
        await ValidateProxyIdsAsync(proxyIds, _db, ct);

        account.AccountProxies = proxyIds.Select(id => new ProviderAccountProxy { AccountId = accountId, ProxyId = id }).ToList();
        account.ProxyMode = mode;
        account.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        _pool.Invalidate();
    }

    public async Task<IReadOnlyList<ProxyAssignment>> GetAssignmentsAsync(long providerId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers
            .Include(p => p.ProviderProxies)
            .Include(p => p.Accounts)
                .ThenInclude(a => a.AccountProxies)
            .FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new KeyNotFoundException($"Provider {providerId} not found.");

        var result = new List<ProxyAssignment>
        {
            new()
            {
                Id = provider.Id, Name = provider.Name, IsProvider = true,
                Mode = provider.ProxyMode,
                ProxyIds = provider.ProviderProxies.Select(x => x.ProxyId).ToList(),
            },
        };
        foreach (var acc in provider.Accounts)
        {
            result.Add(new ProxyAssignment
            {
                Id = acc.Id, Name = acc.Name, IsProvider = false,
                Mode = acc.ProxyMode,
                ProxyIds = acc.AccountProxies.Select(x => x.ProxyId).ToList(),
            });
        }
        return result;
    }
```
(ProxyService đã có `_pool` field — kiểm tra; nếu không, inject `IProxyPool` vào ctor.)

- [ ] **Step 4: Add tests** — cuối `ProxyServiceTests.cs` (dùng `_db`/`_factory`/`_protector` + `AddProxyAsync` sẵn có + `CreateService`):

```csharp
    [Fact]
    public async Task AssignProviderProxies_PersistsAndInvalidate()
    {
        var svc = CreateService();
        var proxy = await AddProxyAsync(port: 9500);
        var providerId = await AddProviderAsync();
        await svc.AssignProviderProxiesAsync(providerId, new[] { proxy }, ProxyMode.Fallback);
        // reload & assert
        var provider = await svc.GetProviderAsync(providerId);
        Assert.Equal(ProxyMode.Fallback, provider.ProxyMode);
        Assert.Equal(1, provider.ProviderProxies.Count);
        Assert.Equal(proxy, provider.ProviderProxies.Single().ProxyId);
    }

    [Fact]
    public async Task AssignProviderProxies_Empty_Clears()
    {
        // assign rồi assign rỗng → ProviderProxies rỗng
    }

    [Fact]
    public async Task AssignProviderProxies_DisabledProxy_Throws()
    {
        // assign proxy đã SetEnabledAsync(false) → InvalidOperationException
    }

    [Fact]
    public async Task AssignAccountProxies_OverridesProvider()
    {
        // assign provider [A], assign account [B] → account wins
    }

    [Fact]
    public async Task GetAssignments_ReturnsProviderAndAccounts()
    {
        // assert list
    }
```

- [ ] **Step 5: Run gates + commit**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
git add -A
git commit -m "feat: add proxy assignment CRUD to proxy service"
```

---

### Task 6: Wire context into call sites + integration test

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/ProviderKeyResolver.cs`
- Modify: `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs`
- Modify: `src/RouterBalancing.Core/Providers/ProviderService.cs`
- Modify: `src/RouterBalancing.Core/Providers/ProviderAccountService.cs`
- Modify: `src/RouterBalancing.Core/Providers/FreeModelSyncService.cs`
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs`
- Modify: `router-balancing/MauiProgram.cs`
- Test: `router balancing test/Proxies/ProxyAssignmentIntegrationTests.cs` (mới)

**Interfaces:**
- Produces: `ProviderKeyResolver.ResolveFirstEnabledAccount`; call sites set `ProxyTarget.Current`; DI có `IProxySelectionResolver`.
- Consumes: Task 2/4.

- [ ] **Step 1: `ProviderKeyResolver` — thêm `ResolveFirstEnabledAccount`**

```csharp
/// <summary>Tài khoản enabled đầu tiên (Priority tăng, tie-break Id) — null nếu không có.</summary>
public static ProviderAccount? ResolveFirstEnabledAccount(Provider provider, ISecretProtector protector) =>
    provider.Accounts?.Where(a => a.Enabled && !string.IsNullOrEmpty(a.ApiKeyEncrypted))
        .OrderBy(a => a.Priority).ThenBy(a => a.Id).FirstOrDefault();
```
(`ResolveFirstEnabledKey` giữ nguyên — vẫn dùng ở 2 call site khác. Hoặc delegate: `return ResolveFirstEnabledAccount(provider, protector)?.Let(_ => protector.Unprotect(...));` — giữ nguyên để không phá call site.)

- [ ] **Step 2: `ChatCompletionsHandler.ForwardAsync`** — đặt `ProxyTarget.Current` quanh body. Thay khối từ `var key = ...` (dòng ~86) đến cuối method bằng (giữ nguyên logic 503):

```csharp
        var account = ProviderKeyResolver.ResolveFirstEnabledAccount(provider, _protector);
        if (account is null)
        {
            // ... (giữ nguyên block log + return 503 hiện tại) ...
        }

        var key = _protector.Unprotect(account.ApiKeyEncrypted);

        ProxyTarget.Current = new ProxyTarget(provider, account);
        try
        {
            // ... (giữ nguyên: UsageCapture, stopwatch, try { response = await upstream.PostChatCompletionAsync(...) },
            //  catch, using(response){...} — toàn bộ body cũ) ...
        }
        finally
        {
            ProxyTarget.Current = null;
        }
```
*(Vô cùng quan trọng: `ProxyTarget.Current` phải set TRƯỚC khi `upstream.PostChatCompletionAsync` gọi handler, và reset cuối.)*

- [ ] **Step 3: `ProviderService.TestConnectionAsync`** (dòng ~148-151) — set context:

```csharp
        var account = ProviderKeyResolver.ResolveFirstEnabledAccount(provider, _protector);
        var key = apiKeyOverride;
        if (string.IsNullOrEmpty(key))
        {
            key = account is not null ? _protector.Unprotect(account.ApiKeyEncrypted) : string.Empty;
        }

        ProxyTarget.Current = new ProxyTarget(provider, account);
        try
        {
            using var request = ProviderRequestFactory.Create(provider, key ?? string.Empty);
            using var response = await _http.CreateClient(ProviderRequestFactory.HttpClientName).SendAsync(request, ct);
            // ... (giữ nguyên result logic) ...
        }
        finally
        {
            ProxyTarget.Current = null;
        }
```

- [ ] **Step 4: `ProviderAccountService.TestAllAsync`** — trong foreach account (dòng ~159-188), trước `var key = ...` set:
```csharp
            ProxyTarget.Current = new ProxyTarget(provider, account);
            try
            {
                // ... (giữ nguyên: key, request, response, success/message logic) ...
            }
            finally
            {
                ProxyTarget.Current = null;
            }
```

- [ ] **Step 5: `FreeModelSyncService.SyncProviderAsync`** — trong `SendModelListAsync` (dòng ~173-180), trước `return await _http.CreateClient(...).SendAsync(...)`:
```csharp
        ProxyTarget.Current = new ProxyTarget(provider, null);
        try
        {
            return await _http.CreateClient(HttpClientName).SendAsync(request, ct);
        }
        finally
        {
            ProxyTarget.Current = null;
        }
```
(Cả `SyncProviderAsync` cần truyền provider vào `SendModelListAsync` — đã có `provider` param.)

- [ ] **Step 6: DI — đăng ký `IProxySelectionResolver`**

`router-balancing/MauiProgram.cs` (sau `AddSingleton<IProxyPool, ProxyPool>()` dòng 75):
```csharp
            builder.Services.AddSingleton<IProxySelectionResolver, ProxySelectionResolver>();
```
`src/RouterBalancing.Core/Server/ProxyApp.cs` (sau `AddTransient<ProxyHealthHandler>()` dòng 39):
```csharp
        builder.Services.AddSingleton<IProxySelectionResolver, ProxySelectionResolver>();
```
(Thêm `using RouterBalancing.Core.Proxies;` nếu chưa có.)

- [ ] **Step 7: Integration test** — `router balancing test/Proxies/ProxyAssignmentIntegrationTests.cs`:

```csharp
// router balancing test/Proxies/ProxyAssignmentIntegrationTests.cs
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Domain.Entities;
using RouterBalancing.Core.Proxies;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;
using router_balancing_test.Server;

namespace router_balancing_test.Proxies;

/// <summary>Fallback thực: 2 proxy thật qua LocalHttpProxyStub (1 chết, 1 sống) +
/// ProxyTarget set provider Fallback [dead, live] → failover tới live.</summary>
public class ProxyAssignmentIntegrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _factory;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly LocalHttpServer _destination = new();
    private readonly List<IDisposable> _stubs = [];

    public ProxyAssignmentIntegrationTests()
    {
        _factory = _db.CreateFactory();
        DbInitializer.Initialize(_factory);
    }

    public void Dispose()
    {
        foreach (var s in _stubs) s.Dispose();
        _destination.Dispose();
        _db.Dispose();
    }

    private async Task<long> AddRowAsync(int port)
    {
        using var db = _factory.CreateDbContext();
        db.OutboundProxies.Add(new OutboundProxy { Scheme = "http", Host = "127.0.0.1", Port = port });
        await db.SaveChangesAsync();
        return db.OutboundProxies.Last().Id;
    }

    private (HttpClient client, ProxyPool pool) CreateClient()
    {
        var pool = new ProxyPool(_factory, _protector, TimeProvider.System, new NullLog());
        var handler = new ProxyHealthHandler(pool, new NullLog(), new ProxySelectionResolver())
        {
            InnerHandler = new SocketsHttpHandler { Proxy = new RoundRobinWebProxy(), UseProxy = true, ConnectTimeout = TimeSpan.FromSeconds(3) },
        };
        return (new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) }, pool);
    }

    private static int ReserveClosedPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [Fact]
    public async Task SendAsync_FallbackAssignment_FailsOverToLive()
    {
        var deadPort = ReserveClosedPort();
        var deadId = await AddRowAsync(deadPort);
        var live = new LocalHttpProxyStub();
        _stubs.Add(live);
        var liveId = await AddRowAsync(live.Port);

        var provider = new Provider
        {
            Id = 1, Name = "Test", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com",
            ProxyMode = ProxyMode.Fallback,
            ProviderProxies = [ new ProviderProxy { ProviderId = 1, ProxyId = deadId },
                                new ProviderProxy { ProviderId = 1, ProxyId = liveId } ],
        };

        var (client, pool) = CreateClient();
        ProxyTarget.Current = new ProxyTarget(provider, null);
        try
        {
            var response = await client.GetAsync(new Uri($"http://127.0.0.1:{_destination.Port}/v1/chat/completions"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(1, live.RequestsHandled);
            Assert.Equal(1, _destination.RequestsHandled);
            // dead không forward (connection refused) — proxy down passive
            Assert.True(pool.Snapshot().Single(s => s.Id == deadId).IsDown);
        }
        finally
        {
            ProxyTarget.Current = null;
        }
    }
}

- [ ] **Step 8: Run gates + commit**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
git add -A
git commit -m "feat: wire proxy assignment into request and probe paths"
```
Expected: test PASSED (mới +1 integration + các test cũ vẫn xanh).

---

### Task 7: UI (Proxies.razor + i18n + Providers badge)

**Files:**
- Modify: `router-balancing/Components/Pages/Proxies.razor`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs`
- Modify: `router-balancing/Components/Pages/Providers.razor` (badge, optional)

**Interfaces:**
- Consumes: `IProxyService.GetAssignmentsAsync`, `AssignProviderProxiesAsync`, `AssignAccountProxiesAsync`.
- Produces: UI gán proxy.

- [ ] **Step 1: i18n keys** — cuối `Translations.cs`, trước `};` của dict `English` và `Vietnamese`, thêm (chèn sau `proxies.status.downUntil`):

English:
```csharp
        ["proxies.assign.title"] = "Assign proxy",
        ["proxies.assign.none"] = "No proxy assigned (direct).",
        ["proxies.assign.mode"] = "Mode",
        ["proxies.assign.mode_rr"] = "Round-robin",
        ["proxies.assign.mode_fallback"] = "Fallback",
        ["proxies.assign.none_mode"] = "None",
        ["proxies.assign.save"] = "Save",
        ["proxies.assign.message"] = "Proxy assigned to this provider.",
        ["proxies.assign.account"] = "Account override",
```
Vietnamese:
```csharp
        ["proxies.assign.title"] = "Gán proxy",
        ["proxies.assign.none"] = "Chưa gán proxy (direct).",
        ["proxies.assign.mode"] = "Chế độ",
        ["proxies.assign.mode_rr"] = "Round-robin",
        ["proxies.assign.mode_fallback"] = "Fallback",
        ["proxies.assign.none_mode"] = "Không có",
        ["proxies.assign.save"] = "Lưu",
        ["proxies.assign.message"] = "Đã gán proxy cho provider này.",
        ["proxies.assign.account"] = "Override theo tài khoản",
```

- [ ] **Step 2: `Proxies.razor` — thêm section gán proxy**

Sau bảng proxy (trước `@code`), thêm:

```razor
@* Section gán proxy cho provider/account (spec proxy-per-provider) *@
<div class="mt-6 rounded border border-border bg-surface p-4">
    <h2 class="text-sm font-semibold mb-3">@L["proxies.assign.title"]</h2>
    <div class="grid gap-2">
        <div class="flex gap-2 items-center">
            <select class="rounded border border-border bg-surface px-2 py-1.5" @bind="_assignProviderId">
                <option value="">— Chọn provider —</option>
                @foreach (var p in _providers)
                {
                    <option value="@p.Id">@p.Name</option>
                }
            </select>
            <select class="rounded border border-border bg-surface px-2 py-1.5" @bind="_assignMode">
                <option value="">@L["proxies.assign.none_mode"]</option>
                <option value="rr">@L["proxies.assign.mode_rr"]</option>
                <option value="fb">@L["proxies.assign.mode_fallback"]</option>
            </select>
            <button class="btn btn-primary" @onclick="SaveAssignment" disabled="@_busy || _assignProviderId is null">
                @L["proxies.assign.save"]
            </button>
        </div>
    </div>
</div>
```

Trong `@code`, thêm:
```csharp
    private List<Provider> _providers = [];
    private long? _assignProviderId;
    private string? _assignMode;
    private List<long> _selectedProxyIds = [];
    private readonly IProviderService _providerSvc; // @inject IProviderService ProviderSvc (đã có? kiểm tra)
```
(Inject `IProviderService` nếu chưa có. Gọi `ProviderSvc.ListAsync()` trong `ReloadAsync`.)

`SaveAssignment` gọi `ProxySvc.AssignProviderProxiesAsync(_assignProviderId, _selectedProxyIds, mode)`; reload sau đó.

- [ ] **Step 3: Run gates + commit**

```bash
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet test "router balancing test/router balancing test.csproj"
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
git add -A
git commit -m "feat: add proxy assignment UI"
```
Expected: build app 0 lỗi/warning; test PASSED.

---

## Self-Review (checklist)

1. **Spec coverage:** M2M junction (Task 1) ✅; most-specific-wins resolver (Task 2) ✅; pool scoped + ordered (Task 3) ✅; handler dispatch Direct/RR/Fallback (Task 4) ✅; CRUD assignment (Task 5) ✅; wire context + probe/sync (Task 6) ✅; UI (Task 7) ✅. D1-D8 đều có task.
2. **Placeholder scan:** `20261002XXXXXX_AddProxyAssignments` — implementer điền sau `dotnet ef` (ghi trong plan). Test `ProxyOutboundHttpStubTests` pattern "xem pattern" — cần code thật; implementer copy từ file có sẵn. OK.
3. **Type consistency:** `ProxySelection(ProxyMode?, IReadOnlyList<long>?)`, `ProxyTarget(Provider, ProviderAccount?)`, `IProxySelectionResolver.Resolve` — nhất quán Task 2→4. `GetNext(IReadOnlyList<long>?)`/`GetLivingInOrder(IReadOnlyList<long>)` nhất quán Task 3→4.
4. **Rủi ro:** handler ctor đổi → 3 test file + DI phải cập nhật (Task 4 Step 3). `ProviderProxies` assignment trong Task 5 phải set `ProviderId = provider.Id` (ghi chú trong code). AsyncLocal reset trong `finally` mọi nơi (Task 6).

**Execution handoff:** Plan done. Chạy **subagent-driven-development** (gợi ý) hoặc **inline execution**. Mỗi task dispatch subagent mới + review giữa các task.
