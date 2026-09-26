# Phase 2A: Provider/Model Management Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thêm màn hình quản lý Provider + Model (`/providers`): CRUD provider với test connection trước khi lưu, quản lý model inline (fetch/manual/bulk/toggle/xóa), metadata auto-fill.

**Architecture:** Service layer trong `RouterBalancing.Core` (namespace `RouterBalancing.Core.Providers`) — `IProviderService`/`IModelService` dùng `IDbContextFactory` + DPAPI `ISecretProtector` + named `HttpClient("provider-probe")`; metadata chain `IModelMetadataProvider` (endpoint → static catalog → null). UI: page Blazor `/providers` + 5 shared components (`Modal`, `ConfirmDialog`, `Pager`, `Badge`, `EmptyState`).

**Tech Stack:** .NET 10 / MAUI Blazor Hybrid, EF Core Sqlite 10.0.12, xUnit, Vite + Tailwind v4.

**Spec:** `docs/superpowers/specs/2026-09-26-provider-model-management-design.md` (đã approve).

## Global Constraints

- Branch: `feat/phase2-provider-mgmt` (đã tạo từ master — KHÔNG tạo/nhảy nhánh khác).
- TFM: app `net10.0-windows10.0.19041.0`; Core/test `net10.0`. `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên.
- Packages Core chỉ thêm `Microsoft.Extensions.Http` nếu chưa có (version 10.0.x khớp family 10.0.12); KHÔNG upgrade package có sẵn.
- **Test:** `dotnet test "router balancing test/router balancing test.csproj" --nologo` — baseline **61** (Phase 1). Tên test `Method_WhenX_ExpectY`.
- **Build app:** `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo` → 0 errors, 0 warnings.
- **npm (bắt buộc trước commit task UI):** `npm run build` với workdir `router-balancing/vite-project`; commit `router-balancing/wwwroot/build/` nếu thay đổi.
- **Không bao giờ** log/hiển thị API key plaintext; key chỉ qua `ISecretProtector.Protect` trước khi xuống DB.
- Comment why-only tiếng Việt; XML doc (`///`) cho public type/method/property; commit message tiếng Anh conventional, 1 task = 1 commit (trừ khi brief nói commit riêng phần).
- i18n: mọi text hiển thị qua `LocalizationService L["key"]`, key thêm vào `Translations.English` + `Translations.Vietnamese` (cùng số key cả 2 ngôn ngữ).
- Không commit file trong `.superpowers/` (đã gitignore).

---

### Task 1: ProviderDraft + ProviderValidator (TDD)

**Files:**
- Create: `src/RouterBalancing.Core/Providers/ProviderDraft.cs`
- Create: `src/RouterBalancing.Core/Providers/ProviderValidator.cs`
- Test: `router balancing test/Providers/ProviderValidatorTests.cs`

**Interfaces:**
- Consumes: (không — đầu tiên)
- Produces:
  - `sealed record ProviderDraft` (ns `RouterBalancing.Core.Providers`): `string Name`, `ProviderType Type`, `string BaseUrl`, `string ApiKey`, `int MaxConcurrent` — tất cả mutable `get; set;` (Blazor `@bind`).
  - `static class ProviderValidator` — `IReadOnlyDictionary<string, string> Validate(ProviderDraft draft)`; key = `nameof(ProviderDraft.X)`, value = i18n error key (`providers.error.*`). Không text EN/VI literal trong validator.

- [ ] **Step 1: Viết failing test**

Tạo `router balancing test/Providers/ProviderValidatorTests.cs`:

```csharp
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class ProviderValidatorTests
{
    private static ProviderDraft ValidDraft() => new()
    {
        Name = "OpenAI",
        Type = ProviderType.OpenAI,
        BaseUrl = "https://api.openai.com",
        ApiKey = "sk-test",
        MaxConcurrent = 4,
    };

    [Fact]
    public void Validate_WhenDraftValid_ReturnsEmpty()
    {
        var errors = ProviderValidator.Validate(ValidDraft());

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_WhenNameBlank_ReturnsNameError()
    {
        var blank = ProviderValidator.Validate(ValidDraft() with { Name = "   " });
        var missing = ProviderValidator.Validate(ValidDraft() with { Name = "" });

        Assert.Equal("providers.error.name", blank[nameof(ProviderDraft.Name)]);
        Assert.Equal("providers.error.name", missing[nameof(ProviderDraft.Name)]);
    }

    [Fact]
    public void Validate_WhenBaseUrlNotHttp_ReturnsBaseUrlError()
    {
        var ftp = ProviderValidator.Validate(ValidDraft() with { BaseUrl = "ftp://example.com" });
        var empty = ProviderValidator.Validate(ValidDraft() with { BaseUrl = "" });

        Assert.Equal("providers.error.baseUrl", ftp[nameof(ProviderDraft.BaseUrl)]);
        Assert.Equal("providers.error.baseUrl", empty[nameof(ProviderDraft.BaseUrl)]);
    }

    [Fact]
    public void Validate_WhenMaxConcurrentOutOfRange_ReturnsMaxConcurrentError()
    {
        var low = ProviderValidator.Validate(ValidDraft() with { MaxConcurrent = 0 });
        var high = ProviderValidator.Validate(ValidDraft() with { MaxConcurrent = 65 });

        Assert.Equal("providers.error.maxConcurrent", low[nameof(ProviderDraft.MaxConcurrent)]);
        Assert.Equal("providers.error.maxConcurrent", high[nameof(ProviderDraft.MaxConcurrent)]);
    }
}
```

- [ ] **Step 2: Chạy test — kỳ vọng FAIL (RED)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: **FAIL** — `error CS0246: The type or namespace name 'ProviderDraft' could not be found`.

- [ ] **Step 3: Viết implementation minimal**

Tạo `src/RouterBalancing.Core/Providers/ProviderDraft.cs`:

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Bản nháp form provider. Property mutable (không phải positional record)
/// để Blazor <c>@bind</c> ghi được — giống <c>SettingsDraft</c>.
/// </summary>
public sealed record ProviderDraft
{
    public string Name { get; set; } = string.Empty;

    public ProviderType Type { get; set; } = ProviderType.OpenAI;

    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Plaintext từ form — chỉ tồn tại trong lúc nhập, không bao giờ log hay persist trực tiếp.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public int MaxConcurrent { get; set; } = 4;
}
```

Tạo `src/RouterBalancing.Core/Providers/ProviderValidator.cs`:

```csharp
namespace RouterBalancing.Core.Providers;

/// <summary>
/// Validate bản nháp form provider. Trả key lỗi i18n (không text literal) —
/// UI dịch theo ngôn ngữ hiện tại, giống <c>SettingsValidator</c>.
/// </summary>
public static class ProviderValidator
{
    /// <summary>Kiểm tra toàn bộ rule; dictionary rỗng = hợp lệ.</summary>
    public static IReadOnlyDictionary<string, string> Validate(ProviderDraft draft)
    {
        var errors = new Dictionary<string, string>();

        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            errors[nameof(ProviderDraft.Name)] = "providers.error.name";
        }

        // Chỉ chấp nhận http(s) — SSRF/parse URL ở tầng service tin được BaseUrl này.
        // Lưu ý: pattern `is not (s && ...)` không hợp lệ ngữ pháp C# — viết bằng ||
        // short-circuit: null/""/không http(s) -> lỗi; http(s):// (mọi hoa thường) -> hợp lệ.
        if (draft.BaseUrl is not string s
            || (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            errors[nameof(ProviderDraft.BaseUrl)] = "providers.error.baseUrl";
        }

        if (draft.MaxConcurrent is < 1 or > 64)
        {
            errors[nameof(ProviderDraft.MaxConcurrent)] = "providers.error.maxConcurrent";
        }

        return errors;
    }
}
```

- [ ] **Step 4: Chạy test — kỳ vọng PASS (GREEN)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed!` — tổng **65** (61 + 4 mới).

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Providers/ "router balancing test/Providers/"
git commit -m "feat: add provider draft and validator"
```

---

### Task 2: IProviderService CRUD (TDD)

**Files:**
- Create: `src/RouterBalancing.Core/Providers/IProviderService.cs`
- Create: `src/RouterBalancing.Core/Providers/ProviderService.cs`
- Test: `router balancing test/Providers/ProviderServiceTests.cs`

**Interfaces:**
- Consumes: Task 1 (`ProviderDraft`, `ProviderValidator`), Phase 1 (`IDbContextFactory<RouterBalancingDbContext>`, `ISecretProtector`, entities `Provider`/`Model`, `TestDb`, `DbInitializer`).
- Produces (ns `RouterBalancing.Core.Providers`):

```csharp
public interface IProviderService
{
    Task<IReadOnlyList<Provider>> ListAsync(CancellationToken ct = default);
    Task<Provider?> GetAsync(long id, CancellationToken ct = default);
    Task<Provider> CreateAsync(ProviderDraft draft, CancellationToken ct = default);
    Task UpdateAsync(long id, ProviderDraft draft, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);
}
```

(Task 3 sẽ bổ sung `TestConnectionAsync` vào interface này.)

- [ ] **Step 1: Viết failing test**

Tạo `router balancing test/Providers/ProviderServiceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ProviderServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();
    private readonly ProviderService _service;

    public ProviderServiceTests()
    {
        // Initialize trước mỗi test — TestDb là file trống, schema chưa có (pattern Phase 1)
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        _service = new ProviderService(_db, _protector);
    }

    public void Dispose() => _testDb.Dispose();

    private static ProviderDraft Draft(string name = "OpenAI", string key = "sk-secret") => new()
    {
        Name = name,
        Type = ProviderType.OpenAI,
        BaseUrl = "https://api.openai.com/",
        ApiKey = key,
        MaxConcurrent = 4,
    };

    [Fact]
    public async Task Create_WhenKeyProvided_EncryptsAndPersists()
    {
        var provider = await _service.CreateAsync(Draft());

        Assert.NotEqual("sk-secret", provider.ApiKeyEncrypted);
        Assert.Equal("sk-secret", _protector.Unprotect(provider.ApiKeyEncrypted));
        Assert.Equal("https://api.openai.com", provider.BaseUrl); // trailing slash đã trim
        Assert.Equal("OpenAI", provider.Name);

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.Equal(provider.ApiKeyEncrypted, saved.ApiKeyEncrypted);
    }

    [Fact]
    public async Task ListAsync_WhenProvidersExist_IncludesModels()
    {
        var provider = await _service.CreateAsync(Draft());
        using (var db = _db.CreateDbContext())
        {
            db.Models.Add(new Model { ProviderId = provider.Id, ModelId = "gpt-4o" });
            await db.SaveChangesAsync();
        }

        var list = await _service.ListAsync();

        var loaded = Assert.Single(list);
        Assert.Equal("gpt-4o", Assert.Single(loaded.Models).ModelId);
    }

    [Fact]
    public async Task Update_WhenApiKeyBlank_KeepsExistingKeyAndRefreshesTimestamp()
    {
        var provider = await _service.CreateAsync(Draft(key: "sk-old"));
        var before = provider.UpdatedAt;

        await Task.Delay(10); // UpdatedAt có độ phân giải tick — đảm bảo khác biệt thực sự
        await _service.UpdateAsync(provider.Id, Draft(name: "Renamed", key: ""));

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal("sk-old", _protector.Unprotect(saved.ApiKeyEncrypted));
        Assert.True(saved.UpdatedAt > before);
    }

    [Fact]
    public async Task Update_WhenApiKeyProvided_Reencrypts()
    {
        var provider = await _service.CreateAsync(Draft(key: "sk-old"));

        await _service.UpdateAsync(provider.Id, Draft(key: "sk-new"));

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.Equal("sk-new", _protector.Unprotect(saved.ApiKeyEncrypted));
    }

    [Fact]
    public async Task Delete_WhenCalled_RemovesProviderAndCascadesModels()
    {
        var provider = await _service.CreateAsync(Draft());
        using (var db = _db.CreateDbContext())
        {
            db.Models.Add(new Model { ProviderId = provider.Id, ModelId = "gpt-4o" });
            await db.SaveChangesAsync();
        }

        await _service.DeleteAsync(provider.Id);

        using var db2 = _db.CreateDbContext();
        Assert.False(await db2.Providers.AnyAsync(p => p.Id == provider.Id));
        Assert.False(await db2.Models.AnyAsync(m => m.ProviderId == provider.Id));
    }

    [Fact]
    public async Task Delete_WhenUnknownId_ThrowsKeyNotFound()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _service.DeleteAsync(999));
    }

    [Fact]
    public async Task SetEnabled_WhenToggled_Persists()
    {
        var provider = await _service.CreateAsync(Draft());

        await _service.SetEnabledAsync(provider.Id, false);

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.False(saved.Enabled);
    }
}
```

- [ ] **Step 2: Chạy test — kỳ vọng FAIL (RED)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: **FAIL** — `CS0246: The type or namespace name 'ProviderService' could not be found`.

- [ ] **Step 3: Viết implementation**

Tạo `src/RouterBalancing.Core/Providers/IProviderService.cs`:

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>CRUD provider — key luôn mã hóa DPAPI trước khi xuống DB.</summary>
public interface IProviderService
{
    /// <summary>Tất cả provider, sắp theo Id tăng dần, đã Include Models.</summary>
    Task<IReadOnlyList<Provider>> ListAsync(CancellationToken ct = default);

    /// <summary>Provider theo id kèm Models; <see langword="null"/> nếu không tồn tại.</summary>
    Task<Provider?> GetAsync(long id, CancellationToken ct = default);

    /// <summary>Tạo provider mới từ bản nháp (key đã Protect).</summary>
    Task<Provider> CreateAsync(ProviderDraft draft, CancellationToken ct = default);

    /// <summary>
    /// Cập nhật provider. <c>draft.ApiKey</c> rỗng = giữ nguyên key cũ
    /// (người dùng sửa form không chủ đích xóa key).
    /// </summary>
    /// <exception cref="KeyNotFoundException">Khi không có provider <paramref name="id"/>.</exception>
    Task UpdateAsync(long id, ProviderDraft draft, CancellationToken ct = default);

    /// <summary>Xóa provider — models con cascade theo cấu hình FK.</summary>
    /// <exception cref="KeyNotFoundException">Khi không có provider <paramref name="id"/>.</exception>
    Task DeleteAsync(long id, CancellationToken ct = default);

    /// <summary>Bật/tắt provider.</summary>
    /// <exception cref="KeyNotFoundException">Khi không có provider <paramref name="id"/>.</exception>
    Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default);
}
```

Tạo `src/RouterBalancing.Core/Providers/ProviderService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Providers;

/// <inheritdoc cref="IProviderService"/>
public sealed class ProviderService : IProviderService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;

    public ProviderService(IDbContextFactory<RouterBalancingDbContext> db, ISecretProtector protector)
    {
        _db = db;
        _protector = protector;
    }

    public async Task<IReadOnlyList<Provider>> ListAsync(CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.Providers
            .Include(p => p.Models)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
    }

    public async Task<Provider?> GetAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        return await db.Providers
            .Include(p => p.Models)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<Provider> CreateAsync(ProviderDraft draft, CancellationToken ct = default)
    {
        var provider = new Provider
        {
            Name = draft.Name.Trim(),
            Type = draft.Type,
            BaseUrl = draft.BaseUrl.Trim().TrimEnd('/'),
            ApiKeyEncrypted = string.IsNullOrEmpty(draft.ApiKey)
                ? string.Empty
                : _protector.Protect(draft.ApiKey),
            MaxConcurrent = draft.MaxConcurrent,
        };

        using var db = _db.CreateDbContext();
        db.Providers.Add(provider);
        await db.SaveChangesAsync(ct);
        return provider;
    }

    public async Task UpdateAsync(long id, ProviderDraft draft, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Provider {id} not found.");

        provider.Name = draft.Name.Trim();
        provider.Type = draft.Type;
        provider.BaseUrl = draft.BaseUrl.Trim().TrimEnd('/');
        provider.MaxConcurrent = draft.MaxConcurrent;
        // Key rỗng khi sửa = giữ nguyên key cũ — không bao giờ ghi đè bằng chuỗi rỗng
        if (!string.IsNullOrEmpty(draft.ApiKey))
        {
            provider.ApiKeyEncrypted = _protector.Protect(draft.ApiKey);
        }
        provider.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Provider {id} not found.");

        // FK Models→Providers là ON DELETE CASCADE (xem RouterBalancingDbContext.OnModelCreating)
        // — xóa provider, DB tự xóa models con.
        db.Providers.Remove(provider);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetEnabledAsync(long id, bool enabled, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == id, ct)
            ?? throw new KeyNotFoundException($"Provider {id} not found.");

        provider.Enabled = enabled;
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 4: Chạy test — kỳ vọng PASS (GREEN)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed!` — tổng **72** (65 + 7 mới).

- [ ] **Step 5: Build app không lỗi (service nằm trong Core, app tham chiếu)**

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/RouterBalancing.Core/Providers/ "router balancing test/Providers/"
git commit -m "feat: add provider crud service with dpapi key encryption"
```

---

### Task 3: TestConnectionAsync + ProviderRequestFactory (TDD)

**Files:**
- Create: `src/RouterBalancing.Core/Providers/ProviderTestResult.cs`
- Create: `src/RouterBalancing.Core/Providers/ProviderRequestFactory.cs`
- Modify: `src/RouterBalancing.Core/Providers/IProviderService.cs` (thêm method)
- Modify: `src/RouterBalancing.Core/Providers/ProviderService.cs` (thêm method + deps)
- Test: `router balancing test/Providers/ProviderTestConnectionTests.cs`

**Interfaces:**
- Consumes: Task 2 (`ProviderService`), Phase 1 (`ISecretProtector`, `ILogService`).
- Produces:
  - `sealed record ProviderTestResult(bool Success, string? Message, DateTimeOffset At)`.
  - `static class ProviderRequestFactory` — `HttpRequestMessage Create(Provider provider, string apiKey, string? path = null)`; `const string HttpClientName = "provider-probe"`.
  - `IProviderService.TestConnectionAsync(Provider provider, string? apiKeyOverride, CancellationToken ct = default)` → `Task<ProviderTestResult>`; `Provider.Id != 0` (đã lưu DB) → persist `LastTestSuccess/LastTestAt/LastTestMessage`.

- [ ] **Step 1: Viết failing test**

Tạo `router balancing test/Providers/ProviderTestConnectionTests.cs`:

```csharp
using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ProviderTestConnectionTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public ProviderTestConnectionTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
    }

    public void Dispose() => _testDb.Dispose();

    /// <summary>Trả response tùy ý; ghi lại request để assert header.</summary>
    private sealed class FakeHandler(HttpStatusCode status, string body = "{}") : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    /// <summary>Handler ném — mô phỏng timeout/mất mạng.</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("No such host is known.");
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private ProviderService ServiceWith(HttpMessageHandler handler) =>
        new(_db, _protector, new StubFactory(handler), new NullLog());

    // NullLog: dùng lại từ `router balancing test/TestDoubles.cs` (Task 3) — KHÔNG khai
    // báo inner class (bản inline thiếu Write/Count → CS0535 với ILogService thật).

    private async Task<Provider> SavedProviderAsync(ProviderType type = ProviderType.OpenAI)
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider
        {
            Name = "P",
            Type = type,
            BaseUrl = "https://api.example.com",
            ApiKeyEncrypted = _protector.Protect("sk-saved"),
            MaxConcurrent = 4,
        };
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    [Fact]
    public async Task TestConnection_When200_ReturnsSuccessAndPersistsResult()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var provider = await SavedProviderAsync();
        var service = ServiceWith(handler);

        var result = await service.TestConnectionAsync(provider, apiKeyOverride: null);

        Assert.True(result.Success);
        Assert.Null(result.Message);
        Assert.NotNull(result.At);

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.True(saved.LastTestSuccess);
        Assert.NotNull(saved.LastTestAt);
    }

    [Fact]
    public async Task TestConnection_When401_ReturnsFailureWithStatusMessage()
    {
        var handler = new FakeHandler(HttpStatusCode.Unauthorized);
        var provider = await SavedProviderAsync();
        var service = ServiceWith(handler);

        var result = await service.TestConnectionAsync(provider, apiKeyOverride: null);

        Assert.False(result.Success);
        Assert.Contains("401", result.Message);

        using var db = _db.CreateDbContext();
        var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
        Assert.False(saved.LastTestSuccess);
    }

    [Fact]
    public async Task TestConnection_WhenHandlerThrows_ReturnsFailureNotThrow()
    {
        var provider = await SavedProviderAsync();
        var service = ServiceWith(new ThrowingHandler());

        var result = await service.TestConnectionAsync(provider, apiKeyOverride: null);

        Assert.False(result.Success);
        Assert.Contains("No such host", result.Message);
    }

    [Fact]
    public async Task TestConnection_WhenOpenAi_SendsBearerHeader()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var provider = await SavedProviderAsync(ProviderType.OpenAI);
        var service = ServiceWith(handler);

        await service.TestConnectionAsync(provider, apiKeyOverride: "sk-from-form");

        Assert.Equal("Bearer sk-from-form",
            handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task TestConnection_WhenAnthropic_SendsApiKeyAndVersionHeaders()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var provider = await SavedProviderAsync(ProviderType.Anthropic);
        var service = ServiceWith(handler);

        await service.TestConnectionAsync(provider, apiKeyOverride: "sk-ant");

        Assert.Equal("sk-ant", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());
    }

    [Fact]
    public async Task TestConnection_WhenNoOverride_DecryptsSavedKeyForRequest()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var provider = await SavedProviderAsync(); // key đã lưu = "sk-saved"
        var service = ServiceWith(handler);

        await service.TestConnectionAsync(provider, apiKeyOverride: null);

        Assert.Equal("Bearer sk-saved",
            handler.LastRequest!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task TestConnection_WhenNotSaved_DoesNotPersist()
    {
        // Provider chưa lưu (Id == 0) — result trả về nhưng không ghi DB
        var unsaved = new Provider
        {
            Name = "New",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
            MaxConcurrent = 4,
        };
        var service = ServiceWith(new FakeHandler(HttpStatusCode.OK));

        var result = await service.TestConnectionAsync(unsaved, "sk-new");

        Assert.True(result.Success);
        using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.Providers.CountAsync());
    }
}
```

- [ ] **Step 2: Chạy test — kỳ vọng FAIL (RED)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: **FAIL** — `CS0246: 'ProviderTestResult'` / `CS1061: 'ProviderService' does not contain a definition for 'TestConnectionAsync'` (và ctor 4 tham số chưa tồn tại).

- [ ] **Step 3: Viết implementation**

Tạo `src/RouterBalancing.Core/Providers/ProviderTestResult.cs`:

```csharp
namespace RouterBalancing.Core.Providers;

/// <summary>Kết quả test connection gần nhất — không chứa key.</summary>
/// <param name="Success">HTTP 2xx hay không.</param>
/// <param name="Message">Lý do fail (status/mô tả exception); <see langword="null"/> khi success.</param>
/// <param name="At">Thời điểm test (UTC).</param>
public sealed record ProviderTestResult(bool Success, string? Message, DateTimeOffset At);
```

Tạo `src/RouterBalancing.Core/Providers/ProviderRequestFactory.cs`:

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Tạo request GET tới provider — dùng chung cho test connection, fetch models
/// và metadata endpoint để header theo Type chỉ viết 1 chỗ (DRY).
/// </summary>
public static class ProviderRequestFactory
{
    /// <summary>Tên named HttpClient — timeout 10s, đăng ký trong MauiProgram.</summary>
    public const string HttpClientName = "provider-probe";

    /// <summary>
    /// Tạo GET request {BaseUrl}{path}. Header theo Type:
    /// OpenAI → <c>Authorization: Bearer</c>; Anthropic → <c>x-api-key</c> + <c>anthropic-version</c>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Khi Type ngoài 2 giá trị đã biết.</exception>
    public static HttpRequestMessage Create(Provider provider, string apiKey, string? path = null)
    {
        var url = provider.BaseUrl.TrimEnd('/') + (path ?? "/v1/models");
        var request = new HttpRequestMessage(HttpMethod.Get, url);

        switch (provider.Type)
        {
            case ProviderType.OpenAI:
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
                break;
            case ProviderType.Anthropic:
                request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(provider.Type), provider.Type, "Unsupported provider type.");
        }

        return request;
    }
}
```

Sửa `IProviderService.cs` — thêm vào cuối interface (giữ XML doc):

```csharp
    /// <summary>
    /// Test kết nối: GET {BaseUrl}/v1/models với key override (form) hoặc key đã lưu.
    /// Provider đã lưu (Id != 0) → persist LastTestSuccess/At/Message.
    /// </summary>
    Task<ProviderTestResult> TestConnectionAsync(Provider provider, string? apiKeyOverride, CancellationToken ct = default);
```

Sửa `ProviderService.cs`:

1. Thêm fields + ctor (đổi signature — mọi caller/test phải cập nhật):

```csharp
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly ILogService _log;

    public ProviderService(
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
```

2. Thêm usings: `using RouterBalancing.Core.Logging;` (và giữ usings còn lại).

3. Thêm method:

```csharp
    public async Task<ProviderTestResult> TestConnectionAsync(
        Provider provider, string? apiKeyOverride, CancellationToken ct = default)
    {
        var at = DateTimeOffset.UtcNow;
        ProviderTestResult result;
        try
        {
            // Override (key đang gõ trên form) ưu tiên; không có → giải mã key đã lưu.
            // Decrypt PHẢI nằm trong try: key DPAPI hỏng (CryptographicException) rơi vào
            // catch → fail với lý do, không ném ra UI (sửa theo review Task 3 — nếu để
            // ngoài try thì CryptographicException trong catch filter là dead code).
            var key = apiKeyOverride;
            if (string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(provider.ApiKeyEncrypted))
            {
                key = _protector.Unprotect(provider.ApiKeyEncrypted);
            }

            using var request = ProviderRequestFactory.Create(provider, key ?? string.Empty);
            using var response = await _http.CreateClient(ProviderRequestFactory.HttpClientName)
                .SendAsync(request, ct);

            result = response.IsSuccessStatusCode
                ? new ProviderTestResult(true, null, at)
                : new ProviderTestResult(false, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", at);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            // Timeout/Socket error/URL sai/key DPAPI hỏng → fail với lý do, không ném ra UI
            result = new ProviderTestResult(false, ex.Message, at);
            _log.Warn($"Test connection failed: {ex.Message}");
        }

        // Id == 0 = bản nháp chưa lưu — không có hàng để ghi LastTest*
        if (provider.Id != 0)
        {
            await PersistTestResultAsync(provider.Id, result, ct);
        }

        return result;
    }

    private async Task PersistTestResultAsync(long id, ProviderTestResult result, CancellationToken ct)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (provider is null) return;
        provider.LastTestSuccess = result.Success;
        provider.LastTestAt = result.At;
        provider.LastTestMessage = result.Message;
        await db.SaveChangesAsync(ct);
    }
```

Sửa `router balancing test/Providers/ProviderServiceTests.cs` — cập nhật ctor trong test class:

```csharp
    public ProviderServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
        // Task 3 mở rộng ctor — test CRUD dùng StubFactory handler không bao giờ được gọi
        _service = new ProviderService(_db, _protector, new NeverHttpFactory(), new NullLog());
    }

    /// <summary>HttpClientFactory ném nếu bị gọi — CRUD không được đụng network.</summary>
    private sealed class NeverHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            throw new InvalidOperationException("CRUD must not perform HTTP calls.");
    }
```

Và thêm `using RouterBalancing.Core.Logging;` + inner classes `NullLog` (copy y hệt từ `ProviderTestConnectionTests` — hoặc nếu trùng lặp quá, tạo file chung `router balancing test/TestDoubles.cs` chứa `NullLog` internal; implementer chọn cách gọn nhất, ưu tiên dùng chung 1 chỗ).

- [ ] **Step 4: Chạy test — kỳ vọng PASS (GREEN)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed!` — tổng **79** (72 + 7 mới).

- [ ] **Step 5: Build app**

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: `0 Warning(s)`, `0 Error(s)`.

- [ ] **Step 6: Commit**

```bash
git add src/RouterBalancing.Core/Providers/ "router balancing test/Providers/"
git commit -m "feat: add provider test connection with typed auth headers"
```

---

### Task 4: IModelService (TDD)

**Files:**
- Create: `src/RouterBalancing.Core/Providers/IModelService.cs`
- Create: `src/RouterBalancing.Core/Providers/ModelService.cs`
- Test: `router balancing test/Providers/ModelServiceTests.cs`

**Interfaces:**
- Consumes: Task 3 (`ProviderRequestFactory.Create`, `ProviderRequestFactory.HttpClientName`), Phase 1 (`IDbContextFactory`, `ISecretProtector`, `IHttpClientFactory`, `ILogService`, entities).
- Produces (ns `RouterBalancing.Core.Providers`):

```csharp
public interface IModelService
{
    Task<(int Added, int Skipped)> FetchFromProviderAsync(long providerId, CancellationToken ct = default);
    Task<Model> AddManualAsync(long providerId, string modelId, CancellationToken ct = default);
    Task<(int Added, int Skipped)> AddBulkAsync(long providerId, IReadOnlyList<string> modelIds, CancellationToken ct = default);
    Task RemoveAsync(long modelId, CancellationToken ct = default);
    Task<int> RemoveAllAsync(long providerId, CancellationToken ct = default);
    Task SetEnabledAsync(long modelId, bool enabled, CancellationToken ct = default);
    Task SetAllEnabledAsync(long providerId, bool enabled, CancellationToken ct = default);
}
```

- [ ] **Step 1: Viết failing test**

Tạo `router balancing test/Providers/ModelServiceTests.cs`:

```csharp
using System.Net;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ModelServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector = new DpapiSecretProtector();

    public ModelServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
    }

    public void Dispose() => _testDb.Dispose();

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    // NullLog: dùng lại từ `router balancing test/TestDoubles.cs` (Task 3) — KHÔNG khai
    // báo inner class (bản inline thiếu Write/Count → CS0535 với ILogService thật).

    private ModelService ServiceWith(string json) =>
        new(_db, _protector, new StubFactory(new JsonHandler(json)), new NullLog());

    private async Task<long> SeedProviderAsync(params string[] existingModels)
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider
        {
            Name = "P",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.example.com",
            ApiKeyEncrypted = _protector.Protect("sk-saved"),
        };
        foreach (var id in existingModels)
        {
            provider.Models.Add(new Model { ModelId = id });
        }
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Id;
    }

    [Fact]
    public async Task FetchFromProvider_WhenOpenAiShape_AddsNewModels_SkipsExisting()
    {
        var providerId = await SeedProviderAsync("gpt-4o");
        var service = ServiceWith("""
            {"data":[{"id":"gpt-4o"},{"id":"gpt-4o-mini"},{"id":"gpt-4o"}]}
            """);

        var (added, skipped) = await service.FetchFromProviderAsync(providerId);

        // Payload cố ý chứa bản sao gpt-4o: unique index (ProviderId, ModelId) chặn
        // duplicate → service phải skip cả bản sao trong payload (spec §4: "skip thay vì
        // throw raw"), không được thêm lần 2.
        Assert.Equal(1, added);   // chỉ gpt-4o-mini là mới
        Assert.Equal(2, skipped); // gpt-4o đã có + bản sao gpt-4o trong payload (dedupe)

        using var db = _db.CreateDbContext();
        var models = await db.Models.Where(m => m.ProviderId == providerId).ToListAsync();
        Assert.Equal(2, models.Count);
        Assert.All(models, m => Assert.False(m.IsManual)); // auto-fetch
    }

    [Fact]
    public async Task FetchFromProvider_WhenAnthropicShape_ParsesIds()
    {
        var providerId = await SeedProviderAsync();
        var service = ServiceWith("""
            {"data":[{"type":"model","id":"claude-3-5-sonnet-20241022","display_name":"Claude 3.5 Sonnet","created_at":"2024-10-22"}]}
            """);

        var (added, skipped) = await service.FetchFromProviderAsync(providerId);

        Assert.Equal(1, added);
        Assert.Equal(0, skipped);

        using var db = _db.CreateDbContext();
        var model = await db.Models.SingleAsync(m => m.ProviderId == providerId);
        Assert.Equal("claude-3-5-sonnet-20241022", model.ModelId);
    }

    [Fact]
    public async Task FetchFromProvider_WhenProviderUnknown_ThrowsKeyNotFound()
    {
        var service = ServiceWith("""{"data":[]}""");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.FetchFromProviderAsync(999));
    }

    [Fact]
    public async Task AddManual_WhenNew_PersistsWithIsManualFlag()
    {
        var providerId = await SeedProviderAsync();
        var service = ServiceWith("""{"data":[]}""");

        var model = await service.AddManualAsync(providerId, "my-model");

        Assert.True(model.IsManual);
        using var db = _db.CreateDbContext();
        var saved = await db.Models.SingleAsync(m => m.Id == model.Id);
        Assert.Equal("my-model", saved.ModelId);
    }

    [Fact]
    public async Task AddManual_WhenDuplicate_ThrowsInvalidOperation()
    {
        var providerId = await SeedProviderAsync("gpt-4o");
        var service = ServiceWith("""{"data":[]}""");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddManualAsync(providerId, "gpt-4o"));
    }

    [Fact]
    public async Task AddBulk_WhenMixedPayload_TrimsDedupsAndSkipsExisting()
    {
        var providerId = await SeedProviderAsync("gpt-4o");
        var service = ServiceWith("""{"data":[]}""");

        var (added, skipped) = await service.AddBulkAsync(providerId,
        [
            "  deepseek-chat  ",   // mới (sau trim)
            "gpt-4o",              // đã có
            "deepseek-chat",       // duplicate trong payload
            "",                    // dòng rỗng — bỏ
            "   ",                 // whitespace — bỏ
        ]);

        Assert.Equal(1, added);
        Assert.Equal(2, skipped);

        using var db = _db.CreateDbContext();
        var model = await db.Models.SingleAsync(m => m.ModelId == "deepseek-chat");
        Assert.True(model.IsManual);
    }

    [Fact]
    public async Task Remove_WhenCalled_DeletesOnlyThatModel()
    {
        var providerId = await SeedProviderAsync("a", "b");
        var service = ServiceWith("""{"data":[]}""");
        using (var db = _db.CreateDbContext())
        {
            var target = await db.Models.SingleAsync(m => m.ModelId == "a");
            await service.RemoveAsync(target.Id);
        }

        using var db2 = _db.CreateDbContext();
        Assert.False(await db2.Models.AnyAsync(m => m.ModelId == "a"));
        Assert.True(await db2.Models.AnyAsync(m => m.ModelId == "b"));
    }

    [Fact]
    public async Task RemoveAll_WhenCalled_ReturnsRemovedCount()
    {
        var providerId = await SeedProviderAsync("a", "b", "c");
        var service = ServiceWith("""{"data":[]}""");

        var removed = await service.RemoveAllAsync(providerId);

        Assert.Equal(3, removed);
        using var db = _db.CreateDbContext();
        Assert.Equal(0, await db.Models.CountAsync(m => m.ProviderId == providerId));
    }

    [Fact]
    public async Task SetAllEnabled_WhenToggled_UpdatesEveryModel()
    {
        var providerId = await SeedProviderAsync("a", "b");
        var service = ServiceWith("""{"data":[]}""");

        await service.SetAllEnabledAsync(providerId, false);

        using var db = _db.CreateDbContext();
        var models = await db.Models.Where(m => m.ProviderId == providerId).ToListAsync();
        Assert.Equal(2, models.Count);
        Assert.All(models, m => Assert.False(m.Enabled));
    }

    [Fact]
    public async Task SetEnabled_WhenToggled_PersistsSingleModel()
    {
        var providerId = await SeedProviderAsync("a");
        var service = ServiceWith("""{"data":[]}""");

        using (var db = _db.CreateDbContext())
        {
            var model = await db.Models.SingleAsync(m => m.ModelId == "a");
            await service.SetEnabledAsync(model.Id, false);
        }

        using var db2 = _db.CreateDbContext();
        Assert.False((await db2.Models.SingleAsync(m => m.ModelId == "a")).Enabled);
    }
}
```

- [ ] **Step 2: Chạy test — kỳ vọng FAIL (RED)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: **FAIL** — `CS0246: 'ModelService'`.

- [ ] **Step 3: Viết implementation**

Tạo `src/RouterBalancing.Core/Providers/IModelService.cs`:

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>Quản lý models trong một provider — fetch tự động, thêm tay, toggle, xóa.</summary>
public interface IModelService
{
    /// <summary>GET /v1/models rồi thêm ids thiếu; trả (số thêm, số bỏ qua).</summary>
    /// <exception cref="KeyNotFoundException">Khi provider không tồn tại.</exception>
    /// <exception cref="HttpRequestException">Khi HTTP không 2xx (UI hiện toast).</exception>
    Task<(int Added, int Skipped)> FetchFromProviderAsync(long providerId, CancellationToken ct = default);

    /// <summary>Thêm 1 model thủ công (IsManual = true).</summary>
    /// <exception cref="InvalidOperationException">Khi modelId đã tồn tại trong provider.</exception>
    Task<Model> AddManualAsync(long providerId, string modelId, CancellationToken ct = default);

    /// <summary>Thêm nhiều model (mỗi phần tử 1 id) — trim, bỏ rỗng, dedupe; trả (số thêm, số bỏ qua).</summary>
    Task<(int Added, int Skipped)> AddBulkAsync(long providerId, IReadOnlyList<string> modelIds, CancellationToken ct = default);

    /// <summary>Xóa 1 model.</summary>
    Task RemoveAsync(long modelId, CancellationToken ct = default);

    /// <summary>Xóa hết models của provider; trả số dòng đã xóa.</summary>
    Task<int> RemoveAllAsync(long providerId, CancellationToken ct = default);

    /// <summary>Bật/tắt 1 model.</summary>
    Task SetEnabledAsync(long modelId, bool enabled, CancellationToken ct = default);

    /// <summary>Bật/tắt toàn bộ models của provider.</summary>
    Task SetAllEnabledAsync(long providerId, bool enabled, CancellationToken ct = default);
}
```

Tạo `src/RouterBalancing.Core/Providers/ModelService.cs`:

```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Providers;

/// <inheritdoc cref="IModelService"/>
public sealed class ModelService : IModelService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ISecretProtector _protector;
    private readonly IHttpClientFactory _http;
    private readonly ILogService _log;

    public ModelService(
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

    public async Task<(int Added, int Skipped)> FetchFromProviderAsync(long providerId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var provider = await db.Providers
            .Include(p => p.Models)
            .FirstOrDefaultAsync(p => p.Id == providerId, ct)
            ?? throw new KeyNotFoundException($"Provider {providerId} not found.");

        var key = string.IsNullOrEmpty(provider.ApiKeyEncrypted)
            ? string.Empty
            : _protector.Unprotect(provider.ApiKeyEncrypted);

        using var request = ProviderRequestFactory.Create(provider, key);
        using var response = await _http.CreateClient(ProviderRequestFactory.HttpClientName)
            .SendAsync(request, ct);
        response.EnsureSuccessStatusCode(); // ném HttpRequestException → UI toast

        // Cả OpenAI lẫn Anthropic đều trả {"data":[{"id":...}]} — parse chung 1 shape;
        // field khác (type/display_name) bỏ qua, chỉ lấy id.
        using var json = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

        var existing = provider.Models.Select(m => m.ModelId).ToHashSet(StringComparer.Ordinal);
        int added = 0, skipped = 0;

        if (json.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl)) continue;
                var modelId = idEl.GetString();
                if (string.IsNullOrWhiteSpace(modelId)) continue;
                if (!existing.Add(modelId)) { skipped++; continue; }

                db.Models.Add(new Model { ProviderId = providerId, ModelId = modelId, IsManual = false });
                added++;
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            _log.Info($"Fetched {added} models for provider {providerId}.");
        }

        return (added, skipped);
    }

    public async Task<Model> AddManualAsync(long providerId, string modelId, CancellationToken ct = default)
    {
        var trimmed = modelId.Trim();
        ArgumentException.ThrowIfNullOrWhiteSpace(trimmed);

        using var db = _db.CreateDbContext();
        if (!await db.Providers.AnyAsync(p => p.Id == providerId, ct))
        {
            throw new KeyNotFoundException($"Provider {providerId} not found.");
        }
        if (await db.Models.AnyAsync(m => m.ProviderId == providerId && m.ModelId == trimmed, ct))
        {
            throw new InvalidOperationException($"Model '{trimmed}' already exists in provider {providerId}.");
        }

        var model = new Model { ProviderId = providerId, ModelId = trimmed, IsManual = true };
        db.Models.Add(model);
        await db.SaveChangesAsync(ct);
        return model;
    }

    public async Task<(int Added, int Skipped)> AddBulkAsync(
        long providerId, IReadOnlyList<string> modelIds, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        if (!await db.Providers.AnyAsync(p => p.Id == providerId, ct))
        {
            throw new KeyNotFoundException($"Provider {providerId} not found.");
        }

        var existing = (await db.Models
            .Where(m => m.ProviderId == providerId)
            .Select(m => m.ModelId)
            .ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

        int added = 0, skipped = 0;
        foreach (var raw in modelIds)
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0) continue; // dòng rỗng — không tính skipped
            if (!existing.Add(trimmed)) { skipped++; continue; }

            db.Models.Add(new Model { ProviderId = providerId, ModelId = trimmed, IsManual = true });
            added++;
        }

        if (added > 0) await db.SaveChangesAsync(ct);
        return (added, skipped);
    }

    public async Task RemoveAsync(long modelId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var model = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct)
            ?? throw new KeyNotFoundException($"Model {modelId} not found.");
        db.Models.Remove(model);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> RemoveAllAsync(long providerId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var models = await db.Models.Where(m => m.ProviderId == providerId).ToListAsync(ct);
        db.Models.RemoveRange(models);
        await db.SaveChangesAsync(ct);
        return models.Count;
    }

    public async Task SetEnabledAsync(long modelId, bool enabled, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var model = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct)
            ?? throw new KeyNotFoundException($"Model {modelId} not found.");
        model.Enabled = enabled;
        await db.SaveChangesAsync(ct);
    }

    public async Task SetAllEnabledAsync(long providerId, bool enabled, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var models = await db.Models.Where(m => m.ProviderId == providerId).ToListAsync(ct);
        foreach (var model in models)
        {
            model.Enabled = enabled;
        }
        await db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 4: Chạy test — kỳ vọng PASS (GREEN)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed!` — tổng **89** (79 + 10 mới).

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Providers/ "router balancing test/Providers/"
git commit -m "feat: add model service with fetch manual bulk and toggles"
```

---

### Task 5: Metadata chain providers (TDD)

**Files:**
- Create: `src/RouterBalancing.Core/Providers/ModelMetadata.cs`
- Create: `src/RouterBalancing.Core/Providers/IModelMetadataProvider.cs`
- Create: `src/RouterBalancing.Core/Providers/ProviderEndpointMetadataProvider.cs`
- Create: `src/RouterBalancing.Core/Providers/StaticCatalogMetadataProvider.cs`
- Test: `router balancing test/Providers/StaticCatalogMetadataProviderTests.cs`
- Test: `router balancing test/Providers/ProviderEndpointMetadataProviderTests.cs`

**Interfaces:**
- Consumes: Task 3 (`ProviderRequestFactory`), Phase 1 (`IHttpClientFactory`, entities `Provider`/`Model`).
- Produces:

```csharp
public sealed record ModelMetadata(
    int? ContextWindow,
    bool? SupportsVision,
    bool? SupportsThink,
    string? ThinkEfforts,      // JSON array hoặc null
    string? InputModalities,   // JSON array hoặc null
    string? OutputModalities); // JSON array hoặc null

public interface IModelMetadataProvider
{
    /// Trả null nếu provider này không biết metadata cho model (chain nhường bước sau).
    Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default);
}
```

- [ ] **Step 1: Viết failing tests**

Tạo `router balancing test/Providers/StaticCatalogMetadataProviderTests.cs`:

```csharp
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class StaticCatalogMetadataProviderTests
{
    private static (Provider Provider, Model Model) Pair(string modelId)
    {
        var provider = new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com" };
        return (provider, new Model { ProviderId = 1, ModelId = modelId });
    }

    [Fact]
    public async Task FetchAsync_WhenGptModel_ReturnsCatalogMetadata()
    {
        var (provider, model) = Pair("gpt-4o-mini");
        var catalog = new StaticCatalogMetadataProvider();

        var meta = await catalog.FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.Equal(128_000, meta.ContextWindow);
        Assert.True(meta.SupportsVision);
        Assert.False(meta.SupportsThink);
    }

    [Fact]
    public async Task FetchAsync_WhenClaudeModel_ReturnsVisionAndThink()
    {
        var (provider, model) = Pair("claude-3-7-sonnet-20250219");
        var catalog = new StaticCatalogMetadataProvider();

        var meta = await catalog.FetchAsync(provider, model);

        Assert.NotNull(meta);
        Assert.Equal(200_000, meta.ContextWindow);
        Assert.True(meta.SupportsThink);
        Assert.NotNull(meta.ThinkEfforts);
    }

    [Fact]
    public async Task FetchAsync_WhenUnknownModel_ReturnsNull()
    {
        var (provider, model) = Pair("some-internal-model-v2");
        var catalog = new StaticCatalogMetadataProvider();

        Assert.Null(await catalog.FetchAsync(provider, model));
    }
}
```

Tạo `router balancing test/Providers/ProviderEndpointMetadataProviderTests.cs`:

```csharp
using System.Net;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class ProviderEndpointMetadataProviderTests
{
    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("boom");
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (Provider Provider, Model Model) Pair() =>
        (new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com" },
         new Model { ProviderId = 1, ModelId = "gpt-4o" });

    [Fact]
    public async Task FetchAsync_WhenEndpointReturnsContextWindow_ParsesMetadata()
    {
        var handler = new JsonHandler("""
            {"id":"gpt-4o","context_window":128000,
             "supported_modalities":{"input":["text","image"],"output":["text"]}}
            """);
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler));
        var (p, m) = Pair();

        var meta = await provider.FetchAsync(p, m);

        Assert.NotNull(meta);
        Assert.Equal(128_000, meta.ContextWindow);
        Assert.True(meta.SupportsVision);
        Assert.Equal("""["text","image"]""", meta.InputModalities);
        // Request đúng path {base}/v1/models/{id}
        Assert.Equal("https://api.example.com/v1/models/gpt-4o", handler.LastUri!.ToString());
    }

    [Fact]
    public async Task FetchAsync_WhenEndpointThrows_ReturnsNull()
    {
        var provider = new ProviderEndpointMetadataProvider(new StubFactory(new ThrowingHandler()));
        var (p, m) = Pair();

        Assert.Null(await provider.FetchAsync(p, m));
    }

    [Fact]
    public async Task FetchAsync_WhenShapeUnrecognized_ReturnsNull()
    {
        var provider = new ProviderEndpointMetadataProvider(
            new StubFactory(new JsonHandler("""{"foo":"bar"}""")));
        var (p, m) = Pair();

        Assert.Null(await provider.FetchAsync(p, m));
    }
}
```

- [ ] **Step 2: Chạy test — kỳ vọng FAIL (RED)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: **FAIL** — `CS0246: 'StaticCatalogMetadataProvider'` / `'ProviderEndpointMetadataProvider'` / `'ModelMetadata'`.

- [ ] **Step 3: Viết implementation**

Tạo `src/RouterBalancing.Core/Providers/ModelMetadata.cs`:

```csharp
namespace RouterBalancing.Core.Providers;

/// <summary>
/// Khả năng của model. Mọi field nullable: provider trả gì biết đó —
/// <see langword="null"/> = không rõ, service giữ nguyên giá trị hiện tại.
/// </summary>
public sealed record ModelMetadata(
    int? ContextWindow,
    bool? SupportsVision,
    bool? SupportsThink,
    string? ThinkEfforts,
    string? InputModalities,
    string? OutputModalities);
```

Tạo `src/RouterBalancing.Core/Providers/IModelMetadataProvider.cs`:

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Một bước trong chain tra metadata model: endpoint của provider → static catalog → null.
/// Implement phải trả <see langword="null"/> (không ném) khi không biết —
/// lỗi mạng/parse được nuốt có log ở tầng service, chain mới tiếp tục được.
/// </summary>
public interface IModelMetadataProvider
{
    /// <summary>Trả metadata hoặc <see langword="null"/> nếu bước này không biết.</summary>
    Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default);
}
```

Tạo `src/RouterBalancing.Core/Providers/ProviderEndpointMetadataProvider.cs`:

```csharp
using System.Text.Json;
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Bước 1: GET {base}/v1/models/{id} — thử parse field mà một số gateway
/// (OpenRouter, Together, v.v.) trả thêm. Shape "sạch" của OpenAI gốc không có
/// capabilities → trả null, nhường static catalog.
/// </summary>
public sealed class ProviderEndpointMetadataProvider(IHttpClientFactory http) : IModelMetadataProvider
{
    public async Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default)
    {
        try
        {
            var key = string.IsNullOrEmpty(provider.ApiKeyEncrypted)
                ? string.Empty
                : new DpapiSecretProtector().Unprotect(provider.ApiKeyEncrypted);
```

> **Lưu ý implementer:** KHÔNG new `DpapiSecretProtector` trong provider — inject `ISecretProtector` qua ctor thay cho dòng trên. Implementation đầy đủ (đã corrected):

```csharp
using System.Text.Json;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Security;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Bước 1: GET {base}/v1/models/{id} — thử parse field mà một số gateway
/// (OpenRouter, Together, v.v.) trả thêm. Shape "sạch" của OpenAI gốc không có
/// capabilities → trả null, nhường static catalog.
/// </summary>
public sealed class ProviderEndpointMetadataProvider : IModelMetadataProvider
{
    private readonly IHttpClientFactory _http;
    private readonly ISecretProtector _protector;

    public ProviderEndpointMetadataProvider(IHttpClientFactory http, ISecretProtector protector)
    {
        _http = http;
        _protector = protector;
    }

    public async Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default)
    {
        try
        {
            var key = string.IsNullOrEmpty(provider.ApiKeyEncrypted)
                ? string.Empty
                : _protector.Unprotect(provider.ApiKeyEncrypted);

            using var request = ProviderRequestFactory.Create(provider, key, $"/v1/models/{model.ModelId}");
            using var response = await _http.CreateClient(ProviderRequestFactory.HttpClientName)
                .SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;

            using var json = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = json.RootElement;

            int? contextWindow = root.TryGetProperty("context_window", out var ctx) && ctx.TryGetInt32(out var ctxVal)
                ? ctxVal
                : null;
            bool? vision = ParseVision(root);
            string? input = ParseModalityList(root, "supported_modalities", "input");
            string? output = ParseModalityList(root, "supported_modalities", "output");

            // Không có field nào quen thuộc → shape lạ, nhường bước sau
            if (contextWindow is null && vision is null && input is null && output is null)
            {
                return null;
            }

            return new ModelMetadata(
                contextWindow,
                vision,
                SupportsThink: null,      // endpoint ít khi trả — catalog lo phần này
                ThinkEfforts: null,
                InputModalities: input,
                OutputModalities: output);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                       or JsonException or InvalidOperationException
                                       or System.Security.Cryptography.CryptographicException)
        {
            // Bước này best-effort — lỗi mạng/parse không được chặn chain
            return null;
        }
    }

    private static bool? ParseVision(JsonElement root)
    {
        if (!root.TryGetProperty("supported_modalities", out var mods)
            || mods.ValueKind != JsonValueKind.Object
            || !mods.TryGetProperty("input", out var input)
            || input.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        foreach (var item in input.EnumerateArray())
        {
            if (string.Equals(item.GetString(), "image", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string? ParseModalityList(JsonElement root, string property, string direction)
    {
        if (!root.TryGetProperty(property, out var mods)
            || mods.ValueKind != JsonValueKind.Object
            || !mods.TryGetProperty(direction, out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var values = list.EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!)
            .ToArray();
        return values.Length == 0 ? null : JsonSerializer.Serialize(values);
    }
}
```

Tạo `src/RouterBalancing.Core/Providers/StaticCatalogMetadataProvider.cs`:

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Providers;

/// <summary>
/// Bước 2: catalog metadata nhúng cho model phổ biến — best-effort, prefix-match.
/// Giá trị là ước lượng công khai tại thời điểm viết; sai số chấp nhận được vì
/// user vẫn thấy badge "manual-edit phase sau" — không chặn functionality.
/// </summary>
public sealed class StaticCatalogMetadataProvider : IModelMetadataProvider
{
    private sealed record Entry(string Prefix, ModelMetadata Meta);

    // Thứ tự quan trọng: prefix dài/đặc thù trước prefix chung chung
    private static readonly Entry[] Catalog =
    [
        new("gpt-4o-mini", new(128_000, SupportsVision: true, SupportsThink: false, null, """["text","image"]""", """["text"]""")),
        new("gpt-4o",     new(128_000, SupportsVision: true, SupportsThink: false, null, """["text","image"]""", """["text"]""")),
        new("gpt-4.1",    new(1_047_576, SupportsVision: true, SupportsThink: false, null, """["text","image"]""", """["text"]""")),
        new("o1",         new(200_000, SupportsVision: true, SupportsThink: true, """["low","medium","high"]""", """["text","image"]""", """["text"]""")),
        new("o3",         new(200_000, SupportsVision: true, SupportsThink: true, """["low","medium","high"]""", """["text","image"]""", """["text"]""")),
        new("claude-3-7-sonnet", new(200_000, SupportsVision: true, SupportsThink: true, """["low","high"]""", """["text","image"]""", """["text"]""")),
        new("claude-3-5",  new(200_000, SupportsVision: true, SupportsThink: false, null, """["text","image"]""", """["text"]""")),
        new("claude-4",    new(200_000, SupportsVision: true, SupportsThink: true, """["low","high"]""", """["text","image"]""", """["text"]""")),
        new("deepseek-reasoner", new(64_000, SupportsVision: false, SupportsThink: true, null, """["text"]""", """["text"]""")),
        new("deepseek-chat",     new(64_000, SupportsVision: false, SupportsThink: false, null, """["text"]""", """["text"]""")),
    ];

    public Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default)
    {
        // OrdinalIgnoreCase: id model thường lowercase nhưng không bắt buộc
        var entry = Catalog.FirstOrDefault(e =>
            model.ModelId.StartsWith(e.Prefix, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(entry?.Meta);
    }
}
```

- [ ] **Step 4: Chạy test — kỳ vọng PASS (GREEN)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed!` — tổng **95** (89 + 6 mới).

- [ ] **Step 5: Commit**

```bash
git add src/RouterBalancing.Core/Providers/ "router balancing test/Providers/"
git commit -m "feat: add model metadata provider chain"
```

---

### Task 6: ModelMetadataService + fill hook + DI wiring

**Files:**
- Create: `src/RouterBalancing.Core/Providers/IModelMetadataService.cs`
- Create: `src/RouterBalancing.Core/Providers/ModelMetadataService.cs`
- Modify: `src/RouterBalancing.Core/Providers/ModelService.cs` (ctor + fill sau khi add)
- Modify: `router-balancing/MauiProgram.cs` (DI + AddHttpClient)
- Modify: `src/RouterBalancing.Core/RouterBalancing.Core.csproj` (package `Microsoft.Extensions.Http` nếu chưa có)
- Modify: `router balancing test/Providers/ModelServiceTests.cs` (update ctor — thêm metadata stub)
- Test: `router balancing test/Providers/ModelMetadataServiceTests.cs`

**Interfaces:**
- Consumes: Task 5 (chain `IModelMetadataProvider`), Task 4 (`ModelService`), Phase 1 (`ILogService`).
- Produces (ns `RouterBalancing.Core.Providers`):
  - `interface IModelMetadataService` — `Task TryFillAsync(long modelId, CancellationToken ct = default)`; ModelService depend vào **interface** (spec §3.4), implementation đăng ký singleton.

- [ ] **Step 1: Viết failing test**

Tạo `router balancing test/Providers/ModelMetadataServiceTests.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Providers;

public class ModelMetadataServiceTests : IDisposable
{
    private readonly TestDb _testDb = new();
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;

    public ModelMetadataServiceTests()
    {
        _db = _testDb.CreateFactory();
        DbInitializer.Initialize(_db);
    }

    public void Dispose() => _testDb.Dispose();

    private sealed class FixedProvider(ModelMetadata? result) : IModelMetadataProvider
    {
        public Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default) =>
            Task.FromResult(result);
    }

    // NullLog: dùng lại từ `router balancing test/TestDoubles.cs` (Task 3) — KHÔNG khai
    // báo inner class (bản inline thiếu Write/Count → CS0535 với ILogService thật).

    private async Task<long> SeedModelAsync()
    {
        using var db = _db.CreateDbContext();
        var provider = new Provider { Name = "P", Type = ProviderType.OpenAI, BaseUrl = "https://api.example.com" };
        provider.Models.Add(new Model { ModelId = "gpt-4o" });
        db.Providers.Add(provider);
        await db.SaveChangesAsync();
        return provider.Models[0].Id;
    }

    [Fact]
    public async Task TryFillAsync_WhenFirstProviderKnows_AppliesAndStops()
    {
        var modelId = await SeedModelAsync();
        var known = new ModelMetadata(128_000, true, false, null, """["text"]""", """["text"]""");
        var service = new ModelMetadataService(_db, new NullLog(),
            [new FixedProvider(known), new FixedProvider(new ModelMetadata(1, null, null, null, null, null))]);

        await service.TryFillAsync(modelId);

        using var db = _db.CreateDbContext();
        var model = await db.Models.SingleAsync(m => m.Id == modelId);
        Assert.Equal(128_000, model.ContextWindow);
        Assert.True(model.SupportsVision);
        // Bước 1 trả non-null → service dừng, không chạy bước 2 (field 1 không bị ghi đè)
        Assert.NotNull(model.InputModalities);
    }

    [Fact]
    public async Task TryFillAsync_WhenFirstReturnsNull_FallsBackToSecond()
    {
        var modelId = await SeedModelAsync();
        var service = new ModelMetadataService(_db, new NullLog(),
            [new FixedProvider(null), new FixedProvider(new ModelMetadata(99_999, null, null, null, null, null))]);

        await service.TryFillAsync(modelId);

        using var db = _db.CreateDbContext();
        var model = await db.Models.SingleAsync(m => m.Id == modelId);
        Assert.Equal(99_999, model.ContextWindow);
    }

    [Fact]
    public async Task TryFillAsync_WhenAllNull_LeavesModelUnchanged()
    {
        var modelId = await SeedModelAsync();
        var service = new ModelMetadataService(_db, new NullLog(), [new FixedProvider(null)]);

        await service.TryFillAsync(modelId);

        using var db = _db.CreateDbContext();
        var model = await db.Models.SingleAsync(m => m.Id == modelId);
        Assert.Null(model.ContextWindow);
        Assert.False(model.SupportsVision);
    }

    [Fact]
    public async Task TryFillAsync_WhenFieldAlreadySet_KeepsExistingValue()
    {
        var modelId = await SeedModelAsync();
        using (var db = _db.CreateDbContext())
        {
            var model = await db.Models.SingleAsync(m => m.Id == modelId);
            model.ContextWindow = 42;
            await db.SaveChangesAsync();
        }
        var service = new ModelMetadataService(_db, new NullLog(),
            [new FixedProvider(new ModelMetadata(128_000, true, null, null, null, null))]);

        await service.TryFillAsync(modelId);

        using var db2 = _db.CreateDbContext();
        var model = await db2.Models.SingleAsync(m => m.Id == modelId);
        Assert.Equal(42, model.ContextWindow); // không ghi đè giá trị đã có (spec §3.3)
        Assert.True(model.SupportsVision);     // false (default) → true vẫn điền được
    }

    [Fact]
    public async Task TryFillAsync_WhenModelUnknown_DoesNothing()
    {
        var service = new ModelMetadataService(_db, new NullLog(), [new FixedProvider(null)]);

        // Không ném — fill là best-effort
        await service.TryFillAsync(999);
    }
}
```

- [ ] **Step 2: Chạy test — kỳ vọng FAIL (RED)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: **FAIL** — `CS0246: 'ModelMetadataService'`.

- [ ] **Step 3: Viết implementation**

Tạo `src/RouterBalancing.Core/Providers/IModelMetadataService.cs`:

```csharp
namespace RouterBalancing.Core.Providers;

/// <summary>Điền metadata cho model sau khi thêm — best-effort, không bao giờ ném ra caller.</summary>
public interface IModelMetadataService
{
    /// <summary>
    /// Chạy chain metadata và ghi vào model (chỉ field còn trống).
    /// Lỗi (mạng, DPAPI, model biến mất) được log warning và nuốt — model đã lưu vẫn OK.
    /// </summary>
    Task TryFillAsync(long modelId, CancellationToken ct = default);
}
```

Tạo `src/RouterBalancing.Core/Providers/ModelMetadataService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Providers;

/// <inheritdoc cref="IModelMetadataService"/>
public sealed class ModelMetadataService : IModelMetadataService
{
    private readonly IDbContextFactory<RouterBalancingDbContext> _db;
    private readonly ILogService _log;
    private readonly IEnumerable<IModelMetadataProvider> _chain;

    public ModelMetadataService(
        IDbContextFactory<RouterBalancingDbContext> db,
        ILogService log,
        IEnumerable<IModelMetadataProvider> chain)
    {
        _db = db;
        _log = log;
        _chain = chain;
    }

    /// <inheritdoc />
    public async Task TryFillAsync(long modelId, CancellationToken ct = default)
    {
        using var db = _db.CreateDbContext();
        var model = await db.Models
            .Include(m => m.Provider)
            .FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (model?.Provider is null) return;

        foreach (var provider in _chain)
        {
            ModelMetadata? meta;
            try
            {
                meta = await provider.FetchAsync(model.Provider, model, ct);
            }
            catch (Exception ex)
            {
                // Contract: bước thường trả null; ném exception là lệch contract →
                // log warning và nhường bước sau (spec §3.3: mọi lỗi → log warning).
                _log.Warn($"Metadata provider {provider.GetType().Name} lỗi cho model {modelId}: {ex.Message}");
                continue;
            }
            if (meta is null) continue;

            // Chỉ ghi field còn trống — không ghi đè giá trị đã có (spec §3.3);
            // bool không nullable trên entity coi false = "chưa rõ" (2A chưa cho sửa tay).
            var changed = false;
            if (meta.ContextWindow is not null && model.ContextWindow is null)
            {
                model.ContextWindow = meta.ContextWindow;
                changed = true;
            }
            if (meta.SupportsVision == true && !model.SupportsVision)
            {
                model.SupportsVision = true;
                changed = true;
            }
            if (meta.SupportsThink == true && !model.SupportsThink)
            {
                model.SupportsThink = true;
                changed = true;
            }
            if (meta.ThinkEfforts is not null && model.ThinkEfforts is null)
            {
                model.ThinkEfforts = meta.ThinkEfforts;
                changed = true;
            }
            if (meta.InputModalities is not null && model.InputModalities is null)
            {
                model.InputModalities = meta.InputModalities;
                changed = true;
            }
            if (meta.OutputModalities is not null && model.OutputModalities is null)
            {
                model.OutputModalities = meta.OutputModalities;
                changed = true;
            }

            if (changed)
            {
                await db.SaveChangesAsync(ct);
            }
            return; // bước đầu biết → dừng chain
        }
    }
}
```

Sửa `src/RouterBalancing.Core/Providers/ModelService.cs`:

1. Thêm field + đổi ctor (tham số thứ 5 — **interface**, không phải class):

```csharp
    private readonly IModelMetadataService _metadata;
```

```csharp
    public ModelService(
        IDbContextFactory<RouterBalancingDbContext> db,
        ISecretProtector protector,
        IHttpClientFactory http,
        ILogService log,
        IModelMetadataService metadata)
    {
        _db = db;
        _protector = protector;
        _http = http;
        _log = log;
        _metadata = metadata;
    }
```

2. Gọi fill cho **đúng models vừa thêm** ở 3 chỗ — nguyên tắc: gom entities vừa `Add` vào list riêng, `SaveChanges` (Id được gán), rồi fill từng cái. KHÔNG query lại theo `IsManual`/`Local` (dễ trúng entity cũ do `Include` đã load vào context).

- `FetchFromProviderAsync`: trong loop `foreach (var item in data.EnumerateArray())`, khai báo **trước loop**:

```csharp
        var created = new List<Model>();
```

thay `db.Models.Add(new Model { ProviderId = providerId, ModelId = modelId, IsManual = false }); added++;` thành:

```csharp
                var newModel = new Model { ProviderId = providerId, ModelId = modelId, IsManual = false };
                db.Models.Add(newModel);
                created.Add(newModel);
                added++;
```

thay khối `if (added > 0) { SaveChanges; log }` thành:

```csharp
        if (created.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var entity in created)
            {
                // Metadata best-effort — lỗi không ảnh hưởng kết quả fetch (đã log bên trong)
                await _metadata.TryFillAsync(entity.Id, ct);
            }
            _log.Info($"Fetched {added} models for provider {providerId}.");
        }
```

- `AddManualAsync`: sau `await db.SaveChangesAsync(ct);` thêm:

```csharp
        await _metadata.TryFillAsync(model.Id, ct);
        return model;
```

- `AddBulkAsync`: khai báo `var created = new List<Model>();` trước loop; trong loop thay `db.Models.Add(...)` thành:

```csharp
                var newModel = new Model { ProviderId = providerId, ModelId = trimmed, IsManual = true };
                db.Models.Add(newModel);
                created.Add(newModel);
                added++;
```

(thay `added++` đang có — giữ nguyên logic đếm `skipped`). Sau loop thay `if (added > 0) await db.SaveChangesAsync(ct);` thành:

```csharp
        if (created.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            foreach (var entity in created)
            {
                await _metadata.TryFillAsync(entity.Id, ct);
            }
        }
```

3. Sửa `router balancing test/Providers/ModelServiceTests.cs`: thay helper `ServiceWith` thành:

```csharp
    private ModelService ServiceWith(string json)
    {
        var metadata = new ModelMetadataService(_db, new NullLog(), [new NoopMetadata()]);
        return new ModelService(_db, _protector, new StubFactory(new JsonHandler(json)), new NullLog(), metadata);
    }

    /// <summary>Catalog stub không biết gì — tests CRUD không quan tâm metadata.</summary>
    private sealed class NoopMetadata : IModelMetadataProvider
    {
        public Task<ModelMetadata?> FetchAsync(Provider provider, Model model, CancellationToken ct = default) =>
            Task.FromResult<ModelMetadata?>(null);
    }
```

(Lớp `NullLog` lấy từ `router balancing test/TestDoubles.cs` (Task 3) — dùng lại, không khai báo inner class mới.)

- [ ] **Step 4: Chạy test — kỳ vọng PASS (GREEN)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed!` — tổng **100** (95 + 5 mới). Lưu ý: `FetchFromProvider_*` và `Add*` của Task 4 vẫn pass với metadata stub (Noop → không điền gì).

- [ ] **Step 5: Đăng ký DI trong `router-balancing/MauiProgram.cs`**

Thêm `using RouterBalancing.Core.Providers;` vào đầu file usings.

Sau `builder.Services.AddSingleton<IProxyHost, ProxyHost>();` thêm:

```csharp
            // Named client cho test connection/fetch models/metadata — timeout 10s (spec §3.1)
            builder.Services.AddHttpClient(ProviderRequestFactory.HttpClientName,
                client => client.Timeout = TimeSpan.FromSeconds(10));
            builder.Services.AddSingleton<IProviderService, ProviderService>();
            builder.Services.AddSingleton<IModelService, ModelService>();
            builder.Services.AddSingleton<IModelMetadataService, ModelMetadataService>();
            // Thứ tự đăng ký = thứ tự chain: endpoint trước, static catalog sau
            builder.Services.AddSingleton<IModelMetadataProvider, ProviderEndpointMetadataProvider>();
            builder.Services.AddSingleton<IModelMetadataProvider, StaticCatalogMetadataProvider>();
```

Kiểm tra package: `grep Microsoft.Extensions.Http src/RouterBalancing.Core/RouterBalancing.Core.csproj` — nếu chưa có, thêm (MAUI app thường đã có transitively qua ASP.NET Components nhưng **Core project tự chứa mới chắc chắn compile**):

```xml
    <PackageReference Include="Microsoft.Extensions.Http" Version="10.0.12" />
```

(Và nếu thêm cho Core thì verify version có trên NuGet; nếu 10.0.12 không tồn tại cho package này thì dùng version 10.0.x gần nhất khớp family và ghi chú trong commit body.)

- [ ] **Step 6: Verify**

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed!` — **100**.

- [ ] **Step 7: Commit**

```bash
git add src/RouterBalancing.Core/ router-balancing/MauiProgram.cs "router balancing test/Providers/"
git commit -m "feat: wire metadata service and provider dependency injection"
```

---

### Task 7: Shared components (Modal, ConfirmDialog, Pager, Badge, EmptyState) + i18n

**Files:**
- Create: `router-balancing/Components/Shared/BadgeVariant.cs`
- Create: `router-balancing/Components/Shared/Modal.razor`
- Create: `router-balancing/Components/Shared/ConfirmDialog.razor`
- Create: `router-balancing/Components/Shared/Pager.razor`
- Create: `router-balancing/Components/Shared/Badge.razor`
- Create: `router-balancing/Components/Shared/EmptyState.razor`
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (keys `modal.*`, `pager.*`, `confirm.*` — EN + VI)
- Modify: `router-balancing/Components/_Imports.razor` (thêm `@using router_balancing.Components.Shared`)

**Interfaces:**
- Consumes: Phase 1 (`LocalizationService`).
- Produces (namespace `router_balancing.Components.Shared`): components tái dùng cho cả Bloc B sau này — params theo spec §4.1: `Modal(Visible, Title, OnClose, ChildContent)`, `ConfirmDialog(Visible, Title, Message, Danger, ConfirmText, CancelText, OnConfirm, OnCancel)`, `Pager(Total, PageSize[+Changed], Page[+Changed], PageSizeOptions)`, `Badge(Text, Variant)`, `EmptyState(Message, Icon?)`.
- Lưu ý: **không unit test cho Razor component** (xUnit chỉ test Core) — verify bằng build + npm build.

- [ ] **Step 1: Bổ sung i18n keys**

Thêm vào cuối `Translations.English` (trước `};` đóng dictionary, đánh dấu comment nhóm):

```csharp
        // Shared components (Phase 2A Task 7)
        ["confirm.cancel"] = "Cancel",
        ["modal.close"] = "Close",
        ["pager.prev"] = "Previous",
        ["pager.next"] = "Next",
        ["pager.page"] = "Page {0}",
        ["pager.of"] = "of",
        ["pager.total"] = "({0} items)",
        ["pager.goto"] = "Go to",
        ["pager.pageSize"] = "Per page",
```

Thêm đúng bộ key đó vào `Translations.Vietnamese` (cùng vị trí cuối dictionary):

```csharp
        // Shared components (Phase 2A Task 7)
        ["confirm.cancel"] = "Hủy",
        ["modal.close"] = "Đóng",
        ["pager.prev"] = "Trước",
        ["pager.next"] = "Sau",
        ["pager.page"] = "Trang {0}",
        ["pager.of"] = "trên",
        ["pager.total"] = "({0} mục)",
        ["pager.goto"] = "Đến trang",
        ["pager.pageSize"] = "Mỗi trang",
```

- [ ] **Step 2: Tạo 5 component + enum**

Tạo `router-balancing/Components/Shared/BadgeVariant.cs`:

```csharp
namespace router_balancing.Components.Shared;

/// <summary>Màu badge — map sang class Tailwind tĩnh trong <c>Badge.razor</c>.</summary>
public enum BadgeVariant
{
    Neutral,
    Success,
    Danger,
    Warning,
    Info,
}
```

Tạo `router-balancing/Components/Shared/Modal.razor`:

```razor
@inject LocalizationService L

@if (Visible)
{
    @* z-50: overlay phải đè lên nav + bảng; backdrop bấm để đóng *@
    <div class="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4"
         tabindex="-1" @onclick="CloseAsync" @onkeydown="OnKeydownAsync">
        <div role="dialog" aria-modal="true" aria-label="@Title"
             class="max-h-[90vh] w-full max-w-lg overflow-y-auto rounded border border-border bg-surface p-4 shadow-xl"
             @onclick:stopPropagation="true">
            <div class="mb-3 flex items-center justify-between gap-4">
                <h2 class="text-base font-semibold">@Title</h2>
                <button type="button" class="btn btn-outline-secondary px-2"
                        aria-label="@L["modal.close"]" @onclick="CloseAsync">&times;</button>
            </div>
            @ChildContent
        </div>
    </div>
}

@code {
    /// <summary>Điều khiển hiển thị — render khi <see langword="true"/>.</summary>
    [Parameter] public bool Visible { get; set; }

    /// <summary>Tiêu đề dialog — text đã i18n ở nơi gọi.</summary>
    [Parameter] public string Title { get; set; } = string.Empty;

    /// <summary>Gọi khi người dùng đóng (Esc / backdrop / nút &times;).</summary>
    [Parameter] public EventCallback OnClose { get; set; }

    [Parameter] public RenderFragment? ChildContent { get; set; }

    private Task CloseAsync() => OnClose.InvokeAsync();

    // Esc đóng: keydown trên overlay bắt được cả khi focus đang ở input bên trong (bubbling)
    private Task OnKeydownAsync(KeyboardEventArgs e) =>
        e.Key == "Escape" ? OnClose.InvokeAsync() : Task.CompletedTask;
}
```

Tạo `router-balancing/Components/Shared/ConfirmDialog.razor`:

```razor
@* Dialog xác nhận — dùng Modal bên trong; nút Cancel có autofocus (mặc định focus Cancel) *@
<Modal Visible="Visible" Title="@Title" OnClose="CancelAsync">
    <p class="mb-4 whitespace-pre-line text-sm">@Message</p>
    <div class="flex justify-end gap-2">
        <button type="button" class="btn btn-outline-secondary" autofocus
                @onclick="CancelAsync">@CancelText</button>
        <button type="button" class="@(Danger ? "btn btn-danger" : "btn btn-primary")"
                @onclick="ConfirmAsync">@ConfirmText</button>
    </div>
</Modal>

@code {
    /// <summary>Điều khiển mở/đóng — khi đóng bằng cách khác (Esc/backdrop) = Cancel.</summary>
    [Parameter] public bool Visible { get; set; }

    [Parameter] public string Title { get; set; } = string.Empty;

    [Parameter] public string Message { get; set; } = string.Empty;

    /// <summary><see langword="true"/> = nút xác nhận màu đỏ (hành động phá hủy).</summary>
    [Parameter] public bool Danger { get; set; }

    /// <summary>Text nút xác nhận — bắt buộc truyền từ nơi gọi (i18n).</summary>
    [Parameter] public string ConfirmText { get; set; } = string.Empty;

    /// <summary>Text nút hủy — bắt buộc truyền từ nơi gọi (i18n, thường <c>L["confirm.cancel"]</c>).</summary>
    [Parameter] public string CancelText { get; set; } = string.Empty;

    [Parameter] public EventCallback OnConfirm { get; set; }

    [Parameter] public EventCallback OnCancel { get; set; }

    private Task ConfirmAsync() => OnConfirm.InvokeAsync();

    private Task CancelAsync() => OnCancel.InvokeAsync();
}
```

Tạo `router-balancing/Components/Shared/Pager.razor`:

```razor
@* Phân trang client-side — state (Page/PageSize) do parent giữ qua @bind để persist qua re-render *@
@inject LocalizationService L

@if (Total > PageSize)
{
    <div class="mt-3 flex flex-wrap items-center gap-3 text-sm">
        <button type="button" class="btn btn-outline-secondary" disabled="@(Page <= 1)" @onclick="PrevAsync">
            @L["pager.prev"]
        </button>

        <span class="opacity-80">
            @string.Format(L["pager.page"], Page) @L["pager.of"] @PageCount — @string.Format(L["pager.total"], Total)
        </span>

        <label class="flex items-center gap-1">
            @L["pager.goto"]
            <input type="number" min="1" max="@PageCount" value="@Page"
                   class="w-16 rounded border border-border bg-surface px-1 py-0.5"
                   @onchange="GotoAsync" />
        </label>

        <label class="flex items-center gap-1">
            @L["pager.pageSize"]
            <select value="@PageSize" class="rounded border border-border bg-surface px-1 py-0.5"
                    @onchange="PageSizeSelectAsync">
                @foreach (var size in PageSizeOptions)
                {
                    <option value="@size">@size</option>
                }
            </select>
        </label>

        <button type="button" class="btn btn-outline-secondary" disabled="@(Page >= PageCount)" @onclick="NextAsync">
            @L["pager.next"]
        </button>
    </div>
}

@code {
    /// <summary>Tổng số dòng.</summary>
    [Parameter] public int Total { get; set; }

    /// <summary>Số dòng mỗi trang — parent giữ state.</summary>
    [Parameter] public int PageSize { get; set; } = 10;

    [Parameter] public EventCallback<int> PageSizeChanged { get; set; }

    /// <summary>Trang hiện tại, 1-based — parent giữ state.</summary>
    [Parameter] public int Page { get; set; } = 1;

    [Parameter] public EventCallback<int> PageChanged { get; set; }

    [Parameter] public int[] PageSizeOptions { get; set; } = [10, 25, 50, 100];

    private int PageCount => Math.Max(1, (Total + PageSize - 1) / PageSize);

    private Task PrevAsync() => PageChanged.InvokeAsync(Page - 1);

    private Task NextAsync() => PageChanged.InvokeAsync(Page + 1);

    private Task GotoAsync(ChangeEventArgs e)
    {
        if (!int.TryParse(e.Value?.ToString(), out var target)) return Task.CompletedTask;
        return PageChanged.InvokeAsync(Math.Clamp(target, 1, PageCount));
    }

    private async Task PageSizeSelectAsync(ChangeEventArgs e)
    {
        if (!int.TryParse(e.Value?.ToString(), out var size)) return;
        await PageSizeChanged.InvokeAsync(size);
        await PageChanged.InvokeAsync(1); // đổi page size → về trang 1 tránh trang trống
    }
}
```

Tạo `router-balancing/Components/Shared/Badge.razor`:

```razor
<span class="inline-flex items-center rounded px-1.5 py-0.5 text-xs @ClassFor(Variant)">@Text</span>

@code {
    /// <summary>Nội dung badge — text đã i18n ở nơi gọi (trừ khi là dữ liệu như "ctx: 128k").</summary>
    [Parameter] public string Text { get; set; } = string.Empty;

    [Parameter] public BadgeVariant Variant { get; set; } = BadgeVariant.Neutral;

    /// <summary>
    /// Map tĩnh từng variant → chuỗi class đầy đủ: Tailwind phải scan thấy literal
    /// trong file (không nối chuỗi động thì class mới được sinh vào CSS).
    /// </summary>
    private static string ClassFor(BadgeVariant variant) => variant switch
    {
        BadgeVariant.Success => "bg-success text-light",
        BadgeVariant.Danger => "bg-danger text-light",
        BadgeVariant.Warning => "bg-warning text-dark",
        BadgeVariant.Info => "bg-info text-light",
        _ => "bg-secondary text-light",
    };
}
```

Tạo `router-balancing/Components/Shared/EmptyState.razor`:

```razor
<div class="flex flex-col items-center gap-2 py-6 text-center text-sm text-secondary">
    @if (!string.IsNullOrEmpty(Icon))
    {
        <span class="text-2xl" aria-hidden="true">@Icon</span>
    }
    <p>@Message</p>
</div>

@code {
    /// <summary>Cần hiển thị — text đã i18n ở nơi gọi.</summary>
    [Parameter] public string Message { get; set; } = string.Empty;

    /// <summary>Emoji trang trí tùy chọn (không bắt buộc).</summary>
    [Parameter] public string? Icon { get; set; }
}
```

- [ ] **Step 3: Cập nhật `_Imports.razor`**

Thêm dòng sau `@using router_balancing.Components.Layout`:

```razor
@using router_balancing.Components.Shared
```

- [ ] **Step 4: Verify**

Run (workdir `router-balancing/vite-project`): `npm run build`
Expected: exit 0 (chưa có class mới bắt buộc — components này chỉ dùng token đã có; chạy để chắc chắn config không vỡ).

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed!` — **100** (không thêm test mới; chỉ verify không phá gì).

- [ ] **Step 5: Commit**

```bash
git add router-balancing/Components/ src/RouterBalancing.Core/Localization/Translations.cs
git commit -m "feat: add shared modal confirm pager badge and empty-state components"
```

---

### Task 8: Page `/providers` — danh sách provider + model panel inline

**Files:**
- Create: `router-balancing/Components/Pages/Providers.razor`
- Modify: `router-balancing/Components/Layout/NavMenu.razor` (NavLink Providers — kế tục pattern có sẵn)
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (keys `providers.*`, `models.*` — EN + VI)
- Modify: `router-balancing/wwwroot/build/` (npm build artifacts)

**Interfaces:**
- Consumes: Task 2 (`IProviderService`), Task 3 (`TestConnectionAsync`), Task 4 (`IModelService`), Task 7 (shared components), Phase 1 (`LocalizationService`, `ToastService`, `ILogService`).
- Produces: `Providers.razor` (`@page "/providers"`) — bảng provider (7 cột, expand row), model panel inline (add/bulk/fetch/toggle/remove + badges metadata), ConfirmDialog xóa. **Chưa có** Add/Edit modal (Task 9).
- Lưu ý: namespace Razor mặc định theo thư mục → component thuộc `router_balancing.Components.Pages`; dùng component Shared không cần `@using` (Task 7 đã thêm vào `_Imports`).

- [ ] **Step 1: Bổ sung i18n keys — `Translations.English`**

```csharp
        // Providers page (Phase 2A Task 8)
        ["nav.providers"] = "Providers",
        ["panel.providers.title"] = "Providers",
        ["providers.col.name"] = "Name",
        ["providers.col.baseUrl"] = "Base URL",
        ["providers.col.models"] = "Models",
        ["providers.col.enabled"] = "Enabled",
        ["providers.col.lastTest"] = "Last test",
        ["providers.col.actions"] = "Actions",
        ["providers.empty"] = "No providers yet.",
        ["providers.type.openai"] = "OpenAI",
        ["providers.type.anthropic"] = "Anthropic",
        ["providers.action.test"] = "Test",
        ["providers.testing"] = "Testing...",
        ["providers.action.delete"] = "Delete",
        ["providers.lastTest.never"] = "Not tested",
        ["providers.lastTest.ok"] = "Passed",
        ["providers.lastTest.fail"] = "Failed",
        ["providers.msg.testPassed"] = "Connection succeeded.",
        ["providers.msg.testFailed"] = "Connection failed: {0}",
        ["providers.confirmDelete"] = "Delete provider \"{0}\"? All {1} models will also be removed (cannot undo).",
        ["providers.deleted"] = "Provider deleted.",
        ["models.section"] = "Models",
        ["models.count"] = "{0}/{1}",
        ["models.col.id"] = "Model",
        ["models.col.metadata"] = "Capabilities",
        ["models.col.enabled"] = "Enabled",
        ["models.col.actions"] = "Actions",
        ["models.addPlaceholder"] = "model id...",
        ["models.bulkPlaceholder"] = "One model id per line...",
        ["models.action.add"] = "Add",
        ["models.action.bulk"] = "Bulk",
        ["models.action.fetch"] = "Fetch",
        ["models.fetching"] = "Fetching...",
        ["models.enableAll"] = "Enable all",
        ["models.disableAll"] = "Disable all",
        ["models.deleteAll"] = "Remove all",
        ["models.empty"] = "No models yet.",
        ["models.manual"] = "manual",
        ["models.badge.vision"] = "vision",
        ["models.badge.think"] = "think",
        ["models.result.added"] = "Added {0}, skipped {1}.",
        ["models.fetchFailed"] = "Fetch failed: {0}",
        ["models.saved"] = "Model added.",
        ["models.deleted"] = "Model removed.",
        ["models.removedAll"] = "Removed {0} models.",
        ["models.confirmDelete"] = "Remove model \"{0}\"?",
        ["models.confirmDeleteAll"] = "Remove all {0} models of this provider? This cannot be undone.",
        ["models.error.duplicate"] = "Model already exists.",
```

- [ ] **Step 2: Bổ sung i18n keys — `Translations.Vietnamese` (cùng bộ key)**

```csharp
        // Providers page (Phase 2A Task 8)
        ["nav.providers"] = "Nhà cung cấp",
        ["panel.providers.title"] = "Nhà cung cấp",
        ["providers.col.name"] = "Tên",
        ["providers.col.baseUrl"] = "Base URL",
        ["providers.col.models"] = "Model",
        ["providers.col.enabled"] = "Bật",
        ["providers.col.lastTest"] = "Test gần nhất",
        ["providers.col.actions"] = "Thao tác",
        ["providers.empty"] = "Chưa có nhà cung cấp nào.",
        ["providers.type.openai"] = "OpenAI",
        ["providers.type.anthropic"] = "Anthropic",
        ["providers.action.test"] = "Kiểm tra",
        ["providers.testing"] = "Đang kiểm tra...",
        ["providers.action.delete"] = "Xóa",
        ["providers.lastTest.never"] = "Chưa test",
        ["providers.lastTest.ok"] = "Đạt",
        ["providers.lastTest.fail"] = "Lỗi",
        ["providers.msg.testPassed"] = "Kết nối thành công.",
        ["providers.msg.testFailed"] = "Kết nối thất bại: {0}",
        ["providers.confirmDelete"] = "Xóa nhà cung cấp \"{0}\"? Toàn bộ {1} model cũng sẽ bị xóa (không thể hoàn tác).",
        ["providers.deleted"] = "Đã xóa nhà cung cấp.",
        ["models.section"] = "Model",
        ["models.count"] = "{0}/{1}",
        ["models.col.id"] = "Model",
        ["models.col.metadata"] = "Khả năng",
        ["models.col.enabled"] = "Bật",
        ["models.col.actions"] = "Thao tác",
        ["models.addPlaceholder"] = "model id...",
        ["models.bulkPlaceholder"] = "Mỗi dòng một model id...",
        ["models.action.add"] = "Thêm",
        ["models.action.bulk"] = "Nhiều dòng",
        ["models.action.fetch"] = "Lấy từ provider",
        ["models.fetching"] = "Đang lấy...",
        ["models.enableAll"] = "Bật tất cả",
        ["models.disableAll"] = "Tắt tất cả",
        ["models.deleteAll"] = "Xóa tất cả",
        ["models.empty"] = "Chưa có model nào.",
        ["models.manual"] = "thủ công",
        ["models.badge.vision"] = "hình ảnh",
        ["models.badge.think"] = "suy luận",
        ["models.result.added"] = "Đã thêm {0}, bỏ qua {1}.",
        ["models.fetchFailed"] = "Lấy danh sách thất bại: {0}",
        ["models.saved"] = "Đã thêm model.",
        ["models.deleted"] = "Đã xóa model.",
        ["models.removedAll"] = "Đã xóa {0} model.",
        ["models.confirmDelete"] = "Xóa model \"{0}\"?",
        ["models.confirmDeleteAll"] = "Xóa toàn bộ {0} model của nhà cung cấp này? Không thể hoàn tác.",
        ["models.error.duplicate"] = "Model đã tồn tại.",
```

- [ ] **Step 3: Thêm NavLink trong `NavMenu.razor`**

Chèn giữa NavLink Dashboard và NavLink Logs:

```razor
    <NavLink class="rounded px-3 py-2 text-sm no-underline hover:bg-background"
             href="providers">
        @L["nav.providers"]
    </NavLink>
```

- [ ] **Step 4: Tạo `Providers.razor`**

Tạo `router-balancing/Components/Pages/Providers.razor`:

```razor
@page "/providers"
@using RouterBalancing.Core.Domain
@using RouterBalancing.Core.Providers
@implements IDisposable
@inject IProviderService ProviderSvc
@inject IModelService ModelSvc
@inject LocalizationService L
@inject ToastService Toast
@inject ILogService Log

<h1 class="mb-4 text-xl font-semibold">@L["panel.providers.title"]</h1>

@if (_loading)
{
    <p class="text-sm opacity-70">...</p>
}
else if (_providers.Count == 0)
{
    <EmptyState Icon="&#9881;&#65039;" Message="@L["providers.empty"]" />
}
else
{
    <div class="overflow-x-auto rounded border border-border bg-surface">
        <table class="w-full text-sm">
            <thead>
                <tr class="border-b border-border text-left text-xs uppercase opacity-70">
                    <th class="w-10 px-2 py-2"></th>
                    <th class="px-2 py-2">@L["providers.col.name"]</th>
                    <th class="px-2 py-2">@L["providers.col.baseUrl"]</th>
                    <th class="px-2 py-2">@L["providers.col.models"]</th>
                    <th class="px-2 py-2">@L["providers.col.enabled"]</th>
                    <th class="px-2 py-2">@L["providers.col.lastTest"]</th>
                    <th class="px-2 py-2 text-right">@L["providers.col.actions"]</th>
                </tr>
            </thead>
            <tbody>
                @foreach (var p in PageItems)
                {
                    <tr @key="@($"{p.Id}-row")" class="border-b border-border">
                        <td class="px-2 py-2">
                            @* Chevron + cột Models cùng một toggle — expand là thao tác chính *@
                            <button type="button" class="btn btn-outline-secondary px-1.5"
                                    aria-expanded="@(_expandedId == p.Id)"
                                    @onclick="() => ToggleExpand(p.Id)">
                                @(_expandedId == p.Id ? "▾" : "▸")
                            </button>
                        </td>
                        <td class="px-2 py-2">
                            <div class="flex items-center gap-2">
                                <span class="font-medium">@p.Name</span>
                                <Badge Text="@TypeLabel(p.Type)" Variant="TypeVariant(p.Type)" />
                            </div>
                        </td>
                        <td class="max-w-64 truncate px-2 py-2 opacity-80" title="@p.BaseUrl">@p.BaseUrl</td>
                        <td class="px-2 py-2">
                            <button type="button" class="hover:underline"
                                    @onclick="() => ToggleExpand(p.Id)">
                                @string.Format(L["models.count"], EnabledCount(p), p.Models.Count)
                            </button>
                        </td>
                        <td class="px-2 py-2">
                            <input type="checkbox" checked="@p.Enabled" disabled="@_busy"
                                   @onchange="() => ToggleProviderEnabledAsync(p)" />
                        </td>
                        <td class="px-2 py-2">
                            @if (p.LastTestSuccess is null)
                            {
                                <Badge Text="@L["providers.lastTest.never"]" Variant="BadgeVariant.Neutral" />
                            }
                            else
                            {
                                <span title="@(p.LastTestMessage ?? string.Empty)">
                                    <Badge Variant="@(p.LastTestSuccess == true ? BadgeVariant.Success : BadgeVariant.Danger)"
                                           Text="@(p.LastTestSuccess == true ? L["providers.lastTest.ok"] : L["providers.lastTest.fail"])" />
                                    @if (p.LastTestAt is { } testedAt)
                                    {
                                        <span class="ml-1 text-xs opacity-70">@testedAt.ToLocalTime().ToString("g")</span>
                                    }
                                </span>
                            }
                        </td>
                        <td class="px-2 py-2 text-right">
                            <div class="flex justify-end gap-1">
                                <button type="button" class="btn btn-outline-secondary"
                                        disabled="@(_busy || _testingId == p.Id)"
                                        @onclick="() => TestAsync(p)">
                                    @(_testingId == p.Id ? L["providers.testing"] : L["providers.action.test"])
                                </button>
                                <button type="button" class="btn btn-outline-danger"
                                        disabled="@(_busy || _confirmDeleteProvider is not null)"
                                        @onclick="() => _confirmDeleteProvider = p">
                                    @L["providers.action.delete"]
                                </button>
                            </div>
                        </td>
                    </tr>

                    @if (_expandedId == p.Id)
                    {
                        <tr @key="@($"{p.Id}-models")" class="border-b border-border bg-background">
                            <td></td>
                            <td colspan="6" class="px-3 py-3">
                                @* ===== Model panel inline (spec §4.2) ===== *@
                                <div class="mb-2 flex flex-wrap items-center gap-2">
                                    <span class="text-sm font-semibold">@L["models.section"]</span>

                                    <input class="w-56 rounded border border-border bg-surface px-2 py-1"
                                           placeholder="@L["models.addPlaceholder"]"
                                           disabled="@_busy" @bind="_newModelId"
                                           @onkeydown="e => OnModelKeyAsync(e, p)" />
                                    <button type="button" class="btn btn-outline-secondary"
                                            disabled="@(_busy || string.IsNullOrWhiteSpace(_newModelId))"
                                            @onclick="() => AddManualAsync(p)">
                                        @L["models.action.add"]
                                    </button>

                                    <button type="button" class="btn btn-outline-secondary"
                                            disabled="@(_busy || _fetchingId == p.Id)"
                                            @onclick="() => FetchAsync(p)">
                                        @(_fetchingId == p.Id ? L["models.fetching"] : L["models.action.fetch"])
                                    </button>
                                    <button type="button" class="btn btn-outline-secondary" disabled="@_busy"
                                            @onclick="() => _bulkOpen = !_bulkOpen">
                                        @L["models.action.bulk"]
                                    </button>

                                    <button type="button" class="btn btn-outline-secondary" disabled="@_busy"
                                            @onclick="() => SetAllModelsAsync(p, true)">
                                        @L["models.enableAll"]
                                    </button>
                                    <button type="button" class="btn btn-outline-secondary" disabled="@_busy"
                                            @onclick="() => SetAllModelsAsync(p, false)">
                                        @L["models.disableAll"]
                                    </button>
                                    <button type="button" class="btn btn-outline-danger" disabled="@_busy"
                                            @onclick="() => _confirmRemoveAll = p">
                                        @L["models.deleteAll"]
                                    </button>
                                </div>

                                @if (_bulkOpen)
                                {
                                    @* Inline toggle thay vì modal con — spec §4.2 cho phép implementer chọn inline *@
                                    <div class="mb-2 flex flex-col gap-2">
                                        <textarea class="h-24 rounded border border-border bg-surface px-2 py-1 font-mono text-xs"
                                                  placeholder="@L["models.bulkPlaceholder"]"
                                                  @bind="_bulkText"></textarea>
                                        <div class="flex gap-2">
                                            <button type="button" class="btn btn-primary" disabled="@_busy"
                                                    @onclick="() => AddBulkAsync(p)">
                                                @L["models.action.add"]
                                            </button>
                                            <button type="button" class="btn btn-outline-secondary"
                                                    @onclick="() => _bulkOpen = false">
                                                @L["confirm.cancel"]
                                            </button>
                                        </div>
                                    </div>
                                }

                                @if (p.Models.Count == 0)
                                {
                                    <EmptyState Message="@L["models.empty"]" />
                                }
                                else
                                {
                                    <div class="overflow-x-auto rounded border border-border bg-surface">
                                        <table class="w-full text-xs">
                                            <thead>
                                                <tr class="border-b border-border text-left uppercase opacity-70">
                                                    <th class="px-2 py-1.5">@L["models.col.id"]</th>
                                                    <th class="px-2 py-1.5">@L["models.col.metadata"]</th>
                                                    <th class="px-2 py-1.5">@L["models.col.enabled"]</th>
                                                    <th class="px-2 py-1.5 text-right">@L["models.col.actions"]</th>
                                                </tr>
                                            </thead>
                                            <tbody>
                                                @foreach (var m in PagedModels(p))
                                                {
                                                    <tr @key="m.Id" class="border-b border-border last:border-b-0">
                                                        <td class="px-2 py-1.5 font-mono">
                                                            @(m.DisplayName is null ? m.ModelId : $"{m.DisplayName} ({m.ModelId})")
                                                        </td>
                                                        <td class="px-2 py-1.5">
                                                            <div class="flex flex-wrap items-center gap-1.5">
                                                                @if (m.IsManual)
                                                                {
                                                                    <Badge Text="@L["models.manual"]" Variant="BadgeVariant.Neutral" />
                                                                }
                                                                @if (m.ContextWindow is { } ctx)
                                                                {
                                                                    <Badge Variant="BadgeVariant.Info" Text="@($"ctx: {FormatCtx(ctx)}")" />
                                                                }
                                                                @if (m.SupportsVision)
                                                                {
                                                                    <Badge Text="@L["models.badge.vision"]" Variant="BadgeVariant.Success" />
                                                                }
                                                                @if (m.SupportsThink)
                                                                {
                                                                    <Badge Text="@L["models.badge.think"]" Variant="BadgeVariant.Warning" />
                                                                }
                                                            </div>
                                                        </td>
                                                        <td class="px-2 py-1.5">
                                                            <input type="checkbox" checked="@m.Enabled" disabled="@_busy"
                                                                   @onchange="() => ToggleModelAsync(m)" />
                                                        </td>
                                                        <td class="px-2 py-1.5 text-right">
                                                            <button type="button" class="btn btn-outline-danger px-1.5"
                                                                    disabled="@(_busy || _confirmDeleteModel is not null)"
                                                                    @onclick="() => _confirmDeleteModel = m">
                                                                &#128465;&#65039;
                                                            </button>
                                                        </td>
                                                    </tr>
                                                }
                                            </tbody>
                                        </table>
                                    </div>

                                    <Pager Total="p.Models.Count" @bind-Page="_modelPage" @bind-PageSize="_modelPageSize" />
                                }
                            </td>
                        </tr>
                    }
                }
            </tbody>
        </table>
    </div>

    <Pager Total="_providers.Count" @bind-Page="_providerPage" @bind-PageSize="_providerPageSize" />
}

@if (_confirmDeleteProvider is { } providerToDelete)
{
    <ConfirmDialog Visible="true"
                   Title="@L["providers.action.delete"]"
                   Message="@string.Format(L["providers.confirmDelete"], providerToDelete.Name, providerToDelete.Models.Count)"
                   Danger="true"
                   ConfirmText="@L["providers.action.delete"]"
                   CancelText="@L["confirm.cancel"]"
                   OnConfirm="DeleteProviderAsync"
                   OnCancel="() => _confirmDeleteProvider = null" />
}

@if (_confirmDeleteModel is { } modelToDelete)
{
    <ConfirmDialog Visible="true"
                   Title="@L["providers.action.delete"]"
                   Message="@string.Format(L["models.confirmDelete"], modelToDelete.ModelId)"
                   Danger="true"
                   ConfirmText="@L["providers.action.delete"]"
                   CancelText="@L["confirm.cancel"]"
                   OnConfirm="DeleteModelAsync"
                   OnCancel="() => _confirmDeleteModel = null" />
}

@if (_confirmRemoveAll is { } providerToRemoveAll)
{
    <ConfirmDialog Visible="true"
                   Title="@L["providers.action.delete"]"
                   Message="@string.Format(L["models.confirmDeleteAll"], providerToRemoveAll.Models.Count)"
                   Danger="true"
                   ConfirmText="@L["models.deleteAll"]"
                   CancelText="@L["confirm.cancel"]"
                   OnConfirm="RemoveAllModelsAsync"
                   OnCancel="() => _confirmRemoveAll = null" />
}

@code {
    private List<Provider> _providers = [];
    private bool _loading = true;
    private bool _busy;

    // Expand + phân trang giữ xuyên suốt thao tác (checklist #7: expand persist)
    private long? _expandedId;
    private int _providerPage = 1;
    private int _providerPageSize = 10;
    private int _modelPage = 1;
    private int _modelPageSize = 10;

    private long? _testingId;
    private long? _fetchingId;
    private string _newModelId = string.Empty;
    private bool _bulkOpen;
    private string _bulkText = string.Empty;

    private Provider? _confirmDeleteProvider;
    private Model? _confirmDeleteModel;
    private Provider? _confirmRemoveAll;

    /// <summary>Provider của trang hiện tại — client-side paging trong bộ nhớ (spec quyết định 4).</summary>
    private IEnumerable<Provider> PageItems => _providers
        .Skip((_providerPage - 1) * _providerPageSize)
        .Take(_providerPageSize);

    protected override async Task OnInitializedAsync()
    {
        L.LanguageChanged += OnLanguageChanged;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _providers = (await ProviderSvc.ListAsync()).ToList();
            // Sau khi xóa bớt, trang hiện tại có thể vượt số trang — clamp về hợp lệ
            var providerPages = Math.Max(1, (_providers.Count + _providerPageSize - 1) / _providerPageSize);
            _providerPage = Math.Clamp(_providerPage, 1, providerPages);
        }
        catch (Exception ex)
        {
            Log.Error("Không tải được danh sách provider.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnLanguageChanged() => _ = InvokeAsync(StateHasChanged);

    private void ToggleExpand(long id)
    {
        if (_expandedId == id)
        {
            _expandedId = null;
            return;
        }
        _expandedId = id;
        _modelPage = 1; // đổi provider đang expand → về trang 1 của bảng model
        _bulkOpen = false;
        _newModelId = string.Empty;
    }

    private async Task ToggleProviderEnabledAsync(Provider provider)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await ProviderSvc.SetEnabledAsync(provider.Id, !provider.Enabled);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Không đổi được trạng thái provider.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task TestAsync(Provider provider)
    {
        _testingId = provider.Id;
        try
        {
            // Entity đã lưu → service tự decrypt key đã lưu và persist LastTest* (Task 3)
            var result = await ProviderSvc.TestConnectionAsync(provider, apiKeyOverride: null);
            if (result.Success)
            {
                Toast.Show(L["providers.msg.testPassed"], ToastSeverity.Success);
            }
            else
            {
                Toast.Show(string.Format(L["providers.msg.testFailed"], result.Message), ToastSeverity.Error);
            }
            await LoadAsync(); // badge LastTest* mới nhất
        }
        catch (Exception ex)
        {
            Log.Error("Test connection thất bại từ danh sách provider.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _testingId = null;
        }
    }

    private async Task DeleteProviderAsync()
    {
        if (_confirmDeleteProvider is not { } provider) return;
        _busy = true;
        try
        {
            await ProviderSvc.DeleteAsync(provider.Id);
            if (_expandedId == provider.Id)
            {
                _expandedId = null;
            }
            Toast.Show(L["providers.deleted"], ToastSeverity.Success);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Không xóa được provider.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
            _confirmDeleteProvider = null;
        }
    }

    private async Task OnModelKeyAsync(KeyboardEventArgs e, Provider provider)
    {
        // Enter trong ô Add model = submit (pattern form nhẹ, không cần nút)
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(_newModelId))
        {
            await AddManualAsync(provider);
        }
    }

    private async Task AddManualAsync(Provider provider)
    {
        if (_busy || string.IsNullOrWhiteSpace(_newModelId)) return;
        _busy = true;
        try
        {
            await ModelSvc.AddManualAsync(provider.Id, _newModelId);
            _newModelId = string.Empty;
            Toast.Show(L["models.saved"], ToastSeverity.Success);
            await ReloadKeepExpandAsync(provider.Id);
        }
        catch (InvalidOperationException)
        {
            // AddManualAsync ném InvalidOperation duy nhất khi duplicate ModelId
            Toast.Show(L["models.error.duplicate"], ToastSeverity.Error);
        }
        catch (Exception ex)
        {
            Log.Error("Thêm model thủ công thất bại.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task AddBulkAsync(Provider provider)
    {
        if (_busy || string.IsNullOrWhiteSpace(_bulkText)) return;
        _busy = true;
        try
        {
            var lines = _bulkText.Replace("\r\n", "\n").Split('\n');
            var (added, skipped) = await ModelSvc.AddBulkAsync(provider.Id, lines);
            _bulkText = string.Empty;
            _bulkOpen = false;
            Toast.Show(string.Format(L["models.result.added"], added, skipped), ToastSeverity.Success);
            await ReloadKeepExpandAsync(provider.Id);
        }
        catch (Exception ex)
        {
            Log.Error("Bulk add model thất bại.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task FetchAsync(Provider provider)
    {
        _fetchingId = provider.Id;
        try
        {
            var (added, skipped) = await ModelSvc.FetchFromProviderAsync(provider.Id);
            Toast.Show(string.Format(L["models.result.added"], added, skipped), ToastSeverity.Success);
            await ReloadKeepExpandAsync(provider.Id);
        }
        catch (HttpRequestException ex)
        {
            Log.Error("Lấy danh sách model thất bại.", ex);
            Toast.Show(string.Format(L["models.fetchFailed"], ex.Message), ToastSeverity.Error);
        }
        catch (TaskCanceledException ex)
        {
            // HttpClient timeout ném TaskCanceledException — tách nhánh để toast đúng loại lỗi
            Log.Error("Lấy danh sách model timeout.", ex);
            Toast.Show(string.Format(L["models.fetchFailed"], ex.Message), ToastSeverity.Error);
        }
        catch (Exception ex)
        {
            Log.Error("Lấy danh sách model thất bại.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _fetchingId = null;
        }
    }

    private async Task ToggleModelAsync(Model model)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await ModelSvc.SetEnabledAsync(model.Id, !model.Enabled);
            await ReloadKeepExpandAsync(model.ProviderId);
        }
        catch (Exception ex)
        {
            Log.Error("Không đổi được trạng thái model.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task SetAllModelsAsync(Provider provider, bool enabled)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            await ModelSvc.SetAllEnabledAsync(provider.Id, enabled);
            await ReloadKeepExpandAsync(provider.Id);
        }
        catch (Exception ex)
        {
            Log.Error("Không bật/tắt được toàn bộ model.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task DeleteModelAsync()
    {
        if (_confirmDeleteModel is not { } model) return;
        _busy = true;
        try
        {
            await ModelSvc.RemoveAsync(model.Id);
            Toast.Show(L["models.deleted"], ToastSeverity.Success);
            await ReloadKeepExpandAsync(model.ProviderId);
        }
        catch (Exception ex)
        {
            Log.Error("Không xóa được model.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
            _confirmDeleteModel = null;
        }
    }

    private async Task RemoveAllModelsAsync()
    {
        if (_confirmRemoveAll is not { } provider) return;
        _busy = true;
        try
        {
            var removed = await ModelSvc.RemoveAllAsync(provider.Id);
            Toast.Show(string.Format(L["models.removedAll"], removed), ToastSeverity.Success);
            await ReloadKeepExpandAsync(provider.Id);
        }
        catch (Exception ex)
        {
            Log.Error("Không xóa được toàn bộ model.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
            _confirmRemoveAll = null;
        }
    }

    /// <summary>Tải lại danh sách nhưng giữ nguyên hàng đang expand + clamp trang model.</summary>
    private async Task ReloadKeepExpandAsync(long providerId)
    {
        await LoadAsync();
        _expandedId = providerId;
        if (_providers.FirstOrDefault(x => x.Id == providerId) is { } current)
        {
            var pages = Math.Max(1, (current.Models.Count + _modelPageSize - 1) / _modelPageSize);
            _modelPage = Math.Clamp(_modelPage, 1, pages);
        }
    }

    /// <summary>Model của trang hiện tại, sắp xếp ổn định theo ModelId (client-side).</summary>
    private IEnumerable<Model> PagedModels(Provider provider) => provider.Models
        .OrderBy(m => m.ModelId, StringComparer.Ordinal)
        .Skip((_modelPage - 1) * _modelPageSize)
        .Take(_modelPageSize);

    private static int EnabledCount(Provider provider) => provider.Models.Count(m => m.Enabled);

    private string TypeLabel(ProviderType type) => type switch
    {
        ProviderType.OpenAI => L["providers.type.openai"],
        _ => L["providers.type.anthropic"],
    };

    private static BadgeVariant TypeVariant(ProviderType type) => type switch
    {
        ProviderType.OpenAI => BadgeVariant.Success,
        _ => BadgeVariant.Info,
    };

    /// <summary>128000 → "128k"; dưới 1000 giữ nguyên — badge ngắn gọn, không cần chính xác tuyệt đối.</summary>
    private static string FormatCtx(int contextWindow) =>
        contextWindow >= 1000 ? $"{contextWindow / 1000}k" : contextWindow.ToString();

    public void Dispose() => L.LanguageChanged -= OnLanguageChanged;
}
```

- [ ] **Step 5: Verify**

Run (workdir `router-balancing/vite-project`): `npm run build`
Expected: exit 0. Kiểm tra file output đổi (class mới của page) — nếu `router-balancing/wwwroot/build/` thay đổi thì commit cùng.

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed!` — **100** (không thêm test).

- [ ] **Step 6: Commit**

```bash
git add router-balancing/Components/ src/RouterBalancing.Core/Localization/Translations.cs router-balancing/wwwroot/build/
git commit -m "feat: add providers page with inline model management panel"
```

---

### Task 9: Modal Add/Edit provider — test-before-save + verify cuối

**Files:**
- Modify: `router-balancing/Components/Pages/Providers.razor` (toolbar, cột Edit, modal + handlers)
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (keys modal provider — EN + VI)

**Interfaces:**
- Consumes: Task 1 (`ProviderDraft`, `ProviderValidator`), Task 2 (`CreateAsync`/`UpdateAsync`/`GetAsync`), Task 3 (`TestConnectionAsync`, `ProviderTestResult`), Task 8 (state/handlers page), Phase 1 (`IAppSettingsService.DefaultMaxConcurrent`).
- Produces: modal Add/Edit trong `Providers.razor` — validate inline, **rule test-before-save**: provider chưa lưu chỉ Save được khi test pass đúng nội dung hiện tại; đã lưu Save luôn enabled; key để trống khi sửa = giữ key cũ.

- [ ] **Step 1: Bổ sung i18n keys — `Translations.English`**

```csharp
        // Provider modal (Phase 2A Task 9)
        ["providers.add"] = "Add provider",
        ["providers.action.edit"] = "Edit",
        ["providers.action.testConnection"] = "Test connection",
        ["providers.field.name"] = "Name",
        ["providers.field.type"] = "Type",
        ["providers.field.baseUrl"] = "Base URL",
        ["providers.field.apiKey"] = "API key",
        ["providers.field.maxConcurrent"] = "Max concurrent",
        ["providers.key.saved"] = "Saved - leave blank to keep",
        ["providers.test.hint"] = "Test the connection before saving a new provider.",
        ["providers.msg.saved"] = "Provider saved.",
        ["providers.error.name"] = "Name is required.",
        ["providers.error.baseUrl"] = "Base URL must start with http:// or https://.",
        ["providers.error.maxConcurrent"] = "Max concurrent must be between 1 and 64.",
```

- [ ] **Step 2: Bổ sung i18n keys — `Translations.Vietnamese` (cùng bộ key)**

```csharp
        // Provider modal (Phase 2A Task 9)
        ["providers.add"] = "Thêm nhà cung cấp",
        ["providers.action.edit"] = "Sửa",
        ["providers.action.testConnection"] = "Kiểm tra kết nối",
        ["providers.field.name"] = "Tên",
        ["providers.field.type"] = "Loại",
        ["providers.field.baseUrl"] = "Base URL",
        ["providers.field.apiKey"] = "API key",
        ["providers.field.maxConcurrent"] = "Đồng thời tối đa",
        ["providers.key.saved"] = "Đã lưu - để trống để giữ",
        ["providers.test.hint"] = "Hãy kiểm tra kết nối trước khi lưu nhà cung cấp mới.",
        ["providers.msg.saved"] = "Đã lưu nhà cung cấp.",
        ["providers.error.name"] = "Tên là bắt buộc.",
        ["providers.error.baseUrl"] = "Base URL phải bắt đầu bằng http:// hoặc https://.",
        ["providers.error.maxConcurrent"] = "Đồng thời tối đa phải từ 1 đến 64.",
```

- [ ] **Step 3: Sửa `Providers.razor` — header/usings/inject**

a) Thêm `@using RouterBalancing.Core.Settings;` ngay sau dòng `@using RouterBalancing.Core.Providers`:

```razor
@using RouterBalancing.Core.Settings
```

b) Thêm inject sau dòng `@inject ILogService Log`:

```razor
@inject IAppSettingsService Settings
```

c) Thay dòng `<h1 class="mb-4 text-xl font-semibold">@L["panel.providers.title"]</h1>` thành toolbar:

```razor
<div class="mb-4 flex items-center justify-between gap-3">
    <h1 class="text-xl font-semibold">@L["panel.providers.title"]</h1>
    <button type="button" class="btn btn-primary" @onclick="OpenAdd">+ @L["providers.add"]</button>
</div>
```

- [ ] **Step 4: Sửa `Providers.razor` — cột Actions thêm nút Edit**

Trong ô actions, chèn nút giữa nút Test và nút Delete:

```razor
                                <button type="button" class="btn btn-outline-secondary"
                                        disabled="@_busy"
                                        @onclick="() => OpenEdit(p)">
                                    @L["providers.action.edit"]
                                </button>
```

- [ ] **Step 5: Sửa `Providers.razor` — markup modal**

Chèn khối `<Modal>...</Modal>` **ngay trước** dòng `@if (_confirmDeleteProvider is { } providerToDelete)`:

```razor
<Modal Visible="_modalVisible"
       Title="@(_editingId is null ? L["providers.add"] : L["providers.action.edit"])"
       OnClose="CloseModal">
    <div class="grid gap-3 sm:grid-cols-2">
        <label class="flex flex-col gap-1 text-sm sm:col-span-2">
            @L["providers.field.name"]
            <input class="rounded border border-border bg-surface px-2 py-1.5"
                   autofocus @bind="_draft.Name" />
        </label>

        <label class="flex flex-col gap-1 text-sm">
            @L["providers.field.type"]
            <select class="rounded border border-border bg-surface px-2 py-1.5" @bind="_draft.Type">
                <option value="@ProviderType.OpenAI">@L["providers.type.openai"]</option>
                <option value="@ProviderType.Anthropic">@L["providers.type.anthropic"]</option>
            </select>
        </label>

        <label class="flex flex-col gap-1 text-sm">
            @L["providers.field.maxConcurrent"]
            <input type="number" min="1" max="64"
                   class="rounded border border-border bg-surface px-2 py-1.5"
                   @bind="_draft.MaxConcurrent" />
        </label>

        <label class="flex flex-col gap-1 text-sm sm:col-span-2">
            @L["providers.field.baseUrl"]
            <input class="rounded border border-border bg-surface px-2 py-1.5"
                   placeholder="https://api.openai.com" spellcheck="false"
                   @bind="_draft.BaseUrl" />
        </label>

        <label class="flex flex-col gap-1 text-sm sm:col-span-2">
            @L["providers.field.apiKey"]
            @* Edit: placeholder "đã lưu" — để trống = giữ key cũ, không hiển thị giá trị (spec §3.1) *@
            <input type="password" class="rounded border border-border bg-surface px-2 py-1.5"
                   autocomplete="off" spellcheck="false"
                   placeholder="@(_editingId is null ? string.Empty : L["providers.key.saved"])"
                   @bind="_draft.ApiKey" />
        </label>
    </div>

    @if (ModalError(nameof(ProviderDraft.Name)) is { } nameError)
    {
        <div class="mt-1 text-sm text-danger">@L[nameError]</div>
    }
    @if (ModalError(nameof(ProviderDraft.BaseUrl)) is { } baseUrlError)
    {
        <div class="mt-1 text-sm text-danger">@L[baseUrlError]</div>
    }
    @if (ModalError(nameof(ProviderDraft.MaxConcurrent)) is { } maxConcurrentError)
    {
        <div class="mt-1 text-sm text-danger">@L[maxConcurrentError]</div>
    }

    <div class="mt-4 flex flex-wrap items-center gap-2">
        <button type="button" class="btn btn-outline-info" disabled="@(_testing || _saving)"
                @onclick="TestDraftAsync">
            @(_testing ? L["providers.testing"] : L["providers.action.testConnection"])
        </button>

        @if (_testResult is { } testResult)
        {
            <Badge Variant="@(testResult.Success ? BadgeVariant.Success : BadgeVariant.Danger)"
                   Text="@(testResult.Success ? L["providers.lastTest.ok"] : L["providers.lastTest.fail"])" />
            @if (testResult.Message is not null)
            {
                <span class="text-xs opacity-70">@testResult.Message</span>
            }
        }
        else if (_editingId is null)
        {
            <span class="text-xs opacity-70">@L["providers.test.hint"]</span>
        }
    </div>

    <div class="mt-4 flex justify-end gap-2">
        <button type="button" class="btn btn-outline-secondary" disabled="@_saving" @onclick="CloseModal">
            @L["confirm.cancel"]
        </button>
        @* Nút Save dùng chung key i18n của Settings ("Save"/"Lưu") — cùng nghĩa, tránh nhân đôi key *@
        <button type="button" class="btn btn-primary"
                disabled="@(!CanSave || _saving || _testing)" @onclick="SaveAsync">
            @L["settings.action.save"]
        </button>
    </div>
</Modal>
```

- [ ] **Step 6: Sửa `Providers.razor` — thêm state + handlers vào `@code`**

a) Khai báo state — chèn ngay sau dòng `private Provider? _confirmRemoveAll;`:

```csharp
    // ===== Modal Add/Edit (Task 9) =====
    private bool _modalVisible;
    private bool _saving;
    private bool _testing;
    private long? _editingId; // null = thêm mới
    private ProviderDraft _draft = new();
    private Dictionary<string, string> _errors = [];
    private ProviderTestResult? _testResult;

    /// <summary>Chữ ký draft lúc test: Type|BaseUrl|ApiKey — đổi form sau test = test mất hiệu lực.</summary>
    private string? _testedSignature;
```

b) Handlers — chèn ngay sau method `RemoveAllModelsAsync` (trước `ReloadKeepExpandAsync`):

```csharp
    private void OpenAdd()
    {
        _editingId = null;
        _draft = new ProviderDraft { MaxConcurrent = Settings.DefaultMaxConcurrent };
        ResetModalState();
        _modalVisible = true;
    }

    private void OpenEdit(Provider provider)
    {
        _editingId = provider.Id;
        // ApiKey để trống = giữ key cũ khi UpdateAsync (spec §3.1); UI hiện placeholder "đã lưu"
        _draft = new ProviderDraft
        {
            Name = provider.Name,
            Type = provider.Type,
            BaseUrl = provider.BaseUrl,
            ApiKey = string.Empty,
            MaxConcurrent = provider.MaxConcurrent,
        };
        ResetModalState();
        _modalVisible = true;
    }

    private void ResetModalState()
    {
        _errors = [];
        _testResult = null;
        _testedSignature = null;
        _testing = false;
        _saving = false;
    }

    private void CloseModal()
    {
        _modalVisible = false;
        ResetModalState();
    }

    /// <summary>Lấy key lỗi i18n của field; null nếu field không có lỗi (pattern SettingsPanel).</summary>
    private string? ModalError(string field) =>
        _errors.TryGetValue(field, out var key) ? key : null;

    private string DraftSignature() =>
        $"{_draft.Type}|{_draft.BaseUrl.Trim().TrimEnd('/')}|{_draft.ApiKey}";

    /// <summary>
    /// Provider mới: chỉ Save khi test pass với đúng nội dung hiện tại của form;
    /// provider đã lưu: Save luôn bật — test là tùy chọn (spec §4.2).
    /// </summary>
    private bool CanSave =>
        _editingId is not null
        || (_testResult is { Success: true } && _testedSignature == DraftSignature());

    private async Task TestDraftAsync()
    {
        _errors = ProviderValidator.Validate(_draft);
        if (_errors.Count > 0) return;

        _testing = true;
        try
        {
            // Đã lưu: lấy entity (có key mã hóa) rồi vá field form — service tự ưu tiên
            // apiKeyOverride khi form đang có key gõ (spec §3.1)
            var target = _editingId is { } id
                ? await ProviderSvc.GetAsync(id) ?? throw new KeyNotFoundException($"Provider {id} not found.")
                : new Provider();
            target.Type = _draft.Type;
            target.BaseUrl = _draft.BaseUrl.Trim().TrimEnd('/');

            _testResult = await ProviderSvc.TestConnectionAsync(
                target, string.IsNullOrEmpty(_draft.ApiKey) ? null : _draft.ApiKey);
            _testedSignature = DraftSignature();

            if (_editingId is not null)
            {
                await LoadAsync(); // LastTest* đã persist trong service — badge cột Last test đổi theo
            }
        }
        catch (Exception ex)
        {
            // Mạng/DPAPI/URL lạ đều hiển thị như test fail trong modal — không nuốt im lặng
            Log.Error("Test connection thất bại từ modal.", ex);
            _testResult = new ProviderTestResult(false, ex.Message, DateTimeOffset.UtcNow);
            _testedSignature = DraftSignature();
        }
        finally
        {
            _testing = false;
        }
    }

    private async Task SaveAsync()
    {
        _errors = ProviderValidator.Validate(_draft);
        if (_errors.Count > 0) return;
        if (!CanSave) return; // chặn cứng — nút đã disabled nhưng double-click vẫn tới được đây

        _saving = true;
        try
        {
            if (_editingId is { } id)
            {
                await ProviderSvc.UpdateAsync(id, _draft);
            }
            else
            {
                await ProviderSvc.CreateAsync(_draft);
            }
            Toast.Show(L["providers.msg.saved"], ToastSeverity.Success);
            CloseModal();
            await LoadAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Không lưu được provider.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _saving = false;
        }
    }
```

- [ ] **Step 7: Verify**

Run (workdir `router-balancing/vite-project`): `npm run build`
Expected: exit 0; commit `router-balancing/wwwroot/build/` nếu đổi.

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed!` — **100**.

- [ ] **Step 8: Commit**

```bash
git add router-balancing/Components/ src/RouterBalancing.Core/Localization/Translations.cs router-balancing/wwwroot/build/
git commit -m "feat: add provider modal with test-before-save gating"
```

---

## Cuối plan — verify & review (ngoài 9 task)

1. **Run toàn bộ:** `dotnet test` (100 xanh) + `dotnet build ... -f net10.0-windows10.0.19041.0` (0W/0E) + `npm run build` (exit 0).
2. **Manual checklist** (spec §5 — chạy tay trên app, báo kết quả user trước khi merge):
   1. Add provider OpenAI thật → test pass → Save → list hiện.
   2. Add provider key sai → test fail hiện lý do, Save **bị disabled**.
   3. Edit provider: key để trống → giữ nguyên (verify bằng Test pass lại).
   4. Fetch models → toast N thêm/M skip; toggle model; metadata badges hiện (gpt-*/claude-*).
   5. Bulk add (một dòng duplicate) → skip 1.
   6. Xóa provider → confirm hiện đúng số model → xóa.
   7. Expand row persist khi toggle các thao tác khác; Pager đổi PageSize/goto.
   8. Language EN/VI đổi đủ text mới; theme dark hiện đủ badge/component.
   9. `dotnet test` xanh, build 0W.
3. **Final whole-branch review** (SDD phase 4 — `review-package` so với `master`) trước khi đề nghị merge.
