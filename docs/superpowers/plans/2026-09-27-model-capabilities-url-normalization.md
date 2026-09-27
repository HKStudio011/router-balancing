# Model Capabilities + URL Normalization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cho phép user sửa tay capabilities (Context window/Vision/Think) của model inline trong `/providers`, mở rộng endpoint parser đọc field tên thay thế, hiện placeholder `—`, và chuẩn hoá BaseUrl để không bao giờ ghép ra `/v1/v1`.

**Architecture:** Backend TDD trong `RouterBalancing.Core.Providers` (helper `ProviderUrl`, service method `UpdateCapabilitiesAsync`, parser alt-fields) rồi UI Blazor inline-edit trong `Providers.razor` + 7 key i18n EN/VI. Không đụng fill chain, không migration.

**Tech Stack:** .NET 10 / MAUI Blazor Hybrid, EF Core (SQLite), xUnit + pattern `TestDb`/`ServiceWith`/`SeedProviderAsync`, Tailwind build qua Vite.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-09-27-model-capabilities-url-normalization-design.md` (§3.1 inline edit, §3.2 alt-fields, §3.3 placeholder `—`, §3.4 URL `/v1`, §4 i18n keys, §5 tests).
- Branch: tạo `feat/model-capabilities-url` từ `master` (đã merge Phase 2A, head `a0d7d1a`) — Task 1 Step 1.
- TFM: `net10.0-windows10.0.19041.0`; `Nullable=enable`, `ImplicitUsings=enable` — giữ nguyên.
- Test (folder VÀ file có space): `dotnet test "router balancing test/router balancing test.csproj" --nologo` — baseline **100 tests xanh trước khi bắt tay**.
- Build app: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo` → 0 Warning / 0 Error.
- npm: `npm run build` với `workdir=router-balancing/vite-project`; nếu `router-balancing/wwwroot/build/` đổi → `git add` kèm.
- Comment "why" tiếng Việt, không lặp "what"; XML doc `///` bắt buộc với public API; member-level `[Parameter]` Razor giữ `<inheritdoc/>`.
- Commit message tiếng Anh conventional (`feat:`/`fix:`); không commit `.superpowers/`; không `git checkout`/`reset`/`stash`/`commit --amend`.
- Không bao giờ render/log API key.
- Text hiển thị qua `L["key"]`, đủ EN/VI (parity 149/149 hiện có → 156/156 sau khi thêm 7 keys).
- Dùng test doubles sẵn có trong `router balancing test/TestDoubles.cs` (`NullLog`, `NeverHttpFactory`); không thêm dependency.

## File Structure

| File | Trách nhiệm | Task |
|---|---|---|
| Create `src/RouterBalancing.Core/Providers/ProviderUrl.cs` | Canonicalize BaseUrl (strip trailing `/` + hậu tố `/v1` lặp) — single source dùng cả save lẫn compose | 1 |
| Create `router balancing test/Providers/ProviderUrlTests.cs` | Unit test canonicalize | 1 |
| Create `router balancing test/Providers/ProviderRequestFactoryTests.cs` | Unit test ghép URL không đôi `/v1` | 1 |
| Modify `src/RouterBalancing.Core/Providers/ProviderRequestFactory.cs:19-22` | Compose idempotent (canonicalize khi path bắt đầu `/v1`) | 1 |
| Modify `src/RouterBalancing.Core/Providers/ProviderService.cs:56,78` | Save canonical (`BaseUrl = ProviderUrl.Canonicalize(...)`) | 1 |
| Modify `router balancing test/Providers/ProviderServiceTests.cs` | Test save canonical | 1 |
| Modify `src/RouterBalancing.Core/Providers/ProviderEndpointMetadataProvider.cs:42-106` | Đọc `context_length`/`max_model_len` + `architecture.*modalities` (fallback) | 2 |
| Modify `router balancing test/Providers/ProviderEndpointMetadataProviderTests.cs` | Test alt-fields + precedence | 2 |
| Modify `src/RouterBalancing.Core/Providers/IModelService.cs` | `UpdateCapabilitiesAsync` contract | 3 |
| Modify `src/RouterBalancing.Core/Providers/ModelService.cs` | Implement `UpdateCapabilitiesAsync` | 3 |
| Modify `router balancing test/Providers/ModelServiceTests.cs` | Test set/clear/range/unknown | 3 |
| Modify `src/RouterBalancing.Core/Localization/Translations.cs` (EN ~dòng 160, VI ~dòng 323) | 7 keys mới | 4 |
| Modify `router-balancing/Components/Pages/Providers.razor` (cell Metadata dòng 196-215, state ~384, handler ~620) | Edit inline + placeholder `—` | 4 |

---

### Task 1: Chuẩn hoá URL `/v1` (helper + factory + save)

**Files:**
- Create: `src/RouterBalancing.Core/Providers/ProviderUrl.cs`
- Create: `router balancing test/Providers/ProviderUrlTests.cs`
- Create: `router balancing test/Providers/ProviderRequestFactoryTests.cs`
- Modify: `src/RouterBalancing.Core/Providers/ProviderRequestFactory.cs:19-22`
- Modify: `src/RouterBalancing.Core/Providers/ProviderService.cs:56,78`
- Test: `router balancing test/Providers/ProviderServiceTests.cs`

**Interfaces:**
- Consumes: `Provider`, `ProviderType`, `ProviderDraft` (record mutable: `Name`, `Type`, `BaseUrl`, `ApiKey`, `MaxConcurrent`), `IHttpClientFactory` pattern hiện có.
- Produces: `public static class ProviderUrl` với `public static string Canonicalize(string baseUrl)`; `ProviderRequestFactory.Create(provider, apiKey, path)` giữ nguyên signature (Task 2/4 vẫn gọi); `ProviderService.CreateAsync/UpdateAsync` trả/persist `BaseUrl` đã canonical.

- [ ] **Step 1: Tạo nhánh**

```powershell
git checkout -b feat/model-capabilities-url
```

Expected: `Switched to a new branch 'feat/model-capabilities-url'`

- [ ] **Step 2: Viết failing test `ProviderUrlTests`**

```csharp
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class ProviderUrlTests
{
    [Theory]
    [InlineData("https://api.openai.com", "https://api.openai.com")]
    [InlineData("https://api.openai.com/", "https://api.openai.com")]
    [InlineData("https://api.openai.com/v1", "https://api.openai.com")]
    [InlineData("https://api.openai.com/v1/", "https://api.openai.com")]
    [InlineData("https://gw.example.com/api/v1", "https://gw.example.com/api")]
    [InlineData("https://gw.example.com/v1/v1", "https://gw.example.com")]
    [InlineData("  https://gw.example.com/v1  ", "https://gw.example.com")]
    public void Canonicalize_WhenVariousForms_StripsTrailingSlashAndVersionSuffix(
        string input, string expected)
    {
        Assert.Equal(expected, ProviderUrl.Canonicalize(input));
    }

    [Fact]
    public void Canonicalize_WhenBaseIsPrefixOfVersion_KeepsIt()
    {
        // Host/path không được cắt oan: chỉ strip đúng hậu tố "/v1"
        Assert.Equal("https://api.v2.example.com", ProviderUrl.Canonicalize("https://api.v2.example.com"));
    }
}
```

- [ ] **Step 3: Chạy test — FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo --filter ProviderUrlTests`
Expected: FAIL — `ProviderUrl` không tồn tại (`error CS0246`).

- [ ] **Step 4: Viết `ProviderUrl.cs`**

```csharp
namespace RouterBalancing.Core.Providers;

/// <summary>
/// Chuẩn hoá BaseUrl provider — chống ghép ra <c>/v1/v1/...</c> khi user dán base kèm <c>/v1</c>.
/// </summary>
public static class ProviderUrl
{
    /// <summary>
    /// Bỏ whitespace đầu/cuối, rồi lặp strip trailing slash và hậu tố <c>/v1</c>
    /// cho đến khi ổn định (xử lý cả base từng bị lưu <c>.../v1/v1</c>).
    /// </summary>
    /// <param name="baseUrl">Base do người dùng nhập, có hoặc không <c>/v1</c>.</param>
    /// <returns>Base canonical: không trailing slash, không hậu tố <c>/v1</c>.</returns>
    public static string Canonicalize(string baseUrl)
    {
        var result = baseUrl.Trim();
        bool changed;
        do
        {
            changed = false;
            var noSlash = result.TrimEnd('/');
            if (!string.Equals(noSlash, result, StringComparison.Ordinal))
            {
                result = noSlash;
                changed = true;
            }
            // OrdinalIgnoreCase: gateway có thể in hoa /V1 nhưng HTTP path chuẩn là lowercase
            if (result.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                result = result[..^"/v1".Length];
                changed = true;
            }
        } while (changed);
        return result;
    }
}
```

- [ ] **Step 5: Chạy test — PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo --filter ProviderUrlTests`
Expected: PASS (8 test).

- [ ] **Step 6: Viết failing test `ProviderRequestFactoryTests`**

```csharp
using RouterBalancing.Core.Domain;
using RouterBalancing.Core.Providers;

namespace router_balancing_test.Providers;

public class ProviderRequestFactoryTests
{
    private static Provider P(string baseUrl) => new()
    {
        Name = "P",
        Type = ProviderType.OpenAI,
        BaseUrl = baseUrl,
        ApiKeyEncrypted = string.Empty,
    };

    [Theory]
    [InlineData("https://api.openai.com")]
    [InlineData("https://api.openai.com/")]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("https://api.openai.com/v1/")]
    public void Create_WhenDefaultPath_AppearsExactlyOneV1(string baseUrl)
    {
        using var request = ProviderRequestFactory.Create(P(baseUrl), "sk-test");

        Assert.Equal("https://api.openai.com/v1/models", request.RequestUri!.ToString());
    }

    [Fact]
    public void Create_WhenBaseHasVersionAndPathIsVersioned_NoDuplicateV1()
    {
        // Base dán kèm /v1 (row lưu trước khi có save-fix) — compose vẫn không đôi v1
        using var request = ProviderRequestFactory.Create(
            P("https://gw.example.com/v1"), "k", "/v1/models/gpt-4o");

        Assert.Equal("https://gw.example.com/v1/models/gpt-4o", request.RequestUri!.ToString());
    }

    [Fact]
    public void Create_WhenPathHasNoVersion_PreservesBaseVersion()
    {
        // Chỉ canonicalize khi path tự bắt đầu /v1 — không đoán với path lạ
        using var request = ProviderRequestFactory.Create(
            P("https://gw.example.com/v1"), "k", "/models");

        Assert.Equal("https://gw.example.com/v1/models", request.RequestUri!.ToString());
    }
}
```

- [ ] **Step 7: Chạy test — FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo --filter ProviderRequestFactoryTests`
Expected: FAIL — 2/4 test đầu PASS (đã trim slash), 2 test có `/v1` FAIL với URL `/v1/v1/models`.

- [ ] **Step 8: Sửa `ProviderRequestFactory.Create`**

Thay dòng 21:

```csharp
// Canonicalize ngay lúc ghép (idempotent): fix runtime cho row lưu trước khi có save-fix
// — chỉ khi path tự có /v1, không đoán với path không version
var requestPath = path ?? "/v1/models";
var baseUrl = requestPath.StartsWith("/v1", StringComparison.Ordinal)
    ? ProviderUrl.Canonicalize(provider.BaseUrl)
    : provider.BaseUrl.TrimEnd('/');
var url = baseUrl + requestPath;
```

Giữ nguyên phần `HttpRequestMessage` + switch header bên dưới.

- [ ] **Step 9: Chạy test — PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo --filter ProviderRequestFactoryTests`
Expected: PASS (6 test).

- [ ] **Step 10: Viết failing test save-canonical trong `ProviderServiceTests`**

Thêm vào class (dùng helper `Draft` có sẵn — sửa `BaseUrl` qua object initializer mới vì `Draft()` hardcode):

```csharp
[Theory]
[InlineData("https://api.example.com/v1", "https://api.example.com")]
[InlineData("https://api.example.com/v1/", "https://api.example.com")]
public async Task Create_WhenBaseUrlEndsWithV1_PersistsCanonicalBaseUrl(string input, string expected)
{
    var provider = await _service.CreateAsync(new ProviderDraft
    {
        Name = "P",
        Type = ProviderType.OpenAI,
        BaseUrl = input,
        ApiKey = "sk",
        MaxConcurrent = 4,
    });

    Assert.Equal(expected, provider.BaseUrl);
    using var db = _db.CreateDbContext();
    Assert.Equal(expected, (await db.Providers.SingleAsync(p => p.Id == provider.Id)).BaseUrl);
}

[Fact]
public async Task Update_WhenBaseUrlEndsWithV1_PersistsCanonicalBaseUrl()
{
    var provider = await _service.CreateAsync(Draft());

    await _service.UpdateAsync(provider.Id, new ProviderDraft
    {
        Name = provider.Name,
        Type = ProviderType.OpenAI,
        BaseUrl = "https://api.example.com/api/v1",
        ApiKey = string.Empty, // key rỗng = giữ key cũ (hành vi hiện hữu)
        MaxConcurrent = 4,
    });

    using var db = _db.CreateDbContext();
    var saved = await db.Providers.SingleAsync(p => p.Id == provider.Id);
    Assert.Equal("https://api.example.com/api", saved.BaseUrl);
}
```

- [ ] **Step 11: Chạy test — FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo --filter ProviderServiceTests`
Expected: 2 test mới FAIL (BaseUrl còn `/v1`), test cũ `Create_WhenKeyProvided_EncryptsAndPersists` vẫn PASS.

- [ ] **Step 12: Sửa `ProviderService` — canonicalize tại save**

`CreateAsync` dòng 56 — thay:

```csharp
            BaseUrl = ProviderUrl.Canonicalize(draft.BaseUrl),
```

`UpdateAsync` dòng 78 — thay:

```csharp
        provider.BaseUrl = ProviderUrl.Canonicalize(draft.BaseUrl);
```

- [ ] **Step 13: Chạy toàn bộ test — PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: **117/117 PASS** (100 cũ + 17 mới = 8 ProviderUrl + 6 factory + 3 ProviderService).

- [ ] **Step 14: Commit**

```powershell
git add "src/RouterBalancing.Core/Providers/ProviderUrl.cs" "src/RouterBalancing.Core/Providers/ProviderRequestFactory.cs" "src/RouterBalancing.Core/Providers/ProviderService.cs" "router balancing test/Providers/ProviderUrlTests.cs" "router balancing test/Providers/ProviderRequestFactoryTests.cs" "router balancing test/Providers/ProviderServiceTests.cs"
git commit -m "fix: normalize provider base url to avoid duplicate /v1"
```

---

### Task 2: Endpoint parser đọc field tên thay thế

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/ProviderEndpointMetadataProvider.cs:42-106`
- Test: `router balancing test/Providers/ProviderEndpointMetadataProviderTests.cs`

**Interfaces:**
- Consumes: `ProviderRequestFactory.Create`, `ModelMetadata(contextWindow, vision, SupportsThink, ThinkEfforts, InputModalities, OutputModalities)`, test doubles `JsonHandler`/`StubFactory`/`Pair()` có sẵn trong test file.
- Produces: cùng contract `FetchAsync(Provider, Model, CancellationToken) → ModelMetadata?` — thêm field đọc vào; `ParseModalityArray` private mới (Refactor nội bộ). Task 3 không phụ thuộc.

- [ ] **Step 1: Viết failing tests — thêm vào `ProviderEndpointMetadataProviderTests`**

```csharp
[Fact]
public async Task FetchAsync_WhenContextLengthOnly_ParsesContextWindow()
{
    // OpenRouter đặt ctx ở context_length, không phải context_window
    var handler = new JsonHandler("""{"id":"m","context_length":65536}""");
    var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());

    var (p, m) = Pair();
    var meta = await provider.FetchAsync(p, m);

    Assert.NotNull(meta);
    Assert.Equal(65_536, meta.ContextWindow);
}

[Fact]
public async Task FetchAsync_WhenMaxModelLenOnly_ParsesContextWindow()
{
    // vLLM đặt ctx ở max_model_len
    var handler = new JsonHandler("""{"id":"m","max_model_len":32768}""");
    var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());

    var (p, m) = Pair();
    var meta = await provider.FetchAsync(p, m);

    Assert.NotNull(meta);
    Assert.Equal(32_768, meta.ContextWindow);
}

[Fact]
public async Task FetchAsync_WhenMultipleContextFields_PrefersContextWindow()
{
    var handler = new JsonHandler("""
        {"id":"m","context_window":128000,"context_length":65536,"max_model_len":32768}
        """);
    var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());

    var (p, m) = Pair();
    var meta = await provider.FetchAsync(p, m);

    Assert.NotNull(meta);
    Assert.Equal(128_000, meta.ContextWindow);
}

[Fact]
public async Task FetchAsync_WhenArchitectureModalities_ParsesModalitiesAndVision()
{
    // OpenRouter shape: modalities nằm trong architecture, không có supported_modalities
    var handler = new JsonHandler("""
        {"id":"m","architecture":{"input_modalities":["text","image"],"output_modalities":["text"]}}
        """);
    var provider = new ProviderEndpointMetadataProvider(new StubFactory(handler), new DpapiSecretProtector());

    var (p, m) = Pair();
    var meta = await provider.FetchAsync(p, m);

    Assert.NotNull(meta);
    Assert.True(meta.SupportsVision);
    Assert.Equal("""["text","image"]""", meta.InputModalities);
    Assert.Equal("""["text"]""", meta.OutputModalities);
}
```

- [ ] **Step 2: Chạy test — FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo --filter ProviderEndpointMetadataProviderTests`
Expected: 4 test mới FAIL (trả `null`), 3 test cũ PASS.

- [ ] **Step 3: Sửa `ProviderEndpointMetadataProvider.FetchAsync` + helper**

Thay khối parse dòng 42-47 thành:

```csharp
            // Ưu tiên tường minh: context_window (OpenAI) > context_length (OpenRouter) > max_model_len (vLLM)
            int? contextWindow = IntFrom(root, "context_window")
                ?? IntFrom(root, "context_length")
                ?? IntFrom(root, "max_model_len");
            bool? vision = ParseVision(root);
            string? input = ParseModalityList(root, "supported_modalities", "input")
                ?? ParseModalityList(root, "architecture", "input_modalities");
            string? output = ParseModalityList(root, "supported_modalities", "output")
                ?? ParseModalityList(root, "architecture", "output_modalities");
```

Thay `ParseVision` (dòng 72-89) thành:

```csharp
    private static bool? ParseVision(JsonElement root)
    {
        // Fallback kiến trúc OpenRouter: supported_modalities.input > architecture.input_modalities
        var input = ParseModalityArray(root, "supported_modalities", "input")
            ?? ParseModalityArray(root, "architecture", "input_modalities");
        if (input is null) return null;
        return input.Contains("image", StringComparer.OrdinalIgnoreCase);
    }
```

Thay `ParseModalityList` (dòng 91-106) thành 2 hàm:

```csharp
    private static string? ParseModalityList(JsonElement root, string property, string direction)
    {
        var values = ParseModalityArray(root, property, direction);
        return values is null ? null : JsonSerializer.Serialize(values);
    }

    /// <summary>Đọc mảng string 2 cấp <c>{property}.{direction}</c>; null nếu shape không khớp.</summary>
    private static string[]? ParseModalityArray(JsonElement root, string property, string direction)
    {
        if (!root.TryGetProperty(property, out var parent)
            || parent.ValueKind != JsonValueKind.Object
            || !parent.TryGetProperty(direction, out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var values = list.EnumerateArray()
            .Select(x => x.GetString())
            .Where(x => !string.IsNullOrEmpty(x))
            .Select(x => x!)
            .ToArray();
        return values.Length == 0 ? null : values;
    }

    private static int? IntFrom(JsonElement root, string property) =>
        root.TryGetProperty(property, out var el) && el.TryGetInt32(out var val) ? val : null;
```

Giữ nguyên khối "shape lạ → null" (dòng 50-53) và catch-filter best-effort.

- [ ] **Step 4: Chạy test — PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo --filter ProviderEndpointMetadataProviderTests`
Expected: PASS (7 test: 3 cũ + 4 mới, không có FAIL).

- [ ] **Step 5: Chạy toàn bộ + build — PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: **121/121 PASS** (không FAIL).

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: 0 Warning / 0 Error.

- [ ] **Step 6: Commit**

```powershell
git add "src/RouterBalancing.Core/Providers/ProviderEndpointMetadataProvider.cs" "router balancing test/Providers/ProviderEndpointMetadataProviderTests.cs"
git commit -m "feat: parse context_length/max_model_len and architecture modalities"
```

---

### Task 3: `IModelService.UpdateCapabilitiesAsync`

**Files:**
- Modify: `src/RouterBalancing.Core/Providers/IModelService.cs`
- Modify: `src/RouterBalancing.Core/Providers/ModelService.cs`
- Test: `router balancing test/Providers/ModelServiceTests.cs`

**Interfaces:**
- Consumes: pattern service hiện hữu (`KeyNotFoundException` cho unknown id, `DbInitializer`, `TestDb`, helper `ServiceWith(json)` + `SeedProviderAsync(params string[])` trong test class).
- Produces (Task 4 dùng): `Task UpdateCapabilitiesAsync(long modelId, int? contextWindow, bool supportsVision, bool supportsThink, CancellationToken ct = default)` — throws `ArgumentOutOfRangeException` khi ctx ngoài `1..10_000_000`, `KeyNotFoundException` khi model không tồn tại.

- [ ] **Step 1: Viết failing tests — thêm vào `ModelServiceTests`**

```csharp
[Fact]
public async Task UpdateCapabilities_WhenNewValues_PersistsAllThree()
{
    var providerId = await SeedProviderAsync("a");
    var service = ServiceWith("{}");
    long modelId;
    using (var db = _db.CreateDbContext())
    {
        modelId = (await db.Models.SingleAsync(m => m.ModelId == "a")).Id;
    }

    await service.UpdateCapabilitiesAsync(modelId, 128_000, supportsVision: true, supportsThink: true);

    using var db2 = _db.CreateDbContext();
    var saved = await db2.Models.SingleAsync(m => m.Id == modelId);
    Assert.Equal(128_000, saved.ContextWindow);
    Assert.True(saved.SupportsVision);
    Assert.True(saved.SupportsThink);
    Assert.Equal(providerId, saved.ProviderId); // không đụng quan hệ
}

[Fact]
public async Task UpdateCapabilities_WhenContextNull_ClearsValue()
{
    await SeedProviderAsync("a");
    var service = ServiceWith("{}");
    long modelId;
    using (var db = _db.CreateDbContext())
    {
        modelId = (await db.Models.SingleAsync(m => m.ModelId == "a")).Id;
    }
    await service.UpdateCapabilitiesAsync(modelId, 64_000, supportsVision: false, supportsThink: false);

    // Ô trống trong UI → null = clear (fill chain không tự chạy lại — chấp nhận theo spec §3.1)
    await service.UpdateCapabilitiesAsync(modelId, contextWindow: null, supportsVision: false, supportsThink: false);

    using var db2 = _db.CreateDbContext();
    var saved = await db2.Models.SingleAsync(m => m.Id == modelId);
    Assert.Null(saved.ContextWindow);
    Assert.False(saved.SupportsVision);
    Assert.False(saved.SupportsThink);
}

[Theory]
[InlineData(0)]
[InlineData(-1)]
[InlineData(10_000_001)]
public async Task UpdateCapabilities_WhenContextOutOfRange_ThrowsArgumentOutOfRange(int badValue)
{
    await SeedProviderAsync("a");
    var service = ServiceWith("{}");
    long modelId;
    using (var db = _db.CreateDbContext())
    {
        modelId = (await db.Models.SingleAsync(m => m.ModelId == "a")).Id;
    }

    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
        service.UpdateCapabilitiesAsync(modelId, badValue, supportsVision: false, supportsThink: false));
}

[Fact]
public async Task UpdateCapabilities_WhenModelUnknown_ThrowsKeyNotFound()
{
    await SeedProviderAsync("a");
    var service = ServiceWith("{}");

    await Assert.ThrowsAsync<KeyNotFoundException>(() =>
        service.UpdateCapabilitiesAsync(modelId: 999_999, contextWindow: 1000,
            supportsVision: false, supportsThink: false));
}
```

- [ ] **Step 2: Chạy test — FAIL**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo --filter UpdateCapabilities`
Expected: FAIL — `UpdateCapabilitiesAsync` chưa tồn tại (`error CS1061`).

- [ ] **Step 3: Thêm contract vào `IModelService`**

Thêm sau `SetAllEnabledAsync`:

```csharp
    /// <summary>Cập nhật capabilities tay (Context window, Vision, Think) — user sửa inline ở UI.</summary>
    /// <param name="modelId">Id model cần sửa.</param>
    /// <param name="contextWindow"><see langword="null"/> = xóa giá trị; hợp lệ 1..10_000_000.</param>
    /// <param name="supportsVision">Model có nhận input ảnh không.</param>
    /// <param name="supportsThink">Model có chế độ suy luận không.</param>
    /// <param name="ct">Token hủy.</param>
    /// <exception cref="ArgumentOutOfRangeException">Khi <paramref name="contextWindow"/> ngoài 1..10_000_000.</exception>
    /// <exception cref="KeyNotFoundException">Khi model không tồn tại.</exception>
    Task UpdateCapabilitiesAsync(long modelId, int? contextWindow, bool supportsVision, bool supportsThink, CancellationToken ct = default);
```

- [ ] **Step 4: Implement trong `ModelService`**

Thêm sau `SetAllEnabledAsync` (giữ `/// <inheritdoc/>`):

```csharp
    /// <inheritdoc/>
    public async Task UpdateCapabilitiesAsync(
        long modelId, int? contextWindow, bool supportsVision, bool supportsThink, CancellationToken ct = default)
    {
        // Relational pattern: null không match → ctx null hợp lệ (clear)
        if (contextWindow is < 1 or > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(contextWindow), contextWindow,
                "Context window phải trong khoảng 1..10.000.000.");
        }

        using var db = _db.CreateDbContext();
        var model = await db.Models.FirstOrDefaultAsync(m => m.Id == modelId, ct)
            ?? throw new KeyNotFoundException($"Model {modelId} not found.");
        model.ContextWindow = contextWindow;
        model.SupportsVision = supportsVision;
        model.SupportsThink = supportsThink;
        await db.SaveChangesAsync(ct);
    }
```

- [ ] **Step 5: Chạy test — PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo --filter UpdateCapabilities`
Expected: PASS (6 test: 1 + 1 + 3 InlineData + 1).

- [ ] **Step 6: Chạy toàn bộ + build — PASS**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: **127/127 PASS**.

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: 0 Warning / 0 Error.

- [ ] **Step 7: Commit**

```powershell
git add "src/RouterBalancing.Core/Providers/IModelService.cs" "src/RouterBalancing.Core/Providers/ModelService.cs" "router balancing test/Providers/ModelServiceTests.cs"
git commit -m "feat: add UpdateCapabilitiesAsync for manual model metadata"
```

---

### Task 4: UI inline edit + placeholder `—` + i18n (7 keys)

**Files:**
- Modify: `src/RouterBalancing.Core/Localization/Translations.cs` (EN dict quanh dòng 160 sau `["models.manual"]`, VI dict quanh dòng 323)
- Modify: `router-balancing/Components/Pages/Providers.razor` — `@using` (đầu file), cell Metadata (dòng 196-215), state (~sau dòng 384), handler (~sau `ToggleModelAsync` dòng 634)

**Interfaces:**
- Consumes: `ModelSvc.UpdateCapabilitiesAsync` (Task 3), `ReloadKeepExpandAsync(long)`, `Toast.Show(string, ToastSeverity)`, `L["key"]`, `_busy` single-flight, `Badge`/`BadgeVariant`, class Tailwind `btn btn-primary`/`btn btn-outline-info`/`btn btn-outline-secondary` (đã có trong build CSS).
- Produces: UI không cần task sau; gates cuối toàn phase.

- [ ] **Step 1: Thêm 7 keys i18n**

Trong `Translations.cs`, dict EN — thêm ngay sau `["models.manual"] = "manual",`:

```csharp
        ["models.action.editCapabilities"] = "Edit",
        ["models.capabilities.title"] = "Capabilities",
        ["models.capabilities.contextWindow"] = "Context window",
        ["models.capabilities.vision"] = "Vision",
        ["models.capabilities.think"] = "Think",
        ["models.error.contextRange"] = "Context window must be 1–10,000,000",
        ["models.msg.capabilitiesSaved"] = "Capabilities saved",
```

Dict VI — thêm ngay sau `["models.manual"] = "thủ công",`:

```csharp
        ["models.action.editCapabilities"] = "Sửa",
        ["models.capabilities.title"] = "Khả năng",
        ["models.capabilities.contextWindow"] = "Context window",
        ["models.capabilities.vision"] = "Vision",
        ["models.capabilities.think"] = "Think",
        ["models.error.contextRange"] = "Context window phải trong khoảng 1–10.000.000",
        ["models.msg.capabilitiesSaved"] = "Đã lưu khả năng",
```

- [ ] **Step 2: State + handlers trong `Providers.razor`**

Thêm `@using System.Globalization` sau dòng `@using RouterBalancing.Core.Settings` (phục vụ `CultureInfo.InvariantCulture` khi parse số).

Thêm 4 field sau `private string _newModelId = string.Empty;` (dòng 384):

```csharp
    // Edit inline capabilities — 1 bộ state duy nhất nên mở row khác tự đóng row cũ (chỉ 1 model sửa 1 lúc)
    private long? _editingCapabilitiesId;
    private string _editCtx = string.Empty;
    private bool _editVision;
    private bool _editThink;
```

Thêm 3 handler sau hàm `ToggleModelAsync` (kết thúc ~dòng 634, sau khối `finally`):

```csharp
    private void BeginEditCapabilities(Model model)
    {
        if (_busy) return;
        _editingCapabilitiesId = model.Id;
        // Trống = clear (null) — phân biệt với giá trị thật 0 (0 vốn bị range chặn)
        _editCtx = model.ContextWindow?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        _editVision = model.SupportsVision;
        _editThink = model.SupportsThink;
    }

    private void CancelEditCapabilities()
    {
        _editingCapabilitiesId = null;
    }

    private async Task SaveCapabilitiesAsync(Model model)
    {
        if (_busy) return;

        int? ctx = null;
        if (!string.IsNullOrWhiteSpace(_editCtx))
        {
            // Parse InvariantCulture để "128000" không phụ thuộc locale của máy user
            if (!int.TryParse(_editCtx, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                || parsed is < 1 or > 10_000_000)
            {
                Toast.Show(L["models.error.contextRange"], ToastSeverity.Error);
                return;
            }
            ctx = parsed;
        }

        _busy = true;
        try
        {
            await ModelSvc.UpdateCapabilitiesAsync(model.Id, ctx, _editVision, _editThink);
            _editingCapabilitiesId = null;
            await ReloadKeepExpandAsync(model.ProviderId);
            Toast.Show(L["models.msg.capabilitiesSaved"], ToastSeverity.Success);
        }
        catch (ArgumentOutOfRangeException)
        {
            // Service ném khi range sai — cùng toast với lỗi parse phía trên
            Toast.Show(L["models.error.contextRange"], ToastSeverity.Error);
        }
        catch (Exception ex)
        {
            Log.Error("Không lưu được capabilities model.", ex);
            Toast.Show(L["dashboard.msg.failed"], ToastSeverity.Error);
        }
        finally
        {
            _busy = false;
        }
    }
```

- [ ] **Step 3: Cell Metadata — edit mode + placeholder**

Thay toàn bộ `<td class="px-2 py-1.5">` (dòng 196-215, nội dung div badges) thành:

```razor
                                                        <td class="px-2 py-1.5">
                                                            @if (_editingCapabilitiesId == m.Id)
                                                            {
                                                                <div class="flex flex-wrap items-center gap-1.5" data-testid="caps-edit-form">
                                                                    <label class="flex items-center gap-1">
                                                                        @L["models.capabilities.contextWindow"]
                                                                        <input type="number" min="1" max="10000000"
                                                                               class="w-24" disabled="@_busy"
                                                                               data-testid="caps-ctx-input"
                                                                               @bind="_editCtx" @bind:event="oninput" />
                                                                    </label>
                                                                    <label class="flex items-center gap-1">
                                                                        <input type="checkbox" disabled="@_busy" @bind="_editVision" />
                                                                        @L["models.capabilities.vision"]
                                                                    </label>
                                                                    <label class="flex items-center gap-1">
                                                                        <input type="checkbox" disabled="@_busy" @bind="_editThink" />
                                                                        @L["models.capabilities.think"]
                                                                    </label>
                                                                    <button type="button" class="btn btn-primary px-1.5"
                                                                            disabled="@_busy" data-testid="caps-save"
                                                                            @onclick="() => SaveCapabilitiesAsync(m)">
                                                                        &#10003;
                                                                    </button>
                                                                    <button type="button" class="btn btn-outline-secondary px-1.5"
                                                                            disabled="@_busy" data-testid="caps-cancel"
                                                                            @onclick="CancelEditCapabilities">
                                                                        &#10005;
                                                                    </button>
                                                                </div>
                                                            }
                                                            else
                                                            {
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
                                                                    @if (!m.IsManual && m.ContextWindow is null && !m.SupportsVision && !m.SupportsThink)
                                                                    {
                                                                        @* Ký hiệu language-neutral — không qua i18n (spec §3.3) *@
                                                                        <span class="opacity-70" data-testid="caps-placeholder">&#8212;</span>
                                                                    }
                                                                    <button type="button" class="btn btn-outline-info px-1.5"
                                                                            disabled="@_busy" data-testid="caps-edit"
                                                                            title="@L["models.action.editCapabilities"]"
                                                                            @onclick="() => BeginEditCapabilities(m)">
                                                                            &#9998;
                                                                    </button>
                                                                </div>
                                                            }
                                                        </td>
```

- [ ] **Step 4: Gates backend + npm**

Run: `dotnet test "router balancing test/router balancing test.csproj" --nologo`
Expected: **127/127 PASS**.

Run: `dotnet build router-balancing/router-balancing.csproj -f net10.0-windows10.0.19041.0 --nologo`
Expected: 0 Warning / 0 Error (lỗi Razor bind/`CultureInfo` sẽ hiện ở đây nếu có).

Run: `npm run build` (workdir `router-balancing/vite-project`)
Expected: exit 0. Nếu `router-balancing/wwwroot/build/` đổi → `git add router-balancing/wwwroot/build/`.

- [ ] **Step 5: Self-test trên app thật (CDP harness có sẵn)**

1. Build + chạy app với env `WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS=--remote-debugging-port=9222`, mở `/providers`.
2. Dùng driver `%TEMP%\opencode\cdp-test\driveN.js` (playwright-core) kiểm tra:
   - Model không metadata → hiện `—` (data-testid `caps-placeholder`); model có badge → không hiện `—`.
   - Bấm ✏️ (`caps-edit`) → form hiện; nhập ctx `0` → bấm ✓ → toast lỗi range (EN/VI), vẫn ở edit mode.
   - Nhập `131072` + tick Vision → ✓ → toast "Capabilities saved/Đã lưu khả năng", badges mới hiện, expand vẫn mở.
   - Bấm ✏️ → sửa → ✕ → không đổi gì.
   - Row khác đang edit thì mở row khác → row cũ đóng (1 bộ state).
3. Provider `/v1` bug: thêm provider `BaseUrl=https://.../v1` (test connection qua gateway thật) → Network log URL đúng `/v1/models` đơn.

- [ ] **Step 6: Commit**

```powershell
git add "src/RouterBalancing.Core/Localization/Translations.cs" "router-balancing/Components/Pages/Providers.razor"
git status   # xác nhận không còn file ngoài ý muốn (kèm wwwroot/build nếu npm đổi)
git commit -m "feat: inline capabilities editing and metadata placeholder"
```

- [ ] **Step 7: Final gate**

Run toàn bộ 3 lệnh (test / build / npm) — cả 3 xanh → báo controller hoàn thành phase để review + merge.
