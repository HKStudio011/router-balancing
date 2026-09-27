# Provider Accounts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Đưa API key từ `Provider` xuống entity mới `ProviderAccount` (đa tài khoản: chia tải/failover/gắn model), kèm service CRUD + test-all và section UI trong modal Provider.

**Architecture:** Entity `ProviderAccount` 1-n `Provider` (key DPAPI, `ModelPatterns`, `Weight`, `Priority`, quota counters, test result); data migration đổi chỗ key cũ → account `Default`; service `IProviderAccountService` (CRUD + `TestAllAsync`); `ProviderKeyResolver` fallback cho metadata/test; UI section trong modal edit Providers.razor. Logic chọn key/quota **thuộc Phase 3 — không code ở đây**.

**Tech Stack:** .NET 10 / EF Core 10 (SQLite, migration qua `RouterBalancing.Design`), xUnit, MAUI Blazor Hybrid, i18n `Translations.cs` (parity gate).

## Global Constraints

- Spec: `docs/superpowers/specs/2026-09-27-provider-accounts-design.md` (Approved, commit `f76b785`).
- Test: `dotnet test "router balancing test/router balancing test.csproj" --nologo` — **đóng app trước khi chạy** (mutex SingleInstanceGuard).
- Build: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo` → 0 warning / 0 error.
- npm: `npm run build` workdir `router-balancing/vite-project` → exit 0 (Task 3).
- Comment tiếng Việt (why, không lặp what); XML doc `///` bắt buộc mọi public type/member; identifier + commit message tiếng Anh conventional; không `/* */`, không TODO vô chủ, không nuốt exception (catch = `Log.Error`/`Warn` + toast).
- Text hiển thị UI qua `L["key"]`; dữ liệu (tên, message lỗi HTTP) hiển thị thẳng.
- Btn variants chỉ `btn`, `btn-primary`, `btn-outline-info`, `btn-outline-secondary`, `btn-outline-danger`.
- Test doubles: `NullLog` (TestDoubles.cs); double mới đặt private nested trong file test (precedent `NeverHttpFactory` trong ProviderServiceTests).
- **Không push** khi chưa user yêu cầu.
- Parity cuối: EN = VI = **209** (183 − 1 key chết `providers.key.saved` + 27 key mới).
- Test count cuối: **178** (147 → 152 sau Task 1 → 178 sau Task 2).

---

### Task 1: Data model + ripple — key chuyển xuống `ProviderAccount`

**Files:**
- Create: `src/RouterBalancing.Core/Domain/Entities/ProviderAccount.cs`
- Create: `src/RouterBalancing.Core/Providers/ProviderKeyResolver.cs`
- Modify: `src/RouterBalancing.Core/Domain/Entities/Provider.cs`
- Modify: `src/RouterBalancing.Core/Storage/RouterBalancingDbContext.cs`
- Modify: `src/RouterBalancing.Core/Providers/ProviderService.cs`
- Modify: `src/RouterBalancing.Core/Providers/IProviderService.cs`
- Modify: `src/RouterBalancing.Core/Providers/ProviderDraft.cs`
- Modify: `src/RouterBalancing.Core/Providers/ModelService.cs`
- Modify: `src/RouterBalancing.Core/Providers/ProviderEndpointMetadataProvider.cs`
- Modify: `src/RouterBalancing.Core/Providers/ModelMetadataService.cs`
- Create: `src/RouterBalancing.Core/Storage/Migrations/<ts>_AddProviderAccounts.cs` (EF sinh + hand-edit)
- Modify: `router balancing test/Providers/ProviderServiceTests.cs`
- Modify: `router balancing test/Providers/ProviderTestConnectionTests.cs`
- Modify: `router balancing test/Providers/ModelServiceTests.cs`
- Modify: `router balancing test/Providers/ProviderRequestFactoryTests.cs`
- Create: `router balancing test/Providers/ProviderKeyResolverTests.cs`

**Interfaces:**
- Consumes: `ISecretProtector` (`Protect`/`Unprotect`), `ProviderRequestFactory.Create(provider, apiKey, path?)`, pattern `DbInitializer.Initialize(TestDb.CreateFactory())`.
- Produces (Task 2+ dùng):
  - `ProviderAccount` entity với nav `Provider`/`Provider.Accounts`.
  - `DbSet<ProviderAccount> ProviderAccounts`.
  - `ProviderKeyResolver.ResolveFirstEnabledKey(Provider, ISecretProtector) → string?`.
  - `IProviderService.CreateAsync` — `draft.ApiKey` không rỗng ⇒ tạo account `"Default"`; `UpdateAsync` bỏ qua `draft.ApiKey`; `ListAsync`/`GetAsync` Include `Accounts`.
  - Test count sau task: **152/0**.

- [ ] **Step 1: Tạo entity `ProviderAccount`**

```csharp
// src/RouterBalancing.Core/Domain/Entities/ProviderAccount.cs
namespace RouterBalancing.Core.Domain;

/// <summary>Tài khoản (API key) thuộc một nhà cung cấp — đa tài khoản, key sống hoàn toàn ở đây.</summary>
public class ProviderAccount
{
    public long Id { get; set; }

    public long ProviderId { get; set; }

    public Provider Provider { get; set; } = null!;

    /// <summary>Tên hiển thị — unique trong cùng provider.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>API key mã hoá DPAPI — không bao giờ lưu/log plaintext.</summary>
    public string ApiKeyEncrypted { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>JSON array of string pattern; <see langword="null"/>/rỗng = match mọi model (Phase 3).</summary>
    public string? ModelPatterns { get; set; }

    /// <summary>Trọng số chia tải khi nhiều key cùng match (Phase 3). 0–10000, mặc định 100.</summary>
    public int Weight { get; set; } = 100;

    /// <summary>Thứ tự failover — nhỏ hơn = dùng trước; −1000–1000, mặc định 0 (Phase 3).</summary>
    public int Priority { get; set; }

    public int? DailyTokenLimit { get; set; }

    public long TokensUsed { get; set; }

    public int? DailyRequestLimit { get; set; }

    public long RequestsUsed { get; set; }

    /// <summary>Ngày UTC của 2 counter trên — reset khi sang ngày (Phase 3 ghi).</summary>
    public DateOnly? UsageDate { get; set; }

    /// <summary>Kết quả test gần nhất; null = chưa test.</summary>
    public bool? LastTestSuccess { get; set; }

    public DateTimeOffset? LastTestAt { get; set; }

    public string? LastTestMessage { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
```

- [ ] **Step 2: Sửa `Provider.cs` — bỏ `ApiKeyEncrypted`, thêm nav `Accounts`**

Cụm dòng 16–17:

```csharp
    /// <summary>API key đã mã hoá DPAPI — không bao giờ lưu plaintext.</summary>
    public string ApiKeyEncrypted { get; set; } = string.Empty;
```

→ **xoá**. Sau dòng `public List<Model> Models { get; set; } = [];` thêm:

```csharp
    /// <summary>Tài khoản (API key) thuộc provider — cascade khi xoá provider.</summary>
    public List<ProviderAccount> Accounts { get; set; } = [];
```

- [ ] **Step 3: Sửa `RouterBalancingDbContext.cs`**

Xoá dòng cấu hình `e.Property(x => x.ApiKeyEncrypted).IsRequired();` trong `Entity<Provider>`.

Thêm DbSet sau `DbSet<ComboItem>`:

```csharp
    public DbSet<ProviderAccount> ProviderAccounts => Set<ProviderAccount>();
```

Thêm block config trong `OnModelCreating` (sau block `Entity<Model>`):

```csharp
        modelBuilder.Entity<ProviderAccount>(e =>
        {
            e.Property(x => x.Name).IsRequired().HasMaxLength(100);
            e.Property(x => x.ApiKeyEncrypted).IsRequired();
            // Đổi tên account trong form có thể trùng account khác cùng provider
            e.HasIndex(x => new { x.ProviderId, x.Name }).IsUnique();
            e.HasOne(x => x.Provider)
                .WithMany(p => p.Accounts)
                .HasForeignKey(x => x.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });
```

- [ ] **Step 4: Tạo `ProviderKeyResolver`**

```csharp
// src/RouterBalancing.Core/Providers/ProviderKeyResolver.cs
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;

namespace RouterBalancing.Core.Providers;

/// <summary>Chọn key plaintext từ ProviderAccount — fallback cho metadata/test trước khi Phase 3 có selection thật.</summary>
public static class ProviderKeyResolver
{
    /// <summary>
    /// Key của account enabled đầu tiên (Priority tăng dần, tie-break Id tăng dần);
    /// <see langword="null"/> nếu provider không có account khả dụng hoặc nav Accounts chưa load.
    /// </summary>
    public static string? ResolveFirstEnabledKey(Provider provider, ISecretProtector protector)
    {
        var account = provider.Accounts?
            .Where(a => a.Enabled && !string.IsNullOrEmpty(a.ApiKeyEncrypted))
            .OrderBy(a => a.Priority)
            .ThenBy(a => a.Id)
            .FirstOrDefault();
        return account is null ? null : protector.Unprotect(account.ApiKeyEncrypted);
    }
}
```

- [ ] **Step 5: Sửa `ProviderService.cs`**

5a. `ListAsync` + `GetAsync` — thêm `.Include(p => p.Accounts)` (UI card test + badge fallback cần Accounts):

```csharp
        return await db.Providers
            .Include(p => p.Models)
            .Include(p => p.Accounts)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
```

```csharp
        return await db.Providers
            .Include(p => p.Models)
            .Include(p => p.Accounts)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
```

5b. `CreateAsync` — body `new Provider {...}` (dòng 52–61) thay bằng:

```csharp
        var provider = new Provider
        {
            Name = draft.Name.Trim(),
            Type = draft.Type,
            BaseUrl = ProviderUrl.Canonicalize(draft.BaseUrl),
            MaxConcurrent = draft.MaxConcurrent,
        };

        // Key ở create = tạo kèm account "Default" — key sống hoàn toàn ở ProviderAccount (spec §4.2)
        if (!string.IsNullOrEmpty(draft.ApiKey))
        {
            provider.Accounts.Add(new ProviderAccount
            {
                Name = "Default",
                ApiKeyEncrypted = _protector.Protect(draft.ApiKey),
                Enabled = true,
                Weight = 100,
                Priority = 0,
            });
        }
```

5c. `UpdateAsync` — thay dòng `// Key rỗng khi sửa... if (!string.IsNullOrEmpty(draft.ApiKey)) {...}` (dòng 80–84) bằng comment:

```csharp
        // draft.ApiKey bị BỎ QUA khi update — key quản lý ở ProviderAccount (spec §4.2)
```

5d. `TestConnectionAsync` — thay dòng 123–131:

```csharp
            // Override (key đang gõ trên form) ưu tiên; không có → account enabled đầu tiên.
            // Unprotect PHẢI nằm trong try: key DPAPI hỏng (CryptographicException) rơi vào
            // catch → fail với lý do, không ném ra UI.
            var key = apiKeyOverride;
            if (string.IsNullOrEmpty(key))
            {
                key = ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector);
            }

            using var request = ProviderRequestFactory.Create(provider, key ?? string.Empty);
```

(Giữ nguyên phần `using var response = ...` phía sau; sửa comment cũ "// Override (key đang gõ trên form) ưu tiên; không có → giải mã key đã lưu." thành 2 dòng trên.)

- [ ] **Step 6: Sửa docs `IProviderService.cs` + `ProviderDraft.cs`**

`IProviderService.cs`:

- `CreateAsync` doc (dòng 14) →

```csharp
    /// <summary>Tạo provider mới từ bản nháp. <c>draft.ApiKey</c> không rỗng → tạo kèm account "Default".</summary>
```

- `UpdateAsync` doc (dòng 17–22) →

```csharp
    /// <summary>
    /// Cập nhật provider. <c>draft.ApiKey</c> bị bỏ qua —
    /// key quản lý ở <c>IProviderAccountService</c> (spec provider-accounts §4.2).
    /// </summary>
    /// <exception cref="KeyNotFoundException">Khi không có provider <paramref name="id"/>.</exception>
```

- `TestConnectionAsync` doc (dòng 33–35) →

```csharp
    /// <summary>
    /// Test kết nối: GET {BaseUrl}/v1/models. <paramref name="apiKeyOverride"/> rỗng →
    /// dùng key của account enabled đầu tiên. Provider đã lưu (Id != 0) → persist LastTest*.
    /// </summary>
```

`ProviderDraft.cs` dòng 17–18 — thay doc:

```csharp
    /// <summary>Plaintext từ form — chỉ tồn tại trong lúc nhập, không bao giờ log hay persist trực tiếp.
    /// Chỉ có nghĩa khi <c>CreateAsync</c> (tạo account "Default"); <c>UpdateAsync</c> bỏ qua.</summary>
    public string ApiKey { get; set; } = string.Empty;
```

- [ ] **Step 7: Sửa 3 consumer metadata**

7a. `ModelService.FetchFromProviderAsync` — query (dòng 38–41) + key (43–45):

```csharp
        var provider = await db.Providers
            .Include(p => p.Models)
            .Include(p => p.Accounts)
            .FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new KeyNotFoundException($"Provider {providerId} not found.");

        var key = ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector) ?? string.Empty;
```

7b. `ProviderEndpointMetadataProvider.FetchAsync` — thay dòng 29–31:

```csharp
            var key = ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector) ?? string.Empty;
```

(Giữ `using RouterBalancing.Core.Security;` — còn dùng cho `ISecretProtector` field.)

7c. `ModelMetadataService.TryFillAsync` — thay dòng 30–32:

```csharp
        var model = await db.Models
            .Include(m => m.Provider)
            .ThenInclude(p => p.Accounts)
            .FirstOrDefaultAsync(m => m.Id == modelId, ct);
```

- [ ] **Step 8: Build — kỳ vọng lỗi compile CHỈ ở test project**

Run: `dotnet build src/RouterBalancing.Core/RouterBalancing.Core.csproj --nologo`
Expected: **0 lỗi** (Core đã biên dịch).

Run: `dotnet build "router balancing test/router_BALANCING_TEST_PLACEHOLDER.csproj" --nologo` — dùng đúng lệnh:

```
dotnet build "router balancing test/router balancing test.csproj" --nologo
```

Expected: **lỗi compile** ở ProviderServiceTests/ProviderTestConnectionTests/ModelServiceTests/ProviderRequestFactoryTests (tham chiếu `ApiKeyEncrypted` đã xoá) — chuyển Step 9 fix.

- [ ] **Step 9: Cập nhật test cũ**

9a. `ProviderServiceTests.cs` — thay test `Create_WhenKeyProvided_EncryptsAndPersists` (dòng 43–56) bằng:

```csharp
    [Fact]
    public async Task Create_WhenKeyProvided_CreatesDefaultAccountEncrypted()
    {
        var provider = await _service.CreateAsync(Draft());

        Assert.Equal("https://api.openai.com", provider.BaseUrl); // trailing slash đã trim
        Assert.Equal("OpenAI", provider.Name);

        using var db = _db.CreateDbContext();
        var account = await db.ProviderAccounts.SingleAsync(a => a.ProviderId == provider.Id);
        Assert.Equal("Default", account.Name);
        Assert.NotEqual("sk-secret", account.ApiKeyEncrypted);
        Assert.Equal("sk-secret", _protector.Unprotect(account.ApiKeyEncrypted));
    }

    [Fact]
    public async Task Create_WhenKeyBlank_NoAccountCreated()
    {
        var provider = await _service.CreateAsync(Draft(key: string.Empty));

        using var db = _db.CreateDbContext();
        Assert.False(await db.ProviderAccounts.AnyAsync(a => a.ProviderId == provider.Id));
    }
```

9b. Thay test `Update_WhenApiKeyBlank_KeepsExistingKeyAndRefreshesTimestamp` (dòng 112–126) bằng:

```csharp
    [Fact]
    public async Task Update_WhenApiKeyBlank_KeepsAccountKeyAndRefreshesTimestamp()
    {
        var provider = await _service.CreateAsync(Draft(key: "sk-old"));
        var before = provider.UpdatedAt;

        await Task.Delay(10); // UpdatedAt có độ phân_resolution tick — đảm bảo khác biệt thực sự
        await _service.UpdateAsync(provider.Id, Draft(name: "Renamed", key: ""));

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("Renamed", saved.Name);
        Assert.True(saved.UpdatedAt > before);
        var account = await db.ProviderAccounts.SingleAsync(a => a.ProviderId == provider.Id);
        Assert.Equal("sk-old", _protector.Unprotect(account.ApiKeyEncrypted));
    }
```

9c. Thay test `Update_WhenApiKeyProvided_Reencrypts` (dòng 128–138) bằng:

```csharp
    [Fact]
    public async Task Update_WhenApiKeyProvided_IgnoresKeyAndKeepsAccount()
    {
        var provider = await _service.CreateAsync(Draft(key: "sk-old"));

        await _service.UpdateAsync(provider.Id, Draft(key: "sk-new"));

        using var db = _db.CreateDbContext();
        var account = await db.ProviderAccounts.SingleAsync(a => a.ProviderId == provider.Id);
        // Key chỉ đổi qua account CRUD — UpdateAsync bỏ qua draft.ApiKey (spec §4.2)
        Assert.Equal("sk-old", _protector.Unprotect(account.ApiKeyEncrypted));
    }
```

9d. Thay test `ListAsync_WhenProvidersExist_IncludesModels` (dòng 96–110) — giữ body cũ, thêm cuối test (trước `}` cuối):

```csharp
    [Fact]
    public async Task ListAsync_IncludesAccounts()
    {
        var provider = await _service.CreateAsync(Draft());

        var list = await _service.ListAsync();

        var loaded = Assert.Single(list);
        Assert.Equal("Default", Assert.Single(loaded.Accounts).Name);
    }
```

9e. `ProviderTestConnectionTests.cs` — thay helper `SavedProviderAsync` (dòng 54–68):

```csharp
    private async Task<Provider> SavedProviderAsync(ProviderType type = ProviderType.OpenAI)
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider
        {
            Name = "P",
            Type = type,
            BaseUrl = "https://api.example.com",
            MaxConcurrent = 4,
            Accounts =
            [
                new ProviderAccount
                {
                    Name = "Default",
                    ApiKeyEncrypted = _protector.Protect("sk-saved"),
                    Enabled = true,
                },
            ],
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider;
    }
```

9f. Đổi tên test `TestConnection_WhenNoOverride_DecryptsSavedKeyForRequest` (dòng 146) → `TestConnection_WhenNoOverride_DecryptsFirstEnabledAccountKey` (body giữ nguyên — entity vừa tạo có Accounts load sẵn).

9g. Thay test `TestConnection_WhenNotSaved_DoesNotPersist` — giữ nguyên (entity `new Provider()` không có Accounts → resolver trả null → key rỗng, override "sk-new" vẫn hoạt động).

Thêm test mới vào cuối class:

```csharp
    [Fact]
    public async Task TestConnection_WhenNoEnabledAccounts_SendsEmptyKey()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var provider = await SavedProviderAsync();
        provider.Accounts[0].Enabled = false; // entity trong tay — resolver phải bỏ account tắt
        var service = ServiceWith(handler);

        await service.TestConnectionAsync(provider, apiKeyOverride: null);

        var auth = handler.LastRequest!.Headers.Authorization;
        Assert.True(auth is null || !auth.ToString().Contains("sk-saved", StringComparison.Ordinal));
    }
```

9h. `ModelServiceTests.cs` — helper `SeedProviderAsync` (dòng 54–60): thay dòng `ApiKeyEncrypted = _protector.Protect("sk-saved"),` bằng block:

```csharp
            Accounts =
            [
                new ProviderAccount
                {
                    Name = "Default",
                    ApiKeyEncrypted = _protector.Protect("sk-saved"),
                    Enabled = true,
                },
            ],
```

9i. `ProviderRequestFactoryTests.cs` — dòng 13 `ApiKeyEncrypted = string.Empty,` → **xoá** (property không còn).

- [ ] **Step 10: Tạo test `ProviderKeyResolverTests.cs`**

```csharp
// router balancing test/Providers/ProviderKeyResolverTests.cs
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;

namespace router_balancing_test.Providers;

public class ProviderKeyResolverTests
{
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    [Fact]
    public void ResolveFirstEnabledKey_OrdersByPriority_SkipsDisabled()
    {
        var provider = new Provider
        {
            Accounts =
            [
                new ProviderAccount { Id = 2, Name = "low", Enabled = true, Priority = 5, ApiKeyEncrypted = _protector.Protect("sk-low") },
                new ProviderAccount { Id = 1, Name = "high", Enabled = true, Priority = 1, ApiKeyEncrypted = _protector.Protect("sk-high") },
                new ProviderAccount { Id = 3, Name = "off", Enabled = false, Priority = 0, ApiKeyEncrypted = _protector.Protect("sk-off") },
            ],
        };

        // off có Priority 0 nhưng tắt → bỏ; high (1) đứng trước low (5)
        Assert.Equal("sk-high", ProviderKeyResolver.ResolveFirstEnabledKey(provider, _protector));
    }

    [Fact]
    public void ResolveFirstEnabledKey_WhenNoAccounts_ReturnsNull()
    {
        Assert.Null(ProviderKeyResolver.ResolveFirstEnabledKey(new Provider(), _protector));
    }
}
```

- [ ] **Step 11: Run test — kỳ vọng 152/0**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed! - Failed: 0, Passed: 152` (147 cũ − 0 đổi số lượng + 5 mới: Create_WhenKeyBlank, ListAsync_IncludesAccounts, TestConnection_WhenNoEnabledAccounts, 2 × resolver).

Nếu fail ở test khác ngoài danh sách đổi ở Step 9 → đọc kỹ assertion, sửa test theo hành vi mới (không sửa service để chạy xanh vội).

- [ ] **Step 12: Tạo migration `AddProviderAccounts`**

Run (từ repo root):

```
dotnet ef migrations add AddProviderAccounts --project src/RouterBalancing.Core --startup-project src/RouterBalancing.Design --context RouterBalancingDbContext --output-dir Storage/Migrations
```

Expected: sinh `src/RouterBalancing.Core/Storage/Migrations/<ts>_AddProviderAccounts.cs` + `.Designer.cs`, snapshot cập nhật; **0 lỗi**.

- [ ] **Step 13: Hand-edit migration — data migration key cũ → account `Default`**

Mở file `<ts>_AddProviderAccounts.cs` vừa sinh:

13a. Trong `Up()`, chèn **ngay TRƯỚC** dòng `migrationBuilder.DropColumn(name: "ApiKeyEncrypted", table: "Provider");`:

```csharp
            // Data migration: key cũ của provider → account "Default" (spec provider-accounts §3)
            migrationBuilder.Sql("""
                INSERT INTO ProviderAccounts
                  (ProviderId, Name, ApiKeyEncrypted, Enabled, Weight, Priority, ModelPatterns,
                   DailyTokenLimit, TokensUsed, DailyRequestLimit, RequestsUsed, UsageDate,
                   LastTestSuccess, LastTestAt, LastTestMessage, CreatedAt, UpdatedAt)
                SELECT Id, 'Default', ApiKeyEncrypted, 1, 100, 0, NULL,
                       NULL, 0, NULL, 0, NULL,
                       NULL, NULL, NULL, strftime('%Y-%m-%dT%H:%M:%fZ','now'), strftime('%Y-%m-%dT%H:%M:%fZ','now')
                FROM Provider WHERE ApiKeyEncrypted <> '';
                """);
```

13b. Trong `Down()`, chèn SQL **SAU** `migrationBuilder.AddColumn(... "ApiKeyEncrypted" ...)` và **TRƯỚC** `migrationBuilder.DropTable(name: "ProviderAccounts", ...)` (UPDATE phải chạy khi cột đã tạo và bảng account còn tồn tại — kiểm tra thứ tự thực tế của file EF sinh ra, chèn đúng vị trí đó):

```csharp
            // Data migration ngược: account "Default" → key cũ của provider
            migrationBuilder.Sql("""
                UPDATE Provider SET ApiKeyEncrypted = (
                  SELECT a.ApiKeyEncrypted FROM ProviderAccounts a
                  WHERE a.ProviderId = Provider.Id AND a.Name = 'Default'
                  ORDER BY a.Id LIMIT 1)
                WHERE EXISTS (
                  SELECT 1 FROM ProviderAccounts a
                  WHERE a.ProviderId = Provider.Id AND a.Name = 'Default');
                """);
```

- [ ] **Step 14: Rebuild + test lại (DbInitializer.Migrate chạy migration mới trên DB tạm)**

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: 0 warning / 0 error.

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: **152/0** (chứng minh migration `Up()` apply được — DB test là file trống nên INSERT no-op, chỉ validate schema).

- [ ] **Step 15: Commit**

```bash
git add -A
git commit -m "feat: move provider api keys to provider accounts"
```

---

### Task 2: `IProviderAccountService` — CRUD + validate + test-all

**Files:**
- Create: `src/RouterBalancing.Core/Providers/ProviderAccountDraft.cs`
- Create: `src/RouterBalancing.Core/Providers/ProviderAccountValidator.cs`
- Create: `src/RouterBalancing.Core/Providers/IProviderAccountService.cs`
- Create: `src/RouterBalancing.Core/Providers/ProviderAccountTestResult.cs`
- Create: `src/RouterBalancing.Core/Providers/ProviderAccountService.cs`
- Modify: `router-balancing/MauiProgram.cs`
- Create: `router balancing test/Providers/ProviderAccountServiceTests.cs`

**Interfaces:**
- Consumes (Task 1): `ProviderAccount`, `DbSet<ProviderAccounts>`, `ProviderKeyResolver` (không dùng trực tiếp), `ProviderRequestFactory.Create`, `ISecretProtector`, `NullLog`.
- Produces (Task 3 dùng — đúng chữ ký này):

```csharp
public interface IProviderAccountService
{
    Task<IReadOnlyList<ProviderAccount>> ListAsync(long providerId, CancellationToken ct = default);
    Task<ProviderAccount> CreateAsync(ProviderAccountDraft draft, CancellationToken ct = default);
    Task<ProviderAccount> UpdateAsync(long id, ProviderAccountDraft draft, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<ProviderAccountTestResult>> TestAllAsync(long providerId, CancellationToken ct = default);
}
// ProviderAccountTestResult(long AccountId, string AccountName, bool Success, string? Message)
```

- DI: `builder.Services.AddSingleton<IProviderAccountService, ProviderAccountService>();`
- Test count sau task: **178/0**.

- [ ] **Step 1: `ProviderAccountDraft.cs`**

```csharp
namespace RouterBalancing.Core.Providers;

/// <summary>
/// Bản nháp form account. Property mutable (không phải positional record)
/// để Blazor <c>@bind</c> ghi được — giống <c>ProviderDraft</c>.
/// </summary>
public sealed class ProviderAccountDraft
{
    public long ProviderId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Plaintext lúc nhập; rỗng khi update = giữ key đã lưu. Không bao giờ log/persist trực tiếp.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Pattern do UI gửi (1 phần tử = 1 dòng textarea); service serialize JSON.</summary>
    public string[] ModelPatterns { get; set; } = [];

    public int Weight { get; set; } = 100;

    public int Priority { get; set; }

    public int? DailyTokenLimit { get; set; }

    public int? DailyRequestLimit { get; set; }
}
```

- [ ] **Step 2: `ProviderAccountValidator.cs`**

```csharp
namespace RouterBalancing.Core.Providers;

/// <summary>
/// Validate draft account ở service boundary. Throw <see cref="ArgumentException"/> với message
/// kỹ thuật (không phải i18n) — UI chặn trước bằng CanSave + input min/max, toast dùng key chung.
/// </summary>
public static class ProviderAccountValidator
{
    /// <param name="requireApiKey">Create bắt buộc key; update rỗng = giữ key cũ.</param>
    /// <exception cref="ArgumentException">Khi draft vi phạm rule nào đó.</exception>
    public static void ValidateAndThrow(ProviderAccountDraft draft, bool requireApiKey)
    {
        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            throw new ArgumentException("Name is required.");
        }
        if (draft.Name.Trim().Length > 100)
        {
            throw new ArgumentException("Name must be 100 characters or fewer.");
        }
        if (requireApiKey && string.IsNullOrWhiteSpace(draft.ApiKey))
        {
            throw new ArgumentException("API key is required.");
        }
        if (draft.Weight is < 0 or > 10_000)
        {
            throw new ArgumentException("Weight must be between 0 and 10000.");
        }
        if (draft.Priority is < -1000 or > 1000)
        {
            throw new ArgumentException("Priority must be between -1000 and 1000.");
        }
        if (draft.DailyTokenLimit is <= 0)
        {
            throw new ArgumentException("Daily token limit must be greater than 0.");
        }
        if (draft.DailyRequestLimit is <= 0)
        {
            throw new ArgumentException("Daily request limit must be greater than 0.");
        }
        if (draft.ModelPatterns.Length > 50)
        {
            throw new ArgumentException("At most 50 model patterns.");
        }
        if (draft.ModelPatterns.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Model pattern must not be empty.");
        }
    }
}
```

- [ ] **Step 3: `ProviderAccountTestResult.cs`**

```csharp
namespace RouterBalancing.Core.Providers;

/// <summary>Kết quả test của một account trong lần TestAll.</summary>
public sealed record ProviderAccountTestResult(long AccountId, string AccountName, bool Success, string? Message);
```

- [ ] **Step 4: `IProviderAccountService.cs`**

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>CRUD + test connection cho các tài khoản (API key) của một provider — key luôn mã hoá DPAPI.</summary>
public interface IProviderAccountService
{
    /// <summary>Accounts theo provider, sắp Priority tăng dần rồi Name — không trả plaintext key.</summary>
    Task<IReadOnlyList<ProviderAccount>> ListAsync(long providerId, CancellationToken ct = default);

    /// <summary>Tạo account mới; <c>draft.ApiKey</c> bắt buộc.</summary>
    /// <exception cref="KeyNotFoundException">Provider không tồn tại.</exception>
    /// <exception cref="InvalidOperationException">Trùng Name trong cùng provider.</exception>
    /// <exception cref="ArgumentException">Draft không hợp lệ (ProviderAccountValidator).</exception>
    Task<ProviderAccount> CreateAsync(ProviderAccountDraft draft, CancellationToken ct = default);

    /// <summary>Cập nhật; <c>draft.ApiKey</c> rỗng = giữ nguyên key cũ.</summary>
    /// <exception cref="KeyNotFoundException">Account không tồn tại.</exception>
    /// <exception cref="InvalidOperationException">Trùng Name trong cùng provider (trừ chính nó).</exception>
    /// <exception cref="ArgumentException">Draft không hợp lệ.</exception>
    Task<ProviderAccount> UpdateAsync(long id, ProviderAccountDraft draft, CancellationToken ct = default);

    /// <summary>Xoá account; cấm xoá account cuối cùng của provider.</summary>
    /// <exception cref="KeyNotFoundException">Account không tồn tại.</exception>
    /// <exception cref="InvalidOperationException">Đây là account cuối cùng của provider.</exception>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>
    /// Test mọi account enabled của provider: ghi LastTest* từng account và
    /// Provider.LastTest* = AND các account enabled (null nếu không có account enabled).
    /// </summary>
    /// <exception cref="KeyNotFoundException">Provider không tồn tại.</exception>
    Task<IReadOnlyList<ProviderAccountTestResult>> TestAllAsync(long providerId, CancellationToken ct = default);
}
```

- [ ] **Step 5: `ProviderAccountService.cs`**

```csharp
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Providers;

/// <inheritdoc cref="IProviderAccountService"/>
public sealed class ProviderAccountService : IProviderAccountService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly ILogService _log;

    /// <inheritdoc/>
    public ProviderAccountService(
        IDbContextFactory<RouterBalancingDbContext> db,
        ISecretProtector protector,
        IHttpClientFactory http,
        ILogService log)
    {
        _db = db;
        _protector = protector;
        _http = http;
        _log = log;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderAccount>> ListAsync(long providerId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.ProviderAccounts
            .Where(a => a.ProviderId == providerId)
            .OrderBy(a => a.Priority)
            .ThenBy(a => a.Name)
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<ProviderAccount> CreateAsync(ProviderAccountDraft draft, CancellationToken ct = default)
    {
        ProviderAccountValidator.ValidateAndThrow(draft, requireApiKey: true);

        using var db = _db.CreateDbContext();
        if (!await db.Providers.AnyAsync(p => p.Id == draft.ProviderId, ct))
        {
            throw new KeyNotFoundException($"Provider {draft.ProviderId} not found.");
        }

        var name = draft.Name.Trim();
        if (await db.ProviderAccounts.AnyAsync(a => a.ProviderId == draft.ProviderId && a.Name == name, ct))
        {
            throw new InvalidOperationException($"Account name '{name}' already exists for provider {draft.ProviderId}.");
        }

        var account = new ProviderAccount
        {
            ProviderId = draft.ProviderId,
            Name = name,
            ApiKeyEncrypted = _protector.Protect(draft.ApiKey.Trim()),
            Enabled = draft.Enabled,
            ModelPatterns = SerializePatterns(draft.ModelPatterns),
            Weight = draft.Weight,
            Priority = draft.Priority,
            DailyTokenLimit = draft.DailyTokenLimit,
            DailyRequestLimit = draft.DailyRequestLimit,
        };

        db.ProviderAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    /// <inheritdoc/>
    public async Task<ProviderAccount> UpdateAsync(long id, ProviderAccountDraft draft, CancellationToken ct = default)
    {
        ProviderAccountValidator.ValidateAndThrow(draft, requireApiKey: false);

        using var db = _db.CreateDbContext();
        var account = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new KeyNotFoundException($"Account {id} not found.");

        var name = draft.Name.Trim();
        if (await db.ProviderAccounts.AnyAsync(
                a => a.ProviderId == account.ProviderId && a.Id != id && a.Name == name, ct))
        {
            throw new InvalidOperationException($"Account name '{name}' already exists for provider {account.ProviderId}.");
        }

        account.Name = name;
        // Key rỗng khi sửa = giữ nguyên key cũ — không bao giờ ghi đè bằng chuỗi rỗng
        if (!string.IsNullOrEmpty(draft.ApiKey))
        {
            account.ApiKeyEncrypted = _protector.Protect(draft.ApiKey.Trim());
        }
        account.Enabled = draft.Enabled;
        account.ModelPatterns = SerializePatterns(draft.ModelPatterns);
        account.Weight = draft.Weight;
        account.Priority = draft.Priority;
        account.DailyTokenLimit = draft.DailyTokenLimit;
        account.DailyRequestLimit = draft.DailyRequestLimit;
        account.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return account;
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var account = await db.ProviderAccounts.FirstOrDefaultAsync(a => a.Id == id, ct)
            ?? throw new KeyNotFoundException($"Account {id} not found.");

        var remaining = await db.ProviderAccounts
            .CountAsync(a => a.ProviderId == account.ProviderId && a.Id != id, ct);
        if (remaining == 0)
        {
            // Provider không được trơ trọi không có key — UI chặn trước bằng nút disable
            throw new InvalidOperationException($"Account {id} is the last account of provider {account.ProviderId}.");
        }

        db.ProviderAccounts.Remove(account);
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProviderAccountTestResult>> TestAllAsync(long providerId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers
            .Include(p => p.Accounts)
            .FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new KeyNotFoundException($"Provider {providerId} not found.");

        var results = new List<ProviderAccountTestResult>();
        var enabled = provider.Accounts
            .Where(a => a.Enabled)
            .OrderBy(a => a.Priority)
            .ThenBy(a => a.Id)
            .ToList();

        foreach (var account in enabled)
        {
            var at = DateTimeOffset.UtcNow;
            bool success;
            string? message;
            try
            {
                var key = _protector.Unprotect(account.ApiKeyEncrypted);
                using var request = ProviderRequestFactory.Create(provider, key);
                using var response = await _http.CreateClient(ProviderRequestFactory.HttpClientName)
                    .SendAsync(request, ct);
                success = response.IsSuccessStatusCode;
                message = success
                    ? null
                    : $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                           or InvalidOperationException or CryptographicException)
            {
                // Timeout/Socket error/URL sai/key DPAPI hỏng → fail với lý do, không ném ra UI
                success = false;
                message = ex.Message;
                _log.Warn($"Account test failed ({account.Name}): {ex.Message}");
            }

            account.LastTestSuccess = success;
            account.LastTestAt = at;
            account.LastTestMessage = message;
            results.Add(new ProviderAccountTestResult(account.Id, account.Name, success, message));
        }

        // Provider badge = AND các account enabled; không có account enabled → trạng thái "chưa test"
        provider.LastTestSuccess = enabled.Count == 0
            ? null
            : enabled.All(a => a.LastTestSuccess == true);
        provider.LastTestAt = enabled.Count == 0 ? null : DateTimeOffset.UtcNow;
        provider.LastTestMessage = enabled.Count == 0
            ? null
            : string.Join("; ", enabled
                .Where(a => a.LastTestSuccess != true)
                .Select(a => $"{a.Name}: {a.LastTestMessage}"));

        await db.SaveChangesAsync(ct);
        return results;
    }

    /// <summary>Rỗng → null (không cần field); có pattern → JSON array ["a","b"].</summary>
    private static string? SerializePatterns(string[] patterns) =>
        patterns.Length == 0 ? null : JsonSerializer.Serialize(patterns);
}
```

- [ ] **Step 6: DI — `router-balancing/MauiProgram.cs`**

Sau dòng `builder.Services.AddSingleton<IModelService, ModelService>();` (dòng 76) thêm:

```csharp
            builder.Services.AddSingleton<IProviderAccountService, ProviderAccountService>();
```

- [ ] **Step 7: Viết test `ProviderAccountServiceTests.cs` — chạy đỏ trước**

```csharp
// router balancing test/Providers/ProviderAccountServiceTests.cs
using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ProviderAccountServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly ProviderAccountService _service;

    public ProviderAccountServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        // CRUD dùng factory ném — không được phép đụng network
        _service = new ProviderAccountService(_db, _protector, new NeverHttpFactory(), new NullLog());
    }

    public void Dispose() => _testDb.Dispose();

    /// <summary>HttpClientFactory ném nếu bị gọi — CRUD không được đụng network.</summary>
    private sealed class NeverHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("CRUD must not perform HTTP calls.");
    }

    private ProviderAccountService ServiceWith(HttpMessageHandler handler) =>
        new(_db, _protector, new StubFactory(handler), new NullLog());

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Trả 200 khi auth chứa "sk-ok", ngược lại 401 — mô phỏng 1 key sống 1 key chết.</summary>
    private sealed class KeyedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var auth = request.Headers.Authorization?.ToString() ?? string.Empty;
            var ok = auth.Contains("sk-ok", StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{}"),
            });
        }
    }

    private sealed class FixedHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
    }

    private async Task<long> SeedProviderAsync(params (string Name, bool Enabled, int Priority)[] accounts)
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider
        {
            Name = "P",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
            MaxConcurrent = 4,
        };
        foreach (var (name, enabled, priority) in accounts)
        {
            provider.Accounts.Add(new ProviderAccount
            {
                Name = name,
                Enabled = enabled,
                Priority = priority,
                ApiKeyEncrypted = _protector.Protect($"sk-{name}"),
            });
        }
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Id;
    }

    private static ProviderAccountDraft Draft(long providerId, string name = "acct", string key = "sk-1") => new()
    {
        ProviderId = providerId,
        Name = name,
        ApiKey = key,
        Enabled = true,
        Weight = 100,
        Priority = 0,
    };

    [Fact]
    public async Task Create_WithKey_SavesDpapiEncrypted()
    {
        var providerId = await SeedProviderAsync();

        var account = await _service.CreateAsync(Draft(providerId));

        Assert.NotEqual("sk-1", account.ApiKeyEncrypted);
        Assert.Equal("sk-1", _protector.Unprotect(account.ApiKeyEncrypted));

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.Equal(account.ApiKeyEncrypted, saved.ApiKeyEncrypted);
    }

    [Fact]
    public async Task Create_DuplicateNameInSameProvider_ThrowsInvalidOperation()
    {
        var providerId = await SeedProviderAsync();
        await _service.CreateAsync(Draft(providerId, name: "dup"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _service.CreateAsync(Draft(providerId, name: "dup")));
    }

    [Fact]
    public async Task Create_SameNameDifferentProviders_Ok()
    {
        var first = await SeedProviderAsync();
        var second = await SeedProviderAsync();
        await _service.CreateAsync(Draft(first, name: "shared"));

        var account = await _service.CreateAsync(Draft(second, name: "shared"));

        Assert.Equal(second, account.ProviderId);
    }

    [Fact]
    public async Task Create_WhenProviderMissing_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.CreateAsync(Draft(999)));
    }

    [Fact]
    public async Task Create_WithPatterns_SerializesJsonArray()
    {
        var providerId = await SeedProviderAsync();

        var account = await _service.CreateAsync(new ProviderAccountDraft
        {
            ProviderId = providerId,
            Name = "scoped",
            ApiKey = "sk-1",
            ModelPatterns = ["gpt-4o*", "o3*"],
        });

        Assert.Equal("""["gpt-4o*","o3*"]""", account.ModelPatterns);
    }

    [Fact]
    public async Task Create_EmptyPattern_ThrowsArgument()
    {
        var providerId = await SeedProviderAsync();
        var draft = Draft(providerId);
        draft.ModelPatterns = ["ok", "   "];

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(draft));
    }

    [Fact]
    public async Task Create_NameTooLong_ThrowsArgument()
    {
        var providerId = await SeedProviderAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => _service.CreateAsync(Draft(providerId, name: new string('a', 101))));
    }

    [Theory]
    [InlineData("", "sk", 100, 0, null, null)]   // name rỗng
    [InlineData("A", "", 100, 0, null, null)]    // create bắt buộc key
    [InlineData("A", "sk", -1, 0, null, null)]   // weight < 0
    [InlineData("A", "sk", 10001, 0, null, null)] // weight > 10000
    [InlineData("A", "sk", 100, -1001, null, null)] // priority < -1000
    [InlineData("A", "sk", 100, 1001, null, null)]  // priority > 1000
    [InlineData("A", "sk", 100, 0, 0, null)]     // token limit <= 0
    [InlineData("A", "sk", 100, 0, null, -1)]    // request limit <= 0
    public async Task Create_InvalidDraft_ThrowsArgument(
        string name, string key, int weight, int priority, int? tokenLimit, int? requestLimit)
    {
        var providerId = await SeedProviderAsync();
        var draft = Draft(providerId, name, key);
        draft.Weight = weight;
        draft.Priority = priority;
        draft.DailyTokenLimit = tokenLimit;
        draft.DailyRequestLimit = requestLimit;

        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(draft));
    }

    [Fact]
    public async Task Update_EmptyApiKey_KeepsExistingKey()
    {
        var providerId = await SeedProviderAsync();
        var account = await _service.CreateAsync(Draft(providerId, key: "sk-old"));
        var draft = Draft(providerId, name: "renamed", key: string.Empty);

        await _service.UpdateAsync(account.Id, draft);

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.Equal("renamed", saved.Name);
        Assert.Equal("sk-old", _protector.Unprotect(saved.ApiKeyEncrypted));
    }

    [Fact]
    public async Task Update_NewKey_ReplacesEncrypted()
    {
        var providerId = await SeedProviderAsync();
        var account = await _service.CreateAsync(Draft(providerId, key: "sk-old"));

        await _service.UpdateAsync(account.Id, Draft(providerId, key: "sk-new"));

        using var db = _db.CreateDbContext();
        var saved = await db.ProviderAccounts.SingleAsync(a => a.Id == account.Id);
        Assert.Equal("sk-new", _protector.Unprotect(saved.ApiKeyEncrypted));
    }

    [Fact]
    public async Task Update_DuplicateNameExcludingSelf_Ok()
    {
        var providerId = await SeedProviderAsync();
        var account = await _service.CreateAsync(Draft(providerId, name: "only"));
        var draft = Draft(providerId, name: "only", key: string.Empty);

        await _service.UpdateAsync(account.Id, draft); // trùng chính nó vẫn hợp lệ

        using var db = _db.CreateDbContext();
        Assert.Equal(1, await db.ProviderAccounts.CountAsync(a => a.ProviderId == providerId));
    }

    [Fact]
    public async Task Update_WhenAccountMissing_ThrowsKeyNotFound()
    {
        var providerId = await SeedProviderAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => _service.UpdateAsync(999, Draft(providerId)));
    }

    [Fact]
    public async Task Delete_LastAccount_ThrowsInvalidOperation()
    {
        var providerId = await SeedProviderAsync(("only", true, 0));
        using (var db = _db.CreateDbContext())
        {
            var id = await db.ProviderAccounts
                .Where(a => a.ProviderId == providerId)
                .Select(a => a.Id)
                .SingleAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(id));
        }
    }

    [Fact]
    public async Task Delete_NotLast_Succeeds()
    {
        var providerId = await SeedProviderAsync(("a", true, 0), ("b", true, 0));
        using (var db = _db.CreateDbContext())
        {
            var id = await db.ProviderAccounts
                .Where(a => a.ProviderId == providerId && a.Name == "a")
                .Select(a => a.Id)
                .SingleAsync();
            await _service.DeleteAsync(id);
        }

        using var db2 = _db.CreateDbContext();
        Assert.Equal(1, await db2.ProviderAccounts.CountAsync(a => a.ProviderId == providerId));
    }

    [Fact]
    public async Task ListAsync_OrdersByPriorityThenName()
    {
        var providerId = await SeedProviderAsync(("mid", true, 5), ("first", true, 0), ("last", true, 9));

        var list = await _service.ListAsync(providerId);

        Assert.Equal(new[] { "first", "mid", "last" }, list.Select(a => a.Name).ToArray());
    }

    [Fact]
    public async Task DeleteProvider_CascadesToAccounts()
    {
        var providerId = await SeedProviderAsync(("a", true, 0), ("b", true, 0));

        using (var db = _db.CreateDbContext())
        {
            var provider = await db.Providers.SingleAsync(p => p.Id == providerId);
            db.Providers.Remove(provider);
            await db.SaveChangesAsync();
        }

        using var db2 = _db.CreateDbContext();
        Assert.False(await db2.ProviderAccounts.AnyAsync(a => a.ProviderId == providerId));
    }

    [Fact]
    public async Task TestAllAsync_MixedResults_WritesEachAndProviderAnd()
    {
        var providerId = await SeedProviderAsync(("ok", true, 1), ("bad", true, 2));
        var service = ServiceWith(new KeyedHandler());

        var results = await service.TestAllAsync(providerId);

        Assert.Equal(2, results.Count);
        Assert.True(results.Single(r => r.AccountName == "ok").Success);
        var bad = results.Single(r => r.AccountName == "bad");
        Assert.False(bad.Success);
        Assert.Contains("401", bad.Message);

        using var db = _db.CreateDbContext();
        Assert.True(await db.ProviderAccounts
            .Where(a => a.ProviderId == providerId && a.Name == "ok")
            .Select(a => a.LastTestSuccess)
            .SingleAsync());
        var provider = await db.Providers.SingleAsync(p => p.Id == providerId);
        Assert.False(provider.LastTestSuccess); // AND của 2 account: 1 fail → false
        Assert.NotNull(provider.LastTestAt);
    }

    [Fact]
    public async Task TestAllAsync_NoEnabledAccounts_SetsProviderTestNull()
    {
        var providerId = await SeedProviderAsync(("off", false, 0));
        var service = ServiceWith(new FixedHandler(HttpStatusCode.OK));

        var results = await service.TestAllAsync(providerId);

        Assert.Empty(results);
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.SingleAsync(p => p.Id == providerId);
        Assert.Null(provider.LastTestSuccess);
        Assert.Null(provider.LastTestAt);
    }

    [Fact]
    public async Task TestAllAsync_WhenProviderMissing_ThrowsKeyNotFound()
    {
        var service = ServiceWith(new FixedHandler(HttpStatusCode.OK));

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.TestAllAsync(999));
    }
}
```

- [ ] **Step 8: Chạy test — kỳ vọng RED trước khi Service xong?**

Service đã viết ở Step 5 (test viết sau code — thứ tự đảo vì service là new file duy nhất; cycle đỏ/xanh gói trong Step 9).

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo --filter "FullyQualifiedName~ProviderAccountServiceTests"`
Expected: toàn bộ pass, **26 test** của class này (18 Fact + 8 InlineData).

- [ ] **Step 9: Chạy full suite**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: **178/0** (152 + 26).

- [ ] **Step 10: Commit**

```bash
git add -A
git commit -m "feat: add provider account service with validation and test-all"
```

---

### Task 3: UI section Tài khoản + i18n + gates

**Files:**
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (+27 key EN/VI, −1 key chết)
- Modify: `router-balancing/Components/Pages/Providers.razor`
- Modify: `router-balancing/wwwroot/build/` (qua `npm run build`)

**Interfaces:**
- Consumes (Task 2): `IProviderAccountService` 5 method, `ProviderAccountDraft`, `ProviderAccountTestResult`.
- Produces: parity **EN = VI = 209**; UI test count giữ **178**; CDP checklist S1–S8 (controller chạy phase C).

- [ ] **Step 1: `Translations.cs` — xóa key chết**

Xoá dòng duy nhất `["providers.key.saved"] = "..."` trong **cả** bảng EN và VI (chỉ dùng ở ô key mode edit — form edit không còn ô key).

- [ ] **Step 2: `Translations.cs` — thêm 27 key EN**

Chèn ngay sau dòng `["combos.msg.deleted"] = "Combo deleted.",` trong bảng `English`:

```csharp
        ["accounts.section.title"] = "Accounts / API keys",
        ["accounts.empty"] = "No accounts yet.",
        ["accounts.action.add"] = "+ Add account",
        ["accounts.action.testAll"] = "Test all",
        ["accounts.field.name"] = "Account name",
        ["accounts.field.apiKey"] = "API key",
        ["accounts.field.enabled"] = "Enabled",
        ["accounts.badge.disabled"] = "Disabled",
        ["accounts.field.patterns"] = "Model patterns",
        ["accounts.field.weight"] = "Weight",
        ["accounts.field.priority"] = "Priority",
        ["accounts.field.tokenLimit"] = "Daily token limit",
        ["accounts.field.requestLimit"] = "Daily request limit",
        ["accounts.placeholder.key"] = "Enter API key",
        ["accounts.placeholder.keySaved"] = "Saved — leave blank to keep",
        ["accounts.placeholder.patterns"] = "One pattern per line; blank = all models",
        ["accounts.patterns.all"] = "All models",
        ["accounts.patterns.count"] = "{0} models",
        ["accounts.usage.format"] = "Tokens {0}/{1} · Requests {2}/{3}",
        ["accounts.usage.noLimit"] = "Tokens {0} · Requests {1}",
        ["accounts.test.none"] = "Not tested",
        ["accounts.msg.saved"] = "Saved account \"{0}\".",
        ["accounts.msg.deleted"] = "Deleted account \"{0}\".",
        ["accounts.msg.testDone"] = "Test done: {0} ok, {1} failed",
        ["accounts.error.duplicateName"] = "Account name already exists for this provider.",
        ["accounts.error.lastAccount"] = "At least one account must remain.",
        ["accounts.confirm.delete"] = "Delete account \"{0}\"?",
```

- [ ] **Step 3: `Translations.cs` — thêm 27 key VI**

Chèn ngay sau dòng `["combos.msg.deleted] = "Đã xóa bộ kết hợp.",` (bản VI — đúng key `combos.msg.deleted` trong bảng `Vietnamese`):

```csharp
        ["accounts.section.title"] = "Tài khoản / API keys",
        ["accounts.empty"] = "Chưa có tài khoản nào.",
        ["accounts.action.add"] = "+ Thêm tài khoản",
        ["accounts.action.testAll"] = "Test tất cả",
        ["accounts.field.name"] = "Tên tài khoản",
        ["accounts.field.apiKey"] = "API key",
        ["accounts.field.enabled"] = "Bật",
        ["accounts.badge.disabled"] = "Đã tắt",
        ["accounts.field.patterns"] = "Gắn model",
        ["accounts.field.weight"] = "Trọng số",
        ["accounts.field.priority"] = "Ưu tiên",
        ["accounts.field.tokenLimit"] = "Giới hạn token/ngày",
        ["accounts.field.requestLimit"] = "Giới hạn request/ngày",
        ["accounts.placeholder.key"] = "Nhập API key",
        ["accounts.placeholder.keySaved"] = "Đã lưu — để trống để giữ nguyên",
        ["accounts.placeholder.patterns"] = "Mỗi dòng một pattern; để trống = mọi model",
        ["accounts.patterns.all"] = "Tất cả model",
        ["accounts.patterns.count"] = "{0} model",
        ["accounts.usage.format"] = "Token {0}/{1} · Request {2}/{3}",
        ["accounts.usage.noLimit"] = "Token {0} · Request {1}",
        ["accounts.test.none"] = "Chưa test",
        ["accounts.msg.saved"] = "Đã lưu tài khoản \"{0}\".",
        ["accounts.msg.deleted"] = "Đã xóa tài khoản \"{0}\".",
        ["accounts.msg.testDone"] = "Test xong: {0} OK, {1} lỗi",
        ["accounts.error.duplicateName"] = "Tên tài khoản đã tồn tại trong nhà cung cấp này.",
        ["accounts.error.lastAccount"] = "Phải giữ lại ít nhất một tài khoản.",
        ["accounts.confirm.delete"] = "Xóa tài khoản \"{0}\"?",
```

- [ ] **Step 4: Parity check (controller — không phải task subagent)**

Đếm key EN == VI == 209 (183 − 1 + 27). Lệch → sửa Step 2/3.

- [ ] **Step 5: `Providers.razor` — inject + using**

Dòng 2 sau `@using RouterBalancing.Core.Domain` thêm:

```razor
@using System.Text.Json
```

Dòng 7 sau `@inject IProviderService ProviderSvc` thêm:

```razor
@inject IProviderAccountService AccountSvc
```

- [ ] **Step 6: Ô API key — chỉ còn ở Create**

Thay block label key hiện tại (dòng 323–330) bằng:

```razor
        @if (_editingId is null)
        {
            <label class="flex flex-col gap-1 text-sm sm:col-span-2">
                @L["providers.field.apiKey"]
                @* Key tạo account "Default" khi save (spec §4.2) — edit quản lý key ở section Tài khoản *@
                <input type="password" class="rounded border border-border bg-surface px-2 py-1.5"
                       autocomplete="off" spellcheck="false"
                       @bind="_draft.ApiKey" />
            </label>
        }
```

- [ ] **Step 7: Section Tài khoản — chèn vào modal edit**

Chèn **ngay sau khối 3 div lỗi field** (`@if (ModalError(nameof(ProviderDraft.MaxConcurrent)) ...) { ... }` — dòng ~341–344) và **trước** `<div class="mt-4 flex flex-wrap items-center gap-2">` (footer test connection):

```razor
    @if (_editingId is not null)
    {
        @* ===== Section Tài khoản (spec provider-accounts §5.2) — key quản lý hoàn toàn ở đây ===== *@
        <div class="mt-4 flex flex-col gap-2 text-sm">
            <div class="flex items-center justify-between">
                <span class="font-semibold">@L["accounts.section.title"]</span>
                <div class="flex gap-2">
                    <button type="button" class="btn btn-outline-secondary"
                            disabled="@(_busy || _testingAccounts || _accountForm is not null)"
                            @onclick="TestAllAccountsAsync">
                        @(_testingAccounts ? L["providers.testing"] : L["accounts.action.testAll"])
                    </button>
                    <button type="button" class="btn btn-outline-secondary"
                            disabled="@(_busy || _accountForm is not null)"
                            @onclick="AddAccountRow">
                        @L["accounts.action.add"]
                    </button>
                </div>
            </div>

            @if (_accounts.Count == 0)
            {
                <EmptyState Message="@L["accounts.empty"]" />
            }
            else
            {
                <div class="overflow-x-auto rounded border border-border bg-surface">
                    <table class="w-full text-xs">
                        <thead>
                            <tr class="border-b border-border text-left uppercase opacity-70">
                                <th class="px-2 py-1.5">@L["accounts.field.name"]</th>
                                <th class="px-2 py-1.5">@L["accounts.field.enabled"]</th>
                                <th class="px-2 py-1.5">@L["accounts.field.patterns"]</th>
                                @* Cột info (W/P + usage) và cột test: nhãn language-neutral / tái dùng key test có sẵn *@
                                <th class="px-2 py-1.5"></th>
                                <th class="px-2 py-1.5">@L["providers.action.test"]</th>
                                <th class="px-2 py-1.5 text-right">@L["providers.col.actions"]</th>
                            </tr>
                        </thead>
                        <tbody>
                            @foreach (var account in _accounts)
                            {
                                <tr @key="account.Id" class="border-b border-border last:border-b-0">
                                    <td class="px-2 py-1.5 font-medium">@account.Name</td>
                                    <td class="px-2 py-1.5">
                                        <Badge Text="@(account.Enabled ? L["accounts.field.enabled"] : L["accounts.badge.disabled"])"
                                               Variant="@(account.Enabled ? BadgeVariant.Success : BadgeVariant.Neutral)" />
                                    </td>
                                    <td class="px-2 py-1.5 opacity-80">@PatternSummary(account)</td>
                                    <td class="px-2 py-1.5 opacity-80">
                                        W@(account.Weight) P@(account.Priority) · @UsageText(account)
                                    </td>
                                    <td class="px-2 py-1.5">
                                        @if (account.LastTestSuccess is null)
                                        {
                                            <span class="opacity-70">@L["accounts.test.none"]</span>
                                        }
                                        else
                                        {
                                            @* Message là dữ liệu HTTP hiển thị thẳng, không qua i18n *@
                                            <span title="@(account.LastTestMessage ?? string.Empty)">
                                                @(account.LastTestSuccess == true ? "✓" : "✗")
                                            </span>
                                        }
                                    </td>
                                    <td class="px-2 py-1.5 text-right">
                                        <button type="button" class="btn btn-outline-info px-1.5" disabled="@_busy"
                                                @onclick="() => BeginEditAccount(account)">✏️</button>
                                        <button type="button" class="btn btn-outline-danger px-1.5"
                                                disabled="@(_busy || _accounts.Count <= 1)"
                                                @onclick="() => AskDeleteAccount(account)">🗑</button>
                                    </td>
                                </tr>
                            }
                        </tbody>
                    </table>
                </div>
            }

            @if (_accountForm is { } accountForm)
            {
                <div class="flex flex-col gap-2 rounded border border-border bg-background px-2 py-2">
                    <div class="grid gap-2 sm:grid-cols-2">
                        <label class="flex flex-col gap-1">
                            @L["accounts.field.name"]
                            <input class="rounded border border-border bg-surface px-2 py-1" maxlength="100"
                                   disabled="@_busy" @bind="accountForm.Name" @bind:event="oninput" />
                        </label>
                        <label class="flex flex-col gap-1">
                            @L["accounts.field.apiKey"]
                            <input type="password" class="rounded border border-border bg-surface px-2 py-1"
                                   autocomplete="off" spellcheck="false" disabled="@_busy"
                                   placeholder="@(accountForm.Id is null ? L["accounts.placeholder.key"] : L["accounts.placeholder.keySaved"])"
                                   @bind="accountForm.ApiKey" @bind:event="oninput" />
                        </label>
                        <label class="flex items-center gap-1.5">
                            <input type="checkbox" disabled="@_busy" @bind="accountForm.Enabled" />
                            @L["accounts.field.enabled"]
                        </label>
                        <label class="flex flex-col gap-1">
                            @L["accounts.field.weight"]
                            <input type="number" min="0" max="10000"
                                   class="rounded border border-border bg-surface px-2 py-1"
                                   disabled="@_busy" @bind="accountForm.Weight" @bind:event="oninput" />
                        </label>
                        <label class="flex flex-col gap-1">
                            @L["accounts.field.priority"]
                            <input type="number" min="-1000" max="1000"
                                   class="rounded border border-border bg-surface px-2 py-1"
                                   disabled="@_busy" @bind="accountForm.Priority" @bind:event="oninput" />
                        </label>
                        <label class="flex flex-col gap-1">
                            @L["accounts.field.tokenLimit"]
                            <input type="number" min="1"
                                   class="rounded border border-border bg-surface px-2 py-1"
                                   disabled="@_busy" @bind="accountForm.DailyTokenLimit" @bind:event="oninput" />
                        </label>
                        <label class="flex flex-col gap-1">
                            @L["accounts.field.requestLimit"]
                            <input type="number" min="1"
                                   class="rounded border border-border bg-surface px-2 py-1"
                                   disabled="@_busy" @bind="accountForm.DailyRequestLimit" @bind:event="oninput" />
                        </label>
                        <label class="flex flex-col gap-1 sm:col-span-2">
                            @L["accounts.field.patterns"]
                            <textarea class="h-16 rounded border border-border bg-surface px-2 py-1 font-mono text-xs"
                                      placeholder="@L["accounts.placeholder.patterns"]"
                                      disabled="@_busy" @bind="accountForm.PatternsText" @bind:event="oninput"></textarea>
                        </label>
                    </div>
                    <div class="flex justify-end gap-2">
                        <button type="button" class="btn btn-outline-secondary" disabled="@_busy" @onclick="CancelAccountForm">
                            @L["confirm.cancel"]
                        </button>
                        <button type="button" class="btn btn-primary"
                                disabled="@(!CanSaveAccount || _busy || _saving)"
                                @onclick="SaveAccountAsync">
                            @L["settings.action.save"]
                        </button>
                    </div>
                </div>
            }
        </div>
    }
```

- [ ] **Step 8: ConfirmDialog xoá account — chèn sau ConfirmDialog `_confirmRemoveAll` (dòng ~403–413)**

```razor
@if (_confirmDeleteAccount is { } accountToDelete)
{
    <ConfirmDialog Visible="true"
                   Title="@L["providers.action.delete"]"
                   Message="@string.Format(L["accounts.confirm.delete"], accountToDelete.Name)"
                   Danger="true"
                   ConfirmText="@L["providers.action.delete"]"
                   CancelText="@L["confirm.cancel"]"
                   OnConfirm="DeleteAccountAsync"
                   OnCancel="CancelDeleteAccount" />
}
```

- [ ] **Step 9: `@code` — state + class `AccountForm`**

Sau khối `private ProviderTestResult? _testResult;` (dòng ~450) thêm:

```csharp
    // ===== Section Accounts trong modal edit (spec provider-accounts §5.2) =====
    private List<ProviderAccount> _accounts = [];
    private bool _testingAccounts;
    private AccountForm? _accountForm;
    private ProviderAccount? _confirmDeleteAccount;

    /// <summary>State form account add/edit — ApiKey chỉ tồn tại lúc nhập, không bao giờ hiển thị lại.</summary>
    private sealed class AccountForm
    {
        public long? Id { get; init; } // null = thêm mới

        public string Name { get; set; } = string.Empty;

        public string ApiKey { get; set; } = string.Empty;

        public bool Enabled { get; set; } = true;

        /// <summary>Textarea 1 pattern/dòng — serialize JSON lúc lưu.</summary>
        public string PatternsText { get; set; } = string.Empty;

        public int Weight { get; set; } = 100;

        public int Priority { get; set; }

        public int? DailyTokenLimit { get; set; }

        public int? DailyRequestLimit { get; set; }
    }
```

- [ ] **Step 10: `@code` — `OpenEdit` tải accounts**

Thay nguyên hàm `OpenEdit` (dòng 819–833):

```csharp
    private async Task OpenEdit(Provider provider)
    {
        _editingId = provider.Id;
        // ApiKey không còn ở form edit — key quản lý ở section Accounts (spec §5.2)
        _draft = new ProviderDraft
        {
            Name = provider.Name,
            Type = provider.Type,
            BaseUrl = provider.BaseUrl,
            ApiKey = string.Empty,
            MaxConcurrent = provider.MaxConcurrent,
        };
        ResetModalState();
        _accountForm = null;
        _accounts = [];
        _modalVisible = true;
        await LoadAccountsAsync(provider.Id);
    }
```

(Gọi site `@onclick="() => OpenEdit(p)"` — lambda trả `Task`, Blazor tự await ✓; cũng sửa comment cũ dòng 822.)

- [ ] **Step 11: `@code` — handlers accounts**

Thêm các hàm này (sau `SaveAsync` hoặc cuối `@code`, trước `ReloadKeepExpandAsync` đều được):

```csharp
    private async Task LoadAccountsAsync(long providerId)
    {
        try
        {
            _accounts = (await AccountSvc.ListAsync(providerId)).ToList();
        }
        catch (Exception ex)
        {
            Log.Error("Không tải được danh sách tài khoản.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
    }

    private void AddAccountRow() => _accountForm = new AccountForm();

    private void BeginEditAccount(ProviderAccount account) =>
        _accountForm = new AccountForm
        {
            Id = account.Id,
            Name = account.Name,
            Enabled = account.Enabled,
            PatternsText = DecodePatterns(account.ModelPatterns),
            Weight = account.Weight,
            Priority = account.Priority,
            DailyTokenLimit = account.DailyTokenLimit,
            DailyRequestLimit = account.DailyRequestLimit,
        };

    private void CancelAccountForm() => _accountForm = null;

    /// <summary>JSON array → textarea 1 pattern/dòng; null/empty → chuỗi rỗng.</summary>
    private static string DecodePatterns(string? json) =>
        string.IsNullOrEmpty(json)
            ? string.Empty
            : string.Join('\n', JsonSerializer.Deserialize<string[]>(json) ?? []);

    private bool CanSaveAccount =>
        _accountForm is { } form
        && form.Name.Trim().Length > 0
        && (form.Id is not null || form.ApiKey.Trim().Length > 0);

    private async Task SaveAccountAsync()
    {
        if (_busy || _accountForm is not { } form || _editingId is not { } providerId) return;
        if (!CanSaveAccount) return; // chặn cứng — nút đã disabled nhưng guard vẫn đứng đây

        var patterns = form.PatternsText.Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var draft = new ProviderAccountDraft
        {
            ProviderId = providerId,
            Name = form.Name.Trim(),
            ApiKey = form.ApiKey.Trim(),
            Enabled = form.Enabled,
            ModelPatterns = patterns,
            Weight = form.Weight,
            Priority = form.Priority,
            DailyTokenLimit = form.DailyTokenLimit,
            DailyRequestLimit = form.DailyRequestLimit,
        };

        _busy = true;
        try
        {
            if (form.Id is { } accountId)
            {
                await AccountSvc.UpdateAsync(accountId, draft);
            }
            else
            {
                await AccountSvc.CreateAsync(draft);
            }

            Toast.Show(string.Format(L["accounts.msg.saved"], draft.Name), ToastSeverity.Success);
            _accountForm = null;
            await LoadAccountsAsync(providerId);
        }
        catch (InvalidOperationException)
        {
            // Trùng tên là InvalidOperation duy nhất ở nhánh save (lastAccount chỉ ném ở delete)
            Toast.Show(L["accounts.error.duplicateName"], ToastSeverity.Error);
        }
        catch (Exception ex)
        {
            Log.Error("Không lưu được tài khoản.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private void AskDeleteAccount(ProviderAccount account)
    {
        // Pre-check: không cho xoá account cuối (nút đã disabled, service là backstop)
        if (_busy || _accounts.Count <= 1) return;
        _confirmDeleteAccount = account;
    }

    private void CancelDeleteAccount() => _confirmDeleteAccount = null;

    private async Task DeleteAccountAsync()
    {
        if (_busy || _confirmDeleteAccount is not { } account) return;

        _busy = true;
        try
        {
            await AccountSvc.DeleteAsync(account.Id);
            Toast.Show(string.Format(L["accounts.msg.deleted"], account.Name), ToastSeverity.Success);
            _confirmDeleteAccount = null;
            await LoadAccountsAsync(account.ProviderId);
        }
        catch (InvalidOperationException)
        {
            // Account cuối cùng — service backstop khi UI pre-check bị race
            Toast.Show(L["accounts.error.lastAccount"], ToastSeverity.Error);
        }
        catch (Exception ex)
        {
            Log.Error("Không xóa được tài khoản.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task TestAllAccountsAsync()
    {
        if (_busy || _editingId is not { } providerId) return;

        _busy = true;
        _testingAccounts = true;
        try
        {
            var results = await AccountSvc.TestAllAsync(providerId);
            var failed = results.Count(r => !r.Success);
            Toast.Show(
                string.Format(L["accounts.msg.testDone"], results.Count - failed, failed),
                failed > 0 ? ToastSeverity.Error : ToastSeverity.Success);
            await LoadAccountsAsync(providerId);
            await LoadAsync(); // Provider.LastTest* (AND các account) đổi theo
        }
        catch (Exception ex)
        {
            Log.Error("Test tài khoản thất bại.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _testingAccounts = false;
            _busy = false;
        }
    }

    /// <summary>Pattern summary: rỗng = mọi model; có = số pattern.</summary>
    private string PatternSummary(ProviderAccount account)
    {
        if (string.IsNullOrEmpty(account.ModelPatterns))
        {
            return L["accounts.patterns.all"];
        }
        var patterns = JsonSerializer.Deserialize<string[]>(account.ModelPatterns) ?? [];
        return string.Format(L["accounts.patterns.count"], patterns.Length);
    }

    /// <summary>Chỉ hiện limit khi CẢ token và request đều set — 1 trong 2 rơi về used-only,
    /// tránh hiển thị "x/x" giả cho bên chưa set.</summary>
    private string UsageText(ProviderAccount account)
    {
        var tokens = account.TokensUsed.ToString("N0");
        var requests = account.RequestsUsed.ToString("N0");
        if (account.DailyTokenLimit is { } tokenLimit && account.DailyRequestLimit is { } requestLimit)
        {
            return string.Format(L["accounts.usage.format"],
                tokens, tokenLimit.ToString("N0"), requests, requestLimit.ToString("N0"));
        }
        return string.Format(L["accounts.usage.noLimit"], tokens, requests);
    }
```

- [ ] **Step 12: Card button Test → TestAll**

Thay thân hàm `TestAsync(Provider provider)` (dòng 521–552) — giữ nguyên phần single-flight `_testingId`, thay block `try`:

```csharp
        try
        {
            // Đa tài khoản: test toàn bộ account enabled — badge provider = AND (service ghi)
            var results = await AccountSvc.TestAllAsync(provider.Id);
            var failed = results.Count(r => !r.Success);
            Toast.Show(
                string.Format(L["accounts.msg.testDone"], results.Count - failed, failed),
                failed > 0 ? ToastSeverity.Error : ToastSeverity.Success);
            await LoadAsync(); // badge LastTest* mới nhất
        }
```

(Giữ `catch`/`finally` hiện có.)

- [ ] **Step 13: Build + test + npm**

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: 0 warning / 0 error.

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: **178/0**.

Run (workdir `router-balancing/vite-project`): `npm run build`
Expected: exit 0 (stage `router-balancing/wwwroot/build/`).

- [ ] **Step 14: Parity + git status (controller)**

- EN = VI = **209**.
- `git status --short` → sạch sau commit.

- [ ] **Step 15: Commit**

```bash
git add -A
git commit -m "feat: add provider accounts section with i18n"
```

- [ ] **Step 16: CDP self-test (controller — phase C, không phải subagent)**

Chạy app với `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`, script `%TEMP%\opencode\cdp-test\drive40.js` (pattern drive32–34):

- **S1** Edit modal → section `Tài khoản / API keys` + bảng (hoặc empty state) visible.
- **S2** Add account đủ field → toast `Đã lưu tài khoản "<tên>".` → dòng mới trong bảng.
- **S3** Duplicate name → toast `Tên tài khoản đã tồn tại...` + sub-form còn mở.
- **S4** Edit account để trống key → lưu OK (key giữ nguyên — verify bằng cách reopen placeholder `Đã lưu — để trống để giữ nguyên`).
- **S5** ≥2 account: 🗑 → confirm `Xóa tài khoản "<tên>"?` → Hủy giữ nguyên; Xóa → toast; về 1 account → nút 🗑 disabled.
- **S6** `Test tất cả` (có key mock không thật → expect ✗ fail đều) → toast `Test xong: 0 OK, N lỗi`, badge từng dòng đổi.
- **S7** Settings đổi `en` → section/Sub-form/placeholder EN (`Accounts / API keys`, `+ Add account`, `Enter API key`) → khôi phục ngôn ngữ cũ.
- **S8** Create provider với key → save → mở edit → account `Default` xuất hiện.

Gate tổng: **178/0 + 0W/0E + npm 0 + parity 209/209 + CDP S1–S8 PASS + git sạch.**

---

## Self-review (plan)

1. **Spec coverage:** §2 (entity/DbContext) → T1 S1–3; §3 (migration + ripple) → T1 S7–S13, resolver T1 S4; §4 (contract/validator/ProviderService change) → T2 S1–6 + T1 S5; §5 (UI) → T3 S5–12; §6 (i18n 27 key − 1 dead) → T3 S1–3; §7 (tests/CDP/gates) → T1 S9–11, T2 S7–9, T3 S13–16; §8 Phase 3 handoff → không code (đúng non-goal).
2. **Placeholder scan:** không TBD/TODO; mọi step code block hoặc lệnh chính xác.
3. **Type consistency:** `ProviderAccountDraft` (T2 S1) khớp `IProviderAccountService` (T2 S4) khớp `SaveAccountAsync` (T3 S11); `ProviderAccountTestResult(AccountId, AccountName, Success, Message)` khớp test `results.Single(r => r.AccountName == ...)` và UI `results.Count(r => !r.Success)`; `ProviderAccounts` DbSet dùng thống nhất ở T1–T2.
