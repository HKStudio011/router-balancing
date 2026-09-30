# Client API Keys (multi-key inbound) + Usage Capture Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thay API key đơn trong settings bằng nhiều client key cho proxy (tên key, revoke, counter daily, rate limit RPM/TPM per-key, usage capture từ upstream) — spec: `docs/superpowers/specs/2026-09-30-client-keys-design.md`.

**Architecture:** Bảng EF `ClientKeys` là single source cho auth — middleware đọc snapshot in-memory, invalidate qua event `KeysChanged`; instance `IClientKeyService` được chia sẻ giữa MAUI container và proxy container theo pattern `_settings` của `ProxyHost`. Rate limit RPM/TPM qua singleton window 60s in-memory (`TimeProvider` để test). Usage capture tee-stream ngay trong `ChatCompletionsHandler.ForwardAsync`, ghi journal row qua helper mới `ILogService.LogRequestUsage`, counter DB đi qua `IClientKeyUsageSink` (fail-open theo spec §10).

**Tech Stack:** .NET 10 / EF Core 10 (SQLite, auto-migration), ASP.NET Core middleware, xUnit.

## Global Constraints

- `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên; TFM app: `net10.0-windows10.0.19041.0`.
- Comment tiếng Việt cho "tại sao" (workaround/cảnh báo an toàn), XML doc cho public API; không thêm comment thừa / TODO vô chủ.
- i18n: mọi key mới phải có trong **cả 2 dict** `English` và `Vietnamese` của `Translations.cs`; parity EN == VI (test tự thêm ở Task 5).
- 1 task = 1 commit; commit message tiếng Anh conventional (`feat:`/`docs:`).
- Không nuốt exception — chỉ các chỗ plan nêu rõ best-effort (spec §3 legacy cleanup) được catch kèm comment giải thích.
- Razor: UTF-8 trực tiếp, **không** HTML entity (guard `RazorParameterEntityTests`).
- Key plaintext chỉ hiển thị 1 lần ở UI; không bao giờ log plaintext; DB chỉ lưu SHA-256 hex.
- App `router-balancing` phải **đóng** trước khi build TFM Windows / chạy test (SingleInstanceGuard giữ mutex).
- Gates chuẩn (mỗi task, trước commit):
  - `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj`
  - `dotnet test "router balancing test/router balancing test.csproj"`
  - Task đụng UI/MauiProgram: thêm `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0`
  - **Không** dùng `dotnet build router-balancing.slnx` làm gate (6 lỗi pre-existing NETSDK1082 + DLL lock).
- Test flake đã biết: `ProxyQueueIntegrationTests` teardown IOException → chạy lại/isolation; test fail do app đang mở → đóng app, chạy lại.

## File Structure

| Hành động | File | Trách nhiệm |
|---|---|---|
| Create | `src/RouterBalancing.Core/Domain/Entities/ClientKey.cs` | Entity bảng `ClientKeys` |
| Create | `src/RouterBalancing.Core/Security/ClientKeyHasher.cs` | SHA-256 hash + sinh key `sk-rb-` + mask hiển thị |
| Create | `src/RouterBalancing.Core/Server/ClientKeyDraft.cs` | Draft tạo/sửa key |
| Create | `src/RouterBalancing.Core/Server/IClientKeyService.cs` + `ClientKeyService.cs` | CRUD + counter daily + event `KeysChanged` |
| Create | `src/RouterBalancing.Core/Server/IClientKeyRateLimiter.cs` + `ClientKeyRateLimiter.cs` | Window RPM/TPM 60s in-memory |
| Create | `src/RouterBalancing.Core/Server/ClientKeyAuthCache.cs` | Snapshot enabled keys (kèm record `ClientKeyAuthInfo`) |
| Create | `src/RouterBalancing.Core/Server/ClientKeyItems.cs` | `HttpContext.Items` keys + helper `IdOf` |
| Create | `src/RouterBalancing.Core/Server/IClientKeyUsageSink.cs` + `ClientKeyUsageSink.cs` | Ghi usage counter + window TPM (fail-open) |
| Create | `src/RouterBalancing.Core/Engine/UsageCapture.cs` | Parse usage + inject `stream_options` + tee-stream |
| Create | migration `Storage/Migrations/*_AddClientKeys.cs` (dotnet-ef) | Schema `ClientKeys` + đổi cột `LogEntry.RequestId` |
| Modify | `Storage/RouterBalancingDbContext.cs` | `DbSet<ClientKey>` + config |
| Modify | `Domain/Entities/LogEntry.cs` | + `ClientKeyId`; `RequestId` Guid? → string? |
| Modify | `Storage/DbInitializer.cs` | `MigrateLegacyApiKey` (settings → bảng, 1 lần) |
| Modify | `router-balancing/MauiProgram.cs` | Gọi `Initialize(protector)` + đăng ký `IClientKeyService` |
| Modify | `Server/ApiKeyMiddleware.cs` | Rewrite: table auth + rate-limit pre-check |
| Modify | `Server/ProxyApp.cs` | DI limiter/cache/sink; `ctx.Items[RequestId]`; attribute journal rows |
| Modify | `Server/ProxyHost.cs` | Inject + chia sẻ `IClientKeyService` sang proxy container |
| Modify | `Engine/ChatCompletionsHandler.cs` | Inject `stream_options`, tee usage, ghi usage, attribute rows |
| Modify | `Logging/ILogService.cs` + `LogService.cs` | + `LogRequestUsage` |
| Modify | `Settings/*` (Keys, IAppSettingsService, AppSettingsService, SettingsDraft, SettingsValidator) | Gỡ `apiKey`/`apiKeyEnabled` (Task 5) |
| Modify | `router-balancing/Components/Pages/SettingsPanel.razor` | Gỡ section API key cũ; thêm bảng + form client key |
| Modify | `Localization/Translations.cs` | + 28 key `clientKeys.*` ×2 dict, gỡ 4 key chết, + `log.severity.debug` |
| Modify | Tests: `ApiKeyMiddlewareTests`, `ProxyHostTests`, `ProxyAppChatIntegrationTests`, `ProxyRetryIntegrationTests`, `ProxyQueueIntegrationTests`, `ProxyControlApiTests`, `DispatcherLoopTests`, `ChatCompletionsHandlerTests`, `TestDoubles.cs`, `SettingsValidatorTests`, `AppSettingsServiceTests` | Cập nhật theo API mới |
| Create | Tests: `Security/ClientKeyHasherTests.cs`, `Storage/DbInitializerLegacyKeyTests.cs`, `Server/ClientKeyServiceTests.cs`, `Server/ClientKeyRateLimiterTests.cs`, `Server/ClientKeyUsageSinkTests.cs`, `Engine/UsageCaptureTests.cs`, `Localization/TranslationParityTests.cs` | Test mới |

## Design decisions locked in this plan (spec gap → plan resolve)

1. **`ClientKey.KeyMask` (string ≤16)** — spec §2 không liệt kê cột mask nhưng §8 bắt buộc hiển thị "Key mask"; hash không suy ra được 4 ký tự cuối → thêm cột lưu mask khi tạo (legacy migration cũng tính từ plaintext trước khi bỏ).
2. **`LogEntry.RequestId`: `Guid?` → `string?`** — không nơi nào ghi cột này; request id là string 8 ký tự (`RequestId.New()`); cần correlation cho usage row (spec §6.2). Cả 2 kiểu đều TEXT trên SQLite, data hiện đều NULL → an toàn.
3. **`IClientKeyUsageSink`** — ranh giới test: test `ForwardAsync` và dispatcher unit test không cần DB thật (stub 1 method).
4. **Attribute journal rows qua `log.Write(new LogEntry { ... ClientKeyId = ClientKeyItems.IdOf(ctx) })`** thay vì nới chữ ký `Info/Warn/Error` (giữ nguyên ILogService surface); message giữ **byte-for-byte** nên các assert `LogAdded`/`Infos` hiện có không vỡ — stub `CapturingLog` ở 2 file test bị ảnh hưởng chuyển sang đổ list theo `Severity`.
5. **`DbInitializer` đọc settings bằng literal `"apiKey"`/`"apiKeyEnabled"`** — 2 hằng này bị gỡ khỏi `SettingsKeys` ở Task 5; tên row là storage contract, kèm comment giải thích.
6. **Singleton chia sẻ 2 container** — `ProxyHost` nhận `IClientKeyService` qua ctor và `AddSingleton(_clientKeys)` (đúng pattern `_settings`): UI CRUD phát `KeysChanged` → cache của proxy invalidate.
7. **UI:** modal Sửa chỉ sửa Name/limits; `Enabled` đổi nhanh bằng checkbox trong bảng (spec §8 — cả 2 cơ chế).
8. **Usage row ghi cả khi auth mở** (`ClientKeyId = null`) — journal đầy đủ; counter/TPM chỉ cộng khi có key.
9. **Legacy migrate + dọn settings trong 1 `SaveChanges`** — diễn giải "best-effort" của spec §3 thành atomic: hoặc migrate trọn vẹn hoặc giữ nguyên; không bao giờ mất key (fail → dialog startup, retry lần sau, idempotent).

---

### Task 0: Commit the plan

**Files:**
- Create: `docs/superpowers/plans/2026-09-30-client-keys.md` (file này)

**Interfaces:**
- Consumes: —
- Produces: plan file để SDD dispatch theo task number.

- [x] **Step 1: Verify spec đã commit, tree chỉ có plan file**

Run:
```powershell
git log --oneline -1   # kỳ vọng: c306ef5 docs: add client keys design spec
git status --short     # chỉ thấy ?? plan file
```
Expected: HEAD = `c306ef5`; chỉ có plan file mới.

- [x] **Step 2: Commit**

```powershell
git add docs/superpowers/plans/2026-09-30-client-keys.md
git commit -m "docs: add client keys implementation plan"
```

---

### Task 1: ClientKey entity + hasher + EF migration + legacy settings migration

**Files:**
- Create: `src/RouterBalancing.Core/Domain/Entities/ClientKey.cs`
- Create: `src/RouterBalancing.Core/Security/ClientKeyHasher.cs`
- Modify: `src/RouterBalancing.Core/Domain/Entities/LogEntry.cs`
- Modify: `src/RouterBalancing.Core/Storage/RouterBalancingDbContext.cs` (DbSet cạnh `ProviderAccounts` + config cuối `OnModelCreating`)
- Modify: `src/RouterBalancing.Core/Storage/DbInitializer.cs`
- Modify: `router-balancing/MauiProgram.cs` (`TryInitializeDatabase`)
- Create (dotnet-ef): `src/RouterBalancing.Core/Storage/Migrations/<ts>_AddClientKeys.cs` + `.Designer.cs` + snapshot
- Test: `router balancing test/Security/ClientKeyHasherTests.cs`, `router balancing test/Storage/DbInitializerLegacyKeyTests.cs`

**Interfaces:**
- Consumes: `ISecretProtector.Unprotect` (DPAPI), `DbInitializer.Initialize`, settings rows `"apiKey"`/`"apiKeyEnabled"`.
- Produces (Task 2+): entity `ClientKey` (spec §2 + `KeyMask`), `ClientKeyHasher.Hash/GeneratePlaintext/Mask`, `DbInitializer.Initialize(factory, ISecretProtector? = null)`, `DbSet<ClientKey> ClientKeys`.

- [ ] **Step 1: Viết failing test `ClientKeyHasherTests`**

Tạo `router balancing test/Security/ClientKeyHasherTests.cs`:

```csharp
using RouterBalancing.Core.Security;

namespace router_balancing_test.Security;

public class ClientKeyHasherTests
{
    [Fact]
    public void Hash_KnownVector_ReturnsSha256HexLower()
        => Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ClientKeyHasher.Hash("abc"));

    [Fact]
    public void Hash_SameInput_ReturnsSameHex()
        => Assert.Equal(ClientKeyHasher.Hash("key-1"), ClientKeyHasher.Hash("key-1"));

    [Fact]
    public void GeneratePlaintext_HasPrefixAnd43Base64UrlChars()
    {
        var key = ClientKeyHasher.GeneratePlaintext();
        Assert.Matches("^sk-rb-[A-Za-z0-9_-]{43}$", key);
    }

    [Fact]
    public void GeneratePlaintext_TwoCalls_Differ()
        => Assert.NotEqual(ClientKeyHasher.GeneratePlaintext(), ClientKeyHasher.GeneratePlaintext());

    [Fact]
    public void Mask_GeneratedKey_ShowsPrefixAndLast4()
    {
        var key = ClientKeyHasher.GeneratePlaintext();
        Assert.Equal($"sk-rb-…{key[^4..]}", ClientKeyHasher.Mask(key));
    }

    [Fact]
    public void Mask_LegacyKeyOver8Chars_ShowsFirst4AndLast4()
        => Assert.Equal("abcd…mnop", ClientKeyHasher.Mask("abcdefghijklmnop"));

    [Fact]
    public void Mask_ShortKey_ShowsWholeAfterEllipsis()
        => Assert.Equal("…short", ClientKeyHasher.Mask("short"));
}
```

- [ ] **Step 2: Viết failing test `DbInitializerLegacyKeyTests`**

Tạo `router balancing test/Storage/DbInitializerLegacyKeyTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Storage;

public class DbInitializerLegacyKeyTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly DpapiSecretProtector _protector = new();

    public void Dispose() => _db.Dispose();

    /// <summary>Migrate schema trước rồi mới ghi settings — bảng AppSetting chưa có lúc file trống.</summary>
    private IDbContextFactory<RouterBalancingDbContext> SeedLegacySettings(string plain, bool enabled)
    {
        var factory = _db.CreateFactory();
        using (var db = factory.CreateDbContext())
        {
            db.Database.Migrate();
            db.AppSettings.Add(new AppSetting { Key = "apiKey", ValueJson = _protector.Protect(plain) });
            db.AppSettings.Add(new AppSetting { Key = "apiKeyEnabled", ValueJson = enabled ? "true" : "false" });
            db.SaveChanges();
        }
        return factory;
    }

    [Fact]
    public void Initialize_WithLegacyKeyEnabled_CreatesRowAndClearsSettings()
    {
        var factory = SeedLegacySettings("old-secret", enabled: true);

        DbInitializer.Initialize(factory, _protector);

        using var db = factory.CreateDbContext();
        var key = Assert.Single(db.ClientKeys.AsNoTracking().ToList());
        Assert.Equal("Legacy key", key.Name);
        Assert.Equal(ClientKeyHasher.Hash("old-secret"), key.KeyHash);
        Assert.True(key.Enabled);
        Assert.False(db.AppSettings.AsNoTracking()
            .Any(a => a.Key == "apiKey" || a.Key == "apiKeyEnabled"));
    }

    [Fact]
    public void Initialize_WithLegacyKeyDisabled_CreatesDisabledRow()
    {
        var factory = SeedLegacySettings("old-secret", enabled: false);

        DbInitializer.Initialize(factory, _protector);

        using var db = factory.CreateDbContext();
        Assert.False(Assert.Single(db.ClientKeys.AsNoTracking().ToList()).Enabled);
    }

    [Fact]
    public void Initialize_RunTwice_DoesNotDuplicate()
    {
        var factory = SeedLegacySettings("old-secret", enabled: true);

        DbInitializer.Initialize(factory, _protector);
        DbInitializer.Initialize(factory, _protector);

        using var db = factory.CreateDbContext();
        Assert.Single(db.ClientKeys.AsNoTracking().ToList());
    }

    [Fact]
    public void Initialize_WithoutLegacyKey_CreatesNoRows()
    {
        var factory = _db.CreateFactory();
        DbInitializer.Initialize(factory);

        using var db = factory.CreateDbContext();
        Assert.Empty(db.ClientKeys.AsNoTracking().ToList());
    }

    [Fact]
    public void Initialize_WhenLegacyKeyButNoProtector_Throws()
    {
        var factory = SeedLegacySettings("old-secret", enabled: true);

        // Mất protector = không giải mã được key cũ → fail loud thay vì âm thầm bỏ auth (spec §3)
        Assert.Throws<InvalidOperationException>(() => DbInitializer.Initialize(factory));
    }
}
```

- [ ] **Step 3: Chạy test — kỳ vọng FAIL (compile)**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ClientKeyHasherTests|FullyQualifiedName~DbInitializerLegacyKeyTests"
```
Expected: FAIL với `CS0246: The type or namespace name 'ClientKeyHasher' could not be found` (và `ClientKey`/`ClientKeys`).

- [ ] **Step 4: Implement entity `ClientKey`**

Tạo `src/RouterBalancing.Core/Domain/Entities/ClientKey.cs`:

```csharp
namespace RouterBalancing.Core.Domain;

/// <summary>
/// API key inbound của client gọi proxy - chỉ lưu SHA-256 (KeyHash), không lưu plaintext.
/// Counter Requests/Tokens reset lười theo <see cref="UsageDate"/> (ngày UTC).
/// </summary>
public class ClientKey
{
    public long Id { get; set; }

    /// <summary>Tên tag client, hiển thị trong bảng UI - không cần duy nhất.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 hex (64 ký tự) của key plaintext - unique index.</summary>
    public string KeyHash { get; set; } = string.Empty;

    /// <summary>Mask hiển thị (vd <c>sk-rb-…Ab12</c>) - hash không suy ra được 4 ký tự cuối.</summary>
    public string KeyMask { get; set; } = string.Empty;

    /// <summary>Revoke mềm: tắt vẫn giữ row, chỉ không match ở middleware.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Counter request trong ngày UTC <see cref="UsageDate"/>.</summary>
    public long RequestsUsed { get; set; }

    /// <summary>Counter token (prompt+completion) trong ngày UTC <see cref="UsageDate"/>.</summary>
    public long TokensUsed { get; set; }

    /// <summary>Ngày UTC của 2 counter trên - lệch ngày thì reset tại lần ghi đầu tiên.</summary>
    public DateOnly? UsageDate { get; set; }

    public DateTimeOffset? LastUsedAt { get; set; }

    /// <summary>RPM; null = không giới hạn.</summary>
    public int? RatePerMinute { get; set; }

    /// <summary>TPM; null = không giới hạn.</summary>
    public int? TokensPerMinute { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 5: Implement `ClientKeyHasher`**

Tạo `src/RouterBalancing.Core/Security/ClientKeyHasher.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace RouterBalancing.Core.Security;

/// <summary>Sinh/hash/mask client key inbound - SHA-256 hex, không bao giờ giữ plaintext.</summary>
public static class ClientKeyHasher
{
    public const string Prefix = "sk-rb-";

    /// <summary>SHA-256(utf8(key)) → hex lower 64 ký tự.</summary>
    public static string Hash(string plaintext) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(plaintext))).ToLowerInvariant();

    /// <summary><c>sk-rb-</c> + 43 ký tự base64url từ 32 bytes CSPRNG (256-bit).</summary>
    public static string GeneratePlaintext()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        var base64 = Convert.ToBase64String(bytes).TrimEnd('=');
        return Prefix + base64.Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Mask cho UI: key sinh ra → <c>sk-rb-…xxxx</c>; key cũ tùy ý → 4 đầu…4 cuối.</summary>
    public static string Mask(string plaintext)
    {
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal))
            return $"{Prefix}…{plaintext[^4..]}";
        if (plaintext.Length <= 8) return $"…{plaintext}";
        return $"{plaintext[..4]}…{plaintext[^4..]}";
    }
}
```

- [ ] **Step 6: Sửa `LogEntry` + `RouterBalancingDbContext`**

`LogEntry.cs` — thay dòng `public Guid? RequestId { get; set; }` thành:

```csharp
    /// <summary>Id 8 ký tự của request proxy (RequestId.New) - correlate các dòng cùng request.</summary>
    public string? RequestId { get; set; }
```

và thêm sau property `CompletionTokens`:

```csharp
    /// <summary>Client key đã dùng request này - null khi request đi qua khi auth đang mở.</summary>
    public long? ClientKeyId { get; set; }
```

`RouterBalancingDbContext.cs` — thêm DbSet cạnh `ProviderAccounts`:

```csharp
    /// <summary>API key inbound của client - chỉ lưu hash.</summary>
    public DbSet<ClientKey> ClientKeys => Set<ClientKey>();
```

và thêm cuối `OnModelCreating` (sau block `modelBuilder.Entity<AppSetting>`):

```csharp
        modelBuilder.Entity<ClientKey>(e =>
        {
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.KeyHash).IsRequired().HasMaxLength(64);
            e.Property(x => x.KeyMask).IsRequired().HasMaxLength(16);
            e.HasIndex(x => x.KeyHash).IsUnique();
            // DateOnly → "yyyy-MM-dd" TEXT: tường minh, không phụ thuộc mapping mặc định của provider
            e.Property(x => x.UsageDate).HasConversion(
                d => d.HasValue ? d.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) : null,
                s => s == null ? null : DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture));
        });
```

- [ ] **Step 7: Thêm `MigrateLegacyApiKey` vào `DbInitializer`**

`DbInitializer.cs` — thêm usings:

```csharp
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
```

Đổi 2 overload `Initialize` (giữ nguyên `BackfillIdentifiers` và các helper khác) và thêm method mới:

```csharp
    /// <summary>Tạo factory theo đường dẫn chuẩn rồi migrate — tiện cho app startup.</summary>
    /// <param name="legacyKeyProtector">Cần để giải mã apiKey cũ trong settings sang bảng ClientKeys.</param>
    public static void Initialize(ISecretProtector? legacyKeyProtector = null)
    {
        var options = new DbContextOptionsBuilder<RouterBalancingDbContext>()
            .UseSqlite($"Data Source={StoragePathProvider.GetDatabasePath()}")
            .Options;
        Initialize(new SimpleFactory(options), legacyKeyProtector);
    }

    public static void Initialize(IDbContextFactory<RouterBalancingDbContext> factory,
        ISecretProtector? legacyKeyProtector = null)
    {
        Directory.CreateDirectory(StoragePathProvider.GetDataDirectory());
        using var db = factory.CreateDbContext();
        db.Database.Migrate();
        BackfillIdentifiers(db);
        MigrateLegacyApiKey(db, legacyKeyProtector);
    }

    /// <summary>
    /// Chuyển apiKey 1 đầu trong settings (DPAPI) sang bảng ClientKeys rồi dọn row cũ — chạy 1 lần
    /// sau Migrate, idempotent nhờ guard "bảng đã có row" (spec client-keys §3).
    /// Đọc row settings TRỰC TIẾP qua DbContext (không qua IAppSettingsService — service có thể
    /// đã cache giá trị trước khi migrate). Tên 2 key là storage contract nên dùng literal:
    /// hằng SettingsKeys.ApiKey/ApiKeyEnabled bị gỡ ở task gỡ settings API (Task 5).
    /// </summary>
    private static void MigrateLegacyApiKey(RouterBalancingDbContext db, ISecretProtector? protector)
    {
        if (db.ClientKeys.Any()) return;

        var keyRow = db.AppSettings.AsNoTracking()
            .FirstOrDefault(a => a.Key == "apiKey");
        if (keyRow is null || string.IsNullOrWhiteSpace(keyRow.ValueJson)) return;

        // Không có protector = không giải mã được key cũ. Fail loud để startup dialog hiện rõ,
        // tránh khi UI gỡ settings apiKey (Task 5) auth âm thầm chuyển sang open.
        if (protector is null)
            throw new InvalidOperationException(
                "apiKey cũ tồn tại trong settings nhưng thiếu ISecretProtector để migrate sang ClientKeys.");

        // Unprotect lỗi (DB copy sang máy khác...) cũng phải nổi lên - không bỏ qua âm thầm.
        var plaintext = protector.Unprotect(keyRow.ValueJson);
        if (string.IsNullOrEmpty(plaintext)) return;

        var enabledRow = db.AppSettings.AsNoTracking()
            .FirstOrDefault(a => a.Key == "apiKeyEnabled");
        var enabled = enabledRow is not null
            && bool.TryParse(enabledRow.ValueJson, out var parsed) && parsed;

        db.ClientKeys.Add(new ClientKey
        {
            Name = "Legacy key",
            KeyHash = ClientKeyHasher.Hash(plaintext),
            KeyMask = ClientKeyHasher.Mask(plaintext),
            Enabled = enabled,
        });
        db.AppSettings.Remove(keyRow);
        if (enabledRow is not null) db.AppSettings.Remove(enabledRow);
        // 1 SaveChanges duy nhất = atomic best-effort (spec §3): migrate xong thì row cũ đi kèm,
        // thất bại → DB giữ nguyên, lần khởi động sau retry (idempotent) — không bao giờ mất key.
        db.SaveChanges();
    }
```

- [ ] **Step 8: Sửa `MauiProgram.TryInitializeDatabase`**

Thay dòng `DbInitializer.Initialize();` bằng:

```csharp
                // Protector cho legacy apiKey migration: settings key cũ phải giải mã được
                // trước khi UI gỡ settings apiKey (spec client-keys §3).
                DbInitializer.Initialize(new DpapiSecretProtector());
```

(`DpapiSecretProtector` đã có using `RouterBalancing.Core.Security` từ registration `AddSingleton<ISecretProtector, DpapiSecretProtector>`.)

- [ ] **Step 9: Tạo EF migration**

```powershell
dotnet ef migrations add AddClientKeys --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations
```
Expected: file `<timestamp>_AddClientKeys.cs` + `.Designer.cs` mới + `RouterBalancingDbContextModelSnapshot.cs` cập nhật; không lỗi. Diff cho `LogEntry.RequestId` (Guid→string) là bình thường (cả 2 TEXT trên SQLite, data NULL).

- [ ] **Step 10: Chạy test — kỳ vọng PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ClientKeyHasherTests|FullyQualifiedName~DbInitializerLegacyKeyTests"
```
Expected: 12 passed (7 hasher + 5 legacy).

- [ ] **Step 11: Gates**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # MauiProgram đổi; app phải đóng
dotnet test "router balancing test/router balancing test.csproj"
```
Expected: 0 error; toàn bộ suite xanh (baseline 360 + 12 test mới; test cũ không vỡ).

- [ ] **Step 12: Commit**

```powershell
git add -A
git commit -m "feat: add ClientKey entity with legacy settings migration"
```

---

### Task 2: ClientKeyService (CRUD + counter daily) + DI wiring

**Files:**
- Create: `src/RouterBalancing.Core/Server/ClientKeyDraft.cs`
- Create: `src/RouterBalancing.Core/Server/IClientKeyService.cs`
- Create: `src/RouterBalancing.Core/Server/ClientKeyService.cs`
- Modify: `router-balancing/MauiProgram.cs` (đăng ký sau dòng `IComboService`, :78)
- Modify: `src/RouterBalancing.Core/Server/ProxyHost.cs` (ctor + field + `AddSingleton(_clientKeys)`)
- Modify: `router balancing test/Server/ProxyHostTests.cs` (3 chỗ `new ProxyHost(...)`: :42, :155, :169)
- Create: `router balancing test/Server/ClientKeyServiceTests.cs`

**Interfaces:**
- Consumes: `ClientKey` + `ClientKeyHasher` (Task 1), `IDbContextFactory<RouterBalancingDbContext>` (đã đăng ký ở MauiProgram :52 và mọi test builder).
- Produces (Task 3+): `IClientKeyService` — event `KeysChanged` (auth cache invalidate), CRUD trả plaintext 1 lần, `RecordRequestAsync`/`RecordTokensAsync` (lazy reset theo ngày UTC).

- [ ] **Step 1: Viết `ClientKeyDraft` + interface**

Tạo `src/RouterBalancing.Core/Server/ClientKeyDraft.cs`:

```csharp
namespace RouterBalancing.Core.Server;

/// <summary>Dữ liệu tạo/sửa client key — không chứa plaintext (key mới chỉ sinh ngẫu nhiên).</summary>
public sealed record ClientKeyDraft(string Name, int? RatePerMinute, int? TokensPerMinute);
```

Tạo `src/RouterBalancing.Core/Server/IClientKeyService.cs`:

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Server;

/// <summary>
/// CRUD + counter daily cho API key inbound. <see cref="KeysChanged"/> phát sau mọi thay đổi
/// ảnh hưởng auth (create/update/delete/enable) để auth cache của proxy invalidate ngay (spec §4.1).
/// </summary>
public interface IClientKeyService
{
    event Action? KeysChanged;

    Task<IReadOnlyList<ClientKey>> ListAsync(CancellationToken ct = default);

    /// <summary>Tạo key mới, trả plaintext 1 lần cho UI hiển thị — server không giữ lại.</summary>
    Task<(ClientKey Key, string Plaintext)> CreateAsync(ClientKeyDraft draft, CancellationToken ct = default);

    Task UpdateAsync(long id, ClientKeyDraft draft, CancellationToken ct = default);

    Task DeleteAsync(long id, CancellationToken ct = default);

    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);

    /// <summary>Cộng 1 vào counter daily + cập nhật LastUsedAt (gọi từ middleware sau khi auth qua).</summary>
    Task RecordRequestAsync(long id, CancellationToken ct = default);

    /// <summary>Cộng prompt+completion vào counter daily (gọi từ usage sink).</summary>
    Task RecordTokensAsync(long id, int promptTokens, int completionTokens, CancellationToken ct = default);
}
```

- [ ] **Step 2: Implement `ClientKeyService`**

Tạo `src/RouterBalancing.Core/Server/ClientKeyService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Server;

/// <inheritdoc cref="IClientKeyService"/>
public sealed class ClientKeyService : IClientKeyService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;

    public event Action? KeysChanged;

    public ClientKeyService(IDbContextFactory<RouterBalancingDbContext> db) => _db = db;

    public async Task<IReadOnlyList<ClientKey>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.ClientKeys.AsNoTracking().OrderBy(k => k.Id).ToListAsync(ct);
    }

    public async Task<(ClientKey Key, string Plaintext)> CreateAsync(ClientKeyDraft draft, CancellationToken ct = default)
    {
        Validate(draft);
        var plaintext = ClientKeyHasher.GeneratePlaintext();
        var now = DateTimeOffset.UtcNow;
        var entity = new ClientKey
        {
            Name = draft.Name.Trim(),
            KeyHash = ClientKeyHasher.Hash(plaintext),
            KeyMask = ClientKeyHasher.Mask(plaintext),
            Enabled = true,
            RatePerMinute = draft.RatePerMinute,
            TokensPerMinute = draft.TokensPerMinute,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            db.ClientKeys.Add(entity);
            await db.SaveChangesAsync(ct);
        }
        KeysChanged?.Invoke();
        return (entity, plaintext);
    }

    public async Task UpdateAsync(long id, ClientKeyDraft draft, CancellationToken ct = default)
    {
        Validate(draft);
        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            var key = await db.ClientKeys.FirstOrDefaultAsync(k => k.Id == id, ct)
                ?? throw new KeyNotFoundException($"Client key {id} không tồn tại.");
            key.Name = draft.Name.Trim();
            key.RatePerMinute = draft.RatePerMinute;
            key.TokensPerMinute = draft.TokensPerMinute;
            key.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        KeysChanged?.Invoke();
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            var key = await db.ClientKeys.FirstOrDefaultAsync(k => k.Id == id, ct)
                ?? throw new KeyNotFoundException($"Client key {id} không tồn tại.");
            db.ClientKeys.Remove(key);
            await db.SaveChangesAsync(ct);
        }
        KeysChanged?.Invoke();
    }

    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        await using (var db = await _db.CreateDbContextAsync(ct))
        {
            var key = await db.ClientKeys.FirstOrDefaultAsync(k => k.Id == id, ct)
                ?? throw new KeyNotFoundException($"Client key {id} không tồn tại.");
            key.Enabled = enabled;
            key.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        KeysChanged?.Invoke();
    }

    public async Task RecordRequestAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var key = await db.ClientKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        // Key bị xóa giữa chừng sau khi auth qua — bỏ qua counter, không throw ra middleware
        if (key is null) return;
        ResetDailyIfNeeded(key, DateTimeOffset.UtcNow);
        key.RequestsUsed++;
        key.LastUsedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    public async Task RecordTokensAsync(long id, int promptTokens, int completionTokens, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var key = await db.ClientKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null) return;
        ResetDailyIfNeeded(key, DateTimeOffset.UtcNow);
        key.TokensUsed += promptTokens + completionTokens;
        await db.SaveChangesAsync(ct);
    }

    // Reset lười tại thời điểm ghi thay vì job nền: counter daily không cần chính xác tuyệt đối
    // tại 00:00 UTC, chỉ cần không bao giờ cộng dồn qua ngày (spec §6.2).
    private static void ResetDailyIfNeeded(ClientKey key, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        if (key.UsageDate == today) return;
        key.UsageDate = today;
        key.RequestsUsed = 0;
        key.TokensUsed = 0;
    }

    // ArgumentException → UI bắt được và hiện validation message cạnh field (không cần custom exception)
    private static void Validate(ClientKeyDraft draft)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
            throw new ArgumentException("Tên key là bắt buộc.", nameof(draft));
        if (draft.Name.Trim().Length > 100)
            throw new ArgumentException("Tên key tối đa 100 ký tự.", nameof(draft));
        if (draft.RatePerMinute is <= 0)
            throw new ArgumentException("RatePerMinute phải lớn hơn 0.", nameof(draft));
        if (draft.TokensPerMinute is <= 0)
            throw new ArgumentException("TokensPerMinute phải lớn hơn 0.", nameof(draft));
    }
}
```

- [ ] **Step 3: Đăng ký DI — MauiProgram + ProxyHost**

`router-balancing/MauiProgram.cs` — thêm sau dòng `builder.Services.AddSingleton<IComboService, ComboService>();` (:78):

```csharp
            // Singleton chia sẻ với proxy container qua ProxyHost (spec §4.1) — event KeysChanged
            // của CHÍNH instance này làm auth cache của proxy invalidate khi UI CRUD key.
            builder.Services.AddSingleton<IClientKeyService, ClientKeyService>();
```

`src/RouterBalancing.Core/Server/ProxyHost.cs` — 3 sửa đổi:

1. Thêm field cạnh `_protector` (:22):

```csharp
    private readonly IClientKeyService _clientKeys;
```

2. Ctor (:33-40) — thêm tham số cuối + gán:

```csharp
    public ProxyHost(IAppSettingsService settings, ILogService log, IDbContextFactory<RouterBalancingDbContext> db,
        ISecretProtector protector, IClientKeyService clientKeys)
    {
        _settings = settings;
        _log = log;
        _db = db;
        _protector = protector;
        _clientKeys = clientKeys;
    }
```

3. `StartAsync` — thêm sau `builder.Services.AddSingleton(_db);` (:60):

```csharp
            builder.Services.AddSingleton(_clientKeys);
```

- [ ] **Step 4: Cập nhật `ProxyHostTests` (3 chỗ ctor)**

File `router balancing test/Server/ProxyHostTests.cs` — thay **cả 3** dòng identical:

```csharp
var host = new ProxyHost(_settings, _log, _db.CreateFactory(), new DpapiSecretProtector());
```

bằng (dùng field, tái sử dụng cho seed Task 3):

```csharp
var host = new ProxyHost(_settings, _log, _db.CreateFactory(), new DpapiSecretProtector(), _clientKeys);
```

và khai báo field + khởi tạo trong ctor (bên cạnh `_settings`/`_log`, sau `DbInitializer.Initialize` :24):

```csharp
    private readonly ClientKeyService _clientKeys;

    // trong ctor, sau dòng DbInitializer.Initialize(...):
    _clientKeys = new ClientKeyService(_db.CreateFactory());
```

- [ ] **Step 5: Viết test `ClientKeyServiceTests`**

Tạo `router balancing test/Server/ClientKeyServiceTests.cs`:

```csharp
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

public class ClientKeyServiceTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ClientKeyService _service;

    public ClientKeyServiceTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _service = new ClientKeyService(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    private static ClientKeyDraft Draft(string name = "app", int? rpm = null, int? tpm = null)
        => new(name, rpm, tpm);

    [Fact]
    public async Task Create_ReturnsPlaintextAndPersistsOnlyHash()
    {
        var (key, plaintext) = await _service.CreateAsync(Draft());

        Assert.StartsWith("sk-rb-", plaintext);
        await using var db = _db.CreateFactory().CreateDbContext();
        var saved = await db.ClientKeys.SingleAsync(k => k.Id == key.Id);
        Assert.Equal(ClientKeyHasher.Hash(plaintext), saved.KeyHash);
        Assert.Equal(ClientKeyHasher.Mask(plaintext), saved.KeyMask);
        Assert.DoesNotContain(plaintext, saved.KeyHash + saved.KeyMask + saved.Name);
        Assert.True(saved.Enabled);
    }

    [Fact]
    public async Task Create_TriggersKeysChanged()
    {
        var raised = 0;
        _service.KeysChanged += () => raised++;

        await _service.CreateAsync(Draft());

        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Create_WhenNameBlank_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(Draft("  ")));

    [Fact]
    public async Task Create_WhenNameOver100Chars_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(Draft(new string('x', 101))));

    [Fact]
    public async Task Create_WhenLimitNotPositive_Throws()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(Draft("a", rpm: 0)));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(Draft("a", tpm: -1)));
    }

    [Fact]
    public async Task List_ReturnsAllOrderedById()
    {
        await _service.CreateAsync(Draft("first"));
        await _service.CreateAsync(Draft("second"));

        var list = await _service.ListAsync();

        Assert.Equal(new[] { "first", "second" }, list.Select(k => k.Name).ToArray());
    }

    [Fact]
    public async Task Update_ChangesNameAndLimits_KeepsHash()
    {
        var (key, plaintext) = await _service.CreateAsync(Draft());

        await _service.UpdateAsync(key.Id, Draft("renamed", rpm: 60, tpm: 1000));

        var updated = await _service.ListAsync();
        var saved = Assert.Single(updated);
        Assert.Equal("renamed", saved.Name);
        Assert.Equal(60, saved.RatePerMinute);
        Assert.Equal(1000, saved.TokensPerMinute);
        Assert.Equal(ClientKeyHasher.Hash(plaintext), saved.KeyHash);
    }

    [Fact]
    public async Task Update_WhenMissing_Throws()
        => await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.UpdateAsync(999, Draft()));

    [Fact]
    public async Task Delete_RemovesRowAndTriggersKeysChanged()
    {
        var (key, _) = await _service.CreateAsync(Draft());
        var raised = 0;
        _service.KeysChanged += () => raised++;

        await _service.DeleteAsync(key.Id);

        Assert.Empty(await _service.ListAsync());
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task SetEnabled_TogglesAndTriggersKeysChanged()
    {
        var (key, _) = await _service.CreateAsync(Draft());
        var raised = 0;
        _service.KeysChanged += () => raised++;

        await _service.SetEnabledAsync(key.Id, false);

        Assert.False((await _service.ListAsync()).Single().Enabled);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task RecordRequest_IncrementsCounterAndLastUsed()
    {
        var (key, _) = await _service.CreateAsync(Draft());

        await _service.RecordRequestAsync(key.Id);

        var saved = (await _service.ListAsync()).Single();
        Assert.Equal(1, saved.RequestsUsed);
        Assert.NotNull(saved.LastUsedAt);
        Assert.NotNull(saved.UsageDate);
    }

    [Fact]
    public async Task RecordRequest_WhenUsageDateOld_ResetsCountersFirst()
    {
        var (key, _) = await _service.CreateAsync(Draft());
        await using (var db = _db.CreateFactory().CreateDbContext())
        {
            var entity = await db.ClientKeys.SingleAsync(k => k.Id == key.Id);
            entity.UsageDate = new DateOnly(2000, 1, 1);
            entity.RequestsUsed = 50;
            entity.TokensUsed = 100;
            await db.SaveChangesAsync();
        }

        await _service.RecordRequestAsync(key.Id);

        var saved = (await _service.ListAsync()).Single();
        Assert.Equal(1, saved.RequestsUsed);   // reset về 0 rồi +1
        Assert.Equal(0, saved.TokensUsed);
    }

    [Fact]
    public async Task RecordTokens_AddsPromptPlusCompletion()
    {
        var (key, _) = await _service.CreateAsync(Draft());

        await _service.RecordTokensAsync(key.Id, 12, 34);

        Assert.Equal(46, (await _service.ListAsync()).Single().TokensUsed);
    }

    [Fact]
    public async Task RecordTokens_WhenUsageDateOld_ResetsBeforeAdd()
    {
        var (key, _) = await _service.CreateAsync(Draft());
        await using (var db = _db.CreateFactory().CreateDbContext())
        {
            var entity = await db.ClientKeys.SingleAsync(k => k.Id == key.Id);
            entity.UsageDate = new DateOnly(2000, 1, 1);
            entity.RequestsUsed = 50;
            entity.TokensUsed = 100;
            await db.SaveChangesAsync();
        }

        await _service.RecordTokensAsync(key.Id, 3, 4);

        var saved = (await _service.ListAsync()).Single();
        Assert.Equal(7, saved.TokensUsed);     // reset về 0 rồi +7
        Assert.Equal(0, saved.RequestsUsed);
    }

    [Fact]
    public async Task RecordRequest_WhenKeyDeleted_NoThrow()
    {
        await _service.RecordRequestAsync(999); // fail-open: counter là thống kê, không được nổ
    }
}
```

- [ ] **Step 6: Chạy test mới — kỳ vọng PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ClientKeyServiceTests"
```
Expected: 15 passed.

- [ ] **Step 7: Gates**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # MauiProgram + ProxyHost đổi; app đóng
dotnet test "router balancing test/router balancing test.csproj"
```
Expected: 0 error; suite xanh — `ProxyHostTests` vẫn pass vì middleware Task 3 chưa đổi.

- [ ] **Step 8: Commit**

```powershell
git add -A
git commit -m "feat: add client key service with CRUD and counters"
```

---

### Task 3: Auth middleware table-based + rate limit RPM/TPM + wiring integration tests

**Files:**
- Create: `src/RouterBalancing.Core/Server/ClientKeyItems.cs`
- Create: `src/RouterBalancing.Core/Server/ClientKeyAuthCache.cs` (kèm record `ClientKeyAuthInfo`)
- Create: `src/RouterBalancing.Core/Server/IClientKeyRateLimiter.cs` + `ClientKeyRateLimiter.cs`
- Modify: `src/RouterBalancing.Core/Server/ApiKeyMiddleware.cs` (rewrite toàn bộ)
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs` (`ConfigureServices`: đăng ký limiter + cache)
- Create: `router balancing test/Server/ClientKeyRateLimiterTests.cs`
- Rewrite: `router balancing test/Server/ApiKeyMiddlewareTests.cs`
- Modify: `ProxyHostTests.cs` (2 test seed key), `ProxyAppChatIntegrationTests.cs`, `ProxyRetryIntegrationTests.cs`, `ProxyQueueIntegrationTests.cs`, `ProxyControlApiTests.cs` (đăng ký `IClientKeyService` vào builder)

**Interfaces:**
- Consumes: `IClientKeyService` (Task 2), `TimeProvider.System` đã đăng ký ở `ProxyApp.ConfigureServices` (:49).
- Produces: middleware trả 401/429 OpenAI-shape (spec §6), `HttpContext.Items[ClientKeyItems.Id]` (Task 4 attribute usage row), snapshot invalidate qua `KeysChanged` (§4.1).

- [ ] **Step 1: `ClientKeyItems` + `ClientKeyAuthCache`**

Tạo `src/RouterBalancing.Core/Server/ClientKeyItems.cs`:

```csharp
using Microsoft.AspNetCore.Http;

namespace RouterBalancing.Core.Server;

/// <summary>Khóa HttpContext.Items do middleware điền — các tầng sau (handler, journal) đọc lại.</summary>
public static class ClientKeyItems
{
    /// <summary>Client key id đã auth (dùng cho usage row + journal).</summary>
    public static readonly string Id = "ClientKeyId";

    /// <summary>Request id 8 ký tự (ProxyApp.New) — correlate journal rows + usage row.</summary>
    public static readonly string RequestId = "RequestId";

    public static long? IdOf(HttpContext context) =>
        context.Items.TryGetValue(Id, out var value) ? value as long? : null;

    public static string? RequestIdOf(HttpContext context) =>
        context.Items.TryGetValue(RequestId, out var value) ? value as string : null;
}
```

Tạo `src/RouterBalancing.Core/Server/ClientKeyAuthCache.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Server;

/// <summary>Dòng match auth từ snapshot — record nhỏ cho hot path, không giữ entity EF.</summary>
public sealed record ClientKeyAuthInfo(long Id, string KeyHash, int? RatePerMinute, int? TokensPerMinute);

/// <summary>
/// Snapshot key đang enabled trong RAM: request không đụng DB, chỉ reload khi
/// <see cref="IClientKeyService.KeysChanged"/> — UI CRUD key có hiệu lực ngay, không restart proxy (spec §4.1).
/// </summary>
public sealed class ClientKeyAuthCache : IDisposable
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly IClientKeyService _keys;
    private readonly object _gate = new();
    private ClientKeyAuthInfo[]? _snapshot;
    private bool _dirty = true;
    private bool _disposed;

    public ClientKeyAuthCache(IDbContextFactory<RouterBalancingDbContext> db, IClientKeyService keys)
    {
        _db = db;
        _keys = keys;
        _keys.KeysChanged += OnKeysChanged;
    }

    public ClientKeyAuthInfo[] GetEnabled()
    {
        lock (_gate)
        {
            if (_snapshot is not null && !_dirty) return _snapshot;
        }

        // Đọc DB ngoài lock (không giữ lock khi I/O); nếu trong lúc đọc có KeysChanged nữa thì
        // _dirty vẫn true và snapshot cũ được giữ, lần gọi sau sẽ đọc lại — không bao giờ nuốt invalidate.
        using var db = _db.CreateDbContext();
        var fresh = db.ClientKeys.AsNoTracking()
            .Where(k => k.Enabled)
            .OrderBy(k => k.Id)
            .Select(k => new ClientKeyAuthInfo(k.Id, k.KeyHash, k.RatePerMinute, k.TokensPerMinute))
            .ToArray();

        lock (_gate)
        {
            if (_dirty)
            {
                _snapshot = fresh;
                _dirty = false;
            }
            return _snapshot!;
        }
    }

    private void OnKeysChanged() => SetDirty();

    private void SetDirty()
    {
        lock (_gate) { _dirty = true; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Không unsubscribe = restart proxy nhiều lần giữ delegate vào cache đã chết (leak)
        _keys.KeysChanged -= OnKeysChanged;
    }
}
```

- [ ] **Step 2: `ClientKeyRateLimiter`**

Tạo `src/RouterBalancing.Core/Server/IClientKeyRateLimiter.cs`:

```csharp
namespace RouterBalancing.Core.Server;

/// <summary>Rate limit RPM/TPM theo key trên window 60s in-memory (spec §6).</summary>
public interface IClientKeyRateLimiter
{
    /// <summary>
    /// Tiêu 1 request vào window của key. Trả <c>(false, retryAfterSec)</c> khi vượt RPM
    /// hoặc token window đã chạm TPM; caller trả 429 + Retry-After.
    /// </summary>
    (bool Allowed, int RetryAfterSec) TryEnter(long keyId, int? ratePerMinute, int? tokensPerMinute);

    /// <summary>Cộng token usage (prompt+completion) vào window hiện tại — chỉ để chặn request sau (spec §6).</summary>
    void AddTokens(long keyId, int tokens);
}
```

Tạo `src/RouterBalancing.Core/Server/ClientKeyRateLimiter.cs`:

```csharp
namespace RouterBalancing.Core.Server;

/// <inheritdoc cref="IClientKeyRateLimiter"/>
/// <remarks>
/// Window cố định 60s, state sống trong RAM của tiến trình proxy — đủ vì proxy là 1 instance
/// loopback duy nhất; không cần share đa process. Dùng <see cref="TimeProvider"/> để test rollover.
/// </remarks>
public sealed class ClientKeyRateLimiter(TimeProvider time) : IClientKeyRateLimiter
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromSeconds(60);

    private readonly Dictionary<long, Window> _windows = new();
    private readonly object _gate = new();

    private sealed class Window
    {
        public long Requests;
        public long Tokens;
        public DateTimeOffset StartedAt;
    }

    public (bool Allowed, int RetryAfterSec) TryEnter(long keyId, int? ratePerMinute, int? tokensPerMinute)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var window = GetOrCreate(keyId, now);

            if (tokensPerMinute is int tpm && window.Tokens >= tpm)
                return (false, RetryAfterSec(now, window));

            if (ratePerMinute is int rpm && window.Requests + 1 > rpm)
                return (false, RetryAfterSec(now, window));

            window.Requests++;
            return (true, 0);
        }
    }

    public void AddTokens(long keyId, int tokens)
    {
        lock (_gate)
        {
            GetOrCreate(keyId, time.GetUtcNow()).Tokens += tokens;
        }
    }

    private Window GetOrCreate(long keyId, DateTimeOffset now)
    {
        if (!_windows.TryGetValue(keyId, out var window) || now - window.StartedAt >= WindowLength)
        {
            window = new Window { StartedAt = now };
            _windows[keyId] = window;
        }
        return window;
    }

    // ceil lên tối thiểu 1: Retry-After: 0 khuyến khích client bắn lại ngay — ngược tác dụng chống hammer
    private static int RetryAfterSec(DateTimeOffset now, Window window)
        => Math.Max(1, (int)Math.Ceiling((window.StartedAt + WindowLength - now).TotalSeconds));
}
```

- [ ] **Step 3: Rewrite `ApiKeyMiddleware`**

Thay toàn bộ nội dung `src/RouterBalancing.Core/Server/ApiKeyMiddleware.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;

namespace RouterBalancing.Core.Server;

/// <summary>
/// Xác thực API key inbound từ bảng ClientKeys (snapshot in-memory qua <see cref="ClientKeyAuthCache"/>)
/// + chặn RPM/TPM trước khi vào pipeline (spec client-keys §6).
/// Không key enabled nào = proxy mở (backward-compat với cài đặt cũ chưa set key).
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, ClientKeyAuthCache keys,
    IClientKeyRateLimiter limiter, IClientKeyService service, ILogService log)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // /health luôn mở để watchdog/monitor không cần secret (spec: port + health)
        if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var enabled = keys.GetEnabled();
        if (enabled.Length == 0)
        {
            await next(context);
            return;
        }

        var provided = ExtractKey(context.Request);
        var match = provided is null ? null : FindByHash(provided, enabled);
        if (match is null)
        {
            await RejectAsync(context, StatusCodes.Status401Unauthorized,
                "Invalid or missing API key", "invalid_request_error", "invalid_api_key");
            return;
        }

        var (allowed, retryAfterSec) = limiter.TryEnter(match.Id, match.RatePerMinute, match.TokensPerMinute);
        if (!allowed)
        {
            context.Response.Headers.RetryAfter = retryAfterSec.ToString();
            await RejectAsync(context, StatusCodes.Status429TooManyRequests,
                "Rate limit exceeded", "rate_limit_exceeded", "rate_limit_exceeded");
            return;
        }

        context.Items[ClientKeyItems.Id] = match.Id;

        // Counter daily là thống kê — DB lỗi không được chặn request (fail-open, spec §10)
        try
        {
            await service.RecordRequestAsync(match.Id);
        }
        catch (Exception ex)
        {
            log.Error("Không ghi được counter request cho client key.", ex, LogCategory.Request);
        }

        await next(context);
    }

    private static string? ExtractKey(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        if (authorization.StartsWith(bearer, StringComparison.OrdinalIgnoreCase))
            return authorization[bearer.Length..].Trim();

        var headerKey = request.Headers["X-API-Key"].ToString();
        return string.IsNullOrEmpty(headerKey) ? null : headerKey;
    }

    private static ClientKeyAuthInfo? FindByHash(string provided, ClientKeyAuthInfo[] enabled)
    {
        var hash = ClientKeyHasher.Hash(provided);
        foreach (var info in enabled)
        {
            // FixedTimeEquals trên hash hex (luôn 64 ký tự) — không rò rỉ key nào match qua timing
            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(hash), Encoding.UTF8.GetBytes(info.KeyHash)))
                return info;
        }
        return null;
    }

    private static Task RejectAsync(HttpContext context, int status, string message, string type, string code) =>
        // Tái dùng WriteErrorAsync có sẵn: shape OpenAI + encoder relax qua ErrorJsonOptions (spec §10)
        ChatCompletionsHandler.WriteErrorAsync(context, status, message, type, param: null, code);
}
```

- [ ] **Step 4: Đăng ký limiter + cache trong `ProxyApp.ConfigureServices`**

`src/RouterBalancing.Core/Server/ProxyApp.cs` — thêm sau `builder.Services.AddSingleton(TimeProvider.System);` (:49):

```csharp
        builder.Services.AddSingleton<IClientKeyRateLimiter, ClientKeyRateLimiter>();
        // Singleton (không factory): cache phải sống 1 lần/proxy container để event KeysChanged
        // attach đúng 1 lần; Dispose của container unsubscribe khi proxy dừng.
        builder.Services.AddSingleton<ClientKeyAuthCache>();
```

`IClientKeyService` **không** đăng ký ở đây — host (ProxyHost :60) và test builder tự đăng ký (pattern `_settings`).

- [ ] **Step 5: Test limiter — tạo `ClientKeyRateLimiterTests`**

Tạo `router balancing test/Server/ClientKeyRateLimiterTests.cs`:

```csharp
using RouterBalancing.Core.Server;

namespace router_balancing_test.Server;

public class ClientKeyRateLimiterTests
{
    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryEnter_BelowRpm_AllowsAndCounts()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));

        Assert.True(limiter.TryEnter(1, ratePerMinute: 2, tokensPerMinute: null).Allowed);
        Assert.True(limiter.TryEnter(1, ratePerMinute: 2, tokensPerMinute: null).Allowed);
    }

    [Fact]
    public void TryEnter_ExceedingRpm_DeniesWithRetryAfter()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));
        limiter.TryEnter(1, 1, null);

        var (allowed, retryAfter) = limiter.TryEnter(1, 1, null);

        Assert.False(allowed);
        Assert.InRange(retryAfter, 1, 60);
    }

    [Fact]
    public void TryEnter_AfterWindowRollover_AllowsAgain()
    {
        var time = new FakeTime(Start);
        var limiter = new ClientKeyRateLimiter(time);
        limiter.TryEnter(1, 1, null);
        Assert.False(limiter.TryEnter(1, 1, null).Allowed);

        time.Advance(TimeSpan.FromSeconds(61));

        Assert.True(limiter.TryEnter(1, 1, null).Allowed);
    }

    [Fact]
    public void TryEnter_WhenRpmNull_NeverLimits()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));

        for (var i = 0; i < 100; i++)
            Assert.True(limiter.TryEnter(1, null, null).Allowed);
    }

    [Fact]
    public void TryEnter_WhenWindowTokensReachTpm_Denies()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));
        limiter.AddTokens(1, 5);

        var (allowed, retryAfter) = limiter.TryEnter(1, null, tokensPerMinute: 5);

        Assert.False(allowed);
        Assert.InRange(retryAfter, 1, 60);
    }

    [Fact]
    public void TryEnter_WhenWindowTokensBelowTpm_Allows()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));
        limiter.AddTokens(1, 4);

        Assert.True(limiter.TryEnter(1, null, 5).Allowed);
    }

    [Fact]
    public void TryEnter_KeysAreIndependent()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));
        limiter.TryEnter(1, 1, null);

        Assert.False(limiter.TryEnter(1, 1, null).Allowed);
        Assert.True(limiter.TryEnter(2, 1, null).Allowed);
    }

    [Fact]
    public void AddTokens_AccumulatesWithinWindow()
    {
        var limiter = new ClientKeyRateLimiter(new FakeTime(Start));
        limiter.AddTokens(1, 3);
        limiter.AddTokens(1, 4);

        Assert.False(limiter.TryEnter(1, null, 7).Allowed);
        Assert.True(limiter.TryEnter(1, null, 8).Allowed);
    }
}
```

- [ ] **Step 6: Rewrite `ApiKeyMiddlewareTests`**

Thay toàn bộ `router balancing test/Server/ApiKeyMiddlewareTests.cs`:

```csharp
using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

public class ApiKeyMiddlewareTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ClientKeyService _keys;
    private readonly ClientKeyAuthCache _cache;
    private readonly ClientKeyRateLimiter _limiter = new(TimeProvider.System);
    private readonly LogService _log;

    public ApiKeyMiddlewareTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _keys = new ClientKeyService(_db.CreateFactory());
        _cache = new ClientKeyAuthCache(_db.CreateFactory(), _keys);
        _log = new LogService(_db.CreateFactory());
    }

    public void Dispose()
    {
        _cache.Dispose();
        _db.Dispose();
    }

    private async Task<string> CreateKeyAsync(bool enabled = true, int? rpm = null, int? tpm = null)
    {
        var (key, plaintext) = await _keys.CreateAsync(new ClientKeyDraft("test", rpm, tpm));
        if (!enabled) await _keys.SetEnabledAsync(key.Id, false);
        return plaintext;
    }

    private async Task<(int StatusCode, bool NextCalled, string Body, object? KeyId)> InvokeAsync(
        string path, string? authorization = null, string? apiKeyHeader = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        if (apiKeyHeader is not null) context.Request.Headers["X-API-Key"] = apiKeyHeader;
        context.Response.Body = new MemoryStream();

        var nextCalled = false;
        var middleware = new ApiKeyMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            _cache, _limiter, _keys, _log);
        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        context.Items.TryGetValue(ClientKeyItems.Id, out var keyId);
        return (context.Response.StatusCode, nextCalled, body, keyId);
    }

    [Fact]
    public async Task Invoke_WhenNoEnabledKeys_PassesThrough()
    {
        var (status, nextCalled, _, _) = await InvokeAsync("/v1/chat/completions");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_OnHealthPath_SkipsAuth_EvenWithKey()
    {
        await CreateKeyAsync();

        var (status, nextCalled, _, _) = await InvokeAsync("/health");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenKeyMissing_Returns401WithErrorBody()
    {
        await CreateKeyAsync();

        var (status, nextCalled, body, _) = await InvokeAsync("/v1/models");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
        Assert.Contains("invalid_api_key", body);
    }

    [Fact]
    public async Task Invoke_WhenKeyWrong_Returns401()
    {
        await CreateKeyAsync();

        var (status, nextCalled, _, _) = await InvokeAsync("/v1/models", authorization: "Bearer wrong-key");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenBearerKeyCorrect_PassesThroughAndSetsKeyId()
    {
        var plaintext = await CreateKeyAsync();

        var (status, nextCalled, _, keyId) = await InvokeAsync(
            "/v1/models", authorization: $"Bearer {plaintext}");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
        Assert.NotNull(keyId);
    }

    [Fact]
    public async Task Invoke_WhenXApiKeyHeaderCorrect_PassesThrough()
    {
        var plaintext = await CreateKeyAsync();

        var (status, nextCalled, _, _) = await InvokeAsync("/v1/models", apiKeyHeader: plaintext);

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenAllKeysDisabled_PassesThrough_OpenMode()
    {
        await CreateKeyAsync(enabled: false);

        var (status, nextCalled, _, _) = await InvokeAsync("/v1/models");

        Assert.Equal(200, status);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Invoke_WhenOneKeyEnabled_DisabledKeyRejected()
    {
        await CreateKeyAsync(enabled: false);
        await CreateKeyAsync(enabled: true);

        var (status, _, _, _) = await InvokeAsync("/v1/models", apiKeyHeader: "sk-rb-wrong");

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    [Fact]
    public async Task Cache_ReflectsKeyCreatedAfterFirstCall_WithoutRestart()
    {
        // Call 1: chưa có key → open
        var (firstStatus, _, _, _) = await InvokeAsync("/v1/models");
        Assert.Equal(200, firstStatus);

        // CRUD key → KeysChanged → cache dirty
        await CreateKeyAsync();

        var (secondStatus, _, _, _) = await InvokeAsync("/v1/models");
        Assert.Equal(StatusCodes.Status401Unauthorized, secondStatus);
    }

    [Fact]
    public async Task Invoke_WhenRpmExceeded_Returns429WithRetryAfter()
    {
        var plaintext = await CreateKeyAsync(rpm: 1);

        var (first, _, _, _) = await InvokeAsync("/v1/models", apiKeyHeader: plaintext);
        Assert.Equal(200, first);

        var context = new DefaultHttpContext();
        context.Request.Path = "/v1/models";
        context.Request.Headers["X-API-Key"] = plaintext;
        context.Response.Body = new MemoryStream();
        var middleware = new ApiKeyMiddleware(_ => Task.CompletedTask, _cache, _limiter, _keys, _log);
        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.True(int.TryParse(context.Response.Headers.RetryAfter, out var retry) && retry >= 1);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync();
        Assert.Contains("rate_limit_exceeded", body);
    }

    [Fact]
    public async Task Invoke_WhenTpmReached_Returns429()
    {
        var plaintext = await CreateKeyAsync(tpm: 5);
        var (key, _) = (await _keys.ListAsync()).Single();
        _limiter.AddTokens(key.Id, 5);

        var (status, nextCalled, _, _) = await InvokeAsync("/v1/models", apiKeyHeader: plaintext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, status);
        Assert.False(nextCalled);
    }

    [Fact]
    public async Task Invoke_AfterSuccessfulAuth_IncrementsDailyCounter()
    {
        var plaintext = await CreateKeyAsync();

        await InvokeAsync("/v1/models", apiKeyHeader: plaintext);

        var saved = (await _keys.ListAsync()).Single();
        Assert.Equal(1, saved.RequestsUsed);
        Assert.NotNull(saved.LastUsedAt);
    }

    [Fact]
    public async Task Invoke_WhenRequestDeniedByRateLimit_DoesNotCountRequest()
    {
        var plaintext = await CreateKeyAsync(rpm: 1);
        await InvokeAsync("/v1/models", apiKeyHeader: plaintext);
        await InvokeAsync("/v1/models", apiKeyHeader: plaintext); // 429

        var saved = (await _keys.ListAsync()).Single();
        Assert.Equal(1, saved.RequestsUsed);
    }
}
```

(Lưu ý: import thêm `using RouterBalancing.Core.Logging;` nếu `LogService` cần — thêm vào nếu compiler báo.)

- [ ] **Step 7: Cập nhật test host/integration — đăng ký `IClientKeyService`**

**a) `ProxyHostTests.cs`** — 2 test seed key thay settings (giữ nguyên assert):

- `Health_WhenApiKeyEnabled_StillOpen` (:75): thay 2 dòng `_settings.SetApiKeyEnabled(true); _settings.SetApiKey("secret-key");` bằng seed key qua `_clientKeys` và **không gửi key** (health vẫn open):

```csharp
        await _clientKeys.CreateAsync(new ClientKeyDraft("health", null, null));
```

- `Models_WhenApiKeyEnabled_RequiresKey` (:87): seed key, request không key → 401, rồi gửi đúng plaintext → pass. Ghi lại plaintext khi seed:

```csharp
        var (_, plaintext) = await _clientKeys.CreateAsync(new ClientKeyDraft("models", null, null));
```

Đọc file khi thực thi để giữ nguyên phần assert/HTTP call hiện có — chỉ thay phần seed + bổ sung case gửi `Bearer {plaintext}` nếu test hiện chưa có.

**b) 4 integration tests** — mỗi file thêm field + 1 dòng đăng ký. Ví dụ `ProxyAppChatIntegrationTests` (ctor :36-40 đã có `var factory = _db.CreateFactory();`):

```csharp
    private readonly ClientKeyService _clientKeys;

    // trong ctor, sau khi factory/_log sẵn sàng:
    _clientKeys = new ClientKeyService(factory);
```

trong `StartAsync` builder, ngay sau `builder.Services.AddSingleton<IDbContextFactory<RouterBalancingDbContext>>(_db.CreateFactory());` (:91):

```csharp
        builder.Services.AddSingleton<IClientKeyService>(_clientKeys);
```

Áp dụng y hệt cho: `ProxyRetryIntegrationTests` (builder :153), `ProxyQueueIntegrationTests` (:111), `ProxyControlApiTests` (:108).

**c) `ProxyAppChatIntegrationTests` — 2 test seed key:**

- `Chat_WhenApiKeyEnabledAndMissing_Returns401OpenAiShape` (:112): thay `_settings.SetApiKeyEnabled(true); _settings.SetApiKey("secret-key");` bằng `await _clientKeys.CreateAsync(new ClientKeyDraft("chat", null, null));` — assert 401 + body giữ nguyên.
- `Health_WhenApiKeyEnabled_StillOpen` (:205): tương tự, chỉ seed key (health open không cần gửi key).

- [ ] **Step 8: Chạy test mới + affected**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ClientKeyRateLimiterTests|FullyQualifiedName~ApiKeyMiddlewareTests|FullyQualifiedName~ProxyHostTests"
```
Expected: 8 limiter + 12 middleware + ProxyHostTests xanh.

- [ ] **Step 9: Gates**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0
dotnet test "router balancing test/router balancing test.csproj"
```
Expected: 0 error; toàn suite xanh (4 integration test files phải đăng ký service — thiếu sẽ fail DI resolution ngay request đầu).

- [ ] **Step 10: Commit**

```powershell
git add -A
git commit -m "feat: authenticate and rate-limit proxy requests via client keys"
```

---

### Task 4: Usage capture (tee-stream) + journal attribution (`ClientKeyId`)

**Files:**
- Create: `src/RouterBalancing.Core/Engine/UsageCapture.cs` (internal — test qua `InternalsVisibleTo`)
- Create: `src/RouterBalancing.Core/Server/IClientKeyUsageSink.cs` + `ClientKeyUsageSink.cs`
- Modify: `src/RouterBalancing.Core/Domain/Enums/LogSeverity.cs` (+ `Debug = -1`)
- Modify: `src/RouterBalancing.Core/Logging/ILogService.cs` + `LogService.cs` (+ `LogRequestUsage`)
- Modify: `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs` (ctor + tee + attribute 3 row)
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs` (sink DI + `Items[RequestId]` + 5 journal row → `Write`)
- Modify: `router-balancing/Components/Pages/LogPanel.razor` (nhãn + option cho Debug)
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (+ `log.severity.debug` ×2 dict)
- Modify tests: `TestDoubles.cs`, `ChatCompletionsHandlerTests.cs`, `DispatcherLoopTests.cs`, `ComboResolverTests.cs`, `ModelHealthWatchdogTests.cs`, `ModelHealthStoreTests.cs`, `LogServiceTests.cs`
- Create tests: `router balancing test/Engine/UsageCaptureTests.cs`, `router balancing test/Server/ClientKeyUsageSinkTests.cs`

**Interfaces:**
- Consumes: `ClientKeyItems` + `IClientKeyRateLimiter` (Task 3), `IClientKeyService.RecordTokensAsync` (Task 2), spec §6–§7.
- Produces: `UsageCapture.WithIncludeUsage/TeeAsync`, `ILogService.LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens)`, `IClientKeyUsageSink.RecordAsync(long? clientKeyId, int promptTokens, int completionTokens, ct)`.

**Design decision thêm (bên cạnh 9 đã chốt ở đầu plan):**
10. **Spec §6/§10 "log Debug"** — codebase không có severity Debug → thêm `LogSeverity.Debug = -1` (dưới Info: filter mặc định ẩn; LogPanel thêm render branch + option filter + i18n `log.severity.debug`). Ghi Debug CHỈ khi `expectsUsage && usage is null` (đã yêu cầu include_usage mà upstream không trả = bất thường); không inject (Anthropic/non-stream) mà thiếu usage = degrade bình thường theo spec → không log (tránh spam mỗi request).
11. **Cả 3 dòng log trong `ForwardAsync` + 5 dòng journal trong endpoint convert sang `log.Write(LogEntry{...})`** để gắn `RequestId`/`ClientKeyId` (spec §7) — **Message giữ nguyên byte-for-byte**; `CapturingLog` của `ChatCompletionsHandlerTests` chuyển route list theo `entry.Severity` (các assert Infos/Warns/Errors cũ giữ nguyên), 4 `CapturingLog` còn lại chỉ thêm method no-op.

- [ ] **Step 1: `UsageCapture`**

Tạo `src/RouterBalancing.Core/Engine/UsageCapture.cs`:

```csharp
using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Đọc usage token từ upstream response (spec client-keys §6):
/// (1) <see cref="WithIncludeUsage"/> chèn <c>stream_options.include_usage=true</c> cho stream OpenAI;
/// (2) <see cref="TeeAsync"/> copy body sang client MÀ không nuốt/chỉnh sửa byte — vừa forward vừa quét usage.
/// </summary>
internal static class UsageCapture
{
    /// <summary>Usage đã đọc được từ response (OpenAI-shape prompt_tokens/completion_tokens).</summary>
    public sealed record Usage(int PromptTokens, int CompletionTokens);

    /// <summary>
    /// Trả body upstream (inject <c>stream_options.include_usage</c> nếu đủ điều kiện) + cờ
    /// <c>ExpectsUsage</c> (true = đã yêu cầu usage → thiếu là bất thường, caller log Debug).
    /// </summary>
    public static (byte[] Body, bool ExpectsUsage) WithIncludeUsage(byte[] body, ProviderType providerType)
    {
        // Chỉ upstream OpenAI-compatible hiểu stream_options; Anthropic contract khác (spec §6)
        if (providerType != ProviderType.OpenAI) return (body, false);

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(Encoding.UTF8.GetString(body));
        }
        catch (JsonException)
        {
            // Body không phải JSON hợp lệ — để validator/upstream xử lý, không đụng vào
            return (body, false);
        }

        if (node is not JsonObject obj) return (body, false);

        var isStream = obj.TryGetPropertyValue("stream", out var streamNode)
            && streamNode is JsonValue streamValue
            && streamValue.TryGetValue<bool>(out var flag)
            && flag;
        if (!isStream) return (body, false);

        var options = obj.TryGetPropertyValue("stream_options", out var optionsNode)
            && optionsNode is JsonObject existing
            ? existing
            : new JsonObject();
        options["include_usage"] = true;
        obj["stream_options"] = options;
        // ToJsonString escape unicode nhưng JSON escape vẫn semantic-equivalent — upstream parse y hệt
        return (Encoding.UTF8.GetBytes(obj.ToJsonString()), true);
    }

    /// <summary>
    /// Forward toàn bộ response sang <paramref name="dest"/> và trả usage (null nếu không có).
    /// SSE → ghi từng chunk ngay, scan dòng theo byte (dòng có thể cắt giữa 2 chunk);
    /// loại khác (JSON non-stream) → buffer, parse, rồi ghi tiếp — spec §6.1.
    /// </summary>
    public static async Task<Usage?> TeeAsync(HttpContent source, Stream dest, CancellationToken ct)
    {
        var stream = await source.ReadAsStreamAsync(ct);
        var mediaType = source.Content.Headers.ContentType?.MediaType;
        if (string.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase))
            return await TeeSseAsync(stream, dest, ct);

        var bytes = new MemoryStream();
        await stream.CopyToAsync(bytes, ct);
        var payload = bytes.ToArray();
        await dest.WriteAsync(payload, ct);
        return ParseUsage(payload);
    }

    internal static async Task<Usage?> TeeSseAsync(Stream source, Stream dest, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        byte[] carry = [];
        Usage? usage = null;
        try
        {
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                // Ghi NGUYÊN chunk cho client TRƯỚC khi scan — byte-forward không đổi (spec §6.1)
                await dest.WriteAsync(buffer.AsMemory(0, read), ct);

                var combined = new byte[carry.Length + read];
                Buffer.BlockCopy(carry, 0, combined, 0, carry.Length);
                Buffer.BlockCopy(buffer, 0, combined, carry.Length, read);

                var lineStart = 0;
                for (var i = 0; i < combined.Length; i++)
                {
                    if (combined[i] != (byte)'\n') continue;
                    usage = ScanSseLine(combined.AsSpan(lineStart, i - lineStart), usage);
                    lineStart = i + 1;
                }
                carry = combined[lineStart..];
            }
            // Dòng cuối không có \n (SSE chuẩn luôn có nhưng không assume) — xử lý nốt
            if (carry.Length > 0)
                usage = ScanSseLine(carry, usage);
            return usage;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Chỉ dòng "data: {...}" chứa usage hợp lệ; JSON hỏng → bỏ qua dòng đó, không nổ stream
    private static Usage? ScanSseLine(ReadOnlySpan<byte> line, Usage? current)
    {
        if (!line.StartsWith("data:"u8)) return current;
        var payload = line["data:"u8.Length..];
        if (payload.StartsWith("[DONE]"u8)) return current;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return current;
            if (!doc.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object
                || !usage.TryGetProperty("prompt_tokens", out var prompt)
                || !usage.TryGetProperty("completion_tokens", out var completion))
                return current;
            // last-wins: nhiều chunk usage → lấy cái cuối (spec §6.1)
            return new Usage(prompt.GetInt32(), completion.GetInt32());
        }
        catch (JsonException)
        {
            return current;
        }
    }

    private static Usage? ParseUsage(ReadOnlySpan<byte> json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("prompt_tokens", out var prompt)
                && usage.TryGetProperty("completion_tokens", out var completion))
                return new Usage(prompt.GetInt32(), completion.GetInt32());
        }
        catch (JsonException)
        {
            // Body không parse được → không có usage, request vẫn thành công (spec §10)
        }
        return null;
    }
}
```

- [ ] **Step 2: `IClientKeyUsageSink` + `ClientKeyUsageSink`**

Tạo `src/RouterBalancing.Core/Server/IClientKeyUsageSink.cs`:

```csharp
namespace RouterBalancing.Core.Server;

/// <summary>
/// Ghi usage token (sau khi response xong) vào counter daily + window TPM (spec §6).
/// Tách riêng để handler/unit test không cần DB thật.
/// </summary>
public interface IClientKeyUsageSink
{
    /// <param name="clientKeyId">null = request đi qua khi auth mở — chỉ ghi journal, không cộng counter.</param>
    Task RecordAsync(long? clientKeyId, int promptTokens, int completionTokens, CancellationToken ct = default);
}
```

Tạo `src/RouterBalancing.Core/Server/ClientKeyUsageSink.cs`:

```csharp
using RouterBalancing.Core.Logging;

namespace RouterBalancing.Core.Server;

/// <inheritdoc cref="IClientKeyUsageSink"/>
public sealed class ClientKeyUsageSink(
    IClientKeyService keys, IClientKeyRateLimiter limiter, ILogService log) : IClientKeyUsageSink
{
    public async Task RecordAsync(long? clientKeyId, int promptTokens, int completionTokens,
        CancellationToken ct = default)
    {
        if (clientKeyId is not long id) return;

        // TPM window là gate thật (in-memory) — cộng TRƯỚC, không phụ thuộc DB (spec §5/§10)
        limiter.AddTokens(id, promptTokens + completionTokens);

        try
        {
            await keys.RecordTokensAsync(id, promptTokens, completionTokens, ct);
        }
        catch (Exception ex)
        {
            // Fail-open: counter là telemetry — DB lỗi không được phá request đã 2xx (spec §10)
            log.Error("Không ghi được usage counter cho client key.", ex, LogCategory.Request);
        }
    }
}
```

Đăng ký trong `ProxyApp.ConfigureServices` — thêm sau 2 dòng limiter/cache của Task 3:

```csharp
        builder.Services.AddSingleton<IClientKeyUsageSink, ClientKeyUsageSink>();
```

- [ ] **Step 3: `LogSeverity.Debug` + `ILogService.LogRequestUsage`**

`src/RouterBalancing.Core/Domain/Enums/LogSeverity.cs` — thêm giá trị đầu:

```csharp
public enum LogSeverity
{
    /// <summary>Dưới Info — filter mặc định ẩn; dùng cho telemetry (usage degrade, spec client-keys §6).</summary>
    Debug = -1,
    Info = 0,
    Warning = 1,
    Error = 2,
}
```

`src/RouterBalancing.Core/Logging/ILogService.cs` — thêm sau `Error(...)`:

```csharp
    /// <summary>Ghi dòng Request ghi lại usage token từ upstream (spec client-keys §6.2).</summary>
    /// <param name="requestId">Request id 8 ký tự — null nếu không có (request ngoài pipeline).</param>
    /// <param name="clientKeyId">Client key đã dùng — null khi auth đang mở.</param>
    void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens);
```

`src/RouterBalancing.Core/Logging/LogService.cs` — thêm implement cạnh `Error(...)` (:77):

```csharp
    public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) =>
        Write(new LogEntry
        {
            Severity = LogSeverity.Info,
            Category = LogCategory.Request,
            Message = $"Usage từ upstream: {promptTokens} prompt token, {completionTokens} completion token.",
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            RequestId = requestId,
            ClientKeyId = clientKeyId,
        });
```

- [ ] **Step 4: Sửa `ChatCompletionsHandler`**

`src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs` — 5 sửa đổi:

1. Ctor (:16-19) — thêm tham số + using `RouterBalancing.Core.Server`:

```csharp
public sealed class ChatCompletionsHandler(
    IUpstreamClient upstream,
    ISecretProtector protector,
    ILogService log,
    IClientKeyUsageSink usageSink)
```

2. `ForwardAsync` — biến thể body + inject trước khi post (:83-87):

```csharp
        // Yêu cầu upstream trả usage cho stream OpenAI (spec §6) — body gốc giữ nguyên ở queue/prepare
        var (requestBody, expectsUsage) = UsageCapture.WithIncludeUsage(body, provider.Type);

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await upstream.PostChatCompletionAsync(provider, key, requestBody, ct);
        }
```

3. No-key Warn (:78) → `Write` kèm id (message GIỮ NGUYÊN):

```csharp
            log.Write(new LogEntry
            {
                Severity = LogSeverity.Warning,
                Category = LogCategory.Request,
                Message = $"Provider '{provider.Name}' không có account enabled nào.",
                RequestId = ClientKeyItems.RequestIdOf(ctx),
                ClientKeyId = ClientKeyItems.IdOf(ctx),
            });
```

4. Upstream error (:94) → `Write` (message + Details/ErrorCode như `ILogService.Error` đang làm):

```csharp
            log.Write(new LogEntry
            {
                Severity = LogSeverity.Error,
                Category = LogCategory.Request,
                Message = $"Không kết nối được upstream '{provider.Name}'.",
                Details = ex.ToString(),
                ErrorCode = ex.GetType().Name,
                RequestId = ClientKeyItems.RequestIdOf(ctx),
                ClientKeyId = ClientKeyItems.IdOf(ctx),
            });
```

5. Success branch (:100-109) — tee thay `CopyToAsync` + ghi usage; và `LogForwarded` chuyển thành instance method có `ctx`:

```csharp
            if (response.IsSuccessStatusCode)
            {
                // 2xx giữ nguyên 3A/3B: stream thẳng — tee quét usage trong lúc copy (spec §6.1)
                ctx.Response.StatusCode = (int)response.StatusCode;
                if (response.Content.Headers.ContentType is { } okType)
                    ctx.Response.ContentType = okType.ToString();
                var usage = await UsageCapture.TeeAsync(response.Content, ctx.Response.Body, ct);
                LogForwarded(ctx, provider, model, response, stopwatch);

                if (usage is not null)
                {
                    log.LogRequestUsage(ClientKeyItems.RequestIdOf(ctx), ClientKeyItems.IdOf(ctx),
                        usage.PromptTokens, usage.CompletionTokens);
                    // Fail-open nằm trong sink: DB lỗi → log Error, không nổ sau khi đã stream (spec §10)
                    await usageSink.RecordAsync(ClientKeyItems.IdOf(ctx),
                        usage.PromptTokens, usage.CompletionTokens, ct);
                }
                else if (expectsUsage)
                {
                    // Đã yêu cầu include_usage mà không có usage — telemetry bất thường, không fail request
                    log.Write(new LogEntry
                    {
                        Severity = LogSeverity.Debug,
                        Category = LogCategory.App,
                        Message = "Upstream không trả usage dù đã yêu cầu include_usage — counter token không tăng.",
                    });
                }
                return new DispatchOutcome.Handled();
            }
```

và thay helper `LogForwarded` (:126-131):

```csharp
    // Tách helper để 2 nhánh (2xx/passthrough) ghi Info đúng 1 lần, không trùng chữ ký log;
    // convert sang Write để gắn RequestId/ClientKeyId (spec §7) — Message giữ nguyên
    private void LogForwarded(HttpContext ctx, Provider provider, Model model,
        HttpResponseMessage response, Stopwatch stopwatch) =>
        log.Write(new LogEntry
        {
            Severity = LogSeverity.Info,
            Category = LogCategory.Request,
            Message =
                $"Chuyển tiếp '{model.ModelId}' → '{provider.Name}': " +
                $"HTTP {(int)response.StatusCode} trong {stopwatch.ElapsedMilliseconds}ms",
            RequestId = ClientKeyItems.RequestIdOf(ctx),
            ClientKeyId = ClientKeyItems.IdOf(ctx),
        });
```

Gọi tại nhánh passthrough (:119) đổi thành `LogForwarded(ctx, provider, model, response, stopwatch);`.

6. Thêm usings: `RouterBalancing.Core.Server` (ClientKeyItems, IClientKeyUsageSink).

- [ ] **Step 5: Sửa `ProxyApp` — Items RequestId + 5 journal row**

`src/RouterBalancing.Core/Server/ProxyApp.cs`:

1. Sau dòng `ctx.Response.Headers["X-Request-Id"] = id;` (:78):

```csharp
            ctx.Items[ClientKeyItems.RequestId] = id;
```

2. 5 dòng journal → `log.Write` (Message GIỮ NGUYÊN từng chữ; thêm `RequestId = id` + `ClientKeyId = ClientKeyItems.IdOf(ctx)`):

| Dòng cũ | Severity | Message giữ nguyên |
|---|---|---|
| :88 `log.Warn("Từ chối request mới: model ...")` | `LogSeverity.Warning` | `$"Từ chối request mới: model '{prepared.ModelId}' đang ManualRetry"` |
| :102 `log.Warn("Không enqueue được request {id}.")` | `LogSeverity.Warning` | `$"Không enqueue được request {id}."` |
| :113 `log.Info("Request {id} bị client ngắt khi đang chờ.")` | `LogSeverity.Info` | `$"Request {id} bị client ngắt khi đang chờ."` |
| :139 `log.Error("Outcome Retryable lọt...")` | `LogSeverity.Error` | `$"Outcome Retryable lọt tới endpoint request {id}."` (không exception → không Details/ErrorCode) |
| :148 `log.Info("Đã huỷ request {id} ...")` | `LogSeverity.Info` | `$"Đã huỷ request {id} (đang chờ), model {prepared.ModelId}."` |

Template chung:

```csharp
                log.Write(new LogEntry
                {
                    Severity = LogSeverity.Warning,
                    Category = LogCategory.Request,
                    Message = $"...",
                    RequestId = id,
                    ClientKeyId = ClientKeyItems.IdOf(ctx),
                });
```

Các dòng ngoài endpoint (dispatcher/watchdog — không có `ctx`) **giữ nguyên** `Info/Warn/Error` → `RequestId`/`ClientKeyId` = null (spec §7 chấp nhận).

- [ ] **Step 6: LogPanel + i18n cho Debug**

`router-balancing/Components/Pages/LogPanel.razor`:

1. Dropdown filter (:37, trước `<option value="Info">`):

```razor
                <option value="Debug">@L["log.severity.debug"]</option>
```

2. Render nhánh (sau `else if (entry.Severity == LogSeverity.Warning) {...}`, trước nhánh else Info):

```razor
                        else if (entry.Severity == LogSeverity.Debug)
                        {
                            <span class="opacity-60">@L["log.severity.debug"]</span>
                        }
```

`src/RouterBalancing.Core/Localization/Translations.cs` — thêm cạnh `log.severity.info` ở **cả 2 dict**:

```csharp
        ["log.severity.debug"] = "Debug",   // English (khoảng :84)
        ["log.severity.debug"] = "Gỡ lỗi",  // Vietnamese (khoảng :314)
```

- [ ] **Step 7: Cập nhật test doubles + test hiện có**

**a) `router balancing test/TestDoubles.cs`** — thêm using `RouterBalancing.Core.Server;`, thêm method vào `NullLog` + class mới:

```csharp
    public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) { }
```

```csharp
/// <summary>IClientKeyUsageSink rỗng — unit test không cần DB/thật.</summary>
internal sealed class NullUsageSink : IClientKeyUsageSink
{
    public Task RecordAsync(long? clientKeyId, int promptTokens, int completionTokens,
        CancellationToken ct = default) => Task.CompletedTask;
}
```

**b) 4 file có `CapturingLog` private** (`DispatcherLoopTests.cs`, `ComboResolverTests.cs`, `ModelHealthWatchdogTests.cs`, `ModelHealthStoreTests.cs`) — thêm method no-op (SUT các file này không convert sang Write nên chỉ cần đủ interface):

```csharp
        public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) { }
```

**c) `DispatcherLoopTests.cs:85`** — thêm tham số sink:

```csharp
        var handler = new ChatCompletionsHandler(upstream, _protector, log, new NullUsageSink());
```

**d) `ChatCompletionsHandlerTests.cs`** — sửa `CapturingLog` (:71-85) route theo Severity + thêm list Debugs/Usages, sửa `Create` (:87-88), thêm 3 test:

```csharp
    private sealed class CapturingLog : ILogService
    {
        public List<string> Infos { get; } = [];
        public List<string> Warns { get; } = [];
        public List<string> Errors { get; } = [];
        public List<string> Debugs { get; } = [];
        public List<(string? RequestId, long? ClientKeyId, int Prompt, int Completion)> Usages { get; } = [];

        public event Action<LogEntry>? LogAdded { add { } remove { } }

        // Handler ghi journal qua Write (kèm RequestId/ClientKeyId, spec §7) — route theo Severity
        // để assert Infos/Warns/Errors cũ vẫn đúng; wrapper Info/Warn/Error gọi Write như LogService thật
        public void Write(LogEntry entry)
        {
            switch (entry.Severity)
            {
                case LogSeverity.Debug: Debugs.Add(entry.Message); break;
                case LogSeverity.Warning: Warns.Add(entry.Message); break;
                case LogSeverity.Error: Errors.Add(entry.Message); break;
                default: Infos.Add(entry.Message); break;
            }
        }
        public void Info(string message, LogCategory category = LogCategory.App) =>
            Write(new LogEntry { Severity = LogSeverity.Info, Category = category, Message = message });
        public void Warn(string message, LogCategory category = LogCategory.App) =>
            Write(new LogEntry { Severity = LogSeverity.Warning, Category = category, Message = message });
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
            Write(new LogEntry { Severity = LogSeverity.Error, Category = category, Message = message });
        public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) =>
            Usages.Add((requestId, clientKeyId, promptTokens, completionTokens));
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }

    private ChatCompletionsHandler Create(IUpstreamClient upstream, CapturingLog? log = null,
        IClientKeyUsageSink? sink = null) =>
        new(upstream, _protector, log ?? new CapturingLog(), sink ?? new NullUsageSink());
```

Thêm using `RouterBalancing.Core.Server;` vào đầu file.

Test mới (cùng file):

```csharp
    private const string StreamJson =
        """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}],"stream":true}""";

    private sealed class BodyCapturingUpstream(Func<HttpResponseMessage> factory) : IUpstreamClient
    {
        public byte[]? LastBody { get; private set; }
        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            LastBody = body;
            return Task.FromResult(factory());
        }
    }

    private sealed class CapturingUsageSink : IClientKeyUsageSink
    {
        public List<(long? KeyId, int Prompt, int Completion)> Records { get; } = [];
        public Task RecordAsync(long? clientKeyId, int promptTokens, int completionTokens,
            CancellationToken ct = default)
        {
            Records.Add((clientKeyId, promptTokens, completionTokens));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ForwardAsync_WhenStreamOpenAi_GetsIncludeUsageInjected()
    {
        var upstream = new BodyCapturingUpstream(
            () => Upstream(200, "data: [DONE]\n\n", "text/event-stream"));
        var sut = Create(upstream);
        var ctx = Ctx();
        var provider = SeedProvider();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(StreamJson), default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.NotNull(upstream.LastBody);
        using var doc = System.Text.Json.JsonDocument.Parse(upstream.LastBody);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public async Task ForwardAsync_WhenSseHasUsage_WritesUsageRowAndCountsSink()
    {
        const string sse =
            "data: {\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":7}}\n\ndata: [DONE]\n\n";
        var log = new CapturingLog();
        var sink = new CapturingUsageSink();
        var sut = Create(new StubUpstream(() => Upstream(200, sse, "text/event-stream")), log, sink);
        var ctx = Ctx();
        ctx.Items[ClientKeyItems.RequestId] = "abc12345";
        ctx.Items[ClientKeyItems.Id] = 42L;
        var provider = SeedProvider();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        var (_, _, body) = await ReadAsync(ctx);
        Assert.Equal(sse, body);                                   // byte-forward nguyên vẹn
        Assert.Equal(("abc12345", 42L, 11, 7), Assert.Single(log.Usages));
        Assert.Equal((42L, 11, 7), Assert.Single(sink.Records));
        Assert.Single(log.Infos);                                  // LogForwarded vẫn đúng 1 Info
    }

    [Fact]
    public async Task ForwardAsync_WhenIncludeUsageButNoUsage_LogsDebugAndStillHandled()
    {
        var log = new CapturingLog();
        var sut = Create(new StubUpstream(() => Upstream(200, "data: [DONE]\n\n", "text/event-stream")), log);
        var ctx = Ctx();
        var provider = SeedProvider();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(StreamJson), default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Single(log.Debugs);
        Assert.Empty(log.Usages);
        Assert.Single(log.Infos);
    }

    [Fact]
    public async Task ForwardAsync_WhenNotStreamNoUsage_LogsNoDebug()
    {
        var log = new CapturingLog();
        var sut = Create(new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx();
        var provider = SeedProvider();

        var outcome = await sut.ForwardAsync(ctx, provider, ModelOf(provider), Body(ValidJson), default);

        Assert.IsType<DispatchOutcome.Handled>(outcome);
        Assert.Empty(log.Debugs);
        Assert.Empty(log.Usages);
        Assert.Single(log.Infos);
    }
```

**e) `LogServiceTests.cs`** — thêm:

```csharp
    [Fact]
    public void LogRequestUsage_PersistsRowWithTokensAndIds()
    {
        var service = Create();

        service.LogRequestUsage("req-1", 42, 10, 5);

        var entry = Assert.Single(service.Query(new LogQuery()));
        Assert.Equal(LogCategory.Request, entry.Category);
        Assert.Equal(LogSeverity.Info, entry.Severity);
        Assert.Equal(10, entry.PromptTokens);
        Assert.Equal(5, entry.CompletionTokens);
        Assert.Equal("req-1", entry.RequestId);
        Assert.Equal(42, entry.ClientKeyId);
    }
```

- [ ] **Step 8: Test `UsageCapture`**

Tạo `router balancing test/Engine/UsageCaptureTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Engine;

public class UsageCaptureTests
{
    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    [Fact]
    public void WithIncludeUsage_OpenAiStream_InjectsIncludeUsageWithoutMutatingOriginal()
    {
        const string json = """{"model":"m","stream":true,"messages":[]}""";
        var body = Bytes(json);

        var (result, expects) = UsageCapture.WithIncludeUsage(body, ProviderType.OpenAI);

        Assert.True(expects);
        Assert.Equal(json, Encoding.UTF8.GetString(body));   // body gốc không đổi
        using var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());
    }

    [Fact]
    public void WithIncludeUsage_ExistingStreamOptions_MergesAndKeepsProps()
    {
        var body = Bytes("""{"stream":true,"stream_options":{"include_usage":false,"x":1}}""");

        var (result, expects) = UsageCapture.WithIncludeUsage(body, ProviderType.OpenAI);

        Assert.True(expects);
        using var doc = JsonDocument.Parse(result);
        var options = doc.RootElement.GetProperty("stream_options");
        Assert.True(options.GetProperty("include_usage").GetBoolean());
        Assert.Equal(1, options.GetProperty("x").GetInt32());
    }

    [Fact]
    public void WithIncludeUsage_NotStream_ReturnsOriginalAndNotExpects()
    {
        var body = Bytes("""{"stream":false,"messages":[]}""");

        var (result, expects) = UsageCapture.WithIncludeUsage(body, ProviderType.OpenAI);

        Assert.False(expects);
        Assert.Same(body, result);
    }

    [Fact]
    public void WithIncludeUsage_AnhropicStream_DoesNotInject()
    {
        var body = Bytes("""{"stream":true}""");

        var (result, expects) = UsageCapture.WithIncludeUsage(body, ProviderType.Anthropic);

        Assert.False(expects);
        Assert.Same(body, result);
    }

    [Fact]
    public void WithIncludeUsage_InvalidJson_ReturnsOriginalAndNotExpects()
    {
        var body = Bytes("{broken");

        var (result, expects) = UsageCapture.WithIncludeUsage(body, ProviderType.OpenAI);

        Assert.False(expects);
        Assert.Same(body, result);
    }

    private static async Task<(byte[] Dest, UsageCapture.Usage? Usage)> TeeJsonAsync(string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var dest = new MemoryStream();
        var usage = await UsageCapture.TeeAsync(content, dest, CancellationToken.None);
        return (dest.ToArray(), usage);
    }

    [Fact]
    public async Task Tee_JsonWithUsage_ForwardsBytesAndParses()
    {
        const string json = """{"id":"x","usage":{"prompt_tokens":11,"completion_tokens":7}}""";

        var (dest, usage) = await TeeJsonAsync(json);

        Assert.Equal(json, Encoding.UTF8.GetString(dest));
        Assert.NotNull(usage);
        Assert.Equal(11, usage.PromptTokens);
        Assert.Equal(7, usage.CompletionTokens);
    }

    [Fact]
    public async Task Tee_JsonWithoutUsage_ReturnsNullForwardsBytes()
    {
        const string json = """{"id":"x"}""";

        var (dest, usage) = await TeeJsonAsync(json);

        Assert.Null(usage);
        Assert.Equal(json, Encoding.UTF8.GetString(dest));
    }

    [Fact]
    public async Task Tee_InvalidJson_ReturnsNullStillForwards()
    {
        const string json = "{broken";

        var (dest, usage) = await TeeJsonAsync(json);

        Assert.Null(usage);
        Assert.Equal(json, Encoding.UTF8.GetString(dest));
    }

    private const string SseFixture =
        "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n" +
        "data: {\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":4}}\n\n" +
        "data: {\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":2}}\n\n" +
        "data: [DONE]\n\n";

    [Fact]
    public async Task Tee_SseWithUsage_ForwardsBytesUnchanged_LastUsageWins()
    {
        using var content = new StringContent(SseFixture, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var usage = await UsageCapture.TeeAsync(content, dest, CancellationToken.None);

        Assert.Equal(SseFixture, Encoding.UTF8.GetString(dest.ToArray()));  // byte-forward nguyên vẹn
        Assert.NotNull(usage);
        Assert.Equal(9, usage.PromptTokens);                                // last-wins (spec §6.1)
        Assert.Equal(2, usage.CompletionTokens);
    }

    [Fact]
    public async Task Tee_SseWithoutUsage_ForwardsAndReturnsNull()
    {
        const string sse = "data: {\"x\":1}\n\ndata: [DONE]\n\n";
        using var content = new StringContent(sse, Encoding.UTF8, "text/event-stream");
        using var dest = new MemoryStream();

        var usage = await UsageCapture.TeeAsync(content, dest, CancellationToken.None);

        Assert.Null(usage);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
    }

    /// <summary>Stream trả data theo chunk tùy ý — dựng lại case dòng SSE cắt giữa 2 lần read.</summary>
    private sealed class ChunkedStream(params byte[][] chunks) : Stream
    {
        private int _chunk;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_chunk >= chunks.Length) return 0;
            var current = chunks[_chunk];
            var take = Math.Min(count, current.Length - _offset);
            Buffer.BlockCopy(current, _offset, buffer, offset, take);
            _offset += take;
            if (_offset >= current.Length) { _chunk++; _offset = 0; }
            return take;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task TeeSse_DataLineSplitAcross7ByteChunks_StillCapturesUsage()
    {
        var all = Encoding.UTF8.GetBytes(SseFixture);
        var chunks = new List<byte[]>();
        for (var i = 0; i < all.Length; i += 7)
            chunks.Add(all[i..Math.Min(i + 7, all.Length)]);
        using var dest = new MemoryStream();

        var usage = await UsageCapture.TeeSseAsync(
            new ChunkedStream([.. chunks]), dest, CancellationToken.None);

        Assert.Equal(SseFixture, Encoding.UTF8.GetString(dest.ToArray()));
        Assert.NotNull(usage);
        Assert.Equal(9, usage.PromptTokens);
    }

    [Fact]
    public async Task TeeSse_MalformedDataLine_SkippedLastGoodUsageKept()
    {
        var sse = "data: {bad json\n\n" +
                  "data: {\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":1}}\n\n" +
                  "data: [DONE]\n";
        using var dest = new MemoryStream();

        var usage = await UsageCapture.TeeSseAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(sse)),
            dest, CancellationToken.None);

        Assert.NotNull(usage);
        Assert.Equal(5, usage.PromptTokens);
        Assert.Equal(sse, Encoding.UTF8.GetString(dest.ToArray()));
    }
}
```

- [ ] **Step 9: Test `ClientKeyUsageSink`**

Tạo `router balancing test/Server/ClientKeyUsageSinkTests.cs`:

```csharp
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

public class ClientKeyUsageSinkTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly ClientKeyService _keys;
    private readonly ClientKeyRateLimiter _limiter = new(TimeProvider.System);

    public ClientKeyUsageSinkTests()
    {
        DbInitializer.Initialize(_db.CreateFactory());
        _keys = new ClientKeyService(_db.CreateFactory());
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task Record_WithKeyId_PersistsTokensAndAdvancesTpmWindow()
    {
        var (key, _) = await _keys.CreateAsync(new ClientKeyDraft("app", null, 10));
        var sink = new ClientKeyUsageSink(_keys, _limiter, new LogService(_db.CreateFactory()));

        await sink.RecordAsync(key.Id, 6, 4);

        Assert.Equal(10, (await _keys.ListAsync()).Single().TokensUsed);
        // TPM window đã chạm 10 → request kế bị 429 (spec §5)
        Assert.False(_limiter.TryEnter(key.Id, null, 10).Allowed);
    }

    [Fact]
    public async Task Record_WithNullKeyId_DoesNothingAndDoesNotThrow()
    {
        var sink = new ClientKeyUsageSink(_keys, _limiter, new LogService(_db.CreateFactory()));

        await sink.RecordAsync(null, 100, 100); // auth mở — không có key để cộng

        Assert.Empty(await _keys.ListAsync());
    }

    [Fact]
    public async Task Record_WhenKeysServiceThrows_LogsErrorAndSwallows()
    {
        var log = new CapturingLog();
        var sink = new ClientKeyUsageSink(new ThrowingKeys(), _limiter, log);

        await sink.RecordAsync(1, 5, 5); // fail-open: không nổ ra caller (spec §10)

        Assert.Contains(log.Errors, m => m.Contains("usage counter"));
    }

    private sealed class ThrowingKeys : IClientKeyService
    {
        public event Action? KeysChanged { add { } remove { } }
        public Task<IReadOnlyList<ClientKey>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ClientKey>>([]);
        public Task<(ClientKey Key, string Plaintext)> CreateAsync(ClientKeyDraft draft, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task UpdateAsync(long id, ClientKeyDraft draft, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task DeleteAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task RecordRequestAsync(long id, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task RecordTokensAsync(long id, int promptTokens, int completionTokens, CancellationToken ct = default)
            => throw new InvalidOperationException("db locked");
    }

    private sealed class CapturingLog : ILogService
    {
        public List<string> Errors { get; } = [];
        public event Action<LogEntry>? LogAdded { add { } remove { } }
        public void Write(LogEntry entry) { }
        public void Info(string message, LogCategory category = LogCategory.App) { }
        public void Warn(string message, LogCategory category = LogCategory.App) { }
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App)
            => Errors.Add(message);
        public void LogRequestUsage(string? requestId, long? clientKeyId, int promptTokens, int completionTokens) { }
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }
}
```

- [ ] **Step 10: Chạy test mới — kỳ vọng PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~UsageCaptureTests|FullyQualifiedName~ClientKeyUsageSinkTests|FullyQualifiedName~ChatCompletionsHandlerTests|FullyQualifiedName~LogServiceTests"
```
Expected: tất cả xanh — kiểm tra đặc biệt các assert cũ `Single(log.Infos)` / `Single(log.Warns)` / `Single(log.Errors)` trong `ChatCompletionsHandlerTests` vẫn pass (route Severity).

- [ ] **Step 11: Gates**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # LogPanel.razor đổi
dotnet test "router balancing test/router balancing test.csproj"
```
Expected: 0 error; toàn suite xanh. Chạy lại lần nữa nếu gặp flake `ProxyQueueIntegrationTests`/mutex.

- [ ] **Step 12: Commit**

```powershell
git add -A
git commit -m "feat: capture upstream usage and attribute request logs"
```

---

### Task 5: UI quản lý key trong Settings + gỡ settings apiKey + i18n parity

**Files:**
- Modify: `src/RouterBalancing.Core/Settings/{SettingsKeys,SettingsDraft,IAppSettingsService,AppSettingsService,SettingsValidator}.cs`
- Modify: `router-balancing/Components/Pages/SettingsPanel.razor`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (−4 key ×2 dict, +28 key `clientKeys.*` ×2 dict)
- Modify tests: `Settings/AppSettingsServiceTests.cs` (−2 test + gỡ `_protector` nếu unused), `Settings/SettingsValidatorTests.cs` (−1 test), + mọi chỗ `new AppSettingsService(factory, protector)`
- Create: `router balancing test/Localization/TranslationParityTests.cs`

**Interfaces:**
- Consumes: `IClientKeyService`/`ClientKeyDraft` (Task 2), Translations (đã có `log.severity.debug` từ Task 4).
- Produces: SettingsPage dùng bảng key; `IAppSettingsService` không còn khái niệm apiKey (spec §3).

- [ ] **Step 1: Gỡ apiKey khỏi Settings layer**

**a) `SettingsKeys.cs`** — xóa 2 hằng `ApiKeyEnabled` (:14) + `ApiKey` (:16-17 kèm XML doc). *(Lưu ý: `DbInitializer.MigrateLegacyApiKey` Task 1 đã dùng literal `"apiKey"`/`"apiKeyEnabled"` nên không vỡ.)*

**b) `SettingsDraft.cs`** — xóa property `ApiKeyEnabled` (:16-17) + `ApiKey` (:18-19).

**c) `IAppSettingsService.cs`** — xóa:
- property `bool ApiKeyEnabled` (:16)
- `<exception>` doc trên `Get` (:33) và `Set` (:37) (tham chiếu `SettingsKeys.ApiKey` không còn — để lại là doc sai)
- 3 method `GetApiKey`/`SetApiKey`/`SetApiKeyEnabled` (:40-47)
- Sửa XML doc dòng :6: `Phát sau mỗi lần Set/SetApiKey` → `Phát sau mỗi lần Set`.

**d) `AppSettingsService.cs`** — xóa:
- ctor param `ISecretProtector protector` + field `_protector` (chỉ dùng cho key đã gỡ — sau khi gỡ, giữ = code rác)
- property `ApiKeyEnabled` (:44)
- `GetApiKey` (:89-99), `SetApiKey` (:101-111), `SetApiKeyEnabled` (:113)
- `GuardApiKey` (:115-120) + 2 lời gọi `GuardApiKey(key)` trong `Get` (:62) và `Set` (:75)
- using `RouterBalancing.Core.Security` nếu không còn dùng.

**e) `SettingsValidator.cs`** — xóa rule (:39-40):

```csharp
        if (draft.ApiKeyEnabled && string.IsNullOrWhiteSpace(draft.ApiKey))
            errors[nameof(SettingsDraft.ApiKey)] = "settings.error.apiKey";
```

**f) Cập nhật mọi chỗ `new AppSettingsService(...)` (grep `new AppSettingsService(` để không bỏ sót)** — bỏ tham số protector:
- `router balancing test/Settings/AppSettingsServiceTests.cs` (`Create()` :20 + field `_protector` nếu không còn chỗ dùng)
- `router balancing test/Engine/DispatcherLoopTests.cs` (:83, :93)
- `router balancing test/Engine/ModelHealthWatchdogTests.cs` (:28)
- `router balancing test/Engine/ModelHealthStoreTests.cs` (:20)
- `router balancing test/Localization/LocalizationServiceTests.cs` (:18)

- [ ] **Step 2: Xóa 3 test cũ**

- `AppSettingsServiceTests.cs`: xóa `SetApiKey_StoresProtectedValue_Roundtrips` (:70-81) và `Get_WhenKeyIsApiKey_ThrowsInvalidOperation` (:83-90) — API đã gỡ.
- `SettingsValidatorTests.cs`: xóa `Validate_WhenApiKeyEnabledWithoutKey_ReturnsApiKeyError` (:74-80).

- [ ] **Step 3: i18n — gỡ 4 key chết + thêm 28 key `clientKeys.*` (CẢ 2 DICT)**

`Translations.cs` — xóa trong **cả** dict: `settings.field.apiKeyEnabled` (:48/:278), `settings.field.apiKey` (:49/:279), `settings.action.regenerate` (:57/:287), `settings.error.apiKey` (:69/:299).

Thêm block sau (chèn ngay trước dòng `["settings.purge"]` trong từng dict — **EN**:

```csharp
        ["clientKeys.group"] = "Client API keys",
        ["clientKeys.hint.empty"] = "No keys yet — the proxy accepts unauthenticated requests. Create a key to require auth.",
        ["clientKeys.hint.plaintext"] = "Copy this key now — it will not be shown again.",
        ["clientKeys.col.name"] = "Name",
        ["clientKeys.col.key"] = "Key",
        ["clientKeys.col.status"] = "Status",
        ["clientKeys.col.requests"] = "Requests today",
        ["clientKeys.col.tokens"] = "Tokens today",
        ["clientKeys.col.lastUsed"] = "Last used",
        ["clientKeys.col.limits"] = "Limits",
        ["clientKeys.col.actions"] = "Actions",
        ["clientKeys.limits.format"] = "RPM {0} · TPM {1}",
        ["clientKeys.limits.none"] = "No limits",
        ["clientKeys.action.create"] = "Create key",
        ["clientKeys.action.edit"] = "Edit",
        ["clientKeys.action.delete"] = "Delete",
        ["clientKeys.action.copy"] = "Copy",
        ["clientKeys.action.save"] = "Save",
        ["clientKeys.field.name"] = "Key name",
        ["clientKeys.field.rpm"] = "Requests per minute (blank = no limit)",
        ["clientKeys.field.tpm"] = "Tokens per minute (blank = no limit)",
        ["clientKeys.error.name"] = "Name is required.",
        ["clientKeys.error.limit"] = "Limit must be a positive number.",
        ["clientKeys.msg.copied"] = "Key copied.",
        ["clientKeys.msg.deleted"] = "Key deleted.",
        ["clientKeys.confirmDelete"] = "Delete this key? Requests using it will be rejected immediately.",
        ["clientKeys.title.new"] = "Create client key",
        ["clientKeys.title.edit"] = "Edit client key",
```

**VI** (cùng thứ tự, cùng keys):

```csharp
        ["clientKeys.group"] = "API key của client",
        ["clientKeys.hint.empty"] = "Chưa có key — proxy đang nhận request không xác thực. Tạo key để bật yêu cầu xác thực.",
        ["clientKeys.hint.plaintext"] = "Sao chép key ngay lúc này — key sẽ không hiển thị lại.",
        ["clientKeys.col.name"] = "Tên",
        ["clientKeys.col.key"] = "Key",
        ["clientKeys.col.status"] = "Trạng thái",
        ["clientKeys.col.requests"] = "Request hôm nay",
        ["clientKeys.col.tokens"] = "Token hôm nay",
        ["clientKeys.col.lastUsed"] = "Lần dùng cuối",
        ["clientKeys.col.limits"] = "Giới hạn",
        ["clientKeys.col.actions"] = "Thao tác",
        ["clientKeys.limits.format"] = "RPM {0} · TPM {1}",
        ["clientKeys.limits.none"] = "Không giới hạn",
        ["clientKeys.action.create"] = "Tạo key",
        ["clientKeys.action.edit"] = "Sửa",
        ["clientKeys.action.delete"] = "Xóa",
        ["clientKeys.action.copy"] = "Sao chép",
        ["clientKeys.action.save"] = "Lưu",
        ["clientKeys.field.name"] = "Tên key",
        ["clientKeys.field.rpm"] = "RPM (để trống = không giới hạn)",
        ["clientKeys.field.tpm"] = "TPM (để trống = không giới hạn)",
        ["clientKeys.error.name"] = "Tên là bắt buộc.",
        ["clientKeys.error.limit"] = "Giới hạn phải là số dương.",
        ["clientKeys.msg.copied"] = "Đã sao chép key.",
        ["clientKeys.msg.deleted"] = "Đã xóa key.",
        ["clientKeys.confirmDelete"] = "Xóa key này? Các request đang dùng key sẽ bị từ chối ngay.",
        ["clientKeys.title.new"] = "Tạo client key",
        ["clientKeys.title.edit"] = "Sửa client key",
```

- [ ] **Step 4: Tạo `TranslationParityTests`**

Tạo `router balancing test/Localization/TranslationParityTests.cs`:

```csharp
using RouterBalancing.Core.Localization;

namespace router_balancing_test.Localization;

public class TranslationParityTests
{
    [Fact]
    public void EnglishAndVietnamese_HaveIdenticalKeySets()
    {
        var missingInVi = Translations.English.Keys
            .Except(Translations.Vietnamese.Keys).OrderBy(k => k).ToList();
        var missingInEn = Translations.Vietnamese.Keys
            .Except(Translations.English.Keys).OrderBy(k => k).ToList();

        Assert.True(missingInVi.Count == 0 && missingInEn.Count == 0,
            $"Keys missing in VI: [{string.Join(", ", missingInVi)}]; " +
            $"Keys missing in EN: [{string.Join(", ", missingInEn)}]");
    }
}
```

*(Nếu `Translations.English` không public → đổi thành accessor mà `LocalizationService` đang dùng; grep `Translations.English` để lấy đúng tên.)*

- [ ] **Step 5: `SettingsPanel.razor` — gỡ section API key cũ, thêm bảng client key**

**a) Đầu file (:1-14):**
- Xóa dòng `@using System.Security.Cryptography` (:2 — chỉ `RegenerateApiKey` dùng, method sẽ gỡ).
- Thêm: `@using RouterBalancing.Core.Domain` + `@using RouterBalancing.Core.Server`.
- Thêm inject (sau `@inject LogRetentionWorker Retention`):

```razor
@inject IClientKeyService ClientKeys
@inject IJSRuntime JS
```

**b) Section Server (:63-98) — chỉ còn Port:**
- Xóa label input apiKey (:73-77), block lỗi `nameof(SettingsDraft.ApiKey)` (:84-87), label checkbox `ApiKeyEnabled` (:89-92), nút `RegenerateApiKey` (:96) — giữ nguyên nút Save.
- `ServerFields` (:184-188): xóa dòng `nameof(SettingsDraft.ApiKey),`.
- `LoadDraft()` (:211-225): xóa 2 dòng `ApiKeyEnabled = Settings.ApiKeyEnabled,` và `ApiKey = Settings.GetApiKey(),`.
- `SaveServerAsync()` (:283-313): xóa block:

```csharp
        Settings.SetApiKeyEnabled(_draft.ApiKeyEnabled);
        if (!string.Equals(_draft.ApiKey, Settings.GetApiKey(), StringComparison.Ordinal))
        {
            Settings.SetApiKey(_draft.ApiKey);
        }
```

- Xóa method `RegenerateApiKey` (:335-339).

**c) Section mới — chèn ngay sau `</section>` kết thúc nhóm Server (:98), trước section Engine (:100):**

```razor
<section class="mb-4 rounded border border-border bg-surface p-4">
    <h2 class="mb-3 text-base font-semibold">@L["clientKeys.group"]</h2>

    @if (_keys.Count == 0)
    {
        @* 0 key = proxy mở — giải thích rõ trước khi user tạo key đầu tiên (spec §8) *@
        <p class="mb-3 text-sm opacity-70">@L["clientKeys.hint.empty"]</p>
    }

    <div class="mb-3 flex flex-wrap items-center gap-2">
        <button class="btn btn-primary" @onclick="OpenCreateKey">@L["clientKeys.action.create"]</button>
        @if (_newKeyPlaintext is not null)
        {
            @* Plaintext hiện đúng 1 lần tại đây — đóng trang là mất vĩnh viễn (spec §2/§8) *@
            <div class="flex flex-1 items-center gap-2 rounded border border-border bg-background px-2 py-1.5 text-sm">
                <code class="flex-1 break-all">@_newKeyPlaintext</code>
                <button class="btn btn-outline-secondary" @onclick="CopyNewKeyAsync">@L["clientKeys.action.copy"]</button>
            </div>
        }
    </div>

    <div class="overflow-x-auto">
        <table class="w-full text-sm">
            <thead>
                <tr class="border-b border-border text-left opacity-70">
                    <th class="px-2 py-1.5">@L["clientKeys.col.name"]</th>
                    <th class="px-2 py-1.5">@L["clientKeys.col.key"]</th>
                    <th class="px-2 py-1.5">@L["clientKeys.col.status"]</th>
                    <th class="px-2 py-1.5">@L["clientKeys.col.requests"]</th>
                    <th class="px-2 py-1.5">@L["clientKeys.col.tokens"]</th>
                    <th class="px-2 py-1.5">@L["clientKeys.col.lastUsed"]</th>
                    <th class="px-2 py-1.5">@L["clientKeys.col.limits"]</th>
                    <th class="px-2 py-1.5">@L["clientKeys.col.actions"]</th>
                </tr>
            </thead>
            <tbody>
                @foreach (var key in _keys)
                {
                    <tr @key="key.Id" class="border-b border-border">
                        <td class="px-2 py-1.5">@key.Name</td>
                        <td class="px-2 py-1.5"><code>@key.KeyMask</code></td>
                        <td class="px-2 py-1.5">
                            @* Toggle nhanh Enabled — không cần mở modal (spec §8) *@
                            <input type="checkbox" checked="@key.Enabled"
                                   @onchange="() => ToggleKeyAsync(key)" />
                        </td>
                        <td class="px-2 py-1.5">@key.RequestsUsed.ToString("N0")</td>
                        <td class="px-2 py-1.5">@key.TokensUsed.ToString("N0")</td>
                        <td class="px-2 py-1.5">@(key.LastUsedAt?.ToLocalTime().ToString("g") ?? "—")</td>
                        <td class="px-2 py-1.5">
                            @(key.RatePerMinute is null && key.TokensPerMinute is null
                                ? L["clientKeys.limits.none"]
                                : string.Format(L["clientKeys.limits.format"],
                                    key.RatePerMinute?.ToString("N0") ?? "—",
                                    key.TokensPerMinute?.ToString("N0") ?? "—"))
                        </td>
                        <td class="px-2 py-1.5">
                            <button class="btn btn-outline-secondary mr-2"
                                    @onclick="() => OpenEditKey(key)">@L["clientKeys.action.edit"]</button>
                            <button class="btn btn-outline-danger"
                                    @onclick="() => ConfirmDeleteKey(key)">@L["clientKeys.action.delete"]</button>
                        </td>
                    </tr>
                }
            </tbody>
        </table>
    </div>
</section>
```

**d) Modal form + ConfirmDialog — chèn sau `</section>` nhóm Data (sau :175), trước `@code`:**

```razor
<Modal Visible="_keyFormOpen"
       Title="@(_keyFormId is null ? L["clientKeys.title.new"] : L["clientKeys.title.edit"])"
       OnClose="CloseKeyForm">
    <label class="mb-2 flex flex-col gap-1 text-sm">
        @L["clientKeys.field.name"]
        <input class="rounded border border-border bg-surface px-2 py-1.5" @bind="_keyFormName" />
    </label>
    @if (_keyFormError is not null)
    {
        <div class="mb-2 text-sm text-danger">@L[_keyFormError]</div>
    }
    <div class="grid gap-3 sm:grid-cols-2">
        <label class="flex flex-col gap-1 text-sm">
            @L["clientKeys.field.rpm"]
            <input class="rounded border border-border bg-surface px-2 py-1.5" type="number" @bind="_keyFormRpm" />
        </label>
        <label class="flex flex-col gap-1 text-sm">
            @L["clientKeys.field.tpm"]
            <input class="rounded border border-border bg-surface px-2 py-1.5" type="number" @bind="_keyFormTpm" />
        </label>
    </div>
    <div class="mt-3 flex justify-end gap-2">
        <button class="btn btn-outline-secondary" @onclick="CloseKeyForm">@L["confirm.cancel"]</button>
        <button class="btn btn-primary" @onclick="SaveKeyFormAsync">@L["clientKeys.action.save"]</button>
    </div>
</Modal>

<ConfirmDialog Visible="_deleteKeyOpen"
               Title="@L["clientKeys.action.delete"]"
               Message="@L["clientKeys.confirmDelete"]"
               Danger="true"
               ConfirmText="@L["clientKeys.action.delete"]"
               CancelText="@L["confirm.cancel"]"
               OnConfirm="DeleteKeyAsync"
               OnCancel="() => _deleteKeyOpen = false" />
```

**e) Code-behind trong `@code` — thêm state + đổi `OnInitialized` + method mới:**

Đổi `OnInitialized` (:205-209) thành:

```csharp
    protected override async Task OnInitializedAsync()
    {
        _draft = LoadDraft();
        L.LanguageChanged += OnLanguageChanged;
        await ReloadKeysAsync();
    }
```

Thêm state (cạnh `_purging`):

```csharp
    private List<ClientKey> _keys = [];
    private bool _keyFormOpen;
    private long? _keyFormId;
    private string _keyFormName = string.Empty;
    private int? _keyFormRpm;
    private int? _keyFormTpm;
    private string? _keyFormError;
    private bool _deleteKeyOpen;
    private ClientKey? _deleteKeyTarget;
    private string? _newKeyPlaintext;
```

Thêm methods (sau `SaveData`):

```csharp
    private async Task ReloadKeysAsync() => _keys = (await ClientKeys.ListAsync()).ToList();

    private void OpenCreateKey()
    {
        _keyFormId = null;
        _keyFormName = string.Empty;
        _keyFormRpm = null;
        _keyFormTpm = null;
        _keyFormError = null;
        _keyFormOpen = true;
    }

    private void OpenEditKey(ClientKey key)
    {
        // Sửa chỉ đổi Name/limits — plaintext không bao giờ xem lại (spec §8)
        _keyFormId = key.Id;
        _keyFormName = key.Name;
        _keyFormRpm = key.RatePerMinute;
        _keyFormTpm = key.TokensPerMinute;
        _keyFormError = null;
        _keyFormOpen = true;
    }

    private void CloseKeyForm() => _keyFormOpen = false;

    private async Task SaveKeyFormAsync()
    {
        if (string.IsNullOrWhiteSpace(_keyFormName))
        {
            _keyFormError = "clientKeys.error.name";
            return;
        }
        if (_keyFormRpm is <= 0 || _keyFormTpm is <= 0)
        {
            _keyFormError = "clientKeys.error.limit";
            return;
        }
        _keyFormError = null;
        try
        {
            var draft = new ClientKeyDraft(_keyFormName.Trim(), _keyFormRpm, _keyFormTpm);
            if (_keyFormId is long id)
            {
                await ClientKeys.UpdateAsync(id, draft);
            }
            else
            {
                var (_, plaintext) = await ClientKeys.CreateAsync(draft);
                _newKeyPlaintext = plaintext; // hiển thị đúng 1 lần
            }
            _keyFormOpen = false;
            await ReloadKeysAsync();
            Toast.Show(L["settings.msg.saved"], ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            // ArgumentException (validate service) hay DB lỗi — đều phải hiện toast, không nuốt
            Log.Error("Lưu client key thất bại.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
    }

    private async Task ToggleKeyAsync(ClientKey key)
    {
        try
        {
            await ClientKeys.SetEnabledAsync(key.Id, !key.Enabled);
            await ReloadKeysAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Đổi trạng thái client key thất bại.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
    }

    private void ConfirmDeleteKey(ClientKey key)
    {
        _deleteKeyTarget = key;
        _deleteKeyOpen = true;
    }

    private async Task DeleteKeyAsync()
    {
        if (_deleteKeyTarget is null) return;
        try
        {
            await ClientKeys.DeleteAsync(_deleteKeyTarget.Id);
            _deleteKeyOpen = false;
            _deleteKeyTarget = null;
            await ReloadKeysAsync();
            Toast.Show(L["clientKeys.msg.deleted"], ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            Log.Error("Xóa client key thất bại.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
    }

    private async Task CopyNewKeyAsync()
    {
        if (_newKeyPlaintext is null) return;
        // Không có clipboard pattern sẵn trong repo — navigator.clipboard qua IJSRuntime
        await JS.InvokeVoidAsync("navigator.clipboard.writeText", _newKeyPlaintext);
        Toast.Show(L["clientKeys.msg.copied"], ToastSeverity.Success);
    }
```

*(Khi thực thi: đọc `Modal.razor` + `ConfirmDialog.razor` + usage ở `Providers.razor` :553-560 để khớp đúng tên parameter/EventCallback — nếu `OnConfirm` là `Func<Task>` thì method trên dùng trực tiếp, nếu là `Action` thì bọc `_ = DeleteKeyAsync();` theo pattern hiện có.)*

- [ ] **Step 6: Chạy test — kỳ vọng PASS**

```powershell
dotnet test "router balancing test/router balancing test.csproj"
```
Expected: xanh — trong đó `TranslationParityTests` 1 test mới pass, 3 test cũ đã xóa không còn, không còn reference `SettingsKeys.ApiKey`.

- [ ] **Step 7: Gates**

```powershell
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # SettingsPanel + Translations đổi
dotnet test "router balancing test/router balancing test.csproj"
```
Expected: 0 error, **0 warning mới**; toàn suite xanh (baseline ước tính ~419 test: 360 + T1 12 + T2 15 + T3 ~13 + T4 ~21 − T5 3 + parity 1).

- [ ] **Step 8: Commit**

```powershell
git add -A
git commit -m "feat: add client key management UI in settings"
```

---

### Task 6: Final verification (gates toàn nhánh)

**Files:** không sửa code — chỉ chạy lệnh + kiểm tra.

- [ ] **Step 1: Đóng app `router-balancing`** (nếu đang mở — mutex sẽ làm test fail).

- [ ] **Step 2: Full gates**

```powershell
git status --short                                   # kỳ vọng: sạch
dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj      # 0 error
dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0   # 0 error
dotnet test "router balancing test/router balancing test.csproj"        # Passed! - Failed: 0
```

- [ ] **Step 3: Kiểm tra spec §11 (testing) — checklist**

- [ ] Hash/auth deterministic + match đúng/sai + disabled không match + 0 enabled → open (`ClientKeyHasherTests`, `ApiKeyMiddlewareTests`)
- [ ] RPM dưới/giới hạn/vượt → 429 + Retry-After; rollover (`ClientKeyRateLimiterTests`)
- [ ] TPM post-hoc → 429 (`ClientKeyUsageSinkTests`, `ApiKeyMiddlewareTests.Invoke_WhenTpmReached`)
- [ ] Daily reset lazy (`ClientKeyServiceTests`)
- [ ] Usage: JSON parse, SSE tee byte-forward + last-wins, inject OpenAI/không inject Anthropic, parse fail degrade (`UsageCaptureTests`, handler tests)
- [ ] Migration legacy idempotent (`DbInitializerLegacyKeyTests`)
- [ ] i18n parity EN==VI (`TranslationParityTests`)
- [ ] `rg "settings.field.apiKey|SettingsKeys.ApiKey\b|GetApiKey|SetApiKey"` → không còn match (trừ lịch sử docs)

- [ ] **Step 4: Dọn dẹp + đóng plan**

```powershell
git status --short                                   # phải sạch
```

**Done criteria:**4 gates trên xanh, checklist spec §11 đầy đủ, working tree sạch. Báo cáo lại người dùng để chọn bước tiếp (final whole-branch review / finishing-a-development-branch).

---

## Self-review (plan)

- [x] Mọi task có code cụ thể (không placeholder "giống Task N"), lệnh chạy + kết quả mong đợi.
- [x] Tên type/method nhất quán giữa các task: `ClientKeyDraft`, `IClientKeyService`, `ClientKeyAuthCache.GetEnabled`, `ClientKeyItems.IdOf/RequestIdOf`, `IClientKeyRateLimiter.TryEnter`, `IClientKeyUsageSink.RecordAsync`, `UsageCapture.WithIncludeUsage/TeeAsync`, `ILogService.LogRequestUsage`.
- [x] `DbInitializer.Initialize(factory, ISecretProtector? = null)` gọi tại MauiProgram có protector; test cũ `Initialize(factory)` vẫn compile (param optional).
- [x] Mọi ILogService implementation trong repo được cập nhật (LogService + NullLog + 5 CapturingLog) — grep `: ILogService` đã liệt kê đủ.
- [x] i18n: 28 key `clientKeys.*` + `log.severity.debug` có trong cả 2 dict; 4 key cũ gỡ cả 2 dict.
- [x] Spec §1–§11 được phủ bởi ít nhất 1 step/test tương ứng.







