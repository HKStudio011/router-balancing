# Proxy Core (Slice 3A) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Endpoint `POST /v1/chat/completions` nhận request OpenAI, validate, resolve model → provider, passthrough streaming nguyên văn tới upstream OpenAI-compatible, trả error contract OpenAI-shaped và ghi log.

**Architecture:** Seam `ProxyApp` (ConfigureServices + ConfigurePipeline) tách cấu hình khỏi `ProxyHost` để integration test dựng cùng pipeline qua TestServer. Engine mới trong `src/RouterBalancing.Core/Engine/`: validator (pure) → resolver (EF) → handler (orchestrate) → `IUpstreamClient` (streaming). Unit test cho từng lớp với test doubles nested private; integration TestServer + stub upstream; e2e curl script với mock upstream Node.

**Tech Stack:** .NET 10, ASP.NET Core minimal API (Kestrel loopback, `CreateSlimBuilder`), EF Core 10 + SQLite, xUnit 2.9.3, `Microsoft.AspNetCore.TestHost` 10.0.12, Node (mock upstream e2e).

**Spec:** `docs/superpowers/specs/2026-09-28-proxy-core-design.md` (đã user-approved, commit `31b62a4`).

## Global Constraints

- Comment/XML doc **tiếng Việt** ("why"); identifier + commit message **tiếng Anh** conventional (AGENTS.md).
- XML doc `///` **bắt buộc** mọi public/protected member (type, method, property, enum value).
- Test doubles **nested private** trong file test; test name mô tả hành vi (`Validate_WhenJsonBroken_ReturnsInvalidJson`).
- **Không log** nội dung `messages`/body/API key (spec §6); log proxy dùng `LogCategory.Request`; mỗi request đúng 1 dòng log.
- Error message (client-facing) **tiếng Anh**; message log **tiếng Việt**.
- **Không đổi** timeout client `"provider-probe"` (10s, MauiProgram:73) — client streaming dùng tên `"upstream"` riêng với `Timeout.InfiniteTimeSpan`.
- **Không thêm key i18n** — parity giữ nguyên EN=208 VI=208.
- Không dùng `WebApplicationFactory<T>` (host trong class library, không có `Program`) — integration theo spec §7.2 dùng TestServer.
- Lệnh test (folder có space): `dotnet test "router balancing test/router balancing test.csproj" --nologo`
- Lệnh build app: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
- **Baseline: 183 tests.** Kỳ vọng cuối plan: **226** (Task2 +13, Task3 +7, Task4 +5, Task5 +10, Task6 +8). *(Task4 +5: 2 factory + 2 client + 1 SSE buffering guard — user-approved amendment sau Task 4 review.)*
- **Đóng app trước khi chạy `dotnet test`** (mutex giữ DB/port).

---

### Task 1: Extract `ProxyApp` seam (refactor, không đổi hành vi)

**Files:**
- Create: `src/RouterBalancing.Core/Server/ProxyApp.cs`
- Modify: `src/RouterBalancing.Core/Server/ProxyHost.cs` (ctor + `StartAsync`, xóa `MapEndpoints`)
- Test: `router balancing test/Server/ProxyHostTests.cs:42,155,169` (thêm tham số ctor)

**Interfaces:**
- Consumes: `ISecretProtector` (`RouterBalancing.Core.Security`, đã đăng ký DI ở MauiProgram:55), `ApiKeyMiddleware`, `IProxyHost`
- Produces (Task 5/6 dùng): `ProxyApp.ConfigureServices(WebApplicationBuilder builder, ISecretProtector protector)`, `ProxyApp.ConfigurePipeline(WebApplication app)`

- [ ] **Step 1: Tạo `ProxyApp.cs` với cấu hình moved từ `ProxyHost`**

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Server;

/// <summary>
/// Cấu hình WebApplication cho proxy — tách khỏi ProxyHost để integration test
/// dựng cùng pipeline qua TestServer (spec 3A §2.1), tránh 2 nơi diverge.
/// Tách 2 method vì middleware/map phải chạy sau Build(): DI đóng băng sau Build.
/// </summary>
public static class ProxyApp
{
    /// <summary>
    /// Đăng ký DI cho proxy. Phải gọi TRƯỚC <c>builder.Build()</c>.
    /// </summary>
    /// <param name="builder">Builder do ProxyHost (hoặc test) tạo.</param>
    /// <param name="protector">Giải mã API key provider — đăng ký singleton dùng chung.</param>
    public static void ConfigureServices(WebApplicationBuilder builder, ISecretProtector protector)
    {
        builder.Services.AddSingleton(protector);
    }

    /// <summary>
    /// Gắn pipeline: auth middleware + endpoint. Gọi NGAY SAU <c>Build()</c>, TRƯỚC <c>StartAsync</c>.
    /// </summary>
    /// <param name="app">WebApplication vừa Build.</param>
    public static void ConfigurePipeline(WebApplication app)
    {
        app.UseMiddleware<ApiKeyMiddleware>();
        MapEndpoints(app);
    }

    private static void MapEndpoints(WebApplication app)
    {
        // /health mở luôn (middleware bỏ qua path này) — watchdog của Phase 2 dùng để ping
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));

        // Danh sách model đã bật, đúng shape OpenAI /v1/models để client không cần phân biệt
        app.MapGet("/v1/models", async (HttpContext http) =>
        {
            var factory = http.RequestServices
                .GetRequiredService<IDbContextFactory<RouterBalancingDbContext>>();
            using var db = factory.CreateDbContext();
            var models = await db.Models.AsNoTracking()
                .Where(m => m.Enabled)
                .OrderBy(m => m.Id)
                .Select(m => new
                {
                    id = m.ModelId,
                    // @object: keyword 'object' không đặt được thẳng làm tên member — JSON vẫn ra "object"
                    @object = "model",
                    created = m.CreatedAt.ToUnixTimeSeconds(),
                    owned_by = m.Provider != null ? m.Provider.Name : "unknown",
                })
                .ToListAsync(http.RequestAborted);

            return Results.Json(new { @object = "list", data = models });
        });
    }
}
```

- [ ] **Step 2: Sửa `ProxyHost.cs`**

Constructor — thêm `ISecretProtector` (đăng ký field):

```csharp
    private readonly ISecretProtector _protector;

    public ProxyHost(IAppSettingsService settings, ILogService log, IDbContextFactory<RouterBalancingDbContext> db,
        ISecretProtector protector)
    {
        _settings = settings;
        _log = log;
        _db = db;
        _protector = protector;
    }
```

Trong `StartAsync`, thay đoạn (dòng ~61-63 hiện tại):

```csharp
            var app = builder.Build();
            app.UseMiddleware<ApiKeyMiddleware>();
            MapEndpoints(app);
```

bằng:

```csharp
            ProxyApp.ConfigureServices(builder, _protector);
            var app = builder.Build();
            ProxyApp.ConfigurePipeline(app);
```

Xóa toàn bộ method `private void MapEndpoints(WebApplication app)` (dòng 133-157) — đã chuyển sang `ProxyApp`.

Thêm `using RouterBalancing.Core.Security;`. Xóa 3 using giờ chết: `using System.Text.Json;`, `using Microsoft.EntityFrameworkCore;`, `using Microsoft.AspNetCore.Http;`.

- [ ] **Step 3: Sửa 3 chỗ `new ProxyHost(...)` trong `ProxyHostTests.cs`**

Cả dòng 42, 155, 169:

```csharp
        var host = new ProxyHost(_settings, _log, _db.CreateFactory(), new DpapiSecretProtector());
```

(`DpapiSecretProtector` đã được `using RouterBalancing.Core.Security;` ở đầu file.)

- [ ] **Step 4: Build — kỳ vọng 0 Error**

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 5: Chạy toàn bộ test — refactor không đổi hành vi**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed! - Failed: 0, Passed: 183` (11 test `ProxyHostTests` gồm `/health`, `/v1/models`, ApiKey 401 — chứng minh seam nguyên vẹn)

- [ ] **Step 6: Commit**

```bash
git add src/RouterBalancing.Core/Server/ProxyApp.cs src/RouterBalancing.Core/Server/ProxyHost.cs "router balancing test/Server/ProxyHostTests.cs"
git commit -m "refactor: extract proxyapp configuration seam from proxyhost"
```

---

### Task 2: `ChatRequestValidator` (TDD)

**Files:**
- Create: `src/RouterBalancing.Core/Engine/ChatRequestValidator.cs`
- Test: `router balancing test/Engine/ChatRequestValidatorTests.cs` (thư mục mới)

**Interfaces:**
- Consumes: `System.Text.Json`
- Produces (Task 5 dùng): `enum ValidationFailure { None, InvalidJson, MissingModel, MissingMessages }`, `readonly record struct ValidationResult(ValidationFailure Failure, string? ModelId)` với property `bool IsValid`, `static ChatRequestValidator.Validate(byte[] body) -> ValidationResult`

- [ ] **Step 1: Viết test failing (9 test)**

```csharp
using System.Text;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class ChatRequestValidatorTests
{
    private static byte[] B(string json) => Encoding.UTF8.GetBytes(json);

    private const string ValidBody =
        """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}],"stream":true,"temperature":0.2}""";

    [Fact]
    public void Validate_WhenJsonBroken_ReturnsInvalidJson()
    {
        var result = ChatRequestValidator.Validate(B("{not json"));

        Assert.Equal(ValidationFailure.InvalidJson, result.Failure);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void Validate_WhenBodyEmpty_ReturnsInvalidJson()
    {
        var result = ChatRequestValidator.Validate([]);

        Assert.Equal(ValidationFailure.InvalidJson, result.Failure);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"scalar\"")]
    [InlineData("null")]
    public void Validate_WhenRootNotObject_ReturnsMissingModel(string json)
    {
        // Root không phải object → không có trường model (V2), TryGetProperty sẽ ném nếu không chặn
        var result = ChatRequestValidator.Validate(B(json));

        Assert.Equal(ValidationFailure.MissingModel, result.Failure);
    }

    [Fact]
    public void Validate_WhenModelMissing_ReturnsMissingModel()
    {
        var result = ChatRequestValidator.Validate(B("""{"messages":[{"role":"user"}]}"""));

        Assert.Equal(ValidationFailure.MissingModel, result.Failure);
    }

    [Theory]
    [InlineData("""{"model":123,"messages":[{"role":"user"}]}""")]
    [InlineData("""{"model":"","messages":[{"role":"user"}]}""")]
    [InlineData("""{"model":"   ","messages":[{"role":"user"}]}""")]
    public void Validate_WhenModelNotNonEmptyString_ReturnsMissingModel(string json)
    {
        var result = ChatRequestValidator.Validate(B(json));

        Assert.Equal(ValidationFailure.MissingModel, result.Failure);
    }

    [Fact]
    public void Validate_WhenMessagesMissing_ReturnsMissingMessages()
    {
        var result = ChatRequestValidator.Validate(B("""{"model":"gpt-4o"}"""));

        Assert.Equal(ValidationFailure.MissingMessages, result.Failure);
    }

    [Fact]
    public void Validate_WhenMessagesEmptyArray_ReturnsMissingMessages()
    {
        var result = ChatRequestValidator.Validate(B("""{"model":"gpt-4o","messages":[]}"""));

        Assert.Equal(ValidationFailure.MissingMessages, result.Failure);
    }

    [Fact]
    public void Validate_WhenMessagesNotArray_ReturnsMissingMessages()
    {
        var result = ChatRequestValidator.Validate(B("""{"model":"gpt-4o","messages":"hi"}"""));

        Assert.Equal(ValidationFailure.MissingMessages, result.Failure);
    }

    [Fact]
    public void Validate_WhenValidBody_ReturnsModelIdAndIsValid()
    {
        var result = ChatRequestValidator.Validate(B(ValidBody));

        Assert.True(result.IsValid);
        Assert.Equal(ValidationFailure.None, result.Failure);
        Assert.Equal("gpt-4o-mini", result.ModelId);
    }
}
```

Lưu ý: 7 `[Fact]` + 2 `[Theory]` × 3 InlineData = 9 method, **13 test case** khi chạy — dùng số chạy thực tế ở Step 5.

- [ ] **Step 2: Chạy test — kỳ vọng FAIL (compile error)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ChatRequestValidatorTests" --nologo`
Expected: FAIL — build lỗi `CS0246: The type or namespace name 'ChatRequestValidator' could not be found`

- [ ] **Step 3: Viết `ChatRequestValidator.cs`**

```csharp
using System.Text.Json;

namespace RouterBalancing.Core.Engine;

/// <summary>Lý do validate chat request thất bại — map sang error contract (spec 3A §4).</summary>
public enum ValidationFailure
{
    /// <summary>Request hợp lệ.</summary>
    None = 0,

    /// <summary>Body không parse được JSON hoặc root không phải object.</summary>
    InvalidJson = 1,

    /// <summary>Thiếu / sai kiểu / rỗng trường <c>model</c>.</summary>
    MissingModel = 2,

    /// <summary>Thiếu / sai kiểu / rỗng trường <c>messages</c>.</summary>
    MissingMessages = 3,
}

/// <summary>Kết quả validate: thành công kèm model id cần resolve, hoặc lý do thất bại.</summary>
public readonly record struct ValidationResult(ValidationFailure Failure, string? ModelId)
{
    /// <summary>True khi request hợp lệ (<see cref="Failure"/> == <see cref="ValidationFailure.None"/>).</summary>
    public bool IsValid => Failure == ValidationFailure.None;
}

/// <summary>
/// Validate body chat request theo rule tối thiểu (spec 3A §3) — chỉ check presence/type,
/// không interpret nội dung: body được forward nguyên từng byte ở tầng handler.
/// </summary>
public static class ChatRequestValidator
{
    /// <summary>
    /// Parse <paramref name="body"/> và chạy rule V1–V3.
    /// </summary>
    /// <param name="body">Body thô nhận từ client (kể cả rỗng).</param>
    /// <returns>Thành công kèm id model; hoặc failure reason cho error contract.</returns>
    public static ValidationResult Validate(byte[] body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Bắt cả body rỗng (Parse ném cho 0 byte) — coi như JSON hỏng (V1)
            return new ValidationResult(ValidationFailure.InvalidJson, null);
        }

        using (doc)
        {
            var root = doc.RootElement;

            // Root không phải object (array/scalar/null) → không có trường model (V2).
            // TryGetProperty ném InvalidOperationException trên non-object nên phải chặn ở đây.
            if (root.ValueKind != JsonValueKind.Object)
                return new ValidationResult(ValidationFailure.MissingModel, null);

            if (!root.TryGetProperty("model", out var model)
                || model.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(model.GetString()))
                return new ValidationResult(ValidationFailure.MissingModel, null);

            if (!root.TryGetProperty("messages", out var messages)
                || messages.ValueKind != JsonValueKind.Array
                || messages.GetArrayLength() == 0)
                return new ValidationResult(ValidationFailure.MissingMessages, null);

            // ValueKind == String đã kiểm ở trên → GetString() không null (không cần !)
            return new ValidationResult(ValidationFailure.None, model.GetString());
        }
    }
}
```

- [ ] **Step 4: Chạy test — kỳ vọng PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ChatRequestValidatorTests" --nologo`
Expected: `Passed! - Failed: 0` (13 case)

- [ ] **Step 5: Chạy toàn bộ suite**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed! - Failed: 0, Passed: 196` (183 + 13 case mới)

- [ ] **Step 6: Commit**

```bash
git add src/RouterBalancing.Core/Engine/ChatRequestValidator.cs "router balancing test/Engine/ChatRequestValidatorTests.cs"
git commit -m "feat: add chat request validator with openai error reasons"
```

---

### Task 3: `ModelResolver` (TDD, temp SQLite)

**Files:**
- Create: `src/RouterBalancing.Core/Engine/IModelResolver.cs`
- Create: `src/RouterBalancing.Core/Engine/ModelResolver.cs`
- Test: `router balancing test/Engine/ModelResolverTests.cs`

**Interfaces:**
- Consumes: `TestDb` (test helper đã có), `IDbContextFactory<RouterBalancingDbContext>`, entity `Provider`/`Model`/`ProviderAccount`, `ProviderType`
- Produces (Task 5 dùng): `IModelResolver.ResolveAsync(string modelId, CancellationToken ct) -> Task<ModelResolveResult>`; `abstract record ModelResolveResult`; `record ModelResolveSuccess(Provider Provider, Model Model) : ModelResolveResult`; `record ModelResolveFailure(string ModelId, ResolveFailure Reason) : ModelResolveResult`; `enum ResolveFailure { NotFound, AnthropicNotSupported }`

- [ ] **Step 1: Viết test failing (7 test)**

```csharp
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class ModelResolverTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private long SeedProvider(string name, ProviderType type = ProviderType.OpenAI,
        bool enabled = true, params string[] modelIds)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = name,
            Type = type,
            BaseUrl = "https://api.openai.com",
            Enabled = enabled,
        };
        foreach (var id in modelIds)
            provider.Models.Add(new Model { ModelId = id, Enabled = true });
        db.Providers.Add(provider);
        db.SaveChanges();
        return provider.Id;
    }

    private ModelResolver CreateSut() => new(_db.CreateFactory());

    [Fact]
    public async Task Resolve_WhenModelExists_ReturnsSuccessWithProviderAndAccountsLoaded()
    {
        var id = SeedProvider("main");
        using (var db = _db.CreateFactory().CreateDbContext())
        {
            var provider = db.Providers.Find(id)!;
            provider.Accounts.Add(new ProviderAccount
            {
                Name = "a1",
                ApiKeyEncrypted = "enc",
                Enabled = true,
            });
            db.SaveChanges();
        }

        var result = await CreateSut().ResolveAsync("gpt-4o-mini", default);

        var ok = Assert.IsType<ModelResolveSuccess>(result);
        Assert.Equal("main", ok.Provider.Name);
        Assert.Equal("gpt-4o-mini", ok.Model.ModelId);
        // Include(Accounts) bắt buộc — ProviderKeyResolver cần nav này (spec §2.3)
        Assert.Contains(ok.Provider.Accounts, a => a.Name == "a1");
    }

    [Fact]
    public async Task Resolve_WhenModelUnknown_ReturnsNotFound()
    {
        SeedProvider("main", modelIds: ["gpt-4o-mini"]);

        var result = await CreateSut().ResolveAsync("nope", default);

        var fail = Assert.IsType<ModelResolveFailure>(result);
        Assert.Equal(ResolveFailure.NotFound, fail.Reason);
        Assert.Equal("nope", fail.ModelId);
    }

    [Fact]
    public async Task Resolve_WhenModelDisabled_ReturnsNotFound()
    {
        using (var db = _db.CreateFactory().CreateDbContext())
        {
            var provider = new Provider { Name = "main", BaseUrl = "https://api.openai.com" };
            provider.Models.Add(new Model { ModelId = "m-off", Enabled = false });
            db.Providers.Add(provider);
            db.SaveChanges();
        }

        var result = await CreateSut().ResolveAsync("m-off", default);

        Assert.IsType<ModelResolveFailure>(result);
    }

    [Fact]
    public async Task Resolve_WhenProviderDisabled_ReturnsNotFound()
    {
        SeedProvider("off", enabled: false, modelIds: ["m1"]);

        var result = await CreateSut().ResolveAsync("m1", default);

        var fail = Assert.IsType<ModelResolveFailure>(result);
        Assert.Equal(ResolveFailure.NotFound, fail.Reason);
    }

    [Fact]
    public async Task Resolve_WhenProviderAnthropic_ReturnsAnthropicNotSupported()
    {
        SeedProvider("claude", ProviderType.Anthropic, modelIds: ["sonnet-4"]);

        var result = await CreateSut().ResolveAsync("sonnet-4", default);

        var fail = Assert.IsType<ModelResolveFailure>(result);
        Assert.Equal(ResolveFailure.AnthropicNotSupported, fail.Reason);
    }

    [Fact]
    public async Task Resolve_WhenDuplicateModelIds_ReturnsLowestIdProvider()
    {
        // 2 provider cùng model id — 3A lấy Id nhỏ nhất, chọn provider thật là việc 3B (spec §2.3)
        SeedProvider("first", modelIds: ["shared-model"]);
        SeedProvider("second", modelIds: ["shared-model"]);

        var result = await CreateSut().ResolveAsync("shared-model", default);

        var ok = Assert.IsType<ModelResolveSuccess>(result);
        Assert.Equal("first", ok.Provider.Name);
    }

    [Fact]
    public async Task Resolve_WhenAnthropicProviderDisabled_ReturnsNotFound()
    {
        // Anthropic nhưng provider tắt → 404 trước, không phải 503 (spec §2.3 thứ tự check)
        SeedProvider("claude-off", ProviderType.Anthropic, enabled: false, modelIds: ["sonnet-4"]);

        var result = await CreateSut().ResolveAsync("sonnet-4", default);

        var fail = Assert.IsType<ModelResolveFailure>(result);
        Assert.Equal(ResolveFailure.NotFound, fail.Reason);
    }
}
```

- [ ] **Step 2: Chạy test — kỳ vọng FAIL (compile error)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ModelResolverTests" --nologo`
Expected: FAIL — `CS0246` (`IModelResolver`/`ModelResolver` chưa tồn tại)

- [ ] **Step 3: Viết `IModelResolver.cs`**

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>Phân loại lỗi resolve model — map sang error contract (spec 3A §4).</summary>
public enum ResolveFailure
{
    /// <summary>Model không tồn tại / đã tắt / provider đã tắt → 404.</summary>
    NotFound = 0,

    /// <summary>Provider là Anthropic — 3A chưa dịch thuật → 503 (spec §1.3 gate 2).</summary>
    AnthropicNotSupported = 1,
}

/// <summary>Kết quả resolve model — Success (provider + model) hoặc Failure có lý do.</summary>
public abstract record ModelResolveResult;

/// <summary>Resolve thành công — provider đã Enabled, type OpenAI, Accounts đã Include.</summary>
public sealed record ModelResolveSuccess(Provider Provider, Model Model) : ModelResolveResult;

/// <summary>Resolve thất bại — kèm model id gốc để dựng error message.</summary>
public sealed record ModelResolveFailure(string ModelId, ResolveFailure Reason) : ModelResolveResult;

/// <summary>Map model id (chính xác) sang provider sẽ phục vụ request.</summary>
public interface IModelResolver
{
    /// <summary>
    /// Tìm model enabled có provider enabled; trả về lý do fail phân loại khi không thấy.
    /// </summary>
    /// <param name="modelId">Model id client gửi — so khớp chính xác, không pattern.</param>
    /// <param name="ct">Token hủy theo request.</param>
    Task<ModelResolveResult> ResolveAsync(string modelId, CancellationToken ct);
}
```

- [ ] **Step 4: Viết `ModelResolver.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Storage;

namespace RouterBalancing.Core.Engine;

/// <inheritdoc/>
public sealed class ModelResolver(IDbContextFactory<RouterBalancingDbContext> db) : IModelResolver
{
    /// <inheritdoc/>
    public async Task<ModelResolveResult> ResolveAsync(string modelId, CancellationToken ct)
    {
        using var context = db.CreateDbContext();
        var model = await context.Models.AsNoTracking()
            .Include(m => m.Provider!)
            .ThenInclude(p => p.Accounts)
            .Where(m => m.ModelId == modelId && m.Enabled && m.Provider != null && m.Provider.Enabled)
            .OrderBy(m => m.Id)
            .FirstOrDefaultAsync(ct);

        // Filter trong Where đã lo provider enabled; null (dangling FK) → NotFound
        if (model?.Provider is not { } provider)
            return new ModelResolveFailure(modelId, ResolveFailure.NotFound);

        // Check Anthropic SAU khi qua được filter enabled (spec §2.3): tắt → 404, bật → 503
        if (provider.Type == ProviderType.Anthropic)
            return new ModelResolveFailure(modelId, ResolveFailure.AnthropicNotSupported);

        return new ModelResolveSuccess(provider, model);
    }
}
```

- [ ] **Step 5: Chạy test — kỳ vọng PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ModelResolverTests" --nologo`
Expected: `Passed! - Failed: 0, Passed: 7`

- [ ] **Step 6: Chạy toàn bộ suite**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed! - Failed: 0, Passed: 203` (196 + 7)

- [ ] **Step 7: Commit**

```bash
git add src/RouterBalancing.Core/Engine/IModelResolver.cs src/RouterBalancing.Core/Engine/ModelResolver.cs "router balancing test/Engine/ModelResolverTests.cs"
git commit -m "feat: add model resolver for exact openai model ids"
```

---

### Task 4: `ProviderRequestFactory` POST + `OpenAiUpstreamClient` (TDD)

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/ProviderRequestFactory.cs:19-28` (thêm optional params)
- Create: `src/RouterBalancing.Core/Engine/IUpstreamClient.cs`
- Create: `src/RouterBalancing.Core/Engine/OpenAiUpstreamClient.cs`
- Test: `router balancing test/Providers/ProviderRequestFactoryTests.cs` (2 test mới)
- Test: `router balancing test/Engine/OpenAiUpstreamClientTests.cs`

**Interfaces:**
- Consumes: `ProviderRequestFactory.Create`, `IHttpClientFactory`, entity `Provider`
- Produces (Task 5/6 dùng): `ProviderRequestFactory.Create(provider, apiKey, path?, method?, content?)`, `IUpstreamClient.PostChatCompletionAsync(Provider provider, string apiKey, byte[] body, CancellationToken ct) -> Task<HttpResponseMessage>`, `OpenAiUpstreamClient.HttpClientName = "upstream"`

- [ ] **Step 1: Viết 2 test factory failing**

Thêm vào cuối class `ProviderRequestFactoryTests`:

```csharp
    [Fact]
    public void Create_WhenPostWithContent_KeepsMethodContentAndHeaders()
    {
        var content = new StringContent("""{"x":1}""", System.Text.Encoding.UTF8, "application/json");
        using var request = ProviderRequestFactory.Create(
            P("https://api.openai.com/v1"), "sk-test", "/v1/chat/completions", HttpMethod.Post, content);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.openai.com/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", request.Headers.Authorization.Parameter);
        Assert.Same(content, request.Content);
    }

    [Fact]
    public void Create_WhenMethodOmitted_DefaultsToGetWithoutContent()
    {
        using var request = ProviderRequestFactory.Create(P("https://api.openai.com"), "sk-test");

        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.Content);
    }
```

- [ ] **Step 2: Chạy test factory — kỳ vọng FAIL (compile error 2 test mới)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProviderRequestFactoryTests" --nologo`
Expected: FAIL — `CS1739`/`CS7036` (tham số `method`/`content` không tồn tại)

- [ ] **Step 3: Sửa `ProviderRequestFactory.Create` — thêm optional params**

Thay signature (dòng 19):

```csharp
    /// <summary>
    /// Tạo request tới provider. Header theo Type:
    /// OpenAI → <c>Authorization: Bearer</c>; Anthropic → <c>x-api-key</c> + <c>anthropic-version</c>.
    /// </summary>
    /// <param name="provider">Provider đích.</param>
    /// <param name="apiKey">Key plaintext sẽ gắn Authorization/x-api-key.</param>
    /// <param name="path">Path mặc định <c>/v1/models</c>.</param>
    /// <param name="method">HTTP method — mặc định GET (probe); POST cho chat completion.</param>
    /// <param name="content">Body request — chỉ dùng khi có <paramref name="method"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">Khi Type ngoài 2 giá trị đã biết.</exception>
    public static HttpRequestMessage Create(Provider provider, string apiKey, string? path = null,
        HttpMethod? method = null, HttpContent? content = null)
```

và thay dòng 28:

```csharp
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, url) { Content = content };
```

- [ ] **Step 4: Chạy lại test factory — PASS (8 case: 6 cũ + 2 mới)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProviderRequestFactoryTests" --nologo`
Expected: `Passed! - Failed: 0, Passed: 8`

- [ ] **Step 5: Viết test upstream client failing**

```csharp
using System.Net;
using System.Text;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;

namespace router_balancing_test.Engine;

public class OpenAiUpstreamClientTests
{
    private static Provider P(string baseUrl = "https://api.openai.com/v1") => new()
    {
        Name = "openai-main",
        Type = ProviderType.OpenAI,
        BaseUrl = baseUrl,
    };

    private static HttpResponseMessage SseResponse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream"),
    };

    private sealed record CapturedRequest(HttpMethod Method, Uri? Uri, string? AuthScheme,
        string? AuthParam, string? ContentType, byte[] Body);

    private sealed class FixedHandler(HttpResponseMessage response, Action<CapturedRequest> capture)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Copy ngay tại đây — request bị Dispose khi SendAsync xong, đọc sau là ObjectDisposed
            var body = request.Content is null
                ? []
                : request.Content.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
            capture(new CapturedRequest(request.Method, request.RequestUri,
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter,
                request.Content?.Headers.ContentType?.ToString(), body));
            return Task.FromResult(response);
        }
    }

    private sealed class FixedFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [Fact]
    public async Task PostChatCompletionAsync_SendsPostToChatPath_WithBearerAndJsonBody()
    {
        CapturedRequest? captured = null;
        var client = new HttpClient(new FixedHandler(SseResponse(), c => captured = c));
        var sut = new OpenAiUpstreamClient(new FixedFactory(client));
        var body = Encoding.UTF8.GetBytes("""{"model":"m","messages":[]}""");

        await sut.PostChatCompletionAsync(P(), "sk-live", body, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured.Method);
        // Base kèm /v1 được canonicalize — không ra /v1/v1 (ProviderUrl)
        Assert.Equal("https://api.openai.com/v1/chat/completions", captured.Uri!.ToString());
        Assert.Equal("Bearer", captured.AuthScheme);
        Assert.Equal("sk-live", captured.AuthParam);
        Assert.StartsWith("application/json", captured.ContentType);
        Assert.Equal(body, captured.Body);
    }

    [Fact]
    public async Task PostChatCompletionAsync_ReturnsUpstreamResponse_WithSseHeaders()
    {
        var client = new HttpClient(new FixedHandler(SseResponse(), _ => { }));
        var sut = new OpenAiUpstreamClient(new FixedFactory(client));

        var response = await sut.PostChatCompletionAsync(P(), "k", [], CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("data: [DONE]\n\n", await response.Content.ReadAsStringAsync());
    }
}
```

- [ ] **Step 6: Chạy test upstream — kỳ vọng FAIL (compile error)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~OpenAiUpstreamClientTests" --nologo`
Expected: FAIL — `CS0246` (`IUpstreamClient`/`OpenAiUpstreamClient`)

- [ ] **Step 7: Viết `IUpstreamClient.cs`**

```csharp
using RouterBalancing.Core.Domain;

namespace RouterBalancing.Core.Engine;

/// <summary>Gửi request chat completion tới upstream provider.</summary>
public interface IUpstreamClient
{
    /// <summary>
    /// POST body JSON thô tới <c>{BaseUrl}/v1/chat/completions</c> với key đã giải mã.
    /// Trả response NGAY KHI có đủ header — body còn stream, không buffer (bắt buộc cho SSE).
    /// </summary>
    /// <param name="provider">Provider đích (OpenAI-compatible).</param>
    /// <param name="apiKey">Plaintext key — người gọi đã resolve từ ProviderAccount.</param>
    /// <param name="body">Body JSON thô nguyên trạng từ client.</param>
    /// <param name="ct">Token hủy theo RequestAborted.</param>
    Task<HttpResponseMessage> PostChatCompletionAsync(Provider provider, string apiKey, byte[] body, CancellationToken ct);
}
```

- [ ] **Step 8: Viết `OpenAiUpstreamClient.cs`**

```csharp
using System.Net.Http.Headers;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace RouterBalancing.Core.Engine;

/// <inheritdoc/>
public sealed class OpenAiUpstreamClient(IHttpClientFactory http) : IUpstreamClient
{
    /// <summary>
    /// Tên named HttpClient cho chat streaming — Timeout vô hạn, đăng ký tại
    /// <c>ProxyApp.ConfigureServices</c>. Tách khỏi "provider-probe" (10s) vì
    /// timeout đó sẽ cắt SSE giữa chừng.
    /// </summary>
    public const string HttpClientName = "upstream";

    /// <inheritdoc/>
    public async Task<HttpResponseMessage> PostChatCompletionAsync(
        Provider provider, string apiKey, byte[] body, CancellationToken ct)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = ProviderRequestFactory.Create(
            provider, apiKey, "/v1/chat/completions", HttpMethod.Post, content);

        // ResponseHeadersRead: hoàn tất khi đủ header, body stream tiếp — bắt buộc cho SSE
        return await http.CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}
```

- [ ] **Step 9: Chạy test upstream — PASS; rồi chạy toàn bộ suite**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~OpenAiUpstreamClientTests" --nologo`
Expected: `Passed! - Failed: 0, Passed: 2`

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed! - Failed: 0, Passed: 208` (203 + 2 factory + 2 client + 1 SSE buffering guard `PostChatCompletionAsync_WithDefaultCompletionOption_DoesNotBufferUpstreamBody`)

> Chốt số: Task 2 case 13 (đã tính: 183→196), Task 3 +7 (→203), Task 4 +5 (→**208**). Nếu chạy ra con số khác, dừng lại báo controller — không commit khi số lệch.

- [ ] **Step 10: Commit**

```bash
git add src/RouterBalancing.Core/Providers/ProviderRequestFactory.cs src/RouterBalancing.Core/Engine/IUpstreamClient.cs src/RouterBalancing.Core/Engine/OpenAiUpstreamClient.cs "router balancing test/Providers/ProviderRequestFactoryTests.cs" "router balancing test/Engine/OpenAiUpstreamClientTests.cs"
git commit -m "feat: add openai upstream client for chat completions"
```

---

### Task 5: `ChatCompletionsHandler` (TDD, unit)

**Files:**
- Create: `src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs`
- Test: `router balancing test/Engine/ChatCompletionsHandlerTests.cs`

**Interfaces:**
- Consumes: `ChatRequestValidator` (Task 2), `IModelResolver`/`ModelResolveSuccess`/`ModelResolveFailure` (Task 3), `IUpstreamClient` (Task 4), `ProviderKeyResolver.ResolveFirstEnabledKey(provider, ISecretProtector)`, `ILogService`, `DefaultHttpContext`
- Produces (Task 6 dùng): `ChatCompletionsHandler(IModelResolver, IUpstreamClient, ISecretProtector, ILogService)`, `Task HandleAsync(HttpContext ctx)`

- [ ] **Step 1: Viết test failing (10 test)**

```csharp
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;

namespace router_balancing_test.Engine;

public class ChatCompletionsHandlerTests
{
    private readonly DpapiSecretProtector _protector = new();

    private static byte[] Body(string json) => Encoding.UTF8.GetBytes(json);
    private const string ValidJson = """{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}]}""";

    private Provider SeedProvider(bool withKey = true)
    {
        var provider = new Provider
        {
            Name = "openai-main",
            Type = ProviderType.OpenAI,
            BaseUrl = "https://api.openai.com",
        };
        provider.Models.Add(new Model { ModelId = "gpt-4o-mini", Enabled = true });
        if (withKey)
            provider.Accounts.Add(new ProviderAccount
            {
                Name = "a1",
                Enabled = true,
                ApiKeyEncrypted = _protector.Protect("sk-live"),
            });
        return provider;
    }

    private static ModelResolveSuccess Success(Provider p) =>
        new(p, p.Models[0]);

    private static DefaultHttpContext Ctx(string? json = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(json is null ? [] : Body(json));
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static async Task<(int Status, string? ContentType, string Body)> ReadAsync(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body, Encoding.UTF8);
        return (ctx.Response.StatusCode, ctx.Response.ContentType, await reader.ReadToEndAsync());
    }

    private static HttpResponseMessage Upstream(int status, string body, string mediaType = "application/json") =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class StubResolver(ModelResolveResult result) : IModelResolver
    {
        public Task<ModelResolveResult> ResolveAsync(string modelId, CancellationToken ct) =>
            Task.FromResult(result);
    }

    private sealed class StubUpstream(Func<HttpResponseMessage> factory) : IUpstreamClient
    {
        public byte[]? LastBody { get; private set; }

        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct)
        {
            LastBody = body;
            return Task.FromResult(factory());
        }
    }

    private sealed class ThrowingUpstream(Exception ex) : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(ex);
    }

    private sealed class CapturingLog : ILogService
    {
        public List<string> Infos { get; } = [];
        public List<string> Warns { get; } = [];
        public List<string> Errors { get; } = [];

        public event Action<LogEntry>? LogAdded { add { } remove { } }
        public void Write(LogEntry entry) { }
        public void Info(string message, LogCategory category = LogCategory.App) => Infos.Add(message);
        public void Warn(string message, LogCategory category = LogCategory.App) => Warns.Add(message);
        public void Error(string message, Exception? exception = null, LogCategory category = LogCategory.App) =>
            Errors.Add(message);
        public IReadOnlyList<LogEntry> Query(LogQuery query) => [];
        public int Count(LogQuery query) => 0;
    }

    private ChatCompletionsHandler Create(
        ModelResolveResult resolve, IUpstreamClient upstream, CapturingLog? log = null) =>
        new(new StubResolver(resolve), upstream, _protector, log ?? new CapturingLog());

    [Fact]
    public async Task HandleAsync_WhenJsonInvalid_Returns400OpenAiShapeAndSingleWarn()
    {
        var log = new CapturingLog();
        var sut = Create(
            new ModelResolveFailure("x", ResolveFailure.NotFound),
            new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx("{broken");

        await sut.HandleAsync(ctx);

        var (status, contentType, body) = await ReadAsync(ctx);
        Assert.Equal(400, status);
        Assert.StartsWith("application/json", contentType);
        Assert.Contains("Invalid JSON body", body);
        Assert.Contains("\"invalid_request_error\"", body);
        Assert.Single(log.Warns);
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task HandleAsync_WhenModelMissing_Returns400ParamModel()
    {
        var sut = Create(
            new ModelResolveFailure("x", ResolveFailure.NotFound),
            new StubUpstream(() => Upstream(200, "{}")));
        var ctx = Ctx("""{"messages":[{"role":"user"}]}""");

        await sut.HandleAsync(ctx);

        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(400, status);
        Assert.Contains("Missing required parameter: 'model'.", body);
        Assert.Contains("\"param\":\"model\"", body);
    }

    [Fact]
    public async Task HandleAsync_WhenMessagesMissing_Returns400ParamMessages()
    {
        var sut = Create(
            new ModelResolveFailure("x", ResolveFailure.NotFound),
            new StubUpstream(() => Upstream(200, "{}")));
        var ctx = Ctx("""{"model":"gpt-4o-mini"}""");

        await sut.HandleAsync(ctx);

        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(400, status);
        Assert.Contains("\"param\":\"messages\"", body);
    }

    [Fact]
    public async Task HandleAsync_WhenModelUnknown_Returns404ModelNotFound()
    {
        var log = new CapturingLog();
        var sut = Create(
            new ModelResolveFailure("nope", ResolveFailure.NotFound),
            new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(404, status);
        Assert.Contains("The model 'nope' does not exist", body);
        Assert.Contains("\"model_not_found\"", body);
        Assert.Single(log.Warns);
    }

    [Fact]
    public async Task HandleAsync_WhenProviderAnthropic_Returns503ServerError()
    {
        var log = new CapturingLog();
        var sut = Create(
            new ModelResolveFailure("sonnet-4", ResolveFailure.AnthropicNotSupported),
            new StubUpstream(() => Upstream(200, "{}")), log);
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(503, status);
        Assert.Contains("not supported yet", body);
        Assert.Contains("\"server_error\"", body);
        Assert.Single(log.Warns);
    }

    [Fact]
    public async Task HandleAsync_WhenNoEnabledKey_Returns503WithProviderName()
    {
        var provider = SeedProvider(withKey: false);
        var sut = Create(
            Success(provider),
            new StubUpstream(() => Upstream(200, "{}")));
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(503, status);
        Assert.Contains("No enabled API key for provider 'openai-main'", body);
    }

    [Fact]
    public async Task HandleAsync_WhenUpstreamThrows_Returns502AndLogsError()
    {
        var log = new CapturingLog();
        var sut = Create(
            Success(SeedProvider()),
            new ThrowingUpstream(new HttpRequestException("connection refused")), log);
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        var (status, _, body) = await ReadAsync(ctx);
        Assert.Equal(502, status);
        Assert.Contains("Upstream provider request failed", body);
        Assert.Single(log.Errors);
        Assert.Empty(log.Infos);
    }

    [Fact]
    public async Task HandleAsync_WhenUpstream429_PassesStatusAndBodyThroughWithInfoLog()
    {
        var log = new CapturingLog();
        var upstreamBody = """{"error":{"message":"rate limited"}}""";
        var sut = Create(
            Success(SeedProvider()),
            new StubUpstream(() => Upstream(429, upstreamBody)), log);
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        var (status, contentType, body) = await ReadAsync(ctx);
        Assert.Equal(429, status);
        Assert.Equal(upstreamBody, body);
        Assert.StartsWith("application/json", contentType);
        // Upstream đã trả response → Info là dòng log duy nhất (spec §6)
        Assert.Single(log.Infos);
        Assert.Empty(log.Warns);
    }

    [Fact]
    public async Task HandleAsync_WhenUpstreamSse_PassesStreamBytesUnchanged()
    {
        var sut = Create(
            Success(SeedProvider()),
            new StubUpstream(() => Upstream(200, "data: {\"x\":1}\n\ndata: [DONE]\n\n", "text/event-stream")));
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        var (status, contentType, body) = await ReadAsync(ctx);
        Assert.Equal(200, status);
        Assert.StartsWith("text/event-stream", contentType);
        Assert.Equal("data: {\"x\":1}\n\ndata: [DONE]\n\n", body);
    }

    [Fact]
    public async Task HandleAsync_WhenSuccess_LogsExactlyOneInfoWithModelAndProvider()
    {
        var log = new CapturingLog();
        var provider = SeedProvider();
        var stub = new StubUpstream(() => Upstream(200, "{}"));
        var sut = Create(Success(provider), stub, log);
        var ctx = Ctx(ValidJson);

        await sut.HandleAsync(ctx);

        Assert.Single(log.Infos);
        Assert.Contains("gpt-4o-mini", log.Infos[0]);
        Assert.Contains("openai-main", log.Infos[0]);
        Assert.Contains("HTTP 200", log.Infos[0]);
        Assert.Empty(log.Warns);
        Assert.Empty(log.Errors);
    }
}
```

Lưu ý: test `HandleAsync_WhenSuccess_...` cũng là nơi gián tiếp xác nhận raw body được forward (stub ghi `LastBody`) — nếu muốn thêm assert, so `stub.LastBody` với `Body(ValidJson)` trong test `WhenUpstreamSse`.

- [ ] **Step 2: Chạy test — kỳ vọng FAIL (compile error)**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ChatCompletionsHandlerTests" --nologo`
Expected: FAIL — `CS0246` (`ChatCompletionsHandler`)

- [ ] **Step 3: Viết `ChatCompletionsHandler.cs`**

```csharp
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Providers;
using RouterBalancing.Core.Security;
using System.Text.Json;

namespace RouterBalancing.Core.Engine;

/// <summary>
/// Orchestrator cho POST /v1/chat/completions: validate → resolve → key → upstream → stream về.
/// Singleton, không giữ state per-request — mọi trạng thái qua <see cref="HttpContext"/>.
/// </summary>
public sealed class ChatCompletionsHandler(
    IModelResolver resolver,
    IUpstreamClient upstream,
    ISecretProtector protector,
    ILogService log)
{
    /// <summary>
    /// Xử lý 1 request chat: trả response (thành công stream hoặc error JSON §4) và đúng 1 dòng log.
    /// </summary>
    /// <param name="ctx">HttpContext của request hiện tại.</param>
    public async Task HandleAsync(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;

        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await ctx.Request.Body.CopyToAsync(buffer, ct);
            body = buffer.ToArray();
        }

        var validation = ChatRequestValidator.Validate(body);
        if (!validation.IsValid)
        {
            var (message, param) = validation.Failure switch
            {
                ValidationFailure.MissingModel =>
                    ("Missing required parameter: 'model'.", "model"),
                ValidationFailure.MissingMessages =>
                    ("Missing required parameter: 'messages'.", "messages"),
                _ => ("Invalid JSON body", null),
            };
            log.Warn($"Yêu cầu chat không hợp lệ: {validation.Failure}.", LogCategory.Request);
            await WriteErrorAsync(ctx, 400, message, "invalid_request_error", param, null);
            return;
        }

        var modelId = validation.ModelId!;
        var resolved = await resolver.ResolveAsync(modelId, ct);
        if (resolved is ModelResolveFailure failure)
        {
            if (failure.Reason == ResolveFailure.NotFound)
            {
                log.Warn($"Model '{failure.ModelId}' không tồn tại hoặc đã tắt.", LogCategory.Request);
                await WriteErrorAsync(ctx, 404, $"The model '{failure.ModelId}' does not exist",
                    "invalid_request_error", "model", "model_not_found");
            }
            else
            {
                log.Warn($"Model '{failure.ModelId}' thuộc provider Anthropic — chưa hỗ trợ (3E).",
                    LogCategory.Request);
                await WriteErrorAsync(ctx, 503, $"Model '{failure.ModelId}' is not supported yet",
                    "server_error", null, null);
            }

            return;
        }

        var success = (ModelResolveSuccess)resolved;
        var key = ProviderKeyResolver.ResolveFirstEnabledKey(success.Provider, protector);
        if (key is null)
        {
            log.Warn($"Provider '{success.Provider.Name}' không có account enabled nào.",
                LogCategory.Request);
            await WriteErrorAsync(ctx, 503,
                $"No enabled API key for provider '{success.Provider.Name}'",
                "server_error", null, null);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await upstream.PostChatCompletionAsync(success.Provider, key, body, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   && !ctx.RequestAborted.IsCancellationRequested)
        {
            // Client tự ngắt (RequestAborted) thì để propagate — không phải lỗi upstream
            log.Error($"Không kết nối được upstream '{success.Provider.Name}'.", ex, LogCategory.Request);
            await WriteErrorAsync(ctx, 502, "Upstream provider request failed", "server_error", null, null);
            return;
        }

        using (response)
        {
            ctx.Response.StatusCode = (int)response.StatusCode;
            if (response.Content.Headers.ContentType is { } contentType)
                ctx.Response.ContentType = contentType.ToString();

            await response.Content.CopyToAsync(ctx.Response.Body, ct);
            log.Info(
                $"Chuyển tiếp '{success.Model.ModelId}' → '{success.Provider.Name}': " +
                $"HTTP {(int)response.StatusCode} trong {stopwatch.ElapsedMilliseconds}ms",
                LogCategory.Request);
        }
    }

    private static async Task WriteErrorAsync(HttpContext ctx, int status, string message,
        string type, string? param, string? code)
    {
        ctx.Response.StatusCode = status;
        // Serialize trực tiếp (không WriteAsJsonAsync) để ContentType đúng như middleware: application/json
        ctx.Response.ContentType = "application/json";
        var payload = JsonSerializer.Serialize(new { error = new { message, type, param, code } });
        await ctx.Response.WriteAsync(payload, ctx.RequestAborted);
    }
}
```

- [ ] **Step 4: Chạy test — kỳ vọng PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ChatCompletionsHandlerTests" --nologo`
Expected: `Passed! - Failed: 0, Passed: 10`

- [ ] **Step 5: Chạy toàn bộ suite**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed! - Failed: 0, Passed: 218` (208 + 10)

- [ ] **Step 6: Commit**

```bash
git add src/RouterBalancing.Core/Engine/ChatCompletionsHandler.cs "router balancing test/Engine/ChatCompletionsHandlerTests.cs"
git commit -m "feat: add chat completions handler with openai error contract"
```

---

### Task 6: Wire endpoint + Integration tests (TestServer)

**Files:**
- Modify: `src/RouterBalancing.Core/Server/ProxyApp.cs` (đăng ký engine + `MapPost`)
- Modify: `router balancing test/router balancing test.csproj` (FrameworkReference + TestHost)
- Test: `router balancing test/Server/ProxyAppChatIntegrationTests.cs`

**Interfaces:**
- Consumes: toàn bộ Task 1–5; `Microsoft.AspNetCore.TestHost` 10.0.12 (đã xác minh có trên nuget.org)
- Produces: endpoint `POST /v1/chat/completions` chạy thật trong pipeline (auth → handler → upstream)

- [ ] **Step 1: Thêm package + FrameworkReference vào test csproj**

Sau `<PropertyGroup>` đóng (sau dòng 9), thêm:

```xml
  <ItemGroup>
    <!-- WebApplication/UseTestServer trong integration test — host của app nằm trong Core (FrameworkReference sẵn) -->
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
```

Vào `<ItemGroup>` package (sau `xunit.runner.visualstudio`):

```xml
    <PackageReference Include="Microsoft.AspNetCore.TestHost" Version="10.0.12" />
```

- [ ] **Step 2: Restore — verify package resolve**

Run: `dotnet restore "router balancing test/router balancing test.csproj"`
Expected: exit 0, không lỗi NU1101/NU1202. Nếu version không tìm thấy: `dotnet package search Microsoft.AspNetCore.TestHost --take 3` rồi pin bản 10.0.x cao nhất và báo controller trước khi tiếp.

- [ ] **Step 3: Sửa `ProxyApp.ConfigureServices` — đăng ký engine + streaming client**

Thay toàn bộ method `ConfigureServices` trong `ProxyApp.cs`:

```csharp
    public static void ConfigureServices(WebApplicationBuilder builder, ISecretProtector protector)
    {
        builder.Services.AddSingleton(protector);

        // Streaming SSE vô hạn — timeout (mặc định 100s) cắt giữa chừng là mất stream;
        // fail kết nối do ConnectTimeout để không treo vô hạn khi upstream chết.
        // ConfigurePrimaryHttpMessageHandler là extension trên IHttpClientBuilder
        // (chaining sau AddHttpClient) - gọi trên HttpClient (client => ...) là CS1929.
        builder.Services.AddHttpClient(OpenAiUpstreamClient.HttpClientName,
            client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(10),
            });

        builder.Services.AddSingleton<IModelResolver, ModelResolver>();
        builder.Services.AddSingleton<IUpstreamClient, OpenAiUpstreamClient>();
        builder.Services.AddSingleton<ChatCompletionsHandler>();
    }
```

Thêm usings vào đầu `ProxyApp.cs`: `using System.Net.Http;`, `using Microsoft.Extensions.DependencyInjection;` (đã có), `using RouterBalancing.Core.Engine;`.

- [ ] **Step 4: Thêm `MapPost` vào `ConfigurePipeline`**

```csharp
    public static void ConfigurePipeline(WebApplication app)
    {
        app.UseMiddleware<ApiKeyMiddleware>();

        // Minimal API resolve ChatCompletionsHandler từ DI (singleton) theo request
        app.MapPost("/v1/chat/completions",
            (ChatCompletionsHandler handler, HttpContext ctx) => handler.HandleAsync(ctx));

        MapEndpoints(app);
    }
```

- [ ] **Step 5: Viết integration test (8 test)**

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Engine;
using RouterBalancing.Core.Logging;
using RouterBalancing.Core.Security;
using RouterBalancing.Core.Server;
using RouterBalancing.Core.Settings;
using RouterBalancing.Core.Storage;

namespace router_balancing_test.Server;

/// <summary>
/// Integration in-proc qua TestServer (spec §7.2): cùng pipeline với ProxyHost thật,
/// khác mỗi IUpstreamClient được stub để không phụ thuộc mạng.
/// </summary>
public class ProxyAppChatIntegrationTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly AppSettingsService _settings;
    private readonly LogService _log;
    private readonly DpapiSecretProtector _protector = new();
    private WebApplication? _app;
    private HttpClient? _client;

    public ProxyAppChatIntegrationTests()
    {
        // TestDb chỉ tạo file trống — migrate trước khi AppSettingsService đọc
        DbInitializer.Initialize(_db.CreateFactory());
        var factory = _db.CreateFactory();
        _settings = new AppSettingsService(factory, new DpapiSecretProtector());
        _log = new LogService(factory);
    }

    public void Dispose()
    {
        _client?.Dispose();
        _app?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _log.Dispose();
        _settings.Dispose();
        _db.Dispose();
    }

    private void SeedProvider(string modelId, ProviderType type = ProviderType.OpenAI)
    {
        using var db = _db.CreateFactory().CreateDbContext();
        var provider = new Provider
        {
            Name = $"p-{modelId}",
            BaseUrl = "https://api.openai.com",
            Type = type,
        };
        provider.Models.Add(new Model { ModelId = modelId, Enabled = true });
        provider.Accounts.Add(new ProviderAccount
        {
            Name = "a1",
            Enabled = true,
            ApiKeyEncrypted = _protector.Protect("sk-live"),
        });
        db.Providers.Add(provider);
        db.SaveChanges();
    }

    private sealed class StubUpstream(Func<HttpResponseMessage> factory) : IUpstreamClient
    {
        public Task<HttpResponseMessage> PostChatCompletionAsync(
            Provider provider, string apiKey, byte[] body, CancellationToken ct) =>
            Task.FromResult(factory());
    }

    private static HttpResponseMessage Sse() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("data: {\"x\":1}\n\ndata: [DONE]\n\n",
            Encoding.UTF8, "text/event-stream"),
    };

    private async Task<HttpClient> StartAsync(IUpstreamClient upstream)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IAppSettingsService>(_settings);
        builder.Services.AddSingleton<ILogService>(_log);
        builder.Services.AddSingleton<IDbContextFactory<RouterBalancingDbContext>>(_db.CreateFactory());

        ProxyApp.ConfigureServices(builder, _protector);
        // Đăng ký SAU ConfigureServices → wins (last registration), stub thay OpenAiUpstreamClient
        builder.Services.AddSingleton(upstream);

        var app = builder.Build();
        ProxyApp.ConfigurePipeline(app);
        await app.StartAsync();
        _app = app;
        _client = app.GetTestClient();
        return _client;
    }

    private static StringContent Json(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Chat_WhenApiKeyEnabledAndMissing_Returns401OpenAiShape()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");
        SeedProvider("gpt-4o-mini");
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"gpt-4o-mini","messages":[{"role":"user"}]}"""));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("invalid_api_key", json.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Chat_WhenValidRequest_PassesSseThroughUnchanged()
    {
        SeedProvider("gpt-4o-mini");
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"gpt-4o-mini","messages":[{"role":"user","content":"hi"}],"stream":true}"""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("data: {\"x\":1}\n\ndata: [DONE]\n\n", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Chat_WhenInvalidJson_Returns400OpenAiShape()
    {
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.PostAsync("/v1/chat/completions",
            new StringContent("{broken", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await ReadJson(response);
        var error = json.GetProperty("error");
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
        Assert.Equal("Invalid JSON body", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Chat_WhenUnknownModel_Returns404ModelNotFound()
    {
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"no-such-model","messages":[{"role":"user"}]}"""));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var json = await ReadJson(response);
        var error = json.GetProperty("error");
        Assert.Equal("model_not_found", error.GetProperty("code").GetString());
        Assert.Equal("model", error.GetProperty("param").GetString());
    }

    [Fact]
    public async Task Chat_WhenProviderAnthropic_Returns503ServerError()
    {
        SeedProvider("sonnet-4", ProviderType.Anthropic);
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"sonnet-4","messages":[{"role":"user"}]}"""));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("server_error", json.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Chat_WhenUpstream429_PassesStatusAndBodyThrough()
    {
        SeedProvider("gpt-4o-mini");
        var upstreamBody = """{"error":{"message":"rate limited","type":"rate_limit_error"}}""";
        var client = await StartAsync(new StubUpstream(() => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(upstreamBody, Encoding.UTF8, "application/json"),
        }));

        var response = await client.PostAsync("/v1/chat/completions",
            Json("""{"model":"gpt-4o-mini","messages":[{"role":"user"}]}"""));

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(upstreamBody, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Health_WhenApiKeyEnabled_StillOpen()
    {
        _settings.SetApiKeyEnabled(true);
        _settings.SetApiKey("secret-key");
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Models_ReturnsEnabledModelsListShape()
    {
        SeedProvider("gpt-4o-mini");
        var client = await StartAsync(new StubUpstream(() => Sse()));

        var response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await ReadJson(response);
        Assert.Equal("list", json.GetProperty("object").GetString());
        var data = json.GetProperty("data").EnumerateArray().ToList();
        Assert.Single(data);
        Assert.Equal("gpt-4o-mini", data[0].GetProperty("id").GetString());
    }
}
```

- [ ] **Step 6: Chạy integration test — kỳ vọng PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --filter "FullyQualifiedName~ProxyAppChatIntegrationTests" --nologo`
Expected: `Passed! - Failed: 0, Passed: 8`
(Nếu fail do `UseTestServer`/namespace — kiểm tra using `Microsoft.AspNetCore.TestHost` + FrameworkReference đã thêm đúng.)

- [ ] **Step 7: Chạy toàn bộ suite**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: `Passed! - Failed: 0, Passed: 226` (218 + 8)

> Chốt số cuối: **226 tests** (183 baseline + 43). Nếu lệch — dừng, báo controller.

- [ ] **Step 8: Build app 0W/0E**

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 9: Commit**

```bash
git add src/RouterBalancing.Core/Server/ProxyApp.cs "router balancing test/router balancing test.csproj" "router balancing test/Server/ProxyAppChatIntegrationTests.cs"
git commit -m "feat: route chat completions through proxy pipeline"
```

---

### Task 7: e2e smoke scripts (mock upstream + curl)

**Files:**
- Create: `scripts/mock-upstream.mjs`
- Create: `scripts/e2e-3a.sh`

**Interfaces:**
- Consumes: endpoint đã có (Task 6), port mặc định 8317 (settings), `scripts/` (thư mục mới)
- Produces: script e2e chạy tay trong Final Gates (controller) — fail-fast prereq, exit 0 = ALL PASS

- [ ] **Step 1: Viết `scripts/mock-upstream.mjs`**

```js
// Mock upstream OpenAI-compatible cho e2e slice 3A.
// Nhận POST /v1/chat/completions (yêu cầu Bearer) và trả SSE echo — không phụ thuộc mạng thật.
import http from 'node:http';

const PORT = Number(process.argv[2] ?? 9999);

const server = http.createServer((req, res) => {
  if (req.method !== 'POST' || !req.url?.includes('/v1/chat/completions')) {
    res.writeHead(404, { 'content-type': 'application/json' });
    res.end(JSON.stringify({ error: { message: 'not found' } }));
    return;
  }

  const chunks = [];
  req.on('data', (c) => chunks.push(c));
  req.on('end', () => {
    const auth = req.headers.authorization ?? '';
    if (!auth.startsWith('Bearer ') || auth.length <= 'Bearer '.length) {
      res.writeHead(401, { 'content-type': 'application/json' });
      res.end(JSON.stringify({ error: { message: 'missing bearer', type: 'invalid_request_error' } }));
      return;
    }

    const body = Buffer.concat(chunks).toString('utf8');
    res.writeHead(200, { 'content-type': 'text/event-stream' });
    res.write(`data: ${JSON.stringify({ echo: JSON.parse(body), key: auth.slice(7) })}\n\n`);
    res.write('data: [DONE]\n\n');
    res.end();
  });
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`mock upstream listening on http://127.0.0.1:${PORT}`);
});
```

- [ ] **Step 2: Viết `scripts/e2e-3a.sh`**

```bash
#!/usr/bin/env bash
# e2e smoke slice 3A (proxy core) — chạy TAY, không thuộc dotnet test.
# Prerequisites (fail-fast nếu thiếu):
#   1. App đang chạy, proxy tại $BASE (mặc định http://127.0.0.1:8317)
#   2. node scripts/mock-upstream.mjs 9999 đang chạy
#   3. Provider 'e2e-mock' trong app: BaseUrl http://127.0.0.1:9999, type OpenAI,
#      >=1 model enabled (id = $MODEL_ID, mặc định e2e-mock-model), >=1 account key enabled
# Cách chạy: bash scripts/e2e-3a.sh   (hoặc MODEL_ID=... BASE=... bash scripts/e2e-3a.sh)
set -euo pipefail

BASE="${BASE:-http://127.0.0.1:8317}"
MODEL_ID="${MODEL_ID:-e2e-mock-model}"
BODY_FILE="$(mktemp)"
trap 'rm -f "$BODY_FILE"' EXIT
FAIL=0

check() { # $1 = tên, $2 = status mong đợi, còn lại = args curl
  local name="$1" expected="$2" actual
  shift 2
  actual=$(curl -s -o "$BODY_FILE" -w '%{http_code}' "$@") || actual=000
  if [[ "$actual" == "$expected" ]]; then
    echo "PASS - $name"
  else
    echo "FAIL - $name (nhận $actual, mong đợi $expected)"
    cat "$BODY_FILE"; echo
    FAIL=1
  fi
}

expect_body() { # $1 = chuỗi cần có, $2 = tên
  if grep -qF "$1" "$BODY_FILE"; then
    echo "PASS - $2"
  else
    echo "FAIL - $2 (không thấy '$1')"
    cat "$BODY_FILE"; echo
    FAIL=1
  fi
}

if ! curl -sf "$BASE/health" >/dev/null; then
  echo "FAIL - proxy không phản hồi tại $BASE/health (app chưa chạy?)"
  exit 1
fi
echo "PASS - proxy alive"

check "GET /health -> 200" 200 "$BASE/health"

check "chat stream -> 200" 200 \
  -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json' \
  -d "{\"model\":\"$MODEL_ID\",\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}],\"stream\":true}"
expect_body 'data:' "SSE có data: chunk"
expect_body '[DONE]' "SSE kết thúc [DONE]"

check "JSON hỏng -> 400" 400 \
  -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json' -d '{broken'
expect_body 'invalid_request_error' "400 type invalid_request_error"
expect_body 'Invalid JSON body' "400 message"

check "model lạ -> 404" 404 \
  -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json' \
  -d '{"model":"no-such-model","messages":[{"role":"user","content":"hi"}]}'
expect_body 'model_not_found' "404 code model_not_found"

check "thiếu messages -> 400" 400 \
  -X POST "$BASE/v1/chat/completions" -H 'Content-Type: application/json' \
  -d "{\"model\":\"$MODEL_ID\"}"
expect_body "'messages'" "400 param messages"

if [[ $FAIL -eq 0 ]]; then
  echo "ALL PASS"
else
  echo "CÓ TEST FAIL"
  exit 1
fi
```

- [ ] **Step 3: Verify script syntax**

Run: `bash -n scripts/e2e-3a.sh && node --check scripts/mock-upstream.mjs`
Expected: exit 0, không output.

- [ ] **Step 4: Self-test mock (không cần app) — start mock, curl, kill**

```powershell
$mock = Start-Process node -ArgumentList "scripts/mock-upstream.mjs","9999" -PassThru -WindowStyle Hidden
Start-Sleep -Seconds 1
# Body curl dùng single-quote: PowerShell 7.6 (native arg passing) làm hỏng -d "{\"model\":\"m\"}"
$ok = curl.exe -s -o - -w "|%{http_code}" -X POST http://127.0.0.1:9999/v1/chat/completions -H "Authorization: Bearer sk-test" -H "Content-Type: application/json" -d '{"model":"m"}'
$unauth = curl.exe -s -o - -w "|%{http_code}" -X POST http://127.0.0.1:9999/v1/chat/completions -H "Content-Type: application/json" -d '{}'
Stop-Process -Id $mock.Id -Force
$ok; $unauth
```

Expected: dòng 1 chứa `data:` + `|200`; dòng 2 chứa `missing bearer` + `|401`.

- [ ] **Step 5: Commit**

```bash
git add scripts/mock-upstream.mjs scripts/e2e-3a.sh
git commit -m "test: add e2e smoke script for proxy core slice3a"
```

---

## Final Gates (controller — trước khi merge)

- [ ] `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo` → **0W/0E**
- [ ] `dotnet test "router balancing test/router balancing test.csproj" --nologo` → **226/0** (app đã đóng)
- [ ] Parity i18n: `Select-String -Path "src\RouterBalancing.Core\Localization\Translations.cs" -Pattern '\["'` → EN=208 (dòng < 232), VI=208 (dòng ≥ 232)
- [ ] `git status --short` sạch
- [ ] **e2e run thật** (controller): mở app → cấu hình provider `e2e-mock` (BaseUrl `http://127.0.0.1:9999`, model `e2e-mock-model` enabled, 1 account key) — cấu hình qua CDP/UI pattern drive40 → start `node scripts/mock-upstream.mjs 9999` → `bash scripts/e2e-3a.sh` → **ALL PASS**
- [ ] Final review (subagent, cumulative diff `ff64ebd..HEAD`) → merge local vào `master` (**KHÔNG push**) → ledger MERGED

## Handoff ghi chú (đã có trong spec §8 — nhắc nhanh cho reviewer)

- 3B chèn selection sau `ModelResolver` (3A để thứ tự `Id` tăng dần), không đổi validator/error/streaming/logging.
- `Provider.MaxConcurrent` chưa dùng ở 3A — 3B dùng làm giới hạn concurrency.
- `IUpstreamClient` là seam 3C (retry) và 3D (đổi key resolution tại `ProviderKeyResolver` call trong handler).
